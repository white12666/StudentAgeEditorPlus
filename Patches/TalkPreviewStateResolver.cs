using System;
using System.Collections.Generic;
using Config;
using View.Evt;

namespace StudentAgeEditorPlus.Patches
{
    internal enum TalkPreviewPlaybackMode
    {
        /// <summary>按实际游戏 NewTalkView 的场景边界回放。</summary>
        Game,
        /// <summary>按作者预览 PreviewTalkView 回放（额外处理 Talk ID 千位组切换）。</summary>
        Preview,
    }

    internal enum TalkPreviewOverlayKind
    {
        None,
        Cg,
        Comic,
        MiniCg,
    }

    /// <summary>会跨 Talk 保留的非人物预览状态。</summary>
    internal sealed class TalkPreviewScreenState
    {
        public bool Blur;
        /// <summary>0=无，4009=泛黄，4010=反色。</summary>
        public int ColorEffect;
        public TalkPreviewOverlayKind Overlay;
        public int OverlayId;
        public int ComicPage;
        public bool WakeEffectActive;
        public float WakeEffectValue;
        public bool PhoneOpen;
        public int PhoneBackgroundId;
        public TalkAxis PhoneAxis = TalkAxis.Right;
        public List<int> PhoneTemporaryRoleIds = new();
        public int BgmAudioId;
        public bool GroupBgmConsumed;

        public TalkPreviewScreenState Clone()
        {
            var clone = (TalkPreviewScreenState)MemberwiseClone();
            clone.PhoneTemporaryRoleIds = new List<int>(PhoneTemporaryRoleIds);
            return clone;
        }
    }

    /// <summary>
    /// 某一时刻的人物持久预览状态。瞬时跳跃、抖动和 3009 表情气泡不跨 Talk；
    /// 3000 则是立绘表情/姿势，会保留在 NewTalkRoleData 中，必须进入快照。
    /// </summary>
    internal sealed class TalkPreviewRoleState
    {
        public int PersonId;
        public TalkAxis Axis = TalkAxis.Init;
        /// <summary>播放器内部的目标方位；隐式建角但尚未定位时可能与 Axis 不同。</summary>
        public TalkAxis TargetAxis = TalkAxis.Init;
        public int Layer = 1;
        public int TargetLayer = 1;
        public int SlotIndex = -1;
        public float OffsetX;
        public float OffsetY;
        public int Cloth = -1;
        public int Hair;
        public int Pose;
        /// <summary>是否确实执行过 3000；false 时静态 L2D 小舞台应保留原版 -1 默认表情。</summary>
        public bool PoseSet;
        public bool Flipped;
        public float Scale = 1f;
        public bool ScaleZero;
        public int ScaleCount;
        public int FlipCount;
        /// <summary>0=正常，1=剪影。</summary>
        public int Shadow;
        public PhoneState PhoneState;

        public TalkPreviewRoleState Clone()
        {
            return (TalkPreviewRoleState)MemberwiseClone();
        }
    }

    /// <summary>
    /// 沿编辑器选定的一条前驱链回放到当前 Talk 得到的快照。
    /// BeforeCurrent 位于当前非空 Talk 的换景/清场之后、ShowCurTxt 建角和动作之前；
    /// AfterCurrent 位于该 Talk 动作完成并移除退场人物之后。空 content Talk 按播放器
    /// 语义整句跳过，因此 Before/After 相同且不会应用其 bg/roles。
    /// </summary>
    internal sealed class TalkPreviewSnapshot
    {
        public Dictionary<int, TalkPreviewRoleState> BeforeCurrent = new();
        public Dictionary<int, TalkPreviewRoleState> AfterCurrent = new();
        public Dictionary<TalkAxis, List<int>> BeforeSlots = new();
        public Dictionary<TalkAxis, List<int>> AfterSlots = new();
        public Dictionary<int, int> BeforeCloths = new();
        public Dictionary<int, int> AfterCloths = new();
        public List<int> BeforeUseCloth = new();
        public List<int> AfterUseCloth = new();
        public TalkPreviewScreenState BeforeCurrentScreen = new();
        /// <summary>当前 Talk 的 PlayBgEffect 执行后、ShowCurTxt 执行前。</summary>
        public TalkPreviewScreenState BeforeShowCurrentScreen = new();
        public TalkPreviewScreenState AfterCurrentScreen = new();
        public List<int> BeforeRoleOrder = new();
        public List<int> AfterRoleOrder = new();
        public TalkAxis BeforeTalkingPos = TalkAxis.Right;
        public TalkAxis AfterTalkingPos = TalkAxis.Right;
        /// <summary>当前 Talk 场景边界执行前的有效背景。</summary>
        public int IncomingBackgroundId = -1;
        public int BeforeBackgroundId = -1;
        public int AfterBackgroundId = -1;
        public bool CurrentSceneBoundary;
        public bool AmbiguousPredecessor;
        /// <summary>
        /// 回溯某一步时，只发现指向该 Talk、但未被任何父 Talk 挂载的选项。
        /// 这与真正没有任何前驱的独立起点不同，预览入口应阻止静默按根节点回放。
        /// </summary>
        public bool OrphanOnlyPredecessor;
        public bool CycleDetected;
        public bool DuplicateTalkId;
        public bool BackgroundContextDependent;
        public int PathLength;

