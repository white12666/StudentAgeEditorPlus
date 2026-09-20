using System.Collections.Generic;
using System.IO;
using Config;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using Sdk.PlatformAPI;

namespace StudentAgeEditorPlus.Patches
{
    // 保留真正的原版/DLC基底，不从合并后的 Cfg 反推本作品已经删除的编号。
    // 仅复制字典索引，配置对象只读；不会改变本局玩家侧仍使用的启动配置。
    internal static class GiftCatalogProvenance
    {
        private static GiftCatalogSources _native;

        internal static void Capture()
        {
            if (_native != null) return;
            _native = new GiftCatalogSources
            {
                Talks = Copy(Cfg.TalkCfgMap),
                Events = Copy(Cfg.EvtCfgMap),
                People = Copy(Cfg.PersonCfgMap),
            };
        }

        private static Dictionary<int, T> Copy<T>(Dictionary<int, T> source) =>
            source != null ? new Dictionary<int, T>(source) : new Dictionary<int, T>();

        internal static GiftStoryCatalog Load(string modRoot)
        {
            ulong currentId = 0;
            string currentPackage = Path.GetFileName(modRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string manifestPath = Path.Combine(modRoot, "manifest.json");
            if (File.Exists(manifestPath))
            {
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                JToken metadata = manifest["metadata"];
                currentId = (ulong?)metadata?["id"] ?? 0;
                currentPackage = (string)metadata?["packageId"] ?? currentPackage;
            }
            var otherDirs = new List<string>();
            ModCtrl modCtrl = ModCtrl.Ins;
            if (modCtrl.activeMods != null)
            {
                foreach (ulong modId in modCtrl.activeMods)
                {
                    string loadedPackage = null;
                    if (modCtrl.modMetadatas != null && modCtrl.modMetadatas.TryGetValue(modId, out var metadata))
                        loadedPackage = metadata?.packageId;
                    string path = Platform.Current.GetModInstallPath(modId.ToString());
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                    string otherManifest = Path.Combine(path, "manifest.json");
                    if (string.IsNullOrEmpty(loadedPackage) && File.Exists(otherManifest))
                        loadedPackage = (string)JObject.Parse(File.ReadAllText(otherManifest))["metadata"]?["packageId"];
                    if (GiftStoryCatalog.IsCurrentMod(currentId, currentPackage, modId, loadedPackage)) continue;
                    if (string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar),
                        System.StringComparison.OrdinalIgnoreCase)) continue;
                    // 与 ModCtrl.LoadModAsset 保持一致：已安装作品固定加载 zh-cn，
                    // 不跟随作者当前界面的语言；当前作品仍使用原生编辑器的 Lang 路径。
                    otherDirs.Add(Path.Combine(path, "Cfgs", "zh-cn"));
                }
            }
            // 没有启用 Mod 时 MergeCfgsAsync 不会被调用，当前 Cfg 就是原生基底。
            return GiftStoryCatalog.Load(modRoot, _native, otherDirs);
        }
    }

    [HarmonyPatch(typeof(ModCtrl), "MergeCfgsAsync")]
    internal static class GiftCatalogProvenancePatch
    {
        private static void Prefix() => GiftCatalogProvenance.Capture();
    }
}
