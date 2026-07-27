using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 游玩路径的崩溃恢复钩子：编辑器界面各自的恢复入口只在打开编辑器时执行，
    /// 但强退后玩家可能直接开始游玩——ModCtrl.LoadModCfgs 读盘前若不先恢复，
    /// 半提交的 Talk/Option（或人物成对表的半新半旧状态）会被合并进内存。
    /// Prefix 挂在 async 方法的启动桩上，调用时同步执行，先于任何文件读取。
    /// </summary>
    [HarmonyPatch(typeof(ModCtrl), "LoadModCfgs")]
    internal static class ModLoadRecoveryPatch
    {
        private static void Prefix(string _path)
        {
            // 任何异常都不得阻断游戏加载：恢复失败时游戏按现状读盘，
            // 与插件未安装时的行为一致。
            try
            {
                if (string.IsNullOrWhiteSpace(_path)) return;
                // _path 形如 <modRoot>/Cfgs/zh-cn，反推两级得到 modRoot。
                string langDir = Path.GetFullPath(_path);
                string cfgsDir = Path.GetDirectoryName(langDir);
                string modRoot = cfgsDir != null
                    ? Path.GetDirectoryName(cfgsDir)
                    : null;
                if (string.IsNullOrWhiteSpace(modRoot)) return;

                RecoverStoryGraph(modRoot, "[ModLoadRecovery]");
                RecoverPersonPair(
                    Path.Combine(langDir, "PersonCfg.json"),
                    Path.Combine(langDir, "PersonGrowCfg.json"),
                    "[ModLoadRecovery]");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ModLoadRecovery] " + e);
            }
        }

        /// <summary>剧情图双文件事务恢复；返回 false 表示日志仍滞留。</summary>
        internal static bool RecoverStoryGraph(string modRoot, string logTag)
        {
            try
            {
                if (!StoryGraphEditPersistence.HasPendingTransaction(modRoot))
                    return true;
                string error;
                bool quarantined;
                if (StoryGraphEditPersistence.TryRecoverPendingTransaction(
                        modRoot, out quarantined, out error))
                {
                    Plugin.Log?.LogWarning(logTag + (quarantined
                        ? " 剧情图旧保存事务无法自动恢复，已隔离放行，配置按磁盘现状读取："
                        : " 已在读取 Mod 配置前恢复未完成的剧情图保存事务：") + modRoot);
                    return true;
                }
                Plugin.Log?.LogError(logTag + " 剧情图保存事务恢复失败（"
                    + modRoot + "）：" + error);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError(logTag + " " + e);
                return false;
            }
        }

        /// <summary>人物成对保存（PersonCfg/PersonGrowCfg）恢复；无未决事务时空跑。</summary>
        internal static bool RecoverPersonPair(
            string personPath, string growPath, string logTag)
        {
            try
            {
                bool recovered;
                string error;
                if (AtomicFilePairTransaction.Recover(
                        personPath, growPath, out recovered, out error))
                {
                    if (recovered)
                        Plugin.Log?.LogWarning(logTag
                            + " 已在读取 Mod 配置前成对恢复人物配置：" + personPath);
                    return true;
                }
                Plugin.Log?.LogError(logTag + " 人物成对保存恢复失败（"
                    + personPath + "）：" + error);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError(logTag + " " + e);
                return false;
            }
        }
    }

    /// <summary>
    /// 工坊上传前清扫插件瞬态残留并转移本地备份：上传把整个 modRoot 交给
    /// SteamUGC.SetItemContent（SteamPlatform.UpdateMod:646），事务临时文件与
    /// .bak（内含上一版剧情全文）否则会随工坊分发给所有订阅者。
    /// SteamCreateMod 成功后同样进入 SteamUploadMod，两条路径都被覆盖。
    /// </summary>
    [HarmonyPatch(typeof(ModPageUploadView), "SteamUploadMod")]
    internal static class ModUploadCleanupPatch
    {
        private static readonly FieldInfo ModRootField =
            AccessTools.Field(typeof(ModPageUploadView), "modRoot");

        private static readonly string[] PersonTransientSuffixes =
        {
            AtomicFilePairTransaction.PendingSuffix,
            AtomicFilePairTransaction.NewSuffix,
            AtomicFilePairTransaction.OldSuffix,
            AtomicFilePairTransaction.AbsentSuffix,
            AtomicFilePairTransaction.LockSuffix,
            ".saep-personsave.restore",
        };

        private static void Prefix(ModPageUploadView __instance)
        {
            // 清理失败绝不阻断上传；最坏情况与插件未安装时相同（残留随包分发）。
            try
            {
                string modRoot = __instance != null && ModRootField != null
                    ? ModRootField.GetValue(__instance) as string
                    : null;
                if (string.IsNullOrWhiteSpace(modRoot)
                    || !Directory.Exists(modRoot))
                    return;
                CleanupBeforeUpload(Path.GetFullPath(modRoot));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ModUploadCleanup] " + e);
            }
        }

        private static void CleanupBeforeUpload(string modRoot)
        {
            // 插件瞬态文件都在配置目录下。
            string cfgsDir = Path.Combine(modRoot, "Cfgs");
            if (!Directory.Exists(cfgsDir)) return;

            // 未决事务先尝试恢复；失败时保留日志与该家族全部文件（含 .bak），
            // 只警告不阻断——删掉事务备份会让下次自动恢复永远无法完成。
            bool storyGraphSafe = ModLoadRecoveryPatch.RecoverStoryGraph(
                modRoot, "[ModUploadCleanup]");
            bool personSafe = !Directory.Exists(cfgsDir)
                              || RecoverPendingPersonPairs(cfgsDir);
            if (!storyGraphSafe)
                SafeToast("剧情图保存事务未能自动恢复，已保留全部恢复文件；"
                    + "本次上传会包含这些残留，建议先重新打开事件完成恢复后再上传。");
            if (!personSafe)
                SafeToast("人物配置成对保存事务未能自动恢复，已保留全部恢复文件；"
                    + "本次上传会包含这些残留，建议先打开人物编辑器完成恢复后再上传。");

            string backupRoot = Path.Combine(
                Paths.ConfigPath, "StudentAgeEditorPlus", "UploadBackups",
                new DirectoryInfo(modRoot).Name);
            int cleaned = 0;
            int moved = 0;
            int failed = 0;
            foreach (string file in EnumerateCleanupTargets(cfgsDir))
            {
                string name = Path.GetFileName(file);
                try
                {
                    if (IsStoryGraphTransient(name))
                    {
                        if (!storyGraphSafe) continue;
                        // 此刻无活动日志，孤儿 .tx 事务备份可能是隔离放行时
                        // 承诺“仍保留在原目录”的最后恢复材料（首次保存即崩溃
                        // 时连 .storygraph.bak 都没有），转移而非删除；
                        // .tmp./.swap. 形状纯属垃圾，维持删除。
                        if (IsStoryGraphTxBackup(name))
                        {
                            MoveToBackupRoot(file, modRoot, backupRoot);
                            moved++;
                        }
                        else
                        {
                            File.Delete(file);
                            cleaned++;
                        }
                    }
                    else if (IsPersonTransient(name))
                    {
                        if (!personSafe) continue;
                        File.Delete(file);
                        cleaned++;
                    }
                    else if (IsUserBackup(name))
                    {
                        if (name.EndsWith(".storygraph.bak",
                                StringComparison.OrdinalIgnoreCase)
                            && !storyGraphSafe) continue;
                        if (name.EndsWith(AtomicFilePairTransaction.BackupSuffix,
                                StringComparison.OrdinalIgnoreCase)
                            && !personSafe) continue;
                        MoveToBackupRoot(file, modRoot, backupRoot);
                        moved++;
                    }
                    else if (IsStoryGraphStaleJournal(name))
                    {
                        // 隔离产物内含作者机绝对路径与文件指纹，不应随工坊
                        // 分发；按用户备份同等转移，保留人工恢复线索。恢复器
                        // 从不读取 stale 文件，转移与活动日志的恢复互不影响。
                        MoveToBackupRoot(file, modRoot, backupRoot);
                        moved++;
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    Plugin.Log?.LogWarning(
                        "[ModUploadCleanup] 处理 " + file + " 失败：" + e.Message);
                }
            }

            if (cleaned > 0 || moved > 0)
            {
                Plugin.Log?.LogInfo("[ModUploadCleanup] 上传前清理 " + cleaned
                    + " 个临时文件，转移 " + moved + " 个本地备份到 " + backupRoot
                    + (failed > 0 ? "；" + failed + " 个文件处理失败（见日志）" : "") + "。");
                SafeToast("已清理 " + cleaned + " 个临时文件、转移 " + moved
                    + " 个本地备份，不会随上传分发"
                    + (failed > 0 ? "（另有 " + failed + " 个文件处理失败，见日志）" : "")
                    + "。");
            }
            else if (failed > 0)
            {
                SafeToast("上传前清理有 " + failed + " 个文件处理失败，请查看日志。");
            }
        }

        /// <summary>逐个 .pending 找到成对文件并恢复；返回 false 表示仍有滞留。</summary>
        private static bool RecoverPendingPersonPairs(string cfgsDir)
        {
            bool allRecovered = true;
            try
            {
                foreach (string pending in Directory.GetFiles(
                    cfgsDir, "*" + AtomicFilePairTransaction.PendingSuffix,
                    SearchOption.AllDirectories))
                {
                    string firstPath = pending.Substring(
                        0, pending.Length
                           - AtomicFilePairTransaction.PendingSuffix.Length);
                    // pending 建在成对事务的第一个文件（PersonCfg.json）上；
                    // 形状不符说明不是本插件写的事务，保守跳过并保留原样。
                    if (!firstPath.EndsWith("PersonCfg.json",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        allRecovered = false;
                        continue;
                    }
                    string growPath = Path.Combine(
                        Path.GetDirectoryName(firstPath) ?? cfgsDir,
                        "PersonGrowCfg.json");
                    if (!ModLoadRecoveryPatch.RecoverPersonPair(
                            firstPath, growPath, "[ModUploadCleanup]"))
                        allRecovered = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ModUploadCleanup] " + e);
                allRecovered = false;
            }
            return allRecovered;
        }

        /// <summary>
        /// 剧情图事务瞬态残留：TalkCfg.json.storygraph.tmp.&lt;id&gt;、
        /// ….storygraph.tx.&lt;id&gt;.bak、….storygraph.swap.&lt;id&gt;.bak，以及
        /// 日志族自身的写盘临时件 .storygraph.transaction.json.tmp.&lt;guid&gt;/
        /// ….swap.&lt;guid&gt;（硬崩溃在 WriteJournal 的 WriteDurable 与 finally
        /// 之间时滞留，恢复路径的 CleanupJournalFile 不清日志族）。
        /// 只认精确形状，绝不会命中任何 *Cfg.json、.storygraph.bak，
        /// 也不会命中必须原样保留的活动日志 .storygraph.transaction.json。
        /// </summary>
        private static bool IsStoryGraphTransient(string fileName)
        {
            int index = fileName.IndexOf(
                ".storygraph.", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            string tail = fileName.Substring(index);
            if (StartsWithAndLonger(tail, ".storygraph.tmp.")) return true;
            if (IsStoryGraphTxBackup(fileName)) return true;
            if (StartsWithAndLonger(tail, ".storygraph.swap.")
                && tail.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                && tail.Length > ".storygraph.swap.".Length + ".bak".Length)
                return true;
            if (StartsWithAndLonger(tail, ".storygraph.transaction.json.tmp."))
                return true;
            if (StartsWithAndLonger(tail, ".storygraph.transaction.json.swap."))
                return true;
            return false;
        }

        /// <summary>事务备份形状 ….storygraph.tx.&lt;id&gt;.bak——上传清扫时转移而非删除。</summary>
        private static bool IsStoryGraphTxBackup(string fileName)
        {
            int index = fileName.IndexOf(
                ".storygraph.tx.", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            string tail = fileName.Substring(index);
            return tail.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                   && tail.Length > ".storygraph.tx.".Length + ".bak".Length;
        }

        /// <summary>隔离产物 .storygraph.transaction.stale-&lt;guid&gt;.json（QuarantineStaleJournal）。</summary>
        private static bool IsStoryGraphStaleJournal(string fileName)
        {
            int index = fileName.IndexOf(
                ".storygraph.transaction.stale-",
                StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            string tail = fileName.Substring(index);
            return tail.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                   && tail.Length > ".storygraph.transaction.stale-".Length
                                    + ".json".Length;
        }

        /// <summary>前缀命中且其后还有内容（事务编号等），排除裸后缀名文件。</summary>
        private static bool StartsWithAndLonger(string value, string prefix)
        {
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                   && value.Length > prefix.Length;
        }

        /// <summary>清扫的枚举范围：Cfgs/ 全量。</summary>
        private static IEnumerable<string> EnumerateCleanupTargets(string cfgsDir)
        {
            if (Directory.Exists(cfgsDir))
                foreach (string file in Directory.GetFiles(
                    cfgsDir, "*", SearchOption.AllDirectories))
                    yield return file;
        }

        private static bool IsPersonTransient(string fileName)
        {
            for (int i = 0; i < PersonTransientSuffixes.Length; i++)
            {
                if (fileName.EndsWith(PersonTransientSuffixes[i],
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsUserBackup(string fileName)
        {
            return fileName.EndsWith(".storygraph.bak",
                       StringComparison.OrdinalIgnoreCase)
                   || fileName.EndsWith(AtomicFilePairTransaction.BackupSuffix,
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>保留 modRoot 内的相对路径转移备份；重名时追加时间戳避免覆盖旧备份。</summary>
        private static void MoveToBackupRoot(
            string file, string modRoot, string backupRoot)
        {
            string full = Path.GetFullPath(file);
            string prefix = modRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string relative = full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length)
                : Path.GetFileName(full);
            string destination = Path.Combine(backupRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (File.Exists(destination))
                destination += "." + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(full, destination);
        }

        private static void SafeToast(string message)
        {
            try { ToastHelper.Toast(message); }
            catch { }
        }
    }
}
