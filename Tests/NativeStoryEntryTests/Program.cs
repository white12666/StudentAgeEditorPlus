using StudentAgeEditorPlus.Patches;

int count = 0;
void Check(string name, bool ok)
{
    if (!ok) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    count++;
}

const int evt = 1999876;
int first = NativeStoryEntryPolicy.FirstTalkId(evt);
Check("Final event ID determines the first dialogue", first == 1999876001);
Check("Invalid event ID cannot produce placeholder 1",
    NativeStoryEntryPolicy.FirstTalkId(0) == -1 &&
    NativeStoryEntryPolicy.FirstTalkId(-1) == -1 &&
    NativeStoryEntryPolicy.FirstTalkId(2000000) == -1);
Check("Maximum event ID stays inside Int32",
    NativeStoryEntryPolicy.FirstTalkId(1999999) + 998 < int.MaxValue);
Check("Title before ID repairs the automatic placeholder",
    NativeStoryEntryPolicy.UsesAutomaticEntry(true, false, new[] { 1 }));
Check("ID before title also initializes an automatic entry",
    NativeStoryEntryPolicy.UsesAutomaticEntry(true, false, new[] { first }));
Check("An explicit cross-event entry is preserved",
    !NativeStoryEntryPolicy.UsesAutomaticEntry(true, true, new[] { 2203001 }));
Check("Existing event entry is preserved",
    !NativeStoryEntryPolicy.UsesAutomaticEntry(false, false, new[] { 1 }));
Check("Empty entries request initialization",
    NativeStoryEntryPolicy.UsesAutomaticEntry(false, true, Array.Empty<int>()) &&
    NativeStoryEntryPolicy.UsesAutomaticEntry(false, false, null));
Check("No selection allocates the actual first free dialogue",
    NativeStoryEntryPolicy.AllocateTalkId(evt, 0, Array.Empty<int>()) == first);
Check("Selected dialogue allocates its successor",
    NativeStoryEntryPolicy.AllocateTalkId(evt, first, new[] { first }) == first + 1);
Check("Saved hidden records are never overwritten",
    NativeStoryEntryPolicy.AllocateTalkId(evt, first, Enumerable.Range(first, 6)) == first + 6);
Check("A selected native placeholder cannot allocate into the native block",
    NativeStoryEntryPolicy.AllocateTalkId(evt, 1, new[] { 1 }) == first);
Check("Last slot wraps inside this event rather than spilling",
    NativeStoryEntryPolicy.AllocateTalkId(evt, first + 998, new[] { first, first + 998 }) == first + 1);
Check("Occupied current, saved and global IDs are all skipped",
    NativeStoryEntryPolicy.AllocateTalkId(evt, first,
        new[] { first, first + 1 }.Concat(new[] { first + 2, first + 3 })) == first + 4);
Check("Exhaustion returns failure without spilling into another event",
    NativeStoryEntryPolicy.AllocateTalkId(evt, first, Enumerable.Range(first, 999)) == -1);
Check("Invalid event cannot allocate",
    NativeStoryEntryPolicy.AllocateTalkId(0, 1, Array.Empty<int>()) == -1);
Console.WriteLine($"All {count} native story entry tests passed.");
