using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace StudentAgeEditorPlus
{
    /// StudentAge MOD 编辑器增强插件（全新工程，取代已废弃的 StudentAgeModEditorFix）。
    /// 修复内容：
    ///   - 场景触发补全：让事件编辑器的「类型」下拉可选所有场景地图的进入/离开触发
    ///     （详见 Patches/SceneEventTypePatch）。
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.studentage.editorplus";
        public const string PluginName = "StudentAge Editor Plus";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log;
        internal static Harmony HarmonyInstance;

        private static ConfigEntry<bool> _runDiagnostic;

        private void Awake()
        {
            Log = Logger;

            // 排查工具：场景触发诊断（默认关闭）。需要时把配置项设为 true 再启动游戏。
            _runDiagnostic = Config.Bind(
                "Diagnostics", "RunSceneTriggerDiagnostic", false,
                "启动时 dump 场景触发机制诊断到 BepInEx/SceneTriggerDiag.txt（排查用，默认关闭）。");

            HarmonyInstance = new Harmony(PluginGuid);
            HarmonyInstance.PatchAll();

            if (_runDiagnostic.Value)
            {
                var diagGo = new GameObject("StudentAgeEditorPlus.Diagnostic");
                Object.DontDestroyOnLoad(diagGo);
                diagGo.hideFlags = HideFlags.HideAndDontSave;
                diagGo.AddComponent<SceneTriggerDiagnostic>();
                Log.LogInfo("场景触发诊断已启用。");
            }

            Log.LogInfo($"{PluginName} v{PluginVersion} 已加载。");
        }
    }
}
