using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 旧版 UGUI InputField 没有滚轮接口，只会在光标移动时改写内部可见行。
    /// 这里让正文 Text 保持完整高度并由原输入框的 RectMask2D 裁切，再只接管
    /// 垂直滚轮和滚动条；点击、选区、拖动光标仍全部交给 InputField。
    /// </summary>
    internal sealed class StoryGraphInputScroll : MonoBehaviour, IScrollHandler
    {
        private const float LeftPadding = 9f;
        private const float RightPadding = 22f;
        private const float TopPadding = 6f;
        private const float BottomPadding = 6f;
        private const float WheelStep = 34f;

        [NonSerialized] private InputField _input;
        [NonSerialized] private Text _text;
        [NonSerialized] private Scrollbar _scrollbar;
        [NonSerialized] private CanvasGroup _scrollbarCanvas;
        [NonSerialized] private ScrollRect _parentScroll;
        [NonSerialized] private string _lastText;
        [NonSerialized] private Vector2 _lastFieldSize;
        [NonSerialized] private float _contentHeight;
        [NonSerialized] private float _viewportHeight;
        [NonSerialized] private float _offset;
        [NonSerialized] private float _maxOffset;
        [NonSerialized] private int _lastCaret = -1;
        [NonSerialized] private bool _dirty;
        [NonSerialized] private bool _followCaret;
        [NonSerialized] private bool _syncingScrollbar;

        internal void Bind(
            InputField input, Scrollbar scrollbar,
            CanvasGroup scrollbarCanvas, ScrollRect parentScroll)
        {
            if (_input != null && _input != input)
                _input.onValueChanged.RemoveListener(OnValueChanged);
            if (_scrollbar != null && _scrollbar != scrollbar)
                _scrollbar.onValueChanged.RemoveListener(OnScrollbarChanged);

            _input = input;
            _text = input != null ? input.textComponent : null;
            _scrollbar = scrollbar;
            _scrollbarCanvas = scrollbarCanvas;
            _parentScroll = parentScroll;
            if (_input != null)
            {
                _input.onValueChanged.RemoveListener(OnValueChanged);
                _input.onValueChanged.AddListener(OnValueChanged);
            }
            if (_scrollbar != null)
            {
                _scrollbar.onValueChanged.RemoveListener(OnScrollbarChanged);
                _scrollbar.onValueChanged.AddListener(OnScrollbarChanged);
            }

            RectTransform placeholder = _input != null
                && _input.placeholder != null
                ? _input.placeholder.rectTransform : null;
            if (placeholder != null)
            {
                Vector2 max = placeholder.offsetMax;
                max.x = -RightPadding;
                placeholder.offsetMax = max;
            }

            _lastText = null;
            _lastFieldSize = new Vector2(float.NaN, float.NaN);
            _offset = 0f;
            _lastCaret = -1;
            _dirty = true;
            _followCaret = true;
        }

        internal void ResetToTop()
        {
            _offset = 0f;
            _lastCaret = -1;
            _dirty = true;
            _followCaret = false;
            if (_scrollbar != null) _scrollbar.SetValueWithoutNotify(0f);
            RefreshLayout();
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (eventData == null) return;
            float delta = eventData.scrollDelta.y;
            if (Mathf.Abs(delta) < 0.001f) return;

            bool canMove = _maxOffset > 0.5f
                           && (delta < 0f
                               ? _offset < _maxOffset - 0.5f
                               : _offset > 0.5f);
            if (!canMove)
            {
                if (_parentScroll != null) _parentScroll.OnScroll(eventData);
                return;
            }

            _offset = Mathf.Clamp(
                _offset - delta * WheelStep, 0f, _maxOffset);
            ApplyOffset();
            eventData.Use();
        }

        private void OnValueChanged(string ignored)
        {
            _dirty = true;
            _followCaret = true;
        }

        private void OnScrollbarChanged(float value)
        {
            if (_syncingScrollbar) return;
            _offset = Mathf.Clamp01(value) * _maxOffset;
            ApplyOffset();
        }

        private void OnRectTransformDimensionsChange()
        {
            _dirty = true;
        }

        private void LateUpdate()
        {
            if (_input == null || _text == null) return;
            RectTransform field = _input.transform as RectTransform;
            Vector2 fieldSize = field != null ? field.rect.size : Vector2.zero;
            string current = _input.text ?? string.Empty;
            if (!string.Equals(current, _lastText, StringComparison.Ordinal)
                || fieldSize != _lastFieldSize)
                _dirty = true;

            if (_dirty) RefreshLayout();

            int caret = _input.caretPosition;
            if (_followCaret || (_input.isFocused && caret != _lastCaret))
            {
                _followCaret = false;
                _lastCaret = caret;
                EnsureCaretVisible();
            }
        }

        private void RefreshLayout()
        {
            if (_input == null || _text == null
                || _scrollbar == null || _scrollbarCanvas == null)
                return;

            RectTransform field = _input.transform as RectTransform;
            RectTransform textRect = _text.rectTransform;
            if (field == null || textRect == null) return;

            _dirty = false;
            _lastText = _input.text ?? string.Empty;
            _lastFieldSize = field.rect.size;
            _viewportHeight = Mathf.Max(
                1f, field.rect.height - TopPadding - BottomPadding);

            ConfigureTextRect(textRect, _viewportHeight, 0f);
            _contentHeight = Mathf.Max(
                _viewportHeight, Mathf.Ceil(_input.preferredHeight));
            _maxOffset = Mathf.Max(0f, _contentHeight - _viewportHeight);
            _offset = Mathf.Clamp(_offset, 0f, _maxOffset);
            ConfigureTextRect(textRect, _contentHeight, _offset);
            _input.ForceLabelUpdate();

            bool needed = _maxOffset > 0.5f;
            _scrollbarCanvas.alpha = needed ? 1f : 0f;
            _scrollbarCanvas.interactable = needed;
            _scrollbarCanvas.blocksRaycasts = needed;
            _scrollbar.interactable = needed;
            _scrollbar.size = needed && _contentHeight > 0f
                ? Mathf.Clamp01(_viewportHeight / _contentHeight)
                : 1f;
            SyncScrollbar();
        }

        private static void ConfigureTextRect(
            RectTransform rect, float height, float offset)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(
                LeftPadding, -TopPadding - height + offset);
            rect.offsetMax = new Vector2(-RightPadding, -TopPadding + offset);
        }

        private void ApplyOffset()
        {
            if (_text == null) return;
            _offset = Mathf.Clamp(_offset, 0f, _maxOffset);
            ConfigureTextRect(_text.rectTransform, _contentHeight, _offset);
            _input?.ForceLabelUpdate();
            SyncScrollbar();
        }

        private void SyncScrollbar()
        {
            if (_scrollbar == null) return;
            _syncingScrollbar = true;
            _scrollbar.SetValueWithoutNotify(
                _maxOffset > 0f ? _offset / _maxOffset : 0f);
            _syncingScrollbar = false;
        }

        private void EnsureCaretVisible()
        {
            if (_input == null || _text == null || _maxOffset <= 0f) return;
            TextGenerator generator = _text.cachedTextGenerator;
            IList<UILineInfo> lines = generator != null ? generator.lines : null;
            if (lines == null || lines.Count == 0) return;

            int caret = Mathf.Clamp(
                _input.caretPosition, 0, (_input.text ?? string.Empty).Length);
            int line = 0;
            for (int i = 1; i < lines.Count; i++)
            {
                if (lines[i].startCharIdx > caret) break;
                line = i;
            }

            float pixelsPerUnit = Mathf.Max(0.01f, _text.pixelsPerUnit);
            float firstTop = lines[0].topY / pixelsPerUnit;
            float lineTop = firstTop - lines[line].topY / pixelsPerUnit;
            float lineBottom = lineTop + lines[line].height / pixelsPerUnit;
            float wanted = _offset;
            if (lineTop < _offset)
                wanted = lineTop;
            else if (lineBottom > _offset + _viewportHeight)
                wanted = lineBottom - _viewportHeight;

            wanted = Mathf.Clamp(wanted, 0f, _maxOffset);
            if (Mathf.Abs(wanted - _offset) < 0.5f) return;
            _offset = wanted;
            ApplyOffset();
        }

        private void OnDestroy()
        {
            if (_input != null)
                _input.onValueChanged.RemoveListener(OnValueChanged);
            if (_scrollbar != null)
                _scrollbar.onValueChanged.RemoveListener(OnScrollbarChanged);
        }
    }
}
