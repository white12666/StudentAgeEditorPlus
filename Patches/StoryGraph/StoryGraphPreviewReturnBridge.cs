using System;
using HarmonyLib;
using View.Evt;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 把剧情图的临时隐藏状态绑定到本次 PreviewTalkView 生命周期。使用弱引用，
    /// 即使事件编辑器在预览过程中被销毁，OnClose 也不会把失效窗口重新显示。
    /// </summary>
    internal static class StoryGraphPreviewReturnBridge
    {
        private static WeakReference _window = new WeakReference(null);

        internal static void Register(StoryGraphWindow window)
        {
            _window = new WeakReference(window);
        }

        internal static void Cancel(StoryGraphWindow window)
        {
            if (!ReferenceEquals(_window.Target, window)) return;
            _window = new WeakReference(null);
            window?.ResumeAfterTalkPreview();
        }

        internal static void Forget(StoryGraphWindow window)
        {
            if (ReferenceEquals(_window.Target, window))
                _window = new WeakReference(null);
        }

        internal static void Resume()
        {
            var window = _window.Target as StoryGraphWindow;
            _window = new WeakReference(null);
            window?.ResumeAfterTalkPreview();
        }
    }

    [HarmonyPatch(typeof(PreviewTalkView), "OnClose")]
    internal static class StoryGraphPreviewClosePatch
    {
        private static void Postfix()
        {
            try
            {
                StoryGraphPreviewReturnBridge.Resume();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StoryGraph.Preview.Resume] " + e);
            }
        }
    }
}
