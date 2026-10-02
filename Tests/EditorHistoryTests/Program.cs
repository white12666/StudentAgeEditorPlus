using StudentAgeEditorPlus.Patches;

int checks = 0;
void Check(string name, bool pass)
{
    if (!pass) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    checks++;
}
EditorUndoHistory<State> History(int capacity = 100)
{
    var h = new EditorUndoHistory<State>((a, b) => a.Text == b.Text, capacity);
    h.Reset(new State("正文"));
    return h;
}
bool Restore(State state) => true;

var history = History();
Check("New session has no history", !history.CanUndo && !history.CanRedo);
history.Record(new State("正文一"), "body", 0);
history.Record(new State("正文一二"), "body", .2);
Check("Continuous typing is one step", history.Count == 1);
history.Move(false, Restore);
Check("Undo restores text before typing", history.Current.Text == "正文" && history.CanRedo);
history.Move(true, Restore);
Check("Redo restores all continuous typing", history.Current.Text == "正文一二");
history.Record(new State("正文一二三"), "body", .3);
Check("Typing after redo starts a new step", history.Count == 2);
history.BreakMerge();
history.Record(new State("误删"), "body", .4);
history.Move(false, Restore);
Check("Selection deletion does not merge with prior typing", history.Current.Text == "正文一二三");
history.Record(new State("新分支"), "body", .5);
Check("New editing after undo clears redo", !history.CanRedo && history.Count == 3);
history.Record(new State("新分支停顿"), "body", 2);
Check("Typing after one second starts a step", history.Count == 4);
history.Record(new State("新字段"), "other", 2.1);
Check("Different fields do not merge", history.Count == 5);
history.Record(new State("新字段", 12), "other", 2.2);
Check("Caret-only change is not an edit", history.Count == 5 && history.Current.Caret == 12);
history.BreakMerge(); // 保存并不调用 Reset
history.Rebase(new State("新字段", 3));
Check("Saving keeps undo history", history.CanUndo && history.Count == 5);
history.Move(false, Restore);
Check("Undo after save changes draft", history.Current.Text == "新分支停顿");
history.Move(true, Restore);
Check("Redo after save restores saved text", history.Current.Text == "新字段");
int n = history.Count;
bool result = history.Move(false, _ => false);
Check("Rejected restore retains current state", !result && history.Current.Text == "新字段" && history.Count == n && !history.CanRedo);
try { history.Move(false, _ => throw new InvalidOperationException()); } catch (InvalidOperationException) { }
Check("Thrown restore does not consume history", history.Current.Text == "新字段" && !history.CanRedo);

var small = History(2);
small.Record(new State("一"), null, 0);
small.Record(new State("二"), null, 1);
small.Record(new State("三"), null, 2);
Check("History is bounded", small.Count == 2);
small.Move(false, Restore);
small.Move(false, Restore);
Check("Oldest retained state stays undoable", small.Current.Text == "一" && !small.CanUndo);
Check("Undo at boundary is harmless", !small.Move(false, Restore));
small.Move(true, Restore);
small.Move(true, Restore);
Check("Redo reaches latest", small.Current.Text == "三" && !small.CanRedo);
Check("Redo at boundary is harmless", !small.Move(true, Restore));
small.Reset(new State("另一事件"));
Check("Reopen resets both stacks", !small.CanUndo && !small.CanRedo && small.Count == 0);

var graph = History();
graph.Record(new State("表单修改"), "body", 0);
graph.BreakMerge();
graph.Record(new State("剧情图保存"), null, 1);
graph.Move(false, Restore);
Check("Graph save is one independent step", graph.Current.Text == "表单修改");
graph.Move(false, Restore);
Check("Earlier form history survives graph save", graph.Current.Text == "正文");
graph.Move(true, Restore);
graph.Move(true, Restore);
Check("Graph save can be redone", graph.Current.Text == "剧情图保存");
graph.Visit(s => { if (s.Text == "正文") s.Text = "$源码$"; });
graph.Move(false, Restore);
graph.Move(false, Restore);
Check("Programmatic source normalization reaches earlier snapshots", graph.Current.Text == "$源码$");

Console.WriteLine($"All {checks} editor history tests passed.");

sealed class State(string text, int caret = 0)
{
    public string Text = text;
    public int Caret = caret;
}
