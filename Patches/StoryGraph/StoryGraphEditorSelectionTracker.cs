using System;
using Config;
using HarmonyLib;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 原版事件编辑器只长期保存当前 Talk；选项编辑弹窗关闭后不会留下“当前选项”。
    /// 这里用弱引用记录最近一次从当前 Talk 打开的 Option，供剧情图首次定位使用。
    /// 切换 Talk 会立即清除 Option，且读取时还会校验事件、父 Talk 与引用列表，
    /// 因此删除选项、取消新建或重开其他事件都不会把定位意图串到下一张图。
    /// </summary>
    internal static class StoryGraphEditorSelectionTracker
    {
        private static WeakReference _view = new WeakReference(null);
        private static int _eventId;
        private static TalkCfg _talk;
        private static OptionCfg _option;
        private static int _optionParentTalkId;

        internal static void RecordTalk(
            ModEvtEditView view, TalkCfg talk, int eventId)
        {
            _view = new WeakReference(view);
            _eventId = eventId;
            _talk = talk;
            _option = null;
            _optionParentTalkId = 0;
        }

        internal static void RecordOption(
            int eventId, int parentTalkId, OptionCfg option)
        {
            var view = _view.Target as ModEvtEditView;
            if (view == null || option == null || _talk == null
                || _eventId != eventId || _talk.id != parentTalkId)
                return;
            _option = option;
            _optionParentTalkId = parentTalkId;
        }

        internal static OptionCfg GetPreferredOption(
            ModEvtEditView view, TalkCfg currentTalk, int eventId)
        {
            if (view == null || currentTalk == null || _option == null
                || !ReferenceEquals(_view.Target, view)
                || _eventId != eventId
                || _optionParentTalkId != currentTalk.id
                || (_talk != null
                    && !ReferenceEquals(_talk, currentTalk)
                    && _talk.id != currentTalk.id))
                return null;

            // 新建后按“完成”才会进入父 Talk.option；删除、取消新建或改到一个
            // 尚未被父对话引用的 ID 时回退到 Talk 定位。
            return currentTalk.option != null
                   && currentTalk.option.Contains(_option.id)
                ? _option
                : null;
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "Select", typeof(TalkCfg))]
    internal static class StoryGraphEditorTalkSelectionPatch
    {
        private static void Postfix(ModEvtEditView __instance, TalkCfg _cfg)
        {
            try
            {
                StoryGraphEditorSelectionTracker.RecordTalk(
                    __instance, _cfg,
                    EvtStoryGraphViewAccess.GetEventId(__instance));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StoryGraph.Selection.Talk] " + e);
            }
        }
    }

    [HarmonyPatch(typeof(ModEvtOptionView), "OnOpen")]
    internal static class StoryGraphEditorOptionSelectionPatch
    {
        private static void Postfix(ModEvtOptionView __instance)
        {
            try
            {
                object[] parms = __instance != null ? __instance.parms : null;
                if (parms == null || parms.Length < 3
                    || !(parms[0] is int eventId)
                    || !(parms[1] is int parentTalkId))
                    return;
                StoryGraphEditorSelectionTracker.RecordOption(
                    eventId, parentTalkId, parms[2] as OptionCfg);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StoryGraph.Selection.Option] " + e);
            }
        }
    }
}