        public bool Reliable => !DuplicateTalkId && !CycleDetected
            && !BackgroundContextDependent;
    }

    /// <summary>
    /// 编辑器显示层使用的只读对话状态解析器。实现的是播放器关键状态机，而不是简单
    /// 把每句 roles 相加：空 Talk 会跳过；有效换景会清场；roleIds 和非退场动作可
    /// 隐式建角；动作使用与 ShowCurTxt 相同的排序比较器；同方位槽位保留 -1 空洞；
    /// 服装缓存独立于人物生命周期，退场/清场后仍会被 GetRoleUseCloth 复用。
    /// </summary>
    internal static class TalkPreviewStateResolver
    {
        private sealed class SimRole
        {
            public int PersonId;
            public TalkAxis Axis = TalkAxis.Init;
            public TalkAxis TargetAxis = TalkAxis.Init;
            public int Layer = 1;
            public int TargetLayer = 1;
            public int SlotIndex = -1;
            public float OffsetX;
            public float OffsetY;
            public int Cloth = -1;
            public int Hair;
            public int Pose;
            public bool PoseSet;
            public bool Flipped;
            public float Scale = 1f;
            public bool ScaleZero;
            public int ScaleCount;
            public int FlipCount;
            public bool PendingPreFlip;
            public int Shadow;
            public PhoneState PhoneState;

            public TalkPreviewRoleState Snapshot()
            {
                return new TalkPreviewRoleState
                {
                    PersonId = PersonId,
                    Axis = Axis,
                    TargetAxis = TargetAxis,
                    Layer = Layer,
                    TargetLayer = TargetLayer,
                    SlotIndex = SlotIndex,
                    OffsetX = OffsetX,
                    OffsetY = OffsetY,
                    Cloth = Cloth,
                    Hair = Hair,
                    Pose = Pose,
                    PoseSet = PoseSet,
                    Flipped = Flipped,
                    Scale = Scale,
                    ScaleZero = ScaleZero,
                    ScaleCount = ScaleCount,
                    FlipCount = FlipCount,
                    Shadow = Shadow,
                    PhoneState = PhoneState,
                };
            }
        }

        private sealed class PlaybackState
        {
            public readonly Dictionary<int, SimRole> Roles = new();
            public readonly List<int> RoleOrder = new();
            public readonly Dictionary<TalkAxis, List<int>> Slots = new();
            public readonly Dictionary<int, int> ClothCache = new();
            public List<int> UseCloth;
            public readonly TalkPreviewScreenState Screen = new();
            public TalkAxis TalkingPos = TalkAxis.Right;
            public int CurrentBg = -1;
            public bool BackgroundContextDependent;
            public int BoundaryTalkId;
        }

        public static TalkPreviewSnapshot Resolve(
            List<TalkCfg> talks,
            Dictionary<int, OptionCfg> options,
            TalkCfg current)
        {
            return Resolve(talks, options, current, null, null,
                TalkPreviewPlaybackMode.Game, null, null);
        }

        public static TalkPreviewSnapshot Resolve(
            List<TalkCfg> talks,
            Dictionary<int, OptionCfg> options,
            TalkCfg current,
            ISet<int> validBackgroundIds,
            ISet<int> validPersonIds,
            TalkPreviewPlaybackMode mode,
            Dictionary<int, AudioCfg> audioCfgs = null,
            Dictionary<int, BgCfg> backgroundCfgs = null)
        {
            var result = new TalkPreviewSnapshot();
            if (current == null) return result;

            List<TalkCfg> path = BuildPath(talks, options, current, result);
            result.PathLength = path.Count;
            if (path.Count == 0) return result;

            var state = new PlaybackState { BoundaryTalkId = path[0].id };
            foreach (TalkCfg talk in path)
            {
                if (talk == null) continue;

                // PreviewTalkView/NewTalkView 在 RefreshTalk 一开始就递归跳过空文本，
                // bg、roles、roleIds、screenEffect 与 audio 均不会执行。
                bool isCurrent = ReferenceEquals(talk, current);
                bool skipped = string.IsNullOrWhiteSpace(talk.content);
                if (isCurrent) result.IncomingBackgroundId = state.CurrentBg;

                bool sceneBoundary = false;
                if (!skipped)
                    sceneBoundary = ApplySceneBoundary(
                        talk, state, validBackgroundIds, mode);
                result.BackgroundContextDependent |= state.BackgroundContextDependent;
                if (!skipped)
                {
                    bool validPositiveBg = talk.bg > 0
                        && (validBackgroundIds == null
                            || validBackgroundIds.Contains(talk.bg));
                    // 非边界分支显式调用 RefreshCloth；切到有效正背景时
                    // RefreshBg 也会调用。纯 -1/-2 黑场本身不会初始化 useCloth。
                    if (!sceneBoundary || validPositiveBg)
                        EnsureUseCloth(state, backgroundCfgs);
                }

                if (isCurrent)
                {
                    result.CurrentSceneBoundary = sceneBoundary;
                    result.BeforeCurrent = CloneRoles(state.Roles);
                    result.BeforeSlots = CloneSlots(state.Slots);
                    result.BeforeCloths = new Dictionary<int, int>(state.ClothCache);
                    result.BeforeUseCloth = state.UseCloth != null
                        ? new List<int>(state.UseCloth)
                        : new List<int>();
                    result.BeforeCurrentScreen = state.Screen.Clone();
                    result.BeforeRoleOrder = new List<int>(state.RoleOrder);
                    result.BeforeTalkingPos = state.TalkingPos;
                    result.BeforeBackgroundId = state.CurrentBg;
                }

                if (!skipped)
                {
                    // PlayBgEffect 发生在 ShowCurTxt 之前；中途预览注入滤镜时要取
                    // 当前句这一步执行后的值，避免把已被本句 4003 清掉的滤镜加回来。
                    ApplyBackgroundEffect(talk, state.Screen);
                    if (isCurrent)
                        result.BeforeShowCurrentScreen = state.Screen.Clone();

                    ApplyTalk(talk, state, validPersonIds);
                    ApplyAudio(talk, state.Screen, audioCfgs);
                }
                else if (isCurrent)
                {
                    result.BeforeShowCurrentScreen = state.Screen.Clone();
                }

                if (isCurrent)
                {
                    result.AfterCurrent = CloneRoles(state.Roles);
                    result.AfterSlots = CloneSlots(state.Slots);
                    result.AfterCloths = new Dictionary<int, int>(state.ClothCache);
                    result.AfterUseCloth = state.UseCloth != null
                        ? new List<int>(state.UseCloth)
                        : new List<int>();
                    result.AfterCurrentScreen = state.Screen.Clone();
                    result.AfterRoleOrder = new List<int>(state.RoleOrder);
                    result.AfterTalkingPos = state.TalkingPos;
                    result.AfterBackgroundId = state.CurrentBg;
                    break;
                }
            }

            return result;
        }

