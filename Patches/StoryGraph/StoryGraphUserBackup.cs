using System;
using System.IO;
using BepInEx;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图保存前版本（*.storygraph.bak）存在作者本机的 BepInEx/config 下，不写进作品目录。
    /// 上传前清扫只在游戏内上传且加载了本插件时生效；作品目录被整包转发、或在未加载插件时
    /// 上传，写在里面的备份（上一版剧情全文）就会随作品分发给所有订阅者。
    /// </summary>
    internal static class StoryGraphUserBackup
    {
        internal const string Suffix = ".storygraph.bak";
        private const string LegacySuffix = ".storygraph.legacy.bak";

        internal const string DisplayRoot = "BepInEx/config/StudentAgeEditorPlus/StoryGraphBackups";

        internal static string RootDirectory
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Paths.ConfigPath))
                    throw new InvalidOperationException("BepInEx 配置目录不可用，无法确定剧情图备份位置。");
                return Path.Combine(Paths.ConfigPath, "StudentAgeEditorPlus", "StoryGraphBackups");
            }
        }

        /// <summary>
        /// 目录名取作品文件夹名，方便作者辨认；追加路径哈希，避免不同位置的同名作品互相覆盖。
        /// </summary>
        internal static string DirectoryFor(string modRoot)
        {
            if (string.IsNullOrWhiteSpace(modRoot))
                throw new ArgumentException("作品目录为空。", nameof(modRoot));
            string full = Path.GetFullPath(modRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(full);
            if (string.IsNullOrWhiteSpace(name)) name = "mod";
            string hash = StoryGraphWorkspace.StableHash(full.ToLowerInvariant()).Substring(0, 8);
            return Path.Combine(RootDirectory, name + "-" + hash);
        }

        internal static string PathFor(string modRoot, string configPath)
        {
            return Path.Combine(DirectoryFor(modRoot), Path.GetFileName(configPath) + Suffix);
        }

        internal static void Publish(string source, string modRoot, string configPath)
        {
            string destination = PathFor(modRoot, configPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }

        /// <summary>
        /// 0.4.22 及更早版本把备份写在配置文件旁边。移到备份目录保留而不删除，
        /// 作品目录里不再留存。返回移动后的位置；没有旧备份时返回 null。
        /// </summary>
        internal static string MoveLegacyOut(string modRoot, string configPath)
        {
            string legacy = configPath + Suffix;
            if (!File.Exists(legacy)) return null;
            string directory = DirectoryFor(modRoot);
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(
                directory, Path.GetFileName(configPath) + LegacySuffix);
            if (File.Exists(destination))
                destination += "." + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(legacy, destination);
            return destination;
        }
    }
}
