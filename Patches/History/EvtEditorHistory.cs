using System;
using System.Collections.Generic;
using Config;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>原版剧情表单的会话历史；不写盘，不接管剧情图或其它弹窗的快捷键。</summary>
    internal sealed class EvtEditorHistory : MonoBehaviour
    {
        private readonly EditorUndoHistory<EvtHistorySnapshot> _history =
            new EditorUndoHistory<EvtHistorySnapshot>(EvtHistorySnapshot.Same);
        private readonly Dictionary<string, EvtHistoryInput> _inputs =
            new Dictionary<string, EvtHistoryInput>(StringComparer.Ordinal);
        private ModEvtEditView _view;
        private StoryGraphWindow _graph;
        private UIButton _undo;
        private UIButton _redo;
        private int _mutating;
        private int _selecting;
        private bool _restoring;
        private bool _ready;
        private bool _saving;
        private bool _preparedSave;
        private bool _saveButtonWrapped;
        private EvtHistorySnapshot _saveSource;
        private NativeSaveAttempt _saveAttempt;
        private CanvasGroup _saveBlocker;
        private bool _wasInteractable;
        private float _pollAt;
        private int _shortcutFrame = -1;

        internal EvtHistoryInput ActiveInput { get; private set; }
        private bool Suppressed => !_ready || _restoring || _saving || _selecting > 0 || _mutating > 0;
        private bool GraphOpen => _graph != null && _graph.IsOpen;
        private bool CanOperate => _ready && !_saving && !GraphOpen && _view != null
            && _view.viewState == ViewState.Opened && UIMgr.IsTopView(_view)
            && !UnsavedEditDialog.IsShowingFor(_view);

        internal static EvtEditorHistory Get(ModEvtEditView view) =>
            view?.gameObject != null ? view.gameObject.GetComponent<EvtEditorHistory>() : null;

        internal static void Attach(ModEvtEditView view)
        {
            if (view?.gameObject == null) return;
            var history = Get(view) ?? view.gameObject.AddComponent<EvtEditorHistory>();
            history._view = view;
            history._graph = view.gameObject.GetComponent<StoryGraphWindow>();
            // OnOpen 最后挂载，小游戏等动态表单已建立；搜索栏不是剧情数据。
            foreach (TMP_InputField field in view.gameObject.GetComponentsInChildren<TMP_InputField>(true))
                if (field == view.inputex_talk || field == view.inputex_talk_cg)
                    history.AddInput(field.gameObject);
            foreach (InputField field in view.gameObject.GetComponentsInChildren<InputField>(true))
                if (field.transform.IsChildOf(view.group_content)) history.AddInput(field.gameObject);
            history.BuildButtons();
            if (!history._saveButtonWrapped)
            {
                var original = view.btn_save.btn.onClick;
                view.btn_save.btn.onClick = new Button.ButtonClickedEvent();
                view.btn_save.btn.onClick.AddListener(() =>
                {
                    if (history._saving) return;
                    history.Safe(history.PrepareSaveRequest);
                    original.Invoke();
                });
                history._saveButtonWrapped = true;
            }
            history.ActiveInput = null;
            history._saveSource = null;
            history._history.Reset(EvtHistoryAccess.Capture(view));
            history._ready = true;
            history.UpdateButtons();
        }

        private void AddInput(GameObject go)
        {
            string path = go.name;
            for (Transform parent = go.transform.parent; parent != null && parent != transform; parent = parent.parent)
                path = parent.name + "/" + path;
            var input = go.GetComponent<EvtHistoryInput>() ?? go.AddComponent<EvtHistoryInput>();
            input.Bind(this, path);
            _inputs[path] = input;
        }

        private void BuildButtons()
        {
            if (_undo != null) return;
            RectTransform add = _view.btn_new.transform;
            // 原「新建」占整条左栏。仅拆这一行，不增加高度、不动搜索/台词列表。
            var row = new GameObject("EditorPlus_HistoryRow", typeof(RectTransform));
            var rect = (RectTransform)row.transform;
            rect.SetParent(add.parent, false);
            rect.anchorMin = add.anchorMin;
            rect.anchorMax = add.anchorMax;
            rect.pivot = add.pivot;
            rect.anchoredPosition = add.anchoredPosition;
            rect.sizeDelta = add.sizeDelta;
            rect.localScale = add.localScale;
            _undo = CloneButton(add.gameObject, rect, "EditorPlus_Undo", "撤销", () => Safe(() => Move(false)));
            _redo = CloneButton(add.gameObject, rect, "EditorPlus_Redo", "重做", () => Safe(() => Move(true)));
            add.SetParent(rect, false);
            PlaceButton(add, 0f, 0.5f, 0f, -4f);
            PlaceButton(_undo.transform, 0.5f, 0.75f, 2f, -2f);
            PlaceButton(_redo.transform, 0.75f, 1f, 4f, 0f);
            _undo.gameObject.AddDescription(type => MainDescHelper.Text(type,
                "撤销上一步（Ctrl+Z）。支持正文、字段修改和新增/删除对话；只改草稿，不直接写盘。"));
            _redo.gameObject.AddDescription(type => MainDescHelper.Text(type,
                "重做（Ctrl+Y / Ctrl+Shift+Z）。在撤销后继续修改，会清除后面的重做记录。"));
        }

        private static UIButton CloneButton(GameObject template, Transform parent, string name,
            string text, Action click)
        {
            var clone = Instantiate(template, parent, false);
            clone.name = name;
            MiniGameUtil.StripBadComponents(clone);
            EvtStoryGraphInitPatch.SetButtonLabel(clone, text);
            var button = new UIButton(clone);
            button.AddClick(() => click());
            return button;
        }

        private static void PlaceButton(RectTransform rect, float left, float right, float insetLeft, float insetRight)
        {
            rect.localScale = Vector3.one;
            rect.anchorMin = new Vector2(left, 0f);
            rect.anchorMax = new Vector2(right, 1f);
            rect.offsetMin = new Vector2(insetLeft, 0f);
            rect.offsetMax = new Vector2(insetRight, 0f);
        }

        internal void BeginInput(EvtHistoryInput input)
        {
            if (Suppressed || GraphOpen || EvtStoryGraphViewAccess.GetCurrent(_view) == null) return;
            if (ActiveInput == input) return;
            PollData();
            _history.BreakMerge();
            ActiveInput = input;
            _history.Rebase(EvtHistoryAccess.Capture(_view, input));
        }

        internal void BreakInputGroup()
        {
            if (!Suppressed) _history.BreakMerge();
        }

        internal void InputChanged(EvtHistoryInput input)
        {
            if (Suppressed || GraphOpen) return;
            // 正常鼠标/键盘聚焦走 OnSelect；程序回填不是作者输入，不建立记录。
            if (ActiveInput != input) return;
            _saveSource = null;
            _history.Record(EvtHistoryAccess.Capture(_view, input, _history.Current), input.Path, Time.unscaledTime);
            UpdateButtons();
        }

        internal void EndInput(EvtHistoryInput input)
        {
            if (Suppressed || ActiveInput != input) return;
            if (_history.Current.InputText != input.Text)
            {
                // Esc 会还原本次聚焦前的文字，把这个还原也记成可撤销的一步。
                _history.Record(EvtHistoryAccess.Capture(_view, input), null, Time.unscaledTime);
            }
            ActiveInput = null;
            _history.Rebase(EvtHistoryAccess.Capture(_view));
            _history.BreakMerge();
            UpdateButtons();
        }

        private void CommitInput()
        {
            GiftFormInput.CommitFocused(_view);
            // 某些 TMP 滚动条配置会推迟 onEndEdit，保存/换句前显式提交最后文本。
            if (ActiveInput != null)
            {
                var input = ActiveInput;
                var tmp = input.GetComponent<TMP_InputField>();
                if (tmp != null) tmp.ReleaseSelection();
                if (ActiveInput != null) EndInput(input);
            }
        }

        internal void BeginAction()
        {
            if (!_ready || _restoring || _saving || GraphOpen) return;
            if (_mutating == 0)
            {
                CommitInput();
                PollData();
                _history.BreakMerge();
            }
            _mutating++;
        }

        internal void EndAction()
        {
            if (_mutating == 0) return;
            if (--_mutating == 0)
            {
                _history.Record(EvtHistoryAccess.Capture(_view), null, Time.unscaledTime);
                UpdateButtons();
            }
        }

        internal string BeforeSelect(TalkCfg talk)
        {
            if (!_ready || _restoring || _saving || GraphOpen) return null;
            if (_mutating == 0)
            {
                CommitInput();
                PollData();
                _history.BreakMerge();
            }
            _selecting++;
            return talk == null ? null : EditorChangeTracking.Fingerprint(talk);
        }

        internal void AfterSelect(TalkCfg talk, string before)
        {
            if (_selecting == 0) return;
            _selecting--;
            if (before != null && talk != null && before != EditorChangeTracking.Fingerprint(talk))
                _history.Visit(snapshot => snapshot.NormalizeTalk(before, talk));
            if (_mutating == 0)
                _history.Rebase(EvtHistoryAccess.Capture(_view));
        }

        private void PollData()
        {
            if (Suppressed || ActiveInput != null || GraphOpen) return;
            var current = EvtHistoryAccess.Capture(_view, null, _history.Current);
            if (_saveSource != null && current.Fingerprint == _saveSource.Fingerprint)
            {
                // 保存完成后的源码回填（LaTeX）不是作者再编辑了一遍。
                _history.Rebase(current);
                _saveSource = null;
            }
            else _history.Record(current, null, Time.unscaledTime);
        }

        internal void GraphSaved()
        {
            if (!_ready || _restoring) return;
            Safe(() =>
            {
                _saveSource = null;
                _history.BreakMerge();
                _history.Record(EvtHistoryAccess.Capture(_view), null, Time.unscaledTime);
            });
        }

        internal void PrepareSaveRequest()
        {
            if (!_ready || _saving) return;
            CommitInput();
            PollData();
            _history.BreakMerge();
            _saveSource = EvtHistoryAccess.Capture(_view);
            _preparedSave = true;
        }

        internal void BeginSave()
        {
            if (!_ready || _saving) return;
            // 点击监听链比 OnClickSave 的烘焙前缀更早，必须在那里提交正文和保存源码快照。
            // 自动化直接调用私有保存方法时，沿用最近一次作者状态，不把烘焙过程记成修改。
            if (!_preparedSave) _saveSource = _history.Current;
            _preparedSave = false;
            _saving = true;
            // 原版异步写盘还会补齐列表；期间不能继续改同一批对象。
            // 只锁表单，Tips 层的保存提示/继续编辑按钮不受影响。
            _saveBlocker = _view.gameObject.GetComponent<CanvasGroup>()
                           ?? _view.gameObject.AddComponent<CanvasGroup>();
            _wasInteractable = _saveBlocker.interactable;
            _saveBlocker.interactable = false;
        }

        internal void EndSave(bool ranOriginal)
        {
            if (!_saving) return;
            _saveAttempt = ranOriginal ? UnsavedEditGuard.LastSave(_view) : null;
            if (_saveAttempt == null) FinishSave();
        }

        private void FinishSave()
        {
            _saving = false;
            _saveAttempt = null;
            UnlockSave();
            // 保存补空列表、LaTeX 烘焙都不是新编辑，不清空已有撤销历史。
            _history.Rebase(EvtHistoryAccess.Capture(_view));
            _history.BreakMerge();
            UpdateButtons();
        }

        internal void Move(bool redo)
        {
            if (!CanOperate || HasComposition()) return;
            PollData();
            _saveSource = null;
            EvtHistorySnapshot rollback = EvtHistoryAccess.Capture(_view, ActiveInput);
            bool moved = _history.Move(redo, target =>
            {
                _restoring = true;
                try
                {
                    ActiveInput?.AbandonFocus();
                    ActiveInput = null;
                    EvtHistoryAccess.Apply(_view, target);
                    RestoreInput(target);
                    return true;
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogError("[EditorHistory] 恢复失败，保留当前草稿：" + e);
                    try
                    {
                        EvtHistoryAccess.Apply(_view, rollback);
                        RestoreInput(rollback);
                    }
                    catch (Exception inner) { Plugin.Log?.LogError("[EditorHistory] 草稿回滚失败：" + inner); }
                    ToastHelper.Toast("撤销/重做没有完成，请保留当前编辑页并查看日志。");
                    return false;
                }
                finally { _restoring = false; }
            });
            if (moved)
                _history.Rebase(EvtHistoryAccess.Capture(_view, ActiveInput));
            UpdateButtons();
        }

        private void RestoreInput(EvtHistorySnapshot snapshot)
        {
            if (snapshot.InputPath == null) return;
            if (_inputs.TryGetValue(snapshot.InputPath, out var input) && input.gameObject.activeInHierarchy)
            {
                ActiveInput = input;
                input.Restore(snapshot);
            }
        }

        internal bool Shortcut(KeyCode key, bool control, bool shift, bool alt)
        {
            if (!control || alt || (key != KeyCode.Z && key != KeyCode.Y && key != KeyCode.S)
                || !CanOperate || HasComposition()) return false;
            if (_shortcutFrame == Time.frameCount) return true;
            _shortcutFrame = Time.frameCount;
            if (key == KeyCode.S)
            {
                CommitInput();
                // 按钮监听链保留其它插件的检查，不能自行直接写 JSON。
                if (_view.btn_save.interactable) _view.btn_save.btn.onClick.Invoke();
            }
            else Move(key == KeyCode.Y || shift);
            return true;
        }

        private bool HasComposition()
        {
            var module = EventSystem.current?.currentInputModule;
            // 使用当前 UI 输入模块，不调用游戏禁用的旧 UnityEngine.Input。
            return module?.input != null && !string.IsNullOrEmpty(module.input.compositionString);
        }

        private static bool InputFocused()
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            return selected != null
                && ((selected.GetComponent<TMP_InputField>()?.isFocused ?? false)
                    || (selected.GetComponent<InputField>()?.isFocused ?? false));
        }

        private void OnGUI()
        {
            // 快速组合键可能在两次 Input System 更新之间完成按下和抬起，
            // wasPressedThisFrame 在本游戏所用版本里会漏掉它。窗口 KeyDown 保留每次按键。
            // 输入框仍走 KeyPressed；两条全局路径共用 Shortcut 的同帧去重。
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown || !_ready || InputFocused()) return;
            Safe(() =>
            {
                if (Shortcut(e.keyCode, e.control, e.shift, e.alt)) e.Use();
            });
        }

        private void UnlockSave()
        {
            if (_saveBlocker != null) _saveBlocker.interactable = _wasInteractable;
            _saveBlocker = null;
        }

        internal void Safe(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                _ready = false;
                _saving = false;
                UnlockSave();
                if (_undo != null) _undo.interactable = false;
                if (_redo != null) _redo.interactable = false;
                Plugin.Log?.LogError("[EditorHistory] 本页历史已停用，原版编辑/保存不受影响：" + e);
            }
        }

        private void Update()
        {
            if (!_ready) return;
            Safe(() =>
            {
                if (_saving && _saveAttempt != null && _saveAttempt.Finished) FinishSave();
                if (!CanOperate) { UpdateButtons(); return; }
                bool focused = InputFocused();
                var keyboard = Keyboard.current;
                // 聚焦时从输入控件 KeyPressed 处理，避免重复执行和中文输入法冲突。
                if (!focused && keyboard != null)
                {
                    KeyCode key = keyboard.zKey.wasPressedThisFrame ? KeyCode.Z
                        : keyboard.yKey.wasPressedThisFrame ? KeyCode.Y
                        : keyboard.sKey.wasPressedThisFrame ? KeyCode.S : KeyCode.None;
                    Shortcut(key, keyboard.ctrlKey.isPressed, keyboard.shiftKey.isPressed, keyboard.altKey.isPressed);
                }
                if (Time.unscaledTime >= _pollAt)
                {
                    _pollAt = Time.unscaledTime + 0.25f;
                    PollData(); // 包括人物/音频选择器的闭包回调和其它插件的字段修改。
                    UpdateButtons();
                }
                if (ActiveInput != null && _history.Current != null)
                {
                    _history.Current.Anchor = ActiveInput.Anchor;
                    _history.Current.Focus = ActiveInput.Focus;
                }
            });
        }

        private void UpdateButtons()
        {
            bool ready = CanOperate;
            if (_undo != null) _undo.interactable = ready && _history.CanUndo;
            if (_redo != null) _redo.interactable = ready && _history.CanRedo;
        }
    }
}
