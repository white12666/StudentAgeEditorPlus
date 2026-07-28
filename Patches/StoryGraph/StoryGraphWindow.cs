using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Config;
using Sdk;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;
using Newtonsoft.Json;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图 UGUI 显示层窗口（契约冻结：Bind / IsOpen / Toggle / Open / Close /
    /// ResetForViewOpen，由 EvtStoryGraphInitPatch / EvtStoryGraphOpenPatch 调用）。
    ///
    /// 全部界面用代码构建，不依赖任何 prefab；组件挂在 ModEvtEditView 根上，
    /// 自建 Canvas（ScreenSpaceOverlay + overrideSorting）必须保持为场景顶层对象。
    /// 注意：游戏根 Canvas 是 ScreenSpaceCamera，Overlay 永远渲染在它之上，
    /// 原版 ToastView 等全局提示会被剧情图盖住——提示改由图内 Toast 显示
    /// （StoryGraphToastRouter + TryShowGraphToast）。
    /// Unity 明确不支持把 Overlay Canvas 嵌在其他 UI 下；生命周期仍由本组件显式管理。
    /// 不用 IMGUI，不用静态字段持有 Unity 对象。
    ///
    /// 坐标约定（关键设计决策）：
    ///  - 布局层输出的显示坐标为「x 向右、y 向下」的画布像素坐标；
    ///  - UGUI 本地坐标 y 向上，因此所有布局点 P 放进 content 时统一转成 (P.x, -P.y)；
    ///  - content 锚在视口中心、pivot 取左上 (0,1)，anchoredPosition C 表示
    ///    「content 左上角相对视口中心的偏移」，缩放直接改 content.localScale；
    ///  - 由此推出：视口局部点 v 与内容局部点 p 的关系为 v = C + p * zoom，
    ///    所有缩放/平移/居中/小地图映射都只围绕这一个等式推导，避免符号混乱。
    /// </summary>
    internal sealed partial class StoryGraphWindow : MonoBehaviour
    {
        private enum EdgeLabelMode
        {
            Hidden,
            Important,
            All,
        }

        private enum NodeAlignment
        {
            Left,
            Right,
            Top,
            Bottom,
        }

        // ==================== 常量 ====================

        private const float MinZoom = 0.2f;          // 缩放下限（契约 0.2~2.5）
        private const float MaxZoom = 2.5f;          // 缩放上限
        private const float FitMaxZoom = 1.25f;      // 适应全图时的放大上限
        // 本窗口沿用游戏 Canvas 的逻辑分辨率。尺寸必须按 1440p 级参考画布设计，
        // 旧版 40/28/13 的工具栏参数实际接近 1080p 小控件，4K 下物理像素虽放大，
        // 但相对游戏 UI 和高 DPI 屏幕仍显得过小。
        private const float ToolbarHeight = 64f;     // 顶部工具栏高
        private const float ToolbarControlHeight = 42f;
        private const float StatusbarHeight = 34f;   // 底部状态栏高
        private const int ToolbarFontSize = 16;
        private const int SecondaryFontSize = 14;
        private const float EdgeLabelMinZoom = 0.38f;// 低于该缩放不画边标签
        private const float SearchFocusMinZoom = 0.6f;// 搜索定位低于此缩放先放大
        private const float SearchFocusZoom = 0.8f;  // 搜索定位提升到的缩放
        private const float PanSlack = 80f;          // 平移钳制：图至少留在视口内的像素
        private const int FallbackCanvasSortingOrder = 30000;
        private const float NodeBorderThickness = 1f;// 节点填充内缩 = 1px 细边框
        private const float MinimapWidth = 240f;     // 小地图尺寸
        private const float MinimapHeight = 168f;
        private const float TooltipWidth = 440f;
        private const float InspectorWidth = 430f;
        private const float PortWidth = 64f;
        private const float PortHeight = 20f;
        private const float ConfirmSeconds = 4f;
        private const float BoxSelectThreshold = 5f;

        // 入口/结束是正常的流程标记，不应仅凭它们弹出悬浮框；下面这些标记
        // 才表示作者需要额外关注或理解的配置状态。
        private const EvtStoryGraphNodeFlags TooltipAttentionFlags =
            EvtStoryGraphNodeFlags.Unreachable |
            EvtStoryGraphNodeFlags.Cycle |
            EvtStoryGraphNodeFlags.SelfLoop |
            EvtStoryGraphNodeFlags.MissingReference |
            EvtStoryGraphNodeFlags.External |
            EvtStoryGraphNodeFlags.DuplicateId |
            EvtStoryGraphNodeFlags.OrphanOption |
            EvtStoryGraphNodeFlags.InvalidData |
            EvtStoryGraphNodeFlags.SharedOption |
            EvtStoryGraphNodeFlags.CgNotClosed;

        // ── 官方纸感配色（全部来自 _agenttmp/official_ui_style_guide.md 实测色表）──
        private static readonly Color PaperBg = HexColor("F3F1EF");      // 全屏暖色底
        private static readonly Color PanelBg = HexColor("FFFBF3");      // 面板/节点/工具栏底
        private static readonly Color PanelBorder = HexColor("C6B187");  // 细边框/分隔线
        private static readonly Color SegmentFill = HexColor("FAE9B3");  // 链段块米黄底
        private static readonly Color OptionFill = HexColor("F3DFC8");   // 选项节点驼色浅底（比链段米黄更偏驼橙，色相拉开）
        private static readonly Color UnusedFill = HexColor("C6C1B6");   // 未使用配置米灰底
        private static readonly Color TitleColor = HexColor("483C29");   // 节点标题深褐
        private static readonly Color InvalidTitleColor = HexColor("A43841"); // 缺失/异常标题红
        private static readonly Color SubtitleColor = HexColor("877368");// 摘要灰棕
        private static readonly Color BodyTextColor = HexColor("692B16");// 正文/标签深棕
        private static readonly Color AccentOrange = HexColor("E4700B"); // 强调橙：悬停/选中/当前命中
        private static readonly Color CurrentRingColor = HexColor("FFCE24");  // 当前定位金黄环
        private static readonly Color SearchRingColor = HexColor("FFCE24");   // 搜索命中黄环（当前命中换橙加粗，见 ApplyNodeVisualState）
        private static readonly Color SelectRingColor = HexColor("E4700B");   // 单击选中橙环
        private static readonly Color HoverBorderColor = HexColor("E4700B");  // 悬停节点边框橙
        private static readonly Color AdjacentBorderColor = HexColor("D19460"); // 相邻节点边框驼
        private static readonly Color EdgeActiveColor = HexColor("877368");   // 活跃边灰棕
        private static readonly Color EdgeIgnoredColor = HexColor("C6B187");  // 被遮蔽边浅驼

        // ==================== 数据与依赖 ====================

        [NonSerialized] private ModEvtEditView _view;
        private bool _open;
        private EvtStoryGraphModel _model;
        private StoryGraphViewModel _viewModel;
        private StoryGraphLayoutResult _layout;
        private string _error;
        private float _lastInputErrorLogTime = -999f; // 输入异常日志节流（5 秒最多一条）
        private bool _hasLoggedUiMetrics;

        // ==================== 视图状态 ====================

        private float _zoom = 1f;
        private bool _pendingInitialCenter;   // 打开后等待视口尺寸有效再居中
        private bool _previewSuspended;       // 预览本句期间隐藏顶层剧情图，但保留草稿

        // 图内 Toast：游戏根 Canvas 是 ScreenSpaceCamera，剧情图是
        // ScreenSpaceOverlay，Overlay 永远渲染在相机空间画布之上，原版
        // ToastView 会被剧情图整体盖住，提示必须画在剧情图自己的画布上
        // （路由见 StoryGraphToastRouter）。
        private RectTransform _graphToastRoot;
        private Text _graphToastLabel;
        private CanvasGroup _graphToastGroup;
        private float _graphToastUntil;
        private string _pendingGraphToast;    // 预览本句暂隐画布期间积压的最新一条
        private Func<string, bool> _graphToastSink;
        private bool _panning;
        private ButtonControl _panButton;     // 平移按住的鼠标键（新输入系统）
        private Vector2 _lastPanPoint;
        private Vector2 _lastScreenSize;
        private Vector2 _lastCanvasSize;

        /// <summary>窗口级展开记忆：刷新重建 ViewModel 后按 key 逐个恢复。</summary>
        private readonly List<string> _expandedKeys = new List<string>();
        private StoryGraphDisplayNode _currentNode;   // 打开时自动定位的节点（青环）
        private StoryGraphDisplayNode _selectedNode;  // 单击选中的节点（白环）
        private TalkCfg _editorFocusTalk;             // 原编辑器当前 Talk（定位意图）
        private OptionCfg _editorFocusOption;         // 最近打开且仍属于该 Talk 的选项
        private StoryGraphDisplayNode _hoverNode;     // 悬停节点
        private readonly HashSet<StoryGraphDisplayNode> _hoverAdjacent =
            new HashSet<StoryGraphDisplayNode>();
        private readonly List<StoryGraphDisplayNode> _matches =
            new List<StoryGraphDisplayNode>();
        private int _matchIndex = -1;
        private string _searchText = string.Empty;
        private EdgeLabelMode _edgeLabelMode = EdgeLabelMode.Important;
        private Dictionary<int, string> _personNames = new Dictionary<int, string>();

        // 可视化编辑始终操作独立深拷贝草稿；显示节点只是草稿的投影，折叠/刷新后
        // 即使对象被重建，也只靠「类型 + 配置 ID」恢复选择。
        private bool _editMode;
        private StoryGraphEditSession _editSession;
        private StoryGraphEditNodeKind _editSelectionKind;
        private int _editSelectionId;
        private int _editSelectionOrdinal;
        private string _editSelectionStableKey;
        private string _editStatus;
        private float _deleteConfirmUntil;
        private float _discardConfirmUntil;
        private float _saveCgConfirmUntil;
        private bool _savingEdit;
        private bool _inspectorVisible = true;
        private bool _settingInspectorText;
        private int _inspectorLiveEditSerial;
        private string _inspectorLiveEditKey;
        private object _inspectorLiveEditTarget;
        private bool _inspectorBasicPreviewDirty;
        // 分组/注释属于作者工作区而非剧情草稿，使用独立快照合并一次聚焦内的
        // 连续字符；数据侧仍由 StoryGraphEditSession 的 liveEditKey 管理。
        private string _workspaceLiveEditKey;
        private string _workspaceLiveEditGroupId;
        private StoryGraphWorkspace.Snapshot _workspaceLiveEditBefore;
        private bool _workspaceLiveEditHistoryRecorded;
        private StoryGraphWorkspace _workspace;
        private string _workspaceModRoot;
        private int _workspaceEventId;
        private readonly HashSet<StoryGraphDisplayNode> _selectedNodes =
            new HashSet<StoryGraphDisplayNode>();
        private readonly HashSet<string> _selectedNodeKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Stack<StoryGraphWorkspace.Snapshot> _workspaceUndo =
            new Stack<StoryGraphWorkspace.Snapshot>();
        private readonly Stack<StoryGraphWorkspace.Snapshot> _workspaceRedo =
            new Stack<StoryGraphWorkspace.Snapshot>();
        // false=剧情数据命令，true=工作区布局命令；与各自撤销栈保持同序。
        private readonly Stack<bool> _editActionTimeline = new Stack<bool>();
        private readonly Stack<bool> _editRedoTimeline = new Stack<bool>();

        // ==================== UGUI 引用（均非静态，Close 时清空） ====================

        [NonSerialized] private GameObject _canvasRoot;
        [NonSerialized] private RectTransform _canvasRect;
        [NonSerialized] private RectTransform _viewport;
        [NonSerialized] private RectTransform _content;
        [NonSerialized] private RectTransform _groupContainer;
        [NonSerialized] private RectTransform _edgeContainer;
        [NonSerialized] private RectTransform _nodeContainer;
        [NonSerialized] private Text _statusText;
        [NonSerialized] private Text _zoomText;
        [NonSerialized] private Text _emptyText;
        [NonSerialized] private InputField _searchInput;
        [NonSerialized] private Button _previousMatchButton;
        [NonSerialized] private Button _nextMatchButton;
        [NonSerialized] private Button _edgeLabelModeButton;
        [NonSerialized] private Text _matchCounterText;
        [NonSerialized] private Font _font;
        [NonSerialized] private GameObject _viewToolbarGroup;
        [NonSerialized] private GameObject _editToolbarGroup;
        [NonSerialized] private Button _previewViewButton;
        [NonSerialized] private Button _previewEditButton;
        [NonSerialized] private Button _returnEditButton;
        [NonSerialized] private Button _addTalkEditButton;
        [NonSerialized] private Button _addOptionEditButton;
        [NonSerialized] private Button _duplicateEditButton;
        [NonSerialized] private Button _deleteEditButton;
        [NonSerialized] private Button _undoEditButton;
        [NonSerialized] private Button _redoEditButton;
        [NonSerialized] private Button _saveEditButton;
        [NonSerialized] private Button _discardEditButton;
        [NonSerialized] private Button _inspectorToggleButton;
        [NonSerialized] private Image _connectionPreview;
        [NonSerialized] private RectTransform _selectionRect;
        [NonSerialized] private RectTransform _contextMenu;
        [NonSerialized] private GameObject _inspectorRoot;
        [NonSerialized] private RectTransform _inspectorContent;
        [NonSerialized] private Text _inspectorTitle;
        [NonSerialized] private Text _inspectorInfo;
        [NonSerialized] private Text _inspectorContentLabel;
        [NonSerialized] private Text _inspectorShowLabel;
        [NonSerialized] private Text _inspectorTagLabel;
        [NonSerialized] private InputField _inspectorContentInput;
        [NonSerialized] private InputField _inspectorShowInput;
        [NonSerialized] private InputField _inspectorTagInput;
        [NonSerialized] private InputField _inspectorJsonInput;
        [NonSerialized] private GameObject _inspectorTagRow;
        [NonSerialized] private Button _inspectorCloseButton;

        // 程序化圆角九宫格贴图（运行时生成、实例缓存复用；Close/OnDestroy 释放，
        // 禁止静态持有 Unity 对象）
        [NonSerialized] private Texture2D _roundedTex;   // 白底圆角 r8：面板/节点/环/tooltip
        [NonSerialized] private Texture2D _chipTex;      // 白底圆角 r4：边标签小片
        [NonSerialized] private Texture2D _buttonTex;    // 金黄竖向渐变 r6 + 底部深边：按钮
        [NonSerialized] private Texture2D _inputTex;     // #E7DFCB r6 + 底部深边：输入框
        [NonSerialized] private Sprite _roundedSprite;
        [NonSerialized] private Sprite _chipSprite;
        [NonSerialized] private Sprite _buttonSprite;
        [NonSerialized] private Sprite _inputSprite;

        // 小地图
        [NonSerialized] private RectTransform _minimapBorder;
        [NonSerialized] private RectTransform _minimapRoot;
        [NonSerialized] private RectTransform _minimapContent;
        [NonSerialized] private RectTransform _minimapFrame;
        private float _mmScale;      // 小地图：布局像素 → 小图像素
        private Vector2 _mmCenter;   // 小地图中心对应的布局坐标
        private Vector2 _mmSize;     // 小地图内容区尺寸

        // tooltip
        [NonSerialized] private RectTransform _tooltip;
        [NonSerialized] private Text _tooltipTitle;
        [NonSerialized] private Text _tooltipSubtitle;
        [NonSerialized] private Text _tooltipFlags;

        // ==================== 对象池与活动实例 ====================

        private readonly List<NodeVisual> _activeNodes = new List<NodeVisual>();
        private readonly Stack<NodeVisual> _nodePool = new Stack<NodeVisual>();
        private readonly Dictionary<StoryGraphDisplayNode, NodeVisual> _nodeLookup =
            new Dictionary<StoryGraphDisplayNode, NodeVisual>();
        private readonly List<EdgeVisual> _activeEdges = new List<EdgeVisual>();
        private readonly Dictionary<StoryGraphDisplayEdge, EdgeVisual> _edgeLookup =
            new Dictionary<StoryGraphDisplayEdge, EdgeVisual>();
        private readonly Stack<Image> _segmentPool = new Stack<Image>();
        private readonly Stack<Image> _arrowPool = new Stack<Image>();
        private readonly Stack<GameObject> _labelPool = new Stack<GameObject>();
        private readonly List<Image> _activeDots = new List<Image>();
        private readonly Stack<Image> _dotPool = new Stack<Image>();
        private readonly List<RaycastResult> _raycastResults = new List<RaycastResult>();

        /// <summary>节点可视对象：边框 + 填充 + 标题/摘要 + 三层高亮环。</summary>
        private sealed class NodeVisual
        {
            internal StoryGraphDisplayNode Node;
            internal GameObject Root;
            internal RectTransform Rect;
            internal GameObject CardRoot;
            internal RectTransform CardRect;
            internal Image CurrentRing;  // 最外：当前定位青环
            internal Image SearchRing;   // 中：搜索命中黄环
            internal Image SelectRing;   // 内：选中白环
            internal Image Border;
            internal Image Fill;
            internal Image ColorBar;   // 左缘语义色条
            internal Text Title;
            internal Text Subtitle;
            internal Image SegmentToggle;
            internal Image SegmentToggleFill;
            internal Image SegmentToggleColorBar;
            internal Text SegmentToggleLabel;
            internal EventTrigger SegmentToggleTrigger;
            internal string SegmentToggleKey;
            internal EventTrigger Trigger;
            internal readonly List<PortVisual> Ports = new List<PortVisual>();
        }

        private sealed class PortVisual
        {
            internal NodeVisual Owner;
            internal StoryGraphEditPortKind Kind;
            internal GameObject Root;
            internal RectTransform Rect;
            internal Image Background;
            internal Text Label;
            internal EventTrigger Trigger;
        }

        private sealed class ConnectionDragState
        {
            internal PortVisual Port;
            internal StoryGraphEditPortKind Kind;
            internal TalkCfg SourceTalk;
            internal OptionCfg SourceOption;
        }

        private sealed class NodeDragState
        {
            internal Vector2 StartPointer;
            internal readonly Dictionary<StoryGraphDisplayNode, Rect> StartRects =
                new Dictionary<StoryGraphDisplayNode, Rect>();
            internal StoryGraphWorkspace.Snapshot Before;
            internal bool BeforeDirty;
            internal bool Moved;
        }

        private sealed class BoxSelectionState
        {
            internal Vector2 StartScreen;
            internal bool Additive;
            internal bool Moved;
            internal string PrimaryKey;
            internal readonly HashSet<string> BaseKeys =
                new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class LayoutNodePosition
        {
            internal Vector2 Position;
            internal bool HasSource;
            internal EvtStoryGraphNodeKind SourceKind;
            internal int SourceId;
            internal TalkCfg Talk;
            internal OptionCfg Option;
            internal TalkCfg LocateTalk;
            internal List<TalkCfg> SegmentTalks;
        }

        /// <summary>
        /// 数据重建前的画布坐标。VisiblePositions 负责辅助框等全部可见节点；
        /// StablePositions 只保存可编辑节点，供工作区持久化。NodePositions 再按
        /// 配置对象身份兜底，避免重复 ID 草稿中的两张卡片被稳定键压到同一点。
        /// OptionPositions 处理唯一父 Talk 删除后由 use 节点变为 orphan 的换键。
        /// </summary>
        private sealed class LayoutPositionSnapshot
        {
            internal readonly Dictionary<string, Vector2> VisiblePositions =
                new Dictionary<string, Vector2>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Vector2> StablePositions =
                new Dictionary<string, Vector2>(StringComparer.Ordinal);
            internal readonly Dictionary<int, List<Vector2>> OptionPositions =
                new Dictionary<int, List<Vector2>>();
            internal readonly List<LayoutNodePosition> NodePositions =
                new List<LayoutNodePosition>();
        }

        private sealed class ClipboardLayoutEntry
        {
            internal bool IsTalk;
            internal int SourceId;
            internal int ParentTalkId;
            internal Vector2 Offset;
        }

        private sealed class StoryGraphClipboardData
        {
            internal List<TalkCfg> Talks;
            internal Dictionary<int, OptionCfg> Options;
            internal readonly List<ClipboardLayoutEntry> Layout =
                new List<ClipboardLayoutEntry>();
            internal bool IncludesBranch;
        }

        private sealed class ContextMenuItem
        {
            internal string Label;
            internal Action Action;
            internal bool Enabled = true;
        }

        private sealed class ContextMenuButtonVisual
        {
            internal GameObject Root;
            internal Button Button;
            internal Text Label;
        }

        private sealed class GroupVisual
        {
            internal StoryGraphWorkspace.GroupData Group;
            internal GameObject Root;
            internal RectTransform Rect;
            internal Text Title;
            internal Text Note;
        }

        private sealed class GroupDragState
        {
            internal GroupVisual Visual;
            internal Vector2 StartPointer;
            internal StoryGraphWorkspace.Snapshot Before;
            internal bool BeforeDirty;
            internal Rect StartGroupBounds;
            internal readonly Dictionary<StoryGraphDisplayNode, Rect> StartRects =
                new Dictionary<StoryGraphDisplayNode, Rect>();
            internal bool Moved;
        }

        private readonly List<ContextMenuButtonVisual> _contextMenuButtons =
            new List<ContextMenuButtonVisual>();

        private ConnectionDragState _connectionDrag;
        private NodeDragState _nodeDrag;
        private BoxSelectionState _boxSelection;
        private StoryGraphClipboardData _clipboard;
        private Vector2 _lastPointerScreen;
        private readonly List<GroupVisual> _activeGroups = new List<GroupVisual>();
        private readonly Dictionary<string, Rect> _groupBounds =
            new Dictionary<string, Rect>(StringComparer.Ordinal);
        private GroupDragState _groupDrag;
        private string _selectedGroupId;

        /// <summary>边可视对象：若干线段条 + 末端箭头 + 标签，记录基础样式供悬停恢复。</summary>
        private sealed class EdgeVisual
        {
            internal StoryGraphDisplayEdge Edge;
            internal readonly List<Image> Segments = new List<Image>();
            internal readonly List<float> Lengths = new List<float>();
            internal Image Arrow;
            internal GameObject LabelRoot;
            internal Text Label;
            internal Color BaseColor;
            internal float BaseWidth;
        }

        // ==================== 契约 API ====================

        internal void Bind(ModEvtEditView view)
        {
            _view = view;
        }

        internal bool IsOpen
        {
            get { return _open; }
        }

        internal void Toggle()
        {
            if (_open) RequestClose();
            else Open();
        }

        internal void Open()
        {
            if (_open) return;
            if (_view == null || _view.gameObject == null)
            {
                Plugin.Log?.LogWarning("[StoryGraph] 事件编辑器已关闭，无法打开剧情图。");
                return;
            }

            // 先让编辑器里聚焦的输入框失焦，触发 onEndEdit 把最后输入写回内存，
            // 然后再做只读快照，避免图上少显示最后一个未提交的字符。
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);

            _open = true;
            try
            {
                EnsureCanvas();
                RefreshGraph(true);
            }
            catch (Exception e)
            {
                // 创建是事务性的：任何一步失败都必须撤掉全屏射线遮罩，
                // 不能留下只能强退游戏的“黑屏半成品”。
                Plugin.Log?.LogError("[StoryGraph.Open] " + e);
                try { Close(); }
                catch (Exception cleanupError)
                {
                    Plugin.Log?.LogError("[StoryGraph.Open.Cleanup] " + cleanupError);
                }
                try { StoryGraphToastRouter.Show("剧情图打开失败，已自动关闭；请查看 BepInEx 日志。"); }
                catch { /* Toast 失败也不能妨碍遮罩回滚。 */ }
            }
        }

        internal void Close()
        {
            // 生命周期/异常回滚必须无条件撤掉顶层 Overlay。用户主动关闭则统一走
            // RequestClose，在这里不再弹确认，避免 OnDisable 时留下全屏遮罩。
            CloseInspectorResourcePicker();
            StoryGraphPreviewReturnBridge.Forget(this);
            if (_graphToastSink != null)
                StoryGraphToastRouter.Unregister(_graphToastSink);
            _pendingGraphToast = null;
            CancelConnectionDrag();
            _boxSelection = null;
            _open = false;
            _editMode = false;
            _editSession = null;
            _editSelectionKind = StoryGraphEditNodeKind.None;
            _editSelectionId = 0;
            _editSelectionOrdinal = 0;
            _editSelectionStableKey = null;
            _editStatus = null;
            _deleteConfirmUntil = 0f;
            _discardConfirmUntil = 0f;
            _saveCgConfirmUntil = 0f;
            _savingEdit = false;
            EndInspectorLiveEdit();
            _inspectorVisible = true;
            _inspectorBasicPreviewDirty = false;
            _workspace = null;
            _workspaceModRoot = null;
            _workspaceEventId = 0;
            ClearEditHistories();
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            _nodeDrag = null;
            _panning = false;
            _panButton = null;
            _pendingInitialCenter = false;
            _previewSuspended = false;
            _lastScreenSize = Vector2.zero;
            _lastCanvasSize = Vector2.zero;
            _hoverNode = null;
            _hoverAdjacent.Clear();

            // 销毁整个 Canvas，池与所有 UGUI 引用一并清空（池里的对象都是
            // Canvas 的子节点，随根销毁，只需清引用）。
            if (_canvasRoot != null)
            {
                UnityEngine.Object.Destroy(_canvasRoot);
            }
            _canvasRoot = null;
            _canvasRect = null;
            _viewport = null;
            _content = null;
            _groupContainer = null;
            _edgeContainer = null;
            _nodeContainer = null;
            _statusText = null;
            _zoomText = null;
            _emptyText = null;
            _searchInput = null;
            _previousMatchButton = null;
            _nextMatchButton = null;
            _edgeLabelModeButton = null;
            _matchCounterText = null;
            _graphToastRoot = null;
            _graphToastLabel = null;
            _graphToastGroup = null;
            _graphToastUntil = 0f;
            _font = null;
            _viewToolbarGroup = null;
            _editToolbarGroup = null;
            _previewViewButton = null;
            _previewEditButton = null;
            _returnEditButton = null;
            _addTalkEditButton = null;
            _addOptionEditButton = null;
            _duplicateEditButton = null;
            _deleteEditButton = null;
            _undoEditButton = null;
            _redoEditButton = null;
            _saveEditButton = null;
            _discardEditButton = null;
            _inspectorToggleButton = null;
            _connectionPreview = null;
            _selectionRect = null;
            _contextMenu = null;
            _contextMenuButtons.Clear();
            ClearInspectorReferences();
            DestroyGeneratedSprites();
            _minimapBorder = null;
            _minimapRoot = null;
            _minimapContent = null;
            _minimapFrame = null;
            _tooltip = null;
            _tooltipTitle = null;
            _tooltipSubtitle = null;
            _tooltipFlags = null;

            _activeGroups.Clear();
            _groupBounds.Clear();
            _groupDrag = null;
            _selectedGroupId = null;
            _activeNodes.Clear();
            _nodePool.Clear();
            _nodeLookup.Clear();
            _activeEdges.Clear();
            _edgeLookup.Clear();
            _segmentPool.Clear();
            _arrowPool.Clear();
            _labelPool.Clear();
            _activeDots.Clear();
            _dotPool.Clear();

            _model = null;
            _viewModel = null;
            _layout = null;
            _error = null;
            _matches.Clear();
            _matchIndex = -1;
            _searchText = string.Empty;
            _personNames.Clear();
            _currentNode = null;
            _selectedNode = null;
            _editorFocusTalk = null;
            _editorFocusOption = null;
        }

        /// <summary>编辑器 OnOpen 时调用：关窗并清掉上一事件的全部状态（含展开记忆）。</summary>
        internal void ResetForViewOpen()
        {
            Close();
            _expandedKeys.Clear();
            _zoom = 1f;
            _lastInputErrorLogTime = -999f;
        }

        /// <summary>
        /// PreviewTalkView 位于游戏自己的 Normal Canvas，而剧情图是更高层的独立
        /// Overlay；预览期间只隐藏画布，不关闭窗口、不丢弃编辑草稿。
        /// </summary>
        internal bool SuspendForTalkPreview()
        {
            if (!_open || _canvasRoot == null || _previewSuspended) return false;
            CloseInspectorResourcePicker();
            HideContextMenu();
            HideTooltip();
            _panning = false;
            _panButton = null;
            // 预览结束后画布重新激活时，不让预览前的旧提示原样闪回。
            HideGraphToastNow();
            _previewSuspended = true;
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
            _canvasRoot.SetActive(false);
            return true;
        }

        internal void ResumeAfterTalkPreview()
        {
            if (!_previewSuspended) return;
            _previewSuspended = false;
            if (!_open || _canvasRoot == null) return;
            _canvasRoot.SetActive(true);
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
            Canvas.ForceUpdateCanvases();
            UpdateResponsiveToolbarLayout();
            if (_content != null)
                _content.anchoredPosition =
                    ClampPan(_content.anchoredPosition);
            RefreshAllNodeStates();
            UpdateMinimapDots();
            UpdateMinimapFrame();
            // 预览打开失败等在画布暂隐期间产生的提示，此刻补发到图内。
            FlushPendingGraphToast();
        }

        private void OnDisable()
        {
            // View 隐藏/销毁都会走到这里；Close 是幂等的。
            Close();
        }

        private void OnDestroy()
        {
            // Overlay Canvas 是场景顶层对象，不会随编辑器根自动销毁；
            // OnDisable 正常会先 Close，这里再做一次最终兜底。
            if (_graphToastSink != null)
                StoryGraphToastRouter.Unregister(_graphToastSink);
            _pendingGraphToast = null;
            StoryGraphPreviewReturnBridge.Forget(this);
            if (_canvasRoot != null)
                UnityEngine.Object.Destroy(_canvasRoot);
            _view = null;
            _canvasRoot = null;
            _canvasRect = null;
            _viewport = null;
            _content = null;
            _groupContainer = null;
            _edgeContainer = null;
            _nodeContainer = null;
            _statusText = null;
            _zoomText = null;
            _emptyText = null;
            _searchInput = null;
            _previousMatchButton = null;
            _nextMatchButton = null;
            _edgeLabelModeButton = null;
            _matchCounterText = null;
            _graphToastRoot = null;
            _graphToastLabel = null;
            _graphToastGroup = null;
            _graphToastUntil = 0f;
            _font = null;
            _viewToolbarGroup = null;
            _editToolbarGroup = null;
            _previewViewButton = null;
            _previewEditButton = null;
            _returnEditButton = null;
            _addTalkEditButton = null;
            _addOptionEditButton = null;
            _duplicateEditButton = null;
            _deleteEditButton = null;
            _undoEditButton = null;
            _redoEditButton = null;
            _saveEditButton = null;
            _discardEditButton = null;
            _inspectorToggleButton = null;
            _connectionPreview = null;
            _selectionRect = null;
            _contextMenu = null;
            _contextMenuButtons.Clear();
            _connectionDrag = null;
            _nodeDrag = null;
            _boxSelection = null;
            _editSession = null;
            _inspectorLiveEditKey = null;
            _inspectorBasicPreviewDirty = false;
            _personNames.Clear();
            _workspace = null;
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            ClearEditHistories();
            _editMode = false;
            _previewSuspended = false;
            ClearInspectorReferences();
            DestroyGeneratedSprites();
            _minimapBorder = null;
            _minimapRoot = null;
            _minimapContent = null;
            _minimapFrame = null;
            _tooltip = null;
            _tooltipTitle = null;
            _tooltipSubtitle = null;
            _tooltipFlags = null;
            _model = null;
            _viewModel = null;
            _layout = null;
            _currentNode = null;
            _selectedNode = null;
            _editorFocusTalk = null;
            _editorFocusOption = null;
            _hoverNode = null;
            _activeGroups.Clear();
            _groupBounds.Clear();
            _groupDrag = null;
            _selectedGroupId = null;
            _activeNodes.Clear();
            _nodePool.Clear();
            _nodeLookup.Clear();
            _activeEdges.Clear();
            _edgeLookup.Clear();
            _segmentPool.Clear();
            _arrowPool.Clear();
            _labelPool.Clear();
            _activeDots.Clear();
            _dotPool.Clear();
            _expandedKeys.Clear();
            _matches.Clear();
            _hoverAdjacent.Clear();
        }

        // ==================== 每帧输入 ====================

        private void Update()
        {
            if (!_open || _canvasRoot == null || _previewSuspended) return;

            // 退出键永远先处理。旧顺序先做居中/尺寸计算；其中任何异常都会让
            // HandleKeyboard 永远执行不到，恰好形成“黑屏后 Esc 也失效”的软锁。
            try
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard != null) HandleKeyboard(keyboard);
            }
            catch (Exception e)
            {
                LogInputErrorThrottled(e);
            }
            if (!_open || _canvasRoot == null) return;

            if (_pendingInitialCenter)
            {
                try { TryInitialCenter(); }
                catch (Exception e)
                {
                    // 居中失败不应每帧重试，更不能阻塞关闭输入。
                    _pendingInitialCenter = false;
                    LogInputErrorThrottled(e);
                }
            }
            try
            {
                WatchScreenResize();
                UpdateEditConfirmationState();
                UpdateGraphToast();
            }
            catch (Exception e) { LogInputErrorThrottled(e); }
            if (!_open || _canvasRoot == null) return;

            try
            {
                Mouse mouse = Mouse.current;
                if (mouse != null)
                {
                    _lastPointerScreen = mouse.position.ReadValue();
                    if (InspectorResourcePickerOpen) return;
                    // 两种模式都把右键交给上下文菜单。查看模式只提供不会写入
                    // 剧情或工作区的视图操作；关闭仍使用 Esc、关闭按钮或菜单项。
                    bool contextHandled = _editMode
                        ? HandleEditContextMenuInput(mouse)
                        : HandleViewContextMenuInput(mouse);
                    if (!contextHandled)
                    {
                        HandleWheelZoom(mouse);
                        HandlePanInput(mouse);
                    }
                }
            }
            catch (Exception e)
            {
                LogInputErrorThrottled(e);
            }
        }

        /// <summary>输入异常每 5 秒最多记一次日志，避免异常刷屏拖垮游戏。</summary>
        private void LogInputErrorThrottled(Exception e)
        {
            if (Time.unscaledTime - _lastInputErrorLogTime < 5f) return;
            _lastInputErrorLogTime = Time.unscaledTime;
            Plugin.Log?.LogError("[StoryGraph.Input] " + e);
        }

        /// <summary>查看模式处理搜索；编辑模式处理撤销、重做、保存、删除与安全退出。</summary>
        private void HandleKeyboard(Keyboard keyboard)
        {
            // 资源查阅器是模态小窗：文字输入仍交给 InputField，但 Esc 和所有
            // 图级快捷键都先被小窗截获，避免搜索时按 Delete 误删剧情节点。
            if (InspectorResourcePickerOpen)
            {
                if (keyboard.escapeKey.wasPressedThisFrame)
                    CloseInspectorResourcePicker();
                return;
            }
            if (_editMode)
            {
                bool ctrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
                bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                InputField focusedInspector = FocusedInspectorInput();
                if (focusedInspector != null)
                {
                    if (keyboard.escapeKey.wasPressedThisFrame)
                    {
                        focusedInspector.DeactivateInputField();
                        if (EventSystem.current != null)
                            EventSystem.current.SetSelectedGameObject(null);
                    }
                    else if (ctrl && keyboard.sKey.wasPressedThisFrame)
                    {
                        // 所有直接配置字段都已实时写入；Ctrl+S 统一先让当前输入失焦，
                        // 由 onEndEdit 恢复任何无效中间态，然后保存最后有效草稿。
                        focusedInspector.DeactivateInputField();
                        if (EventSystem.current != null)
                            EventSystem.current.SetSelectedGameObject(null);
                        EndInspectorLiveEdit();
                        RefreshInspectorJsonFromSelection();
                        _inspectorBasicPreviewDirty = false;
                        SaveEditSession();
                    }
                    // 输入框聚焦时 Ctrl+C/V、Delete、Z 等交给 InputField，
                    // 绝不能误触图级复制、删除或撤销。
                    return;
                }
                if (keyboard.escapeKey.wasPressedThisFrame)
                {
                    if (_contextMenu != null && _contextMenu.gameObject.activeSelf)
                        HideContextMenu();
                    else if (_boxSelection != null) CancelBoxSelection(true);
                    else if (_groupDrag != null) CancelGroupDrag();
                    else if (_connectionDrag != null) CancelConnectionDrag();
                    else RequestExitEditMode();
                    return;
                }
                if (ctrl && keyboard.cKey.wasPressedThisFrame)
                {
                    CopySelectionToClipboard(shift);
                    return;
                }
                if (ctrl && keyboard.vKey.wasPressedThisFrame)
                {
                    PasteClipboard(DefaultPasteScreenPoint());
                    return;
                }
                if (ctrl && keyboard.aKey.wasPressedThisFrame)
                {
                    SelectAllEditableNodes();
                    return;
                }
                if (ctrl && keyboard.sKey.wasPressedThisFrame)
                {
                    SaveEditSession();
                    return;
                }
                if (ctrl && keyboard.zKey.wasPressedThisFrame)
                {
                    if (shift) RedoEdit(); else UndoEdit();
                    return;
                }
                if (ctrl && keyboard.yKey.wasPressedThisFrame)
                {
                    RedoEdit();
                    return;
                }
                if (keyboard.deleteKey.wasPressedThisFrame)
                {
                    RequestDeleteSelected();
                    return;
                }
                if (keyboard.pKey.wasPressedThisFrame)
                {
                    PreviewGraphTalk(SelectedEditPreviewTalk());
                    return;
                }
                if (keyboard.lKey.wasPressedThisFrame)
                {
                    CycleEdgeLabelMode();
                    return;
                }
                return;
            }

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                if (_contextMenu != null && _contextMenu.gameObject.activeSelf)
                {
                    HideContextMenu();
                }
                else if (_searchInput != null && _searchInput.isFocused)
                {
                    _searchInput.DeactivateInputField();
                    if (EventSystem.current != null)
                        EventSystem.current.SetSelectedGameObject(null);
                }
                else
                {
                    RequestClose();
                }
                return;
            }

            if ((_searchInput == null || !_searchInput.isFocused)
                && keyboard.lKey.wasPressedThisFrame)
            {
                CycleEdgeLabelMode();
                return;
            }

            if ((_searchInput == null || !_searchInput.isFocused)
                && keyboard.pKey.wasPressedThisFrame)
            {
                PreviewGraphTalk(SelectedViewPreviewTalk());
                return;
            }

            if (_searchInput != null && _searchInput.isFocused
                && (keyboard.enterKey.wasPressedThisFrame
                    || keyboard.numpadEnterKey.wasPressedThisFrame))
            {
                bool previous = keyboard.leftShiftKey.isPressed
                                || keyboard.rightShiftKey.isPressed;
                FocusMatch(previous ? -1 : 1);
            }
        }

        /// <summary>指针在视口内（且不在小地图上）时，滚轮以指针为中心缩放。</summary>
        private void HandleWheelZoom(Mouse mouse)
        {
            if (_boxSelection != null || _groupDrag != null) return;
            // 量纲差异：新输入系统 Windows 滚轮每格约 ±120（旧 API 约 ±1）。
            // 归一化为「格」并 clamp，防止触控板高分辨率滚动一帧冲过头。
            float scroll = Mathf.Clamp(
                mouse.scroll.ReadValue().y / 120f, -3f, 3f);
            if (Mathf.Abs(scroll) < 0.01f) return;
            Vector2 pointer = mouse.position.ReadValue(); // 屏幕像素、左下原点，同旧 API
            if (!PointerInViewport(pointer) || PointerOverMinimap(pointer)) return;

            float newZoom = Mathf.Clamp(_zoom * Mathf.Pow(1.15f, scroll),
                MinZoom, MaxZoom);
            if (Mathf.Approximately(newZoom, _zoom)) return;

            // 指针屏幕点在缩放前后对应的内容局部坐标之差，乘以新缩放即应补偿的位移：
            // C += (after - before) * newZoom（推导见类头注释的 v = C + p * zoom）。
            Vector2 before;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _content, pointer, null, out before);
            SetZoom(newZoom);
            Vector2 after;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _content, pointer, null, out after);
            _content.anchoredPosition = ClampPan(
                _content.anchoredPosition + (after - before) * _zoom);
            UpdateMinimapFrame();
        }

        /// <summary>
        /// 查看模式左/中键空白拖动平移；编辑模式左键空白框选、中键平移。
        /// 节点与端口由各自 EventTrigger 处理，不进入这里。
        /// </summary>
        private void HandlePanInput(Mouse mouse)
        {
            if (_connectionDrag != null || _nodeDrag != null || _groupDrag != null) return;
            Vector2 pointer = mouse.position.ReadValue();
            if (_boxSelection != null)
            {
                UpdateBoxSelection(mouse, pointer);
                return;
            }

            bool leftDown = mouse.leftButton.wasPressedThisFrame;
            bool midDown = mouse.middleButton.wasPressedThisFrame;
            bool blankViewport = PointerInViewport(pointer)
                && !PointerOverMinimap(pointer) && !PointerOverNode(pointer)
                && !PointerOverGroup(pointer);
            if (_editMode && leftDown && blankViewport)
            {
                BeginBoxSelection(pointer);
                return;
            }

            bool beginPan = midDown || (!_editMode && leftDown);
            if (beginPan && blankViewport)
            {
                _panning = true;
                _panButton = midDown ? mouse.middleButton : mouse.leftButton;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _viewport, pointer, null, out _lastPanPoint);
            }

            if (!_panning) return;
            if (_panButton == null || !_panButton.isPressed)
            {
                _panning = false;
                _panButton = null;
                return;
            }

            Vector2 current;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _viewport, pointer, null, out current);
            Vector2 delta = current - _lastPanPoint;
            _lastPanPoint = current;
            if (delta.sqrMagnitude > 0f)
            {
                _content.anchoredPosition =
                    ClampPan(_content.anchoredPosition + delta);
                UpdateMinimapFrame();
            }
        }

        private void BeginBoxSelection(Vector2 screenPoint)
        {
            Keyboard keyboard = Keyboard.current;
            bool additive = keyboard != null
                && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed
                    || keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
            var state = new BoxSelectionState
            {
                StartScreen = screenPoint,
                Additive = additive,
                PrimaryKey = StoryGraphWorkspace.StableNodeKey(_selectedNode),
            };
            foreach (string key in _selectedNodeKeys) state.BaseKeys.Add(key);
            _boxSelection = state;
            _panning = false;
            _panButton = null;
            HideTooltip();
            if (_selectionRect != null) _selectionRect.gameObject.SetActive(false);
        }

        private void UpdateBoxSelection(Mouse mouse, Vector2 screenPoint)
        {
            BoxSelectionState state = _boxSelection;
            if (state == null) return;
            if (!mouse.leftButton.isPressed)
            {
                EndBoxSelection(screenPoint);
                return;
            }

            if (!state.Moved
                && (screenPoint - state.StartScreen).sqrMagnitude
                    >= BoxSelectThreshold * BoxSelectThreshold)
                state.Moved = true;
            if (!state.Moved) return;
            UpdateSelectionRectVisual(state.StartScreen, screenPoint);
            ApplyBoxSelection(state, ScreenRect(state.StartScreen, screenPoint));
        }

        private void EndBoxSelection(Vector2 screenPoint)
        {
            BoxSelectionState state = _boxSelection;
            _boxSelection = null;
            if (_selectionRect != null) _selectionRect.gameObject.SetActive(false);
            if (state == null) return;
            if (state.Moved)
                ApplyBoxSelection(state, ScreenRect(state.StartScreen, screenPoint));
            else if (!state.Additive)
            {
                _selectedGroupId = null;
                RefreshGroupVisualStates();
                _selectedNodes.Clear();
                _selectedNodeKeys.Clear();
                _selectedNode = null;
                SyncPrimaryEditSelection();
                RefreshAllNodeStates();
                UpdateEditControls();
            }
            UpdateEditSelectionStatus();
        }

        private void CancelBoxSelection(bool restoreBase)
        {
            BoxSelectionState state = _boxSelection;
            _boxSelection = null;
            if (_selectionRect != null) _selectionRect.gameObject.SetActive(false);
            if (!restoreBase || state == null) return;
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            foreach (StoryGraphDisplayNode node in _viewModel != null
                ? _viewModel.Nodes
                : new List<StoryGraphDisplayNode>())
            {
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key) && state.BaseKeys.Contains(key))
                {
                    _selectedNodes.Add(node);
                    _selectedNodeKeys.Add(key);
                }
            }
            _selectedNode = _selectedNodes.FirstOrDefault(node =>
                string.Equals(StoryGraphWorkspace.StableNodeKey(node),
                    state.PrimaryKey, StringComparison.Ordinal))
                ?? _selectedNodes.FirstOrDefault();
            SyncPrimaryEditSelection();
            RefreshAllNodeStates();
            UpdateEditControls();
            UpdateEditSelectionStatus();
        }

        private void UpdateSelectionRectVisual(Vector2 startScreen, Vector2 endScreen)
        {
            if (_selectionRect == null || _viewport == null) return;
            Vector2 start;
            Vector2 end;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _viewport, startScreen, null, out start)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _viewport, endScreen, null, out end)) return;
            _selectionRect.anchoredPosition = (start + end) * 0.5f;
            _selectionRect.sizeDelta = new Vector2(
                Mathf.Max(1f, Mathf.Abs(end.x - start.x)),
                Mathf.Max(1f, Mathf.Abs(end.y - start.y)));
            _selectionRect.gameObject.SetActive(true);
            _selectionRect.transform.SetAsLastSibling();
        }

        private void ApplyBoxSelection(BoxSelectionState state, Rect screenRect)
        {
            if (state == null) return;
            _selectedGroupId = null;
            RefreshGroupVisualStates();
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            StoryGraphDisplayNode primary = null;
            for (int i = 0; i < _activeNodes.Count; i++)
            {
                NodeVisual visual = _activeNodes[i];
                StoryGraphDisplayNode node = visual != null ? visual.Node : null;
                if (node == null || node.SourceNode == null
                    || (node.SourceNode.Talk == null && node.SourceNode.Option == null))
                    continue;
                string key = StoryGraphWorkspace.StableNodeKey(node);
                bool selected = (state.Additive && state.BaseKeys.Contains(key))
                    || ScreenRectOverlapsNode(screenRect, visual.Rect);
                if (!selected) continue;
                _selectedNodes.Add(node);
                if (!string.IsNullOrEmpty(key)) _selectedNodeKeys.Add(key);
                if (primary == null || string.Equals(
                        key, state.PrimaryKey, StringComparison.Ordinal))
                    primary = node;
            }
            _selectedNode = primary;
            SyncPrimaryEditSelection();
            RefreshAllNodeStates();
            UpdateEditControls();
        }

        private static Rect ScreenRect(Vector2 a, Vector2 b)
        {
            float minX = Mathf.Min(a.x, b.x);
            float minY = Mathf.Min(a.y, b.y);
            return new Rect(minX, minY,
                Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }

        private static bool ScreenRectOverlapsNode(Rect selection, RectTransform node)
        {
            if (node == null) return false;
            var corners = new Vector3[4];
            node.GetWorldCorners(corners);
            float minX = Mathf.Min(corners[0].x, corners[2].x);
            float maxX = Mathf.Max(corners[0].x, corners[2].x);
            float minY = Mathf.Min(corners[0].y, corners[2].y);
            float maxY = Mathf.Max(corners[0].y, corners[2].y);
            return selection.xMin <= maxX && selection.xMax >= minX
                && selection.yMin <= maxY && selection.yMax >= minY;
        }

        private bool PointerInViewport(Vector2 pointer)
        {
            return _viewport != null
                   && RectTransformUtility.RectangleContainsScreenPoint(
                       _viewport, pointer, null);
        }

        private bool PointerOverMinimap(Vector2 pointer)
        {
            return _minimapRoot != null
                   && RectTransformUtility.RectangleContainsScreenPoint(
                       _minimapRoot, pointer, null);
        }

        /// <summary>射线检测指针下是否有节点图形（决定拖拽是平移还是节点交互）。</summary>
        private bool PointerOverNode(Vector2 pointer)
        {
            if (EventSystem.current == null || _nodeContainer == null) return false;
            var data = new PointerEventData(EventSystem.current)
            {
                position = pointer,
            };
            _raycastResults.Clear();
            EventSystem.current.RaycastAll(data, _raycastResults);
            for (int i = 0; i < _raycastResults.Count; i++)
            {
                GameObject go = _raycastResults[i].gameObject;
                if (go != null && go.transform.IsChildOf(_nodeContainer))
                    return true;
            }
            return false;
        }

        private bool HandleViewContextMenuInput(Mouse mouse)
        {
            if (_editMode || mouse == null) return false;
            Vector2 pointer = mouse.position.ReadValue();
            bool menuOpen = _contextMenu != null && _contextMenu.gameObject.activeSelf;
            if (menuOpen)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    if (!PointerOverContextMenu(pointer)) HideContextMenu();
                    // 菜单按钮在同一帧由 EventSystem 触发，不能再让底层画布开始平移。
                    return true;
                }
                if (!mouse.rightButton.wasPressedThisFrame) return true;
                HideContextMenu();
            }

            if (!mouse.rightButton.wasPressedThisFrame) return false;
            // 工具栏/状态栏上的右击不再等价于关闭，也不在无关位置弹菜单。
            if (!PointerInViewport(pointer) || PointerOverMinimap(pointer))
                return true;

            NodeVisual visual = FindNodeVisualAt(pointer);
            if (visual != null && visual.Node != null)
            {
                _selectedNode = visual.Node;
                RefreshAllNodeStates();
                ShowViewNodeContextMenu(pointer, visual.Node);
            }
            else
            {
                ShowViewCanvasContextMenu(pointer);
            }
            return true;
        }

        private bool HandleEditContextMenuInput(Mouse mouse)
        {
            if (!_editMode || mouse == null) return false;
            Vector2 pointer = mouse.position.ReadValue();
            bool menuOpen = _contextMenu != null && _contextMenu.gameObject.activeSelf;
            if (menuOpen)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    if (!PointerOverContextMenu(pointer)) HideContextMenu();
                    // 菜单按钮由 EventSystem 在本帧触发；不让底层画布同时框选。
                    return true;
                }
                if (!mouse.rightButton.wasPressedThisFrame) return true;
                HideContextMenu();
            }
            if (!mouse.rightButton.wasPressedThisFrame) return false;
            if (!PointerInViewport(pointer) || PointerOverMinimap(pointer))
                return true;
            if (PointerOverPort(pointer))
                return true; // 端口自身的 PointerClick 会执行断线。

            NodeVisual visual = FindNodeVisualAt(pointer);
            if (visual != null && visual.Node != null
                && visual.Node.SourceNode != null
                && (visual.Node.SourceNode.Talk != null
                    || visual.Node.SourceNode.Option != null))
            {
                if (!_selectedNodes.Contains(visual.Node))
                    SetEditSelection(visual.Node, false, false);
                ShowNodeContextMenu(pointer);
            }
            else
            {
                GroupVisual group = FindGroupVisualAt(pointer);
                if (group != null)
                {
                    _selectedGroupId = group.Group.Id;
                    _selectedNodes.Clear();
                    _selectedNodeKeys.Clear();
                    _selectedNode = null;
                    SyncPrimaryEditSelection();
                    RefreshGroupVisualStates();
                    RefreshInspector();
                    ShowGroupContextMenu(pointer, group);
                }
                else
                {
                    ShowCanvasContextMenu(pointer);
                }
            }
            return true;
        }

        private bool PointerOverGroup(Vector2 screenPoint)
        {
            return FindGroupVisualAt(screenPoint) != null;
        }

        private GroupVisual FindGroupVisualAt(Vector2 screenPoint)
        {
            for (int i = _activeGroups.Count - 1; i >= 0; i--)
            {
                GroupVisual visual = _activeGroups[i];
                if (visual != null && visual.Root.activeInHierarchy
                    && RectTransformUtility.RectangleContainsScreenPoint(
                        visual.Rect, screenPoint, null)) return visual;
            }
            return null;
        }

        private bool PointerOverPort(Vector2 screenPoint)
        {
            for (int i = _activeNodes.Count - 1; i >= 0; i--)
            {
                NodeVisual node = _activeNodes[i];
                if (node == null) continue;
                for (int p = 0; p < node.Ports.Count; p++)
                {
                    PortVisual port = node.Ports[p];
                    if (port != null && port.Root.activeInHierarchy
                        && RectTransformUtility.RectangleContainsScreenPoint(
                            port.Rect, screenPoint, null)) return true;
                }
            }
            return false;
        }

        private bool PointerOverContextMenu(Vector2 screenPoint)
        {
            return _contextMenu != null && _contextMenu.gameObject.activeSelf
                && RectTransformUtility.RectangleContainsScreenPoint(
                    _contextMenu, screenPoint, null);
        }

        private void HideContextMenu()
        {
            if (_contextMenu != null) _contextMenu.gameObject.SetActive(false);
        }

        private void ShowContextMenu(
            Vector2 screenPoint, IList<ContextMenuItem> items)
        {
            if (_contextMenu == null || _canvasRect == null
                || items == null || items.Count == 0) return;
            const float width = 260f;
            const float itemHeight = 38f;
            const float gap = 3f;
            const float padding = 6f;
            float height = padding * 2f
                + items.Count * itemHeight + (items.Count - 1) * gap;
            _contextMenu.sizeDelta = new Vector2(width, height);
            while (_contextMenuButtons.Count < items.Count)
                CreateContextMenuButton();
            for (int i = 0; i < _contextMenuButtons.Count; i++)
            {
                ContextMenuButtonVisual visual = _contextMenuButtons[i];
                if (i >= items.Count)
                {
                    visual.Root.SetActive(false);
                    continue;
                }
                ContextMenuItem item = items[i];
                visual.Root.SetActive(true);
                visual.Label.text = item.Label ?? string.Empty;
                visual.Button.interactable = item.Enabled;
                visual.Button.onClick.RemoveAllListeners();
                Action action = item.Action;
                visual.Button.onClick.AddListener(delegate
                {
                    HideContextMenu();
                    if (action != null) action();
                });
                Place((RectTransform)visual.Root.transform,
                    0f, 1f, 0f, 1f,
                    padding, -(padding + i * (itemHeight + gap)),
                    width - padding * 2f, itemHeight);
            }

            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRect, screenPoint, null, out local);
            Rect canvas = _canvasRect.rect;
            local.x = Mathf.Clamp(local.x,
                canvas.xMin + 8f, canvas.xMax - width - 8f);
            local.y = Mathf.Clamp(local.y,
                canvas.yMin + height + 8f, canvas.yMax - 8f);
            _contextMenu.anchoredPosition = local;
            _contextMenu.gameObject.SetActive(true);
            _contextMenu.transform.SetAsLastSibling();
        }

        private void ShowCanvasContextMenu(Vector2 screenPoint)
        {
            Vector2 point = screenPoint;
            TalkCfg optionParent = SelectedOptionParentTalk();
            var items = new List<ContextMenuItem>
            {
                new ContextMenuItem { Label = "新建对话", Action = delegate { CreateTalkAt(point); } },
                new ContextMenuItem
                {
                    Label = optionParent != null
                        ? "为所选对话新建选项"
                        : "新建选项（先选择所属对话）",
                    Enabled = optionParent != null,
                    Action = delegate { CreateOptionAt(point); },
                },
                new ContextMenuItem { Label = "新建注释框", Action = delegate { CreateNoteAt(point); } },
                new ContextMenuItem { Label = "粘贴  Ctrl+V", Enabled = _clipboard != null,
                    Action = delegate { PasteClipboard(point); } },
                new ContextMenuItem { Label = "全选  Ctrl+A", Action = SelectAllEditableNodes },
                new ContextMenuItem { Label = "自动整理全部节点",
                    Action = delegate { AutoArrangeSelection(true); } },
                new ContextMenuItem { Label = "快捷键与操作说明", Action = ShowShortcutHelp },
                new ContextMenuItem { Label = "显示全图", Action = FitToView },
            };
            ShowContextMenu(screenPoint, items);
        }

        private void ShowViewCanvasContextMenu(Vector2 screenPoint)
        {
            RefreshEditorFocusIntent();
            var items = new List<ContextMenuItem>
            {
                new ContextMenuItem
                {
                    Label = "定位当前编辑条目",
                    Enabled = FindEditorFocusNode() != null,
                    Action = LocateCurrentEditorSelection,
                },
                new ContextMenuItem
                {
                    Label = "自动整理并保存布局",
                    Enabled = _viewModel != null && _viewModel.Nodes.Count > 0,
                    Action = AutoArrangeReadOnlyView,
                },
                new ContextMenuItem { Label = "显示全图", Action = FitToView },
                new ContextMenuItem
                {
                    Label = "切换连线文字  L",
                    Action = CycleEdgeLabelMode,
                },
                new ContextMenuItem
                {
                    Label = "刷新剧情图",
                    Action = delegate { RefreshGraph(false); },
                },
                new ContextMenuItem { Label = "关闭剧情图  Esc", Action = RequestClose },
            };
            ShowContextMenu(screenPoint, items);
        }

        private void ShowViewNodeContextMenu(
            Vector2 screenPoint, StoryGraphDisplayNode node)
        {
            if (node == null) return;
            RefreshEditorFocusIntent();
            bool container = node.IsSegment || node.IsUnusedGroup;
            bool expanded = container && _viewModel != null
                && _viewModel.IsExpanded(node.Key);
            TalkCfg previewTalk = PreviewTalkForNode(node);
            var items = new List<ContextMenuItem>
            {
                new ContextMenuItem
                {
                    Label = "预览本句  P",
                    Enabled = previewTalk != null,
                    Action = delegate { PreviewGraphTalk(previewTalk); },
                },
                new ContextMenuItem
                {
                    Label = "定位此节点",
                    Action = delegate { FocusViewNode(node, false); },
                },
            };
            if (container)
            {
                string label;
                if (node.IsUnusedGroup)
                    label = expanded ? "收起未使用项" : "展开未使用项";
                else
                    label = expanded ? "收起此链段" : "展开此链段";
                string key = node.Key;
                items.Add(new ContextMenuItem
                {
                    Label = label,
                    Action = delegate { ToggleViewContainer(key); },
                });
            }
            items.Add(new ContextMenuItem
            {
                Label = "在原编辑器中选择对应对话",
                Enabled = node.LocateTalk != null,
                Action = delegate { SelectViewNodeInEditor(node); },
            });
            items.Add(new ContextMenuItem
            {
                Label = "定位当前编辑条目",
                Enabled = FindEditorFocusNode() != null,
                Action = LocateCurrentEditorSelection,
            });
            items.Add(new ContextMenuItem
            {
                Label = "自动整理并保存布局",
                Enabled = _viewModel != null && _viewModel.Nodes.Count > 0,
                Action = AutoArrangeReadOnlyView,
            });
            items.Add(new ContextMenuItem { Label = "显示全图", Action = FitToView });
            items.Add(new ContextMenuItem
            {
                Label = "切换连线文字  L",
                Action = CycleEdgeLabelMode,
            });
            items.Add(new ContextMenuItem
            {
                Label = "关闭剧情图  Esc",
                Action = RequestClose,
            });
            ShowContextMenu(screenPoint, items);
        }

        private void ShowNodeContextMenu(Vector2 screenPoint)
        {
            Vector2 point = screenPoint;
            int count = _selectedNodes.Count;
            TalkCfg selectedTalk = count == 1 ? SelectedEditTalk() : null;
            bool singleTalk = selectedTalk != null;
            var items = new List<ContextMenuItem>
            {
                new ContextMenuItem
                {
                    Label = "预览本句  P",
                    Enabled = singleTalk,
                    Action = delegate { PreviewGraphTalk(selectedTalk); },
                },
                new ContextMenuItem
                {
                    Label = "设为结尾（创建“确定”选项）",
                    Enabled = singleTalk,
                    Action = delegate
                    {
                        CreateEndingOptionAt(
                            SelectedEditTalk(), _selectedNode, point, true);
                    },
                },
                new ContextMenuItem { Label = "复制所选  Ctrl+C",
                    Action = delegate { CopySelectionToClipboard(false); } },
                new ContextMenuItem { Label = "复制完整下游分支  Ctrl+Shift+C",
                    Action = delegate { CopySelectionToClipboard(true); } },
                new ContextMenuItem { Label = "粘贴到这里  Ctrl+V", Enabled = _clipboard != null,
                    Action = delegate { PasteClipboard(point); } },
                new ContextMenuItem { Label = "建立节点分组", Enabled = count > 0,
                    Action = CreateGroupFromSelection },
                new ContextMenuItem { Label = "自动整理所选", Enabled = count > 0,
                    Action = delegate { AutoArrangeSelection(false); } },
                new ContextMenuItem { Label = "左对齐", Enabled = count >= 2,
                    Action = delegate { AlignSelection(NodeAlignment.Left); } },
                new ContextMenuItem { Label = "右对齐", Enabled = count >= 2,
                    Action = delegate { AlignSelection(NodeAlignment.Right); } },
                new ContextMenuItem { Label = "上对齐", Enabled = count >= 2,
                    Action = delegate { AlignSelection(NodeAlignment.Top); } },
                new ContextMenuItem { Label = "下对齐", Enabled = count >= 2,
                    Action = delegate { AlignSelection(NodeAlignment.Bottom); } },
                new ContextMenuItem { Label = "水平等距", Enabled = count >= 3,
                    Action = delegate { DistributeSelection(true); } },
                new ContextMenuItem { Label = "垂直等距", Enabled = count >= 3,
                    Action = delegate { DistributeSelection(false); } },
                new ContextMenuItem { Label = count > 1 ? "批量删除所选" : "删除节点",
                    Enabled = count > 0, Action = RequestDeleteSelected },
            };
            ShowContextMenu(screenPoint, items);
        }

        private void ShowGroupContextMenu(Vector2 screenPoint, GroupVisual visual)
        {
            if (visual == null) return;
            string id = visual.Group.Id;
            var items = new List<ContextMenuItem>
            {
                new ContextMenuItem
                {
                    Label = "在属性检查器中编辑",
                    Action = delegate
                    {
                        _selectedGroupId = id;
                        RefreshGroupVisualStates();
                        ShowInspector();
                    },
                },
                new ContextMenuItem
                {
                    Label = visual.Group.IsNote
                        ? "删除注释框"
                        : "删除分组（保留节点）",
                    Action = delegate { RemoveWorkspaceGroup(id); },
                },
            };
            ShowContextMenu(screenPoint, items);
        }

        /// <summary>屏幕尺寸变化时重排工具栏、钳制主图并重算小地图。</summary>
        private void WatchScreenResize()
        {
            Vector2 screen = new Vector2(Screen.width, Screen.height);
            Vector2 canvasSize = _canvasRect != null
                ? _canvasRect.rect.size
                : Vector2.zero;
            bool screenChanged = screen != _lastScreenSize;
            bool canvasChanged = (canvasSize - _lastCanvasSize).sqrMagnitude > 0.01f;
            if (!screenChanged && !canvasChanged) return;

            _lastScreenSize = screen;
            // 弹层的“右栏左侧/窄屏覆盖”位置来自打开时的逻辑画布尺寸；
            // 分辨率改变后关闭重开，避免旧坐标在过渡帧越界。
            if (InspectorResourcePickerOpen) CloseInspectorResourcePicker();
            Canvas.ForceUpdateCanvases();
            // CanvasScaler 与本组件的 Update 顺序没有保证；同时记录逻辑画布尺寸，
            // 若它下一帧才更新，canvasChanged 会再触发一次重排。
            _lastCanvasSize = _canvasRect != null
                ? _canvasRect.rect.size
                : Vector2.zero;
            UpdateResponsiveToolbarLayout();
            HideTooltip(); // 旧屏幕坐标下的悬浮框不能继续停在原位。
            if (_content != null)
                _content.anchoredPosition = ClampPan(_content.anchoredPosition);
            UpdateMinimapDots();
            UpdateMinimapFrame();
        }

        private void UpdateResponsiveToolbarLayout()
        {
            if (_canvasRect == null) return;
            float width = _canvasRect.rect.width;
            if (width < 1f) return;

            if (_editMode)
            {
                LayoutEditToolbar(width);
                return;
            }
            if (_searchInput == null) return;

            // 常见横屏完整显示全部控件；极窄窗口先隐藏纯提示计数，再隐藏
            // “上个结果”（仍可用 Shift+Enter），保证搜索框和关闭按钮不会重叠。
            bool showLabelMode = width >= 1180f;
            bool showPreview = width >= 1280f;
            bool showNext = width >= 850f;
            bool showPrevious = width >= 1080f;
            bool showCounter = width >= 1360f;
            if (_edgeLabelModeButton != null)
                _edgeLabelModeButton.gameObject.SetActive(showLabelMode);
            if (_previewViewButton != null)
                _previewViewButton.gameObject.SetActive(showPreview);
            if (_nextMatchButton != null)
                _nextMatchButton.gameObject.SetActive(showNext);
            if (_previousMatchButton != null)
                _previousMatchButton.gameObject.SetActive(showPrevious);
            if (_matchCounterText != null)
                _matchCounterText.gameObject.SetActive(showCounter);

            float rightReserve = showCounter ? 424f
                : (showPrevious ? 310f : (showNext ? 210f : 108f));
            RectTransform searchRect = _searchInput.transform as RectTransform;
            if (searchRect == null) return;
            if (_edgeLabelModeButton != null)
            {
                RectTransform labelRect = (RectTransform)_edgeLabelModeButton.transform;
                float left = labelRect.anchoredPosition.x
                             + (showLabelMode ? labelRect.sizeDelta.x + 8f : 0f);
                if (showPreview && _previewViewButton != null)
                {
                    RectTransform previewRect =
                        (RectTransform)_previewViewButton.transform;
                    left = previewRect.anchoredPosition.x
                         + previewRect.sizeDelta.x + 8f;
                }
                searchRect.anchoredPosition = new Vector2(
                    left, searchRect.anchoredPosition.y);
            }
            float searchLeft = searchRect.anchoredPosition.x;
            float searchWidth = Mathf.Clamp(
                width - searchLeft - rightReserve, 140f, 320f);
            searchRect.sizeDelta = new Vector2(searchWidth, ToolbarControlHeight);
        }

        private void LayoutEditToolbar(float width)
        {
            float y = -ToolbarHeight * 0.5f;
            bool showAddOption = width >= 820f;
            bool showPreview = width >= 900f;
            bool showDuplicate = width >= 1120f;
            bool showUndo = width >= 700f;
            bool showRedo = width >= 980f;
            if (_addOptionEditButton != null)
                _addOptionEditButton.gameObject.SetActive(showAddOption);
            if (_duplicateEditButton != null)
                _duplicateEditButton.gameObject.SetActive(showDuplicate);
            if (_previewEditButton != null)
                _previewEditButton.gameObject.SetActive(showPreview);
            if (_undoEditButton != null)
                _undoEditButton.gameObject.SetActive(showUndo);
            if (_redoEditButton != null)
                _redoEditButton.gameObject.SetActive(showRedo);

            float x = 12f;
            LayoutLeftToolbarButton(_returnEditButton, ref x, y, 96f);
            LayoutLeftToolbarButton(_addTalkEditButton, ref x, y, 100f);
            if (showAddOption)
                LayoutLeftToolbarButton(_addOptionEditButton, ref x, y, 100f);
            if (showPreview)
                LayoutLeftToolbarButton(_previewEditButton, ref x, y, 104f);
            if (showDuplicate)
                LayoutLeftToolbarButton(_duplicateEditButton, ref x, y, 72f);
            LayoutLeftToolbarButton(_deleteEditButton, ref x, y, 72f);
            if (showUndo)
                LayoutLeftToolbarButton(_undoEditButton, ref x, y, 72f);
            if (showRedo)
                LayoutLeftToolbarButton(_redoEditButton, ref x, y, 72f);

            // 保存/放弃固定靠右、关闭按钮在它们右侧，低分辨率时仍保留核心出口。
            if (_inspectorToggleButton != null)
                Place((RectTransform)_inspectorToggleButton.transform,
                    1f, 1f, 1f, 0.5f, -284f, y, 96f, ToolbarControlHeight);
            if (_saveEditButton != null)
                Place((RectTransform)_saveEditButton.transform,
                    1f, 1f, 1f, 0.5f, -180f, y, 80f, ToolbarControlHeight);
            if (_discardEditButton != null)
                Place((RectTransform)_discardEditButton.transform,
                    1f, 1f, 1f, 0.5f, -92f, y, 80f, ToolbarControlHeight);
        }

        private static void LayoutLeftToolbarButton(
            Button button, ref float x, float y, float width)
        {
            if (button == null || !button.gameObject.activeSelf) return;
            Place((RectTransform)button.transform,
                0f, 1f, 0f, 0.5f, x, y, width, ToolbarControlHeight);
            x += width + 8f;
        }

        private void UpdateToolbarMode()
        {
            if (_viewToolbarGroup != null) _viewToolbarGroup.SetActive(!_editMode);
            if (_editToolbarGroup != null) _editToolbarGroup.SetActive(_editMode);
            UpdateInspectorMode();
            UpdateResponsiveToolbarLayout();
            RefreshInspector();
        }

        private void UpdateInspectorMode()
        {
            bool visible = _editMode && _inspectorVisible;
            if (_inspectorRoot != null) _inspectorRoot.SetActive(visible);
            if (_viewport != null)
                _viewport.offsetMax = new Vector2(
                    visible ? -InspectorWidth : 0f, -ToolbarHeight);
            float right = (visible ? InspectorWidth : 0f) + 12f;
            if (_minimapRoot != null)
                Place(_minimapRoot, 1f, 0f, 1f, 0f,
                    -right, StatusbarHeight + 12f,
                    MinimapWidth, MinimapHeight);
            if (_minimapBorder != null)
                Place(_minimapBorder, 1f, 0f, 1f, 0f,
                    -(right + 1f), StatusbarHeight + 11f,
                    MinimapWidth + 2f, MinimapHeight + 2f);
            SetButtonLabel(_inspectorToggleButton,
                visible ? "隐藏属性" : "显示属性");
            if (_content != null)
                _content.anchoredPosition = ClampPan(_content.anchoredPosition);
            UpdateMinimapFrame();
        }

        private void ToggleInspector()
        {
            if (!_editMode) return;
            FinalizeFocusedInspectorInput();
            if (_inspectorVisible && InspectorResourcePickerOpen)
                CloseInspectorResourcePicker();
            _inspectorVisible = !_inspectorVisible;
            UpdateInspectorMode();
            if (_inspectorVisible) RefreshInspector();
            _editStatus = _inspectorVisible
                ? "已打开属性栏；配置形成合法内容后会立即热应用到剧情草稿。"
                : "已收起属性栏；可用顶部“显示属性”随时恢复。";
            UpdateStatusBar();
        }

        private void ShowInspector()
        {
            if (!_editMode) return;
            if (!_inspectorVisible)
            {
                _inspectorVisible = true;
                UpdateInspectorMode();
            }
            RefreshInspector();
        }

        // ==================== 画布构建 ====================

        private void EnsureCanvas()
        {
            if (_canvasRoot != null) return;

            // 先取字体再建任何 Text，避免 FindUiFont 找到我们自己建的控件。
            _font = SearchBarUtil.FindUiFont();
            // 程序化生成圆角九宫格贴图（必须先于任何控件创建）。
            BuildSprites();

            _canvasRoot = new GameObject("StoryGraphCanvas",
                typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvasRect = (RectTransform)_canvasRoot.transform;

            // 关键：ScreenSpaceOverlay Canvas 必须是场景顶层对象。旧实现把它
            // SetParent 到 ModEvtEditView 下，Unity 2020 对这种层级明确不保证渲染；
            // 实机表现就是只剩黑色遮罩，工具栏/节点消失。这里保持 parent=null，
            // Close/OnDestroy 负责显式销毁。
            _canvasRoot.layer = _view.gameObject.layer;
            _canvasRoot.hideFlags = HideFlags.DontSave;

            var canvas = _canvasRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = ResolveCanvasSortingOrder();

            var scaler = _canvasRoot.GetComponent<CanvasScaler>();
            ConfigureCanvasScaling(canvas, scaler);
            if (_graphToastSink == null) _graphToastSink = TryShowGraphToast;
            StoryGraphToastRouter.Register(_graphToastSink);
            // 打开剧情图的那次点击可能在 pointer-down 阶段先触发输入框
            // onEndEdit 的校验提示（早于此处注册），把 1.5 秒内被回落的
            // 最后一条补发到图内，否则它会被 Overlay 盖住整个生命周期。
            StoryGraphToastRouter.ReplayRecentFallback(1.5f);
            // 先让根 Canvas 得到正确逻辑尺寸，工具栏才能按实际可用宽度自适应。
            Canvas.ForceUpdateCanvases();

            // 全屏暖色纸底 #F3F1EF：实心盖住背后编辑器，raycastTarget=true 挡输入。
            Image dimmer = CreateImage(_canvasRoot, "Dimmer", PaperBg, true);
            Stretch(dimmer.rectTransform, 0f, 0f, 0f, 0f);

            // 创建顺序即渲染顺序：视口在最底，tooltip 在最顶。
            BuildViewport();
            BuildToolbar();
            BuildStatusbar();
            BuildInspector();
            BuildMinimap();
            BuildTooltip();
            BuildContextMenu();

            Canvas.ForceUpdateCanvases();
            UpdateResponsiveToolbarLayout();
            SetZoom(1f);
            _content.anchoredPosition = Vector2.zero;

            if (!_hasLoggedUiMetrics)
            {
                _hasLoggedUiMetrics = true;
                Plugin.Log?.LogInfo(
                    "[StoryGraph.UI] 屏幕=" + Screen.width + "x" + Screen.height
                    + "，参考分辨率=" + scaler.referenceResolution.x + "x"
                    + scaler.referenceResolution.y + "，CanvasScale="
                    + scaler.scaleFactor.ToString("0.###") + "，逻辑画布="
                    + _canvasRect.rect.width.ToString("0.#") + "x"
                    + _canvasRect.rect.height.ToString("0.#"));
            }
        }

        private static int ResolveCanvasSortingOrder()
        {
            try
            {
                // 排序只在 Overlay 画布之间比较（例如游戏的 Blocker=29999）；
                // 相机空间的游戏 UI 与 ToastView 永远在 Overlay 之下，与该值
                // 无关。取 Foreground-1 仅为与游戏语义保持可读的相对位置。
                int foregroundOrder =
                    UIMgr.GetLayerSortingOrder(UILayerType.Foreground);
                if (foregroundOrder > int.MinValue)
                    return foregroundOrder - 1;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.UI] 读取 Foreground 排序失败，使用兼容兜底："
                    + e.GetType().Name + ": " + e.Message);
            }
            return FallbackCanvasSortingOrder;
        }

        private static void ConfigureCanvasScaling(Canvas canvas, CanvasScaler scaler)
        {
            Canvas sourceCanvas = null;
            CanvasScaler sourceScaler = null;
            try
            {
                sourceCanvas = UIMgr.Canvas;
                if (sourceCanvas != null)
                    sourceScaler = sourceCanvas.GetComponent<CanvasScaler>();
            }
            catch
            {
                // 极早期或游戏版本变化时走下方可读性优先的兜底参数。
            }

            if (sourceCanvas != null)
            {
                canvas.targetDisplay = sourceCanvas.targetDisplay;
                canvas.pixelPerfect = sourceCanvas.pixelPerfect;
                canvas.sortingLayerID = sourceCanvas.sortingLayerID;
                canvas.additionalShaderChannels = sourceCanvas.additionalShaderChannels;
            }

            if (sourceScaler == null)
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
                return;
            }

            // 不猜测游戏预制体究竟使用 1920×1080 还是 2560×1440；直接复制
            // 当前实际根 Canvas 的缩放策略，分辨率、窗口模式和 UI 设置都保持一致。
            scaler.uiScaleMode = sourceScaler.uiScaleMode;
            scaler.scaleFactor = sourceScaler.scaleFactor;
            scaler.referencePixelsPerUnit = sourceScaler.referencePixelsPerUnit;
            scaler.referenceResolution = sourceScaler.referenceResolution;
            scaler.screenMatchMode = sourceScaler.screenMatchMode;
            scaler.matchWidthOrHeight = sourceScaler.matchWidthOrHeight;
            scaler.physicalUnit = sourceScaler.physicalUnit;
            scaler.fallbackScreenDPI = sourceScaler.fallbackScreenDPI;
            scaler.defaultSpriteDPI = sourceScaler.defaultSpriteDPI;
            scaler.dynamicPixelsPerUnit = sourceScaler.dynamicPixelsPerUnit;
        }

        private void BuildViewport()
        {
            GameObject vp = CreateUIObject("Viewport", _canvasRoot.transform);
            _viewport = (RectTransform)vp.transform;
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.pivot = new Vector2(0.5f, 0.5f);
            _viewport.offsetMin = new Vector2(0f, StatusbarHeight);
            _viewport.offsetMax = new Vector2(0f, -ToolbarHeight);

            var bg = vp.AddComponent<Image>();
            bg.color = PaperBg; // 视口与全屏底同色，图纸感无缝
            bg.raycastTarget = true;
            vp.AddComponent<RectMask2D>();

            GameObject contentObj = CreateUIObject("Content", _viewport);
            _content = (RectTransform)contentObj.transform;
            _content.anchorMin = new Vector2(0.5f, 0.5f);
            _content.anchorMax = new Vector2(0.5f, 0.5f);
            _content.pivot = new Vector2(0f, 1f); // pivot 在左上：C 即左上角偏移
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = new Vector2(100f, 100f);

            GameObject groupObj = CreateUIObject("GroupContainer", _content);
            _groupContainer = (RectTransform)groupObj.transform;
            Stretch(_groupContainer, 0f, 0f, 0f, 0f);

            GameObject edgeObj = CreateUIObject("EdgeContainer", _content);
            _edgeContainer = (RectTransform)edgeObj.transform;
            Stretch(_edgeContainer, 0f, 0f, 0f, 0f);

            GameObject nodeObj = CreateUIObject("NodeContainer", _content);
            _nodeContainer = (RectTransform)nodeObj.transform;
            Stretch(_nodeContainer, 0f, 0f, 0f, 0f);

            // 拖线预览位于节点层之上，但不接收射线；只在编辑拖拽期间启用。
            _connectionPreview = CreatePlainImage(_content, "ConnectionPreview");
            _connectionPreview.color = AccentOrange;
            _connectionPreview.raycastTarget = false;
            _connectionPreview.gameObject.SetActive(false);

            Image selection = CreateImage(vp, "SelectionRect",
                new Color(AccentOrange.r, AccentOrange.g, AccentOrange.b, 0.14f), false);
            _selectionRect = selection.rectTransform;
            Place(_selectionRect, 0.5f, 0.5f, 0.5f, 0.5f,
                0f, 0f, 1f, 1f);
            Color selectionBorder = new Color(
                AccentOrange.r, AccentOrange.g, AccentOrange.b, 0.9f);
            CreateFrameEdge(selection.gameObject, selectionBorder, true, false);
            CreateFrameEdge(selection.gameObject, selectionBorder, false, false);
            CreateFrameEdge(selection.gameObject, selectionBorder, true, true);
            CreateFrameEdge(selection.gameObject, selectionBorder, false, true);
            selection.gameObject.SetActive(false);

            _emptyText = CreateText(vp, "EmptyMessage", 18, FontStyle.Normal,
                SubtitleColor, TextAnchor.MiddleCenter);
            Stretch(_emptyText.rectTransform, 40f, 40f, 40f, 40f);
            _emptyText.gameObject.SetActive(false);
        }

        private void BuildToolbar()
        {
            GameObject bar = CreateUIObject("Toolbar", _canvasRoot.transform);
            var rt = (RectTransform)bar.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, ToolbarHeight);
            var bg = bar.AddComponent<Image>();
            bg.color = PanelBg;
            bg.raycastTarget = true;

            Image separator = CreateImage(bar, "Separator", PanelBorder, false);
            separator.rectTransform.anchorMin = new Vector2(0f, 0f);
            separator.rectTransform.anchorMax = new Vector2(1f, 0f);
            separator.rectTransform.pivot = new Vector2(0.5f, 0f);
            separator.rectTransform.anchoredPosition = Vector2.zero;
            separator.rectTransform.sizeDelta = new Vector2(0f, 1f);

            float y = -ToolbarHeight * 0.5f;
            _viewToolbarGroup = CreateUIObject("ViewControls", bar.transform);
            Stretch((RectTransform)_viewToolbarGroup.transform, 0f, 0f, 0f, 0f);
            _editToolbarGroup = CreateUIObject("EditControls", bar.transform);
            Stretch((RectTransform)_editToolbarGroup.transform, 0f, 0f, 0f, 0f);

            float x = 12f;
            CreateToolbarButton(_viewToolbarGroup, "编辑剧情", x, y, 92f, EnterEditMode);
            x += 92f + 8f;
            CreateToolbarButton(_viewToolbarGroup, "刷新", x, y, 72f,
                delegate { RefreshGraph(false); });
            x += 72f + 8f;
            CreateToolbarButton(_viewToolbarGroup, "显示全图", x, y, 96f, FitToView);
            x += 96f + 8f;
            CreateToolbarButton(_viewToolbarGroup, "＋", x, y, 44f,
                delegate { ZoomStep(1.25f); });
            x += 44f + 4f;

            _zoomText = CreateText(_viewToolbarGroup, "ZoomLabel", SecondaryFontSize,
                FontStyle.Normal, SubtitleColor, TextAnchor.MiddleCenter);
            Place(_zoomText.rectTransform, 0f, 1f, 0f, 0.5f,
                x, y, 54f, ToolbarControlHeight);
            x += 54f + 4f;
            CreateToolbarButton(_viewToolbarGroup, "－", x, y, 44f,
                delegate { ZoomStep(0.8f); });
            x += 44f + 8f;
            _edgeLabelModeButton = CreateToolbarButton(
                _viewToolbarGroup, "连线：关键", x, y, 108f, CycleEdgeLabelMode);
            x += 108f + 8f;

            _previewViewButton = CreateToolbarButton(
                _viewToolbarGroup, "预览本句", x, y, 104f,
                delegate { PreviewGraphTalk(SelectedViewPreviewTalk()); });
            x += 104f + 8f;

            _searchInput = CreateSearchField(
                _viewToolbarGroup, "搜索编号或文字，回车定位…", x, y, 320f);

            _nextMatchButton = CreateToolbarButton(
                _viewToolbarGroup, "下个结果", -92f, y, 94f,
                delegate { FocusMatch(1); }, true);
            _previousMatchButton = CreateToolbarButton(
                _viewToolbarGroup, "上个结果", -194f, y, 94f,
                delegate { FocusMatch(-1); }, true);
            _matchCounterText = CreateText(_viewToolbarGroup, "MatchCounter",
                SecondaryFontSize, FontStyle.Normal, SubtitleColor, TextAnchor.MiddleRight);
            Place(_matchCounterText.rectTransform,
                1f, 1f, 1f, 0.5f, -296f, y, 108f, ToolbarControlHeight);

            _returnEditButton = CreateToolbarButton(
                _editToolbarGroup, "返回查看", 12f, y, 96f, RequestExitEditMode);
            _addTalkEditButton = CreateToolbarButton(
                _editToolbarGroup, "新增对话", 116f, y, 100f, AddTalkFromToolbar);
            _addOptionEditButton = CreateToolbarButton(
                _editToolbarGroup, "新增选项", 224f, y, 100f, AddOptionFromToolbar);
            _previewEditButton = CreateToolbarButton(
                _editToolbarGroup, "预览本句", 332f, y, 104f,
                delegate { PreviewGraphTalk(SelectedEditPreviewTalk()); });
            _duplicateEditButton = CreateToolbarButton(
                _editToolbarGroup, "复制", 444f, y, 72f, DuplicateSelected);
            _deleteEditButton = CreateToolbarButton(
                _editToolbarGroup, "删除", 524f, y, 72f, RequestDeleteSelected);
            _undoEditButton = CreateToolbarButton(
                _editToolbarGroup, "撤销", 604f, y, 72f, UndoEdit);
            _redoEditButton = CreateToolbarButton(
                _editToolbarGroup, "重做", 684f, y, 72f, RedoEdit);
            _inspectorToggleButton = CreateToolbarButton(
                _editToolbarGroup, "隐藏属性", -284f, y, 96f,
                ToggleInspector, true);
            _saveEditButton = CreateToolbarButton(
                _editToolbarGroup, "保存", -180f, y, 80f, SaveEditSession, true);
            _discardEditButton = CreateToolbarButton(
                _editToolbarGroup, "放弃", -92f, y, 80f, RequestDiscardEdit, true);

            // 关闭按钮不属于任一模式；有未保存草稿时 RequestClose 会拒绝关闭。
            CreateToolbarButton(bar, "关闭", -12f, y, 72f, RequestClose, true);

            UpdateToolbarMode();
            UpdateResponsiveToolbarLayout();
            UpdateSearchNavigationControls();
            UpdateEditControls();
        }

        private void BuildStatusbar()
        {
            GameObject bar = CreateUIObject("Statusbar", _canvasRoot.transform);
            var rt = (RectTransform)bar.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, StatusbarHeight);
            var bg = bar.AddComponent<Image>();
            bg.color = PanelBg;
            bg.raycastTarget = true;

            // 顶边 1px 分隔线 #C6B187
            Image separator = CreateImage(bar, "Separator", PanelBorder, false);
            separator.rectTransform.anchorMin = new Vector2(0f, 1f);
            separator.rectTransform.anchorMax = new Vector2(1f, 1f);
            separator.rectTransform.pivot = new Vector2(0.5f, 1f);
            separator.rectTransform.anchoredPosition = Vector2.zero;
            separator.rectTransform.sizeDelta = new Vector2(0f, 1f);

            _statusText = CreateText(bar, "Status", SecondaryFontSize, FontStyle.Normal,
                SubtitleColor, TextAnchor.MiddleLeft);
            Stretch(_statusText.rectTransform, 10f, 0f, 10f, 0f);
        }

        private void BuildInspector()
        {
            BuildTabbedInspector();
        }


        private Text CreateInspectorLabel(GameObject parent, string text, float y)
        {
            Text label = CreateText(parent, "Label_" + text, SecondaryFontSize,
                FontStyle.Bold, TitleColor, TextAnchor.MiddleLeft);
            label.text = text;
            Place(label.rectTransform, 0f, 1f, 0f, 1f,
                8f, y, InspectorWidth - 42f, 22f);
            return label;
        }

        private InputField CreateInspectorFieldRow(
            GameObject parent, string label, float y,
            out GameObject row, out Text title)
        {
            row = CreateUIObject("Row_" + label, parent.transform);
            RectTransform rect = (RectTransform)row.transform;
            Place(rect, 0f, 1f, 0f, 1f,
                8f, y, InspectorWidth - 42f, 62f);
            title = CreateText(row, "Label", SecondaryFontSize,
                FontStyle.Bold, TitleColor, TextAnchor.MiddleLeft);
            title.text = label;
            Place(title.rectTransform, 0f, 1f, 0f, 1f,
                0f, 0f, InspectorWidth - 42f, 22f);
            return CreateInspectorInput(row, "Input", -24f, 36f, false,
                InspectorWidth - 42f);
        }

        private void SetInspectorPlaceholder(InputField input, string value)
        {
            if (input == null) return;
            // RefreshInspectorSelection 会按当前节点语义反复更新提示。旧实现每次
            // 都新建一个 Text，InputField 只会管理最后一个，之前的提示仍留在
            // 输入框里持续绘制，最终就会叠成一团。占位文本属于控件本身，应当
            // 像 textComponent 一样只创建一次，之后只更新内容与样式。
            Text placeholder = input.placeholder as Text;
            if (placeholder == null
                || placeholder.transform.parent != input.transform)
            {
                Transform existing = input.transform.Find("Placeholder");
                placeholder = existing != null
                    ? existing.GetComponent<Text>()
                    : null;
            }
            if (placeholder == null)
            {
                placeholder = CreateText(input.gameObject, "Placeholder",
                    SecondaryFontSize, FontStyle.Normal,
                    new Color(0.42f, 0.34f, 0.25f, 0.62f),
                    input.lineType == InputField.LineType.MultiLineNewline
                        ? TextAnchor.UpperLeft
                        : TextAnchor.MiddleLeft);
            }
            placeholder.fontSize = SecondaryFontSize;
            placeholder.fontStyle = FontStyle.Normal;
            placeholder.color = new Color(0.42f, 0.34f, 0.25f, 0.62f);
            placeholder.alignment =
                input.lineType == InputField.LineType.MultiLineNewline
                    ? TextAnchor.UpperLeft
                    : TextAnchor.MiddleLeft;
            placeholder.text = value ?? string.Empty;
            placeholder.horizontalOverflow = HorizontalWrapMode.Wrap;
            placeholder.verticalOverflow = VerticalWrapMode.Truncate;
            Stretch(placeholder.rectTransform, 9f, 6f, 9f, 6f);
            input.placeholder = placeholder;
        }

        private InputField CreateInspectorInput(
            GameObject parent, string name, float y, float height,
            bool multiline, float width = InspectorWidth - 42f)
        {
            GameObject go = CreateUIObject(name, parent.transform);
            RectTransform rect = (RectTransform)go.transform;
            Place(rect, 0f, 1f, 0f, 1f, 8f, y, width, height);
            Image image = go.AddComponent<Image>();
            image.color = HexColor("F7F1E3");
            image.raycastTarget = true;
            ApplySprite(image, _inputSprite);
            go.AddComponent<RectMask2D>();
            InputField input = go.AddComponent<InputField>();
            input.contentType = InputField.ContentType.Standard;
            input.lineType = multiline
                ? InputField.LineType.MultiLineNewline
                : InputField.LineType.SingleLine;
            input.caretColor = HexColor("824C24");
            input.selectionColor = new Color(0.89f, 0.62f, 0.29f, 0.4f);
            Text text = CreateText(go, "Text", SecondaryFontSize,
                FontStyle.Normal, BodyTextColor,
                multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = multiline
                ? VerticalWrapMode.Overflow
                : VerticalWrapMode.Truncate;
            Stretch(text.rectTransform, 9f, 6f, 9f, 6f);
            input.textComponent = text;
            return input;
        }

        private void ClearInspectorReferences()
        {
            _inspectorRoot = null;
            _inspectorContent = null;
            _inspectorTitle = null;
            _inspectorInfo = null;
            _inspectorContentLabel = null;
            _inspectorShowLabel = null;
            _inspectorTagLabel = null;
            _inspectorContentInput = null;
            _inspectorShowInput = null;
            _inspectorTagInput = null;
            _inspectorJsonInput = null;
            _inspectorTagRow = null;
            _inspectorCloseButton = null;
            ClearPerformanceInspectorReferences();
        }

        private void RefreshInspector()
        {
            if (_inspectorRoot == null || _inspectorTitle == null) return;
            if (InspectorResourcePickerOpen) CloseInspectorResourcePicker();
            bool visible = _editMode && _inspectorVisible;
            _inspectorRoot.SetActive(visible);
            if (!visible) return;
            _inspectorShowingHelp = false;
            _inspectorBasicPreviewDirty = false;
            RefreshPerformanceInspector();
            StoryGraphWorkspace.GroupData selectedGroup =
                _workspace != null && !string.IsNullOrEmpty(_selectedGroupId)
                    ? _workspace.FindGroup(_selectedGroupId)
                    : null;
            if (!string.IsNullOrEmpty(_selectedGroupId) && selectedGroup == null)
                _selectedGroupId = null;
            if (selectedGroup != null)
            {
                SetInspectorInteractable(true);
                _inspectorJsonInput.interactable = false;
                _inspectorTitle.text = selectedGroup.IsNote
                    ? "注释框"
                    : "节点分组";
                _inspectorInfo.text = selectedGroup.IsNote
                    ? "独立作者注释，不写入剧情 JSON；拖动框体可移动。"
                    : "包含 " + (selectedGroup.NodeKeys != null
                        ? selectedGroup.NodeKeys.Count : 0)
                      + " 个稳定节点键；拖动框体可整体移动成员。";
                _inspectorContentLabel.text = "注释内容";
                if (_inspectorShowLabel != null) _inspectorShowLabel.text = "标题";
                SetInspectorText(_inspectorContentInput, selectedGroup.Note);
                SetInspectorText(_inspectorShowInput, selectedGroup.Title);
                SetInspectorText(_inspectorSpeakerInput, string.Empty);
                SetInspectorText(_inspectorTagInput, string.Empty);
                SetInspectorText(_inspectorNextEventInput, string.Empty);
                SetInspectorText(_inspectorJsonInput,
                    JsonConvert.SerializeObject(selectedGroup, Formatting.Indented));
                if (_inspectorSpeakerRow != null) _inspectorSpeakerRow.SetActive(false);
                if (_inspectorShowRow != null) _inspectorShowRow.SetActive(true);
                if (_inspectorTagRow != null) _inspectorTagRow.SetActive(false);
                if (_inspectorNextEventRow != null)
                    _inspectorNextEventRow.SetActive(false);
                if (_inspectorNextEventHint != null)
                    _inspectorNextEventHint.gameObject.SetActive(false);
                RelayoutBasicInspector();
                RelayoutInspectorHeader();
                return;
            }

            _inspectorContentLabel.text = "正文 content（实时）";
            if (_inspectorSpeakerLabel != null)
                _inspectorSpeakerLabel.text = "对话角色 roleIds";
            if (_inspectorTagLabel != null)
                _inspectorTagLabel.text = "记录标签 tag（可选）";
            StoryGraphDisplayNode node = _selectedNode;
            bool single = node != null && _selectedNodes.Count <= 1
                && node.SourceNode != null
                && (node.SourceNode.Talk != null || node.SourceNode.Option != null);
            SetInspectorInteractable(single);
            if (!single)
            {
                int count = _selectedNodes.Count;
                _inspectorTitle.text = count > 1
                    ? "已选择 " + count + " 个节点"
                    : "属性检查器";
                _inspectorInfo.text = count > 1
                    ? "多选状态可批量移动、删除、对齐和整理；字段编辑需要保留单个节点。"
                    : "选择一个真实对话或选项，即可在这里编辑完整内容，不会遮挡端口。";
                SetInspectorText(_inspectorContentInput, string.Empty);
                SetInspectorText(_inspectorSpeakerInput, string.Empty);
                SetInspectorText(_inspectorShowInput, string.Empty);
                SetInspectorText(_inspectorTagInput, string.Empty);
                SetInspectorText(_inspectorNextEventInput, string.Empty);
                SetInspectorText(_inspectorJsonInput, string.Empty);
                if (_inspectorSpeakerRow != null) _inspectorSpeakerRow.SetActive(false);
                if (_inspectorShowRow != null) _inspectorShowRow.SetActive(false);
                if (_inspectorTagRow != null) _inspectorTagRow.SetActive(false);
                if (_inspectorNextEventRow != null)
                    _inspectorNextEventRow.SetActive(false);
                if (_inspectorNextEventHint != null)
                    _inspectorNextEventHint.gameObject.SetActive(false);
                RelayoutBasicInspector();
                RelayoutInspectorHeader();
                return;
            }

            EvtStoryGraphNode source = node.SourceNode;
            string flags = BuildFlagDescriptions(node);
            if (source.Talk != null)
            {
                _inspectorTitle.text = "对话 " + source.Talk.id;
                _inspectorInfo.text = (source.Subtitle ?? string.Empty)
                    + (string.IsNullOrWhiteSpace(flags)
                        ? string.Empty
                        : "\n提示：" + flags.Replace("\n", "；"));
                SetInspectorText(_inspectorContentInput, source.Talk.content);
                SetInspectorText(_inspectorSpeakerInput,
                    FormatSpeakerRoleIds(source.Talk.roleIds));
                SetInspectorText(_inspectorShowInput, string.Empty);
                SetInspectorText(_inspectorTagInput, string.Empty);
                SetInspectorText(_inspectorNextEventInput, string.Empty);
                SetInspectorText(_inspectorJsonInput,
                    JsonConvert.SerializeObject(source.Talk, Formatting.Indented));
                if (_inspectorSpeakerRow != null) _inspectorSpeakerRow.SetActive(true);
                if (_inspectorShowRow != null) _inspectorShowRow.SetActive(false);
                if (_inspectorTagRow != null) _inspectorTagRow.SetActive(false);
                if (_inspectorNextEventRow != null)
                    _inspectorNextEventRow.SetActive(false);
                if (_inspectorNextEventHint != null)
                    _inspectorNextEventHint.gameObject.SetActive(false);
            }
            else
            {
                int key = _editSession != null
                    ? _editSession.FindOptionKey(source.Option)
                    : source.Id;
                _inspectorTitle.text = "选项 " + key;
                _inspectorInfo.text = (source.Subtitle ?? string.Empty)
                    + (string.IsNullOrWhiteSpace(flags)
                        ? string.Empty
                        : "\n提示：" + flags.Replace("\n", "；"));
                SetInspectorText(_inspectorContentInput, source.Option.content);
                SetInspectorText(_inspectorSpeakerInput, string.Empty);
                SetInspectorText(_inspectorShowInput, string.Empty);
                SetInspectorText(_inspectorTagInput, source.Option.tag);
                EnsurePerformanceResourceNames();
                SetInspectorText(_inspectorNextEventInput,
                    source.Option.nextEvtId.ToString(
                        CultureInfo.InvariantCulture));
                SetInspectorText(_inspectorJsonInput,
                    JsonConvert.SerializeObject(source.Option, Formatting.Indented));
                if (_inspectorSpeakerRow != null) _inspectorSpeakerRow.SetActive(false);
                if (_inspectorShowRow != null) _inspectorShowRow.SetActive(false);
                if (_inspectorTagRow != null) _inspectorTagRow.SetActive(true);
                if (_inspectorNextEventRow != null)
                    _inspectorNextEventRow.SetActive(true);
                if (_inspectorNextEventHint != null)
                    _inspectorNextEventHint.gameObject.SetActive(true);
                RefreshOptionNextEventHint(source.Option);
            }
            RelayoutBasicInspector();
            RelayoutInspectorHeader();
        }

        private void SetInspectorInteractable(bool value)
        {
            if (_inspectorContentInput != null) _inspectorContentInput.interactable = value;
            if (_inspectorSpeakerInput != null) _inspectorSpeakerInput.interactable = value;
            if (_inspectorShowInput != null) _inspectorShowInput.interactable = value;
            if (_inspectorTagInput != null) _inspectorTagInput.interactable = value;
            if (_inspectorJsonInput != null) _inspectorJsonInput.interactable = value;
            SetPerformanceInspectorInteractable(value);
        }

        private void SetInspectorText(InputField input, string value)
        {
            if (input == null) return;
            // 实时写入触发图重建时，检查器对象仍然存活；不要用规范化文本覆盖
            // 正在输入的字符或重置光标。失焦后再统一恢复/格式化最后有效值。
            if (input.isFocused && !string.IsNullOrEmpty(_inspectorLiveEditKey)) return;
            _settingInspectorText = true;
            try { input.SetTextWithoutNotify(value ?? string.Empty); }
            finally { _settingInspectorText = false; }
        }

        private InputField FocusedInspectorInput()
        {
            InputField[] inputs =
            {
                _inspectorContentInput, _inspectorSpeakerInput,
                _inspectorShowInput, _inspectorTagInput, _inspectorJsonInput,
            };
            return inputs.Concat(PerformanceInspectorInputs())
                .FirstOrDefault(input => input != null && input.isFocused);
        }

        /// <summary>
        /// 切页、隐藏属性栏或保存前主动结束当前输入。有效文本早已由
        /// onValueChanged 热应用；这里主要触发 onEndEdit，把无效中间态恢复为
        /// 草稿中的最后有效值，并关闭本次连续输入的撤销事务。
        /// </summary>
        private void FinalizeFocusedInspectorInput()
        {
            InputField focused = FocusedInspectorInput();
            if (focused != null)
            {
                focused.DeactivateInputField();
                if (EventSystem.current != null
                    && ReferenceEquals(
                        EventSystem.current.currentSelectedGameObject,
                        focused.gameObject))
                    EventSystem.current.SetSelectedGameObject(null);
            }
            EndInspectorLiveEdit();
        }

        private void OnInspectorBasicValueChanged(string unused)
        {
            if (_settingInspectorText || !_editMode || _editSession == null) return;

            StoryGraphWorkspace.GroupData group =
                _workspace != null && !string.IsNullOrEmpty(_selectedGroupId)
                    ? _workspace.FindGroup(_selectedGroupId)
                    : null;
            if (group != null)
            {
                UpdateWorkspaceGroupTextLive(group);
                return;
            }

            if (_selectedNode == null || _selectedNodes.Count > 1
                || _selectedNode.SourceNode == null) return;
            EvtStoryGraphNode source = _selectedNode.SourceNode;
            object target = (object)source.Talk ?? source.Option;
            if (target == null) return;
            if (_inspectorLiveEditKey == null
                || !ReferenceEquals(_inspectorLiveEditTarget, target)
                || !_inspectorLiveEditKey.StartsWith(
                    "inspector-basic:", StringComparison.Ordinal))
            {
                EndInspectorLiveEdit();
                _inspectorLiveEditTarget = target;
                _inspectorLiveEditKey = "inspector-basic:"
                    + (++_inspectorLiveEditSerial).ToString();
            }

            bool wasRuntimeBlank = source.Talk != null
                && string.IsNullOrWhiteSpace(source.Talk.content);
            bool historyRecorded;
            string message;
            bool changed;
            if (source.Talk != null)
            {
                changed = _editSession.TryUpdateTalkFieldsLive(
                    source.Talk,
                    _inspectorContentInput.text,
                    source.Talk.roleName,
                    source.Talk.showTxt,
                    _inspectorLiveEditKey,
                    out historyRecorded, out message);
            }
            else
            {
                changed = _editSession.TryUpdateOptionFieldsLive(
                    source.Option,
                    _inspectorContentInput.text,
                    source.Option.showTxt,
                    _inspectorTagInput.text,
                    _inspectorLiveEditKey,
                    out historyRecorded, out message);
            }
            if (!changed) return;

            if (historyRecorded)
            {
                _editActionTimeline.Push(false);
                _editRedoTimeline.Clear();
                _workspaceRedo.Clear();
            }
            _inspectorBasicPreviewDirty = true;
            // Talk 正文是否为空会改变 Option 流的运行时有效性。只在空/非空边界
            // 完整重建一次，避免每个字符都重排，同时立即消除陈旧灰线和结束标记。
            bool runtimeSemanticsChanged = source.Talk != null
                && wasRuntimeBlank != string.IsNullOrWhiteSpace(source.Talk.content);
            if (runtimeSemanticsChanged)
                RefreshGraph(false);
            else
                RefreshLiveNodePreview(target);
            _inspectorBasicPreviewDirty = true;
            _editStatus = message + " 同一次连续输入只占一条撤销记录。";
            UpdateEditControls();
            UpdateStatusBar();
        }

        private void OnInspectorBasicEndEdit(string unused)
        {
            if (_settingInspectorText || !_editMode) return;
            StoryGraphWorkspace.GroupData group =
                _workspace != null && !string.IsNullOrEmpty(_selectedGroupId)
                    ? _workspace.FindGroup(_selectedGroupId)
                    : null;
            if (group != null)
            {
                bool groupChanged = _inspectorBasicPreviewDirty;
                EndWorkspaceLiveFieldEdit();
                if (!groupChanged) return;
                StoryGraphWorkspace.GroupData current = _workspace.FindGroup(group.Id);
                if (current != null)
                {
                    SetInspectorText(_inspectorShowInput, current.Title);
                    SetInspectorText(_inspectorContentInput, current.Note);
                }
                _inspectorBasicPreviewDirty = false;
                _editStatus = "分组/注释文字已实时写入作者工作区；Ctrl+Z 可一次撤销本次连续输入。";
                UpdateStatusBar();
                return;
            }

            bool changed = _inspectorBasicPreviewDirty;
            EndInspectorLiveEdit();
            if (!changed) return;
            RefreshInspectorJsonFromSelection();
            _inspectorBasicPreviewDirty = false;
            _editStatus = "基础字段已实时写入剧情草稿；Ctrl+Z 可一次撤销本次连续输入。";
            UpdateStatusBar();
        }

        private void EndInspectorLiveEdit()
        {
            if (_editSession != null && !string.IsNullOrEmpty(_inspectorLiveEditKey))
                _editSession.EndLiveFieldEdit(_inspectorLiveEditKey);
            _inspectorLiveEditKey = null;
            _inspectorLiveEditTarget = null;
            EndWorkspaceLiveFieldEdit();
        }

        private void EnsureWorkspaceLiveFieldEdit(string groupId)
        {
            if (!string.IsNullOrEmpty(_workspaceLiveEditKey)
                && string.Equals(_workspaceLiveEditGroupId, groupId,
                    StringComparison.Ordinal)) return;

            EndWorkspaceLiveFieldEdit();
            if (_editSession != null && !string.IsNullOrEmpty(_inspectorLiveEditKey))
                _editSession.EndLiveFieldEdit(_inspectorLiveEditKey);
            _inspectorLiveEditKey = null;
            _inspectorLiveEditTarget = null;
            _workspaceLiveEditKey = "workspace-group:"
                + (++_inspectorLiveEditSerial).ToString();
            _workspaceLiveEditGroupId = groupId;
            _workspaceLiveEditBefore = _workspace != null
                ? _workspace.Capture()
                : null;
            _workspaceLiveEditHistoryRecorded = false;
        }

        private void EndWorkspaceLiveFieldEdit()
        {
            _workspaceLiveEditKey = null;
            _workspaceLiveEditGroupId = null;
            _workspaceLiveEditBefore = null;
            _workspaceLiveEditHistoryRecorded = false;
        }

        private void UpdateWorkspaceGroupTextLive(
            StoryGraphWorkspace.GroupData group)
        {
            if (_workspace == null || group == null) return;
            EnsureWorkspaceLiveFieldEdit(group.Id);
            StoryGraphWorkspace.Snapshot rollback = _workspace.Capture();
            bool rollbackDirty = _workspace.Dirty;
            if (!_workspace.UpdateGroupText(
                    group.Id, _inspectorShowInput.text,
                    _inspectorContentInput.text)) return;

            string error;
            if (!_workspace.Save(out error))
            {
                _workspace.Restore(rollback, rollbackDirty);
                StoryGraphWorkspace.GroupData restored = _workspace.FindGroup(group.Id);
                RefreshWorkspaceGroupTextVisual(restored);
                if (restored != null)
                {
                    SetInspectorText(_inspectorShowInput, restored.Title);
                    SetInspectorText(_inspectorContentInput, restored.Note);
                }
                SetEditFeedback(error + "；本次文字输入已回滚。", true);
                return;
            }

            if (!_workspaceLiveEditHistoryRecorded
                && _workspaceLiveEditBefore != null)
            {
                _workspaceUndo.Push(_workspaceLiveEditBefore);
                _workspaceRedo.Clear();
                _editActionTimeline.Push(true);
                _editRedoTimeline.Clear();
                if (_editSession != null) _editSession.ClearRedoHistory();
                _workspaceLiveEditHistoryRecorded = true;
            }
            StoryGraphWorkspace.GroupData current = _workspace.FindGroup(group.Id);
            RefreshWorkspaceGroupTextVisual(current);
            _inspectorBasicPreviewDirty = true;
            _editStatus = (group.IsNote
                    ? "已实时更新注释标题和正文。"
                    : "已实时更新分组标题和说明。")
                + " 同一次连续输入只占一条工作区撤销记录。";
            UpdateEditControls();
            UpdateStatusBar();
        }

        private void RefreshWorkspaceGroupTextVisual(
            StoryGraphWorkspace.GroupData group)
        {
            if (group == null) return;
            foreach (GroupVisual visual in _activeGroups)
            {
                if (visual == null || visual.Group == null
                    || !string.Equals(visual.Group.Id, group.Id,
                        StringComparison.Ordinal)) continue;
                visual.Group = group;
                if (visual.Title != null)
                    visual.Title.text = (group.IsNote ? "注｜" : "组｜")
                        + (group.Title ?? string.Empty);
                if (visual.Note != null)
                {
                    visual.Note.text = group.Note ?? string.Empty;
                    visual.Note.gameObject.SetActive(
                        !string.IsNullOrWhiteSpace(visual.Note.text));
                }
            }
        }

        private void RefreshInspectorJsonFromSelection()
        {
            if (_inspectorJsonInput == null || _selectedNode == null
                || _selectedNode.SourceNode == null) return;
            EvtStoryGraphNode source = _selectedNode.SourceNode;
            object value = (object)source.Talk ?? source.Option;
            if (value != null)
                SetInspectorText(_inspectorJsonInput,
                    JsonConvert.SerializeObject(value, Formatting.Indented));
        }

        private void RefreshLiveNodePreview(object target)
        {
            if (_viewModel == null || target == null) return;
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                if (node == null || node.SourceNode == null) continue;
                EvtStoryGraphNode source = node.SourceNode;
                string subtitle;
                string search;
                if (source.Talk != null && ReferenceEquals(source.Talk, target))
                {
                    string line = EvtStoryGraphModelBuilder.Summarize(
                        source.Talk.content, 48);
                    if (string.IsNullOrEmpty(line)) line = "（空台词）";
                    subtitle = EvtStoryGraphModelBuilder.BuildRoleSummary(
                        source.Talk, _personNames) + "｜" + line;
                    string badges = StoryGraphPerformanceCodec.BuildTalkBadges(source.Talk);
                    if (!string.IsNullOrEmpty(badges)) subtitle += "\n" + badges;
                    subtitle += ExistingPreviewSuffix(source.Subtitle);
                    search = string.Join(" ", new[]
                    {
                        source.Talk.id.ToString(), source.Talk.content,
                        source.Talk.showTxt, source.Talk.roleName, badges,
                    }.Where(value => !string.IsNullOrEmpty(value)).ToArray());
                }
                else if (source.Option != null && ReferenceEquals(source.Option, target))
                {
                    subtitle = EvtStoryGraphModelBuilder.Summarize(
                        source.Option.content, 48);
                    if (string.IsNullOrEmpty(subtitle)) subtitle = "（空选项）";
                    string badges = StoryGraphPerformanceCodec.BuildOptionBadges(source.Option);
                    if (!string.IsNullOrEmpty(badges)) subtitle += "\n" + badges;
                    subtitle += ExistingPreviewSuffix(source.Subtitle);
                    search = string.Join(" ", new[]
                    {
                        source.Option.id.ToString(), source.Option.content,
                        source.Option.showTxt, source.Option.tag, badges,
                    }.Where(value => !string.IsNullOrEmpty(value)).ToArray());
                }
                else continue;

                source.Subtitle = subtitle;
                source.SearchText = search;
                node.Subtitle = subtitle;
                node.SearchText = search;
                NodeVisual visual;
                if (_nodeLookup.TryGetValue(node, out visual)
                    && visual != null && visual.Subtitle != null)
                    visual.Subtitle.text = subtitle;
            }
        }

        private static string ExistingPreviewSuffix(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            int marker = value.IndexOf("　[", StringComparison.Ordinal);
            return marker >= 0 ? value.Substring(marker) : string.Empty;
        }

        private void OnInspectorJsonValueChanged(string unused)
        {
            if (_settingInspectorText || !_editMode || _editSession == null) return;
            string error;
            TryApplyInspectorJsonLive(out error);
        }

        private void OnInspectorJsonEndEdit(string unused)
        {
            if (_settingInspectorText || !_editMode) return;
            string error;
            bool valid = TryApplyInspectorJsonLive(out error);
            EndInspectorLiveEdit();
            RefreshInspectorJsonFromSelection();
            if (!valid)
            {
                SetEditFeedback((error ?? "高级 JSON 不完整")
                    + " 已恢复显示草稿中最后一次有效配置。", false);
                return;
            }
            SetEditFeedback("高级 JSON 的有效内容已实时写入剧情草稿；"
                + "Ctrl+Z 可一次撤销本次连续输入。", false);
        }

        private bool TryApplyInspectorJsonLive(out string error)
        {
            error = null;
            if (!string.IsNullOrEmpty(_selectedGroupId)
                || _editSession == null || _selectedNode == null
                || _selectedNodes.Count > 1 || _selectedNode.SourceNode == null)
            {
                error = "高级 JSON 需要一个真实对话或选项节点。";
                return false;
            }

            EvtStoryGraphNode source = _selectedNode.SourceNode;
            var settings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Error,
            };
            try
            {
                LayoutPositionSnapshot layout = CaptureLayoutPositions();
                bool historyRecorded;
                string message;
                bool changed;
                StoryGraphEditNodeKind kind;
                int id;
                if (source.Talk != null)
                {
                    TalkCfg parsed = JsonConvert.DeserializeObject<TalkCfg>(
                        _inspectorJsonInput.text, settings);
                    string identity = "advanced-talk:" + source.Talk.id;
                    string liveKey = EnsureInspectorPageLiveEdit(
                        identity, "inspector-advanced");
                    TalkCfg updated;
                    changed = _editSession.TryReplaceTalkLive(
                        source.Talk, parsed, liveKey,
                        out updated, out historyRecorded, out message);
                    kind = StoryGraphEditNodeKind.Talk;
                    id = source.Talk.id;
                }
                else if (source.Option != null)
                {
                    int key = _editSession.FindOptionKey(source.Option);
                    OptionCfg parsed = JsonConvert.DeserializeObject<OptionCfg>(
                        _inspectorJsonInput.text, settings);
                    string identity = "advanced-option:" + key;
                    string liveKey = EnsureInspectorPageLiveEdit(
                        identity, "inspector-advanced");
                    OptionCfg updated;
                    changed = _editSession.TryReplaceOptionLive(
                        source.Option, parsed, liveKey,
                        out updated, out historyRecorded, out message);
                    kind = StoryGraphEditNodeKind.Option;
                    id = key;
                }
                else
                {
                    error = "所选节点不是可编辑配置。";
                    return false;
                }

                if (!changed)
                {
                    if (!string.IsNullOrEmpty(message)
                        && message.IndexOf("没有改变", StringComparison.Ordinal) >= 0)
                        return true;
                    error = message;
                    return false;
                }

                RefreshEditGraph(kind, id, message, false,
                    historyRecorded, layout);
                _editStatus = message + " 同一次连续输入只占一条撤销记录。";
                UpdateStatusBar();
                return true;
            }
            catch (Exception e)
            {
                error = "高级 JSON 解析失败，草稿保持最后有效状态："
                        + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private void BuildMinimap()
        {
            // 1px 边框效果：圆角描边底 #C6B187 + 内压奶油圆角面板。
            Image border = CreateImage(_canvasRoot, "MinimapBorder",
                PanelBorder, false);
            _minimapBorder = border.rectTransform;
            ApplySprite(border, _roundedSprite);
            Place(border.rectTransform, 1f, 0f, 1f, 0f,
                -(12f + 1f), StatusbarHeight + 12f - 1f,
                MinimapWidth + 2f, MinimapHeight + 2f);

            GameObject root = CreateUIObject("Minimap", _canvasRoot.transform);
            _minimapRoot = (RectTransform)root.transform;
            Place(_minimapRoot, 1f, 0f, 1f, 0f,
                -12f, StatusbarHeight + 12f, MinimapWidth, MinimapHeight);
            var bg = root.AddComponent<Image>();
            bg.color = PanelBg;
            ApplySprite(bg, _roundedSprite);
            bg.raycastTarget = true; // 接收点击/拖动
            // 视口比整张图大时，视口框可能覆盖小地图全域；矩形遮罩保证
            // 框线和节点点位不会画到小地图外面。
            root.AddComponent<RectMask2D>();

            GameObject content = CreateUIObject("Dots", root.transform);
            _minimapContent = (RectTransform)content.transform;
            _minimapContent.anchorMin = Vector2.zero;
            _minimapContent.anchorMax = Vector2.one;
            _minimapContent.pivot = new Vector2(0.5f, 0.5f);
            _minimapContent.offsetMin = new Vector2(4f, 4f);
            _minimapContent.offsetMax = new Vector2(-4f, -4f);

            GameObject frame = CreateUIObject("ViewportFrame", content.transform);
            _minimapFrame = (RectTransform)frame.transform;
            _minimapFrame.anchorMin = new Vector2(0f, 1f);
            _minimapFrame.anchorMax = new Vector2(0f, 1f);
            _minimapFrame.pivot = new Vector2(0f, 1f);
            var frameColor = new Color(0.894f, 0.439f, 0.043f, 0.9f); // #E4700B 暖橙
            CreateFrameEdge(frame, frameColor, true, false);   // 顶
            CreateFrameEdge(frame, frameColor, false, false);  // 底
            CreateFrameEdge(frame, frameColor, true, true);    // 左
            CreateFrameEdge(frame, frameColor, false, true);   // 右
            frame.SetActive(false);

            // 点击/拖动跳转主视图
            var trigger = root.AddComponent<EventTrigger>();
            trigger.triggers.Add(MakeTrigger(EventTriggerType.PointerDown,
                OnMinimapPointer));
            trigger.triggers.Add(MakeTrigger(EventTriggerType.Drag,
                OnMinimapPointer));
        }

        /// <summary>小地图视口框的一条边。top/left 决定锚在哪一侧。</summary>
        private void CreateFrameEdge(GameObject parent, Color color, bool topOrLeft, bool vertical)
        {
            Image edge = CreateImage(parent, vertical ? "V" : "H", color, false);
            RectTransform rt = edge.rectTransform;
            if (!vertical)
            {
                rt.anchorMin = new Vector2(0f, topOrLeft ? 1f : 0f);
                rt.anchorMax = new Vector2(1f, topOrLeft ? 1f : 0f);
                rt.pivot = new Vector2(0.5f, topOrLeft ? 1f : 0f);
                rt.sizeDelta = new Vector2(0f, 1.5f);
            }
            else
            {
                rt.anchorMin = new Vector2(topOrLeft ? 0f : 1f, 0f);
                rt.anchorMax = new Vector2(topOrLeft ? 0f : 1f, 1f);
                rt.pivot = new Vector2(topOrLeft ? 0f : 1f, 0.5f);
                rt.sizeDelta = new Vector2(1.5f, 0f);
            }
            rt.anchoredPosition = Vector2.zero;
        }

        private void BuildTooltip()
        {
            GameObject tip = CreateUIObject("Tooltip", _canvasRoot.transform);
            _tooltip = (RectTransform)tip.transform;
            _tooltip.anchorMin = new Vector2(0.5f, 0.5f);
            _tooltip.anchorMax = new Vector2(0.5f, 0.5f);
            _tooltip.pivot = new Vector2(0f, 1f); // 以左上角定位
            _tooltip.sizeDelta = new Vector2(TooltipWidth, 72f);

            var bg = tip.AddComponent<Image>();
            bg.color = PanelBorder;
            // 关键：tooltip 不接收射线，否则会触发节点的 PointerExit 造成闪烁。
            bg.raycastTarget = false;
            ApplySprite(bg, _roundedSprite);

            // 内衬：与外框同形圆角、内缩 1px，形成描边；ignoreLayout 防止被 VLG 布局。
            GameObject fillGo = CreateUIObject("Fill", tip.transform);
            var fillRt = (RectTransform)fillGo.transform;
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = new Vector2(1f, 1f);
            fillRt.offsetMax = new Vector2(-1f, -1f);
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.color = PanelBg;
            fillImg.raycastTarget = false;
            ApplySprite(fillImg, _roundedSprite);
            var fillLayout = fillGo.AddComponent<LayoutElement>();
            fillLayout.ignoreLayout = true;

            var layout = tip.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 8, 8);
            layout.spacing = 5f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = tip.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _tooltipTitle = CreateText(tip, "Title", 17, FontStyle.Bold,
                TitleColor, TextAnchor.UpperLeft);
            _tooltipSubtitle = CreateText(tip, "Subtitle", 15, FontStyle.Normal,
                BodyTextColor, TextAnchor.UpperLeft);
            _tooltipFlags = CreateText(tip, "Flags", 15, FontStyle.Normal,
                AccentOrange, TextAnchor.UpperLeft);
            tip.SetActive(false);
        }

        private void BuildContextMenu()
        {
            GameObject menu = CreateUIObject("ContextMenu", _canvasRoot.transform);
            _contextMenu = (RectTransform)menu.transform;
            _contextMenu.anchorMin = new Vector2(0.5f, 0.5f);
            _contextMenu.anchorMax = new Vector2(0.5f, 0.5f);
            _contextMenu.pivot = new Vector2(0f, 1f);
            _contextMenu.sizeDelta = new Vector2(260f, 40f);
            Image background = menu.AddComponent<Image>();
            background.color = PanelBorder;
            background.raycastTarget = true;
            ApplySprite(background, _roundedSprite);
            menu.SetActive(false);
        }

        private ContextMenuButtonVisual CreateContextMenuButton()
        {
            GameObject root = CreateUIObject("MenuItem", _contextMenu);
            RectTransform rect = (RectTransform)root.transform;
            Image image = root.AddComponent<Image>();
            image.color = PanelBg;
            image.raycastTarget = true;
            ApplySprite(image, _chipSprite);
            Button button = root.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = SegmentFill;
            colors.pressedColor = PanelBorder;
            colors.disabledColor = new Color(0.72f, 0.69f, 0.63f, 0.55f);
            colors.fadeDuration = 0.04f;
            button.colors = colors;
            Text label = CreateText(root, "Label", SecondaryFontSize,
                FontStyle.Normal, BodyTextColor, TextAnchor.MiddleLeft);
            Stretch(label.rectTransform, 12f, 0f, 8f, 0f);
            var visual = new ContextMenuButtonVisual
            {
                Root = root,
                Button = button,
                Label = label,
            };
            root.SetActive(false);
            _contextMenuButtons.Add(visual);
            return visual;
        }

        // ==================== 图内 Toast ====================
        // 游戏根 Canvas 是 ScreenSpaceCamera，剧情图是 ScreenSpaceOverlay；
        // Overlay 永远渲染在相机空间画布之上，原版 ToastView 无论把
        // sortingOrder 提到多高都会被剧情图盖住，所以提示画在图画布上。

        private const float GraphToastFadeSeconds = 0.35f;

        /// <summary>StoryGraphToastRouter 的回调：true 表示消息已由剧情图接管。</summary>
        private bool TryShowGraphToast(string message)
        {
            if (!_open || _canvasRoot == null) return false;
            if (_previewSuspended)
            {
                // 预览本句期间画布整体隐藏；只积压最新一条，恢复显示时补发。
                _pendingGraphToast = message;
                return true;
            }
            return ShowGraphToastNow(message);
        }

        private bool ShowGraphToastNow(string message)
        {
            if (_canvasRoot == null || _font == null) return false;
            if (_graphToastRoot == null) BuildGraphToast();
            if (_graphToastRoot == null || _graphToastLabel == null) return false;
            _graphToastLabel.text = message;
            // 宽度随文字收缩；到上限后换行，高度由 ContentSizeFitter 自适应。
            // 检查器可见时（与视口压缩同一条件）在图区内居中并让出右侧
            // InspectorWidth，窄逻辑画布下不会压住检查器标题区。
            float canvasWidth = _canvasRect != null && _canvasRect.rect.width > 0f
                ? _canvasRect.rect.width
                : 1280f;
            float reserved = _editMode && _inspectorVisible ? InspectorWidth : 0f;
            float cap = Mathf.Min(760f,
                Mathf.Max(240f, canvasWidth - reserved - 80f));
            float preferred = _graphToastLabel.preferredWidth + 36f;
            _graphToastRoot.anchoredPosition =
                new Vector2(-reserved * 0.5f, -(ToolbarHeight + 16f));
            _graphToastRoot.sizeDelta = new Vector2(
                Mathf.Clamp(preferred, 240f, cap), _graphToastRoot.sizeDelta.y);
            _graphToastUntil = Time.unscaledTime
                + StoryGraphResourcePickerLogic.ToastDurationSeconds(
                    message != null ? message.Length : 0);
            if (_graphToastGroup != null) _graphToastGroup.alpha = 1f;
            _graphToastRoot.SetAsLastSibling();
            _graphToastRoot.gameObject.SetActive(true);
            return true;
        }

        private void BuildGraphToast()
        {
            GameObject root = CreateUIObject("GraphToast", _canvasRoot.transform);
            _graphToastRoot = (RectTransform)root.transform;
            _graphToastRoot.anchorMin = new Vector2(0.5f, 1f);
            _graphToastRoot.anchorMax = new Vector2(0.5f, 1f);
            _graphToastRoot.pivot = new Vector2(0.5f, 1f);
            _graphToastRoot.anchoredPosition =
                new Vector2(0f, -(ToolbarHeight + 16f));
            _graphToastRoot.sizeDelta = new Vector2(240f, 48f);

            var bg = root.AddComponent<Image>();
            bg.color = PanelBorder;
            // 提示永不拦截画布输入。
            bg.raycastTarget = false;
            ApplySprite(bg, _roundedSprite);

            GameObject fillGo = CreateUIObject("Fill", root.transform);
            var fillRt = (RectTransform)fillGo.transform;
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = new Vector2(1f, 1f);
            fillRt.offsetMax = new Vector2(-1f, -1f);
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.color = PanelBg;
            fillImg.raycastTarget = false;
            ApplySprite(fillImg, _roundedSprite);
            var fillLayout = fillGo.AddComponent<LayoutElement>();
            fillLayout.ignoreLayout = true;

            _graphToastGroup = root.AddComponent<CanvasGroup>();
            _graphToastGroup.interactable = false;
            _graphToastGroup.blocksRaycasts = false;

            var layout = root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 10, 10);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = root.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _graphToastLabel = CreateText(root, "Label", SecondaryFontSize,
                FontStyle.Bold, TitleColor, TextAnchor.MiddleCenter);
            root.SetActive(false);
        }

        private void UpdateGraphToast()
        {
            if (_graphToastRoot == null || !_graphToastRoot.gameObject.activeSelf)
                return;
            float remain = _graphToastUntil - Time.unscaledTime;
            if (remain <= 0f)
            {
                _graphToastRoot.gameObject.SetActive(false);
                return;
            }
            if (_graphToastGroup != null)
                _graphToastGroup.alpha = remain < GraphToastFadeSeconds
                    ? remain / GraphToastFadeSeconds
                    : 1f;
        }

        private void HideGraphToastNow()
        {
            _graphToastUntil = 0f;
            if (_graphToastRoot != null)
                _graphToastRoot.gameObject.SetActive(false);
        }

        private void FlushPendingGraphToast()
        {
            string pending = _pendingGraphToast;
            _pendingGraphToast = null;
            if (string.IsNullOrEmpty(pending)) return;
            if (!ShowGraphToastNow(pending))
            {
                // 图内补发失败时消息先落日志再回落原版 Toast——此时 Overlay
                // 仍在，原版 Toast 大概率看不见，日志是最后的痕迹。
                Plugin.Log?.LogInfo("[StoryGraph.Toast] " + pending);
                try { ToastHelper.Toast(pending); }
                catch { }
            }
        }

        // ==================== UGUI 小工厂 ====================

        private static GameObject CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            // 运行时新建对象默认在 Default 层；跟随 Canvas 层可避免部分输入模块
            // 按 UI layer/culling mask 过滤后按钮看得到却点不到。
            go.layer = parent.gameObject.layer;
            return go;
        }

        private static Image CreateImage(
            GameObject parent, string name, Color color, bool raycast)
        {
            GameObject go = CreateUIObject(name, parent.transform);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        private Text CreateText(
            GameObject parent, string name, int fontSize, FontStyle style,
            Color color, TextAnchor anchor)
        {
            GameObject go = CreateUIObject(name, parent.transform);
            var text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.supportRichText = false;
            text.raycastTarget = false;
            return text;
        }

        private Button CreateToolbarButton(
            GameObject parent, string label, float x, float y, float width,
            UnityEngine.Events.UnityAction onClick, bool rightAnchored = false)
        {
            GameObject go = CreateUIObject("btn_" + label, parent.transform);
            var rt = (RectTransform)go.transform;
            if (rightAnchored)
                Place(rt, 1f, 1f, 1f, 0.5f,
                    x, y, width, ToolbarControlHeight);
            else
                Place(rt, 0f, 1f, 0f, 0.5f,
                    x, y, width, ToolbarControlHeight);

            var img = go.AddComponent<Image>();
            img.color = Color.white;
            img.raycastTarget = true;
            ApplySprite(img, _buttonSprite);
            var button = go.AddComponent<Button>();
            button.targetGraphic = img;
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.97f, 0.97f, 0.97f, 1f);
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.88f, 0.86f, 0.80f, 1f);
            colors.disabledColor = new Color(0.78f, 0.75f, 0.68f, 0.6f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.06f;
            button.colors = colors;
            button.onClick.AddListener(onClick);

            Text text = CreateText(go, "Label", ToolbarFontSize, FontStyle.Bold,
                HexColor("875E3D"), TextAnchor.MiddleCenter);
            text.text = label;
            Stretch(text.rectTransform, 2f, 0f, 2f, 0f);
            return button;
        }

        private InputField CreateSearchField(
            GameObject parent, string placeholder, float x, float y, float width)
        {
            GameObject go = CreateUIObject("SearchField", parent.transform);
            var rt = (RectTransform)go.transform;
            Place(rt, 0f, 1f, 0f, 0.5f,
                x, y, width, ToolbarControlHeight);

            var bg = go.AddComponent<Image>();
            bg.color = Color.white;
            bg.raycastTarget = true;
            ApplySprite(bg, _inputSprite);

            var input = go.AddComponent<InputField>();
            input.contentType = InputField.ContentType.Standard;
            input.characterLimit = 0;
            input.caretColor = HexColor("824C24");
            input.selectionColor = new Color(0.89f, 0.62f, 0.29f, 0.4f);

            Text ph = CreateText(go, "Placeholder", ToolbarFontSize, FontStyle.Normal,
                HexColor("9F9F9F"), TextAnchor.MiddleLeft);
            Stretch(ph.rectTransform, 12f, 0f, 12f, 0f);
            ph.text = placeholder;

            Text txt = CreateText(go, "Text", ToolbarFontSize, FontStyle.Normal,
                HexColor("824C24"), TextAnchor.MiddleLeft);
            Stretch(txt.rectTransform, 12f, 0f, 12f, 0f);

            input.textComponent = txt;
            input.placeholder = ph;
            input.onValueChanged.AddListener(OnSearchChanged);
            return input;
        }

        // ==================== 运行时贴图工厂 ====================
        // 官方纸感：圆角面板 / 边签 chip / 金黄按钮 / 输入框底板全部运行时生成，
        // 实例缓存于字段，Close/OnDestroy 时统一释放。

        private delegate Color TexelShader(int x, int y, int size);

        private static Color HexColor(string hex)
        {
            uint v = uint.Parse(hex, System.Globalization.NumberStyles.HexNumber);
            return new Color(
                ((v >> 16) & 0xFF) / 255f,
                ((v >> 8) & 0xFF) / 255f,
                (v & 0xFF) / 255f,
                1f);
        }

        /// <summary>生成带抗锯齿圆角的方形贴图；shader 决定每个像素的基础色。</summary>
        private static Texture2D MakeRoundedTexture(int size, int radius, TexelShader shader)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            float r = radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 像素中心到内缩 radius 的方盒最近点的距离 → 圆角 SDF，0.5 像素过渡抗锯齿
                    float px = x + 0.5f, py = y + 0.5f;
                    float cx = Mathf.Clamp(px, r, size - r);
                    float cy = Mathf.Clamp(py, r, size - r);
                    float dx = px - cx, dy = py - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(r - d + 0.5f);
                    Color c = shader(x, y, size);
                    c.a *= alpha;
                    pixels[y * size + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        private static Sprite MakeSprite(Texture2D tex, int border)
        {
            return Sprite.Create(
                tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
        }

        private static void ApplySprite(Image img, Sprite sprite)
        {
            img.sprite = sprite;
            img.type = Image.Type.Sliced;
        }

        private void BuildSprites()
        {
            const int size = 32;

            // 通用圆角面板：纯白（靠 Image.color 上色），大圆角
            _roundedTex = MakeRoundedTexture(size, 8, (x, y, s) => Color.white);
            _roundedSprite = MakeSprite(_roundedTex, 10);

            // 边标签 chip：纯白，小圆角
            _chipTex = MakeRoundedTexture(size, 4, (x, y, s) => Color.white);
            _chipSprite = MakeSprite(_chipTex, 6);

            // 工具栏按钮：金黄渐变 + 底部 2px 深边
            Color btnEdge = HexColor("B89B6E");
            Color btnBase = HexColor("D1BA87");
            Color btnTop = HexColor("FFF7B7");
            _buttonTex = MakeRoundedTexture(size, 6, (x, y, s) =>
                y < 2 ? btnEdge : Color.Lerp(btnBase, btnTop, (float)y / (s - 1)));
            _buttonSprite = MakeSprite(_buttonTex, 8);

            // 输入框：暖纸底 + 底部 2px 暗边
            Color inEdge = HexColor("CFC3A8");
            Color inBase = HexColor("E7DFCB");
            _inputTex = MakeRoundedTexture(size, 6, (x, y, s) =>
                y < 2 ? inEdge : inBase);
            _inputSprite = MakeSprite(_inputTex, 8);
        }

        private void DestroyGeneratedSprites()
        {
            if (_roundedSprite != null) { UnityEngine.Object.Destroy(_roundedSprite); _roundedSprite = null; }
            if (_roundedTex != null) { UnityEngine.Object.Destroy(_roundedTex); _roundedTex = null; }
            if (_chipSprite != null) { UnityEngine.Object.Destroy(_chipSprite); _chipSprite = null; }
            if (_chipTex != null) { UnityEngine.Object.Destroy(_chipTex); _chipTex = null; }
            if (_buttonSprite != null) { UnityEngine.Object.Destroy(_buttonSprite); _buttonSprite = null; }
            if (_buttonTex != null) { UnityEngine.Object.Destroy(_buttonTex); _buttonTex = null; }
            if (_inputSprite != null) { UnityEngine.Object.Destroy(_inputSprite); _inputSprite = null; }
            if (_inputTex != null) { UnityEngine.Object.Destroy(_inputTex); _inputTex = null; }
        }

        private static EventTrigger.Entry MakeTrigger(
            EventTriggerType type, UnityEngine.Events.UnityAction<BaseEventData> callback)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(callback);
            return entry;
        }

        private static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        private static void Place(
            RectTransform rt, float anchorX, float anchorY,
            float pivotX, float pivotY, float x, float y, float w, float h)
        {
            rt.anchorMin = new Vector2(anchorX, anchorY);
            rt.anchorMax = new Vector2(anchorX, anchorY);
            rt.pivot = new Vector2(pivotX, pivotY);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        // ==================== 数据：刷新 / 布局 / 重建 ====================

        /// <summary>
        /// 重建数据管线：快照 → 模型 → ViewModel（恢复展开记忆）→ 布局 → 重绘。
        /// centerOnCurrent 为 true 时（打开窗口）之后自动居中当前编辑 Talk。
        /// </summary>
        private void RefreshGraph(
            bool centerOnCurrent,
            LayoutPositionSnapshot preservedLayout = null)
        {
            if (!_open || _view == null) return;
            HideContextMenu();
            ClearHover();
            _error = null;

            List<TalkCfg> talks;
            Dictionary<int, OptionCfg> options;
            int eventId;
            List<int> entries;
            bool entriesKnown;
            ISet<int> knownTalkIds = null;
            ISet<int> knownEventIds = null;
            string accessError;
            if (_editMode && _editSession != null)
            {
                talks = _editSession.Talks;
                options = _editSession.Options;
                eventId = _editSession.EventId;
                entries = _editSession.Entries;
                entriesKnown = _editSession.EntriesKnown;
                knownTalkIds = _editSession.SnapshotKnownTalkIds();
                knownEventIds = _editSession.SnapshotKnownEventIds();
                accessError = null;
            }
            else if (!EvtStoryGraphViewAccess.TrySnapshot(
                    _view, out talks, out options, out eventId,
                    out entries, out entriesKnown, out accessError))
            {
                _model = null;
                _viewModel = null;
                _layout = null;
                _error = accessError ?? "无法读取当前事件数据。";
                ClearSearchResultsForUnavailableGraph();
                RebuildVisuals();
                RefreshInspector();
                UpdateStatusBar();
                return;
            }
            else
            {
                SnapshotKnownGraphTargets(
                    talks, eventId, out knownTalkIds, out knownEventIds);
            }
            EvtCfg eventConfig = SnapshotEventConfiguration(eventId);
            if (!_editMode)
                MergeReferencedOptionsForSnapshot(
                    options, eventConfig, talks);

            try
            {
                _personNames = EvtStoryGraphViewAccess.SnapshotPersonNames(_view)
                    ?? new Dictionary<int, string>();
                _model = EvtStoryGraphModelBuilder.Build(
                    talks, options, eventId, entries, entriesKnown, _personNames,
                    knownTalkIds, knownEventIds, eventConfig);
                _viewModel = StoryGraphViewModel.Build(_model);
                RestoreExpansions();
                if (_editMode)
                {
                    ExpandAllForEditing();
                }
                else
                {
                    // 先展开目标所在链段/未使用区，再做唯一一次布局。若先布局、
                    // 再为了定位展开，会造成进入剧情图时节点瞬移或辅助框畸变。
                    RefreshEditorFocusIntent(eventId);
                    _currentNode = EnsureEditorFocusVisible();
                }
                _layout = BuildProjectionStableLayout();
                EnsureWorkspace(eventId);
                ApplyWorkspaceLayout();
                ApplyPreservedLayout(preservedLayout);

                if (_editMode)
                {
                    _currentNode = null;
                    _selectedNode = FindEditSelectionNode();
                    RestoreMultiSelection();
                    _matches.Clear();
                    _matchIndex = -1;
                    UpdateSearchNavigationControls();
                }
                else
                {
                    _selectedNode = null;
                    UpdateSearch(false);   // 保留关键词，重建匹配列表
                }

                RebuildVisuals();
                RefreshInspector();
                UpdateStatusBar();
                if (centerOnCurrent)
                {
                    _pendingInitialCenter = true;
                }
                else
                {
                    // 手动刷新：保持当前缩放平移，只做钳制修正。
                    _content.anchoredPosition = ClampPan(_content.anchoredPosition);
                    UpdateMinimapFrame();
                }
                UpdateEditControls();
                Plugin.Log?.LogInfo("[StoryGraph] 已刷新：" + _model.Summary
                    + (_editMode ? "（编辑草稿）" : string.Empty));
            }
            catch (Exception e)
            {
                _model = null;
                _viewModel = null;
                _layout = null;
                _error = "剧情图构建失败；异常配置已隔离，不会影响编辑器："
                         + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogError("[StoryGraph.Build] " + e);
                ClearSearchResultsForUnavailableGraph();
                RebuildVisuals();
                RefreshInspector();
                UpdateStatusBar();
            }
        }

        private void SnapshotKnownGraphTargets(
            IEnumerable<TalkCfg> localTalks,
            int eventId,
            out ISet<int> knownTalkIds,
            out ISet<int> knownEventIds)
        {
            var talks = new HashSet<int>();
            var events = new HashSet<int>();
            if (localTalks != null)
                foreach (TalkCfg talk in localTalks)
                    if (talk != null && talk.id > 0) talks.Add(talk.id);
            if (eventId > 0) events.Add(eventId);
            try
            {
                if (Cfg.TalkCfgMap != null)
                    talks.UnionWith(Cfg.TalkCfgMap.Keys);
                if (Cfg.EvtCfgMap != null)
                    events.UnionWith(Cfg.EvtCfgMap.Keys);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph] 读取运行时全局配置编号失败：" + e.Message);
            }

            string modRoot;
            string error;
            if (EvtStoryGraphViewAccess.TryGetModRoot(
                    _view, out modRoot, out error))
            {
                var ignoredOptions = new HashSet<int>();
                StoryGraphEditPersistence.TryReadPersistedIds(
                    modRoot, talks, ignoredOptions);
                StoryGraphEditPersistence.TryReadPersistedEventIds(
                    modRoot, events);
            }
            knownTalkIds = talks;
            knownEventIds = events;
        }

        private EvtCfg SnapshotEventConfiguration(int eventId)
        {
            string modRoot = null;
            string ignored;
            EvtStoryGraphViewAccess.TryGetModRoot(
                _view, out modRoot, out ignored);
            EvtCfg evt;
            string error;
            if (StoryGraphEditPersistence.TryReadEffectiveEvent(
                    modRoot, eventId, out evt, out error))
                return evt;
            Plugin.Log?.LogWarning("[StoryGraph] " + error);
            return null;
        }

        /// <summary>返回本次实际补进 options 字典的选项编号；进入编辑模式时
        /// 交给会话作为“借来显示”的冻结集合，避免本体共享选项被写进 Mod。</summary>
        private HashSet<int> MergeReferencedOptionsForSnapshot(
            IDictionary<int, OptionCfg> options,
            EvtCfg eventConfig,
            IEnumerable<TalkCfg> talks)
        {
            var mergedIds = new HashSet<int>();
            if (options == null) return mergedIds;
            var referencedIds = new HashSet<int>();
            if (eventConfig != null && eventConfig.options != null)
                referencedIds.UnionWith(eventConfig.options);
            if (talks != null)
            {
                foreach (TalkCfg talk in talks)
                    if (talk != null && talk.option != null)
                        referencedIds.UnionWith(talk.option);
            }
            // 原编辑器内存对象可能带有尚未普通保存的修改，绝不能用磁盘/全局
            // 配置覆盖；只补齐原编辑器会从 Cfg.OptionCfgMap 回退读取的缺项。
            referencedIds.RemoveWhere(id =>
                id <= 0 || options.ContainsKey(id));
            if (referencedIds.Count == 0) return mergedIds;

            string modRoot = null;
            string ignored;
            EvtStoryGraphViewAccess.TryGetModRoot(
                _view, out modRoot, out ignored);
            string error;
            if (!StoryGraphEditPersistence.TryMergeReferencedOptions(
                    modRoot, referencedIds, options, out error))
                Plugin.Log?.LogWarning("[StoryGraph] " + error);
            foreach (int id in referencedIds)
                if (options.ContainsKey(id)) mergedIds.Add(id);
            return mergedIds;
        }

        private void EnsureWorkspace(int eventId)
        {
            string modRoot;
            string error;
            if (!EvtStoryGraphViewAccess.TryGetModRoot(_view, out modRoot, out error))
                return;
            string normalized;
            try { normalized = System.IO.Path.GetFullPath(modRoot); }
            catch { normalized = modRoot ?? string.Empty; }
            if (_workspace != null && _workspaceEventId == eventId
                && string.Equals(_workspaceModRoot, normalized,
                    StringComparison.OrdinalIgnoreCase)) return;
            _workspace = StoryGraphWorkspace.Load(normalized, eventId);
            _workspaceModRoot = normalized;
            _workspaceEventId = eventId;
            ClearEditHistories();
        }

        private void ApplyWorkspaceLayout()
        {
            if (_workspace == null || _layout == null || _viewModel == null) return;
            Dictionary<string, Vector2> positions = _workspace.GetPositions();
            if (positions.Count == 0) return;
            StoryGraphLayoutEngine.ApplyManualPositions(
                _layout, _viewModel.LayoutNodes, positions, _viewModel.Edges);
        }

        private void ApplyPreservedLayout(LayoutPositionSnapshot snapshot)
        {
            if (snapshot == null || _layout == null || _viewModel == null
                || snapshot.NodePositions.Count == 0) return;
            var positions = new Dictionary<string, Vector2>(
                snapshot.VisiblePositions, StringComparer.Ordinal);

            // 同一 OptionCfg 可被多个父 Talk 复用，只有删除前后都恰好一个显示实例时
            // 才能在 parent-key 与 orphan-key 之间安全迁移，避免猜错复用节点位置。
            Dictionary<int, List<StoryGraphDisplayNode>> currentOptions = _viewModel.Nodes
                .Where(node => node != null && node.SourceNode != null
                    && node.SourceNode.Option != null)
                .GroupBy(node => node.SourceNode.Id)
                .ToDictionary(group => group.Key, group => group.ToList());
            foreach (KeyValuePair<int, List<StoryGraphDisplayNode>> pair in currentOptions)
            {
                List<Vector2> oldPositions;
                if (pair.Value.Count != 1
                    || !snapshot.OptionPositions.TryGetValue(
                        pair.Key, out oldPositions)
                    || oldPositions == null || oldPositions.Count != 1) continue;
                StoryGraphDisplayNode node = pair.Value[0];
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key) && !positions.ContainsKey(key))
                    positions[key] = oldPositions[0];
            }

            StoryGraphLayoutEngine.ApplyManualPositions(
                _layout, _viewModel.Nodes, positions, _viewModel.Edges);

            // 稳定键有意不区分重复 Talk ID，因为重复配置不能保存；但修复这类
            // 草稿时，删除其中一项不能让幸存卡片继承另一项的坐标。对象身份在
            // 当前数据命令内保持不变，因此用它覆盖一次既精确又不会污染持久化键。
            var used = new HashSet<LayoutNodePosition>();
            bool exactPositionApplied = false;
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                LayoutNodePosition old = FindPreservedNodePosition(
                    node, snapshot.NodePositions, used);
                Rect rect;
                if (old == null || !_layout.TryGetRect(node, out rect)) continue;
                rect.position = old.Position;
                _layout.NodeRects[node] = rect;
                used.Add(old);
                exactPositionApplied = true;
            }
            if (exactPositionApplied)
                StoryGraphLayoutEngine.RerouteCurrent(
                    _layout, _viewModel.Edges);
        }

        private static LayoutNodePosition FindPreservedNodePosition(
            StoryGraphDisplayNode node,
            IList<LayoutNodePosition> candidates,
            ISet<LayoutNodePosition> used)
        {
            if (node == null || candidates == null) return null;
            EvtStoryGraphNode source = node.SourceNode;
            if (source != null && source.Talk != null)
                return candidates.FirstOrDefault(item => item != null
                    && !used.Contains(item)
                    && ReferenceEquals(item.Talk, source.Talk));

            if (source != null && source.Option != null)
                return candidates.FirstOrDefault(item => item != null
                    && !used.Contains(item)
                    && ReferenceEquals(item.Option, source.Option)
                    && ReferenceEquals(item.LocateTalk, source.LocateTalk));

            if (node.IsSegment && node.SegmentNodes != null)
            {
                List<TalkCfg> talks = node.SegmentNodes
                    .Where(item => item != null && item.Talk != null)
                    .Select(item => item.Talk)
                    .ToList();
                LayoutNodePosition best = null;
                int bestOverlap = 0;
                foreach (LayoutNodePosition item in candidates)
                {
                    if (item == null || used.Contains(item)
                        || item.SegmentTalks == null) continue;
                    int overlap = item.SegmentTalks.Count(oldTalk =>
                        talks.Any(currentTalk =>
                            ReferenceEquals(currentTalk, oldTalk)));
                    if (overlap <= bestOverlap) continue;
                    best = item;
                    bestOverlap = overlap;
                }
                if (bestOverlap > 0) return best;
            }

            if (source != null)
                return candidates.FirstOrDefault(item => item != null
                    && !used.Contains(item) && item.HasSource
                    && item.SourceKind == source.Kind
                    && item.SourceId == source.Id
                    && ReferenceEquals(item.LocateTalk, source.LocateTalk));
            return null;
        }

        /// <summary>
        /// 刷新后恢复展开状态：对窗口记忆的每个 key，新 ViewModel 若未展开则
        /// ToggleSegment 一次；key 已失效（IsExpanded 恒 false 或抛异常）则从记忆剔除。
        /// </summary>
        private void RestoreExpansions()
        {
            if (_viewModel == null || _expandedKeys.Count == 0) return;
            for (int i = _expandedKeys.Count - 1; i >= 0; i--)
            {
                string key = _expandedKeys[i];
                bool expanded;
                try
                {
                    if (!_viewModel.IsExpanded(key)) _viewModel.ToggleSegment(key);
                    expanded = _viewModel.IsExpanded(key);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning(
                        "[StoryGraph] 恢复展开状态失败 key=" + key + "：" + e.Message);
                    expanded = false;
                }
                if (!expanded) _expandedKeys.RemoveAt(i);
            }
        }

        private void RefreshEditorFocusIntent()
        {
            RefreshEditorFocusIntent(EvtStoryGraphViewAccess.GetEventId(_view));
        }

        private void RefreshEditorFocusIntent(int eventId)
        {
            _editorFocusTalk = EvtStoryGraphViewAccess.GetCurrent(_view);
            _editorFocusOption = StoryGraphEditorSelectionTracker.GetPreferredOption(
                _view, _editorFocusTalk, eventId);
        }

        private StoryGraphDisplayNode FindEditorFocusNode()
        {
            if (_viewModel == null) return null;
            StoryGraphDisplayNode optionNode = _editorFocusOption != null
                ? _viewModel.FindNodeForOption(
                    _editorFocusOption, _editorFocusTalk)
                : null;
            return optionNode ?? (_editorFocusTalk != null
                ? _viewModel.FindNodeForTalk(_editorFocusTalk)
                : null);
        }

        /// <summary>
        /// 展开当前编辑条目所在的唯一折叠容器并返回真实卡片。只改 ViewModel
        /// 展开态与本窗口记忆，不写剧情、工作区或布局坐标。
        /// </summary>
        private StoryGraphDisplayNode EnsureEditorFocusVisible()
        {
            if (_viewModel == null) return null;
            for (int guard = 0; guard < 4; guard++)
            {
                StoryGraphDisplayNode target = FindEditorFocusNode();
                if (target == null || (!target.IsSegment && !target.IsUnusedGroup))
                    return target;

                string key = target.Key;
                if (string.IsNullOrEmpty(key)) return target;
                try
                {
                    bool before = _viewModel.IsExpanded(key);
                    if (!before) _viewModel.ToggleSegment(key);
                    if (!_viewModel.IsExpanded(key)) return target;
                    if (!_expandedKeys.Contains(key)) _expandedKeys.Add(key);
                    if (before) return FindEditorFocusNode();
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning(
                        "[StoryGraph.Locate] 展开定位目标失败 key="
                        + key + "：" + e.Message);
                    return target;
                }
            }
            return FindEditorFocusNode();
        }

        /// <summary>
        /// 编辑时不允许伪链段代替或挤压真实配置；使用不含链段把手的纯编辑投影。
        /// </summary>
        private void ExpandAllForEditing()
        {
            if (_viewModel == null) return;
            _viewModel.ExpandAllForEditing();
        }

        /// <summary>
        /// 退出编辑后沿用编辑画布的真实节点投影。链段仍可由首句卡片上的纸签
        /// 主动收起，但不会在模式切换瞬间自动换成一个“N 句对话”辅助块。
        /// </summary>
        private void RememberExpandedSegmentsAfterEditing()
        {
            if (_viewModel == null) return;
            foreach (string key in _viewModel.SnapshotSegmentKeys())
            {
                if (!string.IsNullOrEmpty(key) && !_expandedKeys.Contains(key))
                    _expandedKeys.Add(key);
            }
        }

        private void RestoreMultiSelection()
        {
            _selectedNodes.Clear();
            if (_viewModel == null) return;
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key) && _selectedNodeKeys.Contains(key))
                    _selectedNodes.Add(node);
            }
            if (_selectedNode != null)
            {
                _selectedNodes.Add(_selectedNode);
                string key = StoryGraphWorkspace.StableNodeKey(_selectedNode);
                if (!string.IsNullOrEmpty(key)) _selectedNodeKeys.Add(key);
            }
        }

        private StoryGraphDisplayNode FindEditSelectionNode()
        {
            if (_viewModel == null || _editSession == null) return null;
            if (_editSelectionKind == StoryGraphEditNodeKind.Talk)
            {
                TalkCfg talk = _editSession.FindTalk(
                    _editSelectionId, _editSelectionOrdinal);
                return talk != null ? _viewModel.FindNodeForTalk(talk) : null;
            }
            if (_editSelectionKind == StoryGraphEditNodeKind.Option)
            {
                OptionCfg option = _editSession.FindOption(_editSelectionId);
                if (option == null) return null;
                StoryGraphDisplayNode fallback = null;
                foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
                {
                    if (node == null || node.SourceNode == null
                        || !ReferenceEquals(node.SourceNode.Option, option)) continue;
                    if (fallback == null) fallback = node;
                    if (!string.IsNullOrEmpty(_editSelectionStableKey)
                        && string.Equals(
                            StoryGraphWorkspace.StableNodeKey(node),
                            _editSelectionStableKey, StringComparison.Ordinal))
                        return node;
                }
                return fallback;
            }
            return null;
        }

        private void SetEditSelection(
            StoryGraphDisplayNode node, bool additive = false, bool toggle = false)
        {
            EndInspectorLiveEdit();
            _selectedGroupId = null;
            RefreshGroupVisualStates();
            string stableKey = StoryGraphWorkspace.StableNodeKey(node);
            if (!additive)
            {
                _selectedNodes.Clear();
                _selectedNodeKeys.Clear();
            }
            bool wasSelected = node != null && _selectedNodes.Contains(node);
            if (toggle && wasSelected)
            {
                _selectedNodes.Remove(node);
                if (!string.IsNullOrEmpty(stableKey)) _selectedNodeKeys.Remove(stableKey);
                if (ReferenceEquals(_selectedNode, node))
                    _selectedNode = _selectedNodes.FirstOrDefault();
            }
            else
            {
                _selectedNode = node;
                if (node != null) _selectedNodes.Add(node);
                if (!string.IsNullOrEmpty(stableKey)) _selectedNodeKeys.Add(stableKey);
            }
            SyncPrimaryEditSelection();
            _deleteConfirmUntil = 0f;
            UpdateEditControls();
            RefreshAllNodeStates();
            RefreshInspector();
            UpdateStatusBar();
        }

        private void SyncPrimaryEditSelection()
        {
            StoryGraphDisplayNode node = _selectedNode;
            if (node != null && node.SourceNode != null && node.SourceNode.Talk != null)
            {
                _editSelectionKind = StoryGraphEditNodeKind.Talk;
                _editSelectionId = node.SourceNode.Talk.id;
                _editSelectionOrdinal = _editSession != null
                    ? _editSession.FindTalkOrdinal(node.SourceNode.Talk)
                    : 0;
            }
            else if (node != null && node.SourceNode != null && node.SourceNode.Option != null)
            {
                _editSelectionKind = StoryGraphEditNodeKind.Option;
                int key = _editSession != null
                    ? _editSession.FindOptionKey(node.SourceNode.Option)
                    : int.MinValue;
                _editSelectionId = key != int.MinValue
                    ? key
                    : node.SourceNode.Option.id;
                _editSelectionOrdinal = 0;
            }
            else
            {
                _editSelectionKind = StoryGraphEditNodeKind.None;
                _editSelectionId = 0;
                _editSelectionOrdinal = 0;
            }
            _editSelectionStableKey = StoryGraphWorkspace.StableNodeKey(node);
        }

        /// <summary>链段/折叠区切换后：保持缩放平移，重新布局并重绘。</summary>
        private void RelayoutKeepView()
        {
            if (_viewModel == null) return;
            ClearHover();
            try
            {
                _layout = BuildProjectionStableLayout();
                ApplyWorkspaceLayout();
            }
            catch (Exception e)
            {
                _layout = null;
                _error = "布局失败：" + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogError("[StoryGraph.Layout] " + e);
            }

            // ToggleSegment 会把全部显示节点重建为新对象；旧的当前节点、选中节点
            // 和搜索结果都已失效。此前“上个/下个”在展开或收起后仍拿旧节点去布局表
            // 查位置，因此看起来完全没反应。这里统一重新映射并重跑搜索。
            RefreshEditorFocusIntent();
            _currentNode = FindEditorFocusNode();
            _selectedNode = null;

            RebuildVisuals();
            UpdateSearch(false);
            _content.anchoredPosition = ClampPan(_content.anchoredPosition);
            UpdateMinimapFrame();
        }

        /// <summary>
        /// 使用包含不可见折叠成员的完整布局骨架计算坐标，再只为可见边布线。
        /// 因此展开/收起只改变绘制内容，不会让后续节点换列、换槽或改走线。
        /// </summary>
        private StoryGraphLayoutResult BuildProjectionStableLayout()
        {
            if (_viewModel == null) return null;
            StoryGraphLayoutResult result = StoryGraphLayoutEngine.Layout(
                _viewModel.LayoutNodes,
                _viewModel.LayoutEdges,
                _viewModel.RootNode);
            StoryGraphLayoutEngine.RerouteCurrent(result, _viewModel.Edges);
            return result;
        }

        /// <summary>把当前布局结果整体重绘：先连线（下层）后节点（上层），最后小地图。</summary>
        private void RebuildVisuals()
        {
            ReleaseAllVisuals();

            bool hasGraph = _layout != null && _viewModel != null
                            && _viewModel.Nodes.Count > 0;
            if (_emptyText != null)
            {
                _emptyText.gameObject.SetActive(!hasGraph);
                if (!hasGraph)
                    _emptyText.text = _error ?? "当前事件没有可显示的数据。";
            }
            if (!hasGraph)
            {
                UpdateMinimapDots();
                UpdateMinimapFrame();
                return;
            }

            RebuildGroupVisuals();

            // content 尺寸只是参考范围（子元素全部锚左上角），盖住布局外接矩形即可。
            Rect bounds = _layout.Bounds;
            _content.sizeDelta = new Vector2(
                Mathf.Max(100f, bounds.xMax + StoryGraphMetrics.Margin),
                Mathf.Max(100f, bounds.yMax + StoryGraphMetrics.Margin));

            if (_layout.Edges != null)
            {
                for (int i = 0; i < _layout.Edges.Count; i++)
                    BuildEdgeVisual(_layout.Edges[i]);
            }
            for (int i = 0; i < _viewModel.Nodes.Count; i++)
                BuildNodeVisual(_viewModel.Nodes[i]);

            UpdateEdgeLabelsVisibility();
            RefreshAllNodeStates();
            UpdateMinimapDots();
            UpdateMinimapFrame();
        }

        private void ReleaseAllVisuals()
        {
            ClearHover();
            for (int i = 0; i < _activeGroups.Count; i++)
            {
                if (_activeGroups[i] == null || _activeGroups[i].Root == null) continue;
                _activeGroups[i].Root.SetActive(false);
                UnityEngine.Object.Destroy(_activeGroups[i].Root);
            }
            _activeGroups.Clear();
            _groupBounds.Clear();
            for (int i = 0; i < _activeNodes.Count; i++) ReleaseNode(_activeNodes[i]);
            _activeNodes.Clear();
            _nodeLookup.Clear();
            for (int i = 0; i < _activeEdges.Count; i++) ReleaseEdge(_activeEdges[i]);
            _activeEdges.Clear();
            _edgeLookup.Clear();
        }

        private void RebuildGroupVisuals()
        {
            if (_groupContainer == null || _workspace == null || _layout == null
                || _viewModel == null) return;
            float minX = _layout.Bounds.xMin;
            float minY = _layout.Bounds.yMin;
            float maxX = _layout.Bounds.xMax;
            float maxY = _layout.Bounds.yMax;
            foreach (StoryGraphWorkspace.GroupData group in _workspace.Groups)
            {
                Rect bounds;
                if (group == null || !TryGetGroupBounds(group, out bounds)) continue;
                _groupBounds[group.Id] = bounds;
                GroupVisual visual = CreateGroupVisual(group, bounds);
                _activeGroups.Add(visual);
                minX = Mathf.Min(minX, bounds.xMin - StoryGraphMetrics.Margin * 0.25f);
                minY = Mathf.Min(minY, bounds.yMin - StoryGraphMetrics.Margin * 0.25f);
                maxX = Mathf.Max(maxX, bounds.xMax + StoryGraphMetrics.Margin * 0.25f);
                maxY = Mathf.Max(maxY, bounds.yMax + StoryGraphMetrics.Margin * 0.25f);
            }
            _layout.Bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            RefreshGroupVisualStates();
        }

        private bool TryGetGroupBounds(
            StoryGraphWorkspace.GroupData group, out Rect bounds)
        {
            bounds = new Rect();
            if (group == null) return false;
            if (group.IsNote)
            {
                bounds = new Rect(group.X, group.Y,
                    Mathf.Max(160f, group.Width), Mathf.Max(100f, group.Height));
                return true;
            }

            bool found = false;
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            var keys = new HashSet<string>(
                group.NodeKeys ?? new List<string>(), StringComparer.Ordinal);
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                string key = StoryGraphWorkspace.StableNodeKey(node);
                Rect rect;
                if (string.IsNullOrEmpty(key) || !keys.Contains(key)
                    || !_layout.TryGetRect(node, out rect)) continue;
                found = true;
                minX = Mathf.Min(minX, rect.xMin);
                minY = Mathf.Min(minY, rect.yMin);
                maxX = Mathf.Max(maxX, rect.xMax);
                maxY = Mathf.Max(maxY, rect.yMax);
            }
            if (!found) return false;
            float noteExtra = string.IsNullOrWhiteSpace(group.Note) ? 0f : 52f;
            bounds = Rect.MinMaxRect(
                Mathf.Max(8f, minX - 28f),
                Mathf.Max(8f, minY - 44f),
                maxX + 28f,
                maxY + 28f + noteExtra);
            return true;
        }

        private GroupVisual CreateGroupVisual(
            StoryGraphWorkspace.GroupData group, Rect bounds)
        {
            GameObject root = CreateUIObject(
                group.IsNote ? "Note" : "Group", _groupContainer);
            RectTransform rect = (RectTransform)root.transform;
            Place(rect, 0f, 1f, 0f, 1f,
                bounds.x, -bounds.y, bounds.width, bounds.height);
            Color color = ParseGroupColor(group.Color);
            Image background = root.AddComponent<Image>();
            background.color = new Color(color.r, color.g, color.b,
                group.IsNote ? 0.20f : 0.11f);
            background.raycastTarget = _editMode;
            ApplySprite(background, _roundedSprite);

            Image header = CreateImage(root, "Header",
                new Color(color.r, color.g, color.b, 0.42f), false);
            header.rectTransform.anchorMin = new Vector2(0f, 1f);
            header.rectTransform.anchorMax = new Vector2(1f, 1f);
            header.rectTransform.pivot = new Vector2(0.5f, 1f);
            header.rectTransform.anchoredPosition = Vector2.zero;
            header.rectTransform.sizeDelta = new Vector2(0f, 34f);
            ApplySprite(header, _roundedSprite);

            Text title = CreateText(root, "Title", 15, FontStyle.Bold,
                TitleColor, TextAnchor.MiddleLeft);
            title.text = (group.IsNote ? "注｜" : "组｜")
                         + (group.Title ?? string.Empty);
            title.rectTransform.anchorMin = new Vector2(0f, 1f);
            title.rectTransform.anchorMax = new Vector2(1f, 1f);
            title.rectTransform.pivot = new Vector2(0.5f, 1f);
            title.rectTransform.anchoredPosition = Vector2.zero;
            title.rectTransform.sizeDelta = new Vector2(-20f, 34f);

            Text note = CreateText(root, "NoteText", 13, FontStyle.Normal,
                BodyTextColor, TextAnchor.UpperLeft);
            note.text = group.Note ?? string.Empty;
            note.rectTransform.anchorMin = Vector2.zero;
            note.rectTransform.anchorMax = Vector2.one;
            note.rectTransform.offsetMin = new Vector2(12f, 10f);
            note.rectTransform.offsetMax = new Vector2(-12f, -42f);
            note.gameObject.SetActive(!string.IsNullOrWhiteSpace(note.text));

            var visual = new GroupVisual
            {
                Group = group,
                Root = root,
                Rect = rect,
                Title = title,
                Note = note,
            };
            EventTrigger trigger = root.AddComponent<EventTrigger>();
            trigger.triggers.Add(MakeTrigger(EventTriggerType.PointerClick,
                delegate(BaseEventData data)
                {
                    OnGroupPointerClick(visual, data as PointerEventData);
                }));
            trigger.triggers.Add(MakeTrigger(EventTriggerType.BeginDrag,
                delegate(BaseEventData data)
                {
                    BeginGroupDrag(visual, data as PointerEventData);
                }));
            trigger.triggers.Add(MakeTrigger(EventTriggerType.Drag,
                delegate(BaseEventData data)
                {
                    UpdateGroupDrag(data as PointerEventData);
                }));
            trigger.triggers.Add(MakeTrigger(EventTriggerType.EndDrag,
                delegate(BaseEventData data)
                {
                    EndGroupDrag(data as PointerEventData);
                }));
            return visual;
        }

        private static Color ParseGroupColor(string value)
        {
            Color color;
            if (!string.IsNullOrWhiteSpace(value)
                && ColorUtility.TryParseHtmlString("#" + value.Trim(), out color))
                return color;
            return HexColor("D7C49A");
        }

        private void RefreshGroupVisualStates()
        {
            foreach (GroupVisual visual in _activeGroups)
            {
                if (visual == null || visual.Root == null) continue;
                Image image = visual.Root.GetComponent<Image>();
                if (image == null) continue;
                Color color = ParseGroupColor(visual.Group.Color);
                bool selected = string.Equals(
                    _selectedGroupId, visual.Group.Id, StringComparison.Ordinal);
                image.color = new Color(color.r, color.g, color.b,
                    selected ? 0.28f : (visual.Group.IsNote ? 0.20f : 0.11f));
            }
        }

        private void OnGroupPointerClick(GroupVisual visual, PointerEventData data)
        {
            if (!_editMode || visual == null || data == null
                || data.button != PointerEventData.InputButton.Left) return;
            EndInspectorLiveEdit();
            _selectedGroupId = visual.Group.Id;
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            _selectedNode = null;
            SyncPrimaryEditSelection();
            RefreshAllNodeStates();
            RefreshGroupVisualStates();
            UpdateEditControls();
            RefreshInspector();
            _editStatus = visual.Group.IsNote
                ? "已选择注释框；可拖动整体移动，并在属性检查器编辑标题和内容。"
                : "已选择分组；拖动分组空白区域可整体移动成员节点。";
            UpdateStatusBar();
        }

        // ==================== 节点构建与池 ====================

        private NodeVisual AcquireNode()
        {
            NodeVisual nv = _nodePool.Count > 0 ? _nodePool.Pop() : CreateNodeVisual();
            nv.Root.SetActive(true);
            return nv;
        }

        private void ReleaseNode(NodeVisual nv)
        {
            if (nv == null) return;
            nv.Trigger.triggers.Clear();
            for (int i = 0; i < nv.Ports.Count; i++)
            {
                nv.Ports[i].Root.SetActive(false);
                nv.Ports[i].Owner = nv;
            }
            nv.SegmentToggleKey = null;
            if (nv.SegmentToggle != null)
                nv.SegmentToggle.gameObject.SetActive(false);
            nv.Node = null;
            nv.Root.SetActive(false);
            _nodePool.Push(nv);
        }

        private NodeVisual CreateNodeVisual()
        {
            var nv = new NodeVisual();
            GameObject root = CreateUIObject("Node", _nodeContainer);
            nv.Root = root;
            nv.Rect = (RectTransform)root.transform;
            nv.CardRoot = CreateUIObject("Card", root.transform);
            nv.CardRect = (RectTransform)nv.CardRoot.transform;
            Stretch(nv.CardRect, 0f, 0f, 0f, 0f);

            // 三层高亮环：铺在边框底下，靠负边距露出。
            nv.CurrentRing = CreateImage(
                nv.CardRoot, "CurrentRing", CurrentRingColor, false);
            Stretch(nv.CurrentRing.rectTransform, -7f, -7f, -7f, -7f);
            ApplySprite(nv.CurrentRing, _roundedSprite);
            nv.SearchRing = CreateImage(
                nv.CardRoot, "SearchRing", SearchRingColor, false);
            Stretch(nv.SearchRing.rectTransform, -5f, -5f, -5f, -5f);
            ApplySprite(nv.SearchRing, _roundedSprite);
            nv.SelectRing = CreateImage(
                nv.CardRoot, "SelectRing", SelectRingColor, false);
            Stretch(nv.SelectRing.rectTransform, -3f, -3f, -3f, -3f);
            ApplySprite(nv.SelectRing, _roundedSprite);

            nv.Border = CreateImage(
                nv.CardRoot, "Border", Color.white, true); // 射线接收者
            Stretch(nv.Border.rectTransform, 0f, 0f, 0f, 0f);
            ApplySprite(nv.Border, _roundedSprite);
            nv.Fill = CreateImage(nv.CardRoot, "Fill", Color.black, false);
            Stretch(nv.Fill.rectTransform,
                NodeBorderThickness, NodeBorderThickness,
                NodeBorderThickness, NodeBorderThickness);
            ApplySprite(nv.Fill, _roundedSprite);

            // 左缘语义色条：普通矩形 Image，承担原边框的旗标语义。
            nv.ColorBar = CreateImage(
                nv.CardRoot, "ColorBar", Color.white, false);
            nv.ColorBar.rectTransform.anchorMin = new Vector2(0f, 0f);
            nv.ColorBar.rectTransform.anchorMax = new Vector2(0f, 1f);
            nv.ColorBar.rectTransform.offsetMin = new Vector2(2f, 4f);
            nv.ColorBar.rectTransform.offsetMax = new Vector2(7f, -4f);

            nv.Title = CreateText(nv.CardRoot, "Title", 14, FontStyle.Bold,
                TitleColor, TextAnchor.UpperLeft);
            // 标题：顶部固定 20px 高横带（左缩进让开色条）
            nv.Title.rectTransform.anchorMin = new Vector2(0f, 1f);
            nv.Title.rectTransform.anchorMax = new Vector2(1f, 1f);
            nv.Title.rectTransform.offsetMin = new Vector2(14f, -24f);
            nv.Title.rectTransform.offsetMax = new Vector2(-8f, -4f);

            nv.Subtitle = CreateText(
                nv.CardRoot, "Subtitle", 11, FontStyle.Normal,
                SubtitleColor, TextAnchor.UpperLeft);
            nv.Subtitle.lineSpacing = 1.2f;
            // 摘要：标题之下填满剩余区域，自动换行截断
            nv.Subtitle.rectTransform.anchorMin = Vector2.zero;
            nv.Subtitle.rectTransform.anchorMax = Vector2.one;
            nv.Subtitle.rectTransform.offsetMin = new Vector2(14f, 5f);
            nv.Subtitle.rectTransform.offsetMax = new Vector2(-8f, -27f);

            // 展开链段的收起入口是卡片下方的独立操作带；外层 Node 已由布局层
            // 为它预留固定高度。视觉恢复为旧版暖黄链段卡片，但仍不成为图节点。
            nv.SegmentToggle = CreateImage(
                root, "SegmentToggle", AdjacentBorderColor, true);
            Place(nv.SegmentToggle.rectTransform,
                0f, 0f, 0f, 0f, 0f, 0f, 250f,
                StoryGraphMetrics.SegmentControlHeight);
            ApplySprite(nv.SegmentToggle, _roundedSprite);
            nv.SegmentToggleFill = CreateImage(
                nv.SegmentToggle.gameObject, "Fill", SegmentFill, false);
            Stretch(nv.SegmentToggleFill.rectTransform, 2f, 2f, 2f, 2f);
            ApplySprite(nv.SegmentToggleFill, _roundedSprite);
            nv.SegmentToggleColorBar = CreateImage(
                nv.SegmentToggle.gameObject, "ColorBar", AdjacentBorderColor, false);
            nv.SegmentToggleColorBar.rectTransform.anchorMin = new Vector2(0f, 0f);
            nv.SegmentToggleColorBar.rectTransform.anchorMax = new Vector2(0f, 1f);
            nv.SegmentToggleColorBar.rectTransform.offsetMin = new Vector2(3f, 5f);
            nv.SegmentToggleColorBar.rectTransform.offsetMax = new Vector2(8f, -5f);
            nv.SegmentToggleLabel = CreateText(
                nv.SegmentToggle.gameObject, "Label", 12,
                FontStyle.Bold, TitleColor, TextAnchor.MiddleLeft);
            Stretch(nv.SegmentToggleLabel.rectTransform, 16f, 0f, 8f, 0f);
            nv.SegmentToggleTrigger =
                nv.SegmentToggle.gameObject.AddComponent<EventTrigger>();
            NodeVisual capturedToggleOwner = nv;
            nv.SegmentToggleTrigger.triggers.Add(MakeTrigger(
                EventTriggerType.PointerClick,
                delegate(BaseEventData data)
                {
                    OnSegmentTogglePointerClick(
                        capturedToggleOwner, data as PointerEventData);
                }));
            nv.SegmentToggleTrigger.triggers.Add(MakeTrigger(
                EventTriggerType.PointerEnter,
                delegate(BaseEventData data)
                {
                    if (capturedToggleOwner.Node != null)
                        SetHover(capturedToggleOwner.Node);
                }));
            nv.SegmentToggleTrigger.triggers.Add(MakeTrigger(
                EventTriggerType.PointerExit,
                delegate(BaseEventData data) { ClearHover(); }));
            nv.SegmentToggle.gameObject.SetActive(false);

            nv.Trigger = nv.CardRoot.AddComponent<EventTrigger>();
            for (int i = 0; i < 3; i++)
                nv.Ports.Add(CreatePortVisual(nv, i));
            return nv;
        }

        private PortVisual CreatePortVisual(NodeVisual owner, int index)
        {
            var port = new PortVisual { Owner = owner };
            GameObject go = CreateUIObject(
                "EditPort" + index, owner.CardRoot.transform);
            port.Root = go;
            port.Rect = (RectTransform)go.transform;
            port.Background = go.AddComponent<Image>();
            port.Background.color = SegmentFill;
            port.Background.raycastTarget = true;
            ApplySprite(port.Background, _chipSprite);
            port.Label = CreateText(go, "Label", 11, FontStyle.Bold,
                BodyTextColor, TextAnchor.MiddleCenter);
            Stretch(port.Label.rectTransform, 3f, 0f, 3f, 0f);
            port.Trigger = go.AddComponent<EventTrigger>();
            port.Trigger.triggers.Add(MakeTrigger(EventTriggerType.BeginDrag,
                delegate(BaseEventData data) { BeginConnectionDrag(port, data as PointerEventData); }));
            port.Trigger.triggers.Add(MakeTrigger(EventTriggerType.Drag,
                delegate(BaseEventData data) { UpdateConnectionDrag(data as PointerEventData); }));
            port.Trigger.triggers.Add(MakeTrigger(EventTriggerType.EndDrag,
                delegate(BaseEventData data) { EndConnectionDrag(data as PointerEventData); }));
            port.Trigger.triggers.Add(MakeTrigger(EventTriggerType.PointerClick,
                delegate(BaseEventData data) { OnPortPointerClick(port, data as PointerEventData); }));
            port.Trigger.triggers.Add(MakeTrigger(EventTriggerType.PointerEnter,
                delegate(BaseEventData data) { OnPortPointerEnter(port); }));
            go.SetActive(false);
            return port;
        }

        private void BuildNodeVisual(StoryGraphDisplayNode node)
        {
            if (node == null) return;
            Rect rect;
            if (!_layout.TryGetRect(node, out rect)) return;

            NodeVisual nv = AcquireNode();
            nv.Node = node;
            // 布局坐标 y 向下 → content 本地 (x, -y)；pivot 左上，rect 从左上展开。
            Place(nv.Rect, 0f, 1f, 0f, 1f, rect.x, -rect.y, rect.width, rect.height);

            if (node.IsSegment)
            {
                // 链段块：独立底色 + 「▶ N 句对话」，摘要取 ViewModel 给的首句摘要。
                nv.Title.text = SegmentTitle(node);
                string subtitle = node.Subtitle;
                if (string.IsNullOrEmpty(subtitle)
                    && node.SegmentNodes != null && node.SegmentNodes.Count > 0
                    && node.SegmentNodes[0] != null)
                    subtitle = node.SegmentNodes[0].Subtitle;
                nv.Subtitle.text = subtitle ?? string.Empty;
            }
            else
            {
                // 入口/结束属于常规流程信息，直接写在卡片标题上；这样正文完整时
                // 无需为了说明色条再弹出一个重复内容的悬浮框。
                nv.Title.text = BuildNodeTitle(node);
                nv.Subtitle.text = node.Subtitle ?? string.Empty;
            }

            ConfigureViewSegmentToggle(nv);
            ConfigureEditPorts(nv);

            NodeVisual captured = nv;
            nv.Trigger.triggers.Add(MakeTrigger(
                EventTriggerType.PointerEnter,
                delegate(BaseEventData data) { SetHover(captured.Node); }));
            nv.Trigger.triggers.Add(MakeTrigger(
                EventTriggerType.PointerExit,
                delegate(BaseEventData data) { ClearHover(); }));
            nv.Trigger.triggers.Add(MakeTrigger(
                EventTriggerType.PointerClick,
                delegate(BaseEventData data)
                {
                    OnNodePointerClick(captured, data as PointerEventData);
                }));
            nv.Trigger.triggers.Add(MakeTrigger(
                EventTriggerType.BeginDrag,
                delegate(BaseEventData data)
                {
                    BeginNodeDrag(captured, data as PointerEventData);
                }));
            nv.Trigger.triggers.Add(MakeTrigger(
                EventTriggerType.Drag,
                delegate(BaseEventData data)
                {
                    UpdateNodeDrag(data as PointerEventData);
                }));
            nv.Trigger.triggers.Add(MakeTrigger(
                EventTriggerType.EndDrag,
                delegate(BaseEventData data)
                {
                    EndNodeDrag(data as PointerEventData);
                }));

            _activeNodes.Add(nv);
            _nodeLookup[node] = nv;
        }

        private void ConfigureViewSegmentToggle(NodeVisual nv)
        {
            if (nv == null) return;
            nv.SegmentToggleKey = null;
            nv.SegmentToggle.gameObject.SetActive(false);
            bool reserveSegmentControl = nv.Node != null
                && nv.Node.HasExpandedSegmentControl;
            Stretch(nv.CardRect, 0f,
                reserveSegmentControl
                    ? StoryGraphMetrics.SegmentControlReserve : 0f,
                0f, 0f);
            nv.Title.rectTransform.offsetMax = new Vector2(-8f, -4f);
            nv.Subtitle.rectTransform.offsetMin = new Vector2(14f, 5f);
            if (_editMode || _viewModel == null || nv.Node == null) return;

            string key;
            int count;
            if (!_viewModel.TryGetExpandedSegmentHead(
                    nv.Node, out key, out count)) return;

            nv.SegmentToggleKey = key;
            nv.SegmentToggleLabel.text = ExpandedSegmentToggleTitle(count);
            nv.SegmentToggle.gameObject.SetActive(true);
            nv.SegmentToggle.transform.SetAsLastSibling();
        }

        private void OnSegmentTogglePointerClick(
            NodeVisual nv, PointerEventData data)
        {
            if (_editMode || nv == null || data == null
                || data.button != PointerEventData.InputButton.Left
                || string.IsNullOrEmpty(nv.SegmentToggleKey)) return;
            ToggleViewContainer(nv.SegmentToggleKey);
        }

        private void ConfigureEditPorts(NodeVisual nv)
        {
            for (int i = 0; i < nv.Ports.Count; i++)
                nv.Ports[i].Root.SetActive(false);
            if (!_editMode || _editSession == null || nv.Node == null
                || nv.Node.SourceNode == null) return;

            EvtStoryGraphNode source = nv.Node.SourceNode;
            if (source.Talk != null)
            {
                if (MiniGameUtil.IsParamJump(source.Talk.miniGame))
                {
                    // 16/45 的结果写在 miniGame 参数里；显示普通胜负端口会诱导作者
                    // 写入运行时完全不读的 nextTalk/nextTalk2。仍保留“选项”端口。
                    ConfigurePort(nv.Ports[2], StoryGraphEditPortKind.TalkOption, -40f);
                    return;
                }
                bool showFailure = ShouldShowFailurePort(source.Talk);
                ConfigurePort(nv.Ports[0], StoryGraphEditPortKind.TalkNext,
                    showFailure ? -18f : -28f);
                if (showFailure)
                    ConfigurePort(nv.Ports[1], StoryGraphEditPortKind.TalkNext2, -48f);
                ConfigurePort(nv.Ports[2], StoryGraphEditPortKind.TalkOption,
                    showFailure ? -78f : -68f);
            }
            else if (source.Option != null)
            {
                bool ownMiniGame = source.Option.miniGame != null
                                   && source.Option.miniGame.Count > 0;
                bool parentMiniGame = !ownMiniGame && source.LocateTalk != null
                                      && source.LocateTalk.miniGame != null
                                      && source.LocateTalk.miniGame.Count > 0;
                bool eventMiniGame = !ownMiniGame && source.ParentEvent != null
                                     && source.ParentEvent.miniGame != null
                                     && source.ParentEvent.miniGame.Count > 0;
                // 参数跳转结果由小游戏页编辑；父 Talk 挂小游戏时，Option 自身
                // talkId/talkId2 也会被覆盖；事件级 Option 同样会被事件小游戏接管。
                // 这些情况下都不展示运行时不会读取的端口。
                if (MiniGameUtil.IsParamJump(source.Option.miniGame)
                    || parentMiniGame || eventMiniGame) return;
                int optionGameId;
                string optionGameError;
                bool successOnly = ownMiniGame && MiniGameUtil.TryGetGameId(
                    source.Option.miniGame, out optionGameId, out optionGameError)
                    && optionGameId == 29;
                bool showFailure = !successOnly && ShouldShowFailurePort(source.Option);
                ConfigurePort(nv.Ports[0], StoryGraphEditPortKind.OptionTalk,
                    showFailure ? -24f : -40f);
                if (showFailure)
                    ConfigurePort(nv.Ports[1], StoryGraphEditPortKind.OptionTalk2, -56f);
            }
        }

        private static bool ShouldShowFailurePort(TalkCfg talk)
        {
            return talk != null
                && ((talk.nextTalk2 != null && talk.nextTalk2.Any(value => value != 0))
                    || (talk.check != null && talk.check.Count > 0)
                    || (talk.miniGame != null && talk.miniGame.Count > 0));
        }

        private static bool ShouldShowFailurePort(OptionCfg option)
        {
            return option != null
                && ((option.talkId2 != null && option.talkId2.Any(value => value != 0))
                    || (option.check != null && option.check.Count > 0)
                    || (option.miniGame != null && option.miniGame.Count > 0));
        }

        private void ConfigurePort(
            PortVisual port, StoryGraphEditPortKind kind, float y)
        {
            port.Kind = kind;
            Place(port.Rect, 1f, 1f, 0f, 0.5f,
                4f, y, PortWidth, PortHeight);
            bool connected = PortHasConnection(port.Owner.Node, kind);
            port.Label.text = (connected ? "● " : "○ ")
                              + ShortPortLabel(port.Owner.Node, kind);
            port.Background.color = connected ? SegmentFill : PanelBg;
            port.Root.SetActive(true);
            port.Root.transform.SetAsLastSibling();
        }

        private static string ShortPortLabel(
            StoryGraphDisplayNode node, StoryGraphEditPortKind kind)
        {
            EvtStoryGraphNode source = node != null ? node.SourceNode : null;
            bool talkMiniGame = source != null && source.Talk != null
                && source.Talk.miniGame != null && source.Talk.miniGame.Count > 0;
            bool optionMiniGame = source != null && source.Option != null
                && source.Option.miniGame != null && source.Option.miniGame.Count > 0;
            switch (kind)
            {
                case StoryGraphEditPortKind.TalkNext:
                    return talkMiniGame ? "成功" : "下一句";
                case StoryGraphEditPortKind.TalkNext2: return "失败";
                case StoryGraphEditPortKind.TalkOption: return "选项";
                case StoryGraphEditPortKind.OptionTalk:
                    return optionMiniGame ? "成功" : "结果";
                case StoryGraphEditPortKind.OptionTalk2: return "失败";
                default: return "连接";
            }
        }

        private static bool PortHasConnection(
            StoryGraphDisplayNode node, StoryGraphEditPortKind kind)
        {
            if (node == null || node.SourceNode == null) return false;
            TalkCfg talk = node.SourceNode.Talk;
            OptionCfg option = node.SourceNode.Option;
            List<int> values = null;
            switch (kind)
            {
                case StoryGraphEditPortKind.TalkNext:
                    values = talk != null ? talk.nextTalk : null; break;
                case StoryGraphEditPortKind.TalkNext2:
                    values = talk != null ? talk.nextTalk2 : null; break;
                case StoryGraphEditPortKind.TalkOption:
                    values = talk != null ? talk.option : null; break;
                case StoryGraphEditPortKind.OptionTalk:
                    values = option != null ? option.talkId : null; break;
                case StoryGraphEditPortKind.OptionTalk2:
                    values = option != null ? option.talkId2 : null; break;
            }
            return values != null && values.Any(value => value != 0);
        }

        private void BeginConnectionDrag(PortVisual port, PointerEventData data)
        {
            if (_nodeDrag != null
                || !_editMode || _editSession == null || port == null || data == null
                || data.button != PointerEventData.InputButton.Left
                || port.Owner == null || port.Owner.Node == null
                || port.Owner.Node.SourceNode == null) return;

            EvtStoryGraphNode source = port.Owner.Node.SourceNode;
            _connectionDrag = new ConnectionDragState
            {
                Port = port,
                Kind = port.Kind,
                SourceTalk = source.Talk,
                SourceOption = source.Option,
            };
            _panning = false;
            _panButton = null;
            HideTooltip();
            if (_connectionPreview != null)
                _connectionPreview.gameObject.SetActive(true);
            UpdateConnectionDrag(data);
            _editStatus = "正在连接“" + StoryGraphEditSession.PortLabel(port.Kind)
                          + "”：松到目标对话卡片（或它的端口）即可连接；"
                          + "松到空白处打开创建菜单，右击端口可断开。";
            UpdateStatusBar();
        }

        private void UpdateConnectionDrag(PointerEventData data)
        {
            if (_connectionDrag == null || data == null || _content == null
                || _connectionPreview == null) return;
            Vector2 start;
            Vector2 end;
            Vector2 startScreen = RectTransformUtility.WorldToScreenPoint(
                null, _connectionDrag.Port.Rect.TransformPoint(
                    _connectionDrag.Port.Rect.rect.center));
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _content, startScreen, null, out start)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _content, data.position, null, out end)) return;
            DrawConnectionPreview(start, end);
        }

        private void DrawConnectionPreview(Vector2 start, Vector2 end)
        {
            Vector2 delta = end - start;
            float length = delta.magnitude;
            if (length < 0.5f) length = 0.5f;
            RectTransform rt = _connectionPreview.rectTransform;
            Place(rt, 0f, 1f, 0.5f, 0.5f,
                (start.x + end.x) * 0.5f,
                (start.y + end.y) * 0.5f,
                length, Mathf.Max(2f, 4f / Mathf.Max(_zoom, 0.01f)));
            rt.localRotation = Quaternion.Euler(
                0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            _connectionPreview.gameObject.SetActive(true);
            _connectionPreview.transform.SetAsLastSibling();
        }

        private void EndConnectionDrag(PointerEventData data)
        {
            EndInspectorLiveEdit();
            ConnectionDragState drag = _connectionDrag;
            CancelConnectionDrag();
            if (drag == null || data == null || _editSession == null) return;
            // 先检查实际可见视口，再解析目标。节点 Rect 即使被 RectMask2D 裁掉仍
            // active，旧顺序可能在工具栏/小地图背后命中不可见节点。
            if (!PointerInViewport(data.position) || PointerOverMinimap(data.position))
            {
                SetEditFeedback("已取消连线：请在剧情图可见画布内松开。", false);
                return;
            }

            NodeVisual targetVisual = FindNodeVisualAt(data.position);
            StoryGraphDisplayNode targetNode = targetVisual != null
                ? targetVisual.Node
                : null;
            TalkCfg targetTalk = targetNode != null && targetNode.SourceNode != null
                ? targetNode.SourceNode.Talk
                : null;
            OptionCfg targetOption = targetNode != null && targetNode.SourceNode != null
                ? targetNode.SourceNode.Option
                : null;

            string message;
            if (targetTalk != null || targetOption != null)
            {
                if (_editSession.TryConnect(drag.Kind,
                        drag.SourceTalk, drag.SourceOption,
                        targetTalk, targetOption, out message))
                {
                    StoryGraphEditNodeKind kind = targetTalk != null
                        ? StoryGraphEditNodeKind.Talk
                        : StoryGraphEditNodeKind.Option;
                    int id = targetTalk != null
                        ? targetTalk.id
                        : _editSession.FindOptionKey(targetOption);
                    RefreshEditGraph(kind, id, message, false);
                }
                else SetEditFeedback(message, true);
                return;
            }

            ConnectionDragState pending = drag;
            Vector2 point = data.position;
            bool createsOption = drag.Kind == StoryGraphEditPortKind.TalkOption;
            if (createsOption)
            {
                // OptionCfg 没有“普通/条件/结束/小游戏”四种存储类型；先创建
                // 一个完整可配置的通用选项，作者随后在右侧属性中自由组合逻辑、
                // 路线与小游戏，避免创建前做一次没有约束力的重复选择。
                CreateOptionFromTemplateAt(
                    pending.SourceTalk,
                    pending.Port != null && pending.Port.Owner != null
                        ? pending.Port.Owner.Node : null,
                    point, StoryGraphOptionTemplateKind.Direct,
                    true, true);
                return;
            }

            var items = new List<ContextMenuItem>
            {
                new ContextMenuItem
                {
                    Label = "新建对话并连接",
                    Action = delegate { CreateConnectedAt(pending, point); },
                },
            };
            items.Add(new ContextMenuItem
            {
                Label = "取消",
                Action = delegate
                {
                    SetEditFeedback("已取消空白处创建节点。", false);
                },
            });
            ShowContextMenu(point, items);
            _editStatus = "已松开到空白处：请选择要创建的对话，或取消。";
            UpdateStatusBar();
        }

        private void CancelConnectionDrag()
        {
            _connectionDrag = null;
            if (_connectionPreview != null)
            {
                _connectionPreview.gameObject.SetActive(false);
                _connectionPreview.rectTransform.localRotation = Quaternion.identity;
            }
        }

        private NodeVisual FindNodeVisualAt(Vector2 screenPoint)
        {
            for (int i = _activeNodes.Count - 1; i >= 0; i--)
            {
                NodeVisual visual = _activeNodes[i];
                if (visual == null || !visual.Root.activeInHierarchy) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(
                        visual.Rect, screenPoint, null))
                    return visual;

                // ComfyUI 用户会自然地把线松在目标端口上。端口位于卡片 Rect
                // 之外，旧实现只命中卡片本体，因而会误判为空白并弹出创建菜单。
                foreach (PortVisual port in visual.Ports)
                {
                    if (port == null || port.Root == null
                        || !port.Root.activeInHierarchy
                        || (_connectionDrag != null
                            && ReferenceEquals(port, _connectionDrag.Port))) continue;
                    if (RectTransformUtility.RectangleContainsScreenPoint(
                            port.Rect, screenPoint, null))
                        return visual;
                }
            }
            return null;
        }

        private void OnPortPointerClick(PortVisual port, PointerEventData data)
        {
            if (!_editMode || _editSession == null || port == null || data == null
                || data.button != PointerEventData.InputButton.Right
                || port.Owner == null || port.Owner.Node == null
                || port.Owner.Node.SourceNode == null) return;
            EvtStoryGraphNode source = port.Owner.Node.SourceNode;
            List<int> values = _editSession.SnapshotPortValues(
                port.Kind, source.Talk, source.Option);
            List<int> connectedIndexes = Enumerable.Range(0, values.Count)
                .Where(index => values[index] != 0).ToList();
            if (connectedIndexes.Count > 1)
            {
                ShowPortDisconnectMenu(
                    port, source, values, connectedIndexes, data.position);
                return;
            }

            string message;
            if (_editSession.TryClearPort(
                    port.Kind, source.Talk, source.Option, out message))
                RefreshAfterPortMutation(source, message);
            else SetEditFeedback(message, false);
        }

        private void ShowPortDisconnectMenu(
            PortVisual port,
            EvtStoryGraphNode source,
            IList<int> values,
            IList<int> connectedIndexes,
            Vector2 screenPoint)
        {
            var items = new List<ContextMenuItem>();
            if (port.Kind == StoryGraphEditPortKind.TalkNext2)
            {
                items.Add(new ContextMenuItem
                {
                    Label = "整组恢复为 nextTalk（清空失败分支）",
                    Action = delegate
                    {
                        string message;
                        if (_editSession.TryClearPort(
                                port.Kind, source.Talk, source.Option, out message))
                            RefreshAfterPortMutation(source, message);
                        else SetEditFeedback(message, false);
                    },
                });
            }
            else
            {
                const int maxIndividualItems = 7;
                for (int i = 0;
                     i < connectedIndexes.Count && i < maxIndividualItems;
                     i++)
                {
                    int index = connectedIndexes[i];
                    int targetId = values[index];
                    items.Add(new ContextMenuItem
                    {
                        Label = "断开 "
                              + PortSlotMenuLabel(port.Kind, values.Count, index)
                              + " → " + targetId,
                        Action = delegate
                        {
                            string message;
                            if (_editSession.TryClearPortSlot(
                                    port.Kind, source.Talk, source.Option,
                                    index, out message))
                                RefreshAfterPortMutation(source, message);
                            else SetEditFeedback(message, false);
                        },
                    });
                }
                if (connectedIndexes.Count > maxIndividualItems)
                    items.Add(new ContextMenuItem
                    {
                        Label = "另有 "
                              + (connectedIndexes.Count - maxIndividualItems)
                              + " 条（可先逐条处理）",
                        Enabled = false,
                    });
                items.Add(new ContextMenuItem
                {
                    Label = "清空整个端口（" + connectedIndexes.Count + " 条）",
                    Action = delegate
                    {
                        string message;
                        if (_editSession.TryClearPort(
                                port.Kind, source.Talk, source.Option, out message))
                            RefreshAfterPortMutation(source, message);
                        else SetEditFeedback(message, false);
                    },
                });
            }
            ShowContextMenu(screenPoint, items);
            _editStatus = port.Kind == StoryGraphEditPortKind.TalkNext2
                ? "原生 nextTalk2 不能独立清空首个性别槽；菜单会安全地让整组回退到 nextTalk。"
                : "该端口有多条连线：请选择要断开的具体槽位，或明确清空整个端口。";
            UpdateStatusBar();
        }

        private void RefreshAfterPortMutation(
            EvtStoryGraphNode source, string message)
        {
            StoryGraphEditNodeKind kind = source != null && source.Talk != null
                ? StoryGraphEditNodeKind.Talk
                : StoryGraphEditNodeKind.Option;
            int id = source != null && source.Talk != null
                ? source.Talk.id
                : _editSession.FindOptionKey(source != null ? source.Option : null);
            RefreshEditGraph(kind, id, message, false);
        }

        private static string PortSlotMenuLabel(
            StoryGraphEditPortKind port, int count, int index)
        {
            if (port == StoryGraphEditPortKind.TalkOption)
                return "选项第 " + (index + 1) + " 项";
            if (count <= 1) return "共用槽位";
            if (index == 0) return "男性槽位";
            if (index == 1) return "女性槽位";
            return "额外第 " + (index + 1) + " 项（运行时忽略）";
        }

        private void OnPortPointerEnter(PortVisual port)
        {
            if (!_editMode || port == null) return;
            _editStatus = PortPurpose(port)
                          + " 拖到目标对话卡片或端口建立连线；"
                          + "拖到空白处选择新建；右击可逐条断线；Ctrl+Z 撤销。";
            UpdateStatusBar();
        }

        private static string PortPurpose(PortVisual port)
        {
            if (port == null || port.Owner == null || port.Owner.Node == null
                || port.Owner.Node.SourceNode == null)
                return "拖动端口建立连线。";
            EvtStoryGraphNode source = port.Owner.Node.SourceNode;
            if (port.Kind == StoryGraphEditPortKind.TalkNext)
            {
                if (source.Talk != null && source.Talk.miniGame != null
                    && source.Talk.miniGame.Count > 0)
                    return "“成功”是该对话小游戏成功后的去向（原生 nextTalk）。";
            }
            if (port.Kind == StoryGraphEditPortKind.TalkNext2)
            {
                if (source.Talk != null && source.Talk.miniGame != null
                    && source.Talk.miniGame.Count > 0)
                    return "“失败”是该对话小游戏失败后的去向。";
                if (source.Talk != null && source.Talk.check != null
                    && source.Talk.check.Count > 0)
                    return "“失败”是 check 条件不成立后的去向。";
                return "“失败”对应原生 nextTalk2；当前已有该字段，可能不会被普通对话执行。";
            }
            if (port.Kind == StoryGraphEditPortKind.OptionTalk)
            {
                if (source.Option != null && source.Option.miniGame != null
                    && source.Option.miniGame.Count > 0)
                {
                    int gameId;
                    string error;
                    if (MiniGameUtil.TryGetGameId(
                            source.Option.miniGame, out gameId, out error)
                        && gameId == 29)
                        return "“成功”是大头贴 29 结束后的唯一去向（原生 talkId）。";
                    return "“成功”是该选项小游戏成功后的去向（原生 talkId）。";
                }
            }
            if (port.Kind == StoryGraphEditPortKind.OptionTalk2)
            {
                if (source.Option != null && source.Option.miniGame != null
                    && source.Option.miniGame.Count > 0)
                    return "“失败”是该选项小游戏失败后的去向。";
                if (source.Option != null && source.Option.check != null
                    && source.Option.check.Count > 0)
                    return "“失败”是选项 check 条件不成立后的去向。";
                return "“失败”对应原生 talkId2；当前已有该字段，可能不会被普通选项执行。";
            }
            return "拖动“" + StoryGraphEditSession.PortLabel(port.Kind) + "”建立连线。";
        }

        private void BeginNodeDrag(NodeVisual visual, PointerEventData data)
        {
            // 拖动是独立的工作区动作；先封口连续文字编辑，避免随后保存的坐标
            // 与上一轮分组/注释输入共用同一条统一撤销记录。
            EndInspectorLiveEdit();
            // 端口子对象拥有自己的 EventTrigger，正常情况下 EventSystem 只会把
            // drag 交给最近的 handler；这里再显式互斥，防自定义输入模块冒泡。
            if (_connectionDrag != null) return;
            if (!_editMode || _workspace == null || _layout == null
                || visual == null || visual.Node == null || data == null
                || data.button != PointerEventData.InputButton.Left
                || visual.Node.SourceNode == null
                || (visual.Node.SourceNode.Talk == null
                    && visual.Node.SourceNode.Option == null)) return;
            Keyboard keyboard = Keyboard.current;
            bool additive = keyboard != null
                && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed
                    || keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
            if (!_selectedNodes.Contains(visual.Node))
                SetEditSelection(visual.Node, additive, false);

            Vector2 pointer;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _content, data.position, null, out pointer)) return;
            var drag = new NodeDragState
            {
                StartPointer = pointer,
                Before = _workspace.Capture(),
                BeforeDirty = _workspace.Dirty,
            };
            foreach (StoryGraphDisplayNode node in _selectedNodes)
            {
                Rect rect;
                if (node != null && node.SourceNode != null
                    && (node.SourceNode.Talk != null || node.SourceNode.Option != null)
                    && _layout.TryGetRect(node, out rect))
                    drag.StartRects[node] = rect;
            }
            if (drag.StartRects.Count == 0) return;
            _nodeDrag = drag;
            _panning = false;
            _panButton = null;
            HideTooltip();
        }

        private void UpdateNodeDrag(PointerEventData data)
        {
            if (_connectionDrag != null
                || _nodeDrag == null || data == null || _layout == null) return;
            Vector2 pointer;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _content, data.position, null, out pointer)) return;
            Vector2 delta = new Vector2(
                pointer.x - _nodeDrag.StartPointer.x,
                _nodeDrag.StartPointer.y - pointer.y);
            Keyboard keyboard = Keyboard.current;
            bool free = keyboard != null
                && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
            if (!free)
            {
                const float grid = 10f;
                delta.x = Mathf.Round(delta.x / grid) * grid;
                delta.y = Mathf.Round(delta.y / grid) * grid;
            }
            float minStartX = float.MaxValue;
            float minStartY = float.MaxValue;
            foreach (Rect startRect in _nodeDrag.StartRects.Values)
            {
                minStartX = Mathf.Min(minStartX, startRect.x);
                minStartY = Mathf.Min(minStartY, startRect.y);
            }
            // 多选必须共用同一 delta；逐节点 Mathf.Max 会在碰到左/上边界时
            // 压缩节点间距，破坏相对布局。
            delta.x = Mathf.Max(delta.x, StoryGraphMetrics.Margin - minStartX);
            delta.y = Mathf.Max(delta.y, StoryGraphMetrics.Margin - minStartY);
            if (delta.sqrMagnitude < 0.01f) return;
            _nodeDrag.Moved = true;
            foreach (KeyValuePair<StoryGraphDisplayNode, Rect> pair in _nodeDrag.StartRects)
            {
                Rect rect = pair.Value;
                rect.x += delta.x;
                rect.y += delta.y;
                _layout.NodeRects[pair.Key] = rect;
                NodeVisual visual;
                if (_nodeLookup.TryGetValue(pair.Key, out visual))
                    Place(visual.Rect, 0f, 1f, 0f, 1f,
                        rect.x, -rect.y, rect.width, rect.height);
            }
            StoryGraphLayoutEngine.RerouteCurrent(_layout, _viewModel.Edges);
            // 拖动期间暂不重建小地图点；松手时统一刷新，避免大型图每帧
            // 同时重建边和小地图。
            RebuildEdgesOnly(false);
        }

        private void EndNodeDrag(PointerEventData data)
        {
            NodeDragState drag = _nodeDrag;
            _nodeDrag = null;
            if (drag == null || !drag.Moved || _workspace == null || _layout == null)
                return;

            foreach (StoryGraphDisplayNode node in drag.StartRects.Keys)
            {
                Rect rect;
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key) && _layout.TryGetRect(node, out rect))
                    _workspace.SetPosition(key, rect.position);
            }

            string error;
            if (!_workspace.Save(out error))
            {
                // 写盘失败不产生半条历史：工作区、布局矩形和统一时间线全部
                // 回到拖动前，下一次 Ctrl+Z 仍对应原来的最后一个成功操作。
                _workspace.Restore(drag.Before, drag.BeforeDirty);
                foreach (KeyValuePair<StoryGraphDisplayNode, Rect> pair in drag.StartRects)
                    _layout.NodeRects[pair.Key] = pair.Value;
                ApplyNodeRectVisuals(drag.StartRects.Keys);
                StoryGraphLayoutEngine.RerouteCurrent(_layout, _viewModel.Edges);
                RebuildEdgesOnly(true);
                SetEditFeedback(error + "；本次节点移动已回滚。", true);
            }
            else
            {
                _workspaceUndo.Push(drag.Before);
                _workspaceRedo.Clear();
                _editActionTimeline.Push(true);
                _editRedoTimeline.Clear();
                // 新的布局分支会使统一 redo 失效；同步清掉数据会话里已经
                // 撤销出来的 redo，避免未来时间线再次接入时读到陈旧快照。
                if (_editSession != null) _editSession.ClearRedoHistory();
                UpdateMinimapDots();
                UpdateMinimapFrame();
                _editStatus = "已移动 " + drag.StartRects.Count
                              + " 个节点；坐标仅保存在作者工作区，放弃剧情草稿也会保留。"
                              + " Alt 拖动可关闭网格吸附。";
                UpdateStatusBar();
            }
            if (_content != null)
                _content.anchoredPosition = ClampPan(_content.anchoredPosition);
            UpdateEditControls();
        }

        private void BeginGroupDrag(GroupVisual visual, PointerEventData data)
        {
            // 分组移动与标题/正文输入必须是两条独立工作区历史。
            EndInspectorLiveEdit();
            if (!_editMode || _workspace == null || _layout == null
                || visual == null || data == null
                || data.button != PointerEventData.InputButton.Left) return;
            Vector2 pointer;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _content, data.position, null, out pointer)) return;
            Rect bounds;
            if (!_groupBounds.TryGetValue(visual.Group.Id, out bounds)) return;
            var drag = new GroupDragState
            {
                Visual = visual,
                StartPointer = pointer,
                Before = _workspace.Capture(),
                BeforeDirty = _workspace.Dirty,
                StartGroupBounds = bounds,
            };
            var keys = new HashSet<string>(
                visual.Group.NodeKeys ?? new List<string>(), StringComparer.Ordinal);
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                Rect rect;
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key) && keys.Contains(key)
                    && _layout.TryGetRect(node, out rect))
                    drag.StartRects[node] = rect;
            }
            _selectedGroupId = visual.Group.Id;
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            _selectedNode = null;
            SyncPrimaryEditSelection();
            RefreshGroupVisualStates();
            RefreshInspector();
            _groupDrag = drag;
            _panning = false;
            _panButton = null;
            HideTooltip();
        }

        private void UpdateGroupDrag(PointerEventData data)
        {
            if (_groupDrag == null || data == null || _layout == null) return;
            Vector2 pointer;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _content, data.position, null, out pointer)) return;
            Vector2 delta = new Vector2(
                pointer.x - _groupDrag.StartPointer.x,
                _groupDrag.StartPointer.y - pointer.y);
            Keyboard keyboard = Keyboard.current;
            bool free = keyboard != null
                && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
            if (!free)
            {
                const float grid = 10f;
                delta.x = Mathf.Round(delta.x / grid) * grid;
                delta.y = Mathf.Round(delta.y / grid) * grid;
            }
            float minGroupPosition = _groupDrag.Visual.Group.IsNote
                ? StoryGraphMetrics.Margin
                : 8f;
            float minDeltaX = minGroupPosition - _groupDrag.StartGroupBounds.x;
            float minDeltaY = minGroupPosition - _groupDrag.StartGroupBounds.y;
            if (_groupDrag.StartRects.Count > 0)
            {
                minDeltaX = Mathf.Max(minDeltaX,
                    StoryGraphMetrics.Margin
                    - _groupDrag.StartRects.Values.Min(rect => rect.x));
                minDeltaY = Mathf.Max(minDeltaY,
                    StoryGraphMetrics.Margin
                    - _groupDrag.StartRects.Values.Min(rect => rect.y));
            }
            delta.x = Mathf.Max(delta.x, minDeltaX);
            delta.y = Mathf.Max(delta.y, minDeltaY);
            if (delta.sqrMagnitude < 0.01f) return;
            _groupDrag.Moved = true;
            foreach (KeyValuePair<StoryGraphDisplayNode, Rect> pair
                in _groupDrag.StartRects)
            {
                Rect rect = pair.Value;
                rect.position += delta;
                _layout.NodeRects[pair.Key] = rect;
            }
            ApplyNodeRectVisuals(_groupDrag.StartRects.Keys);
            Rect groupRect = _groupDrag.StartGroupBounds;
            groupRect.position += delta;
            _groupBounds[_groupDrag.Visual.Group.Id] = groupRect;
            Place(_groupDrag.Visual.Rect, 0f, 1f, 0f, 1f,
                groupRect.x, -groupRect.y, groupRect.width, groupRect.height);
            if (_groupDrag.StartRects.Count > 0)
            {
                StoryGraphLayoutEngine.RerouteCurrent(_layout, _viewModel.Edges);
                RebuildEdgesOnly(false);
            }
        }

        private void EndGroupDrag(PointerEventData data)
        {
            GroupDragState drag = _groupDrag;
            _groupDrag = null;
            if (drag == null || !drag.Moved || _workspace == null) return;
            foreach (StoryGraphDisplayNode node in drag.StartRects.Keys)
            {
                Rect rect;
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key) && _layout.TryGetRect(node, out rect))
                    _workspace.SetPosition(key, rect.position);
            }
            if (drag.Visual.Group.IsNote)
            {
                Rect bounds;
                if (_groupBounds.TryGetValue(drag.Visual.Group.Id, out bounds))
                    _workspace.SetGroupBounds(drag.Visual.Group.Id, bounds);
            }
            string error;
            if (!_workspace.Save(out error))
            {
                _workspace.Restore(drag.Before, drag.BeforeDirty);
                foreach (KeyValuePair<StoryGraphDisplayNode, Rect> pair in drag.StartRects)
                    _layout.NodeRects[pair.Key] = pair.Value;
                SetEditFeedback(error + "；分组移动已回滚。", true);
                RefreshGraph(false);
                return;
            }
            _workspaceUndo.Push(drag.Before);
            _workspaceRedo.Clear();
            _editActionTimeline.Push(true);
            _editRedoTimeline.Clear();
            if (_editSession != null) _editSession.ClearRedoHistory();
            _editStatus = drag.Visual.Group.IsNote
                ? "已移动注释框；位置已保存到作者工作区。"
                : "已整体移动分组成员；节点坐标已保存到作者工作区。";
            RefreshGraph(false);
            UpdateEditControls();
            UpdateStatusBar();
        }

        private void CancelGroupDrag()
        {
            GroupDragState drag = _groupDrag;
            _groupDrag = null;
            if (drag == null) return;
            foreach (KeyValuePair<StoryGraphDisplayNode, Rect> pair in drag.StartRects)
                _layout.NodeRects[pair.Key] = pair.Value;
            RefreshGraph(false);
            SetEditFeedback("已取消分组移动。", false);
        }

        private void AlignSelection(NodeAlignment alignment)
        {
            List<StoryGraphDisplayNode> nodes = EditableLayoutNodes(false);
            if (nodes.Count < 2)
            {
                SetEditFeedback("至少选择两个节点才能对齐。", false);
                return;
            }
            ApplyWorkspaceRectMutation(nodes, "对齐 " + nodes.Count + " 个节点",
                delegate(Dictionary<StoryGraphDisplayNode, Rect> rects)
                {
                    float target;
                    if (alignment == NodeAlignment.Left)
                        target = nodes.Min(node =>
                            StoryGraphMetrics.NodeCardRect(
                                node, rects[node]).xMin);
                    else if (alignment == NodeAlignment.Right)
                        target = nodes.Max(node =>
                            StoryGraphMetrics.NodeCardRect(
                                node, rects[node]).xMax);
                    else if (alignment == NodeAlignment.Top)
                        target = nodes.Min(node =>
                            StoryGraphMetrics.NodeCardRect(
                                node, rects[node]).yMin);
                    else
                        target = nodes.Max(node =>
                            StoryGraphMetrics.NodeCardRect(
                                node, rects[node]).yMax);
                    foreach (StoryGraphDisplayNode node in nodes)
                    {
                        Rect rect = rects[node];
                        Rect card = StoryGraphMetrics.NodeCardRect(node, rect);
                        if (alignment == NodeAlignment.Left)
                            rect.x += target - card.xMin;
                        else if (alignment == NodeAlignment.Right)
                            rect.x += target - card.xMax;
                        else if (alignment == NodeAlignment.Top)
                            rect.y += target - card.yMin;
                        else
                            rect.y += target - card.yMax;
                        rects[node] = rect;
                    }
                });
        }

        private void DistributeSelection(bool horizontal)
        {
            List<StoryGraphDisplayNode> nodes = EditableLayoutNodes(false);
            if (nodes.Count < 3)
            {
                SetEditFeedback("至少选择三个节点才能等距分布。", false);
                return;
            }
            ApplyWorkspaceRectMutation(nodes,
                horizontal ? "水平等距分布节点" : "垂直等距分布节点",
                delegate(Dictionary<StoryGraphDisplayNode, Rect> rects)
                {
                    List<StoryGraphDisplayNode> ordered = horizontal
                        ? nodes.OrderBy(node => StoryGraphMetrics.NodeCardRect(
                            node, rects[node]).center.x).ToList()
                        : nodes.OrderBy(node => StoryGraphMetrics.NodeCardRect(
                            node, rects[node]).center.y).ToList();
                    float first = horizontal
                        ? StoryGraphMetrics.NodeCardRect(
                            ordered[0], rects[ordered[0]]).center.x
                        : StoryGraphMetrics.NodeCardRect(
                            ordered[0], rects[ordered[0]]).center.y;
                    float last = horizontal
                        ? StoryGraphMetrics.NodeCardRect(
                            ordered[ordered.Count - 1],
                            rects[ordered[ordered.Count - 1]]).center.x
                        : StoryGraphMetrics.NodeCardRect(
                            ordered[ordered.Count - 1],
                            rects[ordered[ordered.Count - 1]]).center.y;
                    float step = (last - first) / (ordered.Count - 1);
                    for (int i = 1; i < ordered.Count - 1; i++)
                    {
                        StoryGraphDisplayNode node = ordered[i];
                        Rect rect = rects[node];
                        Rect card = StoryGraphMetrics.NodeCardRect(node, rect);
                        if (horizontal)
                            rect.x += first + step * i - card.center.x;
                        else
                            rect.y += first + step * i - card.center.y;
                        rects[node] = rect;
                    }
                });
        }

        private void AutoArrangeSelection(bool all)
        {
            EndInspectorLiveEdit();
            List<StoryGraphDisplayNode> nodes = EditableLayoutNodes(all);
            if (!all) nodes = ExpandOptionLayoutSelection(nodes);
            if (nodes.Count == 0 || _workspace == null)
            {
                SetEditFeedback("没有可自动整理的真实节点。", false);
                return;
            }

            // “自动”布局不能把编辑态（所有链段强制展开）的 15 个真实坐标固化到
            // 工作区；否则回到查看态后链段虽然折成 1 个块，下游节点仍保留第 15 列
            // 的固定 x，视觉上就留下 15 列空白。这里改为删除目标节点的手动坐标：
            // 当前编辑态由完整展开图即时排布，返回查看态后又会按折叠显示图即时排布，
            // 两种状态各自使用正确占位，同时确定性算法保证重开后布局不漂移。
            StoryGraphWorkspace.Snapshot before = _workspace.Capture();
            bool beforeDirty = _workspace.Dirty;
            int removed = 0;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (StoryGraphDisplayNode node in nodes)
            {
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key) && keys.Add(key)
                    && _workspace.RemovePosition(key))
                    removed++;
            }
            if (removed == 0)
            {
                _workspace.Restore(before, beforeDirty);
                SetEditFeedback("这些节点已经使用折叠感知的自动布局，无需调整。", false);
                return;
            }

            string error;
            if (!_workspace.Save(out error))
            {
                _workspace.Restore(before, beforeDirty);
                SetEditFeedback(error + "；自动整理已回滚。", true);
                return;
            }
            _workspaceUndo.Push(before);
            _workspaceRedo.Clear();
            _editActionTimeline.Push(true);
            _editRedoTimeline.Clear();
            if (_editSession != null) _editSession.ClearRedoHistory();
            _editStatus = (all ? "已自动整理全部节点" : "已自动整理所选节点")
                        + "；布局会随链段展开/折叠自动压缩，不再保留展开态空隙。";
            RefreshGraph(false);
            UpdateEditControls();
            UpdateStatusBar();
        }

        private List<StoryGraphDisplayNode> EditableLayoutNodes(bool all)
        {
            if (_viewModel == null || _layout == null)
                return new List<StoryGraphDisplayNode>();
            IEnumerable<StoryGraphDisplayNode> source = all
                ? _viewModel.Nodes
                : _selectedNodes;
            return source.Where(IsEditableLayoutNode)
                .Distinct().ToList();
        }

        private bool IsEditableLayoutNode(StoryGraphDisplayNode node)
        {
            return node != null && node.SourceNode != null
                && (node.SourceNode.Talk != null || node.SourceNode.Option != null)
                && _layout != null && _layout.NodeRects.ContainsKey(node);
        }

        private List<StoryGraphDisplayNode> ExpandOptionLayoutSelection(
            IEnumerable<StoryGraphDisplayNode> selected)
        {
            var result = new HashSet<StoryGraphDisplayNode>(
                (selected ?? Enumerable.Empty<StoryGraphDisplayNode>())
                    .Where(IsEditableLayoutNode));
            if (_viewModel == null || result.Count == 0) return result.ToList();

            // “整理所选 Talk”应清掉它的直接 Option 子图坐标；否则只释放父 Talk，
            // Option 与结果 Talk 仍被旧坐标钉住，自动分层看起来就像畸变。
            List<StoryGraphDisplayNode> selectedTalks = result
                .Where(node => node.SourceNode != null
                    && node.SourceNode.Talk != null).ToList();
            var optionNodes = new HashSet<StoryGraphDisplayNode>();
            foreach (StoryGraphDisplayNode talk in selectedTalks)
            {
                foreach (StoryGraphDisplayEdge edge in talk.Outgoing)
                {
                    if (edge != null
                        && edge.Kind == EvtStoryGraphEdgeKind.TalkOption
                        && IsEditableLayoutNode(edge.To))
                    {
                        optionNodes.Add(edge.To);
                        result.Add(edge.To);
                    }
                }
            }
            foreach (StoryGraphDisplayNode option in optionNodes)
            {
                foreach (StoryGraphDisplayEdge edge in option.Outgoing)
                {
                    if (edge != null
                        && (edge.Kind == EvtStoryGraphEdgeKind.OptionTalk
                            || edge.Kind == EvtStoryGraphEdgeKind.OptionTalk2)
                        && IsEditableLayoutNode(edge.To))
                        result.Add(edge.To);
                }
            }
            return result.ToList();
        }

        private void ApplyWorkspaceRectMutation(
            List<StoryGraphDisplayNode> nodes,
            string description,
            Action<Dictionary<StoryGraphDisplayNode, Rect>> mutation)
        {
            EndInspectorLiveEdit();
            if (_workspace == null || _layout == null || _viewModel == null
                || nodes == null || nodes.Count == 0 || mutation == null) return;
            StoryGraphWorkspace.Snapshot before = _workspace.Capture();
            bool beforeDirty = _workspace.Dirty;
            var oldRects = new Dictionary<StoryGraphDisplayNode, Rect>();
            foreach (StoryGraphDisplayNode node in nodes)
                oldRects[node] = _layout.NodeRects[node];
            try
            {
                mutation(_layout.NodeRects);
                bool changed = false;
                foreach (StoryGraphDisplayNode node in nodes)
                {
                    Rect oldRect = oldRects[node];
                    Rect rect = _layout.NodeRects[node];
                    if ((oldRect.position - rect.position).sqrMagnitude > 0.01f)
                        changed = true;
                    string key = StoryGraphWorkspace.StableNodeKey(node);
                    if (!string.IsNullOrEmpty(key))
                        _workspace.SetPosition(key, rect.position);
                }
                if (!changed)
                {
                    _workspace.Restore(before, beforeDirty);
                    foreach (KeyValuePair<StoryGraphDisplayNode, Rect> pair in oldRects)
                        _layout.NodeRects[pair.Key] = pair.Value;
                    ApplyNodeRectVisuals(nodes);
                    SetEditFeedback("节点已经处于目标布局，无需调整。", false);
                    return;
                }

                string error;
                if (!_workspace.Save(out error))
                {
                    _workspace.Restore(before, beforeDirty);
                    foreach (KeyValuePair<StoryGraphDisplayNode, Rect> pair in oldRects)
                        _layout.NodeRects[pair.Key] = pair.Value;
                    ApplyNodeRectVisuals(nodes);
                    StoryGraphLayoutEngine.RerouteCurrent(_layout, _viewModel.Edges);
                    RebuildEdgesOnly(true);
                    SetEditFeedback(error + "；布局操作已回滚。", true);
                    return;
                }

                _workspaceUndo.Push(before);
                _workspaceRedo.Clear();
                _editActionTimeline.Push(true);
                _editRedoTimeline.Clear();
                if (_editSession != null) _editSession.ClearRedoHistory();
                ApplyNodeRectVisuals(nodes);
                StoryGraphLayoutEngine.RerouteCurrent(_layout, _viewModel.Edges);
                RebuildEdgesOnly(true);
                if (_content != null)
                    _content.anchoredPosition = ClampPan(_content.anchoredPosition);
                _editStatus = description + "；坐标已保存到作者工作区。";
                UpdateEditControls();
                UpdateStatusBar();
            }
            catch (Exception e)
            {
                _workspace.Restore(before, beforeDirty);
                foreach (KeyValuePair<StoryGraphDisplayNode, Rect> pair in oldRects)
                    _layout.NodeRects[pair.Key] = pair.Value;
                ApplyNodeRectVisuals(nodes);
                StoryGraphLayoutEngine.RerouteCurrent(_layout, _viewModel.Edges);
                RebuildEdgesOnly(true);
                Plugin.Log?.LogError("[StoryGraph.Workspace.Mutation] " + e);
                SetEditFeedback("布局操作失败并已回滚：" + e.Message, true);
            }
        }

        private void ApplyNodeRectVisuals(IEnumerable<StoryGraphDisplayNode> nodes)
        {
            if (nodes == null || _layout == null) return;
            foreach (StoryGraphDisplayNode node in nodes)
            {
                Rect rect;
                NodeVisual visual;
                if (node == null || !_layout.TryGetRect(node, out rect)
                    || !_nodeLookup.TryGetValue(node, out visual)) continue;
                Place(visual.Rect, 0f, 1f, 0f, 1f,
                    rect.x, -rect.y, rect.width, rect.height);
            }
        }

        private void UpdateGroupVisualBounds()
        {
            if (_layout == null) return;
            _groupBounds.Clear();
            float minX = _layout.Bounds.xMin;
            float minY = _layout.Bounds.yMin;
            float maxX = _layout.Bounds.xMax;
            float maxY = _layout.Bounds.yMax;
            foreach (GroupVisual visual in _activeGroups)
            {
                Rect bounds;
                if (visual == null || !TryGetGroupBounds(visual.Group, out bounds))
                    continue;
                _groupBounds[visual.Group.Id] = bounds;
                Place(visual.Rect, 0f, 1f, 0f, 1f,
                    bounds.x, -bounds.y, bounds.width, bounds.height);
                minX = Mathf.Min(minX, bounds.xMin - StoryGraphMetrics.Margin * 0.25f);
                minY = Mathf.Min(minY, bounds.yMin - StoryGraphMetrics.Margin * 0.25f);
                maxX = Mathf.Max(maxX, bounds.xMax + StoryGraphMetrics.Margin * 0.25f);
                maxY = Mathf.Max(maxY, bounds.yMax + StoryGraphMetrics.Margin * 0.25f);
            }
            _layout.Bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private void RebuildEdgesOnly(bool updateMinimap = true)
        {
            UpdateGroupVisualBounds();
            for (int i = 0; i < _activeEdges.Count; i++) ReleaseEdge(_activeEdges[i]);
            _activeEdges.Clear();
            _edgeLookup.Clear();
            if (_layout != null && _layout.Edges != null)
            {
                for (int i = 0; i < _layout.Edges.Count; i++)
                    BuildEdgeVisual(_layout.Edges[i]);
                Rect bounds = _layout.Bounds;
                _content.sizeDelta = new Vector2(
                    Mathf.Max(100f, bounds.xMax + StoryGraphMetrics.Margin),
                    Mathf.Max(100f, bounds.yMax + StoryGraphMetrics.Margin));
            }
            UpdateEdgeLabelsVisibility();
            if (updateMinimap)
            {
                UpdateMinimapDots();
                UpdateMinimapFrame();
            }
        }

        // ==================== 连线构建与池 ====================

        private Image AcquireSegment(RectTransform parent)
        {
            Image img = _segmentPool.Count > 0 ? _segmentPool.Pop() : CreatePlainImage(parent, "Seg");
            img.transform.SetParent(parent, false);
            img.gameObject.SetActive(true);
            return img;
        }

        private Image AcquireArrow(RectTransform parent)
        {
            Image img = _arrowPool.Count > 0 ? _arrowPool.Pop() : CreatePlainImage(parent, "Arrow");
            img.transform.SetParent(parent, false);
            img.gameObject.SetActive(true);
            return img;
        }

        private Image CreatePlainImage(RectTransform parent, string name)
        {
            GameObject go = CreateUIObject(name, parent);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            return img;
        }

        private GameObject AcquireLabel()
        {
            GameObject go = _labelPool.Count > 0 ? _labelPool.Pop() : CreateLabelObject();
            go.SetActive(true);
            return go;
        }

        private GameObject CreateLabelObject()
        {
            GameObject go = CreateUIObject("EdgeLabel", _edgeContainer);
            var img = go.AddComponent<Image>();
            img.color = PanelBg;
            img.raycastTarget = false;
            ApplySprite(img, _chipSprite);
            Text txt = CreateText(go, "Text", 11, FontStyle.Normal,
                SubtitleColor, TextAnchor.MiddleCenter);
            Stretch(txt.rectTransform, 4f, 0f, 4f, 0f);
            return go;
        }

        private void ReleaseEdge(EdgeVisual ev)
        {
            if (ev == null) return;
            for (int i = 0; i < ev.Segments.Count; i++)
            {
                ev.Segments[i].gameObject.SetActive(false);
                _segmentPool.Push(ev.Segments[i]);
            }
            ev.Segments.Clear();
            ev.Lengths.Clear();
            if (ev.Arrow != null)
            {
                ev.Arrow.gameObject.SetActive(false);
                _arrowPool.Push(ev.Arrow);
                ev.Arrow = null;
            }
            if (ev.LabelRoot != null)
            {
                ev.LabelRoot.SetActive(false);
                _labelPool.Push(ev.LabelRoot);
                ev.LabelRoot = null;
                ev.Label = null;
            }
            ev.Edge = null;
        }

        /// <summary>按 RoutedEdge.Points 逐段画拉伸 Image，末端放旋转 45° 的方块箭头。</summary>
        private void BuildEdgeVisual(StoryGraphRoutedEdge routed)
        {
            if (routed == null || routed.Edge == null) return;
            List<Vector2> points = routed.Points;
            if (points == null || points.Count < 2) return;

            StoryGraphDisplayEdge edge = routed.Edge;
            var ev = new EdgeVisual
            {
                Edge = edge,
                BaseColor = EdgeBaseColor(edge),
                BaseWidth = edge.IsRuntimeEdge ? 3f : 1f, // 运行时边加粗，被遮蔽边 1px
            };

            float lastAngle = 0f;
            bool hasSegment = false;
            Vector2 lastEnd = Vector2.zero;
            for (int i = 1; i < points.Count; i++)
            {
                Vector2 a = new Vector2(points[i - 1].x, -points[i - 1].y);
                Vector2 b = new Vector2(points[i].x, -points[i].y);
                Vector2 d = b - a;
                float length = d.magnitude;
                if (length < 0.5f) continue; // 跳过零长段

                Image seg = AcquireSegment(_edgeContainer);
                seg.color = ev.BaseColor;
                RectTransform rt = seg.rectTransform;
                Place(rt, 0f, 1f, 0.5f, 0.5f,
                    (a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, length, ev.BaseWidth);
                rt.localRotation = Quaternion.Euler(
                    0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);

                ev.Segments.Add(seg);
                ev.Lengths.Add(length);
                lastAngle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
                lastEnd = b;
                hasSegment = true;
            }
            if (!hasSegment)
            {
                ReleaseEdge(ev);
                return;
            }

            // 箭头：10x10 方块旋转 45° 成菱形，角对准末段方向。
            Image arrow = AcquireArrow(_edgeContainer);
            arrow.color = ev.BaseColor;
            Place(arrow.rectTransform, 0f, 1f, 0.5f, 0.5f,
                lastEnd.x, lastEnd.y, 10f, 10f);
            arrow.rectTransform.localRotation =
                Quaternion.Euler(0f, 0f, lastAngle + 45f);
            ev.Arrow = arrow;

            if (!string.IsNullOrEmpty(routed.Edge.Label))
            {
                GameObject labelGo = AcquireLabel();
                var labelRt = (RectTransform)labelGo.transform;
                Text txt = labelGo.GetComponentInChildren<Text>(true);
                txt.text = Truncate(routed.Edge.Label, 34);
                float width = Mathf.Clamp(txt.preferredWidth + 12f, 40f, 260f);
                Place(labelRt, 0f, 1f, 0.5f, 0.5f,
                    routed.LabelPos.x, -routed.LabelPos.y, width, 18f);
                ev.LabelRoot = labelGo;
                ev.Label = txt;
            }

            _activeEdges.Add(ev);
            _edgeLookup[edge] = ev;
        }

        // ==================== 视图变换 ====================

        private void SetZoom(float zoom)
        {
            _zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            if (_content != null)
                _content.localScale = new Vector3(_zoom, _zoom, 1f);
            UpdateZoomText();
            UpdateEdgeLabelsVisibility();
        }

        /// <summary>工具栏 +/-：以视口中心为锚点缩放。</summary>
        private void ZoomStep(float factor)
        {
            ZoomTo(_zoom * factor);
        }

        /// <summary>以视口中心为锚点缩放到目标值：v=0 处不动 ⇒ C' = C·(z'/z)。</summary>
        private void ZoomTo(float target)
        {
            if (_content == null) return;
            float oldZoom = _zoom;
            float newZoom = Mathf.Clamp(target, MinZoom, MaxZoom);
            if (Mathf.Approximately(newZoom, oldZoom)) return;
            Vector2 c = _content.anchoredPosition * (newZoom / oldZoom);
            SetZoom(newZoom);
            _content.anchoredPosition = ClampPan(c);
            UpdateMinimapFrame();
        }

        /// <summary>适应全图：缩到能看全 Bounds，放大不超过 1.25。</summary>
        private void FitToView()
        {
            if (_layout == null || _viewport == null || _content == null) return;
            Rect bounds = _layout.Bounds;
            Rect view = _viewport.rect;
            if (bounds.width <= 0f || bounds.height <= 0f
                || view.width <= 0f || view.height <= 0f) return;
            float zoom = Mathf.Min(
                (view.width - 40f) / bounds.width,
                (view.height - 40f) / bounds.height);
            SetZoom(Mathf.Clamp(zoom, MinZoom, FitMaxZoom));
            CenterOnLayoutPoint(bounds.center);
        }

        private void CenterOnNode(StoryGraphDisplayNode node)
        {
            if (node == null || _layout == null) return;
            Rect rect;
            if (!_layout.TryGetRect(node, out rect)) return;
            CenterOnLayoutPoint(
                StoryGraphMetrics.NodeCardRect(node, rect).center);
        }

        /// <summary>让布局点 P 落在视口中心：C = (-P.x·z, P.y·z)（符号来自 y 向下布局）。</summary>
        private void CenterOnLayoutPoint(Vector2 layoutPoint)
        {
            if (_content == null) return;
            _content.anchoredPosition = ClampPan(new Vector2(
                -layoutPoint.x * _zoom, layoutPoint.y * _zoom));
            UpdateMinimapFrame();
        }

        /// <summary>
        /// 平移钳制：小图只在即将越出安全边距时纠正，不在每次刷新时强制居中；
        /// 这样删除最外侧节点/注释框导致 Bounds 缩小时，幸存节点不会整图跳位。
        /// 大图仍保证至少 PanSlack 像素留在视口内，防止图被拖丢。
        /// </summary>
        private Vector2 ClampPan(Vector2 c)
        {
            if (_layout == null || _viewport == null) return c;
            Rect b = _layout.Bounds;
            Rect v = _viewport.rect;
            float z = _zoom;

            if (b.width * z <= v.width - PanSlack * 2f)
            {
                c.x = ClampSmallGraphAxis(
                    c.x, v.xMin, v.xMax, b.xMin, b.xMax,
                    z, PanSlack, false);
            }
            else
            {
                float left = c.x + b.xMin * z;
                float right = c.x + b.xMax * z;
                if (left > v.xMax - PanSlack) c.x = v.xMax - PanSlack - b.xMin * z;
                else if (right < v.xMin + PanSlack) c.x = v.xMin + PanSlack - b.xMax * z;
            }

            if (b.height * z <= v.height - PanSlack * 2f)
            {
                c.y = ClampSmallGraphAxis(
                    c.y, v.yMin, v.yMax, b.yMin, b.yMax,
                    z, PanSlack, true);
            }
            else
            {
                // content 的 y = C.y - yDown·z：布局上缘(y 小)对应屏幕上缘(y 大)
                float top = c.y - b.yMin * z;
                float bottom = c.y - b.yMax * z;
                if (top < v.yMin + PanSlack) c.y = v.yMin + PanSlack + b.yMin * z;
                else if (bottom > v.yMax - PanSlack) c.y = v.yMax - PanSlack + b.yMax * z;
            }
            return c;
        }

        /// <summary>
        /// 小图单轴钳制的纯计算版本，供无 Unity 窗口的回归测试复用。
        /// vertical=false 时画布坐标与屏幕 x 同向；vertical=true 时布局 y 向下，
        /// content 屏幕 y 与之反向。只要当前平移仍能让整张图留在安全边距内，
        /// 就原样返回，避免 Bounds 缩小时产生无意义的重新居中。
        /// </summary>
        internal static float ClampSmallGraphAxis(
            float current,
            float viewportMin,
            float viewportMax,
            float boundsMin,
            float boundsMax,
            float zoom,
            float slack,
            bool vertical)
        {
            float min = vertical
                ? viewportMin + slack + boundsMax * zoom
                : viewportMin + slack - boundsMin * zoom;
            float max = vertical
                ? viewportMax - slack + boundsMin * zoom
                : viewportMax - slack - boundsMax * zoom;
            if (min <= max) return Mathf.Clamp(current, min, max);
            float center = (boundsMin + boundsMax) * 0.5f * zoom;
            return vertical ? center : -center;
        }

        private void UpdateZoomText()
        {
            if (_zoomText != null)
                _zoomText.text = Mathf.RoundToInt(_zoom * 100f) + "%";
        }

        private void CycleEdgeLabelMode()
        {
            _edgeLabelMode = _edgeLabelMode == EdgeLabelMode.Important
                ? EdgeLabelMode.All
                : (_edgeLabelMode == EdgeLabelMode.All
                    ? EdgeLabelMode.Hidden
                    : EdgeLabelMode.Important);
            UpdateEdgeLabelsVisibility();
            string label = _edgeLabelMode == EdgeLabelMode.Important
                ? "连线：关键"
                : (_edgeLabelMode == EdgeLabelMode.All ? "连线：全部" : "连线：隐藏");
            SetButtonLabel(_edgeLabelModeButton, label);
            if (_statusText != null)
                _statusText.text = "连线文字已切换为“"
                    + (_edgeLabelMode == EdgeLabelMode.Important ? "仅关键分支"
                        : _edgeLabelMode == EdgeLabelMode.All ? "全部" : "隐藏")
                    + "”；悬停节点仍会突出相关连线。";
        }

        private void UpdateEdgeLabelsVisibility()
        {
            bool zoomAllows = _zoom >= EdgeLabelMinZoom;
            for (int i = 0; i < _activeEdges.Count; i++)
            {
                EdgeVisual visual = _activeEdges[i];
                if (visual.LabelRoot == null) continue;
                bool show = zoomAllows && ShouldShowEdgeLabel(visual.Edge);
                visual.LabelRoot.SetActive(show);
            }
        }

        private bool ShouldShowEdgeLabel(StoryGraphDisplayEdge edge)
        {
            if (edge == null || _edgeLabelMode == EdgeLabelMode.Hidden) return false;
            if (_editMode && _edgeLabelMode != EdgeLabelMode.All) return false;
            if (_edgeLabelMode == EdgeLabelMode.All) return true;
            // 默认只保留真正影响作者判断的分支语义。普通 nextTalk、事件入口和
            // “对话→选项”已有节点/端口表达，不再用文字压在线和卡片之间。
            if (_hoverNode != null && (ReferenceEquals(edge.From, _hoverNode)
                || ReferenceEquals(edge.To, _hoverNode))) return true;
            if (edge.Flags != EvtStoryGraphEdgeFlags.None) return true;
            if (edge.Kind == EvtStoryGraphEdgeKind.NextTalk2
                || edge.Kind == EvtStoryGraphEdgeKind.OptionTalk2
                || edge.Kind == EvtStoryGraphEdgeKind.NextEvent) return true;
            string label = edge.Label ?? string.Empty;
            return label.IndexOf("条件", StringComparison.Ordinal) >= 0
                   || label.IndexOf("小游戏", StringComparison.Ordinal) >= 0
                   || label.IndexOf("[男]", StringComparison.Ordinal) >= 0
                   || label.IndexOf("[女]", StringComparison.Ordinal) >= 0
                   || label.IndexOf("回退", StringComparison.Ordinal) >= 0
                   || label.IndexOf("不会执行", StringComparison.Ordinal) >= 0
                   || label.IndexOf("额外项", StringComparison.Ordinal) >= 0;
        }

        /// <summary>打开后首次居中：等视口尺寸就绪；找不到当前节点则适应全图。</summary>
        private void TryInitialCenter()
        {
            if (_viewport == null || _viewport.rect.width < 50f) return; // 下帧再试
            _pendingInitialCenter = false;
            if (_currentNode != null && _layout != null)
            {
                Rect rect;
                if (_layout.TryGetRect(_currentNode, out rect))
                {
                    // 缩得太小看不清单个节点，定位前先提到可读缩放
                    if (_zoom < 0.7f) ZoomTo(0.85f);
                    CenterOnNode(_currentNode);
                    RefreshAllNodeStates(); // 显示青色定位环
                    return;
                }
            }
            FitToView();
        }

        // ==================== 悬停高亮与 tooltip ====================

        private void SetHover(StoryGraphDisplayNode node)
        {
            if (node == null || ReferenceEquals(_hoverNode, node)) return;
            ClearHover();
            _hoverNode = node;
            for (int i = 0; i < node.Incoming.Count; i++)
            {
                HighlightEdge(node.Incoming[i]);
                if (node.Incoming[i].From != null
                    && !ReferenceEquals(node.Incoming[i].From, node))
                    _hoverAdjacent.Add(node.Incoming[i].From);
            }
            for (int i = 0; i < node.Outgoing.Count; i++)
            {
                HighlightEdge(node.Outgoing[i]);
                if (node.Outgoing[i].To != null
                    && !ReferenceEquals(node.Outgoing[i].To, node))
                    _hoverAdjacent.Add(node.Outgoing[i].To);
            }
            RefreshAllNodeStates();
            // 编辑模式右侧要留给端口、拖线和后续属性检查器；悬浮框会直接盖住
            // 端口与相邻节点，因此编辑时只保留链路高亮，诊断写入状态栏/检查器。
            if (!_editMode) ShowTooltip(node);
            UpdateEdgeLabelsVisibility();
        }

        private void ClearHover()
        {
            if (_hoverNode == null && _hoverAdjacent.Count == 0)
            {
                HideTooltip();
                return;
            }
            for (int i = 0; i < _activeEdges.Count; i++)
                SetEdgeHighlighted(_activeEdges[i], false);
            _hoverNode = null;
            _hoverAdjacent.Clear();
            RefreshAllNodeStates();
            HideTooltip();
            UpdateEdgeLabelsVisibility();
        }

        private void HighlightEdge(StoryGraphDisplayEdge edge)
        {
            EdgeVisual ev;
            if (edge != null && _edgeLookup.TryGetValue(edge, out ev))
                SetEdgeHighlighted(ev, true);
        }

        /// <summary>相连边加粗变色 / 恢复基础样式。</summary>
        private void SetEdgeHighlighted(EdgeVisual ev, bool on)
        {
            if (ev == null) return;
            Color color = on ? HighlightColor(ev.BaseColor) : ev.BaseColor;
            float width = on ? ev.BaseWidth * 1.8f : ev.BaseWidth;
            for (int i = 0; i < ev.Segments.Count; i++)
            {
                ev.Segments[i].color = color;
                RectTransform rt = ev.Segments[i].rectTransform;
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, width);
            }
            if (ev.Arrow != null)
            {
                ev.Arrow.color = color;
                float size = on ? 14f : 10f;
                ev.Arrow.rectTransform.sizeDelta = new Vector2(size, size);
            }
        }

        private static Color HighlightColor(Color baseColor)
        {
            // 高亮统一品牌橙，不再向白提亮
            Color c = AccentOrange;
            c.a = 0.98f;
            return c;
        }

        /// <summary>
        /// 悬浮框只在提供「节点上看不到的信息」时才显示：
        /// 文字被截断 / 有需要解释的标志 / 链段与未使用组的成员 ID 预览。
        /// </summary>
        private bool ShouldShowTooltip(StoryGraphDisplayNode node)
        {
            if (node == null) return false;
            // 链段/未使用组：悬浮框附成员编号预览，必有增量信息。
            if (node.IsSegment || node.IsUnusedGroup) return true;
            // 入口与结束只是常规流程标记；只有异常、跨组等状态才需要解释。
            if ((node.Flags & TooltipAttentionFlags) != 0) return true;
            // 作者提示（如 CG 自然结束的可选 4017 建议）不设旗标也要能看到。
            if (node.SourceNode != null
                && node.SourceNode.AuthorWarnings.Count > 0) return true;

            NodeVisual nv;
            if (_nodeLookup.TryGetValue(node, out nv) && nv != null
                && (IsTextClipped(nv.Title) || IsTextClipped(nv.Subtitle)))
                return true;

            // 模型为了卡片可读性会把正文摘要到 48 字；即使摘要本身刚好放得下，
            // 原文仍有隐藏内容，此时悬浮框应该给出更完整的文本。
            return HasHiddenFullText(node);
        }

        /// <summary>
        /// 文本在当前排版区域内是否被纵向截断。Text.preferredHeight 会正确处理
        /// Canvas 像素倍率和中文逐字换行；旧实现把整段无空格中文当成“超长单词”，
        /// 导致明明已经完整换行仍被误判为截断。
        /// </summary>
        private static bool IsTextClipped(Text text)
        {
            if (text == null || string.IsNullOrEmpty(text.text)) return false;
            Rect rect = text.rectTransform.rect;
            if (rect.width < 1f || rect.height < 1f) return false;
            try
            {
                return text.preferredHeight > rect.height + 1.5f;
            }
            catch
            {
                // 悬浮提示属于辅助信息，排版探测失败时宁可不弹，也不能影响节点交互。
                return false;
            }
        }

        private static bool HasHiddenFullText(StoryGraphDisplayNode node)
        {
            if (node == null || node.SourceNode == null) return false;
            string full = BuildFullSubtitle(node.SourceNode);
            return !string.Equals(full, node.Subtitle ?? string.Empty,
                StringComparison.Ordinal);
        }

        /// <summary>从原配置恢复适合悬浮框阅读的较完整正文（超长内容做安全限长）。</summary>
        private static string BuildFullSubtitle(EvtStoryGraphNode source)
        {
            if (source == null) return string.Empty;
            if (source.Talk != null)
            {
                string role = ExtractSubtitlePrefix(source.Subtitle);
                string content = NormalizeDetailText(source.Talk.content, "（空台词）");
                string result = string.IsNullOrEmpty(role) ? content : role + "｜" + content;
                string badges = StoryGraphPerformanceCodec.BuildTalkBadges(source.Talk);
                if (!string.IsNullOrEmpty(badges)) result += "\n" + badges;
                return result + ExistingPreviewSuffix(source.Subtitle);
            }
            if (source.Option != null)
            {
                string result = NormalizeDetailText(source.Option.content, "（空选项）");
                string badges = StoryGraphPerformanceCodec.BuildOptionBadges(source.Option);
                if (!string.IsNullOrEmpty(badges)) result += "\n" + badges;
                return result + ExistingPreviewSuffix(source.Subtitle);
            }
            return source.Subtitle ?? string.Empty;
        }

        private static string ExtractSubtitlePrefix(string subtitle)
        {
            if (string.IsNullOrEmpty(subtitle)) return string.Empty;
            int separator = subtitle.IndexOf('｜');
            return separator >= 0 ? subtitle.Substring(0, separator) : string.Empty;
        }

        private static string NormalizeDetailText(string value, string emptyText)
        {
            if (string.IsNullOrWhiteSpace(value)) return emptyText;
            const int maxLength = 320;
            // 只为悬浮框保留 maxLength+1 个字符，恶意或误填的超长台词不会
            // 让一次悬停按原文长度分配大数组。
            var chars = new char[maxLength + 1];
            int count = 0;
            bool previousSpace = false;
            bool truncated = false;
            for (int i = 0; i < value.Length; i++)
            {
                char normalized = char.IsWhiteSpace(value[i]) ? ' ' : value[i];
                if (normalized == ' ' && previousSpace) continue;
                if (count >= chars.Length)
                {
                    truncated = true;
                    break;
                }
                chars[count++] = normalized;
                previousSpace = normalized == ' ';
            }
            if (count > maxLength)
            {
                count = maxLength;
                truncated = true;
            }
            string result = new string(chars, 0, count).Trim();
            return truncated
                ? result + "…（内容过长，已省略后文）"
                : result;
        }

        /// <summary>成员编号预览：显示前 max 个，剩余数量用中文说明。</summary>
        private static string BuildIdPreview(
            IReadOnlyList<EvtStoryGraphNode> members, int max)
        {
            if (members == null || members.Count == 0) return string.Empty;
            int shown = Mathf.Min(max, members.Count);
            string[] ids = new string[shown];
            for (int i = 0; i < shown; i++)
                ids[i] = members[i] != null ? members[i].Id.ToString() : "?";
            string preview = string.Join("、", ids);
            if (members.Count > shown)
                preview += "……另有 " + (members.Count - shown) + " 项";
            return preview;
        }

        private void ShowTooltip(StoryGraphDisplayNode node)
        {
            if (_tooltip == null || node == null || _canvasRect == null) return;
            NodeVisual nv;
            if (!_nodeLookup.TryGetValue(node, out nv)) return;
            if (!ShouldShowTooltip(node)) return; // 信息无增量时不打扰

            bool titleClipped = IsTextClipped(nv.Title);
            bool subtitleClipped = IsTextClipped(nv.Subtitle);
            bool hiddenFullText = HasHiddenFullText(node);
            bool isGroup = node.IsSegment || node.IsUnusedGroup;
            string flags = BuildFlagDescriptions(node);
            bool showBody = isGroup || subtitleClipped || hiddenFullText;

            // 只有配置提示、正文又完整可见时，不再重复抄一遍标题和正文。
            _tooltipTitle.text = titleClipped || showBody
                ? (node.IsSegment ? SegmentTitle(node) : BuildNodeTitle(node))
                : "配置提示";

            string subtitle = string.Empty;
            if (node.IsSegment && node.SegmentNodes != null
                && node.SegmentNodes.Count > 0)
            {
                EvtStoryGraphNode first = node.SegmentNodes[0];
                EvtStoryGraphNode last = node.SegmentNodes[node.SegmentNodes.Count - 1];
                if (first != null && !string.IsNullOrEmpty(first.Subtitle))
                    subtitle = "首句：" + first.Subtitle;
                if (last != null && !ReferenceEquals(last, first)
                    && !string.IsNullOrEmpty(last.Subtitle))
                    subtitle += "\n末句：" + last.Subtitle;
                subtitle += "\n包含的对话编号：" + BuildIdPreview(node.SegmentNodes, 8);
                subtitle += "\n单击展开或收起；双击可定位到第一句";
            }
            else if (node.IsUnusedGroup)
            {
                subtitle = node.Subtitle ?? string.Empty;
                string ids = BuildIdPreview(
                    _viewModel != null ? _viewModel.UnusedMembers : null, 8);
                if (!string.IsNullOrEmpty(ids))
                    subtitle += "\n包含的配置编号：" + ids;
                subtitle += "\n单击展开或收起未使用配置";
            }
            else if (showBody)
            {
                subtitle = node.SourceNode != null
                    ? BuildFullSubtitle(node.SourceNode)
                    : (node.Subtitle ?? string.Empty);
            }
            _tooltipSubtitle.gameObject.SetActive(!string.IsNullOrWhiteSpace(subtitle));
            _tooltipSubtitle.text = subtitle.Trim();

            _tooltipFlags.gameObject.SetActive(!string.IsNullOrEmpty(flags));
            _tooltipFlags.text = flags;

            _tooltip.gameObject.SetActive(true);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_tooltip);

            // overlay 下 GetWorldCorners 即屏幕像素：0左下 1左上 2右上 3右下
            var corners = new Vector3[4];
            nv.Rect.GetWorldCorners(corners);
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRect, new Vector2(corners[2].x + 12f, corners[2].y - 8f),
                null, out local);

            Rect canvasRect = _canvasRect.rect;
            float tipW = _tooltip.sizeDelta.x;
            float tipH = _tooltip.rect.height;
            float x = local.x;
            if (x + tipW > canvasRect.xMax - 8f)
            {
                // 右侧放不下：翻到节点左侧
                Vector2 leftLocal;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, new Vector2(corners[1].x - 12f, corners[1].y - 8f),
                    null, out leftLocal);
                x = leftLocal.x - tipW;
            }
            x = Mathf.Clamp(x, canvasRect.xMin + 8f, canvasRect.xMax - tipW - 8f);
            float y = Mathf.Clamp(local.y,
                canvasRect.yMin + tipH + 8f, canvasRect.yMax - 8f);
            _tooltip.anchoredPosition = new Vector2(x, y);
        }

        private void HideTooltip()
        {
            if (_tooltip != null) _tooltip.gameObject.SetActive(false);
        }

        // ==================== 节点点击 ====================

        private static TalkCfg PreviewTalkForNode(
            StoryGraphDisplayNode node)
        {
            return node != null && node.SourceNode != null
                ? node.SourceNode.Talk
                : null;
        }

        private TalkCfg SelectedViewPreviewTalk()
        {
            // 一旦用户显式选中了非对话节点，P 不应悄悄改去预览当前黄环；
            // 只有尚未选择任何卡片时才以自动定位的当前 Talk 作为默认值。
            return _selectedNode != null
                ? PreviewTalkForNode(_selectedNode)
                : PreviewTalkForNode(_currentNode);
        }

        private void PreviewGraphTalk(TalkCfg talk)
        {
            if (!_open || _previewSuspended) return;
            if (talk == null)
            {
                try { StoryGraphToastRouter.Show("请先选择一个真实对话节点"); }
                catch { }
                return;
            }
            if (string.IsNullOrWhiteSpace(talk.content))
            {
                try
                {
                    StoryGraphToastRouter.Show(
                        "当前对话内容为空，会被预览器直接跳过；请选择一条有正文的对话预览");
                }
                catch { }
                return;
            }

            IEnumerable<TalkCfg> talks = null;
            IDictionary<int, OptionCfg> options = null;
            if (_editMode)
            {
                FinalizeFocusedInspectorInput();
                if (_editSession == null)
                {
                    SetEditFeedback("剧情图草稿已失效，无法预览本句。", true);
                    return;
                }
                talks = _editSession.Talks;
                options = _editSession.Options;
            }

            // Overlay 剧情图若不暂时隐藏，会把游戏自己的 PreviewTalkView
            // 完全盖住。这里只暂停显示和输入，编辑草稿仍留在内存中。
            if (!SuspendForTalkPreview()) return;
            StoryGraphPreviewReturnBridge.Register(this);
            bool opened = EvtTalkPreviewPatch.TryOpenPreview(
                _view, talk, talks, options);
            if (!opened)
                StoryGraphPreviewReturnBridge.Cancel(this);
        }

        private void ToggleViewContainer(string key)
        {
            if (_editMode || _viewModel == null || string.IsNullOrEmpty(key))
                return;
            try
            {
                _viewModel.ToggleSegment(key);
                if (_viewModel.IsExpanded(key))
                {
                    if (!_expandedKeys.Contains(key)) _expandedKeys.Add(key);
                }
                else
                {
                    _expandedKeys.Remove(key);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StoryGraph] 展开/折叠失败：" + e);
                return;
            }
            RelayoutKeepView();
        }

        private void FocusViewNode(
            StoryGraphDisplayNode node, bool markAsCurrent)
        {
            if (node == null || _layout == null) return;
            Rect rect;
            if (!_layout.TryGetRect(node, out rect)) return;
            if (markAsCurrent) _currentNode = node;
            _selectedNode = node;
            if (_zoom < 0.7f) ZoomTo(0.85f);
            CenterOnNode(node);
            RefreshAllNodeStates();
        }

        /// <summary>
        /// 当前目标若还在折叠容器中，先展开再重排一次；重排后必须重新查找
        /// 显示节点，因为 ToggleSegment 会重建全部 StoryGraphDisplayNode。
        /// </summary>
        private StoryGraphDisplayNode RevealEditorFocusForExistingLayout()
        {
            StoryGraphDisplayNode before = FindEditorFocusNode();
            bool needsRelayout = before != null
                && (before.IsSegment || before.IsUnusedGroup)
                && _viewModel != null
                && !_viewModel.IsExpanded(before.Key);
            StoryGraphDisplayNode target = EnsureEditorFocusVisible();
            if (needsRelayout)
            {
                RelayoutKeepView();
                target = FindEditorFocusNode();
            }
            _currentNode = target;
            return target;
        }

        private void LocateCurrentEditorSelection()
        {
            if (_editMode) return;
            RefreshEditorFocusIntent();
            StoryGraphDisplayNode target = RevealEditorFocusForExistingLayout();
            if (target == null)
            {
                try { StoryGraphToastRouter.Show("原编辑器当前没有可定位的对话或选项"); }
                catch { }
                return;
            }
            FocusViewNode(target, true);
            try
            {
                StoryGraphToastRouter.Show(_editorFocusOption != null
                    ? "已定位到选项 " + _editorFocusOption.id
                    : "已定位到对话 " + _editorFocusTalk.id);
            }
            catch { }
        }

        private void SelectViewNodeInEditor(StoryGraphDisplayNode node)
        {
            if (_editMode || node == null || node.LocateTalk == null
                || _view == null) return;
            try
            {
                EvtStoryGraphNavigation.SelectTalkAndScroll(
                    _view, node.LocateTalk);
                // Select 的 Harmony 后置会把选项意图清回 Talk；若它位于折叠链段，
                // 立即展开并把图上的当前环同步到真实卡片。
                RefreshEditorFocusIntent();
                StoryGraphDisplayNode target =
                    RevealEditorFocusForExistingLayout();
                if (target != null) FocusViewNode(target, true);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StoryGraph] 定位原编辑器失败：" + e);
            }
        }

        private void AutoArrangeReadOnlyView()
        {
            if (_editMode || _viewModel == null || _viewModel.Nodes.Count == 0)
                return;
            if (_workspace == null)
            {
                try { StoryGraphToastRouter.Show("作者工作区尚未初始化，无法保存自动布局"); }
                catch { }
                return;
            }
            StoryGraphLayoutResult previous = _layout;
            StoryGraphWorkspace.Snapshot before = _workspace.Capture();
            bool beforeDirty = _workspace.Dirty;
            try
            {
                ClearHover();
                StoryGraphLayoutResult arranged = BuildProjectionStableLayout();
                // 自动布局本身是确定性的；删除全部手工坐标比保存当前折叠投影
                // 更可靠。这样以后展开链段/未使用区时，隐藏节点也会参与完整
                // 自动布局，不会被本次折叠状态留下的坐标拉扯变形。
                _workspace.ClearPositions();
                _layout = arranged;
                RebuildVisuals();
                string error;
                if (!_workspace.Save(out error))
                {
                    _workspace.Restore(before, beforeDirty);
                    _layout = previous;
                    RebuildVisuals();
                    try { StoryGraphToastRouter.Show(error + "；自动整理已回滚"); }
                    catch { }
                    return;
                }
                FitToView();
                try { StoryGraphToastRouter.Show("已自动整理并保存作者工作区布局"); }
                catch { }
            }
            catch (Exception e)
            {
                _workspace.Restore(before, beforeDirty);
                _layout = previous;
                try { RebuildVisuals(); }
                catch { }
                Plugin.Log?.LogError("[StoryGraph.View.Arrange] " + e);
                try { StoryGraphToastRouter.Show("自动整理失败；布局已回滚，剧情数据未改变"); }
                catch { }
            }
        }

        private void OnNodePointerClick(NodeVisual nv, PointerEventData data)
        {
            if (nv == null || nv.Node == null || _viewModel == null || data == null)
                return;
            StoryGraphDisplayNode node = nv.Node;

            if (_editMode)
            {
                if (data.button != PointerEventData.InputButton.Left) return;
                if (node.SourceNode != null
                    && (node.SourceNode.Talk != null || node.SourceNode.Option != null))
                {
                    Keyboard keyboard = Keyboard.current;
                    bool ctrl = keyboard != null
                        && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed);
                    bool shift = keyboard != null
                        && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
                    SetEditSelection(node, ctrl || shift, ctrl);
                    UpdateEditSelectionStatus();
                }
                else
                {
                    SetEditFeedback(
                        "编辑模式只能选择真实对话或选项；事件入口、缺失引用和外部占位节点不可修改。",
                        false);
                }
                return;
            }

            if (data.button != PointerEventData.InputButton.Left) return;
            if (data.clickCount >= 2)
            {
                // 双击：定位编辑器里的对应 Talk，图保持打开
                if (node.LocateTalk != null && _view != null)
                {
                    try
                    {
                        EvtStoryGraphNavigation.SelectTalkAndScroll(
                            _view, node.LocateTalk);
                        // 编辑器当前选中已变化：同步青色定位环
                        RefreshEditorFocusIntent();
                        StoryGraphDisplayNode located =
                            RevealEditorFocusForExistingLayout();
                        if (located != null) _currentNode = located;
                        RefreshAllNodeStates();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log?.LogError("[StoryGraph] 双击定位失败：" + e);
                    }
                }
                return;
            }

            // 单击：链段块/未使用折叠区 → 展开/折叠（保持缩放平移）；普通节点 → 选中
            if (node.IsSegment || node.IsUnusedGroup)
            {
                ToggleViewContainer(node.Key);
                return;
            }

            _selectedNode = node;
            RefreshAllNodeStates();
        }

        private void UpdateEditSelectionStatus()
        {
            RefreshInspector();
            int count = _selectedNodes.Count;
            if (count == 0 || _selectedNode == null)
            {
                _editStatus = "当前未选择节点。Ctrl 点击可切换多选，Shift 点击可追加选择。";
                UpdateStatusBar();
                return;
            }
            if (count > 1)
            {
                _editStatus = "已选择 " + count
                              + " 个节点；拖动任一已选节点可整体移动，Delete 可二次确认后批量删除。";
                UpdateStatusBar();
                return;
            }

            EvtStoryGraphNode source = _selectedNode.SourceNode;
            if (source != null && source.Talk != null)
                _editStatus = "已选择对话 " + source.Talk.id
                              + "。拖动右侧端口建立分支；Delete 删除；保存后可回原表单编辑全文。";
            else if (source != null && source.Option != null)
                _editStatus = "已选择选项 " + _editSelectionId
                              + "。拖动“结果”端口连接后续对话。";
            else
                _editStatus = "已选择 1 个节点。";
            string attention = BuildFlagDescriptions(_selectedNode);
            if (!string.IsNullOrWhiteSpace(attention))
                _editStatus += "　提示：" + attention.Replace("\n", "；");
            UpdateStatusBar();
        }

        // ==================== 节点状态刷新 ====================

        private void RefreshAllNodeStates()
        {
            for (int i = 0; i < _activeNodes.Count; i++)
                ApplyNodeVisualState(_activeNodes[i]);
            UpdatePreviewControls();
        }

        private void ApplyNodeVisualState(NodeVisual nv)
        {
            if (nv == null || nv.Node == null) return;
            StoryGraphDisplayNode node = nv.Node;
            nv.CurrentRing.gameObject.SetActive(ReferenceEquals(node, _currentNode));
            // 命中黄环；当前命中换成更粗的外扩橙环——整图缩进视口时
            // ClampPan 会抵消居中，循环切换的反馈全靠这圈环。
            bool isMatch = _matches.Contains(node);
            bool isCurrentMatch = isMatch && IsCurrentMatch(node);
            nv.SearchRing.gameObject.SetActive(isMatch);
            if (isMatch)
            {
                nv.SearchRing.color = isCurrentMatch ? AccentOrange : SearchRingColor;
                float margin = isCurrentMatch ? -9f : -5f;
                nv.SearchRing.rectTransform.offsetMin = new Vector2(margin, margin);
                nv.SearchRing.rectTransform.offsetMax = new Vector2(-margin, -margin);
            }
            nv.SelectRing.gameObject.SetActive(
                ReferenceEquals(node, _selectedNode) || _selectedNodes.Contains(node));
            if (ReferenceEquals(node, _hoverNode))
                nv.Border.color = HoverBorderColor;
            else if (_hoverAdjacent.Contains(node))
                nv.Border.color = AdjacentBorderColor;
            else
                nv.Border.color = NodeBaseBorder(node);
            nv.Fill.color = NodeBaseFill(node);
            // 选项节点 2px 边框（Fill 内缩 = 边框厚度），分支点更醒目
            float inset = node.Kind == StoryGraphDisplayNodeKind.Option
                ? 2f : NodeBorderThickness;
            nv.Fill.rectTransform.offsetMin = new Vector2(inset, inset);
            nv.Fill.rectTransform.offsetMax = new Vector2(-inset, -inset);
            // 语义色条：未使用组不显示，其余按旗标优先级着色
            nv.ColorBar.gameObject.SetActive(!node.IsUnusedGroup);
            nv.ColorBar.color = NodeBarColor(node);
            nv.Title.color =
                node.HasFlag(EvtStoryGraphNodeFlags.MissingReference)
                || node.HasFlag(EvtStoryGraphNodeFlags.InvalidData)
                || node.HasFlag(EvtStoryGraphNodeFlags.CgNotClosed)
                    ? InvalidTitleColor
                    : TitleColor;
        }

        /// <summary>是否为「上个/下个」循环中的当前命中（_matchIndex 指向它）。</summary>
        private bool IsCurrentMatch(StoryGraphDisplayNode node)
        {
            return node != null
                && _matchIndex >= 0 && _matchIndex < _matches.Count
                && ReferenceEquals(_matches[_matchIndex], node);
        }

        // ==================== 可视化编辑 ====================

        /// <summary>剧情草稿是否有未保存改动。</summary>
        private bool HasUnsavedGraphChanges()
        {
            if (_editSession == null) return false;
            return _editSession.Dirty;
        }

        private void RequestClose()
        {
            if (_editMode && _editSession != null && HasUnsavedGraphChanges())
            {
                SetEditFeedback(
                    "有未保存的剧情图修改。请先点“保存”，或连续两次点“放弃”后再关闭。", true);
                return;
            }
            Close();
        }

        private void EnterEditMode()
        {
            if (_editMode || _savingEdit || _view == null) return;
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);

            string error;
            string modRoot;
            if (!EvtStoryGraphViewAccess.TryGetModRoot(_view, out modRoot, out error))
            {
                SetEditFeedback(error, true);
                return;
            }
            if (StoryGraphEditPersistence.HasPendingTransaction(modRoot))
            {
                bool quarantined;
                if (!StoryGraphEditPersistence.TryRecoverPendingTransaction(
                        modRoot, out quarantined, out error))
                    SetEditFeedback(error, true);
                else if (quarantined)
                    // 隔离放行什么都没恢复，磁盘保持现状（可能含会话外改动），
                    // 不能与下面的“已恢复”共用文案误导用户跳过检查。
                    SetEditFeedback(
                        "旧保存事务无法自动恢复，已隔离放行；当前文件保持现状，请核对配置内容后再编辑。备份位置见日志。", true);
                else
                    SetEditFeedback(
                        "已恢复上次强退留下的保存事务。当前事件是在恢复前加载的，请关闭并重新打开事件后再编辑。", true);
                return;
            }

            List<TalkCfg> talks;
            Dictionary<int, OptionCfg> options;
            int eventId;
            List<int> entries;
            bool entriesKnown;
            if (!EvtStoryGraphViewAccess.TrySnapshot(
                    _view, out talks, out options, out eventId,
                    out entries, out entriesKnown, out error))
            {
                SetEditFeedback(error ?? "无法读取事件数据，不能进入编辑模式。", true);
                return;
            }
            HashSet<int> mergedOptionIds = MergeReferencedOptionsForSnapshot(
                options, SnapshotEventConfiguration(eventId), talks);

            try
            {
                // 快照容器持有原编辑器实时对象引用，先深拷贝隔离，绝不改写编辑器
                // 内存；会话随后照常再克隆一次。选项同理，而且更严格——合并进来的
                // 共享选项直接来自全局 Cfg.OptionCfgMap，就地改写等于污染本局全局配置。
                talks = StoryGraphEditSession.CloneTalks(talks);
                options = StoryGraphEditSession.CloneOptions(options);
                // 可选 LaTeX 探针（StudentAgeLatex 插件在场时挂接）：会话构造前把
                // 磁盘上的烘焙成品换回作者的 $ 源码。深拷贝草稿就地改写，缺席时零成本。
                StoryGraphLatexProbe.SwapbackSource?.Invoke(modRoot, eventId, talks, options);
                _editSession = new StoryGraphEditSession(
                    talks, options, eventId, entries, entriesKnown, modRoot,
                    mergedOptionIds);
                // 每次进入都是全新的深拷贝草稿；旧会话的统一时间线绝不能
                // 复用，否则放弃后重进会把 Ctrl+Z 指向已销毁的 session。
                ClearEditHistories();
                _selectedNodes.Clear();
                _selectedNodeKeys.Clear();
                TalkCfg current = EvtStoryGraphViewAccess.GetCurrent(_view);
                _editSelectionKind = current != null
                    ? StoryGraphEditNodeKind.Talk
                    : StoryGraphEditNodeKind.None;
                _editSelectionId = current != null ? current.id : 0;
                _inspectorVisible = true;
                _inspectorBasicPreviewDirty = false;
                EndInspectorLiveEdit();
                _editMode = true;
                _editStatus = "编辑草稿已就绪：左键空白框选，中键平移，右键打开菜单；"
                              + "P 预览所选对话，Ctrl+C 复制，Ctrl+Shift+C 复制下游分支，Ctrl+V 粘贴。";
                _deleteConfirmUntil = 0f;
                _discardConfirmUntil = 0f;
                _saveCgConfirmUntil = 0f;
                ClearHover();
                UpdateToolbarMode();
                RefreshGraph(false);
                if (_selectedNode != null) CenterOnNode(_selectedNode);
                UpdateEditControls();
                UpdateStatusBar();
            }
            catch (Exception e)
            {
                _editMode = false;
                _editSession = null;
                UpdateToolbarMode();
                Plugin.Log?.LogError("[StoryGraph.Edit.Enter] " + e);
                SetEditFeedback("进入编辑模式失败：" + e.Message, true);
            }
        }

        private void RequestExitEditMode()
        {
            if (!_editMode) return;
            if (_editSession != null && HasUnsavedGraphChanges())
            {
                SetEditFeedback(
                    "草稿尚未保存。请先保存，或连续两次点击“放弃”退出编辑模式。", true);
                return;
            }
            ExitEditMode(false);
        }

        private void RequestDiscardEdit()
        {
            if (!_editMode || _editSession == null) return;
            if (!HasUnsavedGraphChanges())
            {
                ExitEditMode(false);
                return;
            }
            if (Time.unscaledTime > _discardConfirmUntil)
            {
                _discardConfirmUntil = Time.unscaledTime + ConfirmSeconds;
                _deleteConfirmUntil = 0f;
                _saveCgConfirmUntil = 0f;
                _editStatus = "再次点击“放弃”将丢弃全部未保存修改；此操作不能撤销。";
                UpdateEditControls();
                UpdateStatusBar();
                try { StoryGraphToastRouter.Show("请再次点击“放弃”确认丢弃剧情图草稿"); }
                catch { }
                return;
            }
            ExitEditMode(true);
        }

        private void ExitEditMode(bool discarded)
        {
            CancelConnectionDrag();
            CancelBoxSelection(true);
            EndInspectorLiveEdit();
            _nodeDrag = null;
            RememberExpandedSegmentsAfterEditing();
            _editMode = false;
            _editSession = null;
            _editSelectionKind = StoryGraphEditNodeKind.None;
            _editSelectionId = 0;
            _editSelectionOrdinal = 0;
            _editSelectionStableKey = null;
            _selectedGroupId = null;
            ClearEditHistories();
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            _deleteConfirmUntil = 0f;
            _discardConfirmUntil = 0f;
            _saveCgConfirmUntil = 0f;
            _editStatus = null;
            UpdateToolbarMode();
            RefreshGraph(false);
            if (discarded)
            {
                try { StoryGraphToastRouter.Show("已放弃未保存的剧情图修改"); }
                catch { }
            }
        }

        private void ShowShortcutHelp()
        {
            _selectedGroupId = null;
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            _selectedNode = null;
            SyncPrimaryEditSelection();
            RefreshAllNodeStates();
            RefreshGroupVisualStates();
            ShowInspector();
            if (_inspectorTitle == null) return;
            _inspectorTitle.text = "剧情图操作说明";
            _inspectorInfo.text = "ComfyUI 式编辑快捷键；作者工作区布局会即时保存，"
                                + "放弃剧情草稿不会删除布局。";
            _inspectorContentLabel.text = "鼠标操作";
            if (_inspectorShowLabel != null)
                _inspectorShowLabel.text = "键盘快捷键";
            SetInspectorText(_inspectorContentInput,
                "左键空白拖动：框选\nCtrl/Shift 点击：多选\n拖动节点：移动所选\n"
                + "Alt+拖动：关闭 10px 网格吸附\n中键拖动：平移画布\n"
                                + "拖动端口：连接；“选项”端口松到空白会直接创建通用选项，条件/结束/小游戏在右侧属性中配置\n"
                + "右击端口：单线直接断开；多线选择具体槽位或明确清空（可撤销）\n"
                + "右击末句节点：可创建“确定”结尾；右击节点/空白/分组：上下文菜单");
            SetInspectorText(_inspectorShowInput,
                "Ctrl+A 全选　Ctrl+C 复制　Ctrl+Shift+C 复制下游分支\n"
                + "Ctrl+V 粘贴　Delete 单个立即删除 / 批量二次确认\n"
                + "Ctrl+Z 撤销　Ctrl+Y / Ctrl+Shift+Z 重做\n"
                + "P 预览所选对话　Ctrl+S 保存剧情　L 切换连线标签　Esc 取消/返回");
            SetInspectorText(_inspectorJsonInput, string.Empty);
            SetInspectorText(_inspectorSpeakerInput, string.Empty);
            SetInspectorText(_inspectorNextEventInput, string.Empty);
            if (_inspectorSpeakerRow != null) _inspectorSpeakerRow.SetActive(false);
            if (_inspectorShowRow != null) _inspectorShowRow.SetActive(true);
            if (_inspectorTagRow != null) _inspectorTagRow.SetActive(false);
            if (_inspectorNextEventRow != null)
                _inspectorNextEventRow.SetActive(false);
            if (_inspectorNextEventHint != null)
                _inspectorNextEventHint.gameObject.SetActive(false);
            SetInspectorInteractable(false);
            _inspectorShowingHelp = true;
            RelayoutBasicInspector();
            RelayoutInspectorHeader();
            _editStatus = "操作说明已显示在右侧属性面板。";
            UpdateStatusBar();
        }

        private void SelectAllEditableNodes()
        {
            if (!_editMode || _viewModel == null) return;
            _selectedGroupId = null;
            RefreshGroupVisualStates();
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                if (node == null || node.SourceNode == null
                    || (node.SourceNode.Talk == null && node.SourceNode.Option == null))
                    continue;
                _selectedNodes.Add(node);
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key)) _selectedNodeKeys.Add(key);
            }
            _selectedNode = _selectedNodes.FirstOrDefault();
            SyncPrimaryEditSelection();
            RefreshAllNodeStates();
            UpdateEditControls();
            UpdateEditSelectionStatus();
        }

        private void CopySelectionToClipboard(bool includeBranch)
        {
            if (_editSession == null || _viewModel == null || _layout == null) return;
            List<TalkCfg> talks;
            List<OptionCfg> options;
            CollectSelectedConfigs(out talks, out options);
            if (talks.Count == 0 && options.Count == 0)
            {
                SetEditFeedback("请先选择要复制的真实对话或选项节点。", false);
                return;
            }

            string error;
            if (includeBranch && !ExpandClipboardBranch(talks, options, out error))
            {
                SetEditFeedback(error, true);
                return;
            }
            if (talks.GroupBy(talk => talk.id).Any(group => group.Count() > 1))
            {
                SetEditFeedback("选区含有重复对话 ID，无法可靠重映射分支；请先修复重复编号。", true);
                return;
            }

            // 按会话原顺序冻结，保证多次粘贴的编号分配与布局顺序确定。
            talks = _editSession.Talks.Where(talk => talk != null
                && talks.Any(item => ReferenceEquals(item, talk))).ToList();
            var optionEntries = _editSession.Options
                .Where(pair => pair.Value != null
                    && options.Any(item => ReferenceEquals(item, pair.Value)))
                .OrderBy(pair => pair.Key).ToList();
            if (optionEntries.Select(pair => pair.Value)
                .GroupBy(option => option).Any(group => group.Count() > 1))
            {
                SetEditFeedback("选区含有同一选项对象的多键别名，已拒绝复制。", true);
                return;
            }

            var clipboard = new StoryGraphClipboardData
            {
                Talks = StoryGraphEditSession.CloneTalks(talks),
                Options = StoryGraphEditSession.CloneOptions(optionEntries),
                IncludesBranch = includeBranch,
            };
            var layouts = new List<KeyValuePair<ClipboardLayoutEntry, Rect>>();
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                EvtStoryGraphNode source = node != null ? node.SourceNode : null;
                if (source == null) continue;
                bool explicitlySelected = _selectedNodes.Contains(node);
                bool includeNode = false;
                int sourceId = 0;
                int parentId = source.LocateTalk != null ? source.LocateTalk.id : 0;
                if (source.Talk != null
                    && talks.Any(item => ReferenceEquals(item, source.Talk)))
                {
                    includeNode = includeBranch || explicitlySelected;
                    sourceId = source.Talk.id;
                }
                else if (source.Option != null
                    && options.Any(item => ReferenceEquals(item, source.Option)))
                {
                    includeNode = explicitlySelected || (includeBranch
                        && (source.LocateTalk == null || talks.Any(item =>
                            ReferenceEquals(item, source.LocateTalk))));
                    sourceId = _editSession.FindOptionKey(source.Option);
                }
                Rect rect;
                if (!includeNode || sourceId == int.MinValue
                    || !_layout.TryGetRect(node, out rect)) continue;
                var entry = new ClipboardLayoutEntry
                {
                    IsTalk = source.Talk != null,
                    SourceId = sourceId,
                    ParentTalkId = parentId,
                };
                layouts.Add(new KeyValuePair<ClipboardLayoutEntry, Rect>(entry, rect));
                minX = Mathf.Min(minX, rect.x);
                minY = Mathf.Min(minY, rect.y);
            }
            if (layouts.Count > 0)
            {
                foreach (KeyValuePair<ClipboardLayoutEntry, Rect> pair in layouts)
                {
                    pair.Key.Offset = new Vector2(
                        pair.Value.x - minX, pair.Value.y - minY);
                    clipboard.Layout.Add(pair.Key);
                }
            }
            _clipboard = clipboard;
            SetEditFeedback("已复制 " + clipboard.Talks.Count + " 个对话和 "
                + clipboard.Options.Count + " 个选项"
                + (includeBranch ? "（包含完整下游分支）" : string.Empty)
                + "；Ctrl+V 在鼠标位置粘贴。", false);
        }

        private bool ExpandClipboardBranch(
            List<TalkCfg> talks, List<OptionCfg> options, out string error)
        {
            error = null;
            int talkIndex = 0;
            int optionIndex = 0;
            while (talkIndex < talks.Count || optionIndex < options.Count)
            {
                while (talkIndex < talks.Count)
                {
                    TalkCfg talk = talks[talkIndex++];
                    if (!AddClipboardTalkTargets(talk.nextTalk, talks, out error)
                        || !AddClipboardTalkTargets(talk.nextTalk2, talks, out error)
                        || !AddClipboardMiniGameTargets(talk.miniGame, talks, out error))
                        return false;
                    if (talk.option == null) continue;
                    foreach (int optionId in talk.option)
                    {
                        OptionCfg option;
                        if (optionId == 0
                            || !_editSession.Options.TryGetValue(optionId, out option)
                            || option == null) continue;
                        if (!options.Any(item => ReferenceEquals(item, option)))
                            options.Add(option);
                    }
                }
                while (optionIndex < options.Count)
                {
                    OptionCfg option = options[optionIndex++];
                    if (!AddClipboardTalkTargets(option.talkId, talks, out error)
                        || !AddClipboardTalkTargets(option.talkId2, talks, out error)
                        || !AddClipboardMiniGameTargets(option.miniGame, talks, out error))
                        return false;
                }
            }
            return true;
        }

        private bool AddClipboardMiniGameTargets(
            List<double> miniGame, List<TalkCfg> talks, out string error)
        {
            error = null;
            if (!MiniGameUtil.IsParamJump(miniGame)) return true;
            List<int> targets;
            if (!MiniGameUtil.TryGetParamJumpTargets(
                    miniGame, out targets, out error)) return false;
            return AddClipboardTalkTargets(targets, talks, out error);
        }

        private bool AddClipboardTalkTargets(
            List<int> ids, List<TalkCfg> talks, out string error)
        {
            error = null;
            if (ids == null) return true;
            foreach (int id in ids)
            {
                if (id == 0) continue;
                List<TalkCfg> matches = _editSession.Talks
                    .Where(talk => talk != null && talk.id == id).ToList();
                if (matches.Count > 1)
                {
                    error = "分支引用了重复对话 ID " + id
                            + "，无法确定应复制哪一份配置。";
                    return false;
                }
                if (matches.Count == 1
                    && !talks.Any(item => ReferenceEquals(item, matches[0])))
                    talks.Add(matches[0]);
            }
            return true;
        }

        private Vector2 DefaultPasteScreenPoint()
        {
            if (PointerInViewport(_lastPointerScreen)
                && !PointerOverMinimap(_lastPointerScreen))
                return _lastPointerScreen;
            if (_viewport == null) return Vector2.zero;
            return RectTransformUtility.WorldToScreenPoint(
                null, _viewport.TransformPoint(_viewport.rect.center));
        }

        private bool TryScreenToLayout(Vector2 screenPoint, out Vector2 layoutPoint)
        {
            layoutPoint = Vector2.zero;
            if (_content == null) return false;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _content, screenPoint, null, out local)) return false;
            layoutPoint = new Vector2(
                Mathf.Max(StoryGraphMetrics.Margin, local.x),
                Mathf.Max(StoryGraphMetrics.Margin, -local.y));
            return true;
        }

        private void PasteClipboard(Vector2 screenPoint)
        {
            EndInspectorLiveEdit();
            if (_editSession == null || _clipboard == null)
            {
                SetEditFeedback("剧情图剪贴板为空；先用 Ctrl+C 或 Ctrl+Shift+C 复制。", false);
                return;
            }
            Vector2 target;
            if (!TryScreenToLayout(screenPoint, out target))
            {
                SetEditFeedback("无法确定粘贴位置。", true);
                return;
            }
            TalkCfg near = SelectedEditTalk();
            if (near == null && _editSelectionKind == StoryGraphEditNodeKind.Option)
                near = _editSession.FindParentTalk(_editSelectionId);
            StoryGraphPasteResult result;
            string message;
            if (!_editSession.TryPaste(
                    _clipboard.Talks, _clipboard.Options, near,
                    out result, out message))
            {
                SetEditFeedback(message, true);
                return;
            }

            StoryGraphEditNodeKind kind = result.Talks.Count > 0
                ? StoryGraphEditNodeKind.Talk
                : StoryGraphEditNodeKind.Option;
            int id = result.Talks.Count > 0
                ? result.Talks[0].id
                : result.Options.Keys.First();
            RefreshEditGraph(kind, id, message, false);
            ApplyPastedLayout(result, target, message);
        }

        private void ApplyPastedLayout(
            StoryGraphPasteResult result, Vector2 target, string baseMessage)
        {
            if (result == null || _viewModel == null) return;
            StoryGraphWorkspace.Snapshot before = _workspace != null
                ? _workspace.Capture()
                : null;
            bool beforeDirty = _workspace != null && _workspace.Dirty;
            var positioned = new HashSet<StoryGraphDisplayNode>();
            StoryGraphDisplayNode primary = null;
            foreach (ClipboardLayoutEntry entry in _clipboard.Layout)
            {
                StoryGraphDisplayNode node = FindPastedLayoutNode(
                    entry, result, positioned);
                if (node == null) continue;
                positioned.Add(node);
                if (primary == null) primary = node;
                if (_workspace != null)
                {
                    string key = StoryGraphWorkspace.StableNodeKey(node);
                    if (!string.IsNullOrEmpty(key))
                        _workspace.SetPosition(key, target + entry.Offset);
                }
            }

            string warning = string.Empty;
            if (_workspace != null && _workspace.Dirty)
            {
                string error;
                if (!_workspace.Save(out error))
                {
                    _workspace.Restore(before, beforeDirty);
                    warning = "；但保存粘贴布局失败，已改用自动布局：" + error;
                }
            }

            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                EvtStoryGraphNode source = node != null ? node.SourceNode : null;
                bool pasted = source != null
                    && ((source.Talk != null && result.Talks.Any(item =>
                            ReferenceEquals(item, source.Talk)))
                        || (source.Option != null && result.Options.Values.Any(item =>
                            ReferenceEquals(item, source.Option))));
                if (!pasted) continue;
                _selectedNodes.Add(node);
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key)) _selectedNodeKeys.Add(key);
                if (primary == null) primary = node;
            }
            _selectedNode = primary;
            SyncPrimaryEditSelection();
            RefreshGraph(false);
            _editStatus = baseMessage + warning;
            UpdateEditControls();
            UpdateStatusBar();
        }

        private StoryGraphDisplayNode FindPastedLayoutNode(
            ClipboardLayoutEntry entry,
            StoryGraphPasteResult result,
            ISet<StoryGraphDisplayNode> used)
        {
            if (entry.IsTalk)
            {
                int id;
                if (!result.TalkIdMap.TryGetValue(entry.SourceId, out id)) return null;
                return _viewModel.Nodes.FirstOrDefault(node => node != null
                    && !used.Contains(node) && node.SourceNode != null
                    && node.SourceNode.Talk != null
                    && node.SourceNode.Talk.id == id);
            }

            int optionId;
            OptionCfg option;
            if (!result.OptionIdMap.TryGetValue(entry.SourceId, out optionId)
                || !result.Options.TryGetValue(optionId, out option)) return null;
            int parentId = 0;
            bool hasParent = entry.ParentTalkId > 0
                && result.TalkIdMap.TryGetValue(entry.ParentTalkId, out parentId);
            StoryGraphDisplayNode fallback = null;
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                EvtStoryGraphNode source = node != null ? node.SourceNode : null;
                if (source == null || used.Contains(node)
                    || !ReferenceEquals(source.Option, option)) continue;
                if (fallback == null) fallback = node;
                if (hasParent && source.LocateTalk != null
                    && source.LocateTalk.id == parentId) return node;
                if (!hasParent && source.LocateTalk == null) return node;
            }
            return fallback;
        }

        private void CreateGroupFromSelection()
        {
            EndInspectorLiveEdit();
            if (_workspace == null || _selectedNodes.Count == 0)
            {
                SetEditFeedback("请先选择要加入分组的真实节点。", false);
                return;
            }
            var keys = _selectedNodes.Select(StoryGraphWorkspace.StableNodeKey)
                .Where(key => !string.IsNullOrEmpty(key))
                .Distinct(StringComparer.Ordinal).ToList();
            if (keys.Count == 0)
            {
                SetEditFeedback("当前选择中没有可持久化的真实节点。", false);
                return;
            }
            StoryGraphWorkspace.Snapshot before = _workspace.Capture();
            bool beforeDirty = _workspace.Dirty;
            StoryGraphWorkspace.GroupData group = _workspace.CreateGroup(
                "新分组", keys);
            _selectedGroupId = group.Id;
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            _selectedNode = null;
            SyncPrimaryEditSelection();
            CommitWorkspaceMetadata(before, beforeDirty,
                "已创建包含 " + keys.Count + " 个节点的分组。", group.Id);
        }

        private void CreateNoteAt(Vector2 screenPoint)
        {
            EndInspectorLiveEdit();
            if (_workspace == null) return;
            Vector2 position;
            if (!TryScreenToLayout(screenPoint, out position))
            {
                SetEditFeedback("无法确定注释框位置。", true);
                return;
            }
            StoryGraphWorkspace.Snapshot before = _workspace.Capture();
            bool beforeDirty = _workspace.Dirty;
            StoryGraphWorkspace.GroupData note = _workspace.CreateGroup(
                "新注释", null, "F0C987", string.Empty, true);
            _workspace.SetGroupBounds(note.Id,
                new Rect(position.x, position.y, 320f, 180f));
            _selectedGroupId = note.Id;
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            _selectedNode = null;
            SyncPrimaryEditSelection();
            CommitWorkspaceMetadata(before, beforeDirty,
                "已创建注释框；可在属性检查器填写标题和内容。", note.Id);
        }

        private void RemoveWorkspaceGroup(string id)
        {
            EndInspectorLiveEdit();
            if (_workspace == null || string.IsNullOrEmpty(id)) return;
            StoryGraphWorkspace.Snapshot before = _workspace.Capture();
            bool beforeDirty = _workspace.Dirty;
            StoryGraphWorkspace.GroupData group = _workspace.FindGroup(id);
            if (!_workspace.RemoveGroup(id))
            {
                SetEditFeedback("分组已不存在。", false);
                return;
            }
            _selectedGroupId = null;
            CommitWorkspaceMetadata(before, beforeDirty,
                group != null && group.IsNote
                    ? "已删除注释框。"
                    : "已删除分组；成员节点和剧情数据均未删除。",
                null);
        }

        private bool CommitWorkspaceMetadata(
            StoryGraphWorkspace.Snapshot before, bool beforeDirty,
            string description, string selectedGroupId)
        {
            // 所有离散元数据事务在提交前结束连续输入；即使未来新增调用点，
            // 也不会让文字编辑状态跨越到下一次工作区动作。
            EndInspectorLiveEdit();
            if (_workspace == null) return false;
            string error;
            if (!_workspace.Save(out error))
            {
                _workspace.Restore(before, beforeDirty);
                _selectedGroupId = null;
                RefreshGraph(false);
                SetEditFeedback(error + "；工作区操作已回滚。", true);
                return false;
            }
            _workspaceUndo.Push(before);
            _workspaceRedo.Clear();
            _editActionTimeline.Push(true);
            _editRedoTimeline.Clear();
            if (_editSession != null) _editSession.ClearRedoHistory();
            _selectedGroupId = selectedGroupId;
            _editStatus = description;
            RefreshGraph(false);
            UpdateEditControls();
            UpdateStatusBar();
            return true;
        }

        private void CreateConnectedAt(
            ConnectionDragState drag, Vector2 screenPoint)
        {
            EndInspectorLiveEdit();
            if (_editSession == null || drag == null) return;
            StoryGraphEditNodeKind expectedKind =
                drag.Kind == StoryGraphEditPortKind.TalkOption
                    ? StoryGraphEditNodeKind.Option
                    : StoryGraphEditNodeKind.Talk;
            Vector2 position;
            bool hasPosition = TryRightSpawnPosition(
                drag.Port != null && drag.Port.Owner != null
                    ? drag.Port.Owner.Node : null,
                expectedKind, out position);
            if (!hasPosition)
                hasPosition = TryScreenToLayout(screenPoint, out position);

            TalkCfg createdTalk;
            OptionCfg createdOption;
            string message;
            if (!_editSession.TryCreateConnected(drag.Kind,
                    drag.SourceTalk, drag.SourceOption,
                    out createdTalk, out createdOption, out message))
            {
                SetEditFeedback(message, true);
                return;
            }
            PositionCreatedNode(
                createdTalk != null
                    ? StoryGraphEditNodeKind.Talk
                    : StoryGraphEditNodeKind.Option,
                createdTalk != null ? createdTalk.id : createdOption.id,
                position, hasPosition,
                message + (hasPosition ? "；新节点已放在来源节点右侧。" : string.Empty),
                false);
        }

        private void CreateTalkAt(Vector2 screenPoint)
        {
            EndInspectorLiveEdit();
            if (_editSession == null) return;
            TalkCfg near = SelectedEditTalk();
            TalkCfg created;
            string message;
            if (!_editSession.TryAddTalk(near, out created, out message))
            {
                SetEditFeedback(message, true);
                return;
            }
            PositionCreatedNode(
                StoryGraphEditNodeKind.Talk, created.id, screenPoint, message);
        }

        private void CreateOptionAt(Vector2 screenPoint)
        {
            EndInspectorLiveEdit();
            if (_editSession == null) return;
            TalkCfg parent = SelectedOptionParentTalk();
            if (parent == null)
            {
                SetEditFeedback(
                    "选项必须属于一条对话；请先选择它要显示在哪句对话之后。",
                    false);
                return;
            }
            CreateOptionFromTemplateAt(
                parent, DisplayNodeForTalk(parent) ?? _selectedNode,
                screenPoint, StoryGraphOptionTemplateKind.Direct,
                false, true);
        }

        private TalkCfg SelectedOptionParentTalk()
        {
            if (_editSession == null || _selectedNodes.Count > 1) return null;
            TalkCfg parent = SelectedEditTalk();
            if (parent == null
                && _editSelectionKind == StoryGraphEditNodeKind.Option)
                parent = CurrentOptionLocateTalk()
                    ?? _editSession.FindParentTalk(_editSelectionId);
            return parent != null && _editSession.Talks.Contains(parent)
                ? parent : null;
        }

        private void CreateOptionFromTemplateAt(
            TalkCfg parent, StoryGraphDisplayNode origin,
            Vector2 screenPoint, StoryGraphOptionTemplateKind template,
            bool preferRight, bool focus)
        {
            EndInspectorLiveEdit();
            if (_editSession == null) return;
            Vector2 position = Vector2.zero;
            bool hasPosition = preferRight && TryRightSpawnPosition(
                origin, StoryGraphEditNodeKind.Option, out position);
            if (!hasPosition)
                hasPosition = TryScreenToLayout(screenPoint, out position);

            OptionCfg created;
            string message;
            if (!_editSession.TryAddOption(
                    parent, template, out created, out message))
            {
                SetEditFeedback(message, true);
                return;
            }
            _optionConditionalDraftId =
                template == StoryGraphOptionTemplateKind.Conditional
                    ? created.id : int.MinValue;
            PositionCreatedNode(
                StoryGraphEditNodeKind.Option, created.id,
                position, hasPosition,
                message + (hasPosition
                    ? "；新节点已放在所属对话右侧。" : string.Empty),
                focus);
            ShowInspector();
            if (template == StoryGraphOptionTemplateKind.Conditional)
            {
                SwitchInspectorPage(InspectorPage.Logic, true);
                SetEditFeedback(message
                    + " 判断条件留空时始终走成立路线；请先补充条件。", false);
            }
            else if (template == StoryGraphOptionTemplateKind.MiniGame)
            {
                SwitchInspectorPage(InspectorPage.MiniGame, true);
                OpenInspectorResourcePicker(StoryGraphResourceKind.MiniGame);
            }
            else
            {
                SwitchInspectorPage(InspectorPage.Basic, true);
            }
        }

        private void CreateEndingOptionAt(
            TalkCfg parent,
            StoryGraphDisplayNode origin,
            Vector2 screenPoint,
            bool focus)
        {
            EndInspectorLiveEdit();
            if (_editSession == null) return;

            Vector2 position;
            bool hasPosition = TryRightSpawnPosition(
                origin, StoryGraphEditNodeKind.Option, out position);
            if (!hasPosition)
                hasPosition = TryScreenToLayout(screenPoint, out position);

            OptionCfg created;
            string message;
            if (!_editSession.TryAddEndingOption(parent, out created, out message))
            {
                SetEditFeedback(message, true);
                return;
            }
            PositionCreatedNode(
                StoryGraphEditNodeKind.Option, created.id,
                position, hasPosition,
                message + (hasPosition ? "；结尾节点已放在末句右侧。" : string.Empty),
                focus);
        }

        private void PositionCreatedNode(
            StoryGraphEditNodeKind kind, int id,
            Vector2 screenPoint, string baseMessage)
        {
            Vector2 position;
            bool hasPosition = TryScreenToLayout(screenPoint, out position);
            PositionCreatedNode(
                kind, id, position, hasPosition, baseMessage, false);
        }

        private void PositionCreatedNode(
            StoryGraphEditNodeKind kind, int id,
            Vector2 position, bool hasPosition,
            string baseMessage, bool focus)
        {
            RefreshEditGraph(kind, id, baseMessage, false);
            string warning = string.Empty;
            if (hasPosition && _workspace != null && _selectedNode != null)
            {
                StoryGraphWorkspace.Snapshot before = _workspace.Capture();
                bool beforeDirty = _workspace.Dirty;
                string key = StoryGraphWorkspace.StableNodeKey(_selectedNode);
                if (!string.IsNullOrEmpty(key))
                {
                    _workspace.SetPosition(key, position);
                    string error;
                    if (!_workspace.Save(out error))
                    {
                        _workspace.Restore(before, beforeDirty);
                        warning = "；但保存节点位置失败，已使用自动布局：" + error;
                    }
                    RefreshGraph(false);
                }
            }
            _editStatus = baseMessage + warning;
            if (focus && _selectedNode != null) CenterOnNode(_selectedNode);
            UpdateEditControls();
            UpdateStatusBar();
        }

        private bool TryRightSpawnPosition(
            StoryGraphDisplayNode origin,
            StoryGraphEditNodeKind targetKind,
            out Vector2 position)
        {
            position = Vector2.zero;
            if (origin == null || _layout == null || _layout.NodeRects == null)
                return false;
            Rect source;
            if (!_layout.NodeRects.TryGetValue(origin, out source)) return false;

            Vector2 size = targetKind == StoryGraphEditNodeKind.Option
                ? new Vector2(225f, 82f)
                : new Vector2(250f, 96f);
            float pitchY = size.y + StoryGraphMetrics.RowGap;
            for (int column = 1; column <= 8; column++)
            {
                float x = Mathf.Clamp(
                    source.x + StoryGraphMetrics.ColumnGap * column,
                    StoryGraphMetrics.Margin, 1000000f - size.x);
                for (int slot = 0; slot < 17; slot++)
                {
                    int distance = (slot + 1) / 2;
                    float direction = slot == 0 ? 0f : (slot % 2 == 1 ? 1f : -1f);
                    float y = Mathf.Clamp(
                        source.y + direction * distance * pitchY,
                        StoryGraphMetrics.Margin, 1000000f - size.y);
                    var padded = new Rect(
                        x - 18f, y - 18f,
                        size.x + 36f, size.y + 36f);
                    bool occupied = _layout.NodeRects.Values.Any(
                        rect => padded.Overlaps(rect));
                    if (occupied) continue;
                    position = new Vector2(x, y);
                    return true;
                }
            }
            return false;
        }

        private void AddTalkFromToolbar()
        {
            EndInspectorLiveEdit();
            if (_editSession == null) return;
            TalkCfg near = SelectedEditTalk();
            if (near == null && _editSelectionKind == StoryGraphEditNodeKind.Option)
                near = _editSession.FindParentTalk(_editSelectionId);
            Vector2 position;
            bool hasPosition = TryRightSpawnPosition(
                _selectedNode, StoryGraphEditNodeKind.Talk, out position);
            TalkCfg created;
            string message;
            if (_editSession.TryAddTalk(near, out created, out message))
                PositionCreatedNode(
                    StoryGraphEditNodeKind.Talk, created.id,
                    position, hasPosition,
                    message + (hasPosition ? "；已放在当前节点右侧。" : string.Empty),
                    true);
            else SetEditFeedback(message, true);
        }

        private void AddOptionFromToolbar()
        {
            EndInspectorLiveEdit();
            if (_editSession == null) return;
            TalkCfg parent = SelectedOptionParentTalk();
            if (parent == null)
            {
                SetEditFeedback(
                    "先选择一条对话，再点击“新增选项”；选项不能脱离所属对话单独运行。",
                    false);
                return;
            }
            StoryGraphDisplayNode origin = DisplayNodeForTalk(parent) ?? _selectedNode;
            Vector2 menuPoint = _addOptionEditButton != null
                ? RectTransformUtility.WorldToScreenPoint(
                    null, ((RectTransform)_addOptionEditButton.transform)
                        .TransformPoint(new Vector3(
                            ((RectTransform)_addOptionEditButton.transform).rect.xMin,
                            ((RectTransform)_addOptionEditButton.transform).rect.yMin,
                            0f)))
                : _lastPointerScreen;
            CreateOptionFromTemplateAt(
                parent, origin, menuPoint,
                StoryGraphOptionTemplateKind.Direct,
                true, true);
        }

        private void DuplicateSelected()
        {
            EndInspectorLiveEdit();
            if (_editSession == null) return;
            if (_selectedNodes.Count > 1)
            {
                SetEditFeedback("当前选择了多个节点；请先保留一个节点再复制。", false);
                return;
            }
            string message;
            if (_editSelectionKind == StoryGraphEditNodeKind.Talk)
            {
                Vector2 position;
                bool hasPosition = TryRightSpawnPosition(
                    _selectedNode, StoryGraphEditNodeKind.Talk, out position);
                TalkCfg created;
                if (_editSession.TryDuplicateTalk(
                        SelectedEditTalk(), out created, out message))
                    PositionCreatedNode(
                        StoryGraphEditNodeKind.Talk, created.id,
                        position, hasPosition,
                        message + (hasPosition ? "；已放在原节点右侧。" : string.Empty),
                        true);
                else SetEditFeedback(message, true);
                return;
            }
            if (_editSelectionKind == StoryGraphEditNodeKind.Option)
            {
                OptionCfg source = SelectedEditOption();
                int sourceKey = source != null
                    ? _editSession.FindOptionKey(source)
                    : int.MinValue;
                TalkCfg parent = CurrentOptionLocateTalk();
                if (parent == null && sourceKey != int.MinValue)
                    parent = _editSession.FindParentTalk(sourceKey);
                StoryGraphDisplayNode origin = DisplayNodeForTalk(parent) ?? _selectedNode;
                Vector2 position;
                bool hasPosition = TryRightSpawnPosition(
                    origin, StoryGraphEditNodeKind.Option, out position);
                OptionCfg created;
                if (_editSession.TryDuplicateOption(
                        source, parent, out created, out message))
                    PositionCreatedNode(
                        StoryGraphEditNodeKind.Option, created.id,
                        position, hasPosition,
                        message + (hasPosition ? "；已放在原节点右侧。" : string.Empty),
                        true);
                else SetEditFeedback(message, true);
                return;
            }
            SetEditFeedback("请先选择要复制的真实对话或选项节点。", false);
        }

        private void RequestDeleteSelected()
        {
            EndInspectorLiveEdit();
            if (_editSession == null) return;
            if (!string.IsNullOrEmpty(_selectedGroupId))
            {
                RemoveWorkspaceGroup(_selectedGroupId);
                return;
            }
            List<TalkCfg> talks;
            List<OptionCfg> options;
            CollectSelectedConfigs(out talks, out options);
            int count = talks.Count + options.Count;
            if (count == 0)
            {
                SetEditFeedback("请先选择要删除的真实对话或选项节点。", false);
                return;
            }
            int sharedOptionReferences = 0;
            if (count == 1 && options.Count == 1)
            {
                int optionId = _editSession.FindOptionKey(options[0]);
                if (optionId != int.MinValue)
                    sharedOptionReferences =
                        _editSession.CountOptionReferences(optionId);
            }
            bool needsConfirmation = count > 1 || sharedOptionReferences > 1;
            // 普通单节点删除可撤销并立即执行；批量删除和共享 OptionCfg 会影响多处，
            // 必须明确二次确认，避免作者以为只删掉了眼前这一张“使用卡片”。
            if (needsConfirmation && Time.unscaledTime > _deleteConfirmUntil)
            {
                _deleteConfirmUntil = Time.unscaledTime + ConfirmSeconds;
                _discardConfirmUntil = 0f;
                _saveCgConfirmUntil = 0f;
                _editStatus = sharedOptionReferences > 1
                    ? "该选项配置被 " + sharedOptionReferences
                      + " 处引用。再次点击“删除”或再次按 Delete，才会从所有引用处移除。"
                    : "再次点击“删除”或再次按 Delete，才会批量删除所选 "
                      + count + " 个配置并安全断开引用。";
                UpdateEditControls();
                UpdateStatusBar();
                return;
            }

            LayoutPositionSnapshot preservedLayout =
                CaptureLayoutPositions();
            string message;
            bool changed = _editSession.TryDeleteMany(talks, options, out message);
            _deleteConfirmUntil = 0f;
            if (!changed)
            {
                SetEditFeedback(message, true);
                UpdateEditControls();
                return;
            }
            _editSelectionKind = StoryGraphEditNodeKind.None;
            _editSelectionId = 0;
            _editSelectionOrdinal = 0;
            _editSelectionStableKey = null;
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            RefreshEditGraph(StoryGraphEditNodeKind.None, 0, message, false,
                true, preservedLayout);
            string layoutError;
            if (TryPersistPreservedLayout(preservedLayout, out layoutError))
            {
                SetEditFeedback(message
                    + "；其余节点已保持原位，如需重新压缩空隙可使用“自动整理”。", false);
            }
            else
            {
                SetEditFeedback(message
                    + "；当前画面已保持原位，但布局坐标未能持久化："
                    + layoutError, true);
            }
        }

        private LayoutPositionSnapshot CaptureLayoutPositions()
        {
            if (_viewModel == null || _layout == null) return null;
            var snapshot = new LayoutPositionSnapshot();
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                Rect rect;
                if (node == null || !_layout.TryGetRect(node, out rect)) continue;
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key)
                    && !snapshot.VisiblePositions.ContainsKey(key))
                    snapshot.VisiblePositions.Add(key, rect.position);

                var record = new LayoutNodePosition
                {
                    Position = rect.position,
                    LocateTalk = node.SourceNode != null
                        ? node.SourceNode.LocateTalk
                        : node.LocateTalk,
                };
                EvtStoryGraphNode source = node.SourceNode;
                if (source != null)
                {
                    record.HasSource = true;
                    record.SourceKind = source.Kind;
                    record.SourceId = source.Id;
                    record.Talk = source.Talk;
                    record.Option = source.Option;
                }
                else if (node.IsSegment && node.SegmentNodes != null)
                {
                    record.SegmentTalks = node.SegmentNodes
                        .Where(item => item != null && item.Talk != null)
                        .Select(item => item.Talk)
                        .ToList();
                }
                snapshot.NodePositions.Add(record);

                if (!IsEditableLayoutNode(node)) continue;
                if (!string.IsNullOrEmpty(key)
                    && !snapshot.StablePositions.ContainsKey(key))
                    snapshot.StablePositions.Add(key, rect.position);
                if (source == null || source.Option == null) continue;
                List<Vector2> values;
                if (!snapshot.OptionPositions.TryGetValue(source.Id, out values))
                {
                    values = new List<Vector2>();
                    snapshot.OptionPositions.Add(source.Id, values);
                }
                values.Add(rect.position);
            }
            return snapshot.NodePositions.Count > 0 ? snapshot : null;
        }

        private bool TryPersistPreservedLayout(
            LayoutPositionSnapshot snapshot, out string error)
        {
            error = null;
            if (snapshot == null || snapshot.StablePositions.Count == 0) return true;
            if (_workspace == null || _viewModel == null || _layout == null)
            {
                error = "工作区布局尚未初始化";
                return false;
            }

            StoryGraphWorkspace.Snapshot before = _workspace.Capture();
            bool beforeDirty = _workspace.Dirty;
            foreach (KeyValuePair<string, Vector2> pair in snapshot.StablePositions)
                _workspace.SetPosition(pair.Key, pair.Value);
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                if (!IsEditableLayoutNode(node)) continue;
                Rect rect;
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key) && _layout.TryGetRect(node, out rect))
                    _workspace.SetPosition(key, rect.position);
            }

            if (_workspace.Save(out error)) return true;
            _workspace.Restore(before, beforeDirty);
            return false;
        }

        private void CollectSelectedConfigs(
            out List<TalkCfg> talks, out List<OptionCfg> options)
        {
            talks = new List<TalkCfg>();
            options = new List<OptionCfg>();
            IEnumerable<StoryGraphDisplayNode> nodes = _selectedNodes.Count > 0
                ? _selectedNodes
                : (_selectedNode != null
                    ? new[] { _selectedNode }
                    : Enumerable.Empty<StoryGraphDisplayNode>());
            foreach (StoryGraphDisplayNode node in nodes)
            {
                EvtStoryGraphNode source = node != null ? node.SourceNode : null;
                if (source == null) continue;
                if (source.Talk != null
                    && !talks.Any(item => ReferenceEquals(item, source.Talk)))
                    talks.Add(source.Talk);
                if (source.Option != null
                    && !options.Any(item => ReferenceEquals(item, source.Option)))
                    options.Add(source.Option);
            }
        }

        private void ClearEditHistories()
        {
            EndInspectorLiveEdit();
            _workspaceUndo.Clear();
            _workspaceRedo.Clear();
            _editActionTimeline.Clear();
            _editRedoTimeline.Clear();
        }

        private void UndoEdit()
        {
            EndInspectorLiveEdit();
            if (_editSession == null || _editActionTimeline.Count == 0)
            {
                SetEditFeedback("没有可撤销的操作。", false);
                return;
            }

            // 先 Peek，只有底层操作完整成功后才移动统一时间线。
            // 这样布局 Save 失败不会吞掉动作或制造栈错位。
            bool workspaceAction = _editActionTimeline.Peek();
            string message;
            if (workspaceAction)
            {
                if (_workspace == null || _workspaceUndo.Count == 0)
                {
                    SetEditFeedback("工作区撤销记录已失效。", true);
                    return;
                }
                StoryGraphWorkspace.Snapshot current = _workspace.Capture();
                bool currentDirty = _workspace.Dirty;
                _workspace.Restore(_workspaceUndo.Peek());
                string error;
                if (!_workspace.Save(out error))
                {
                    _workspace.Restore(current, currentDirty);
                    SetEditFeedback(error + "；撤销未生效。", true);
                    return;
                }
                _workspaceUndo.Pop();
                _workspaceRedo.Push(current);
                message = "已撤销节点布局操作。";
            }
            else if (!_editSession.TryUndo(out message))
            {
                SetEditFeedback(message, false);
                return;
            }

            _editActionTimeline.Pop();
            _editRedoTimeline.Push(workspaceAction);
            _editStatus = message;
            RefreshGraph(false);
            UpdateEditControls();
        }

        private void RedoEdit()
        {
            EndInspectorLiveEdit();
            if (_editSession == null || _editRedoTimeline.Count == 0)
            {
                SetEditFeedback("没有可重做的操作。", false);
                return;
            }

            bool workspaceAction = _editRedoTimeline.Peek();
            string message;
            if (workspaceAction)
            {
                if (_workspace == null || _workspaceRedo.Count == 0)
                {
                    SetEditFeedback("工作区重做记录已失效。", true);
                    return;
                }
                StoryGraphWorkspace.Snapshot current = _workspace.Capture();
                bool currentDirty = _workspace.Dirty;
                _workspace.Restore(_workspaceRedo.Peek());
                string error;
                if (!_workspace.Save(out error))
                {
                    _workspace.Restore(current, currentDirty);
                    SetEditFeedback(error + "；重做未生效。", true);
                    return;
                }
                _workspaceRedo.Pop();
                _workspaceUndo.Push(current);
                message = "已重做节点布局操作。";
            }
            else if (!_editSession.TryRedo(out message))
            {
                SetEditFeedback(message, false);
                return;
            }

            _editRedoTimeline.Pop();
            _editActionTimeline.Push(workspaceAction);
            _editStatus = message;
            RefreshGraph(false);
            UpdateEditControls();
        }

        private void SaveEditSession()
        {
            if (!_editMode || _editSession == null || _savingEdit) return;
            FinalizeFocusedInspectorInput();
            // screenEffect 与高级 JSON 都支持逐字热应用。保存前重建一次模型，
            // 确保 CG 路径诊断对应当前最后一个合法草稿值。
            RefreshGraph(false);
            if (!_editSession.Dirty)
            {
                SetEditFeedback("当前草稿没有需要保存的修改。", false);
                return;
            }

            string error;
            if (!_editSession.TryValidate(out error))
            {
                SetEditFeedback("无法保存：" + error, true);
                return;
            }

            // 可选 LaTeX 探针：行内烘焙 + 块级公式物化落在会话草稿上，写盘仍由
            // 本侧原子事务负责。探针返回非空即整体拦下（草稿与磁盘均未改，属合法中止）。
            if (StoryGraphLatexProbe.PrepareForSave != null)
            {
                string latexBlock = StoryGraphLatexProbe.PrepareForSave(_editSession);
                if (!string.IsNullOrEmpty(latexBlock))
                {
                    SetEditFeedback(latexBlock, true);
                    return;
                }
            }

            int unclosedCg = _model != null ? _model.CgUnclosedCount : 0;
            if (unclosedCg > 0
                && (_saveCgConfirmUntil <= 0f
                    || Time.unscaledTime > _saveCgConfirmUntil))
            {
                _saveCgConfirmUntil = Time.unscaledTime + ConfirmSeconds;
                _deleteConfirmUntil = 0f;
                _discardConfirmUntil = 0f;
                SetEditFeedback(
                    "检测到 " + unclosedCg
                    + " 个 CG/漫画开始节点存在会把画面带出当前剧情的分支"
                    + "（跳转其它事件/跨组或选项死端收尾）且未执行“关闭 CG（4017）”。"
                    + "请修正红色警告节点；若确实要让画面延续到后续剧情，"
                    + "请在 4 秒内再次点击“仍要保存”。", true);
                UpdateEditControls();
                return;
            }
            _saveCgConfirmUntil = 0f;
            _savingEdit = true;
            UpdateEditControls();
            try
            {
                // 先保存原编辑器内存快照，再把草稿应用到实时字段。只有内存同步成功
                // 才开始写盘；若写盘失败就恢复旧内存，杜绝“磁盘新、编辑器旧，随后
                // 原保存按钮又把旧数据覆盖回来”的状态。
                List<TalkCfg> originalTalks;
                Dictionary<int, OptionCfg> originalOptions;
                int originalEventId;
                List<int> originalEntries;
                bool originalEntriesKnown;
                if (!EvtStoryGraphViewAccess.TrySnapshot(
                        _view, out originalTalks, out originalOptions,
                        out originalEventId, out originalEntries,
                        out originalEntriesKnown, out error))
                {
                    SetEditFeedback("保存前读取原编辑器状态失败：" + error, true);
                    return;
                }
                TalkCfg originalCurrent = EvtStoryGraphViewAccess.GetCurrent(_view);
                int originalSelectId = originalCurrent != null ? originalCurrent.id : 0;

                int selectTalkId = _editSelectionKind == StoryGraphEditNodeKind.Talk
                    ? _editSelectionId
                    : 0;
                if (selectTalkId == 0 && _editSelectionKind == StoryGraphEditNodeKind.Option)
                {
                    TalkCfg parent = _editSession.FindParentTalk(_editSelectionId);
                    if (parent != null) selectTalkId = parent.id;
                }
                List<TalkCfg> liveTalks = StoryGraphEditSession.CloneTalks(
                    _editSession.Talks);
                Dictionary<int, OptionCfg> liveOptions =
                    StoryGraphEditSession.CloneOptions(_editSession.Options);
                // 借来显示的共享选项绝不能写回原编辑器内存：原版保存按钮会把
                // 内存选项全量写进 mod 的 OptionCfg.json，等于绕过剧情图保存
                // 管线的冻结过滤。原编辑器运行时会从 Cfg.OptionCfgMap 回退读取，
                // 显示不受影响。
                foreach (int frozenId in _editSession.FrozenBuiltInOptionIds)
                    liveOptions.Remove(frozenId);
                if (!EvtStoryGraphViewAccess.TryApplyDraft(
                        _view, liveTalks, liveOptions, selectTalkId, out error))
                {
                    SetEditFeedback(error, true);
                    return;
                }

                if (!StoryGraphEditPersistence.TrySave(_editSession, out error))
                {
                    string restoreError;
                    bool restored = EvtStoryGraphViewAccess.TryApplyDraft(
                        _view, originalTalks, originalOptions,
                        originalSelectId, out restoreError);
                    SetEditFeedback(error + (restored
                        ? "；原事件编辑器内存已恢复。"
                        : "；且恢复原编辑器内存失败，请立即关闭并重开该事件：" + restoreError), true);
                    return;
                }

                string workspaceCleanup = PruneSavedWorkspacePositions();
                _editSession.MarkSaved();
                _saveCgConfirmUntil = 0f;
                // 普通表单的删除持久化补丁以“最近一次已保存 ID”为基线；剧情图
                // 新增/删除成功后立即刷新，确保同一事件窗口里切回普通界面也能删除。
                ModEvtDeletePersistence.Capture(_view);
                ClearEditHistories();
                string message = "已保存 TalkCfg.json 与 OptionCfg.json，并同步回事件编辑器；"
                                 + "同时保留 .storygraph.bak 和崩溃恢复日志保护。"
                                 + "「预览本句」用的是最新内容、随时可看；"
                                 + "只有本局内正式触发该事件走的仍是启动时合并的旧数据，"
                                 + "重启游戏后生效。"
                                 + workspaceCleanup;
                _editStatus = message;
                RefreshGraph(false);
                UpdateEditControls();
                UpdateStatusBar();
                try
                {
                    StoryGraphToastRouter.Show(
                        "剧情图修改已保存。「预览本句」立即可看新内容；"
                        + "正式触发该事件需重启游戏后生效");
                }
                catch { }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StoryGraph.Edit.Save] " + e);
                SetEditFeedback("保存剧情图失败：" + e.Message, true);
            }
            finally
            {
                _savingEdit = false;
                UpdateEditControls();
            }
        }

        private string PruneSavedWorkspacePositions()
        {
            if (_workspace == null || _viewModel == null) return string.Empty;
            var validKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (StoryGraphDisplayNode node in _viewModel.Nodes)
            {
                if (node == null || node.SourceNode == null
                    || (node.SourceNode.Talk == null && node.SourceNode.Option == null))
                    continue;
                string key = StoryGraphWorkspace.StableNodeKey(node);
                if (!string.IsNullOrEmpty(key)) validKeys.Add(key);
            }

            StoryGraphWorkspace.Snapshot before = _workspace.Capture();
            bool beforeDirty = _workspace.Dirty;
            int removedPositions = _workspace.RemovePositionsExcept(validKeys);
            int removedGroups;
            int removedGroupKeys = _workspace.PruneGroupNodeKeys(
                validKeys, out removedGroups);
            if (removedPositions == 0 && removedGroupKeys == 0
                && removedGroups == 0) return string.Empty;
            string error;
            if (_workspace.Save(out error))
                return " 已清理 " + removedPositions + " 条旧布局坐标、"
                       + removedGroupKeys + " 条失效分组成员键和 "
                       + removedGroups + " 个空分组。";

            _workspace.Restore(before, beforeDirty);
            Plugin.Log?.LogWarning("[StoryGraph.Workspace.Prune] " + error);
            return " 注意：剧情已保存，但清理旧布局/分组信息失败；下次仍可继续使用：" + error;
        }

        private TalkCfg SelectedEditTalk()
        {
            return _editSession != null
                   && _editSelectionKind == StoryGraphEditNodeKind.Talk
                ? _editSession.FindTalk(_editSelectionId, _editSelectionOrdinal)
                : null;
        }

        /// <summary>
        /// “预览本句”只接受单个真实 Talk。多选时不能悄悄使用主选节点，
        /// 否则按钮、右键菜单和 P 快捷键会表现出三套不同语义。
        /// </summary>
        private TalkCfg SelectedEditPreviewTalk()
        {
            return _selectedNodes.Count <= 1 ? SelectedEditTalk() : null;
        }

        private OptionCfg SelectedEditOption()
        {
            return _editSession != null
                   && _editSelectionKind == StoryGraphEditNodeKind.Option
                ? _editSession.FindOption(_editSelectionId)
                : null;
        }

        private TalkCfg CurrentOptionLocateTalk()
        {
            if (_editSession == null || _selectedNode == null
                || _selectedNode.SourceNode == null) return null;
            TalkCfg locate = _selectedNode.SourceNode.LocateTalk;
            return locate != null && _editSession.Talks.Contains(locate)
                ? locate
                : null;
        }

        private StoryGraphDisplayNode DisplayNodeForTalk(TalkCfg talk)
        {
            return talk != null && _viewModel != null
                ? _viewModel.FindNodeForTalk(talk)
                : null;
        }

        private void RefreshEditGraph(
            StoryGraphEditNodeKind selectionKind,
            int selectionId,
            string message,
            bool focus,
            bool recordDataAction = true,
            LayoutPositionSnapshot preservedLayout = null)
        {
            _editSelectionKind = selectionKind;
            _editSelectionId = selectionId;
            _editSelectionStableKey = null;
            _selectedGroupId = null;
            // 数据命令会指定新的主选择；不要把重建前的多选键与新主节点合并，
            // 否则同一 OptionCfg 的其它使用节点会在刷新后意外变成多选。
            _selectedNodes.Clear();
            _selectedNodeKeys.Clear();
            // 新建节点和端口目标通常编号唯一；已有重复项由节点单击记录精确 ordinal。
            if (selectionKind != StoryGraphEditNodeKind.Talk)
                _editSelectionOrdinal = 0;
            else if (_editSession == null
                || _editSession.FindTalk(selectionId, _editSelectionOrdinal) == null)
                _editSelectionOrdinal = 0;
            _deleteConfirmUntil = 0f;
            _discardConfirmUntil = 0f;
            _saveCgConfirmUntil = 0f;
            _editStatus = message;
            if (recordDataAction)
            {
                _editActionTimeline.Push(false);
                _editRedoTimeline.Clear();
                _workspaceRedo.Clear();
            }
            RefreshGraph(false, preservedLayout);
            if (focus && _selectedNode != null)
                CenterOnNode(_selectedNode);
            UpdateEditControls();
            UpdateStatusBar();
        }

        private void SetEditFeedback(string message, bool toast)
        {
            _editStatus = message ?? string.Empty;
            UpdateStatusBar();
            if (toast)
            {
                try { StoryGraphToastRouter.Show(_editStatus); }
                catch { }
            }
        }

        private void UpdateEditControls()
        {
            bool available = _editMode && _editSession != null && !_savingEdit;
            bool nodeSelected = available
                && (_editSelectionKind == StoryGraphEditNodeKind.Talk
                    || _editSelectionKind == StoryGraphEditNodeKind.Option);
            bool groupSelected = available && !string.IsNullOrEmpty(_selectedGroupId);
            bool selected = nodeSelected || groupSelected;
            bool singleSelected = nodeSelected && _selectedNodes.Count <= 1;
            if (_addTalkEditButton != null) _addTalkEditButton.interactable = available;
            if (_addOptionEditButton != null) _addOptionEditButton.interactable = available;
            if (_duplicateEditButton != null) _duplicateEditButton.interactable = singleSelected;
            if (_deleteEditButton != null) _deleteEditButton.interactable = selected;
            if (_undoEditButton != null)
                _undoEditButton.interactable = available && _editActionTimeline.Count > 0;
            if (_redoEditButton != null)
                _redoEditButton.interactable = available && _editRedoTimeline.Count > 0;
            bool unsaved = available && HasUnsavedGraphChanges();
            if (_saveEditButton != null)
                _saveEditButton.interactable = unsaved;
            if (_discardEditButton != null)
                _discardEditButton.interactable = unsaved;
            SetButtonLabel(_deleteEditButton,
                Time.unscaledTime <= _deleteConfirmUntil ? "再次删除" : "删除");
            SetButtonLabel(_discardEditButton,
                Time.unscaledTime <= _discardConfirmUntil ? "再次放弃" : "放弃");
            SetButtonLabel(_saveEditButton, _savingEdit
                ? "保存中"
                : (_saveCgConfirmUntil > 0f
                   && Time.unscaledTime <= _saveCgConfirmUntil
                    ? "仍要保存"
                    : "保存"));
            UpdatePreviewControls();
        }

        private void UpdatePreviewControls()
        {
            if (_previewViewButton != null)
            {
                _previewViewButton.interactable = _open && !_editMode
                    && !_previewSuspended
                    && SelectedViewPreviewTalk() != null;
            }
            if (_previewEditButton != null)
            {
                _previewEditButton.interactable = _open && _editMode
                    && !_previewSuspended && _editSession != null
                    && !_savingEdit
                    && SelectedEditPreviewTalk() != null;
            }
        }

        private void UpdateEditConfirmationState()
        {
            bool changed = false;
            if (_deleteConfirmUntil > 0f && Time.unscaledTime > _deleteConfirmUntil)
            {
                _deleteConfirmUntil = 0f;
                changed = true;
            }
            if (_discardConfirmUntil > 0f && Time.unscaledTime > _discardConfirmUntil)
            {
                _discardConfirmUntil = 0f;
                changed = true;
            }
            if (_saveCgConfirmUntil > 0f && Time.unscaledTime > _saveCgConfirmUntil)
            {
                _saveCgConfirmUntil = 0f;
                changed = true;
            }
            if (changed) UpdateEditControls();
        }

        // ==================== 搜索 ====================

        private void OnSearchChanged(string text)
        {
            _searchText = text ?? string.Empty;
            UpdateSearch(false);
        }

        /// <summary>Id 精确命中优先，其次 SearchText 忽略大小写包含。</summary>
        private void UpdateSearch(bool focusFirst)
        {
            _matches.Clear();
            _matchIndex = -1;
            bool graphAvailable = _viewModel != null && _layout != null;
            if (_searchInput != null) _searchInput.interactable = graphAvailable;
            if (graphAvailable && !string.IsNullOrWhiteSpace(_searchText))
            {
                string query = _searchText.Trim();
                _matches.AddRange(_viewModel.Nodes
                    .Where(node => node != null
                        && (node.Id.ToString() == query
                            || (!string.IsNullOrEmpty(node.SearchText)
                                && node.SearchText.IndexOf(
                                    query, StringComparison.OrdinalIgnoreCase) >= 0)))
                    .OrderByDescending(node => node.Id.ToString() == query)
                    .ThenBy(node => node.Kind == StoryGraphDisplayNodeKind.Talk ? 0
                        : node.Kind == StoryGraphDisplayNodeKind.Option ? 1 : 2)
                    .ThenBy(node => node.Id)
                    .ThenBy(node => node.Key, StringComparer.Ordinal));
            }
            RefreshAllNodeStates();
            UpdateSearchNavigationControls();
            UpdateStatusBar();
            if (focusFirst && _matches.Count > 0) FocusMatch(1);
        }

        /// <summary>上个/下个循环定位；首次按「上个」跳到最后一个。</summary>
        private void FocusMatch(int direction)
        {
            if (_viewModel == null || _layout == null) return;
            if (_matches.Count == 0)
            {
                UpdateSearch(false);
                if (_matches.Count == 0) return;
            }
            if (_matchIndex < 0)
            {
                _matchIndex = direction >= 0 ? 0 : _matches.Count - 1;
            }
            else
            {
                int count = _matches.Count;
                _matchIndex = ((_matchIndex + direction) % count + count) % count;
            }

            StoryGraphDisplayNode node = _matches[_matchIndex];
            if (_zoom < SearchFocusMinZoom) ZoomTo(SearchFocusZoom); // 先放大再居中
            CenterOnNode(node);
            // 即使整张图小于视口、ClampPan 无需移动，也要给出明确可见反馈。
            _selectedNode = node;
            RefreshAllNodeStates();
            UpdateSearchNavigationControls();
            UpdateStatusBar();
        }

        private void UpdateSearchNavigationControls()
        {
            bool hasMatches = _viewModel != null && _layout != null
                              && _matches.Count > 0;
            bool hasMultiple = hasMatches && _matches.Count > 1;
            if (_previousMatchButton != null)
                _previousMatchButton.interactable = hasMultiple;
            if (_nextMatchButton != null)
                _nextMatchButton.interactable = hasMatches;
            SetButtonLabel(_previousMatchButton, "上个结果");
            SetButtonLabel(_nextMatchButton,
                hasMatches && !hasMultiple ? "定位结果" : "下个结果");
            if (_matchCounterText == null) return;

            if (string.IsNullOrWhiteSpace(_searchText))
                _matchCounterText.text = "输入关键词";
            else if (!hasMatches)
                _matchCounterText.text = "没有结果";
            else if (_matchIndex < 0)
                _matchCounterText.text = "共 " + _matches.Count + " 项";
            else
                _matchCounterText.text = (_matchIndex + 1) + " / " + _matches.Count;
        }

        private static void SetButtonLabel(Button button, string label)
        {
            if (button == null) return;
            Text text = button.GetComponentInChildren<Text>(true);
            if (text != null) text.text = label ?? string.Empty;
        }

        private void ClearSearchResultsForUnavailableGraph()
        {
            _matches.Clear();
            _matchIndex = -1;
            _currentNode = null;
            _selectedNode = null;
            if (_searchInput != null) _searchInput.interactable = false;
            UpdateSearchNavigationControls();
        }

        // ==================== 小地图 ====================

        /// <summary>布局变化时重画节点小点，并重算小地图映射参数。</summary>
        private void UpdateMinimapDots()
        {
            for (int i = 0; i < _activeDots.Count; i++)
            {
                _activeDots[i].gameObject.SetActive(false);
                _dotPool.Push(_activeDots[i]);
            }
            _activeDots.Clear();
            _mmScale = 0f;
            if (_minimapContent == null || _layout == null || _viewModel == null)
                return;

            Rect bounds = _layout.Bounds;
            if (bounds.width <= 0f || bounds.height <= 0f) return;
            _mmSize = _minimapContent.rect.size;
            if (_mmSize.x < 10f || _mmSize.y < 10f) return;
            _mmScale = Mathf.Min(_mmSize.x / bounds.width,
                _mmSize.y / bounds.height);
            _mmCenter = bounds.center;

            foreach (KeyValuePair<string, Rect> pair in _groupBounds)
            {
                StoryGraphWorkspace.GroupData group = _workspace != null
                    ? _workspace.FindGroup(pair.Key)
                    : null;
                if (group == null) continue;
                Image area = _dotPool.Count > 0
                    ? _dotPool.Pop()
                    : CreatePlainImage(_minimapContent, "GroupArea");
                area.transform.SetParent(_minimapContent, false);
                area.gameObject.SetActive(true);
                Color color = ParseGroupColor(group.Color);
                color.a = group.IsNote ? 0.34f : 0.20f;
                area.color = color;
                Vector2 mapped = MapToMinimap(pair.Value.center);
                Place(area.rectTransform, 0f, 1f, 0.5f, 0.5f,
                    mapped.x, -mapped.y,
                    Mathf.Max(3f, pair.Value.width * _mmScale),
                    Mathf.Max(3f, pair.Value.height * _mmScale));
                _activeDots.Add(area);
            }

            for (int i = 0; i < _viewModel.Nodes.Count; i++)
            {
                StoryGraphDisplayNode node = _viewModel.Nodes[i];
                Rect rect;
                if (node == null || !_layout.TryGetRect(node, out rect)) continue;
                Image dot = _dotPool.Count > 0
                    ? _dotPool.Pop()
                    : CreatePlainImage(_minimapContent, "Dot");
                dot.transform.SetParent(_minimapContent, false);
                dot.gameObject.SetActive(true);
                Color color = NodeBarColor(node); // 与左缘色条同语义，鸟瞰更好认
                color.a = 0.95f;
                dot.color = color;
                Vector2 mapped = MapToMinimap(
                    StoryGraphMetrics.NodeCardRect(node, rect).center);
                Place(dot.rectTransform, 0f, 1f, 0.5f, 0.5f,
                    mapped.x, -mapped.y, 4f, 4f);
                _activeDots.Add(dot);
            }
        }

        /// <summary>布局坐标（y 向下）→ 小地图内容区坐标（y 向下，左上原点）。</summary>
        private Vector2 MapToMinimap(Vector2 layoutPoint)
        {
            return new Vector2(
                (layoutPoint.x - _mmCenter.x) * _mmScale + _mmSize.x * 0.5f,
                (layoutPoint.y - _mmCenter.y) * _mmScale + _mmSize.y * 0.5f);
        }

        /// <summary>只更新小地图上的视口框（平移/缩放时调用，不重画小点）。</summary>
        private void UpdateMinimapFrame()
        {
            if (_minimapFrame == null) return;
            if (_layout == null || _viewport == null || _content == null
                || _mmScale <= 0f)
            {
                _minimapFrame.gameObject.SetActive(false);
                return;
            }
            Rect v = _viewport.rect;
            Vector2 c = _content.anchoredPosition;
            float z = _zoom;
            // 视口四角（视口局部 y 向上）→ 布局坐标（y 向下）
            Vector2 topLeft = new Vector2(
                (v.xMin - c.x) / z, (c.y - v.yMax) / z);
            Vector2 bottomRight = new Vector2(
                (v.xMax - c.x) / z, (c.y - v.yMin) / z);
            Vector2 a = MapToMinimap(topLeft);
            Vector2 b = MapToMinimap(bottomRight);

            // 视口范围可大于图本身，映射结果会落到小地图外；先裁进内容区，
            // 再保证至少 6px 可见，避免产生越界巨框或负尺寸。
            float left = Mathf.Clamp(Mathf.Min(a.x, b.x), 0f, _mmSize.x);
            float right = Mathf.Clamp(Mathf.Max(a.x, b.x), 0f, _mmSize.x);
            float top = Mathf.Clamp(Mathf.Min(a.y, b.y), 0f, _mmSize.y);
            float bottom = Mathf.Clamp(Mathf.Max(a.y, b.y), 0f, _mmSize.y);
            float frameWidth = Mathf.Min(_mmSize.x, Mathf.Max(6f, right - left));
            float frameHeight = Mathf.Min(_mmSize.y, Mathf.Max(6f, bottom - top));
            if (left + frameWidth > _mmSize.x) left = _mmSize.x - frameWidth;
            if (top + frameHeight > _mmSize.y) top = _mmSize.y - frameHeight;

            _minimapFrame.gameObject.SetActive(true);
            _minimapFrame.anchoredPosition = new Vector2(left, -top);
            _minimapFrame.sizeDelta = new Vector2(frameWidth, frameHeight);
        }

        /// <summary>小地图点击/拖动：映射回布局坐标并居中主视图。</summary>
        private void OnMinimapPointer(BaseEventData baseData)
        {
            var data = baseData as PointerEventData;
            if (data == null || _layout == null || _mmScale <= 0f
                || _minimapContent == null) return;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _minimapContent, data.position, null, out local)) return;
            // local y 向上 → 布局 y 向下
            Vector2 layoutPoint = new Vector2(
                _mmCenter.x + local.x / _mmScale,
                _mmCenter.y - local.y / _mmScale);
            CenterOnLayoutPoint(layoutPoint);
        }

        // ==================== 配色与文案 ====================

        private static Color NodeBaseFill(StoryGraphDisplayNode node)
        {
            Color color;
            switch (node.Kind)
            {
                case StoryGraphDisplayNodeKind.Segment:
                    color = SegmentFill; break;      // 链段：暖黄纸签
                case StoryGraphDisplayNodeKind.Option:
                    color = OptionFill; break;       // 选项：驼色浅底，与 Talk 奶油底一眼可辨
                case StoryGraphDisplayNodeKind.UnusedGroup:
                    color = UnusedFill; break;       // 未使用：灰
                default:
                    color = PanelBg; break;          // 普通节点：纸卡
            }
            if (node.HasFlag(EvtStoryGraphNodeFlags.Unreachable))
                color = Color.Lerp(color, UnusedFill, 0.55f);
            return color;
        }

        private static Color NodeBaseBorder(StoryGraphDisplayNode node)
        {
            // 旗标语义已移到左缘色条；边框只保留选项/链段/默认三档。
            if (node.Kind == StoryGraphDisplayNodeKind.Option
                || node.Kind == StoryGraphDisplayNodeKind.Segment)
                return AdjacentBorderColor;
            return PanelBorder;
        }

        /// <summary>左缘语义色条：承接旧版边框配色的旗标优先级。</summary>
        private static Color NodeBarColor(StoryGraphDisplayNode node)
        {
            if (node.HasFlag(EvtStoryGraphNodeFlags.SelfLoop)
                || node.HasFlag(EvtStoryGraphNodeFlags.MissingReference)
                || node.HasFlag(EvtStoryGraphNodeFlags.InvalidData)
                || node.HasFlag(EvtStoryGraphNodeFlags.CgNotClosed))
                return HexColor("C10000");           // 危险红
            if (node.HasFlag(EvtStoryGraphNodeFlags.Cycle))
                return AccentOrange;                 // 环：橙
            if (node.HasFlag(EvtStoryGraphNodeFlags.Entry))
                return HexColor("497B36");           // 入口：绿
            if (node.HasFlag(EvtStoryGraphNodeFlags.Terminal))
                return HexColor("774AB6");           // 终点：紫
            if (node.HasFlag(EvtStoryGraphNodeFlags.External))
                return HexColor("1883AB");           // 外部：蓝
            if (node.Kind == StoryGraphDisplayNodeKind.Option
                || node.Kind == StoryGraphDisplayNodeKind.Segment)
                return AdjacentBorderColor;          // 选项/链段：暖棕
            return PanelBorder;
        }

        private static Color EdgeBaseColor(StoryGraphDisplayEdge edge)
        {
            if ((edge.Flags & EvtStoryGraphEdgeFlags.RuntimeIgnored) != 0)
            {
                Color ignored = EdgeIgnoredColor;
                ignored.a = 0.8f;
                return ignored;
            }
            if ((edge.Flags & EvtStoryGraphEdgeFlags.BrokenReference) != 0)
            {
                Color broken = HexColor("C10000");
                broken.a = 0.95f;
                return broken;
            }
            // 不再按 Kind 细分，活跃边统一暖灰棕
            Color active = EdgeActiveColor;
            active.a = 0.95f;
            return active;
        }

        private string SegmentTitle(StoryGraphDisplayNode node)
        {
            int count = node != null && node.SegmentNodes != null
                ? node.SegmentNodes.Count
                : 0;
            return "▶ " + count + " 句对话（点击展开）";
        }

        private static string ExpandedSegmentToggleTitle(int count)
        {
            return "▼ " + count + " 句对话（点击收起）";
        }

        private static string BuildNodeTitle(StoryGraphDisplayNode node)
        {
            if (node == null) return string.Empty;
            string title = node.Title ?? string.Empty;
            if (node.HasFlag(EvtStoryGraphNodeFlags.Entry)) title += "　[入口]";
            if (node.HasFlag(EvtStoryGraphNodeFlags.Terminal))
            {
                bool directTalkEnd = node.SourceNode != null
                                     && node.SourceNode.Talk != null;
                title += directTalkEnd ? "　[直接结束]" : "　[结束]";
            }
            if (node.HasFlag(EvtStoryGraphNodeFlags.CgNotClosed))
                title += "　[CG未关闭]";
            return title;
        }

        /// <summary>tooltip 用的标志位中文说明，每行一条。</summary>
        private static string BuildFlagDescriptions(StoryGraphDisplayNode node)
        {
            if (node == null) return string.Empty;
            var lines = new List<string>();
            if (node.HasFlag(EvtStoryGraphNodeFlags.Entry))
                lines.Add("剧情入口：游戏会从这句开始播放");
            if (node.HasFlag(EvtStoryGraphNodeFlags.Terminal))
            {
                if (node.SourceNode != null && node.SourceNode.Talk != null)
                    lines.Add("直接结束：正文播完后再次点击会直接退出剧情；"
                              + "若要显示“确定”按钮，请右击节点选择“设为结尾”");
                else if (node.SourceNode != null && node.SourceNode.Option != null)
                    lines.Add("结束选项：玩家点击这个选项后退出剧情");
                else
                    lines.Add("剧情结束：执行到这里后不再跳到其它节点");
            }
            if (node.HasFlag(EvtStoryGraphNodeFlags.Unreachable))
                lines.Add("无法到达：从剧情入口按游戏实际分支走不到这里");
            if (node.HasFlag(EvtStoryGraphNodeFlags.SelfLoop))
                lines.Add("自我循环：执行后会直接跳回自身");
            else if (node.HasFlag(EvtStoryGraphNodeFlags.Cycle))
                lines.Add("循环路径：该节点位于会绕回前文的剧情路径中");
            if (node.HasFlag(EvtStoryGraphNodeFlags.MissingReference))
                lines.Add("缺失引用：当前事件数据中找不到被引用的配置");
            if (node.HasFlag(EvtStoryGraphNodeFlags.External))
                lines.Add("跨组跳转：目标不在当前事件已加载的剧情组中");
            if (node.HasFlag(EvtStoryGraphNodeFlags.DuplicateId))
                lines.Add("编号重复：存在多个相同 ID，保存后后面的配置会覆盖前面的配置");
            if (node.HasFlag(EvtStoryGraphNodeFlags.OrphanOption))
                lines.Add("未被使用的选项：没有任何事件或对话引用该选项");
            if (node.HasFlag(EvtStoryGraphNodeFlags.SharedOption))
                lines.Add("共享选项配置：修改或删除它会同时影响所有引用这条 OptionCfg 的对话");
            if (node.HasFlag(EvtStoryGraphNodeFlags.InvalidData))
                lines.Add("配置异常：字段值无效，或字段组合不会按预期执行");
            int authorWarningCount = 0;
            if (node.SourceNode != null)
            {
                foreach (string warning in node.SourceNode.AuthorWarnings)
                {
                    if (string.IsNullOrWhiteSpace(warning)
                        || lines.Contains(warning)) continue;
                    lines.Add(warning);
                    authorWarningCount++;
                }
            }
            if (node.HasFlag(EvtStoryGraphNodeFlags.CgNotClosed)
                && authorWarningCount == 0)
                lines.Add("CG 未关闭：至少一条可达分支在跳出当前剧情图"
                          + "或选项收尾前没有执行关闭 CG（4017）");
            return string.Join("\n", lines.ToArray());
        }

        private void UpdateStatusBar()
        {
            if (_statusText == null) return;
            string text;
            if (_model != null)
            {
                text = _model.Summary;
                if (_model.Diagnostics.Count > 0)
                    text += "　⚠ " + _model.Diagnostics[0];
            }
            else
            {
                text = _error ?? "无数据";
            }
            bool graphAvailable = _model != null && _layout != null;
            if (_editMode)
            {
                string dirty = _editSession != null && _editSession.Dirty ? " *未保存" : "";
                text = "[编辑草稿" + dirty + "]　" + text;
                if (!string.IsNullOrWhiteSpace(_editStatus))
                    text += "　｜　" + _editStatus;
                _statusText.text = Truncate(text, 190);
                return;
            }
            if (graphAvailable && _matches.Count > 0)
            {
                if (_matchIndex >= 0 && _matchIndex < _matches.Count)
                {
                    StoryGraphDisplayNode current = _matches[_matchIndex];
                    text += "　｜　搜索结果 " + (_matchIndex + 1) + "/"
                            + _matches.Count + "：" + BuildNodeTitle(current);
                }
                else
                {
                    text += "　｜　找到 " + _matches.Count
                            + " 个结果；按回车或“下个结果”定位";
                }
            }
            else if (graphAvailable && !string.IsNullOrWhiteSpace(_searchText))
            {
                text += "　｜　没有找到符合“"
                        + Truncate(_searchText.Trim(), 18) + "”的节点";
            }
            if (graphAvailable)
                text += "　｜　P：预览所选对话　｜　右键：视图操作";
            _statusText.text = Truncate(text, 150);
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
                return value ?? string.Empty;
            return value.Substring(0, max) + "…";
        }
    }
}
