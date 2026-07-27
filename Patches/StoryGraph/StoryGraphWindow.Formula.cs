using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Config;
using Newtonsoft.Json;
using StudentAgeTypeset.Latex;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图属性栏的 LaTeX 分区与保存编排（设计 §5 / §6.1 / §7）。
    ///
    /// 分工硬约束：StoryGraphEditSession 只依赖纯转译器 LatexInlineTranspiler，
    /// 渲染（FormulaRenderer）与物化（FormulaAssetService）全部留在窗口层——
    /// 三者现居 StudentAgeTypeset 类库（Typeset\Latex\），约束不变：
    /// 会话测试工程不加载 SkiaSharp/CSharpMath，任何反向依赖都会让回归失效。
    ///
    /// 编辑态数据挂在会话生命周期内的 _latexStore 内存态上，Unity 对象一律
    /// 实例字段 + [NonSerialized]，随 Close/OnDestroy 释放（禁止静态字段持有）。
    /// </summary>
    internal sealed partial class StoryGraphWindow
    {
        // Basic 页是固定坐标布局：新增控件必须参与 RelayoutBasicInspector 的
        // 游标推进，否则后续控件会被压盖、页面高度也会算短。
        private const float InlineFormulaPreviewHeight = 46f;
        private const float FormulaInputHeight = 92f;
        private const float FormulaPreviewHeight = 150f;
        private const float FormulaPreviewWidth = InspectorWidth - 42f;
        /// <summary>块级公式重渲染防抖（设计 §6.1：400ms，照 confirm-until 模式在 Update 里到点触发）。</summary>
        private const float FormulaPreviewDebounceSeconds = 0.4f;
        /// <summary>属性栏预览字号；成品卡片按安全区另行自适应，与此无关。</summary>
        private const float FormulaPreviewFontSize = 44f;

        [NonSerialized] private Text _inspectorInlinePreviewLabel;
        [NonSerialized] private TextMeshProUGUI _inspectorInlinePreview;
        [NonSerialized] private Text _inspectorInlineWarning;
        [NonSerialized] private Text _inspectorFormulaLabel;
        [NonSerialized] private InputField _inspectorFormulaInput;
        [NonSerialized] private GameObject _inspectorFormulaPreviewSlot;
        [NonSerialized] private RawImage _inspectorFormulaPreview;
        [NonSerialized] private Text _inspectorFormulaStatus;
        [NonSerialized] private Button _inspectorFormulaAutoCloseButton;
        [NonSerialized] private Text _inspectorFormulaAutoCloseLabel;
        [NonSerialized] private Button _inspectorFormulaRemoveButton;
        [NonSerialized] private Button _inspectorFormulaModeButton;
        [NonSerialized] private Text _inspectorFormulaModeLabel;
        // 预览纹理由 LoadImage 逐次新建，照 _roundedTex 模式在 Close/OnDestroy 释放。
        [NonSerialized] private Texture2D _inspectorFormulaTexture;

        private bool _formulaSectionVisible;
        private bool _inlineFormulaPreviewVisible;
        // 上一次真正写进标签的文本：两个提示标签都是 Truncate 且高度只在
        // RelayoutBasicInspector 里按当时文本算一次，文本变长必须重排，
        // 否则多出来的行（尤其缺字形告警）会被静默裁掉（D5-2）。
        private string _inlineFormulaWarningText = string.Empty;
        private string _formulaStatusTextShown = string.Empty;
        private bool _formulaAutoCloseDraft = true;
        /// <summary>排版模式草稿：0=纯公式，1=图文混排（见 FormulaCardMode）。</summary>
        private FormulaCardMode _formulaModeDraft = FormulaCardMode.Math;
        private float _formulaPreviewDueAt;
        private string _formulaPreviewLatex;
        private string _formulaPreviewError;

        // ==================== 构建 ====================

        /// <summary>由 BuildBasicInspectorPage 末尾调用：行内预览 + 块级公式区。</summary>
        private void BuildFormulaInspectorSection(GameObject page)
        {
            _inspectorInlinePreviewLabel = CreateInspectorLabel(
                page, "行内公式预览（玩家所见）", -4f);
            _inspectorInlinePreview = CreateInlineFormulaPreviewLabel(page);
            _inspectorInlineWarning = CreateInspectorNote(
                page, string.Empty, -4f, 22f, AccentOrange);
            _inspectorInlineWarning.fontSize = 12;

            _inspectorFormulaLabel = CreateInspectorLabel(
                page, "块级公式 LaTeX（整句 MiniCG）", -4f);
            _inspectorFormulaInput = CreateInspectorInput(
                page, "FormulaLatex", -4f, FormulaInputHeight, true);
            SetInspectorPlaceholder(_inspectorFormulaInput,
                "例：\\frac{a}{b} = c^2；留空表示这句没有块级公式");
            _inspectorFormulaInput.onValueChanged.AddListener(
                OnFormulaInputValueChanged);
            _inspectorFormulaInput.onEndEdit.AddListener(OnFormulaInputEndEdit);

            _inspectorFormulaPreviewSlot = CreateUIObject(
                "FormulaPreview", page.transform);
            Place((RectTransform)_inspectorFormulaPreviewSlot.transform,
                0f, 1f, 0f, 1f, 8f, -4f, FormulaPreviewWidth, FormulaPreviewHeight);
            GameObject image = CreateUIObject(
                "Image", _inspectorFormulaPreviewSlot.transform);
            var imageRect = (RectTransform)image.transform;
            imageRect.anchorMin = new Vector2(0.5f, 0.5f);
            imageRect.anchorMax = new Vector2(0.5f, 0.5f);
            imageRect.pivot = new Vector2(0.5f, 0.5f);
            imageRect.anchoredPosition = Vector2.zero;
            imageRect.sizeDelta = new Vector2(FormulaPreviewWidth, FormulaPreviewHeight);
            _inspectorFormulaPreview = image.AddComponent<RawImage>();
            _inspectorFormulaPreview.raycastTarget = false;
            _inspectorFormulaPreview.enabled = false;

            _inspectorFormulaStatus = CreateInspectorNote(
                page, string.Empty, -4f, 40f, SubtitleColor);
            _inspectorFormulaStatus.fontSize = 12;

            _inspectorFormulaAutoCloseButton = CreatePageButton(
                page, "下一句自动关闭：开", 8f, -4f, 182f, ToggleFormulaAutoClose);
            _inspectorFormulaAutoCloseLabel =
                _inspectorFormulaAutoCloseButton.GetComponentInChildren<Text>();
            _inspectorFormulaRemoveButton = CreatePageButton(
                page, "移除块级公式", 198f, -4f, 182f, RemoveFormulaBlock);
            _inspectorFormulaModeButton = CreatePageButton(
                page, "排版：纯公式", 8f, -4f, 182f, ToggleFormulaMode);
            _inspectorFormulaModeLabel =
                _inspectorFormulaModeButton.GetComponentInChildren<Text>();

            SetFormulaSectionActive(false);
            SetInlineFormulaPreviewActive(false, false);
        }

        /// <summary>
        /// 行内预览必须是 TMP：窗口自建的 legacy Text 是 supportRichText=false，
        /// 显示不出 sup/sub。字体取 ModEvtEditView.txtex_talk.font，与玩家所见同源；
        /// 取不到字体就整体不建预览（宁可没有，也不给作者一个字形与游戏不同的假预览）。
        /// </summary>
        private TextMeshProUGUI CreateInlineFormulaPreviewLabel(GameObject page)
        {
            TMP_FontAsset font = ResolveTalkFontAsset();
            if (font == null)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Latex] 取不到对话 TMP 字体，行内公式预览已禁用。");
                return null;
            }
            GameObject go = CreateUIObject("InlineFormulaPreview", page.transform);
            Place((RectTransform)go.transform, 0f, 1f, 0f, 1f,
                8f, -4f, InspectorWidth - 42f, InlineFormulaPreviewHeight);
            var label = go.AddComponent<TextMeshProUGUI>();
            label.font = font;
            label.fontSize = 18f;
            label.color = BodyTextColor;
            label.richText = true;
            label.raycastTarget = false;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.overflowMode = TextOverflowModes.Truncate;
            label.text = string.Empty;
            return label;
        }

        private TMP_FontAsset ResolveTalkFontAsset()
        {
            try
            {
                if (_view != null && _view.txtex_talk != null
                    && _view.txtex_talk.font != null)
                    return _view.txtex_talk.font;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Latex] 读取对话字体失败：" + e.Message);
            }
            try { return TMP_Settings.defaultFontAsset; }
            catch { return null; }
        }

        private void ClearFormulaInspectorReferences()
        {
            // 预览纹理不在这里清：它由 DestroyFormulaPreviewTexture 显式销毁，
            // 而调用顺序是 ClearInspectorReferences → DestroyFormulaPreviewTexture。
            _inspectorInlinePreviewLabel = null;
            _inspectorInlinePreview = null;
            _inspectorInlineWarning = null;
            _inspectorFormulaLabel = null;
            _inspectorFormulaInput = null;
            _inspectorFormulaPreviewSlot = null;
            _inspectorFormulaPreview = null;
            _inspectorFormulaStatus = null;
            _inspectorFormulaAutoCloseButton = null;
            _inspectorFormulaAutoCloseLabel = null;
            _inspectorFormulaRemoveButton = null;
            _inspectorFormulaModeButton = null;
            _inspectorFormulaModeLabel = null;
            _formulaSectionVisible = false;
            _inlineFormulaPreviewVisible = false;
            _inlineFormulaWarningText = string.Empty;
            _formulaStatusTextShown = string.Empty;
        }

        // ==================== 布局 ====================

        private void LayoutInlineFormulaPreview(ref float cursor)
        {
            if (!_inlineFormulaPreviewVisible || _inspectorInlinePreview == null) return;
            SetInspectorRect(
                _inspectorInlinePreviewLabel?.rectTransform,
                TakeInspectorSlot(ref cursor, 22f, 2f), 22f);
            SetInspectorRect(
                _inspectorInlinePreview.rectTransform,
                TakeInspectorSlot(ref cursor, InlineFormulaPreviewHeight, 4f),
                InlineFormulaPreviewHeight);
            if (_inspectorInlineWarning == null
                || !_inspectorInlineWarning.gameObject.activeSelf) return;
            float warningHeight = PreferredInspectorTextHeight(
                _inspectorInlineWarning, 22f, 96f);
            SetInspectorRect(
                _inspectorInlineWarning.rectTransform,
                TakeInspectorSlot(ref cursor, warningHeight, 6f), warningHeight);
        }

        private void LayoutFormulaInspectorSection(ref float cursor)
        {
            if (!_formulaSectionVisible || _inspectorFormulaInput == null) return;
            SetInspectorRect(
                _inspectorFormulaLabel?.rectTransform,
                TakeInspectorSlot(ref cursor, 22f, 4f), 22f);
            SetInspectorRect(
                (RectTransform)_inspectorFormulaInput.transform,
                TakeInspectorSlot(ref cursor, FormulaInputHeight, 8f),
                FormulaInputHeight);
            SetInspectorRect(
                _inspectorFormulaPreviewSlot != null
                    ? (RectTransform)_inspectorFormulaPreviewSlot.transform
                    : null,
                TakeInspectorSlot(ref cursor, FormulaPreviewHeight, 8f),
                FormulaPreviewHeight);
            float statusHeight = PreferredInspectorTextHeight(
                _inspectorFormulaStatus, 40f, 160f);
            SetInspectorRect(
                _inspectorFormulaStatus?.rectTransform,
                TakeInspectorSlot(ref cursor, statusHeight, 8f), statusHeight);
            float buttonsY = TakeInspectorSlot(ref cursor, 40f, 12f);
            SetInspectorRect(
                _inspectorFormulaAutoCloseButton != null
                    ? (RectTransform)_inspectorFormulaAutoCloseButton.transform
                    : null,
                buttonsY, 40f);
            SetInspectorRect(
                _inspectorFormulaRemoveButton != null
                    ? (RectTransform)_inspectorFormulaRemoveButton.transform
                    : null,
                buttonsY, 40f);
            SetInspectorRect(
                _inspectorFormulaModeButton != null
                    ? (RectTransform)_inspectorFormulaModeButton.transform
                    : null,
                TakeInspectorSlot(ref cursor, 40f, 8f), 40f);
        }

        private void SetFormulaSectionActive(bool active)
        {
            _formulaSectionVisible = active;
            SetComponentActive(_inspectorFormulaLabel, active);
            SetComponentActive(_inspectorFormulaInput, active);
            if (_inspectorFormulaPreviewSlot != null)
                _inspectorFormulaPreviewSlot.SetActive(active);
            SetComponentActive(_inspectorFormulaStatus, active);
            SetComponentActive(_inspectorFormulaAutoCloseButton, active);
            SetComponentActive(_inspectorFormulaRemoveButton, active);
            SetComponentActive(_inspectorFormulaModeButton, active);
        }

        private void SetInlineFormulaPreviewActive(bool active, bool warning)
        {
            _inlineFormulaPreviewVisible = active && _inspectorInlinePreview != null;
            SetComponentActive(_inspectorInlinePreviewLabel, _inlineFormulaPreviewVisible);
            SetComponentActive(_inspectorInlinePreview, _inlineFormulaPreviewVisible);
            SetComponentActive(_inspectorInlineWarning,
                _inlineFormulaPreviewVisible && warning);
        }

        private static void SetComponentActive(Component component, bool active)
        {
            if (component != null) component.gameObject.SetActive(active);
        }

        // ==================== 选择联动 ====================

        /// <summary>块级公式是「每句一个画面槽位」的附件，只有单选真实对话时才可编辑。</summary>
        private TalkCfg FormulaTargetTalk()
        {
            if (!_editMode || _editSession == null) return null;
            if (!string.IsNullOrEmpty(_selectedGroupId)) return null;
            if (_selectedNode == null || _selectedNodes.Count > 1
                || _selectedNode.SourceNode == null) return null;
            return _selectedNode.SourceNode.Talk;
        }

        /// <summary>
        /// 当前选择对应的正文原文。行内预览必须用它，不能读 _inspectorContentInput：
        /// RefreshInspector 里公式区刷新排在正文回填之前，而回填走
        /// SetTextWithoutNotify（不触发逐键回调），读输入框只会拿到上一个被选中
        /// 节点的遗留文本（D5-1）。
        /// </summary>
        private string CurrentInspectorContentSource()
        {
            if (!_editMode || _editSession == null) return null;
            if (!string.IsNullOrEmpty(_selectedGroupId)) return null;
            if (_selectedNode == null || _selectedNodes.Count > 1
                || _selectedNode.SourceNode == null) return null;
            return InlineFormulaSourceOf(
                _selectedNode.SourceNode.Talk, _selectedNode.SourceNode.Option);
        }

        /// <summary>行内预览的取值口径（对话优先，其次选项；都没有则无预览目标）。</summary>
        private static string InlineFormulaSourceOf(TalkCfg talk, OptionCfg option)
        {
            if (talk != null) return talk.content;
            return option != null ? option.content : null;
        }

        /// <summary>RefreshInspector 单点调用：按当前选择重建 LaTeX 两个分区的显示。</summary>
        private void RefreshFormulaInspectorSection()
        {
            if (_inspectorFormulaInput == null) return;
            RefreshInlineFormulaPreview(CurrentInspectorContentSource(), false);

            TalkCfg talk = FormulaTargetTalk();
            if (talk == null)
            {
                SetFormulaSectionActive(false);
                CancelFormulaPreview();
                SetInspectorText(_inspectorFormulaInput, string.Empty);
                return;
            }

            SetFormulaSectionActive(true);
            LatexBlockData block = _latexStore != null
                ? _latexStore.GetBlock(_editSession.EventId, talk.id)
                : null;
            SetInspectorText(_inspectorFormulaInput,
                block != null ? block.Latex : string.Empty);
            _formulaAutoCloseDraft = block == null || block.AutoClose;
            _formulaModeDraft = block != null && block.Mode == (int)FormulaCardMode.Text
                ? FormulaCardMode.Text
                : FormulaCardMode.Math;
            UpdateFormulaAutoCloseButton();
            // 状态事件（EvtCfg.type==60 / displayType==1）走 StateEvtView，它整个
            // 不解释 screenEffect（StateEvtView.cs:132 只认 4006），块级公式挂上去
            // 是死数据。写之前就禁掉，别让作者白写（LATEX-D2-3）。
            bool editable = _latexStore != null && FormulaRenderer.IsAvailable
                            && !_editSession.IsStateEventView;
            _inspectorFormulaInput.interactable = editable;
            if (_inspectorFormulaRemoveButton != null)
                _inspectorFormulaRemoveButton.interactable =
                    _latexStore != null && block != null;
            if (_inspectorFormulaAutoCloseButton != null)
                _inspectorFormulaAutoCloseButton.interactable = _latexStore != null;
            _formulaPreviewError = null;
            // 切节点立即重渲（不防抖）：防抖是给逐键输入用的。
            RequestFormulaPreview(block != null ? block.Latex : null, true);
            UpdateFormulaStatusText();
        }

        // ==================== 行内实时预览（设计 §6.1） ====================

        /// <summary>
        /// 逐键路径的入口：正文以输入框的实时文本为准（草稿已热应用，但输入框才是
        /// 本帧最新的一份）。
        /// </summary>
        private void RefreshInlineFormulaPreview(bool relayoutOnChange = true)
        {
            RefreshInlineFormulaPreview(
                _inspectorContentInput != null ? _inspectorContentInput.text : null,
                relayoutOnChange);
        }

        /// <summary>
        /// 纯字符串变换 + TMP 赋值，不防抖。source 由调用方显式给出，避免读到
        /// 上一个选中节点遗留在输入框里的文本（D5-1）。显隐或告警文本变化时才重排
        /// 属性栏——告警从 1 条变 3 条同样要重排，否则超出旧高度的行会被
        /// Truncate 静默裁掉（D5-2）。
        /// </summary>
        private void RefreshInlineFormulaPreview(string source, bool relayoutOnChange)
        {
            if (_inspectorInlinePreview == null) return;
            bool hasTarget = _editMode && _editSession != null
                             && string.IsNullOrEmpty(_selectedGroupId)
                             && _selectedNode != null && _selectedNodes.Count <= 1
                             && _selectedNode.SourceNode != null
                             && (_selectedNode.SourceNode.Talk != null
                                 || _selectedNode.SourceNode.Option != null);
            bool show = hasTarget && LatexInlineTranspiler.ContainsLatex(source);
            string warning = null;
            if (show)
            {
                LatexBakeResult result = LatexInlineTranspiler.Bake(source);
                _inspectorInlinePreview.text = result.Baked ?? string.Empty;
                warning = BuildInlineFormulaWarning(result);
                if (_inspectorInlineWarning != null)
                    _inspectorInlineWarning.text = warning ?? string.Empty;
            }
            string warningText = show && warning != null ? warning : string.Empty;
            bool changed = InlineFormulaLayoutChanged(
                _inlineFormulaPreviewVisible, show,
                _inlineFormulaWarningText, warningText);
            _inlineFormulaWarningText = warningText;
            SetInlineFormulaPreviewActive(show, warning != null);
            if (changed && relayoutOnChange) RelayoutBasicInspector();
        }

        /// <summary>
        /// 行内预览是否需要重排：显隐变化，或告警文本内容变化（行数可能变）。
        /// 只比显隐会让「1 条告警 → 3 条告警」保持 22f 单行高度，缺字形那段被裁掉。
        /// </summary>
        private static bool InlineFormulaLayoutChanged(
            bool wasVisible, bool nowVisible,
            string previousWarning, string nextWarning)
        {
            if (wasVisible != nowVisible) return true;
            return !string.Equals(
                previousWarning ?? string.Empty, nextWarning ?? string.Empty,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// 转译警告 + 缺字形探测。对话字体是动态 atlas（运行时按需入册），因此用
        /// tryAddCharacter 真探一次源字体，比只查已入册表准确。
        /// </summary>
        private string BuildInlineFormulaWarning(LatexBakeResult result)
        {
            var parts = new List<string>();
            if (result != null && result.Warnings != null && result.Warnings.Count > 0)
                parts.AddRange(result.Warnings.Take(2));
            string missing = CollectMissingGlyphs(
                result != null ? result.Baked : null);
            if (!string.IsNullOrEmpty(missing))
                parts.Add("对话字体缺少字形：" + missing
                          + "（玩家侧会显示成方块，建议改用块级公式）");
            if (parts.Count == 0) return null;
            return string.Join("；", parts.ToArray());
        }

        private string CollectMissingGlyphs(string baked)
        {
            TMP_FontAsset font = _inspectorInlinePreview != null
                ? _inspectorInlinePreview.font
                : null;
            if (font == null || string.IsNullOrEmpty(baked)) return null;
            var missing = new StringBuilder();
            try
            {
                foreach (char c in LatexInlineTranspiler.CollectSpecialChars(baked))
                {
                    if (char.IsSurrogate(c)) continue;
                    if (font.HasCharacter(c, true, true)) continue;
                    if (missing.Length > 0) missing.Append(' ');
                    missing.Append(c);
                    if (missing.Length >= 24) break;
                }
            }
            catch (Exception e)
            {
                // 字形探测失败不影响烘焙，也不该打断编辑。
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Latex] 字形探测失败：" + e.Message);
                return null;
            }
            return missing.Length > 0 ? missing.ToString() : null;
        }

        // ==================== 块级公式编辑 ====================

        private void OnFormulaInputValueChanged(string unused)
        {
            if (_settingInspectorText || !_editMode || _editSession == null) return;
            TalkCfg talk = FormulaTargetTalk();
            if (talk == null || _latexStore == null
                || _inspectorFormulaInput == null) return;

            int eventId = _editSession.EventId;
            string latex = _inspectorFormulaInput.text;
            LatexBlockData block = _latexStore.GetBlock(eventId, talk.id);
            if (string.IsNullOrWhiteSpace(latex))
            {
                if (block != null)
                {
                    RememberRemovedFormulaBlock(talk.id, block);
                    _latexStore.RemoveBlock(eventId, talk.id);
                }
            }
            else
            {
                if (block == null)
                {
                    block = new LatexBlockData { AutoClose = _formulaAutoCloseDraft, Mode = (int)_formulaModeDraft };
                    // 清空后又改回来：沿用墓碑里的旧登记，避免重复分配 cgId、
                    // 也让上次自动补写的 [4017] 继续被认领而不是被当成作者手写。
                    LatexBlockData tombstone;
                    if (_formulaRemovedBlocks.TryGetValue(talk.id, out tombstone)
                        && tombstone != null)
                    {
                        block.CgId = tombstone.CgId;
                        block.AutoCloseTalkIds = tombstone.AutoCloseTalkIds != null
                            ? new List<int>(tombstone.AutoCloseTalkIds)
                            : new List<int>();
                    }
                }
                _formulaRemovedBlocks.Remove(talk.id);
                block.Latex = latex;
                block.AutoClose = _formulaAutoCloseDraft;
                block.Mode = (int)_formulaModeDraft;
                _latexStore.SetBlock(eventId, talk.id, block);
            }
            _formulaEditedSinceEnter = true;
            _formulaPreviewError = null;
            RequestFormulaPreview(latex, false);
            if (_inspectorFormulaRemoveButton != null)
                _inspectorFormulaRemoveButton.interactable =
                    !string.IsNullOrWhiteSpace(latex);
            UpdateFormulaStatusText();
            UpdateEditControls();
        }

        private void OnFormulaInputEndEdit(string unused)
        {
            if (_settingInspectorText || !_editMode) return;
            _editStatus = "块级公式源码保存在编辑器私有边车里；"
                          + "点“保存”才会渲染 PNG 并挂上画面指令。";
            UpdateStatusBar();
        }

        private void ToggleFormulaAutoClose()
        {
            if (!_editMode || _editSession == null || _latexStore == null) return;
            _formulaAutoCloseDraft = !_formulaAutoCloseDraft;
            TalkCfg talk = FormulaTargetTalk();
            if (talk != null)
            {
                LatexBlockData block = _latexStore.GetBlock(_editSession.EventId, talk.id);
                if (block != null)
                {
                    block.AutoClose = _formulaAutoCloseDraft;
                    _latexStore.SetBlock(_editSession.EventId, talk.id, block);
                    _formulaEditedSinceEnter = true;
                }
            }
            UpdateFormulaAutoCloseButton();
            _editStatus = _formulaAutoCloseDraft
                ? "已开启“下一句自动关闭”：保存时会给每个下一句补写“关闭 CG（4017）”。"
                : "已关闭“下一句自动关闭”：公式图会一直显示到作者自己关闭或事件结束。";
            UpdateStatusBar();
            UpdateEditControls();
        }

        /// <summary>
        /// 切换排版模式。图文模式（TextPainter）让整段话连同其中的 $…$ 一起由
        /// LaTeX 引擎排版——这是唯一能拿到"正宗 LaTeX 字形"的通道：行内烘焙受限于
        /// 游戏字体，只能用 &lt;i&gt; 合成斜体，出不来数学斜体的味道。
        /// 模式参与 PNG 内容寻址，切换后下次保存会重新出图。
        /// </summary>
        private void ToggleFormulaMode()
        {
            if (!_editMode || _editSession == null || _latexStore == null) return;
            _formulaModeDraft = _formulaModeDraft == FormulaCardMode.Math
                ? FormulaCardMode.Text
                : FormulaCardMode.Math;
            TalkCfg talk = FormulaTargetTalk();
            if (talk != null)
            {
                LatexBlockData block = _latexStore.GetBlock(_editSession.EventId, talk.id);
                if (block != null)
                {
                    block.Mode = (int)_formulaModeDraft;
                    _latexStore.SetBlock(_editSession.EventId, talk.id, block);
                    _formulaEditedSinceEnter = true;
                }
            }
            UpdateFormulaModeButton();
            // 模式变了，预览必须按新模式重渲。
            _formulaPreviewError = null;
            RequestFormulaPreview(
                _inspectorFormulaInput != null ? _inspectorFormulaInput.text : null, true);
            _editStatus = _formulaModeDraft == FormulaCardMode.Text
                ? "已切到“图文混排”：整段话按 LaTeX 排版、$…$ 之间是公式，自动折行。"
                : "已切到“纯公式”：整条式子居中放大撑满画面。";
            UpdateStatusBar();
            UpdateEditControls();
        }

        private void UpdateFormulaModeButton()
        {
            if (_inspectorFormulaModeLabel == null) return;
            _inspectorFormulaModeLabel.text =
                _formulaModeDraft == FormulaCardMode.Text ? "排版：图文混排" : "排版：纯公式";
        }

        private void RemoveFormulaBlock()
        {
            if (!_editMode || _editSession == null || _latexStore == null) return;
            TalkCfg talk = FormulaTargetTalk();
            if (talk == null) return;
            LatexBlockData block = _latexStore.GetBlock(_editSession.EventId, talk.id);
            if (block == null)
            {
                SetEditFeedback("这句没有块级公式。", false);
                return;
            }
            RememberRemovedFormulaBlock(talk.id, block);
            _latexStore.RemoveBlock(_editSession.EventId, talk.id);
            _formulaEditedSinceEnter = true;
            SetInspectorText(_inspectorFormulaInput, string.Empty);
            CancelFormulaPreview();
            if (_inspectorFormulaRemoveButton != null)
                _inspectorFormulaRemoveButton.interactable = false;
            UpdateFormulaStatusText();
            _editStatus = "已移除对话 " + talk.id + " 的块级公式；保存时会清除该句的"
                          + "“显示 MiniCG（4019）”并回收自动补写的“关闭 CG（4017）”。";
            UpdateStatusBar();
            UpdateEditControls();
        }

        /// <summary>
        /// 公式移除墓碑：边车条目已删，保存时仍要按旧登记回收画面指令。只保留
        /// 第一次记下的登记（后续编辑不会改变要回收的那份磁盘状态）。
        /// </summary>
        private void RememberRemovedFormulaBlock(int talkId, LatexBlockData block)
        {
            if (block == null || _formulaRemovedBlocks.ContainsKey(talkId)) return;
            _formulaRemovedBlocks[talkId] = block.Clone();
        }

        private void UpdateFormulaAutoCloseButton()
        {
            if (_inspectorFormulaAutoCloseLabel == null) return;
            _inspectorFormulaAutoCloseLabel.text = _formulaAutoCloseDraft
                ? "下一句自动关闭：开"
                : "下一句自动关闭：关";
        }

        private void UpdateFormulaStatusText()
        {
            if (_inspectorFormulaStatus == null) return;
            string message;
            bool problem = true;
            if (_latexStore == null)
            {
                message = "LaTeX 源码边车不可用（读取失败），本次编辑无法保存块级公式。";
            }
            else if (_editSession != null && _editSession.IsStateEventView)
            {
                message = StateEventFormulaHint;
            }
            else if (!FormulaRenderer.IsAvailable)
            {
                message = "块级公式不可用：" + (FormulaRenderer.UnavailableReason
                                          ?? "渲染库未就绪");
            }
            else if (!string.IsNullOrEmpty(_formulaPreviewError))
            {
                message = _formulaPreviewError;
            }
            else
            {
                problem = false;
                message = "保存时渲染成整句 MiniCG（画面指令 [4019]），"
                          + "该句原有画面指令必须先清空。";
                if (!string.IsNullOrEmpty(FormulaRenderer.CjkWarning))
                    message += " " + FormulaRenderer.CjkWarning;
            }
            _inspectorFormulaStatus.color = problem ? AccentOrange : SubtitleColor;
            if (string.Equals(_formulaStatusTextShown, message, StringComparison.Ordinal))
            {
                _inspectorFormulaStatus.text = message;
                return;
            }
            _inspectorFormulaStatus.text = message;
            _formulaStatusTextShown = message;
            // 状态文案在防抖渲染回调与逐键回调里改写，两条路径都不经过
            // RelayoutBasicInspector：文本变长时不重排就会被 Truncate 裁掉
            // （渲染报错正是最需要看全的那一段）。只在文本真变时重排（D5-2）。
            if (_formulaSectionVisible) RelayoutBasicInspector();
        }

        /// <summary>状态事件不支持画面指令时的统一文案（UI 提示与保存告警同源）。</summary>
        private const string StateEventFormulaHint =
            "该事件按状态事件视图播放（EvtCfg.type=60 / displayType=1），"
            + "游戏不解释画面指令，块级公式不会显示；"
            + "请改用行内 $…$ 公式，或把事件改成普通对话事件。";

        // ==================== 预览渲染（400ms 防抖） ====================

        private void RequestFormulaPreview(string latex, bool immediate)
        {
            _formulaPreviewLatex = latex;
            if (string.IsNullOrWhiteSpace(latex))
            {
                _formulaPreviewDueAt = 0f;
                ShowFormulaPreviewTexture(null);
                return;
            }
            if (!FormulaRenderer.IsAvailable)
            {
                _formulaPreviewDueAt = 0f;
                ShowFormulaPreviewTexture(null);
                return;
            }
            // 0 是“无待办”哨兵（与 confirm-until 同约定）：游戏刚起步那一帧
            // unscaledTime 可能为 0，抬到 Epsilon 以免这次重渲永远不触发。
            float due = Time.unscaledTime
                        + (immediate ? 0f : FormulaPreviewDebounceSeconds);
            _formulaPreviewDueAt = due > 0f ? due : float.Epsilon;
        }

        /// <summary>Update 里到点触发一次重渲（照 confirm-until 模式，不用协程/线程）。</summary>
        private void UpdateFormulaPreviewDebounce()
        {
            if (_formulaPreviewDueAt <= 0f) return;
            if (Time.unscaledTime < _formulaPreviewDueAt) return;
            _formulaPreviewDueAt = 0f;
            RenderFormulaPreviewNow();
        }

        /// <summary>
        /// 退出编辑模式/关闭窗口时清空待渲染与已显示的预览。纹理在这里就释放，
        /// 不必等 Close 的统一销毁——公式区可能在窗口存活期间被反复切换。
        /// </summary>
        private void CancelFormulaPreview()
        {
            _formulaPreviewDueAt = 0f;
            _formulaPreviewLatex = null;
            _formulaPreviewError = null;
            ShowFormulaPreviewTexture(null);
        }

        private void RenderFormulaPreviewNow()
        {
            if (_inspectorFormulaPreview == null) return;
            string latex = _formulaPreviewLatex;
            if (string.IsNullOrWhiteSpace(latex))
            {
                ShowFormulaPreviewTexture(null);
                return;
            }
            byte[] png;
            string error;
            if (!FormulaRenderer.TryRenderPreview(
                    latex, FormulaPreviewFontSize, _formulaModeDraft,
                    out png, out error)
                || png == null || png.Length == 0)
            {
                ShowFormulaPreviewTexture(null);
                _formulaPreviewError = "公式无法渲染：" + (error ?? "未知错误");
                UpdateFormulaStatusText();
                return;
            }

            Texture2D texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(png))
                {
                    UnityEngine.Object.Destroy(texture);
                    ShowFormulaPreviewTexture(null);
                    _formulaPreviewError = "公式预览图解码失败。";
                    UpdateFormulaStatusText();
                    return;
                }
            }
            catch (Exception e)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                ShowFormulaPreviewTexture(null);
                _formulaPreviewError = "公式预览失败：" + e.Message;
                UpdateFormulaStatusText();
                return;
            }
            ShowFormulaPreviewTexture(texture);
            _formulaPreviewError = null;
            UpdateFormulaStatusText();
        }

        /// <summary>接管纹理所有权：旧纹理立即销毁，null 表示只清空显示。</summary>
        private void ShowFormulaPreviewTexture(Texture2D texture)
        {
            if (!ReferenceEquals(texture, _inspectorFormulaTexture))
                DestroyFormulaPreviewTexture();
            _inspectorFormulaTexture = texture;
            if (_inspectorFormulaPreview == null) return;
            _inspectorFormulaPreview.texture = texture;
            _inspectorFormulaPreview.enabled = texture != null;
            if (texture == null) return;
            // 预览槽是固定尺寸；按比例缩放并居中，最大不超过原始像素（放大只会糊）。
            float scale = Mathf.Min(
                FormulaPreviewWidth / Math.Max(1, texture.width),
                FormulaPreviewHeight / Math.Max(1, texture.height));
            if (scale > 1f) scale = 1f;
            _inspectorFormulaPreview.rectTransform.sizeDelta = new Vector2(
                texture.width * scale, texture.height * scale);
        }

        private void DestroyFormulaPreviewTexture()
        {
            if (_inspectorFormulaPreview != null)
            {
                _inspectorFormulaPreview.texture = null;
                _inspectorFormulaPreview.enabled = false;
            }
            if (_inspectorFormulaTexture == null) return;
            UnityEngine.Object.Destroy(_inspectorFormulaTexture);
            _inspectorFormulaTexture = null;
        }

        // ==================== 保存编排（设计 §5 / §7） ====================

        /// <summary>
        /// 保存前的块级公式编排：物化（PNG → CGCfg.json）后经会话 API 把
        /// screenEffect 落进草稿，随剧情 JSON 一起提交。任一步失败返回 false，
        /// 调用方必须中止本次保存——此时尚未写任何剧情 JSON，最坏只留下可 GC 的孤儿。
        /// </summary>
        private bool TryOrchestrateFormulaEffects(
            out List<string> warnings, out string error)
        {
            warnings = new List<string>();
            error = null;
            if (_editSession == null) return true;

            int eventId = _editSession.EventId;
            var plans = new List<StoryGraphFormulaEffectPlan>();
            var mounted = new Dictionary<int, LatexBlockData>();

            // 阶段 1：本轮被移除的公式（含对话已被删除的情形）只回收画面指令。
            foreach (KeyValuePair<int, LatexBlockData> pair in _formulaRemovedBlocks)
            {
                if (pair.Value == null) continue;
                plans.Add(new StoryGraphFormulaEffectPlan
                {
                    TalkId = pair.Key,
                    CgId = 0,
                    PreviousAutoCloseTalkIds = pair.Value.AutoCloseTalkIds != null
                        ? new List<int>(pair.Value.AutoCloseTalkIds)
                        : new List<int>(),
                });
            }

            // 阶段 2：边车登记的公式逐条物化。写入顺序恒为 PNG → CGCfg.json →
            // （调用方的）TrySave，引用永远最后落盘。
            if (_latexStore != null)
            {
                string modRoot = _editSession.ModRoot;
                string packageId = FormulaAssetService.ResolvePackageId(modRoot);
                // 状态事件（StateEvtView）整个不解释 screenEffect：不物化、不挂载，
                // 并把此前已经写进 JSON 的 [4019]/[4017] 死数据按移除计划回收。
                // 走告警而不是 error——不能因为这个阻断作者保存正文（LATEX-D2-3）。
                bool stateEventView = _editSession.IsStateEventView;
                var stateEventBlocked = new List<int>();
                HashSet<int> occupied = null;
                foreach (int talkId in _latexStore.CollectTalkIds(eventId))
                {
                    LatexBlockData block = _latexStore.GetBlock(eventId, talkId);
                    if (block == null || string.IsNullOrWhiteSpace(block.Latex)) continue;
                    if (_editSession.FindTalk(talkId) == null)
                    {
                        // 对话已从草稿删除：只回收旧登记，边车条目在保存成功后剪除。
                        plans.Add(new StoryGraphFormulaEffectPlan
                        {
                            TalkId = talkId,
                            CgId = 0,
                            PreviousAutoCloseTalkIds = block.AutoCloseTalkIds != null
                                ? new List<int>(block.AutoCloseTalkIds)
                                : new List<int>(),
                        });
                        continue;
                    }
                    if (stateEventView)
                    {
                        stateEventBlocked.Add(talkId);
                        if (block.CgId <= 0
                            && (block.AutoCloseTalkIds == null
                                || block.AutoCloseTalkIds.Count == 0))
                            continue;
                        plans.Add(new StoryGraphFormulaEffectPlan
                        {
                            TalkId = talkId,
                            CgId = 0,
                            PreviousAutoCloseTalkIds = block.AutoCloseTalkIds != null
                                ? new List<int>(block.AutoCloseTalkIds)
                                : new List<int>(),
                        });
                        // 源码留在边车（作者随时能改回普通对话事件），但资产登记
                        // 作废，让 GC 能回收已经没人引用的 PNG / CGCfg 条目。
                        block.CgId = 0;
                        block.AutoCloseTalkIds = new List<int>();
                        _latexStore.SetBlock(eventId, talkId, block);
                        continue;
                    }
                    if (string.IsNullOrEmpty(packageId))
                    {
                        error = "无法确定 Mod 目录名（packageId），块级公式不能物化；本次保存已中止。";
                        return false;
                    }
                    if (occupied == null) occupied = CollectOccupiedCgIds();
                    FormulaMaterializeResult materialized =
                        FormulaAssetService.MaterializeBlock(
                            modRoot, packageId, eventId, block.Latex, occupied,
                            null,
                            block.Mode == (int)FormulaCardMode.Text
                                ? FormulaCardMode.Text
                                : FormulaCardMode.Math);
                    if (!materialized.Success)
                    {
                        error = "对话 " + talkId + " 的块级公式无法生成："
                                + materialized.Error + " 本次保存已中止。";
                        return false;
                    }
                    occupied.Add(materialized.CgId);
                    // 立刻登记进本局运行时 CG 表：游戏只在启动时合并 mod 配置
                    // （ModCtrl.LoadModCfgs），刚分配的编号按构造不在 Cfg.CGCfgMap 里，
                    // 不登记的话保存完直接游玩会走 CGView.cs:74 的裸索引器抛
                    // KeyNotFoundException（面板停在空白态）。登记用绝对路径，
                    // 绕开 GetFullUrl 对 modPackageIds 的依赖（见 LocalizeFormulaUrls）。
                    RegisterRuntimeFormulaCg(
                        modRoot, materialized.CgId, materialized.Url, eventId);
                    block.CgId = materialized.CgId;
                    mounted[talkId] = block;
                    plans.Add(new StoryGraphFormulaEffectPlan
                    {
                        TalkId = talkId,
                        CgId = materialized.CgId,
                        AutoClose = block.AutoClose,
                        PreviousAutoCloseTalkIds = block.AutoCloseTalkIds != null
                            ? new List<int>(block.AutoCloseTalkIds)
                            : new List<int>(),
                    });
                }
                if (stateEventBlocked.Count > 0)
                    warnings.Add("事件 " + eventId + " 的对话 "
                                 + string.Join("、", stateEventBlocked
                                     .Take(4).Select(id => id.ToString()).ToArray())
                                 + (stateEventBlocked.Count > 4 ? " 等" : string.Empty)
                                 + " 登记了块级公式，但" + StateEventFormulaHint
                                 + "（源码已留在编辑器边车，改成普通对话事件后可直接保存生效）");
            }

            if (plans.Count == 0) return true;

            bool changed;
            string message;
            if (!_editSession.TryApplyFormulaScreenEffects(
                    plans, out changed, out message))
            {
                error = message;
                return false;
            }

            // 回写登记：cgId 与本次真正补写的 [4017] 下一句，供下次保存回收。
            foreach (StoryGraphFormulaEffectPlan plan in plans)
            {
                if (plan.CgId <= 0) continue;
                LatexBlockData block;
                if (!mounted.TryGetValue(plan.TalkId, out block) || block == null)
                    continue;
                block.AutoCloseTalkIds = new List<int>(plan.AppliedAutoCloseTalkIds);
                _latexStore.SetBlock(eventId, plan.TalkId, block);
                foreach (int skipped in plan.SkippedAutoCloseTalkIds)
                    warnings.Add("对话 " + plan.TalkId + " 的下一句 " + skipped
                                 + " 已有画面指令或正文为空，未自动补写“关闭 CG”："
                                 + "公式图会继续显示到后续剧情。");
                // 跨事件/草稿外的后继：这里写不了 [4017]，公式图会被带出本事件。
                foreach (int external in plan.ExternalSuccessorTalkIds)
                    warnings.Add("对话 " + plan.TalkId + " 的下一句 " + external
                                 + " 不在当前事件里，无法自动补写“关闭 CG”："
                                 + "公式图会跟着跳转带出本事件，"
                                 + "请在跳转前的那一句手动加上“关闭 CG（4017）”。");
                // 汇合点：4017 是全局关闭，写上去会掐断作者另一条 CG 线。
                foreach (int merged in plan.MergeSkippedTalkIds)
                    warnings.Add("对话 " + plan.TalkId + " 的下一句 " + merged
                                 + " 同时还能从另一条 CG/漫画线走到，"
                                 + "自动关闭会一并掐断那条线，因此未补写“关闭 CG”："
                                 + "请自行决定在哪一句关闭画面。");
            }
            if (changed) _formulaEditedSinceEnter = true;
            return true;
        }

        /// <summary>
        /// 分配避让集：运行时 CG 表里**不属于公式专用段**的键（本 mod 的 CGCfg.json
        /// 由 MaterializeBlock 自行并入，那才是硬避让的另一半）。
        ///
        /// Cfg.CGCfgMap 是游戏启动时合并的静态快照，本会话内只增不减；公式段编号又
        /// 只有 float 精确槽可用（每事件 12/13 个）。若把段内残留也算硬占用，改几轮
        /// 公式就会把窗口耗尽，且「删除不再使用的公式」对启动快照完全无效——保存
        /// 从此被拦到重启游戏为止（L3-5）。段内残留因此按软占用处理：允许复用，
        /// 代价是本局运行时该编号仍指向旧 PNG，调用方据此给出「需重启」提示。
        /// 读表失败不能中止保存——最坏是与运行时表撞号，由物化侧的文件避让兜住。
        /// </summary>
        /// <summary>
        /// 把刚物化的公式条目登记进本局运行时 CG 表（Cfg.CGCfgMap），让保存后无需
        /// 重启即可预览与游玩。urls 用本机绝对路径：作者正在编辑的作品未必在
        /// modPackageIds 里，相对 url 经 GetFullUrl 会静默解析失败并淡入白框。
        /// 落盘的 CGCfg.json 仍是相对 url，订阅者侧不受影响。
        /// 失败只记日志——登记不上最坏是回到"要重启才生效"，不该影响保存。
        /// </summary>
        private static void RegisterRuntimeFormulaCg(
            string modRoot, int cgId, string url, int evtId)
        {
            if (cgId <= 0) return;
            try
            {
                string local = FormulaAssetService.TryResolveLocalPngPath(modRoot, url);
                if (local == null)
                {
                    Plugin.Log?.LogWarning(
                        "[StoryGraph.Latex] 公式 PNG 未找到，运行时 CG 表未登记 "
                        + cgId + "（本局需重启才能显示）。");
                    return;
                }
                Dictionary<int, CGCfg> map = Cfg.CGCfgMap;
                if (map == null) return;
                map[cgId] = new CGCfg
                {
                    id = cgId,
                    name = "[公式] evt" + evtId + "-" + cgId,
                    urls = new List<string> { local },
                    // 与落盘条目一致：CG 图鉴跳过 group<0（CGLibraryView.cs:66）。
                    group = -1,
                };
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Latex] 运行时 CG 表登记失败（本局需重启才能显示）："
                    + e.Message);
            }
        }

        private static HashSet<int> CollectOccupiedCgIds()
        {
            var occupied = new HashSet<int>();
            try
            {
                Dictionary<int, CGCfg> map = Cfg.CGCfgMap;
                if (map != null)
                    foreach (int id in map.Keys)
                        if (!FormulaAssetService.IsFormulaCgId(id)) occupied.Add(id);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Latex] 读取运行时 CG 表失败，公式编号只对本 mod 文件避让："
                    + e.Message);
            }
            return occupied;
        }

        /// <summary>
        /// 运行时 CG 表里公式专用段的 id → 首个 url。复用软占用编号时用它判断
        /// 本局运行时是否仍指向旧图（指向同一张就没有任何提示的必要）。
        /// </summary>
        private static Dictionary<int, string> CollectRuntimeFormulaCgUrls()
        {
            var result = new Dictionary<int, string>();
            try
            {
                Dictionary<int, CGCfg> map = Cfg.CGCfgMap;
                if (map == null) return result;
                foreach (KeyValuePair<int, CGCfg> pair in map)
                {
                    if (!FormulaAssetService.IsFormulaCgId(pair.Key)) continue;
                    List<string> urls = pair.Value != null ? pair.Value.urls : null;
                    result[pair.Key] = urls != null && urls.Count > 0
                        ? urls[0]
                        : string.Empty;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Latex] 读取运行时公式 CG 条目失败：" + e.Message);
            }
            return result;
        }

        /// <summary>
        /// TrySave 提交成功之后的 best-effort 收尾（对齐 PruneSavedWorkspacePositions）：
        /// 写边车（行内源码 + 块级登记）并清理孤儿公式资产。全程只返回提示文字，
        /// 绝不把已提交的保存判成失败（committedOnDisk 纪律）。
        /// </summary>
        private string PersistLatexSidecarAndGc()
        {
            if (_editSession == null) return string.Empty;
            var notes = new List<string>();
            int eventId = _editSession.EventId;
            string modRoot = _editSession.ModRoot;
            bool sidecarSaved = true;

            if (_latexStore != null)
            {
                try
                {
                    // 磁盘上是烘焙产物，会话内存是作者原文：把原文登记进边车，
                    // 下次进入编辑模式才能换回源码继续编辑。
                    var liveIds = new HashSet<int>();
                    foreach (TalkCfg talk in _editSession.Talks)
                    {
                        if (talk == null) continue;
                        liveIds.Add(talk.id);
                        _latexStore.SetTalkSource(eventId, talk.id,
                            LatexInlineTranspiler.ContainsLatex(talk.content)
                                ? talk.content
                                : null);
                    }
                    // 已从草稿删除的对话条目必须剪除，否则其 CgId 会永远把孤儿 PNG 保活。
                    foreach (int talkId in _latexStore.CollectTalkIds(eventId))
                        if (!liveIds.Contains(talkId))
                            _latexStore.PruneTalk(eventId, talkId);

                    // 选项正文同样会被保存管线烘焙（ApplyOptionChanges），源码不登记
                    // 就是一次保存后永久丢失（D6-2）。借来显示的共享选项不登记：
                    // 它们既不写进本 mod 的 OptionCfg.json，也不该进本 mod 的边车。
                    var frozenOptionIds = new HashSet<int>(
                        _editSession.FrozenBuiltInOptionIds);
                    var liveOptionIds = new HashSet<int>();
                    if (_editSession.Options != null)
                    {
                        foreach (KeyValuePair<int, OptionCfg> pair
                                 in _editSession.Options)
                        {
                            if (pair.Value == null
                                || frozenOptionIds.Contains(pair.Key)) continue;
                            liveOptionIds.Add(pair.Key);
                            _latexStore.SetOptionSource(eventId, pair.Key,
                                LatexInlineTranspiler.ContainsLatex(pair.Value.content)
                                    ? pair.Value.content
                                    : null);
                        }
                    }
                    foreach (int optionId in _latexStore.CollectOptionIds(eventId))
                        if (!liveOptionIds.Contains(optionId))
                            _latexStore.PruneOption(eventId, optionId);

                    string saveError;
                    if (!_latexStore.Save(out saveError))
                    {
                        sidecarSaved = false;
                        notes.Add("注意：剧情已保存，但 LaTeX 源码边车写入失败，"
                                  + "下次进入编辑模式可能看到烘焙文本而非源码：" + saveError);
                    }
                }
                catch (Exception e)
                {
                    sidecarSaved = false;
                    Plugin.Log?.LogError("[StoryGraph.Latex.Sidecar] " + e);
                    notes.Add("注意：剧情已保存，但 LaTeX 源码边车更新失败："
                              + e.Message);
                }
            }

            _formulaRemovedBlocks.Clear();
            // 边车没写成盘时保持“待保存”，作者再点一次保存即可重试（剧情 JSON 已提交，
            // 会走“只有边车登记变化”的分支，不会重复写剧情文件）。
            if (sidecarSaved) _formulaEditedSinceEnter = false;

            string gcNote = RunFormulaAssetGc(modRoot);
            if (!string.IsNullOrEmpty(gcNote)) notes.Add(gcNote);
            if (notes.Count == 0) return string.Empty;
            return " " + string.Join(" ", notes.ToArray());
        }

        /// <summary>
        /// 孤儿公式资产清理（设计 §4 GC 第一层）。引用并集必须覆盖整个 mod 的
        /// TalkCfg.json，而不只是当前事件——否则别的事件的公式图会被误删、
        /// 玩家侧破图。读不到 TalkCfg.json 就整体跳过（无法反查引用就一个都不能删）。
        /// </summary>
        private string RunFormulaAssetGc(string modRoot)
        {
            try
            {
                HashSet<int> referenced;
                string readError;
                if (!TryCollectModFormulaReferences(
                        modRoot, out referenced, out readError))
                    return "注意：公式资产清理已跳过（" + readError + "）。";
                if (_latexStore != null)
                    foreach (int id in _latexStore.CollectBlockCgIds())
                        referenced.Add(id);
                if (_editSession != null)
                    foreach (int id in FormulaAssetService
                                 .CollectReferencedFormulaCgIds(_editSession.Talks))
                        referenced.Add(id);

                FormulaGcResult gc = FormulaAssetService.GcOrphans(modRoot, referenced);
                if (!gc.Success) return "注意：公式资产清理未完成：" + gc.Error;
                foreach (string warning in gc.Warnings)
                    Plugin.Log?.LogWarning("[StoryGraph.Latex.Gc] " + warning);
                if (gc.RemovedEntries == 0 && gc.DeletedPngs == 0) return string.Empty;
                return "已清理 " + gc.RemovedEntries + " 条无引用公式 CG 条目和 "
                       + gc.DeletedPngs + " 张孤儿公式图。";
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StoryGraph.Latex.Gc] " + e);
                return "注意：公式资产清理异常（不影响已保存的剧情）：" + e.Message;
            }
        }

        /// <summary>
        /// 委托给 FormulaAssetService 的共享实现（原版删除路径的 GC 用的是同一份，
        /// 两处口径必须一致）。
        /// </summary>
        private static bool TryCollectModFormulaReferences(
            string modRoot, out HashSet<int> referenced, out string error)
        {
            return FormulaAssetService.TryCollectModTalkFormulaReferences(
                modRoot, out referenced, out error);
        }

        /// <summary>编排告警统一走图内 Toast，并全量落日志（Toast 只显示前几条）。</summary>
        private void ShowFormulaWarnings(List<string> warnings)
        {
            if (warnings == null || warnings.Count == 0) return;
            foreach (string warning in warnings)
                Plugin.Log?.LogWarning("[StoryGraph.Latex] " + warning);
            string text = string.Join("\n",
                warnings.Take(3).ToArray());
            if (warnings.Count > 3)
                text += "\n（另有 " + (warnings.Count - 3) + " 条，详见日志）";
            try { StoryGraphToastRouter.Show(text); }
            catch { }
        }
    }
}
