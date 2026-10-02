using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Config;
using HarmonyLib;
using Newtonsoft.Json;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using View.Hint;
using View.Main;
using View.Mod;

namespace StudentAgeEditorPlus.HistoryTests
{
    /// <summary>
    /// 外部键盘验收：只负责临时作品准备和状态观测，绝不调用 ProcessEvent/Move/Shortcut。
    /// 实际点击和组合键由桌面驱动投递；command.txt 仅用于测试场景切换与收尾。
    /// </summary>
    public sealed class ExternalKeyboardProbe : MonoBehaviour
    {
        private const int EventId = 1987653;
        private const int First = EventId * 1000 + 1;
        private const string Text = "这是一段不能丢失的原文。 Native text keep safe.";
        private ModEvtEditView _view;
        private string _root;
        private string _file;
        private Assembly _assembly;
        private float _next;
        private bool _ready;
        private bool _initialBackground;
        private int _sequence;
        private string _ime = "";
        private readonly List<object> _keys = new List<object>();
        private readonly List<object> _imeEvents = new List<object>();
        private readonly List<object> _windowKeys = new List<object>();
        private Keyboard _keyboard;

        private IEnumerator Start()
        {
            _initialBackground = Application.runInBackground;
            Application.runInBackground = true;
            while (!MenuReady()) yield return new WaitForSecondsRealtime(.2f);
            _assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "StudentAgeEditorPlus");
            _root = Path.Combine(Plugin.Output, "project");
            string dir = Path.Combine(_root, "Cfgs", LocalizationMgr.Lang);
            Directory.CreateDirectory(dir);
            _file = Path.Combine(dir, "TalkCfg.json");
            File.WriteAllText(_file, JsonConvert.SerializeObject(new Dictionary<int, TalkCfg>
            {
                [First] = new TalkCfg { id = First, content = Text },
                [First + 1] = new TalkCfg { id = First + 1, content = "第二句，验证跨句撤销。 Second line." }
            }));
            File.WriteAllText(Path.Combine(dir, "EvtCfg.json"), JsonConvert.SerializeObject(new Dictionary<int, EvtCfg>
            {
                [EventId] = new EvtCfg { id = EventId, title = "真实键盘测试临时作品", talkId = new List<int> { First } }
            }));
            yield return Open();
            _keyboard = Keyboard.current;
            if (_keyboard != null) _keyboard.onIMECompositionChange += OnComposition;
            _ready = true;
            Observe();
            File.WriteAllText(Path.Combine(Plugin.Output, "ready.txt"), _assembly.GetName().Version.ToString());
        }

        private static bool MenuReady()
        {
            try { return UIMgr.IsViewOpened<EntryView>(); } catch { return false; }
        }

        private IEnumerator Open()
        {
            UIMgr.OpenView<ModEvtEditView>(UILayerType.None, null, _root, EventId, new List<int> { First });
            while ((_view = UIMgr.GetView<ModEvtEditView>(false) as ModEvtEditView) == null
                   || _view.viewState != ViewState.Opened) yield return new WaitForSecondsRealtime(.1f);
            yield return new WaitForSecondsRealtime(.5f);
            var talks = (List<TalkCfg>)AccessTools.Field(typeof(ModEvtEditView), "talkCfgs").GetValue(_view);
            AccessTools.Method(typeof(ModEvtEditView), "Select").Invoke(_view, new object[] { talks[0] });
            yield return new WaitForSecondsRealtime(.3f);
        }

        private void OnComposition(IMECompositionString value)
        {
            _ime = value.ToString();
            _imeEvents.Add(new { frame = Time.frameCount, text = _ime });
        }

        private void OnGUI()
        {
            if (!_ready) return;
            Event e = Event.current;
            if (e != null && (e.type == EventType.KeyDown || e.type == EventType.KeyUp))
                _windowKeys.Add(new { frame = Time.frameCount, type = e.type.ToString(),
                    key = e.keyCode.ToString(), ctrl = e.control, shift = e.shift,
                    character = (int)e.character });
        }

