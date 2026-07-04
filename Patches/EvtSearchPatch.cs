using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Config;
using GenUI.Mod;
using HarmonyLib;
using TMPro;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    // ═════════════════════════════════════════════════════════════════
    //  事件关键词搜索功能
    //
    //  给三个编辑器界面各注入一个搜索输入框，实时过滤列表：
    //    A. ModEvtBrowserView — 事件浏览器（从官方事件库导入）
    //    B. ModNormalEditView — 通用编辑器（编辑 mod 自己的配置列表）
    //    C. ModEvtEditView    — 事件对话编辑器（编辑事件内的对话台词）
    //
    //  全部纯运行时动态创建，不修改本体 prefab，不干扰原有 UI 逻辑。
    // ═════════════════════════════════════════════════════════════════

    // ───────────────────────────────────────────────────────────────────
    //  搜索栏 UI 工具
    // ───────────────────────────────────────────────────────────────────

    internal static class SearchBarUtil
    {
        public const float SearchBarHeight = 36f;
        public const string SearchBarName = "EditorPlus_SearchBar";

        /// <summary>从场景中已有 Text 组件获取游戏字体，确保中文正常。</summary>
        public static Font FindUiFont()
        {
            var t = UnityEngine.Object.FindObjectOfType<Text>();
            if (t != null && t.font != null) return t.font;
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        /// <summary>
        /// 创建一个带背景、占位文字、输入文字的 InputField 搜索栏。
        /// 调用方负责通过 PlaceAboveScroll 设置 RectTransform 的锚点和位置。
        /// </summary>
        public static (GameObject go, InputField input) Create(Transform parent, string placeholder)
        {
            // 根节点：背景 + InputField
            var go = new GameObject(SearchBarName, typeof(RectTransform), typeof(Image), typeof(InputField));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);

            var bg = go.GetComponent<Image>();
            bg.color = new Color(0.12f, 0.12f, 0.12f, 0.9f);
            bg.raycastTarget = true;

            var input = go.GetComponent<InputField>();
            input.contentType = InputField.ContentType.Standard;
            input.characterLimit = 0;
            input.caretColor = Color.white;
            input.selectionColor = new Color(0.3f, 0.5f, 1f, 0.5f);

            var font = FindUiFont();
            const int fontSize = 16;

            // Placeholder
            var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
            var phRt = phGo.GetComponent<RectTransform>();
            phRt.SetParent(rt, false);
            FillParent(phRt, 8f, 2f);
            var phText = phGo.GetComponent<Text>();
            phText.font = font;
            phText.fontSize = fontSize;
            phText.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            phText.alignment = TextAnchor.MiddleLeft;
            phText.text = placeholder;
            phText.raycastTarget = false;
            phText.horizontalOverflow = HorizontalWrapMode.Wrap;
            phText.verticalOverflow = VerticalWrapMode.Truncate;

            // Text（输入显示）
            var txtGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.SetParent(rt, false);
            FillParent(txtRt, 8f, 2f);
            var txt = txtGo.GetComponent<Text>();
            txt.font = font;
            txt.fontSize = fontSize;
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.text = "";
            txt.raycastTarget = false;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Truncate;

            input.textComponent = txt;
            input.placeholder = phText;

            go.AddComponent<SearchBarCleanup>();
            return (go, input);
        }

        /// <summary>让 RectTransform 填满父节点，留出指定内边距。</summary>
        private static void FillParent(RectTransform rt, float hPad, float vPad)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(hPad, vPad);
            rt.offsetMax = new Vector2(-hPad, -vPad);
        }

        /// <summary>
        /// 将 containerRt 顶部下移 height，在腾出的空间放置 barRt。
        /// 返回 containerRt 原始 offsetMax（用于关闭时恢复）。
        /// 要求 containerRt 使用拉伸锚点 (0,0,1,1)，绝大多数滚动视图都满足。
        /// barRt 的 Y 锚点设为顶部锚点（与 containerRt 顶部对齐），
        /// 这样 offsetMin/offsetMax 相对于父容器顶部计算，
        /// 搜索栏只占据顶部 height 像素的窄条，不会覆盖整个列表区域。
        /// </summary>
        public static Vector2 PlaceAboveScroll(RectTransform containerRt, RectTransform barRt, float height)
        {
            var originalOffsetMax = containerRt.offsetMax;
            containerRt.offsetMax = new Vector2(originalOffsetMax.x, originalOffsetMax.y - height);

            // Y 轴用顶部锚点（anchorMin.y = anchorMax.y = 容器顶锚点），
            // X 轴跟随容器的左右拉伸锚点。
            barRt.anchorMin = new Vector2(containerRt.anchorMin.x, containerRt.anchorMax.y);
            barRt.anchorMax = new Vector2(containerRt.anchorMax.x, containerRt.anchorMax.y);  // 同一行：Y 锚在顶部
            barRt.pivot = new Vector2(0.5f, 0.5f);
            barRt.offsetMin = new Vector2(containerRt.offsetMin.x, containerRt.offsetMax.y);
            barRt.offsetMax = new Vector2(containerRt.offsetMax.x, containerRt.offsetMax.y + height);
            return originalOffsetMax;
        }

        /// <summary>
        /// 从 itemgroup_content 向上查找 ScrollRect；找不到则直接用 itemgroup_content 自身。
        /// ModEvtBrowserUI 没有 ScrollRect 字段，itemgroup_content 是根的直接子节点，
        /// 但 prefab 中它可能自身带 ScrollRect 组件，也可能被 ScrollRect 包裹。
        /// </summary>
        public static RectTransform FindScrollContainer(UIItemGroup itemGroup)
        {
            if (itemGroup?.gameObject == null) return null;
            var go = itemGroup.gameObject;

            // 先看自身是否是 ScrollRect
            var sr = go.GetComponent<ScrollRect>();
            if (sr != null) return sr.GetComponent<RectTransform>();

            // 再向上找
            sr = go.GetComponentInParent<ScrollRect>();
            if (sr != null) return sr.GetComponent<RectTransform>();

            // 回退：直接用 itemgroup_content 自身的 RectTransform
            return go.GetComponent<RectTransform>();
        }

        /// <summary>在 parent 下查找已有的搜索栏。</summary>
        public static GameObject FindExisting(Transform parent)
        {
            if (parent == null) return null;
            var found = parent.Find(SearchBarName);
            return found?.gameObject;
        }

        /// <summary>销毁已有搜索栏，并在销毁前恢复其占用的滚动区域空间。</summary>
        public static void DestroyExisting(Transform parent)
        {
            var existing = FindExisting(parent);
            if (existing == null) return;
            var cleanup = existing.GetComponent<SearchBarCleanup>();
            if (cleanup != null)
                cleanup.RestoreScroll();
            UnityEngine.Object.Destroy(existing);
        }

        /// <summary>
        /// 清空搜索后，滚动到当前选中条目使其可见。
        /// 通过 UIItemGroup.FindCell 找到选中项的 cell，
        /// 再根据 cell 在 scrollLeft.content 中的位置计算 verticalNormalizedPosition。
        /// </summary>
        /// <param name="itemgroup">列表组件（itemgroup_item / itemgroup_list）</param>
        /// <param name="scrollLeft">外层 ScrollRect</param>
        /// <param name="selectedData">当前选中的数据对象（curSelect）</param>
        public static void ScrollToSelection(UIItemGroup itemgroup, ScrollRect scrollLeft, object selectedData)
        {
            if (itemgroup == null || scrollLeft == null || selectedData == null) return;

            var cell = itemgroup.FindCell(selectedData);
            if (cell?.transform == null) return;

            var content = scrollLeft.content;
            if (content == null) return;

            // 确保布局已更新（SetDatas 在本帧刚调用过）
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);

            float contentHeight = content.rect.height;
            float viewportHeight = scrollLeft.viewport?.rect.height ?? contentHeight;
            if (contentHeight <= viewportHeight) return; // 不需要滚动

            // Unity ScrollRect 的 verticalNormalizedPosition:
            //   1 = 顶部, 0 = 底部
            // content 的 pivot 通常在顶部 (0,1)，子元素 localPosition.y 从顶部向下为负
            // cellTop = 距 content 顶部的距离（正值）
            float cellY = -cell.transform.localPosition.y; // 从顶部到 cell 的距离
            float cellHeight = cell.transform.rect.height;
            float cellCenter = cellY + cellHeight * 0.5f;

            // 让 cell 出现在视口中间
            float scrollable = contentHeight - viewportHeight;
            float target = cellCenter - viewportHeight * 0.5f;
            float normalized = Mathf.Clamp01(1f - target / scrollable);
            scrollLeft.verticalNormalizedPosition = normalized;
        }
    }

    /// <summary>
    /// 搜索栏自动清理：当父 View 被关闭时，恢复滚动区域尺寸。
    /// 不在 OnDisable 中销毁自身——搜索栏随 View 的 gameObject 一同被
    /// ResMgr 回收或由下次 OnOpen 的 DestroyExisting 清理。
    /// </summary>
    internal class SearchBarCleanup : MonoBehaviour
    {
        [NonSerialized] public RectTransform ScrollToRestore;
        [NonSerialized] public Vector2 OriginalOffsetMax;

        public void RestoreScroll()
        {
            if (ScrollToRestore != null)
                ScrollToRestore.offsetMax = OriginalOffsetMax;
        }

        private void OnDisable()
        {
            // View 被关闭时恢复滚动区域尺寸，但不销毁自身。
            // 销毁由 DestroyExisting（下次 OnOpen）或 ResMgr 回收处理。
            RestoreScroll();
        }

        private void OnDestroy()
        {
            RestoreScroll();
        }
    }

    // ───────────────────────────────────────────────────────────────────
    //  搜索匹配逻辑
    // ───────────────────────────────────────────────────────────────────

    internal static class SearchMatch
    {
        /// <summary>
        /// 判断 searchText 是否匹配指定 id 和文本字段（不区分大小写）。
        /// 空白搜索返回 true（不过滤）。中文天然支持。
        /// </summary>
        public static bool Match(string searchText, int id, params string[] textFields)
        {
            if (string.IsNullOrWhiteSpace(searchText)) return true;
            searchText = searchText.Trim();

            if (id.ToString().IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (textFields != null)
            {
                foreach (var field in textFields)
                {
                    if (field != null &&
                        field.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 将 text 中所有与 keyword 匹配的子串用富文本 color 标签包裹（不区分大小写），
        /// 使匹配部分在 UI 上高亮显示。keyword 为空白时原样返回。
        /// </summary>
        public static string Highlight(string text, string keyword, string colorHex = "#D4A017")
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(keyword))
                return text;
            keyword = keyword.Trim();
            if (keyword.Length == 0) return text;

            var sb = new System.Text.StringBuilder(text.Length + keyword.Length * 32);
            int pos = 0;
            while (pos < text.Length)
            {
                int idx = text.IndexOf(keyword, pos, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    sb.Append(text, pos, text.Length - pos);
                    break;
                }
                if (idx > pos)
                    sb.Append(text, pos, idx - pos);
                sb.Append("<color=").Append(colorHex).Append('>');
                sb.Append(text, idx, keyword.Length);
                sb.Append("</color>");
                pos = idx + keyword.Length;
            }
            return sb.ToString();
        }
    }

    /// <summary>每个 View 实例的搜索状态。</summary>
    internal class SearchState
    {
        public InputField Input;
        public string CurrentSearchText; // 当前生效的搜索关键词（Trim 后），空白时为 null
    }

    // ═════════════════════════════════════════════════════════════════
    //  A. 事件浏览器搜索（ModEvtBrowserView）
    //     从官方事件库导入事件时，可按标题或 ID 搜索 + 角色筛选联动。
    // ═════════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(ModEvtBrowserView), "OnOpen")]
    internal static class EvtBrowserSearchPatch
    {
        private static readonly ConditionalWeakTable<ModEvtBrowserView, SearchState> _states = new();

        private static void Postfix(ModEvtBrowserView __instance)
        {
            try
            {
                // ModEvtBrowserUI 没有 ScrollRect 字段，itemgroup_content 是根直接子节点。
                // 通过 itemgroup_content 查找滚动容器（ScrollRect 或自身）。
                var containerRt = SearchBarUtil.FindScrollContainer(__instance.itemgroup_content);
                if (containerRt == null) return;
                var parent = containerRt.parent;
                if (parent == null) return;

                // 销毁可能残留的旧搜索栏（复用 prefab 时）
                SearchBarUtil.DestroyExisting(parent);

                var (barGo, input) = SearchBarUtil.Create(parent, "搜索 ID 或标题…");
                var barRt = barGo.GetComponent<RectTransform>();

                var originalOffsetMax = SearchBarUtil.PlaceAboveScroll(containerRt, barRt, SearchBarUtil.SearchBarHeight);

                var cleanup = barGo.GetComponent<SearchBarCleanup>();
                cleanup.ScrollToRestore = containerRt;
                cleanup.OriginalOffsetMax = originalOffsetMax;

                var state = new SearchState { Input = input };
                _states.Remove(__instance);
                _states.Add(__instance, state);

                input.onValueChanged.AddListener(text =>
                {
                    try
                    {
                        if (__instance == null || __instance.gameObject == null) return;
                        ApplyFilters(__instance, text);
                    }
                    catch (Exception e) { Plugin.Log.LogError($"[EvtBrowserSearch] {e}"); }
                });

                Plugin.Log.LogInfo("[EvtBrowserSearch] 搜索栏已注入。");
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtBrowserSearchInit] {e}"); }
        }

        /// <summary>按搜索文本 + 角色筛选双重过滤 allEvtCfgs，更新 evts 和分页。</summary>
        internal static void ApplyFilters(ModEvtBrowserView view, string searchText)
        {
            var t = Traverse.Create(view);
            var allEvtCfgs = t.Field("allEvtCfgs").GetValue<Dictionary<int, EvtCfg>>();
            var evts = t.Field("evts").GetValue<List<int>>();
            if (allEvtCfgs == null || evts == null) return;

            // 读取当前角色筛选 ID
            int roleId = 0;
            var roleIds = t.Field("roleIds").GetValue<List<Dropdown.OptionData>>();
            var dropdown = view.dropdown_filit;
            if (roleIds != null && dropdown != null && dropdown.value >= 0 && dropdown.value < roleIds.Count)
            {
                if (roleIds[dropdown.value] is ModDropdownOption opt)
                    roleId = opt.id;
            }

            // 过滤
            evts.Clear();
            // 记录当前搜索词供渲染高亮使用
            var state = GetState(view);
            var trimmedSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
            if (state != null) state.CurrentSearchText = trimmedSearch;
            foreach (var kvp in allEvtCfgs)
            {
                // 保持原版行为：跳过无标题事件
                if (kvp.Value.title.IsEmpty()) continue;
                // 角色筛选
                if (roleId != 0 && kvp.Value.npc != roleId) continue;
                // 文本搜索
                if (!SearchMatch.Match(searchText, kvp.Key, kvp.Value.title)) continue;
                evts.Add(kvp.Key);
            }

            // 更新分页
            int cntPerPage = t.Field("cntPerPage").GetValue<int>();
            if (cntPerPage <= 0) cntPerPage = 40;
            int totalPage = Mathf.CeilToInt((float)evts.Count / cntPerPage);
            t.Field("totalPage").SetValue(totalPage);

            if (view.txt_page_total != null)
                view.txt_page_total.text = totalPage.ToString();

            // 清空搜索时，跳转到包含最近选中事件的页面（而非第1页）
            int targetPage = 1;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                var selects = t.Field("selects").GetValue<List<int>>();
                if (selects != null && selects.Count > 0)
                {
                    int lastSelected = selects[selects.Count - 1];
                    int idx = evts.IndexOf(lastSelected);
                    if (idx >= 0)
                        targetPage = idx / cntPerPage + 1;
                }
            }
            t.Field("curPage").SetValue(targetPage);
            view.SetPage(targetPage);
        }

        internal static SearchState GetState(ModEvtBrowserView view)
        {
            _states.TryGetValue(view, out var s);
            return s;
        }
    }

    /// <summary>角色下拉变化时，重新应用文本搜索（两个筛选器联动）。</summary>
    [HarmonyPatch(typeof(ModEvtBrowserView), "OnFilit")]
    internal static class EvtBrowserSearchRefilterPatch
    {
        private static void Postfix(ModEvtBrowserView __instance)
        {
            try
            {
                var state = EvtBrowserSearchPatch.GetState(__instance);
                if (state?.Input == null || string.IsNullOrEmpty(state.Input.text)) return;
                EvtBrowserSearchPatch.ApplyFilters(__instance, state.Input.text);
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtBrowserSearchRefilter] {e}"); }
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  B. 通用编辑器搜索（ModNormalEditView）
    //     编辑 mod 自己的配置列表时，可按 ID 或名称搜索。
    //     对所有配置类型生效（EvtCfg、CGCfg、ItemCfg 等）。
    // ═════════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(ModNormalEditView), "OnOpen")]
    internal static class NormalEditSearchPatch
    {
        private static readonly ConditionalWeakTable<ModNormalEditView, SearchState> _states = new();

        private static void Postfix(ModNormalEditView __instance)
        {
            try
            {
                var scrollLeft = __instance.scroll_left;
                if (scrollLeft == null) return;
                var scrollRt = scrollLeft.GetComponent<RectTransform>();
                if (scrollRt == null) return;
                var parent = scrollRt.parent;
                if (parent == null) return;

                // 销毁残留
                SearchBarUtil.DestroyExisting(parent);

                var (barGo, input) = SearchBarUtil.Create(parent, "搜索 ID 或名称…");
                var barRt = barGo.GetComponent<RectTransform>();

                var originalOffsetMax = SearchBarUtil.PlaceAboveScroll(scrollRt, barRt, SearchBarUtil.SearchBarHeight);

                var cleanup = barGo.GetComponent<SearchBarCleanup>();
                cleanup.ScrollToRestore = scrollRt;
                cleanup.OriginalOffsetMax = originalOffsetMax;

                var state = new SearchState { Input = input };
                _states.Remove(__instance);
                _states.Add(__instance, state);

                input.onValueChanged.AddListener(text =>
                {
                    try
                    {
                        if (__instance == null || __instance.gameObject == null) return;
                        ApplyFilter(__instance, text);
                    }
                    catch (Exception e) { Plugin.Log.LogError($"[NormalEditSearch] {e}"); }
                });

                Plugin.Log.LogInfo("[NormalEditSearch] 搜索栏已注入。");
            }
            catch (Exception e) { Plugin.Log.LogError($"[NormalEditSearchInit] {e}"); }
        }

        /// <summary>过滤 cfgs 列表并刷新 itemgroup_item 显示。</summary>
        internal static void ApplyFilter(ModNormalEditView view, string searchText)
        {
            var t = Traverse.Create(view);
            var cfgsList = t.Field("cfgs").GetValue<List<object>>();
            if (cfgsList == null) return;

            // 空搜索 → 显示全部
            if (string.IsNullOrWhiteSpace(searchText))
            {
                var st = GetState(view);
                if (st != null) st.CurrentSearchText = null;
                view.itemgroup_item.SetDatas(cfgsList);
                // 滚动到选中条目，保持上下文
                var curSelect = t.Field("curSelect").GetValue<object>();
                SearchBarUtil.ScrollToSelection(view.itemgroup_item, view.scroll_left, curSelect);
                return;
            }

            searchText = searchText.Trim();
            {
                var st = GetState(view);
                if (st != null) st.CurrentSearchText = searchText;
            }
            var cfgType = t.Field("cfgType").GetValue<System.Type>();
            var nameFields = t.Field("nameFields").GetValue<List<string>>();
            if (cfgType == null) return;

            var idField = cfgType.GetField("id");
            var filtered = new List<object>();

            foreach (var obj in cfgsList)
            {
                if (obj == null) continue;

                int id = 0;
                if (idField != null)
                    id = (int)idField.GetValue(obj);

                // 取显示文本（与 OnRenderItem 逻辑一致：name > title > content > text）
                string displayText = null;
                if (nameFields != null)
                {
                    foreach (var nf in nameFields)
                    {
                        var field = cfgType.GetField(nf);
                        if (field == null) continue;
                        var val = field.GetValue(obj);
                        if (val is string s && !string.IsNullOrEmpty(s))
                        {
                            displayText = s;
                            break;
                        }
                        if (val is List<string> { Count: > 0 } list)
                        {
                            displayText = list[0];
                            break;
                        }
                    }
                }

                if (SearchMatch.Match(searchText, id, displayText))
                    filtered.Add(obj);
            }

            view.itemgroup_item.SetDatas(filtered);
        }

        internal static SearchState GetState(ModNormalEditView view)
        {
            _states.TryGetValue(view, out var s);
            return s;
        }

        /// <summary>清除搜索文本（新增/删除条目时调用，让用户看到完整列表）。</summary>
        internal static void ClearSearch(ModNormalEditView view)
        {
            var state = GetState(view);
            if (state?.Input != null && !string.IsNullOrEmpty(state.Input.text))
                state.Input.text = ""; // 触发 onValueChanged → 显示全部
        }
    }

    /// <summary>新增条目后清除搜索（让新条目立即可见）。</summary>
    [HarmonyPatch(typeof(ModNormalEditView), "OnClickNewItem")]
    internal static class NormalEditSearchRefilterPatch
    {
        private static void Postfix(ModNormalEditView __instance)
        {
            try { NormalEditSearchPatch.ClearSearch(__instance); }
            catch (Exception e) { Plugin.Log.LogError($"[NormalEditSearchRefilter] {e}"); }
        }
    }

    /// <summary>删除条目后清除搜索（让列表变化立即可见）。</summary>
    [HarmonyPatch(typeof(ModNormalEditView), "DeleteCur")]
    internal static class NormalEditSearchRefilterPatch2
    {
        private static void Postfix(ModNormalEditView __instance)
        {
            try { NormalEditSearchPatch.ClearSearch(__instance); }
            catch (Exception e) { Plugin.Log.LogError($"[NormalEditSearchRefilter2] {e}"); }
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  C. 事件对话编辑器搜索（ModEvtEditView）
    //     编辑事件对话台词时，可按 ID 或台词内容搜索。
    // ═════════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(ModEvtEditView), "OnOpen")]
    internal static class EvtEditSearchPatch
    {
        private static readonly ConditionalWeakTable<ModEvtEditView, SearchState> _states = new();

        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                var scrollLeft = __instance.scroll_left;
                if (scrollLeft == null) return;
                var scrollRt = scrollLeft.GetComponent<RectTransform>();
                if (scrollRt == null) return;
                var parent = scrollRt.parent;
                if (parent == null) return;

                SearchBarUtil.DestroyExisting(parent);

                var (barGo, input) = SearchBarUtil.Create(parent, "搜索 ID 或台词…");
                var barRt = barGo.GetComponent<RectTransform>();

                var originalOffsetMax = SearchBarUtil.PlaceAboveScroll(scrollRt, barRt, SearchBarUtil.SearchBarHeight);

                var cleanup = barGo.GetComponent<SearchBarCleanup>();
                cleanup.ScrollToRestore = scrollRt;
                cleanup.OriginalOffsetMax = originalOffsetMax;

                var state = new SearchState { Input = input };
                _states.Remove(__instance);
                _states.Add(__instance, state);

                input.onValueChanged.AddListener(text =>
                {
                    try
                    {
                        if (__instance == null || __instance.gameObject == null) return;
                        ApplyFilter(__instance, text);
                    }
                    catch (Exception e) { Plugin.Log.LogError($"[EvtEditSearch] {e}"); }
                });

                Plugin.Log.LogInfo("[EvtEditSearch] 搜索栏已注入。");
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtEditSearchInit] {e}"); }
        }

        /// <summary>过滤 talkCfgs 列表并刷新 itemgroup_list 显示。</summary>
        internal static void ApplyFilter(ModEvtEditView view, string searchText)
        {
            var talkCfgs = Traverse.Create(view).Field("talkCfgs").GetValue<List<TalkCfg>>();
            if (talkCfgs == null) return;

            if (string.IsNullOrWhiteSpace(searchText))
            {
                var st = GetState(view);
                if (st != null) st.CurrentSearchText = null;
                view.itemgroup_list.SetDatas(talkCfgs);
                // 滚动到选中条目，保持上下文
                var curSelect = Traverse.Create(view).Field("curSelect").GetValue<TalkCfg>();
                SearchBarUtil.ScrollToSelection(view.itemgroup_list, view.scroll_left, curSelect);
                return;
            }

            searchText = searchText.Trim();
            {
                var st = GetState(view);
                if (st != null) st.CurrentSearchText = searchText;
            }
            var filtered = new List<TalkCfg>();
            foreach (var cfg in talkCfgs)
            {
                if (cfg == null) continue;
                if (SearchMatch.Match(searchText, cfg.id, cfg.content))
                    filtered.Add(cfg);
            }

            view.itemgroup_list.SetDatas(filtered);
        }

        internal static SearchState GetState(ModEvtEditView view)
        {
            _states.TryGetValue(view, out var s);
            return s;
        }

        /// <summary>清除搜索文本（新增对话时调用）。</summary>
        internal static void ClearSearch(ModEvtEditView view)
        {
            var state = GetState(view);
            if (state?.Input != null && !string.IsNullOrEmpty(state.Input.text))
                state.Input.text = "";
        }
    }

    /// <summary>新增对话后清除搜索（让新对话立即可见）。</summary>
    [HarmonyPatch(typeof(ModEvtEditView), "OnClickNew")]
    internal static class EvtEditSearchRefilterPatch
    {
        private static void Postfix(ModEvtEditView __instance)
        {
            try { EvtEditSearchPatch.ClearSearch(__instance); }
            catch (Exception e) { Plugin.Log.LogError($"[EvtEditSearchRefilter] {e}"); }
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  D. 列表项关键词高亮
    //     原版渲染方法设置完纯文本后，Postfix 把搜索关键词包裹富文本
    //     color 标签，使匹配文字高亮显示（黄色）。
    // ═════════════════════════════════════════════════════════════════

    /// <summary>事件浏览器列表项高亮。</summary>
    [HarmonyPatch(typeof(ModEvtBrowserView), "OnRender")]
    internal static class EvtBrowserHighlightPatch
    {
        private static void Postfix(ModEvtBrowserView __instance, UICell _cell)
        {
            try
            {
                var state = EvtBrowserSearchPatch.GetState(__instance);
                if (string.IsNullOrEmpty(state?.CurrentSearchText)) return;
                if (_cell is Cell_ModEvtBrowserItemUI cell)
                {
                    var txt = cell.txt_item;
                    if (txt != null)
                    {
                        txt.supportRichText = true;
                        txt.text = SearchMatch.Highlight(txt.text, state.CurrentSearchText);
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtBrowserHighlight] {e}"); }
        }
    }

    /// <summary>通用编辑器列表项高亮。</summary>
    [HarmonyPatch(typeof(ModNormalEditView), "OnRenderItem")]
    internal static class NormalEditHighlightPatch
    {
        private static void Postfix(ModNormalEditView __instance, UICell _cell)
        {
            try
            {
                var state = NormalEditSearchPatch.GetState(__instance);
                if (string.IsNullOrEmpty(state?.CurrentSearchText)) return;
                if (_cell is Cell_ModNormalEditItemUI cell)
                {
                    var txt = cell.txtex_name;
                    if (txt != null)
                    {
                        txt.text = SearchMatch.Highlight(txt.text, state.CurrentSearchText);
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[NormalEditHighlight] {e}"); }
        }
    }

    /// <summary>事件对话编辑器列表项高亮。</summary>
    [HarmonyPatch(typeof(ModEvtEditView), "OnRenderItem")]
    internal static class EvtEditHighlightPatch
    {
        private static void Postfix(ModEvtEditView __instance, UICell _cell)
        {
            try
            {
                var state = EvtEditSearchPatch.GetState(__instance);
                if (string.IsNullOrEmpty(state?.CurrentSearchText)) return;
                if (_cell is Cell_ModNormalEditItemUI cell)
                {
                    var txt = cell.txtex_name;
                    if (txt != null)
                    {
                        txt.text = SearchMatch.Highlight(txt.text, state.CurrentSearchText);
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtEditHighlight] {e}"); }
        }
    }

}
