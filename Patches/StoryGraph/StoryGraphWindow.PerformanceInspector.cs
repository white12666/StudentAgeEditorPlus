using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Config;
using Sdk;
using UnityEngine;
using UnityEngine.UI;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图属性栏的演出编排分页。流程仍由 Talk/Option 节点承担，人物、画面、
    /// 声音和 effect/effect2 只作为节点附着属性，不引入会打散剧情流程的动作节点。
    /// </summary>
    internal sealed partial class StoryGraphWindow
    {
        private enum InspectorPage
        {
            Basic,
            Cast,
            Stage,
            Logic,
            MiniGame,
            Advanced,
        }

        [NonSerialized] private ScrollRect _inspectorScroll;
        [NonSerialized] private GameObject _inspectorBasicPage;
        [NonSerialized] private GameObject _inspectorCastPage;
        [NonSerialized] private GameObject _inspectorStagePage;
        [NonSerialized] private GameObject _inspectorLogicPage;
        [NonSerialized] private GameObject _inspectorMiniGamePage;
        [NonSerialized] private GameObject _inspectorAdvancedPage;
        [NonSerialized] private Button _inspectorBasicTab;
        [NonSerialized] private Button _inspectorCastTab;
        [NonSerialized] private Button _inspectorStageTab;
        [NonSerialized] private Button _inspectorLogicTab;
        [NonSerialized] private Button _inspectorMiniGameTab;
        [NonSerialized] private Button _inspectorAdvancedTab;
        private InspectorPage _inspectorPage = InspectorPage.Basic;

        [NonSerialized] private InputField _inspectorSpeakerInput;
        [NonSerialized] private GameObject _inspectorSpeakerRow;
        [NonSerialized] private Text _inspectorSpeakerLabel;
        [NonSerialized] private GameObject _inspectorShowRow;
        [NonSerialized] private InputField _inspectorNextEventInput;
        [NonSerialized] private GameObject _inspectorNextEventRow;
        [NonSerialized] private Text _inspectorNextEventHint;
        private bool _inspectorShowingHelp;
        private float _inspectorHeaderHeight = 154f;
        private float _basicInspectorPageHeight = 430f;

        [NonSerialized] private Text _inspectorCastIntro;
        [NonSerialized] private GameObject _inspectorRoleIdsRow;
        [NonSerialized] private GameObject _inspectorHighlightsRow;
        [NonSerialized] private InputField _inspectorRoleIdsInput;
        [NonSerialized] private InputField _inspectorHighlightsInput;
        [NonSerialized] private InputField _inspectorRoleActionsInput;
        [NonSerialized] private InputField _inspectorActionActorInput;
        [NonSerialized] private InputField _inspectorActionArgsInput;
        [NonSerialized] private Text _inspectorRoleNamesHint;
        [NonSerialized] private Text _inspectorRoleActionsLabel;
        [NonSerialized] private Text _inspectorRoleActionsDescriptionLabel;
        [NonSerialized] private Text _inspectorRoleActionsDescription;
        [NonSerialized] private GameObject _inspectorActionActorRow;
        [NonSerialized] private GameObject _inspectorActionArgsRow;
        [NonSerialized] private Button[] _inspectorActionTemplateButtons;
        [NonSerialized] private Button[] _inspectorCastFooterButtons;
        private float _castInspectorPageHeight = 1050f;

        [NonSerialized] private GameObject _inspectorBackgroundRow;
        [NonSerialized] private GameObject _inspectorAudioRow;
        [NonSerialized] private InputField _inspectorBackgroundInput;
        [NonSerialized] private InputField _inspectorAudioInput;
        [NonSerialized] private InputField _inspectorScreenEffectInput;
        [NonSerialized] private InputField _inspectorVocalsInput;
        [NonSerialized] private InputField _inspectorScreenArgsInput;
        [NonSerialized] private Text _inspectorResourceHint;
        [NonSerialized] private Text _inspectorScreenEffectLabel;
        [NonSerialized] private Text _inspectorScreenDescription;
        [NonSerialized] private GameObject _inspectorVocalsRow;
        [NonSerialized] private GameObject _inspectorScreenArgsRow;
        [NonSerialized] private Button[] _inspectorScreenTemplateButtons;
        [NonSerialized] private Button[] _inspectorStageFooterButtons;
        private float _stageInspectorPageHeight = 790f;

        [NonSerialized] private Text _inspectorLogicIntro;
        [NonSerialized] private Button _inspectorDirectOptionModeButton;
        [NonSerialized] private Button _inspectorConditionalOptionModeButton;
        [NonSerialized] private Text _inspectorVisibilityConditionLabel;
        [NonSerialized] private InputField _inspectorVisibilityConditionInput;
        [NonSerialized] private Text _inspectorResultConditionLabel;
        [NonSerialized] private InputField _inspectorResultConditionInput;
        [NonSerialized] private Text _inspectorConditionPreview;
        [NonSerialized] private Button _inspectorConditionHelpButton;
        [NonSerialized] private Text _inspectorLogicRoutesLabel;
        [NonSerialized] private GameObject _inspectorSuccessRouteRow;
        [NonSerialized] private GameObject _inspectorFailureRouteRow;
        [NonSerialized] private Text _inspectorSuccessRouteLabel;
        [NonSerialized] private Text _inspectorFailureRouteLabel;
        [NonSerialized] private InputField _inspectorSuccessRouteInput;
        [NonSerialized] private InputField _inspectorFailureRouteInput;
        [NonSerialized] private Text _inspectorSuccessEffectsLabel;
        [NonSerialized] private Text _inspectorFailureEffectsLabel;
        [NonSerialized] private InputField _inspectorSuccessEffectsInput;
        [NonSerialized] private InputField _inspectorFailureEffectsInput;
        [NonSerialized] private Button _inspectorCopyLogicButton;
        [NonSerialized] private Button _inspectorPasteLogicButton;
        [NonSerialized] private Button _inspectorClearLogicButton;
        private int _optionConditionalDraftId = int.MinValue;
        private int _optionDirectModeConfirmId = int.MinValue;
        private float _optionDirectModeConfirmUntil;

        [NonSerialized] private Text _inspectorMiniGameIntro;
        [NonSerialized] private Text _inspectorMiniGameLabel;
        [NonSerialized] private InputField _inspectorMiniGameInput;
        [NonSerialized] private Text _inspectorMiniGameSummary;
        [NonSerialized] private Text _inspectorMiniGameFlowHint;
        [NonSerialized] private Button _inspectorClearMiniGameButton;
        [NonSerialized] private Text _inspectorMiniGameCommonLabel;
        [NonSerialized] private Text _inspectorMiniGameCommonBody;
        [NonSerialized] private Text _inspectorMiniGameSpecialLabel;
        [NonSerialized] private Text _inspectorMiniGameSpecialBody;
        [NonSerialized] private Text _inspectorAdvancedLabel;
        [NonSerialized] private Button _inspectorAdvancedReloadButton;
        [NonSerialized] private Text _inspectorAdvancedWarning;
        private float _logicInspectorPageHeight = 650f;
        private float _miniGameInspectorPageHeight = 720f;
        private float _advancedInspectorPageHeight = 680f;

        private StoryGraphPerformanceData _castClipboard;
        private StoryGraphPerformanceData _stageClipboard;
        private StoryGraphPerformanceData _wholePerformanceClipboard;
        private StoryGraphLogicData _logicClipboard;
        private string _performanceResourceSignature;
        private readonly Dictionary<int, string> _backgroundNames =
            new Dictionary<int, string>();
        private readonly Dictionary<int, string> _cgNames =
            new Dictionary<int, string>();
        private readonly Dictionary<int, string> _audioNames =
            new Dictionary<int, string>();
        private readonly Dictionary<int, string> _eventNames =
            new Dictionary<int, string>();
        private readonly HashSet<int> _audioBrowseIds =
            new HashSet<int>();
        private readonly Dictionary<int, string> _faceNames =
            new Dictionary<int, string>();

        private void BuildTabbedInspector()
        {
            _inspectorRoot = CreateUIObject("Inspector", _canvasRoot.transform);
            RectTransform root = (RectTransform)_inspectorRoot.transform;
            root.anchorMin = new Vector2(1f, 0f);
            root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(1f, 0.5f);
            root.offsetMin = new Vector2(-InspectorWidth, StatusbarHeight);
            root.offsetMax = new Vector2(0f, -ToolbarHeight);
            Image panel = _inspectorRoot.AddComponent<Image>();
            panel.color = PanelBg;
            panel.raycastTarget = true;

            Image separator = CreateImage(_inspectorRoot, "LeftSeparator", PanelBorder, false);
            separator.rectTransform.anchorMin = new Vector2(0f, 0f);
            separator.rectTransform.anchorMax = new Vector2(0f, 1f);
            separator.rectTransform.pivot = new Vector2(0f, 0.5f);
            separator.rectTransform.sizeDelta = new Vector2(1f, 0f);
            separator.rectTransform.anchoredPosition = Vector2.zero;

            GameObject viewportGo = CreateUIObject("ScrollViewport", root);
            RectTransform viewport = (RectTransform)viewportGo.transform;
            Stretch(viewport, 8f, 8f, 8f, 8f);
            Image viewportImage = viewportGo.AddComponent<Image>();
            viewportImage.color = new Color(1f, 1f, 1f, 0.001f);
            viewportImage.raycastTarget = true;
            viewportGo.AddComponent<RectMask2D>();

            GameObject contentGo = CreateUIObject("InspectorContent", viewport);
            _inspectorContent = (RectTransform)contentGo.transform;
            _inspectorContent.anchorMin = new Vector2(0f, 1f);
            _inspectorContent.anchorMax = new Vector2(1f, 1f);
            _inspectorContent.pivot = new Vector2(0.5f, 1f);
            _inspectorContent.anchoredPosition = Vector2.zero;
            _inspectorContent.sizeDelta = new Vector2(0f, 620f);

            _inspectorScroll = _inspectorRoot.AddComponent<ScrollRect>();
            _inspectorScroll.viewport = viewport;
            _inspectorScroll.content = _inspectorContent;
            _inspectorScroll.horizontal = false;
            _inspectorScroll.vertical = true;
            _inspectorScroll.movementType = ScrollRect.MovementType.Clamped;
            _inspectorScroll.scrollSensitivity = 34f;

            _inspectorTitle = CreateText(contentGo, "Title", 20, FontStyle.Bold,
                TitleColor, TextAnchor.MiddleLeft);
            Place(_inspectorTitle.rectTransform, 0f, 1f, 0f, 1f,
                8f, -4f, InspectorWidth - 78f, 34f);
            _inspectorTitle.text = "属性检查器";

            _inspectorInfo = CreateText(contentGo, "Info", SecondaryFontSize,
                FontStyle.Normal, SubtitleColor, TextAnchor.UpperLeft);
            _inspectorInfo.horizontalOverflow = HorizontalWrapMode.Wrap;
            _inspectorInfo.verticalOverflow = VerticalWrapMode.Truncate;
            Place(_inspectorInfo.rectTransform, 0f, 1f, 0f, 1f,
                8f, -42f, InspectorWidth - 42f, 60f);

            // 430 宽检查器放六页时使用短标签；完整含义在各页标题中说明。
            _inspectorBasicTab = CreateInspectorTab(
                contentGo, "基础", 8f, 60f, InspectorPage.Basic);
            _inspectorCastTab = CreateInspectorTab(
                contentGo, "人物", 72f, 60f, InspectorPage.Cast);
            _inspectorStageTab = CreateInspectorTab(
                contentGo, "画面", 136f, 60f, InspectorPage.Stage);
            _inspectorLogicTab = CreateInspectorTab(
                contentGo, "逻辑", 200f, 60f, InspectorPage.Logic);
            _inspectorMiniGameTab = CreateInspectorTab(
                contentGo, "小游戏", 264f, 60f, InspectorPage.MiniGame);
            _inspectorAdvancedTab = CreateInspectorTab(
                contentGo, "高级", 328f, 60f, InspectorPage.Advanced);

            _inspectorBasicPage = CreateInspectorPage(contentGo, "BasicPage");
            _inspectorCastPage = CreateInspectorPage(contentGo, "CastPage");
            _inspectorStagePage = CreateInspectorPage(contentGo, "StagePage");
            _inspectorLogicPage = CreateInspectorPage(contentGo, "LogicPage");
            _inspectorMiniGamePage = CreateInspectorPage(contentGo, "MiniGamePage");
            _inspectorAdvancedPage = CreateInspectorPage(contentGo, "AdvancedPage");
            BuildBasicInspectorPage(_inspectorBasicPage);
            BuildCastInspectorPage(_inspectorCastPage);
            BuildStageInspectorPage(_inspectorStagePage);
            BuildLogicInspectorPage(_inspectorLogicPage);
            BuildMiniGameInspectorPage(_inspectorMiniGamePage);
            BuildAdvancedInspectorPage(_inspectorAdvancedPage);

            _inspectorCloseButton = CreateToolbarButton(
                _inspectorRoot, "×", -8f, -20f, 34f, ToggleInspector, true);
            _inspectorCloseButton.transform.SetAsLastSibling();
            SwitchInspectorPage(InspectorPage.Basic, false);
            _inspectorRoot.SetActive(false);
        }

        private Button CreateInspectorTab(
            GameObject parent, string label, float x, float width, InspectorPage page)
        {
            Button button = CreateToolbarButton(
                parent, label, x, -112f, width,
                delegate { SwitchInspectorPage(page, true); });
            Place((RectTransform)button.transform, 0f, 1f, 0f, 1f,
                x, -108f, width, 36f);
            Text text = button.GetComponentInChildren<Text>();
            if (text != null) text.fontSize = 13;
            return button;
        }

        private GameObject CreateInspectorPage(GameObject parent, string name)
        {
            GameObject page = CreateUIObject(name, parent.transform);
            RectTransform rect = (RectTransform)page.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -154f);
            rect.sizeDelta = new Vector2(0f, 1100f);
            return page;
        }

        private void BuildBasicInspectorPage(GameObject page)
        {
            _inspectorContentLabel = CreateInspectorLabel(
                page, "正文 content（实时）", -4f);
            _inspectorContentInput = CreateInspectorInput(
                page, "Content", -28f, 122f, true);
            SetInspectorPlaceholder(_inspectorContentInput,
                "输入时会立即更新节点卡片和剧情草稿…");
            AttachInspectorContentScroll(_inspectorContentInput);
            BuildLatexPreview(page);

            _inspectorSpeakerInput = CreateInspectorFieldRow(
                page, "对话角色 roleIds", -158f,
                out _inspectorSpeakerRow, out _inspectorSpeakerLabel);
            SetInspectorPlaceholder(_inspectorSpeakerInput,
                "例如：旁白、主角、1001 或人物名称；多人用逗号分隔");

            _inspectorShowInput = CreateInspectorFieldRow(
                page, "标题", -158f,
                out _inspectorShowRow, out _inspectorShowLabel);
            SetInspectorPlaceholder(_inspectorShowInput, "分组或注释框标题");
            _inspectorTagInput = CreateInspectorFieldRow(
                page, "记录标签 tag（可选）", -158f,
                out _inspectorTagRow, out _inspectorTagLabel);
            SetInspectorPlaceholder(_inspectorTagInput,
                "选择该选项后写入的剧情记录标签");
            Text nextEventLabel;
            _inspectorNextEventInput = CreateInspectorFieldRow(
                page, "后备事件 nextEvtId（ID / 标题）", -224f,
                out _inspectorNextEventRow, out nextEventLabel);
            SetInspectorPlaceholder(_inspectorNextEventInput,
                "0=不跳转；仅在本选项没有可执行 Talk 结果时使用");
            _inspectorEventLookupButton = AttachInspectorLookupButton(
                _inspectorNextEventRow, _inspectorNextEventInput,
                StoryGraphResourceKind.Event);
            _inspectorNextEventHint = CreateInspectorNote(
                page,
                "nextEvtId 是后备路径：小游戏接管或当前条件已有 Talk 结果时不会执行。",
                -290f, 64f, SubtitleColor);

            _inspectorContentInput.onValueChanged.AddListener(OnInspectorBasicValueChanged);
            _inspectorSpeakerInput.onValueChanged.AddListener(OnInspectorSpeakerValueChanged);
            _inspectorShowInput.onValueChanged.AddListener(OnInspectorBasicValueChanged);
            _inspectorTagInput.onValueChanged.AddListener(OnInspectorBasicValueChanged);
            _inspectorNextEventInput.onValueChanged.AddListener(
                OnOptionNextEventValueChanged);
            _inspectorContentInput.onEndEdit.AddListener(OnInspectorBasicEndEdit);
            _inspectorSpeakerInput.onEndEdit.AddListener(OnInspectorSpeakerEndEdit);
            _inspectorShowInput.onEndEdit.AddListener(OnInspectorBasicEndEdit);
            _inspectorTagInput.onEndEdit.AddListener(OnInspectorBasicEndEdit);
            _inspectorNextEventInput.onEndEdit.AddListener(
                OnOptionNextEventEndEdit);
        }

        private void BuildCastInspectorPage(GameObject page)
        {
            _inspectorCastIntro = CreateInspectorNote(
                page,
                "合法配置会立即热应用；常用动作可用下方模板追加。"
                + "原生多行动作、未知动作和额外参数都会保留。",
                -4f, 48f, SubtitleColor);

            Text roleIdsLabel;
            _inspectorRoleIdsInput = CreateInspectorFieldRow(
                page, "对话角色 roleIds（可多人）", -58f,
                out _inspectorRoleIdsRow, out roleIdsLabel);
            SetInspectorPlaceholder(_inspectorRoleIdsInput,
                "与基础页对话角色相同；-1=旁白，0=主角");

            Text highlightLabel;
            _inspectorHighlightsInput = CreateInspectorFieldRow(
                page, "高亮人物 highlights", -124f,
                out _inspectorHighlightsRow, out highlightLabel);
            SetInspectorPlaceholder(_inspectorHighlightsInput, "留空表示不单独高亮");

            _inspectorRoleNamesHint = CreateInspectorNote(
                page, string.Empty, -190f, 44f, SubtitleColor);
            _inspectorRoleActionsLabel = CreateInspectorLabel(
                page, "人物动作 roles（人物 ID, 动作码, 参数…）", -234f);
            _inspectorRoleActionsInput = CreateInspectorInput(
                page, "RoleActions", -258f, 210f, true);
            SetInspectorPlaceholder(_inspectorRoleActionsInput,
                "例：1001, 1001, 0, 3, 0\n例：1001, 3000, 12");
            _inspectorRoleIdsInput.onValueChanged.AddListener(OnCastInspectorValueChanged);
            _inspectorHighlightsInput.onValueChanged.AddListener(OnCastInspectorValueChanged);
            _inspectorRoleActionsInput.onValueChanged.AddListener(OnCastInspectorValueChanged);
            _inspectorRoleIdsInput.onEndEdit.AddListener(OnCastInspectorEndEdit);
            _inspectorHighlightsInput.onEndEdit.AddListener(OnCastInspectorEndEdit);
            _inspectorRoleActionsInput.onEndEdit.AddListener(OnCastInspectorEndEdit);

            _inspectorRoleActionsDescriptionLabel = CreateInspectorLabel(
                page, "动作解释（只读预览）", -476f);
            _inspectorRoleActionsDescription = CreateInspectorNote(
                page, string.Empty, -500f, 104f, BodyTextColor);

            Text actorLabel;
            _inspectorActionActorInput = CreateInspectorFieldRow(
                page, "模板人物 ID", -612f,
                out _inspectorActionActorRow, out actorLabel);
            SetInspectorPlaceholder(_inspectorActionActorInput, "要执行动作的人物编号");
            Text argsLabel;
            _inspectorActionArgsInput = CreateInspectorFieldRow(
                page, "模板参数（可留空）", -678f,
                out _inspectorActionArgsRow, out argsLabel);
            SetInspectorPlaceholder(_inspectorActionArgsInput, "逗号分隔；按原生参数顺序填写");

            _inspectorActionTemplateButtons = new[]
            {
                CreateActionTemplateButton(page, "放置进场", 8f, -750f, 1001,
                    new[] { 0f, 3f, 0f }),
                CreateActionTemplateButton(page, "淡入进场", 198f, -750f, 1002,
                    new[] { 0f, 3f, 0f }),
                CreateActionTemplateButton(page, "退场", 8f, -796f, 2001,
                    new[] { 0f, 0f }),
                CreateActionTemplateButton(page, "表情/姿势", 198f, -796f, 3000,
                    new[] { 0f }),
                CreateActionTemplateButton(page, "抖动", 8f, -842f, 3002,
                    new[] { 0.5f, 0f }),
                CreateActionTemplateButton(page, "横向移动", 198f, -842f, 3004,
                    new[] { 100f, 0f, 0f, 0.5f }),
                CreateActionTemplateButton(page, "纵向移动", 8f, -888f, 3008,
                    new[] { 100f, 0f, 0f }),
                CreateActionTemplateButton(page, "转身", 198f, -888f, 3005,
                    new[] { 0f }),
            };

            _inspectorCastFooterButtons = new[]
            {
                CreatePageButton(
                    page, "复制人物", 8f, -940f, 118f, CopyCastPerformance),
                CreatePageButton(
                    page, "粘贴人物", 136f, -940f, 118f, PasteCastPerformance),
                CreatePageButton(
                    page, "清空人物", 264f, -940f, 124f, ClearCastPerformance),
                CreatePageButton(
                    page, "复制整套", 8f, -988f, 118f, CopyWholePerformance),
                CreatePageButton(
                    page, "粘贴整套", 136f, -988f, 118f, PasteWholePerformance),
                CreatePageButton(
                    page, "清空整套", 264f, -988f, 124f, ClearWholePerformance),
            };
            RelayoutCastInspector();
        }

        private void RelayoutCastInspector()
        {
            if (_inspectorCastIntro == null
                || _inspectorRoleActionsInput == null) return;
            float cursor = -4f;
            float introHeight = PreferredInspectorTextHeight(
                _inspectorCastIntro, 44f, 104f);
            SetInspectorRect(
                _inspectorCastIntro.rectTransform,
                TakeInspectorSlot(ref cursor, introHeight, 10f),
                introHeight);
            SetInspectorRect(
                _inspectorRoleIdsRow != null
                    ? (RectTransform)_inspectorRoleIdsRow.transform : null,
                TakeInspectorSlot(ref cursor, 62f, 4f), 62f);
            SetInspectorRect(
                _inspectorHighlightsRow != null
                    ? (RectTransform)_inspectorHighlightsRow.transform : null,
                TakeInspectorSlot(ref cursor, 62f, 6f), 62f);

            float roleHintHeight = PreferredInspectorTextHeight(
                _inspectorRoleNamesHint, 44f, 112f);
            SetInspectorRect(
                _inspectorRoleNamesHint?.rectTransform,
                TakeInspectorSlot(ref cursor, roleHintHeight, 8f),
                roleHintHeight);
            SetInspectorRect(
                _inspectorRoleActionsLabel?.rectTransform,
                TakeInspectorSlot(ref cursor, 22f, 6f), 22f);
            SetInspectorRect(
                (RectTransform)_inspectorRoleActionsInput.transform,
                TakeInspectorSlot(ref cursor, 210f, 10f), 210f);
            SetInspectorRect(
                _inspectorRoleActionsDescriptionLabel?.rectTransform,
                TakeInspectorSlot(ref cursor, 22f, 6f), 22f);

            float descriptionHeight = PreferredInspectorTextHeight(
                _inspectorRoleActionsDescription, 52f, 260f);
            SetInspectorRect(
                _inspectorRoleActionsDescription?.rectTransform,
                TakeInspectorSlot(ref cursor, descriptionHeight, 10f),
                descriptionHeight);
            SetInspectorRect(
                _inspectorActionActorRow != null
                    ? (RectTransform)_inspectorActionActorRow.transform : null,
                TakeInspectorSlot(ref cursor, 62f, 4f), 62f);
            SetInspectorRect(
                _inspectorActionArgsRow != null
                    ? (RectTransform)_inspectorActionArgsRow.transform : null,
                TakeInspectorSlot(ref cursor, 62f, 10f), 62f);

            for (int row = 0; row < 4; row++)
            {
                float rowY = TakeInspectorSlot(ref cursor, 40f, 6f);
                PositionInspectorButtonPair(
                    _inspectorActionTemplateButtons, row * 2, rowY);
            }
            cursor -= 6f;
            for (int row = 0; row < 2; row++)
            {
                float rowY = TakeInspectorSlot(ref cursor, 40f, 8f);
                PositionInspectorButtonTriple(
                    _inspectorCastFooterButtons, row * 3, rowY);
            }

            _castInspectorPageHeight = Math.Max(900f, 0f - cursor);
            RefreshInspectorContentHeight();
        }

        private void BuildStageInspectorPage(GameObject page)
        {
            Text bgLabel;
            _inspectorBackgroundInput = CreateInspectorFieldRow(
                page, "背景 bg（ID / 名称）", -4f,
                out _inspectorBackgroundRow, out bgLabel);
            SetInspectorPlaceholder(_inspectorBackgroundInput, "0=沿用；也可输入配置名称");
            _inspectorBackgroundLookupButton = AttachInspectorLookupButton(
                _inspectorBackgroundRow, _inspectorBackgroundInput,
                StoryGraphResourceKind.Background);
            Text audioLabel;
            _inspectorAudioInput = CreateInspectorFieldRow(
                page, "音乐 / 音效 audio（ID / 名称）", -70f,
                out _inspectorAudioRow, out audioLabel);
            SetInspectorPlaceholder(_inspectorAudioInput,
                "-1=结束时恢复日常 BGM；0=本句不切换；也可输入名称");
            _inspectorAudioLookupButton = AttachInspectorLookupButton(
                _inspectorAudioRow, _inspectorAudioInput,
                StoryGraphResourceKind.Audio);
            _inspectorResourceHint = CreateInspectorNote(
                page, string.Empty, -136f, 50f, SubtitleColor);

            _inspectorScreenEffectLabel = CreateInspectorLabel(
                page, "屏幕效果 screenEffect（效果码, 参数…）", -194f);
            _inspectorScreenEffectInput = CreateInspectorInput(
                page, "ScreenEffect", -218f, 82f, true);
            SetInspectorPlaceholder(_inspectorScreenEffectInput,
                "例：4015, 9001（显示 CG）");
            _inspectorScreenDescription = CreateInspectorNote(
                page, string.Empty, -308f, 72f, BodyTextColor);

            Text vocalsLabel;
            _inspectorVocalsInput = CreateInspectorFieldRow(
                page, "语音 vocals（原生数字列表）", -388f,
                out _inspectorVocalsRow, out vocalsLabel);
            SetInspectorPlaceholder(_inspectorVocalsInput, "留空表示无语音参数");
            Text screenArgsLabel;
            _inspectorScreenArgsInput = CreateInspectorFieldRow(
                page, "模板参数 / CG 编号", -454f,
                out _inspectorScreenArgsRow, out screenArgsLabel);
            SetInspectorPlaceholder(_inspectorScreenArgsInput,
                "显示 CG 时填 CG ID；其它模板可留空");
            _inspectorCgLookupButton = AttachInspectorLookupButton(
                _inspectorScreenArgsRow, _inspectorScreenArgsInput,
                StoryGraphResourceKind.Cg);

            _inspectorBackgroundInput.onValueChanged.AddListener(OnStageInspectorValueChanged);
            _inspectorAudioInput.onValueChanged.AddListener(OnStageInspectorValueChanged);
            _inspectorScreenEffectInput.onValueChanged.AddListener(OnStageInspectorValueChanged);
            _inspectorVocalsInput.onValueChanged.AddListener(OnStageInspectorValueChanged);
            _inspectorBackgroundInput.onEndEdit.AddListener(OnStageInspectorEndEdit);
            _inspectorAudioInput.onEndEdit.AddListener(OnStageInspectorEndEdit);
            _inspectorScreenEffectInput.onEndEdit.AddListener(OnStageInspectorEndEdit);
            _inspectorVocalsInput.onEndEdit.AddListener(OnStageInspectorEndEdit);

            _inspectorScreenTemplateButtons = new[]
            {
                CreateScreenTemplateButton(page, "屏幕震动", 8f, -526f, 4001,
                    new[] { 1f }, false),
                CreateScreenTemplateButton(page, "清除效果", 198f, -526f, 4003,
                    new float[0], false),
                CreateScreenTemplateButton(page, "泛黄滤镜", 8f, -572f, 4009,
                    new float[0], false),
                CreateScreenTemplateButton(page, "底片反色", 198f, -572f, 4010,
                    new float[0], false),
                CreateScreenTemplateButton(page, "白屏闪光", 8f, -618f, 4012,
                    new float[0], false),
                CreateScreenTemplateButton(page, "彩带庆祝", 198f, -618f, 4013,
                    new float[0], false),
                CreateScreenTemplateButton(page, "显示 CG", 8f, -664f, 4015,
                    new float[0], true),
                CreateScreenTemplateButton(page, "关闭 CG", 198f, -664f, 4017,
                    new float[0], false),
            };

            _inspectorStageFooterButtons = new[]
            {
                CreatePageButton(
                    page, "复制画面", 8f, -716f, 118f, CopyStagePerformance),
                CreatePageButton(
                    page, "粘贴画面", 136f, -716f, 118f, PasteStagePerformance),
                CreatePageButton(
                    page, "清空画面", 264f, -716f, 124f, ClearStagePerformance),
            };
            RelayoutStageInspector();
        }

        private void RelayoutStageInspector()
        {
            if (_inspectorBackgroundInput == null
                || _inspectorScreenEffectInput == null) return;
            float cursor = -4f;
            SetInspectorRect(
                _inspectorBackgroundRow != null
                    ? (RectTransform)_inspectorBackgroundRow.transform : null,
                TakeInspectorSlot(ref cursor, 62f, 4f), 62f);
            SetInspectorRect(
                _inspectorAudioRow != null
                    ? (RectTransform)_inspectorAudioRow.transform : null,
                TakeInspectorSlot(ref cursor, 62f, 6f), 62f);

            float resourceHeight = PreferredInspectorTextHeight(
                _inspectorResourceHint, 44f, 116f);
            SetInspectorRect(
                _inspectorResourceHint?.rectTransform,
                TakeInspectorSlot(ref cursor, resourceHeight, 8f),
                resourceHeight);
            SetInspectorRect(
                _inspectorScreenEffectLabel?.rectTransform,
                TakeInspectorSlot(ref cursor, 22f, 6f), 22f);
            SetInspectorRect(
                (RectTransform)_inspectorScreenEffectInput.transform,
                TakeInspectorSlot(ref cursor, 82f, 8f), 82f);

            float descriptionHeight = PreferredInspectorTextHeight(
                _inspectorScreenDescription, 48f, 180f);
            SetInspectorRect(
                _inspectorScreenDescription?.rectTransform,
                TakeInspectorSlot(ref cursor, descriptionHeight, 8f),
                descriptionHeight);
            SetInspectorRect(
                _inspectorVocalsRow != null
                    ? (RectTransform)_inspectorVocalsRow.transform : null,
                TakeInspectorSlot(ref cursor, 62f, 4f), 62f);
            SetInspectorRect(
                _inspectorScreenArgsRow != null
                    ? (RectTransform)_inspectorScreenArgsRow.transform : null,
                TakeInspectorSlot(ref cursor, 62f, 10f), 62f);

            for (int row = 0; row < 4; row++)
            {
                float rowY = TakeInspectorSlot(ref cursor, 40f, 6f);
                PositionInspectorButtonPair(
                    _inspectorScreenTemplateButtons, row * 2, rowY);
            }
            cursor -= 6f;
            float footerY = TakeInspectorSlot(ref cursor, 40f, 10f);
            PositionInspectorButtonTriple(
                _inspectorStageFooterButtons, 0, footerY);

            _stageInspectorPageHeight = Math.Max(720f, 0f - cursor);
            RefreshInspectorContentHeight();
        }

        private void BuildLogicInspectorPage(GameObject page)
        {
            StoryGraphLogicInspectorPresentation presentation =
                StoryGraphLogicInspectorPresentation.Create(
                    true, false, false);
            _inspectorLogicIntro = CreateInspectorNote(
                page, presentation.Intro, -4f, 72f, SubtitleColor);

            _inspectorDirectOptionModeButton = CreatePageButton(
                page, "直接进入剧情", 8f, -86f, 182f,
                SelectDirectOptionMode);
            _inspectorConditionalOptionModeButton = CreatePageButton(
                page, "根据条件分支", 206f, -86f, 182f,
                SelectConditionalOptionMode);

            _inspectorVisibilityConditionLabel = CreateInspectorLabel(
                page, presentation.VisibilityConditionLabel ?? string.Empty,
                -86f);
            _inspectorVisibilityConditionInput = CreateInspectorInput(
                page, "VisibilityConditions", -114f, 84f, true);
            SetInspectorPlaceholder(
                _inspectorVisibilityConditionInput,
                presentation.VisibilityConditionPlaceholder);

            _inspectorResultConditionLabel = CreateInspectorLabel(
                page, presentation.ResultConditionLabel, -204f);
            _inspectorResultConditionInput = CreateInspectorInput(
                page, "ResultConditions", -232f, 84f, true);
            SetInspectorPlaceholder(
                _inspectorResultConditionInput,
                presentation.ResultConditionPlaceholder);

            _inspectorConditionPreview = CreateInspectorNote(
                page, "当前判断：始终满足（未设置条件）",
                -324f, 64f, BodyTextColor);
            _inspectorConditionPreview.fontSize = 12;
            _inspectorConditionHelpButton = CreatePageButton(
                page, "查看条件写法", 248f, -396f, 140f,
                ModCtrl.ShowDoc);

            _inspectorLogicRoutesLabel = CreateInspectorLabel(
                page, presentation.RoutesLabel, -446f);
            _inspectorSuccessRouteInput = CreateInspectorFieldRow(
                page, presentation.SuccessRouteLabel, -474f,
                out _inspectorSuccessRouteRow,
                out _inspectorSuccessRouteLabel);
            _inspectorFailureRouteInput = CreateInspectorFieldRow(
                page, presentation.FailureRouteLabel, -542f,
                out _inspectorFailureRouteRow,
                out _inspectorFailureRouteLabel);
            SetInspectorPlaceholder(_inspectorSuccessRouteInput,
                "例如：1001　或　1001, 1002");
            SetInspectorPlaceholder(_inspectorFailureRouteInput,
                "例如：1003　或　1003, 1004");

            _inspectorSuccessEffectsLabel = CreateInspectorLabel(
                page, presentation.PrimaryLabel, -610f);
            _inspectorSuccessEffectsInput = CreateInspectorInput(
                page, "SuccessEffects", -638f, 116f, true);
            SetInspectorPlaceholder(_inspectorSuccessEffectsInput,
                presentation.PrimaryPlaceholder);
            _inspectorFailureEffectsLabel = CreateInspectorLabel(
                page, presentation.SecondaryLabel, -764f);
            _inspectorFailureEffectsInput = CreateInspectorInput(
                page, "FailureEffects", -792f, 116f, true);
            SetInspectorPlaceholder(_inspectorFailureEffectsInput,
                presentation.SecondaryPlaceholder);
            InputField[] logicInputs =
            {
                _inspectorVisibilityConditionInput,
                _inspectorResultConditionInput,
                _inspectorSuccessRouteInput,
                _inspectorFailureRouteInput,
                _inspectorSuccessEffectsInput,
                _inspectorFailureEffectsInput,
            };
            foreach (InputField input in logicInputs)
            {
                input.onValueChanged.AddListener(
                    OnLogicInspectorValueChanged);
                input.onEndEdit.AddListener(OnLogicInspectorEndEdit);
            }

            _inspectorCopyLogicButton = CreatePageButton(
                page, "复制预设", 8f, -924f, 118f, CopyLogicPerformance);
            _inspectorPasteLogicButton = CreatePageButton(
                page, "粘贴预设", 136f, -924f, 118f, PasteLogicPerformance);
            _inspectorClearLogicButton = CreatePageButton(
                page, "清空逻辑", 264f, -924f, 124f, ClearLogicPerformance);
            RefreshLogicInspectorPresentation(null, null);
        }

        private void BuildMiniGameInspectorPage(GameObject page)
        {
            StoryGraphMiniGameInspectorPresentation presentation =
                StoryGraphMiniGameInspectorPresentation.Create(true);
            _inspectorMiniGameIntro = CreateInspectorNote(
                page, presentation.Intro, -4f, 72f, SubtitleColor);
            _inspectorMiniGameLabel = CreateInspectorLabel(
                page, presentation.InputLabel, -86f);
            _inspectorMiniGameInput = CreateInspectorInput(
                page, "MiniGame", -114f, 46f, false);
            SetInspectorPlaceholder(_inspectorMiniGameInput,
                presentation.InputPlaceholder);
            _inspectorMiniGameInput.onValueChanged.AddListener(OnMiniGameInspectorValueChanged);
            _inspectorMiniGameInput.onEndEdit.AddListener(OnMiniGameInspectorEndEdit);

            _inspectorMiniGameSummary = CreateInspectorNote(
                page, "尚未添加小游戏。", -170f, 60f, BodyTextColor);
            _inspectorMiniGameFlowHint = CreateInspectorNote(
                page, string.Empty, -238f, 70f, AccentOrange);
            _inspectorMiniGameSummary.fontSize = 12;
            _inspectorMiniGameFlowHint.fontSize = 12;

            _inspectorClearMiniGameButton = CreatePageButton(
                page, "移除小游戏", 248f, -320f, 140f, ClearMiniGameInspector);
            _inspectorMiniGameLookupButton = CreatePageButton(
                page, "① 选择 / 更换小游戏", 8f, -320f, 230f,
                delegate
                {
                    OpenInspectorResourcePicker(
                        StoryGraphResourceKind.MiniGame);
                });

            _inspectorMiniGameCommonLabel = CreateInspectorLabel(
                page, presentation.FlowLabel, -378f);
            _inspectorMiniGameCommonBody = CreateInspectorNote(page,
                presentation.FlowBody,
                -406f, 112f, SubtitleColor);
            _inspectorMiniGameCommonBody.fontSize = 12;

            _inspectorMiniGameSpecialLabel = CreateInspectorLabel(
                page, presentation.SpecialLabel, -532f);
            _inspectorMiniGameSpecialBody = CreateInspectorNote(page,
                presentation.SpecialBody,
                -560f, 112f, AccentOrange);
            _inspectorMiniGameSpecialBody.fontSize = 12;
            RelayoutMiniGameInspector();
        }

        private void RefreshLogicInspectorPresentation(
            TalkCfg talk, OptionCfg option)
        {
            if (_inspectorLogicIntro == null
                || _inspectorResultConditionInput == null
                || _inspectorSuccessEffectsInput == null
                || _inspectorFailureEffectsInput == null) return;
            bool isTalk = talk != null || option == null;
            bool hasMiniGame = talk != null
                ? talk.miniGame != null && talk.miniGame.Count > 0
                : option != null && option.miniGame != null
                  && option.miniGame.Count > 0;
            bool hasFailureData = talk != null && talk.effect2 != null
                                  && talk.effect2.Count > 0;
            StoryGraphLogicInspectorPresentation presentation =
                StoryGraphLogicInspectorPresentation.Create(
                    isTalk, hasMiniGame, hasFailureData,
                    IsConditionalOptionEditor(option, hasMiniGame));
            if (option != null && presentation.IsConditionalOption)
                _optionConditionalDraftId = option.id;

            _inspectorLogicIntro.text = presentation.Intro;
            _inspectorLogicIntro.fontSize = 12;
            if (_inspectorDirectOptionModeButton != null)
            {
                _inspectorDirectOptionModeButton.gameObject.SetActive(
                    presentation.ShowOptionModeSelector);
                SetButtonLabel(_inspectorDirectOptionModeButton,
                    presentation.IsConditionalOption
                        ? "直接进入剧情" : "● 直接进入剧情");
                _inspectorDirectOptionModeButton.interactable =
                    presentation.ShowOptionModeSelector
                    && presentation.IsConditionalOption;
            }
            if (_inspectorConditionalOptionModeButton != null)
            {
                _inspectorConditionalOptionModeButton.gameObject.SetActive(
                    presentation.ShowOptionModeSelector);
                SetButtonLabel(_inspectorConditionalOptionModeButton,
                    presentation.IsConditionalOption
                        ? "● 根据条件分支" : "根据条件分支");
                _inspectorConditionalOptionModeButton.interactable =
                    presentation.ShowOptionModeSelector
                    && !presentation.IsConditionalOption;
            }
            if (_inspectorVisibilityConditionLabel != null)
            {
                _inspectorVisibilityConditionLabel.text =
                    presentation.VisibilityConditionLabel ?? string.Empty;
                _inspectorVisibilityConditionLabel.gameObject.SetActive(
                    presentation.ShowVisibilityCondition);
            }
            if (_inspectorVisibilityConditionInput != null)
            {
                _inspectorVisibilityConditionInput.gameObject.SetActive(
                    presentation.ShowVisibilityCondition);
                SetInspectorPlaceholder(
                    _inspectorVisibilityConditionInput,
                    presentation.VisibilityConditionPlaceholder);
            }
            if (_inspectorResultConditionLabel != null)
            {
                _inspectorResultConditionLabel.text =
                    presentation.ResultConditionLabel;
                _inspectorResultConditionLabel.gameObject.SetActive(
                    presentation.ShowResultCondition);
            }
            _inspectorResultConditionInput.gameObject.SetActive(
                presentation.ShowResultCondition);
            SetInspectorPlaceholder(
                _inspectorResultConditionInput,
                presentation.ResultConditionPlaceholder);
            if (_inspectorLogicRoutesLabel != null)
                _inspectorLogicRoutesLabel.text = presentation.RoutesLabel;
            if (_inspectorSuccessRouteLabel != null)
                _inspectorSuccessRouteLabel.text =
                    presentation.SuccessRouteLabel;
            if (_inspectorFailureRouteLabel != null)
                _inspectorFailureRouteLabel.text =
                    presentation.FailureRouteLabel;
            if (_inspectorFailureRouteRow != null)
                _inspectorFailureRouteRow.SetActive(
                    presentation.ShowFailureRoute);
            if (_inspectorSuccessEffectsLabel != null)
                _inspectorSuccessEffectsLabel.text =
                    presentation.PrimaryLabel;
            if (_inspectorFailureEffectsLabel != null)
            {
                _inspectorFailureEffectsLabel.text =
                    presentation.SecondaryLabel;
                _inspectorFailureEffectsLabel.gameObject.SetActive(
                    presentation.ShowSecondary);
            }
            SetInspectorPlaceholder(
                _inspectorSuccessEffectsInput,
                presentation.PrimaryPlaceholder);
            SetInspectorPlaceholder(
                _inspectorFailureEffectsInput,
                presentation.SecondaryPlaceholder);
            _inspectorFailureEffectsInput.gameObject.SetActive(
                presentation.ShowSecondary);
            UpdateLogicDescriptionFromInput();
            RelayoutLogicInspector(presentation);
        }

        private bool IsConditionalOptionEditor(
            OptionCfg option, bool hasMiniGame)
        {
            if (option == null) return false;
            if (_optionConditionalDraftId == option.id) return true;
            if (option.check != null && option.check.Count > 0) return true;
            // 普通选项没有失败阶段；既存失败数据必须展开给作者看，不能静默隐藏。
            return !hasMiniGame
                   && ((option.talkId2 != null
                        && option.talkId2.Any(value => value != 0))
                       || (option.effect2 != null
                           && option.effect2.Count > 0));
        }

        private void RelayoutLogicInspector(
            StoryGraphLogicInspectorPresentation presentation)
        {
            if (presentation == null) return;
            float introHeight = PreferredInspectorTextHeight(
                _inspectorLogicIntro, 54f, 104f);
            float previewHeight = PreferredInspectorTextHeight(
                _inspectorConditionPreview, 42f, 128f);
            StoryGraphLogicInspectorLayout layout =
                StoryGraphLogicInspectorLayout.Create(
                    presentation.ShowVisibilityCondition,
                    presentation.ShowOptionModeSelector,
                    presentation.ShowResultCondition,
                    presentation.ShowFailureRoute,
                    presentation.ShowSecondary,
                    introHeight, previewHeight);
            SetInspectorRect(
                _inspectorLogicIntro?.rectTransform,
                layout.IntroY, layout.IntroHeight);

            if (presentation.ShowOptionModeSelector)
            {
                SetInspectorRect(
                    _inspectorDirectOptionModeButton != null
                        ? (RectTransform)_inspectorDirectOptionModeButton.transform
                        : null,
                    layout.ModeY, layout.ModeHeight);
                SetInspectorRect(
                    _inspectorConditionalOptionModeButton != null
                        ? (RectTransform)_inspectorConditionalOptionModeButton.transform
                        : null,
                    layout.ModeY, layout.ModeHeight);
            }

            if (presentation.ShowVisibilityCondition)
            {
                SetInspectorRect(
                    _inspectorVisibilityConditionLabel?.rectTransform,
                    layout.VisibilityLabelY, 22f);
                SetInspectorRect(
                    _inspectorVisibilityConditionInput != null
                        ? (RectTransform)_inspectorVisibilityConditionInput.transform
                        : null,
                    layout.VisibilityInputY, 84f);
            }

            if (presentation.ShowResultCondition)
            {
                SetInspectorRect(
                    _inspectorResultConditionLabel?.rectTransform,
                    layout.ResultLabelY, 22f);
                SetInspectorRect(
                    _inspectorResultConditionInput != null
                        ? (RectTransform)_inspectorResultConditionInput.transform
                        : null,
                    layout.ResultInputY, 84f);
            }
            SetInspectorRect(
                _inspectorConditionPreview?.rectTransform,
                layout.ConditionPreviewY,
                layout.ConditionPreviewHeight);
            SetInspectorRect(
                _inspectorConditionHelpButton != null
                    ? (RectTransform)_inspectorConditionHelpButton.transform
                    : null,
                layout.HelpButtonY, 36f);
            SetInspectorRect(
                _inspectorLogicRoutesLabel?.rectTransform,
                layout.RoutesLabelY, 22f);
            SetInspectorRect(
                _inspectorSuccessRouteRow != null
                    ? (RectTransform)_inspectorSuccessRouteRow.transform
                    : null,
                layout.SuccessRouteY, 62f);
            if (presentation.ShowFailureRoute)
                SetInspectorRect(
                    _inspectorFailureRouteRow != null
                        ? (RectTransform)_inspectorFailureRouteRow.transform
                        : null,
                    layout.FailureRouteY, 62f);
            SetInspectorRect(
                _inspectorSuccessEffectsLabel?.rectTransform,
                layout.PrimaryLabelY, 22f);
            SetInspectorRect(
                _inspectorSuccessEffectsInput != null
                    ? (RectTransform)_inspectorSuccessEffectsInput.transform
                    : null,
                layout.PrimaryInputY, layout.PrimaryInputHeight);

            if (presentation.ShowSecondary)
            {
                SetInspectorRect(
                    _inspectorFailureEffectsLabel?.rectTransform,
                    layout.SecondaryLabelY, 22f);
                SetInspectorRect(
                    _inspectorFailureEffectsInput != null
                        ? (RectTransform)_inspectorFailureEffectsInput.transform
                        : null,
                    layout.SecondaryInputY,
                    layout.SecondaryInputHeight);
            }

            SetInspectorRect(
                _inspectorCopyLogicButton != null
                    ? (RectTransform)_inspectorCopyLogicButton.transform : null,
                layout.FooterY, 40f);
            SetInspectorRect(
                _inspectorPasteLogicButton != null
                    ? (RectTransform)_inspectorPasteLogicButton.transform : null,
                layout.FooterY, 40f);
            SetInspectorRect(
                _inspectorClearLogicButton != null
                    ? (RectTransform)_inspectorClearLogicButton.transform : null,
                layout.FooterY, 40f);
            _logicInspectorPageHeight = layout.PageHeight;
            RefreshInspectorContentHeight();
        }

        private void RefreshMiniGameInspectorPresentation(
            TalkCfg talk, OptionCfg option)
        {
            if (_inspectorMiniGameIntro == null) return;
            StoryGraphMiniGameInspectorPresentation presentation =
                StoryGraphMiniGameInspectorPresentation.Create(talk != null);
            _inspectorMiniGameIntro.text = presentation.Intro;
            _inspectorMiniGameIntro.fontSize = 12;
            if (_inspectorMiniGameLabel != null)
                _inspectorMiniGameLabel.text = presentation.InputLabel;
            SetInspectorPlaceholder(
                _inspectorMiniGameInput, presentation.InputPlaceholder);
            if (_inspectorMiniGameCommonLabel != null)
                _inspectorMiniGameCommonLabel.text = presentation.FlowLabel;
            if (_inspectorMiniGameCommonBody != null)
                _inspectorMiniGameCommonBody.text = presentation.FlowBody;
            if (_inspectorMiniGameSpecialLabel != null)
                _inspectorMiniGameSpecialLabel.text = presentation.SpecialLabel;
            if (_inspectorMiniGameSpecialBody != null)
                _inspectorMiniGameSpecialBody.text = presentation.SpecialBody;
            RelayoutMiniGameInspector();
        }

        private void RelayoutMiniGameInspector()
        {
            if (_inspectorMiniGameIntro == null
                || _inspectorMiniGameInput == null
                || _inspectorMiniGameSummary == null) return;
            _inspectorMiniGameSummary.text =
                StoryGraphInspectorText.CompactCharacters(
                    _inspectorMiniGameSummary.text, 190);
            if (_inspectorMiniGameFlowHint != null)
                _inspectorMiniGameFlowHint.text =
                    StoryGraphInspectorText.CompactCharacters(
                        _inspectorMiniGameFlowHint.text, 260);
            bool hasFlow = _inspectorMiniGameFlowHint != null
                           && !string.IsNullOrWhiteSpace(
                               _inspectorMiniGameFlowHint.text);
            float introHeight = PreferredInspectorTextHeight(
                _inspectorMiniGameIntro, 58f, 104f);
            float summaryHeight = PreferredInspectorTextHeight(
                _inspectorMiniGameSummary, 38f, 112f);
            float flowHeight = hasFlow
                ? PreferredInspectorTextHeight(
                    _inspectorMiniGameFlowHint, 38f, 154f)
                : 0f;
            float commonHeight = PreferredInspectorTextHeight(
                _inspectorMiniGameCommonBody, 72f, 146f);
            float specialHeight = PreferredInspectorTextHeight(
                _inspectorMiniGameSpecialBody, 72f, 146f);
            StoryGraphMiniGameInspectorLayout layout =
                StoryGraphMiniGameInspectorLayout.Create(
                    introHeight, summaryHeight, flowHeight,
                    commonHeight, specialHeight);

            SetInspectorRect(
                _inspectorMiniGameIntro.rectTransform,
                layout.IntroY, layout.IntroHeight);
            SetInspectorRect(
                _inspectorMiniGameLabel?.rectTransform,
                layout.InputLabelY, 22f);
            SetInspectorRect(
                (RectTransform)_inspectorMiniGameInput.transform,
                layout.InputY, 46f);
            SetInspectorRect(
                _inspectorMiniGameSummary.rectTransform,
                layout.SummaryY, layout.SummaryHeight);
            if (_inspectorMiniGameFlowHint != null)
            {
                _inspectorMiniGameFlowHint.gameObject.SetActive(hasFlow);
                SetInspectorRect(
                    _inspectorMiniGameFlowHint.rectTransform,
                    layout.FlowY, layout.FlowHeight);
            }
            SetInspectorRect(
                _inspectorMiniGameLookupButton != null
                    ? (RectTransform)_inspectorMiniGameLookupButton.transform
                    : null,
                layout.ButtonsY, 40f);
            SetInspectorRect(
                _inspectorClearMiniGameButton != null
                    ? (RectTransform)_inspectorClearMiniGameButton.transform
                    : null,
                layout.ButtonsY, 40f);
            SetInspectorRect(
                _inspectorMiniGameCommonLabel?.rectTransform,
                layout.CommonLabelY, 22f);
            SetInspectorRect(
                _inspectorMiniGameCommonBody?.rectTransform,
                layout.CommonBodyY, layout.CommonBodyHeight);
            SetInspectorRect(
                _inspectorMiniGameSpecialLabel?.rectTransform,
                layout.SpecialLabelY, 22f);
            SetInspectorRect(
                _inspectorMiniGameSpecialBody?.rectTransform,
                layout.SpecialBodyY, layout.SpecialBodyHeight);
            _miniGameInspectorPageHeight = layout.PageHeight;
            RefreshInspectorContentHeight();
        }

        private static float PreferredInspectorTextHeight(
            Text text, float minimum, float maximum)
        {
            if (text == null) return minimum;
            float preferred = text.preferredHeight;
            if (float.IsNaN(preferred) || float.IsInfinity(preferred))
                preferred = minimum;
            return Mathf.Clamp(preferred + 3f, minimum, maximum);
        }

        private static void SetInspectorRect(
            RectTransform rect, float y, float height)
        {
            if (rect == null) return;
            rect.anchoredPosition = new Vector2(
                rect.anchoredPosition.x, y);
            rect.sizeDelta = new Vector2(
                rect.sizeDelta.x, Math.Max(0f, height));
        }

        private static float TakeInspectorSlot(
            ref float cursor, float height, float bottomGap)
        {
            float top = cursor;
            cursor -= Math.Max(0f, height) + Math.Max(0f, bottomGap);
            return top;
        }

        private static void PositionInspectorButtonPair(
            Button[] buttons, int start, float y)
        {
            if (buttons == null) return;
            for (int i = start; i < start + 2 && i < buttons.Length; i++)
                if (buttons[i] != null)
                    SetInspectorRect(
                        (RectTransform)buttons[i].transform, y, 40f);
        }

        private static void PositionInspectorButtonTriple(
            Button[] buttons, int start, float y)
        {
            if (buttons == null) return;
            for (int i = start; i < start + 3 && i < buttons.Length; i++)
                if (buttons[i] != null)
                    SetInspectorRect(
                        (RectTransform)buttons[i].transform, y, 40f);
        }

        private void RelayoutInspectorHeader()
        {
            if (_inspectorInfo == null) return;
            _inspectorInfo.text = StoryGraphInspectorText.CompactCharacters(
                _inspectorInfo.text, 220);
            float infoHeight = PreferredInspectorTextHeight(
                _inspectorInfo, 60f, 168f);
            SetInspectorRect(_inspectorInfo.rectTransform, -42f, infoHeight);

            float tabsY = -42f - infoHeight - 6f;
            foreach (Button tab in new[]
            {
                _inspectorBasicTab, _inspectorCastTab, _inspectorStageTab,
                _inspectorLogicTab, _inspectorMiniGameTab, _inspectorAdvancedTab,
            })
            {
                if (tab != null)
                    SetInspectorRect((RectTransform)tab.transform, tabsY, 36f);
            }

            float pageY = tabsY - 46f;
            foreach (GameObject page in new[]
            {
                _inspectorBasicPage, _inspectorCastPage, _inspectorStagePage,
                _inspectorLogicPage, _inspectorMiniGamePage,
                _inspectorAdvancedPage,
            })
            {
                if (page == null) continue;
                RectTransform rect = (RectTransform)page.transform;
                rect.anchoredPosition = new Vector2(
                    rect.anchoredPosition.x, pageY);
            }
            _inspectorHeaderHeight = Math.Max(154f, 0f - pageY);
            RefreshInspectorContentHeight();
        }

        private void RelayoutBasicInspector()
        {
            if (_inspectorContentInput == null) return;
            float cursor = -4f;
            SetInspectorRect(
                _inspectorContentLabel?.rectTransform,
                TakeInspectorSlot(ref cursor, 22f, 2f), 22f);
            float contentHeight = _inspectorShowingHelp
                ? PreferredInspectorTextHeight(
                    _inspectorContentInput.textComponent, 122f, 280f) + 12f
                : 122f;
            SetInspectorRect(
                (RectTransform)_inspectorContentInput.transform,
                TakeInspectorSlot(ref cursor, contentHeight, 8f),
                contentHeight);
            LayoutLatexPreview(ref cursor);

            bool showPrimaryRow =
                (_inspectorSpeakerRow != null
                 && _inspectorSpeakerRow.activeSelf)
                || (_inspectorShowRow != null
                    && _inspectorShowRow.activeSelf)
                || (_inspectorTagRow != null
                    && _inspectorTagRow.activeSelf);
            if (showPrimaryRow)
            {
                float rowY = TakeInspectorSlot(ref cursor, 62f, 4f);
                foreach (GameObject row in new[]
                {
                    _inspectorSpeakerRow, _inspectorShowRow, _inspectorTagRow,
                })
                    if (row != null)
                        SetInspectorRect(
                            (RectTransform)row.transform, rowY, 62f);
            }

            bool showNextEvent = _inspectorNextEventRow != null
                                 && _inspectorNextEventRow.activeSelf;
            if (showNextEvent)
                SetInspectorRect(
                    (RectTransform)_inspectorNextEventRow.transform,
                    TakeInspectorSlot(ref cursor, 62f, 4f), 62f);

            bool showHint = showNextEvent
                            && _inspectorNextEventHint != null
                            && _inspectorNextEventHint.gameObject.activeSelf;
            if (showHint)
            {
                float hintHeight = PreferredInspectorTextHeight(
                    _inspectorNextEventHint, 48f, 136f);
                SetInspectorRect(
                    _inspectorNextEventHint.rectTransform,
                    TakeInspectorSlot(ref cursor, hintHeight, 12f),
                    hintHeight);
            }
            _basicInspectorPageHeight = Math.Max(300f, 0f - cursor);
            RefreshInspectorContentHeight();
        }

        private void BuildAdvancedInspectorPage(GameObject page)
        {
            _inspectorAdvancedLabel = CreateInspectorLabel(
                page, "完整原生配置 JSON", -4f);
            _inspectorJsonInput = CreateInspectorInput(
                page, "RawJson", -28f, 500f, true);
            _inspectorJsonInput.textComponent.fontSize = 12;
            _inspectorJsonInput.textComponent.lineSpacing = 1.05f;
            _inspectorJsonInput.onValueChanged.AddListener(OnInspectorJsonValueChanged);
            _inspectorJsonInput.onEndEdit.AddListener(OnInspectorJsonEndEdit);

            _inspectorAdvancedReloadButton = CreatePageButton(
                page, "重新载入草稿", 8f, -544f, 152f, RefreshInspector);

            _inspectorAdvancedWarning = CreateInspectorNote(page,
                "高级 JSON 可编辑条件及全部原生字段；每当文本形成完整合法 JSON 就立即写入。输入过程中的残缺 JSON 不修改草稿，失焦时会恢复最后一次有效内容。ID 仍不允许修改。",
                -592f, 72f, AccentOrange);
            _inspectorAdvancedWarning.fontSize = 12;
            RelayoutAdvancedInspector();
        }

        private void RelayoutAdvancedInspector()
        {
            if (_inspectorJsonInput == null) return;
            float cursor = -4f;
            SetInspectorRect(
                _inspectorAdvancedLabel?.rectTransform,
                TakeInspectorSlot(ref cursor, 22f, 6f), 22f);
            SetInspectorRect(
                (RectTransform)_inspectorJsonInput.transform,
                TakeInspectorSlot(ref cursor, 500f, 16f), 500f);
            SetInspectorRect(
                _inspectorAdvancedReloadButton != null
                    ? (RectTransform)_inspectorAdvancedReloadButton.transform
                    : null,
                TakeInspectorSlot(ref cursor, 40f, 8f), 40f);
            float warningHeight = PreferredInspectorTextHeight(
                _inspectorAdvancedWarning, 72f, 180f);
            SetInspectorRect(
                _inspectorAdvancedWarning?.rectTransform,
                TakeInspectorSlot(ref cursor, warningHeight, 16f),
                warningHeight);
            _advancedInspectorPageHeight = Math.Max(680f, 0f - cursor);
            RefreshInspectorContentHeight();
        }

        private Button CreatePageButton(
            GameObject parent, string label, float x, float y, float width,
            UnityEngine.Events.UnityAction action)
        {
            Button button = CreateToolbarButton(parent, label, x, y, width, action);
            Place((RectTransform)button.transform, 0f, 1f, 0f, 1f,
                x, y, width, 40f);
            Text text = button.GetComponentInChildren<Text>();
            if (text != null) text.fontSize = 13;
            return button;
        }

        private Text CreateInspectorNote(
            GameObject parent, string value, float y, float height, Color color)
        {
            Text text = CreateText(parent, "Note", SecondaryFontSize,
                FontStyle.Normal, color, TextAnchor.UpperLeft);
            text.text = value ?? string.Empty;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            Place(text.rectTransform, 0f, 1f, 0f, 1f,
                8f, y, InspectorWidth - 42f, height);
            return text;
        }

        private Button CreateActionTemplateButton(
            GameObject page, string label, float x, float y,
            int actionCode, float[] defaults)
        {
            return CreatePageButton(page, label, x, y, 182f,
                delegate { AppendRoleActionTemplate(actionCode, defaults); });
        }

        private Button CreateScreenTemplateButton(
            GameObject page, string label, float x, float y,
            int effectCode, float[] defaults, bool requiresParameter)
        {
            return CreatePageButton(page, label, x, y, 182f,
                delegate { SetScreenEffectTemplate(effectCode, defaults, requiresParameter); });
        }

        private void SwitchInspectorPage(InspectorPage page, bool resetScroll)
        {
            if (!InspectorPageAvailable(page)) page = InspectorPage.Basic;
            if (InspectorResourcePickerOpen && page != _inspectorPage)
                CloseInspectorResourcePicker();
            if (resetScroll && page != _inspectorPage)
                FinalizeFocusedInspectorInput();
            _inspectorPage = page;
            SetPageActive(_inspectorBasicPage, page == InspectorPage.Basic);
            SetPageActive(_inspectorCastPage, page == InspectorPage.Cast);
            SetPageActive(_inspectorStagePage, page == InspectorPage.Stage);
            SetPageActive(_inspectorLogicPage, page == InspectorPage.Logic);
            SetPageActive(_inspectorMiniGamePage, page == InspectorPage.MiniGame);
            SetPageActive(_inspectorAdvancedPage, page == InspectorPage.Advanced);

            SetCurrentTab(_inspectorBasicTab, page == InspectorPage.Basic);
            SetCurrentTab(_inspectorCastTab, page == InspectorPage.Cast);
            SetCurrentTab(_inspectorStageTab, page == InspectorPage.Stage);
            SetCurrentTab(_inspectorLogicTab, page == InspectorPage.Logic);
            SetCurrentTab(_inspectorMiniGameTab, page == InspectorPage.MiniGame);
            SetCurrentTab(_inspectorAdvancedTab, page == InspectorPage.Advanced);
            RefreshInspectorContentHeight();
            if (resetScroll && _inspectorScroll != null)
                _inspectorScroll.verticalNormalizedPosition = 1f;
        }

        private static void SetPageActive(GameObject page, bool active)
        {
            if (page != null) page.SetActive(active);
        }

        private static void SetCurrentTab(Button tab, bool current)
        {
            if (tab != null && tab.gameObject.activeSelf)
                tab.interactable = !current;
        }

        private void RefreshInspectorContentHeight()
        {
            if (_inspectorContent == null) return;
            _inspectorContent.sizeDelta = new Vector2(
                0f, _inspectorHeaderHeight
                    + InspectorPageHeight(_inspectorPage));
        }

        private float InspectorPageHeight(InspectorPage page)
        {
            switch (page)
            {
                case InspectorPage.Cast: return _castInspectorPageHeight;
                case InspectorPage.Stage: return _stageInspectorPageHeight;
                case InspectorPage.Logic:
                    return _logicInspectorPageHeight;
                case InspectorPage.MiniGame:
                    return _miniGameInspectorPageHeight;
                case InspectorPage.Advanced:
                    return _advancedInspectorPageHeight;
                default: return _basicInspectorPageHeight;
            }
        }

        private bool InspectorPageAvailable(InspectorPage page)
        {
            if (page == InspectorPage.Basic) return true;
            if (!string.IsNullOrEmpty(_selectedGroupId)) return false;
            if (_selectedNode == null || _selectedNodes.Count > 1
                || _selectedNode.SourceNode == null) return false;
            EvtStoryGraphNode source = _selectedNode.SourceNode;
            bool talk = source.Talk != null;
            bool option = source.Option != null;
            switch (page)
            {
                case InspectorPage.Cast:
                case InspectorPage.Stage:
                    return talk;
                case InspectorPage.Logic:
                case InspectorPage.MiniGame:
                case InspectorPage.Advanced:
                    return talk || option;
                default:
                    return true;
            }
        }

        private void RefreshPerformanceInspector()
        {
            if (_inspectorBasicPage == null) return;
            bool single = string.IsNullOrEmpty(_selectedGroupId)
                && _selectedNode != null && _selectedNodes.Count <= 1
                && _selectedNode.SourceNode != null;
            TalkCfg talk = single ? _selectedNode.SourceNode.Talk : null;
            OptionCfg option = single ? _selectedNode.SourceNode.Option : null;

            SetTabVisible(_inspectorCastTab, talk != null);
            SetTabVisible(_inspectorStageTab, talk != null);
            SetTabVisible(_inspectorLogicTab, talk != null || option != null);
            SetTabVisible(_inspectorMiniGameTab, talk != null || option != null);
            SetTabVisible(_inspectorAdvancedTab, talk != null || option != null);
            if (!InspectorPageAvailable(_inspectorPage))
                _inspectorPage = InspectorPage.Basic;

            if (talk != null)
            {
                EnsurePerformanceResourceNames();
                StoryGraphPerformanceData data = StoryGraphPerformanceData.FromTalk(talk);
                SetInspectorText(_inspectorRoleIdsInput,
                    StoryGraphPerformanceCodec.FormatIntList(data.RoleIds));
                SetInspectorText(_inspectorHighlightsInput,
                    StoryGraphPerformanceCodec.FormatIntList(data.Highlights));
                SetInspectorText(_inspectorRoleActionsInput,
                    StoryGraphPerformanceCodec.FormatNested(data.RoleActions));
                if (_inspectorRoleNamesHint != null)
                    _inspectorRoleNamesHint.text = BuildCastRoleHints(
                        data.RoleIds, data.Highlights);
                RefreshRoleActionDescription(data.RoleActions);

                SetInspectorText(_inspectorBackgroundInput,
                    data.BackgroundId.ToString(CultureInfo.InvariantCulture));
                SetInspectorText(_inspectorAudioInput,
                    data.AudioId.ToString(CultureInfo.InvariantCulture));
                SetInspectorText(_inspectorScreenEffectInput,
                    StoryGraphPerformanceCodec.FormatFloatList(data.ScreenEffect));
                SetInspectorText(_inspectorVocalsInput,
                    StoryGraphPerformanceCodec.FormatFloatList(data.Vocals));
                RefreshStageDescriptions(data);
                StoryGraphLogicData logic =
                    StoryGraphLogicData.FromTalk(talk);
                SetInspectorText(
                    _inspectorVisibilityConditionInput, string.Empty);
                SetInspectorText(_inspectorResultConditionInput,
                    StoryGraphPerformanceCodec.FormatNestedDouble(
                        logic.ResultConditions));
                SetInspectorText(_inspectorSuccessRouteInput,
                    StoryGraphPerformanceCodec.FormatIntList(
                        logic.SuccessTargets));
                SetInspectorText(_inspectorFailureRouteInput,
                    StoryGraphPerformanceCodec.FormatIntList(
                        logic.FailureTargets));
                SetInspectorText(_inspectorSuccessEffectsInput,
                    StoryGraphPerformanceCodec.FormatNested(
                        logic.SuccessEffects));
                SetInspectorText(_inspectorFailureEffectsInput,
                    StoryGraphPerformanceCodec.FormatNested(
                        logic.FailureEffects));
                SetInspectorText(_inspectorMiniGameInput,
                    FormatMiniGame(talk.miniGame));
                RefreshLogicInspectorPresentation(talk, null);
                RefreshMiniGameInspectorPresentation(talk, null);
            }
            else if (option != null)
            {
                ClearCastAndStageInspectorText();
                StoryGraphLogicData logic = StoryGraphLogicData.FromOption(option);
                SetInspectorText(_inspectorVisibilityConditionInput,
                    StoryGraphPerformanceCodec.FormatNestedDouble(
                        logic.VisibilityConditions));
                SetInspectorText(_inspectorResultConditionInput,
                    StoryGraphPerformanceCodec.FormatNestedDouble(
                        logic.ResultConditions));
                SetInspectorText(_inspectorSuccessRouteInput,
                    StoryGraphPerformanceCodec.FormatIntList(
                        logic.SuccessTargets));
                SetInspectorText(_inspectorFailureRouteInput,
                    StoryGraphPerformanceCodec.FormatIntList(
                        logic.FailureTargets));
                SetInspectorText(_inspectorSuccessEffectsInput,
                    StoryGraphPerformanceCodec.FormatNested(logic.SuccessEffects));
                SetInspectorText(_inspectorFailureEffectsInput,
                    StoryGraphPerformanceCodec.FormatNested(logic.FailureEffects));
                SetInspectorText(_inspectorMiniGameInput,
                    FormatMiniGame(option.miniGame));
                RefreshLogicInspectorPresentation(null, option);
                RefreshMiniGameInspectorPresentation(null, option);
            }
            else
            {
                ClearCastAndStageInspectorText();
                SetInspectorText(
                    _inspectorVisibilityConditionInput, string.Empty);
                SetInspectorText(
                    _inspectorResultConditionInput, string.Empty);
                SetInspectorText(
                    _inspectorSuccessRouteInput, string.Empty);
                SetInspectorText(
                    _inspectorFailureRouteInput, string.Empty);
                SetInspectorText(_inspectorSuccessEffectsInput, string.Empty);
                SetInspectorText(_inspectorFailureEffectsInput, string.Empty);
                SetInspectorText(_inspectorMiniGameInput, string.Empty);
            }
            UpdateMiniGameDescriptionFromInput();
            SwitchInspectorPage(_inspectorPage, false);
        }

        private static void SetTabVisible(Button tab, bool visible)
        {
            if (tab != null) tab.gameObject.SetActive(visible);
        }

        private void ClearCastAndStageInspectorText()
        {
            SetInspectorText(_inspectorRoleIdsInput, string.Empty);
            SetInspectorText(_inspectorHighlightsInput, string.Empty);
            SetInspectorText(_inspectorRoleActionsInput, string.Empty);
            SetInspectorText(_inspectorActionActorInput, string.Empty);
            SetInspectorText(_inspectorActionArgsInput, string.Empty);
            if (_inspectorRoleNamesHint != null) _inspectorRoleNamesHint.text = string.Empty;
            if (_inspectorRoleActionsDescription != null)
                _inspectorRoleActionsDescription.text = string.Empty;
            SetInspectorText(_inspectorBackgroundInput, string.Empty);
            SetInspectorText(_inspectorAudioInput, string.Empty);
            SetInspectorText(_inspectorScreenEffectInput, string.Empty);
            SetInspectorText(_inspectorVocalsInput, string.Empty);
            SetInspectorText(_inspectorScreenArgsInput, string.Empty);
            if (_inspectorResourceHint != null) _inspectorResourceHint.text = string.Empty;
            if (_inspectorScreenDescription != null)
                _inspectorScreenDescription.text = string.Empty;
        }

        private void SetPerformanceInspectorInteractable(bool value)
        {
            TalkCfg talk;
            OptionCfg option;
            bool target = TryGetPerformanceTarget(out talk, out option);
            bool talkEnabled = value && target && talk != null;
            bool logicEnabled = value && target && (talk != null || option != null);
            SetInputsInteractable(talkEnabled,
                _inspectorRoleIdsInput, _inspectorHighlightsInput,
                _inspectorRoleActionsInput, _inspectorActionActorInput,
                _inspectorActionArgsInput, _inspectorBackgroundInput,
                _inspectorAudioInput, _inspectorScreenEffectInput,
                _inspectorVocalsInput, _inspectorScreenArgsInput);
            if (_inspectorBackgroundLookupButton != null)
                _inspectorBackgroundLookupButton.interactable = talkEnabled;
            if (_inspectorAudioLookupButton != null)
                _inspectorAudioLookupButton.interactable = talkEnabled;
            if (_inspectorCgLookupButton != null)
                _inspectorCgLookupButton.interactable = talkEnabled;
            SetInputsInteractable(logicEnabled,
                _inspectorVisibilityConditionInput,
                _inspectorResultConditionInput,
                _inspectorSuccessRouteInput,
                _inspectorFailureRouteInput,
                _inspectorSuccessEffectsInput, _inspectorFailureEffectsInput,
                _inspectorMiniGameInput);
            if (_inspectorConditionHelpButton != null)
                _inspectorConditionHelpButton.interactable = logicEnabled;
            bool conditionalOption = option != null
                && IsConditionalOptionEditor(
                    option, option.miniGame != null
                            && option.miniGame.Count > 0);
            if (_inspectorDirectOptionModeButton != null
                && _inspectorDirectOptionModeButton.gameObject.activeSelf)
                _inspectorDirectOptionModeButton.interactable =
                    logicEnabled && option != null && conditionalOption;
            if (_inspectorConditionalOptionModeButton != null
                && _inspectorConditionalOptionModeButton.gameObject.activeSelf)
                _inspectorConditionalOptionModeButton.interactable =
                    logicEnabled && option != null && !conditionalOption;
            SetInputsInteractable(
                value && target && option != null,
                _inspectorNextEventInput);
            if (_inspectorEventLookupButton != null)
                _inspectorEventLookupButton.interactable =
                    value && target && option != null;
            if (_inspectorClearMiniGameButton != null)
                _inspectorClearMiniGameButton.interactable = logicEnabled;
            if (_inspectorMiniGameLookupButton != null)
                _inspectorMiniGameLookupButton.interactable = logicEnabled;
        }

        private static void SetInputsInteractable(bool value, params InputField[] inputs)
        {
            if (inputs == null) return;
            foreach (InputField input in inputs)
                if (input != null) input.interactable = value;
        }

        private IEnumerable<InputField> PerformanceInspectorInputs()
        {
            yield return _inspectorRoleIdsInput;
            yield return _inspectorHighlightsInput;
            yield return _inspectorRoleActionsInput;
            yield return _inspectorActionActorInput;
            yield return _inspectorActionArgsInput;
            yield return _inspectorBackgroundInput;
            yield return _inspectorAudioInput;
            yield return _inspectorScreenEffectInput;
            yield return _inspectorVocalsInput;
            yield return _inspectorScreenArgsInput;
            yield return _inspectorVisibilityConditionInput;
            yield return _inspectorResultConditionInput;
            yield return _inspectorSuccessRouteInput;
            yield return _inspectorFailureRouteInput;
            yield return _inspectorSuccessEffectsInput;
            yield return _inspectorFailureEffectsInput;
            yield return _inspectorMiniGameInput;
            yield return _inspectorNextEventInput;
        }

        private bool IsPerformanceInspectorInput(InputField input)
        {
            return input != null && PerformanceInspectorInputs().Any(
                candidate => ReferenceEquals(candidate, input));
        }

        private string FormatSpeakerRoleIds(IEnumerable<int> roleIds)
        {
            List<int> ids = roleIds != null
                ? new List<int>(roleIds)
                : new List<int>();
            if (ids.Count == 0) return "-1（旁白）";
            return string.Join(", ", ids.Select(FormatSpeakerRoleId).ToArray());
        }

        private string FormatSpeakerRoleId(int id)
        {
            if (id == -1) return "-1（旁白）";
            if (id == 0) return "0（主角）";
            string name;
            return _personNames.TryGetValue(id, out name)
                   && !string.IsNullOrWhiteSpace(name)
                ? id + "（" + name.Trim() + "）"
                : id.ToString(CultureInfo.InvariantCulture);
        }

        private bool TryParseSpeakerRoleIds(
            string text, out List<int> roleIds, out string error)
        {
            roleIds = new List<int>();
            error = null;
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                roleIds.Add(-1);
                return true;
            }
            string normalized = value.Replace('，', ',')
                .Replace('；', ';').Replace('\r', '\n');
            string[] tokens = normalized.Split(
                new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token.Length == 0) continue;
                int bracket = token.IndexOfAny(new[] { '（', '(' });
                string numeric = bracket > 0
                    ? token.Substring(0, bracket).Trim()
                    : token;
                int id;
                if (!int.TryParse(numeric, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out id))
                {
                    if (string.Equals(token, "旁白", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(token, "未指定", StringComparison.OrdinalIgnoreCase))
                        id = -1;
                    else if (string.Equals(token, "主角", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(token, "玩家", StringComparison.OrdinalIgnoreCase))
                        id = 0;
                    else
                    {
                        List<KeyValuePair<int, string>> exact = _personNames
                            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value)
                                && string.Equals(pair.Value.Trim(), token,
                                    StringComparison.OrdinalIgnoreCase))
                            .Take(5).ToList();
                        List<KeyValuePair<int, string>> matches = exact.Count > 0
                            ? exact
                            : _personNames.Where(pair =>
                                    !string.IsNullOrWhiteSpace(pair.Value)
                                    && pair.Value.IndexOf(token,
                                        StringComparison.OrdinalIgnoreCase) >= 0)
                                .Take(5).ToList();
                        if (matches.Count != 1)
                        {
                            error = matches.Count > 1
                                ? "人物名称“" + token + "”匹配多个 ID："
                                  + string.Join("、", matches.Select(pair =>
                                      pair.Key + "（" + pair.Value + "）").ToArray())
                                  + "；请填写明确 ID。"
                                : "找不到对话角色“" + token
                                  + "”；请填写 -1、0、人物 ID 或唯一名称。";
                            return false;
                        }
                        id = matches[0].Key;
                    }
                }
                roleIds.Add(id);
            }
            if (roleIds.Count == 0) roleIds.Add(-1);
            if (roleIds.Count > 1 && roleIds.Contains(-1))
            {
                error = "旁白不能与主角或其他人物同时作为说话人。";
                return false;
            }
            if (roleIds.Count != roleIds.Distinct().Count())
            {
                error = "对话角色中存在重复人物，请去掉重复项。";
                return false;
            }
            return true;
        }

        private void OnInspectorSpeakerValueChanged(string unused)
        {
            if (_settingInspectorText || !_editMode || _editSession == null) return;
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null) return;

            List<int> roleIds;
            string error;
            // 名称逐字输入时会短暂处于“不存在/有歧义”状态；这些中间态只保留
            // 在输入框，不修改草稿也不刷 Toast，失焦时再给出明确说明。
            if (!TryParseSpeakerRoleIds(
                    _inspectorSpeakerInput.text, out roleIds, out error)) return;

            if (_inspectorLiveEditKey == null
                || !ReferenceEquals(_inspectorLiveEditTarget, talk)
                || !_inspectorLiveEditKey.StartsWith(
                    "inspector-speaker:", StringComparison.Ordinal))
            {
                EndInspectorLiveEdit();
                _inspectorLiveEditTarget = talk;
                _inspectorLiveEditKey = "inspector-speaker:"
                    + (++_inspectorLiveEditSerial).ToString();
            }

            bool historyRecorded;
            string message;
            if (!_editSession.TryUpdateTalkSpeakerLive(
                    talk, roleIds, _inspectorLiveEditKey,
                    out historyRecorded, out message)) return;
            if (historyRecorded)
            {
                _editActionTimeline.Push(false);
                _editRedoTimeline.Clear();
                _workspaceRedo.Clear();
            }

            _inspectorBasicPreviewDirty = true;
            RefreshLiveNodePreview(talk);
            SetInspectorText(_inspectorRoleIdsInput,
                StoryGraphPerformanceCodec.FormatIntList(talk.roleIds));
            if (_inspectorRoleNamesHint != null)
                _inspectorRoleNamesHint.text = BuildCastRoleHints(
                    talk.roleIds, talk.highlights);
            RelayoutCastInspector();
            _editStatus = message + " 同一次连续输入只占一条撤销记录。";
            UpdateEditControls();
            UpdateStatusBar();
        }

        private void OnInspectorSpeakerEndEdit(string unused)
        {
            if (_settingInspectorText || !_editMode) return;
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null) return;
            List<int> roleIds;
            string error;
            bool valid = TryParseSpeakerRoleIds(
                _inspectorSpeakerInput.text, out roleIds, out error);
            bool changed = _inspectorBasicPreviewDirty;
            EndInspectorLiveEdit();
            RefreshInspectorJsonFromSelection();
            _inspectorBasicPreviewDirty = false;
            if (!valid)
            {
                // 逐字输入允许短暂的不存在/歧义状态，但失焦后不要让无效文本
                // 继续伪装成草稿值；恢复成最后一次真正写入的 roleIds。
                SetInspectorText(_inspectorSpeakerInput,
                    FormatSpeakerRoleIds(talk.roleIds));
                SetEditFeedback(error
                    + " 已恢复显示草稿中最后一次有效的对话角色。", false);
                return;
            }
            SetEditFeedback((changed
                    ? "对话角色已实时写入剧情草稿："
                    : "当前对话角色：")
                + FormatSpeakerRoleIds(talk.roleIds)
                + (changed ? "；Ctrl+Z 可一次撤销本次连续输入。" : string.Empty),
                false);
        }

        private static string FormatMiniGame(IEnumerable<double> values)
        {
            if (values == null) return string.Empty;
            return string.Join(", ", values.Select(value =>
                value.ToString("R", CultureInfo.InvariantCulture)).ToArray());
        }

        private static bool TryParseMiniGameText(
            string text, out List<double> values, out string error)
        {
            values = new List<double>();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            string normalized = (text ?? string.Empty)
                .Replace('，', ',').Replace('；', ',')
                .Replace(';', ',').Replace('|', ',')
                .Replace('\r', ',').Replace('\n', ',');
            string[] tokens = normalized.Split(
                new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                double value;
                if (!double.TryParse(tokens[i], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value)
                    || double.IsNaN(value) || double.IsInfinity(value))
                {
                    error = "小游戏第 " + (i + 1)
                            + " 项不是有限数字：" + tokens[i];
                    return false;
                }
                values.Add(value);
            }
            return true;
        }

        private void UpdateMiniGameDescriptionFromInput()
        {
            if (_inspectorMiniGameSummary == null
                || _inspectorMiniGameFlowHint == null
                || _inspectorMiniGameInput == null) return;
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option))
            {
                _inspectorMiniGameSummary.text =
                    "请先在剧情图中选择一条对话或一个选项。";
                _inspectorMiniGameFlowHint.text = string.Empty;
                RelayoutMiniGameInspector();
                return;
            }

            List<double> values;
            string error;
            if (!TryParseMiniGameText(
                    _inspectorMiniGameInput.text, out values, out error))
            {
                _inspectorMiniGameSummary.text =
                    "设置格式有误。这里只能填写数字，多个值请用逗号隔开。\n"
                    + error;
                _inspectorMiniGameSummary.color = AccentOrange;
                _inspectorMiniGameFlowHint.text = "尚未保存，请修改后再离开输入框。";
                RelayoutMiniGameInspector();
                return;
            }
            if (values.Count == 0)
            {
                if (_inspectorMiniGameLabel != null)
                    _inspectorMiniGameLabel.text = "② 按提示补充玩法设置";
                _inspectorMiniGameSummary.text =
                    "尚未添加小游戏。点击上方“① 选择 / 更换小游戏”开始。";
                _inspectorMiniGameSummary.color = BodyTextColor;
                TalkCfg parent = option != null && _selectedNode != null
                                 && _selectedNode.SourceNode != null
                    ? _selectedNode.SourceNode.LocateTalk
                    : null;
                if (parent != null && parent.miniGame != null
                    && parent.miniGame.Count > 0)
                {
                    int parentGameId;
                    string parentError;
                    string parentName = MiniGameUtil.TryGetGameId(
                            parent.miniGame, out parentGameId, out parentError)
                        ? "“" + MiniGameUtil.GameName(parentGameId) + "”"
                        : "一个尚未配置完整的小游戏";
                    _inspectorMiniGameFlowHint.text =
                        "这个选项会沿用上级对话的" + parentName
                        + "。玩家选择后仍会进入该小游戏，这个选项自己的结果连线不会执行。"
                        + "\n若要改成另一个小游戏，可直接在这里选择。";
                }
                else
                {
                    _inspectorMiniGameFlowHint.text = talk != null
                        ? "没有小游戏时，这条对话仍按“下一句 / 选项”继续。"
                        : "没有小游戏时，这个选项仍按普通“结果”继续。";
                }
                RelayoutMiniGameInspector();
                return;
            }

            int gameId;
            if (!MiniGameUtil.TryGetGameId(values, out gameId, out error))
            {
                _inspectorMiniGameSummary.text = error;
                _inspectorMiniGameSummary.color = AccentOrange;
                _inspectorMiniGameFlowHint.text = "尚未保存，请修改小游戏编号。";
                RelayoutMiniGameInspector();
                return;
            }
            string gameName = MiniGameUtil.GameName(gameId);
            if (_inspectorMiniGameLabel != null)
            {
                _inspectorMiniGameLabel.text =
                    MiniGameUtil.NeedParamIds.Contains(gameId)
                        ? "② 补充“" + StoryGraphInspectorText.Ellipsize(
                            gameName, 18) + "”的玩法设置"
                        : "② 当前玩法（无需额外设置）";
            }
            string warning = MiniGameUtil.Validate(values, talk != null);
            bool supported = (talk != null
                    ? MiniGameUtil.TalkSupportedIds
                    : MiniGameUtil.OptionSupportedIds)
                .Contains(gameId);
            string saveState = string.IsNullOrEmpty(warning)
                ? "✓ 设置完整，已保存到剧情草稿。"
                : !supported || MiniGameUtil.ParamJumpIds.Contains(gameId)
                    ? "⚠ 尚未保存：" + warning
                    : "⚠ 还没填完整，暂未保存。请按上一行提示补齐。";
            _inspectorMiniGameSummary.text = "已选择“" + gameName
                + "”（编号 " + gameId + "）\n"
                + MiniGameUtil.ParamHint(gameId) + "\n"
                + saveState;
            _inspectorMiniGameSummary.color = string.IsNullOrEmpty(warning)
                ? BodyTextColor : AccentOrange;
            _inspectorMiniGameFlowHint.text = BuildMiniGameFlowHint(
                talk, option, values, gameId);
            RelayoutMiniGameInspector();
        }

        private static string BuildMiniGameFlowHint(
            TalkCfg talk, OptionCfg option, List<double> values, int gameId)
        {
            if (MiniGameUtil.ParamJumpIds.Contains(gameId))
            {
                List<int> targets;
                string error;
                if (!MiniGameUtil.TryGetParamJumpTargets(
                        values, out targets, out error))
                    return "⚠ " + error;
                string labels = string.Join("\n", targets.Select((id, index) =>
                    (gameId == 16 ? "成功" + index + "次" : "连对" + index + "题")
                    + " → 对话 " + id).ToArray());
                return "小游戏结束后的剧情：\n" + labels
                       + "\n这些玩法按成绩直接跳转，画布上的普通成功 / 失败出口不会执行。";
            }

            if (talk != null)
            {
                bool win = talk.nextTalk != null && talk.nextTalk.Any(id => id != 0);
                bool lose = talk.nextTalk2 != null && talk.nextTalk2.Any(id => id != 0);
                bool options = talk.option != null && talk.option.Any(id => id != 0);
                string flow = "小游戏结束后的剧情：成功 → “成功”出口；失败 → “失败”出口。";
                if (!win && !lose)
                    flow += "\n⚠ 两个出口都还没连接，小游戏结束后将无法继续剧情。";
                else if (!win)
                    flow += "\n⚠ 尚未连接成功出口。";
                else if (!lose)
                    flow += "\n⚠ 尚未连接失败出口，玩家失败后可能卡住。";
                if (options)
                    flow += "\n⚠ 这条对话还有选项：玩家选完选项后才进入小游戏；选项原本的结果连线会被覆盖。";
                return flow;
            }

            bool optionWin = option != null && option.talkId != null
                             && option.talkId.Any(id => id != 0);
            bool optionLose = option != null && option.talkId2 != null
                              && option.talkId2.Any(id => id != 0);
            string optionFlow =
                "小游戏结束后的剧情：成功 → “成功”出口；失败 → “失败”出口。";
            if (!optionWin) optionFlow += "\n⚠ 尚未连接成功出口。";
            if (!optionLose && gameId != 29)
                optionFlow += "\n⚠ 尚未连接失败出口，玩家失败后可能卡住。";
            if (gameId == 29)
                optionFlow += "\n大头贴只使用成功出口，不需要连接失败出口。";
            return optionFlow;
        }

        private void ClearMiniGameInspector()
        {
            if (_inspectorMiniGameInput == null) return;
            SetInspectorText(_inspectorMiniGameInput, string.Empty);
            OnMiniGameInspectorValueChanged(string.Empty);
            OnMiniGameInspectorEndEdit(string.Empty);
        }

        private bool TryGetPerformanceTarget(out TalkCfg talk, out OptionCfg option)
        {
            talk = null;
            option = null;
            if (_editSession == null || !string.IsNullOrEmpty(_selectedGroupId)
                || _selectedNode == null || _selectedNodes.Count > 1
                || _selectedNode.SourceNode == null) return false;
            talk = _selectedNode.SourceNode.Talk;
            option = _selectedNode.SourceNode.Option;
            return talk != null || option != null;
        }

        private string EnsureInspectorPageLiveEdit(object target, string prefix)
        {
            if (target == null || string.IsNullOrEmpty(prefix)) return null;
            bool sameTarget = ReferenceEquals(_inspectorLiveEditTarget, target)
                              || Equals(_inspectorLiveEditTarget, target);
            if (_inspectorLiveEditKey == null || !sameTarget
                || !_inspectorLiveEditKey.StartsWith(
                    prefix + ":", StringComparison.Ordinal))
            {
                EndInspectorLiveEdit();
                _inspectorLiveEditTarget = target;
                _inspectorLiveEditKey = prefix + ":"
                    + (++_inspectorLiveEditSerial).ToString();
            }
            return _inspectorLiveEditKey;
        }

        private void RecordInspectorPageLiveHistory(bool historyRecorded)
        {
            if (!historyRecorded) return;
            _editActionTimeline.Push(false);
            _editRedoTimeline.Clear();
            _workspaceRedo.Clear();
        }

        private bool TryReadCastInspector(
            TalkCfg talk, out StoryGraphPerformanceData data, out string error)
        {
            data = talk != null
                ? StoryGraphPerformanceData.FromTalk(talk)
                : null;
            error = null;
            return data != null
                && StoryGraphPerformanceCodec.TryParseIntList(
                    _inspectorRoleIdsInput.text, "对话角色", out data.RoleIds, out error)
                && StoryGraphPerformanceCodec.TryParseIntList(
                    _inspectorHighlightsInput.text, "高亮人物", out data.Highlights, out error)
                && StoryGraphPerformanceCodec.TryParseNested(
                    _inspectorRoleActionsInput.text, "人物动作", 2,
                    out data.RoleActions, out error);
        }

        private bool TryReadStageInspector(
            TalkCfg talk, out StoryGraphPerformanceData data, out string error)
        {
            data = talk != null
                ? StoryGraphPerformanceData.FromTalk(talk)
                : null;
            error = null;
            if (data == null) return false;
            EnsurePerformanceResourceNames();
            return TryParseNamedResourceId(_inspectorBackgroundInput.text,
                       _backgroundNames, "背景", out data.BackgroundId, out error)
                   && TryParseNamedResourceId(_inspectorAudioInput.text,
                       _audioNames, "声音", out data.AudioId, out error)
                   && StoryGraphPerformanceCodec.TryParseFloatList(
                       _inspectorScreenEffectInput.text, "屏幕效果",
                       out data.ScreenEffect, out error)
                   && StoryGraphPerformanceCodec.TryParseFloatList(
                       _inspectorVocalsInput.text, "语音参数",
                       out data.Vocals, out error);
        }

        private bool TryReadLogicInspector(
            out StoryGraphLogicData logic, out string error)
        {
            error = null;
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option))
            {
                logic = new StoryGraphLogicData();
                error = "请先选择一条对话或一个选项。";
                return false;
            }
            bool isTalk = talk != null;
            logic = isTalk
                ? StoryGraphLogicData.FromTalk(talk)
                : StoryGraphLogicData.FromOption(option);
            if (!isTalk
                && !StoryGraphPerformanceCodec.TryParseNestedDouble(
                    _inspectorVisibilityConditionInput.text,
                    "显示条件", 2,
                    out logic.VisibilityConditions, out error))
                return false;
            if (!StoryGraphPerformanceCodec.TryParseNestedDouble(
                    _inspectorResultConditionInput.text,
                    isTalk ? "下一句判断" : "选择后判断", 2,
                    out logic.ResultConditions, out error)
                || !StoryGraphPerformanceCodec.TryParseIntList(
                    _inspectorSuccessRouteInput.text,
                    "成立去向", out logic.SuccessTargets, out error)
                || !StoryGraphPerformanceCodec.TryParseIntList(
                    _inspectorFailureRouteInput.text,
                    "不成立去向", out logic.FailureTargets, out error)
                || !StoryGraphPerformanceCodec.TryParseNested(
                       _inspectorSuccessEffectsInput.text,
                       isTalk ? "本句结束效果" : "条件成立效果", 1,
                       out logic.SuccessEffects, out error)
                || !StoryGraphPerformanceCodec.TryParseNested(
                       _inspectorFailureEffectsInput.text,
                       isTalk ? "小游戏失败效果" : "条件不成立效果", 1,
                       out logic.FailureEffects, out error))
                return false;
            return logic.TryValidate(out error);
        }

        private void UpdateLogicDescriptionFromInput()
        {
            if (_inspectorConditionPreview == null) return;
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option))
            {
                _inspectorConditionPreview.text =
                    "选择一条对话或一个选项后，这里会显示条件和剧情去向。";
                _inspectorConditionPreview.color = SubtitleColor;
                return;
            }

            StoryGraphLogicData logic;
            string error;
            bool valid = TryReadLogicInspector(out logic, out error);
            if (!valid)
            {
                _inspectorConditionPreview.text = "尚未保存：" + error;
                _inspectorConditionPreview.color = AccentOrange;
            }
            else
            {
                bool optionConditional = option != null
                    && IsConditionalOptionEditor(
                        option, option.miniGame != null
                                && option.miniGame.Count > 0);
                string conditionText = talk != null
                    ? "当前判断："
                      + StoryGraphConditionText.Describe(
                          logic.ResultConditions)
                    : "显示条件："
                      + StoryGraphConditionText.Describe(
                          logic.VisibilityConditions)
                      + (optionConditional
                          ? "\n选择后判断："
                            + StoryGraphConditionText.Describe(
                                logic.ResultConditions)
                          : "\n选择后：直接执行，不做结果判断");
                _inspectorConditionPreview.text = conditionText + "\n"
                    + BuildLogicRouteSummary(
                        talk, option, logic, optionConditional);
                _inspectorConditionPreview.color = BodyTextColor;
            }

            bool hasMiniGame = talk != null
                ? talk.miniGame != null && talk.miniGame.Count > 0
                : option != null && option.miniGame != null
                  && option.miniGame.Count > 0;
            bool hasFailureData = talk != null && talk.effect2 != null
                                  && talk.effect2.Count > 0;
            RelayoutLogicInspector(
                StoryGraphLogicInspectorPresentation.Create(
                    talk != null, hasMiniGame, hasFailureData,
                    IsConditionalOptionEditor(option, hasMiniGame)));
        }

        private static string BuildLogicRouteSummary(
            TalkCfg talk, OptionCfg option, StoryGraphLogicData logic,
            bool optionConditional)
        {
            IList<double> miniGame = talk != null
                ? talk.miniGame : option != null ? option.miniGame : null;
            if (MiniGameUtil.IsParamJump(miniGame))
                return "剧情去向：由小游戏设置中的成绩路线决定；下方成立 / 不成立去向暂不执行。";
            string success = DescribeLogicTargets(
                logic != null ? logic.SuccessTargets : null, false);
            if (option != null && !optionConditional
                && (miniGame == null || miniGame.Count == 0))
                return "剧情去向：选择后 → " + success;
            string failure = DescribeLogicTargets(
                logic != null ? logic.FailureTargets : null, talk != null);
            if (miniGame != null && miniGame.Count > 0)
                return "剧情去向：小游戏成功 → " + success
                       + "；小游戏失败 → " + failure;
            string summary = "剧情去向：成立 → " + success
                           + "；不成立 → " + failure;
            return summary;
        }

        private static string DescribeLogicTargets(
            IList<int> values, bool emptyFallsBack)
        {
            if (values == null || values.Count == 0)
                return emptyFallsBack ? "沿用成立去向" : "未连接";
            if (values.Count == 1)
            {
                if (values[0] <= 0)
                    return emptyFallsBack ? "沿用成立去向" : "未连接";
                return "对话 " + values[0];
            }
            string result = "男 → "
                + (values[0] > 0 ? "对话 " + values[0] : "未连接")
                + "，女 → "
                + (values[1] > 0 ? "对话 " + values[1] : "未连接");
            if (values.Count > 2)
                result += "（另有 " + (values.Count - 2)
                          + " 项，原版运行时不会读取）";
            return result;
        }

        private void RestorePerformancePageAfterInvalidInput(string error)
        {
            EndInspectorLiveEdit();
            RefreshPerformanceInspector();
            RefreshInspectorJsonFromSelection();
            SetEditFeedback((error ?? "当前输入不完整")
                + " 已恢复显示草稿中最后一次有效配置。", false);
        }

        private void OnCastInspectorValueChanged(string unused)
        {
            if (_settingInspectorText || !_editMode || _editSession == null) return;
            UpdateRoleActionTranslationFromInput();
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null) return;
            StoryGraphPerformanceData data;
            string error;
            if (!TryReadCastInspector(talk, out data, out error)) return;
            string liveKey = EnsureInspectorPageLiveEdit(talk, "inspector-cast");
            bool historyRecorded;
            string message;
            if (!_editSession.TryUpdateTalkPerformanceLive(
                    talk, data, liveKey,
                    out historyRecorded, out message)) return;
            RecordInspectorPageLiveHistory(historyRecorded);
            RefreshLiveNodePreview(talk);
            SetInspectorText(_inspectorSpeakerInput,
                FormatSpeakerRoleIds(talk.roleIds));
            if (_inspectorRoleNamesHint != null)
                _inspectorRoleNamesHint.text = BuildCastRoleHints(
                    talk.roleIds, talk.highlights);
            RefreshRoleActionDescription(data.RoleActions);
            _editStatus = message + " 同一次连续输入只占一条撤销记录。";
            UpdateEditControls();
            UpdateStatusBar();
        }

        private void OnCastInspectorEndEdit(string unused)
        {
            if (_settingInspectorText || !_editMode) return;
            TalkCfg talk;
            OptionCfg option;
            StoryGraphPerformanceData data;
            string error = null;
            bool valid = TryGetPerformanceTarget(out talk, out option)
                         && talk != null
                         && TryReadCastInspector(talk, out data, out error);
            EndInspectorLiveEdit();
            if (!valid)
            {
                RestorePerformancePageAfterInvalidInput(error);
                return;
            }
            RefreshInspectorJsonFromSelection();
            SetEditFeedback("人物演出已实时写入剧情草稿；Ctrl+Z 可一次撤销本次连续输入。", false);
        }

        private void OnStageInspectorValueChanged(string unused)
        {
            if (_settingInspectorText || !_editMode || _editSession == null) return;
            UpdateScreenDescriptionFromInput();
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null) return;
            StoryGraphPerformanceData data;
            string error;
            if (!TryReadStageInspector(talk, out data, out error)) return;
            int oldCgFlow = ScreenEffectFlowMarker(talk.screenEffect);
            int newCgFlow = ScreenEffectFlowMarker(data.ScreenEffect);
            string liveKey = EnsureInspectorPageLiveEdit(talk, "inspector-stage");
            bool historyRecorded;
            string message;
            if (!_editSession.TryUpdateTalkPerformanceLive(
                    talk, data, liveKey,
                    out historyRecorded, out message)) return;
            RecordInspectorPageLiveHistory(historyRecorded);
            if (oldCgFlow != newCgFlow)
                RefreshGraph(false);
            else
                RefreshLiveNodePreview(talk);
            RefreshStageDescriptions(data);
            _editStatus = message + " 同一次连续输入只占一条撤销记录。";
            UpdateEditControls();
            UpdateStatusBar();
        }

        private void OnStageInspectorEndEdit(string unused)
        {
            if (_settingInspectorText || !_editMode) return;
            TalkCfg talk;
            OptionCfg option;
            StoryGraphPerformanceData data;
            string error = null;
            bool valid = TryGetPerformanceTarget(out talk, out option)
                         && talk != null
                         && TryReadStageInspector(talk, out data, out error);
            EndInspectorLiveEdit();
            if (!valid)
            {
                RestorePerformancePageAfterInvalidInput(error);
                return;
            }
            RefreshInspectorJsonFromSelection();
            SetEditFeedback("画面与声音已实时写入剧情草稿；Ctrl+Z 可一次撤销本次连续输入。", false);
        }

        private void OnLogicInspectorValueChanged(string unused)
        {
            if (_settingInspectorText || !_editMode || _editSession == null) return;
            UpdateLogicDescriptionFromInput();
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option)) return;
            StoryGraphLogicData before = talk != null
                ? StoryGraphLogicData.FromTalk(talk)
                : StoryGraphLogicData.FromOption(option);
            StoryGraphLogicData logic;
            string error;
            if (!TryReadLogicInspector(out logic, out error)) return;
            object target = (object)talk ?? option;
            string liveKey = EnsureInspectorPageLiveEdit(target, "inspector-logic");
            bool historyRecorded;
            string message;
            bool changed;
            if (talk != null)
            {
                changed = _editSession.TryUpdateTalkLogicLive(
                    talk, logic, liveKey,
                    out historyRecorded, out message);
            }
            else
            {
                changed = _editSession.TryUpdateOptionLogicLive(
                    option, logic, liveKey,
                    out historyRecorded, out message);
            }
            if (!changed) return;
            bool graphMeaningChanged = !LogicGraphMeaningEquals(
                before, logic);
            if (graphMeaningChanged)
            {
                StoryGraphEditNodeKind kind = talk != null
                    ? StoryGraphEditNodeKind.Talk
                    : StoryGraphEditNodeKind.Option;
                int id = talk != null ? talk.id
                    : _editSession.FindOptionKey(option);
                LayoutPositionSnapshot layout = CaptureLayoutPositions();
                RefreshEditGraph(kind, id, message, false,
                    historyRecorded, layout);
            }
            else
            {
                RecordInspectorPageLiveHistory(historyRecorded);
                RefreshLiveNodePreview(target);
            }
            _editStatus = message + " 同一次连续输入只占一条撤销记录。";
            UpdateEditControls();
            UpdateStatusBar();
        }

        private void OnLogicInspectorEndEdit(string unused)
        {
            if (_settingInspectorText || !_editMode) return;
            TalkCfg talk;
            OptionCfg option;
            StoryGraphLogicData logic;
            string error = null;
            bool valid = TryGetPerformanceTarget(out talk, out option)
                         && TryReadLogicInspector(out logic, out error);
            EndInspectorLiveEdit();
            if (!valid)
            {
                RestorePerformancePageAfterInvalidInput(error);
                return;
            }
            RefreshInspectorJsonFromSelection();
            RefreshLogicInspectorPresentation(talk, option);
            SetEditFeedback(
                "条件、剧情去向与效果已写入剧情草稿；Ctrl+Z 可一次撤销本次连续输入。",
                false);
        }

        private static bool LogicGraphMeaningEquals(
            StoryGraphLogicData left, StoryGraphLogicData right)
        {
            if (left == null || right == null) return false;
            return string.Equals(
                       StoryGraphPerformanceCodec.FormatNestedDouble(
                           left.VisibilityConditions),
                       StoryGraphPerformanceCodec.FormatNestedDouble(
                           right.VisibilityConditions),
                       StringComparison.Ordinal)
                   && string.Equals(
                       StoryGraphPerformanceCodec.FormatNestedDouble(
                           left.ResultConditions),
                       StoryGraphPerformanceCodec.FormatNestedDouble(
                           right.ResultConditions),
                       StringComparison.Ordinal)
                   && (left.SuccessTargets ?? new List<int>()).SequenceEqual(
                       right.SuccessTargets ?? new List<int>())
                   && (left.FailureTargets ?? new List<int>()).SequenceEqual(
                       right.FailureTargets ?? new List<int>());
        }

        private void OnMiniGameInspectorValueChanged(string unused)
        {
            if (_settingInspectorText || !_editMode || _editSession == null) return;
            UpdateMiniGameDescriptionFromInput();
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option)) return;
            List<double> values;
            string error;
            if (!TryParseMiniGameText(
                    _inspectorMiniGameInput.text, out values, out error)) return;
            string warning = MiniGameUtil.Validate(values, talk != null);
            if (!string.IsNullOrEmpty(warning)) return;

            object target = (object)talk ?? option;
            string liveKey = EnsureInspectorPageLiveEdit(target, "inspector-minigame");
            bool historyRecorded;
            string message;
            bool changed;
            StoryGraphEditNodeKind kind;
            int id;
            if (talk != null)
            {
                changed = _editSession.TryUpdateTalkMiniGameLive(
                    talk, values, liveKey,
                    out historyRecorded, out message);
                kind = StoryGraphEditNodeKind.Talk;
                id = talk.id;
            }
            else
            {
                changed = _editSession.TryUpdateOptionMiniGameLive(
                    option, values, liveKey,
                    out historyRecorded, out message);
                kind = StoryGraphEditNodeKind.Option;
                id = _editSession.FindOptionKey(option);
            }
            if (!changed) return;
            LayoutPositionSnapshot layout = CaptureLayoutPositions();
            RefreshEditGraph(kind, id, message, false,
                historyRecorded, layout);
            _editStatus = message + " 同一次连续输入只占一条撤销记录。";
            UpdateStatusBar();
        }

        private void OnMiniGameInspectorEndEdit(string unused)
        {
            if (_settingInspectorText || !_editMode) return;
            TalkCfg talk;
            OptionCfg option;
            List<double> values;
            string error = null;
            bool valid = TryGetPerformanceTarget(out talk, out option)
                         && TryParseMiniGameText(
                             _inspectorMiniGameInput.text, out values, out error)
                         && string.IsNullOrEmpty(
                             MiniGameUtil.Validate(values, talk != null));
            EndInspectorLiveEdit();
            if (!valid)
            {
                SetInspectorText(_inspectorMiniGameInput,
                    FormatMiniGame(talk != null ? talk.miniGame : option != null
                        ? option.miniGame : null));
                UpdateMiniGameDescriptionFromInput();
                SetEditFeedback((error ?? "小游戏配置不完整或不受当前节点支持")
                    + "；已恢复最后一次有效配置。", false);
                return;
            }
            RefreshInspectorJsonFromSelection();
            RefreshLogicInspectorPresentation(talk, option);
            SetEditFeedback("小游戏已实时写入剧情草稿；Ctrl+Z 可一次撤销本次连续输入。", false);
        }

        private void OnOptionNextEventValueChanged(string unused)
        {
            if (_settingInspectorText || !_editMode || _editSession == null)
                return;
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option)
                || option == null) return;
            EnsurePerformanceResourceNames();
            int nextEventId;
            string error;
            if (!TryParseNamedResourceId(
                    _inspectorNextEventInput.text,
                    _eventNames, "下一事件",
                    out nextEventId, out error)
                || nextEventId < 0)
            {
                if (_inspectorNextEventHint != null)
                {
                    _inspectorNextEventHint.text = error
                        ?? "下一事件 ID 只能为 0 或正整数。";
                    _inspectorNextEventHint.color = AccentOrange;
                    RelayoutBasicInspector();
                }
                return;
            }

            string liveKey = EnsureInspectorPageLiveEdit(
                option, "inspector-next-event");
            bool historyRecorded;
            string message;
            if (!_editSession.TryUpdateOptionNextEventLive(
                    option, nextEventId, liveKey,
                    out historyRecorded, out message))
            {
                RefreshOptionNextEventHint(option);
                return;
            }
            RecordInspectorPageLiveHistory(historyRecorded);
            LayoutPositionSnapshot layout = CaptureLayoutPositions();
            int key = _editSession.FindOptionKey(option);
            RefreshEditGraph(
                StoryGraphEditNodeKind.Option, key,
                message, false, historyRecorded, layout);
            RefreshOptionNextEventHint(option);
        }

        private void OnOptionNextEventEndEdit(string unused)
        {
            if (_settingInspectorText || !_editMode) return;
            TalkCfg talk;
            OptionCfg option;
            int nextEventId;
            string error = null;
            bool valid = TryGetPerformanceTarget(out talk, out option)
                         && option != null
                         && TryParseNamedResourceId(
                             _inspectorNextEventInput.text,
                             _eventNames, "下一事件",
                             out nextEventId, out error)
                         && nextEventId >= 0;
            EndInspectorLiveEdit();
            if (!valid)
            {
                SetInspectorText(
                    _inspectorNextEventInput,
                    option != null
                        ? option.nextEvtId.ToString(
                            CultureInfo.InvariantCulture)
                        : string.Empty);
                RefreshOptionNextEventHint(option);
                SetEditFeedback((error
                    ?? "下一事件 ID 只能为 0 或正整数")
                    + "；已恢复最后一次有效配置。", false);
                return;
            }
            RefreshInspectorJsonFromSelection();
            RefreshOptionNextEventHint(option);
            SetEditFeedback(
                "选项的后备下一事件已实时写入剧情草稿；"
                + "图中的事件跳转边已同步。", false);
        }

        private void RefreshOptionNextEventHint(OptionCfg option)
        {
            if (_inspectorNextEventHint == null) return;
            if (option == null)
            {
                _inspectorNextEventHint.text = string.Empty;
                RelayoutBasicInspector();
                return;
            }
            _inspectorNextEventHint.color = SubtitleColor;
            string current = option.nextEvtId > 0
                ? FormatNamedResource(
                    option.nextEvtId, _eventNames, "无后备事件")
                : "0（不跳转）";
            bool miniGameOverride = option.miniGame != null
                                    && option.miniGame.Count > 0;
            if (!miniGameOverride && _selectedNode?.SourceNode != null)
            {
                TalkCfg parent = _selectedNode.SourceNode.LocateTalk;
                EvtCfg parentEvent = _selectedNode.SourceNode.ParentEvent;
                miniGameOverride = (parent?.miniGame != null
                                    && parent.miniGame.Count > 0)
                                   || (parentEvent?.miniGame != null
                                       && parentEvent.miniGame.Count > 0);
            }
            _inspectorNextEventHint.text = "当前：" + current
                + "\n仅在当前条件没有 >1 的 Talk 结果时执行"
                + (miniGameOverride
                    ? "；⚠ 当前被小游戏流程覆盖。"
                    : "。");
            _inspectorNextEventHint.text =
                StoryGraphInspectorText.CompactCharacters(
                    _inspectorNextEventHint.text, 180);
            RelayoutBasicInspector();
        }

        private void ApplyTalkPerformance(TalkCfg talk, StoryGraphPerformanceData data)
        {
            EndInspectorLiveEdit();
            string message;
            if (_editSession.TryUpdateTalkPerformance(talk, data, out message))
                RefreshEditGraph(StoryGraphEditNodeKind.Talk, talk.id, message, false);
            else
                SetEditFeedback(message, true);
        }

        private void SelectConditionalOptionMode()
        {
            EndInspectorLiveEdit();
            TalkCfg talk;
            OptionCfg option;
            if (_editSession == null
                || !TryGetPerformanceTarget(out talk, out option)
                || option == null)
            {
                SetEditFeedback("请选择一个选项后切换分支模式。", false);
                return;
            }
            int key = _editSession.FindOptionKey(option);
            _optionConditionalDraftId = key != int.MinValue
                ? key : option.id;
            _optionDirectModeConfirmId = int.MinValue;
            _optionDirectModeConfirmUntil = 0f;
            RefreshLogicInspectorPresentation(null, option);
            SetEditFeedback(
                "已展开“根据条件分支”：填写判断条件后，成立与不成立路线才会分别执行。",
                false);
        }

        private void SelectDirectOptionMode()
        {
            EndInspectorLiveEdit();
            TalkCfg talk;
            OptionCfg option;
            if (_editSession == null
                || !TryGetPerformanceTarget(out talk, out option)
                || option == null)
            {
                SetEditFeedback("请选择一个选项后切换进入模式。", false);
                return;
            }

            int key = _editSession.FindOptionKey(option);
            StoryGraphLogicData logic = StoryGraphLogicData.FromOption(option);
            bool hasMiniGame = option.miniGame != null
                               && option.miniGame.Count > 0;
            bool clearsFailureData = !hasMiniGame
                && ((logic.FailureTargets != null
                     && logic.FailureTargets.Count > 0)
                    || (logic.FailureEffects != null
                        && logic.FailureEffects.Count > 0));
            bool changesStoredLogic =
                logic.ResultConditions != null
                && logic.ResultConditions.Count > 0
                || clearsFailureData;
            if (changesStoredLogic
                && (_optionDirectModeConfirmId != key
                    || Time.unscaledTime > _optionDirectModeConfirmUntil))
            {
                _optionDirectModeConfirmId = key;
                _optionDirectModeConfirmUntil =
                    Time.unscaledTime + ConfirmSeconds;
                SetEditFeedback(
                    hasMiniGame
                        ? "再次点击“直接进入剧情”，将清空选择后的判断；小游戏成功 / 失败配置会保留。"
                        : "再次点击“直接进入剧情”，将清空判断条件、不成立路线和不成立效果。",
                    false);
                return;
            }

            _optionDirectModeConfirmId = int.MinValue;
            _optionDirectModeConfirmUntil = 0f;
            _optionConditionalDraftId = int.MinValue;
            if (!changesStoredLogic)
            {
                RefreshLogicInspectorPresentation(null, option);
                SetEditFeedback(
                    "当前为“直接进入剧情”：玩家选择后执行效果并进入唯一结果路线。",
                    false);
                return;
            }

            logic.ResultConditions.Clear();
            if (!hasMiniGame)
            {
                logic.FailureTargets.Clear();
                logic.FailureEffects.Clear();
            }
            ApplyOptionLogic(option, logic);
        }

        private void ApplyOptionLogic(OptionCfg option, StoryGraphLogicData logic)
        {
            EndInspectorLiveEdit();
            int key = _editSession.FindOptionKey(option);
            string message;
            if (_editSession.TryUpdateOptionLogic(option, logic, out message))
                RefreshEditGraph(StoryGraphEditNodeKind.Option, key, message, false);
            else
                SetEditFeedback(message, true);
        }

        private void ApplyTalkLogic(TalkCfg talk, StoryGraphLogicData logic)
        {
            EndInspectorLiveEdit();
            string message;
            if (_editSession.TryUpdateTalkLogic(talk, logic, out message))
                RefreshEditGraph(
                    StoryGraphEditNodeKind.Talk, talk.id, message, false);
            else
                SetEditFeedback(message, true);
        }

        private void AppendRoleActionTemplate(int actionCode, float[] defaults)
        {
            int actor;
            if (_inspectorActionActorInput == null
                || !int.TryParse(_inspectorActionActorInput.text.Trim(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out actor))
            {
                SetEditFeedback("请先填写模板人物 ID。", true);
                return;
            }
            List<float> args;
            string error;
            if (string.IsNullOrWhiteSpace(_inspectorActionArgsInput.text))
                args = defaults != null ? new List<float>(defaults) : new List<float>();
            else if (!StoryGraphPerformanceCodec.TryParseFloatList(
                         _inspectorActionArgsInput.text, "动作模板参数", out args, out error))
            {
                SetEditFeedback(error, true);
                return;
            }
            var row = new List<float> { actor, actionCode };
            row.AddRange(args);
            string text = StoryGraphPerformanceCodec.FormatFloatList(row);
            string old = _inspectorRoleActionsInput.text;
            SetInspectorText(_inspectorRoleActionsInput,
                string.IsNullOrWhiteSpace(old) ? text : old.TrimEnd() + "\n" + text);
            UpdateRoleActionTranslationFromInput();
            OnCastInspectorValueChanged(string.Empty);
            OnCastInspectorEndEdit(string.Empty);
        }

        private void SetScreenEffectTemplate(
            int effectCode, float[] defaults, bool requiresParameter)
        {
            List<float> args;
            string error;
            bool acceptsParameters = requiresParameter
                || (defaults != null && defaults.Length > 0);
            if (!acceptsParameters)
            {
                // 清除滤镜、关闭 CG 等无参模板不能继承上一次填写的 CG ID。
                args = new List<float>();
            }
            else if (string.IsNullOrWhiteSpace(_inspectorScreenArgsInput.text))
            {
                if (requiresParameter)
                {
                    SetEditFeedback("显示 CG 前请在“模板参数 / CG 编号”填写 CG ID。", true);
                    return;
                }
                args = defaults != null ? new List<float>(defaults) : new List<float>();
            }
            else if (!StoryGraphPerformanceCodec.TryParseFloatList(
                         _inspectorScreenArgsInput.text, "屏幕模板参数", out args, out error))
            {
                SetEditFeedback(error, true);
                return;
            }
            var values = new List<float> { effectCode };
            values.AddRange(args);
            SetInspectorText(_inspectorScreenEffectInput,
                StoryGraphPerformanceCodec.FormatFloatList(values));
            if (_inspectorScreenDescription != null)
                _inspectorScreenDescription.text = BuildScreenEffectDescription(values);
            OnStageInspectorValueChanged(string.Empty);
            OnStageInspectorEndEdit(string.Empty);
        }

        private void UpdateScreenDescriptionFromInput()
        {
            if (_inspectorScreenDescription == null
                || _inspectorScreenEffectInput == null) return;
            List<float> values;
            string error;
            if (!StoryGraphPerformanceCodec.TryParseFloatList(
                    _inspectorScreenEffectInput.text, "屏幕效果", out values, out error))
            {
                _inspectorScreenDescription.text = error;
                RelayoutStageInspector();
                return;
            }
            _inspectorScreenDescription.text =
                StoryGraphInspectorText.CompactCharacters(
                    BuildScreenEffectDescription(values), 260);
            RelayoutStageInspector();
        }

        private void UpdateRoleActionTranslationFromInput()
        {
            List<List<float>> actions;
            string error;
            if (!StoryGraphPerformanceCodec.TryParseNested(
                    _inspectorRoleActionsInput.text, "人物动作", 2,
                    out actions, out error))
            {
                if (_inspectorRoleActionsDescription != null)
                    _inspectorRoleActionsDescription.text = error;
                RelayoutCastInspector();
                return;
            }
            RefreshRoleActionDescription(actions);
        }

        private void RefreshRoleActionDescription(List<List<float>> actions)
        {
            if (_inspectorRoleActionsDescription == null) return;
            Dictionary<int, PersonCfg> persons = BuildPersonConfigsForTranslation();
            string translated = TalkActionTranslator.Translate(actions, persons);
            string faces = BuildFaceHint(actions);
            string description =
                (string.IsNullOrEmpty(translated) ? "（无人物动作）" : translated)
                + (string.IsNullOrEmpty(faces) ? string.Empty : "\n" + faces);
            description = StoryGraphInspectorText.CompactCharacters(
                StoryGraphInspectorText.CompactLines(description, 12), 360);
            _inspectorRoleActionsDescription.text = description;
            RelayoutCastInspector();
        }

        private Dictionary<int, PersonCfg> BuildPersonConfigsForTranslation()
        {
            var result = new Dictionary<int, PersonCfg>();
            if (Cfg.PersonCfgMap != null)
                foreach (KeyValuePair<int, PersonCfg> pair in Cfg.PersonCfgMap)
                    result[pair.Key] = pair.Value;
            foreach (KeyValuePair<int, string> pair in _personNames)
            {
                // 当前 Mod 的人物配置应覆盖同 ID 全局名称；只构造翻译用临时对象，
                // 绝不修改 Cfg.PersonCfgMap 中的共享配置。
                result[pair.Key] = new PersonCfg { name = pair.Value };
            }
            return result;
        }

        private string BuildCastRoleHints(
            IEnumerable<int> roleIds,
            IEnumerable<int> highlightIds)
        {
            return BuildPersonIdHint(
                       "说话人", roleIds,
                       "未填写 roleIds（按旁白/未指定处理）")
                   + "\n"
                   + BuildPersonIdHint(
                       "高亮人物", highlightIds,
                       "未单独指定（由说话人与人物动作决定）");
        }

        private string BuildPersonIdHint(
            string label,
            IEnumerable<int> ids,
            string emptyText)
        {
            List<string> parts = (ids ?? Enumerable.Empty<int>())
                .Select(FormatSpeakerRoleId)
                .ToList();
            string joined = StoryGraphInspectorText.JoinBounded(
                parts, 8, "人");
            return parts.Count > 0
                ? StoryGraphInspectorText.CompactCharacters(
                    label + "：" + joined, 160)
                : label + "：" + emptyText + "。";
        }

        private string BuildFaceHint(IEnumerable<List<float>> actions)
        {
            var parts = new List<string>();
            if (actions != null)
            {
                foreach (List<float> row in actions)
                {
                    if (row == null || row.Count < 3 || (int)row[1] != 3000) continue;
                    int id = (int)row[2];
                    string name;
                    if (_faceNames.TryGetValue(id, out name) && !string.IsNullOrEmpty(name))
                        parts.Add(id + "（" + name + "）");
                }
            }
            string joined = StoryGraphInspectorText.JoinBounded(
                parts.Distinct(), 8, "项");
            return parts.Count > 0
                ? StoryGraphInspectorText.CompactCharacters(
                    "表情资源：" + joined, 160)
                : string.Empty;
        }

        private void RefreshStageDescriptions(StoryGraphPerformanceData data)
        {
            if (_inspectorResourceHint != null)
                _inspectorResourceHint.text =
                    StoryGraphInspectorText.CompactCharacters(
                        "合法配置会立即热应用｜背景："
                        + FormatNamedResource(
                            data.BackgroundId, _backgroundNames, "沿用/无")
                        + "\n声音："
                        + (data.AudioId == -1
                            ? "-1（剧情结束时切回日常 BGM）"
                            : FormatNamedResource(
                                data.AudioId, _audioNames, "本句不切换")),
                        160);
            if (_inspectorScreenDescription != null)
                _inspectorScreenDescription.text =
                    StoryGraphInspectorText.CompactCharacters(
                        BuildScreenEffectDescription(data.ScreenEffect), 260);
            RelayoutStageInspector();
        }

        private string BuildScreenEffectDescription(List<float> values)
        {
            string text = TalkActionTranslator.TranslateScreenEffect(values)
                          ?? "无屏幕效果";
            int code = values != null && values.Count > 0 ? (int)values[0] : 0;
            if (values != null && values.Count > 1
                && (code == 4015 || code == 4019))
            {
                int cgId = (int)values[1];
                text += "；资源：" + FormatNamedResource(cgId, _cgNames, "未知 CG");
            }
            bool skipped = (code == 4015 || code == 4016
                            || code == 4017 || code == 4019)
                           && _selectedNode != null
                           && _selectedNode.SourceNode?.Talk != null
                           && string.IsNullOrWhiteSpace(
                               _selectedNode.SourceNode.Talk.content);
            if (skipped)
                text += "\n⚠ 本句正文为空，原版会直接跳过整句；这条 CG 指令不会执行。";
            else if (code == 4015 || code == 4019)
                text += "\n⚠ 会持续显示；请在所有结束分支前安排“关闭 CG（4017）”。";
            else if (code == 4016)
                text += "\n⚠ 漫画会持续显示；请在所有结束分支前安排“关闭 CG（4017）”。";
            else if (code == 4017)
                text += "\n✓ 会结束此前显示的 CG、迷你 CG 或漫画。";
            return text;
        }

        private static int ScreenEffectFlowMarker(List<float> values)
        {
            if (values == null || values.Count == 0) return 0;
            int code = (int)values[0];
            if (code == 4015 || code == 4016 || code == 4019) return 1;
            return code == 4017 ? 2 : 0;
        }

        private void EnsurePerformanceResourceNames()
        {
            string root = _workspaceModRoot ?? string.Empty;
            string signature = BuildPerformanceResourceSignature(root);
            if (string.Equals(signature, _performanceResourceSignature,
                    StringComparison.Ordinal)) return;

            // 先完整加载到临时字典，再一次性替换缓存。任何异常都不会留下“半套”
            // 名称并错误记录命中键；下次刷新仍会重试。
            try
            {
                var backgrounds = new Dictionary<int, string>();
                var cgs = new Dictionary<int, string>();
                var audios = new Dictionary<int, string>();
                var audioBrowseIds = new HashSet<int>();
                var events = new Dictionary<int, string>();
                var faces = new Dictionary<int, string>();
                foreach (KeyValuePair<int, BgCfg> pair in
                    EvtPreviewConfigLoader.LoadBackgrounds(root))
                    backgrounds[pair.Key] = pair.Value != null
                        ? pair.Value.name : null;
                foreach (KeyValuePair<int, CGCfg> pair in
                    EvtPreviewConfigLoader.LoadCgs(root))
                    cgs[pair.Key] = pair.Value != null
                        ? pair.Value.name : null;
                foreach (KeyValuePair<int, AudioCfg> pair in
                    EvtPreviewConfigLoader.LoadAudios(root))
                {
                    if (pair.Value == null) continue;
                    audios[pair.Key] = !string.IsNullOrEmpty(pair.Value.name)
                        ? pair.Value.name : pair.Value.url;
                    // 与原版 ModSelectAudioView 一致，只把有资源地址的背景音乐
                    // （type=1）放进查阅列表；已有其它类型引用仍保留名称解释。
                    if (StoryGraphResourcePickerLogic.IsAudioBrowsable(
                        pair.Value.type, pair.Value.url))
                        audioBrowseIds.Add(pair.Key);
                }
                foreach (KeyValuePair<int, EvtCfg> pair in
                    EvtPreviewConfigLoader.LoadEvents(root))
                {
                    if (pair.Value == null) continue;
                    string name = FirstNonBlank(
                        pair.Value.title,
                        pair.Value.content,
                        pair.Value.desc);
                    if (string.IsNullOrWhiteSpace(name))
                        name = "事件 " + pair.Key;
                    name = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    if (name.Length > 44)
                        name = name.Substring(0, 44) + "…";
                    events[pair.Key] = name;
                }
                foreach (KeyValuePair<int, ModFaceCfg> pair in
                    EvtPreviewConfigLoader.LoadFaces(root))
                    faces[pair.Key] = pair.Value != null
                        ? pair.Value.name : null;

                ReplaceNameCache(_backgroundNames, backgrounds);
                ReplaceNameCache(_cgNames, cgs);
                ReplaceNameCache(_audioNames, audios);
                ReplaceNameCache(_eventNames, events);
                ReplaceNameCache(_faceNames, faces);
                _audioBrowseIds.Clear();
                foreach (int id in audioBrowseIds) _audioBrowseIds.Add(id);
                _performanceResourceSignature = signature;
            }
            catch (Exception e)
            {
                _performanceResourceSignature = null;
                Plugin.Log?.LogWarning("[StoryGraph.Performance] 加载资源名称失败：" + e.Message);
            }
        }

        private static string BuildPerformanceResourceSignature(string root)
        {
            string language = LocalizationMgr.Lang ?? string.Empty;
            var parts = new List<string>
            {
                root ?? string.Empty,
                language,
            };
            string directory = Path.Combine(
                root ?? string.Empty, "Cfgs", language);
            foreach (string fileName in new[]
            {
                "BgCfg.json", "CGCfg.json", "AudioCfg.json", "EvtCfg.json",
                "ModFaceCfg.json",
            })
            {
                string path = Path.Combine(directory, fileName);
                try
                {
                    if (!File.Exists(path))
                    {
                        parts.Add(fileName + ":missing");
                        continue;
                    }
                    var info = new FileInfo(path);
                    parts.Add(fileName + ":" + info.Length + ":"
                              + info.LastWriteTimeUtc.Ticks);
                }
                catch (Exception e)
                {
                    // 文件状态本身无法读取时也纳入键，并让加载器给出原有回退日志。
                    parts.Add(fileName + ":error:" + e.GetType().Name);
                }
            }
            return string.Join("|", parts.ToArray());
        }

        private static void ReplaceNameCache(
            IDictionary<int, string> target, IDictionary<int, string> source)
        {
            target.Clear();
            foreach (KeyValuePair<int, string> pair in source)
                target[pair.Key] = pair.Value;
        }

        private static string FirstNonBlank(params string[] values)
        {
            if (values == null) return null;
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return null;
        }

        private static string FormatNamedResource(
            int id, IDictionary<int, string> names, string zeroText)
        {
            if (id == 0) return "0（" + zeroText + "）";
            string name;
            return names != null && names.TryGetValue(id, out name)
                   && !string.IsNullOrWhiteSpace(name)
                ? id + "（" + name.Trim() + "）"
                : id + "（未找到名称）";
        }

        private static bool TryParseNamedResourceId(
            string text, IDictionary<int, string> names, string label,
            out int id, out string error)
        {
            id = 0;
            error = null;
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0) return true;
            int bracket = value.IndexOfAny(new[] { '（', '(', ' ' });
            string numeric = bracket > 0 ? value.Substring(0, bracket).Trim() : value;
            if (int.TryParse(numeric, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out id)) return true;

            List<KeyValuePair<int, string>> exact = (names
                ?? new Dictionary<int, string>())
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value)
                    && string.Equals(pair.Value.Trim(), value,
                        StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1)
            {
                id = exact[0].Key;
                return true;
            }
            List<KeyValuePair<int, string>> matches = (names
                ?? new Dictionary<int, string>())
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value)
                    && pair.Value.IndexOf(value,
                        StringComparison.OrdinalIgnoreCase) >= 0).Take(4).ToList();
            if (matches.Count == 1)
            {
                id = matches[0].Key;
                return true;
            }
            if (matches.Count > 1)
            {
                error = label + "名称“" + value + "”匹配多个配置："
                    + string.Join("、", matches.Select(pair =>
                        pair.Key + "（" + pair.Value + "）").ToArray())
                    + "；请改填明确 ID。";
                return false;
            }
            error = label + "必须是整数 ID，或当前 Mod / 全局配置中的唯一名称：" + value;
            return false;
        }

        private void CopyCastPerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null)
            {
                SetEditFeedback("请选择一个对话节点后复制人物演出。", true);
                return;
            }
            _castClipboard = StoryGraphPerformanceData.FromTalk(talk);
            SetEditFeedback("已复制人物、动作和高亮；本会话内可粘贴到其它对话。", false);
        }

        private void PasteCastPerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null)
            {
                SetEditFeedback("请选择一个对话节点后粘贴人物演出。", true);
                return;
            }
            if (_castClipboard == null)
            {
                SetEditFeedback("本会话还没有复制人物演出。", true);
                return;
            }
            StoryGraphPerformanceData data = StoryGraphPerformanceData.FromTalk(talk);
            data.RoleIds = StoryGraphPerformanceData.Clone(_castClipboard.RoleIds);
            data.RoleActions = StoryGraphPerformanceData.CloneNested(_castClipboard.RoleActions);
            data.Highlights = StoryGraphPerformanceData.Clone(_castClipboard.Highlights);
            ApplyTalkPerformance(talk, data);
        }

        private void ClearCastPerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null)
            {
                SetEditFeedback("请选择一个对话节点后清空人物演出。", true);
                return;
            }
            StoryGraphPerformanceData data = StoryGraphPerformanceData.FromTalk(talk);
            data.RoleIds.Clear();
            data.RoleActions.Clear();
            data.Highlights.Clear();
            ApplyTalkPerformance(talk, data);
        }

        private void CopyStagePerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null)
            {
                SetEditFeedback("请选择一个对话节点后复制画面声音。", true);
                return;
            }
            _stageClipboard = StoryGraphPerformanceData.FromTalk(talk);
            SetEditFeedback("已复制背景、屏幕效果、声音和语音参数。", false);
        }

        private void PasteStagePerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null)
            {
                SetEditFeedback("请选择一个对话节点后粘贴画面声音。", true);
                return;
            }
            if (_stageClipboard == null)
            {
                SetEditFeedback("本会话还没有复制画面声音。", true);
                return;
            }
            StoryGraphPerformanceData data = StoryGraphPerformanceData.FromTalk(talk);
            data.BackgroundId = _stageClipboard.BackgroundId;
            data.AudioId = _stageClipboard.AudioId;
            data.ScreenEffect = StoryGraphPerformanceData.Clone(_stageClipboard.ScreenEffect);
            data.Vocals = StoryGraphPerformanceData.Clone(_stageClipboard.Vocals);
            ApplyTalkPerformance(talk, data);
        }

        private void ClearStagePerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null)
            {
                SetEditFeedback("请选择一个对话节点后清空画面声音。", true);
                return;
            }
            StoryGraphPerformanceData data = StoryGraphPerformanceData.FromTalk(talk);
            data.BackgroundId = 0;
            data.AudioId = 0;
            data.ScreenEffect.Clear();
            data.Vocals.Clear();
            ApplyTalkPerformance(talk, data);
        }

        private void CopyWholePerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null)
            {
                SetEditFeedback("请选择一个对话节点后复制整套演出。", true);
                return;
            }
            _wholePerformanceClipboard = StoryGraphPerformanceData.FromTalk(talk);
            SetEditFeedback("已复制整套人物、画面声音和逻辑效果演出。", false);
        }

        private void PasteWholePerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null)
            {
                SetEditFeedback("请选择一个对话节点后粘贴整套演出。", true);
                return;
            }
            if (_wholePerformanceClipboard == null)
            {
                SetEditFeedback("本会话还没有复制整套演出。", true);
                return;
            }
            ApplyTalkPerformance(talk, _wholePerformanceClipboard.Clone());
        }

        private void ClearWholePerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option) || talk == null)
            {
                SetEditFeedback("请选择一个对话节点后清空整套演出。", true);
                return;
            }
            ApplyTalkPerformance(talk, new StoryGraphPerformanceData());
        }

        private void CopyLogicPerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option))
            {
                SetEditFeedback("请选择一个对话或选项节点后复制逻辑预设。", true);
                return;
            }
            _logicClipboard = talk != null
                ? StoryGraphLogicData.FromTalk(talk)
                : StoryGraphLogicData.FromOption(option);
            // 逻辑预设可以复用条件与效果，但不能把源节点的剧情去向带到目标节点。
            _logicClipboard.SuccessTargets.Clear();
            _logicClipboard.FailureTargets.Clear();
            SetEditFeedback(talk != null
                ? "已复制对话的判断条件与效果（不会复制剧情去向）。"
                : "已复制选项的显示条件、结果判断与效果（不会复制剧情去向）。", false);
        }

        private void PasteLogicPerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option))
            {
                SetEditFeedback("请选择一个对话或选项节点后粘贴逻辑预设。", true);
                return;
            }
            if (_logicClipboard == null)
            {
                SetEditFeedback("本会话还没有复制逻辑预设。", true);
                return;
            }
            StoryGraphLogicData logic = _logicClipboard.Clone();
            StoryGraphLogicData current = talk != null
                ? StoryGraphLogicData.FromTalk(talk)
                : StoryGraphLogicData.FromOption(option);
            logic.SuccessTargets = new List<int>(current.SuccessTargets);
            logic.FailureTargets = new List<int>(current.FailureTargets);
            if (talk != null)
                ApplyTalkLogic(talk, logic);
            else
                ApplyOptionLogic(option, logic);
        }

        private void ClearLogicPerformance()
        {
            TalkCfg talk;
            OptionCfg option;
            if (!TryGetPerformanceTarget(out talk, out option))
            {
                SetEditFeedback("请选择一个对话或选项节点后清空逻辑效果。", true);
                return;
            }
            StoryGraphLogicData logic = talk != null
                ? StoryGraphLogicData.FromTalk(talk)
                : StoryGraphLogicData.FromOption(option);
            logic.VisibilityConditions.Clear();
            logic.ResultConditions.Clear();
            logic.SuccessEffects.Clear();
            logic.FailureEffects.Clear();
            if (talk != null)
                ApplyTalkLogic(talk, logic);
            else
                ApplyOptionLogic(option, logic);
        }

        private void ClearPerformanceInspectorReferences()
        {
            _inspectorScroll = null;
            _inspectorBasicPage = null;
            _inspectorCastPage = null;
            _inspectorStagePage = null;
            _inspectorLogicPage = null;
            _inspectorMiniGamePage = null;
            _inspectorAdvancedPage = null;
            _inspectorBasicTab = null;
            _inspectorCastTab = null;
            _inspectorStageTab = null;
            _inspectorLogicTab = null;
            _inspectorMiniGameTab = null;
            _inspectorAdvancedTab = null;
            _inspectorSpeakerInput = null;
            _inspectorSpeakerRow = null;
            _inspectorSpeakerLabel = null;
            _inspectorShowRow = null;
            _inspectorShowingHelp = false;
            _inspectorHeaderHeight = 154f;
            _basicInspectorPageHeight = 430f;
            _inspectorCastIntro = null;
            _inspectorRoleIdsRow = null;
            _inspectorHighlightsRow = null;
            _inspectorRoleIdsInput = null;
            _inspectorHighlightsInput = null;
            _inspectorRoleActionsInput = null;
            _inspectorActionActorInput = null;
            _inspectorActionArgsInput = null;
            _inspectorRoleNamesHint = null;
            _inspectorRoleActionsLabel = null;
            _inspectorRoleActionsDescriptionLabel = null;
            _inspectorRoleActionsDescription = null;
            _inspectorActionActorRow = null;
            _inspectorActionArgsRow = null;
            _inspectorActionTemplateButtons = null;
            _inspectorCastFooterButtons = null;
            _castInspectorPageHeight = 1050f;
            _inspectorBackgroundRow = null;
            _inspectorAudioRow = null;
            _inspectorBackgroundInput = null;
            _inspectorAudioInput = null;
            _inspectorScreenEffectInput = null;
            _inspectorVocalsInput = null;
            _inspectorScreenArgsInput = null;
            _inspectorResourceHint = null;
            _inspectorScreenEffectLabel = null;
            _inspectorScreenDescription = null;
            _inspectorVocalsRow = null;
            _inspectorScreenArgsRow = null;
            _inspectorScreenTemplateButtons = null;
            _inspectorStageFooterButtons = null;
            _stageInspectorPageHeight = 790f;
            _inspectorLogicIntro = null;
            _inspectorDirectOptionModeButton = null;
            _inspectorConditionalOptionModeButton = null;
            _inspectorVisibilityConditionLabel = null;
            _inspectorVisibilityConditionInput = null;
            _inspectorResultConditionLabel = null;
            _inspectorResultConditionInput = null;
            _inspectorConditionPreview = null;
            _inspectorConditionHelpButton = null;
            _inspectorLogicRoutesLabel = null;
            _inspectorSuccessRouteRow = null;
            _inspectorFailureRouteRow = null;
            _inspectorSuccessRouteLabel = null;
            _inspectorFailureRouteLabel = null;
            _inspectorSuccessRouteInput = null;
            _inspectorFailureRouteInput = null;
            _inspectorSuccessEffectsLabel = null;
            _inspectorFailureEffectsLabel = null;
            _inspectorSuccessEffectsInput = null;
            _inspectorFailureEffectsInput = null;
            _inspectorCopyLogicButton = null;
            _inspectorPasteLogicButton = null;
            _inspectorClearLogicButton = null;
            _optionConditionalDraftId = int.MinValue;
            _optionDirectModeConfirmId = int.MinValue;
            _optionDirectModeConfirmUntil = 0f;
            _inspectorMiniGameIntro = null;
            _inspectorMiniGameLabel = null;
            _inspectorMiniGameInput = null;
            _inspectorMiniGameSummary = null;
            _inspectorMiniGameFlowHint = null;
            _inspectorClearMiniGameButton = null;
            _inspectorMiniGameCommonLabel = null;
            _inspectorMiniGameCommonBody = null;
            _inspectorMiniGameSpecialLabel = null;
            _inspectorMiniGameSpecialBody = null;
            _inspectorAdvancedLabel = null;
            _inspectorAdvancedReloadButton = null;
            _inspectorAdvancedWarning = null;
            _logicInspectorPageHeight = 650f;
            _miniGameInspectorPageHeight = 720f;
            _advancedInspectorPageHeight = 680f;
            _inspectorNextEventInput = null;
            _inspectorNextEventRow = null;
            _inspectorNextEventHint = null;
            _castClipboard = null;
            _stageClipboard = null;
            _wholePerformanceClipboard = null;
            _logicClipboard = null;
            _performanceResourceSignature = null;
            _backgroundNames.Clear();
            _cgNames.Clear();
            _audioNames.Clear();
            _eventNames.Clear();
            _audioBrowseIds.Clear();
            _faceNames.Clear();
            ClearResourcePickerReferences();
            _inspectorPage = InspectorPage.Basic;
        }
    }
}
