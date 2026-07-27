using System;
using UnityEngine;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图打开期间的 Toast 路由。游戏根 Canvas 是 ScreenSpaceCamera，而剧情图
    /// 是 ScreenSpaceOverlay：Unity 里 Overlay 画布永远渲染在所有相机空间画布之
    /// 上，原版 ToastView 无论把 sortingOrder 提到多高都会被剧情图整体盖住（旧的
    /// StoryGraphToastLayerBridge 排序方案因此从未生效，已随本类删除）。
    /// 剧情图注册回调后，提示改画在剧情图自己的画布上；「预览本句」暂隐画布期间
    /// 由窗口暂存、恢复显示时补发。未注册或回调拒收时回落原版 Toast。
    /// </summary>
    internal static class StoryGraphToastRouter
    {
        private static Func<string, bool> _sink;
        private static string _lastFallbackMessage;
        private static float _lastFallbackTime;

        internal static void Register(Func<string, bool> sink)
        {
            _sink = sink;
        }

        internal static void Unregister(Func<string, bool> sink)
        {
            if (ReferenceEquals(_sink, sink)) _sink = null;
        }

        /// <summary>剧情图可见（或暂隐待恢复）时画到图内画布，否则原版 Toast。</summary>
        internal static void Show(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            Func<string, bool> sink = _sink;
            if (sink != null)
            {
                try
                {
                    if (sink(message)) return;
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning(
                        "[StoryGraph.Toast] 图内提示失败，回落原版 Toast："
                        + e.GetType().Name + ": " + e.Message);
                }
            }
            // 回落消息永远先落日志：原版 ToastCtrl.CanToast 在试玩被中途打断的
            // 个别时序下可能卡在 false（消息静默入队不显示），日志是唯一可
            // 追查的痕迹。同时记下最后一条，供剧情图刚建画布时补发。
            Plugin.Log?.LogInfo("[StoryGraph.Toast] " + message);
            _lastFallbackMessage = message;
            _lastFallbackTime = Time.unscaledTime;
            try { ToastHelper.Toast(message); }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Toast] 原版 Toast 失败："
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 剧情图刚建好画布时补发窗口期内被回落的最后一条消息。典型场景：打开
        /// 剧情图的那次点击在 pointer-down 阶段先触发了输入框 onEndEdit 校验
        /// 提示（早于 sink 注册），提示落在相机空间 ToastView 上，随即被新建的
        /// Overlay 盖住整个生命周期。
        /// </summary>
        internal static void ReplayRecentFallback(float windowSeconds)
        {
            Func<string, bool> sink = _sink;
            string message = _lastFallbackMessage;
            if (sink == null || string.IsNullOrEmpty(message)) return;
            if (Time.unscaledTime - _lastFallbackTime > windowSeconds) return;
            _lastFallbackMessage = null;
            try { sink(message); }
            catch { }
        }
    }
}
