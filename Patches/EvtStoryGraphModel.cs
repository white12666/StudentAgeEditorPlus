using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Config;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图只读取 ModEvtEditView 当前持有的 TalkCfg / OptionCfg 对象。
    /// 模型不会改写配置，也不会调用会依据性别/条件改变结果的运行时方法；
    /// 所有原始分支值都会保留为边，运行时不会读取的多余列表项会明确标灰。
    /// </summary>
    internal enum EvtStoryGraphNodeKind
    {
        Event,
        Talk,
        Option,
        MissingTalk,
        MissingOption,
        MissingEvent,
        ExternalTalk,
        ExternalEvent,
    }

    [Flags]
    internal enum EvtStoryGraphNodeFlags
    {
        None = 0,
        Entry = 1 << 0,
        Terminal = 1 << 1,
        Unreachable = 1 << 2,
        Cycle = 1 << 3,
        SelfLoop = 1 << 4,
        MissingReference = 1 << 5,
        External = 1 << 6,
        DuplicateId = 1 << 7,
        OrphanOption = 1 << 8,
        InvalidData = 1 << 9,
        SharedOption = 1 << 10,
        CgNotClosed = 1 << 11,
    }

    internal enum EvtStoryGraphEdgeKind
    {
        EventEntry,
        NextTalk,
        NextTalk2,
        TalkOption,
        OptionTalk,
        OptionTalk2,
        NextEvent,
    }

    [Flags]
    internal enum EvtStoryGraphEdgeFlags
    {
        None = 0,
        RuntimeIgnored = 1 << 0,
        RuntimeFallback = 1 << 1,
        BrokenReference = 1 << 2,
        ExternalJump = 1 << 3,
    }

    internal sealed class EvtStoryGraphNode
    {
        internal string Key;
        internal EvtStoryGraphNodeKind Kind;
        internal EvtStoryGraphNodeFlags Flags;
        internal int Id;
        internal string Title;
        internal string Subtitle;
        internal string SearchText;
        internal TalkCfg Talk;
        internal OptionCfg Option;
        internal EvtCfg ParentEvent;

        /// <summary>
        /// Talk 节点就是自身；Option 节点保存首个引用它的父 Talk，供点击时定位。
        /// 孤立选项和占位节点可以为 null。
        /// </summary>
        internal TalkCfg LocateTalk;

        internal readonly List<EvtStoryGraphEdge> Incoming = new List<EvtStoryGraphEdge>();
        internal readonly List<EvtStoryGraphEdge> Outgoing = new List<EvtStoryGraphEdge>();
        internal readonly List<string> AuthorWarnings = new List<string>();

        internal bool HasFlag(EvtStoryGraphNodeFlags flag)
        {
            return (Flags & flag) != 0;
        }
    }

    internal sealed class EvtStoryGraphEdge
    {
        internal EvtStoryGraphNode From;
        internal EvtStoryGraphNode To;
        internal EvtStoryGraphEdgeKind Kind;
        internal EvtStoryGraphEdgeFlags Flags;
        internal string Label;
        internal int SourceIndex;

        internal bool IsRuntimeEdge
        {
            get { return (Flags & EvtStoryGraphEdgeFlags.RuntimeIgnored) == 0; }
        }
    }

    internal sealed class EvtStoryGraphModel
    {
        internal int EventId;
        internal EvtStoryGraphNode EventNode;
        internal readonly List<EvtStoryGraphNode> Nodes = new List<EvtStoryGraphNode>();
        internal readonly List<EvtStoryGraphEdge> Edges = new List<EvtStoryGraphEdge>();
        internal readonly List<string> Diagnostics = new List<string>();

        internal int TalkCount;
        internal int OptionCount;
        internal int MissingCount;
        internal int ExternalCount;
        internal int UnreachableCount;
        internal int CycleCount;
        internal int TerminalCount;
        internal int DuplicateCount;
        internal int CgUnclosedCount;

        internal string Summary
        {
            get
            {
                return string.Format(
                    "对话 {0}　选项 {1}　连线 {2}　结束节点 {3}　无法到达 {4}　循环 {5}　缺失引用 {6}　跨组跳转 {7}　CG未关闭 {8}",
                    TalkCount, OptionCount, Edges.Count, TerminalCount,
                    UnreachableCount, CycleCount, MissingCount, ExternalCount,
                    CgUnclosedCount);
            }
        }
    }

    /// <summary>
    /// 对话正文播完后是否进入选项界面。剧情图建模与保存前预检共用这一判定，
    /// 两边对"游戏会不会读取 nextTalk"不能各说各话。
    /// </summary>
    internal static class StoryGraphTalkFlow
    {
        internal static bool OptionsIntercept(
            string content, IEnumerable<int> optionIds, bool stateEventView)
        {
            // StateEvtView 的透传判定是 content.IsEmpty()==IsNullOrEmpty，
            // 纯空白正文会正常显示并停在选项处；NewTalkView（RefreshTalk）
            // 用 IsNullOrWhiteSpace，两条路径判定不同。
            bool contentShown = stateEventView
                ? !string.IsNullOrEmpty(content)
                : !string.IsNullOrWhiteSpace(content);
            return contentShown && optionIds != null && optionIds.Any(id => id != 0);
        }
    }

    internal static class EvtStoryGraphModelBuilder
    {
        private sealed class DfsFrame
        {
            internal EvtStoryGraphNode Node;
            internal int NextIndex;
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

            public bool Equals(T x, T y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(T obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }

        private sealed class BuildContext
        {
            internal readonly EvtStoryGraphModel Model = new EvtStoryGraphModel();
            internal readonly Dictionary<int, List<EvtStoryGraphNode>> TalksById =
                new Dictionary<int, List<EvtStoryGraphNode>>();
            internal readonly Dictionary<int, EvtStoryGraphNode> OptionsByKey =
                new Dictionary<int, EvtStoryGraphNode>();
            internal readonly Dictionary<EvtStoryGraphNode, Dictionary<int, EvtStoryGraphNode>>
                OptionUses = new Dictionary<EvtStoryGraphNode, Dictionary<int, EvtStoryGraphNode>>();
            internal readonly Dictionary<EvtStoryGraphNode, bool> OptionContextActive =
                new Dictionary<EvtStoryGraphNode, bool>();
            internal readonly Dictionary<EvtStoryGraphNode, EvtCfg> OptionEventParents =
                new Dictionary<EvtStoryGraphNode, EvtCfg>();
            internal readonly HashSet<EvtStoryGraphNode> ClaimedOptionTemplates =
                new HashSet<EvtStoryGraphNode>();
            internal readonly Dictionary<string, EvtStoryGraphNode> Placeholders =
                new Dictionary<string, EvtStoryGraphNode>(StringComparer.Ordinal);
            internal readonly HashSet<int> KnownTalkGroups = new HashSet<int>();
            internal ISet<int> KnownTalkIds;
            internal ISet<int> KnownEventIds;
            internal IDictionary<int, string> PersonNames;
            internal EvtCfg EventConfig;
            internal int EventId;
            internal int NodeSerial;

            /// <summary>
            /// 事件按 StateEvtView 播放（type==60 或 displayType==1）：
            /// talk.check/nextTalk2 与 option.nextEvtId 从不读取，
            /// 选项 talkId/talkId2 阈值为 >0 而非 >1。
            /// </summary>
            internal bool StateEventView;
        }

        internal static EvtStoryGraphModel Build(
            IList<TalkCfg> talkCfgs,
            IDictionary<int, OptionCfg> optionCfgs,
            int evtId,
            IEnumerable<int> entryTalkIds,
            bool entriesKnown = true,
            IDictionary<int, string> personNames = null,
            ISet<int> knownTalkIds = null,
            ISet<int> knownEventIds = null,
            EvtCfg eventConfig = null)
        {
            var context = new BuildContext
            {
                EventId = evtId,
                PersonNames = personNames,
                KnownTalkIds = knownTalkIds != null
                    ? new HashSet<int>(knownTalkIds)
                    : null,
                KnownEventIds = knownEventIds != null
                    ? new HashSet<int>(knownEventIds)
                    : null,
                EventConfig = eventConfig,
            };
            context.StateEventView = IsStateEventViewConfig(eventConfig);
            EvtStoryGraphModel model = context.Model;
            model.EventId = evtId;
            if (context.StateEventView)
                AddDiagnostic(model,
                    "本事件按状态演出(StateEvtView)播放：对话判断(check)不求值、"
                    + "选项的 nextEvtId 不读取、选项分支落空时直接关闭界面。");

            List<int> entries = SafeCopyIds(entryTalkIds, model, "事件入口");

            AddEventNode(context);
            AddTalkNodes(context, talkCfgs);
            AddOptionNodes(context, optionCfgs);
            AddTalkEdges(context);
            AddEventOptionEdges(context);
            AnnotateSharedOptionUses(context);
            AddOptionEdges(context);
            AddEntryEdges(context, entries, entriesKnown);
            MarkTerminalNodes(model);
            MarkReachability(model);
            MarkCycles(model);
            StoryGraphCgFlowAnalyzer.Annotate(model);
            FinishStatistics(model);
            return model;
        }

        /// <summary>CommonEvtMgr 分发：type==60 或 displayType==1 走 StateEvtView。</summary>
        private static bool IsStateEventViewConfig(EvtCfg evt)
        {
            try
            {
                return evt != null && (evt.type == 60 || evt.displayType == 1);
            }
            catch
            {
                return false;
            }
        }

        private static void AddEventNode(BuildContext context)
        {
            var details = new List<string> { "该事件的剧情入口" };
            if (context.StateEventView)
                details.Add("状态演出事件(StateEvtView)");
            EvtCfg evt = context.EventConfig;
            if (evt != null)
            {
                int optionCount = evt.options != null
                    ? evt.options.Count(value => value != 0)
                    : 0;
                if (optionCount > 0)
                {
                    string optionText = "事件级选项 " + optionCount + " 个";
                    if (evt.maxoptions > 0 && evt.maxoptions < optionCount)
                        optionText += "（每次随机显示 " + evt.maxoptions + " 个）";
                    details.Add(optionText);
                }
                if (HasItems(evt.miniGame))
                {
                    int gameId;
                    string ignored;
                    if (MiniGameUtil.TryGetGameId(
                            evt.miniGame, out gameId, out ignored))
                        details.Add("事件小游戏 " + MiniGameUtil.GameName(gameId));
                    else
                        details.Add("事件小游戏配置异常");
                }
            }
            var node = new EvtStoryGraphNode
            {
                Key = "event:" + context.EventId,
                Kind = EvtStoryGraphNodeKind.Event,
                Id = context.EventId,
                Title = "事件 " + context.EventId,
                Subtitle = string.Join("　", details.ToArray()),
                SearchText = "事件 event evt " + context.EventId,
            };
            context.Model.EventNode = node;
            context.Model.Nodes.Add(node);
        }

        private static void AddTalkNodes(BuildContext context, IList<TalkCfg> source)
        {
            var talks = new List<TalkCfg>();
            if (source != null)
            {
                try
                {
                    for (int i = 0; i < source.Count; i++)
                    {
                        TalkCfg talk = null;
                        try { talk = source[i]; }
                        catch (Exception e)
                        {
                            AddDiagnostic(context.Model,
                                "读取对话列表 talkCfgs[" + i + "] 失败：" + ShortError(e));
                        }
                        if (talk == null)
                        {
                            AddDiagnostic(context.Model,
                                "对话列表 talkCfgs[" + i + "] 为空，已跳过。", 8);
                            continue;
                        }
                        talks.Add(talk);
                    }
                }
                catch (Exception e)
                {
                    AddDiagnostic(context.Model, "读取对话列表 talkCfgs 失败：" + ShortError(e));
                }
            }

            // 同一个对象可能因多个入口组被编辑器重复加入；它仍是同一条保存记录，
            // 不应被画成重复节点。不同对象使用同一 ID 才是真正的保存覆盖冲突。
            var seenObjects = new HashSet<TalkCfg>(ReferenceComparer<TalkCfg>.Instance);
            talks = talks.Where(talk => seenObjects.Add(talk)).ToList();

            var counts = new Dictionary<int, int>();
            foreach (TalkCfg talk in talks)
            {
                int count;
                counts.TryGetValue(talk.id, out count);
                counts[talk.id] = count + 1;
                if (talk.id > 0) context.KnownTalkGroups.Add(GetTalkGroup(talk.id));
            }

            var ordinals = new Dictionary<int, int>();
            foreach (TalkCfg talk in talks)
            {
                int ordinal;
                ordinals.TryGetValue(talk.id, out ordinal);
                ordinal++;
                ordinals[talk.id] = ordinal;

                string role = BuildRoleSummary(talk, context.PersonNames);
                string line = Summarize(talk.content, 48);
                if (string.IsNullOrEmpty(line)) line = "（空台词）";
                string performance = StoryGraphPerformanceCodec.BuildTalkBadges(talk);
                string subtitle = role + "｜" + line;
                if (!string.IsNullOrEmpty(performance))
                    subtitle += "\n" + performance;

                var node = new EvtStoryGraphNode
                {
                    Key = "talk:" + talk.id + ":" + ordinal + ":" + (++context.NodeSerial),
                    Kind = EvtStoryGraphNodeKind.Talk,
                    Id = talk.id,
                    Talk = talk,
                    LocateTalk = talk,
                    Title = "对话 " + talk.id,
                    Subtitle = subtitle,
                    SearchText = JoinSearchText(
                        talk.id.ToString(), talk.content, talk.showTxt,
                        talk.roleName, role, performance),
                };
                if (counts[talk.id] > 1)
                {
                    node.Flags |= EvtStoryGraphNodeFlags.DuplicateId;
                    node.Subtitle += "　[编号重复 " + ordinal + "/" + counts[talk.id] + "]";
                }
                if (talk.id <= 0)
                    node.Flags |= EvtStoryGraphNodeFlags.InvalidData;

                List<EvtStoryGraphNode> sameId;
                if (!context.TalksById.TryGetValue(talk.id, out sameId))
                {
                    sameId = new List<EvtStoryGraphNode>();
                    context.TalksById.Add(talk.id, sameId);
                }
                sameId.Add(node);
                context.Model.Nodes.Add(node);
            }

            foreach (KeyValuePair<int, List<EvtStoryGraphNode>> pair in context.TalksById)
            {
                if (pair.Value.Count <= 1) continue;
                AddDiagnostic(context.Model,
                    "对话 " + pair.Key + " 存在 " + pair.Value.Count
                    + " 份不同配置；保存后后面的配置会覆盖前面的配置。", 12);
            }
        }

        private static void AddOptionNodes(
            BuildContext context, IDictionary<int, OptionCfg> source)
        {
            var entries = new List<KeyValuePair<int, OptionCfg>>();
            if (source != null)
            {
                try
                {
                    foreach (KeyValuePair<int, OptionCfg> pair in source)
                        entries.Add(pair);
                }
                catch (Exception e)
                {
                    AddDiagnostic(context.Model, "读取选项列表 optionCfgs 失败：" + ShortError(e));
                }
            }
            entries.Sort((a, b) => a.Key.CompareTo(b.Key));

            var valueIdCounts = new Dictionary<int, int>();
            foreach (KeyValuePair<int, OptionCfg> pair in entries)
            {
                if (pair.Value == null) continue;
                int count;
                valueIdCounts.TryGetValue(pair.Value.id, out count);
                valueIdCounts[pair.Value.id] = count + 1;
            }

            foreach (KeyValuePair<int, OptionCfg> pair in entries)
            {
                int key = pair.Key;
                OptionCfg option = pair.Value;
                string line = option != null ? Summarize(option.content, 48) : "（配置为空）";
                if (string.IsNullOrEmpty(line)) line = "（空选项）";
                string performance = option != null
                    ? StoryGraphPerformanceCodec.BuildOptionBadges(option)
                    : string.Empty;
                string subtitle = line;
                if (!string.IsNullOrEmpty(performance))
                    subtitle += "\n" + performance;

                var node = new EvtStoryGraphNode
                {
                    Key = "option:" + key + ":" + (++context.NodeSerial),
                    Kind = EvtStoryGraphNodeKind.Option,
                    Id = key,
                    Option = option,
                    Title = "选项 " + key,
                    Subtitle = subtitle,
                    SearchText = JoinSearchText(
                        key.ToString(), option != null ? option.content : null,
                        option != null ? option.showTxt : null,
                        option != null ? option.tag : null, performance),
                };

                if (option == null)
                {
                    node.Flags |= EvtStoryGraphNodeFlags.InvalidData;
                    AddDiagnostic(context.Model,
                        "选项列表 optionCfgs[" + key + "] 为空，已保留并标记为异常配置。", 8);
                }
                else
                {
                    if (option.id != key)
                    {
                        node.Flags |= EvtStoryGraphNodeFlags.InvalidData;
                        node.Subtitle += "　[配置编号与字典键不一致：id=" + option.id + "]";
                    }
                    int sameValueId;
                    if (valueIdCounts.TryGetValue(option.id, out sameValueId)
                        && sameValueId > 1)
                        node.Flags |= EvtStoryGraphNodeFlags.DuplicateId;
                }

                // Dictionary 本身不允许重复 key；异常实现若仍给出重复项，保留第一个。
                if (context.OptionsByKey.ContainsKey(key))
                {
                    node.Flags |= EvtStoryGraphNodeFlags.DuplicateId;
                    AddDiagnostic(context.Model,
                        "选项字典出现重复键 " + key + "，引用将定位到第一项。", 8);
                }
                else
                {
                    context.OptionsByKey.Add(key, node);
                }
                context.Model.Nodes.Add(node);
            }
        }

        private static void AddTalkEdges(BuildContext context)
        {
            List<EvtStoryGraphNode> talkNodes = context.Model.Nodes
                .Where(n => n.Kind == EvtStoryGraphNodeKind.Talk).ToList();

            foreach (EvtStoryGraphNode node in talkNodes)
            {
                TalkCfg talk = node.Talk;
                if (talk == null) continue;
                List<int> next = SafeCopyIds(talk.nextTalk, context.Model,
                    "对话 " + talk.id + " 的下一句字段（nextTalk）");
                List<int> next2 = SafeCopyIds(talk.nextTalk2, context.Model,
                    "对话 " + talk.id + " 的备用分支字段（nextTalk2）");
                List<int> optionIds = SafeCopyIds(talk.option, context.Model,
                    "对话 " + talk.id + " 的选项字段（option）");
                bool hasOptionValues = optionIds.Any(id => id != 0);
                int validOptionCount = optionIds.Count(id => id != 0);
                if (talk.maxoptions > 0
                    && talk.maxoptions < validOptionCount)
                {
                    node.Subtitle = (node.Subtitle ?? string.Empty)
                                  + "　[每次随机显示 "
                                  + talk.maxoptions + "/"
                                  + validOptionCount + " 个选项]";
                    AddDiagnostic(context.Model,
                        "对话 " + talk.id + " 配置了 maxoptions="
                        + talk.maxoptions + "；图中展示全部候选，游戏每次会随机显示其中 "
                        + talk.maxoptions + " 个。", 16);
                }
                bool optionsIntercept = StoryGraphTalkFlow.OptionsIntercept(
                    talk.content, optionIds, context.StateEventView);
                bool hasMiniGame = HasItems(talk.miniGame);
                if (context.StateEventView && HasItems(talk.check))
                {
                    node.AuthorWarnings.Add(
                        "状态演出事件不求值对话判断（check），成立/失败分支不会分流。");
                    AddDiagnostic(context.Model,
                        "对话 " + talk.id + " 配置了判断条件（check）；"
                        + "状态演出事件不求值该字段，成立/失败分支不会分流。");
                }
                bool miniGameValid = ValidateMiniGameConfig(
                    context, node, talk.miniGame, true, "对话 " + talk.id);
                if (hasMiniGame && miniGameValid)
                    ValidateMiniGameFlowTargets(context, node, talk.miniGame,
                        next, next2, true, "对话 " + talk.id);

                AddTalkOptionEdges(context, node, optionIds, optionsIntercept);

                if (optionsIntercept)
                {
                    // 正文结束后进入选项 UI，父 Talk 的直接 NextTalk 不会执行。
                    AddTalkTargetList(context, node, next,
                        EvtStoryGraphEdgeKind.NextTalk,
                        "直接下一句（nextTalk，已被选项流程覆盖）", true, false);
                    AddTalkTargetList(context, node, next2,
                        EvtStoryGraphEdgeKind.NextTalk2,
                        "直接备用分支（nextTalk2，已被选项流程覆盖）", true, false);
                    continue;
                }

                if (hasOptionValues)
                {
                    // RefreshTalk 遇到空正文会立即 NextTalk，配置的 option 永远不展示。
                    node.Flags |= EvtStoryGraphNodeFlags.InvalidData;
                    AddDiagnostic(context.Model,
                        "对话 " + talk.id + " 的正文为空，游戏中不会显示它配置的选项。", 12);
                }

                if (hasMiniGame)
                {
                    if (!miniGameValid)
                    {
                        AddTalkTargetList(context, node, next,
                            EvtStoryGraphEdgeKind.NextTalk,
                            "小游戏配置无效，成功出口不会可靠执行", true, false);
                        AddTalkTargetList(context, node, next2,
                            EvtStoryGraphEdgeKind.NextTalk2,
                            "小游戏配置无效，失败出口不会可靠执行", true, false);
                    }
                    else if (MiniGameUtil.IsParamJump(talk.miniGame))
                    {
                        // 讲价(16)和连线(45)由小游戏界面直接读取 miniGame 参数中的
                        // Talk ID；nextTalk/nextTalk2 即使有值也不会参与结果跳转。
                        AddTalkTargetList(context, node, next,
                            EvtStoryGraphEdgeKind.NextTalk,
                            "nextTalk（参数跳转小游戏不读取）", true, false);
                        AddTalkTargetList(context, node, next2,
                            EvtStoryGraphEdgeKind.NextTalk2,
                            "nextTalk2（参数跳转小游戏不读取）", true, false);
                        AddMiniGameParameterTargets(context, node, talk.miniGame,
                            EvtStoryGraphEdgeKind.NextTalk, false, talk.id);
                    }
                    else
                    {
                        // 普通小游戏界面按胜负把原始列表交给 ShowTalk(List<int>)。
                        AddTalkTargetList(context, node, next,
                            EvtStoryGraphEdgeKind.NextTalk, "小游戏成功", false, false);
                        AddTalkTargetList(context, node, next2,
                            EvtStoryGraphEdgeKind.NextTalk2, "小游戏失败", false, false);
                    }
                    continue;
                }

                AddNormalTalkFlow(context, node, talk, next, next2);
            }
        }

        private static void AddNormalTalkFlow(
            BuildContext context,
            EvtStoryGraphNode node,
            TalkCfg talk,
            List<int> next,
            List<int> next2)
        {
            if (context.StateEventView)
            {
                // StateEvtView 推进只读 nextTalk（OnClickSkip→NextTalk），
                // check/nextTalk2 从不求值；槽位 0 不是结束而是软锁死
                // （ShowTalk 对 talkId==0 直接 return）。
                if (next != null && next.Count == 1 && next[0] == 0)
                {
                    // 单槽 [0]：AddTalkTargetList 对 0 值不画边，若无诊断该
                    // 对话在图上会像“正常终点”，实为双性别软锁死。
                    AddDiagnostic(context.Model,
                        "对话 " + node.Id + " 的 nextTalk 两性共用槽为 0："
                        + "状态演出事件没有“对话 0=结束”语义，点跳过会停在原地"
                        + "（软锁死）。");
                }
                else if (next != null && next.Count > 1)
                {
                    for (int gender = 0; gender < 2; gender++)
                    {
                        if (RuntimeSlotValue(next, gender) != 0) continue;
                        AddDiagnostic(context.Model,
                            "对话 " + node.Id + " 的" + GenderName(gender)
                            + " nextTalk 为 0：状态演出事件没有“对话 0=结束”语义，"
                            + "该性别点跳过会停在原地。");
                    }
                }
                AddTalkTargetList(context, node, next,
                    EvtStoryGraphEdgeKind.NextTalk, "下一句（nextTalk）",
                    false, false);
                AddTalkTargetList(context, node, next2,
                    EvtStoryGraphEdgeKind.NextTalk2, "备用分支（nextTalk2）",
                    true, false, "状态演出事件(StateEvtView)不读取此字段");
                return;
            }

            bool conditional = HasItems(talk.check);
            DiagnoseGenderSlots(context, node, next, next2, conditional);
            AddTalkTargetList(context, node, next,
                EvtStoryGraphEdgeKind.NextTalk,
                conditional ? "条件成立" : "下一句（nextTalk）", false, false);

            bool next2FallsBack = next2.Count == 0 || next2[0] == 0;
            AddNormalTalkFalseTargets(context, node, next, next2,
                conditional, next2FallsBack);

            // NextTalk() 先检查 GetNextTalk()==0；只有该性别的真分支非零，条件假
            // 才能继续。GetNextTalk2 首项 0/空时按该性别回退到 nextTalk。
            if (conditional && next2FallsBack)
            {
                for (int i = 0; i < RuntimeSlotCount(next); i++)
                {
                    int targetId = RuntimeSlotValue(next, i);
                    if (targetId == 0) continue;
                    EvtStoryGraphNode target = GetTalkTarget(context, node, targetId);
                    AddEdge(context.Model, node, target,
                        EvtStoryGraphEdgeKind.NextTalk2,
                        "条件不成立（回退到 nextTalk）" + GenderSuffix(next.Count, i), i,
                        EvtStoryGraphEdgeFlags.RuntimeFallback |
                        ReferenceFlags(target));
                }
            }
        }

        private static void DiagnoseGenderSlots(
            BuildContext context,
            EvtStoryGraphNode node,
            List<int> next,
            List<int> next2,
            bool conditional)
        {
            if (next != null && next.Count > 1)
            {
                for (int gender = 0; gender < 2; gender++)
                {
                    if (RuntimeSlotValue(next, gender) != 0) continue;
                    AddDiagnostic(context.Model,
                        "对话 " + node.Id + " 的" + GenderName(gender)
                        + " nextTalk 为 0：该性别会在这里直接结束。", 16);
                }
            }
            if (!conditional || next2 == null || next2.Count == 0
                || next2[0] == 0) return;
            for (int gender = 0; gender < 2; gender++)
            {
                if (RuntimeSlotValue(next, gender) <= 0
                    || RuntimeSlotValue(next2, gender) > 0) continue;
                node.Flags |= EvtStoryGraphNodeFlags.InvalidData;
                AddDiagnostic(context.Model,
                    "对话 " + node.Id + " 的" + GenderName(gender)
                    + "条件失败槽为 0；游戏会尝试打开 Talk 0 而不是正常结束。", 16);
            }
        }

        private static void AddNormalTalkFalseTargets(
            BuildContext context,
            EvtStoryGraphNode source,
            List<int> next,
            List<int> next2,
            bool conditional,
            bool next2FallsBack)
        {
            for (int i = 0; i < next2.Count; i++)
            {
                int targetId = next2[i];
                if (targetId == 0) continue;
                EvtStoryGraphNode target = GetTalkTarget(context, source, targetId);
                bool guardAllowsSlot = i < 2 && RuntimeSlotValue(next, i) != 0;
                bool ignored = i >= 2 || !conditional || !guardAllowsSlot
                               || next2FallsBack;
                EvtStoryGraphEdgeFlags flags = ReferenceFlags(target);
                if (ignored) flags |= EvtStoryGraphEdgeFlags.RuntimeIgnored;
                string label = conditional ? "条件不成立" : "备用分支（nextTalk2）";
                label += GenderSuffix(next2.Count, i);
                if (next2FallsBack) label += "（该字段不执行，改走回退分支）";
                else if (!conditional) label += "（没有条件，游戏会一直视为成立）";
                else if (!guardAllowsSlot) label += "（nextTalk 为 0，剧情会先结束）";
                else if (i >= 2) label += "（游戏只读取前两项）";
                AddEdge(context.Model, source, target,
                    EvtStoryGraphEdgeKind.NextTalk2, label, i, flags);
            }
        }

        private static void AddTalkOptionEdges(
            BuildContext context,
            EvtStoryGraphNode talkNode,
            List<int> optionIds,
            bool runtimeActive)
        {
            var usesForTalk = new Dictionary<int, EvtStoryGraphNode>();
            context.OptionUses[talkNode] = usesForTalk;
            for (int i = 0; i < optionIds.Count; i++)
            {
                int optionId = optionIds[i];
                if (optionId == 0) continue;
                EvtStoryGraphNode optionNode;
                if (!usesForTalk.TryGetValue(optionId, out optionNode))
                {
                    EvtStoryGraphNode template;
                    if (!context.OptionsByKey.TryGetValue(optionId, out template))
                    {
                        optionNode = GetMissingOption(context, optionId, talkNode);
                    }
                    else
                    {
                        optionNode = GetOptionUse(context, template, talkNode);
                    }
                    usesForTalk.Add(optionId, optionNode);
                    context.OptionContextActive[optionNode] = runtimeActive;
                }

                string summary = optionNode.Option != null
                    ? Summarize(optionNode.Option.content, 24)
                    : "缺失";
                string label = "选项 " + optionId;
                if (!string.IsNullOrEmpty(summary)) label += " " + summary;
                if (optionNode.Option != null
                    && HasItems(optionNode.Option.precondition))
                    label += "（受前置条件控制）";
                EvtStoryGraphEdgeFlags flags = ReferenceFlags(optionNode);
                if (!runtimeActive || optionNode.Kind == EvtStoryGraphNodeKind.MissingOption)
                    flags |= EvtStoryGraphEdgeFlags.RuntimeIgnored;
                if (!runtimeActive) label += "（正文为空，游戏中不会显示）";
                else if (optionNode.Kind == EvtStoryGraphNodeKind.MissingOption)
                    label += "（选项配置缺失，游戏中不会显示）";
                AddEdge(context.Model, talkNode, optionNode,
                    EvtStoryGraphEdgeKind.TalkOption, label, i, flags);
            }
        }

        private static void AddEventOptionEdges(BuildContext context)
        {
            EvtCfg evt = context.EventConfig;
            if (evt == null || evt.options == null || evt.options.Count == 0)
                return;
            List<int> optionIds = SafeCopyIds(
                evt.options, context.Model,
                "事件 " + context.EventId + " 的事件级选项字段（options）");
            var uses = new Dictionary<int, EvtStoryGraphNode>();
            int validCount = optionIds.Count(id => id != 0);
            if (evt.maxoptions > 0 && evt.maxoptions < validCount)
                AddDiagnostic(context.Model,
                    "事件 " + context.EventId + " 配置了 maxoptions="
                    + evt.maxoptions + "；图中展示全部候选，但游戏每次会随机显示其中 "
                    + evt.maxoptions + " 个。", 16);

            // CommonEvtMgr.ShowEvent 先判 content：为空直接走入口对话/事件效果，
            // 事件屏被整体跳过（原版 mod 编辑器不暴露正文字段，mod 事件必然为空）；
            // 状态演出事件更早分流到 StateEvtView，同样从不读取事件级选项。
            string optionsNeverShownReason = null;
            if (context.StateEventView)
                optionsNeverShownReason = "状态演出事件(StateEvtView)不读取事件级选项";
            else if (string.IsNullOrEmpty(evt.content))
                optionsNeverShownReason = "事件正文为空，事件屏被跳过";
            if (optionsNeverShownReason != null && validCount > 0)
            {
                EvtStoryGraphNode eventNode = context.Model.EventNode;
                if (eventNode != null)
                {
                    eventNode.Flags |= EvtStoryGraphNodeFlags.InvalidData;
                    string warning = context.StateEventView
                        ? "状态演出事件(StateEvtView)：事件级选项在游戏中永不显示。"
                        : "事件正文为空：事件级选项在游戏中永不显示"
                          + "（游戏会直接进入入口对话或只执行事件效果）。";
                    if (!eventNode.AuthorWarnings.Contains(warning))
                        eventNode.AuthorWarnings.Add(warning);
                }
                AddDiagnostic(context.Model,
                    "事件 " + context.EventId + " 配置了事件级选项，但"
                    + optionsNeverShownReason + "，这些选项在游戏中永不显示。", 12);
            }

            for (int i = 0; i < optionIds.Count; i++)
            {
                int optionId = optionIds[i];
                EvtStoryGraphNode optionNode;
                if (!uses.TryGetValue(optionId, out optionNode))
                {
                    EvtStoryGraphNode template;
                    if (!context.OptionsByKey.TryGetValue(optionId, out template))
                        optionNode = GetMissingOption(
                            context, optionId, null, true);
                    else
                        optionNode = GetEventOptionUse(context, template);
                    uses.Add(optionId, optionNode);
                    context.OptionContextActive[optionNode] =
                        optionsNeverShownReason == null;
                    context.OptionEventParents[optionNode] = evt;
                    optionNode.ParentEvent = evt;
                }

                string summary = optionNode.Option != null
                    ? Summarize(optionNode.Option.content, 24)
                    : "缺失";
                string label = "事件选项 " + optionId;
                if (!string.IsNullOrEmpty(summary)) label += " " + summary;
                if (optionNode.Option != null
                    && HasItems(optionNode.Option.precondition))
                    label += "（受前置条件控制）";
                if (evt.maxoptions > 0 && evt.maxoptions < validCount)
                    label += "（随机候选）";
                EvtStoryGraphEdgeFlags flags = ReferenceFlags(optionNode);
                if (optionsNeverShownReason != null)
                {
                    flags |= EvtStoryGraphEdgeFlags.RuntimeIgnored;
                    label += "（" + optionsNeverShownReason + "，永不显示）";
                }
                if (optionNode.Kind == EvtStoryGraphNodeKind.MissingOption)
                {
                    flags |= EvtStoryGraphEdgeFlags.RuntimeIgnored;
                    if (optionsNeverShownReason == null)
                        label += "（配置缺失，游戏会跳过）";
                }
                AddEdge(context.Model, context.Model.EventNode, optionNode,
                    EvtStoryGraphEdgeKind.TalkOption, label, i, flags);
            }
        }

        private static EvtStoryGraphNode GetOptionUse(
            BuildContext context,
            EvtStoryGraphNode template,
            EvtStoryGraphNode talkNode)
        {
            if (context.ClaimedOptionTemplates.Add(template))
            {
                template.LocateTalk = talkNode.Talk;
                return template;
            }

            // OptionCfg 可被多个 Talk 引用，而父 Talk 的小游戏会改变选项后续语义。
            // 为每个额外父 Talk 建立使用节点，保留同一只读配置对象与独立定位来源。
            var clone = new EvtStoryGraphNode
            {
                Key = "option-use:" + template.Id + ":talk:"
                      + talkNode.Id + ":" + (++context.NodeSerial),
                Kind = EvtStoryGraphNodeKind.Option,
                Flags = template.Flags,
                Id = template.Id,
                Title = template.Title,
                Subtitle = template.Subtitle,
                SearchText = template.SearchText,
                Option = template.Option,
                LocateTalk = talkNode.Talk,
            };
            context.Model.Nodes.Add(clone);
            return clone;
        }

        private static EvtStoryGraphNode GetEventOptionUse(
            BuildContext context,
            EvtStoryGraphNode template)
        {
            if (context.ClaimedOptionTemplates.Add(template))
            {
                template.LocateTalk = null;
                return template;
            }
            var clone = new EvtStoryGraphNode
            {
                Key = "option-use:" + template.Id + ":event:"
                      + context.EventId + ":" + (++context.NodeSerial),
                Kind = EvtStoryGraphNodeKind.Option,
                Flags = template.Flags,
                Id = template.Id,
                Title = template.Title,
                Subtitle = template.Subtitle,
                SearchText = template.SearchText,
                Option = template.Option,
                LocateTalk = null,
            };
            context.Model.Nodes.Add(clone);
            return clone;
        }

        private static void AnnotateSharedOptionUses(BuildContext context)
        {
            var groups =
                new Dictionary<OptionCfg, List<EvtStoryGraphNode>>(
                    ReferenceComparer<OptionCfg>.Instance);
            foreach (EvtStoryGraphNode node in context.Model.Nodes)
            {
                if (node.Kind != EvtStoryGraphNodeKind.Option
                    || node.Option == null) continue;
                List<EvtStoryGraphNode> uses;
                if (!groups.TryGetValue(node.Option, out uses))
                {
                    uses = new List<EvtStoryGraphNode>();
                    groups.Add(node.Option, uses);
                }
                uses.Add(node);
            }

            foreach (List<EvtStoryGraphNode> uses in groups.Values)
            {
                int referenceCount = uses.Sum(node => node.Incoming.Count(edge =>
                    edge.Kind == EvtStoryGraphEdgeKind.TalkOption));
                if (referenceCount <= 1) continue;
                foreach (EvtStoryGraphNode node in uses)
                {
                    node.Flags |= EvtStoryGraphNodeFlags.SharedOption;
                    node.Subtitle = (node.Subtitle ?? string.Empty)
                                  + "　[共享配置：" + referenceCount + " 处引用]";
                }
            }
        }

        private static void AddTalkTargetList(
            BuildContext context,
            EvtStoryGraphNode source,
            List<int> ids,
            EvtStoryGraphEdgeKind kind,
            string baseLabel,
            bool forceIgnored,
            bool runtimeFallbackMode,
            string forceIgnoredReason = null)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                int targetId = ids[i];
                if (targetId == 0) continue;
                EvtStoryGraphNode target = GetTalkTarget(context, source, targetId);
                EvtStoryGraphEdgeFlags flags = ReferenceFlags(target);
                bool ignored = i >= 2 || forceIgnored || runtimeFallbackMode;
                if (ignored) flags |= EvtStoryGraphEdgeFlags.RuntimeIgnored;
                string label = baseLabel + GenderSuffix(ids.Count, i);
                if (runtimeFallbackMode) label += "（该字段不执行，改走回退分支）";
                else if (forceIgnored)
                    label += "（" + (forceIgnoredReason ?? "游戏中不会执行") + "）";
                else if (i >= 2) label += "（游戏只读取前两项）";
                AddEdge(context.Model, source, target, kind, label, i, flags);
            }
        }

        private static void AddOptionEdges(BuildContext context)
        {
            List<EvtStoryGraphNode> optionNodes = context.Model.Nodes
                .Where(n => n.Kind == EvtStoryGraphNodeKind.Option).ToList();
            foreach (EvtStoryGraphNode node in optionNodes)
            {
                OptionCfg option = node.Option;
                if (option == null) continue;
                bool contextActive;
                if (!context.OptionContextActive.TryGetValue(node, out contextActive))
                    contextActive = false; // 未被 Talk 使用的模板是孤立配置。

                List<int> talkIds = SafeCopyIds(option.talkId, context.Model,
                    "选项 " + node.Id + " 的结果字段（talkId）");
                List<int> talkIds2 = SafeCopyIds(option.talkId2, context.Model,
                    "选项 " + node.Id + " 的备用结果字段（talkId2）");
                TalkCfg parent = node.LocateTalk;
                EvtCfg eventParent;
                context.OptionEventParents.TryGetValue(node, out eventParent);
                bool optionMiniGame = HasItems(option.miniGame);
                bool parentMiniGame = parent != null && HasItems(parent.miniGame);
                bool eventMiniGame = eventParent != null
                                     && HasItems(eventParent.miniGame);
                bool optionMiniGameValid = ValidateMiniGameConfig(
                    context, node, option.miniGame, false, "选项 " + node.Id);
                bool parentMiniGameValid = !parentMiniGame
                    || string.IsNullOrEmpty(MiniGameUtil.Validate(parent.miniGame, true));
                if (optionMiniGame && optionMiniGameValid)
                    ValidateMiniGameFlowTargets(context, node, option.miniGame,
                        talkIds, talkIds2, false, "选项 " + node.Id);

                if (optionMiniGame)
                {
                    if (!optionMiniGameValid)
                    {
                        AddOptionTargetList(context, node, talkIds,
                            EvtStoryGraphEdgeKind.OptionTalk,
                            "小游戏配置无效，成功出口不会可靠执行", true, false);
                        AddOptionTargetList(context, node, talkIds2,
                            EvtStoryGraphEdgeKind.OptionTalk2,
                            "小游戏配置无效，失败出口不会可靠执行", true, false);
                    }
                    else if (MiniGameUtil.IsParamJump(option.miniGame))
                    {
                        AddOptionTargetList(context, node, talkIds,
                            EvtStoryGraphEdgeKind.OptionTalk,
                            "talkId（参数跳转小游戏不读取）", true, false);
                        AddOptionTargetList(context, node, talkIds2,
                            EvtStoryGraphEdgeKind.OptionTalk2,
                            "talkId2（参数跳转小游戏不读取）", true, false);
                        AddMiniGameParameterTargets(context, node, option.miniGame,
                            EvtStoryGraphEdgeKind.OptionTalk, !contextActive,
                            parent != null ? parent.id : context.EventId * 1000);
                    }
                    else
                    {
                        int gameId;
                        string gameIdError;
                        if (!MiniGameUtil.TryGetGameId(
                                option.miniGame, out gameId, out gameIdError))
                            gameId = 0;
                        // 与会话预检 ValidateOptionRuntime 的豁免同源：
                        // 29=大头贴只回调成功出口；36=谈判组队 CloseView 只读
                        // talkId；47=漫展派对 CloseView 只关闭 NewTalkView，
                        // talkId/talkId2 全不读，视图自行收尾。
                        bool successIgnored = gameId == 47;
                        bool failureIgnored = gameId == 29 || gameId == 36
                                              || gameId == 47;
                        string successLabel;
                        if (gameId == 29) successLabel = "大头贴结束";
                        else if (gameId == 47)
                            successLabel = "talkId（漫展派对 47 自行收尾，不读取）";
                        else successLabel = "小游戏成功";
                        string failureLabel;
                        if (gameId == 29) failureLabel = "talkId2（大头贴 29 不读取）";
                        else if (gameId == 36)
                            failureLabel = "talkId2（谈判组队 36 不读取）";
                        else if (gameId == 47)
                            failureLabel = "talkId2（漫展派对 47 不读取）";
                        else failureLabel = "小游戏失败";
                        AddOptionTargetList(context, node, talkIds,
                            EvtStoryGraphEdgeKind.OptionTalk, successLabel,
                            !contextActive || successIgnored, false);
                        AddOptionTargetList(context, node, talkIds2,
                            EvtStoryGraphEdgeKind.OptionTalk2, failureLabel,
                            !contextActive || failureIgnored, false);
                    }
                    AddNextEventEdge(context, node, option.nextEvtId, true,
                        "已被选项自身的小游戏流程覆盖");
                }
                else if (parentMiniGame)
                {
                    AddOptionTargetList(context, node, talkIds,
                        EvtStoryGraphEdgeKind.OptionTalk, "选项自身结果",
                        true, true);
                    AddOptionTargetList(context, node, talkIds2,
                        EvtStoryGraphEdgeKind.OptionTalk2, "选项自身条件假",
                        true, true);
                    AddNextEventEdge(context, node, option.nextEvtId, true,
                        "已被所属对话的小游戏流程覆盖");
                    if (!parentMiniGameValid)
                    {
                        node.Flags |= EvtStoryGraphNodeFlags.InvalidData;
                        List<int> parentSuccess = SafeCopyIds(parent.nextTalk, context.Model,
                            "所属对话 " + parent.id + " 的无效小游戏成功分支");
                        List<int> parentFailure = SafeCopyIds(parent.nextTalk2, context.Model,
                            "所属对话 " + parent.id + " 的无效小游戏失败分支");
                        AddOptionTargetList(context, node, parentSuccess,
                            EvtStoryGraphEdgeKind.OptionTalk,
                            "所属对话小游戏配置无效，成功出口不会可靠执行",
                            true, false, parent.id);
                        AddOptionTargetList(context, node, parentFailure,
                            EvtStoryGraphEdgeKind.OptionTalk2,
                            "所属对话小游戏配置无效，失败出口不会可靠执行",
                            true, false, parent.id);
                    }
                    else if (MiniGameUtil.IsParamJump(parent.miniGame))
                        AddMiniGameParameterTargets(context, node, parent.miniGame,
                            EvtStoryGraphEdgeKind.OptionTalk, !contextActive, parent.id);
                    else
                        AddParentTalkMiniGameFlow(context, node, parent, contextActive);
                }
                else if (eventMiniGame)
                {
                    AddOptionTargetList(context, node, talkIds,
                        EvtStoryGraphEdgeKind.OptionTalk,
                        "选项自身结果（被事件小游戏覆盖）",
                        true, false);
                    AddOptionTargetList(context, node, talkIds2,
                        EvtStoryGraphEdgeKind.OptionTalk2,
                        "选项自身条件假（被事件小游戏覆盖）",
                        true, false);
                    AddNextEventEdge(context, node, option.nextEvtId, true,
                        "已被所属事件的小游戏流程覆盖");

                    int gameId;
                    string gameIdError;
                    if (!MiniGameUtil.TryGetGameId(
                            eventParent.miniGame, out gameId, out gameIdError))
                    {
                        node.Flags |= EvtStoryGraphNodeFlags.InvalidData;
                        AddDiagnostic(context.Model,
                            "事件 " + context.EventId
                            + " 的小游戏配置无效：" + gameIdError, 16);
                    }
                    else if (MiniGameUtil.IsParamJump(eventParent.miniGame))
                    {
                        AddMiniGameParameterTargets(
                            context, node, eventParent.miniGame,
                            EvtStoryGraphEdgeKind.OptionTalk,
                            !contextActive, context.EventId * 1000);
                    }
                    else
                    {
                        node.Subtitle = (node.Subtitle ?? string.Empty)
                                      + "　[后续由事件小游戏 "
                                      + MiniGameUtil.GameName(gameId) + " 接管]";
                        AddDiagnostic(context.Model,
                            "事件选项 " + node.Id + " 被选中后先进入事件小游戏 "
                            + MiniGameUtil.GameName(gameId)
                            + "；OptionCfg 的 talkId/talkId2/nextEvtId 不会执行，"
                            + "小游戏结束行为取决于该编号对 Evt 来源的实现。", 16);
                    }
                }
                else
                {
                    AddNormalOptionFlow(context, node, option,
                        talkIds, talkIds2, contextActive);
                }
            }

            foreach (EvtStoryGraphNode node in optionNodes)
            {
                // Orphan 表示“没有任何事件或 Talk 配置引用”，而不是“当前运行时暂时
                // 不会进入”。父 Talk 正文为空时 TalkOption 会保留为灰色
                // RuntimeIgnored 结构边；这仍然是明确的父子关系，不应误报孤立。
                bool hasParent = node.Incoming.Any(e =>
                    e.Kind == EvtStoryGraphEdgeKind.TalkOption);
                if (!hasParent) node.Flags |= EvtStoryGraphNodeFlags.OrphanOption;
            }
        }

        private static bool ValidateMiniGameConfig(
            BuildContext context,
            EvtStoryGraphNode node,
            List<double> miniGame,
            bool isTalk,
            string owner)
        {
            if (!HasItems(miniGame)) return true;
            string warning = MiniGameUtil.Validate(miniGame, isTalk);
            if (string.IsNullOrEmpty(warning)) return true;
            node.Flags |= EvtStoryGraphNodeFlags.InvalidData;
            AddDiagnostic(context.Model, owner + " 的小游戏配置无效：" + warning, 16);
            return false;
        }

        private static void ValidateMiniGameFlowTargets(
            BuildContext context,
            EvtStoryGraphNode node,
            List<double> miniGame,
            List<int> success,
            List<int> failure,
            bool isTalk,
            string owner)
        {
            if (!HasItems(miniGame) || MiniGameUtil.IsParamJump(miniGame)) return;
            int gameId;
            string error;
            if (!MiniGameUtil.TryGetGameId(miniGame, out gameId, out error)) return;
            int minimumTargetId = isTalk ? 1 : 2;
            bool hasSuccess = success != null
                              && success.Any(id => id >= minimumTargetId);
            bool hasFailure = failure != null
                              && failure.Any(id => id >= minimumTargetId);
            // 与会话预检 ValidateOptionRuntime 对齐：47 双出口全不读、
            // 36/29 不读失败出口（仅选项触发成立，对话侧不可照搬）。
            bool successOptional = !isTalk && gameId == 47;
            bool failureOptional = !isTalk
                && (gameId == 29 || gameId == 36 || gameId == 47);
            if ((hasSuccess || successOptional)
                && (hasFailure || failureOptional)) return;

            node.Flags |= EvtStoryGraphNodeFlags.InvalidData;
            var missing = new List<string>();
            if (!hasSuccess && !successOptional)
                missing.Add(isTalk ? "成功端口 nextTalk" : "成功端口 talkId");
            if (!hasFailure && !failureOptional)
                missing.Add(isTalk ? "失败端口 nextTalk2" : "失败端口 talkId2");
            AddDiagnostic(context.Model, owner + " 已配置小游戏 " + gameId
                + "，但缺少 " + string.Join("、", missing.ToArray())
                + "；可先保存小游戏，再从节点端口补齐。", 16);
        }

        private static void AddMiniGameParameterTargets(
            BuildContext context,
            EvtStoryGraphNode source,
            List<double> miniGame,
            EvtStoryGraphEdgeKind kind,
            bool forceIgnored,
            int sourceTalkId)
        {
            List<int> targets;
            string error;
            if (!MiniGameUtil.TryGetParamJumpTargets(
                    miniGame, out targets, out error))
            {
                source.Flags |= EvtStoryGraphNodeFlags.InvalidData;
                AddDiagnostic(context.Model,
                    "节点 " + source.Id + " 的小游戏参数结果无效：" + error, 16);
                return;
            }
            int gameId;
            if (!MiniGameUtil.TryGetGameId(miniGame, out gameId, out error)) return;
            string gameName = MiniGameUtil.GameName(gameId);
            for (int i = 0; i < targets.Count; i++)
            {
                int targetId = targets[i];
                EvtStoryGraphNode target = GetTalkTarget(
                    context, source, targetId, sourceTalkId);
                EvtStoryGraphEdgeFlags flags = ReferenceFlags(target);
                if (forceIgnored) flags |= EvtStoryGraphEdgeFlags.RuntimeIgnored;
                string result = gameId == 16
                    ? "成功 " + i + " 次"
                    : "连对 " + i + " 题";
                string label = "小游戏 " + gameName + "：" + result
                               + " → 参数中的对话 " + targetId;
                if (forceIgnored) label += "（当前引用位置不会执行）";
                AddEdge(context.Model, source, target, kind, label, i, flags);
            }
        }

        private static void AddParentTalkMiniGameFlow(
            BuildContext context,
            EvtStoryGraphNode optionNode,
            TalkCfg parent,
            bool contextActive)
        {
            List<int> success = SafeCopyIds(parent.nextTalk, context.Model,
                "所属对话 " + parent.id + " 的小游戏成功分支（nextTalk）");
            List<int> failure = SafeCopyIds(parent.nextTalk2, context.Model,
                "所属对话 " + parent.id + " 的小游戏失败分支（nextTalk2）");
            AddOptionTargetList(context, optionNode, success,
                EvtStoryGraphEdgeKind.OptionTalk, "所属对话的小游戏成功",
                !contextActive, false, parent.id);
            AddOptionTargetList(context, optionNode, failure,
                EvtStoryGraphEdgeKind.OptionTalk2, "所属对话的小游戏失败",
                !contextActive, false, parent.id);
        }

        private static void AddNormalOptionFlow(
            BuildContext context,
            EvtStoryGraphNode node,
            OptionCfg option,
            List<int> talkIds,
            List<int> talkIds2,
            bool contextActive)
        {
            bool conditional = HasItems(option.check);
            AddOptionTargetList(context, node, talkIds,
                EvtStoryGraphEdgeKind.OptionTalk,
                conditional ? "条件成立" : "选项结果",
                !contextActive, true);
            AddOptionTargetList(context, node, talkIds2,
                EvtStoryGraphEdgeKind.OptionTalk2, "条件不成立",
                !contextActive || !conditional, true);

            // SelectOption 仅在当前性别/条件分支没有 >1 的 Talk 目标时，才继续
            // 尝试 nextEvtId。只要任一可发生分支会落空，该事件跳转就是可能路径。
            bool trueFallsThrough = HasRuntimeSlotAtMostOne(talkIds);
            bool falseFallsThrough = conditional && HasRuntimeSlotAtMostOne(talkIds2);
            bool canReachNextEvent = contextActive && option.nextEvtId > 0
                                     && (trueFallsThrough || falseFallsThrough);
            AddNextEventEdge(context, node, option.nextEvtId,
                !canReachNextEvent, canReachNextEvent
                    ? null
                    : "前面的对话分支不会落空，或当前引用位置不会执行");
        }

        private static void AddOptionTargetList(
            BuildContext context,
            EvtStoryGraphNode source,
            List<int> ids,
            EvtStoryGraphEdgeKind kind,
            string baseLabel,
            bool forceIgnored,
            bool normalOptionThreshold,
            int sourceTalkId = int.MinValue)
        {
            int originId = sourceTalkId != int.MinValue
                ? sourceTalkId
                : (source.LocateTalk != null
                    ? source.LocateTalk.id
                    : context.EventId * 1000);
            for (int i = 0; i < ids.Count; i++)
            {
                int targetId = ids[i];
                if (targetId == 0) continue;
                EvtStoryGraphNode target = GetTalkTarget(
                    context, source, targetId, originId);
                EvtStoryGraphEdgeFlags flags = ReferenceFlags(target);
                // StateEvtView 的跳转阈值是 GetNextTalk()>0：talkId=1 会真跳转
                // （目标缺失时 KeyNotFound 崩溃），只有非正数不会执行。
                bool invalidForNormalOption = normalOptionThreshold
                    && targetId <= (context.StateEventView ? 0 : 1);
                bool ignored = forceIgnored || i >= 2 || invalidForNormalOption;
                if (ignored) flags |= EvtStoryGraphEdgeFlags.RuntimeIgnored;
                string label = baseLabel + GenderSuffix(ids.Count, i);
                if (invalidForNormalOption)
                {
                    label += context.StateEventView
                        ? "（状态演出事件编号 >0 即跳转，当前值不会执行）"
                        : "（编号必须大于 1，当前值不会执行）";
                    source.Flags |= EvtStoryGraphNodeFlags.InvalidData;
                }
                else if (forceIgnored) label += "（游戏中不会执行）";
                else if (i >= 2) label += "（游戏只读取前两项）";
                AddEdge(context.Model, source, target, kind, label, i, flags);
            }
        }

        private static void AddNextEventEdge(
            BuildContext context,
            EvtStoryGraphNode source,
            int eventId,
            bool forceIgnored,
            string ignoredReason)
        {
            if (eventId == 0) return;
            // StateEvtView 的选项点击流程从不读取 nextEvtId（分支落空即
            // CloseView），统一在此覆盖所有调用点的判定。
            if (context.StateEventView && eventId > 0)
            {
                forceIgnored = true;
                ignoredReason = "状态演出事件(StateEvtView)不读取此字段";
            }
            bool returnsToCurrent = eventId > 0 && eventId == context.EventId;
            EvtStoryGraphNode target = returnsToCurrent
                ? context.Model.EventNode
                : GetExternalEvent(context, eventId);
            EvtStoryGraphEdgeFlags flags = ReferenceFlags(target);
            if (!returnsToCurrent) flags |= EvtStoryGraphEdgeFlags.ExternalJump;
            if (forceIgnored || eventId <= 0)
                flags |= EvtStoryGraphEdgeFlags.RuntimeIgnored;
            string label = eventId > 0
                ? "下一事件（nextEvtId） " + eventId
                  + (returnsToCurrent ? "（返回当前事件）" : string.Empty)
                : "无效的下一事件编号（nextEvtId） " + eventId;
            if (eventId <= 0)
            {
                label += "（游戏中不会执行）";
                source.Flags |= EvtStoryGraphNodeFlags.InvalidData;
            }
            else if (forceIgnored && !string.IsNullOrEmpty(ignoredReason))
                label += "（" + ignoredReason + "）";
            AddEdge(context.Model, source, target,
                EvtStoryGraphEdgeKind.NextEvent, label, 0, flags);
        }

        private static int RuntimeSlotCount(List<int> ids)
        {
            if (ids == null || ids.Count == 0) return 0;
            return Math.Min(ids.Count, 2);
        }

        private static int RuntimeSlotValue(List<int> ids, int slot)
        {
            if (ids == null || ids.Count == 0 || slot < 0) return 0;
            if (ids.Count == 1) return ids[0];
            return slot < 2 ? ids[slot] : 0;
        }

        private static bool HasRuntimeSlotAtMostOne(List<int> ids)
        {
            if (ids == null || ids.Count == 0) return true;
            int slots = ids.Count == 1 ? 1 : 2;
            for (int i = 0; i < slots; i++)
                if (RuntimeSlotValue(ids, i) <= 1) return true;
            return false;
        }

        private static void AddEntryEdges(
            BuildContext context, List<int> entryIds, bool entriesKnown)
        {
            bool hasExplicit = entryIds.Any(id => id != 0);
            // 正文非空且事件级选项可解析时，NewTalkView 事件屏接管流程：
            // 继续按钮（btn_evt）隐藏，EvtCfg.talkId 不会自动播放，剧情只能
            // 经事件级选项的跳转进入（StateEvtView 不走事件屏，不适用）。
            EvtCfg evtCfg = context.EventConfig;
            bool eventOptionsInterceptEntry = !context.StateEventView
                && evtCfg != null
                && !string.IsNullOrEmpty(evtCfg.content)
                && evtCfg.options != null
                && evtCfg.options.Any(id =>
                    id != 0 && context.OptionsByKey.ContainsKey(id));
            if (hasExplicit)
            {
                if (eventOptionsInterceptEntry)
                    AddDiagnostic(context.Model,
                        "事件 " + context.EventId + " 的正文非空且配置了事件级选项："
                        + "游戏先显示事件屏，入口对话（talkId）不会自动播放，"
                        + "只能经事件级选项进入剧情。", 14);
                if (entryIds.Count > 1)
                {
                    for (int gender = 0; gender < 2; gender++)
                        if (RuntimeSlotValue(entryIds, gender) == 0)
                            AddDiagnostic(context.Model,
                                "事件 " + context.EventId + " 的"
                                + GenderName(gender)
                                + "入口为 0：该性别不会进入对话剧情。", 16);
                }
                for (int i = 0; i < entryIds.Count; i++)
                {
                    int id = entryIds[i];
                    if (id == 0) continue;
                    EvtStoryGraphNode target = GetTalkTarget(
                        context, context.Model.EventNode, id,
                        context.EventId * 1000);
                    EvtStoryGraphEdgeFlags flags = ReferenceFlags(target);
                    if (i >= 2) flags |= EvtStoryGraphEdgeFlags.RuntimeIgnored;
                    else if (eventOptionsInterceptEntry)
                        flags |= EvtStoryGraphEdgeFlags.RuntimeIgnored;
                    else target.Flags |= EvtStoryGraphNodeFlags.Entry;
                    string label = "事件入口" + GenderSuffix(entryIds.Count, i);
                    if (i >= 2) label += "（游戏只读取前两项）";
                    else if (eventOptionsInterceptEntry)
                        label += "（事件正文与事件级选项接管流程，此入口不自动播放）";
                    AddEdge(context.Model, context.Model.EventNode, target,
                        EvtStoryGraphEdgeKind.EventEntry, label, i, flags);
                }
                return;
            }

            // 成功读取到空列表/[0] 就代表运行时没有事件入口，不能凭空推断。
            if (entriesKnown)
            {
                AddDiagnostic(context.Model, "当前事件的入口字段（talkId）没有有效的对话编号。", 8);
                return;
            }

            // 旧版本或反射降级拿不到 EvtCfg.talkId 时，仅用当前内存图推断入口。
            // 优先约定的 evtId*1000+1，其次当前事件组零入度，再次全图零入度。
            var candidates = new List<EvtStoryGraphNode>();
            EvtStoryGraphNode conventional = GetResolvedTalk(context, context.EventId * 1000 + 1);
            if (conventional != null)
                candidates.Add(conventional);
            else
            {
                candidates.AddRange(context.Model.Nodes.Where(n =>
                    n.Kind == EvtStoryGraphNodeKind.Talk
                    && GetTalkGroup(n.Id) == context.EventId
                    && !n.Incoming.Any(e => e.IsRuntimeEdge && IsLocalFlowEdge(e.Kind))));
                if (candidates.Count == 0)
                {
                    candidates.AddRange(context.Model.Nodes.Where(n =>
                        n.Kind == EvtStoryGraphNodeKind.Talk
                        && !n.Incoming.Any(e => e.IsRuntimeEdge && IsLocalFlowEdge(e.Kind))));
                }
                if (candidates.Count == 0)
                {
                    EvtStoryGraphNode first = context.Model.Nodes
                        .Where(n => n.Kind == EvtStoryGraphNodeKind.Talk)
                        .OrderBy(n => n.Id).FirstOrDefault();
                    if (first != null) candidates.Add(first);
                }
            }

            foreach (EvtStoryGraphNode target in candidates.Distinct())
            {
                target.Flags |= EvtStoryGraphNodeFlags.Entry;
                AddEdge(context.Model, context.Model.EventNode, target,
                    EvtStoryGraphEdgeKind.EventEntry,
                    "推断的剧情入口（无法读取 EvtCfg.talkId）", 0,
                    EvtStoryGraphEdgeFlags.None);
            }
            if (candidates.Count > 0)
                AddDiagnostic(context.Model,
                    "未能读取事件入口字段（EvtCfg.talkId），已根据当前连线关系推断入口。", 8);
        }

        private static EvtStoryGraphNode GetTalkTarget(
            BuildContext context,
            EvtStoryGraphNode source,
            int targetId,
            int sourceTalkId = int.MinValue)
        {
            EvtStoryGraphNode local = GetResolvedTalk(context, targetId);
            if (local != null) return local;

            int sourceId = sourceTalkId != int.MinValue
                ? sourceTalkId
                : (source.Talk != null ? source.Talk.id : context.EventId * 1000);
            bool external = IsExternalTalk(context, sourceId, targetId);
            string key = (external ? "external-talk:" : "missing-talk:") + targetId;
            EvtStoryGraphNode node;
            if (context.Placeholders.TryGetValue(key, out node)) return node;

            node = new EvtStoryGraphNode
            {
                Key = key,
                Kind = external
                    ? EvtStoryGraphNodeKind.ExternalTalk
                    : EvtStoryGraphNodeKind.MissingTalk,
                Id = targetId,
                Title = external ? "跨组对话 " + targetId : "缺失的对话 " + targetId,
                Subtitle = external
                    ? "目标位于当前事件未加载的其它剧情组"
                    : "当前事件数据中找不到被引用的对话",
                SearchText = JoinSearchText(targetId.ToString(),
                    external ? "外部 跳转" : "缺失 引用"),
                Flags = external
                    ? EvtStoryGraphNodeFlags.External
                    : EvtStoryGraphNodeFlags.MissingReference,
                LocateTalk = source.LocateTalk,
            };
            if (targetId <= 0)
                node.Flags |= EvtStoryGraphNodeFlags.InvalidData |
                              EvtStoryGraphNodeFlags.MissingReference;
            context.Placeholders.Add(key, node);
            context.Model.Nodes.Add(node);
            return node;
        }

        private static EvtStoryGraphNode GetMissingOption(
            BuildContext context,
            int optionId,
            EvtStoryGraphNode parent,
            bool eventLevel = false)
        {
            // 缺失选项按父 Talk 分开，点击占位时一定回到真实引用来源。
            string parentKey = parent != null ? parent.Key : "none";
            string key = "missing-option:" + optionId + ":from:" + parentKey;
            EvtStoryGraphNode node;
            if (context.Placeholders.TryGetValue(key, out node)) return node;
            node = new EvtStoryGraphNode
            {
                Key = key,
                Kind = EvtStoryGraphNodeKind.MissingOption,
                Id = optionId,
                Title = "缺失选项 " + optionId,
                Subtitle = eventLevel
                    ? "事件的 options 字段引用了不存在的选项配置（OptionCfg）"
                    : "对话的 option 字段引用了不存在的选项配置（OptionCfg）",
                SearchText = JoinSearchText(optionId.ToString(), "缺失 选项 引用"),
                Flags = EvtStoryGraphNodeFlags.MissingReference,
                LocateTalk = parent != null ? parent.Talk : null,
            };
            if (optionId <= 0)
                node.Flags |= EvtStoryGraphNodeFlags.InvalidData;
            context.Placeholders.Add(key, node);
            context.Model.Nodes.Add(node);
            return node;
        }

        private static EvtStoryGraphNode GetExternalEvent(
            BuildContext context, int eventId)
        {
            if (eventId == context.EventId && context.Model.EventNode != null)
                return context.Model.EventNode;
            string key = "external-event:" + eventId;
            EvtStoryGraphNode node;
            if (context.Placeholders.TryGetValue(key, out node)) return node;
            bool missing = eventId <= 0
                           || (context.KnownEventIds != null
                               && !context.KnownEventIds.Contains(eventId));
            node = new EvtStoryGraphNode
            {
                Key = key,
                Kind = missing
                    ? EvtStoryGraphNodeKind.MissingEvent
                    : EvtStoryGraphNodeKind.ExternalEvent,
                Id = eventId,
                Title = missing
                    ? "缺失的事件 " + eventId
                    : "跳转到其它事件 " + eventId,
                Subtitle = missing
                    ? "OptionCfg.nextEvtId 指向的事件配置不存在"
                    : "由选项配置的下一事件字段（OptionCfg.nextEvtId）跳转",
                SearchText = JoinSearchText(eventId.ToString(),
                    missing ? "缺失 事件 nextEvtId" : "外部 事件 nextEvtId"),
                Flags = missing
                    ? EvtStoryGraphNodeFlags.InvalidData
                      | EvtStoryGraphNodeFlags.MissingReference
                    : EvtStoryGraphNodeFlags.External,
            };
            context.Placeholders.Add(key, node);
            context.Model.Nodes.Add(node);
            return node;
        }

        private static EvtStoryGraphNode GetResolvedTalk(BuildContext context, int id)
        {
            List<EvtStoryGraphNode> nodes;
            // ModEvtEditView 保存时按列表顺序 dictionary[id] = talk，后项覆盖前项。
            return context.TalksById.TryGetValue(id, out nodes) && nodes.Count > 0
                ? nodes[nodes.Count - 1]
                : null;
        }

        private static bool IsExternalTalk(
            BuildContext context, int sourceTalkId, int targetId)
        {
            if (targetId <= 0) return false;
            // 有全局有效 ID 集合时以真实配置存在性为准：本地没有但全局存在才是
            // 跨组跳转；全局也不存在就是缺失引用，不能再凭“组号不同”涂成安全蓝色。
            if (context.KnownTalkIds != null)
                return context.KnownTalkIds.Contains(targetId);
            int targetGroup = GetTalkGroup(targetId);
            if (targetGroup <= 0) return false;
            // ModEvtEditView 会把事件入口涉及的组全部载入 talkCfgs；目标组完全
            // 不在当前内存集合中，才称为“外部跳转”。已知组内缺项称为“丢失引用”。
            return !context.KnownTalkGroups.Contains(targetGroup);
        }

        private static int GetTalkGroup(int talkId)
        {
            return talkId > 0 ? talkId / 1000 : 0;
        }

        private static void AddEdge(
            EvtStoryGraphModel model,
            EvtStoryGraphNode from,
            EvtStoryGraphNode to,
            EvtStoryGraphEdgeKind kind,
            string label,
            int sourceIndex,
            EvtStoryGraphEdgeFlags flags)
        {
            if (from == null || to == null) return;
            var edge = new EvtStoryGraphEdge
            {
                From = from,
                To = to,
                Kind = kind,
                Label = label ?? kind.ToString(),
                SourceIndex = sourceIndex,
                Flags = flags,
            };
            model.Edges.Add(edge);
            from.Outgoing.Add(edge);
            to.Incoming.Add(edge);
        }

        private static EvtStoryGraphEdgeFlags ReferenceFlags(EvtStoryGraphNode target)
        {
            EvtStoryGraphEdgeFlags flags = EvtStoryGraphEdgeFlags.None;
            if (target.HasFlag(EvtStoryGraphNodeFlags.MissingReference))
                flags |= EvtStoryGraphEdgeFlags.BrokenReference;
            if (target.HasFlag(EvtStoryGraphNodeFlags.External))
                flags |= EvtStoryGraphEdgeFlags.ExternalJump;
            return flags;
        }

        private static void MarkTerminalNodes(EvtStoryGraphModel model)
        {
            foreach (EvtStoryGraphNode node in model.Nodes)
            {
                if (node.Kind != EvtStoryGraphNodeKind.Talk
                    && node.Kind != EvtStoryGraphNodeKind.Option) continue;
                // option 流在父 Talk 正文暂时为空时会被标为 RuntimeIgnored，
                // 但配置结构仍然存在。若仍标成“结束”，作者刚拖出的
                // Talk → Option → Talk 整条链都会看似断裂；真正的运行时问题
                // 已由 InvalidData/灰线说明，不应再伪装成没有输出。
                bool hasFlow = node.Outgoing.Any(e => e.IsRuntimeEdge
                    || IsStructuralOptionFlow(e.Kind));
                if (!hasFlow) node.Flags |= EvtStoryGraphNodeFlags.Terminal;
            }
        }

        private static bool IsStructuralOptionFlow(EvtStoryGraphEdgeKind kind)
        {
            return kind == EvtStoryGraphEdgeKind.TalkOption
                || kind == EvtStoryGraphEdgeKind.OptionTalk
                || kind == EvtStoryGraphEdgeKind.OptionTalk2;
        }

        private static void MarkReachability(EvtStoryGraphModel model)
        {
            var reached = new HashSet<EvtStoryGraphNode>();
            var queue = new Queue<EvtStoryGraphNode>();
            if (model.EventNode != null)
            {
                reached.Add(model.EventNode);
                queue.Enqueue(model.EventNode);
            }
            while (queue.Count > 0)
            {
                EvtStoryGraphNode node = queue.Dequeue();
                foreach (EvtStoryGraphEdge edge in node.Outgoing)
                {
                    if (!edge.IsRuntimeEdge || edge.To == null) continue;
                    if (reached.Add(edge.To)) queue.Enqueue(edge.To);
                }
            }

            foreach (EvtStoryGraphNode node in model.Nodes)
            {
                if ((node.Kind == EvtStoryGraphNodeKind.Talk
                     || node.Kind == EvtStoryGraphNodeKind.Option)
                    && !reached.Contains(node))
                    node.Flags |= EvtStoryGraphNodeFlags.Unreachable;
            }
        }

        /// <summary>
        /// 迭代版 Kosaraju SCC：只把实际运行时会经过的本地图边计入环。
        /// 不使用递归，恶意数据制造超长 Talk 链时也不会栈溢出。
        /// </summary>
        private static void MarkCycles(EvtStoryGraphModel model)
        {
            List<EvtStoryGraphNode> nodes = model.Nodes.Where(IsCycleNode).ToList();
            var adjacency = new Dictionary<EvtStoryGraphNode, List<EvtStoryGraphNode>>();
            var reverse = new Dictionary<EvtStoryGraphNode, List<EvtStoryGraphNode>>();
            foreach (EvtStoryGraphNode node in nodes)
            {
                adjacency[node] = new List<EvtStoryGraphNode>();
                reverse[node] = new List<EvtStoryGraphNode>();
            }
            foreach (EvtStoryGraphEdge edge in model.Edges)
            {
                if (!edge.IsRuntimeEdge || !IsCycleNode(edge.From)
                    || !IsCycleNode(edge.To)) continue;
                adjacency[edge.From].Add(edge.To);
                reverse[edge.To].Add(edge.From);
                if (ReferenceEquals(edge.From, edge.To))
                    edge.From.Flags |= EvtStoryGraphNodeFlags.SelfLoop |
                                       EvtStoryGraphNodeFlags.Cycle;
            }

            var visited = new HashSet<EvtStoryGraphNode>();
            var finishOrder = new List<EvtStoryGraphNode>(nodes.Count);
            var frames = new Stack<DfsFrame>();
            foreach (EvtStoryGraphNode start in nodes)
            {
                if (!visited.Add(start)) continue;
                frames.Push(new DfsFrame { Node = start });
                while (frames.Count > 0)
                {
                    DfsFrame frame = frames.Peek();
                    List<EvtStoryGraphNode> nextNodes = adjacency[frame.Node];
                    if (frame.NextIndex < nextNodes.Count)
                    {
                        EvtStoryGraphNode next = nextNodes[frame.NextIndex++];
                        if (visited.Add(next))
                            frames.Push(new DfsFrame { Node = next });
                        continue;
                    }
                    frames.Pop();
                    finishOrder.Add(frame.Node);
                }
            }

            visited.Clear();
            var pending = new Stack<EvtStoryGraphNode>();
            for (int i = finishOrder.Count - 1; i >= 0; i--)
            {
                EvtStoryGraphNode start = finishOrder[i];
                if (!visited.Add(start)) continue;
                var component = new List<EvtStoryGraphNode>();
                pending.Push(start);
                while (pending.Count > 0)
                {
                    EvtStoryGraphNode current = pending.Pop();
                    component.Add(current);
                    foreach (EvtStoryGraphNode previous in reverse[current])
                    {
                        if (visited.Add(previous)) pending.Push(previous);
                    }
                }

                bool cyclic = component.Count > 1;
                if (!cyclic && component.Count == 1)
                    cyclic = adjacency[component[0]].Any(next =>
                        ReferenceEquals(next, component[0]));
                if (!cyclic) continue;
                foreach (EvtStoryGraphNode member in component)
                    member.Flags |= EvtStoryGraphNodeFlags.Cycle;
            }
        }

        private static bool IsCycleNode(EvtStoryGraphNode node)
        {
            return node != null && (node.Kind == EvtStoryGraphNodeKind.Event
                                    || node.Kind == EvtStoryGraphNodeKind.Talk
                                    || node.Kind == EvtStoryGraphNodeKind.Option);
        }

        private static bool IsLocalFlowEdge(EvtStoryGraphEdgeKind kind)
        {
            return kind == EvtStoryGraphEdgeKind.NextTalk
                   || kind == EvtStoryGraphEdgeKind.NextTalk2
                   || kind == EvtStoryGraphEdgeKind.TalkOption
                   || kind == EvtStoryGraphEdgeKind.OptionTalk
                   || kind == EvtStoryGraphEdgeKind.OptionTalk2;
        }

        private static void FinishStatistics(EvtStoryGraphModel model)
        {
            model.TalkCount = model.Nodes.Count(n => n.Kind == EvtStoryGraphNodeKind.Talk);
            model.OptionCount = model.Nodes.Count(n => n.Kind == EvtStoryGraphNodeKind.Option);
            model.MissingCount = model.Nodes.Count(n =>
                n.HasFlag(EvtStoryGraphNodeFlags.MissingReference));
            model.ExternalCount = model.Nodes.Count(n =>
                n.HasFlag(EvtStoryGraphNodeFlags.External));
            model.UnreachableCount = model.Nodes.Count(n =>
                n.HasFlag(EvtStoryGraphNodeFlags.Unreachable));
            model.CycleCount = model.Nodes.Count(n =>
                n.HasFlag(EvtStoryGraphNodeFlags.Cycle));
            model.TerminalCount = model.Nodes.Count(n =>
                n.HasFlag(EvtStoryGraphNodeFlags.Terminal));
            model.DuplicateCount = model.Nodes.Count(n =>
                n.HasFlag(EvtStoryGraphNodeFlags.DuplicateId));
        }

        private static List<int> SafeCopyIds(
            IEnumerable<int> source, EvtStoryGraphModel model, string fieldName)
        {
            var result = new List<int>();
            if (source == null) return result;
            try
            {
                foreach (int value in source) result.Add(value);
            }
            catch (Exception e)
            {
                AddDiagnostic(model,
                    "读取 " + fieldName + " 失败，已保留此前可读项：" + ShortError(e));
            }
            return result;
        }

        private static bool HasItems<T>(ICollection<T> source)
        {
            try { return source != null && source.Count > 0; }
            catch { return false; }
        }

        private static string GenderSuffix(int count, int index)
        {
            if (count <= 1) return string.Empty;
            if (index == 0) return " [男]";
            if (index == 1) return " [女]";
            return " [额外项 " + (index + 1) + "]";
        }

        private static string GenderName(int gender)
        {
            return gender == 0 ? "男性" : "女性";
        }

        internal static string BuildRoleSummary(
            TalkCfg talk, IDictionary<int, string> personNames)
        {
            if (talk == null) return "角色未识别";
            string explicitName = Summarize(talk.roleName, 18);
            List<int> roleIds = SafeCopyIds(talk.roleIds, null,
                "对话 " + talk.id + " 的角色字段（roleIds）");
            // 原版 IsSomebodyTalking 对空列表返回 false，此时姓名栏不会显示；
            // roleName 即使有值也不能把空 roleIds 变成真实说话人。
            if (roleIds.Count == 0) return "旁白/未指定";

            var parts = new List<string>();
            for (int i = 0; i < roleIds.Count && i < 4; i++)
            {
                int id = roleIds[i];
                if (id == -1)
                {
                    parts.Add("旁白");
                    continue;
                }
                if (id == 0)
                {
                    parts.Add(!string.IsNullOrEmpty(explicitName)
                        ? explicitName + "（主角）"
                        : "主角");
                    continue;
                }

                string name = i == 0 ? explicitName : null;
                if (string.IsNullOrEmpty(name) && personNames != null)
                {
                    string configured;
                    if (personNames.TryGetValue(id, out configured))
                        name = Summarize(configured, 18);
                }
                parts.Add(!string.IsNullOrEmpty(name)
                    ? name + "（" + id + "）"
                    : "角色" + id);
            }
            if (roleIds.Count > 4) parts.Add("…");
            return string.Join("，", parts);
        }

        internal static string Summarize(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = new char[value.Length];
            int count = 0;
            bool previousSpace = false;
            foreach (char c in value)
            {
                char normalized = char.IsWhiteSpace(c) ? ' ' : c;
                if (normalized == ' ' && previousSpace) continue;
                chars[count++] = normalized;
                previousSpace = normalized == ' ';
                if (count >= maxLength) break;
            }
            string result = new string(chars, 0, count).Trim();
            if (value.Length > count) result += "…";
            return result;
        }

        private static string JoinSearchText(params string[] values)
        {
            return string.Join(" ", values.Where(v => !string.IsNullOrEmpty(v)).ToArray());
        }

        private static void AddDiagnostic(
            EvtStoryGraphModel model, string message, int max = 16)
        {
            if (model == null || string.IsNullOrEmpty(message)) return;
            if (model.Diagnostics.Count < max) model.Diagnostics.Add(message);
        }

        private static string ShortError(Exception e)
        {
            if (e == null) return "未知错误";
            return e.GetType().Name + ": " + e.Message;
        }
    }
}
