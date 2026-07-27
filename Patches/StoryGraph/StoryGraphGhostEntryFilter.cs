using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Config;
using HarmonyLib;
using Sdk;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 「启动残影」过滤：修复"删除剧情节点→保存→退出剧情→重进后节点复活"。
    ///
    /// 根因（2026-07-27 确诊）：游戏启动时把启用 Mod 的配置一次性合并进全局
    /// Cfg.TalkCfgMap / Cfg.OptionCfgMap（ModCtrl.Load → MergeCfgsAsync →
    /// Cfg.AddItem），此后整局不再移除任何条目；而 ModEvtEditView.LoadTalkOptionCfg
    /// 重建编辑列表时，会把「事件段（事件号*1000+1..999 / 选项 *100+1..99）内、
    /// 不在 Mod JSON 里」的全局表条目并进列表——本意是给覆盖内置事件的 Mod 补上
    /// 未覆盖的原生对话，副作用是把本局删除并保存的节点从启动副本里原样复活。
    /// 复活体还会连锁触发两个陷阱：被 BuildProtectedIds（全局−持久化∪原生）误判
    /// 为"内置基底"从而在剧情图里无法再删；且任何一次保存（原版全量 upsert 或
    /// 剧情图 ApplyTalkChanges 写会话全部对话）都会把它重新写回 JSON。
    ///
    /// 修复规则：编辑列表只保留 id ∈ 当前 Mod JSON ∪ 原生快照。原生快照来自
    /// StoryGraphConfigProvenance（Mod 合并前捕获；启动顺序 Cfg.Init(Async) →
    /// Cfg.Init(AsyncDLC) → ModCtrl.Load，故快照天然包含 DLC——覆盖 DLC 事件不受
    /// 影响）。其余条目即启动残影（或另一启用 Mod 在同段的撞号条目），一律不进
    /// 编辑列表：编辑器只信 JSON 与原生基底。快照未捕获时（本局没有任何 Mod 合并）
    /// SnapshotNative 降级返回当前全局全集，过滤自动退化为空操作（fail-open）。
    ///
    /// 有意不动 Cfg.TalkCfgMap 本体：全局表里的启动旧链条仍互相引用，抠掉单个 id
    /// 会让本局内正式触发该事件时 CommonEvtMgr 直接 KeyNotFound（CommonEvtMgr.cs:77
    /// 为无保护索引）。"本局游玩用启动旧数据、重启后生效"的既有语义保持不变。
    ///
    /// 有意不patch ModPreviewTipsView（原版预览的同款并集）：预览剧情链以 JSON 条目
    /// 优先，正常删除后的残影不可达，纯属惰性数据；而 Load 内部直接 OpenView 预览
    /// 视图，postfix 时机上无法保证先于消费。
    /// </summary>
    internal static class StoryGraphGhostEntryFilter
    {
        /// <summary>
        /// 残影判定：正编号、不属于当前 Mod JSON、也不属于原生（含 DLC）快照。
        /// id ≤ 0 一律不判残影——非法编号交由会话校验报告，不在加载期静默吞掉。
        /// </summary>
        internal static bool IsGhostId(
            int id, HashSet<int> ownedJsonIds, HashSet<int> nativeIds)
        {
            return id > 0
                   && (ownedJsonIds == null || !ownedJsonIds.Contains(id))
                   && (nativeIds == null || !nativeIds.Contains(id));
        }

        /// <summary>
        /// 就地过滤对话列表；返回被移除的残影编号（保持原列表相对顺序）。
        /// null 槽位原样保留——它们不是残影，留给会话校验去报告。
        /// </summary>
        internal static List<int> FilterTalkList(
            List<TalkCfg> talks, HashSet<int> ownedJsonIds, HashSet<int> nativeIds)
        {
            var removed = new List<int>();
            if (talks == null || talks.Count == 0) return removed;
            for (int i = talks.Count - 1; i >= 0; i--)
            {
                TalkCfg talk = talks[i];
                if (talk == null) continue;
                if (!IsGhostId(talk.id, ownedJsonIds, nativeIds)) continue;
                talks.RemoveAt(i);
                removed.Add(talk.id);
            }
            removed.Reverse();
            return removed;
        }

        /// <summary>就地过滤选项字典；返回被移除的残影键。判定用字典键——
        /// 原版加载即按 value.id 作键写入，键才是后续所有查找的事实身份。</summary>
        internal static List<int> FilterOptionMap(
            Dictionary<int, OptionCfg> options,
            HashSet<int> ownedJsonIds, HashSet<int> nativeIds)
        {
            var removed = new List<int>();
            if (options == null || options.Count == 0) return removed;
            foreach (KeyValuePair<int, OptionCfg> pair in options)
                if (IsGhostId(pair.Key, ownedJsonIds, nativeIds))
                    removed.Add(pair.Key);
            for (int i = 0; i < removed.Count; i++) options.Remove(removed[i]);
            removed.Sort();
            return removed;
        }

        /// <summary>
        /// 读取 Mod 配置 JSON 的编号全集（value.id，与原版加载的块匹配口径一致）。
        /// 文件不存在返回空集；解析异常向上抛，由补丁层整体 fail-open——损坏的
        /// JSON 会让原版加载自己先炸，这里绝不能把"读不懂"当成"全是残影"。
        /// </summary>
        internal static HashSet<int> CollectOwnedTalkIds(string jsonPath)
        {
            return CollectOwnedIds<TalkCfg>(jsonPath, cfg => cfg.id);
        }

        internal static HashSet<int> CollectOwnedOptionIds(string jsonPath)
        {
            return CollectOwnedIds<OptionCfg>(jsonPath, cfg => cfg.id);
        }

        private static HashSet<int> CollectOwnedIds<T>(
            string jsonPath, Func<T, int> idOf)
        {
            var result = new HashSet<int>();
            if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
                return result;
            Dictionary<string, T> map = ModCtrl.DeserializeJsonToCfgMap<T>(jsonPath);
            if (map == null) return result;
            foreach (KeyValuePair<string, T> pair in map)
            {
                if (pair.Value == null) continue;
                int id = idOf(pair.Value);
                if (id > 0) result.Add(id);
            }
            return result;
        }

        internal static string DescribeIds(List<int> ids)
        {
            if (ids == null || ids.Count == 0) return "无";
            const int previewCount = 20;
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Count && i < previewCount; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(ids[i]);
            }
            if (ids.Count > previewCount)
                sb.Append("……共 ").Append(ids.Count).Append(" 个");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 事件对话编辑器加载后的残影过滤挂点。任何失败都整体放弃过滤并保留原版
    /// 行为（fail-open）：过滤器坏了顶多退回"会复活"的旧现状，绝不能清空编辑器。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "LoadTalkOptionCfg")]
    internal static class ModEvtEditViewGhostFilterPatch
    {
        private static readonly FieldInfo TalkCfgsField =
            AccessTools.Field(typeof(ModEvtEditView), "talkCfgs");
        private static readonly FieldInfo OptionCfgsField =
            AccessTools.Field(typeof(ModEvtEditView), "optionCfgs");
        private static readonly HashSet<string> Warnings = new HashSet<string>();

        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                Apply(__instance);
            }
            catch (Exception e)
            {
                WarnOnce("apply",
                    "启动残影过滤失败，本次按原版行为展示（已删除节点可能临时复活，"
                    + "重启游戏后自然消失）：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Apply(ModEvtEditView view)
        {
            if (view == null) return;
            if (TalkCfgsField == null || OptionCfgsField == null)
            {
                WarnOnce("fields",
                    "当前游戏版本找不到 ModEvtEditView.talkCfgs/optionCfgs，"
                    + "启动残影过滤已停用。");
                return;
            }

            var talks = TalkCfgsField.GetValue(view) as List<TalkCfg>;
            var options = OptionCfgsField.GetValue(view) as Dictionary<int, OptionCfg>;
            bool hasTalks = talks != null && talks.Count > 0;
            bool hasOptions = options != null && options.Count > 0;
            if (!hasTalks && !hasOptions) return;

            string modRoot;
            string rootError;
            if (!EvtStoryGraphViewAccess.TryGetModRoot(view, out modRoot, out rootError))
            {
                WarnOnce("modRoot", "启动残影过滤读不到 Mod 根目录，已跳过：" + rootError);
                return;
            }

            // 与原版加载完全同源的路径口径（LocalizationMgr.Lang，而非写死 zh-cn）。
            string cfgDir = Path.Combine(modRoot, "Cfgs/" + LocalizationMgr.Lang);
            HashSet<int> ownedTalkIds = StoryGraphGhostEntryFilter
                .CollectOwnedTalkIds(Path.Combine(cfgDir, "TalkCfg.json"));
            HashSet<int> ownedOptionIds = StoryGraphGhostEntryFilter
                .CollectOwnedOptionIds(Path.Combine(cfgDir, "OptionCfg.json"));
            HashSet<int> nativeTalkIds =
                StoryGraphConfigProvenance.SnapshotNativeTalkIds(ownedTalkIds);
            HashSet<int> nativeOptionIds =
                StoryGraphConfigProvenance.SnapshotNativeOptionIds(ownedOptionIds);

            List<int> removedTalks = StoryGraphGhostEntryFilter.FilterTalkList(
                talks, ownedTalkIds, nativeTalkIds);
            List<int> removedOptions = StoryGraphGhostEntryFilter.FilterOptionMap(
                options, ownedOptionIds, nativeOptionIds);
            if (removedTalks.Count == 0 && removedOptions.Count == 0) return;

            Plugin.Log?.LogWarning(
                "[StoryGraph.GhostFilter] 已过滤启动残影：对话 "
                + StoryGraphGhostEntryFilter.DescribeIds(removedTalks)
                + "；选项 " + StoryGraphGhostEntryFilter.DescribeIds(removedOptions)
                + "。它们不在当前 Mod 的配置 JSON 里（通常是本局删除并保存过的节点），"
                + "仅存在于启动时合并的运行时副本中；不再并回编辑列表，防止已删节点复活。");
        }

        private static void WarnOnce(string key, string message)
        {
            if (!Warnings.Add(key)) return;
            Plugin.Log?.LogWarning("[StoryGraph.GhostFilter] " + message);
        }
    }
}
