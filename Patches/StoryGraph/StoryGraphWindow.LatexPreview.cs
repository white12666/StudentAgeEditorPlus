using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Text = TMPro.TextMeshProUGUI;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图基础页的可选 LaTeX 实时预览。EditorPlus 只负责 UI 与防抖；
    /// 具体转译、数学字体和 $$ 精灵由 StudentAgeLatex 经探针提供。
    /// </summary>
    internal sealed partial class StoryGraphWindow
    {
        // 一行普通正文 + 最高 4.5em 的 $$ 精灵 + 内边距；低于此值时 TMP 的
        // Truncate 会在排版前整颗丢弃高根式，而不是只裁掉超出的几个像素。
        private const float LatexPreviewHeight = 156f;
        private const float LatexPreviewDebounceSeconds = 0.25f;

        [NonSerialized] private Text _inspectorLatexPreviewLabel;
        [NonSerialized] private GameObject _inspectorLatexPreviewPanel;
        [NonSerialized] private TextMeshProUGUI _inspectorLatexPreview;
        [NonSerialized] private Text _inspectorLatexPreviewStatus;
        private bool _inspectorLatexPreviewVisible;
        private string _pendingLatexPreviewSource;
        private int _pendingLatexPreviewNodeId;
        private bool _pendingLatexPreviewIsOption;
        private string _renderedLatexPreviewSource;
        private int _renderedLatexPreviewNodeId;
        private bool _renderedLatexPreviewIsOption;
        private float _latexPreviewDueAt;

        private void BuildLatexPreview(GameObject page)
        {
            _inspectorLatexPreviewLabel = CreateInspectorLabel(
                page, "LaTeX 预览（实时）", -4f);

            _inspectorLatexPreviewPanel = CreateUIObject(
                "LatexLivePreview", page.transform);
            Place(
                (RectTransform)_inspectorLatexPreviewPanel.transform,
                0f, 1f, 0f, 1f,
                8f, -4f, InspectorWidth - 42f, LatexPreviewHeight);
            Image background = _inspectorLatexPreviewPanel.AddComponent<Image>();
            background.color = HexColor("F7F1E3");
            background.raycastTarget = false;
            ApplySprite(background, _inputSprite);
            _inspectorLatexPreviewPanel.AddComponent<RectMask2D>();

            // 一条金色校样线标识“这是结果区”，颜色与剧情图按钮/选中态同源。
            GameObject accent = CreateUIObject(
                "LiveAccent", _inspectorLatexPreviewPanel.transform);
            Image accentImage = accent.AddComponent<Image>();
            accentImage.color = AccentOrange;
            accentImage.raycastTarget = false;
            RectTransform accentRect = (RectTransform)accent.transform;
            accentRect.anchorMin = new Vector2(0f, 0f);
            accentRect.anchorMax = new Vector2(0f, 1f);
            accentRect.pivot = new Vector2(0f, 0.5f);
            accentRect.anchoredPosition = Vector2.zero;
            accentRect.sizeDelta = new Vector2(3f, 0f);

            TMP_FontAsset font = ResolveLatexPreviewFont();
            if (font != null)
            {
                GameObject textObject = CreateUIObject(
                    "PreviewText", _inspectorLatexPreviewPanel.transform);
                _inspectorLatexPreview = textObject.AddComponent<TextMeshProUGUI>();
                _inspectorLatexPreview.font = font;
                _inspectorLatexPreview.fontSize = 18f;
                _inspectorLatexPreview.color = BodyTextColor;
                _inspectorLatexPreview.richText = true;
                _inspectorLatexPreview.raycastTarget = false;
                _inspectorLatexPreview.alignment = TextAlignmentOptions.TopLeft;
                _inspectorLatexPreview.enableWordWrapping = true;
                _inspectorLatexPreview.overflowMode = TextOverflowModes.Truncate;
                Stretch(_inspectorLatexPreview.rectTransform, 12f, 8f, 10f, 8f);
            }

            _inspectorLatexPreviewStatus = CreateInspectorNote(
                page, string.Empty, -4f, 24f, AccentOrange);
            _inspectorLatexPreviewStatus.fontSize = 12;
            SetLatexPreviewVisible(false, null, false);
        }

        private TMP_FontAsset ResolveLatexPreviewFont()
        {
            try
            {
                if (_view != null && _view.txtex_talk != null
                    && _view.txtex_talk.font != null)
                    return _view.txtex_talk.font;
                if (_view != null && _view.txtex_talk_cg != null
                    && _view.txtex_talk_cg.font != null)
                    return _view.txtex_talk_cg.font;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.LatexPreview] 读取对话字体失败：" + e.Message);
            }
            try { return TMP_Settings.defaultFontAsset; }
            catch { return null; }
        }

        private void LayoutLatexPreview(ref float cursor)
        {
            if (!_inspectorLatexPreviewVisible
                || _inspectorLatexPreviewPanel == null) return;
            SetInspectorRect(
                _inspectorLatexPreviewLabel?.rectTransform,
                TakeInspectorSlot(ref cursor, 22f, 2f), 22f);
            SetInspectorRect(
                (RectTransform)_inspectorLatexPreviewPanel.transform,
                TakeInspectorSlot(ref cursor, LatexPreviewHeight, 4f),
                LatexPreviewHeight);
            if (_inspectorLatexPreviewStatus == null
                || !_inspectorLatexPreviewStatus.gameObject.activeSelf) return;
            float statusHeight = PreferredInspectorTextHeight(
                _inspectorLatexPreviewStatus, 24f, 88f);
            SetInspectorRect(
                _inspectorLatexPreviewStatus.rectTransform,
                TakeInspectorSlot(ref cursor, statusHeight, 6f), statusHeight);
        }

        private bool TryGetSelectedLatexPreviewTarget(
            out string source, out int nodeId, out bool isOption)
        {
            source = null;
            nodeId = 0;
            isOption = false;
            if (!_editMode || _editSession == null
                || !string.IsNullOrEmpty(_selectedGroupId)
                || _selectedNode == null || _selectedNodes.Count > 1
                || _selectedNode.SourceNode == null)
                return false;
            if (_selectedNode.SourceNode.Talk != null)
            {
                nodeId = _selectedNode.SourceNode.Talk.id;
                source = _selectedNode.SourceNode.Talk.content ?? string.Empty;
                return nodeId > 0;
            }
            if (_selectedNode.SourceNode.Option == null) return false;
            isOption = true;
            nodeId = _editSession.FindOptionKey(
                _selectedNode.SourceNode.Option);
            source = _selectedNode.SourceNode.Option.content ?? string.Empty;
            return nodeId > 0;
        }

        private void RefreshLatexPreviewFromSelection(bool relayout)
        {
            string source;
            int nodeId;
            bool isOption;
            if (TryGetSelectedLatexPreviewTarget(
                    out source, out nodeId, out isOption))
                RequestLatexPreview(
                    source, nodeId, isOption, true, relayout);
            else
                RequestLatexPreview(null, 0, false, true, relayout);
        }

        private void ScheduleLatexPreview(string source)
        {
            string ignored;
            int nodeId;
            bool isOption;
            if (TryGetSelectedLatexPreviewTarget(
                    out ignored, out nodeId, out isOption))
                RequestLatexPreview(
                    source ?? string.Empty, nodeId, isOption, false, true);
            else
                RequestLatexPreview(null, 0, false, false, true);
        }

        private void RequestLatexPreview(
            string source, int nodeId, bool isOption,
            bool immediate, bool relayout)
        {
            bool contextual = StoryGraphLatexProbe.RenderPreviewForSession != null
                              && _editSession != null && nodeId > 0;
            if (_inspectorLatexPreview == null
                || (StoryGraphLatexProbe.RenderPreview == null && !contextual)
                || source == null || nodeId <= 0
                || (source.IndexOf('$') < 0 && !contextual))
            {
                _pendingLatexPreviewSource = null;
                _pendingLatexPreviewNodeId = 0;
                _pendingLatexPreviewIsOption = false;
                _renderedLatexPreviewSource = null;
                _renderedLatexPreviewNodeId = 0;
                _renderedLatexPreviewIsOption = false;
                if (SetLatexPreviewVisible(false, null, false) && relayout)
                    RelayoutBasicInspector();
                return;
            }

            _pendingLatexPreviewSource = source;
            _pendingLatexPreviewNodeId = nodeId;
            _pendingLatexPreviewIsOption = isOption;
            if (source.IndexOf('$') < 0
                && SetLatexPreviewVisible(false, null, false) && relayout)
                RelayoutBasicInspector();
            if (immediate)
            {
                RenderPendingLatexPreview(relayout);
                return;
            }
            _latexPreviewDueAt = Time.unscaledTime + LatexPreviewDebounceSeconds;
        }

        private void UpdateLatexPreview()
        {
            if (_pendingLatexPreviewSource == null
                || Time.unscaledTime < _latexPreviewDueAt) return;
            RenderPendingLatexPreview(true);
        }

        private void RenderPendingLatexPreview(bool relayout)
        {
            string source = _pendingLatexPreviewSource;
            int nodeId = _pendingLatexPreviewNodeId;
            bool isOption = _pendingLatexPreviewIsOption;
            _pendingLatexPreviewSource = null;
            _pendingLatexPreviewNodeId = 0;
            _pendingLatexPreviewIsOption = false;
            _latexPreviewDueAt = 0f;
            bool contextual = StoryGraphLatexProbe.RenderPreviewForSession != null
                              && _editSession != null && nodeId > 0;
            if (source == null
                || (!contextual && StoryGraphLatexProbe.RenderPreview == null)) return;
            if (_inspectorLatexPreviewVisible
                && string.Equals(
                    source, _renderedLatexPreviewSource,
                    StringComparison.Ordinal)
                && nodeId == _renderedLatexPreviewNodeId
                && isOption == _renderedLatexPreviewIsOption) return;

            string rendered;
            string warning;
            bool show;
            try
            {
                show = contextual
                    ? StoryGraphLatexProbe.RenderPreviewForSession(
                        _editSession, nodeId, isOption, source,
                        out rendered, out warning)
                    : StoryGraphLatexProbe.RenderPreview(
                        source, out rendered, out warning);
            }
            catch (Exception e)
            {
                show = true;
                rendered = null;
                warning = "实时预览失败：" + e.Message;
                Plugin.Log?.LogWarning(
                    "[StoryGraph.LatexPreview] " + e.Message);
            }

            _renderedLatexPreviewSource = show ? source : null;
            _renderedLatexPreviewNodeId = show ? nodeId : 0;
            _renderedLatexPreviewIsOption = show && isOption;
            if (_inspectorLatexPreview != null)
            {
                _inspectorLatexPreview.text = !string.IsNullOrEmpty(rendered)
                    ? rendered
                    : show ? "等待公式闭合…" : string.Empty;
            }
            bool changed = SetLatexPreviewVisible(
                show, warning, !string.IsNullOrEmpty(warning));
            if (changed && relayout) RelayoutBasicInspector();
            if (show && _inspectorLatexPreview != null)
            {
                try { _inspectorLatexPreview.ForceMeshUpdate(true, true); }
                catch { }
            }
        }

        private bool SetLatexPreviewVisible(
            bool visible, string warning, bool showWarning)
        {
            bool warningWasVisible = _inspectorLatexPreviewStatus != null
                                     && _inspectorLatexPreviewStatus.gameObject.activeSelf;
            string previousWarning = _inspectorLatexPreviewStatus != null
                ? _inspectorLatexPreviewStatus.text : string.Empty;
            bool changed = _inspectorLatexPreviewVisible != visible
                           || warningWasVisible != (visible && showWarning)
                           || !string.Equals(
                               previousWarning, warning ?? string.Empty,
                               StringComparison.Ordinal);
            _inspectorLatexPreviewVisible = visible;
            if (_inspectorLatexPreviewLabel != null)
                _inspectorLatexPreviewLabel.gameObject.SetActive(visible);
            if (_inspectorLatexPreviewPanel != null)
                _inspectorLatexPreviewPanel.SetActive(visible);
            if (_inspectorLatexPreviewStatus != null)
            {
                _inspectorLatexPreviewStatus.text = warning ?? string.Empty;
                _inspectorLatexPreviewStatus.gameObject.SetActive(
                    visible && showWarning);
            }
            return changed;
        }

        /// <summary>
        /// 保存与“预览本句”都必须越过 250ms 防抖：作者刚输入完便触发动作时，
        /// 最后一版成功预览也应成为本次会话候选；无公式正文同样登记清除旧快照。
        /// </summary>
        private void FlushPendingLatexPreview()
        {
            if (_pendingLatexPreviewSource != null)
                RenderPendingLatexPreview(false);
        }

        private void ClearLatexPreviewReferences()
        {
            _inspectorLatexPreviewLabel = null;
            _inspectorLatexPreviewPanel = null;
            _inspectorLatexPreview = null;
            _inspectorLatexPreviewStatus = null;
            _inspectorLatexPreviewVisible = false;
            _pendingLatexPreviewSource = null;
            _pendingLatexPreviewNodeId = 0;
            _pendingLatexPreviewIsOption = false;
            _renderedLatexPreviewSource = null;
            _renderedLatexPreviewNodeId = 0;
            _renderedLatexPreviewIsOption = false;
            _latexPreviewDueAt = 0f;
        }
    }
}