        private static List<TalkCfg> BuildPath(
            List<TalkCfg> talks,
            Dictionary<int, OptionCfg> options,
            TalkCfg current,
            TalkPreviewSnapshot result)
        {
            var reverse = new List<TalkCfg>();
            if (talks == null || talks.Count == 0)
            {
                reverse.Add(current);
                return reverse;
            }

            var idCounts = new Dictionary<int, int>();
            foreach (TalkCfg talk in talks)
            {
                if (talk == null) continue;
                idCounts.TryGetValue(talk.id, out int count);
                idCounts[talk.id] = count + 1;
            }
            foreach (KeyValuePair<int, int> count in idCounts)
            {
                if (count.Value > 1)
                {
                    result.DuplicateTalkId = true;
                    break;
                }
            }

            var visited = new HashSet<int>();
            TalkCfg cursor = current;
            while (cursor != null)
            {
                if (!visited.Add(cursor.id))
                {
                    result.CycleDetected = true;
                    break;
                }

                reverse.Add(cursor);
                TalkCfg previous = FindPredecessor(
                    talks, options, cursor.id, out bool ambiguous,
                    out bool orphanOnly);
                result.AmbiguousPredecessor |= ambiguous;
                result.OrphanOnlyPredecessor |= orphanOnly;
                if (previous == null) break;
                if (visited.Contains(previous.id))
                {
                    result.CycleDetected = true;
                    break;
                }
                cursor = previous;
            }

            reverse.Reverse();
            return reverse;
        }

        /// <summary>
        /// 选择顺序尽量跟随 ModEvtEditView.FindRoles：先 talkCfgs 中第一个直接前驱；
        /// 没有时按 optionCfgs 顺序寻找第一个指向目标且确有父 Talk 的 option。
        /// 指向目标但没有父 Talk 的孤立 option 不参与前驱选择，避免旧残留连线遮蔽
        /// 后面的唯一有效分支；若只有这种孤立引用，则另行标记并由预览入口阻止。
        /// </summary>
        private static TalkCfg FindPredecessor(
            List<TalkCfg> talks,
            Dictionary<int, OptionCfg> options,
            int targetTalkId,
            out bool ambiguous,
            out bool orphanOnly)
        {
            ambiguous = false;
            orphanOnly = false;
            TalkCfg firstDirect = null;
            int directCount = 0;
            foreach (TalkCfg talk in talks)
            {
                if (talk == null) continue;
                // 不排除 talk.id == targetTalkId：自指 nextTalk 也是实际前驱环，
                // 返回当前对象后由 BuildPath 的 visited 立即标记 CycleDetected。
                if (!Contains(talk.nextTalk, targetTalkId)
                    && !Contains(talk.nextTalk2, targetTalkId)) continue;
                if (firstDirect == null) firstDirect = talk;
                directCount++;
            }

            FindOptionParentCandidates(
                talks, options, targetTalkId,
                out TalkCfg firstOptionParent,
                out int optionParentCount,
                out bool hasMatchingOption);
            if (firstDirect != null)
            {
                ambiguous = directCount > 1 || optionParentCount > 0;
                return firstDirect;
            }

            if (firstOptionParent != null)
            {
                ambiguous = optionParentCount > 1;
                return firstOptionParent;
            }

            // 没有直接前驱，也没有任何有效 option 父级，但配置中仍有 option
            // 指向目标：这是删除父连线后留下的孤立结果边，不能冒充真正的根节点。
            orphanOnly = hasMatchingOption;
            return null;
        }

