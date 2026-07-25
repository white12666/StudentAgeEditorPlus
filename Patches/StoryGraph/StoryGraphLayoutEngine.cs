using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图布局层（契约冻结）：显示图 → 节点矩形 + 正交连线路径。
    /// 只读显示图，不改任何节点/边数据。
    ///
    /// 算法（对应任务约定）：
    ///   1) 分量划分：沿所有边做无向连通分量；root 所在分量为主分量排在最上，
    ///      其余分量（如展开后的未使用配置）各自独立布局后垂直堆叠在主图下方，
    ///      分量之间留 StoryGraphMetrics.ComponentGapY。
    ///   2) 分量内分层：取运行时活跃边，以及作者已配置但因父 Talk 正文为空而
    ///      暂时 RuntimeIgnored 的 TalkOption/OptionTalk/OptionTalk2 结构边；并排除
    ///      自环与指向 root 的事件回边（否则“选项 → 下一事件 → 返回本事件”会把主流程和 root
    ///      缩进同一个超点，DAG 无从谈起）；迭代 Kosaraju（显式栈，不递归）
    ///      缩点成 DAG，再做最长路径分层——折叠成员由 ViewModel 以不可见布局
    ///      占位保留，因此布局层始终看到与完全展开状态相同的逐句骨架；
    ///      同 SCC 节点同层，在层内相邻垂直堆叠。
    ///   3) 层内排序：初始按语义 Kind/Id（Segment 按其代表的 Talk 排序，Key
    ///      兜底保证确定性），之后 2 轮重心排序
    ///      减交叉，每轮先下行（用上一层行号）再上行（用下一层行号），只看
    ///      相邻层的相对行号；SCC 作为整块参与排序，成员不被拆散。
    ///   4) 坐标：x = Margin + level * ColumnGap；y 先按父节点重心靠拢、
    ///      再自上而下消解重叠（间距 RowGap），最后再一轮子节点向父靠拢。
    ///   5) 布线：前向边（目标层 &gt; 源层）= 源右缘独立接点 → 水平到两列中缝的
    ///      独立车道 → 竖直 → 目标左缘独立接点；同一走廊的多条边稳定分轨，
    ///      不再共享整段折线。回边（目标层 &lt;= 源层，含同层边与指向 root
    ///      的事件回边）= 源顶缘 → 图上方通道 → 目标顶缘，通道按分配顺序每档
    ///      抬高 BackEdgeChannelGap，最高点计入 Bounds；自环画右侧小矩形环；
    ///      LabelPos 取折线最长段中点。
    ///
    /// 全程迭代、无递归，所有循环次数都有上界：空图、单节点、全不可达、
    /// 环套环等任意输入都不会死循环，也不会产生节点重叠。
    /// </summary>
    internal static class StoryGraphLayoutEngine
    {
        // —— 契约未规定细节的小尺寸调参（均远小于 Margin，不会顶出画布）——
        private const float SelfLoopDepth = 22f;      // 自环矩形环向右伸出距离
        private const float SelfLoopHalfHeight = 14f; // 自环矩形环半高
        private const float SelfLoopStep = 10f;       // 同节点多个自环依次外移量
        private const float SameColumnNudge = 14f;    // 同列回边出入口横向错开量
        private const float PointEpsilon = 0.01f;     // 折线连续重复点去除阈值
        private const float ForwardLaneGap = 9f;      // 并行前向边中心线间距（高亮后仍可分辨）
        private const float ForwardLaneCluster = 6f;  // 小于此距离的中缝视为同一走廊
        private const float ForwardPortGap = 12f;     // 节点左右缘的多边接点间距（容纳高亮箭头）
        private const float BackPortGap = 12f;        // 节点顶缘的回边接点间距
        private const float NodePortPadding = 12f;    // 接点避开圆角和语义色条
        private const float CorridorPadding = 6f;     // 中缝车道不贴住卡片边缘

        /// <summary>契约冻结的唯一入口：布局整张显示图。</summary>
        internal static StoryGraphLayoutResult Layout(
            List<StoryGraphDisplayNode> nodes,
            List<StoryGraphDisplayEdge> edges,
            StoryGraphDisplayNode root)
        {
            var result = new StoryGraphLayoutResult
            {
                NodeRects = new Dictionary<StoryGraphDisplayNode, Rect>(),
                Edges = new List<StoryGraphRoutedEdge>(),
                // 空图保底：给一块 Margin 见方的画布，显示层不会拿到零尺寸。
                Bounds = new Rect(0f, 0f,
                    StoryGraphMetrics.Margin * 2f, StoryGraphMetrics.Margin * 2f),
            };

            var nodeSet = new HashSet<StoryGraphDisplayNode>();
            if (nodes != null)
            {
                foreach (StoryGraphDisplayNode node in nodes)
                    if (node != null) nodeSet.Add(node);
            }
            if (nodeSet.Count == 0) return result; // 空图

            // 只接受端点都在节点集内的边；悬空边整条丢弃，避免布线时取不到矩形。
            var validEdges = new List<IndexedEdge>();
            if (edges != null)
            {
                for (int i = 0; i < edges.Count; i++)
                {
                    StoryGraphDisplayEdge edge = edges[i];
                    if (edge == null || edge.From == null || edge.To == null) continue;
                    if (!nodeSet.Contains(edge.From) || !nodeSet.Contains(edge.To)) continue;
                    validEdges.Add(new IndexedEdge { Index = i, Edge = edge });
                }
            }

            List<ComponentContext> components =
                SplitComponents(nodes, validEdges, nodeSet, root);

            foreach (ComponentContext ctx in components)
            {
                BuildLayering(ctx, root); // SCC 缩点 + 最长路径分层
                OrderLayers(ctx);         // 层内初始序 + 2 轮重心排序
                PlaceNodes(ctx);          // x/y 坐标（局部分量坐标，x 已是最终值）
                MeasureComponent(ctx);    // 内容外接范围（含回边通道与自环环）
            }

            // 垂直堆叠：主分量在最上，其余分量依次接在下方。
            float cursorY = StoryGraphMetrics.Margin;
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            var routed = new List<KeyValuePair<int, StoryGraphRoutedEdge>>();

            foreach (ComponentContext ctx in components)
            {
                float offsetY = cursorY - ctx.ContentTop;
                foreach (KeyValuePair<StoryGraphDisplayNode, Rect> pair in ctx.Rects)
                {
                    Rect rect = pair.Value;
                    rect.y += offsetY;
                    result.NodeRects[pair.Key] = rect;
                    minX = Mathf.Min(minX, rect.xMin);
                    minY = Mathf.Min(minY, rect.yMin);
                    maxX = Mathf.Max(maxX, rect.xMax);
                    maxY = Mathf.Max(maxY, rect.yMax);
                }

                // 回边通道（在节点带上方）与自环环（在节点带右侧）也计入内容范围。
                minY = Mathf.Min(minY, ctx.ContentTop + offsetY);
                maxX = Mathf.Max(maxX, ctx.ContentRight);
                cursorY = ctx.ContentBottom + offsetY + StoryGraphMetrics.ComponentGapY;

                RouteEdges(ctx, offsetY, result.NodeRects, routed);
            }

            // 输出边恢复全局输入顺序，显示层按下标对齐边数据时不会出现错位。
            routed.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (KeyValuePair<int, StoryGraphRoutedEdge> pair in routed)
                result.Edges.Add(pair.Value);

            // Bounds = 全部节点矩形 + 通道（含自环环外缘）+ Margin 的外接矩形。
            result.Bounds = Rect.MinMaxRect(
                minX - StoryGraphMetrics.Margin, minY - StoryGraphMetrics.Margin,
                maxX + StoryGraphMetrics.Margin, maxY + StoryGraphMetrics.Margin);
            return result;
        }

        /// <summary>
        /// 把作者工作区的手动左上角坐标覆盖到自动布局，并按实际矩形重新布线。
        /// 坐标只影响显示结果，不写入 TalkCfg / OptionCfg。
        /// </summary>
        internal static void ApplyManualPositions(
            StoryGraphLayoutResult result,
            IEnumerable<StoryGraphDisplayNode> nodes,
            IDictionary<string, Vector2> positions,
            IList<StoryGraphDisplayEdge> edges)
        {
            if (result == null || result.NodeRects == null) return;
            if (nodes != null && positions != null)
            {
                foreach (StoryGraphDisplayNode node in nodes)
                {
                    if (node == null) continue;
                    string key = StoryGraphWorkspace.StableNodeKey(node);
                    Vector2 position;
                    Rect rect;
                    if (string.IsNullOrEmpty(key)
                        || !positions.TryGetValue(key, out position)
                        || float.IsNaN(position.x) || float.IsInfinity(position.x)
                        || float.IsNaN(position.y) || float.IsInfinity(position.y)
                        || !result.NodeRects.TryGetValue(node, out rect)) continue;
                    rect.x = Mathf.Clamp(position.x,
                        StoryGraphMetrics.Margin, 1000000f);
                    rect.y = Mathf.Clamp(position.y,
                        StoryGraphMetrics.Margin, 1000000f);
                    result.NodeRects[node] = rect;
                }
            }
            RerouteCurrent(result, edges);
        }

        /// <summary>节点拖动期间按当前 NodeRects 重建正交边与 Bounds。</summary>
        internal static void RerouteCurrent(
            StoryGraphLayoutResult result,
            IList<StoryGraphDisplayEdge> edges)
        {
            if (result == null || result.NodeRects == null) return;
            var routed = new List<StoryGraphRoutedEdge>();
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (Rect rect in result.NodeRects.Values)
            {
                minX = Mathf.Min(minX, rect.xMin);
                minY = Mathf.Min(minY, rect.yMin);
                maxX = Mathf.Max(maxX, rect.xMax);
                maxY = Mathf.Max(maxY, rect.yMax);
            }
            if (result.NodeRects.Count == 0)
            {
                result.Edges = routed;
                result.Bounds = new Rect(0f, 0f,
                    StoryGraphMetrics.Margin * 2f, StoryGraphMetrics.Margin * 2f);
                return;
            }

            float channelTop = minY;
            int backChannel = 0;
            var selfLoopSeen = new Dictionary<StoryGraphDisplayNode, int>();
            var requests = new List<RouteRequest>();
            if (edges != null)
            {
                for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
                {
                    StoryGraphDisplayEdge edge = edges[edgeIndex];
                    if (edge == null || edge.From == null || edge.To == null) continue;
                    Rect from;
                    Rect to;
                    if (!result.NodeRects.TryGetValue(edge.From, out from)
                        || !result.NodeRects.TryGetValue(edge.To, out to)) continue;
                    from = StoryGraphMetrics.NodeCardRect(edge.From, from);
                    to = StoryGraphMetrics.NodeCardRect(edge.To, to);
                    var request = new RouteRequest
                    {
                        InputIndex = edgeIndex,
                        Edge = edge,
                        From = from,
                        To = to,
                    };
                    if (ReferenceEquals(edge.From, edge.To))
                    {
                        int ordinal;
                        selfLoopSeen.TryGetValue(edge.From, out ordinal);
                        selfLoopSeen[edge.From] = ordinal + 1;
                        request.Kind = RouteKind.SelfLoop;
                        request.SelfLoopOrdinal = ordinal;
                    }
                    else if (to.xMin >= from.xMax + 8f)
                    {
                        request.Kind = RouteKind.Forward;
                        request.BaseCorridorX =
                            (from.xMax + to.xMin) * 0.5f;
                        request.CorridorX = request.BaseCorridorX;
                    }
                    else
                    {
                        request.Kind = RouteKind.Back;
                        request.ChannelY = channelTop
                            - (++backChannel) * StoryGraphMetrics.BackEdgeChannelGap;
                    }
                    requests.Add(request);
                }
            }

            AssignRouteLanes(requests);
            foreach (RouteRequest request in requests)
            {
                List<Vector2> points = BuildRoutePoints(request);
                foreach (Vector2 point in points)
                {
                    minX = Mathf.Min(minX, point.x);
                    minY = Mathf.Min(minY, point.y);
                    maxX = Mathf.Max(maxX, point.x);
                    maxY = Mathf.Max(maxY, point.y);
                }
                routed.Add(new StoryGraphRoutedEdge
                {
                    Edge = request.Edge,
                    Points = points,
                    LabelPos = LongestSegmentMidpoint(points),
                });
            }
            result.Edges = routed;
            result.Bounds = Rect.MinMaxRect(
                minX - StoryGraphMetrics.Margin,
                minY - StoryGraphMetrics.Margin,
                maxX + StoryGraphMetrics.Margin,
                maxY + StoryGraphMetrics.Margin);
        }

        // ====================================================================
        // 第一步：分量划分
        // ====================================================================

        private static List<ComponentContext> SplitComponents(
            List<StoryGraphDisplayNode> nodes,
            List<IndexedEdge> edges,
            HashSet<StoryGraphDisplayNode> nodeSet,
            StoryGraphDisplayNode root)
        {
            // 无向邻接：所有边（含运行时被忽略边）都参与连通性判定。
            var adjacency = new Dictionary<StoryGraphDisplayNode, List<StoryGraphDisplayNode>>();
            foreach (StoryGraphDisplayNode node in nodeSet)
                adjacency[node] = new List<StoryGraphDisplayNode>();
            foreach (IndexedEdge indexed in edges)
            {
                adjacency[indexed.Edge.From].Add(indexed.Edge.To);
                adjacency[indexed.Edge.To].Add(indexed.Edge.From);
            }

            // 迭代 BFS 划分连通分量（不递归）；按输入顺序遍历保证结果确定。
            var componentOf = new Dictionary<StoryGraphDisplayNode, int>();
            var components = new List<ComponentContext>();
            var queue = new Queue<StoryGraphDisplayNode>();
            foreach (StoryGraphDisplayNode start in nodes)
            {
                if (start == null || !nodeSet.Contains(start)) continue;
                if (componentOf.ContainsKey(start)) continue; // 去重 + 已访问
                var ctx = new ComponentContext();
                int id = components.Count;
                componentOf[start] = id;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    StoryGraphDisplayNode current = queue.Dequeue();
                    ctx.Nodes.Add(current);
                    foreach (StoryGraphDisplayNode next in adjacency[current])
                    {
                        if (componentOf.ContainsKey(next)) continue;
                        componentOf[next] = id;
                        queue.Enqueue(next);
                    }
                }
                components.Add(ctx);
            }

            // 边按 From 所属分量分桶（同一条边两端必然同分量）。
            foreach (IndexedEdge indexed in edges)
                components[componentOf[indexed.Edge.From]].Edges.Add(indexed);

            // 主分量 = root 所在分量；root 缺失/不在节点集时退化为首节点所在分量。
            int mainIndex = 0;
            if (root != null)
            {
                int found;
                if (componentOf.TryGetValue(root, out found)) mainIndex = found;
            }

            var ordered = new List<ComponentContext> { components[mainIndex] };
            var rest = new List<ComponentContext>();
            for (int i = 0; i < components.Count; i++)
                if (i != mainIndex) rest.Add(components[i]);
            // 次分量按“最小节点”的 Kind/Id/Key 排序，输出与输入顺序无关。
            rest.Sort((a, b) => CompareNodes(MinNode(a.Nodes), MinNode(b.Nodes)));
            ordered.AddRange(rest);
            return ordered;
        }

        // ====================================================================
        // 第二步：分量内分层（SCC 缩点 + 最长路径）
        // ====================================================================

        private static void BuildLayering(ComponentContext ctx, StoryGraphDisplayNode root)
        {
            // 分层约束图：运行时活跃边 + 作者 Option 结构边；排除自环与指向
            // root 的事件回边；平行边去重。结构边只影响排版，视觉和诊断仍保留
            // RuntimeIgnored，绝不伪装成游戏实际会执行。
            foreach (StoryGraphDisplayNode node in ctx.Nodes)
            {
                ctx.Succ[node] = new List<StoryGraphDisplayNode>();
                ctx.Pred[node] = new List<StoryGraphDisplayNode>();
            }
            var dedupe = new Dictionary<StoryGraphDisplayNode, HashSet<StoryGraphDisplayNode>>();
            foreach (IndexedEdge indexed in ctx.Edges)
            {
                StoryGraphDisplayEdge edge = indexed.Edge;
                if (!IsLayeringConstraint(edge)) continue;
                if (ReferenceEquals(edge.From, edge.To)) continue;
                if (root != null && ReferenceEquals(edge.To, root)) continue;
                HashSet<StoryGraphDisplayNode> targets;
                if (!dedupe.TryGetValue(edge.From, out targets))
                {
                    targets = new HashSet<StoryGraphDisplayNode>();
                    dedupe.Add(edge.From, targets);
                }
                if (!targets.Add(edge.To)) continue;
                ctx.Succ[edge.From].Add(edge.To);
                ctx.Pred[edge.To].Add(edge.From);
            }

            // 迭代 Kosaraju 第一遍：显式栈 DFS 记录完成顺序（防超长链栈溢出）。
            var visited = new HashSet<StoryGraphDisplayNode>();
            var finishOrder = new List<StoryGraphDisplayNode>(ctx.Nodes.Count);
            var frames = new Stack<DfsFrame>();
            foreach (StoryGraphDisplayNode start in ctx.Nodes)
            {
                if (!visited.Add(start)) continue;
                frames.Push(new DfsFrame { Node = start });
                while (frames.Count > 0)
                {
                    DfsFrame frame = frames.Peek();
                    List<StoryGraphDisplayNode> nextNodes = ctx.Succ[frame.Node];
                    if (frame.NextIndex < nextNodes.Count)
                    {
                        StoryGraphDisplayNode next = nextNodes[frame.NextIndex++];
                        if (visited.Add(next)) frames.Push(new DfsFrame { Node = next });
                        continue;
                    }
                    frames.Pop();
                    finishOrder.Add(frame.Node);
                }
            }

            // 第二遍：按完成顺序逆序在反向图上收集 SCC。
            var pending = new Stack<StoryGraphDisplayNode>();
            var sccMembers = new List<List<StoryGraphDisplayNode>>();
            for (int i = finishOrder.Count - 1; i >= 0; i--)
            {
                StoryGraphDisplayNode start = finishOrder[i];
                if (ctx.Scc.ContainsKey(start)) continue;
                int id = sccMembers.Count;
                var members = new List<StoryGraphDisplayNode>();
                ctx.Scc[start] = id;
                pending.Push(start);
                while (pending.Count > 0)
                {
                    StoryGraphDisplayNode current = pending.Pop();
                    members.Add(current);
                    foreach (StoryGraphDisplayNode prev in ctx.Pred[current])
                    {
                        if (ctx.Scc.ContainsKey(prev)) continue;
                        ctx.Scc[prev] = id;
                        pending.Push(prev);
                    }
                }
                members.Sort(CompareNodes); // 成员排序 → 同层堆叠顺序确定
                sccMembers.Add(members);
            }

            // 缩点成 DAG（超点间平行边去重）。
            int sccCount = sccMembers.Count;
            var sccSucc = new List<int>[sccCount];
            var indegree = new int[sccCount];
            for (int i = 0; i < sccCount; i++) sccSucc[i] = new List<int>();
            var dagEdges = new HashSet<long>();
            for (int i = 0; i < sccCount; i++)
            {
                foreach (StoryGraphDisplayNode member in sccMembers[i])
                {
                    foreach (StoryGraphDisplayNode next in ctx.Succ[member])
                    {
                        int j = ctx.Scc[next];
                        if (j == i) continue;
                        if (!dagEdges.Add((long)i * sccCount + j)) continue;
                        sccSucc[i].Add(j);
                        indegree[j]++;
                    }
                }
            }

            // Kahn 拓扑序上做最长路径分层：布局占位已经还原逐句骨架，
            // 每条约束边正常推进一层即可保持展开前后的下游层一致。
            // 层级本身与队列弹出顺序无关（入队时全部前驱已处理完），结果确定。
            var level = new int[sccCount];
            var queue = new Queue<int>();
            for (int i = 0; i < sccCount; i++)
                if (indegree[i] == 0) queue.Enqueue(i);
            int processed = 0;
            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                processed++;
                foreach (int v in sccSucc[u])
                {
                    if (level[v] < level[u] + 1) level[v] = level[u] + 1;
                    indegree[v]--;
                    if (indegree[v] == 0) queue.Enqueue(v);
                }
            }
            if (processed < sccCount)
            {
                // 防御分支：DAG 上 Kahn 必然处理完全部超点，理论不可达；
                // 万一未来改动破坏无环性，给剩余超点依次补层级，保证不死循环。
                int fallback = 0;
                for (int i = 0; i < sccCount; i++) fallback = Math.Max(fallback, level[i]);
                for (int i = 0; i < sccCount; i++)
                {
                    if (indegree[i] <= 0) continue; // 已处理的超点入度必为 0
                    fallback++;
                    level[i] = fallback;
                }
            }

            for (int i = 0; i < sccCount; i++)
                foreach (StoryGraphDisplayNode member in sccMembers[i])
                    ctx.Level[member] = level[i];
            ctx.SccMembers = sccMembers;
        }

        private static bool IsLayeringConstraint(StoryGraphDisplayEdge edge)
        {
            return edge != null && (edge.IsRuntimeEdge
                || edge.Kind == EvtStoryGraphEdgeKind.TalkOption
                || edge.Kind == EvtStoryGraphEdgeKind.OptionTalk
                || edge.Kind == EvtStoryGraphEdgeKind.OptionTalk2);
        }

        // ====================================================================
        // 第三步：层内排序（初始 Kind/Id + 2 轮重心排序）
        // ====================================================================

        private static void OrderLayers(ComponentContext ctx)
        {
            // 层 → 块列表；块 = SCC，成员在层内相邻堆叠，整块参与排序不被拆散。
            int sccCount = Math.Max(1, ctx.SccMembers.Count);
            var blockByKey = new Dictionary<long, Block>();
            foreach (StoryGraphDisplayNode node in ctx.Nodes)
            {
                int level = ctx.Level[node];
                long key = (long)level * sccCount + ctx.Scc[node];
                Block block;
                if (!blockByKey.TryGetValue(key, out block))
                {
                    block = new Block();
                    blockByKey.Add(key, block);
                    List<Block> layerBlocks;
                    if (!ctx.Layers.TryGetValue(level, out layerBlocks))
                    {
                        layerBlocks = new List<Block>();
                        ctx.Layers.Add(level, layerBlocks);
                    }
                    layerBlocks.Add(block);
                }
                block.Members.Add(node);
            }

            var levelKeys = ctx.Layers.Keys.OrderBy(v => v).ToList();
            foreach (int level in levelKeys)
            {
                foreach (Block block in ctx.Layers[level])
                    block.Members.Sort(CompareNodes);
                // 初始按 Kind/Id：以块内最小成员为代表（OrderBy 稳定，Key 已兜底）。
                ctx.Layers[level] = ctx.Layers[level]
                    .OrderBy(b => b.Members[0], Comparer<StoryGraphDisplayNode>.Create(CompareNodes))
                    .ToList();
            }

            // 行号表：节点在所在层的当前行（块展开后的序号）。
            var row = new Dictionary<StoryGraphDisplayNode, int>();
            foreach (int level in levelKeys) RebuildLayerRows(ctx.Layers[level], row);

            // 2 轮重心排序：每轮先下行（用 L-1 层父节点行号）再上行（用 L+1 层
            // 子节点行号）；只看相邻层——跨多层的长边不直接参与排序，与契约一致。
            for (int pass = 0; pass < 2; pass++)
            {
                for (int li = 1; li < levelKeys.Count; li++)
                {
                    int level = levelKeys[li];
                    int prevLevel = levelKeys[li - 1];
                    foreach (Block block in ctx.Layers[level])
                        block.Barycenter = BlockBarycenter(block, ctx.Pred, ctx.Level, row, prevLevel);
                    ctx.Layers[level] = ctx.Layers[level].OrderBy(b => b.Barycenter).ToList();
                    RebuildLayerRows(ctx.Layers[level], row);
                }
                for (int li = levelKeys.Count - 2; li >= 0; li--)
                {
                    int level = levelKeys[li];
                    int nextLevel = levelKeys[li + 1];
                    foreach (Block block in ctx.Layers[level])
                        block.Barycenter = BlockBarycenter(block, ctx.Succ, ctx.Level, row, nextLevel);
                    ctx.Layers[level] = ctx.Layers[level].OrderBy(b => b.Barycenter).ToList();
                    RebuildLayerRows(ctx.Layers[level], row);
                }
            }
        }

        private static float BlockBarycenter(
            Block block,
            Dictionary<StoryGraphDisplayNode, List<StoryGraphDisplayNode>> neighbors,
            Dictionary<StoryGraphDisplayNode, int> levels,
            Dictionary<StoryGraphDisplayNode, int> row,
            int adjacentLevel)
        {
            double sum = 0;
            int count = 0;
            foreach (StoryGraphDisplayNode member in block.Members)
            {
                List<StoryGraphDisplayNode> list;
                if (!neighbors.TryGetValue(member, out list)) continue;
                foreach (StoryGraphDisplayNode other in list)
                {
                    int otherLevel;
                    if (!levels.TryGetValue(other, out otherLevel) || otherLevel != adjacentLevel)
                        continue;
                    int otherRow;
                    if (!row.TryGetValue(other, out otherRow)) continue;
                    sum += otherRow;
                    count++;
                }
            }
            if (count > 0) return (float)(sum / count);
            // 没有相邻层连接的块保持当前位置，避免无谓漂移。
            double self = 0;
            foreach (StoryGraphDisplayNode member in block.Members)
                self += row[member];
            return (float)(self / block.Members.Count);
        }

        private static void RebuildLayerRows(
            List<Block> blocks, Dictionary<StoryGraphDisplayNode, int> row)
        {
            int r = 0;
            foreach (Block block in blocks)
                foreach (StoryGraphDisplayNode member in block.Members)
                    row[member] = r++;
        }

        // ====================================================================
        // 第四步：坐标分配（局部分量坐标，y 从 0 起，x 直接是最终值）
        // ====================================================================

        private static void PlaceNodes(ComponentContext ctx)
        {
            var levelKeys = ctx.Layers.Keys.OrderBy(v => v).ToList();
            var sizes = new Dictionary<StoryGraphDisplayNode, Vector2>();
            foreach (int level in levelKeys)
                foreach (Block block in ctx.Layers[level])
                    foreach (StoryGraphDisplayNode member in block.Members)
                        sizes[member] = StoryGraphMetrics.NodeSize(member);

            // 最低层（正常即第 0 层）：没有父层可靠拢，按排序结果自上而下顺排。
            int baseLevel = levelKeys[0];
            {
                float y = 0f;
                foreach (Block block in ctx.Layers[baseLevel])
                {
                    foreach (StoryGraphDisplayNode member in block.Members)
                    {
                        Vector2 size = sizes[member];
                        ctx.Rects[member] = new Rect(
                            StoryGraphMetrics.Margin + baseLevel * StoryGraphMetrics.ColumnGap,
                            y, size.x, size.y);
                        y += size.y + StoryGraphMetrics.RowGapAfter(member);
                    }
                }
            }

            // 其余层共两轮：第一轮“按父节点重心靠拢 + 自上而下消解重叠”，
            // 第二轮即契约要求的“再一轮子节点向父靠拢”。
            // 按期望值稳定排序决定放置顺序（并列时保持重心排序的减交叉结果）；
            // 不回写块顺序——y 排序可能使 SCC 块与非块成员交错，拆块反而破坏
            // “同 SCC 相邻堆叠”的约定，而固定两轮保证必终止。
            for (int round = 0; round < 2; round++)
            {
                for (int li = 1; li < levelKeys.Count; li++)
                {
                    int level = levelKeys[li];
                    var ordered = new List<StoryGraphDisplayNode>();
                    foreach (Block block in ctx.Layers[level])
                        ordered.AddRange(block.Members);

                    // 无更早层父节点时的兜底期望位置：按层内平均步长均匀铺开。
                    float pitch = 0f;
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        StoryGraphDisplayNode node = ordered[i];
                        pitch += sizes[node].y;
                        if (i + 1 < ordered.Count)
                            pitch += StoryGraphMetrics.RowGapAfter(node);
                    }
                    if (ordered.Count > 0)
                        pitch /= ordered.Count;

                    var desired = new Dictionary<StoryGraphDisplayNode, float>();
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        StoryGraphDisplayNode node = ordered[i];
                        float want;
                        if (!TryParentBarycenter(node, ctx, sizes[node], out want))
                        {
                            Rect current;
                            want = round > 0 && ctx.Rects.TryGetValue(node, out current)
                                ? current.y  // 第二轮没有父节点就原地不动
                                : i * pitch; // 第一轮均匀铺开
                        }
                        desired[node] = want;
                    }

                    ordered = ordered.OrderBy(n => desired[n]).ToList();
                    float cursor = 0f;
                    foreach (StoryGraphDisplayNode node in ordered)
                    {
                        Vector2 size = sizes[node];
                        // 自上而下消解重叠：尽量靠近期望位置，但不越过前节点底部。
                        float y = Math.Max(desired[node], cursor);
                        ctx.Rects[node] = new Rect(
                            StoryGraphMetrics.Margin + level * StoryGraphMetrics.ColumnGap,
                            y, size.x, size.y);
                        cursor = y + size.y
                            + StoryGraphMetrics.RowGapAfter(node);
                    }
                }
            }
        }

        private static bool TryParentBarycenter(
            StoryGraphDisplayNode node,
            ComponentContext ctx,
            Vector2 size,
            out float desiredTop)
        {
            double sum = 0;
            int count = 0;
            int nodeLevel = ctx.Level[node];
            List<StoryGraphDisplayNode> parents;
            if (ctx.Pred.TryGetValue(node, out parents))
            {
                foreach (StoryGraphDisplayNode parent in parents)
                {
                    // 只向更早的层靠拢；同层（SCC 内部）边不参与，避免互相拉扯。
                    if (ctx.Level[parent] >= nodeLevel) continue;
                    Rect rect;
                    if (!ctx.Rects.TryGetValue(parent, out rect)) continue;
                    Rect cardRect = StoryGraphMetrics.NodeCardRect(parent, rect);
                    sum += cardRect.y + cardRect.height * 0.5f;
                    count++;
                }
            }
            if (count == 0)
            {
                desiredTop = 0f;
                return false;
            }
            Rect localCard = StoryGraphMetrics.NodeCardRect(
                node, new Rect(0f, 0f, size.x, size.y));
            desiredTop = (float)(sum / count)
                - (localCard.y + localCard.height * 0.5f);
            return true;
        }

        // ====================================================================
        // 第五步前：分量内容外接范围（含回边通道与自环环，局部分量坐标）
        // ====================================================================

        private static void MeasureComponent(ComponentContext ctx)
        {
            float bottom = 0f, right = 0f;
            foreach (KeyValuePair<StoryGraphDisplayNode, Rect> pair in ctx.Rects)
            {
                bottom = Mathf.Max(bottom, pair.Value.yMax);
                right = Mathf.Max(right, pair.Value.xMax);
            }

            int backEdges = 0;
            var selfLoops = new Dictionary<StoryGraphDisplayNode, int>();
            foreach (IndexedEdge indexed in ctx.Edges)
            {
                StoryGraphDisplayEdge edge = indexed.Edge;
                if (ReferenceEquals(edge.From, edge.To))
                {
                    int count;
                    selfLoops.TryGetValue(edge.From, out count);
                    selfLoops[edge.From] = count + 1;
                    continue;
                }
                if (ctx.Level[edge.To] <= ctx.Level[edge.From]) backEdges++;
            }
            foreach (KeyValuePair<StoryGraphDisplayNode, int> pair in selfLoops)
            {
                Rect rect = ctx.Rects[pair.Key];
                // 多个自环逐个外移 SelfLoopStep，最外一个决定内容右缘。
                right = Mathf.Max(right,
                    rect.xMax + SelfLoopDepth + (pair.Value - 1) * SelfLoopStep);
            }

            // 回边通道在节点带上方逐档抬高：第 j 条回边（0 起）在 -(j+1)*Gap，
            // 最高通道即内容顶；无回边时内容顶 = 节点带顶 0。
            ctx.ContentTop = backEdges > 0
                ? -backEdges * StoryGraphMetrics.BackEdgeChannelGap
                : 0f;
            ctx.ContentBottom = bottom;
            ctx.ContentRight = right;
        }

        // ====================================================================
        // 第五步：布线（画布坐标）
        // ====================================================================

        private static void RouteEdges(
            ComponentContext ctx,
            float offsetY,
            Dictionary<StoryGraphDisplayNode, Rect> finalRects,
            List<KeyValuePair<int, StoryGraphRoutedEdge>> output)
        {
            int channel = 0;
            var selfLoopSeen = new Dictionary<StoryGraphDisplayNode, int>();
            var requests = new List<RouteRequest>();
            foreach (IndexedEdge indexed in ctx.Edges) // 已按全局输入顺序
            {
                StoryGraphDisplayEdge edge = indexed.Edge;
                Rect fromRect = StoryGraphMetrics.NodeCardRect(
                    edge.From, finalRects[edge.From]);
                Rect toRect = StoryGraphMetrics.NodeCardRect(
                    edge.To, finalRects[edge.To]);
                var request = new RouteRequest
                {
                    InputIndex = indexed.Index,
                    Edge = edge,
                    From = fromRect,
                    To = toRect,
                };

                if (ReferenceEquals(edge.From, edge.To))
                {
                    int ordinal;
                    selfLoopSeen.TryGetValue(edge.From, out ordinal);
                    selfLoopSeen[edge.From] = ordinal + 1;
                    request.Kind = RouteKind.SelfLoop;
                    request.SelfLoopOrdinal = ordinal;
                }
                else if (ctx.Level[edge.To] > ctx.Level[edge.From])
                {
                    request.Kind = RouteKind.Forward;
                    request.BaseCorridorX =
                        (fromRect.xMax + toRect.xMin) * 0.5f;
                    request.CorridorX = request.BaseCorridorX;
                }
                else
                {
                    // 回边按分配顺序每档抬高 BackEdgeChannelGap；
                    // 与 MeasureComponent 的口径一致，最高通道已被计入 Bounds。
                    float channelY = offsetY
                        - (channel + 1) * StoryGraphMetrics.BackEdgeChannelGap;
                    channel++;
                    request.Kind = RouteKind.Back;
                    request.ChannelY = channelY;
                }
                requests.Add(request);
            }

            AssignRouteLanes(requests);
            foreach (RouteRequest request in requests)
            {
                List<Vector2> points = BuildRoutePoints(request);
                output.Add(new KeyValuePair<int, StoryGraphRoutedEdge>(
                    request.InputIndex,
                    new StoryGraphRoutedEdge
                    {
                        Edge = request.Edge,
                        Points = points,
                        LabelPos = LongestSegmentMidpoint(points),
                    }));
            }
        }

        /// <summary>
        /// 为同一节点边缘和同一列间走廊分配稳定车道。排序只依赖节点坐标和
        /// 原始边序，因此自动整理、拖动后重布线及展开/收起都不会随机换轨。
        /// </summary>
        private static void AssignRouteLanes(List<RouteRequest> requests)
        {
            if (requests == null || requests.Count == 0) return;
            List<RouteRequest> forward = requests
                .Where(item => item.Kind == RouteKind.Forward).ToList();
            List<RouteRequest> back = requests
                .Where(item => item.Kind == RouteKind.Back).ToList();

            AssignForwardPortOffsets(forward, true);
            AssignForwardPortOffsets(forward, false);
            AssignForwardCorridors(forward);
            AssignBackPortOffsets(back, true);
            AssignBackPortOffsets(back, false);
        }

        private static void AssignForwardPortOffsets(
            List<RouteRequest> requests, bool sourceSide)
        {
            foreach (IGrouping<StoryGraphDisplayNode, RouteRequest> group
                     in requests.GroupBy(item => sourceSide
                         ? item.Edge.From : item.Edge.To))
            {
                List<RouteRequest> ordered = group.ToList();
                ordered.Sort((a, b) =>
                {
                    Rect ar = sourceSide ? a.To : a.From;
                    Rect br = sourceSide ? b.To : b.From;
                    int cmp = CenterY(ar).CompareTo(CenterY(br));
                    if (cmp != 0) return cmp;
                    cmp = CenterX(ar).CompareTo(CenterX(br));
                    return cmp != 0 ? cmp : a.InputIndex.CompareTo(b.InputIndex);
                });
                if (ordered.Count == 0) continue;
                Rect owner = sourceSide ? ordered[0].From : ordered[0].To;
                float halfSpan = Mathf.Max(
                    0f, owner.height * 0.5f - NodePortPadding);
                for (int i = 0; i < ordered.Count; i++)
                {
                    float offset = SymmetricSlotOffset(
                        i, ordered.Count, ForwardPortGap, halfSpan);
                    if (sourceSide) ordered[i].StartPortOffset = offset;
                    else ordered[i].EndPortOffset = offset;
                }
            }
        }

        private static void AssignBackPortOffsets(
            List<RouteRequest> requests, bool sourceSide)
        {
            foreach (IGrouping<StoryGraphDisplayNode, RouteRequest> group
                     in requests.GroupBy(item => sourceSide
                         ? item.Edge.From : item.Edge.To))
            {
                List<RouteRequest> ordered = group.ToList();
                ordered.Sort((a, b) =>
                {
                    Rect ar = sourceSide ? a.To : a.From;
                    Rect br = sourceSide ? b.To : b.From;
                    int cmp = CenterX(ar).CompareTo(CenterX(br));
                    if (cmp != 0) return cmp;
                    cmp = CenterY(ar).CompareTo(CenterY(br));
                    return cmp != 0 ? cmp : a.InputIndex.CompareTo(b.InputIndex);
                });
                if (ordered.Count == 0) continue;
                Rect owner = sourceSide ? ordered[0].From : ordered[0].To;
                float halfSpan = Mathf.Max(
                    0f, owner.width * 0.5f - NodePortPadding);
                for (int i = 0; i < ordered.Count; i++)
                {
                    float offset = SymmetricSlotOffset(
                        i, ordered.Count, BackPortGap, halfSpan);
                    if (sourceSide) ordered[i].StartPortOffset = offset;
                    else ordered[i].EndPortOffset = offset;
                }
            }
        }

        private static void AssignForwardCorridors(List<RouteRequest> requests)
        {
            if (requests.Count == 0) return;
            List<RouteRequest> ordered = requests
                .OrderBy(item => item.BaseCorridorX)
                .ThenBy(item => CenterY(item.From))
                .ThenBy(item => CenterY(item.To))
                .ThenBy(item => item.InputIndex)
                .ToList();
            int first = 0;
            while (first < ordered.Count)
            {
                int end = first + 1;
                while (end < ordered.Count
                       && ordered[end].BaseCorridorX
                          - ordered[end - 1].BaseCorridorX
                          <= ForwardLaneCluster)
                    end++;
                AssignForwardCorridorCluster(ordered, first, end);
                first = end;
            }
        }

        private static void AssignForwardCorridorCluster(
            List<RouteRequest> ordered, int first, int end)
        {
            if (end <= first) return;

            // 相同 x 走廊但纵向范围完全不相交的边可以安全复用同一轨道。
            // 这也防止展开远处的独立分量时，无端改变主剧情已有边的车道。
            List<RouteRequest> byVerticalRange = ordered
                .Skip(first).Take(end - first)
                .OrderBy(VerticalRangeMin)
                .ThenBy(VerticalRangeMax)
                .ThenBy(item => item.InputIndex)
                .ToList();
            int rangeFirst = 0;
            while (rangeFirst < byVerticalRange.Count)
            {
                int rangeEnd = rangeFirst + 1;
                float coveredTo = VerticalRangeMax(
                    byVerticalRange[rangeFirst]);
                while (rangeEnd < byVerticalRange.Count
                       && VerticalRangeMin(byVerticalRange[rangeEnd])
                          <= coveredTo + ForwardLaneCluster)
                {
                    coveredTo = Mathf.Max(
                        coveredTo, VerticalRangeMax(byVerticalRange[rangeEnd]));
                    rangeEnd++;
                }
                AssignForwardTrackGroup(
                    byVerticalRange.GetRange(
                        rangeFirst, rangeEnd - rangeFirst));
                rangeFirst = rangeEnd;
            }
        }

        private static void AssignForwardTrackGroup(
            List<RouteRequest> requests)
        {
            int count = requests.Count;
            if (count <= 0) return;
            requests.Sort((a, b) =>
            {
                int cmp = a.BaseCorridorX.CompareTo(b.BaseCorridorX);
                if (cmp != 0) return cmp;
                cmp = CenterY(a.From).CompareTo(CenterY(b.From));
                if (cmp != 0) return cmp;
                cmp = CenterY(a.To).CompareTo(CenterY(b.To));
                return cmp != 0 ? cmp : a.InputIndex.CompareTo(b.InputIndex);
            });
            if (count == 1)
            {
                requests[0].CorridorX = requests[0].BaseCorridorX;
                return;
            }

            // 取所有边都能安全使用的走廊交集；自动布局的同层卡片会形成宽裕
            // 交集。手工把卡片拖得过近时，下面还有逐边钳制的防御回退。
            float lower = float.MinValue;
            float upper = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                lower = Mathf.Max(lower,
                    requests[i].From.xMax + CorridorPadding);
                upper = Mathf.Min(upper,
                    requests[i].To.xMin - CorridorPadding);
            }
            if (upper >= lower)
            {
                float center = (lower + upper) * 0.5f;
                float gap = Mathf.Min(
                    ForwardLaneGap, (upper - lower) / (count - 1));
                for (int i = 0; i < count; i++)
                    requests[i].CorridorX = center
                        + (i - (count - 1) * 0.5f) * gap;
                return;
            }

            for (int i = 0; i < count; i++)
            {
                RouteRequest request = requests[i];
                float ownLower = request.From.xMax + 1f;
                float ownUpper = request.To.xMin - 1f;
                float halfSpan = Mathf.Max(
                    0f, (ownUpper - ownLower) * 0.5f);
                float desired = request.BaseCorridorX + SymmetricSlotOffset(
                    i, count, ForwardLaneGap, halfSpan);
                request.CorridorX = ownUpper >= ownLower
                    ? Mathf.Clamp(desired, ownLower, ownUpper)
                    : request.BaseCorridorX;
            }
        }

        private static float VerticalRangeMin(RouteRequest request)
        {
            float start = CenterY(request.From) + request.StartPortOffset;
            float end = CenterY(request.To) + request.EndPortOffset;
            return Mathf.Min(start, end);
        }

        private static float VerticalRangeMax(RouteRequest request)
        {
            float start = CenterY(request.From) + request.StartPortOffset;
            float end = CenterY(request.To) + request.EndPortOffset;
            return Mathf.Max(start, end);
        }

        private static float SymmetricSlotOffset(
            int index, int count, float preferredGap, float halfSpan)
        {
            if (count <= 1) return 0f;
            float gap = Mathf.Min(
                preferredGap, Mathf.Max(0f, halfSpan) * 2f / (count - 1));
            return (index - (count - 1) * 0.5f) * gap;
        }

        private static float CenterX(Rect rect)
        {
            return rect.x + rect.width * 0.5f;
        }

        private static float CenterY(Rect rect)
        {
            return rect.y + rect.height * 0.5f;
        }

        private static List<Vector2> BuildRoutePoints(RouteRequest request)
        {
            List<Vector2> points;
            switch (request.Kind)
            {
                case RouteKind.SelfLoop:
                    points = BuildSelfLoopPoints(
                        request.From, request.SelfLoopOrdinal);
                    break;
                case RouteKind.Back:
                    points = BuildBackEdgePoints(
                        request.From, request.To, request.ChannelY,
                        request.StartPortOffset, request.EndPortOffset);
                    break;
                default:
                    points = BuildForwardPoints(
                        request.From, request.To, request.CorridorX,
                        request.StartPortOffset, request.EndPortOffset);
                    break;
            }
            return DedupePoints(points);
        }

        private static List<Vector2> BuildForwardPoints(
            Rect from, Rect to, float corridorX,
            float startPortOffset, float endPortOffset)
        {
            // 每条边在源/目标卡片边缘都有独立接点，并在列间中缝拥有独立车道。
            // 跨多层边的竖直段仍可能穿过中间列；这里只解决同走廊边彼此遮蔽，
            // 不改变既有的无障碍绕行契约。
            var start = new Vector2(
                from.xMax, from.y + from.height * 0.5f + startPortOffset);
            var end = new Vector2(
                to.xMin, to.y + to.height * 0.5f + endPortOffset);
            return new List<Vector2>
            {
                start,
                new Vector2(corridorX, start.y),
                new Vector2(corridorX, end.y),
                end,
            };
        }

        private static List<Vector2> BuildBackEdgePoints(
            Rect from, Rect to, float channelY,
            float entryOffset, float exitOffset)
        {
            // 契约规定：回边从源顶缘出发走图上方通道，再落到目标顶缘。
            // 同列回边出入口横坐标相同会缩成零宽环线，出口横移 Nudge 错开
            // （仍落在目标顶缘上，节点宽度远大于 Nudge，不会越界）。
            float entryX = from.x + from.width * 0.5f + entryOffset;
            float exitX = to.x + to.width * 0.5f + exitOffset;
            if (Math.Abs(entryX - exitX) < PointEpsilon)
            {
                exitX += SameColumnNudge;
                if (exitX > to.xMax) exitX = to.xMax; // 防御：不越过目标右缘
            }
            return new List<Vector2>
            {
                new Vector2(entryX, from.yMin),
                new Vector2(entryX, channelY),
                new Vector2(exitX, channelY),
                new Vector2(exitX, to.yMin),
            };
        }

        private static List<Vector2> BuildSelfLoopPoints(Rect rect, int ordinal)
        {
            // 自环：右侧小矩形环；同一节点多个自环逐个外移避免完全重合。
            float right = rect.xMax + SelfLoopDepth + ordinal * SelfLoopStep;
            float centerY = rect.y + rect.height * 0.5f;
            return new List<Vector2>
            {
                new Vector2(rect.xMax, centerY - SelfLoopHalfHeight),
                new Vector2(right, centerY - SelfLoopHalfHeight),
                new Vector2(right, centerY + SelfLoopHalfHeight),
                new Vector2(rect.xMax, centerY + SelfLoopHalfHeight),
            };
        }

        private static List<Vector2> DedupePoints(List<Vector2> points)
        {
            // 去掉连续重复点（例如前向边两端同高时竖直段长度为零）。
            var result = new List<Vector2>(points.Count);
            foreach (Vector2 point in points)
            {
                if (result.Count > 0
                    && Vector2.Distance(result[result.Count - 1], point) < PointEpsilon)
                    continue;
                result.Add(point);
            }
            return result;
        }

        private static Vector2 LongestSegmentMidpoint(List<Vector2> points)
        {
            // LabelPos = 折线最长段中点；并列取先出现的一段，保证确定。
            if (points == null || points.Count == 0) return Vector2.zero;
            if (points.Count == 1) return points[0];
            float best = -1f;
            Vector2 mid = points[0];
            for (int i = 0; i + 1 < points.Count; i++)
            {
                float length = Vector2.Distance(points[i], points[i + 1]);
                if (length > best)
                {
                    best = length;
                    mid = (points[i] + points[i + 1]) * 0.5f;
                }
            }
            return mid;
        }

        // ====================================================================
        // 排序工具与内部数据结构（全部私有，不污染命名空间）
        // ====================================================================

        private static int CompareNodes(StoryGraphDisplayNode a, StoryGraphDisplayNode b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int cmp = NodeSortRank(a).CompareTo(NodeSortRank(b));
            if (cmp != 0) return cmp;
            cmp = a.Id.CompareTo(b.Id);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(a.Key ?? string.Empty, b.Key ?? string.Empty);
        }

        private static int NodeSortRank(StoryGraphDisplayNode node)
        {
            if (node == null) return 99;
            // Segment 只是若干 Talk 的查看投影，Id 也是首句 Talk.id。若按独立
            // Segment Kind 排序，展开时它变回 Talk 后会跨过同层另一分支，造成
            // 上下位置互换。始终按 Talk 身份排序即可保持折叠前后分支顺序。
            return node.Kind == StoryGraphDisplayNodeKind.Segment
                ? (int)StoryGraphDisplayNodeKind.Talk
                : (int)node.Kind;
        }

        private static StoryGraphDisplayNode MinNode(List<StoryGraphDisplayNode> nodes)
        {
            StoryGraphDisplayNode best = null;
            foreach (StoryGraphDisplayNode node in nodes)
                if (best == null || CompareNodes(node, best) < 0) best = node;
            return best;
        }

        private sealed class DfsFrame
        {
            internal StoryGraphDisplayNode Node;
            internal int NextIndex;
        }

        private enum RouteKind
        {
            Forward,
            Back,
            SelfLoop,
        }

        /// <summary>一条边在分轨前后的全部瞬时布线数据。</summary>
        private sealed class RouteRequest
        {
            internal int InputIndex;
            internal StoryGraphDisplayEdge Edge;
            internal Rect From;
            internal Rect To;
            internal RouteKind Kind;
            internal int SelfLoopOrdinal;
            internal float ChannelY;
            internal float BaseCorridorX;
            internal float CorridorX;
            internal float StartPortOffset;
            internal float EndPortOffset;
        }

        private struct IndexedEdge
        {
            internal int Index; // 在原始 edges 列表中的下标
            internal StoryGraphDisplayEdge Edge;
        }

        /// <summary>同层排序的最小单位：一个 SCC 的相邻成员堆。</summary>
        private sealed class Block
        {
            internal readonly List<StoryGraphDisplayNode> Members =
                new List<StoryGraphDisplayNode>();
            internal float Barycenter;
        }

        /// <summary>一个连通分量的全部布局中间状态。</summary>
        private sealed class ComponentContext
        {
            internal readonly List<StoryGraphDisplayNode> Nodes =
                new List<StoryGraphDisplayNode>();
            internal readonly List<IndexedEdge> Edges = new List<IndexedEdge>();
            internal readonly Dictionary<StoryGraphDisplayNode, List<StoryGraphDisplayNode>> Succ =
                new Dictionary<StoryGraphDisplayNode, List<StoryGraphDisplayNode>>();
            internal readonly Dictionary<StoryGraphDisplayNode, List<StoryGraphDisplayNode>> Pred =
                new Dictionary<StoryGraphDisplayNode, List<StoryGraphDisplayNode>>();
            internal readonly Dictionary<StoryGraphDisplayNode, int> Scc =
                new Dictionary<StoryGraphDisplayNode, int>();
            internal readonly Dictionary<StoryGraphDisplayNode, int> Level =
                new Dictionary<StoryGraphDisplayNode, int>();
            internal readonly Dictionary<int, List<Block>> Layers =
                new Dictionary<int, List<Block>>();
            internal readonly Dictionary<StoryGraphDisplayNode, Rect> Rects =
                new Dictionary<StoryGraphDisplayNode, Rect>();
            internal List<List<StoryGraphDisplayNode>> SccMembers =
                new List<List<StoryGraphDisplayNode>>();
            internal float ContentTop;    // 局部内容顶（含回边通道，可为负）
            internal float ContentBottom; // 局部内容底（节点带底）
            internal float ContentRight;  // 局部内容右缘（含自环环）
        }
    }
}
