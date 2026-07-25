using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Sdk;
using StudentAgeSocialRoles;
using View.Main;

namespace StudentAgeSocialRoleRuntime
{
    /// <summary>
    /// 玩家端轻量运行库。只解析可社交角色资料标记并修正社交资料页，
    /// 不包含任何编辑器 UI；作为独立共享依赖发布，过渡期才临时随作品携带。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = SocialRoleRuntimeContract.PluginGuid;
        public const string PluginName = "StudentAge Social Role Runtime";
        public const string PluginVersion = "0.2.1";

        internal static ManualLogSource Log;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            try
            {
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll(typeof(Plugin).Assembly);
                LogPatchStatus();
                Log.LogInfo($"{PluginName} v{PluginVersion} 已加载。");
            }
            catch (Exception ex)
            {
                _harmony?.UnpatchSelf();
                Log.LogError($"{PluginName} 无法安装必要补丁，Runtime 已停用：{ex}");
            }
        }

        private static void LogPatchStatus()
        {
            MethodBase[] targets =
            {
                AccessTools.Method(typeof(RoleMgr), nameof(RoleMgr.GetClassName),
                    new[] { typeof(int) }),
                AccessTools.DeclaredMethod(typeof(DetailSocialView), "RefreshProfile", Type.EmptyTypes),
                AccessTools.Method(typeof(UIMgr), "Init", Type.EmptyTypes),
            };
            int installed = targets.Count(target =>
            {
                HarmonyLib.Patches info = target == null ? null : Harmony.GetPatchInfo(target);
                return info != null &&
                    (info.Prefixes.Any(patch => patch.owner == PluginGuid) ||
                     info.Postfixes.Any(patch => patch.owner == PluginGuid));
            });
            if (installed == targets.Length)
                Log.LogInfo($"[Patch] Social Role Runtime 关键补丁自检通过：{installed}/{targets.Length}");
            else
                throw new MissingMethodException(
                    $"Social Role Runtime 关键补丁安装不完整：{installed}/{targets.Length}");
        }

        private void OnDestroy()
        {
            // StudentAge 会在首个 Unity 场景建立时销毁 BepInEx_Manager 上的插件组件，
            // 但游戏和后续 UI 仍继续运行。不能在这里 UnpatchSelf，否则玩家端资料补丁
            // 会在 Mod、存档和社交界面加载前被静默卸载。
            Log?.LogWarning("[Lifecycle] Social Role Runtime 插件组件已销毁；Harmony 补丁继续运行。");
        }
    }
}