        private static void FindOptionParentCandidates(
            List<TalkCfg> talks,
            Dictionary<int, OptionCfg> options,
            int targetTalkId,
            out TalkCfg firstParent,
            out int parentCount,
            out bool hasMatchingOption)
        {
            firstParent = null;
            parentCount = 0;
            hasMatchingOption = false;
            if (options == null || options.Count == 0) return;

            var parents = new HashSet<TalkCfg>();
            foreach (KeyValuePair<int, OptionCfg> entry in options)
            {
                OptionCfg option = entry.Value;
                if (option == null || (!Contains(option.talkId, targetTalkId)
                    && !Contains(option.talkId2, targetTalkId))) continue;
                hasMatchingOption = true;

                TalkCfg firstForOption = null;
                foreach (TalkCfg talk in talks)
                {
                    if (talk == null || !Contains(talk.option, entry.Key)) continue;
                    if (firstForOption == null) firstForOption = talk;
                    parents.Add(talk);
                }
                // 保留原来的 optionCfgs/talkCfgs 选取顺序，但会越过没有父级的
                // 孤立 option，选择后面的第一个真实分支。
                if (firstParent == null && firstForOption != null)
                    firstParent = firstForOption;
            }
            parentCount = parents.Count;
        }

        private static bool ApplySceneBoundary(
            TalkCfg talk,
            PlaybackState state,
            ISet<int> validBackgroundIds,
            TalkPreviewPlaybackMode mode)
        {
            int bg = talk.bg;
            bool validPositiveBg = bg > 0
                && (validBackgroundIds == null || validBackgroundIds.Contains(bg));
            bool groupChanged = mode == TalkPreviewPlaybackMode.Preview
                && state.BoundaryTalkId / 1000 != talk.id / 1000;

            // NewTalkView 可由事件入口预先带入背景；编辑器不知道该 initialBg。
            // 若在首个有效背景前已有 bg=0 人物，则“显式写回同一背景是否清场”
            // 取决于调用现场，不能假装得到唯一答案。
            if (mode == TalkPreviewPlaybackMode.Game && validPositiveBg
                && state.CurrentBg < 0 && state.Roles.Count > 0)
                state.BackgroundContextDependent = true;

            bool backgroundChanged = validPositiveBg && bg != state.CurrentBg;
            bool boundary = bg == -1 || bg == -2 || backgroundChanged || groupChanged;
            if (!boundary) return false;

            state.BoundaryTalkId = talk.id;
            if (bg != -2)
                ClearRoles(state);

            // PreviewTalkView.OnBlackBg 每次边界都会关闭 CG/漫画；手机层不会关。
            state.Screen.Overlay = TalkPreviewOverlayKind.None;
            state.Screen.OverlayId = 0;
            state.Screen.ComicPage = 0;

            // RefreshBg 只在切到另一个有效正背景时重置该背景 UIEffect。
            if (backgroundChanged)
            {
                state.Screen.Blur = false;
                state.Screen.ColorEffect = 0;
                state.CurrentBg = bg;
            }

            // BlackBg/BlackBg2 在当前句 audio!=0 时会先停止现有音乐，
            // 当前句自己的 BGM 稍后由 PlayAudio 重新设置。
            if (talk.audio != 0)
            {
                state.Screen.BgmAudioId = 0;
            }

            // -1 保留上一有效背景，-2/0/无效正数不会被 TryRefreshBg 接纳。
            return true;
        }

        private static void ClearRoles(PlaybackState state)
        {
            state.Roles.Clear();
            state.RoleOrder.Clear();
            state.Slots.Clear();
            state.TalkingPos = TalkAxis.Right;
            // roleCloths/ClothCache、手机开关和 tmpRoleIds 是播放器独立字段，
            // ClearRoles 不会清除；4008 后续会安全忽略已经不存在的临时人物。
        }

        private static void EnsureUseCloth(
            PlaybackState state,
            Dictionary<int, BgCfg> backgroundCfgs)
        {
            // PreviewTalkView.RefreshCloth 只在 useCloth 为空时初始化一次；后续
            // 即便换背景也不会替换。中途预览必须恢复这个独立于角色的缓存。
            if (state.UseCloth != null && state.UseCloth.Count > 0) return;
            if (state.CurrentBg > 0 && backgroundCfgs != null
                && backgroundCfgs.TryGetValue(state.CurrentBg, out BgCfg bg)
                && bg?.cloth != null && bg.cloth.Count > 0)
            {
                state.UseCloth = new List<int>(bg.cloth);
            }
            else
            {
                state.UseCloth = new List<int> { 0 };
            }
        }

        private static void ApplyBackgroundEffect(
            TalkCfg talk,
            TalkPreviewScreenState screen)
        {
            if (talk?.screenEffect == null || talk.screenEffect.Count == 0) return;
            switch ((int)talk.screenEffect[0])
            {
                case 4002:
                    screen.Blur = true;
                    break;
                case 4003:
                    screen.Blur = false;
                    screen.ColorEffect = 0;
                    break;
                case 4009:
                    screen.ColorEffect = 4009;
                    break;
                case 4010:
                    screen.ColorEffect = 4010;
                    break;
            }
        }

