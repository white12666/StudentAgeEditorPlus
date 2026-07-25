using System.Collections.Generic;
using Config;
using UnityEngine;

namespace StudentAgeEditorPlus.Patches
{
    // ============================================================================
    // 剧情图 UGUI 重做 · 三层共享契约（本文件由架构方统一维护，实现方只读不改）
    //
    // 三层分工：
    //   StoryGraphViewModel.cs    —— 折叠层：EvtStoryGraphModel → 显示图（链段折叠/展开）
    //   StoryGraphLayoutEngine.cs —— 布局层：显示图 → 节点矩形 + 连线路径
    //   StoryGraphWindow.cs       —— 显示层：UGUI 窗口（画布/节点/连线/小地图/搜索/交互）
    //
    // 约定：
    //  - 链段块 Key = "segment:" + 段内首节点的模型 Key；展开状态按此 Key 记忆；
    //  - “未使用配置”折叠区块 Key = "unused"，IsUnusedGroup = true；
    //  - 所有显示层坐标均为画布本地坐标（像素），x 向右、y 向下；
    //  - 窗口导航助手（在 EvtStoryGraphPatch.cs 中保留实现）：
    //      internal static class EvtStoryGraphNavigation
    //      { internal static void SelectTalkAndScroll(ModEvtEditView view, TalkCfg talk); }
    // ============================================================================

    /// <summary>显示图节点种类。Segment=折叠的链段块；UnusedGroup=未使用配置折叠区。</summary>
    internal enum StoryGraphDisplayNodeKind
    {
        Event,
        Talk,
        Option,
        Missing,
        External,
        Segment,
        UnusedGroup,
    }

    /// <summary>显示图节点：普通节点包一个模型节点；链段块含有序成员列表。</summary>
    internal sealed class StoryGraphDisplayNode
    {
        internal string Key;
        internal StoryGraphDisplayNodeKind Kind;
        internal EvtStoryGraphNodeFlags Flags;
        internal int Id;
        internal string Title;
        internal string Subtitle;
        internal string SearchText;

        /// <summary>普通节点指向对应模型节点；链段块/折叠区为 null。</summary>
        internal EvtStoryGraphNode SourceNode;

        /// <summary>链段块：段内模型节点（剧情顺序）；非链段为 null。</summary>
        internal List<EvtStoryGraphNode> SegmentNodes;

        /// <summary>双击定位用的 TalkCfg；链段块取首节点的 Talk；可为 null。</summary>
        internal TalkCfg LocateTalk;

        /// <summary>true 表示这是“未使用配置”折叠区块（Kind 同时为 UnusedGroup）。</summary>
        internal bool IsUnusedGroup;

        /// <summary>
        /// 展开链段首句的内联收起元数据。它仍是普通 Talk 节点；这两个字段只让
        /// 布局层为卡片下方的操作带预留空间，不会创建新的图节点或图边。
        /// </summary>
        internal string ExpandedSegmentKey;
        internal int ExpandedSegmentCount;

        internal readonly List<StoryGraphDisplayEdge> Incoming = new List<StoryGraphDisplayEdge>();
        internal readonly List<StoryGraphDisplayEdge> Outgoing = new List<StoryGraphDisplayEdge>();

        internal bool IsSegment
        {
            get { return SegmentNodes != null && SegmentNodes.Count > 0; }
        }

        internal bool HasExpandedSegmentControl
        {
            get
            {
                return Kind == StoryGraphDisplayNodeKind.Talk
                    && ExpandedSegmentCount > 0
                    && !string.IsNullOrEmpty(ExpandedSegmentKey);
            }
        }

        internal bool HasFlag(EvtStoryGraphNodeFlags flag)
        {
            return (Flags & flag) != 0;
        }
    }

    /// <summary>显示图边：透传模型边的语义，端点为显示节点。</summary>
    internal sealed class StoryGraphDisplayEdge
    {
        internal StoryGraphDisplayNode From;
        internal StoryGraphDisplayNode To;
        internal EvtStoryGraphEdgeKind Kind;
        internal EvtStoryGraphEdgeFlags Flags;
        internal string Label;

        internal bool IsRuntimeEdge
        {
            get { return (Flags & EvtStoryGraphEdgeFlags.RuntimeIgnored) == 0; }
        }
    }

    /// <summary>布局层输出的一条已布线边：正交折线途经点（画布坐标）+ 标签位置。</summary>
    internal sealed class StoryGraphRoutedEdge
    {
        internal StoryGraphDisplayEdge Edge;
        internal List<Vector2> Points;
        internal Vector2 LabelPos;
    }

    /// <summary>布局层输出：节点矩形表 + 已布线边 + 画布外接矩形（含回边通道）。</summary>
    internal sealed class StoryGraphLayoutResult
    {
        internal Dictionary<StoryGraphDisplayNode, Rect> NodeRects;
        internal List<StoryGraphRoutedEdge> Edges;
        internal Rect Bounds;

