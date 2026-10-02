using StudentAgeEditorPlus.Patches;

int count = 0;
void Check(string name, bool ok)
{
    if (!ok) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    count++;
}

EditorSnapshot Snapshot(params FakeTalk[] talks)
{
    var snapshot = new EditorSnapshot();
    foreach (FakeTalk talk in talks)
        snapshot.Add("对话", talk.id.ToString(), talk, new FakeTalk { id = talk.id });
    return snapshot;
}

FakeTalk Talk(int id, string content) => new FakeTalk
{
    id = id,
    content = content,
    nextTalk = new List<int> { id + 1 },
    roles = new List<List<float>> { new List<float> { 101f, 3006f } },
    speed = 1.5f,
    extra = new Dictionary<int, string> { [1] = "a", [2] = "b" },
};

// ── 指纹 ──
Check("Null and empty lists fingerprint the same (native save fills null lists)",
    EditorChangeTracking.Fingerprint(new FakeTalk { id = 1 })
    == EditorChangeTracking.Fingerprint(new FakeTalk
    {
        id = 1, nextTalk = new List<int>(), roles = new List<List<float>>(),
        extra = new Dictionary<int, string>(),
    }));
Check("Null and empty strings fingerprint the same (input focus loss writes empty text)",
    EditorChangeTracking.Fingerprint(new FakeTalk { id = 1, content = null })
    == EditorChangeTracking.Fingerprint(new FakeTalk { id = 1, content = "" }));
Check("A nested list edit changes the fingerprint",
    EditorChangeTracking.Fingerprint(Talk(1, "x"))
    != EditorChangeTracking.Fingerprint(Mutate(Talk(1, "x"), t => t.roles[0][1] = 3007f)));
Check("Dictionary insertion order does not matter",
    EditorChangeTracking.Fingerprint(Mutate(Talk(1, "x"), t => t.extra = new Dictionary<int, string> { [2] = "b", [1] = "a" }))
    == EditorChangeTracking.Fingerprint(Talk(1, "x")));
Check("Float values that round-trip through an input field are unchanged",
    EditorChangeTracking.Fingerprint(Mutate(Talk(1, "x"), t => t.speed = float.Parse("1.5")))
    == EditorChangeTracking.Fingerprint(Talk(1, "x")));
Check("Real float edits are detected",
    EditorChangeTracking.Fingerprint(Mutate(Talk(1, "x"), t => t.speed = 1.25f))
    != EditorChangeTracking.Fingerprint(Talk(1, "x")));
Check("Field declaration order does not affect the fingerprint",
    EditorChangeTracking.Fingerprint(new OrderA { id = 1, name = "n" })
    == EditorChangeTracking.Fingerprint(new OrderB { name = "n", id = 1 }));

