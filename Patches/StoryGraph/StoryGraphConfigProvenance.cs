using System;
using System.Collections.Generic;
using System.Linq;
using Config;
using HarmonyLib;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 记录 ModCtrl 把创意工坊/本地 Mod 合并进 Cfg 之前的原生配置编号。
    ///
    /// 游戏完成 Mod 加载后，Cfg.*CfgMap 同时包含原版、DLC 与所有启用 Mod；
    /// 因此“当前编号存在于 Cfg”不能证明它是游戏内置项。删除保护必须同时知道
    /// 当前 Mod 实际拥有的 JSON 编号和合并前的原生编号。
    /// </summary>
    internal static class StoryGraphConfigProvenance
    {
        private static readonly object Sync = new object();
        private static HashSet<int> _nativeTalkIds = new HashSet<int>();
        private static HashSet<int> _nativeOptionIds = new HashSet<int>();
        private static HashSet<int> _nativeEventIds = new HashSet<int>();
        private static bool _captured;

        internal static void CaptureBeforeModMerge()
        {
            lock (Sync)
            {
                if (_captured) return;
                _nativeTalkIds = SnapshotKeys(() => Cfg.TalkCfgMap);
                _nativeOptionIds = SnapshotKeys(() => Cfg.OptionCfgMap);
                _nativeEventIds = SnapshotKeys(() => Cfg.EvtCfgMap);
                _captured = true;
            }
            Plugin.Log?.LogInfo(
                "[StoryGraph.ConfigProvenance] 已在 Mod 配置合并前记录原生编号："
                + "Talk " + _nativeTalkIds.Count
                + "，Option " + _nativeOptionIds.Count
                + "，Evt " + _nativeEventIds.Count + "。");
        }

        internal static HashSet<int> SnapshotNativeTalkIds(
            IEnumerable<int> ownedIds)
        {
            return SnapshotNative(
                _nativeTalkIds, _captured, SafeCurrentTalkIds(), ownedIds);
        }

        internal static HashSet<int> SnapshotNativeOptionIds(
            IEnumerable<int> ownedIds)
        {
            return SnapshotNative(
                _nativeOptionIds, _captured, SafeCurrentOptionIds(), ownedIds);
        }

        internal static HashSet<int> SnapshotNativeEventIds(
            IEnumerable<int> ownedIds)
        {
            return SnapshotNative(
                _nativeEventIds, _captured, SafeCurrentEventIds(), ownedIds);
        }

        /// <summary>
        /// 当前会话不可删除的编号 = 其它已加载配置 + 真正的原生配置。
        /// 当前 Mod 自己的持久化编号即使已被游戏合并进 Cfg，也不应被误判为内置。
        /// </summary>
        internal static HashSet<int> BuildProtectedIds(
            IEnumerable<int> globalIds,
            IEnumerable<int> ownedIds,
            IEnumerable<int> nativeIds)
        {
            var result = new HashSet<int>(
                globalIds ?? Enumerable.Empty<int>());
            if (ownedIds != null) result.ExceptWith(ownedIds);
            if (nativeIds != null) result.UnionWith(nativeIds);
            result.RemoveWhere(id => id <= 0);
            return result;
        }

        private static HashSet<int> SnapshotNative(
            HashSet<int> captured,
            bool hasCapture,
            IEnumerable<int> currentGlobal,
            IEnumerable<int> ownedIds)
        {
            lock (Sync)
            {
                if (hasCapture) return new HashSet<int>(captured);
            }

            // 插件随 BepInEx 在游戏启动前装好 MergeCfgsAsync 前缀，而
            // activeMods 为空时 ModCtrl.Load 提前 return、MergeCfgsAsync
            // 根本不执行。因此“未捕获”意味着尚无任何 Mod 合并进 Cfg，
            // 当前全局编号就是纯原生全集，直接整体返回。绝不能再减去
            // ownedIds：Mod JSON 中的内置覆盖副本编号本来就属于原生集，
            // 减掉会让“内置基底禁止伪删除”保护静默失效（覆盖被删后
            // 以原版原文复活）。理论上的热重载边缘（合并发生过但前缀
            // 没装）会把 Mod 自身已合并的条目也判为原生——只会过度保护、
            // 拒绝删除（fail-closed），可接受；ownedIds 参数保留仅为
            // 调用方语义完整，这里有意不使用。
            var fallback = new HashSet<int>(
                currentGlobal ?? Enumerable.Empty<int>());
            fallback.RemoveWhere(id => id <= 0);
            return fallback;
        }

        private static HashSet<int> SafeCurrentTalkIds()
        {
            return SnapshotKeys(() => Cfg.TalkCfgMap);
        }

        private static HashSet<int> SafeCurrentOptionIds()
        {
            return SnapshotKeys(() => Cfg.OptionCfgMap);
        }

        private static HashSet<int> SafeCurrentEventIds()
        {
            return SnapshotKeys(() => Cfg.EvtCfgMap);
        }

        private static HashSet<int> SnapshotKeys<T>(
            Func<Dictionary<int, T>> getter)
        {
            try
            {
                Dictionary<int, T> map = getter != null ? getter() : null;
                return map != null
                    ? new HashSet<int>(map.Keys.Where(id => id > 0))
                    : new HashSet<int>();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.ConfigProvenance] 读取配置编号失败：" + e.Message);
                return new HashSet<int>();
            }
        }
    }

    [HarmonyPatch(typeof(ModCtrl), "MergeCfgsAsync")]
    internal static class StoryGraphConfigProvenancePatch
    {
        private static void Prefix()
        {
            StoryGraphConfigProvenance.CaptureBeforeModMerge();
        }
    }
}
