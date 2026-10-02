using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using Config;
using HarmonyLib;
using Newtonsoft.Json;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using View.Hint;
using View.Main;
using View.Mod;

namespace StudentAgeEditorPlus.HistoryTests
{
    [BepInPlugin("com.studentage.editorplus.historytests", "EditorPlus History Tests", "1.0.6")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static string Output;
        internal static bool ExternalKeys;
        internal static bool BlockSave;
        private void Awake()
        {
            string request = Path.Combine(Paths.GameRootPath, "_agenttmp", "editor-history-qa", "request.txt");
            if (!File.Exists(request)) return;
            string[] requestLines = File.ReadAllLines(request);
            Output = requestLines[0].Trim();
            ExternalKeys = requestLines.Length > 1 && requestLines[1].Trim() == "external-keys";
            if (!Path.IsPathRooted(Output) || Directory.Exists(Output))
                throw new InvalidOperationException("History QA requires a fresh absolute output directory");
            Directory.CreateDirectory(Output);
            File.Delete(request);
            new Harmony("com.studentage.editorplus.historytests").Patch(
                AccessTools.Method(typeof(UIMgr), "Init"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(EnsureRunner)));
            new Harmony("com.studentage.editorplus.historytests").Patch(
                AccessTools.Method(typeof(ModEvtEditView), "OnClickSave"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowSave)));
            EnsureRunner();
        }
        private static void EnsureRunner()
        {
            if (ExternalKeys)
            {
                if (FindObjectOfType<ExternalKeyboardProbe>() != null) return;
                var probe = new GameObject("EditorHistoryExternalKeyboardQA");
                DontDestroyOnLoad(probe);
                probe.AddComponent<ExternalKeyboardProbe>();
                return;
            }
            if (FindObjectOfType<HistoryGameTests>() != null) return;
            var host = new GameObject("EditorHistoryQA");
            DontDestroyOnLoad(host);
            host.AddComponent<HistoryGameTests>();
        }
        private static bool AllowSave() => !BlockSave;
    }

    public sealed class HistoryGameTests : MonoBehaviour
    {
        private readonly Stack<IEnumerator> _routines = new Stack<IEnumerator>();
        private float _next;
        private float _deadline;
        private ModEvtEditView _view;
        private object _history;
        private string _project;
        private string _file;
        private Assembly _assembly;
        private int _checks;
        private bool _initialBackground;
        private const int EventId = 1987652;
        private const int First = EventId * 1000 + 1;
        private const string Original = "这是一段不该丢失的台词。误删以后需要完整恢复。";

        private void Start()
        {
            _initialBackground = Application.runInBackground;
            Application.runInBackground = true;
            _deadline = Time.realtimeSinceStartup + 180f;
            _routines.Push(Run());
        }
        private void Update()
        {
            if (_routines.Count == 0 || Time.realtimeSinceStartup < _next) return;
            try
            {
                if (Time.realtimeSinceStartup > _deadline) throw new TimeoutException("History QA timeout");
                while (_routines.Count > 0)
                {
                    IEnumerator routine = _routines.Peek();
                    if (!routine.MoveNext()) { _routines.Pop(); continue; }
                    if (routine.Current is IEnumerator nested) { _routines.Push(nested); continue; }
                    _next = Time.realtimeSinceStartup + (routine.Current is float delay ? delay : 0f);
                    return;
                }
                Finish(null);
            }
            catch (Exception e) { Finish(e); }
        }
        private void Check(string label, bool pass)
        {
            File.AppendAllText(Path.Combine(Plugin.Output, "steps.txt"),
                (pass ? "PASS " : "FAIL ") + label + Environment.NewLine);
            if (!pass) throw new Exception(label);
            _checks++;
        }
        private void Finish(Exception error)
        {
            _routines.Clear();
            Application.runInBackground = _initialBackground;
            File.WriteAllText(Path.Combine(Plugin.Output, "result.txt"),
                error == null ? "PASS " + _checks : "FAIL " + error);
            Application.Quit();
        }
        private static bool Ready()
        {
            try { return UIMgr.IsViewOpened<EntryView>(); } catch { return false; }
        }
        private static object Call(object target, string method, params object[] args) =>
            AccessTools.Method(target.GetType(), method).Invoke(target, args);
        private TalkCfg Current => (TalkCfg)AccessTools.Field(typeof(ModEvtEditView), "curSelect").GetValue(_view);
        private List<TalkCfg> Talks => (List<TalkCfg>)AccessTools.Field(typeof(ModEvtEditView), "talkCfgs").GetValue(_view);
        private Dictionary<int, OptionCfg> Options => (Dictionary<int, OptionCfg>)
            AccessTools.Field(typeof(ModEvtEditView), "optionCfgs").GetValue(_view);
        private TMP_InputField Body => _view.inputex_talk;
        private Button Undo => _view.gameObject.GetComponentsInChildren<Button>(true).Single(b => b.name == "EditorPlus_Undo");
        private Button Redo => _view.gameObject.GetComponentsInChildren<Button>(true).Single(b => b.name == "EditorPlus_Redo");

        private static Rect ScreenRect(Transform target)
        {
            var corners = new Vector3[4];
            ((RectTransform)target).GetWorldCorners(corners);
            Canvas canvas = target.GetComponentInParent<Canvas>();
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
        }
        private static void Click(GameObject go)
        {
            var data = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                position = ScreenRect(go.transform).center,
                clickCount = 1
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(data, hits);
            if (hits.Count == 0 || ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject) != go)
                throw new InvalidOperationException("Click intercepted: " + go.name);
            data.pointerPressRaycast = hits[0];
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerClickHandler);
        }
        private static void Key(TMP_InputField field, KeyCode key, EventModifiers modifiers = EventModifiers.None,
            char character = '\0') =>
            field.ProcessEvent(new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers, character = character });

        private bool Dirty()
        {
            object[] args = { _view, null };
            return (bool)AccessTools.Method(_assembly.GetType("StudentAgeEditorPlus.Patches.UnsavedEditGuard"),
                "TryGetUnsavedChanges").Invoke(null, args);
        }
        private void Commit() => EventSystem.current.SetSelectedGameObject(null);
        private void Move(bool redo) => Call(_history, "Move", redo);
        private void Shot(string name) => ScreenCapture.CaptureScreenshot(Path.Combine(Plugin.Output, name + ".png"));
        private void Select(int id) => Call(_view, "Select", Talks.Single(t => t.id == id));
        private void ResetHistory() => AccessTools.Method(_history.GetType(), "Attach").Invoke(null, new object[] { _view });

        private IEnumerator Open()
        {
            UIMgr.OpenView<ModEvtEditView>(UILayerType.None, null, _project, EventId, new List<int> { First });
            while ((_view = UIMgr.GetView<ModEvtEditView>(false) as ModEvtEditView) == null
                   || _view.viewState != ViewState.Opened) yield return .1f;
            yield return .6f;
            _history = _view.gameObject.GetComponent(_assembly.GetType("StudentAgeEditorPlus.Patches.EvtEditorHistory"));
            Check("History attached", _history != null);
            Select(First);
            yield return .5f;
        }

        private IEnumerator Run()
        {
            while (!Ready()) yield return .3f;
            yield return 1f;
            _assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "StudentAgeEditorPlus");
            File.WriteAllText(Path.Combine(Plugin.Output, "version.txt"), _assembly.GetName().Version.ToString());
            _project = Path.Combine(Plugin.Output, "project");
            string cfgDir = Path.Combine(_project, "Cfgs", LocalizationMgr.Lang);
            Directory.CreateDirectory(cfgDir);
            _file = Path.Combine(cfgDir, "TalkCfg.json");
            File.WriteAllText(_file, JsonConvert.SerializeObject(new Dictionary<int, TalkCfg>
            {
                [First] = new TalkCfg { id = First, content = Original },
                [First + 1] = new TalkCfg { id = First + 1, content = "第二句台词" }
            }));
            File.WriteAllText(Path.Combine(cfgDir, "EvtCfg.json"), JsonConvert.SerializeObject(new Dictionary<int, EvtCfg>
            {
                [EventId] = new EvtCfg { id = EventId, title = "撤销回归临时作品", talkId = new List<int> { First } }
            }));
            yield return Open();
            Check("Ordinary and CG text disable focus-select-all", !Body.onFocusSelectAll && !_view.inputex_talk_cg.onFocusSelectAll);
            Check("Undo redo initially disabled", !Undo.interactable && !Redo.interactable);
            Rect add = ScreenRect(_view.btn_new.transform), undo = ScreenRect(Undo.transform), redo = ScreenRect(Redo.transform);
            Check("Header buttons do not overlap", add.xMax < undo.xMin && undo.xMax < redo.xMin);
            Check("Header stays within list width", redo.xMax <= ScreenRect(_view.scroll_left.transform).xMax + 1);
            Check("Search remains below header", ScreenRect(EvtSearch()).yMax <= undo.yMin + 1);
            Shot("01_header");
            yield return .2f;

            Click(Body.gameObject);
            yield return .3f;
            Check("Click places caret rather than selects paragraph",
                Body.isFocused && Body.selectionStringAnchorPosition == Body.selectionStringFocusPosition);
            Key(Body, KeyCode.None, EventModifiers.None, '好');
            yield return .15f;
            Check("Typing preserves existing paragraph", Body.text.Length == Original.Length + 1);
            Key(Body, KeyCode.Z, EventModifiers.Control);
            yield return .2f;
            Check("Ctrl+Z while focused restores paragraph", Body.text == Original && Body.isFocused);
            Key(Body, KeyCode.Y, EventModifiers.Control);
            yield return .2f;
            Check("Ctrl+Y redoes typed character", Body.text.Length == Original.Length + 1);
            Key(Body, KeyCode.Z, EventModifiers.Control);
            yield return .2f;
            Key(Body, KeyCode.A, EventModifiers.Control);
            Key(Body, KeyCode.Backspace);
            yield return .2f;
            Check("Actual select-all backspace deletes paragraph", Body.text == "");
            Key(Body, KeyCode.Z, EventModifiers.Control);
            yield return .2f;
            Check("Deleted whole paragraph recovered without blur", Body.text == Original);
            Key(Body, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            yield return .2f;
            Check("Ctrl+Shift+Z redoes deletion", Body.text == "");
            Key(Body, KeyCode.Z, EventModifiers.Control);
            yield return .2f;
            Shot("02_recovered_paragraph");
            Commit();
            yield return .3f;
            ResetHistory();

            Click(Body.gameObject);
            yield return .2f;
            Key(Body, KeyCode.End, EventModifiers.Control);
            foreach (char c in "连续输入") Key(Body, KeyCode.None, EventModifiers.None, c);
            yield return .3f;
            Check("Continuous input appends", Body.text == Original + "连续输入");
            Click(Undo.gameObject);
            yield return .3f;
            Check("Click undo restores continuous input in one step", Body.text == Original);
            Click(Redo.gameObject);
            yield return .3f;
            Check("Click redo restores continuous input", Body.text == Original + "连续输入");
            Commit();
            yield return .2f;
            Select(First + 1);
            yield return .3f;
            Move(false);
            yield return .3f;
            Check("Undo after changing selection restores correct talk", Current.id == First && Body.text == Original);
            Move(true);
            yield return .3f;
            Commit();
            Select(First);
            yield return .3f;
            ResetHistory();

            // 保存键必须先提交仍聚焦的正文，不能保存少最后几个字的旧 model。
            Click(Body.gameObject);
            yield return .2f;
            Key(Body, KeyCode.End, EventModifiers.Control);
            Key(Body, KeyCode.None, EventModifiers.None, '存');
            yield return .2f;
            string saved = Body.text;
            Key(Body, KeyCode.S, EventModifiers.Control);
            yield return 1.5f;
            Check("Ctrl+S writes latest focused text", JsonConvert.DeserializeObject<Dictionary<int, TalkCfg>>(File.ReadAllText(_file))[First].content == saved);
            Check("Saved draft is clean", !Dirty());
            Move(false);
            yield return .3f;
            Commit();
            yield return .3f;
            Check("Save retains history; undo marks dirty", Body.text != saved && Dirty());
            Move(true);
            yield return .3f;
            Commit();
            yield return .3f;
            Check("Redo to saved text is clean", Body.text == saved && !Dirty());
            ResetHistory();

            // 普通 InputField 中间态也能撤销，不先触发原版的数字/列表解析。
            Click(_view.input_action.gameObject);
            yield return .2f;
            _view.input_action.text = "未写完的参数";
            yield return .2f;
            _view.input_action.ProcessEvent(new Event
                { type = EventType.KeyDown, keyCode = KeyCode.Z, modifiers = EventModifiers.Control });
            yield return .2f;
            Check("Legacy field undo avoids committing invalid intermediate text", _view.input_action.text == "");
            _view.input_action.ProcessEvent(new Event
                { type = EventType.KeyDown, keyCode = KeyCode.Y, modifiers = EventModifiers.Control });
            yield return .2f;
            Check("Legacy field redo restores exact uncommitted text", _view.input_action.text == "未写完的参数");
            _view.input_action.ProcessEvent(new Event
                { type = EventType.KeyDown, keyCode = KeyCode.Z, modifiers = EventModifiers.Control });
            yield return .2f;
            Commit();
            yield return .2f;
            ResetHistory();

            // 整句删除、新增和选项是独立步骤，Ctrl+Z 不要求当前仍选中一句。
            Select(First + 1);
            yield return .2f;
            Click(_view.btn_delete.gameObject);
            yield return .2f;
            Check("Delete line removes record", Talks.Count == 1 && Current == null);
            Click(Undo.gameObject);
            yield return .3f;
            Check("Undo delete restores selection and record", Talks.Count == 2 && Current.id == First + 1);
            Click(Redo.gameObject);
            yield return .2f;
            Check("Redo delete works with no text focus", Talks.Count == 1);
            Click(Undo.gameObject);
            yield return .2f;
            int count = Talks.Count;
            Click(_view.btn_new.gameObject);
            yield return .3f;
            Check("New line adds record", Talks.Count == count + 1);
            Check("New action after undo clears redo", !Redo.interactable);
            Click(Undo.gameObject);
            yield return .3f;
            Check("Undo new removes record and auto-link", Talks.Count == count && Current.nextTalk.IsEmpty());

            Call(_view, "OnEditOption", new OptionCfg { id = EventId * 100 + 1, content = "临时选项" });
            yield return .3f;
            Check("Option applied", Options.Count == 1 && Current.option.Count == 1);
            Move(false);
            yield return .3f;
            Check("Undo option restores option dictionary and parent links", Options.Count == 0 && Current.option.IsEmpty());
            Move(true);
            yield return .3f;
            Check("Redo option restores both", Options.Count == 1 && Current.option.Count == 1);

            // CG 正文和普通正文共用历史，但按钮始终留在左上角。
            Select(First);
            yield return .2f;
            int cgId = Cfg.CGCfgMap.Keys.First();
            Call(_view, "OnSelectCg", cgId);
            yield return .4f;
            Check("CG mode entered", _view.inputex_talk_cg.gameObject.activeInHierarchy);
            TMP_InputField cg = _view.inputex_talk_cg;
            Check("Switching to CG retains latest committed ordinary text", cg.text == Current.content);
            string cgOriginal = cg.text;
            Click(cg.gameObject);
            yield return .2f;
            Check("CG click does not select entire text", cg.selectionStringAnchorPosition == cg.selectionStringFocusPosition);
            Key(cg, KeyCode.A, EventModifiers.Control);
            Key(cg, KeyCode.Delete);
            yield return .2f;
            Key(cg, KeyCode.Z, EventModifiers.Control);
            yield return .2f;
            Check("CG Ctrl+Z recovers deleted text", cg.text == cgOriginal);
            Commit();
            yield return .2f;
            // 丢弃的 CG 操作还原，后面的保存用不带 CG 的临时剧情。
            Current.screenEffect = null;
            Select(First);
            yield return .3f;
            ResetHistory();

            // 别的插件拒绝保存时仍可继续编辑/撤销，不误清脏标记。
            Click(Body.gameObject);
            yield return .2f;
            Key(Body, KeyCode.End, EventModifiers.Control);
            Key(Body, KeyCode.None, EventModifiers.None, '拦');
            yield return .2f;
            Plugin.BlockSave = true;
            Key(Body, KeyCode.S, EventModifiers.Control);
            yield return .4f;
            Plugin.BlockSave = false;
            Check("Blocked save keeps disk unchanged and draft dirty",
                Dirty() && JsonConvert.DeserializeObject<Dictionary<int, TalkCfg>>(File.ReadAllText(_file))[First].content == saved);
            Move(false);
            yield return .3f;
            Commit();
            yield return .2f;
            Check("Undo remains usable after blocked save", Body.text == saved);

            var search = EvtSearch().GetComponent<InputField>();
            Click(search.gameObject);
            yield return .2f;
            search.text = "台词";
            yield return .2f;
            search.ProcessEvent(new Event
                { type = EventType.KeyDown, keyCode = KeyCode.S, modifiers = EventModifiers.Control });
            yield return 1.2f;
            Check("Ctrl+S also saves while search is focused", !Dirty());
            search.text = "";
            yield return .2f;

            // 模态确认框显示时快捷键不能穿透到底下的剧情页。
            HintHelper.ShowConfirm("QA modal", null, null, true);
            yield return .6f;
            Check("Modal blocks form undo shortcut", !(bool)Call(_history, "Shortcut", KeyCode.Z, true, false, false));
            UIMgr.CloseView<CommonComfirmView>();
            yield return .3f;

            var graph = _view.gameObject.GetComponents<MonoBehaviour>().Single(c => c.GetType().Name == "StoryGraphWindow");
            Call(graph, "Open");
            yield return .6f;
            Check("Graph blocks form undo shortcut", !(bool)Call(_history, "Shortcut", KeyCode.Z, true, false, false));
            Select(First + 1);
            Call(graph, "EnterEditMode");
            yield return .4f;
            var session = AccessTools.Field(graph.GetType(), "_editSession").GetValue(graph);
            Check("Actual graph edit session opened", session != null);
            var graphTalks = (List<TalkCfg>)AccessTools.Property(session.GetType(), "Talks").GetValue(session);
            TalkCfg graphTalk = graphTalks.Single(t => t.id == First + 1);
            object[] updateArgs = { graphTalk, "剧情图同步后的正文", graphTalk.roleName, graphTalk.showTxt, null };
            Check("Graph draft edit succeeds", (bool)AccessTools.Method(session.GetType(), "TryUpdateTalkFields").Invoke(session, updateArgs));
            Call(graph, "SaveEditSession");
            yield return .7f;
            Check("Actual graph save updates disk", JsonConvert.DeserializeObject<Dictionary<int, TalkCfg>>(File.ReadAllText(_file))[First + 1].content == "剧情图同步后的正文");
            Call(graph, "Close");
            yield return .4f;
            Move(false);
            yield return .3f;
            Check("Graph sync can be undone as one step", Talks.Single(t => t.id == First + 1).content != "剧情图同步后的正文");
            Move(true);
            yield return .3f;
            Check("Graph sync can be redone", Talks.Single(t => t.id == First + 1).content == "剧情图同步后的正文");

            // 每次重新打开都从磁盘建立新的历史，绝不把别的事件历史带进来。
            AccessTools.Method(_assembly.GetType("StudentAgeEditorPlus.Patches.UnsavedEditGuard"), "CloseWithoutPrompt")
                .Invoke(null, new object[] { _view, true, false });
            yield return .5f;
            yield return Open();
            Check("Reopen has no undo or redo", !Undo.interactable && !Redo.interactable);
            Check("Reopen loads saved text, not discarded history", Body.text == saved);
            Shot("03_final");
            yield return .3f;
        }

        private Transform EvtSearch() => _view.gameObject.GetComponentsInChildren<InputField>()
            .Single(f => f.name == "EditorPlus_SearchBar").transform;
    }
}
