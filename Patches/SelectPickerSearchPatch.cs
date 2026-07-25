using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Config;
using GenUI.Mod;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    // ═════════════════════════════════════════════════════════════════
    //  选择器搜索功能（背景 / CG / 背景+CG 混合选择器）
    //
    //  给三个选择器界面各注入一个搜索输入框，实时过滤列表 + 关键词高亮：
    //    A. ModSelectBgView   — 背景选择器（“插入背景”）
    //    B. ModSelectCGView   — CG 选择器（“开始CG”）
    //    C. ModSelectBgCgView — 背景+CG 混合选择器
    //
    //  注入方式与 EvtBrowserSearchPatch 完全一致：
    //  OnOpen Postfix 注入搜索栏 → 快照完整 ids → onValueChanged 时
    //  从快照重建 ids 并重算分页 → OnRender Postfix 做关键词高亮。
    //  全部纯运行时动态创建，不修改本体 prefab，不干扰原有 UI 逻辑。
    // ═════════════════════════════════════════════════════════════════

    // ───────────────────────────────────────────────────────────────────
    //  各选择器的搜索状态（继承 SearchState，增加完整 ids 快照）
    // ───────────────────────────────────────────────────────────────────

    /// <summary>背景 / CG 选择器状态：快照完整的 int 编号列表。</summary>
    internal class SelectPickerState : SearchState
    {
        public List<int> AllIds;
    }

    /// <summary>背景+CG 混合选择器状态：快照完整的元组列表。</summary>
    internal class SelectBgCgState : SearchState
    {
        public List<(int id, string name, string url, bool isBg)> AllIds;
    }

    // ───────────────────────────────────────────────────────────────────
    //  搜索栏宽度收紧组件
    //
    //  PlaceAboveScroll 按滚动容器宽度拉伸搜索栏，但这三个选择器的窗口
    //  比 4 列格子列表宽，搜索栏右边缘会超出列表区域。本组件在延迟一帧
    //  （等 GridLayoutGroup 排好格子）后测量所有 cell 的最左/最右边缘，
    //  把搜索栏右边缘收紧到与列表右边缘对齐（保持与左边缘相同的留白）。
    //  只会在确实变窄时应用；测量失败时保持原样，安全回退。
    // ───────────────────────────────────────────────────────────────────

    /// <summary>把搜索栏右边缘收紧到格子列表右边缘的运行时组件。</summary>
    internal class SearchBarGridClamp : MonoBehaviour
    {
        public UIItemGroup itemGroup;
        public RectTransform barRt;

        private void OnEnable()
        {
            StartCoroutine(ClampAfterLayout());
        }

        // 延迟一帧：OnOpen 时 GridLayoutGroup 可能还没排好格子位置
        private IEnumerator ClampAfterLayout()
        {
            yield return new WaitForEndOfFrame();
            Clamp();
        }

        private void Clamp()
        {
            const string tag = "[SearchBarGridClamp]";
            try
            {
                if (itemGroup?.gameObject == null || barRt == null)
                {
                    Plugin.Log.LogInfo($"{tag} 跳过：itemGroup 或 barRt 为空。");
                    return;
                }
                var parent = barRt.parent as RectTransform;
                if (parent == null)
                {
                    Plugin.Log.LogInfo($"{tag} 跳过：barRt.parent 不是 RectTransform。");
                    return;
                }

                // 无格子则保持原样（安全回退）
                // 注意：UICell 不是 Component，不能用 GetComponentsInChildren 查找，
                // 必须通过 UIItemGroup.GetCells() 取当前正在显示的格子列表。
                var cells = itemGroup.GetCells();
                if (cells == null || cells.Count == 0)
                {
                    Plugin.Log.LogInfo($"{tag} 跳过：itemGroup 当前没有显示中的格子（itemGroup={itemGroup.gameObject.name}）。");
                    return;
                }

                // 先强制重排，确保格子位置是最新的
                var groupRt = itemGroup.gameObject.GetComponent<RectTransform>();
                if (groupRt != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(groupRt);

                // 换算所有 cell 的最左 / 最右 x 到搜索栏父节点空间
                float left = float.MaxValue, right = float.MinValue;
                foreach (var cell in cells)
                {
                    var crt = cell.transform as RectTransform;
                    if (crt == null) continue;
                    float l = parent.InverseTransformPoint(
                        crt.TransformPoint(new Vector3(crt.rect.xMin, 0f, 0f))).x;
                    float r = parent.InverseTransformPoint(
                        crt.TransformPoint(new Vector3(crt.rect.xMax, 0f, 0f))).x;
                    if (l < left) left = l;
                    if (r > right) right = r;
                }
                if (right <= left)
                {
                    Plugin.Log.LogInfo($"{tag} 跳过：格子测量异常 left={left:F1} right={right:F1}（cells={cells.Count}）。");
                    return;
                }

                // 保持与左边缘相同的留白：barLeft 为搜索栏左边缘在父空间的位置
                float parentW = parent.rect.width;
                float barLeft = barRt.anchorMin.x * parentW + barRt.offsetMin.x;
                float barRight = barRt.anchorMax.x * parentW + barRt.offsetMax.x;
                float inset = Mathf.Max(0f, left - barLeft);
                float desiredRight = right + inset;
                float newX = desiredRight - barRt.anchorMax.x * parentW;

                Plugin.Log.LogInfo(
                    $"{tag} 测量：cells={cells.Count} 格子左缘={left:F1} 格子右缘={right:F1} " +
                    $"parentW={parentW:F1} bar左={barLeft:F1} bar右={barRight:F1} " +
                    $"inset={inset:F1} 期望右缘={desiredRight:F1} newX={newX:F1} 当前offsetMax.x={barRt.offsetMax.x:F1} " +
                    $"parent={parent.name}");

                // 只在确实变窄时应用，y 分量保持不变
                if (newX < barRt.offsetMax.x)
                {
                    barRt.offsetMax = new Vector2(newX, barRt.offsetMax.y);
                    Plugin.Log.LogInfo($"{tag} 已收紧：offsetMax.x {barRt.offsetMax.x:F1}（应用后）。");
                }
                else
                {
                    Plugin.Log.LogInfo($"{tag} 未应用：newX 不小于当前 offsetMax.x（收紧量不足或方向相反）。");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[SearchBarGridClamp] 收紧失败，保持原样：{e}");
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  A. 背景选择器搜索（ModSelectBgView）
    //     名称查找：先 customBgCfgs（可能为 null），再 Cfg.BgCfgMap。
    // ═════════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(ModSelectBgView), "OnOpen")]
    internal static class SelectBgSearchPatch
    {
        private static readonly ConditionalWeakTable<ModSelectBgView, SelectPickerState> _states = new();

        private static void Postfix(ModSelectBgView __instance)
        {
            try
            {
                // ModEvtBrowserUI 没有 ScrollRect 字段，通过 itemgroup_content 查找滚动容器
                var containerRt = SearchBarUtil.FindScrollContainer(__instance.itemgroup_content);
                if (containerRt == null) return;
                var parent = containerRt.parent;
                if (parent == null) return;

                // 销毁可能残留的旧搜索栏（复用 prefab 时）
                SearchBarUtil.DestroyExisting(parent);

                var (barGo, input) = SearchBarUtil.Create(parent, "搜索编号（ID）或背景名…");
                var barRt = barGo.GetComponent<RectTransform>();

                var originalOffsetMax = SearchBarUtil.PlaceAboveScroll(containerRt, barRt, SearchBarUtil.SearchBarHeight);

                var cleanup = barGo.GetComponent<SearchBarCleanup>();
                cleanup.ScrollToRestore = containerRt;
                cleanup.OriginalOffsetMax = originalOffsetMax;

                // 延迟一帧后把搜索栏右边缘收紧到格子列表右边缘
                var clamp = barGo.AddComponent<SearchBarGridClamp>();
                clamp.itemGroup = __instance.itemgroup_content;
                clamp.barRt = barRt;

                // base.OnOpen 已填充 ids，立即快照完整列表
                var ids = Traverse.Create(__instance).Field("ids").GetValue<List<int>>();
                var state = new SelectPickerState
                {
                    Input = input,
                    AllIds = ids != null ? new List<int>(ids) : new List<int>()
                };
                _states.Remove(__instance);
                _states.Add(__instance, state);

                input.onValueChanged.AddListener(text =>
                {
                    try
                    {
                        if (__instance == null || __instance.gameObject == null) return;
                        ApplyFilter(__instance, text);
                    }
                    catch (Exception e) { Plugin.Log.LogError($"[SelectBgSearch] {e}"); }
                });

                Plugin.Log.LogInfo("[SelectBgSearch] 搜索栏已注入。");
            }
            catch (Exception e) { Plugin.Log.LogError($"[SelectBgSearchInit] {e}"); }
        }

        /// <summary>从 AllIds 快照重建 ids，按搜索文本过滤后重算分页并回到第 1 页。</summary>
        internal static void ApplyFilter(ModSelectBgView view, string searchText)
        {
            var state = GetState(view);
            if (state?.AllIds == null) return;

            var t = Traverse.Create(view);
            var ids = t.Field("ids").GetValue<List<int>>();
            if (ids == null) return;

            // 记录当前搜索词供渲染高亮使用
            var trimmedSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
            state.CurrentSearchText = trimmedSearch;

            // customBgCfgs 可能为 null（parms.Length < 2 时），需空检查
            var customCfgs = t.Field("customBgCfgs").GetValue<Dictionary<int, BgCfg>>();

            ids.Clear();
            foreach (var id in state.AllIds)
            {
                string name = null;
                if (customCfgs != null && customCfgs.TryGetValue(id, out var customCfg) && customCfg != null)
                    name = customCfg.name;
                else if (Cfg.BgCfgMap != null && Cfg.BgCfgMap.TryGetValue(id, out var cfg) && cfg != null)
                    name = cfg.name;

                if (SearchMatch.Match(searchText, id, name))
                    ids.Add(id);
            }

            // 重算分页
            int cntPerPage = t.Field("cntPerPage").GetValue<int>();
            if (cntPerPage <= 0) cntPerPage = 40;
            int totalPage = Mathf.CeilToInt((float)ids.Count / cntPerPage);
            t.Field("totalPage").SetValue(totalPage);

            if (view.txt_page_total != null)
                view.txt_page_total.text = totalPage.ToString();

            // 回到第 1 页（空结果时 GetRange(0,0) 安全，无需特判）
            t.Field("curPage").SetValue(1);
            view.SetPage(1);
        }

        internal static SelectPickerState GetState(ModSelectBgView view)
        {
            _states.TryGetValue(view, out var s);
            return s;
        }
    }

    /// <summary>背景选择器列表项关键词高亮。</summary>
    [HarmonyPatch(typeof(ModSelectBgView), "OnRender")]
    internal static class SelectBgHighlightPatch
    {
        private static void Postfix(ModSelectBgView __instance, UICell _cell)
        {
            try
            {
                var state = SelectBgSearchPatch.GetState(__instance);
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
            catch (Exception e) { Plugin.Log.LogError($"[SelectBgHighlight] {e}"); }
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  B. CG 选择器搜索（ModSelectCGView）
    //     名称查找：先 customCGCfgs（可能为 null），再 Cfg.CGCfgMap。
    // ═════════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(ModSelectCGView), "OnOpen")]
    internal static class SelectCGSearchPatch
    {
        private static readonly ConditionalWeakTable<ModSelectCGView, SelectPickerState> _states = new();

        private static void Postfix(ModSelectCGView __instance)
        {
            try
            {
                var containerRt = SearchBarUtil.FindScrollContainer(__instance.itemgroup_content);
                if (containerRt == null) return;
                var parent = containerRt.parent;
                if (parent == null) return;

                SearchBarUtil.DestroyExisting(parent);

                var (barGo, input) = SearchBarUtil.Create(parent, "搜索编号（ID）或CG名…");
                var barRt = barGo.GetComponent<RectTransform>();

                var originalOffsetMax = SearchBarUtil.PlaceAboveScroll(containerRt, barRt, SearchBarUtil.SearchBarHeight);

                var cleanup = barGo.GetComponent<SearchBarCleanup>();
                cleanup.ScrollToRestore = containerRt;
                cleanup.OriginalOffsetMax = originalOffsetMax;

                // 延迟一帧后把搜索栏右边缘收紧到格子列表右边缘
                var clamp = barGo.AddComponent<SearchBarGridClamp>();
                clamp.itemGroup = __instance.itemgroup_content;
                clamp.barRt = barRt;

                // base.OnOpen 已填充 ids，立即快照完整列表
                var ids = Traverse.Create(__instance).Field("ids").GetValue<List<int>>();
                var state = new SelectPickerState
                {
                    Input = input,
                    AllIds = ids != null ? new List<int>(ids) : new List<int>()
                };
                _states.Remove(__instance);
                _states.Add(__instance, state);

                input.onValueChanged.AddListener(text =>
                {
                    try
                    {
                        if (__instance == null || __instance.gameObject == null) return;
                        ApplyFilter(__instance, text);
                    }
                    catch (Exception e) { Plugin.Log.LogError($"[SelectCGSearch] {e}"); }
                });

                Plugin.Log.LogInfo("[SelectCGSearch] 搜索栏已注入。");
            }
            catch (Exception e) { Plugin.Log.LogError($"[SelectCGSearchInit] {e}"); }
        }

        /// <summary>从 AllIds 快照重建 ids，按搜索文本过滤后重算分页并回到第 1 页。</summary>
        internal static void ApplyFilter(ModSelectCGView view, string searchText)
        {
            var state = GetState(view);
            if (state?.AllIds == null) return;

            var t = Traverse.Create(view);
            var ids = t.Field("ids").GetValue<List<int>>();
            if (ids == null) return;

            var trimmedSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
            state.CurrentSearchText = trimmedSearch;

            // customCGCfgs 可能为 null（parms.Length < 2 时），需空检查
            var customCfgs = t.Field("customCGCfgs").GetValue<Dictionary<int, CGCfg>>();

            ids.Clear();
            foreach (var id in state.AllIds)
            {
                string name = null;
                if (customCfgs != null && customCfgs.TryGetValue(id, out var customCfg) && customCfg != null)
                    name = customCfg.name;
                else if (Cfg.CGCfgMap != null && Cfg.CGCfgMap.TryGetValue(id, out var cfg) && cfg != null)
                    name = cfg.name;

                if (SearchMatch.Match(searchText, id, name))
                    ids.Add(id);
            }

            int cntPerPage = t.Field("cntPerPage").GetValue<int>();
            if (cntPerPage <= 0) cntPerPage = 40;
            int totalPage = Mathf.CeilToInt((float)ids.Count / cntPerPage);
            t.Field("totalPage").SetValue(totalPage);

            if (view.txt_page_total != null)
                view.txt_page_total.text = totalPage.ToString();

            t.Field("curPage").SetValue(1);
            view.SetPage(1);
        }

        internal static SelectPickerState GetState(ModSelectCGView view)
        {
            _states.TryGetValue(view, out var s);
            return s;
        }
    }

    /// <summary>CG 选择器列表项关键词高亮。</summary>
    [HarmonyPatch(typeof(ModSelectCGView), "OnRender")]
    internal static class SelectCGHighlightPatch
    {
        private static void Postfix(ModSelectCGView __instance, UICell _cell)
        {
            try
            {
                var state = SelectCGSearchPatch.GetState(__instance);
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
            catch (Exception e) { Plugin.Log.LogError($"[SelectCGHighlight] {e}"); }
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  C. 背景+CG 混合选择器搜索（ModSelectBgCgView）
    //     ids 是元组列表，过滤直接用元组的 id/name，无需查配置表。
    // ═════════════════════════════════════════════════════════════════

    [HarmonyPatch(typeof(ModSelectBgCgView), "OnOpen")]
    internal static class SelectBgCgSearchPatch
    {
        private static readonly ConditionalWeakTable<ModSelectBgCgView, SelectBgCgState> _states = new();

        private static void Postfix(ModSelectBgCgView __instance)
        {
            try
            {
                var containerRt = SearchBarUtil.FindScrollContainer(__instance.itemgroup_content);
                if (containerRt == null) return;
                var parent = containerRt.parent;
                if (parent == null) return;

                SearchBarUtil.DestroyExisting(parent);

                var (barGo, input) = SearchBarUtil.Create(parent, "搜索编号（ID）或名称…");
                var barRt = barGo.GetComponent<RectTransform>();

                var originalOffsetMax = SearchBarUtil.PlaceAboveScroll(containerRt, barRt, SearchBarUtil.SearchBarHeight);

                var cleanup = barGo.GetComponent<SearchBarCleanup>();
                cleanup.ScrollToRestore = containerRt;
                cleanup.OriginalOffsetMax = originalOffsetMax;

                // 延迟一帧后把搜索栏右边缘收紧到格子列表右边缘
                var clamp = barGo.AddComponent<SearchBarGridClamp>();
                clamp.itemGroup = __instance.itemgroup_content;
                clamp.barRt = barRt;

                // base.OnOpen 已填充 ids，立即快照完整元组列表
                var ids = Traverse.Create(__instance).Field("ids")
                    .GetValue<List<(int id, string name, string url, bool isBg)>>();
                var state = new SelectBgCgState
                {
                    Input = input,
                    AllIds = ids != null
                        ? new List<(int id, string name, string url, bool isBg)>(ids)
                        : new List<(int id, string name, string url, bool isBg)>()
                };
                _states.Remove(__instance);
                _states.Add(__instance, state);

                input.onValueChanged.AddListener(text =>
                {
                    try
                    {
                        if (__instance == null || __instance.gameObject == null) return;
                        ApplyFilter(__instance, text);
                    }
                    catch (Exception e) { Plugin.Log.LogError($"[SelectBgCgSearch] {e}"); }
                });

                Plugin.Log.LogInfo("[SelectBgCgSearch] 搜索栏已注入。");
            }
            catch (Exception e) { Plugin.Log.LogError($"[SelectBgCgSearchInit] {e}"); }
        }

        /// <summary>从 AllIds 快照重建 ids，按搜索文本过滤后重算分页并回到第 1 页。</summary>
        internal static void ApplyFilter(ModSelectBgCgView view, string searchText)
        {
            var state = GetState(view);
            if (state?.AllIds == null) return;

            var t = Traverse.Create(view);
            var ids = t.Field("ids").GetValue<List<(int id, string name, string url, bool isBg)>>();
            if (ids == null) return;

            var trimmedSearch = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
            state.CurrentSearchText = trimmedSearch;

            ids.Clear();
            foreach (var item in state.AllIds)
            {
                // 元组自带 id/name，直接匹配，无需查配置表
                if (SearchMatch.Match(searchText, item.id, item.name))
                    ids.Add(item);
            }

            int cntPerPage = t.Field("cntPerPage").GetValue<int>();
            if (cntPerPage <= 0) cntPerPage = 40;
            int totalPage = Mathf.CeilToInt((float)ids.Count / cntPerPage);
            t.Field("totalPage").SetValue(totalPage);

            if (view.txt_page_total != null)
                view.txt_page_total.text = totalPage.ToString();

            t.Field("curPage").SetValue(1);
            view.SetPage(1);
        }

        internal static SelectBgCgState GetState(ModSelectBgCgView view)
        {
            _states.TryGetValue(view, out var s);
            return s;
        }
    }

    /// <summary>背景+CG 混合选择器列表项关键词高亮。</summary>
    [HarmonyPatch(typeof(ModSelectBgCgView), "OnRender")]
    internal static class SelectBgCgHighlightPatch
    {
        private static void Postfix(ModSelectBgCgView __instance, UICell _cell)
        {
            try
            {
                var state = SelectBgCgSearchPatch.GetState(__instance);
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
            catch (Exception e) { Plugin.Log.LogError($"[SelectBgCgHighlight] {e}"); }
        }
    }
}
