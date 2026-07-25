using System;
using System.Collections.Generic;
using System.Reflection;
using Config;
using HarmonyLib;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>集中封装对 ModEvtEditView 私有内存字段和 Select 的安全访问。</summary>
    internal static class EvtStoryGraphViewAccess
    {
        private static readonly FieldInfo TalkCfgsField =
            AccessTools.Field(typeof(ModEvtEditView), "talkCfgs");
        private static readonly FieldInfo OptionCfgsField =
            AccessTools.Field(typeof(ModEvtEditView), "optionCfgs");
        private static readonly FieldInfo PersonCfgsField =
            AccessTools.Field(typeof(ModEvtEditView), "personCfgs");
        private static readonly FieldInfo EventIdField =
            AccessTools.Field(typeof(ModEvtEditView), "evtId");
        private static readonly FieldInfo CurrentField =
            AccessTools.Field(typeof(ModEvtEditView), "curSelect");
        private static readonly FieldInfo ModRootField =
            AccessTools.Field(typeof(ModEvtEditView), "modRoot");
        private static readonly MethodInfo SelectMethod =
            AccessTools.Method(typeof(ModEvtEditView), "Select", new[] { typeof(TalkCfg) });
        private static readonly HashSet<string> Warnings = new HashSet<string>();

        internal static bool TrySnapshot(
            ModEvtEditView view,
            out List<TalkCfg> talks,
            out Dictionary<int, OptionCfg> options,
            out int eventId,
            out List<int> entries,
            out bool entriesKnown,
            out string error)
        {
            talks = new List<TalkCfg>();
            options = new Dictionary<int, OptionCfg>();
            entries = new List<int>();
            entriesKnown = false;
            eventId = 0;
            error = null;
            if (view == null)
            {
                error = "事件编辑器引用已失效。";
                return false;
            }
            if (TalkCfgsField == null)
            {
                error = "当前游戏版本找不到对话数据（ModEvtEditView.talkCfgs），剧情图已停用。";
                WarnOnce("talkCfgs", error);
                return false;
            }

            try
            {
                var rawTalks = TalkCfgsField.GetValue(view) as List<TalkCfg>;
                if (rawTalks != null)
                {
                    // 快照容器、保留配置对象引用：模型只读，点击时仍能传回原 Select。
                    for (int i = 0; i < rawTalks.Count; i++) talks.Add(rawTalks[i]);
                }

                if (OptionCfgsField != null)
                {
                    var rawOptions = OptionCfgsField.GetValue(view)
                        as Dictionary<int, OptionCfg>;
                    if (rawOptions != null)
                    {
                        foreach (KeyValuePair<int, OptionCfg> pair in rawOptions)
                            options[pair.Key] = pair.Value;
                    }
                }
                else
                {
                    WarnOnce("optionCfgs",
                        "当前游戏版本找不到选项数据（ModEvtEditView.optionCfgs）；剧情图仍会显示对话跳转，但无法展开选项。" );
                }

                if (EventIdField != null)
                {
                    object value = EventIdField.GetValue(view);
                    if (value is int id) eventId = id;
                }
                else
                {
                    WarnOnce("evtId",
                        "当前游戏版本找不到事件编号（ModEvtEditView.evtId）；事件根节点将暂时显示为 0。" );
                }

                // 实际 OnOpen 参数第 3 项就是 EvtCfg.talkId；它仍是当前 View 的
                // 内存参数，不读磁盘、不改数据。拿不到时模型会按拓扑关系推断入口。
                try
                {
                    if (view.parms != null && view.parms.Length > 2)
                    {
                        if (view.parms[2] == null)
                        {
                            // OnOpen 明确收到 null，等价于当前事件无入口。
                            entriesKnown = true;
                        }
                        else if (view.parms[2] is IEnumerable<int> rawEntries)
                        {
                            foreach (int entry in rawEntries) entries.Add(entry);
                            entriesKnown = true;
                        }
                        else
                        {
                            WarnOnce("entry-type",
                                "当前事件入口参数类型异常，将按拓扑推断。" );
                        }
                    }
                }
                catch (Exception e)
                {
                    WarnOnce("entry",
                        "读取当前事件入口失败，将按拓扑推断：" + e.Message);
                }
                return true;
            }
            catch (Exception e)
            {
                error = "读取事件编辑器内存失败：" + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogError("[EvtStoryGraph.Access] " + e);
                return false;
            }
        }

        internal static Dictionary<int, string> SnapshotPersonNames(ModEvtEditView view)
        {
            var result = new Dictionary<int, string>();
            if (view == null || PersonCfgsField == null)
            {
                WarnOnce("personCfgs",
                    "当前游戏版本找不到人物数据（ModEvtEditView.personCfgs），剧情图暂时只显示角色 ID。" );
                return result;
            }
            try
            {
                var persons = PersonCfgsField.GetValue(view)
                    as Dictionary<int, PersonCfg>;
                if (persons == null) return result;
                foreach (KeyValuePair<int, PersonCfg> pair in persons)
                {
                    string name = pair.Value != null ? pair.Value.name : null;
                    if (!string.IsNullOrWhiteSpace(name)) result[pair.Key] = name.Trim();
                }
            }
            catch (Exception e)
            {
                WarnOnce("person-names", "读取人物名称失败，将保留角色 ID：" + e.Message);
            }
            return result;
        }

        internal static TalkCfg GetCurrent(ModEvtEditView view)
        {
            if (view == null || CurrentField == null) return null;
            try { return CurrentField.GetValue(view) as TalkCfg; }
            catch (Exception e)
            {
                WarnOnce("curSelect", "读取当前对话失败：" + e.Message);
                return null;
            }
        }

        internal static int GetEventId(ModEvtEditView view)
        {
            if (view == null || EventIdField == null) return 0;
            try
            {
                object value = EventIdField.GetValue(view);
                return value is int id ? id : 0;
            }
            catch (Exception e)
            {
                WarnOnce("evtId-current", "读取当前事件编号失败：" + e.Message);
                return 0;
            }
        }

        internal static bool TryGetModRoot(
            ModEvtEditView view, out string modRoot, out string error)
        {
            modRoot = null;
            error = null;
            if (view == null || ModRootField == null)
            {
                error = "当前游戏版本找不到 Mod 根目录（ModEvtEditView.modRoot）。";
                WarnOnce("modRoot", error);
                return false;
            }
            try
            {
                modRoot = ModRootField.GetValue(view) as string;
                if (string.IsNullOrWhiteSpace(modRoot))
                {
                    error = "当前事件编辑器没有有效的 Mod 根目录。";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = "读取 Mod 根目录失败：" + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogError("[EvtStoryGraph.ModRoot] " + e);
                return false;
            }
        }

        /// <summary>
        /// 剧情图成功写盘后，把深拷贝草稿同步给原事件编辑器。两个私有容器与左栏选择
        /// 作为一个小事务更新；任一步骤失败都会尽量恢复原对象，避免编辑器留在半应用状态。
        /// </summary>
        internal static bool TryApplyDraft(
            ModEvtEditView view,
            List<TalkCfg> talks,
            Dictionary<int, OptionCfg> options,
            int selectTalkId,
            out string error)
        {
            error = null;
            if (view == null || TalkCfgsField == null || OptionCfgsField == null
                || SelectMethod == null)
            {
                error = "当前游戏版本缺少应用剧情草稿所需的编辑器字段或方法。";
                WarnOnce("apply-draft", error);
                return false;
            }

            object oldTalks = null;
            object oldOptions = null;
            TalkCfg oldCurrent = null;
            try
            {
                oldTalks = TalkCfgsField.GetValue(view);
                oldOptions = OptionCfgsField.GetValue(view);
                oldCurrent = CurrentField != null
                    ? CurrentField.GetValue(view) as TalkCfg
                    : null;

                var liveTalks = talks ?? new List<TalkCfg>();
                var liveOptions = options ?? new Dictionary<int, OptionCfg>();
                TalkCfg selected = liveTalks.Find(t => t != null && t.id == selectTalkId);

                TalkCfgsField.SetValue(view, liveTalks);
                OptionCfgsField.SetValue(view, liveOptions);
                try { EvtEditSearchPatch.ClearSearch(view); }
                catch (Exception e)
                {
                    WarnOnce("apply-clear-search", "应用草稿时清除左栏搜索失败：" + e.Message);
                }
                if (view.itemgroup_list != null)
                    view.itemgroup_list.SetDatas(liveTalks);
                SelectMethod.Invoke(view, new object[] { selected });
                return true;
            }
            catch (Exception e)
            {
                try
                {
                    if (oldTalks != null) TalkCfgsField.SetValue(view, oldTalks);
                    if (oldOptions != null) OptionCfgsField.SetValue(view, oldOptions);
                    var restoredTalks = oldTalks as List<TalkCfg>;
                    if (view.itemgroup_list != null && restoredTalks != null)
                        view.itemgroup_list.SetDatas(restoredTalks);
                    SelectMethod.Invoke(view, new object[] { oldCurrent });
                }
                catch (Exception rollbackError)
                {
                    Plugin.Log?.LogError("[EvtStoryGraph.Apply.Rollback] " + rollbackError);
                }
                Exception actual = e is TargetInvocationException && e.InnerException != null
                    ? e.InnerException
                    : e;
                error = "同步剧情草稿到事件编辑器失败，已尝试恢复原内存；"
                        + "磁盘尚未提交，请保留草稿并查看日志："
                        + actual.GetType().Name + ": " + actual.Message;
                Plugin.Log?.LogError("[EvtStoryGraph.Apply] " + actual);
                return false;
            }
        }

        internal static bool TrySelectAndScroll(
            ModEvtEditView view, TalkCfg talk, List<TalkCfg> talks, out string error)
        {
            error = null;
            if (view == null || talk == null)
            {
                error = "该节点没有可定位的本地对话。";
                return false;
            }
            if (SelectMethod == null)
            {
                error = "当前游戏版本不支持定位对话（找不到 ModEvtEditView.Select(TalkCfg)）。";
                WarnOnce("Select", error);
                return false;
            }
            try
            {
                // 先清除原左栏过滤，使目标 cell 一定会出现在 itemgroup_list。
                try { EvtEditSearchPatch.ClearSearch(view); }
                catch (Exception e)
                {
                    WarnOnce("clear-search", "清除左栏搜索失败：" + e.Message);
                }

                SelectMethod.Invoke(view, new object[] { talk });
                if (view.itemgroup_list != null)
                {
                    List<TalkCfg> currentTalks = talks;
                    if (TalkCfgsField != null)
                    {
                        var live = TalkCfgsField.GetValue(view) as List<TalkCfg>;
                        if (live != null) currentTalks = live;
                    }
                    if (currentTalks != null) view.itemgroup_list.SetDatas(currentTalks);
                }
                SearchBarUtil.ScrollToSelection(
                    view.itemgroup_list, view.scroll_left, talk);
                return true;
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException ?? e;
                error = "定位对话失败：" + inner.GetType().Name + ": " + inner.Message;
                Plugin.Log?.LogError("[EvtStoryGraph.Select] " + inner);
                return false;
            }
            catch (Exception e)
            {
                error = "定位对话失败：" + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogError("[EvtStoryGraph.Select] " + e);
                return false;
            }
        }

        private static void WarnOnce(string key, string message)
        {
            if (!Warnings.Add(key)) return;
            Plugin.Log?.LogWarning("[EvtStoryGraph] " + message);
        }
    }

    /// <summary>InitUI 后克隆稳定的保存按钮，在原按钮列注入“剧情图”。</summary>
    [HarmonyPatch(typeof(ModEvtEditView), "InitUI")]
    internal static class EvtStoryGraphInitPatch
    {
        internal const string ButtonName = "btn_story_graph";
        internal const string ButtonLabel = "剧情图";

        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                Inject(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[EvtStoryGraph.Init] " + e);
            }
        }

        internal static StoryGraphWindow Inject(ModEvtEditView view)
        {
            if (view == null || view.gameObject == null) return null;
            StoryGraphWindow window =
                view.gameObject.GetComponent<StoryGraphWindow>();
            if (window == null)
                window = view.gameObject.AddComponent<StoryGraphWindow>();
            window.Bind(view);

            UIButton template = view.btn_save;
            if (template == null || template.gameObject == null)
            {
                Plugin.Log?.LogWarning(
                    "[EvtStoryGraph] 找不到 btn_save 模板，剧情图按钮未注入。" );
                return window;
            }
            Transform parent = template.gameObject.transform.parent;
            if (parent == null)
            {
                Plugin.Log?.LogWarning(
                    "[EvtStoryGraph] btn_save 没有父节点，剧情图按钮未注入。" );
                return window;
            }

            Transform existing = parent.Find(ButtonName);
            GameObject buttonObject;
            if (existing != null)
            {
                buttonObject = existing.gameObject;
            }
            else
            {
                buttonObject = UnityEngine.Object.Instantiate(
                    template.gameObject, parent, false);
                buttonObject.name = ButtonName;
                MiniGameUtil.StripBadComponents(buttonObject);
            }

            Button button = buttonObject.GetComponent<Button>();
            if (button == null)
            {
                Plugin.Log?.LogWarning(
                    "[EvtStoryGraph] 克隆按钮缺少 UnityEngine.UI.Button，功能已降级停用。" );
                buttonObject.SetActive(false);
                return window;
            }
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(window.Toggle);
            SetButtonLabel(buttonObject, ButtonLabel);
            buttonObject.transform.SetAsLastSibling();
            buttonObject.SetActive(true);
            Plugin.Log?.LogInfo("[EvtStoryGraph] 剧情图按钮已注入。" );
            return window;
        }

        internal static void SetButtonLabel(GameObject go, string label)
        {
            if (go == null) return;
            Text text = go.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                text.text = label;
                text.resizeTextForBestFit = true;
                text.resizeTextMinSize = 10;
                return;
            }
            TMP_Text tmp = go.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null) tmp.text = label;
        }
    }

    /// <summary>
    /// OnOpen 在原版 LoadTalkOptionCfg/Select 完成后执行。组件每次重开都丢弃旧模型，
    /// 确保不会把上一事件的 TalkCfg 引用带到新 View。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "OnOpen")]
    internal static class EvtStoryGraphOpenPatch
    {
        private static bool Prefix(ModEvtEditView __instance)
        {
            try
            {
                string modRoot = __instance != null && __instance.parms != null
                                 && __instance.parms.Length > 0
                    ? __instance.parms[0] as string
                    : null;
                if (!StoryGraphEditPersistence.HasPendingTransaction(modRoot)) return true;
                string error;
                if (!StoryGraphEditPersistence.TryRecoverPendingTransaction(
                        modRoot, out error))
                {
                    Plugin.Log?.LogError("[EvtStoryGraph.Recover] " + error);
                    try { ToastHelper.Toast(error + "；为避免载入半提交数据，本次事件打开已取消。" ); }
                    catch { }
                    return false; // 跳过原 OnOpen，绝不能把跨版本 Talk/Option 载入内存。
                }
                Plugin.Log?.LogWarning(
                    "[EvtStoryGraph] 已在加载事件数据前恢复上次未完成的剧情图保存。" );
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[EvtStoryGraph.Recover] " + e);
                try { ToastHelper.Toast("检查剧情图保存事务失败，本次事件打开已取消；请查看日志。" ); }
                catch { }
                return false;
            }
        }

        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                StoryGraphWindow window = __instance?.gameObject != null
                    ? __instance.gameObject.GetComponent<StoryGraphWindow>()
                    : null;
                if (window == null) window = EvtStoryGraphInitPatch.Inject(__instance);
                window?.ResetForViewOpen();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[EvtStoryGraph.OnOpen] " + e);
            }
        }
    }

    /// <summary>
    /// 剧情图窗口 → 事件编辑器左栏的导航助手（契约约定入口）。
    /// 新 UGUI 窗口（StoryGraphWindow）双击节点时调用：在左栏选中对应 Talk
    /// 并滚动到位，图窗口本身保持打开。
    /// 清搜索 → 反射 Select → itemgroup_list.SetDatas → ScrollToSelection 的
    /// 具体实现保留在 EvtStoryGraphViewAccess.TrySelectAndScroll（含 try/catch
    /// 与日志），这里只把成功/失败结果用 Toast 反馈给用户。
    /// </summary>
    internal static class EvtStoryGraphNavigation
    {
        internal static void SelectTalkAndScroll(ModEvtEditView view, TalkCfg talk)
        {
            try
            {
                string error;
                // talks 传 null：TrySelectAndScroll 内部会实时重读
                // ModEvtEditView.talkCfgs，SetDatas 总是使用当前内存列表，
                // 而不是窗口打开时留下的旧快照。
                if (EvtStoryGraphViewAccess.TrySelectAndScroll(
                        view, talk, null, out error))
                {
                    ToastHelper.Toast("已定位到对话 " + talk.id);
                }
                else
                {
                    ToastHelper.Toast(error ?? "无法定位节点。" );
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[EvtStoryGraph.Navigate] " + e);
            }
        }
    }
}
