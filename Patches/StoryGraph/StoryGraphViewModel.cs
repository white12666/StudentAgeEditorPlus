using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Config;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图 · 折叠层（三层之一）：把 <see cref="EvtStoryGraphModel"/> 按契约规则
    /// 转换为当前折叠状态下的显示图（StoryGraphDisplayNode / StoryGraphDisplayEdge）。
    ///
    /// 折叠规则（从严白名单）：
    ///  1) 链段：模型节点可进链段 ⇔ Kind==Talk 且 Flags==None，
    ///     且恰好 1 条活跃入边（种类 ∈ EventEntry/NextTalk/NextTalk2）
    ///     与恰好 1 条活跃出边（种类 ∈ NextTalk/NextTalk2）。
    ///     “活跃” = 运行时实际会走（IsRuntimeEdge，即非 RuntimeIgnored）；
    ///     RuntimeFallback 不是 RuntimeIgnored，仍算活跃，因此条件分支、
    ///     选项遮蔽、小游戏分流的 Talk 都会因活跃出边 ≠1 而被排除。
    ///     极大连续可折序列长度 ≥2 才合并为 Segment 块；长度 1 一律不折。
    ///  2) 未使用配置：带 Unreachable / OrphanOption 标志的 Talk/Option/Missing
    ///     类节点默认收进 Key="unused" 的 UnusedGroup 块；External 节点一律不收。
    ///     这些节点必然 Flags!=None，与链段白名单天然互斥，不会双重归类。
    ///  3) 可见边：模型边映射到可见节点；块内边隐藏。布局边另保留完整逐句
    ///     骨架，折叠成员除首句外变成不可见 Talk 占位，因此折叠不会改变任何
    ///     后续节点的列、同层槽位或分支排序。
    ///
    /// 展开状态用 HashSet&lt;string&gt; 记忆（链段 Key 使用首成员模型 Key 去掉
    /// 每次重建都会变化的尾部 serial；未使用区 Key = "unused"），Build 默认全折叠。ToggleSegment 翻转后立即
    /// 重建 Nodes/Edges 为全新实例——布局/窗口层不得缓存旧的显示节点引用。
    ///
    /// 链段展开后只显示真实成员与真实边，不再生成参与可见拓扑的“收起把手”或
    /// 合成边；折叠态的布局占位仅存在于 LayoutNodes/LayoutEdges，从不交给窗口绘制。
    /// 首句真实 Talk 携带操作带元数据，布局层在卡片下方预留固定空间，查看层显示
    /// “点击收起”，编辑层隐藏文字但保留相同占位。这样模式切换不会增加一列、
    /// 制造回形连线，操作带也不会覆盖真实对话卡片。
    /// </summary>
    internal sealed class StoryGraphViewModel
    {
        // 契约规定的固定标识。
        private const string UnusedGroupKey = "unused";
        private const string SegmentKeyPrefix = "segment:";

        private EvtStoryGraphModel _model;
        private StoryGraphDisplayNode _root;
        private List<StoryGraphDisplayNode> _nodes = new List<StoryGraphDisplayNode>();
        private List<StoryGraphDisplayEdge> _edges = new List<StoryGraphDisplayEdge>();
        private List<StoryGraphDisplayNode> _layoutNodes =
            new List<StoryGraphDisplayNode>();
        private List<StoryGraphDisplayEdge> _layoutEdges =
            new List<StoryGraphDisplayEdge>();

        // —— Analyze 产物：只与模型有关，Build 一次终身有效，不随展开状态变化 ——
        private readonly List<List<EvtStoryGraphNode>> _chains =
            new List<List<EvtStoryGraphNode>>();                       // 只收长度≥2 的极大链
        private readonly List<string> _chainKeys = new List<string>(); // 与 _chains 同下标
        private readonly HashSet<string> _segmentKeys = new HashSet<string>();
        private readonly Dictionary<EvtStoryGraphNode, int> _chainByMember =
            new Dictionary<EvtStoryGraphNode, int>(ReferenceComparer<EvtStoryGraphNode>.Instance);
        private readonly List<EvtStoryGraphNode> _unusedMembers =
            new List<EvtStoryGraphNode>();                             // 模型顺序，稳定
        private readonly HashSet<EvtStoryGraphNode> _unusedSet =
            new HashSet<EvtStoryGraphNode>(ReferenceComparer<EvtStoryGraphNode>.Instance);
        private readonly Dictionary<TalkCfg, EvtStoryGraphNode> _nodeByTalk =
            new Dictionary<TalkCfg, EvtStoryGraphNode>(ReferenceComparer<TalkCfg>.Instance);

        // —— 运行状态 ——
        private readonly HashSet<string> _expandedKeys = new HashSet<string>(); // 默认全折叠
        private Dictionary<EvtStoryGraphNode, StoryGraphDisplayNode> _displayByModel =
            new Dictionary<EvtStoryGraphNode, StoryGraphDisplayNode>(
                ReferenceComparer<EvtStoryGraphNode>.Instance);

        // ====================================================================
        // 契约 API
        // ====================================================================

        internal static StoryGraphViewModel Build(EvtStoryGraphModel model)
        {
            var vm = new StoryGraphViewModel { _model = model };
            vm.Analyze();
            vm.Rebuild();
            return vm;
        }

        internal EvtStoryGraphModel Model
        {
            get { return _model; }
        }

        internal StoryGraphDisplayNode RootNode
        {
            get { return _root; }
        }

        internal List<StoryGraphDisplayNode> Nodes
        {
            get { return _nodes; }
        }

        internal List<StoryGraphDisplayEdge> Edges
        {
            get { return _edges; }
        }

        /// <summary>
        /// 布局专用投影。折叠链段仍为被隐藏的成员保留不可见占位和真实约束边，
        /// 窗口只绘制 Nodes/Edges，因此这些对象不会进入搜索、交互或剧情显示。
        /// </summary>
        internal List<StoryGraphDisplayNode> LayoutNodes
        {
            get { return _layoutNodes; }
        }

        internal List<StoryGraphDisplayEdge> LayoutEdges
        {
            get { return _layoutEdges; }
        }

        /// <summary>
        /// 未使用配置成员（模型顺序，只读视图）。
        /// 显示层悬浮框用它列成员 ID 预览；不改变任何契约冻结签名，纯增量。
        /// </summary>
        internal IReadOnlyList<EvtStoryGraphNode> UnusedMembers
        {
            get { return _unusedMembers; }
        }

        internal bool IsExpanded(string segmentKey)
        {
            return segmentKey != null && _expandedKeys.Contains(segmentKey);
        }

        /// <summary>返回当前模型中的稳定链段 Key 快照，供窗口跨模式保留展开状态。</summary>
        internal List<string> SnapshotSegmentKeys()
        {
            return new List<string>(_chainKeys);
        }

        /// <summary>
        /// 展开态不再创建合成段块；查看层可据此在首句真实卡片内显示收起控件。
        /// </summary>
        internal bool TryGetExpandedSegmentHead(
            StoryGraphDisplayNode display, out string segmentKey, out int count)
        {
            segmentKey = null;
            count = 0;
            if (display == null || !display.HasExpandedSegmentControl)
                return false;
            segmentKey = display.ExpandedSegmentKey;
            count = display.ExpandedSegmentCount;
            return true;
        }

        internal void ToggleSegment(string segmentKey)
        {
            if (string.IsNullOrEmpty(segmentKey)) return;
            // 只接受真实存在的折叠块 Key，防止调用方传入脏数据污染展开状态：
            // 要么是有成员的 "unused"，要么是某条长度≥2 链的段 Key。
            bool isUnusedKey = segmentKey == UnusedGroupKey;
            if (isUnusedKey)
            {
                if (_unusedMembers.Count == 0) return;
            }
            else if (!_segmentKeys.Contains(segmentKey))
            {
                return;
            }

            if (!_expandedKeys.Add(segmentKey)) _expandedKeys.Remove(segmentKey);
            Rebuild();
        }

        /// <summary>
        /// 编辑模式只展示可实际修改的配置节点。链段/未使用区全部展开；显示图中
        /// 只有真实成员与真实边。首句仍保留操作带占位以稳定模式切换，但不会显示
        /// 控件，也不会产生可选择、可持久化的辅助节点。
        /// </summary>
        internal void ExpandAllForEditing()
        {
            foreach (string key in _segmentKeys) _expandedKeys.Add(key);
            if (_unusedMembers.Count > 0) _expandedKeys.Add(UnusedGroupKey);
            Rebuild();
        }

        internal StoryGraphDisplayNode FindNodeForTalk(TalkCfg talk)
        {
            if (talk == null) return null;
            // TalkCfg → 模型节点 → 当前显示节点。折叠态下链段成员/未使用组成员
            // 在 _displayByModel 中分别指向段块/未使用块；展开态下链段成员指向
            // 各自的成员节点，一次查表即满足
            // “SourceNode.Talk、链段成员、未使用组成员”三种来源的定位要求。
            EvtStoryGraphNode modelNode;
            if (!_nodeByTalk.TryGetValue(talk, out modelNode) || modelNode == null)
                return null;
            StoryGraphDisplayNode display;
            return _displayByModel.TryGetValue(modelNode, out display) ? display : null;
        }

        /// <summary>
        /// 按原版选项编辑弹窗里的 OptionCfg 定位当前显示节点。共享选项会为每个
        /// 父 Talk 生成独立使用节点，因此优先匹配 preferredParent；折叠态下仍由
        /// _displayByModel 返回未使用区块，调用方可先展开区块再重新查询。
        /// </summary>
        internal StoryGraphDisplayNode FindNodeForOption(
            OptionCfg option, TalkCfg preferredParent)
        {
            if (option == null || _model == null || _model.Nodes == null)
                return null;

            EvtStoryGraphNode best = null;
            int bestScore = 0;
            for (int i = 0; i < _model.Nodes.Count; i++)
            {
                EvtStoryGraphNode node = _model.Nodes[i];
                if (node == null || node.Kind != EvtStoryGraphNodeKind.Option
                    || node.Option == null) continue;

                bool sameObject = ReferenceEquals(node.Option, option);
                bool sameId = node.Option.id == option.id;
                if (!sameObject && !sameId) continue;

                int parentScore = 0;
                if (preferredParent != null && node.LocateTalk != null)
                {
                    parentScore = ReferenceEquals(
                        node.LocateTalk, preferredParent)
                        ? 2
                        : (node.LocateTalk.id == preferredParent.id ? 1 : 0);
                }

                // 对象引用比可编辑 ID 更可靠；父 Talk 的对象引用还能在重复
                // Talk ID 的异常草稿中区分共享选项的具体使用节点。
                int score = (sameObject ? 8 : 4) + parentScore;
                if (score <= bestScore) continue;
                best = node;
                bestScore = score;
            }

            StoryGraphDisplayNode display;
            return best != null && _displayByModel.TryGetValue(best, out display)
                ? display
                : null;
        }

        // ====================================================================
        // 预处理：分类链段 / 未使用组（与展开状态无关）
        // ====================================================================

        private void Analyze()
        {
            // 防御：空模型 / 节点列表缺失时保持空图，不抛异常。
            if (_model == null || _model.Nodes == null) return;

            var byRef = ReferenceComparer<EvtStoryGraphNode>.Instance;
            var foldable = new HashSet<EvtStoryGraphNode>(byRef);
            var activeIn = new Dictionary<EvtStoryGraphNode, EvtStoryGraphEdge>(byRef);
            var activeOut = new Dictionary<EvtStoryGraphNode, EvtStoryGraphEdge>(byRef);
            var foldableInOrder = new List<EvtStoryGraphNode>();

            foreach (EvtStoryGraphNode node in _model.Nodes)
            {
                if (node == null) continue;

                // FindNodeForTalk 用的对象索引；同一 TalkCfg 对象模型已按对象去重，
                // 这里再兜底一次“同对象只保留首个映射”。
                if (node.Talk != null && !_nodeByTalk.ContainsKey(node.Talk))
                    _nodeByTalk.Add(node.Talk, node);

                if (IsUnusedMember(node))
                {
                    _unusedMembers.Add(node);
                    _unusedSet.Add(node);
                }

                EvtStoryGraphEdge inEdge, outEdge;
                if (TryGetFoldableEdges(node, out inEdge, out outEdge))
                {
                    foldable.Add(node);
                    activeIn.Add(node, inEdge);
                    activeOut.Add(node, outEdge);
                    foldableInOrder.Add(node);
                }
            }

            // 串链：可折节点的唯一活跃出边指向另一个可折节点即相连。
            // 一致性说明：若 n 的唯一活跃出边指向可折的 m，则该边就是 m 的唯一
            // 活跃入边（m 恰有 1 条），因此 prev/next 两方向天然吻合，无冲突。
            var prevMap = new Dictionary<EvtStoryGraphNode, EvtStoryGraphNode>(byRef);
            var nextMap = new Dictionary<EvtStoryGraphNode, EvtStoryGraphNode>(byRef);
            foreach (EvtStoryGraphNode node in foldableInOrder)
            {
                EvtStoryGraphNode to = activeOut[node].To;
                if (to != null && foldable.Contains(to)) nextMap[node] = to;
                EvtStoryGraphNode from = activeIn[node].From;
                if (from != null && foldable.Contains(from)) prevMap[node] = from;
            }

            // 从每个链头（无可折前驱）出发取极大连续序列。带 Cycle/SelfLoop 标志
            // 的节点已被 Flags==None 排除，理论上不会成环；visited 只是兜底防御。
            var visited = new HashSet<EvtStoryGraphNode>(byRef);
            foreach (EvtStoryGraphNode head in foldableInOrder)
            {
                if (prevMap.ContainsKey(head) || visited.Contains(head)) continue;

                var chain = new List<EvtStoryGraphNode>();
                EvtStoryGraphNode current = head;
                while (current != null && foldable.Contains(current) && visited.Add(current))
                {
                    chain.Add(current);
                    EvtStoryGraphNode next;
                    current = nextMap.TryGetValue(current, out next) ? next : null;
                }

                if (chain.Count < 2) continue; // 长度 1 不折，保持普通 Talk 显示节点

                string key = BuildStableSegmentKey(chain[0]);
                int index = _chains.Count;
                _chains.Add(chain);
                _chainKeys.Add(key);
                _segmentKeys.Add(key);
                foreach (EvtStoryGraphNode member in chain) _chainByMember.Add(member, index);
            }
        }

        private static string BuildStableSegmentKey(EvtStoryGraphNode head)
        {
            string modelKey = head != null ? head.Key : null;
            if (string.IsNullOrEmpty(modelKey))
                return SegmentKeyPrefix + "unknown";
            // Talk 模型键格式为 talk:{id}:{ordinal}:{serial}。id+ordinal 已能
            // 唯一标识同一轮数据，尾部 serial 会因前方节点增删而漂移。
            if (modelKey.StartsWith("talk:", System.StringComparison.Ordinal))
            {
                int last = modelKey.LastIndexOf(':');
                if (last > "talk:".Length)
                    modelKey = modelKey.Substring(0, last);
            }
            return SegmentKeyPrefix + modelKey;
        }

        /// <summary>链段白名单判定；通过时给出唯一活跃入边/出边。</summary>
        private static bool TryGetFoldableEdges(
            EvtStoryGraphNode node,
            out EvtStoryGraphEdge inEdge,
            out EvtStoryGraphEdge outEdge)
        {
            inEdge = null;
            outEdge = null;
            if (node == null || node.Kind != EvtStoryGraphNodeKind.Talk) return false;
            // 从严：任何标志位（Entry/Terminal/Unreachable/Cycle/DuplicateId/…）
            // 都取消折叠资格，保证段内全是“干净”的线性对白。
            if (node.Flags != EvtStoryGraphNodeFlags.None) return false;

            int inCount = 0, outCount = 0;
            if (node.Incoming != null)
            {
                foreach (EvtStoryGraphEdge e in node.Incoming)
                {
                    if (e == null || !e.IsRuntimeEdge) continue;
                    inCount++;
                    inEdge = e;
                }
            }
            if (node.Outgoing != null)
            {
                foreach (EvtStoryGraphEdge e in node.Outgoing)
                {
                    if (e == null || !e.IsRuntimeEdge) continue;
                    outCount++;
                    outEdge = e;
                }
            }
            if (inCount != 1 || outCount != 1) return false;

            // 选项边（TalkOption/OptionTalk/OptionTalk2）、NextEvent 一律不折；
            // 它们不在白名单内，出现即判负。
            if (inEdge.Kind != EvtStoryGraphEdgeKind.EventEntry
                && inEdge.Kind != EvtStoryGraphEdgeKind.NextTalk
                && inEdge.Kind != EvtStoryGraphEdgeKind.NextTalk2) return false;
            if (outEdge.Kind != EvtStoryGraphEdgeKind.NextTalk
                && outEdge.Kind != EvtStoryGraphEdgeKind.NextTalk2) return false;
            return true;
        }

        /// <summary>未使用组成员判定：Talk/Option/Missing 类 + 不可达或孤立选项。</summary>
        private static bool IsUnusedMember(EvtStoryGraphNode node)
        {
            if (node == null) return false;
            switch (node.Kind)
            {
                case EvtStoryGraphNodeKind.Talk:
                case EvtStoryGraphNodeKind.Option:
                case EvtStoryGraphNodeKind.MissingTalk:
                case EvtStoryGraphNodeKind.MissingOption:
                    break;
                default:
                    return false; // Event / ExternalTalk / ExternalEvent 一律不收
            }
            if (node.HasFlag(EvtStoryGraphNodeFlags.External)) return false; // 双保险
            return node.HasFlag(EvtStoryGraphNodeFlags.Unreachable)
                   || node.HasFlag(EvtStoryGraphNodeFlags.OrphanOption);
        }

        // ====================================================================
        // 重建：按当前展开状态生成显示节点与显示边
        // ====================================================================

        private void Rebuild()
        {
            var nodes = new List<StoryGraphDisplayNode>();
            var edges = new List<StoryGraphDisplayEdge>();
            var layoutNodes = new List<StoryGraphDisplayNode>();
            var layoutEdges = new List<StoryGraphDisplayEdge>();
            var displayByModel =
                new Dictionary<EvtStoryGraphNode, StoryGraphDisplayNode>(
                    ReferenceComparer<EvtStoryGraphNode>.Instance);
            var layoutByModel =
                new Dictionary<EvtStoryGraphNode, StoryGraphDisplayNode>(
                    ReferenceComparer<EvtStoryGraphNode>.Instance);
            StoryGraphDisplayNode root = null;

            if (_model != null && _model.Nodes != null)
            {
                // 预创建链段块：链成员在模型列表中不一定相邻，先建好块，
                // 主循环无论先遇到哪个成员都能正确归并/定位。
                // 折叠态 → segmentByChain（成员全部归并进段块）；
                // 展开态 → 只平铺真实成员，不创建参与拓扑的辅助节点。
                Dictionary<int, StoryGraphDisplayNode> segmentByChain = null;
                for (int i = 0; i < _chains.Count; i++)
                {
                    if (_expandedKeys.Contains(_chainKeys[i])) continue;
                    if (segmentByChain == null)
                        segmentByChain = new Dictionary<int, StoryGraphDisplayNode>();
                    segmentByChain[i] = CreateSegmentNode(_chains[i], _chainKeys[i]);
                }

                // 未使用配置块：默认折叠；展开时整组平铺为普通节点（布局层会把
                // 它们当作与主图分离的独立分量处理）。
                StoryGraphDisplayNode unusedGroup = null;
                if (_unusedMembers.Count > 0 && !_expandedKeys.Contains(UnusedGroupKey))
                    unusedGroup = CreateUnusedGroupNode();

                // —— 第一遍：显示节点 ——
                foreach (EvtStoryGraphNode modelNode in _model.Nodes)
                {
                    if (modelNode == null) continue;

                    int chainIndex;
                    bool isChainMember = _chainByMember.TryGetValue(modelNode, out chainIndex);

                    StoryGraphDisplayNode segment;
                    if (isChainMember
                        && segmentByChain != null
                        && segmentByChain.TryGetValue(chainIndex, out segment))
                    {
                        // 可见投影把全部成员归并到段块；布局投影则只用段块替代
                        // 首成员，其余成员各保留一个不可见 Talk 占位。于是折叠
                        // 前后的列数、同层槽位和下游布局骨架完全一致。
                        displayByModel[modelNode] = segment;
                        if (ReferenceEquals(_chains[chainIndex][0], modelNode))
                        {
                            nodes.Add(segment);
                            layoutNodes.Add(segment);
                            layoutByModel[modelNode] = segment;
                        }
                        else
                        {
                            StoryGraphDisplayNode placeholder =
                                CreateLayoutPlaceholder(modelNode);
                            layoutNodes.Add(placeholder);
                            layoutByModel[modelNode] = placeholder;
                        }
                        continue;
                    }

                    if (unusedGroup != null && _unusedSet.Contains(modelNode))
                    {
                        displayByModel[modelNode] = unusedGroup; // 组块循环后统一入列
                        layoutByModel[modelNode] = unusedGroup;
                        continue;
                    }

                    // 普通显示节点：Event / 不可折 Talk / Option / Missing / External，
                    // 以及展开态的链段成员、展开态的未使用组成员、长度 1 的链。
                    StoryGraphDisplayNode display = CreatePlainNode(modelNode);
                    if (isChainMember
                        && ReferenceEquals(_chains[chainIndex][0], modelNode)
                        && _expandedKeys.Contains(_chainKeys[chainIndex]))
                    {
                        // 仍是一个真实 Talk 显示节点；仅增加布局/显示元数据。
                        display.ExpandedSegmentKey = _chainKeys[chainIndex];
                        display.ExpandedSegmentCount = _chains[chainIndex].Count;
                    }
                    nodes.Add(display);
                    layoutNodes.Add(display);
                    displayByModel[modelNode] = display;
                    layoutByModel[modelNode] = display;
                    if (ReferenceEquals(modelNode, _model.EventNode)) root = display;
                }

                // 未使用块放列表末尾：主图顺序保持稳定，布局层按独立分量放置。
                if (unusedGroup != null)
                {
                    nodes.Add(unusedGroup);
                    layoutNodes.Add(unusedGroup);
                }

                // —— 第二遍：显示边（模型边 1:1 映射）——
                if (_model.Edges != null)
                {
                    foreach (EvtStoryGraphEdge modelEdge in _model.Edges)
                    {
                        if (modelEdge == null || modelEdge.From == null
                            || modelEdge.To == null) continue;

                        StoryGraphDisplayNode layoutFrom, layoutTo;
                        if (layoutByModel.TryGetValue(
                                modelEdge.From, out layoutFrom)
                            && layoutFrom != null
                            && layoutByModel.TryGetValue(
                                modelEdge.To, out layoutTo)
                            && layoutTo != null
                            && (!ReferenceEquals(layoutFrom, layoutTo)
                                || ReferenceEquals(
                                    modelEdge.From, modelEdge.To)))
                        {
                            // 布局边保留折叠链内部的真实逐句约束；窗口完成布局后
                            // 会用可见 Edges 重新布线，所以这些边永远不会被画出。
                            layoutEdges.Add(new StoryGraphDisplayEdge
                            {
                                From = layoutFrom,
                                To = layoutTo,
                                Kind = modelEdge.Kind,
                                Flags = modelEdge.Flags,
                                Label = modelEdge.Label,
                            });
                        }

                        StoryGraphDisplayNode from, to;
                        if (!displayByModel.TryGetValue(modelEdge.From, out from)
                            || from == null) continue;
                        if (!displayByModel.TryGetValue(modelEdge.To, out to)
                            || to == null) continue;

                        // 两端被折叠进同一块 ⇒ 块内部边，丢弃；
                        // 模型本身的自环边（同一模型节点）是真实配置异常，保留。
                        if (ReferenceEquals(from, to)
                            && !ReferenceEquals(modelEdge.From, modelEdge.To)) continue;

                        var displayEdge = new StoryGraphDisplayEdge
                        {
                            From = from,
                            To = to,
                            Kind = modelEdge.Kind,
                            Flags = modelEdge.Flags,
                            Label = modelEdge.Label,
                        };
                        edges.Add(displayEdge);
                        from.Outgoing.Add(displayEdge);
                        to.Incoming.Add(displayEdge);
                    }
                }

            }

            // 每次重建都换成全新列表实例，旧引用不会被原地修改。
            _nodes = nodes;
            _edges = edges;
            _layoutNodes = layoutNodes;
            _layoutEdges = layoutEdges;
            _root = root;
            _displayByModel = displayByModel;
        }

        // ====================================================================
        // 显示节点构造
        // ====================================================================

        /// <summary>普通显示节点：1:1 包裹一个模型节点，LocateTalk 透传（可为 null）。</summary>
        private static StoryGraphDisplayNode CreatePlainNode(EvtStoryGraphNode source)
        {
            return new StoryGraphDisplayNode
            {
                Key = source.Key,
                Kind = ToDisplayKind(source.Kind),
                Flags = source.Flags,
                Id = source.Id,
                Title = source.Title,
                Subtitle = source.Subtitle,
                SearchText = source.SearchText,
                SourceNode = source,
                SegmentNodes = null,
                LocateTalk = source.LocateTalk,
                IsUnusedGroup = false,
                ExpandedSegmentKey = null,
                ExpandedSegmentCount = 0,
            };
        }

        /// <summary>
        /// 折叠链中除首句外的布局占位。它只存在于 LayoutNodes，不进入 Nodes，
        /// 因而不会绘制、命中、搜索、持久化或被作者误认为真实辅助节点。
        /// </summary>
        private static StoryGraphDisplayNode CreateLayoutPlaceholder(
            EvtStoryGraphNode source)
        {
            StoryGraphDisplayNode placeholder = CreatePlainNode(source);
            placeholder.Key = "layout-hidden:" + (source.Key ?? string.Empty);
            return placeholder;
        }

        /// <summary>
        /// 链段块：Title="N 句对话"，摘要/定位取剧情序首成员。
        /// 只在折叠态作为整段的唯一代表；展开态不创建该辅助节点。
        /// </summary>
        private static StoryGraphDisplayNode CreateSegmentNode(
            List<EvtStoryGraphNode> chain, string key)
        {
            EvtStoryGraphNode first = chain[0];
            var search = new StringBuilder();
            search.Append(chain.Count).Append(" 句对话 链段");
            foreach (EvtStoryGraphNode member in chain)
            {
                // 段内每句台词都要能被搜索命中，即使块处于折叠态。
                if (member != null && !string.IsNullOrEmpty(member.SearchText))
                    search.Append(' ').Append(member.SearchText);
            }

            return new StoryGraphDisplayNode
            {
                Key = key,
                Kind = StoryGraphDisplayNodeKind.Segment,
                Flags = EvtStoryGraphNodeFlags.None, // 成员全部 Flags==None
                Id = first.Id,
                Title = chain.Count + " 句对话",
                Subtitle = first.Subtitle,
                SearchText = search.ToString(),
                SourceNode = null,
                // 副本防外部修改内部链；契约要求按剧情序。
                SegmentNodes = new List<EvtStoryGraphNode>(chain),
                // 双击定位取首成员 Talk；Talk 缺失时兜底 LocateTalk（可能仍为 null）。
                LocateTalk = first.Talk != null ? first.Talk : first.LocateTalk,
                IsUnusedGroup = false,
            };
        }

        /// <summary>未使用配置折叠区：Key="unused"，Title="未使用配置（N）"。</summary>
        private StoryGraphDisplayNode CreateUnusedGroupNode()
        {
            int talks = 0, options = 0, missing = 0;
            var flags = EvtStoryGraphNodeFlags.None;
            var search = new StringBuilder("未使用配置 不可达 孤立选项 unused");
            foreach (EvtStoryGraphNode member in _unusedMembers)
            {
                flags |= member.Flags; // 聚合标志供显示层着色（Unreachable/OrphanOption）
                switch (member.Kind)
                {
                    case EvtStoryGraphNodeKind.Talk: talks++; break;
                    case EvtStoryGraphNodeKind.Option: options++; break;
                    default: missing++; break; // MissingTalk / MissingOption
                }
                if (!string.IsNullOrEmpty(member.SearchText))
                    search.Append(' ').Append(member.SearchText);
            }

            return new StoryGraphDisplayNode
            {
                Key = UnusedGroupKey,
                Kind = StoryGraphDisplayNodeKind.UnusedGroup,
                Flags = flags,
                Id = 0,
                Title = "未使用的配置（" + _unusedMembers.Count + "）",
                Subtitle = BuildUnusedSubtitle(talks, options, missing),
                SearchText = search.ToString(),
                SourceNode = null,
                SegmentNodes = null,   // 非链段，契约规定为 null；成员由本类私有保存
                LocateTalk = null,     // 组块无单一定位目标
                IsUnusedGroup = true,
            };
        }

        /// <summary>未使用块副标题：按类型计数，如“对话 3 · 选项 2 · 缺失引用 1”。</summary>
        private static string BuildUnusedSubtitle(int talks, int options, int missing)
        {
            var parts = new List<string>();
            if (talks > 0) parts.Add("对话 " + talks);
            if (options > 0) parts.Add("选项 " + options);
            if (missing > 0) parts.Add("缺失引用 " + missing);
            return parts.Count > 0 ? string.Join(" · ", parts.ToArray()) : null;
        }

        private static StoryGraphDisplayNodeKind ToDisplayKind(EvtStoryGraphNodeKind kind)
        {
            switch (kind)
            {
                case EvtStoryGraphNodeKind.Event:
                    return StoryGraphDisplayNodeKind.Event;
                case EvtStoryGraphNodeKind.Talk:
                    return StoryGraphDisplayNodeKind.Talk;
                case EvtStoryGraphNodeKind.Option:
                    return StoryGraphDisplayNodeKind.Option;
                case EvtStoryGraphNodeKind.ExternalTalk:
                case EvtStoryGraphNodeKind.ExternalEvent:
                    return StoryGraphDisplayNodeKind.External;
                case EvtStoryGraphNodeKind.MissingTalk:
                case EvtStoryGraphNodeKind.MissingOption:
                case EvtStoryGraphNodeKind.MissingEvent:
                default:
                    return StoryGraphDisplayNodeKind.Missing;
            }
        }

        // ====================================================================
        // 引用相等比较器：netstandard2.0 没有内置 ReferenceEqualityComparer，
        // 而 TalkCfg/模型节点都必须按对象身份（ReferenceEquals）匹配。
        // ====================================================================

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceComparer<T> Instance =
                new ReferenceComparer<T>();

            public bool Equals(T x, T y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(T obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
