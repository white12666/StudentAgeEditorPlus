using System.Collections.Generic;
using System.Linq;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 原版的 CG、迷你 CG 与漫画都是跨 Talk 持续的界面状态，只有 4017（或退出
    /// 整个剧情视图）会明确关闭。这里按剧情图的真实运行边检查每一个开始节点，
    /// 找出至少一条分支在关闭前就结束、跳出当前事件或落到坏引用的情况。
    /// </summary>
    internal static class StoryGraphCgFlowAnalyzer
    {
        private const int ShowCg = 4015;
        private const int ShowComic = 4016;
        private const int CloseCg = 4017;
        private const int ShowMiniCg = 4019;

        internal static void Annotate(EvtStoryGraphModel model)
        {
            if (model == null) return;

            foreach (EvtStoryGraphNode node in model.Nodes)
            {
                int rawCode = RawScreenCode(node);
                if (!IsOverlayCommand(rawCode) || !IsTalk(node)
                    || !string.IsNullOrWhiteSpace(node.Talk.content))
                    continue;
                node.Flags |= EvtStoryGraphNodeFlags.InvalidData;
                AddAuthorWarning(
                    node,
                    "这条对话正文为空；原版会直接跳过整句，因此这里配置的 "
                    + rawCode + " 不会执行。请先填写正文，或把 CG 指令移到有正文的节点。");
                if (model.Diagnostics.Count < 16)
                    model.Diagnostics.Add(
                        "对话 " + node.Id + " 的正文为空，CG 指令 "
                        + rawCode + " 会被游戏跳过。");
            }

            int unclosedStarts = 0;
            int firstStartId = 0;
            foreach (EvtStoryGraphNode start in model.Nodes)
            {
                if (!IsTalk(start) || start.HasFlag(EvtStoryGraphNodeFlags.Unreachable)
                    || !StartsOverlay(ScreenCode(start)))
                    continue;

                var jumpOuts = new HashSet<EvtStoryGraphNode>();
                var optionDeadEnds = new HashSet<EvtStoryGraphNode>();
                var naturalEnds = new HashSet<EvtStoryGraphNode>();
                CollectOverlayEndpoints(start, jumpOuts, optionDeadEnds, naturalEnds);

                // 纯对话终端：链条自然结束时 NewTalkView.CloseView 销毁整个剧情
                // 视图，CG 随之关闭，没有泄漏（原版大量 CG 事件即此形状）。只给
                // 可选提示，不标危险红、不计入保存二次确认。
                foreach (EvtStoryGraphNode endpoint in naturalEnds)
                    AddAuthorWarning(
                        endpoint,
                        "CG 将随剧情结束自动关闭；如需提前恢复对话画面"
                        + "可在此前加入 4017（可选）。");

                if (jumpOuts.Count == 0 && optionDeadEnds.Count == 0) continue;

                unclosedStarts++;
                if (firstStartId == 0) firstStartId = start.Id;
                MarkWarning(
                    start,
                    "CG/漫画从这里开始，但至少一条可达分支在跳出当前剧情图"
                    + "或选项收尾前没有执行“关闭 CG（4017）”。");
                foreach (EvtStoryGraphNode endpoint in jumpOuts)
                {
                    if (ReferenceEquals(endpoint, start)) continue;
                    MarkWarning(
                        endpoint,
                        "执行到这里会离开当前剧情图（跳转其它事件/跨组/引用缺失），"
                        + "CG/漫画仍处于显示状态并会带进后续画面；"
                        + "请在此前分支加入“关闭 CG（4017）”。");
                }
                foreach (EvtStoryGraphNode endpoint in optionDeadEnds)
                {
                    if (ReferenceEquals(endpoint, start)) continue;
                    MarkWarning(
                        endpoint,
                        "这个选项收尾时没有后续剧情跳转，CG/漫画仍处于显示状态；"
                        + "若事件队列中还有下一个事件会把画面带过去，"
                        + "请在此前加入“关闭 CG（4017）”。");
                }
            }

            model.CgUnclosedCount = unclosedStarts;
            if (unclosedStarts > 0 && model.Diagnostics.Count < 16)
            {
                model.Diagnostics.Add(
                    "检测到 " + unclosedStarts
                    + " 个 CG/漫画开始节点存在未关闭分支（跳出剧情图或选项死端收尾）；"
                    + "首个是对话 " + firstStartId + "。保存时会要求再次确认。");
            }
        }

        /// <summary>
        /// 从覆盖层开始节点沿运行边遍历，把关闭前就离开覆盖层生命周期的端点分为
        /// 三类：跳出剧情图（事件跳转/跨组/坏引用——画面带进后续剧情）、选项死端
        /// （收尾后事件队列的下一个事件仍在同一视图播放）、纯对话自然结束
        /// （CloseView 销毁整个视图，无泄漏）。
        /// </summary>
        private static void CollectOverlayEndpoints(
            EvtStoryGraphNode start,
            HashSet<EvtStoryGraphNode> jumpOuts,
            HashSet<EvtStoryGraphNode> optionDeadEnds,
            HashSet<EvtStoryGraphNode> naturalEnds)
        {
            var visited = new HashSet<EvtStoryGraphNode>();
            var queue = new Queue<EvtStoryGraphNode>();
            visited.Add(start);
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                EvtStoryGraphNode current = queue.Dequeue();
                if (!ReferenceEquals(current, start) && IsTalk(current))
                {
                    int code = ScreenCode(current);
                    // 新的覆盖层会取代此前的覆盖层；由它自己的分析负责后续闭合。
                    if (code == CloseCg || StartsOverlay(code)) continue;
                }

                if (IsBoundaryNode(current))
                {
                    jumpOuts.Add(current);
                    continue;
                }

                List<EvtStoryGraphEdge> outgoing = current.Outgoing
                    .Where(edge => edge != null && edge.IsRuntimeEdge)
                    .ToList();
                if (outgoing.Count == 0)
                {
                    if (current != null
                        && current.Kind == EvtStoryGraphNodeKind.Option)
                        optionDeadEnds.Add(current);
                    else if (IsTalk(current))
                        naturalEnds.Add(current);
                    else
                        jumpOuts.Add(current);
                    continue;
                }

                foreach (EvtStoryGraphEdge edge in outgoing)
                {
                    EvtStoryGraphNode target = edge.To;
                    bool leavesGraph = edge.Kind == EvtStoryGraphEdgeKind.NextEvent
                        || (edge.Flags & (EvtStoryGraphEdgeFlags.BrokenReference
                                          | EvtStoryGraphEdgeFlags.ExternalJump)) != 0
                        || target == null
                        || IsBoundaryNode(target);
                    if (leavesGraph)
                    {
                        jumpOuts.Add(target ?? current);
                        continue;
                    }

                    if (IsTalk(target))
                    {
                        int code = ScreenCode(target);
                        if (code == CloseCg || StartsOverlay(code)) continue;
                    }
                    if (visited.Add(target)) queue.Enqueue(target);
                }
            }
        }

        private static bool IsTalk(EvtStoryGraphNode node)
        {
            return node != null
                   && node.Kind == EvtStoryGraphNodeKind.Talk
                   && node.Talk != null;
        }

        private static bool IsBoundaryNode(EvtStoryGraphNode node)
        {
            if (node == null) return true;
            return node.Kind == EvtStoryGraphNodeKind.MissingTalk
                   || node.Kind == EvtStoryGraphNodeKind.MissingOption
                   || node.Kind == EvtStoryGraphNodeKind.MissingEvent
                   || node.Kind == EvtStoryGraphNodeKind.ExternalTalk
                   || node.Kind == EvtStoryGraphNodeKind.ExternalEvent;
        }

        private static int ScreenCode(EvtStoryGraphNode node)
        {
            return IsTalk(node) && !string.IsNullOrWhiteSpace(node.Talk.content)
                ? RawScreenCode(node)
                : 0;
        }

        private static int RawScreenCode(EvtStoryGraphNode node)
        {
            return node?.Talk?.screenEffect != null
                   && node.Talk.screenEffect.Count > 0
                ? (int)node.Talk.screenEffect[0]
                : 0;
        }

        private static bool StartsOverlay(int code)
        {
            return code == ShowCg || code == ShowComic || code == ShowMiniCg;
        }

        private static bool IsOverlayCommand(int code)
        {
            return StartsOverlay(code) || code == CloseCg;
        }

        private static void MarkWarning(EvtStoryGraphNode node, string warning)
        {
            if (node == null) return;
            node.Flags |= EvtStoryGraphNodeFlags.CgNotClosed;
            AddAuthorWarning(node, warning);
        }

        private static void AddAuthorWarning(
            EvtStoryGraphNode node, string warning)
        {
            if (node == null) return;
            if (!string.IsNullOrEmpty(warning)
                && !node.AuthorWarnings.Contains(warning))
                node.AuthorWarnings.Add(warning);
        }
    }
}