        private void Update()
        {
            if (!_ready) return;
            try
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null)
                {
                    foreach (var key in keyboard.allKeys)
                        if (key.wasPressedThisFrame)
                            _keys.Add(new { frame = Time.frameCount, key = key.keyCode.ToString(),
                                ctrl = keyboard.ctrlKey.isPressed, shift = keyboard.shiftKey.isPressed,
                                alt = keyboard.altKey.isPressed, focus = FocusName(),
                                ime = _ime });
                }
                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + .15f;
                string command = Path.Combine(Plugin.Output, "command.txt");
                if (File.Exists(command))
                {
                    string text = File.ReadAllText(command).Trim();
                    File.Delete(command);
                    Command(text);
                }
                Observe();
            }
            catch (Exception e)
            {
                File.AppendAllText(Path.Combine(Plugin.Output, "probe-errors.txt"), e + Environment.NewLine);
            }
        }

        private void Command(string text)
        {
            // 不提供直接撤销/重做/保存入口；这些操作必须来自被测试的键盘或鼠标路径。
            if (text == "modal")
                HintHelper.ShowConfirm("键盘隔离测试：这里不应操作底下的剧情。", null, null, true);
            else if (text == "close-modal")
                UIMgr.CloseView<CommonComfirmView>();
            else if (text == "cg")
                AccessTools.Method(typeof(ModEvtEditView), "OnSelectCg")
                    .Invoke(_view, new object[] { Cfg.CGCfgMap.Keys.First() });
            else if (text == "normal")
                AccessTools.Method(typeof(ModEvtEditView), "OnClickCGEnd").Invoke(_view, null);
            else if (text == "quit")
            {
                _ready = false;
                try { Observe(); }
                finally
                {
                    Application.runInBackground = _initialBackground;
                    File.WriteAllText(Path.Combine(Plugin.Output, "stopped.txt"), "Probe requested a normal game exit.");
                    Application.Quit();
                }
            }
            else if (text.StartsWith("shot:", StringComparison.Ordinal))
                ScreenCapture.CaptureScreenshot(Path.Combine(Plugin.Output, text.Substring(5) + ".png"));
            File.WriteAllText(Path.Combine(Plugin.Output, "last-command.txt"), text);
        }

        private static object InputState(TMP_InputField field) => new
        {
            field.name, text = field.text, focused = field.isFocused,
            active = field.gameObject.activeInHierarchy,
            anchor = field.selectionStringAnchorPosition, focus = field.selectionStringFocusPosition,
            rect = Rect(field.transform)
        };
        private static string FocusName()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            // Unity 销毁的控件仍可能作为 EventSystem 的旧引用存在，?. 不检查 Unity 的假 null。
            return selected != null ? selected.name : null;
        }
        private static object Rect(Transform transform)
        {
            var corners = new Vector3[4];
            ((RectTransform)transform).GetWorldCorners(corners);
            Canvas canvas = transform.GetComponentInParent<Canvas>();
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return new { x = a.x, y = Screen.height - b.y, width = b.x - a.x, height = b.y - a.y };
        }

        private void Observe()
        {
            if (_view?.gameObject == null) return;
            var current = (TalkCfg)AccessTools.Field(typeof(ModEvtEditView), "curSelect").GetValue(_view);
            var talks = (List<TalkCfg>)AccessTools.Field(typeof(ModEvtEditView), "talkCfgs").GetValue(_view);
            var component = _view.gameObject.GetComponent(_assembly.GetType("StudentAgeEditorPlus.Patches.EvtEditorHistory"));
            object history = component == null ? null : AccessTools.Field(component.GetType(), "_history").GetValue(component);
            var search = _view.gameObject.GetComponentsInChildren<InputField>().SingleOrDefault(f => f.name == "EditorPlus_SearchBar");
            object[] dirtyArgs = { _view, null };
            bool dirty = (bool)AccessTools.Method(_assembly.GetType("StudentAgeEditorPlus.Patches.UnsavedEditGuard"),
                "TryGetUnsavedChanges").Invoke(null, dirtyArgs);
            string uiComposition = EventSystem.current?.currentInputModule?.input?.compositionString ?? "";
            Dictionary<int, TalkCfg> saved = File.Exists(_file)
                ? JsonConvert.DeserializeObject<Dictionary<int, TalkCfg>>(File.ReadAllText(_file)) : null;
            var data = new
            {
                sequence = ++_sequence, frame = Time.frameCount,
                version = _assembly.GetName().Version.ToString(),
                screen = new { width = Screen.width, height = Screen.height, appFocused = Application.isFocused },
                focus = FocusName(),
                current = current?.id, dirty,
                history = history == null ? null : new
                {
                    canUndo = (bool)AccessTools.Property(history.GetType(), "CanUndo").GetValue(history),
                    canRedo = (bool)AccessTools.Property(history.GetType(), "CanRedo").GetValue(history),
                    count = (int)AccessTools.Property(history.GetType(), "Count").GetValue(history)
                },
                normal = InputState(_view.inputex_talk), cg = InputState(_view.inputex_talk_cg),
                legacy = new { text = _view.input_action.text, focused = _view.input_action.isFocused, rect = Rect(_view.input_action.transform) },
                search = search == null ? null : new { text = search.text, focused = search.isFocused, rect = Rect(search.transform) },
                talks = talks.Select(t => new { t.id, t.content, t.nextTalk, t.screenEffect }).ToArray(),
                saved = saved?.Values.Select(t => new { t.id, t.content }).ToArray(),
                buttons = _view.gameObject.GetComponentsInChildren<Button>(false)
                    .Where(b => b.name == "EditorPlus_Undo" || b.name == "EditorPlus_Redo" || b.name == "btn_delete" || b.name == "btn_new")
                    .Select(b => new { b.name, enabled = b.IsInteractable(), rect = Rect(b.transform) }).ToArray(),
                modal = UIMgr.IsViewOpened<CommonComfirmView>(),
                ime = new { inputSystem = _ime, uiModule = uiComposition, events = _imeEvents.ToArray() },
                keyEvents = _keys.ToArray(), windowKeyEvents = _windowKeys.ToArray()
            };
            File.WriteAllText(Path.Combine(Plugin.Output, "state.json"), JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private void OnDestroy()
        {
            if (_keyboard != null) _keyboard.onIMECompositionChange -= OnComposition;
        }
    }
}
