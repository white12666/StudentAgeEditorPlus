using System;
using System.Collections.Generic;
using System.Reflection;
using Config;
using HarmonyLib;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    internal sealed class EvtHistorySnapshot
    {
        internal List<TalkCfg> Talks;
        internal Dictionary<int, OptionCfg> Options;
        internal int Selected = -1;
        internal string InputPath;
        internal string InputText;
        internal int Anchor;
        internal int Focus;
        internal string Fingerprint;

        internal void UpdateFingerprint()
        {
            Fingerprint = EditorChangeTracking.Fingerprint(Talks)
                          + EditorChangeTracking.Fingerprint(Options);
        }

        internal static bool Same(EvtHistorySnapshot a, EvtHistorySnapshot b) =>
            a.Fingerprint == b.Fingerprint && a.InputPath == b.InputPath
            && a.InputText == b.InputText;

        // LaTeX 选中时把已保存的烘焙文本还原成源码，不应产生撤销步骤；
        // 历史里同一版本也换成源码，避免以后从更新过的边车恢复出错误版本。
        internal void NormalizeTalk(string before, TalkCfg after)
        {
            bool changed = false;
            for (int i = 0; i < Talks.Count; i++)
            {
                if (Talks[i]?.id != after.id
                    || EditorChangeTracking.Fingerprint(Talks[i]) != before) continue;
                if (!changed) Talks = new List<TalkCfg>(Talks);
                Talks[i] = EditorChangeTracking.DeepCopy(after);
                changed = true;
            }
            if (changed) UpdateFingerprint();
        }
    }

    internal static class EvtHistoryAccess
    {
        private static readonly FieldInfo TalksField = AccessTools.Field(typeof(ModEvtEditView), "talkCfgs");
        private static readonly FieldInfo OptionsField = AccessTools.Field(typeof(ModEvtEditView), "optionCfgs");
        private static readonly FieldInfo CurrentCellField = AccessTools.Field(typeof(ModEvtEditView), "curSelectCell");
        private static readonly MethodInfo SelectMethod = AccessTools.Method(typeof(ModEvtEditView), "Select");

        internal static EvtHistorySnapshot Capture(ModEvtEditView view, EvtHistoryInput input = null,
            EvtHistorySnapshot previous = null)
        {
            var talks = (List<TalkCfg>)TalksField.GetValue(view) ?? new List<TalkCfg>();
            var options = (Dictionary<int, OptionCfg>)OptionsField.GetValue(view) ?? new Dictionary<int, OptionCfg>();
            string fingerprint = EditorChangeTracking.Fingerprint(talks) + EditorChangeTracking.Fingerprint(options);
            bool sameData = previous != null && previous.Fingerprint == fingerprint;
            var snapshot = new EvtHistorySnapshot
            {
                // 未失焦正文只变输入字符串，复用不可变的数据快照，避免每个字复制整段剧情。
                Talks = sameData ? previous.Talks : EditorChangeTracking.DeepCopy(talks),
                Options = sameData ? previous.Options : EditorChangeTracking.DeepCopy(options),
                Selected = talks.IndexOf(EvtStoryGraphViewAccess.GetCurrent(view)),
                Fingerprint = fingerprint,
            };
            if (input != null)
            {
                snapshot.InputPath = input.Path;
                snapshot.InputText = input.Text;
                snapshot.Anchor = input.Anchor;
                snapshot.Focus = input.Focus;
            }
            return snapshot;
        }

        internal static void Apply(ModEvtEditView view, EvtHistorySnapshot snapshot)
        {
            var talks = EditorChangeTracking.DeepCopy(snapshot.Talks);
            var options = EditorChangeTracking.DeepCopy(snapshot.Options);
            TalkCfg selected = snapshot.Selected >= 0 && snapshot.Selected < talks.Count
                ? talks[snapshot.Selected] : null;
            TalksField.SetValue(view, talks);
            OptionsField.SetValue(view, options);
            CurrentCellField.SetValue(view, null);
            EvtEditSearchPatch.ClearSearch(view);
            // 先 Select 再绑定列表：重建后的 cell 必须以新对象引用显示选中状态。
            SelectMethod.Invoke(view, new object[] { selected });
            view.itemgroup_list.SetDatas(talks);
            if (selected != null)
                SearchBarUtil.ScrollToSelection(view.itemgroup_list, view.scroll_left, selected);
        }
    }
}