        private static void ApplyTalk(
            TalkCfg talk,
            PlaybackState state,
            ISet<int> validPersonIds)
        {
            var existedBefore = new HashSet<int>(state.Roles.Keys);
            var talkingRoleIds = new List<int>();
            bool somebodyTalking = IsSomebodyTalking(talk.roleIds);
            if (!somebodyTalking)
            {
                state.TalkingPos = TalkAxis.Right;
            }
            else if (talk.roleIds != null)
            {
                foreach (int personId in talk.roleIds)
                {
                    SimRole role = GetOrCreateRole(state, personId, validPersonIds);
                    if (role != null) talkingRoleIds.Add(personId);
                }
            }

            var actions = new List<List<float>>();
            if (talk.roles != null && talk.roles.Count > 0)
            {
                var sorted = new List<List<float>>(talk.roles);
                sorted.Sort(CompareLikePlayer);
                var autoEnterAdded = new HashSet<int>();

                foreach (List<float> action in sorted)
                {
                    if (!TryReadAction(action, out int personId, out int code)) continue;
                    int type = GetActionType(code);
                    if (type == 5 && code == 5001) continue;
                    if (type == 2 && !state.Roles.ContainsKey(personId)) continue;

                    SimRole role = GetOrCreateRole(state, personId, validPersonIds);
                    if (role == null) continue;
                    bool newlySeenThisTalk = !existedBefore.Contains(personId);

                    if (type == 1)
                    {
                        if (action.Count > 3)
                            role.TargetAxis = (TalkAxis)(int)action[3];
                        // 对应 ShowCurTxt.list3：显式进场已负责定位，后续同人物
                        // 3006/3014/3012/3007 只预设状态，不再额外补无参1001。
                        autoEnterAdded.Add(personId);
                        actions.Add(action);
                        continue;
                    }

                    if (newlySeenThisTalk && ApplyNewRoleSpecialAction(
                        role, action, code, state.ClothCache))
                    {
                        if (autoEnterAdded.Add(personId))
                            actions.Add(new List<float> { personId, 1001f });
                        continue;
                    }

                    actions.Add(action);
                }
            }
            else if (talk.roleIds != null)
            {
                foreach (int personId in talk.roleIds)
                {
                    if (personId != -1 && !existedBefore.Contains(personId)
                        && state.Roles.ContainsKey(personId))
                    {
                        actions.Add(new List<float> { personId, 1001f });
                    }
                }
            }

            // PlayScreenEffect 在 PlayRoleEffect 之前运行。4005 会预设表情，
            // 4007/4008 会改变手机人物并增删动作；必须放在动作执行前模拟。
            ApplyPreRoleScreenEffect(
                talk, state, validPersonIds, talkingRoleIds, actions);

            var removeRoles = new List<int>();
            foreach (List<float> action in actions)
            {
                if (!TryReadAction(action, out int personId, out int code)) continue;
                if (!state.Roles.TryGetValue(personId, out SimRole role)) continue;
                if (role.PendingPreFlip)
                {
                    // 原播放器在预处理阶段只保存一个 targetFlip=true；多个 3007
                    // 会折叠成首个播放动作上的一次翻转，而不是按次数奇偶抵消。
                    role.PendingPreFlip = false;
                    role.Flipped = !role.Flipped;
                    role.FlipCount++;
                }
                ApplyAction(role, action, code, state);
                if (code == 2001 || code == 2002) removeRoles.Add(personId);
            }

            // RefreshTalkingRole 在退场回收前读取 talkingRoles[0].targetAxis。
            if (somebodyTalking && talkingRoleIds.Count > 0
                && state.Roles.TryGetValue(talkingRoleIds[0], out SimRole firstTalking))
            {
                state.TalkingPos = firstTalking.TargetAxis;
            }

            foreach (int personId in removeRoles)
            {
                if (!state.Roles.Remove(personId)) continue;
                state.RoleOrder.Remove(personId);
                if (talkingRoleIds.Contains(personId)) state.TalkingPos = TalkAxis.Right;
            }
        }