// ── 快照比较 ──
{
    EditorSnapshot baseline = Snapshot(Talk(1, "one"), Talk(2, "two"), new FakeTalk { id = 3 });
    EditorSnapshot current = Snapshot(Talk(1, "ONE"), Talk(4, "four"), new FakeTalk { id = 5 });
    EditorChangeSummary summary = EditorChangeTracking.Compare(baseline, current);
    Check("Modified, added and removed records are counted",
        summary.HasChanges
        && summary.Count("对话", EditorChangeKind.Modified) == 1
        && summary.Count("对话", EditorChangeKind.Added) == 1
        && summary.Count("对话", EditorChangeKind.Removed) == 1);
    Check("Blank new records and removed blank records are ignored",
        !summary.ChangedKeys.Contains("对话:5") && !summary.ChangedKeys.Contains("对话:3"));
    Check("Summary text lists modified, added, removed",
        summary.Describe() == "对话修改 1 条、新增 1 条、删除 1 条");
}
{
    EditorSnapshot baseline = Snapshot(Talk(1, "one"), Talk(2, "two"));
    EditorSnapshot current = Snapshot(Talk(1, "one"), Talk(2, "two"));
    Check("Identical content has no changes", !EditorChangeTracking.Compare(baseline, current).HasChanges);
}
{
    EditorSnapshot baseline = Snapshot(new FakeTalk { id = 0, content = "a" }, new FakeTalk { id = 0, content = "b" });
    EditorSnapshot current = Snapshot(new FakeTalk { id = 0, content = "a" });
    EditorChangeSummary summary = EditorChangeTracking.Compare(baseline, current);
    Check("Duplicate IDs are tracked separately",
        baseline.Records.ContainsKey("对话:0#2") && summary.Count("对话", EditorChangeKind.Removed) == 1);
}
{
    // LaTeX 场景：写盘的是烘焙成品，写盘确认后内存又换回源码，两者都是已保存状态。
    EditorSnapshot written = Snapshot(Talk(1, "<sprite=1>"));
    EditorSnapshot before = Snapshot(Talk(1, "$x^2$"));
    Check("Accepted pre-save state is not reported as modified",
        !EditorChangeTracking.Compare(written, Snapshot(Talk(1, "$x^2$")), before).HasChanges);
    Check("Written state is not reported as modified",
        !EditorChangeTracking.Compare(written, Snapshot(Talk(1, "<sprite=1>")), before).HasChanges);
    Check("A third state is still reported",
        EditorChangeTracking.Compare(written, Snapshot(Talk(1, "$y$")), before).HasChanges);
}
{
    var baseline = new EditorSnapshot();
    baseline.Add("对话", "1", Talk(1, "a"), null);
    baseline.Add("选项", "101", new FakeOption { id = 101, content = "go" }, null);
    var current = new EditorSnapshot();
    current.Add("对话", "1", Talk(1, "b"), null);
    EditorChangeSummary summary = EditorChangeTracking.Compare(baseline, current);
    Check("Kinds are reported in page order",
        summary.Describe() == "对话修改 1 条；选项删除 1 条");
    string key = EditorSnapshot.KeyOf("对话", "1");
    baseline.Replace(key, new EditorRecordState("对话", EditorChangeTracking.Fingerprint(Talk(1, "b")), false));
    Check("Replacing a baseline record absorbs a programmatic change",
        EditorChangeTracking.Compare(baseline, current).Describe() == "选项删除 1 条");
}

// ── 深拷贝 ──
{
    FakeTalk original = Talk(7, "text");
    original.grid = new[] { new List<int> { 1, 2 } };
    FakeTalk copy = EditorChangeTracking.DeepCopy(original);
    Check("Deep copy keeps every value",
        !ReferenceEquals(copy, original)
        && EditorChangeTracking.Fingerprint(copy) == EditorChangeTracking.Fingerprint(original));
    copy.nextTalk.Add(99);
    copy.roles[0][0] = 202f;
    copy.extra[1] = "changed";
    copy.grid[0].Add(3);
    copy.content = "edited";
    Check("Editing the copy never touches the original",
        original.nextTalk.Count == 1 && original.roles[0][0] == 101f
        && original.extra[1] == "a" && original.grid[0].Count == 2 && original.content == "text");
    Check("Deep copy of null is null", EditorChangeTracking.DeepCopy<FakeTalk>(null) == null);
    bool rejected = false;
    try { EditorChangeTracking.DeepCopy(new Unsupported()); }
    catch (NotSupportedException) { rejected = true; }
    Check("Unknown collection types are refused instead of shared", rejected);
}

