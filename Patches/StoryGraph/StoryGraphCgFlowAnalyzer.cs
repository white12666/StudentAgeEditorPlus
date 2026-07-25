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

                HashSet<EvtStoryGraphNode> endpoints = FindLeakEndpoints(start);
                if (endpoints.Count == 0) continue;

                unclosedStarts++;
                if (firstStartId == 0) firstStartId = start.Id;
                MarkWarning(
                    start,
                    "CG/漫画从这里开始，但至少一条可达分支在剧情结束或离开当前剧情图前"
                    + "没有执行“关闭 CG（4017）”。");
                foreach (EvtStoryGraphNode endpoint in endpoints)
                {
                    if (ReferenceEquals(endpoint, start)) continue;
                    MarkWarning(
                        endpoint,
                        "执行到这里时 CG/漫画仍处于显示状态；请在此前分支加入"
                        + "“关闭 CG（4017）”。");
                }
            }

            model.CgUnclosedCount = unclosedStarts;
            if (unclosedStarts > 0 && model.Diagnostics.Count < 16)
            {
                model.Diagnostics.Add(
                    "检测到 " + unclosedStarts
                    + " 个 CG/漫画开始节点存在未关闭分支；首个是对话 "
                    + firstStartId + "。保存时会要求再次确认。");
            }
        }

        private static HashSet<EvtStoryGraphNode> FindLeakEndpoints(
            EvtStoryGraphNode start)
        {
            var endpoints = new HashSet<EvtStoryGraphNode>();
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
                    endpoints.Add(current);
                    continue;
                }

                List<EvtStoryGraphEdge> outgoing = current.Outgoing
                    .Where(edge => edge != null && edge.IsRuntimeEdge)
                    .ToList();
                if (outgoing.Count == 0)
                {
                    endpoints.Add(current);
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
                        endpoints.Add(target ?? current);
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
            return endpoints;
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
