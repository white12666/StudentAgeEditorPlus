using Config;
using StudentAgeEditorPlus.Patches;
using StudentAgeSocialRoles;

internal static class Program
{
    private static int failures;

    private static int Main()
    {
        Run("birthday validation", TestBirthdayValidation);
        Run("profile codec round-trip", TestProfileCodecRoundTrip);
        Run("atomic pair commit", TestAtomicPairCommit);
        Run("atomic pair delete", TestAtomicPairDelete);
        Run("failed second commit rolls back first", TestFailedSecondCommitRollsBackFirst);
        Run("interrupted pair recovery", TestInterruptedPairRecovery);
        Run("absent pair recovery", TestAbsentPairRecovery);

        Console.WriteLine(failures == 0
            ? "All social-role safety tests passed."
            : $"{failures} social-role safety test(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e)
        {
            failures++;
            Console.Error.WriteLine("FAIL " + name + ": " + e.Message);
        }
    }

    private static void TestBirthdayValidation()
    {
        Assert(SocialRoleProfileUtil.TryValidateBirthday(new[] { 1996, 2, 29 }, out _),
            "valid leap day rejected");
        Assert(!SocialRoleProfileUtil.TryValidateBirthday(new[] { 1995, 2, 29 }, out _),
            "invalid day accepted");
        Assert(!SocialRoleProfileUtil.TryValidateBirthday(new[] { 1995, 1 }, out _),
            "short birthday accepted");
    }

    private static void TestProfileCodecRoundTrip()
    {
        SocialRoleProfileData data = SocialRoleProfileCodec.NewDefault();
        data.Mode = SocialRoleProfileMode.Teacher;
        data.Stages[0].PrimaryValue = "鹅城一中";
        string note = SocialRoleProfileCodec.Write("作者原备注", data);
        var cfg = new PersonCfg { note = note, init = new List<int> { 2 } };
        Assert(SocialRoleProfileCodec.TryRead(cfg, out SocialRoleProfileData decoded),
            "written marker was unreadable");
        Assert(decoded.Mode == SocialRoleProfileMode.Teacher, "mode changed during round-trip");
        Assert(decoded.Stages[0].PrimaryValue == "鹅城一中", "profile text changed during round-trip");
        Assert(note.Contains("作者原备注", StringComparison.Ordinal), "original note was removed");
    }

    private static void TestAtomicPairCommit()
    {
        WithTempDirectory(dir =>
        {
            string first = Path.Combine(dir, "PersonCfg.json");
            string second = Path.Combine(dir, "PersonGrowCfg.json");
            File.WriteAllText(first, "old-person");
            File.WriteAllText(second, "old-grow");

            Assert(AtomicFilePairTransaction.SavePair(first, "new-person", second, "new-grow", out string error), error);
            Assert(File.ReadAllText(first) == "new-person", "first file not committed");
            Assert(File.ReadAllText(second) == "new-grow", "second file not committed");
            Assert(File.ReadAllText(first + AtomicFilePairTransaction.BackupSuffix) == "old-person",
                "first backup missing");
            Assert(File.ReadAllText(second + AtomicFilePairTransaction.BackupSuffix) == "old-grow",
                "second backup missing");
            Assert(!File.Exists(first + AtomicFilePairTransaction.PendingSuffix), "pending marker leaked");
        });
    }

    private static void TestAtomicPairDelete()
    {
        WithTempDirectory(dir =>
        {
            string first = Path.Combine(dir, "PersonCfg.json");
            string second = Path.Combine(dir, "PersonGrowCfg.json");
            File.WriteAllText(first, "person");
            File.WriteAllText(second, "grow");
            Assert(AtomicFilePairTransaction.SavePair(first, null, second, null, out string error), error);
            Assert(!File.Exists(first) && !File.Exists(second), "empty maps were not deleted as a pair");
        });
    }

    private static void TestFailedSecondCommitRollsBackFirst()
    {
        WithTempDirectory(dir =>
        {
            string first = Path.Combine(dir, "PersonCfg.json");
            // 让第二个“目标文件”实际成为目录：准备阶段仍能完成，第二次提交必定失败。
            string second = Path.Combine(dir, "PersonGrowCfg.json");
            File.WriteAllText(first, "old-person");
            Directory.CreateDirectory(second);

            Assert(!AtomicFilePairTransaction.SavePair(
                    first, "new-person", second, "new-grow", out string error),
                "invalid second target unexpectedly committed");
            Assert(!string.IsNullOrWhiteSpace(error), "commit failure did not return an error");
            Assert(File.ReadAllText(first) == "old-person",
                "first file was not rolled back after second commit failed");
            Assert(!File.Exists(first + AtomicFilePairTransaction.PendingSuffix),
                "rollback left a pending marker");
        });
    }

    private static void TestInterruptedPairRecovery()
    {
        WithTempDirectory(dir =>
        {
            string first = Path.Combine(dir, "PersonCfg.json");
            string second = Path.Combine(dir, "PersonGrowCfg.json");
            File.WriteAllText(first, "new-person");
            File.WriteAllText(second, "old-grow");
            File.WriteAllText(first + AtomicFilePairTransaction.OldSuffix, "old-person");
            File.WriteAllText(second + AtomicFilePairTransaction.OldSuffix, "old-grow");
            File.WriteAllText(first + AtomicFilePairTransaction.PendingSuffix, "SAEP_PERSON_SAVE_V1");

            Assert(AtomicFilePairTransaction.Recover(first, second, out bool recovered, out string error), error);
            Assert(recovered, "pending transaction was not reported");
            Assert(File.ReadAllText(first) == "old-person", "first file was not rolled back");
            Assert(File.ReadAllText(second) == "old-grow", "second file was not rolled back");
        });
    }

    private static void TestAbsentPairRecovery()
    {
        WithTempDirectory(dir =>
        {
            string first = Path.Combine(dir, "PersonCfg.json");
            string second = Path.Combine(dir, "PersonGrowCfg.json");
            File.WriteAllText(first, "partial-person");
            File.WriteAllText(second, "partial-grow");
            File.WriteAllText(first + AtomicFilePairTransaction.AbsentSuffix, "absent");
            File.WriteAllText(second + AtomicFilePairTransaction.AbsentSuffix, "absent");
            File.WriteAllText(first + AtomicFilePairTransaction.PendingSuffix, "SAEP_PERSON_SAVE_V1");

            Assert(AtomicFilePairTransaction.Recover(first, second, out bool recovered, out string error), error);
            Assert(recovered, "absent transaction was not reported");
            Assert(!File.Exists(first) && !File.Exists(second), "originally absent files were not removed");
        });
    }

    private static void WithTempDirectory(Action<string> action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "saep-social-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            action(dir);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
