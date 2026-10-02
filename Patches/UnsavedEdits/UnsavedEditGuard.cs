using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Config;
using HarmonyLib;
using Sdk;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>一次原版保存：点保存那一刻与原版实际写出的内存快照，以及写盘结果。</summary>
    internal sealed class NativeSaveAttempt
    {
        private Action<NativeSaveAttempt> _finished;

        internal NativeSaveAttempt(EditorSnapshot before)
        {
            Before = before;
        }

        /// <summary>保存前缀链开始前的内容，即作者点保存时看到的内容。</summary>
        internal EditorSnapshot Before { get; }

        /// <summary>原版保存同步部分结束时的内容（其它插件的保存前处理已生效）。</summary>
        internal EditorSnapshot Written { get; set; }

        internal SaveCompletionProbe Probe { get; set; }
        internal bool Finished { get; private set; }
        internal Exception Failure { get; private set; }

        internal void OnFinished(Action<NativeSaveAttempt> callback)
        {
            if (Finished) callback(this);
            else _finished += callback;
        }

        internal void Complete(Exception failure)
        {
            if (Finished) return;
            Failure = failure;
            Finished = true;
            Action<NativeSaveAttempt> callback = _finished;
            _finished = null;
            callback?.Invoke(this);
        }
    }

    /// <summary>
    /// 原版编辑页的未保存提醒。打开页面和每次保存成功后记录快照；点「退出」（或点遮罩关闭）
    /// 时与内存比较，有差异就先询问，不直接关闭。任何环节出错都退回原版行为，绝不拦住退出。
    /// </summary>
    internal static class UnsavedEditGuard
    {
        private sealed class State
        {
            internal EditorSnapshot Baseline;
            internal EditorSnapshot Accepted;
            internal NativeSaveAttempt LastSave;
        }

        private static readonly ConditionalWeakTable<BaseView, State> States =
            new ConditionalWeakTable<BaseView, State>();
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);
        private static BaseView _closingWithoutPrompt;

        /// <summary>以当前内存为「已保存」状态（打开页面、剧情图保存成功后调用）。</summary>
        internal static void Capture(BaseView view)
        {
            if (view == null || !NativeEditorPages.IsGuarded(view)) return;
            try
            {
                EditorSnapshot snapshot = NativeEditorPages.Capture(view);
                State state = States.GetValue(view, _ => new State());
                state.Baseline = snapshot;
                state.Accepted = null;
                state.LastSave = null;
            }
            catch (Exception e)
            {
                States.Remove(view);
                WarnOnce("capture:" + view.GetType().Name,
                    "记录「" + NativeEditorPages.DisplayName(view)
                    + "」的已保存状态失败，本页退出时不做未保存提醒：" + e.Message);
            }
        }

        internal static bool TryGetUnsavedChanges(BaseView view, out EditorChangeSummary summary)
        {
            summary = null;
            State state;
            if (view == null || !States.TryGetValue(view, out state) || state.Baseline == null)
                return false;
            EditorSnapshot baseline = state.Baseline;
            EditorSnapshot accepted = state.Accepted;
            NativeSaveAttempt save = state.LastSave;
            if (save != null && !save.Finished && save.Written != null)
            {
                // 刚点过保存、写盘还没结束：按这次保存的内容比较；写盘失败会退回旧状态。
                baseline = save.Written;
                accepted = save.Before;
            }
            summary = EditorChangeTracking.Compare(baseline, NativeEditorPages.Capture(view), accepted);
            return summary.HasChanges;
        }

        internal static NativeSaveAttempt LastSave(BaseView view)
        {
            State state;
            return view != null && States.TryGetValue(view, out state) ? state.LastSave : null;
        }

        /// <summary>UIMgr.CloseView 前缀：返回 false 表示先弹提醒、暂不关闭。</summary>
        internal static bool BeforeClose(BaseView view, bool sendCloseEvt, bool closeInBackground)
        {
            if (view == null || !NativeEditorPages.IsGuarded(view)
                || ReferenceEquals(view, _closingWithoutPrompt)
                || view.viewState != ViewState.Opened)
                return true;
            if (UnsavedEditDialog.IsShowingFor(view)) return false;

            string page = NativeEditorPages.DisplayName(view);
            EditorChangeSummary summary;
            int dropped;
            try
            {
                GiftFormInput.CommitFocused(view);
                if (!TryGetUnsavedChanges(view, out summary)) return true;
                dropped = NativeEditorPages.CountDroppedOnSave(view);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnsavedEdits] 检查「" + page + "」的未保存修改失败，按原版直接退出：" + e);
                return true;
            }

            Plugin.Log?.LogInfo("[UnsavedEdits] 「" + page + "」有未保存的修改，退出前询问："
                                + summary.Describe() + "（" + Preview(summary.ChangedKeys) + "）");
            UnsavedEditDialog.Show(view, page, summary, dropped,
                () => CloseWithoutPrompt(view, sendCloseEvt, closeInBackground));
            return false;
        }

        /// <summary>不经提醒直接关闭（「不保存退出」、保存完成后退出，以及需要绕过提醒的自动化脚本）。</summary>
        internal static void CloseWithoutPrompt(BaseView view, bool sendCloseEvt = true,
            bool closeInBackground = false)
        {
            BaseView previous = _closingWithoutPrompt;
            _closingWithoutPrompt = view;
            try { UIMgr.CloseView(view, sendCloseEvt, closeInBackground); }
            finally { _closingWithoutPrompt = previous; }
        }

        internal static NativeSaveAttempt BeginSave(BaseView view, bool probeAsyncVoid)
        {
            State state;
            if (view == null || !States.TryGetValue(view, out state) || state.Baseline == null)
                return null;
            try
            {
                var attempt = new NativeSaveAttempt(NativeEditorPages.Capture(view));
                SynchronizationContext current = SynchronizationContext.Current;
                if (probeAsyncVoid && current != null && !(current is SaveCompletionProbe))
                {
                    attempt.Probe = new SaveCompletionProbe(current);
                    SynchronizationContext.SetSynchronizationContext(attempt.Probe);
                }
                return attempt;
            }
            catch (Exception e)
            {
                WarnOnce("save-before:" + view.GetType().Name,
                    "保存前记录「" + NativeEditorPages.DisplayName(view) + "」失败，退出时可能误报未保存：" + e.Message);
                return null;
            }
        }

        /// <summary>原版 async void OnClickSave 的后缀：写盘何时结束由探针报告。</summary>
        internal static void EndAsyncVoidSave(BaseView view, NativeSaveAttempt attempt, bool ranOriginal)
        {
            if (attempt == null) return;
            SaveCompletionProbe probe = attempt.Probe;
            if (probe != null && ReferenceEquals(SynchronizationContext.Current, probe))
                SynchronizationContext.SetSynchronizationContext(probe.Inner);
            if (!ranOriginal) return;
            if (!Register(view, attempt)) return;
            if (probe == null || !probe.Started)
            {
                // 没观察到异步保存（没有同步上下文等）：退回旧行为，立即视为已保存。
                if (probe != null)
                    WarnOnce("probe", "未能跟踪原版保存的写盘进度，保存后立即按已保存处理。");
                Finish(view, attempt, null);
                return;
            }
            probe.Seal(() => Finish(view, attempt, probe.Failure));
        }

        /// <summary>原版 async Task Save() 的后缀。</summary>
        internal static void EndTaskSave(BaseView view, NativeSaveAttempt attempt, Task task, bool ranOriginal)
        {
            if (attempt == null || !ranOriginal) return;
            if (!Register(view, attempt)) return;
            SynchronizationContext context = SynchronizationContext.Current;
            if (task == null || context == null)
            {
                Finish(view, attempt, null);
                return;
            }
            task.ContinueWith(done =>
            {
                Exception failure = done.IsFaulted
                    ? done.Exception.GetBaseException()
                    : done.IsCanceled ? new TaskCanceledException(done) : null;
                context.Post(_ => Finish(view, attempt, failure), null);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>
        /// 选中对话时其它插件可能改写它（例如 StudentAgeLatex 把烘焙正文换回 $ 源码）。
        /// 只要选中前它仍是已保存的样子，就把选中造成的变化一并视为已保存。
        /// 返回选中前的指纹；不需要跟踪时返回 null。
        /// </summary>
        internal static string BeforeSelectTalk(ModEvtEditView view, TalkCfg talk)
        {
            State state;
            if (view == null || talk == null || !States.TryGetValue(view, out state) || state.Baseline == null)
                return null;
            try
            {
                if (!NativeEditorPages.HasUniqueTalkId(view, talk)) return null;
                string key = NativeEditorPages.TalkKey(talk);
                string fingerprint = EditorChangeTracking.Fingerprint(talk);
                foreach (EditorSnapshot snapshot in SavedSnapshots(state))
                    if (Matches(snapshot, key, fingerprint)) return fingerprint;
                return null;
            }
            catch (Exception e)
            {
                WarnOnce("select-before", "选中对话时比较已保存状态失败：" + e.Message);
                return null;
            }
        }

        internal static void AfterSelectTalk(ModEvtEditView view, TalkCfg talk, string before)
        {
            State state;
            if (before == null || talk == null || !States.TryGetValue(view, out state)) return;
            try
            {
                EditorRecordState after = NativeEditorPages.TalkState(talk);
                if (string.Equals(after.Fingerprint, before, StringComparison.Ordinal)) return;
                string key = NativeEditorPages.TalkKey(talk);
                foreach (EditorSnapshot snapshot in SavedSnapshots(state))
                    if (Matches(snapshot, key, before)) snapshot.Replace(key, after);
            }
            catch (Exception e)
            {
                WarnOnce("select-after", "选中对话后更新已保存状态失败：" + e.Message);
            }
        }

        private static bool Register(BaseView view, NativeSaveAttempt attempt)
        {
            State state;
            if (!States.TryGetValue(view, out state)) return false;
            try
            {
                attempt.Written = NativeEditorPages.Capture(view);
            }
            catch (Exception e)
            {
                WarnOnce("save-after:" + view.GetType().Name,
                    "保存后记录「" + NativeEditorPages.DisplayName(view) + "」失败，退出时可能误报未保存：" + e.Message);
                return false;
            }
            state.LastSave = attempt;
            return true;
        }

        private static void Finish(BaseView view, NativeSaveAttempt attempt, Exception failure)
        {
            State state;
            if (failure != null)
            {
                Plugin.Log?.LogWarning("[UnsavedEdits] 「" + NativeEditorPages.DisplayName(view)
                                       + "」保存没有完成，仍按未保存处理：" + failure.GetType().Name + ": " + failure.Message);
            }
            else if (States.TryGetValue(view, out state) && ReferenceEquals(state.LastSave, attempt))
            {
                state.Baseline = attempt.Written;
                state.Accepted = attempt.Before;
            }
            attempt.Complete(failure);
        }

        private static IEnumerable<EditorSnapshot> SavedSnapshots(State state)
        {
            if (state.Baseline != null) yield return state.Baseline;
            if (state.Accepted != null) yield return state.Accepted;
            NativeSaveAttempt save = state.LastSave;
            if (save != null && !save.Finished)
            {
                if (save.Written != null) yield return save.Written;
                if (save.Before != null) yield return save.Before;
            }
        }

        private static bool Matches(EditorSnapshot snapshot, string key, string fingerprint)
        {
            EditorRecordState record;
            return snapshot != null && snapshot.TryGet(key, out record)
                   && string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal);
        }

        private static string Preview(IReadOnlyList<string> keys)
        {
            const int limit = 8;
            var shown = new List<string>();
            for (int i = 0; i < keys.Count && i < limit; i++) shown.Add(keys[i]);
            string text = string.Join("、", shown.ToArray());
            return keys.Count > limit ? text + " 等 " + keys.Count + " 条" : text;
        }

        private static void WarnOnce(string key, string message)
        {
            if (Warned.Add(key)) Plugin.Log?.LogWarning("[UnsavedEdits] " + message);
        }
    }

    [HarmonyPatch]
    internal static class UnsavedEditBaselinePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ModEvtEditView), "OnOpen");
            yield return AccessTools.Method(typeof(ModNormalEditView), "OnOpen");
            yield return AccessTools.Method(typeof(ModPersonEditView), "OnOpen");
            yield return AccessTools.Method(typeof(ModEndingEditView), "OnOpen");
        }

        // 等其它 OnOpen 补丁都改完内存再记录。
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(BaseView __instance, bool __runOriginal)
        {
            if (__runOriginal) UnsavedEditGuard.Capture(__instance);
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "Select", typeof(TalkCfg))]
    internal static class UnsavedEditSelectPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(ModEvtEditView __instance, TalkCfg _cfg, out string __state)
        {
            __state = UnsavedEditGuard.BeforeSelectTalk(__instance, _cfg);
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ModEvtEditView __instance, TalkCfg _cfg, string __state)
        {
            UnsavedEditGuard.AfterSelectTalk(__instance, _cfg, __state);
        }
    }

    /// <summary>事件对话页、结局页：原版保存是 async void OnClickSave。</summary>
    [HarmonyPatch]
    internal static class UnsavedEditAsyncVoidSavePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ModEvtEditView), "OnClickSave");
            yield return AccessTools.Method(typeof(ModEndingEditView), "OnClickSave");
        }

        // 最先执行：在其它插件的保存前处理（如 LaTeX 烘焙）改写内存之前记录。
        [HarmonyPriority(Priority.First)]
        private static void Prefix(BaseView __instance, out NativeSaveAttempt __state)
        {
            __state = UnsavedEditGuard.BeginSave(__instance, true);
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(BaseView __instance, bool __runOriginal, NativeSaveAttempt __state)
        {
            UnsavedEditGuard.EndAsyncVoidSave(__instance, __state, __runOriginal);
        }
    }

    /// <summary>配置页、人物页：真正写盘的是 async Task Save()（保存按钮和「编辑事件」都会调用）。</summary>
    [HarmonyPatch]
    internal static class UnsavedEditTaskSavePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ModNormalEditView), "Save");
            yield return AccessTools.Method(typeof(ModPersonEditView), "Save");
        }

        [HarmonyPriority(Priority.First)]
        private static void Prefix(BaseView __instance, out NativeSaveAttempt __state)
        {
            __state = UnsavedEditGuard.BeginSave(__instance, false);
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(BaseView __instance, bool __runOriginal, Task __result,
            NativeSaveAttempt __state)
        {
            UnsavedEditGuard.EndTaskSave(__instance, __state, __result, __runOriginal);
        }
    }

    [HarmonyPatch(typeof(UIMgr), nameof(UIMgr.CloseView), typeof(BaseView), typeof(bool), typeof(bool))]
    internal static class UnsavedEditClosePatch
    {
        private static bool Prefix(BaseView _view, bool _sendCloseEvt, bool _closeInBackground)
        {
            return UnsavedEditGuard.BeforeClose(_view, _sendCloseEvt, _closeInBackground);
        }
    }
}
