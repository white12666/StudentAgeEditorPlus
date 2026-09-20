using Newtonsoft.Json;
using StudentAgeEditorPlus.Patches;
using Config;

int passed = 0;
void Check(string name, Action check)
{
    check();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Assert(bool value, string message = "Assertion failed")
{
    if (!value) throw new Exception(message);
}
GiftBindingDraft Draft(params int[] talks) => GiftBindingDraft.Create(
    new List<int> { 101 }, new List<List<int>> { talks.ToList() }, null);

Check("Opening and cancelling does not mutate source lists", () =>
{
    var npcs = new List<int> { 101, 102 };
    var talks = new List<List<int>> { new() { 11 }, new() { 21, 22 } };
    var types = new List<int> { 0, 1 };
    var draft = GiftBindingDraft.Create(npcs, talks, types);
    draft.Rows[0].SetTalk(0, 33);
    draft.Rows[1].ItemMode = 0;
    draft.Move(0, 1);
    Assert(npcs.SequenceEqual(new[] { 101, 102 }));
    Assert(talks[0].SequenceEqual(new[] { 11 }) && types[1] == 1);
});
Check("Shared entry validates", () => Assert(Draft(11).Validate(id => id == 11) == null));
Check("Male and female entries validate", () => Assert(Draft(11, 22).Validate(id => id == 11 || id == 22) == null));
Check("Gender disabled by zero is preserved", () =>
{
    var draft = Draft(11, 0);
    Assert(draft.Validate(id => id == 11) == null);
    draft.Rows[0].SetSeparate(false);
    draft.Rows[0].SetSeparate(true);
    Assert(draft.Rows[0].TalkIds.SequenceEqual(new[] { 11, 0 }));
});
Check("Female-only entry validates", () => Assert(Draft(0, 22).Validate(id => id == 22) == null));
Check("Shared to separate copies common entry", () =>
{
    var row = Draft(11).Rows[0];
    row.SetSeparate(true);
    Assert(row.TalkIds.SequenceEqual(new[] { 11, 11 }));
});
Check("Switching back to separate restores female draft", () =>
{
    var row = Draft(11, 22).Rows[0];
    row.SetSeparate(false);
    row.SetTalk(0, 33);
    row.SetSeparate(true);
    Assert(row.TalkIds.SequenceEqual(new[] { 33, 22 }));
});
Check("Empty and zero-only entries are rejected", () =>
{
    Assert(Draft().Validate(_ => true) != null);
    Assert(Draft(0).Validate(_ => true) != null);
    Assert(Draft(0, 0).Validate(_ => true) != null);
});
Check("Missing actual Talk ID is rejected, not guessed from event ID", () =>
    Assert(Draft(777).Validate(id => id == 777001) != null));
Check("Negative and third entries are rejected", () =>
{
    Assert(Draft(-1).Validate(_ => true) != null);
    Assert(Draft(11, 22, 33).Validate(_ => true) != null);
});
Check("Extra parallel-list data is retained and blocks export", () =>
{
    var source = new List<List<int>> { new() { 11 }, new() { 22 } };
    var draft = GiftBindingDraft.Create(new() { 101 }, source, null);
    Assert(draft.ShapeError != null && draft.Validate(_ => true) != null);
    bool blocked = false;
    try { draft.Export(out _, out _, out _); }
    catch (InvalidOperationException) { blocked = true; }
    Assert(blocked && source.Count == 2);
});
Check("Short type lists use native zero defaults", () =>
{
    var draft = GiftBindingDraft.Create(new() { 101, 102 }, new() { new() { 11 }, new() { 22 } }, new() { 1 });
    draft.Export(out _, out _, out var types);
    Assert(types.SequenceEqual(new[] { 1, 0 }));
});
Check("Move keeps NPC, entry and item mode aligned", () =>
{
    var draft = GiftBindingDraft.Create(new() { 101, 102 }, new() { new() { 11 }, new() { 22 } }, new() { 0, 1 });
    draft.Move(0, 1);
    draft.Export(out var npcs, out var talks, out var types);
    Assert(npcs.SequenceEqual(new[] { 102, 101 }) && talks[0][0] == 22 && types[0] == 1);
    talks[0][0] = 999;
    Assert(draft.Rows[0].TalkIds[0] == 22);
});
Check("Removing a row removes its complete binding", () =>
{
    var draft = GiftBindingDraft.Create(new() { 101, 102 }, new() { new() { 11 }, new() { 22 } }, new() { 0, 1 });
    draft.Rows.RemoveAt(0);
    draft.Export(out var npcs, out var talks, out var types);
    Assert(npcs.Single() == 102 && talks.Single().Single() == 22 && types.Single() == 1);
});
Check("Duplicate NPC additions and legacy duplicates are rejected", () =>
{
    var draft = Draft(11);
    Assert(!draft.AddNpc(101));
    draft.Rows.Add(new GiftBindingRow(101, new[] { 22 }, 0));
    Assert(draft.Validate(_ => true) != null);
});
Check("Empty binding is inactive while invalid item modes are rejected", () =>
{
    Assert(GiftBindingDraft.Create(null, null, null).Validate(_ => true) == null);
    var draft = Draft(11);
    draft.Rows[0].ItemMode = 9;
    Assert(draft.Validate(_ => true) != null);
});
Check("Removing all rows exports three empty lists and validates", () =>
{
    var draft = Draft(11);
    draft.Rows.Clear();
    Assert(draft.Validate(_ => false) == null);
    draft.Export(out var npcs, out var talks, out var types);
    Assert(npcs.Count == 0 && talks.Count == 0 && types.Count == 0);
});
Check("Empty NPCs cannot silently hide orphaned dialogue or type groups", () =>
{
    Assert(GiftBindingDraft.Create(new(), new() { new() { 11 } }, new()).Validate(_ => true) != null);
    Assert(GiftBindingDraft.Create(new(), new(), new() { 1 }).Validate(_ => true) != null);
});
Check("Explicit NPC clear removes all parallel fields without mutating old lists", () =>
{
    var npcs = new List<int> { 101, 102 };
    var talks = new List<List<int>> { new() { 11 }, new() { 21, 22 } };
    var types = new List<int> { 0, 1 };
    GiftFormSync.RemapNpcs("", npcs, talks, types, out var n, out var t, out var y);
    Assert(n.Count == 0 && t.Count == 0 && y.Count == 0);
    Assert(npcs.Count == 2 && talks[1].SequenceEqual(new[] { 21, 22 }) && types[1] == 1);
});
Check("Whitespace clears and adding after clear does not resurrect old bindings", () =>
{
    GiftFormSync.RemapNpcs(" \t ", new() { 101 }, new() { new() { 11 } }, new() { 1 },
        out var n, out var t, out var y);
    GiftFormSync.RemapNpcs("101", n, t, y, out n, out t, out y);
    Assert(n.Single() == 101 && t.Single().Count == 0 && y.Single() == 0);
});
Check("Manual reorder follows NPC identity, preserving gender slots and item mode", () =>
{
    GiftFormSync.RemapNpcs("102， 101", new() { 101, 102 },
        new() { new() { 11, 12 }, new() { 21, 0 } }, new() { 0, 1 }, out var n, out var t, out var y);
    Assert(n.SequenceEqual(new[] { 102, 101 }) && t[0].SequenceEqual(new[] { 21, 0 }) &&
        t[1].SequenceEqual(new[] { 11, 12 }) && y.SequenceEqual(new[] { 1, 0 }));
});
Check("Manual deletion keeps surviving NPC's own binding", () =>
{
    GiftFormSync.RemapNpcs("102", new() { 101, 102 },
        new() { new() { 11 }, new() { 21, 22 } }, new() { 0, 1 }, out var n, out var t, out var y);
    Assert(n.Single() == 102 && t.Single().SequenceEqual(new[] { 21, 22 }) && y.Single() == 1);
});
Check("Replacing an NPC creates an unbound row, not the removed NPC's dialogue", () =>
{
    GiftFormSync.RemapNpcs("103,102", new() { 101, 102 },
        new() { new() { 11 }, new() { 21 } }, new() { 1, 1 }, out var n, out var t, out var y);
    Assert(n[0] == 103 && t[0].Count == 0 && y[0] == 0 && t[1].Single() == 21);
});
Check("Invalid NPC text is rejected without changing the original binding", () =>
{
    var npcs = new List<int> { 101 };
    var talks = new List<List<int>> { new() { 11 } };
    foreach (string text in new[] { "101,101", "0", "-1", "abc", "101,", "101,,102", "2147483648" })
    {
        bool rejected = false;
        try { GiftFormSync.RemapNpcs(text, npcs, talks, null, out _, out _, out _); }
        catch (FormatException) { rejected = true; }
        Assert(rejected && npcs.Single() == 101 && talks.Single().Single() == 11);
    }
});
Check("Unchanged NPC input retains malformed legacy groups for explicit correction", () =>
{
    GiftFormSync.RemapNpcs("101", new() { 101 }, new() { new() { 11 }, new() { 22 } }, new() { 1 },
        out _, out var t, out _);
    Assert(t.Count == 2);
});
Check("Ambiguous legacy remap is rejected, but explicit clear is allowed", () =>
{
    foreach (var old in new[] { new List<int> { 101, 101 }, new List<int> { 101 } })
    {
        bool rejected = false;
        try { GiftFormSync.RemapNpcs("102", old, new() { new() { 11 }, new() { 22 } }, null,
            out _, out _, out _); } catch (FormatException) { rejected = true; }
        Assert(rejected);
        GiftFormSync.RemapNpcs("", old, new() { new() { 11 }, new() { 22 } }, null, out var n, out var t, out var y);
        Assert(n.Count == 0 && t.Count == 0 && y.Count == 0);
    }
});
Check("Manual blank dialogue groups retain their NPC positions", () =>
{
    var groups = GiftFormSync.ParseTalks(";21，22");
    Assert(groups.Count == 2 && groups[0].Count == 0 && groups[1].SequenceEqual(new[] { 21, 22 }));
    Assert(GiftFormSync.ParseTalks("").Count == 0 && GiftFormSync.ParseIds("").Count == 0);
});
Check("Search matches title, dialogue and numeric ID", () =>
{
    var choice = new GiftStoryChoice { Id = 881002, Title = "生日礼物", Content = "谢谢你的礼物 Gift" };
    Assert(choice.Matches("生日") && choice.Matches("谢谢") && choice.Matches("1002") &&
        choice.Matches(" gift ") && choice.Matches("") && !choice.Matches("不存在"));
});

string root = Path.Combine(Path.GetTempPath(), "editorplus-gift-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "Cfgs", "zh-cn"));
void Write(string table, string json) => File.WriteAllText(Path.Combine(root, "Cfgs", "zh-cn", table + ".json"), json);
try
{
    Cfg.TalkCfgMap = new() { [11] = new TalkCfg { id = 11, content = "旧对白" } };
    Cfg.EvtCfgMap = new() { [9] = new EvtCfg { id = 9, title = "官方事件", talkId = new() { 11 } } };
    Cfg.PersonCfgMap = new() { [101] = new PersonCfg { id = 101, name = "官方人物" } };
    Write("TalkCfg", """{"11":{"id":11,"content":"新对白"},"50":{"id":50,"content":"真正入口"},"99":{"id":99,"content":"备用入口"}}""");
    Write("EvtCfg", """{"700":{"id":700,"title":"生日礼物","talkId":[50,99]}}""");
    Write("PersonCfg", """{"101":{"id":101,"name":"修改后人物"}}""");
    Check("Current project data overrides loaded data", () =>
    {
        var catalog = GiftStoryCatalog.Load(root);
        Assert(catalog.Talks[11].Content == "新对白" && catalog.Talks[11].IsLocal);
        Assert(catalog.NpcName(101) == "修改后人物");
    });
    Check("Catalog uses real entry IDs and gender labels", () =>
    {
        var catalog = GiftStoryCatalog.Load(root);
        Assert(catalog.Talks[50].IsEntry && catalog.Talks[50].Title.Contains("生日礼物（男主入口）"));
        Assert(catalog.Talks[99].Title.Contains("女主入口") && !catalog.Talks.ContainsKey(700001));
    });
    Check("Reload sees freshly saved dialogue without restarting game", () =>
    {
        Write("TalkCfg", """{"11":{"id":11,"content":"再次保存"}}""");
        Assert(GiftStoryCatalog.Load(root).Talks[11].Content == "再次保存");
    });
    Check("Invalid project JSON fails closed instead of falling back", () =>
    {
        Write("TalkCfg", "{broken");
        bool failed = false;
        try { GiftStoryCatalog.Load(root); } catch (JsonException) { failed = true; }
        Assert(failed && Cfg.TalkCfgMap[11].content == "旧对白");
    });
    Check("Mismatched JSON keys and duplicate records fail closed", () =>
    {
        foreach (string invalid in new[] { """{"11":{"id":22}}""", """{"11":{"id":11},"11":{"id":11}}""" })
        {
            Write("TalkCfg", invalid);
            bool failed = false;
            try { GiftStoryCatalog.Load(root); } catch (JsonException) { failed = true; }
            Assert(failed);
        }
    });
    Check("Deleted project dialogue and event never revive from loaded maps", () =>
    {
        Cfg.TalkCfgMap[500] = new TalkCfg { id = 500, content = "已删除旧对白" };
        Cfg.EvtCfgMap[600] = new EvtCfg { id = 600, title = "已删除旧事件", talkId = new() { 500 } };
        Write("TalkCfg", "{}");
        Write("EvtCfg", "{}");
        var native = new GiftCatalogSources
        {
            Talks = new() { [11] = new TalkCfg { id = 11, content = "原版对白" } },
            Events = new() { [9] = new EvtCfg { id = 9, title = "原版事件", talkId = new() { 11 } } },
            People = new() { [101] = new PersonCfg { id = 101, name = "原版人物" } },
        };
        var catalog = GiftStoryCatalog.Load(root, native);
        Assert(!catalog.Talks.ContainsKey(500));
        Assert(Draft(500).Validate(id => catalog.Talks.ContainsKey(id)) != null);
        Assert(catalog.Talks[11].Content == "原版对白" && catalog.Talks[11].Title == "原版事件");
    });
    Check("Other mods retain native load priority and project overrides them", () =>
    {
        string first = Path.Combine(root, "other-first");
        string second = Path.Combine(root, "other-second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "TalkCfg.json"), """{"77":{"id":77,"content":"先加载的其它作品"}}""");
        File.WriteAllText(Path.Combine(second, "TalkCfg.json"), """{"77":{"id":77,"content":"后加载的其它作品"}}""");
        var native = new GiftCatalogSources { Talks = new() };
        var catalog = GiftStoryCatalog.Load(root, native, new[] { first, second });
        Assert(catalog.Talks[77].Content == "先加载的其它作品");
        Write("TalkCfg", """{"77":{"id":77,"content":"当前作品"}}""");
        catalog = GiftStoryCatalog.Load(root, native, new[] { first, second });
        Assert(catalog.Talks[77].Content == "当前作品" && catalog.Talks[77].IsLocal);
    });
    Check("Current workshop copy is identified by published ID or package", () =>
    {
        Assert(GiftStoryCatalog.IsCurrentMod(123, "mine", 123, "renamed"));
        Assert(GiftStoryCatalog.IsCurrentMod(0, "mine", 123, "mine"));
        Assert(!GiftStoryCatalog.IsCurrentMod(0, "mine", 456, "other"));
        Assert(!GiftStoryCatalog.IsCurrentMod(0, "", 0, ""));
    });
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"All {passed} gift binding tests passed.");

// Minimal data contracts for the linked, non-Unity production catalog.
namespace Config
{
    public class TalkCfg { public int id; public string content; }
    public class EvtCfg { public int id; public string title; public List<int> talkId; }
    public class PersonCfg { public int id; public string name; }
    public static class Cfg
    {
        public static Dictionary<int, TalkCfg> TalkCfgMap;
        public static Dictionary<int, EvtCfg> EvtCfgMap;
        public static Dictionary<int, PersonCfg> PersonCfgMap;
    }
}
namespace Sdk { public static class LocalizationMgr { public static string Lang = "zh-cn"; } }
