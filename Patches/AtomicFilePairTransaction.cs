using System;
using System.IO;
using System.Text;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 两个配置文件的可恢复事务。新内容先在目标目录完整落盘，再记录 pending，
    /// 最后逐个原子替换；若进程在两个替换之间退出，下次调用 Recover 会把整对文件
    /// 恢复到事务前版本。最后一次成功保存前的文件另保留为 .saep-personsave.bak。
    /// </summary>
    internal static class AtomicFilePairTransaction
    {
        internal const string NewSuffix = ".saep-personsave.new";
        internal const string OldSuffix = ".saep-personsave.old";
        internal const string AbsentSuffix = ".saep-personsave.absent";
        internal const string BackupSuffix = ".saep-personsave.bak";
        internal const string PendingSuffix = ".saep-personsave.pending";
        internal const string LockSuffix = ".saep-personsave.lock";

        private static readonly object Gate = new();

        internal static bool SavePair(
            string firstPath,
            string firstContent,
            string secondPath,
            string secondContent,
            out string error)
        {
            error = null;
            string activeLockPath = null;
            try
            {
                ValidatePaths(firstPath, secondPath);
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(firstPath)));
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(secondPath)));

                    string lockPath = Path.GetFullPath(firstPath) + LockSuffix;
                    activeLockPath = lockPath;
                    using (var lockStream = new FileStream(
                        lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        if (!RecoverCore(firstPath, secondPath, out _, out string recoverError))
                            throw new IOException("上次人物保存事务恢复失败：" + recoverError);

                        CleanupOrphans(firstPath, secondPath);
                        var first = new Entry(firstPath, firstContent);
                        var second = new Entry(secondPath, secondContent);
                        Prepare(first);
                        Prepare(second);
                        PreserveLastBackup(first);
                        PreserveLastBackup(second);

                        string pending = PendingPath(firstPath);
                        WriteDurable(pending + NewSuffix, "SAEP_PERSON_SAVE_V1");
                        File.Move(pending + NewSuffix, pending);

                        try
                        {
                            Commit(first);
                            Commit(second);

                            // pending 的删除是提交点。它之前若强退，下次会回滚旧文件；
                            // 它之后只剩无害的事务残留，可在下次保存时清理。
                            File.Delete(pending);
                            CleanupOrphans(firstPath, secondPath);
                            lockStream.Flush(true);
                            return true;
                        }
                        catch
                        {
                            if (!RecoverCore(firstPath, secondPath, out _, out string rollbackError))
                                throw new IOException("保存失败且自动回滚未完成：" + rollbackError);
                            throw;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                TryDeleteReleasedLock(activeLockPath);
            }
        }

        internal static bool Recover(
            string firstPath,
            string secondPath,
            out bool recovered,
            out string error)
        {
            recovered = false;
            error = null;
            string activeLockPath = null;
            try
            {
                ValidatePaths(firstPath, secondPath);
                lock (Gate)
                {
                    // 没有 pending 就没有需要恢复的目标变更；避免仅打开编辑器便创建锁文件。
                    if (!File.Exists(PendingPath(firstPath))) return true;

                    string firstDirectory = Path.GetDirectoryName(Path.GetFullPath(firstPath));
                    string secondDirectory = Path.GetDirectoryName(Path.GetFullPath(secondPath));
                    Directory.CreateDirectory(firstDirectory);
                    Directory.CreateDirectory(secondDirectory);
                    string lockPath = Path.GetFullPath(firstPath) + LockSuffix;
                    activeLockPath = lockPath;
                    using (var lockStream = new FileStream(
                        lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        bool result = RecoverCore(firstPath, secondPath, out recovered, out error);
                        lockStream.Flush(true);
                        return result;
                    }
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                TryDeleteReleasedLock(activeLockPath);
            }
        }

        private static bool RecoverCore(
            string firstPath,
            string secondPath,
            out bool recovered,
            out string error)
        {
            recovered = false;
            error = null;
            string pending = PendingPath(firstPath);
            if (!File.Exists(pending)) return true;

            try
            {
                Restore(new Entry(firstPath, null));
                Restore(new Entry(secondPath, null));
                File.Delete(pending);
                CleanupOrphans(firstPath, secondPath);
                recovered = true;
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static void ValidatePaths(string firstPath, string secondPath)
        {
            if (string.IsNullOrWhiteSpace(firstPath) || string.IsNullOrWhiteSpace(secondPath))
                throw new ArgumentException("人物配置保存路径为空。");
            string first = Path.GetFullPath(firstPath);
            string second = Path.GetFullPath(secondPath);
            if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("成对事务的两个目标路径不能相同。");
        }

        private static void Prepare(Entry entry)
        {
            DeleteIfExists(entry.NewPath);
            DeleteIfExists(entry.OldPath);
            DeleteIfExists(entry.AbsentPath);

            if (File.Exists(entry.Path))
                File.Copy(entry.Path, entry.OldPath, true);
            else
                WriteDurable(entry.AbsentPath, "absent");

            if (entry.Content != null)
                WriteDurable(entry.NewPath, entry.Content);
        }

        private static void PreserveLastBackup(Entry entry)
        {
            if (File.Exists(entry.OldPath))
                File.Copy(entry.OldPath, entry.BackupPath, true);
            else
                DeleteIfExists(entry.BackupPath);
        }

        private static void Commit(Entry entry)
        {
            if (entry.Content == null)
            {
                DeleteIfExists(entry.Path);
                return;
            }

            if (File.Exists(entry.Path))
                File.Replace(entry.NewPath, entry.Path, null, true);
            else
                File.Move(entry.NewPath, entry.Path);
        }

        private static void Restore(Entry entry)
        {
            if (File.Exists(entry.OldPath))
            {
                string restorePath = entry.Path + ".saep-personsave.restore";
                DeleteIfExists(restorePath);
                File.Copy(entry.OldPath, restorePath, true);
                if (File.Exists(entry.Path))
                    File.Replace(restorePath, entry.Path, null, true);
                else
                    File.Move(restorePath, entry.Path);
                return;
            }

            if (File.Exists(entry.AbsentPath))
            {
                DeleteIfExists(entry.Path);
                return;
            }

            throw new IOException("事务快照不完整，无法确认旧文件状态：" + entry.Path);
        }

        private static void CleanupOrphans(string firstPath, string secondPath)
        {
            CleanupEntry(new Entry(firstPath, null));
            CleanupEntry(new Entry(secondPath, null));
            DeleteIfExists(PendingPath(firstPath) + NewSuffix);
        }

        private static void CleanupEntry(Entry entry)
        {
            DeleteIfExists(entry.NewPath);
            DeleteIfExists(entry.OldPath);
            DeleteIfExists(entry.AbsentPath);
            DeleteIfExists(entry.Path + ".saep-personsave.restore");
        }

        private static string PendingPath(string firstPath)
        {
            return Path.GetFullPath(firstPath) + PendingSuffix;
        }

        private static void WriteDurable(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
            {
                writer.Write(content ?? string.Empty);
                writer.Flush();
                stream.Flush(true);
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static void TryDeleteReleasedLock(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { DeleteIfExists(path); }
            catch { /* 另一进程可能刚接管同一个锁；由它在释放时清理。 */ }
        }

        private sealed class Entry
        {
            internal readonly string Path;
            internal readonly string Content;
            internal readonly string NewPath;
            internal readonly string OldPath;
            internal readonly string AbsentPath;
            internal readonly string BackupPath;

            internal Entry(string path, string content)
            {
                Path = System.IO.Path.GetFullPath(path);
                Content = content;
                NewPath = Path + NewSuffix;
                OldPath = Path + OldSuffix;
                AbsentPath = Path + AbsentSuffix;
                BackupPath = Path + BackupSuffix;
            }
        }
    }
}
