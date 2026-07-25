using System;
using System.Collections.Generic;
using System.Globalization;
using Config;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StudentAgeEditorPlus.Patches
{
    internal sealed partial class StoryGraphWindow
    {
        [NonSerialized] private GameObject _resourcePickerBlocker;
        [NonSerialized] private GameObject _resourcePickerRoot;
        [NonSerialized] private InputField _resourcePickerSearchInput;
        [NonSerialized] private Text _resourcePickerPageText;
        [NonSerialized] private Button _resourcePickerPreviousButton;
        [NonSerialized] private Button _resourcePickerSortButton;
        [NonSerialized] private Button _resourcePickerNextButton;
        [NonSerialized] private Button _inspectorBackgroundLookupButton;
        [NonSerialized] private Button _inspectorAudioLookupButton;
        [NonSerialized] private Button _inspectorCgLookupButton;
        [NonSerialized] private Button _inspectorMiniGameLookupButton;
        [NonSerialized] private Button _inspectorEventLookupButton;

        private readonly List<ResourcePickerRowVisual> _resourcePickerRows =
            new List<ResourcePickerRowVisual>();
        private List<StoryGraphResourceEntry> _resourcePickerEntries =
            new List<StoryGraphResourceEntry>();
        private StoryGraphResourceKind _resourcePickerKind;
        private int _resourcePickerPage = 1;
        private bool _resourcePickerDescending;
        private int _resourcePickerPageSize =
            StoryGraphResourcePickerLogic.DefaultPageSize;

        private sealed class ResourcePickerRowVisual
        {
            internal GameObject Root;
            internal Image Background;
            internal Button Button;
            internal Text Label;
            internal StoryGraphResourceEntry Entry;
        }

        /// <summary>
        /// 把普通整行输入框收窄，在右侧保留原版风格的“查阅”按钮。按钮仍在
        /// 属性栏的裁剪范围内，不会向屏幕右侧伸出。
        /// </summary>
        private Button AttachInspectorLookupButton(
            GameObject row,
            InputField input,
            StoryGraphResourceKind kind)
        {
            if (row == null || input == null) return null;
            const float buttonWidth = 64f;
            const float buttonX = 324f;
            const float inputWidth = 308f;
            RectTransform inputRect = input.transform as RectTransform;
            if (inputRect != null)
                inputRect.sizeDelta = new Vector2(inputWidth, inputRect.sizeDelta.y);

            Button button = CreateToolbarButton(
                row, "查阅", buttonX, -24f, buttonWidth,
                delegate { OpenInspectorResourcePicker(kind); });
            Place((RectTransform)button.transform,
                0f, 1f, 0f, 1f, buttonX, -24f, buttonWidth, 36f);
            Text text = button.GetComponentInChildren<Text>();
            if (text != null) text.fontSize = 13;
            return button;
        }

        private void OpenInspectorResourcePicker(StoryGraphResourceKind kind)
        {
            if (!_editMode || _canvasRoot == null || _canvasRect == null) return;
            TalkCfg talk;
            OptionCfg option;
            bool hasTarget = TryGetPerformanceTarget(out talk, out option);
            bool targetAllowed = kind == StoryGraphResourceKind.MiniGame
                ? hasTarget
                : kind == StoryGraphResourceKind.Event
                    ? hasTarget && option != null
                    : hasTarget && talk != null;
            if (!targetAllowed)
            {
                SetEditFeedback(
                    kind == StoryGraphResourceKind.MiniGame
                        ? "请先在剧情图中选择一条对话或一个选项。"
                        : kind == StoryGraphResourceKind.Event
                            ? "请选择一个选项节点后查阅后备下一事件。"
                            : "请选择一个对话节点后查阅演出资源。",
                    true);
                return;
            }

            FinalizeFocusedInspectorInput();
            EndInspectorLiveEdit();
            EnsurePerformanceResourceNames();
            CloseInspectorResourcePicker();

            _resourcePickerKind = kind;
            _resourcePickerPage = 1;
            _resourcePickerEntries = BuildResourcePickerEntries(kind);

            StoryGraphResourcePickerPlacement placement =
                StoryGraphResourcePickerLogic.CalculatePlacement(
                    _canvasRect.rect.width,
                    _canvasRect.rect.height,
                    _inspectorVisible ? InspectorWidth : 0f,
                    ToolbarHeight,
                    StatusbarHeight);
            if (placement.Width < 280f || placement.Height < 250f)
            {
                SetEditFeedback(
                    "当前窗口空间不足以安全显示资源查阅器，请先放大游戏窗口。",
                    true);
                return;
            }
            _resourcePickerPageSize = Mathf.Clamp(
                Mathf.FloorToInt((placement.Height - 174f) / 48f),
                1, StoryGraphResourcePickerLogic.DefaultPageSize);

            _resourcePickerBlocker = CreateUIObject(
                "ResourcePickerBlocker", _canvasRoot.transform);
            RectTransform blockerRect =
                (RectTransform)_resourcePickerBlocker.transform;
            Stretch(blockerRect, 0f, 0f, 0f, 0f);
            Image blockerImage = _resourcePickerBlocker.AddComponent<Image>();
            blockerImage.color = new Color(0.18f, 0.14f, 0.08f, 0.08f);
            blockerImage.raycastTarget = true;
            Button blockerButton = _resourcePickerBlocker.AddComponent<Button>();
            blockerButton.targetGraphic = blockerImage;
            blockerButton.transition = Selectable.Transition.None;
            blockerButton.onClick.AddListener(CloseInspectorResourcePicker);

            _resourcePickerRoot = CreateUIObject(
                "InspectorResourcePicker", _canvasRoot.transform);
            RectTransform root = (RectTransform)_resourcePickerRoot.transform;
            Place(root, 1f, 1f, 1f, 1f,
                -placement.RightInset, -placement.TopInset,
                placement.Width, placement.Height);
            Image border = _resourcePickerRoot.AddComponent<Image>();
            border.color = PanelBorder;
            border.raycastTarget = true;
            ApplySprite(border, _roundedSprite);
            Image panel = CreateImage(
                _resourcePickerRoot, "Panel", PanelBg, true);
            Stretch(panel.rectTransform, 2f, 2f, 2f, 2f);
            ApplySprite(panel, _roundedSprite);

            Text title = CreateText(
                _resourcePickerRoot, "Title", 18, FontStyle.Bold,
                TitleColor, TextAnchor.MiddleLeft);
            title.text = ResourcePickerTitle(kind);
            Place(title.rectTransform, 0f, 1f, 0f, 1f,
                14f, -10f, placement.Width - 100f, 36f);

            Button close = CreateToolbarButton(
                _resourcePickerRoot, "关闭", -12f, -10f, 68f,
                CloseInspectorResourcePicker, true);
            Place((RectTransform)close.transform,
                1f, 1f, 1f, 1f, -12f, -10f, 68f, 34f);
            Text closeText = close.GetComponentInChildren<Text>();
            if (closeText != null) closeText.fontSize = 13;

            _resourcePickerSearchInput = CreateResourcePickerSearchField(
                placement.Width, ResourcePickerSearchPlaceholder(kind));
            _resourcePickerSearchInput.onValueChanged.AddListener(
                delegate
                {
                    _resourcePickerPage = 1;
                    RefreshInspectorResourcePicker();
                });

            _resourcePickerRows.Clear();
            for (int i = 0; i < _resourcePickerPageSize; i++)
                _resourcePickerRows.Add(CreateResourcePickerRow(
                    i, placement.Width));

            float footerY = -placement.Height + 48f;
            _resourcePickerPreviousButton = CreateToolbarButton(
                _resourcePickerRoot, "上一页", 12f, footerY, 82f,
                delegate
                {
                    _resourcePickerPage--;
                    RefreshInspectorResourcePicker();
                });
            Place((RectTransform)_resourcePickerPreviousButton.transform,
                0f, 1f, 0f, 1f, 12f, footerY, 82f, 36f);
            Text previousText =
                _resourcePickerPreviousButton.GetComponentInChildren<Text>();
            if (previousText != null) previousText.fontSize = 13;

            _resourcePickerSortButton = CreateToolbarButton(
                _resourcePickerRoot, string.Empty, 0f, footerY, 88f,
                delegate
                {
                    _resourcePickerDescending = !_resourcePickerDescending;
                    _resourcePickerPage = 1;
                    RefreshInspectorResourcePicker();
                });
            Place((RectTransform)_resourcePickerSortButton.transform,
                0.5f, 1f, 0.5f, 1f, 0f, footerY, 88f, 36f);
            Text sortText =
                _resourcePickerSortButton.GetComponentInChildren<Text>();
            if (sortText != null) sortText.fontSize = 13;

            _resourcePickerNextButton = CreateToolbarButton(
                _resourcePickerRoot, "下一页", -12f, footerY, 82f,
                delegate
                {
                    _resourcePickerPage++;
                    RefreshInspectorResourcePicker();
                }, true);
            Place((RectTransform)_resourcePickerNextButton.transform,
                1f, 1f, 1f, 1f, -12f, footerY, 82f, 36f);
            Text nextText =
                _resourcePickerNextButton.GetComponentInChildren<Text>();
            if (nextText != null) nextText.fontSize = 13;

            _resourcePickerPageText = CreateText(
                _resourcePickerRoot, "Page", SecondaryFontSize,
                FontStyle.Normal, SubtitleColor, TextAnchor.MiddleCenter);
            Place(_resourcePickerPageText.rectTransform,
                0.5f, 1f, 0.5f, 1f,
                0f, footerY + 38f,
                Mathf.Max(100f, placement.Width - 24f), 30f);

            _resourcePickerBlocker.transform.SetAsLastSibling();
            _resourcePickerRoot.transform.SetAsLastSibling();
            RefreshInspectorResourcePicker();
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(
                    _resourcePickerSearchInput.gameObject);
            _resourcePickerSearchInput.ActivateInputField();
        }

        private InputField CreateResourcePickerSearchField(
            float panelWidth, string placeholder)
        {
            GameObject go = CreateUIObject(
                "ResourceSearch", _resourcePickerRoot.transform);
            RectTransform rect = (RectTransform)go.transform;
            Place(rect, 0f, 1f, 0f, 1f,
                12f, -54f, panelWidth - 24f, 40f);
            Image image = go.AddComponent<Image>();
            image.color = HexColor("F7F1E3");
            image.raycastTarget = true;
            ApplySprite(image, _inputSprite);
            go.AddComponent<RectMask2D>();

            InputField input = go.AddComponent<InputField>();
            input.contentType = InputField.ContentType.Standard;
            input.lineType = InputField.LineType.SingleLine;
            input.caretColor = HexColor("824C24");
            input.selectionColor = new Color(0.89f, 0.62f, 0.29f, 0.4f);

            Text ph = CreateText(go, "Placeholder", SecondaryFontSize,
                FontStyle.Normal,
                new Color(0.42f, 0.34f, 0.25f, 0.62f),
                TextAnchor.MiddleLeft);
            ph.text = placeholder;
            Stretch(ph.rectTransform, 10f, 4f, 10f, 4f);
            input.placeholder = ph;

            Text value = CreateText(go, "Text", SecondaryFontSize,
                FontStyle.Normal, BodyTextColor, TextAnchor.MiddleLeft);
            Stretch(value.rectTransform, 10f, 4f, 10f, 4f);
            input.textComponent = value;
            return input;
        }

        private ResourcePickerRowVisual CreateResourcePickerRow(
            int index, float panelWidth)
        {
            GameObject row = CreateUIObject(
                "ResourceRow" + index, _resourcePickerRoot.transform);
            RectTransform rect = (RectTransform)row.transform;
            Place(rect, 0f, 1f, 0f, 1f,
                12f, -104f - index * 48f, panelWidth - 24f, 42f);
            Image image = row.AddComponent<Image>();
            image.color = PanelBg;
            image.raycastTarget = true;
            ApplySprite(image, _chipSprite);
            Button button = row.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = SegmentFill;
            colors.pressedColor = PanelBorder;
            colors.disabledColor = new Color(0.82f, 0.79f, 0.72f, 0.55f);
            colors.fadeDuration = 0.05f;
            button.colors = colors;
            Text label = CreateText(
                row, "Label", SecondaryFontSize, FontStyle.Normal,
                BodyTextColor, TextAnchor.MiddleLeft);
            Stretch(label.rectTransform, 12f, 2f, 12f, 2f);
            var visual = new ResourcePickerRowVisual
            {
                Root = row,
                Background = image,
                Button = button,
                Label = label,
            };
            button.onClick.AddListener(delegate
            {
                if (visual.Entry != null)
                    SelectInspectorResource(visual.Entry);
            });
            return visual;
        }

        private List<StoryGraphResourceEntry> BuildResourcePickerEntries(
            StoryGraphResourceKind kind)
        {
            IDictionary<int, string> names = ResourceNames(kind);
            var result = new List<StoryGraphResourceEntry>();
            if (kind == StoryGraphResourceKind.Background)
                result.Add(new StoryGraphResourceEntry(0, "沿用上一背景"));
            else if (kind == StoryGraphResourceKind.Audio)
            {
                result.Add(new StoryGraphResourceEntry(
                    -1, "剧情结束时切回日常 BGM"));
                result.Add(new StoryGraphResourceEntry(
                    0, "本句不切换音乐 / 音效"));
            }
            else if (kind == StoryGraphResourceKind.MiniGame)
            {
                TalkCfg talk;
                OptionCfg option;
                TryGetPerformanceTarget(out talk, out option);
                return StoryGraphResourcePickerLogic.BuildMiniGameEntries(
                    talk != null);
            }
            else if (kind == StoryGraphResourceKind.Event)
                result.Add(new StoryGraphResourceEntry(
                    0, "不设置后备下一事件"));

            foreach (KeyValuePair<int, string> pair in names)
            {
                if (pair.Key <= 0) continue;
                // 原版背景选择器只接受 >= 200000 的 BgCfg；较小编号是
                // 共用配置表里的普通图片/内部资源，并不是剧情背景。
                if (kind == StoryGraphResourceKind.Background
                    && !StoryGraphResourcePickerLogic.IsBackgroundBrowsable(
                        pair.Key))
                    continue;
                if (kind == StoryGraphResourceKind.Audio
                    && !_audioBrowseIds.Contains(pair.Key))
                    continue;
                result.Add(new StoryGraphResourceEntry(pair.Key, pair.Value));
            }
            return result;
        }

        private IDictionary<int, string> ResourceNames(
            StoryGraphResourceKind kind)
        {
            switch (kind)
            {
                case StoryGraphResourceKind.Audio: return _audioNames;
                case StoryGraphResourceKind.Cg: return _cgNames;
                case StoryGraphResourceKind.Event: return _eventNames;
                case StoryGraphResourceKind.MiniGame:
                    return new Dictionary<int, string>();
                default: return _backgroundNames;
            }
        }

        private int CurrentResourcePickerId(StoryGraphResourceKind kind)
        {
            int id;
            string error;
            if (kind == StoryGraphResourceKind.Background)
                return TryParseNamedResourceId(
                    _inspectorBackgroundInput != null
                        ? _inspectorBackgroundInput.text : string.Empty,
                    _backgroundNames, "背景", out id, out error)
                    ? id : 0;
            if (kind == StoryGraphResourceKind.Audio)
                return TryParseNamedResourceId(
                    _inspectorAudioInput != null
                        ? _inspectorAudioInput.text : string.Empty,
                    _audioNames, "声音", out id, out error)
                    ? id : 0;
            if (kind == StoryGraphResourceKind.MiniGame)
            {
                List<double> values;
                if (_inspectorMiniGameInput != null
                    && TryParseMiniGameText(
                        _inspectorMiniGameInput.text, out values, out error)
                    && MiniGameUtil.TryGetGameId(
                        values, out id, out error))
                    return id;
                return 0;
            }
            if (kind == StoryGraphResourceKind.Event)
                return TryParseNamedResourceId(
                    _inspectorNextEventInput != null
                        ? _inspectorNextEventInput.text : string.Empty,
                    _eventNames, "下一事件", out id, out error)
                    ? id : 0;

            List<float> effect;
            if (_inspectorScreenEffectInput != null
                && StoryGraphPerformanceCodec.TryParseFloatList(
                    _inspectorScreenEffectInput.text,
                    "屏幕效果", out effect, out error)
                && effect != null && effect.Count > 1
                && ((int)effect[0] == 4015 || (int)effect[0] == 4019))
                return (int)effect[1];
            return TryParseNamedResourceId(
                _inspectorScreenArgsInput != null
                    ? _inspectorScreenArgsInput.text : string.Empty,
                _cgNames, "CG", out id, out error)
                ? id : 0;
        }

        private void RefreshInspectorResourcePicker()
        {
            if (_resourcePickerRoot == null) return;
            StoryGraphResourcePage page = StoryGraphResourcePickerLogic.Query(
                _resourcePickerEntries,
                _resourcePickerSearchInput != null
                    ? _resourcePickerSearchInput.text : string.Empty,
                _resourcePickerPage,
                _resourcePickerPageSize,
                _resourcePickerDescending);
            _resourcePickerPage = page.Page;
            int selectedId = CurrentResourcePickerId(_resourcePickerKind);
            RectTransform pickerRect =
                _resourcePickerRoot.transform as RectTransform;
            float pickerWidth = pickerRect != null
                ? Math.Max(
                    280f, Math.Max(
                        pickerRect.rect.width, pickerRect.sizeDelta.x))
                : 280f;
            int nameLimit = Mathf.Clamp(
                Mathf.FloorToInt((pickerWidth - 70f) / 12f) * 2 - 10,
                24, 84);
            for (int i = 0; i < _resourcePickerRows.Count; i++)
            {
                ResourcePickerRowVisual visual = _resourcePickerRows[i];
                if (i >= page.Items.Count)
                {
                    visual.Entry = null;
                    visual.Root.SetActive(false);
                    continue;
                }

                StoryGraphResourceEntry entry = page.Items[i];
                bool selected = entry.Id == selectedId;
                visual.Entry = entry;
                visual.Root.SetActive(true);
                string displayName = StoryGraphInspectorText.Ellipsize(
                    entry.Name, nameLimit);
                visual.Label.text = (selected ? "✓  " : string.Empty)
                                    + "[" + entry.Id.ToString(
                                        CultureInfo.InvariantCulture) + "] "
                                    + displayName;
                visual.Label.fontStyle =
                    selected ? FontStyle.Bold : FontStyle.Normal;
                visual.Background.color = selected ? SegmentFill : PanelBg;
                visual.Button.interactable = true;
            }

            if (_resourcePickerPageText != null)
                _resourcePickerPageText.text =
                    page.TotalCount + " 项　"
                    + page.Page + " / " + page.PageCount;
            if (_resourcePickerPreviousButton != null)
                _resourcePickerPreviousButton.interactable = page.Page > 1;
            if (_resourcePickerNextButton != null)
                _resourcePickerNextButton.interactable =
                    page.Page < page.PageCount;
            if (_resourcePickerSortButton != null)
            {
                Text sortText =
                    _resourcePickerSortButton.GetComponentInChildren<Text>();
                if (sortText != null)
                    sortText.text = _resourcePickerDescending
                        ? "ID 倒序" : "ID 正序";
            }
        }

        private void SelectInspectorResource(StoryGraphResourceEntry entry)
        {
            if (entry == null) return;
            StoryGraphResourceKind kind = _resourcePickerKind;
            CloseInspectorResourcePicker();
            EndInspectorLiveEdit();
            string id = entry.Id.ToString(CultureInfo.InvariantCulture);

            if (kind == StoryGraphResourceKind.Background)
            {
                CommitStageResourceInput(_inspectorBackgroundInput, id);
                SetEditFeedback("已选择背景 " + id + "（"
                                + StoryGraphInspectorText.Ellipsize(
                                    entry.Name, 48)
                                + "），并实时写入剧情草稿。", false);
                return;
            }
            if (kind == StoryGraphResourceKind.Audio)
            {
                CommitStageResourceInput(_inspectorAudioInput, id);
                SetEditFeedback("已选择声音 " + id + "（"
                                + StoryGraphInspectorText.Ellipsize(
                                    entry.Name, 48)
                                + "），并实时写入剧情草稿。", false);
                return;
            }
            if (kind == StoryGraphResourceKind.MiniGame)
            {
                if (_inspectorMiniGameInput == null) return;
                _inspectorMiniGameInput.SetTextWithoutNotify(id);
                OnMiniGameInspectorValueChanged(id);
                bool needsParameters =
                    MiniGameUtil.NeedParamIds.Contains(entry.Id);
                if (!needsParameters)
                {
                    OnMiniGameInspectorEndEdit(id);
                }
                else
                {
                    if (EventSystem.current != null)
                        EventSystem.current.SetSelectedGameObject(
                            _inspectorMiniGameInput.gameObject);
                    _inspectorMiniGameInput.ActivateInputField();
                    _inspectorMiniGameInput.MoveTextEnd(false);
                }
                string miniGameName = StoryGraphInspectorText.Ellipsize(
                    entry.Name, 48);
                SetEditFeedback(needsParameters
                    ? "已选择“" + miniGameName
                      + "”。请在第②步按提示补齐设置；填写完整后自动保存。"
                    : "已选择“" + miniGameName + "”，设置已保存。", false);
                return;
            }
            if (kind == StoryGraphResourceKind.Event)
            {
                if (_inspectorNextEventInput == null) return;
                _inspectorNextEventInput.SetTextWithoutNotify(id);
                OnOptionNextEventValueChanged(id);
                OnOptionNextEventEndEdit(id);
                SetEditFeedback(entry.Id == 0
                    ? "已清空选项的后备下一事件。"
                    : "已选择后备下一事件 " + id + "（"
                      + StoryGraphInspectorText.Ellipsize(
                          entry.Name, 48)
                      + "），并实时写入剧情草稿。", false);
                return;
            }

            SetInspectorText(_inspectorScreenArgsInput, id);
            int effectCode = 4015;
            List<float> current;
            string error;
            if (_inspectorScreenEffectInput != null
                && StoryGraphPerformanceCodec.TryParseFloatList(
                    _inspectorScreenEffectInput.text,
                    "屏幕效果", out current, out error)
                && current != null && current.Count > 0
                && (int)current[0] == 4019)
                effectCode = 4019;
            CommitStageResourceInput(
                _inspectorScreenEffectInput,
                effectCode.ToString(CultureInfo.InvariantCulture)
                + ", " + id);
            SetEditFeedback("已选择 CG " + id + "（"
                            + StoryGraphInspectorText.Ellipsize(
                                entry.Name, 48)
                            + "），并实时写入显示 CG 效果。", false);
        }

        private void CommitStageResourceInput(InputField input, string value)
        {
            if (input == null) return;
            input.SetTextWithoutNotify(value ?? string.Empty);
            OnStageInspectorValueChanged(value);
            OnStageInspectorEndEdit(value);
        }

        private static string ResourcePickerTitle(StoryGraphResourceKind kind)
        {
            switch (kind)
            {
                case StoryGraphResourceKind.Audio: return "查阅背景音乐";
                case StoryGraphResourceKind.Cg: return "查阅 CG 资源";
                case StoryGraphResourceKind.MiniGame: return "选择小游戏";
                case StoryGraphResourceKind.Event: return "查阅后备下一事件";
                default: return "查阅背景资源";
            }
        }

        private static string ResourcePickerSearchPlaceholder(
            StoryGraphResourceKind kind)
        {
            switch (kind)
            {
                case StoryGraphResourceKind.Audio:
                    return "搜索声音 ID 或音乐名称…";
                case StoryGraphResourceKind.Cg:
                    return "搜索 CG ID 或名称…";
                case StoryGraphResourceKind.MiniGame:
                    return "按名称或编号搜索小游戏…";
                case StoryGraphResourceKind.Event:
                    return "搜索事件 ID、标题或摘要…";
                default:
                    return "搜索背景 ID 或名称…";
            }
        }

        private void CloseInspectorResourcePicker()
        {
            if (EventSystem.current != null
                && _resourcePickerSearchInput != null
                && EventSystem.current.currentSelectedGameObject
                    == _resourcePickerSearchInput.gameObject)
                EventSystem.current.SetSelectedGameObject(null);
            if (_resourcePickerRoot != null)
                UnityEngine.Object.Destroy(_resourcePickerRoot);
            if (_resourcePickerBlocker != null)
                UnityEngine.Object.Destroy(_resourcePickerBlocker);
            _resourcePickerRoot = null;
            _resourcePickerBlocker = null;
            _resourcePickerSearchInput = null;
            _resourcePickerPageText = null;
            _resourcePickerPreviousButton = null;
            _resourcePickerSortButton = null;
            _resourcePickerNextButton = null;
            _resourcePickerRows.Clear();
            _resourcePickerEntries.Clear();
            _resourcePickerPage = 1;
            _resourcePickerPageSize =
                StoryGraphResourcePickerLogic.DefaultPageSize;
        }

        private bool InspectorResourcePickerOpen
        {
            get { return _resourcePickerRoot != null; }
        }

        private void ClearResourcePickerReferences()
        {
            _resourcePickerBlocker = null;
            _resourcePickerRoot = null;
            _resourcePickerSearchInput = null;
            _resourcePickerPageText = null;
            _resourcePickerPreviousButton = null;
            _resourcePickerSortButton = null;
            _resourcePickerNextButton = null;
            _inspectorBackgroundLookupButton = null;
            _inspectorAudioLookupButton = null;
            _inspectorCgLookupButton = null;
            _inspectorMiniGameLookupButton = null;
            _inspectorEventLookupButton = null;
            _resourcePickerRows.Clear();
            _resourcePickerEntries.Clear();
            _resourcePickerPage = 1;
            _resourcePickerDescending = false;
            _resourcePickerPageSize =
                StoryGraphResourcePickerLogic.DefaultPageSize;
        }
    }
}