// ── 保存完成探针（真实 async void） ──
{
    var main = new QueueContext();
    SynchronizationContext.SetSynchronizationContext(main);
    var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    bool wrote = false;
    var probe = new SaveCompletionProbe(main);
    SynchronizationContext.SetSynchronizationContext(probe);
    FakeSave(gate.Task, () => wrote = true);
    SynchronizationContext.SetSynchronizationContext(main);
    int finished = 0;
    probe.Seal(() => finished++);
    main.RunPending();
    Check("Probe sees the async void save start but waits for the write",
        probe.Started && !probe.Finished && finished == 0);
    gate.SetResult(true);
    Pump(main, () => finished > 0);
    Check("Probe reports completion once, on the main context, after the write",
        wrote && probe.Finished && probe.Failure == null && finished == 1
        && SynchronizationContext.Current == main);
    Pump(main, () => false, 5);
    Check("Completion is not reported twice", finished == 1);
    Check("Operation accounting is forwarded to the game context",
        main.Started == 1 && main.Completed == 1);
}
{
    var main = new QueueContext();
    SynchronizationContext.SetSynchronizationContext(main);
    var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var probe = new SaveCompletionProbe(main);
    SynchronizationContext.SetSynchronizationContext(probe);
    FailingSave(gate.Task);
    SynchronizationContext.SetSynchronizationContext(main);
    int finished = 0;
    probe.Seal(() => finished++);
    gate.SetResult(true);
    Pump(main, () => finished > 0);
    Check("A failed write is reported as failure",
        finished == 1 && probe.Failure is IOException && probe.Failure.Message == "disk full");
    Check("The failure still reaches the game context",
        main.Errors.Count == 1 && main.Errors[0] is IOException);
}
{
    var main = new QueueContext();
    SynchronizationContext.SetSynchronizationContext(main);
    var probe = new SaveCompletionProbe(main);
    SynchronizationContext.SetSynchronizationContext(probe);
    SyncSave();
    SynchronizationContext.SetSynchronizationContext(main);
    int finished = 0;
    probe.Seal(() => finished++);
    Pump(main, () => finished > 0);
    Check("A save that finishes synchronously is reported after Seal", probe.Finished && finished == 1);
}
{
    var main = new QueueContext();
    SynchronizationContext.SetSynchronizationContext(main);
    var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var probe = new SaveCompletionProbe(main);
    SynchronizationContext.SetSynchronizationContext(probe);
    FakeSave(first.Task, () => { });
    FakeSave(second.Task, () => { });
    SynchronizationContext.SetSynchronizationContext(main);
    int finished = 0;
    probe.Seal(() => finished++);
    first.SetResult(true);
    Pump(main, () => false, 10);
    Check("Probe waits for every async operation started during the save", !probe.Finished && finished == 0);
    second.SetResult(true);
    Pump(main, () => finished > 0);
    Check("Probe finishes when the last operation completes", probe.Finished && finished == 1);
}

SynchronizationContext.SetSynchronizationContext(null);
Console.WriteLine($"All {count} unsaved edit tests passed.");

static FakeTalk Mutate(FakeTalk talk, Action<FakeTalk> change)
{
    change(talk);
    return talk;
}

static void Pump(QueueContext context, Func<bool> done, int rounds = 400)
{
    for (int i = 0; i < rounds; i++)
    {
        context.RunPending();
        if (done()) return;
        Thread.Sleep(5);
    }
}

static async void FakeSave(Task gate, Action wrote)
{
    await gate;
    wrote();
}

static async void FailingSave(Task gate)
{
    await gate;
    throw new IOException("disk full");
}

#pragma warning disable CS1998
static async void SyncSave()
{
}
#pragma warning restore CS1998

internal sealed class FakeTalk
{
    public int id;
    public string content;
    public List<int> nextTalk;
    public List<List<float>> roles;
    public float speed;
    public Dictionary<int, string> extra;
    public List<int>[] grid;
}

internal sealed class FakeOption
{
    public int id;
    public string content;
}

internal sealed class Unsupported
{
    public HashSet<int> ids = new HashSet<int> { 1 };
}

internal sealed class OrderA
{
    public int id;
    public string name;
}

internal sealed class OrderB
{
    public string name;
    public int id;
}

/// <summary>单线程“主线程”上下文：回调排队，由测试手动泵出，行为接近 UnitySynchronizationContext。</summary>
internal sealed class QueueContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object State)> _queue = new();
    public int Started;
    public int Completed;
    public readonly List<Exception> Errors = new();

    public override void Post(SendOrPostCallback d, object state)
    {
        lock (_queue) _queue.Enqueue((d, state));
    }

    public override void OperationStarted() => Interlocked.Increment(ref Started);

    public override void OperationCompleted() => Interlocked.Increment(ref Completed);

    public void RunPending()
    {
        while (true)
        {
            (SendOrPostCallback Callback, object State) item;
            lock (_queue)
            {
                if (_queue.Count == 0) return;
                item = _queue.Dequeue();
            }
            SynchronizationContext previous = Current;
            SetSynchronizationContext(this);
            try { item.Callback(item.State); }
            catch (Exception e) { Errors.Add(e); }
            finally { SetSynchronizationContext(previous); }
        }
    }
}
