using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using Config;
using UnityEngine;

namespace StudentAgeEditorPlus
{
    /// 一次性运行时诊断：核实"场景事件只能改 JSON 才能触发"的成因。
    /// 等游戏把配置表加载完后，dump 真实的 EvtTypeCfgMap / MapCfgMap、
    /// 场景触发类型(800/900+mapId)的覆盖缺口，以及 EvtCfg 字段的编辑器特性。
    /// 结论写入 BepInEx 日志 + 独立文件，方便取证。
    internal class SceneTriggerDiagnostic : MonoBehaviour
    {
        private bool _done;
        private float _elapsed;

        private void Update()
        {
            if (_done) return;
            _elapsed += Time.unscaledDeltaTime;
            // 等配置加载完（主菜单时即就绪）；最多等 120 秒
            if (!ConfigReady())
            {
                if (_elapsed > 120f)
                {
                    _done = true;
                    Plugin.Log.LogWarning("[Diag] 等待配置加载超时(120s)，放弃诊断。");
                }
                return;
            }
            _done = true;
            var sb = new StringBuilder();
            try { RunDiagnostic(sb); }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Diag] 诊断异常: {e}");
                sb.AppendLine("诊断异常: " + e.Message);
            }
            finally
            {
                try
                {
                    string outPath = Path.Combine(Paths.BepInExRootPath, "SceneTriggerDiag.txt");
                    File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
                    Plugin.Log.LogInfo($"[Diag] 诊断报告已写入: {outPath}");
                }
                catch (Exception e) { Plugin.Log.LogError($"[Diag] 写文件失败: {e}"); }
            }
        }

        private static bool ConfigReady()
        {
            try
            {
                return Cfg.EvtTypeCfgMap != null && Cfg.EvtTypeCfgMap.Count > 0
                    && Cfg.MapCfgMap != null && Cfg.MapCfgMap.Count > 0;
            }
            catch { return false; }
        }

        private void RunDiagnostic(StringBuilder sb)
        {
            void W(string s) { sb.AppendLine(s); Plugin.Log.LogInfo("[Diag] " + s); }

            W("==================== 场景触发机制诊断 ====================");

            // ---- 1. 真实 EvtTypeCfgMap ----
            var typeKeys = Cfg.EvtTypeCfgMap.Keys.OrderBy(k => k).ToList();
            W($"EvtTypeCfgMap 共 {typeKeys.Count} 条。");
            var sceneTypes = typeKeys.Where(k => k >= 800 && k <= 999).ToList();
            W($"其中场景触发类型(800-999) {sceneTypes.Count} 个: {string.Join(",", sceneTypes)}");
            foreach (var k in sceneTypes)
            {
                var c = Cfg.EvtTypeCfgMap[k];
                int mapOf = k % 100; // 900+mapId / 800+mapId
                string kind = k >= 900 ? "进入" : "离开";
                W($"    type={k} = [{kind}地图{mapOf}] name=\"{c.name}\"");
            }

            // ---- 2. 真实 MapCfgMap：哪些地图会走场景事件逻辑(type==0) ----
            // 见 CommonEvtMgr.ShowSiteEvent:1200 —— 仅 MapCfg.type==0 的地图才查 evtDict[900+mapId]
            var sceneCapableMaps = Cfg.MapCfgMap.Values
                .Where(m => m.type == 0)
                .Select(m => m.id).OrderBy(i => i).ToList();
            W($"MapCfgMap 共 {Cfg.MapCfgMap.Count} 张; 其中 type==0 (会跑场景进入/离开事件) 的有 {sceneCapableMaps.Count} 张: {string.Join(",", sceneCapableMaps)}");

            // ---- 3. 覆盖缺口：哪些场景地图在编辑器 type 下拉里选不到 ----
            var enterTypes = new HashSet<int>(typeKeys.Where(k => k >= 900 && k <= 999).Select(k => k - 900));
            var leaveTypes = new HashSet<int>(typeKeys.Where(k => k >= 800 && k <= 899).Select(k => k - 800));
            var missingEnter = sceneCapableMaps.Where(m => !enterTypes.Contains(m)).ToList();
            var missingLeave = sceneCapableMaps.Where(m => !leaveTypes.Contains(m)).ToList();

            W("---- 缺口（关键证据）----");
            foreach (var m in sceneCapableMaps)
            {
                string mapName = Cfg.MapCfgMap.TryGetValue(m, out var mc) ? mc.name : "?";
                bool hasEnter = enterTypes.Contains(m);
                bool hasLeave = leaveTypes.Contains(m);
                string mark = (hasEnter && hasLeave) ? "OK" : "!! 缺失";
                W($"    地图{m,-4} \"{mapName}\"  进入type{900 + m}:{(hasEnter ? "有" : "无")}  离开type{800 + m}:{(hasLeave ? "有" : "无")}  [{mark}]");
            }
            W($">>> 缺『进入』类型、编辑器 type 下拉选不到的地图: [{string.Join(",", missingEnter)}]");
            W($">>> 缺『离开』类型、编辑器 type 下拉选不到的地图: [{string.Join(",", missingLeave)}]");

            // ---- 4. 反射证明：EvtCfg.type 在编辑器里是受限下拉、无自由输入 ----
            W("---- EvtCfg 字段的编辑器特性(反射) ----");
            DumpFieldAttr<EvtCfg>(W, "type");
            DumpFieldAttr<EvtCfg>(W, "mapId");

            // type 字段是 dropdown(range!=null) 时，ModNormalEditView 会隐藏文本输入(input_value)，
            // 只能从 EvtTypeCfgMap 选。所以上面 missingEnter 里的地图，模组作者无法在 UI 里设置对应 type。
            W("---- 结论 ----");
            if (missingEnter.Count > 0 || missingLeave.Count > 0)
            {
                W($"确认问题存在：{missingEnter.Count} 张场景地图的『进入』触发类型不在 EvtTypeCfgMap 中，");
                W("而编辑器 type 字段是受限下拉(CfgPropertyRange=EvtTypeCfg)、不允许手填，");
                W("因此这些场景事件无法在编辑器里配置触发，只能手改 JSON 设置 type=900+地图号。");
            }
            else
            {
                W("未发现缺口：所有 type==0 地图都有对应场景触发类型（与预期不符，需复查）。");
            }
            W("=========================================================");
        }

        // 用 CustomAttributeData 检查特性的“声明”，不实例化特性本身——
        // 避免触发 CfgPropertyAttribute 构造函数里的 DescCtrl.GetTxt（主菜单时可能未就绪而 NRE）。
        private static void DumpFieldAttr<T>(Action<string> W, string fieldName)
        {
            var f = typeof(T).GetField(fieldName);
            if (f == null) { W($"    字段 {fieldName} 不存在"); return; }

            CustomAttributeData propAttr = null, rangeAttr = null;
            foreach (var a in CustomAttributeData.GetCustomAttributes(f))
            {
                string n = a.AttributeType.Name;
                if (n == "CfgPropertyAttribute") propAttr = a;
                else if (n == "CfgPropertyRangeAttribute") rangeAttr = a;
            }

            string propDesc = propAttr == null ? "无[CfgProperty]→编辑器不显示"
                : $"[CfgProperty 构造参数=({string.Join(", ", propAttr.ConstructorArguments.Select(x => x.Value))})]";
            string rangeDesc;
            if (rangeAttr == null)
            {
                rangeDesc = "无range→文本输入框(可自由填写)";
            }
            else
            {
                var args = rangeAttr.ConstructorArguments;
                string cfgType = args.Count > 0 && args[0].Value is Type t ? t.Name : "?";
                rangeDesc = $"range={cfgType}→受限下拉框(只能从该表选, 无自由输入)";
            }
            W($"    {fieldName}: {propDesc}; {rangeDesc}");
        }
    }
}