        private static void ApplyPreRoleScreenEffect(
            TalkCfg talk,
            PlaybackState state,
            ISet<int> validPersonIds,
            List<int> talkingRoleIds,
            List<List<float>> actions)
        {
            if (talk?.screenEffect == null || talk.screenEffect.Count == 0) return;
            int code = (int)talk.screenEffect[0];
            switch (code)
            {
                case 4005:
                    if (talk.screenEffect.Count > 1)
                    {
                        int pose = (int)talk.screenEffect[1];
                        foreach (SimRole role in state.Roles.Values)
                        {
                            role.Pose = pose;
                            role.PoseSet = true;
                        }
                    }
                    break;

                case 4007:
                    OpenPhone(talk, state, validPersonIds, actions);
                    break;

                case 4008:
                    ClosePhone(state, talkingRoleIds);
                    break;

                case 4011:
                    if (talk.screenEffect.Count > 1 && talk.screenEffect[1] != 0f)
                    {
                        state.Screen.WakeEffectActive = true;
                        state.Screen.WakeEffectValue = talk.screenEffect[1];
                    }
                    else
                    {
                        state.Screen.WakeEffectActive = false;
                        state.Screen.WakeEffectValue = 0f;
                    }
                    break;

                case 4015:
                    if (talk.screenEffect.Count > 1)
                    {
                        state.Screen.Overlay = TalkPreviewOverlayKind.Cg;
                        state.Screen.OverlayId = (int)talk.screenEffect[1];
                        state.Screen.ComicPage = 0;
                    }
                    break;

                case 4016:
                    if (talk.screenEffect.Count > 2)
                    {
                        state.Screen.Overlay = TalkPreviewOverlayKind.Comic;
                        state.Screen.OverlayId = (int)talk.screenEffect[1];
                        state.Screen.ComicPage = (int)talk.screenEffect[2];
                    }
                    break;

                case 4017:
                    state.Screen.Overlay = TalkPreviewOverlayKind.None;
                    state.Screen.OverlayId = 0;
                    state.Screen.ComicPage = 0;
                    break;

                case 4019:
                    if (talk.screenEffect.Count > 1)
                    {
                        state.Screen.Overlay = TalkPreviewOverlayKind.MiniCg;
                        state.Screen.OverlayId = (int)talk.screenEffect[1];
                        state.Screen.ComicPage = 0;
                    }
                    break;
            }
        }

        private static void OpenPhone(
            TalkCfg talk,
            PlaybackState state,
            ISet<int> validPersonIds,
            List<List<float>> actions)
        {
            // PreviewTalkView 的 4007 只有当前句存在说话人物时才打开。
            if (talk.roleIds == null || talk.roleIds.Count == 0) return;

            state.Screen.PhoneOpen = true;
            if (talk.screenEffect.Count > 1)
                state.Screen.PhoneBackgroundId = (int)talk.screenEffect[1];

            MarkPhoneRoles(talk.roleIds, state, PhoneState.AlreadyIn);
            MarkPhoneRoles(talk.highlights, state, PhoneState.AlreadyIn);

            TalkAxis phoneAxis = TalkAxis.Right;
            int anchorId = talk.highlights != null && talk.highlights.Count > 0
                ? talk.highlights[0]
                : 0;
            if (state.Roles.TryGetValue(anchorId, out SimRole anchor))
            {
                TalkAxis axis = anchor.TargetAxis == TalkAxis.Left
                    || anchor.TargetAxis == TalkAxis.Right
                        ? anchor.TargetAxis
                        : anchor.Axis;
                phoneAxis = axis == TalkAxis.Left ? TalkAxis.Right : TalkAxis.Left;
            }
            state.Screen.PhoneAxis = phoneAxis;

            for (int i = 2; i < talk.screenEffect.Count; i++)
            {
                int personId = (int)talk.screenEffect[i];
                SimRole role = GetOrCreateRole(state, personId, validPersonIds);
                if (role == null) continue;
                role.PhoneState = PhoneState.NewIn;
                // 原版直接把 axis 改为 Init，不从旧 posRoles 移除；随后补1001。
                role.Axis = TalkAxis.Init;
                role.TargetAxis = phoneAxis;
                state.Screen.PhoneTemporaryRoleIds.Add(personId);
                actions.Add(new List<float> { personId, 1001f });
            }
        }

        private static void ClosePhone(
            PlaybackState state,
            List<int> talkingRoleIds)
        {
            if (!state.Screen.PhoneOpen) return;

            state.Screen.PhoneOpen = false;
            foreach (int personId in state.Screen.PhoneTemporaryRoleIds)
            {
                if (!state.Roles.Remove(personId)) continue;
                state.RoleOrder.Remove(personId);
                // 原版直接回收并 Remove，不清 posRoles；保留幽灵槽以匹配后续站位。
                if (talkingRoleIds.Contains(personId)) state.TalkingPos = TalkAxis.Right;
            }
            state.Screen.PhoneTemporaryRoleIds.Clear();
            foreach (SimRole role in state.Roles.Values)
                role.PhoneState = PhoneState.None;
        }

        private static void MarkPhoneRoles(
            List<int> ids,
            PlaybackState state,
            PhoneState phoneState)
        {
            if (ids == null) return;
            foreach (int personId in ids)
            {
                if (state.Roles.TryGetValue(personId, out SimRole role))
                    role.PhoneState = phoneState;
            }
        }

        private static void ApplyAudio(
            TalkCfg talk,
            TalkPreviewScreenState screen,
            Dictionary<int, AudioCfg> audioCfgs)
        {
            if (talk == null) return;
            // PreviewTalkView.playEvtGroupBgm 默认 false 且没有置 true 的入口；
            // 中途预览不能凭 audio=0 合成一首随机事件组 BGM。
            screen.GroupBgmConsumed = true;
            if (talk.audio <= 0) return;

            AudioCfg audio = null;
            if (audioCfgs != null) audioCfgs.TryGetValue(talk.audio, out audio);
            if (audio == null && Cfg.AudioCfgMap != null)
                Cfg.AudioCfgMap.TryGetValue(talk.audio, out audio);
            if (audio != null && audio.type == 1)
            {
                screen.BgmAudioId = talk.audio;
            }
        }

