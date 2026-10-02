using System;
using System.Reflection;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Hint;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 退出编辑页时的三选一提醒：不保存退出 / 继续编辑 / 保存并退出。
    /// 直接打开原版确认框（CommonComfirmView），只在它的按钮行里多复制一个「不保存退出」，
    /// 外观、遮罩、动画和音效都与原版一致；提示和保存状态写在原版底部提示行。
    /// </summary>
    internal static class UnsavedEditDialog
    {
        internal const string DiscardButtonName = "btn_editorplus_discard";

        private const float ButtonWidth = 200f;
        private const float ButtonSpacing = 20f;
        private const float OpenTimeoutSeconds = 5f;
        private const float SaveTimeoutSeconds = 20f;

        private sealed class Session
        {
            internal BaseView Host;
            internal string Page;
            internal string Note;
            internal Action CloseHost;
            internal float Requested;
            internal CommonComfirmView View;
            internal UIButton Save;
            internal UIButton Discard;
            internal GameObject DiscardObject;
            internal HorizontalLayoutGroup Layout;
            internal float Spacing;
            internal Vector2 OkSize;
            internal Vector2 CancelSize;
            internal NativeSaveAttempt Waiting;
            internal float WaitStarted;
            internal bool Released;
        }

        private static Session _current;

        internal static bool IsShowingFor(BaseView view)
        {
            Session session = _current;
            if (session == null || session.Released || !ReferenceEquals(session.Host, view)) return false;
            // 原版确认框的加载回调一直没来时，允许作者再点一次退出重新弹出。
            return session.View != null || Time.unscaledTime - session.Requested < OpenTimeoutSeconds;
        }

        internal static void Show(BaseView host, string page, EditorChangeSummary summary, int dropped,
            Action closeHost)
        {
            Dismiss();
            string message = "「" + page + "」有未保存的修改：" + summary.Describe()
                             + "。\n直接退出会丢失这些修改。";
            // 原版确认框同一时间只有一个实例，别处的确认框还开着时不能借用，否则会顶掉它的回调。
            if (UIMgr.IsViewOpeningOrOpened<CommonComfirmView>())
            {
                Plugin.Log?.LogWarning("[UnsavedEdits] 已有确认框打开，暂不退出「" + page + "」。");
                return;
            }

            var session = new Session
            {
                Host = host,
                Page = page,
                CloseHost = closeHost,
                Requested = Time.unscaledTime,
                Note = dropped > 0 ? "其中 " + dropped + " 条记录编号为 0，原版保存时会丢弃，请先填写编号" : null,
            };
            _current = session;
            try
            {
                UIMgr.OpenView<CommonComfirmView>(UILayerType.None, () => OnOpened(session), new object[]
                {
                    message, null, null, true, "还有修改没有保存", "保存并退出", "继续编辑", session.Note, true,
                });
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnsavedEdits] 打开原版确认框失败：" + e);
                _current = null;
                ShowFallback(page, summary, closeHost);
            }
        }

        internal static void Dismiss()
        {
            Session session = _current;
            _current = null;
            if (session?.View != null && session.View.viewState == ViewState.Opened)
                UIMgr.CloseView(session.View);
        }

        private static void OnOpened(Session session)
        {
            var view = UIMgr.GetView<CommonComfirmView>(false) as CommonComfirmView;
            if (view == null || view.gameObject == null) return;
            if (!IsCurrent(session))
            {
                UIMgr.CloseView(view);
                return;
            }
            session.View = view;
            try
            {
                Decorate(session, view);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnsavedEdits] 给确认框加「不保存退出」失败，改为两个按钮：" + e);
                Restore(session);
                try
                {
                    view.txt_ok.text = "不保存退出";
                    view.btn_ok.AddClick(() => OnDiscard(session));
                }
                catch (Exception inner)
                {
                    Plugin.Log?.LogError("[UnsavedEdits] 确认框无法使用，留在编辑页：" + inner);
                    Dismiss();
                }
            }
        }

        private static void Decorate(Session session, CommonComfirmView view)
        {
            session.Layout = view.group_btn.GetComponent<HorizontalLayoutGroup>();
            session.OkSize = view.btn_ok.transform.sizeDelta;
            session.CancelSize = view.btn_cancel.transform.sizeDelta;
            if (session.Layout != null)
            {
                session.Spacing = session.Layout.spacing;
                session.Layout.spacing = ButtonSpacing;
            }

            // 复制原版「取消」按钮作为「不保存退出」，放在最左边；运行时点击回调不会被复制。
            GameObject discard = UnityEngine.Object.Instantiate(view.btn_cancel.gameObject, view.group_btn, false);
            discard.name = DiscardButtonName;
            discard.transform.SetSiblingIndex(view.btn_cancel.gameObject.transform.GetSiblingIndex());
            session.DiscardObject = discard;
            Text label = discard.GetComponentInChildren<Text>(true);
            if (label != null) label.text = "不保存退出";
            session.Discard = new UIButton(discard);
            session.Discard.AddClick(() => OnDiscard(session));
            session.Save = view.btn_ok;
            view.btn_ok.AddClick(() => OnSave(session));

            // 原版面板宽 700，三颗 220 宽的按钮放不下，统一收窄一些。
            SetWidth(view.btn_ok.transform, ButtonWidth);
            SetWidth(view.btn_cancel.transform, ButtonWidth);
            SetWidth(session.Discard.transform, ButtonWidth);

            // 原版确认框的界面对象会回收复用；隐藏时必须把改动还原，不能带进下一个原版确认框。
            var watcher = discard.AddComponent<UnsavedEditDialogWatcher>();
            watcher.Tick = () => Tick(session);
            watcher.Gone = () => Restore(session);
            LayoutRebuilder.ForceRebuildLayoutImmediate(view.group_btn);
        }

        private static void SetWidth(RectTransform rect, float width)
        {
            if (rect != null) rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
        }

        private static void Restore(Session session)
        {
            if (session.Released) return;
            session.Released = true;
            if (ReferenceEquals(_current, session)) _current = null;
            CommonComfirmView view = session.View;
            if (view == null) return;
            if (session.Layout != null) session.Layout.spacing = session.Spacing;
            if (view.btn_ok?.transform != null && session.OkSize != Vector2.zero)
                view.btn_ok.transform.sizeDelta = session.OkSize;
            if (view.btn_cancel?.transform != null && session.CancelSize != Vector2.zero)
                view.btn_cancel.transform.sizeDelta = session.CancelSize;
            if (session.DiscardObject != null) UnityEngine.Object.Destroy(session.DiscardObject);
        }

        private static void Tick(Session session)
        {
            if (!IsCurrent(session)) return;
            BaseView host = session.Host;
            if (host == null || host.viewState != ViewState.Opened || host.gameObject == null)
            {
                Dismiss();
                return;
            }
            if (session.Waiting != null && Time.unscaledTime - session.WaitStarted > SaveTimeoutSeconds)
            {
                session.Waiting = null;
                SetInteractable(session, true);
                SetStatus(session, "保存迟迟没有完成，可以再试一次或继续编辑");
            }
        }

        private static void OnSave(Session session)
        {
            if (!IsCurrent(session) || session.Waiting != null) return;
            NativeSaveAttempt previous = UnsavedEditGuard.LastSave(session.Host);
            try
            {
                NativeEditorPages.InvokeSave(session.Host);
            }
            catch (Exception e)
            {
                Exception inner = e is TargetInvocationException && e.InnerException != null
                    ? e.InnerException : e;
                Plugin.Log?.LogError("[UnsavedEdits] 调用原版保存失败：" + inner);
                SetStatus(session, "保存失败：" + Short(inner.Message) + "，修改仍保留");
                return;
            }

            NativeSaveAttempt attempt = UnsavedEditGuard.LastSave(session.Host);
            if (attempt == null || ReferenceEquals(attempt, previous))
            {
                SetStatus(session, "这次没有保存（原因见屏幕提示），修改仍保留");
                return;
            }
            session.Waiting = attempt;
            session.WaitStarted = Time.unscaledTime;
            SetInteractable(session, false);
            SetStatus(session, "正在保存……");
            attempt.OnFinished(done => OnSaveFinished(session, done));
        }

        private static void OnSaveFinished(Session session, NativeSaveAttempt attempt)
        {
            if (!IsCurrent(session) || !ReferenceEquals(session.Waiting, attempt)) return;
            session.Waiting = null;
            if (attempt.Failure != null)
            {
                SetInteractable(session, true);
                SetStatus(session, "保存失败：" + Short(attempt.Failure.Message) + "，修改仍保留，可以重试");
                return;
            }
            Plugin.Log?.LogInfo("[UnsavedEdits] 「" + session.Page + "」已保存，退出。");
            Dismiss();
            session.CloseHost();
        }

        private static void OnDiscard(Session session)
        {
            if (!IsCurrent(session) || session.Waiting != null) return;
            Plugin.Log?.LogInfo("[UnsavedEdits] 作者选择不保存，退出「" + session.Page + "」。");
            Dismiss();
            session.CloseHost();
        }

        private static bool IsCurrent(Session session) =>
            session != null && !session.Released && ReferenceEquals(_current, session);

        private static void SetInteractable(Session session, bool interactable)
        {
            if (session.Save != null) session.Save.interactable = interactable;
            if (session.Discard != null) session.Discard.interactable = interactable;
        }

        /// <summary>写在原版底部提示行（带提示图标，单行）；清空时恢复编号为 0 的提醒。</summary>
        private static void SetStatus(Session session, string text)
        {
            Text bottom = session.View?.txt_bot;
            if (bottom == null) return;
            bottom.text = string.IsNullOrEmpty(text) ? session.Note : text;
            bottom.gameObject.SetActive(!string.IsNullOrEmpty(bottom.text));
        }

        private static string Short(string message)
        {
            message = (message ?? string.Empty).Replace('\n', ' ').Trim();
            return message.Length > 40 ? message.Substring(0, 40) + "…" : message;
        }

        private static void ShowFallback(string page, EditorChangeSummary summary, Action closeHost)
        {
            try
            {
                HintHelper.ShowConfirm(
                    "「" + page + "」有未保存的修改：" + summary.Describe() + "。\n不保存直接退出会丢失这些修改。",
                    () => closeHost(), null, _showCloseBtn: true, "还有修改没有保存",
                    "不保存退出", "继续编辑", null, _enableCloseByRightClick: true);
            }
            catch (Exception e)
            {
                // 两种提醒都弹不出来时按原版直接退出，不能让作者被困在页面里。
                Plugin.Log?.LogError("[UnsavedEdits] 原版确认框也无法显示，按原版直接退出：" + e);
                closeHost();
            }
        }
    }

    internal sealed class UnsavedEditDialogWatcher : MonoBehaviour
    {
        internal Action Tick;
        internal Action Gone;

        private void Update() => Tick?.Invoke();

        private void OnDisable() => Gone?.Invoke();

        private void OnDestroy() => Gone?.Invoke();
    }
}
