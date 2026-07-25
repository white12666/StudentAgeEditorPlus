using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using StudentAgeSocialRoles;
using UnityEngine;

namespace StudentAgeEditorPlus
{
    /// <summary>
    /// StudentAge MOD 编辑器增强插件（全新工程，取代已废弃的 StudentAgeModEditorFix）。
    /// 编辑器增强入口。除通用字段与事件编辑修复外，还为 NPC 人物编辑器增加
    /// 可社交角色的教师/成人/自定义资料；玩家端显示由独立 SocialRoleRuntime 负责。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(SocialRoleRuntimeContract.PluginGuid,
        BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.studentage.editorplus";
        public const string PluginName = "StudentAge Editor Plus";
        public const string PluginVersion = "0.3.1";

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
            LogRuntimeStatus();

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

        private static void LogRuntimeStatus()
        {
            if (!Chainloader.PluginInfos.TryGetValue(
                    SocialRoleRuntimeContract.PluginGuid, out var runtime))
            {
                Log.LogWarning(
                    "未检测到独立的 StudentAge Social Role Runtime。资料仍可编辑，" +
                    "但教师/成人/自定义资料无法在游戏内正确预览；请单独安装 Runtime。");
                return;
            }

            var minimum = new System.Version(SocialRoleRuntimeContract.MinimumVersionForV2);
            if (runtime.Metadata.Version.CompareTo(minimum) < 0)
            {
                Log.LogWarning(
                    $"检测到 Social Role Runtime v{runtime.Metadata.Version}，" +
                    $"低于 V{SocialRoleRuntimeContract.DataFormatVersion} 资料所需的最低版本 " +
                    $"v{minimum}，请升级独立 Runtime。");
                return;
            }

            var safeMinimum = new System.Version(SocialRoleRuntimeContract.MinimumSafeVersion);
            if (runtime.Metadata.Version.CompareTo(safeMinimum) < 0)
            {
                Log.LogWarning(
                    $"检测到 Social Role Runtime v{runtime.Metadata.Version}，资料显示可用，"
                    + $"但缺少 v{safeMinimum} 起提供的旧档生日修复；请升级 Runtime。");
                return;
            }

            Log.LogInfo(
                $"已检测到独立 Social Role Runtime v{runtime.Metadata.Version}，" +
                "游戏内资料预览可用。");
        }
    }
}