        private static int CompareLikePlayer(List<float> a, List<float> b)
        {
            if (!TryReadAction(a, out int personA, out int codeA)
                || !TryReadAction(b, out int personB, out int codeB)) return 0;
            if (personA != personB) return 0;
            int typeA = GetActionType(codeA);
            int typeB = GetActionType(codeB);
            if (typeA == 1) return -1;
            return typeB == 1 ? 1 : 0;
        }

        private static bool ApplyNewRoleSpecialAction(
            SimRole role,
            List<float> action,
            int code,
            Dictionary<int, int> clothCache)
        {
            switch (code)
            {
                case 3007:
                    role.PendingPreFlip = true;
                    return true;
                case 3006:
                    if (action.Count > 2)
                    {
                        role.Cloth = (int)action[2];
                        clothCache[role.PersonId] = role.Cloth;
                    }
                    return true;
                case 3012:
                    role.Shadow = 1;
                    return true;
                case 3013:
                    role.Shadow = 0;
                    return true;
                case 3014:
                    if (action.Count > 2) role.Hair = (int)action[2];
                    return true;
                default:
                    return false;
            }
        }

        private static SimRole GetOrCreateRole(
            PlaybackState state,
            int personId,
            ISet<int> validPersonIds)
        {
            if (personId < 0) return null;
            if (validPersonIds != null && !validPersonIds.Contains(personId)) return null;
            if (state.Roles.TryGetValue(personId, out SimRole role)) return role;

            role = new SimRole
            {
                PersonId = personId,
                TargetAxis = state.TalkingPos == TalkAxis.Mid
                    ? TalkAxis.Left
                    : (TalkAxis)((int)state.TalkingPos % 2 + 1),
            };
            if (state.ClothCache.TryGetValue(personId, out int cloth)) role.Cloth = cloth;
            state.Roles.Add(personId, role);
            state.RoleOrder.Add(personId);
            return role;
        }

        private static void ApplyAction(
            SimRole role,
            List<float> action,
            int code,
            PlaybackState state)
        {
            switch (code)
            {
                case 1001:
                {
                    // 省略层级参数时播放器默认 1；显式 0 才保留 targetLayer。
                    int layer = action.Count > 2 ? (int)action[2] : 1;
                    if (layer != 0) role.TargetLayer = layer;
                    // 1001 的 HelpCheckRoleAction 仅在 parms != null 时处理方位。
                    // 自动补出的 [id,1001] 会传 null，必须保留 GetRoleData 设置的
                    // 默认 targetAxis，不能错误改回当前 Axis(Init)。
                    if (action.Count > 2)
                    {
                        TalkAxis requested = action.Count > 3
                            ? (TalkAxis)(int)action[3]
                            : TalkAxis.None;
                        role.TargetAxis = requested != TalkAxis.None ? requested : role.Axis;
                    }
                    break;
                }
                case 1002:
                case 1003:
                {
                    int layer = action.Count > 2 ? (int)action[2] : 1;
                    if (layer != 0) role.TargetLayer = layer;
                    TalkAxis requested = action.Count > 3
                        ? (TalkAxis)(int)action[3]
                        : TalkAxis.None;
                    role.TargetAxis = requested != TalkAxis.None ? requested : role.Axis;
                    break;
                }
                case 2001:
                    if (action.Count > 2 && (int)action[2] != 0
                        && (TalkAxis)(int)action[2] != role.Axis)
                        role.TargetAxis = (TalkAxis)(int)action[2];
                    else
                        role.TargetAxis = TalkAxis.Init;
                    break;
                case 2002:
                    role.TargetAxis = TalkAxis.Init;
                    break;
                case 3000:
                    if (action.Count > 2)
                    {
                        role.Pose = (int)action[2];
                        role.PoseSet = true;
                    }
                    break;
                case 3005:
                case 3007:
                    role.Flipped = !role.Flipped;
                    role.FlipCount++;
                    break;
                case 3006:
                    if (action.Count > 2)
                    {
                        role.Cloth = (int)action[2];
                        state.ClothCache[role.PersonId] = role.Cloth;
                    }
                    break;
                case 3014:
                    if (action.Count > 2) role.Hair = (int)action[2];
                    break;
                case 3012:
                    role.Shadow = 1;
                    break;
                case 3013:
                    role.Shadow = 0;
                    break;
            }

            bool axisOrLayerChanged = role.Axis != role.TargetAxis
                || role.Layer != role.TargetLayer;
            if (axisOrLayerChanged)
            {
                UpdateSlot(role, state, GetActionType(code) == 2);
                role.OffsetX = 0f;
                role.OffsetY = 0f;

                float baseScale = role.TargetLayer == -1 ? 2f : 1f;
                bool scaleTweenRuns = Math.Abs(role.Scale - baseScale) > 0.0001f;
                role.Scale = baseScale;
                role.ScaleZero = false;
                role.ScaleCount = 0;
                // DOScale(Vector3.one * baseScale) 同时把根节点 x 符号恢复为正；
                // 只有实际需要缩放 tween 时才会发生。
                if (scaleTweenRuns)
                {
                    role.Flipped = false;
                    role.FlipCount = 0;
                }

                role.Axis = role.TargetAxis;
                role.Layer = role.TargetLayer;
            }

            if (code == 3004 && action.Count > 2)
                role.OffsetX += action[2];
            else if (code == 3008 && action.Count > 2)
                role.OffsetY += action[2];
            else if (code == 3003)
            {
                float multiplier = action.Count <= 2 || action[2] != 0f ? 1.1f : 0f;
                float oldScale = role.Scale;
                role.Scale *= multiplier;
                // PlayRoleEffect 对变化后的 targetScale 调用 DOScale(float)，目标
                // Vector3 的 x 为正，因此会清掉此前根节点 DOScaleX 形成的翻转。
                // 若缩放值未变（如 0×1.1 仍为0），真实代码不会启动 tween。
                if (Math.Abs(oldScale - role.Scale) > 0.0001f)
                {
                    role.Flipped = false;
                    role.FlipCount = 0;
                }
                if (multiplier == 0f)
                {
                    role.ScaleZero = true;
                }
                else if (!role.ScaleZero)
                {
                    role.ScaleCount++;
                }
            }

            // CheckRoleAction 会在首个动作时把 -1 服装解析为持久 cloth cache；
            // 无显式缓存时背景默认服装依赖运行时 bgCfg，快照保留 -1 交给预览器解析。
            if (role.Cloth < 0 && state.ClothCache.TryGetValue(role.PersonId, out int cloth))
                role.Cloth = cloth;
        }

