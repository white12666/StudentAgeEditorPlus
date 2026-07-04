using System.Collections.Generic;
using System.Linq;
using Config;
using HarmonyLib;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 修复："场景事件只能改 JSON 才能触发"。
    ///
    /// 成因：场景进入/离开事件靠 EvtCfg.type 编码触发（进入=900+地图号，离开=800+地图号，
    /// 见 CommonEvtMgr.ShowSiteEvent）。但编辑器的 type 字段是受限下拉框
    /// (CfgPropertyRange=EvtTypeCfg)，下拉项只来自 Cfg.EvtTypeCfgMap。基础表只为
    /// 地图 {1-8,11,17} 定义了场景类型，其余 type==0 的地图（9/10/12/101-103 及 mod 自建图）
    /// 选不到对应类型 → 只能手改 JSON。
    ///
    /// 修复：在事件表编辑器构建字段（也就是构建 type 下拉）之前，把缺失的场景类型幂等
    /// 注入 EvtTypeCfgMap。原生下拉读的就是这张表，会自动多出这些选项，无需自绘 UI。
    /// 注入条目仅存在于作者当前会话；产出的 mod 只是 type=9xx，在原版客户端可正常触发，
    /// 玩家无需依赖本插件（ShowEvent 路径不查 EvtTypeCfgMap，已验证）。
    /// </summary>
    [HarmonyPatch(typeof(ModNormalEditView), "InitFields")]
    internal static class SceneEventTypePatch
    {
        private static void Prefix()
        {
            SceneEventTypes.EnsureRegistered();
        }
    }

    internal static class SceneEventTypes
    {
        // 进入后缀 / 离开后缀
        private const string EnterSuffix = "(进入)";
        private const string LeaveSuffix = "(离开)";

        private static bool _loggedOnce;

        /// <summary>
        /// 对每个 type==0 的地图，确保 EvtTypeCfgMap 含有其进入(900+id)/离开(800+id) 类型。
        /// 幂等：已存在则跳过。离开类型仅在 id<100 时注入（否则 800+id≥900 会与低编号地图的
        /// 进入类型撞号，这是原版编码本身的限制）。
        /// </summary>
        public static void EnsureRegistered()
        {
            var typeMap = Cfg.EvtTypeCfgMap;
            var mapMap = Cfg.MapCfgMap;
            if (typeMap == null || mapMap == null) return;

            // 克隆模板：优先用现有场景类型，缺失则用安全默认值
            EvtTypeCfg enterTpl = typeMap.TryGetValue(901, out var e) ? e : null;
            EvtTypeCfg leaveTpl = typeMap.TryGetValue(801, out var l) ? l : null;

            var added = new List<int>();

            foreach (var map in mapMap.Values)
            {
                if (map == null || map.type != 0) continue;

                int enterType = 900 + map.id;
                if (!typeMap.ContainsKey(enterType))
                {
                    typeMap[enterType] = Build(enterType, MapName(map) + EnterSuffix, enterTpl);
                    added.Add(enterType);
                }

                // 离开类型：仅 id<100 时安全（800+id < 900，不与进入段撞号）
                if (map.id < 100)
                {
                    int leaveType = 800 + map.id;
                    if (!typeMap.ContainsKey(leaveType))
                    {
                        typeMap[leaveType] = Build(leaveType, MapName(map) + LeaveSuffix, leaveTpl);
                        added.Add(leaveType);
                    }
                }
            }

            if (added.Count > 0 && !_loggedOnce)
            {
                _loggedOnce = true;
                Plugin.Log.LogInfo(
                    $"[SceneEventType] 已为缺失场景补全 {added.Count} 个事件类型: " +
                    string.Join(",", added.OrderBy(x => x)));
            }
        }

        private static string MapName(MapCfg map)
        {
            return string.IsNullOrEmpty(map.name) ? ("地图" + map.id) : map.name;
        }

        private static EvtTypeCfg Build(int id, string name, EvtTypeCfg tpl)
        {
            return new EvtTypeCfg
            {
                id = id,
                name = name,
                emptyIsTrue = tpl?.emptyIsTrue ?? 1,
                // 克隆模板的触发类别列表；缺模板则用 {1}（与基础场景类型一致）
                type = (tpl?.type != null) ? new List<int>(tpl.type) : new List<int> { 1 }
            };
        }
    }
}
