using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Config;
using Newtonsoft.Json;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>剧情图编辑模式可建立的原生配置连线。首版统一写入双方共用的单值槽。</summary>
    internal enum StoryGraphEditPortKind
    {
        TalkNext,
        TalkNext2,
        TalkOption,
        OptionTalk,
        OptionTalk2,
    }

    internal enum StoryGraphEditNodeKind
    {
        None,
        Talk,
        Option,
    }

    /// <summary>
    /// 四种值只是内部快捷初始化方式，不是新的存储类型；最终都写入原生
    /// OptionCfg。主界面统一创建 Direct 空白选项，作者再自由配置条件、路线、
    /// 结束行为与小游戏；Ending 等值仅供专门的快捷动作与回归兼容使用。
    /// </summary>
    internal enum StoryGraphOptionTemplateKind
    {
        Direct,
        Conditional,
        Ending,
        MiniGame,
    }

    internal sealed class StoryGraphPasteResult
    {
        internal readonly Dictionary<int, int> TalkIdMap =
            new Dictionary<int, int>();
        internal readonly Dictionary<int, int> OptionIdMap =
            new Dictionary<int, int>();
        internal readonly List<TalkCfg> Talks = new List<TalkCfg>();
        internal readonly Dictionary<int, OptionCfg> Options =
            new Dictionary<int, OptionCfg>();
    }

    /// <summary>
    /// 与 ModEvtEditView 实时对象隔离的编辑草稿。
    ///
    /// 每次作者操作前保存整份深拷贝快照。事件配置通常只有几十到数百项，作者操作频率又远低于
    /// 每帧刷新；用整图快照换取简单、确定的撤销和失败回滚，比让临时显示节点承担数据身份安全。
    /// </summary>
    internal sealed class StoryGraphEditSession
    {
        private sealed class State
        {
            internal List<TalkCfg> Talks;
            internal Dictionary<int, OptionCfg> Options;
        }

        private sealed class HistoryEntry
        {
            internal State State;
            internal string Description;
        }

        private readonly Stack<HistoryEntry> _undo = new Stack<HistoryEntry>();
        private readonly Stack<HistoryEntry> _redo = new Stack<HistoryEntry>();
        // 属性检查器逐字写入时，同一次聚焦只保存一份“输入前”快照。
        // 任何其它数据/布局命令、撤销、重做或显式结束输入都会清除此键。
        private string _liveFieldEditKey;
        private readonly HashSet<int> _initialTalkIds = new HashSet<int>();
        private readonly HashSet<int> _initialOptionIds = new HashSet<int>();
        private readonly HashSet<int> _persistedTalkIds = new HashSet<int>();
        private readonly HashSet<int> _persistedOptionIds = new HashSet<int>();
        private readonly HashSet<int> _persistedEventIds = new HashSet<int>();
        private readonly HashSet<int> _builtInTalkIds = new HashSet<int>();
        private readonly HashSet<int> _builtInOptionIds = new HashSet<int>();
        private readonly HashSet<int> _builtInEventIds = new HashSet<int>();
        // 进入编辑模式时为补齐图显示而从全局 Cfg 合并进来、且不属于当前 Mod
        // JSON 的共享选项（如全局“确定”选项 1、跨事件段引用）。它们只借来显示：
        // 不可编辑、保存时绝不写入当前 Mod 的 OptionCfg.json，也不写回原编辑器
        // 内存——否则 Mod 会携带本体选项的冻结副本，在所有玩家机器上覆盖本体。
        private readonly HashSet<int> _frozenBuiltInOptionIds = new HashSet<int>();
        private readonly HashSet<int> _globallyReferencedTalkIds = new HashSet<int>();
        private readonly HashSet<int> _globallyReferencedOptionIds = new HashSet<int>();
        private readonly Dictionary<int, string> _initialTalkJson =
            new Dictionary<int, string>();
        private readonly Dictionary<int, string> _initialOptionJson =
            new Dictionary<int, string>();

        internal List<TalkCfg> Talks { get; private set; }
        internal Dictionary<int, OptionCfg> Options { get; private set; }
        internal int EventId { get; private set; }
        internal List<int> Entries { get; private set; }
        internal bool EntriesKnown { get; private set; }
        internal string ModRoot { get; private set; }
        internal bool Dirty { get; private set; }
        internal string LastAction { get; private set; }

        /// <summary>
        /// 事件按 StateEvtView 播放（EvtCfg.type==60 或 displayType==1，
        /// CommonEvtMgr 分发处）。该视图推进只读 talk.nextTalk，
        /// talk.check/nextTalk2 与 option.nextEvtId 从不读取，
        /// 选项 talkId/talkId2 的跳转阈值是 >0 而非 >1。
        /// </summary>
        internal bool IsStateEventView { get; private set; }

        internal bool CanUndo { get { return _undo.Count > 0; } }
        internal bool CanRedo { get { return _redo.Count > 0; } }
        internal IEnumerable<int> InitialTalkIds { get { return _initialTalkIds; } }
        internal IEnumerable<int> InitialOptionIds { get { return _initialOptionIds; } }

        internal StoryGraphEditSession(
            IList<TalkCfg> talks,
            IDictionary<int, OptionCfg> options,
            int eventId,
            IEnumerable<int> entries,
            bool entriesKnown,
            string modRoot,
            IEnumerable<int> mergedReferencedOptionIds = null)
        {
            // 插件持久层固定读写 Cfgs/zh-cn（游戏运行时也只从该目录合并 mod 配置），
            // 而原版编辑器跟随 LocalizationMgr.Lang；zh-hant 下图的数据源与写盘目录
            // 分叉、跨事件删除保护读不到真实 EvtCfg，宁可禁用编辑。读取语言失败时
            // 放行，不能因反射/初始化问题误伤 zh-cn 用户。
            string currentLanguage = null;
            try
            {
                currentLanguage = Sdk.LocalizationMgr.Lang;
            }
            catch (Exception languageError)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Edit] 读取游戏语言失败，按 zh-cn 继续："
                    + languageError.Message);
            }
            if (!string.IsNullOrEmpty(currentLanguage)
                && !string.Equals(currentLanguage, "zh-cn", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "当前游戏语言为 " + currentLanguage
                    + "：剧情图的保存与删除保护目前只支持 zh-cn 配置目录，"
                    + "为避免数据分叉已禁用编辑。请把游戏语言切回简体中文后再用剧情图编辑"
                    + "（游戏运行时本就只读取 Cfgs/zh-cn）。");

            Talks = CloneTalks(talks);
            Options = CloneOptions(options);
            EventId = eventId;
            Entries = entries != null ? new List<int>(entries) : new List<int>();
            EntriesKnown = entriesKnown;
            ModRoot = modRoot;
            try
            {
                EvtCfg viewEvt;
                string viewError;
                if (StoryGraphEditPersistence.TryReadEffectiveEvent(
                        modRoot, eventId, out viewEvt, out viewError)
                    && viewEvt != null)
                    IsStateEventView = viewEvt.type == 60
                                       || viewEvt.displayType == 1;
                else if (!string.IsNullOrEmpty(viewError))
                    Plugin.Log?.LogWarning(
                        "[StoryGraph.Edit] 读取事件显示类型失败，按普通对话事件校验："
                        + viewError);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Edit] 读取事件显示类型失败，按普通对话事件校验："
                    + e.Message);
            }

            foreach (TalkCfg talk in Talks)
                if (talk != null)
                {
                    _initialTalkIds.Add(talk.id);
                    _initialTalkJson[talk.id] =
                        JsonConvert.SerializeObject(talk, Formatting.None);
                }
            foreach (KeyValuePair<int, OptionCfg> pair in Options)
            {
                _initialOptionIds.Add(pair.Key);
                if (pair.Value != null)
                    _initialOptionJson[pair.Key] =
                        JsonConvert.SerializeObject(pair.Value, Formatting.None);
            }

            string recoveryError;
            if (!StoryGraphEditPersistence.TryRecoverPendingTransaction(
                    modRoot, out recoveryError))
                throw new InvalidOperationException(recoveryError);
            StoryGraphEditPersistence.TryReadPersistedIds(
                modRoot, _persistedTalkIds, _persistedOptionIds);
            StoryGraphEditPersistence.TryReadPersistedEventIds(
                modRoot, _persistedEventIds);
            try
            {
                HashSet<int> nativeTalkIds =
                    StoryGraphConfigProvenance.SnapshotNativeTalkIds(
                        _persistedTalkIds);
                HashSet<int> nativeOptionIds =
                    StoryGraphConfigProvenance.SnapshotNativeOptionIds(
                        _persistedOptionIds);
                HashSet<int> nativeEventIds =
                    StoryGraphConfigProvenance.SnapshotNativeEventIds(
                        _persistedEventIds);
                _builtInTalkIds.UnionWith(
                    StoryGraphConfigProvenance.BuildProtectedIds(
                        Cfg.TalkCfgMap != null
                            ? Cfg.TalkCfgMap.Keys : Enumerable.Empty<int>(),
                        _persistedTalkIds, nativeTalkIds));
                _builtInOptionIds.UnionWith(
                    StoryGraphConfigProvenance.BuildProtectedIds(
                        Cfg.OptionCfgMap != null
                            ? Cfg.OptionCfgMap.Keys : Enumerable.Empty<int>(),
                        _persistedOptionIds, nativeOptionIds));
                _builtInEventIds.UnionWith(
                    StoryGraphConfigProvenance.BuildProtectedIds(
                        Cfg.EvtCfgMap != null
                            ? Cfg.EvtCfgMap.Keys : Enumerable.Empty<int>(),
                        _persistedEventIds, nativeEventIds));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[StoryGraph.Edit] 读取配置来源失败：" + e.Message);
            }
            // 冻结集 = 合并注入且不在当前 Mod JSON 中的选项。判定只看
            // _persistedOptionIds：即使来源快照（provenance）失效，也宁可多冻结
            // 一个借来的选项，绝不把本体共享选项放进可写集合（fail-closed）。
            if (mergedReferencedOptionIds != null)
            {
                foreach (int mergedId in mergedReferencedOptionIds)
                    if (mergedId > 0 && !_persistedOptionIds.Contains(mergedId))
                        _frozenBuiltInOptionIds.Add(mergedId);
            }
            string referenceError;
            if (!StoryGraphEditPersistence.TryReadGloballyReferencedIds(
                    modRoot, _globallyReferencedTalkIds,
                    _globallyReferencedOptionIds, out referenceError))
                throw new InvalidOperationException(referenceError);
            LastAction = "已建立独立编辑草稿；尚未修改原文件";
        }

        internal IEnumerable<int> FrozenBuiltInOptionIds
        {
            get { return _frozenBuiltInOptionIds; }
        }

        internal bool IsFrozenBuiltInOption(int key)
        {
            return _frozenBuiltInOptionIds.Contains(key);
        }

        private bool EnsureOptionEditable(int key, out string message)
        {
            if (!_frozenBuiltInOptionIds.Contains(key))
            {
                message = null;
                return true;
            }
            message = "选项 " + key + " 是游戏内置/其它 Mod 的共享配置，剧情图只借来显示，"
                      + "不会把它写入当前 Mod。如需自定义，请右键“复制”生成本事件的"
                      + "新选项，再把对话的选项引用改挂到新编号上。";
            return false;
        }

        internal bool TryAddTalk(TalkCfg near, out TalkCfg created, out string message)
        {
            created = null;
            if (EntriesKnown
                && (Entries == null || !Entries.Any(value => value > 0)))
            {
                message = "当前事件没有有效的 EvtCfg.talkId 入口。剧情图目前不能修改事件配置，"
                        + "因此不会创建一个游戏永远进不去的孤立对话；请先在事件表单中设置入口。";
                return false;
            }
            int id;
            string entryError;
            bool fillsMissingEntry = TryAllocateMissingEntryTalkId(
                out id, out entryError);
            if (!fillsMissingEntry && !string.IsNullOrEmpty(entryError))
            {
                message = entryError;
                return false;
            }
            if (!fillsMissingEntry && !TryAllocateTalkId(near, out id))
            {
                message = "当前剧情组的 001～999 对话编号已经用完。";
                return false;
            }

            BeginMutation((fillsMissingEntry ? "补建入口对话 " : "新增对话 ") + id);
            created = CreateTalk(id);
            Talks.Add(created);
            message = fillsMissingEntry
                ? "已按当前事件的 talkId 补建入口对话 " + id
                  + "；它现在会作为剧情入口，可继续从端口拖线。"
                : "已新增对话 " + id
                  + "；可从端口拖线连接，或保存后回表单填写正文。";
            return true;
        }

        internal bool TryAddOption(
            TalkCfg parent, out OptionCfg created, out string message)
        {
            return TryAddOption(
                parent, StoryGraphOptionTemplateKind.Direct,
                out created, out message);
        }

        internal bool TryAddOption(
            TalkCfg parent, StoryGraphOptionTemplateKind template,
            out OptionCfg created, out string message)
        {
            created = null;
            if (parent == null || !Talks.Contains(parent))
            {
                message = "选项必须属于一条对话；请先选择它要显示在哪句对话之后。";
                return false;
            }
            if ((int)template < (int)StoryGraphOptionTemplateKind.Direct
                || (int)template > (int)StoryGraphOptionTemplateKind.MiniGame)
            {
                message = "选项用途无效，未创建任何配置。";
                return false;
            }
            int id;
            if (!TryAllocateOptionId(parent, out id))
            {
                message = "当前剧情组的 01～99 选项编号已经用完。";
                return false;
            }

            BeginMutation("新增选项 " + id);
            created = CreateOption(id);
            if (template == StoryGraphOptionTemplateKind.Ending)
                created.content = "确定";
            Options[id] = created;
            if (parent.option == null) parent.option = new List<int>();
            if (!parent.option.Contains(id)) parent.option.Add(id);
            switch (template)
            {
                case StoryGraphOptionTemplateKind.Conditional:
                    message = "已创建并挂到对话 " + parent.id + " 的条件分支选项 "
                              + id + "；下一步填写判断条件和成立 / 不成立路线。";
                    break;
                case StoryGraphOptionTemplateKind.Ending:
                    message = "已创建并挂到对话 " + parent.id + " 的结束选项 "
                              + id + "“确定”；玩家选择后会退出当前剧情。";
                    break;
                case StoryGraphOptionTemplateKind.MiniGame:
                    message = "已创建并挂到对话 " + parent.id + " 的小游戏选项 "
                              + id + "；下一步选择玩法并连接结果剧情。";
                    break;
                default:
                    message = "已创建并挂到对话 " + parent.id + " 的选项 "
                              + id + "；可在右侧配置文字、条件、路线或小游戏。";
                    break;
            }
            return true;
        }

        /// <summary>
        /// 为一条没有后续流程的末句创建原生“确定”选项。
        ///
        /// NewTalkView 对两种结尾的处理不同：Talk 没有 nextTalk/option 时，
        /// 玩家再次点击正文会直接关掉剧情；Talk 挂着一个没有 talkId/nextEvtId
        /// 的 Option 时，游戏会先显示该选项，选择后再退出剧情。
        /// </summary>
        internal bool TryAddEndingOption(
            TalkCfg parent, out OptionCfg created, out string message)
        {
            created = null;
            if (parent == null || !Talks.Contains(parent))
            {
                message = "请先选择一条真实对话作为剧情末句。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parent.content))
            {
                message = "对话 " + parent.id
                          + " 的正文为空；游戏会跳过空正文，也就不会显示结尾选项。"
                          + "请先填写末句正文。";
                return false;
            }
            if (HasNonZero(parent.option))
            {
                message = "对话 " + parent.id
                          + " 已有选项；请直接编辑现有选项，或先右击“选项”端口断开。";
                return false;
            }
            if (HasNonZero(parent.nextTalk) || HasNonZero(parent.nextTalk2))
            {
                message = "对话 " + parent.id
                          + " 仍有后续对话连线。为避免把已有分支静默覆盖，"
                          + "请先右击对应端口断开，再设为结尾。";
                return false;
            }
            if (parent.miniGame != null && parent.miniGame.Count > 0)
            {
                message = "对话 " + parent.id
                          + " 配置了小游戏；其结束流程应由小游戏结果端口决定，"
                          + "不能直接改成普通“确定”结尾。";
                return false;
            }

            int id;
            if (!TryAllocateOptionId(parent, out id))
            {
                message = "当前剧情组的 01～99 选项编号已经用完。";
                return false;
            }

            BeginMutation("为对话 " + parent.id + " 创建结尾选项 " + id);
            created = CreateOption(id);
            created.content = "确定";
            Options[id] = created;
            // 零只是表单占位，不是有效引用；直接换成唯一结尾项，避免运行时
            // 先尝试读取 OptionCfg[0] 并产生一条无意义错误日志。
            parent.option = new List<int> { id };
            message = "已为对话 " + parent.id + " 创建并连接结尾选项 "
                      + id + "“确定”；玩家点击后会退出剧情。";
            return true;
        }

        internal bool TryDuplicateTalk(
            TalkCfg source, out TalkCfg created, out string message)
        {
            created = null;
            if (source == null || !Talks.Contains(source))
            {
                message = "请先选择一个真实对话节点。";
                return false;
            }
            int id;
            if (!TryAllocateTalkId(source, out id))
            {
                message = "当前剧情组没有可用的对话编号。";
                return false;
            }

            BeginMutation("复制对话 " + source.id + " 为 " + id);
            created = CloneTalk(source);
            created.id = id;
            // ComfyUI 式复制只复制节点参数，不复制输出连线，避免一次复制悄悄多出执行路径。
            created.nextTalk = new List<int>();
            created.nextTalk2 = new List<int>();
            created.option = new List<int>();
            // 16/45 的输出藏在 miniGame 参数中；单节点复制同样不能把旧分支带过去。
            bool paramJumpCleared = MiniGameUtil.IsParamJump(created.miniGame);
            if (paramJumpCleared)
                created.miniGame = new List<double>();
            Talks.Add(created);
            message = "已复制为对话 " + id + "；输出连线已留空"
                      + (paramJumpCleared
                          ? "；参数跳转小游戏（含玩法与题库参数）已清空，请重新配置"
                          : string.Empty)
                      + "。";
            return true;
        }

        internal bool TryDuplicateOption(
            OptionCfg source, TalkCfg parent, out OptionCfg created, out string message)
        {
            created = null;
            if (source == null || !Options.Values.Contains(source))
            {
                message = "请先选择一个真实选项节点。";
                return false;
            }
            if (parent == null || !Talks.Contains(parent))
            {
                message = "无法确定这个选项属于哪条对话；为避免生成孤立选项，复制已取消。";
                return false;
            }
            int id;
            if (!TryAllocateOptionId(parent, out id))
            {
                message = "当前剧情组没有可用的选项编号。";
                return false;
            }

            BeginMutation("复制选项 " + source.id + " 为 " + id);
            created = CloneOption(source);
            created.id = id;
            created.talkId = new List<int>();
            created.talkId2 = new List<int>();
            // 复制体只在当前事件编辑器实例内存活；nextEvtId 指向同一 Mod 可见的
            // 事件，保留是安全且符合预期的，悬空目标由保存前 nextEvtId 预检兜底。
            bool paramJumpCleared = MiniGameUtil.IsParamJump(created.miniGame);
            if (paramJumpCleared)
                created.miniGame = new List<double>();
            Options[id] = created;
            if (parent != null && Talks.Contains(parent))
            {
                if (parent.option == null) parent.option = new List<int>();
                if (!parent.option.Contains(id)) parent.option.Add(id);
            }
            message = "已复制为选项 " + id + "；结果连线已留空"
                      + (created.nextEvtId > 0 ? "，后备事件跳转已保留" : string.Empty)
                      + (paramJumpCleared
                          ? "；参数跳转小游戏（含玩法与题库参数）已清空，请重新配置"
                          : string.Empty)
                      + "。";
            return true;
        }

        /// <summary>
        /// 粘贴一组配置。只保留组内连线，所有指向组外的输出都会置空；
        /// 新 ID 在当前草稿、完整 Mod、内置配置和全局悬空引用之间统一避让。
        /// 整组只产生一条撤销记录，分配失败时完全不修改草稿。
        /// </summary>
        internal bool TryPaste(
            IList<TalkCfg> talkTemplates,
            IDictionary<int, OptionCfg> optionTemplates,
            TalkCfg near,
            out StoryGraphPasteResult result,
            out string message)
        {
            result = null;
            var talks = talkTemplates != null
                ? talkTemplates.Where(item => item != null).ToList()
                : new List<TalkCfg>();
            var options = optionTemplates != null
                ? optionTemplates.Where(pair => pair.Value != null)
                    .OrderBy(pair => pair.Key).ToList()
                : new List<KeyValuePair<int, OptionCfg>>();
            if (talks.Count == 0 && options.Count == 0)
            {
                message = "剪贴板中没有可粘贴的对话或选项。";
                return false;
            }

            var originalTalkIds = new HashSet<int>();
            foreach (TalkCfg talk in talks)
            {
                if (talk.id <= 0 || !originalTalkIds.Add(talk.id))
                {
                    message = "剪贴板包含无效或重复的对话编号 " + talk.id + "，已拒绝粘贴。";
                    return false;
                }
            }
            var originalOptionIds = new HashSet<int>();
            foreach (KeyValuePair<int, OptionCfg> pair in options)
            {
                if (pair.Key <= 0 || !originalOptionIds.Add(pair.Key))
                {
                    message = "剪贴板包含无效或重复的选项键 " + pair.Key + "，已拒绝粘贴。";
                    return false;
                }
            }

            var paste = new StoryGraphPasteResult();
            var usedTalkIds = BuildUsedTalkIds();
            var usedOptionIds = BuildUsedOptionIds();
            TalkCfg validNear = near != null && Talks.Contains(near) ? near : null;
            var internallyReferencedOptions = new HashSet<int>(
                talks.SelectMany(talk => talk.option ?? new List<int>())
                    .Where(value => value > 0));
            List<int> detachedOptionKeys = options
                .Select(pair => pair.Key)
                .Where(key => !internallyReferencedOptions.Contains(key))
                .ToList();
            if (detachedOptionKeys.Count > 0 && validNear == null)
            {
                message = "剪贴板包含 " + detachedOptionKeys.Count
                          + " 个没有所属对话的选项；请先选择要挂载它们的对话再粘贴。";
                return false;
            }
            int group = validNear != null && validNear.id > 0
                ? validNear.id / 1000
                : EventId;
            if (group <= 0) group = EventId > 0 ? EventId : 1;

            foreach (TalkCfg template in talks)
            {
                int id;
                if (!TryAllocateGroupedId(group, 1000, 999, usedTalkIds, out id))
                {
                    message = "当前剧情组剩余编号不足，无法一次粘贴 "
                              + talks.Count + " 个对话。";
                    return false;
                }
                paste.TalkIdMap.Add(template.id, id);
                usedTalkIds.Add(id);
            }
            foreach (KeyValuePair<int, OptionCfg> pair in options)
            {
                int id;
                if (!TryAllocateGroupedId(group, 100, 99, usedOptionIds, out id))
                {
                    message = "当前剧情组剩余编号不足，无法一次粘贴 "
                              + options.Count + " 个选项。";
                    return false;
                }
                paste.OptionIdMap.Add(pair.Key, id);
                usedOptionIds.Add(id);
            }

            // 先在临时对象中完成全部深拷贝和引用重映射；只有全部成功后
            // 才 BeginMutation，避免异常形成半组配置。
            // 条件参数（family 3）里的组内对话/选项引用同步换新编号；
            // 组外引用是合法创作，保持原值。
            int conditionRemaps = 0;
            int falseBranchRepairs = 0;
            int paramJumpClears = 0;
            int preservedNextEvents = 0;
            foreach (TalkCfg template in talks)
            {
                TalkCfg clone = CloneTalk(template);
                clone.id = paste.TalkIdMap[template.id];
                clone.nextTalk = RemapSlots(template.nextTalk, paste.TalkIdMap);
                clone.nextTalk2 = RemapSlots(template.nextTalk2, paste.TalkIdMap);
                // RemapSlots 把组外目标原位写 0，但 nextTalk2 首槽 0 是
                // 整组回退开关：不修正会让已粘贴的其余槽位静默不可达。
                // 单槽 [0] 与原生回退等价，保持现状。
                if (clone.nextTalk2 != null && clone.nextTalk2.Count > 1
                    && clone.nextTalk2[0] == 0
                    && clone.nextTalk2.Skip(1).Any(value => value > 0))
                {
                    RepairFalseBranchFirstSlot(clone.nextTalk2, clone.nextTalk, 0);
                    falseBranchRepairs++;
                }
                clone.option = RemapCollection(template.option, paste.OptionIdMap);
                clone.miniGame = RemapMiniGameTargets(
                    template.miniGame, paste.TalkIdMap);
                // RemapMiniGameTargets 对无法整组重映射的参数跳转返回空表：
                // 源是参数跳转（必非空）而结果为空即被整表清空。
                if (MiniGameUtil.IsParamJump(template.miniGame)
                    && (clone.miniGame == null || clone.miniGame.Count == 0))
                    paramJumpClears++;
                int remapped;
                clone.check = ConditionRefUtil.RemapInGroup(
                    template.check, paste.TalkIdMap, paste.OptionIdMap,
                    out remapped);
                conditionRemaps += remapped;
                paste.Talks.Add(clone);
            }
            foreach (KeyValuePair<int, OptionCfg> pair in options)
            {
                int id = paste.OptionIdMap[pair.Key];
                OptionCfg clone = CloneOption(pair.Value);
                clone.id = id;
                clone.talkId = RemapSlots(pair.Value.talkId, paste.TalkIdMap);
                clone.talkId2 = RemapSlots(pair.Value.talkId2, paste.TalkIdMap);
                clone.miniGame = RemapMiniGameTargets(
                    pair.Value.miniGame, paste.TalkIdMap);
                if (MiniGameUtil.IsParamJump(pair.Value.miniGame)
                    && (clone.miniGame == null || clone.miniGame.Count == 0))
                    paramJumpClears++;
                int remapped;
                clone.check = ConditionRefUtil.RemapInGroup(
                    pair.Value.check, paste.TalkIdMap, paste.OptionIdMap,
                    out remapped);
                conditionRemaps += remapped;
                clone.precondition = ConditionRefUtil.RemapInGroup(
                    pair.Value.precondition, paste.TalkIdMap, paste.OptionIdMap,
                    out remapped);
                conditionRemaps += remapped;
                clone.stateCond = ConditionRefUtil.RemapInGroup(
                    pair.Value.stateCond, paste.TalkIdMap, paste.OptionIdMap,
                    out remapped);
                conditionRemaps += remapped;
                // 剪贴板只在当前事件编辑器实例内存活；nextEvtId 指向同一 Mod
                // 可见的事件，保留是安全的，悬空目标由保存前 nextEvtId 预检兜底。
                if (clone.nextEvtId > 0) preservedNextEvents++;
                paste.Options.Add(id, clone);
            }

            BeginMutation("粘贴 " + paste.Talks.Count + " 个对话和 "
                          + paste.Options.Count + " 个选项");
            Talks.AddRange(paste.Talks);
            foreach (KeyValuePair<int, OptionCfg> pair in paste.Options)
                Options.Add(pair.Key, pair.Value);
            if (detachedOptionKeys.Count > 0)
            {
                if (validNear.option == null) validNear.option = new List<int>();
                foreach (int oldKey in detachedOptionKeys)
                {
                    int mapped = paste.OptionIdMap[oldKey];
                    if (!validNear.option.Contains(mapped))
                        validNear.option.Add(mapped);
                }
            }
            result = paste;
            message = "已粘贴 " + paste.Talks.Count + " 个对话和 "
                      + paste.Options.Count + " 个选项；组外连线已安全断开"
                      + (detachedOptionKeys.Count > 0
                          ? "，独立选项已挂到对话 " + validNear.id
                          : string.Empty)
                      + (falseBranchRepairs > 0
                          ? "；失败分支首槽已按整组回退语义修正 "
                            + falseBranchRepairs + " 处"
                          : string.Empty)
                      + (paramJumpClears > 0
                          ? "；" + paramJumpClears
                            + " 处参数跳转小游戏（含玩法与题库参数）因目标不在本组内"
                            + "已整表清空，请重新配置"
                          : string.Empty)
                      + (preservedNextEvents > 0
                          ? "；" + preservedNextEvents + " 个选项的后备事件跳转已保留"
                          : string.Empty)
                      + "。"
                      + (conditionRemaps > 0
                          ? "（组内条件引用已同步重映射 " + conditionRemaps + " 处）"
                          : string.Empty);
            return true;
        }

        internal bool TryUpdateTalkFields(
            TalkCfg talk, string content, string roleName, string showTxt,
            out string message)
        {
            bool historyRecorded;
            return TryUpdateTalkFieldsCore(
                talk, content, roleName, showTxt, null,
                out historyRecorded, out message);
        }

        /// <summary>
        /// 属性检查器实时写入。相同 liveEditKey 的连续字符只产生一条撤销记录；
        /// historyRecorded 告诉窗口是否需要同步追加统一数据/布局时间线。
        /// </summary>
        internal bool TryUpdateTalkFieldsLive(
            TalkCfg talk, string content, string roleName, string showTxt,
            string liveEditKey, out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                historyRecorded = false;
                message = "实时字段编辑键无效。";
                return false;
            }
            return TryUpdateTalkFieldsCore(
                talk, content, roleName, showTxt, liveEditKey,
                out historyRecorded, out message);
        }

        private bool TryUpdateTalkFieldsCore(
            TalkCfg talk, string content, string roleName, string showTxt,
            string liveEditKey, out bool historyRecorded, out string message)
        {
            historyRecorded = false;
            if (talk == null || !Talks.Contains(talk))
            {
                message = "所选对话已不在当前草稿中。";
                return false;
            }
            content = content ?? string.Empty;
            roleName = TalkRoleNameUtil.NormalizeOverride(roleName);
            showTxt = showTxt ?? string.Empty;
            if (string.Equals(talk.content, content, StringComparison.Ordinal)
                && string.Equals(talk.roleName, roleName, StringComparison.Ordinal)
                && string.Equals(talk.showTxt, showTxt, StringComparison.Ordinal))
            {
                message = "基础字段没有变化。";
                return false;
            }
            historyRecorded = BeginFieldMutation(
                "编辑对话 " + talk.id + " 的基础字段", liveEditKey);
            talk.content = content;
            talk.roleName = roleName;
            talk.showTxt = showTxt;
            message = "已实时更新对话 " + talk.id + " 的正文和显示字段。";
            return true;
        }

        /// <summary>
        /// 修改真正决定说话人的 TalkCfg.roleIds。roleName 只覆盖显示文字，不能把
        /// “主角”变成另一人物；基础页因此单独使用此命令。
        /// </summary>
        internal bool TryUpdateTalkSpeaker(
            TalkCfg talk, IEnumerable<int> roleIds, out string message)
        {
            bool historyRecorded;
            return TryUpdateTalkSpeakerCore(
                talk, roleIds, null, out historyRecorded, out message);
        }

        /// <summary>
        /// 对话角色实时写入。相同 liveEditKey 的连续有效输入只产生一条撤销记录；
        /// 无效输入在写入前被拒绝，historyRecorded 告诉窗口是否需要追加统一时间线。
        /// </summary>
        internal bool TryUpdateTalkSpeakerLive(
            TalkCfg talk, IEnumerable<int> roleIds, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                historyRecorded = false;
                message = "实时对话角色编辑键无效。";
                return false;
            }
            return TryUpdateTalkSpeakerCore(
                talk, roleIds, liveEditKey, out historyRecorded, out message);
        }

        private bool TryUpdateTalkSpeakerCore(
            TalkCfg talk, IEnumerable<int> roleIds, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            historyRecorded = false;
            if (talk == null || !Talks.Contains(talk))
            {
                message = "所选对话已不在当前草稿中。";
                return false;
            }
            List<int> next = roleIds != null
                ? new List<int>(roleIds)
                : new List<int>();
            if (next.Count > 1 && next.Contains(-1))
            {
                message = "旁白（-1）不能与主角或其他人物同时作为说话人。";
                return false;
            }
            if (next.Count != next.Distinct().Count())
            {
                message = "对话角色列表包含重复人物，请去掉重复 ID。";
                return false;
            }
            List<int> current = talk.roleIds ?? new List<int>();
            if (current.SequenceEqual(next))
            {
                message = "对话角色没有变化。";
                return false;
            }

            string description = "修改对话 " + talk.id + " 的说话人";
            if (string.IsNullOrEmpty(liveEditKey))
            {
                BeginMutation(description);
                historyRecorded = true;
            }
            else
            {
                historyRecorded = BeginFieldMutation(description, liveEditKey);
            }
            talk.roleIds = next;
            message = (string.IsNullOrEmpty(liveEditKey)
                    ? "已更新对话 "
                    : "已实时更新对话 ")
                + talk.id + " 的说话人；节点摘要和人物演出页已同步。";
            return true;
        }

        internal bool TryUpdateOptionFields(
            OptionCfg option, string content, string showTxt, string tag,
            out string message)
        {
            bool historyRecorded;
            return TryUpdateOptionFieldsCore(
                option, content, showTxt, tag, null,
                out historyRecorded, out message);
        }

        internal bool TryUpdateOptionFieldsLive(
            OptionCfg option, string content, string showTxt, string tag,
            string liveEditKey, out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                historyRecorded = false;
                message = "实时字段编辑键无效。";
                return false;
            }
            return TryUpdateOptionFieldsCore(
                option, content, showTxt, tag, liveEditKey,
                out historyRecorded, out message);
        }

        private bool TryUpdateOptionFieldsCore(
            OptionCfg option, string content, string showTxt, string tag,
            string liveEditKey, out bool historyRecorded, out string message)
        {
            historyRecorded = false;
            int key = FindOptionKey(option);
            if (option == null || key == int.MinValue)
            {
                message = "所选选项已不在当前草稿中。";
                return false;
            }
            if (!EnsureOptionEditable(key, out message)) return false;
            content = content ?? string.Empty;
            showTxt = showTxt ?? string.Empty;
            tag = tag ?? string.Empty;
            if (string.Equals(option.content, content, StringComparison.Ordinal)
                && string.Equals(option.showTxt, showTxt, StringComparison.Ordinal)
                && string.Equals(option.tag, tag, StringComparison.Ordinal))
            {
                message = "基础字段没有变化。";
                return false;
            }
            historyRecorded = BeginFieldMutation(
                "编辑选项 " + key + " 的基础字段", liveEditKey);
            option.content = content;
            option.showTxt = showTxt;
            option.tag = tag;
            message = "已实时更新选项 " + key + " 的正文和显示字段。";
            return true;
        }

        internal bool TryUpdateOptionNextEventLive(
            OptionCfg option, int nextEventId, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            historyRecorded = false;
            int key = FindOptionKey(option);
            if (option == null || key == int.MinValue)
            {
                message = "所选选项已不在当前草稿中。";
                return false;
            }
            if (!EnsureOptionEditable(key, out message)) return false;
            if (string.IsNullOrEmpty(liveEditKey))
            {
                message = "实时下一事件编辑键无效。";
                return false;
            }
            if (nextEventId < 0)
            {
                message = "下一事件 ID 只能为 0（不跳转）或正整数。";
                return false;
            }
            if (option.nextEvtId == nextEventId)
            {
                message = "后备下一事件没有变化。";
                return false;
            }

            historyRecorded = BeginFieldMutation(
                "编辑选项 " + key + " 的后备下一事件", liveEditKey);
            option.nextEvtId = nextEventId;
            message = nextEventId == 0
                ? "已实时清空选项 " + key + " 的后备下一事件。"
                : "已实时把选项 " + key
                  + " 的后备下一事件设为 " + nextEventId + "。";
            return true;
        }

        /// <summary>
        /// 原子写入 TalkCfg 的人物、画面声音与逻辑演出字段。调用方可以只改快照中的
        /// 一组字段；其余字段从当前 Talk 快照继承，因此每次“应用一页”只产生一条历史。
        /// </summary>
        internal bool TryUpdateTalkPerformance(
            TalkCfg talk, StoryGraphPerformanceData performance,
            out string message)
        {
            bool historyRecorded;
            return TryUpdateTalkPerformanceCore(
                talk, performance, null, out historyRecorded, out message);
        }

        internal bool TryUpdateTalkPerformanceLive(
            TalkCfg talk, StoryGraphPerformanceData performance,
            string liveEditKey, out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                historyRecorded = false;
                message = "实时演出编辑键无效。";
                return false;
            }
            return TryUpdateTalkPerformanceCore(
                talk, performance, liveEditKey,
                out historyRecorded, out message);
        }

        private bool TryUpdateTalkPerformanceCore(
            TalkCfg talk, StoryGraphPerformanceData performance,
            string liveEditKey, out bool historyRecorded, out string message)
        {
            historyRecorded = false;
            if (talk == null || !Talks.Contains(talk))
            {
                message = "所选对话已不在当前草稿中。";
                return false;
            }
            if (performance == null)
            {
                message = "演出配置为空，草稿未修改。";
                return false;
            }
            string validation;
            if (!performance.TryValidate(out validation))
            {
                message = "演出配置校验失败：" + validation;
                return false;
            }
            StoryGraphPerformanceData current =
                StoryGraphPerformanceData.FromTalk(talk);
            if (current.ContentEquals(performance))
            {
                message = "演出配置没有变化。";
                return false;
            }

            historyRecorded = BeginFieldMutation(
                "编辑对话 " + talk.id + " 的演出配置", liveEditKey);
            performance.ApplyTo(talk);
            message = (string.IsNullOrEmpty(liveEditKey)
                    ? "已更新对话 " : "已实时更新对话 ")
                      + talk.id + " 的演出配置。";
            return true;
        }

        /// <summary>原子写入 TalkCfg.miniGame；编号、参数和来源支持范围与原表单共用。</summary>
        internal bool TryUpdateTalkMiniGame(
            TalkCfg talk, IEnumerable<double> miniGame, out string message)
        {
            bool historyRecorded;
            return TryUpdateTalkMiniGameCore(
                talk, miniGame, null, out historyRecorded, out message);
        }

        internal bool TryUpdateTalkMiniGameLive(
            TalkCfg talk, IEnumerable<double> miniGame, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                historyRecorded = false;
                message = "实时小游戏编辑键无效。";
                return false;
            }
            return TryUpdateTalkMiniGameCore(
                talk, miniGame, liveEditKey,
                out historyRecorded, out message);
        }

        private bool TryUpdateTalkMiniGameCore(
            TalkCfg talk, IEnumerable<double> miniGame, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            historyRecorded = false;
            if (talk == null || !Talks.Contains(talk))
            {
                message = "所选对话已不在当前草稿中。";
                return false;
            }
            List<double> next;
            if (!TryPrepareMiniGame(miniGame, true, out next, out message))
                return false;
            List<double> current = talk.miniGame ?? new List<double>();
            if (current.SequenceEqual(next))
            {
                message = "小游戏配置没有变化。";
                return false;
            }

            historyRecorded = BeginFieldMutation(
                "编辑对话 " + talk.id + " 的小游戏", liveEditKey);
            talk.miniGame = next;
            message = next.Count == 0
                ? "已实时清空对话 " + talk.id + " 的小游戏。"
                : "已实时更新对话 " + talk.id + " 的小游戏；流程语义和端口已刷新。";
            return true;
        }

        /// <summary>原子写入 OptionCfg.miniGame；空列表表示清空。</summary>
        internal bool TryUpdateOptionMiniGame(
            OptionCfg option, IEnumerable<double> miniGame, out string message)
        {
            bool historyRecorded;
            return TryUpdateOptionMiniGameCore(
                option, miniGame, null, out historyRecorded, out message);
        }

        internal bool TryUpdateOptionMiniGameLive(
            OptionCfg option, IEnumerable<double> miniGame, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                historyRecorded = false;
                message = "实时小游戏编辑键无效。";
                return false;
            }
            return TryUpdateOptionMiniGameCore(
                option, miniGame, liveEditKey,
                out historyRecorded, out message);
        }

        private bool TryUpdateOptionMiniGameCore(
            OptionCfg option, IEnumerable<double> miniGame, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            historyRecorded = false;
            int key = FindOptionKey(option);
            if (option == null || key == int.MinValue)
            {
                message = "所选选项已不在当前草稿中。";
                return false;
            }
            if (!EnsureOptionEditable(key, out message)) return false;
            List<double> next;
            if (!TryPrepareMiniGame(miniGame, false, out next, out message))
                return false;
            List<double> current = option.miniGame ?? new List<double>();
            if (current.SequenceEqual(next))
            {
                message = "小游戏配置没有变化。";
                return false;
            }

            historyRecorded = BeginFieldMutation(
                "编辑选项 " + key + " 的小游戏", liveEditKey);
            option.miniGame = next;
            message = next.Count == 0
                ? "已实时清空选项 " + key + " 的小游戏。"
                : "已实时更新选项 " + key + " 的小游戏；流程语义和端口已刷新。";
            return true;
        }

        private static bool TryPrepareMiniGame(
            IEnumerable<double> miniGame,
            bool isTalk,
            out List<double> values,
            out string message)
        {
            values = null;
            if (miniGame == null)
            {
                message = "小游戏配置为空对象，草稿未修改；清空请传入空列表。";
                return false;
            }
            values = new List<double>(miniGame);
            string warning = MiniGameUtil.Validate(values, isTalk);
            if (!string.IsNullOrEmpty(warning))
            {
                message = "小游戏配置校验失败：" + warning;
                values = null;
                return false;
            }
            message = null;
            return true;
        }

        private static bool TryValidateMiniGameStorage(
            IEnumerable<double> miniGame, out string error)
        {
            error = null;
            if (miniGame == null) return true;
            int index = 0;
            foreach (double value in miniGame)
            {
                index++;
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    error = "小游戏第 " + index + " 项不能是 NaN 或 Infinity。";
                    return false;
                }
            }
            return true;
        }

        private static bool TryValidateMiniGameConfiguration(
            IEnumerable<double> miniGame, bool isTalk, out string error)
        {
            error = null;
            if (!TryValidateMiniGameStorage(miniGame, out error)) return false;
            List<double> values = miniGame != null
                ? new List<double>(miniGame)
                : new List<double>();
            error = MiniGameUtil.Validate(values, isTalk);
            return string.IsNullOrEmpty(error);
        }

        /// <summary>
        /// 原子写入 TalkCfg 的判断、成立/不成立去向与两组效果。
        /// </summary>
        internal bool TryUpdateTalkLogic(
            TalkCfg talk, StoryGraphLogicData logic, out string message)
        {
            bool historyRecorded;
            return TryUpdateTalkLogicCore(
                talk, logic, null, out historyRecorded, out message);
        }

        internal bool TryUpdateTalkLogicLive(
            TalkCfg talk, StoryGraphLogicData logic, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                historyRecorded = false;
                message = "实时逻辑编辑键无效。";
                return false;
            }
            return TryUpdateTalkLogicCore(
                talk, logic, liveEditKey,
                out historyRecorded, out message);
        }

        private bool TryUpdateTalkLogicCore(
            TalkCfg talk, StoryGraphLogicData logic, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            historyRecorded = false;
            if (talk == null || !Talks.Contains(talk))
            {
                message = "所选对话已不在当前草稿中。";
                return false;
            }
            if (logic == null)
            {
                message = "逻辑配置为空，草稿未修改。";
                return false;
            }
            string validation;
            if (!logic.TryValidate(out validation))
            {
                message = "逻辑配置校验失败：" + validation;
                return false;
            }
            if (StoryGraphLogicData.FromTalk(talk).ContentEquals(logic))
            {
                message = "逻辑配置没有变化。";
                return false;
            }

            historyRecorded = BeginFieldMutation(
                "编辑对话 " + talk.id + " 的完整逻辑", liveEditKey);
            logic.ApplyTo(talk);
            message = (string.IsNullOrEmpty(liveEditKey)
                    ? "已更新对话 " : "已实时更新对话 ")
                      + talk.id + " 的判断、剧情去向与效果。";
            return true;
        }

        /// <summary>
        /// 原子写入 OptionCfg 的显示条件、选择后判断、成立/不成立去向与效果。
        /// </summary>
        internal bool TryUpdateOptionLogic(
            OptionCfg option, StoryGraphLogicData logic,
            out string message)
        {
            bool historyRecorded;
            return TryUpdateOptionEffectsCore(
                option, logic, null, out historyRecorded, out message);
        }

        internal bool TryUpdateOptionLogicLive(
            OptionCfg option, StoryGraphLogicData logic, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                historyRecorded = false;
                message = "实时逻辑编辑键无效。";
                return false;
            }
            return TryUpdateOptionEffectsCore(
                option, logic, liveEditKey,
                out historyRecorded, out message);
        }

        // 兼容现有调用与回归测试；方法名来自旧版只编辑 effect/effect2 的实现。
        internal bool TryUpdateOptionEffects(
            OptionCfg option, StoryGraphLogicData logic,
            out string message)
        {
            bool historyRecorded;
            return TryUpdateOptionEffectsCore(
                option, logic, null, out historyRecorded, out message);
        }

        internal bool TryUpdateOptionEffectsLive(
            OptionCfg option, StoryGraphLogicData logic, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                historyRecorded = false;
                message = "实时逻辑编辑键无效。";
                return false;
            }
            return TryUpdateOptionEffectsCore(
                option, logic, liveEditKey,
                out historyRecorded, out message);
        }

        private bool TryUpdateOptionEffectsCore(
            OptionCfg option, StoryGraphLogicData logic, string liveEditKey,
            out bool historyRecorded, out string message)
        {
            historyRecorded = false;
            int key = FindOptionKey(option);
            if (option == null || key == int.MinValue)
            {
                message = "所选选项已不在当前草稿中。";
                return false;
            }
            if (!EnsureOptionEditable(key, out message)) return false;
            if (logic == null)
            {
                message = "逻辑配置为空，草稿未修改。";
                return false;
            }
            string validation;
            if (!logic.TryValidate(out validation))
            {
                message = "逻辑配置校验失败：" + validation;
                return false;
            }
            if (StoryGraphLogicData.FromOption(option).ContentEquals(logic))
            {
                message = "逻辑配置没有变化。";
                return false;
            }

            historyRecorded = BeginFieldMutation(
                "编辑选项 " + key + " 的完整逻辑", liveEditKey);
            logic.ApplyTo(option);
            message = (string.IsNullOrEmpty(liveEditKey)
                    ? "已更新选项 " : "已实时更新选项 ")
                      + key + " 的显示条件、判断、剧情去向与效果。";
            return true;
        }

        internal void EndLiveFieldEdit(string liveEditKey)
        {
            if (string.IsNullOrEmpty(liveEditKey)
                || string.Equals(_liveFieldEditKey, liveEditKey,
                    StringComparison.Ordinal))
                _liveFieldEditKey = null;
        }

        /// <summary>
        /// 原版事件表单保存桥接：保留构造时的 initial ID 集合作为删除基线，
        /// 仅替换本次要写入的当前草稿。失败时不改变会话，且不制造撤销记录。
        /// </summary>
        internal bool TrySynchronizeDraftForExternalSave(
            IEnumerable<TalkCfg> talks,
            IEnumerable<KeyValuePair<int, OptionCfg>> options,
            out string error)
        {
            error = null;
            List<TalkCfg> nextTalks;
            Dictionary<int, OptionCfg> nextOptions;
            try
            {
                nextTalks = CloneTalks(talks);
                nextOptions = CloneOptions(options);
            }
            catch (Exception e)
            {
                error = "复制普通事件编辑器草稿失败：" + e.Message;
                return false;
            }

            List<TalkCfg> oldTalks = Talks;
            Dictionary<int, OptionCfg> oldOptions = Options;
            Talks = nextTalks;
            Options = nextOptions;
            _liveFieldEditKey = null;
            if (TryValidate(out error)) return true;
            Talks = oldTalks;
            Options = oldOptions;
            return false;
        }

        internal bool TryReplaceTalk(
            TalkCfg target, TalkCfg replacement,
            out TalkCfg updated, out string message)
        {
            bool historyRecorded;
            return TryReplaceTalkCore(
                target, replacement, null,
                out updated, out historyRecorded, out message);
        }

        internal bool TryReplaceTalkLive(
            TalkCfg target, TalkCfg replacement, string liveEditKey,
            out TalkCfg updated, out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                updated = null;
                historyRecorded = false;
                message = "实时高级 JSON 编辑键无效。";
                return false;
            }
            return TryReplaceTalkCore(
                target, replacement, liveEditKey,
                out updated, out historyRecorded, out message);
        }

        private bool TryReplaceTalkCore(
            TalkCfg target, TalkCfg replacement, string liveEditKey,
            out TalkCfg updated, out bool historyRecorded, out string message)
        {
            updated = null;
            historyRecorded = false;
            int index = target != null ? Talks.FindIndex(item =>
                ReferenceEquals(item, target)) : -1;
            if (index < 0 || replacement == null)
            {
                message = "高级配置目标已失效或 JSON 为空。";
                return false;
            }
            if (replacement.id != target.id)
            {
                message = "高级 JSON 不能修改对话 ID（当前 " + target.id
                          + "，输入 " + replacement.id + "）；请保持编号不变。";
                return false;
            }
            string performanceError;
            if (!StoryGraphPerformanceData.FromTalk(replacement)
                    .TryValidate(out performanceError))
            {
                message = "高级 JSON 的演出字段校验失败：" + performanceError;
                return false;
            }
            string logicError;
            if (!StoryGraphLogicData.FromTalk(replacement)
                    .TryValidate(out logicError))
            {
                message = "高级 JSON 的逻辑字段校验失败：" + logicError;
                return false;
            }
            string miniGameError;
            if (!TryValidateMiniGameConfiguration(
                    replacement.miniGame, true, out miniGameError))
            {
                message = "高级 JSON 的小游戏字段无效：" + miniGameError;
                return false;
            }
            if (SerializedConfigEquals(target, replacement))
            {
                message = "高级 JSON 没有改变对话配置。";
                return false;
            }

            updated = CloneTalk(replacement);
            historyRecorded = BeginFieldMutation(
                "应用对话 " + target.id + " 的高级 JSON", liveEditKey);
            Talks[index] = updated;
            message = (string.IsNullOrEmpty(liveEditKey)
                    ? "已应用对话 " : "已实时应用对话 ")
                      + updated.id + " 的完整原生字段。";
            return true;
        }

        internal bool TryReplaceOption(
            OptionCfg target, OptionCfg replacement,
            out OptionCfg updated, out string message)
        {
            bool historyRecorded;
            return TryReplaceOptionCore(
                target, replacement, null,
                out updated, out historyRecorded, out message);
        }

        internal bool TryReplaceOptionLive(
            OptionCfg target, OptionCfg replacement, string liveEditKey,
            out OptionCfg updated, out bool historyRecorded, out string message)
        {
            if (string.IsNullOrEmpty(liveEditKey))
            {
                updated = null;
                historyRecorded = false;
                message = "实时高级 JSON 编辑键无效。";
                return false;
            }
            return TryReplaceOptionCore(
                target, replacement, liveEditKey,
                out updated, out historyRecorded, out message);
        }

        private bool TryReplaceOptionCore(
            OptionCfg target, OptionCfg replacement, string liveEditKey,
            out OptionCfg updated, out bool historyRecorded, out string message)
        {
            updated = null;
            historyRecorded = false;
            int key = FindOptionKey(target);
            if (key == int.MinValue || replacement == null)
            {
                message = "高级配置目标已失效或 JSON 为空。";
                return false;
            }
            if (!EnsureOptionEditable(key, out message)) return false;
            if (replacement.id != key)
            {
                message = "高级 JSON 不能修改选项字典键（当前 " + key
                          + "，输入 id=" + replacement.id + "）；请保持编号不变。";
                return false;
            }
            string logicError;
            if (!StoryGraphLogicData.FromOption(replacement)
                    .TryValidate(out logicError))
            {
                message = "高级 JSON 的逻辑效果校验失败：" + logicError;
                return false;
            }
            string miniGameError;
            if (!TryValidateMiniGameConfiguration(
                    replacement.miniGame, false, out miniGameError))
            {
                message = "高级 JSON 的小游戏字段无效：" + miniGameError;
                return false;
            }
            if (SerializedConfigEquals(target, replacement))
            {
                message = "高级 JSON 没有改变选项配置。";
                return false;
            }

            updated = CloneOption(replacement);
            historyRecorded = BeginFieldMutation(
                "应用选项 " + key + " 的高级 JSON", liveEditKey);
            Options[key] = updated;
            message = (string.IsNullOrEmpty(liveEditKey)
                    ? "已应用选项 " : "已实时应用选项 ")
                      + key + " 的完整原生字段。";
            return true;
        }

        private static bool SerializedConfigEquals<T>(T left, T right)
            where T : class
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            return string.Equals(
                JsonConvert.SerializeObject(left, Formatting.None),
                JsonConvert.SerializeObject(right, Formatting.None),
                StringComparison.Ordinal);
        }

        internal bool TryConnect(
            StoryGraphEditPortKind port,
            TalkCfg sourceTalk,
            OptionCfg sourceOption,
            TalkCfg targetTalk,
            OptionCfg targetOption,
            out string message)
        {
            string validation;
            if (!ValidateConnectionSource(port, sourceTalk, sourceOption, out validation))
            {
                message = validation;
                return false;
            }
            if (!EnsureConnectionSourceEditable(port, sourceOption, out validation))
            {
                message = validation;
                return false;
            }

            if (port == StoryGraphEditPortKind.TalkOption)
            {
                if (targetOption == null || !Options.Values.Contains(targetOption))
                {
                    message = "“选项”端口只能连接到真实选项节点。";
                    return false;
                }
                int targetKey = FindOptionKey(targetOption);
                if (targetKey == int.MinValue)
                {
                    message = "无法确定目标选项的字典键。";
                    return false;
                }
                if (sourceTalk.option != null && sourceTalk.option.Contains(targetKey))
                {
                    message = "对话 " + sourceTalk.id + " 已经引用选项 " + targetKey + "。";
                    return false;
                }
                BeginMutation("连接对话 " + sourceTalk.id + " 到选项 " + targetKey);
                if (sourceTalk.option == null) sourceTalk.option = new List<int>();
                sourceTalk.option.Add(targetKey);
                message = "已连接：对话 " + sourceTalk.id + " → 选项 " + targetKey + "。";
                return true;
            }

            if (targetTalk == null || !Talks.Contains(targetTalk))
            {
                message = "该端口只能连接到真实对话节点。";
                return false;
            }

            List<int> oldValues = GetPortValues(port, sourceTalk, sourceOption);
            if (IsSingleValue(oldValues, targetTalk.id))
            {
                message = "该端口已经连接到对话 " + targetTalk.id + "。";
                return false;
            }
            if (oldValues != null && oldValues.Count > 1)
            {
                message = "该端口含有多个性别槽位或额外配置。为避免拖线时静默覆盖，"
                          + "首版不会直接替换；请在原表单中编辑，或先右击端口明确清空，再重新连接。";
                return false;
            }

            string sourceName = sourceTalk != null
                ? "对话 " + sourceTalk.id
                : "选项 " + sourceOption.id;
            BeginMutation("重连" + sourceName + "的" + PortLabel(port));
            SetPortValues(port, sourceTalk, sourceOption,
                new List<int> { targetTalk.id });
            string replaced = HasNonZero(oldValues)
                ? "（原端口内容已替换，可撤销）"
                : string.Empty;
            message = "已连接：" + sourceName + "的" + PortLabel(port)
                      + " → 对话 " + targetTalk.id + replaced;
            return true;
        }

        /// <summary>端口拖到画布空白处：一次撤销内自动创建兼容节点并连接。</summary>
        internal bool TryCreateConnected(
            StoryGraphEditPortKind port,
            TalkCfg sourceTalk,
            OptionCfg sourceOption,
            out TalkCfg createdTalk,
            out OptionCfg createdOption,
            out string message)
        {
            createdTalk = null;
            createdOption = null;
            string validation;
            if (!ValidateConnectionSource(port, sourceTalk, sourceOption, out validation))
            {
                message = validation;
                return false;
            }
            if (!EnsureConnectionSourceEditable(port, sourceOption, out validation))
            {
                message = validation;
                return false;
            }

            if (port == StoryGraphEditPortKind.TalkOption)
            {
                int optionId;
                if (!TryAllocateOptionId(sourceTalk, out optionId))
                {
                    message = "当前剧情组没有可用的选项编号。";
                    return false;
                }
                BeginMutation("创建并连接选项 " + optionId);
                createdOption = CreateOption(optionId);
                Options[optionId] = createdOption;
                if (sourceTalk.option == null) sourceTalk.option = new List<int>();
                sourceTalk.option.Add(optionId);
                message = "已创建选项 " + optionId + "；继续从它的“结果”端口拖出分支。";
                return true;
            }

            List<int> existing = GetPortValues(port, sourceTalk, sourceOption);
            if (existing != null && existing.Count > 1)
            {
                message = "该端口含有多个性别槽位或额外配置，不能用空白拖放直接覆盖；"
                          + "请先在表单处理，或右击端口明确清空。";
                return false;
            }

            TalkCfg near = sourceTalk;
            if (near == null && sourceOption != null)
            {
                int sourceKey = FindOptionKey(sourceOption);
                if (sourceKey != int.MinValue) near = FindParentTalk(sourceKey);
            }
            int talkId;
            if (!TryAllocateTalkId(near, out talkId))
            {
                message = "当前剧情组没有可用的对话编号。";
                return false;
            }

            BeginMutation("创建并连接对话 " + talkId);
            createdTalk = CreateTalk(talkId);
            Talks.Add(createdTalk);
            SetPortValues(port, sourceTalk, sourceOption,
                new List<int> { talkId });
            message = "已创建并连接对话 " + talkId
                      + (HasNonZero(existing) ? "（原单值连线已替换，可撤销）" : string.Empty)
                      + "；保存后可回表单填写正文。";
            return true;
        }

        internal bool TryClearPort(
            StoryGraphEditPortKind port,
            TalkCfg sourceTalk,
            OptionCfg sourceOption,
            out string message)
        {
            string validation;
            if (!ValidateConnectionSource(port, sourceTalk, sourceOption, out validation))
            {
                message = validation;
                return false;
            }
            if (!EnsureConnectionSourceEditable(port, sourceOption, out validation))
            {
                message = validation;
                return false;
            }
            List<int> values = GetPortValues(port, sourceTalk, sourceOption);
            if (!HasNonZero(values))
            {
                message = PortLabel(port) + "当前没有连线。";
                return false;
            }

            string sourceName = sourceTalk != null
                ? "对话 " + sourceTalk.id
                : "选项 " + sourceOption.id;
            BeginMutation("断开" + sourceName + "的" + PortLabel(port));
            SetPortValues(port, sourceTalk, sourceOption, new List<int>());
            message = "已断开" + sourceName + "的" + PortLabel(port) + "；可用撤销恢复。";
            return true;
        }

        internal List<int> SnapshotPortValues(
            StoryGraphEditPortKind port,
            TalkCfg sourceTalk,
            OptionCfg sourceOption)
        {
            string validation;
            if (!ValidateConnectionSource(
                    port, sourceTalk, sourceOption, out validation))
                return new List<int>();
            List<int> values = GetPortValues(port, sourceTalk, sourceOption);
            return values != null ? new List<int>(values) : new List<int>();
        }

        internal bool TryClearPortSlot(
            StoryGraphEditPortKind port,
            TalkCfg sourceTalk,
            OptionCfg sourceOption,
            int index,
            out string message)
        {
            string validation;
            if (!ValidateConnectionSource(port, sourceTalk, sourceOption, out validation))
            {
                message = validation;
                return false;
            }
            if (!EnsureConnectionSourceEditable(port, sourceOption, out validation))
            {
                message = validation;
                return false;
            }
            List<int> values = GetPortValues(port, sourceTalk, sourceOption);
            if (values == null || index < 0 || index >= values.Count
                || values[index] == 0)
            {
                message = PortLabel(port) + "的该槽位已经没有连线。";
                return false;
            }
            if (port == StoryGraphEditPortKind.TalkNext2 && values.Count > 1)
            {
                message = "nextTalk2 的首项 0 是“整组回退”开关，不能安全地只清掉一个性别槽位；"
                        + "请使用端口菜单中的“整组恢复为 nextTalk”。";
                return false;
            }

            int removedId = values[index];
            string slotLabel = PortSlotLabel(port, values.Count, index);
            string sourceName = sourceTalk != null
                ? "对话 " + sourceTalk.id
                : "选项 " + sourceOption.id;
            BeginMutation("断开" + sourceName + "的" + PortLabel(port)
                          + slotLabel);
            if (port == StoryGraphEditPortKind.TalkOption || index >= 2)
                values.RemoveAt(index);
            else if (values.Count == 1)
                values.Clear();
            else
                values[index] = 0; // 保留男/女槽位，绝不让后一项左移改变含义。
            message = "已断开" + sourceName + "的" + PortLabel(port)
                    + "·" + slotLabel
                    + " → " + removedId + "；其余槽位保持不变，可撤销。";
            return true;
        }

        /// <summary>
        /// 会话内其余配置的条件参数（family 3）对目标的引用。连线断开逻辑不清理
        /// 条件参数（语义不可自动推断），因此删除前 fail-closed 整体拒绝；
        /// excluded* 是本次一并删除的对象，它们之间的互相引用不算阻塞。
        /// </summary>
        private string DescribeSessionConditionReferences(
            StoryGraphEditNodeKind targetKind,
            int targetId,
            ICollection<TalkCfg> excludedTalks,
            ICollection<int> excludedOptionKeys)
        {
            var holders = new List<string>();
            foreach (TalkCfg talk in Talks)
            {
                if (talk == null) continue;
                if (excludedTalks != null && ContainsReference(excludedTalks, talk))
                    continue;
                bool hit = targetKind == StoryGraphEditNodeKind.Talk
                    ? ConditionRefUtil.ReferencesTalk(talk.check, targetId)
                    : ConditionRefUtil.ReferencesOption(talk.check, targetId);
                if (hit) holders.Add("对话 " + talk.id);
            }
            foreach (KeyValuePair<int, OptionCfg> pair in Options)
            {
                if (pair.Value == null) continue;
                if (excludedOptionKeys != null
                    && excludedOptionKeys.Contains(pair.Key)) continue;
                bool hit;
                if (targetKind == StoryGraphEditNodeKind.Talk)
                    hit = ConditionRefUtil.ReferencesTalk(pair.Value.check, targetId)
                          || ConditionRefUtil.ReferencesTalk(
                              pair.Value.precondition, targetId)
                          || ConditionRefUtil.ReferencesTalk(
                              pair.Value.stateCond, targetId);
                else
                    hit = ConditionRefUtil.ReferencesOption(pair.Value.check, targetId)
                          || ConditionRefUtil.ReferencesOption(
                              pair.Value.precondition, targetId)
                          || ConditionRefUtil.ReferencesOption(
                              pair.Value.stateCond, targetId);
                if (hit) holders.Add("选项 " + pair.Key);
            }
            return holders.Count > 0
                ? string.Join("、", holders.ToArray())
                : null;
        }

        /// <summary>
        /// 借来显示的共享选项（冻结克隆）永不写回 Mod JSON，对它们“断线”不会
        /// 持久化：若其 talkId/talkId2/参数跳转仍引用被删对话，保存后全局配置
        /// 照旧指向已删除的对话，运行时点击即 KeyNotFound 崩溃。删除必须
        /// fail-closed，与条件参数守卫同语义。
        /// </summary>
        private string DescribeFrozenOptionJumpReferences(int talkId)
        {
            List<string> holders = null;
            foreach (KeyValuePair<int, OptionCfg> pair in Options)
            {
                OptionCfg option = pair.Value;
                if (option == null
                    || !_frozenBuiltInOptionIds.Contains(pair.Key)) continue;
                bool hit = (option.talkId != null && option.talkId.Contains(talkId))
                           || (option.talkId2 != null && option.talkId2.Contains(talkId))
                           || MiniGameReferencesTalk(option.miniGame, talkId);
                if (!hit) continue;
                if (holders == null) holders = new List<string>();
                holders.Add("选项 " + pair.Key);
            }
            return holders != null ? string.Join("、", holders.ToArray()) : null;
        }

        internal bool TryDeleteTalk(TalkCfg talk, out string message)
        {
            if (talk == null || !Talks.Contains(talk))
            {
                message = "请先选择一个真实对话节点。";
                return false;
            }

            int id = talk.id;
            bool anotherSameId = Talks.Any(item =>
                item != null && !ReferenceEquals(item, talk) && item.id == id);
            if (!anotherSameId && _builtInTalkIds.Contains(id))
            {
                message = "对话 " + id
                          + " 存在游戏内置基底；删除 Mod 覆盖后它仍会在重开时恢复。"
                          + "首版不提供不可持久化的“伪删除”。";
                return false;
            }
            if (!anotherSameId && _initialTalkIds.Contains(id)
                && !_persistedTalkIds.Contains(id))
            {
                message = "对话 " + id
                          + " 不属于当前 Mod 的持久化记录，无法安全删除。";
                return false;
            }
            if (!anotherSameId && Entries != null && Entries.Contains(id))
            {
                message = "对话 " + id
                          + " 是当前事件入口。首版不能在剧情图里改 EvtCfg.talkId，请先在事件配置中更换入口。";
                return false;
            }
            if (!anotherSameId && _initialTalkIds.Contains(id))
            {
                string references;
                string referenceError;
                if (!StoryGraphEditPersistence.TryFindExternalReferences(
                        this, StoryGraphEditNodeKind.Talk, id,
                        out references, out referenceError))
                {
                    message = referenceError;
                    return false;
                }
                if (!string.IsNullOrEmpty(references))
                {
                    message = "不能删除对话 " + id + "；当前图之外仍有引用：" + references;
                    return false;
                }
            }
            if (!anotherSameId)
            {
                string conditionHolders = DescribeSessionConditionReferences(
                    StoryGraphEditNodeKind.Talk, id,
                    new[] { talk }, null);
                if (conditionHolders != null)
                {
                    message = "不能删除对话 " + id + "；" + conditionHolders
                              + " 的条件参数仍引用它，请先修改这些条件参数。";
                    return false;
                }
                string frozenHolders = DescribeFrozenOptionJumpReferences(id);
                if (frozenHolders != null)
                {
                    message = "不能删除对话 " + id + "；" + frozenHolders
                              + " 是借来显示的共享配置，仍跳转到它且断线不会写盘；"
                              + "如不想在本事件使用该选项，请先断开对话的选项引用。";
                    return false;
                }
            }

            BeginMutation("删除对话 " + id);
            Talks.Remove(talk);
            int refs = 0;
            if (!anotherSameId)
            {
                foreach (TalkCfg item in Talks)
                {
                    if (item == null) continue;
                    refs += ClearTalkReferencesTo(item, id);
                }
                foreach (KeyValuePair<int, OptionCfg> pair in Options)
                {
                    if (pair.Value == null) continue;
                    // 冻结克隆的断线不会写盘，且必须与全局内置保持逐字节一致
                    //（上方守卫已保证它们不引用被删对话），跳过。
                    if (_frozenBuiltInOptionIds.Contains(pair.Key)) continue;
                    refs += ClearOptionReferencesTo(pair.Value, id);
                }
            }
            message = "已删除对话 " + id
                      + (refs > 0 ? "，并断开 " + refs + " 处入线" : string.Empty)
                      + "；保存前可撤销。";
            return true;
        }

        internal bool TryDeleteOption(OptionCfg option, out string message)
        {
            if (option == null || !Options.Values.Contains(option))
            {
                message = "请先选择一个真实选项节点。";
                return false;
            }
            int key = FindOptionKey(option);
            if (key == int.MinValue)
            {
                message = "无法确定该选项在字典中的键。";
                return false;
            }
            if (_frozenBuiltInOptionIds.Contains(key))
            {
                message = "选项 " + key
                          + " 是借来显示的共享配置，不在当前 Mod 中，无法删除；"
                          + "如不想在本事件使用它，请断开对话的选项引用。";
                return false;
            }
            if (_builtInOptionIds.Contains(key))
            {
                message = "选项 " + key
                          + " 存在游戏内置基底，删除覆盖后仍会在重开时恢复；已阻止伪删除。";
                return false;
            }
            if (_initialOptionIds.Contains(key) && !_persistedOptionIds.Contains(key))
            {
                message = "选项 " + key + " 不属于当前 Mod 的持久化记录，无法安全删除。";
                return false;
            }
            if (_initialOptionIds.Contains(key))
            {
                string references;
                string referenceError;
                if (!StoryGraphEditPersistence.TryFindExternalReferences(
                        this, StoryGraphEditNodeKind.Option, key,
                        out references, out referenceError))
                {
                    message = referenceError;
                    return false;
                }
                if (!string.IsNullOrEmpty(references))
                {
                    message = "不能删除选项 " + key + "；当前图之外仍有引用：" + references;
                    return false;
                }
            }
            {
                string conditionHolders = DescribeSessionConditionReferences(
                    StoryGraphEditNodeKind.Option, key,
                    null, new[] { key });
                if (conditionHolders != null)
                {
                    message = "不能删除选项 " + key + "；" + conditionHolders
                              + " 的条件参数仍引用它，请先修改这些条件参数。";
                    return false;
                }
            }

            BeginMutation("删除选项 " + key);
            Options.Remove(key);
            int refs = 0;
            foreach (TalkCfg talk in Talks)
                if (talk != null) refs += RemoveAll(talk.option, key);
            message = "已删除选项 " + key
                      + (refs > 0 ? "，并从 " + refs + " 处对话引用中移除" : string.Empty)
                      + "；保存前可撤销。";
            return true;
        }

        /// <summary>批量删除真实配置；全部安全检查通过后才建立一条撤销记录。</summary>
        internal bool TryDeleteMany(
            IEnumerable<TalkCfg> talkCandidates,
            IEnumerable<OptionCfg> optionCandidates,
            out string message)
        {
            var talks = new List<TalkCfg>();
            if (talkCandidates != null)
            {
                foreach (TalkCfg talk in talkCandidates)
                {
                    if (talk == null || !Talks.Contains(talk))
                    {
                        message = "批量删除包含已失效的对话节点，操作已取消。";
                        return false;
                    }
                    if (!ContainsReference(talks, talk)) talks.Add(talk);
                }
            }

            var optionEntries = new List<KeyValuePair<int, OptionCfg>>();
            if (optionCandidates != null)
            {
                foreach (OptionCfg option in optionCandidates)
                {
                    if (option == null)
                    {
                        message = "批量删除包含已失效的选项节点，操作已取消。";
                        return false;
                    }
                    List<KeyValuePair<int, OptionCfg>> matches = Options
                        .Where(pair => ReferenceEquals(pair.Value, option)).ToList();
                    if (matches.Count != 1)
                    {
                        message = matches.Count == 0
                            ? "批量删除中的选项已不在当前草稿中。"
                            : "同一选项对象对应多个字典键，无法安全批量删除。";
                        return false;
                    }
                    if (!optionEntries.Any(pair => pair.Key == matches[0].Key))
                        optionEntries.Add(matches[0]);
                }
            }
            if (talks.Count == 0 && optionEntries.Count == 0)
            {
                message = "没有可删除的真实对话或选项节点。";
                return false;
            }

            var deletingTalkIds = new HashSet<int>(talks.Select(talk => talk.id));
            var deletingOptionIds = new HashSet<int>(optionEntries.Select(pair => pair.Key));
            foreach (int id in deletingTalkIds)
            {
                bool remains = Talks.Any(talk => talk != null && talk.id == id
                    && !ContainsReference(talks, talk));
                if (remains) continue;
                if (_builtInTalkIds.Contains(id))
                {
                    message = "对话 " + id
                              + " 存在游戏内置基底；批量删除已整体取消。";
                    return false;
                }
                if (_initialTalkIds.Contains(id) && !_persistedTalkIds.Contains(id))
                {
                    message = "对话 " + id
                              + " 不属于当前 Mod 的持久化记录；批量删除已整体取消。";
                    return false;
                }
                if (Entries != null && Entries.Contains(id))
                {
                    message = "对话 " + id
                              + " 是当前事件入口；请先在事件配置中更换入口。";
                    return false;
                }
                if (_initialTalkIds.Contains(id))
                {
                    string references;
                    string referenceError;
                    if (!StoryGraphEditPersistence.TryFindExternalReferences(
                            this, StoryGraphEditNodeKind.Talk, id,
                            out references, out referenceError))
                    {
                        message = referenceError;
                        return false;
                    }
                    if (!string.IsNullOrEmpty(references))
                    {
                        message = "不能删除对话 " + id
                                  + "；当前图之外仍有引用：" + references;
                        return false;
                    }
                }
                string conditionHolders = DescribeSessionConditionReferences(
                    StoryGraphEditNodeKind.Talk, id, talks, deletingOptionIds);
                if (conditionHolders != null)
                {
                    message = "不能删除对话 " + id + "；" + conditionHolders
                              + " 的条件参数仍引用它，请先修改这些条件参数；"
                              + "批量删除已整体取消。";
                    return false;
                }
                string frozenHolders = DescribeFrozenOptionJumpReferences(id);
                if (frozenHolders != null)
                {
                    message = "不能删除对话 " + id + "；" + frozenHolders
                              + " 是借来显示的共享配置，仍跳转到它且断线不会写盘；"
                              + "如不想在本事件使用该选项，请先断开对话的选项引用；"
                              + "批量删除已整体取消。";
                    return false;
                }
            }

            foreach (KeyValuePair<int, OptionCfg> pair in optionEntries)
            {
                int id = pair.Key;
                if (_frozenBuiltInOptionIds.Contains(id))
                {
                    message = "选项 " + id
                              + " 是借来显示的共享配置，不在当前 Mod 中，无法删除；"
                              + "批量删除已整体取消。如不想在本事件使用它，请断开对话的选项引用。";
                    return false;
                }
                if (_builtInOptionIds.Contains(id))
                {
                    message = "选项 " + id
                              + " 存在游戏内置基底；批量删除已整体取消。";
                    return false;
                }
                if (_initialOptionIds.Contains(id) && !_persistedOptionIds.Contains(id))
                {
                    message = "选项 " + id
                              + " 不属于当前 Mod 的持久化记录；批量删除已整体取消。";
                    return false;
                }
                if (_initialOptionIds.Contains(id))
                {
                    string references;
                    string referenceError;
                    if (!StoryGraphEditPersistence.TryFindExternalReferences(
                            this, StoryGraphEditNodeKind.Option, id,
                            out references, out referenceError))
                    {
                        message = referenceError;
                        return false;
                    }
                    if (!string.IsNullOrEmpty(references))
                    {
                        message = "不能删除选项 " + id
                                  + "；当前图之外仍有引用：" + references;
                        return false;
                    }
                }
                string conditionHolders = DescribeSessionConditionReferences(
                    StoryGraphEditNodeKind.Option, id, talks, deletingOptionIds);
                if (conditionHolders != null)
                {
                    message = "不能删除选项 " + id + "；" + conditionHolders
                              + " 的条件参数仍引用它，请先修改这些条件参数；"
                              + "批量删除已整体取消。";
                    return false;
                }
            }

            BeginMutation("批量删除 " + talks.Count + " 个对话和 "
                          + optionEntries.Count + " 个选项");
            Talks.RemoveAll(talk => ContainsReference(talks, talk));
            foreach (KeyValuePair<int, OptionCfg> pair in optionEntries)
                Options.Remove(pair.Key);

            int disconnected = 0;
            foreach (int id in deletingTalkIds)
            {
                if (Talks.Any(talk => talk != null && talk.id == id)) continue;
                foreach (TalkCfg talk in Talks)
                {
                    if (talk == null) continue;
                    disconnected += ClearTalkReferencesTo(talk, id);
                }
                foreach (KeyValuePair<int, OptionCfg> optionPair in Options)
                {
                    if (optionPair.Value == null) continue;
                    // 与 TryDeleteTalk 同理：冻结克隆跳过，守卫已挡住其引用。
                    if (_frozenBuiltInOptionIds.Contains(optionPair.Key)) continue;
                    disconnected += ClearOptionReferencesTo(optionPair.Value, id);
                }
            }
            foreach (int id in deletingOptionIds)
                foreach (TalkCfg talk in Talks)
                    if (talk != null) disconnected += RemoveAll(talk.option, id);

            message = "已批量删除 " + talks.Count + " 个对话和 "
                      + optionEntries.Count + " 个选项"
                      + (disconnected > 0 ? "，并安全断开 " + disconnected + " 处引用" : string.Empty)
                      + "；保存前可一次撤销。";
            return true;
        }

        internal bool TryUndo(out string message)
        {
            _liveFieldEditKey = null;
            if (_undo.Count == 0)
            {
                message = "没有可撤销的操作。";
                return false;
            }
            HistoryEntry entry = _undo.Pop();
            _redo.Push(new HistoryEntry
            {
                State = CaptureState(),
                Description = entry.Description,
            });
            RestoreState(entry.State);
            Dirty = _undo.Count > 0;
            LastAction = "已撤销：" + entry.Description;
            message = LastAction;
            return true;
        }

        internal void ClearRedoHistory()
        {
            _liveFieldEditKey = null;
            _redo.Clear();
        }

        internal bool TryRedo(out string message)
        {
            _liveFieldEditKey = null;
            if (_redo.Count == 0)
            {
                message = "没有可重做的操作。";
                return false;
            }
            HistoryEntry entry = _redo.Pop();
            _undo.Push(new HistoryEntry
            {
                State = CaptureState(),
                Description = entry.Description,
            });
            RestoreState(entry.State);
            Dirty = true;
            LastAction = "已重做：" + entry.Description;
            message = LastAction;
            return true;
        }

        internal bool TryValidate(out string error)
        {
            error = null;
            if (Talks == null || Options == null)
            {
                error = "编辑草稿的数据容器已失效。";
                return false;
            }

            var issues = new List<string>();
            var talkIds = new HashSet<int>();
            for (int i = 0; i < Talks.Count; i++)
            {
                TalkCfg talk = Talks[i];
                if (talk == null)
                {
                    issues.Add("对话列表第 " + (i + 1) + " 项为空。");
                    continue;
                }
                if (talk.id <= 0)
                    issues.Add("对话编号必须大于 0，当前为 " + talk.id + "。");
                if (!talkIds.Add(talk.id))
                    issues.Add("存在重复的对话编号 " + talk.id
                             + "；请删除或在原表单中改号后再保存。");
                string performanceError;
                if (!StoryGraphPerformanceData.FromTalk(talk)
                        .TryValidate(out performanceError))
                    issues.Add("对话 " + talk.id + " 的演出字段无效："
                             + performanceError);
                string logicError;
                if (!StoryGraphLogicData.FromTalk(talk)
                        .TryValidate(out logicError))
                    issues.Add("对话 " + talk.id + " 的逻辑字段无效："
                             + logicError);
                string miniGameError;
                if (!TryValidateMiniGameStorage(
                        talk.miniGame, out miniGameError))
                    issues.Add("对话 " + talk.id + " 的小游戏字段无效："
                             + miniGameError);
            }

            foreach (KeyValuePair<int, OptionCfg> pair in Options)
            {
                if (pair.Key <= 0 || pair.Value == null)
                {
                    issues.Add("选项字典包含无效项：" + pair.Key + "。");
                    continue;
                }
                if (pair.Value.id != pair.Key)
                    issues.Add("选项字典键 " + pair.Key + " 与配置编号 "
                             + pair.Value.id + " 不一致。");
                string logicError;
                if (!StoryGraphLogicData.FromOption(pair.Value)
                        .TryValidate(out logicError))
                    issues.Add("选项 " + pair.Key + " 的逻辑效果无效："
                             + logicError);
                string miniGameError;
                if (!TryValidateMiniGameStorage(
                        pair.Value.miniGame, out miniGameError))
                    issues.Add("选项 " + pair.Key + " 的小游戏字段无效："
                             + miniGameError);
            }

            HashSet<int> knownTalkIds = BuildKnownTalkIds();
            HashSet<int> knownOptionIds = BuildKnownOptionIds();
            HashSet<int> knownEventIds = BuildKnownEventIds();
            var talksById = new Dictionary<int, TalkCfg>();
            foreach (TalkCfg talk in Talks)
                if (talk != null && talk.id > 0) talksById[talk.id] = talk;
            HashSet<int> reachableTalkIds;
            HashSet<int> reachableOptionIds;
            if (EntriesKnown)
            {
                BuildRuntimeReachability(
                    talksById, out reachableTalkIds, out reachableOptionIds);
            }
            else
            {
                reachableTalkIds = new HashSet<int>(talksById.Keys);
                reachableOptionIds = new HashSet<int>(Options.Keys);
            }
            ValidateEventEntries(knownTalkIds, issues);
            foreach (TalkCfg talk in Talks)
                if (talk != null && reachableTalkIds.Contains(talk.id)
                    && ShouldValidateTalkRuntime(talk))
                {
                    ValidateTalkRuntime(
                        talk, knownTalkIds, knownOptionIds,
                        IsStateEventView, issues);
                    if (IsStateEventView)
                        ValidateStateEventTalkFlow(talk, issues);
                }
            foreach (KeyValuePair<int, OptionCfg> pair in Options)
                if (pair.Value != null && reachableOptionIds.Contains(pair.Key)
                    && ShouldValidateOptionRuntime(pair.Key, pair.Value))
                    ValidateOptionRuntime(
                        pair.Key, pair.Value, knownTalkIds, knownEventIds,
                        IsStateEventView, issues);
            ValidateAutomaticTalkCycles(issues);
            issues = issues.Distinct(StringComparer.Ordinal).ToList();

            if (issues.Count == 0) return true;
            const int previewCount = 10;
            var preview = new List<string>();
            for (int i = 0; i < issues.Count && i < previewCount; i++)
                preview.Add((i + 1) + ". " + issues[i]);
            error = "保存前运行时预检发现 " + issues.Count + " 个问题：\n"
                  + string.Join("\n", preview.ToArray());
            if (issues.Count > previewCount)
                error += "\n……另有 " + (issues.Count - previewCount)
                       + " 项，请先处理以上问题后再次预检。";
            Plugin.Log?.LogWarning("[StoryGraph.Edit.Preflight] " + error);
            return false;
        }

        private HashSet<int> BuildKnownTalkIds()
        {
            var result = new HashSet<int>(_builtInTalkIds);
            result.UnionWith(_persistedTalkIds);
            var current = new HashSet<int>(
                Talks.Where(value => value != null && value.id > 0)
                    .Select(value => value.id));
            foreach (int initialId in _initialTalkIds)
                if (!current.Contains(initialId)
                    && !_builtInTalkIds.Contains(initialId))
                    result.Remove(initialId);
            result.UnionWith(current);
            return result;
        }

        private HashSet<int> BuildKnownOptionIds()
        {
            var result = new HashSet<int>(_builtInOptionIds);
            result.UnionWith(_persistedOptionIds);
            var current = new HashSet<int>(
                Options.Where(pair => pair.Key > 0 && pair.Value != null)
                    .Select(pair => pair.Key));
            foreach (int initialId in _initialOptionIds)
                if (!current.Contains(initialId)
                    && !_builtInOptionIds.Contains(initialId))
                    result.Remove(initialId);
            result.UnionWith(current);
            return result;
        }

        private HashSet<int> BuildKnownEventIds()
        {
            var result = new HashSet<int>(_builtInEventIds);
            result.UnionWith(_persistedEventIds);
            if (EventId > 0) result.Add(EventId);
            return result;
        }

        private bool ShouldValidateTalkRuntime(TalkCfg talk)
        {
            if (talk == null || talk.id <= 0) return false;
            if (!_builtInTalkIds.Contains(talk.id)) return true;
            try
            {
                TalkCfg builtIn;
                if (Cfg.TalkCfgMap != null
                    && Cfg.TalkCfgMap.TryGetValue(talk.id, out builtIn)
                    && builtIn != null)
                {
                    return !string.Equals(
                        JsonConvert.SerializeObject(builtIn, Formatting.None),
                        JsonConvert.SerializeObject(talk, Formatting.None),
                        StringComparison.Ordinal);
                }
            }
            catch { }
            string initial;
            return _persistedTalkIds.Contains(talk.id)
                   || !_initialTalkJson.TryGetValue(talk.id, out initial)
                   || !string.Equals(initial, JsonConvert.SerializeObject(
                       talk, Formatting.None), StringComparison.Ordinal);
        }

        private bool ShouldValidateOptionRuntime(int key, OptionCfg option)
        {
            if (key <= 0 || option == null) return false;
            if (!_builtInOptionIds.Contains(key)) return true;
            try
            {
                OptionCfg builtIn;
                if (Cfg.OptionCfgMap != null
                    && Cfg.OptionCfgMap.TryGetValue(key, out builtIn)
                    && builtIn != null)
                {
                    return !string.Equals(
                        JsonConvert.SerializeObject(builtIn, Formatting.None),
                        JsonConvert.SerializeObject(option, Formatting.None),
                        StringComparison.Ordinal);
                }
            }
            catch { }
            string initial;
            return _persistedOptionIds.Contains(key)
                   || !_initialOptionJson.TryGetValue(key, out initial)
                   || !string.Equals(initial, JsonConvert.SerializeObject(
                       option, Formatting.None), StringComparison.Ordinal);
        }

        internal ISet<int> SnapshotKnownTalkIds()
        {
            return BuildKnownTalkIds();
        }

        internal ISet<int> SnapshotKnownEventIds()
        {
            return BuildKnownEventIds();
        }

        internal int CountOptionReferences(int optionId)
        {
            if (optionId <= 0 || Talks == null) return 0;
            int count = 0;
            foreach (TalkCfg talk in Talks)
            {
                if (talk == null || talk.option == null) continue;
                count += talk.option.Count(value => value == optionId);
            }
            return count;
        }

        private void ValidateEventEntries(
            ISet<int> knownTalkIds, ICollection<string> issues)
        {
            if (!EntriesKnown || Entries == null) return;
            int count = Math.Min(Entries.Count, 2);
            for (int i = 0; i < count; i++)
            {
                int targetId = Entries[i];
                if (targetId < 0)
                {
                    issues.Add("事件 " + EventId + " 的 "
                             + GenderSlotLabel(Entries.Count, i)
                             + "入口不能是负数 " + targetId + "。");
                }
                else if (targetId > 0 && !knownTalkIds.Contains(targetId))
                {
                    issues.Add("事件 " + EventId + " 的 "
                             + GenderSlotLabel(Entries.Count, i)
                             + "入口指向不存在的对话 " + targetId + "。");
                }
            }
        }

        private static void ValidateTalkPresentationRuntime(
            TalkCfg talk, ICollection<string> issues)
        {
            if (talk.roleIds == null)
                issues.Add("对话 " + talk.id
                         + " 的 roleIds 为 null；游戏在无人物动作时会直接枚举它并抛异常。");

            if (talk.roles != null)
            {
                for (int i = 0; i < talk.roles.Count; i++)
                {
                    List<float> row = talk.roles[i];
                    if (row == null || row.Count < 2) continue;
                    int actionId = (int)row[1];
                    try
                    {
                        if (Cfg.TalkAnimeCfgMap == null
                            || !Cfg.TalkAnimeCfgMap.ContainsKey(actionId))
                        {
                            issues.Add("对话 " + talk.id + " 的人物动作第 "
                                     + (i + 1) + " 行引用不存在的动作码 "
                                     + actionId + "；播放时会直接索引失败。");
                            continue;
                        }
                    }
                    catch (Exception e)
                    {
                        issues.Add("对话 " + talk.id + " 无法核对人物动作码 "
                                 + actionId + "：" + e.Message);
                        continue;
                    }

                    if ((actionId == 3000 || actionId == 3006
                         || actionId == 3009 || actionId == 3014)
                        && row.Count < 3)
                    {
                        issues.Add("对话 " + talk.id + " 的动作码 " + actionId
                                 + " 至少需要 1 个参数；播放时会读取越界。");
                    }
                }
            }

            if (talk.screenEffect == null || talk.screenEffect.Count == 0) return;
            int screenCode = (int)talk.screenEffect[0];
            if (string.IsNullOrWhiteSpace(talk.content)
                && (screenCode == 4015 || screenCode == 4016
                    || screenCode == 4017 || screenCode == 4019))
            {
                issues.Add("对话 " + talk.id + " 的正文为空；游戏会直接跳过整句，"
                         + "因此 CG 指令 " + screenCode
                         + " 不会执行。请填写正文或把指令移到其它节点。");
            }
            int required = RequiredScreenEffectColumns(screenCode);
            if (required > 0 && talk.screenEffect.Count < required)
                issues.Add("对话 " + talk.id + " 的屏幕效果 " + screenCode
                         + " 至少需要 " + (required - 1)
                         + " 个参数；播放时会读取越界。");
        }

        private static int RequiredScreenEffectColumns(int screenCode)
        {
            switch (screenCode)
            {
                case 4014:
                case 4016:
                    return 3;
                case 4004:
                case 4005:
                case 4007:
                case 4011:
                case 4015:
                case 4018:
                case 4019:
                    return 2;
                default:
                    return 0;
            }
        }

        private static void ValidateTalkRuntime(
            TalkCfg talk,
            ISet<int> knownTalkIds,
            ISet<int> knownOptionIds,
            bool stateEventView,
            ICollection<string> issues)
        {
            ValidateTalkPresentationRuntime(talk, issues);
            string miniGameError;
            if (!TryValidateMiniGameConfiguration(
                    talk.miniGame, true, out miniGameError))
                issues.Add("对话 " + talk.id + " 的小游戏字段无效："
                         + miniGameError);
            bool hasMiniGame = talk.miniGame != null && talk.miniGame.Count > 0;
            bool paramJump = hasMiniGame && MiniGameUtil.IsParamJump(talk.miniGame);
            bool hasContent = !string.IsNullOrWhiteSpace(talk.content);

            if (hasContent && talk.option != null && talk.option.Count > 0)
            {
                for (int i = 0; i < talk.option.Count; i++)
                {
                    int optionId = talk.option[i];
                    if (optionId <= 0)
                    {
                        issues.Add("对话 " + talk.id + " 的选项第 " + (i + 1)
                                 + " 项为 " + optionId
                                 + "；游戏会尝试显示选项界面却找不到该选项。");
                    }
                    else if (!knownOptionIds.Contains(optionId))
                    {
                        issues.Add("对话 " + talk.id + " 引用不存在的选项 "
                                 + optionId + "；该选择可能留下空白且无法继续。");
                    }
                }
            }

            if (paramJump)
            {
                ValidateParamJumpTargets(
                    "对话 " + talk.id, talk.miniGame, knownTalkIds, issues);
                return;
            }

            ValidateTalkTargetSlots(
                "对话 " + talk.id + " 的 nextTalk",
                talk.nextTalk, knownTalkIds, 1, true, issues);
            if (hasMiniGame)
            {
                ValidateTalkTargetSlots(
                    "对话 " + talk.id + " 的 nextTalk2",
                    talk.nextTalk2, knownTalkIds, 1, true, issues);
                int gameId;
                string ignored;
                MiniGameUtil.TryGetGameId(talk.miniGame, out gameId, out ignored);
                if (!HasRuntimeTarget(talk.nextTalk, 1))
                    issues.Add("对话 " + talk.id + " 的小游戏 "
                             + MiniGameUtil.GameName(gameId)
                             + " 没有成功出口，结束后可能停住。");
                if (!HasRuntimeTarget(talk.nextTalk2, 1))
                    issues.Add("对话 " + talk.id + " 的小游戏 "
                             + MiniGameUtil.GameName(gameId)
                             + " 没有失败出口，失败后可能停住。");
                return;
            }

            // StateEvtView 推进只读 nextTalk（OnClickSkip→NextTalk），
            // talk.check/nextTalk2 从不求值，条件失败分支规则不适用。
            if (stateEventView) return;
            if (talk.check == null || talk.check.Count == 0) return;
            bool next2FallsBack = talk.nextTalk2 == null
                                  || talk.nextTalk2.Count == 0
                                  || talk.nextTalk2[0] == 0;
            for (int gender = 0; gender < 2; gender++)
            {
                int primary = RuntimeGenderValue(talk.nextTalk, gender);
                if (primary <= 0) continue;
                int secondary = next2FallsBack
                    ? primary
                    : RuntimeGenderValue(talk.nextTalk2, gender);
                if (secondary <= 0)
                {
                    issues.Add("对话 " + talk.id + " 的"
                             + GenderName(gender)
                             + "条件失败分支会跳到 Talk 0；游戏不会把它当作正常结束。"
                             + "请填写 nextTalk2，或将其首项设为 0 以整组回退到 nextTalk。");
                }
                else if (!knownTalkIds.Contains(secondary))
                {
                    issues.Add("对话 " + talk.id + " 的"
                             + GenderName(gender)
                             + "条件失败分支指向不存在的对话 " + secondary + "。");
                }
            }
        }

        private static void ValidateOptionRuntime(
            int optionId,
            OptionCfg option,
            ISet<int> knownTalkIds,
            ISet<int> knownEventIds,
            bool stateEventView,
            ICollection<string> issues)
        {
            string miniGameError;
            if (!TryValidateMiniGameConfiguration(
                    option.miniGame, false, out miniGameError))
                issues.Add("选项 " + optionId + " 的小游戏字段无效："
                         + miniGameError);
            bool hasMiniGame = option.miniGame != null
                               && option.miniGame.Count > 0;
            bool paramJump = hasMiniGame
                             && MiniGameUtil.IsParamJump(option.miniGame);
            // StateEvtView 的选项跳转阈值是 GetNextTalk()>0：talkId=1 会真的
            // 跳转，目标缺失时 Cfg.TalkCfgMap[1] 直接 KeyNotFound 崩溃。
            int minimumTalkTarget = stateEventView ? 1 : 2;
            if (paramJump)
            {
                ValidateParamJumpTargets(
                    "选项 " + optionId, option.miniGame, knownTalkIds, issues);
            }
            else
            {
                ValidateTalkTargetSlots(
                    "选项 " + optionId + " 的 talkId",
                    option.talkId, knownTalkIds, minimumTalkTarget, false, issues);
                ValidateTalkTargetSlots(
                    "选项 " + optionId + " 的 talkId2",
                    option.talkId2, knownTalkIds, minimumTalkTarget, false, issues);
                if (hasMiniGame)
                {
                    int gameId;
                    string ignored;
                    MiniGameUtil.TryGetGameId(option.miniGame, out gameId, out ignored);
                    // 47=漫展派对（ExpoPartyView）：选项入口的 CloseView 只关闭
                    // NewTalkView，talkId/talkId2/success/fail 全部不读，流程由
                    // 视图自行收尾（原版 31502101 双出口全空即活例）。
                    if (gameId != 47 && !HasRuntimeTarget(option.talkId, 2))
                        issues.Add("选项 " + optionId + " 的小游戏 "
                                 + MiniGameUtil.GameName(gameId)
                                 + " 没有成功出口，结束后可能停住。");
                    // 29=大头贴只回调成功出口；36=谈判组队（NegotiationTeamView）
                    // CloseView 只读 talkId 成功出口（原版 31106805 无失败出口）。
                    if (gameId != 29 && gameId != 36 && gameId != 47
                        && !HasRuntimeTarget(option.talkId2, 2))
                        issues.Add("选项 " + optionId + " 的小游戏 "
                                 + MiniGameUtil.GameName(gameId)
                                 + " 没有失败出口，失败后可能停住。");
                }
            }

            // StateEvtView 的选项点击流程从不读取 nextEvtId（落空即 CloseView），
            // 悬空引用无害，不做拦截。
            if (!stateEventView && option.nextEvtId > 0
                && !knownEventIds.Contains(option.nextEvtId))
                issues.Add("选项 " + optionId + " 的 nextEvtId 指向不存在的事件 "
                         + option.nextEvtId + "。");
        }

        /// <summary>
        /// StateEvtView 专属：无可拦截选项（或空正文自动透传）的对话只靠
        /// nextTalk 推进；ShowTalk 对空列表直接索引 [0] 越界崩溃，对 0 直接
        /// return（没有"对话 0=结束"语义），玩家会停在原地。
        /// </summary>
        private static void ValidateStateEventTalkFlow(
            TalkCfg talk, ICollection<string> issues)
        {
            // StateEvtView 的透传判定是 content.IsEmpty()==string.IsNullOrEmpty
            //（BasicTypeExtension.IsEmpty），纯空白正文会正常显示并停在选项处，
            // 不能按 IsNullOrWhiteSpace 误判为透传。
            bool hasVisibleOptions = !string.IsNullOrEmpty(talk.content)
                                     && talk.option != null
                                     && talk.option.Any(id => id != 0);
            if (hasVisibleOptions) return;
            if (talk.nextTalk == null || talk.nextTalk.Count == 0)
            {
                issues.Add("对话 " + talk.id
                         + " 在状态演出事件中没有可显示的选项，且 nextTalk 为空；"
                         + "玩家点跳过（或空正文自动透传）时会数组越界崩溃。");
                return;
            }
            if (RuntimeGenderValue(talk.nextTalk, 0) == 0
                && RuntimeGenderValue(talk.nextTalk, 1) == 0)
            {
                issues.Add("对话 " + talk.id + " 的 nextTalk 槽位都是 0；"
                         + "状态演出事件没有“对话 0=结束”语义，会软锁死；"
                         + "请让链条最终落到一条带选项的对话（选项落空即优雅关闭）。");
            }
        }

        /// <summary>
        /// reportNegative 仅对对话槽位为 true：ShowTalk 对非 0 值直接索引
        /// TalkCfgMap，负数即崩。选项槽位有 GetNextTalk()>0（StateEvtView）
        /// 或 >1（NewTalkView）闸门，负数到不了索引处，只会优雅落空。
        /// </summary>
        private static void ValidateTalkTargetSlots(
            string owner,
            IList<int> values,
            ISet<int> knownTalkIds,
            int minimumRuntimeId,
            bool reportNegative,
            ICollection<string> issues)
        {
            if (values == null) return;
            int count = Math.Min(values.Count, 2);
            for (int i = 0; i < count; i++)
            {
                int targetId = values[i];
                if (targetId >= minimumRuntimeId
                    && !knownTalkIds.Contains(targetId))
                {
                    issues.Add(owner + " 的 " + GenderSlotLabel(values.Count, i)
                             + "指向不存在的对话 " + targetId + "。");
                }
                else if (reportNegative && targetId < 0)
                {
                    issues.Add(owner + " 的 " + GenderSlotLabel(values.Count, i)
                             + "不能是负数 " + targetId + "。");
                }
            }
        }

        private static void ValidateParamJumpTargets(
            string owner,
            IList<double> miniGame,
            ISet<int> knownTalkIds,
            ICollection<string> issues)
        {
            List<int> targets;
            string error;
            if (!MiniGameUtil.TryGetParamJumpTargets(
                    miniGame, out targets, out error))
                return; // 具体形状错误已由 MiniGameUtil.Validate 报出。
            foreach (int targetId in targets)
                if (!knownTalkIds.Contains(targetId))
                    issues.Add(owner + " 的参数跳转结果指向不存在的对话 "
                             + targetId + "。");
        }

        private void ValidateAutomaticTalkCycles(ICollection<string> issues)
        {
            var byId = new Dictionary<int, TalkCfg>();
            foreach (TalkCfg talk in Talks)
                if (talk != null && talk.id > 0) byId[talk.id] = talk;

            var automatic = new HashSet<int>();
            foreach (KeyValuePair<int, TalkCfg> pair in byId)
            {
                TalkCfg talk = pair.Value;
                bool hasMiniGame = talk.miniGame != null
                                   && talk.miniGame.Count > 0;
                bool hasStoppingOptions = !string.IsNullOrWhiteSpace(talk.content)
                                          && talk.option != null
                                          && talk.option.Count > 0;
                if (!hasMiniGame && !hasStoppingOptions)
                    automatic.Add(pair.Key);
            }
            if (EntriesKnown)
                automatic.IntersectWith(
                    BuildReachableTalkIdsForValidation(byId));
            if (automatic.Count == 0) return;

            var edges = automatic.ToDictionary(
                id => id, id => new HashSet<int>());
            var indegree = automatic.ToDictionary(id => id, id => 0);
            foreach (int id in automatic)
            {
                TalkCfg talk = byId[id];
                for (int gender = 0; gender < 2; gender++)
                {
                    int primary = RuntimeGenderValue(talk.nextTalk, gender);
                    AddAutomaticEdge(id, primary, automatic, edges, indegree);
                    // StateEvtView 从不求值 check/nextTalk2，失败分支不构成
                    // 循环边；OnClickSkip 沿 nextTalk 的循环仍照常检测。
                    if (primary <= 0 || IsStateEventView || talk.check == null
                        || talk.check.Count == 0) continue;
                    bool fallsBack = talk.nextTalk2 == null
                                     || talk.nextTalk2.Count == 0
                                     || talk.nextTalk2[0] == 0;
                    int secondary = fallsBack
                        ? primary
                        : RuntimeGenderValue(talk.nextTalk2, gender);
                    AddAutomaticEdge(id, secondary, automatic, edges, indegree);
                }
            }

            var queue = new Queue<int>(
                indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
            int visited = 0;
            while (queue.Count > 0)
            {
                int id = queue.Dequeue();
                visited++;
                foreach (int target in edges[id])
                {
                    indegree[target]--;
                    if (indegree[target] == 0) queue.Enqueue(target);
                }
            }
            if (visited == automatic.Count) return;
            var residual = new HashSet<int>(indegree
                .Where(pair => pair.Value > 0)
                .Select(pair => pair.Key));
            // Kahn 残差含“仅被循环指向的下游非环节点”，且同一事件可能有多个
            // 互不连通的循环——门控必须按真环（强连通分量）逐个独立判定，
            // 否则下游节点或另一循环上的随机行会错误豁免确定性死循环。
            foreach (HashSet<int> component in
                     ResidualCycleComponents(residual, edges))
            {
                // 原版自带确定性自环（16 个自环对话）；作者一字未改的既有/
                // 内置结构不拦截保存，本环里至少有一个本次会话修改或新增的
                // 成员才算作者引入的问题。
                if (!component.Any(id => ShouldValidateTalkRuntime(byId[id])))
                    continue;
                // family 0/333 每次求值独立随机，但必须真的构成分歧出口——
                // 成员未整组回退且至少一个性别的真/假分支有一侧离开本环，
                // 循环才有概率终止；StateEvtView 从不求值 check，不适用。
                if (!IsStateEventView && component.Any(
                        id => HasEscapableRandomBranch(byId[id], component)))
                    continue;
                string sample = string.Join("、", component
                    .OrderBy(id => id)
                    .Select(id => id.ToString())
                    .Take(6).ToArray());
                issues.Add("直连对话形成确定性循环（涉及 " + sample
                         + "），没有小游戏、可见选项或随机条件出口。"
                         + "点击“跳过”会无限循环；若其中有空正文，"
                         + "正常播放会同步递归直至栈溢出。");
            }
        }

        /// <summary>
        /// 残差图中真正构成循环的强连通分量（尺寸 1 的分量仅在自指时算环，
        /// 被循环指向的下游节点被排除）。残差规模小，用逐点 BFS 可达集求解。
        /// </summary>
        private static List<HashSet<int>> ResidualCycleComponents(
            HashSet<int> residual, IDictionary<int, HashSet<int>> edges)
        {
            var reach = new Dictionary<int, HashSet<int>>();
            foreach (int start in residual)
            {
                var seen = new HashSet<int>(); // 路径长度 ≥1 的可达集，不含起点自身
                var queue = new Queue<int>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                    foreach (int next in edges[queue.Dequeue()])
                        if (residual.Contains(next) && seen.Add(next))
                            queue.Enqueue(next);
                reach[start] = seen;
            }
            var assigned = new HashSet<int>();
            var components = new List<HashSet<int>>();
            foreach (int id in residual)
            {
                if (assigned.Contains(id)) continue;
                var component = new HashSet<int> { id };
                foreach (int other in reach[id])
                    if (other != id && reach[other].Contains(id))
                        component.Add(other);
                assigned.UnionWith(component);
                if (component.Count > 1 || reach[id].Contains(id))
                    components.Add(component);
            }
            return components;
        }

        /// <summary>
        /// 成员的随机条件是否真能把执行带离所在循环：check 含 family 0/333、
        /// nextTalk2 未整组回退（GetNextTalk2 语义：null/空/首项 0 ⇒ 与
        /// nextTalk 同边），且至少一个性别的真/假分支目标不同并有一侧不在
        /// 本分量内（含 ≤0 的结束值与带选项/小游戏的非自动对话）。
        /// </summary>
        private static bool HasEscapableRandomBranch(
            TalkCfg talk, ICollection<int> component)
        {
            if (talk == null || !HasRandomConditionRow(talk.check)) return false;
            if (talk.nextTalk2 == null || talk.nextTalk2.Count == 0
                || talk.nextTalk2[0] == 0)
                return false;
            for (int gender = 0; gender < 2; gender++)
            {
                int primary = RuntimeGenderValue(talk.nextTalk, gender);
                int secondary = RuntimeGenderValue(talk.nextTalk2, gender);
                if (primary == secondary) continue;
                if (!component.Contains(primary) || !component.Contains(secondary))
                    return true;
            }
            return false;
        }

        /// <summary>family 0=ConditionerRandom、333=ConditionerRandom2，每次求值独立随机。</summary>
        private static bool HasRandomConditionRow(List<List<double>> check)
        {
            if (check == null) return false;
            foreach (List<double> row in check)
            {
                if (row == null || row.Count == 0) continue;
                if (row[0] == 0d || row[0] == 333d) return true;
            }
            return false;
        }

        private HashSet<int> BuildReachableTalkIdsForValidation(
            IDictionary<int, TalkCfg> talks)
        {
            HashSet<int> reachable;
            HashSet<int> ignoredOptions;
            BuildRuntimeReachability(
                talks, out reachable, out ignoredOptions);
            return reachable;
        }

        private void BuildRuntimeReachability(
            IDictionary<int, TalkCfg> talks,
            out HashSet<int> reachable,
            out HashSet<int> seenOptions)
        {
            reachable = new HashSet<int>();
            seenOptions = new HashSet<int>();
            var talkQueue = new Queue<int>();
            var optionQueue = new Queue<int>();
            if (Entries != null)
                foreach (int id in Entries)
                    if (id > 0) talkQueue.Enqueue(id);

            EvtCfg evt;
            string ignored;
            if (StoryGraphEditPersistence.TryReadEffectiveEvent(
                    ModRoot, EventId, out evt, out ignored)
                && evt != null)
            {
                if (evt.options != null)
                    foreach (int id in evt.options)
                        if (id > 0) optionQueue.Enqueue(id);
                EnqueueParamTargets(talkQueue, evt.miniGame);
            }

            while (talkQueue.Count > 0 || optionQueue.Count > 0)
            {
                if (talkQueue.Count > 0)
                {
                    int id = talkQueue.Dequeue();
                    if (!reachable.Add(id)) continue;
                    TalkCfg talk;
                    if (!talks.TryGetValue(id, out talk) || talk == null) continue;
                    EnqueueRuntimeTalkSlots(talkQueue, talk.nextTalk);
                    EnqueueRuntimeTalkSlots(talkQueue, talk.nextTalk2);
                    EnqueueParamTargets(talkQueue, talk.miniGame);
                    if (talk.option != null)
                        foreach (int optionId in talk.option)
                            if (optionId > 0) optionQueue.Enqueue(optionId);
                }
                else
                {
                    int id = optionQueue.Dequeue();
                    if (!seenOptions.Add(id)) continue;
                    OptionCfg option;
                    if (!Options.TryGetValue(id, out option) || option == null)
                        continue;
                    EnqueueRuntimeTalkSlots(talkQueue, option.talkId);
                    EnqueueRuntimeTalkSlots(talkQueue, option.talkId2);
                    EnqueueParamTargets(talkQueue, option.miniGame);
                }
            }
        }

        private static void EnqueueRuntimeTalkSlots(
            Queue<int> queue, IList<int> values)
        {
            if (queue == null || values == null) return;
            int count = Math.Min(values.Count, 2);
            for (int i = 0; i < count; i++)
                if (values[i] > 0) queue.Enqueue(values[i]);
        }

        private static void EnqueueParamTargets(
            Queue<int> queue, IList<double> miniGame)
        {
            if (queue == null || !MiniGameUtil.IsParamJump(miniGame)) return;
            List<int> targets;
            string ignored;
            if (!MiniGameUtil.TryGetParamJumpTargets(
                    miniGame, out targets, out ignored)) return;
            foreach (int id in targets)
                if (id > 0) queue.Enqueue(id);
        }

        private static void AddAutomaticEdge(
            int source,
            int target,
            ISet<int> automatic,
            IDictionary<int, HashSet<int>> edges,
            IDictionary<int, int> indegree)
        {
            if (!automatic.Contains(target) || !edges[source].Add(target)) return;
            indegree[target]++;
        }

        private static bool HasRuntimeTarget(IList<int> values, int minimumId)
        {
            if (values == null) return false;
            int count = Math.Min(values.Count, 2);
            for (int i = 0; i < count; i++)
                if (values[i] >= minimumId) return true;
            return false;
        }

        private static int RuntimeGenderValue(IList<int> values, int gender)
        {
            if (values == null || values.Count == 0) return 0;
            if (values.Count == 1) return values[0];
            return values[Math.Max(0, Math.Min(1, gender))];
        }

        private static string GenderSlotLabel(int count, int index)
        {
            if (count <= 1) return "共用槽位";
            if (index == 0) return "男性槽位";
            if (index == 1) return "女性槽位";
            return "第 " + (index + 1) + " 槽位";
        }

        private static string PortSlotLabel(
            StoryGraphEditPortKind port, int count, int index)
        {
            return port == StoryGraphEditPortKind.TalkOption
                ? "选项第 " + (index + 1) + " 项"
                : GenderSlotLabel(count, index);
        }

        private static string GenderName(int gender)
        {
            return gender == 0 ? "男性" : "女性";
        }

        internal void MarkSaved()
        {
            _liveFieldEditKey = null;
            _undo.Clear();
            _redo.Clear();
            Dirty = false;
            _initialTalkIds.Clear();
            _initialTalkJson.Clear();
            // _persisted* 表示整份 Mod JSON 的占用集合，不只是当前会话。保存后
            // 只能并入当前 ID，不能 Clear，否则第二轮新增会重新撞上草稿外记录。
            foreach (TalkCfg talk in Talks)
            {
                if (talk == null) continue;
                _initialTalkIds.Add(talk.id);
                _persistedTalkIds.Add(talk.id);
                _initialTalkJson[talk.id] =
                    JsonConvert.SerializeObject(talk, Formatting.None);
            }
            _initialOptionIds.Clear();
            _initialOptionJson.Clear();
            foreach (KeyValuePair<int, OptionCfg> pair in Options)
            {
                _initialOptionIds.Add(pair.Key);
                // 冻结克隆刚被 ApplyOptionChanges 有意跳过、从未写入 Mod
                // OptionCfg.json，不能并入“整份 Mod JSON 占用集合”；
                // _initialOptionIds/_initialOptionJson 保留以维持 TrySave
                // 的 initialIds 跳过逻辑。
                if (!_frozenBuiltInOptionIds.Contains(pair.Key))
                    _persistedOptionIds.Add(pair.Key);
                if (pair.Value != null)
                    _initialOptionJson[pair.Key] =
                        JsonConvert.SerializeObject(pair.Value, Formatting.None);
            }
            LastAction = "已安全保存剧情图修改";
        }

        internal TalkCfg FindTalk(int id)
        {
            return FindTalk(id, 0);
        }

        internal TalkCfg FindTalk(int id, int ordinal)
        {
            if (Talks == null) return null;
            int match = 0;
            foreach (TalkCfg talk in Talks)
            {
                if (talk == null || talk.id != id) continue;
                if (match == Math.Max(0, ordinal)) return talk;
                match++;
            }
            return null;
        }

        internal int FindTalkOrdinal(TalkCfg target)
        {
            if (target == null || Talks == null) return 0;
            int ordinal = 0;
            foreach (TalkCfg talk in Talks)
            {
                if (talk == null || talk.id != target.id) continue;
                if (ReferenceEquals(talk, target)) return ordinal;
                ordinal++;
            }
            return 0;
        }

        internal OptionCfg FindOption(int id)
        {
            OptionCfg value;
            return Options != null && Options.TryGetValue(id, out value) ? value : null;
        }

        internal TalkCfg FindParentTalk(int optionId)
        {
            return Talks != null
                ? Talks.FirstOrDefault(t => t != null && t.option != null
                    && t.option.Contains(optionId))
                : null;
        }

        private bool BeginFieldMutation(string description, string liveEditKey)
        {
            if (!string.IsNullOrEmpty(liveEditKey)
                && string.Equals(_liveFieldEditKey, liveEditKey,
                    StringComparison.Ordinal))
            {
                // 同一次输入继续改当前草稿；撤销栈底仍保留首次输入前的完整快照。
                Dirty = true;
                LastAction = description;
                return false;
            }

            BeginMutation(description);
            if (!string.IsNullOrEmpty(liveEditKey))
                _liveFieldEditKey = liveEditKey;
            return true;
        }

        private void BeginMutation(string description)
        {
            _liveFieldEditKey = null;
            _undo.Push(new HistoryEntry
            {
                State = CaptureState(),
                Description = description,
            });
            _redo.Clear();
            Dirty = true;
            LastAction = description;
        }

        private State CaptureState()
        {
            return new State
            {
                Talks = CloneTalks(Talks),
                Options = CloneOptions(Options),
            };
        }

        private void RestoreState(State state)
        {
            Talks = state != null ? CloneTalks(state.Talks) : new List<TalkCfg>();
            Options = state != null
                ? CloneOptions(state.Options)
                : new Dictionary<int, OptionCfg>();
        }

        /// <summary>
        /// 新事件可能先把 EvtCfg.talkId 写成约定的 xxx001，TalkCfg 却尚未创建。
        /// 该入口会出现在全局引用集合里，普通分配器因而会错误跳到 xxx002。
        /// 新建 Talk 时先安全认领当前事件明确但缺失的本地入口；若同一编号还被
        /// 其它事件/节点引用，则停止新增而不是继续制造一个不可达的 xxx002。
        /// </summary>
        private bool TryAllocateMissingEntryTalkId(out int id, out string error)
        {
            id = -1;
            error = null;
            if (!EntriesKnown || Entries == null || Entries.Count == 0) return false;

            int group = EventId > 0 ? EventId : 1;
            var seen = new HashSet<int>();
            foreach (int candidate in Entries)
            {
                if (candidate <= 0 || candidate / 1000 != group
                    || !seen.Add(candidate)) continue;
                if (Talks.Any(talk => talk != null && talk.id == candidate)) continue;

                if (_persistedTalkIds.Contains(candidate)
                    || _builtInTalkIds.Contains(candidate))
                {
                    error = "事件入口对话 " + candidate
                            + " 尚未加载，但该编号已有持久化或游戏内置配置；"
                            + "为避免覆盖，已取消新增。请先回到事件表单检查入口。";
                    return false;
                }

                string references;
                string referenceError;
                if (!StoryGraphEditPersistence.TryFindExternalReferences(
                        this, StoryGraphEditNodeKind.Talk, candidate,
                        out references, out referenceError, true))
                {
                    error = referenceError;
                    return false;
                }
                if (!string.IsNullOrEmpty(references))
                {
                    error = "事件入口对话 " + candidate
                            + " 尚不存在，但该编号还被当前图之外引用："
                            + references
                            + "。为避免把入口静默跳到下一号，本次新增已取消。";
                    return false;
                }

                id = candidate;
                return true;
            }
            return false;
        }

        private bool TryAllocateTalkId(TalkCfg near, out int id)
        {
            int group = near != null && near.id > 0 ? near.id / 1000 : EventId;
            if (group <= 0) group = EventId > 0 ? EventId : 1;
            return TryAllocateGroupedId(
                group, 1000, 999, BuildUsedTalkIds(), out id);
        }

        private bool TryAllocateOptionId(TalkCfg parent, out int id)
        {
            int group = parent != null && parent.id > 0 ? parent.id / 1000 : EventId;
            if (group <= 0) group = EventId > 0 ? EventId : 1;
            return TryAllocateGroupedId(
                group, 100, 99, BuildUsedOptionIds(), out id);
        }

        private HashSet<int> BuildUsedTalkIds()
        {
            var used = new HashSet<int>(
                Talks.Where(talk => talk != null).Select(talk => talk.id));
            used.UnionWith(_persistedTalkIds);
            used.UnionWith(_builtInTalkIds);
            used.UnionWith(_globallyReferencedTalkIds);
            return used;
        }

        private HashSet<int> BuildUsedOptionIds()
        {
            var used = new HashSet<int>(Options.Keys);
            used.UnionWith(_persistedOptionIds);
            used.UnionWith(_builtInOptionIds);
            used.UnionWith(_globallyReferencedOptionIds);
            return used;
        }

        private static bool TryAllocateGroupedId(
            int group, int multiplier, int maxSuffix,
            ISet<int> used, out int id)
        {
            for (int suffix = 1; suffix <= maxSuffix; suffix++)
            {
                long candidate = (long)group * multiplier + suffix;
                if (candidate > int.MaxValue) break;
                if (!used.Contains((int)candidate))
                {
                    id = (int)candidate;
                    return true;
                }
            }
            id = -1;
            return false;
        }

        private static List<int> RemapSlots(
            List<int> source, IDictionary<int, int> map)
        {
            if (source == null) return null;
            var result = new List<int>(source.Count);
            foreach (int value in source)
            {
                int mapped;
                result.Add(value != 0 && map.TryGetValue(value, out mapped)
                    ? mapped
                    : 0);
            }
            return result;
        }

        private static List<double> RemapMiniGameTargets(
            List<double> source, IDictionary<int, int> map)
        {
            if (source == null) return null;
            if (!MiniGameUtil.IsParamJump(source))
                return new List<double>(source);
            List<int> targets;
            string error;
            if (!MiniGameUtil.TryGetParamJumpTargets(
                    source, out targets, out error))
                return new List<double>();
            var result = new List<double>(source);
            int firstTarget = (int)source[0] == 16 ? 1 : 2;
            for (int i = 0; i < targets.Count; i++)
            {
                int mapped;
                if (!map.TryGetValue(targets[i], out mapped))
                    return new List<double>();
                result[firstTarget + i] = mapped;
            }
            return result;
        }

        internal static int ClearParamJumpMiniGameReferencing(
            TalkCfg talk, int targetId)
        {
            if (talk == null || !MiniGameReferencesTalk(talk.miniGame, targetId))
                return 0;
            talk.miniGame = new List<double>();
            return 1;
        }

        internal static int ClearParamJumpMiniGameReferencing(
            OptionCfg option, int targetId)
        {
            if (option == null || !MiniGameReferencesTalk(option.miniGame, targetId))
                return 0;
            option.miniGame = new List<double>();
            return 1;
        }

        internal static bool MiniGameReferencesTalk(
            IList<double> miniGame, int targetId)
        {
            if (targetId <= 0 || !MiniGameUtil.IsParamJump(miniGame)) return false;
            List<int> targets;
            string error;
            return MiniGameUtil.TryGetParamJumpTargets(
                       miniGame, out targets, out error)
                   && targets.Contains(targetId);
        }

        internal static int ClearTalkReferencesTo(
            TalkCfg talk, int targetId)
        {
            if (talk == null || targetId <= 0) return 0;
            int count = ClearGenderTargetSlots(talk.nextTalk, targetId);
            count += ClearTalkFalseTargetSlots(talk, targetId);
            count += ClearParamJumpMiniGameReferencing(talk, targetId);
            return count;
        }

        internal static int ClearOptionReferencesTo(
            OptionCfg option, int targetId)
        {
            if (option == null || targetId <= 0) return 0;
            int count = ClearGenderTargetSlots(option.talkId, targetId);
            count += ClearGenderTargetSlots(option.talkId2, targetId);
            count += ClearParamJumpMiniGameReferencing(option, targetId);
            return count;
        }

        /// <summary>
        /// 性别槽位不能 RemoveAll：删掉男性槽会让原女性值左移并错误地变成两性共用。
        /// 前两槽改为 0 保留索引；游戏不会读取的额外槽才从尾部移除。
        /// </summary>
        private static int ClearGenderTargetSlots(
            List<int> values, int targetId)
        {
            if (values == null || values.Count == 0) return 0;
            int count = values.Count(value => value == targetId);
            if (count == 0) return 0;
            if (values.Count == 1)
            {
                values.Clear();
                return count;
            }
            for (int i = Math.Min(1, values.Count - 1); i >= 0; i--)
                if (values[i] == targetId) values[i] = 0;
            for (int i = values.Count - 1; i >= 2; i--)
                if (values[i] == targetId) values.RemoveAt(i);
            return count;
        }

        /// <summary>
        /// TalkCfg.nextTalk2 的首项 0 不是“男性结束”，而是整组回退开关。
        /// 删除首槽目标时优先写入对应 nextTalk 作为安全回退；若男性本来就会先结束，
        /// 用仍有效的女性槽保持首项非零，从而不悄悄吞掉女性分支。
        /// </summary>
        private static int ClearTalkFalseTargetSlots(
            TalkCfg talk, int targetId)
        {
            List<int> values = talk != null ? talk.nextTalk2 : null;
            if (values == null || values.Count == 0) return 0;
            int count = values.Count(value => value == targetId);
            if (count == 0) return 0;
            if (values.Count == 1)
            {
                values.Clear(); // 空列表会按原生规则安全回退到 nextTalk。
                return count;
            }

            for (int i = values.Count - 1; i >= 2; i--)
                if (values[i] == targetId) values.RemoveAt(i);

            if (values[0] == targetId)
            {
                RepairFalseBranchFirstSlot(values, talk.nextTalk, targetId);
                if (values.Count == 0) return count;
            }
            if (values.Count > 1 && values[1] == targetId)
            {
                int fallback = RuntimeGenderValue(talk.nextTalk, 1);
                values[1] = fallback > 0 && fallback != targetId ? fallback : 0;
            }
            return count;
        }

        /// <summary>
        /// nextTalk2 首槽重写为可用回退值：优先男性 nextTalk 主值，其次
        /// 女性槽自身，再次女性 nextTalk 主值；全部落空则整组清空——
        /// 等价原生回退，且不假装保留了失败分支。删除路径传 excludedId
        /// 排除正被删除的目标，粘贴等其余路径传 0。
        /// </summary>
        private static void RepairFalseBranchFirstSlot(
            List<int> nextTalk2, List<int> nextTalk, int excludedId)
        {
            int fallback = RuntimeGenderValue(nextTalk, 0);
            if (fallback == excludedId) fallback = 0;
            if (fallback <= 0 && nextTalk2.Count > 1
                && nextTalk2[1] > 0 && nextTalk2[1] != excludedId)
                fallback = nextTalk2[1];
            if (fallback <= 0)
            {
                int femalePrimary = RuntimeGenderValue(nextTalk, 1);
                if (femalePrimary > 0 && femalePrimary != excludedId)
                    fallback = femalePrimary;
            }
            if (fallback > 0) nextTalk2[0] = fallback;
            else nextTalk2.Clear();
        }

        private static List<int> RemapCollection(
            List<int> source, IDictionary<int, int> map)
        {
            if (source == null) return null;
            var result = new List<int>();
            foreach (int value in source)
            {
                int mapped;
                if (value != 0 && map.TryGetValue(value, out mapped))
                    result.Add(mapped);
            }
            return result;
        }

        internal int FindOptionKey(OptionCfg option)
        {
            foreach (KeyValuePair<int, OptionCfg> pair in Options)
                if (ReferenceEquals(pair.Value, option)) return pair.Key;
            return int.MinValue;
        }

        private bool ValidateConnectionSource(
            StoryGraphEditPortKind port,
            TalkCfg sourceTalk,
            OptionCfg sourceOption,
            out string error)
        {
            bool talkPort = port == StoryGraphEditPortKind.TalkNext
                            || port == StoryGraphEditPortKind.TalkNext2
                            || port == StoryGraphEditPortKind.TalkOption;
            if (talkPort)
            {
                if (sourceTalk == null || !Talks.Contains(sourceTalk))
                {
                    error = "连线起点不是当前草稿中的真实对话。";
                    return false;
                }
            }
            else if (sourceOption == null || !Options.Values.Contains(sourceOption))
            {
                error = "连线起点不是当前草稿中的真实选项。";
                return false;
            }
            error = null;
            return true;
        }

        // 连线命令写的是端口源对象自身的字段：源是选项（talkId/talkId2）时等同
        // 于编辑该选项，冻结的共享选项必须拒绝；对话侧挂上/断开选项引用
        // （TalkOption 端口）改的是对话字段，不受此限制。
        private bool EnsureConnectionSourceEditable(
            StoryGraphEditPortKind port, OptionCfg sourceOption, out string error)
        {
            if ((port == StoryGraphEditPortKind.OptionTalk
                 || port == StoryGraphEditPortKind.OptionTalk2)
                && sourceOption != null)
            {
                int key = FindOptionKey(sourceOption);
                if (key != int.MinValue && !EnsureOptionEditable(key, out error))
                    return false;
            }
            error = null;
            return true;
        }

        private static List<int> GetPortValues(
            StoryGraphEditPortKind port, TalkCfg talk, OptionCfg option)
        {
            switch (port)
            {
                case StoryGraphEditPortKind.TalkNext: return talk.nextTalk;
                case StoryGraphEditPortKind.TalkNext2: return talk.nextTalk2;
                case StoryGraphEditPortKind.TalkOption: return talk.option;
                case StoryGraphEditPortKind.OptionTalk: return option.talkId;
                case StoryGraphEditPortKind.OptionTalk2: return option.talkId2;
                default: return null;
            }
        }

        private static void SetPortValues(
            StoryGraphEditPortKind port,
            TalkCfg talk,
            OptionCfg option,
            List<int> values)
        {
            switch (port)
            {
                case StoryGraphEditPortKind.TalkNext: talk.nextTalk = values; break;
                case StoryGraphEditPortKind.TalkNext2: talk.nextTalk2 = values; break;
                case StoryGraphEditPortKind.TalkOption: talk.option = values; break;
                case StoryGraphEditPortKind.OptionTalk: option.talkId = values; break;
                case StoryGraphEditPortKind.OptionTalk2: option.talkId2 = values; break;
            }
        }

        internal static string PortLabel(StoryGraphEditPortKind port)
        {
            switch (port)
            {
                case StoryGraphEditPortKind.TalkNext: return "下一句";
                case StoryGraphEditPortKind.TalkNext2:
                    return "失败分支（条件不成立或小游戏失败）";
                case StoryGraphEditPortKind.TalkOption: return "选项";
                case StoryGraphEditPortKind.OptionTalk: return "结果";
                case StoryGraphEditPortKind.OptionTalk2:
                    return "失败结果（条件不成立或小游戏失败）";
                default: return "连线";
            }
        }

        private static bool IsSingleValue(List<int> values, int id)
        {
            return values != null && values.Count == 1 && values[0] == id;
        }

        private static bool HasNonZero(List<int> values)
        {
            return values != null && values.Any(value => value != 0);
        }

        private static int RemoveAll(List<int> values, int id)
        {
            if (values == null) return 0;
            int before = values.Count;
            values.RemoveAll(value => value == id);
            return before - values.Count;
        }

        private static bool ContainsReference<T>(IEnumerable<T> values, T target)
            where T : class
        {
            return values != null && values.Any(value => ReferenceEquals(value, target));
        }

        private static TalkCfg CreateTalk(int id)
        {
            return new TalkCfg
            {
                id = id,
                content = string.Empty,
                roleName = null,
                showTxt = string.Empty,
                check = new List<List<double>>(),
                effect = new List<List<float>>(),
                effect2 = new List<List<float>>(),
                highlights = new List<int>(),
                miniGame = new List<double>(),
                nextTalk = new List<int>(),
                nextTalk2 = new List<int>(),
                option = new List<int>(),
                replace = new List<int>(),
                roleIds = new List<int>(),
                roles = new List<List<float>>(),
                screenEffect = new List<float>(),
                vocals = new List<float>(),
            };
        }

        private static OptionCfg CreateOption(int id)
        {
            return new OptionCfg
            {
                id = id,
                content = string.Empty,
                showTxt = string.Empty,
                tag = string.Empty,
                check = new List<List<double>>(),
                effect = new List<List<float>>(),
                effect2 = new List<List<float>>(),
                miniGame = new List<double>(),
                precondition = new List<List<double>>(),
                pressure = new List<List<float>>(),
                stateCond = new List<List<double>>(),
                talkId = new List<int>(),
                talkId2 = new List<int>(),
            };
        }

        internal static List<TalkCfg> CloneTalks(IEnumerable<TalkCfg> source)
        {
            var result = new List<TalkCfg>();
            if (source == null) return result;
            foreach (TalkCfg talk in source) result.Add(talk != null ? CloneTalk(talk) : null);
            return result;
        }

        internal static Dictionary<int, OptionCfg> CloneOptions(
            IEnumerable<KeyValuePair<int, OptionCfg>> source)
        {
            var result = new Dictionary<int, OptionCfg>();
            if (source == null) return result;
            foreach (KeyValuePair<int, OptionCfg> pair in source)
                result[pair.Key] = pair.Value != null ? CloneOption(pair.Value) : null;
            return result;
        }

        internal static TalkCfg CloneTalk(TalkCfg src)
        {
            if (src == null) return null;
            return new TalkCfg
            {
                audio = src.audio,
                bg = src.bg,
                check = CloneNested(src.check),
                content = src.content,
                effect = CloneNested(src.effect),
                effect2 = CloneNested(src.effect2),
                highlights = CloneList(src.highlights),
                id = src.id,
                maxoptions = src.maxoptions,
                miniGame = CloneList(src.miniGame),
                nextTalk = CloneList(src.nextTalk),
                nextTalk2 = CloneList(src.nextTalk2),
                option = CloneList(src.option),
                replace = CloneList(src.replace),
                roleIds = CloneList(src.roleIds),
                roleName = TalkRoleNameUtil.NormalizeOverride(src.roleName),
                roles = CloneNested(src.roles),
                screenEffect = CloneList(src.screenEffect),
                showTxt = src.showTxt,
                time = src.time,
                vocals = CloneList(src.vocals),
            };
        }

        internal static OptionCfg CloneOption(OptionCfg src)
        {
            if (src == null) return null;
            return new OptionCfg
            {
                check = CloneNested(src.check),
                content = src.content,
                effect = CloneNested(src.effect),
                effect2 = CloneNested(src.effect2),
                id = src.id,
                miniGame = CloneList(src.miniGame),
                nextEvtId = src.nextEvtId,
                precondition = CloneNested(src.precondition),
                pressure = CloneNested(src.pressure),
                showTxt = src.showTxt,
                stateCond = CloneNested(src.stateCond),
                tag = src.tag,
                talkId = CloneList(src.talkId),
                talkId2 = CloneList(src.talkId2),
            };
        }

        private static List<T> CloneList<T>(List<T> source)
        {
            return source != null ? new List<T>(source) : null;
        }

        private static List<List<T>> CloneNested<T>(List<List<T>> source)
        {
            if (source == null) return null;
            var result = new List<List<T>>(source.Count);
            foreach (List<T> item in source)
                result.Add(item != null ? new List<T>(item) : null);
            return result;
        }
    }

    /// <summary>
    /// 将草稿合并回 Mod 的 zh-cn 配置文件。与原版保存相比额外跟踪并移除被明确删除的键，
    /// 两个 JSON 都先写临时文件，再替换目标；任一替换失败都会尝试恢复先前文件。
    /// </summary>
    internal static class StoryGraphEditPersistence
    {
        private const int MaxReferencePreview = 12;

        private sealed class FileFingerprint
        {
            internal bool Exists;
            internal long Length;
            internal byte[] Hash;
        }

        private sealed class FilePlan
        {
            internal string Path;
            internal string Temp;
            internal string TransactionBackup;
            internal string SwapBackup;
            internal string UserBackup;
            internal bool Existed;
            internal bool Replaced;
            internal FileFingerprint Expected;
            internal FileFingerprint Replacement;
        }

        private sealed class JournalFile
        {
            public string Path;
            public string Temp;
            public string TransactionBackup;
            public string SwapBackup;
            public bool Existed;
            public long OriginalLength;
            public string OriginalHash;
            public long ReplacementLength;
            public string ReplacementHash;
        }

        private sealed class TransactionJournal
        {
            public bool Committed;
            public JournalFile Talk;
            public JournalFile Option;
        }

        /// <summary>
        /// 恢复前对单个配置文件的判定。后两种是“确定性不可恢复”——再试多少次
        /// 结果都一样，只能隔离日志放行；瞬时 IO/解析失败不产生判定值，
        /// 而是抛异常走外层 catch 继续阻塞，留待下次自愈或人工处理。
        /// </summary>
        private enum JournalFileState
        {
            AtOriginal,
            Restorable,
            ReplacementWithoutBackup,
            ExternalChange,
        }

        internal static bool TryReadPersistedIds(
            string modRoot, HashSet<int> talkIds, HashSet<int> optionIds)
        {
            if (talkIds == null || optionIds == null || string.IsNullOrWhiteSpace(modRoot))
                return false;
            try
            {
                foreach (KeyValuePair<string, TalkCfg> pair in
                         LoadMap<TalkCfg>(ConfigPath<TalkCfg>(modRoot)))
                {
                    int keyId;
                    if (int.TryParse(pair.Key, out keyId)) talkIds.Add(keyId);
                    if (pair.Value != null) talkIds.Add(pair.Value.id);
                }
                foreach (KeyValuePair<string, OptionCfg> pair in
                         LoadMap<OptionCfg>(ConfigPath<OptionCfg>(modRoot)))
                {
                    int keyId;
                    if (int.TryParse(pair.Key, out keyId)) optionIds.Add(keyId);
                    if (pair.Value != null) optionIds.Add(pair.Value.id);
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[StoryGraph.Edit] 读取持久化编号失败：" + e.Message);
                return false;
            }
        }

        internal static bool TryReadPersistedEventIds(
            string modRoot, HashSet<int> eventIds)
        {
            if (eventIds == null || string.IsNullOrWhiteSpace(modRoot))
                return false;
            try
            {
                foreach (KeyValuePair<string, EvtCfg> pair in
                         LoadMap<EvtCfg>(ConfigPath<EvtCfg>(modRoot)))
                {
                    int keyId;
                    if (int.TryParse(pair.Key, out keyId) && keyId > 0)
                        eventIds.Add(keyId);
                    if (pair.Value != null && pair.Value.id > 0)
                        eventIds.Add(pair.Value.id);
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Edit] 读取持久化事件编号失败：" + e.Message);
                return false;
            }
        }

        internal static bool TryReadEffectiveEvent(
            string modRoot, int eventId, out EvtCfg evt, out string error)
        {
            evt = null;
            error = null;
            if (eventId <= 0)
            {
                error = "当前事件编号无效。";
                return false;
            }
            try
            {
                EvtCfg builtIn;
                if (Cfg.EvtCfgMap != null
                    && Cfg.EvtCfgMap.TryGetValue(eventId, out builtIn))
                    evt = builtIn;
                if (!string.IsNullOrWhiteSpace(modRoot))
                {
                    Dictionary<string, EvtCfg> persisted =
                        LoadMap<EvtCfg>(ConfigPath<EvtCfg>(modRoot));
                    foreach (KeyValuePair<string, EvtCfg> pair in persisted)
                    {
                        int keyId;
                        bool keyMatches = int.TryParse(pair.Key, out keyId)
                                          && keyId == eventId;
                        if (keyMatches
                            || (pair.Value != null && pair.Value.id == eventId))
                            evt = pair.Value;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                error = "读取当前事件完整配置失败："
                      + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        internal static bool TryMergeReferencedOptions(
            string modRoot,
            IEnumerable<int> optionIds,
            IDictionary<int, OptionCfg> target,
            out string error)
        {
            error = null;
            if (target == null)
            {
                error = "事件级选项目标容器为空。";
                return false;
            }
            try
            {
                Dictionary<int, OptionCfg> effective = EffectiveMap(
                    SafeBuiltInMap(() => Cfg.OptionCfgMap),
                    !string.IsNullOrWhiteSpace(modRoot)
                        ? LoadMap<OptionCfg>(ConfigPath<OptionCfg>(modRoot))
                        : new Dictionary<string, OptionCfg>(),
                    value => value != null ? value.id : 0);
                if (optionIds == null) return true;
                foreach (int id in optionIds)
                {
                    OptionCfg option;
                    if (id > 0 && effective.TryGetValue(id, out option)
                        && option != null)
                        target[id] = option;
                }
                return true;
            }
            catch (Exception e)
            {
                error = "读取事件级 OptionCfg 失败："
                      + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        internal static bool TryReadGloballyReferencedIds(
            string modRoot,
            HashSet<int> talkIds,
            HashSet<int> optionIds,
            out string error)
        {
            error = null;
            if (talkIds == null || optionIds == null
                || string.IsNullOrWhiteSpace(modRoot))
            {
                error = "无法建立全局引用编号集合。";
                return false;
            }
            try
            {
                Dictionary<int, EvtCfg> events = EffectiveMap(
                    SafeBuiltInMap(() => Cfg.EvtCfgMap),
                    LoadMap<EvtCfg>(ConfigPath<EvtCfg>(modRoot)),
                    value => value != null ? value.id : 0);
                Dictionary<int, TalkCfg> talks = EffectiveMap(
                    SafeBuiltInMap(() => Cfg.TalkCfgMap),
                    LoadMap<TalkCfg>(ConfigPath<TalkCfg>(modRoot)),
                    value => value != null ? value.id : 0);
                Dictionary<int, OptionCfg> options = EffectiveMap(
                    SafeBuiltInMap(() => Cfg.OptionCfgMap),
                    LoadMap<OptionCfg>(ConfigPath<OptionCfg>(modRoot)),
                    value => value != null ? value.id : 0);
                foreach (EvtCfg evt in events.Values)
                {
                    if (evt == null) continue;
                    AddIds(talkIds, evt.talkId);
                    AddMiniGameTalkIds(talkIds, evt.miniGame);
                    AddIds(optionIds, evt.options);
                    ConditionRefUtil.CollectTalkIds(evt.condition, talkIds);
                    ConditionRefUtil.CollectOptionIds(evt.condition, optionIds);
                }
                foreach (TalkCfg talk in talks.Values)
                {
                    if (talk == null) continue;
                    AddIds(talkIds, talk.nextTalk);
                    AddIds(talkIds, talk.nextTalk2);
                    AddMiniGameTalkIds(talkIds, talk.miniGame);
                    AddIds(optionIds, talk.option);
                    ConditionRefUtil.CollectTalkIds(talk.check, talkIds);
                    ConditionRefUtil.CollectOptionIds(talk.check, optionIds);
                }
                foreach (OptionCfg option in options.Values)
                {
                    if (option == null) continue;
                    AddIds(talkIds, option.talkId);
                    AddIds(talkIds, option.talkId2);
                    AddMiniGameTalkIds(talkIds, option.miniGame);
                    ConditionRefUtil.CollectTalkIds(option.check, talkIds);
                    ConditionRefUtil.CollectOptionIds(option.check, optionIds);
                    ConditionRefUtil.CollectTalkIds(option.precondition, talkIds);
                    ConditionRefUtil.CollectOptionIds(option.precondition, optionIds);
                    ConditionRefUtil.CollectTalkIds(option.stateCond, talkIds);
                    ConditionRefUtil.CollectOptionIds(option.stateCond, optionIds);
                }
                return true;
            }
            catch (Exception e)
            {
                error = "读取全局剧情引用失败；为避免自动编号影响其它事件，编辑模式已停用："
                        + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogError("[StoryGraph.Edit.ReservedIds] " + e);
                return false;
            }
        }

        /// <summary>
        /// 扫描当前 Mod 文件与游戏内置表组成的有效配置，阻止删除仍被会话外事件/节点引用的键。
        /// 当前会话内的来源配置会在同一次保存中同步断线，因此从外部引用结果中排除。
        /// </summary>
        internal static bool TryFindExternalReferences(
            StoryGraphEditSession session,
            StoryGraphEditNodeKind targetKind,
            int targetId,
            out string references,
            out string error,
            bool ignoreCurrentSessionEntry = false)
        {
            references = null;
            error = null;
            if (session == null || string.IsNullOrWhiteSpace(session.ModRoot))
            {
                error = "无法确定 Mod 根目录，不能执行全局引用检查。";
                return false;
            }
            try
            {
                Dictionary<int, EvtCfg> events = EffectiveMap(
                    SafeBuiltInMap(() => Cfg.EvtCfgMap),
                    LoadMap<EvtCfg>(ConfigPath<EvtCfg>(session.ModRoot)),
                    value => value != null ? value.id : 0);
                Dictionary<int, TalkCfg> talks = EffectiveMap(
                    SafeBuiltInMap(() => Cfg.TalkCfgMap),
                    LoadMap<TalkCfg>(ConfigPath<TalkCfg>(session.ModRoot)),
                    value => value != null ? value.id : 0);
                Dictionary<int, OptionCfg> options = EffectiveMap(
                    SafeBuiltInMap(() => Cfg.OptionCfgMap),
                    LoadMap<OptionCfg>(ConfigPath<OptionCfg>(session.ModRoot)),
                    value => value != null ? value.id : 0);

                var localTalkSources = new HashSet<int>(session.InitialTalkIds);
                foreach (TalkCfg talk in session.Talks)
                    if (talk != null) localTalkSources.Add(talk.id);
                var localOptionSources = new HashSet<int>(session.InitialOptionIds);
                foreach (int id in session.Options.Keys) localOptionSources.Add(id);
                var found = new List<string>();
                Dictionary<int, TalkCfg> projectedTalks =
                    ProjectTalkMapForSession(talks, session);
                Dictionary<int, OptionCfg> projectedOptions =
                    ProjectOptionMapForSession(options, session);
                AddCrossEventReachabilityReferences(
                    events, projectedTalks, projectedOptions,
                    session.EventId, targetKind, targetId, found);

                if (targetKind == StoryGraphEditNodeKind.Talk)
                {
                    foreach (KeyValuePair<int, EvtCfg> pair in events)
                    {
                        if (pair.Value == null) continue;
                        if (Contains(pair.Value.talkId, targetId)
                            && !(ignoreCurrentSessionEntry
                                && IsCurrentSessionEntryReference(
                                    session, pair.Key, pair.Value, targetId)))
                            AddReference(found, "事件 " + pair.Key + " 的入口（EvtCfg.talkId）");
                        if (StoryGraphEditSession.MiniGameReferencesTalk(
                                pair.Value.miniGame, targetId))
                            AddReference(found, "事件 " + pair.Key
                                + " 的参数跳转小游戏结果（EvtCfg.miniGame）");
                        if (ConditionRefUtil.ReferencesTalk(
                                pair.Value.condition, targetId))
                            AddReference(found, "事件 " + pair.Key + " 的条件参数");
                    }
                    foreach (KeyValuePair<int, TalkCfg> pair in talks)
                    {
                        if (localTalkSources.Contains(pair.Key) || pair.Value == null) continue;
                        if (Contains(pair.Value.nextTalk, targetId))
                            AddReference(found, "对话 " + pair.Key + " 的下一句");
                        if (Contains(pair.Value.nextTalk2, targetId))
                            AddReference(found, "对话 " + pair.Key + " 的备用分支");
                        if (StoryGraphEditSession.MiniGameReferencesTalk(
                                pair.Value.miniGame, targetId))
                            AddReference(found, "对话 " + pair.Key
                                + " 的参数跳转小游戏结果");
                        if (ConditionRefUtil.ReferencesTalk(
                                pair.Value.check, targetId))
                            AddReference(found, "对话 " + pair.Key + " 的条件参数");
                    }
                    foreach (KeyValuePair<int, OptionCfg> pair in options)
                    {
                        if (localOptionSources.Contains(pair.Key) || pair.Value == null) continue;
                        if (Contains(pair.Value.talkId, targetId))
                            AddReference(found, "选项 " + pair.Key + " 的结果");
                        if (Contains(pair.Value.talkId2, targetId))
                            AddReference(found, "选项 " + pair.Key + " 的备用结果");
                        if (StoryGraphEditSession.MiniGameReferencesTalk(
                                pair.Value.miniGame, targetId))
                            AddReference(found, "选项 " + pair.Key
                                + " 的参数跳转小游戏结果");
                        if (ConditionRefUtil.ReferencesTalk(pair.Value.check, targetId)
                            || ConditionRefUtil.ReferencesTalk(
                                pair.Value.precondition, targetId)
                            || ConditionRefUtil.ReferencesTalk(
                                pair.Value.stateCond, targetId))
                            AddReference(found, "选项 " + pair.Key + " 的条件参数");
                    }
                }
                else if (targetKind == StoryGraphEditNodeKind.Option)
                {
                    foreach (KeyValuePair<int, EvtCfg> pair in events)
                    {
                        if (pair.Value == null) continue;
                        if (Contains(pair.Value.options, targetId))
                            AddReference(found, "事件 " + pair.Key + " 的事件级选项（EvtCfg.options）");
                        if (ConditionRefUtil.ReferencesOption(
                                pair.Value.condition, targetId))
                            AddReference(found, "事件 " + pair.Key + " 的条件参数");
                    }
                    foreach (KeyValuePair<int, TalkCfg> pair in talks)
                    {
                        if (localTalkSources.Contains(pair.Key) || pair.Value == null) continue;
                        if (Contains(pair.Value.option, targetId))
                            AddReference(found, "对话 " + pair.Key + " 的选项字段");
                        if (ConditionRefUtil.ReferencesOption(
                                pair.Value.check, targetId))
                            AddReference(found, "对话 " + pair.Key + " 的条件参数");
                    }
                    foreach (KeyValuePair<int, OptionCfg> pair in options)
                    {
                        if (localOptionSources.Contains(pair.Key) || pair.Value == null) continue;
                        if (ConditionRefUtil.ReferencesOption(pair.Value.check, targetId)
                            || ConditionRefUtil.ReferencesOption(
                                pair.Value.precondition, targetId)
                            || ConditionRefUtil.ReferencesOption(
                                pair.Value.stateCond, targetId))
                            AddReference(found, "选项 " + pair.Key + " 的条件参数");
                    }
                }

                references = string.Join("；", found.ToArray());
                if (found.Count >= MaxReferencePreview) references += "；……";
                return true;
            }
            catch (Exception e)
            {
                error = "全局引用检查失败；为避免破坏其它事件，本次删除已取消："
                        + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogError("[StoryGraph.Edit.References] " + e);
                return false;
            }
        }

        private static Dictionary<int, TalkCfg> ProjectTalkMapForSession(
            IDictionary<int, TalkCfg> source,
            StoryGraphEditSession session)
        {
            var result = source != null
                ? new Dictionary<int, TalkCfg>(source)
                : new Dictionary<int, TalkCfg>();
            foreach (int id in session.InitialTalkIds) result.Remove(id);
            foreach (TalkCfg talk in session.Talks)
                if (talk != null && talk.id > 0) result[talk.id] = talk;
            return result;
        }

        private static Dictionary<int, OptionCfg> ProjectOptionMapForSession(
            IDictionary<int, OptionCfg> source,
            StoryGraphEditSession session)
        {
            var result = source != null
                ? new Dictionary<int, OptionCfg>(source)
                : new Dictionary<int, OptionCfg>();
            foreach (int id in session.InitialOptionIds) result.Remove(id);
            foreach (KeyValuePair<int, OptionCfg> pair in session.Options)
                if (pair.Key > 0 && pair.Value != null)
                    result[pair.Key] = pair.Value;
            return result;
        }

        /// <summary>
        /// 同一组 TalkCfg/OptionCfg 可以被多个 EvtCfg 共用。仅扫描“直接字段来源”会因
        /// 当前会话把整组 Talk 当成本地来源而漏报；这里从其它事件入口做可达性遍历，
        /// 只要目标仍属于另一事件的剧情路径，就把删除视为跨事件破坏。
        /// </summary>
        private static void AddCrossEventReachabilityReferences(
            IDictionary<int, EvtCfg> events,
            IDictionary<int, TalkCfg> talks,
            IDictionary<int, OptionCfg> options,
            int currentEventId,
            StoryGraphEditNodeKind targetKind,
            int targetId,
            List<string> found)
        {
            if (events == null || targetId <= 0 || found == null) return;
            foreach (KeyValuePair<int, EvtCfg> eventPair in events)
            {
                if (found.Count >= MaxReferencePreview) return;
                if (eventPair.Key == currentEventId || eventPair.Value == null)
                    continue;

                var talkQueue = new Queue<int>();
                var optionQueue = new Queue<int>();
                var seenTalks = new HashSet<int>();
                var seenOptions = new HashSet<int>();
                EnqueuePositive(talkQueue, eventPair.Value.talkId);
                EnqueuePositive(optionQueue, eventPair.Value.options);
                EnqueueMiniGameTargets(talkQueue, eventPair.Value.miniGame);
                bool reachesTarget = false;

                while (!reachesTarget
                       && (talkQueue.Count > 0 || optionQueue.Count > 0))
                {
                    if (talkQueue.Count > 0)
                    {
                        int talkId = talkQueue.Dequeue();
                        if (!seenTalks.Add(talkId)) continue;
                        if (targetKind == StoryGraphEditNodeKind.Talk
                            && talkId == targetId)
                        {
                            reachesTarget = true;
                            break;
                        }
                        TalkCfg talk;
                        if (talks == null
                            || !talks.TryGetValue(talkId, out talk)
                            || talk == null) continue;
                        EnqueuePositive(talkQueue, talk.nextTalk);
                        EnqueuePositive(talkQueue, talk.nextTalk2);
                        EnqueueMiniGameTargets(talkQueue, talk.miniGame);
                        EnqueuePositive(optionQueue, talk.option);
                    }
                    else
                    {
                        int optionId = optionQueue.Dequeue();
                        if (!seenOptions.Add(optionId)) continue;
                        if (targetKind == StoryGraphEditNodeKind.Option
                            && optionId == targetId)
                        {
                            reachesTarget = true;
                            break;
                        }
                        OptionCfg option;
                        if (options == null
                            || !options.TryGetValue(optionId, out option)
                            || option == null) continue;
                        EnqueuePositive(talkQueue, option.talkId);
                        EnqueuePositive(talkQueue, option.talkId2);
                        EnqueueMiniGameTargets(talkQueue, option.miniGame);
                    }
                }

                if (reachesTarget)
                    AddReference(found, "事件 " + eventPair.Key
                                      + " 的可达剧情路径（共享剧情组）");
            }
        }

        private static void EnqueuePositive(
            Queue<int> queue, IEnumerable<int> values)
        {
            if (queue == null || values == null) return;
            foreach (int value in values)
                if (value > 0) queue.Enqueue(value);
        }

        private static void EnqueueMiniGameTargets(
            Queue<int> queue, IList<double> miniGame)
        {
            if (queue == null || !MiniGameUtil.IsParamJump(miniGame)) return;
            List<int> targets;
            string error;
            if (!MiniGameUtil.TryGetParamJumpTargets(
                    miniGame, out targets, out error)) return;
            EnqueuePositive(queue, targets);
        }

        internal static bool TrySave(StoryGraphEditSession session, out string error)
        {
            error = null;
            if (session == null || string.IsNullOrWhiteSpace(session.ModRoot))
            {
                error = "无法确定当前 Mod 根目录，未写入任何文件。";
                return false;
            }
            if (!session.TryValidate(out error)) return false;
            if (!TryRecoverPendingTransaction(session.ModRoot, out error)) return false;

            FilePlan talkPlan = null;
            FilePlan optionPlan = null;
            FileStream talkGuard = null;
            FileStream optionGuard = null;
            TransactionJournal journal = null;
            // Committed=true 落盘即提交点：两个正式 JSON 已一致替换完成。此后任何
            // 失败（如用户备份发布）都只能降级为警告，绝不能翻转回滚——否则一份
            // .tx 备份恰好缺失时会回滚成功一半、失败一半，落成半新半旧终态。
            // 该语义必须与恢复路径的 PublishRecoveredUserBackup 保持一致。
            bool committedOnDisk = false;
            string journalPath = JournalPath(session.ModRoot);
            try
            {
                string talkPath = ConfigPath<TalkCfg>(session.ModRoot);
                string optionPath = ConfigPath<OptionCfg>(session.ModRoot);
                // 现有文件以 Share.Read|Share.Delete 打开：允许 File.Replace 原子换名，
                // 但拒绝其它工具在本次合并期间取得写句柄。不存在的文件仍由下方
                // 指纹复核保护创建竞争。
                talkGuard = AcquireReadGuard(talkPath);
                optionGuard = AcquireReadGuard(optionPath);
                FileFingerprint talkFingerprint = CaptureFingerprint(talkPath);
                Dictionary<string, TalkCfg> talkMap = LoadMap<TalkCfg>(talkPath);
                EnsureUnchanged(talkPath, talkFingerprint,
                    "读取 TalkCfg.json 期间文件被其它工具修改");
                FileFingerprint optionFingerprint = CaptureFingerprint(optionPath);
                Dictionary<string, OptionCfg> optionMap = LoadMap<OptionCfg>(optionPath);
                EnsureUnchanged(optionPath, optionFingerprint,
                    "读取 OptionCfg.json 期间文件被其它工具修改");
                var initialTalkIds = new HashSet<int>(session.InitialTalkIds);
                var initialOptionIds = new HashSet<int>(session.InitialOptionIds);
                var currentTalkIds = new HashSet<int>(
                    session.Talks.Where(t => t != null).Select(t => t.id));
                var currentOptionIds = new HashSet<int>(session.Options.Keys);

                // 保存前再次检查删除目标，覆盖“编辑期间其它工具刚写入新引用”的竞争窗口。
                foreach (int oldId in initialTalkIds)
                {
                    if (currentTalkIds.Contains(oldId)) continue;
                    string refs;
                    string refError;
                    if (!TryFindExternalReferences(session,
                            StoryGraphEditNodeKind.Talk, oldId, out refs, out refError))
                        throw new InvalidOperationException(refError);
                    if (!string.IsNullOrEmpty(refs))
                        throw new InvalidOperationException(
                            "对话 " + oldId + " 新增了会话外引用：" + refs);
                }
                foreach (int oldId in initialOptionIds)
                {
                    if (currentOptionIds.Contains(oldId)) continue;
                    string refs;
                    string refError;
                    if (!TryFindExternalReferences(session,
                            StoryGraphEditNodeKind.Option, oldId, out refs, out refError))
                        throw new InvalidOperationException(refError);
                    if (!string.IsNullOrEmpty(refs))
                        throw new InvalidOperationException(
                            "选项 " + oldId + " 新增了会话外引用：" + refs);
                }
                // 新 ID 即使没有同名配置，也可能已被其它事件作为“缺失引用”占位。
                // 把它补成真实节点会悄悄改变会话外剧情，因此保存前同样拒绝。
                foreach (int newId in currentTalkIds)
                {
                    if (initialTalkIds.Contains(newId)) continue;
                    string refs;
                    string refError;
                    if (!TryFindExternalReferences(session,
                            StoryGraphEditNodeKind.Talk, newId,
                            out refs, out refError, true))
                        throw new InvalidOperationException(refError);
                    if (!string.IsNullOrEmpty(refs))
                        throw new InvalidOperationException(
                            "新对话 " + newId + " 已被会话外配置引用：" + refs);
                }
                foreach (int newId in currentOptionIds)
                {
                    if (initialOptionIds.Contains(newId)) continue;
                    string refs;
                    string refError;
                    if (!TryFindExternalReferences(session,
                            StoryGraphEditNodeKind.Option, newId, out refs, out refError))
                        throw new InvalidOperationException(refError);
                    if (!string.IsNullOrEmpty(refs))
                        throw new InvalidOperationException(
                            "新选项 " + newId + " 已被会话外配置引用：" + refs);
                }

                ApplyTalkChanges(talkMap, session, initialTalkIds, currentTalkIds);
                ApplyOptionChanges(optionMap, session, initialOptionIds, currentOptionIds);

                string transactionId = Guid.NewGuid().ToString("N");
                talkPlan = Prepare(talkPath,
                    JsonConvert.SerializeObject(talkMap, Formatting.Indented),
                    transactionId, talkFingerprint);
                optionPlan = Prepare(optionPath,
                    JsonConvert.SerializeObject(optionMap, Formatting.Indented),
                    transactionId, optionFingerprint);
                CreateTransactionBackup(talkPlan);
                CreateTransactionBackup(optionPlan);
                journal = new TransactionJournal
                {
                    Committed = false,
                    Talk = ToJournalFile(talkPlan),
                    Option = ToJournalFile(optionPlan),
                };
                WriteJournal(journalPath, journal);

                Replace(talkPlan);
                Replace(optionPlan);

                // 两个目标均已替换后先写提交标记。若此后崩溃，恢复器保留新文件；
                // 若标记前崩溃，恢复器用两份事务备份统一回到旧版本。
                journal.Committed = true;
                WriteJournal(journalPath, journal);
                committedOnDisk = true;
                try
                {
                    PublishUserBackup(talkPlan);
                    PublishUserBackup(optionPlan);
                }
                catch (Exception backupError)
                {
                    // 与 PublishRecoveredUserBackup 同语义：提交已完整落盘，
                    // 用户备份发布失败不构成保存失败，更不允许触发回滚。
                    Plugin.Log?.LogWarning(
                        "[StoryGraph.Edit.Save] 新配置已完整提交，"
                        + "但发布 .storygraph.bak 用户备份失败：" + backupError.Message);
                }
                DeleteIfExists(journalPath);
                CleanupTransactionArtifacts(talkPlan);
                CleanupTransactionArtifacts(optionPlan);
                return true;
            }
            catch (Exception e)
            {
                if (committedOnDisk)
                {
                    // 防御分支：提交点之后理论上不再有可抛出的步骤，但若真的走到
                    // 这里，新配置对已经一致落盘，绝不能回滚或改写提交标记。
                    Plugin.Log?.LogError(
                        "[StoryGraph.Edit.Save] 提交点之后出现异常，"
                        + "新配置已完整落盘，不执行回滚：" + e);
                    DeleteIfExists(journalPath);
                    CleanupTransactionArtifacts(talkPlan);
                    CleanupTransactionArtifacts(optionPlan);
                    return true;
                }
                if (journal != null)
                {
                    journal.Committed = false;
                    try { WriteJournal(journalPath, journal); }
                    catch (Exception journalError)
                    {
                        Plugin.Log?.LogError("[StoryGraph.Edit.Journal] " + journalError);
                    }
                }
                bool optionRestored = TryRollback(optionPlan);
                bool talkRestored = TryRollback(talkPlan);
                if (optionRestored && talkRestored)
                {
                    DeleteIfExists(journalPath);
                    CleanupTransactionArtifacts(talkPlan);
                    CleanupTransactionArtifacts(optionPlan);
                }
                error = "保存失败"
                        + (optionRestored && talkRestored
                            ? "，原文件已恢复"
                            : "，自动恢复未完整完成；下次进入编辑模式会继续恢复")
                        + "：" + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogError("[StoryGraph.Edit.Save] " + e);
                return false;
            }
            finally
            {
                DeleteTempAndSwap(talkPlan);
                DeleteTempAndSwap(optionPlan);
                try { optionGuard?.Dispose(); }
                catch { }
                try { talkGuard?.Dispose(); }
                catch { }
            }
        }

        internal static bool HasPendingTransaction(string modRoot)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(modRoot)
                       && File.Exists(JournalPath(modRoot));
            }
            catch { return false; }
        }

        internal static bool TryRecoverPendingTransaction(
            string modRoot, out string error)
        {
            bool quarantined;
            return TryRecoverPendingTransaction(modRoot, out quarantined, out error);
        }

        /// <summary>
        /// 发现上次强退留下的事务日志时，统一完成提交清理或回滚两份配置。
        /// quarantined=true 表示日志确定性不可恢复、已隔离放行：磁盘保持
        /// 现状，什么都没有被恢复，调用方的提示不得声称“已恢复”。
        /// </summary>
        internal static bool TryRecoverPendingTransaction(
            string modRoot, out bool quarantined, out string error)
        {
            quarantined = false;
            error = null;
            if (string.IsNullOrWhiteSpace(modRoot))
            {
                error = "无法确定 Mod 根目录，不能检查剧情图保存事务。";
                return false;
            }
            string path = JournalPath(modRoot);
            if (!File.Exists(path)) return true;
            try
            {
                TransactionJournal journal = JsonConvert.DeserializeObject<TransactionJournal>(
                    File.ReadAllText(path));
                if (journal == null || journal.Talk == null || journal.Option == null)
                    throw new InvalidDataException("事务日志内容不完整。" );
                ValidateJournalFile(journal.Talk, ConfigPath<TalkCfg>(modRoot));
                ValidateJournalFile(journal.Option, ConfigPath<OptionCfg>(modRoot));

                if (journal.Committed)
                {
                    PublishRecoveredUserBackup(journal.Talk);
                    PublishRecoveredUserBackup(journal.Option);
                }
                else
                {
                    // 回滚是双文件统一动作：先各自判定，任何一份确定性不可恢复
                    // 时都不得只回滚另一份（会落成半新半旧终态），改为隔离日志
                    // 放行；判定期间的瞬时 IO 异常仍走 catch 继续阻塞。
                    JournalFileState talkState = ClassifyJournalFile(journal.Talk);
                    JournalFileState optionState = ClassifyJournalFile(journal.Option);
                    if (IsDeterministicallyUnrecoverable(talkState)
                        || IsDeterministicallyUnrecoverable(optionState))
                    {
                        QuarantineStaleJournal(path, talkState, optionState);
                        quarantined = true;
                        return true;
                    }
                    RestoreJournalFile(journal.Option);
                    RestoreJournalFile(journal.Talk);
                    Plugin.Log?.LogWarning(
                        "[StoryGraph.Edit] 检测到上次未完成的双文件保存，已统一恢复旧版本。" );
                }
                CleanupJournalFile(journal.Talk);
                CleanupJournalFile(journal.Option);
                DeleteIfExists(path);
                return true;
            }
            catch (Exception e)
            {
                error = "检测到未完成的剧情图保存，但自动恢复失败。请保留文件并查看日志："
                        + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogError("[StoryGraph.Edit.Recover] " + e);
                return false;
            }
        }

        private static void ApplyTalkChanges(
            Dictionary<string, TalkCfg> map,
            StoryGraphEditSession session,
            HashSet<int> initialIds,
            HashSet<int> currentIds)
        {
            foreach (int oldId in initialIds)
            {
                if (currentIds.Contains(oldId)) continue;
                List<string> keys = FindKeys(map, oldId, value => value != null ? value.id : 0);
                EnsureAtMostOneKey("对话", oldId, keys);
                if (keys.Count == 1) map.Remove(keys[0]);
            }
            foreach (TalkCfg talk in session.Talks)
            {
                int id = talk.id;
                List<string> keys = FindKeys(map, id, value => value != null ? value.id : 0);
                EnsureAtMostOneKey("对话", id, keys);
                if (!initialIds.Contains(id) && keys.Count > 0)
                    throw new InvalidOperationException(
                        "新对话编号 " + id + " 已被草稿外的 TalkCfg.json 记录占用，未覆盖原数据。" );
                if (!initialIds.Contains(id) && BuiltInTalkContains(id))
                    throw new InvalidOperationException(
                        "新对话编号 " + id + " 与游戏内置 TalkCfg 冲突，未创建 Mod 覆盖。" );
                if (keys.Count == 1) map.Remove(keys[0]);
                TalkCfg value = StoryGraphEditSession.CloneTalk(talk);
                Normalize(value);
                map[id.ToString()] = value;
            }
        }

        private static void ApplyOptionChanges(
            Dictionary<string, OptionCfg> map,
            StoryGraphEditSession session,
            HashSet<int> initialIds,
            HashSet<int> currentIds)
        {
            foreach (int oldId in initialIds)
            {
                if (currentIds.Contains(oldId)) continue;
                List<string> keys = FindKeys(map, oldId, value => value != null ? value.id : 0);
                EnsureAtMostOneKey("选项", oldId, keys);
                if (keys.Count == 1) map.Remove(keys[0]);
            }
            foreach (KeyValuePair<int, OptionCfg> pair in session.Options)
            {
                int id = pair.Key;
                // 借来显示的本体共享选项永不写入当前 Mod 的 OptionCfg.json，
                // 否则 Mod 会携带它的冻结副本，在所有玩家机器上覆盖本体。
                if (session.IsFrozenBuiltInOption(id)) continue;
                List<string> keys = FindKeys(map, id, value => value != null ? value.id : 0);
                EnsureAtMostOneKey("选项", id, keys);
                if (!initialIds.Contains(id) && keys.Count > 0)
                    throw new InvalidOperationException(
                        "新选项编号 " + id + " 已被草稿外的 OptionCfg.json 记录占用，未覆盖原数据。" );
                if (!initialIds.Contains(id) && BuiltInOptionContains(id))
                    throw new InvalidOperationException(
                        "新选项编号 " + id + " 与游戏内置 OptionCfg 冲突，未创建 Mod 覆盖。" );
                if (keys.Count == 1) map.Remove(keys[0]);
                OptionCfg value = StoryGraphEditSession.CloneOption(pair.Value);
                Normalize(value);
                map[id.ToString()] = value;
            }
        }

        private static List<string> FindKeys<T>(
            Dictionary<string, T> map, int id, Func<T, int> valueId)
        {
            var keys = new List<string>();
            foreach (KeyValuePair<string, T> pair in map)
            {
                int parsed;
                bool keyMatches = int.TryParse(pair.Key, out parsed) && parsed == id;
                int actualId = pair.Value != null ? valueId(pair.Value) : 0;
                bool valueMatches = actualId == id;
                if (keyMatches && !valueMatches)
                {
                    // 例如键 "1001" 内却放 id=2002。直接 map["1001"] = 新值会
                    // 静默吞掉草稿外的 2002；删除时也不能按键名误删它。
                    throw new InvalidOperationException(
                        "JSON 键“" + pair.Key + "”声称对应编号 " + id
                        + "，但记录内部编号为 " + actualId
                        + "；已中止保存，请先修正键与 id。" );
                }
                if (keyMatches || valueMatches) keys.Add(pair.Key);
            }
            return keys.Distinct(StringComparer.Ordinal).ToList();
        }

        private static void EnsureAtMostOneKey(
            string kind, int id, List<string> keys)
        {
            if (keys.Count <= 1) return;
            throw new InvalidOperationException(
                kind + " " + id + " 同时映射到多个 JSON 键（"
                + string.Join("、", keys.ToArray())
                + "）；为避免猜测覆盖顺序，已中止保存，请先手工合并重复记录。" );
        }

        private static bool BuiltInTalkContains(int id)
        {
            return Cfg.TalkCfgMap != null && Cfg.TalkCfgMap.ContainsKey(id);
        }

        private static bool BuiltInOptionContains(int id)
        {
            return Cfg.OptionCfgMap != null && Cfg.OptionCfgMap.ContainsKey(id);
        }

        private static Dictionary<int, T> SafeBuiltInMap<T>(
            Func<Dictionary<int, T>> getter)
        {
            // 读取异常必须冒泡给外层并阻止删除；把失败静默当成空表会漏掉
            // 游戏内置或其它事件的引用，违背全局删除保护的 fail-closed 原则。
            Dictionary<int, T> source = getter != null ? getter() : null;
            return source != null
                ? new Dictionary<int, T>(source)
                : new Dictionary<int, T>();
        }

        private static Dictionary<int, T> EffectiveMap<T>(
            Dictionary<int, T> builtIn,
            Dictionary<string, T> mod,
            Func<T, int> valueId)
        {
            var result = builtIn ?? new Dictionary<int, T>();
            foreach (KeyValuePair<string, T> pair in mod)
            {
                int id = pair.Value != null ? valueId(pair.Value) : 0;
                if (id <= 0) int.TryParse(pair.Key, out id);
                if (id > 0) result[id] = pair.Value;
            }
            return result;
        }

        private static bool Contains(List<int> values, int id)
        {
            return values != null && values.Contains(id);
        }

        private static void AddIds(HashSet<int> target, List<int> values)
        {
            if (target == null || values == null) return;
            foreach (int value in values)
                if (value > 0) target.Add(value);
        }

        private static void AddMiniGameTalkIds(
            ISet<int> target, IList<double> miniGame)
        {
            if (target == null || !MiniGameUtil.IsParamJump(miniGame)) return;
            List<int> values;
            string error;
            if (!MiniGameUtil.TryGetParamJumpTargets(
                    miniGame, out values, out error)) return;
            foreach (int value in values)
                if (value > 0) target.Add(value);
        }

        private static bool IsCurrentSessionEntryReference(
            StoryGraphEditSession session,
            int eventKey,
            EvtCfg evt,
            int targetId)
        {
            if (session == null || evt == null || !session.EntriesKnown
                || session.Entries == null || targetId <= 0) return false;
            bool sameEvent = eventKey == session.EventId || evt.id == session.EventId;
            return sameEvent && session.Entries.Contains(targetId);
        }

        private static void AddReference(List<string> found, string value)
        {
            if (found.Count >= MaxReferencePreview || found.Contains(value)) return;
            found.Add(value);
        }

        private static string ConfigPath<T>(string modRoot)
        {
            return Path.Combine(modRoot, "Cfgs", "zh-cn", typeof(T).Name + ".json");
        }

        private static string JournalPath(string modRoot)
        {
            return Path.Combine(modRoot, "Cfgs", "zh-cn", ".storygraph.transaction.json");
        }

        private static Dictionary<string, T> LoadMap<T>(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, T>();
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, T>();
            return JsonConvert.DeserializeObject<Dictionary<string, T>>(json)
                   ?? new Dictionary<string, T>();
        }

        private static FileStream AcquireReadGuard(string path)
        {
            if (!File.Exists(path)) return null;
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
        }

        private static FileFingerprint CaptureFingerprint(string path)
        {
            if (!File.Exists(path))
                return new FileFingerprint
                {
                    Exists = false,
                    Length = 0L,
                    Hash = new byte[0],
                };
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                return new FileFingerprint
                {
                    Exists = true,
                    Length = stream.Length,
                    Hash = sha.ComputeHash(stream),
                };
            }
        }

        private static bool FingerprintEquals(
            FileFingerprint left, FileFingerprint right)
        {
            if (left == null || right == null
                || left.Exists != right.Exists || left.Length != right.Length)
                return false;
            byte[] a = left.Hash ?? new byte[0];
            byte[] b = right.Hash ?? new byte[0];
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static void EnsureUnchanged(
            string path, FileFingerprint expected, string reason)
        {
            FileFingerprint current = CaptureFingerprint(path);
            if (!FingerprintEquals(expected, current))
                throw new IOException(reason
                    + "；为避免覆盖会话外改动，本次保存已取消，请刷新剧情图后重试。" );
        }

        private static FilePlan Prepare(
            string path, string json, string transactionId,
            FileFingerprint expected)
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("配置路径没有父目录：" + path);
            Directory.CreateDirectory(directory);
            var plan = new FilePlan
            {
                Path = path,
                Temp = path + ".storygraph.tmp." + transactionId,
                TransactionBackup = path + ".storygraph.tx." + transactionId + ".bak",
                SwapBackup = path + ".storygraph.swap." + transactionId + ".bak",
                UserBackup = path + ".storygraph.bak",
                Existed = expected != null && expected.Exists,
                Expected = expected,
            };
            WriteDurable(plan.Temp, json ?? "{}");
            plan.Replacement = CaptureFingerprint(plan.Temp);
            return plan;
        }

        private static void CreateTransactionBackup(FilePlan plan)
        {
            if (plan == null) return;
            EnsureUnchanged(plan.Path, plan.Expected,
                "建立事务备份前配置文件被其它工具修改");
            if (!plan.Existed) return;
            File.Copy(plan.Path, plan.TransactionBackup, true);
            // 事务备份必须先于正式替换真实落盘：断电后日志若指向一份只存在
            // 于 OS 缓存的备份，恢复器将无从回滚。
            FlushFileToDisk(plan.TransactionBackup);
            FileFingerprint backup = CaptureFingerprint(plan.TransactionBackup);
            if (!FingerprintEquals(plan.Expected, backup))
                throw new IOException("事务备份与读取时的配置内容不一致，已取消保存。" );
        }

        private static JournalFile ToJournalFile(FilePlan plan)
        {
            return new JournalFile
            {
                Path = plan.Path,
                Temp = plan.Temp,
                TransactionBackup = plan.TransactionBackup,
                SwapBackup = plan.SwapBackup,
                Existed = plan.Existed,
                OriginalLength = plan.Expected != null ? plan.Expected.Length : 0L,
                OriginalHash = HashToString(plan.Expected),
                ReplacementLength = plan.Replacement != null ? plan.Replacement.Length : 0L,
                ReplacementHash = HashToString(plan.Replacement),
            };
        }

        private static void Replace(FilePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            EnsureUnchanged(plan.Path, plan.Expected,
                "正式替换前配置文件被其它工具修改");
            // 悲观标记：Windows ReplaceFile 存在文档化的部分失败态（如
            // ERROR_UNABLE_TO_MOVE_REPLACEMENT_2——原文件已改名为 swap 备份、
            // 替换文件未就位即抛出）。标记必须先于改名动作，失败后才会进
            // TryRollback 的指纹判定而不是被 !Replaced 早退直接当作“未动过”。
            plan.Replaced = true;
            if (plan.Existed)
            {
                DeleteIfExists(plan.SwapBackup);
                File.Replace(plan.Temp, plan.Path, plan.SwapBackup, true);
                DeleteIfExists(plan.SwapBackup);
            }
            else
            {
                File.Move(plan.Temp, plan.Path);
            }
            FileFingerprint written = CaptureFingerprint(plan.Path);
            if (!FingerprintEquals(plan.Replacement, written))
                throw new IOException("正式配置写入后指纹不一致，事务将回滚。" );
        }

        private static void PublishUserBackup(FilePlan plan)
        {
            if (plan == null || !plan.Existed) return;
            if (!File.Exists(plan.TransactionBackup))
                throw new FileNotFoundException("事务备份不存在", plan.TransactionBackup);
            File.Copy(plan.TransactionBackup, plan.UserBackup, true);
        }

        private static bool TryRollback(FilePlan plan)
        {
            if (plan == null || !plan.Replaced) return true;
            try
            {
                FileFingerprint current = CaptureFingerprint(plan.Path);
                if (FingerprintEquals(current, plan.Expected))
                {
                    plan.Replaced = false;
                    return true;
                }
                if (plan.Existed && !current.Exists)
                {
                    // File.Replace 的部分失败态：原文件已被改名走、替换文件
                    // 未就位，目标此刻缺失。事务备份在替换前已强制落盘，直接
                    // 补回；备份缺失则返回 false 保住日志与 .tx 留待下次恢复。
                    if (!File.Exists(plan.TransactionBackup)) return false;
                    File.Copy(plan.TransactionBackup, plan.Path, true);
                    FlushFileToDisk(plan.Path);
                    if (!FingerprintEquals(
                            CaptureFingerprint(plan.Path), plan.Expected))
                        return false;
                    plan.Replaced = false;
                    return true;
                }
                if (!FingerprintEquals(current, plan.Replacement))
                    return false; // 有会话外新写入，绝不拿旧备份覆盖它。
                if (plan.Existed)
                {
                    if (!File.Exists(plan.TransactionBackup)) return false;
                    File.Copy(plan.TransactionBackup, plan.Path, true);
                    // 回滚成功后调用方随即删除日志与 .tx 备份（NTFS 元数据操作
                    // 先于缓存数据落盘），这里必须先把回滚内容真实推到磁盘。
                    FlushFileToDisk(plan.Path);
                }
                else if (File.Exists(plan.Path))
                {
                    File.Delete(plan.Path);
                }
                plan.Replaced = false;
                return FingerprintEquals(CaptureFingerprint(plan.Path), plan.Expected);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StoryGraph.Edit.Rollback] " + e);
                return false;
            }
        }

        private static void WriteJournal(string path, TransactionJournal journal)
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("事务日志路径没有父目录。" );
            Directory.CreateDirectory(directory);
            string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
            string swap = path + ".swap." + Guid.NewGuid().ToString("N");
            try
            {
                WriteDurable(temp,
                    JsonConvert.SerializeObject(journal, Formatting.Indented));
                if (File.Exists(path))
                {
                    File.Replace(temp, path, swap, true);
                    DeleteIfExists(swap);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                DeleteIfExists(temp);
                DeleteIfExists(swap);
            }
        }

        private static void ValidateJournalFile(
            JournalFile file, string expectedPath)
        {
            if (file == null) throw new InvalidDataException("事务文件项为空。" );
            string expected = Path.GetFullPath(expectedPath);
            string expectedName = Path.GetFileName(expected);
            // 日志由作者机写下后可能随创意工坊整目录上传（SetItemContent 不触发
            // 任何清理钩子）或被整目录挪动，记录的绝对路径在本机必然对不上；
            // 恢复只关心同目录内这几个文件，因此校验文件名形状后一律重定位
            // （rebase）到本机 Mod 路径。指纹校验保持不变。
            if (!string.Equals(expectedName,
                    Path.GetFileName(file.Path ?? string.Empty),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "事务日志目标文件名与配置不符：" + file.Path);
            file.Path = expected;
            string directory = Path.GetDirectoryName(expected);
            file.Temp = RebaseJournalAuxPath(
                file.Temp, directory, expectedName + ".storygraph.tmp.");
            file.TransactionBackup = RebaseJournalAuxPath(
                file.TransactionBackup, directory,
                expectedName + ".storygraph.tx.", ".bak");
            file.SwapBackup = RebaseJournalAuxPath(
                file.SwapBackup, directory,
                expectedName + ".storygraph.swap.", ".bak");
            // 同时验证 Base64 指纹字段，损坏日志必须 fail-closed，不能猜测回滚。
            FingerprintFromJournal(file, false);
            FingerprintFromJournal(file, true);
        }

        private static string RebaseJournalAuxPath(
            string value, string directory,
            string requiredPrefix, string requiredSuffix = null)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("事务日志包含空的临时路径。" );
            string name = Path.GetFileName(value);
            int minLength = requiredPrefix.Length
                            + (requiredSuffix != null ? requiredSuffix.Length : 0);
            if (name.Length <= minLength
                || !name.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(requiredSuffix)
                    && !name.EndsWith(requiredSuffix, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("事务日志临时路径格式无效：" + name);
            return Path.Combine(directory, name);
        }

        private static void PublishRecoveredUserBackup(JournalFile file)
        {
            if (file == null || !file.Existed || !File.Exists(file.TransactionBackup)) return;
            try
            {
                File.Copy(file.TransactionBackup, file.Path + ".storygraph.bak", true);
            }
            catch (Exception e)
            {
                // Committed=true 代表两个正式 JSON 已一致落盘；用户备份发布失败
                // 不应把一次完整提交误判成待回滚事务并永久阻塞事件加载。
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Edit.Recover] 新文件已提交，但发布 .storygraph.bak 失败："
                    + e.Message);
            }
        }

        private static JournalFileState ClassifyJournalFile(JournalFile file)
        {
            if (file == null) throw new InvalidDataException("事务文件项为空。" );
            FileFingerprint original = FingerprintFromJournal(file, false);
            FileFingerprint replacement = FingerprintFromJournal(file, true);
            FileFingerprint current = CaptureFingerprint(file.Path);
            if (FingerprintEquals(current, original))
                return JournalFileState.AtOriginal;
            // 断电恰落在 File.Replace 两次改名之间时目标文件缺失，但事务备份
            // 仍是保存前原文——确定性可恢复（File.Copy 天然支持目标缺失），
            // 不能归入 ExternalChange 被隔离放行；备份缺失或内容不符则维持
            // 下方的不可恢复判定。
            if (file.Existed && !current.Exists
                && File.Exists(file.TransactionBackup)
                && FingerprintEquals(
                    CaptureFingerprint(file.TransactionBackup), original))
                return JournalFileState.Restorable;
            if (!FingerprintEquals(current, replacement))
                return JournalFileState.ExternalChange;
            if (file.Existed && !File.Exists(file.TransactionBackup))
                return JournalFileState.ReplacementWithoutBackup;
            return JournalFileState.Restorable;
        }

        private static bool IsDeterministicallyUnrecoverable(JournalFileState state)
        {
            return state == JournalFileState.ExternalChange
                   || state == JournalFileState.ReplacementWithoutBackup;
        }

        private static string DescribeJournalFileState(JournalFileState state)
        {
            switch (state)
            {
                case JournalFileState.AtOriginal:
                    return "仍是保存前的旧版本，无需回滚";
                case JournalFileState.Restorable:
                    return "是未提交的新版本，事务备份可用";
                case JournalFileState.ReplacementWithoutBackup:
                    return "已是未提交的新版本，但事务备份缺失，无料可回滚";
                case JournalFileState.ExternalChange:
                    return "在会话外被其它工具改写，回滚会覆盖该改动";
                default:
                    return state.ToString();
            }
        }

        private static void QuarantineStaleJournal(
            string journalPath,
            JournalFileState talkState, JournalFileState optionState)
        {
            string directory = Path.GetDirectoryName(journalPath) ?? string.Empty;
            string staleName = ".storygraph.transaction.stale-"
                               + Guid.NewGuid().ToString("N") + ".json";
            // 改名失败（占用/权限）冒泡给上层继续阻塞：放行必须以日志确实离开
            // 待恢复位置为前提，否则每次打开都会再撞同一份日志。隔离名不以
            // Cfg.json 结尾，不会被 ModCtrl 的 *Cfg.json 通配加载。
            File.Move(journalPath, Path.Combine(directory, staleName));
            Plugin.Log?.LogWarning(
                "[StoryGraph.Edit.Recover] 旧保存事务已确认无法自动恢复，"
                + "日志已隔离为 " + staleName + " 并放行本次加载。判定："
                + "TalkCfg.json " + DescribeJournalFileState(talkState)
                + "；OptionCfg.json " + DescribeJournalFileState(optionState)
                + "。相关 .storygraph.tx.*.bak 事务备份仍保留在原目录，"
                + "保存前的版本另见同目录 .storygraph.bak（如存在）。" );
            try
            {
                // 走路由：保存前防御性复查可能在剧情图 Overlay 激活期间触发，
                // 原版 Toast 会被整体盖住；非图路径由路由回落原版 Toast。
                StoryGraphToastRouter.Show(
                    "检测到无法自动恢复的旧保存事务，已隔离放行；详情与备份位置见日志。" );
            }
            catch { }
        }

        private static void RestoreJournalFile(JournalFile file)
        {
            JournalFileState state = ClassifyJournalFile(file);
            if (state == JournalFileState.AtOriginal) return; // 此文件尚未替换。
            if (state != JournalFileState.Restorable)
                throw new IOException(
                    "回滚前配置状态再次变化（" + DescribeJournalFileState(state)
                    + "），本次不回滚：" + file.Path);
            if (file.Existed)
            {
                File.Copy(file.TransactionBackup, file.Path, true);
                // 调用方随即删除日志与 .tx 备份（NTFS 元数据操作先于缓存数据
                // 落盘），恢复内容必须先真实推到磁盘，二次断电才不会撕裂。
                FlushFileToDisk(file.Path);
            }
            else if (File.Exists(file.Path))
            {
                File.Delete(file.Path);
            }
            if (!FingerprintEquals(CaptureFingerprint(file.Path),
                    FingerprintFromJournal(file, false)))
                throw new IOException("恢复后的配置指纹与事务旧版本不一致：" + file.Path);
        }

        private static string HashToString(FileFingerprint fingerprint)
        {
            return Convert.ToBase64String(
                fingerprint != null && fingerprint.Hash != null
                    ? fingerprint.Hash
                    : new byte[0]);
        }

        private static FileFingerprint FingerprintFromJournal(
            JournalFile file, bool replacement)
        {
            string encoded = replacement ? file.ReplacementHash : file.OriginalHash;
            byte[] hash;
            try { hash = Convert.FromBase64String(encoded ?? string.Empty); }
            catch (FormatException e)
            {
                throw new InvalidDataException("事务日志指纹格式无效。", e);
            }
            bool exists = replacement || file.Existed;
            if ((exists && hash.Length != 32) || (!exists && hash.Length != 0))
                throw new InvalidDataException("事务日志 SHA-256 指纹长度无效。" );
            return new FileFingerprint
            {
                Exists = exists,
                Length = replacement ? file.ReplacementLength : file.OriginalLength,
                Hash = hash,
            };
        }

        private static void CleanupJournalFile(JournalFile file)
        {
            if (file == null) return;
            DeleteIfExists(file.Temp);
            DeleteIfExists(file.SwapBackup);
            DeleteIfExists(file.TransactionBackup);
        }

        private static void CleanupTransactionArtifacts(FilePlan plan)
        {
            if (plan == null) return;
            DeleteIfExists(plan.TransactionBackup);
            DeleteIfExists(plan.SwapBackup);
        }

        private static void DeleteTempAndSwap(FilePlan plan)
        {
            if (plan == null) return;
            DeleteIfExists(plan.Temp);
            DeleteIfExists(plan.SwapBackup);
        }

        private static void DeleteIfExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* 清理失败保留文件，不能覆盖真正的提交/回滚结果。 */ }
        }

        /// <summary>
        /// 事务里先行写下的临时/日志文件必须真实落盘（WriteThrough+Flush(true)，
        /// 与 AtomicFilePairTransaction.WriteDurable 同法）：普通 WriteAllText 只
        /// 进 OS 缓存，断电后日志可能指向不存在的内容。UTF8 无 BOM。
        /// </summary>
        private static void WriteDurable(string path, string content)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                stream, new UTF8Encoding(false), 4096, true))
            {
                writer.Write(content ?? string.Empty);
                writer.Flush();
                stream.Flush(true);
            }
        }

        /// <summary>File.Copy 产物重开写句柄 Flush(true)，把整份内容推到磁盘。</summary>
        private static void FlushFileToDisk(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Flush(true);
            }
        }

        private static void Normalize(TalkCfg value)
        {
            if (value == null) return;
            value.roleName = TalkRoleNameUtil.NormalizeOverride(value.roleName);
            if (value.check == null) value.check = new List<List<double>>();
            if (value.effect == null) value.effect = new List<List<float>>();
            if (value.effect2 == null) value.effect2 = new List<List<float>>();
            if (value.highlights == null) value.highlights = new List<int>();
            if (value.miniGame == null) value.miniGame = new List<double>();
            if (value.nextTalk == null) value.nextTalk = new List<int>();
            if (value.nextTalk2 == null) value.nextTalk2 = new List<int>();
            if (value.option == null) value.option = new List<int>();
            if (value.replace == null) value.replace = new List<int>();
            if (value.roleIds == null) value.roleIds = new List<int>();
            if (value.roles == null) value.roles = new List<List<float>>();
            if (value.screenEffect == null) value.screenEffect = new List<float>();
            if (value.vocals == null) value.vocals = new List<float>();
        }

        private static void Normalize(OptionCfg value)
        {
            if (value == null) return;
            if (value.check == null) value.check = new List<List<double>>();
            if (value.effect == null) value.effect = new List<List<float>>();
            if (value.effect2 == null) value.effect2 = new List<List<float>>();
            if (value.miniGame == null) value.miniGame = new List<double>();
            if (value.precondition == null) value.precondition = new List<List<double>>();
            if (value.pressure == null) value.pressure = new List<List<float>>();
            if (value.stateCond == null) value.stateCond = new List<List<double>>();
            if (value.talkId == null) value.talkId = new List<int>();
            if (value.talkId2 == null) value.talkId2 = new List<int>();
        }
    }

}