        private static void UpdateSlot(SimRole role, PlaybackState state, bool isOut)
        {
            if (IsSceneAxis(role.Axis)
                && state.Slots.TryGetValue(role.Axis, out List<int> oldSlots))
            {
                int oldIndex = oldSlots.IndexOf(role.PersonId);
                if (oldIndex >= 0) oldSlots[oldIndex] = -1;
            }
            role.SlotIndex = -1;

            if (role.TargetAxis == TalkAxis.Init || !IsSceneAxis(role.TargetAxis))
                return;
            // 原 UpdatePosRoles 的出场快速返回只覆盖 Left/Right；指定从 Mid
            // 退场时仍会把 ID 写入 Mid 槽，人物随后从 roles 删除但 ghost 槽保留。
            if (isOut && (role.TargetAxis == TalkAxis.Left
                || role.TargetAxis == TalkAxis.Right))
                return;

            if (!state.Slots.TryGetValue(role.TargetAxis, out List<int> targetSlots))
            {
                targetSlots = new List<int>();
                state.Slots.Add(role.TargetAxis, targetSlots);
            }
            int slot = targetSlots.IndexOf(-1);
            if (slot < 0)
            {
                slot = targetSlots.Count;
                targetSlots.Add(role.PersonId);
            }
            else
            {
                targetSlots[slot] = role.PersonId;
            }
            role.SlotIndex = slot;
        }

        private static bool IsSomebodyTalking(List<int> roleIds)
        {
            if (roleIds == null || roleIds.Count == 0) return false;
            return roleIds.Count != 1 || roleIds[0] != -1;
        }

        private static bool IsSceneAxis(TalkAxis axis)
        {
            return axis == TalkAxis.Left || axis == TalkAxis.Right || axis == TalkAxis.Mid;
        }

        private static int GetActionType(int code)
        {
            try
            {
                if (Cfg.TalkAnimeCfgMap != null
                    && Cfg.TalkAnimeCfgMap.TryGetValue(code, out TalkAnimeCfg cfg))
                    return cfg.type;
            }
            catch
            {
                // 编辑器配置尚未加载完整时回退硬编码，不让预览状态解析中断。
            }

            if (code >= 1001 && code <= 1003) return 1;
            if (code == 2001 || code == 2002) return 2;
            if (code >= 3000 && code <= 3014) return 3;
            if (code >= 4001 && code <= 4019) return 4;
            if (code == 5001 || code == 5002) return 5;
            return 0;
        }

        private static bool TryReadAction(List<float> action, out int personId, out int code)
        {
            personId = 0;
            code = 0;
            if (action == null || action.Count < 2) return false;
            personId = (int)action[0];
            code = (int)action[1];
            return personId >= 0; // 0 是主角；-1 是旁白占位。
        }

        private static Dictionary<int, TalkPreviewRoleState> CloneRoles(
            Dictionary<int, SimRole> source)
        {
            var result = new Dictionary<int, TalkPreviewRoleState>(source.Count);
            foreach (KeyValuePair<int, SimRole> role in source)
                result[role.Key] = role.Value.Snapshot();
            return result;
        }

        private static Dictionary<TalkAxis, List<int>> CloneSlots(
            Dictionary<TalkAxis, List<int>> source)
        {
            var result = new Dictionary<TalkAxis, List<int>>();
            foreach (KeyValuePair<TalkAxis, List<int>> slots in source)
                result[slots.Key] = new List<int>(slots.Value);
            return result;
        }

        private static bool Contains(List<int> list, int value)
        {
            return list != null && list.Contains(value);
        }
    }
}
