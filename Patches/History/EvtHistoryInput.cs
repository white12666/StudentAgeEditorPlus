using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>只绑定事件编辑页的字段；输入未失焦时也保留原文和选区。</summary>
    internal sealed class EvtHistoryInput : MonoBehaviour, ISelectHandler, IPointerDownHandler
    {
        internal EvtEditorHistory Owner;
        internal string Path;
        private TMP_InputField _tmp;
        private InputField _legacy;

        internal string Text => _tmp != null ? _tmp.text : _legacy.text;
        internal bool IsFocused => _tmp != null ? _tmp.isFocused : _legacy.isFocused;
        internal int Anchor => _tmp != null ? _tmp.selectionStringAnchorPosition : _legacy.selectionAnchorPosition;
        internal int Focus => _tmp != null ? _tmp.selectionStringFocusPosition : _legacy.selectionFocusPosition;

        internal void Bind(EvtEditorHistory owner, string path)
        {
            if (Owner != null) return;
            Owner = owner;
            Path = path;
            _tmp = GetComponent<TMP_InputField>();
            _legacy = GetComponent<InputField>();
            if (_tmp != null)
            {
                // 普通正文和 CG 正文均点哪里就从哪里编辑，Ctrl+A 仍保留。
                _tmp.onFocusSelectAll = false;
                _tmp.onValueChanged.AddListener(Changed);
                _tmp.onEndEdit.AddListener(Ended);
            }
            else
            {
                _legacy.onValueChanged.AddListener(Changed);
                _legacy.onEndEdit.AddListener(Ended);
            }
        }

        public void OnSelect(BaseEventData data) => Owner.Safe(() => Owner.BeginInput(this));
        public void OnPointerDown(PointerEventData data) => Owner.Safe(Owner.BreakInputGroup);
        private void Changed(string text) { if (IsFocused) Owner.Safe(() => Owner.InputChanged(this)); }
        private void Ended(string text) => Owner.Safe(() => Owner.EndInput(this));

        internal void AbandonFocus()
        {
            // 撤销要丢弃的是当前未提交输入，不能让旧 onEndEdit 在换记录后回写。
            if (_tmp != null)
            {
                var original = _tmp.onEndEdit;
                _tmp.onEndEdit = new TMP_InputField.SubmitEvent();
                try { _tmp.DeactivateInputField(); EventSystem.current?.SetSelectedGameObject(null); }
                finally { _tmp.onEndEdit = original; }
            }
            else
            {
                var original = _legacy.onEndEdit;
                _legacy.onEndEdit = new InputField.SubmitEvent();
                try { _legacy.DeactivateInputField(); EventSystem.current?.SetSelectedGameObject(null); }
                finally { _legacy.onEndEdit = original; }
            }
        }

        internal void Restore(EvtHistorySnapshot snapshot)
        {
            if (_tmp != null) _tmp.text = snapshot.InputText;
            else _legacy.text = snapshot.InputText;
            EventSystem.current?.SetSelectedGameObject(gameObject);
            if (_tmp != null) _tmp.ActivateInputField(); else _legacy.ActivateInputField();
            SetSelection(snapshot);
            // InputField 在下一帧激活时会改选区；激活完再恢复一次。
            StartCoroutine(RestoreSelection(snapshot));
        }

        private IEnumerator RestoreSelection(EvtHistorySnapshot snapshot)
        {
            yield return null;
            if (IsFocused && Owner.ActiveInput == this && Text == snapshot.InputText)
                SetSelection(snapshot);
        }

        private void SetSelection(EvtHistorySnapshot snapshot)
        {
            int a = Mathf.Clamp(snapshot.Anchor, 0, Text.Length);
            int f = Mathf.Clamp(snapshot.Focus, 0, Text.Length);
            if (_tmp != null)
            {
                _tmp.ForceLabelUpdate();
                _tmp.selectionStringAnchorPosition = a;
                _tmp.selectionStringFocusPosition = f;
                _tmp.ForceLabelUpdate();
            }
            else
            {
                _legacy.selectionAnchorPosition = a;
                _legacy.selectionFocusPosition = f;
                _legacy.ForceLabelUpdate();
            }
        }
    }
}