        internal bool TryGetRect(StoryGraphDisplayNode node, out Rect rect)
        {
            if (NodeRects != null && node != null)
            {
                return NodeRects.TryGetValue(node, out rect);
            }
            rect = default(Rect);
            return false;
        }
    }

    /// <summary>布局与显示共用的尺寸常量（唯一来源，两边不得各自另写）。</summary>
    internal static class StoryGraphMetrics
    {
        internal const float Margin = 90f;          // 画布四周留白
        internal const float ColumnGap = 330f;      // 相邻列中心距
        internal const float RowGap = 46f;          // 同列节点垂直间距
        internal const float BackEdgeChannelGap = 26f; // 回边顶部通道每档高度
        internal const float ComponentGapY = 120f;  // 独立分量（展开后的未使用区）之间的垂直间隔
        internal const float TalkCardHeight = 96f;
        internal const float SegmentControlHeight = 34f;
        internal const float SegmentControlGap = 6f;
        internal const float SegmentControlReserve =
            SegmentControlHeight + SegmentControlGap;
        internal const float MinimumRowGap = 6f;

        internal static Vector2 NodeSize(StoryGraphDisplayNode node)
        {
            switch (node.Kind)
            {
                case StoryGraphDisplayNodeKind.Event: return new Vector2(190f, 72f);
                case StoryGraphDisplayNodeKind.Talk:
                    return new Vector2(250f, TalkCardHeight
                        + (node.HasExpandedSegmentControl
                            ? SegmentControlReserve : 0f));
                case StoryGraphDisplayNodeKind.Option: return new Vector2(225f, 82f);
                case StoryGraphDisplayNodeKind.Segment: return new Vector2(260f, 96f);
                case StoryGraphDisplayNodeKind.UnusedGroup: return new Vector2(240f, 64f);
                default: return new Vector2(205f, 70f); // Missing / External
            }
        }

        /// <summary>
        /// 布局矩形包含卡片下方的链段操作带；连线与卡片绘制只使用这里返回的
        /// 真实卡片矩形，避免连接点被辅助控件向下拉偏。
        /// </summary>
        internal static Rect NodeCardRect(
            StoryGraphDisplayNode node, Rect layoutRect)
        {
            if (node != null && node.HasExpandedSegmentControl)
                layoutRect.height = Mathf.Max(
                    0f, layoutRect.height - SegmentControlReserve);
            return layoutRect;
        }

        /// <summary>
        /// 展开链段的旧式收起卡片占用原本的行间空白；只额外保留最小安全间距，
        /// 避免因为操作卡变高而把同列分支整体大幅推开。
        /// </summary>
        internal static float RowGapAfter(StoryGraphDisplayNode node)
        {
            return node != null && node.HasExpandedSegmentControl
                ? Mathf.Max(MinimumRowGap, RowGap - SegmentControlReserve)
                : RowGap;
        }
    }

    // ============================================================================
    // 各实现文件必须暴露的 API（签名冻结，实现方不得更改）：
    //
    // 【StoryGraphViewModel.cs】
    //   internal sealed class StoryGraphViewModel
    //   {
    //       internal static StoryGraphViewModel Build(EvtStoryGraphModel model);
    //       internal EvtStoryGraphModel Model { get; }
    //       internal StoryGraphDisplayNode RootNode { get; }
    //       internal List<StoryGraphDisplayNode> Nodes { get; }    // 当前折叠状态下的显示节点
    //       internal List<StoryGraphDisplayEdge> Edges { get; }
    //       internal bool IsExpanded(string segmentKey);
    //       internal void ToggleSegment(string segmentKey);        // 展开/折叠并重建 Nodes/Edges
    //       internal StoryGraphDisplayNode FindNodeForTalk(TalkCfg talk); // 找不到返回 null
    //       internal StoryGraphDisplayNode FindNodeForOption(
    //           OptionCfg option, TalkCfg preferredParent);         // 共享选项按父对话消歧
    //   }
    //
    // 【StoryGraphLayoutEngine.cs】
    //   internal static class StoryGraphLayoutEngine
    //   {
    //       internal static StoryGraphLayoutResult Layout(
    //           List<StoryGraphDisplayNode> nodes,
    //           List<StoryGraphDisplayEdge> edges,
    //           StoryGraphDisplayNode root);
    //   }
    //
    // 【StoryGraphWindow.cs】
    //   internal sealed class StoryGraphWindow : MonoBehaviour
    //   {
    //       internal void Bind(ModEvtEditView view);   // 由 EvtStoryGraphInitPatch 调用
    //       internal bool IsOpen { get; }
    //       internal void Toggle();
    //       internal void Open();
    //       internal void Close();
    //       internal void ResetForViewOpen();          // 由 EvtStoryGraphOpenPatch 调用
    //   }
    // ============================================================================
}
