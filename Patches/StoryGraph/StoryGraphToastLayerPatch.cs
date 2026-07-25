using System;
using HarmonyLib;
using Sdk;
using UnityEngine;
using View.Common;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图使用独立顶层 Overlay Canvas；原版 ToastView 虽位于 Foreground，
    /// 却仍是游戏根 Canvas 的子画布，跨根画布时可能被剧情图整体盖住。
    /// 剧情图打开期间只给 ToastView 自身增加 overrideSorting；关闭后恢复原状。
    /// </summary>
    internal static class StoryGraphToastLayerBridge
    {
        private static bool _active;
        private static int _sortingOrder;
        private static int _sortingLayerId;

        internal static void Activate(Canvas storyGraphCanvas)
        {
            if (storyGraphCanvas == null) return;
            _active = true;
            _sortingOrder =
                StoryGraphResourcePickerLogic.ToastSortingOrder(
                    storyGraphCanvas.sortingOrder);
            _sortingLayerId = storyGraphCanvas.sortingLayerID;
            TryElevateExistingToast();
        }

        internal static void Deactivate()
        {
            _active = false;
            try
            {
                ToastView toast = UIMgr.GetView<ToastView>(false) as ToastView;
                Restore(toast);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.ToastLayer] 恢复 Toast 排序失败："
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        internal static void Ensure(ToastView toast)
        {
            if (!_active || toast == null || toast.gameObject == null) return;
            try
            {
                Canvas canvas = toast.gameObject.GetComponent<Canvas>();
                bool added = canvas == null;
                if (added) canvas = toast.gameObject.AddComponent<Canvas>();

                StoryGraphToastCanvasMarker marker =
                    toast.gameObject.GetComponent<StoryGraphToastCanvasMarker>();
                if (marker == null)
                {
                    marker =
                        toast.gameObject.AddComponent<StoryGraphToastCanvasMarker>();
                    marker.Capture(canvas, added);
                }

                canvas.overrideSorting = true;
                canvas.sortingLayerID = _sortingLayerId;
                canvas.sortingOrder = _sortingOrder;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.ToastLayer] 提升 Toast 排序失败："
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void TryElevateExistingToast()
        {
            try
            {
                Ensure(UIMgr.GetView<ToastView>(false) as ToastView);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.ToastLayer] 查找 ToastView 失败："
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Restore(ToastView toast)
        {
            if (toast == null || toast.gameObject == null) return;
            StoryGraphToastCanvasMarker marker =
                toast.gameObject.GetComponent<StoryGraphToastCanvasMarker>();
            if (marker == null) return;
            marker.Restore();
            UnityEngine.Object.Destroy(marker);
        }
    }

    internal sealed class StoryGraphToastCanvasMarker : MonoBehaviour
    {
        private Canvas _canvas;
        private bool _addedCanvas;
        private bool _originalOverrideSorting;
        private int _originalSortingOrder;
        private int _originalSortingLayerId;

        internal void Capture(Canvas canvas, bool addedCanvas)
        {
            _canvas = canvas;
            _addedCanvas = addedCanvas;
            if (canvas == null) return;
            _originalOverrideSorting = canvas.overrideSorting;
            _originalSortingOrder = canvas.sortingOrder;
            _originalSortingLayerId = canvas.sortingLayerID;
        }

        internal void Restore()
        {
            if (_canvas == null) return;
            _canvas.overrideSorting = _originalOverrideSorting;
            _canvas.sortingOrder = _originalSortingOrder;
            _canvas.sortingLayerID = _originalSortingLayerId;
            if (_addedCanvas)
                UnityEngine.Object.Destroy(_canvas);
            _canvas = null;
        }

        private void OnDestroy()
        {
            // ToastView 可能随游戏关闭而先销毁；恢复方法本身是幂等的。
            Restore();
        }
    }

    [HarmonyPatch(typeof(ToastView), nameof(ToastView.OnOpen))]
    internal static class StoryGraphToastViewOpenPatch
    {
        private static void Postfix(ToastView __instance)
        {
            StoryGraphToastLayerBridge.Ensure(__instance);
        }
    }
}
