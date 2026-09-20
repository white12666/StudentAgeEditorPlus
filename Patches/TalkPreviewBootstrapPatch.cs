using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Coffee.UIEffects;
using Config;
using DG.Tweening;
using GenUI.Common;
using HarmonyLib;
using Sdk;
using UnityEngine;
using View.Evt;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// “预览本句”的执行前快照。它不把历史动作塞进当前 Talk，而是在
    /// PreviewTalkView 第一次 ShowCurTxt 前直接建立播放器内部状态；当前 Talk 的
    /// 原始 actions/screenEffect/audio 随后仍由游戏按原顺序、原 delay 执行。
    /// </summary>
    internal sealed class TalkPreviewBootstrapContext
    {
        public int StartTalkId;
        public TalkPreviewSnapshot Snapshot;
        public bool BackgroundPrimed;
        public bool Consumed;
        public bool ResumeIssued;
        public int Generation = 1;
        public bool IsInjectingRoles;
    }

    internal static class TalkPreviewBootstrapRuntime
    {
        private static readonly ConditionalWeakTable<PreviewTalkView, TalkPreviewBootstrapContext>
            Contexts = new();

        internal static void Register(
            PreviewTalkView view,
            TalkPreviewBootstrapContext context)
        {
            if (view == null) return;
            Contexts.Remove(view);
            if (context != null) Contexts.Add(view, context);
        }

        internal static void Unregister(PreviewTalkView view)
        {
            if (view != null) Contexts.Remove(view);
        }

        internal static bool TryGet(
            PreviewTalkView view,
            out TalkPreviewBootstrapContext context)
        {
            context = null;
            return view != null && Contexts.TryGetValue(view, out context);
        }

        internal static void NotifyRefresh(
            PreviewTalkView view,
            int talkId)
        {
            if (!TryGet(view, out TalkPreviewBootstrapContext context)) return;
            // 异步 CG/漫画恢复尚未回调时，只要发生任何新的 RefreshTalk，
            // 旧回调都必须失效（即使刷新的是同一个 ID）。
            if (context.Consumed)
                unchecked { context.Generation++; }
        }

        internal static bool WasCgActiveBeforeStart(PreviewTalkView view)
        {
            if (!TryGet(view, out TalkPreviewBootstrapContext context)
                || context.Consumed || context.Snapshot?.BeforeShowCurrentScreen == null)
                return false;
            TalkPreviewOverlayKind overlay =
                context.Snapshot.BeforeShowCurrentScreen.Overlay;
            return overlay == TalkPreviewOverlayKind.Cg
                || overlay == TalkPreviewOverlayKind.MiniCg;
        }

        /// <summary>
        /// 在首句 RefreshTalk 前预置前文有效背景。这样原方法仍能根据当前 bg
        /// 决定是否换景/黑场，bg=0/-1/-2 也能继承真实背景。
        /// </summary>
        internal static void PrimeBackground(
            PreviewTalkView view,
            int talkId,
            bool firstOpen)
        {
            if (!firstOpen || !TryGet(view, out TalkPreviewBootstrapContext context)
                || context.BackgroundPrimed || context.StartTalkId != talkId) return;
            context.BackgroundPrimed = true;

            // useCloth 是 PreviewTalkView 只初始化一次的背景服装缓存，必须在
            // RefreshBg/GetRoleUseCloth 之前恢复，不能让新 View 按当前背景重算。
            if (context.Snapshot?.BeforeUseCloth != null)
                view.useCloth = new List<int>(context.Snapshot.BeforeUseCloth);

            int backgroundId = context.Snapshot?.IncomingBackgroundId ?? -1;
            if (backgroundId <= 0) return;

            try
            {
                var t = Traverse.Create(view);
                var backgrounds = t.Field("bgCfgMap").GetValue<Dictionary<int, BgCfg>>();
                if (backgrounds == null || !backgrounds.ContainsKey(backgroundId)) return;
                t.Method("RefreshBg", new[] { typeof(int), typeof(float) })
                    .GetValue(backgroundId, 0f);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(
                    $"[EvtTalkBootstrap.Background] 背景{backgroundId}预置失败，将由当前句自行处理: {e.Message}");
            }
        }

        /// <summary>
        /// 返回 true = 继续本次原 ShowCurTxt；false = CG/漫画异步初始化完成后再重入。
        /// </summary>
        internal static bool BeforeFirstShow(PreviewTalkView view)
        {
            if (!TryGet(view, out TalkPreviewBootstrapContext context)
                || context.Consumed) return true;

            TalkCfg currentCfg = Traverse.Create(view).Field("cfg").GetValue<TalkCfg>();
            if (currentCfg == null || currentCfg.id != context.StartTalkId)
            {
                Plugin.Log.LogWarning(
                    "[EvtTalkBootstrap] 首次 ShowCurTxt 已不是请求的起始 Talk，取消上下文注入。");
                unchecked { context.Generation++; }
                context.Consumed = true;
                return true;
            }

            context.Consumed = true;
            int resumeGeneration = context.Generation;

            try
            {
                InjectPlayerState(view, context);
                TalkPreviewScreenState screen =
                    context.Snapshot?.BeforeShowCurrentScreen ?? new TalkPreviewScreenState();
                ApplyBackgroundFilter(view, screen);
                ApplyWakeEffect(view, screen);
                ApplyPhone(view, screen);
                ApplyMusic(view, screen);

                Action resume = () =>
                {
                    try
                    {
                        if (view.viewState != ViewState.Opened
                            || !TryGet(view, out TalkPreviewBootstrapContext active)
                            || !ReferenceEquals(active, context)
                            || active.Generation != resumeGeneration
                            || active.ResumeIssued) return;
                        TalkCfg cfg = Traverse.Create(view).Field("cfg").GetValue<TalkCfg>();
                        if (cfg == null || cfg.id != context.StartTalkId) return;
                        active.ResumeIssued = true;
                        Traverse.Create(view).Method("ShowCurTxt").GetValue();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"[EvtTalkBootstrap.Resume] {e}");
                    }
                };
                // 即使 cgPanel 已存在，原 ShowCG 也会在写入新 parms 之前同步调
                // callback；延后一帧可避免“恢复旧CG”返回后覆盖当前句的新CG。
                Action continueShow = () =>
                    Singleton<TimerMgr>.Ins.FrameDelay(resume, 1);

                switch (screen.Overlay)
                {
                    case TalkPreviewOverlayKind.Cg:
                        Traverse.Create(view)
                            .Method("ShowCG", new[] { typeof(int), typeof(Action) })
                            .GetValue(screen.OverlayId, continueShow);
                        return false;
                    case TalkPreviewOverlayKind.Comic:
                        Traverse.Create(view)
                            .Method("ShowComic", new[] { typeof(int), typeof(int), typeof(Action) })
                            .GetValue(screen.OverlayId, screen.ComicPage, continueShow);
                        return false;
                    case TalkPreviewOverlayKind.MiniCg:
                        // 缺号时不显示 MiniCG，但状态恢复必须继续走下去。
                        if (!ShowMiniCg(view, screen.OverlayId, continueShow))
                            continueShow();
                        return false;
                    default:
                        return true;
                }
            }
            catch (Exception e)
            {
                // 快照恢复失败时清掉可能已建了一半的 Cell，再放行游戏原方法；
                // 不能让增强功能阻断预览，也不能让半注入状态与当前动作混用。
                Plugin.Log.LogError($"[EvtTalkBootstrap] {e}");
                ResetFailedBootstrap(view);
                return true;
            }
        }

        private static void ResetFailedBootstrap(PreviewTalkView view)
        {
            try
            {
                var t = Traverse.Create(view);
                t.Method("ClearRoles").GetValue();
                t.Field("isPhoneing").SetValue(false);
                t.Field("tmpRoleIds").GetValue<List<int>>()?.Clear();
                view.icon_bg_3.image.fillAmount = 0f;
                view.icon_bg_3.gameObject.SetActive(false);
                t.Method("HideCGComic").GetValue();
            }
            catch (Exception cleanup)
            {
                Plugin.Log.LogError($"[EvtTalkBootstrap.Cleanup] {cleanup}");
            }
        }

        internal static bool TryRenderInjectedRole(
            PreviewTalkView view,
            Cell_NewTalkRoleItemUI cell)
        {
            if (cell == null || !TryGet(view, out TalkPreviewBootstrapContext context)
                || !context.IsInjectingRoles || cell.data == null
                || context.Snapshot?.BeforeCurrent == null) return false;

            int personId = (int)cell.data;
            if (!context.Snapshot.BeforeCurrent.TryGetValue(
                    personId, out TalkPreviewRoleState state)) return false;

            try
            {
                var t = Traverse.Create(view);
                var persons = t.Field("personCfgMap")
                    .GetValue<Dictionary<int, PersonCfg>>();
                if (persons == null || !persons.TryGetValue(personId, out PersonCfg person))
                    return false;

                int cloth = state.Cloth;
                if (cloth < 0)
                {
                    cloth = t.Method("GetRoleUseCloth", new[] { typeof(int) })
                        .GetValue<int>(personId);
                }
                TalkCfg current = t.Field("cfg").GetValue<TalkCfg>();
                bool talking = current?.roleIds != null
                    && current.roleIds.Contains(personId);
                int colorId = talking ? (state.Shadow == 1 ? 1 : 0) : 6;
                // 动态播放器的 NewTalkRoleData.targetPose 默认就是0，且每次
                // PlayRoleEffect 都会应用；这里恢复最终值而不是静态小舞台的-1哨兵。
                int pose = state.Pose;
                int layerOrder = t.Field("layerOrder").GetValue<int>();
                int gradeState = t.Field("gradeState").GetValue<int>();
                GenderDefine gender = t.Field("gender").GetValue<GenderDefine>();
                var faces = t.Field("faceCfgMap")
                    .GetValue<Dictionary<int, ModFaceCfg>>();

                PreviewRoleRenderer.Render(
                    person, cell, layerOrder + 1, colorId, 1f, cloth,
                    pose, false, state.Hair, gradeState, gender, faces);
                cell.img_bubble_face.gameObject.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(
                    $"[EvtTalkBootstrap.RoleRender] 人物{personId}恢复失败，回退原渲染: {e.Message}");
                return false;
            }
        }

        private static void InjectPlayerState(
            PreviewTalkView view,
            TalkPreviewBootstrapContext context)
        {
            TalkPreviewSnapshot snapshot = context.Snapshot;
            if (snapshot == null || snapshot.BeforeCurrent == null) return;

            var t = Traverse.Create(view);
            var roles = t.Field("roles")
                .GetValue<Dictionary<int, NewTalkRoleData>>();
            if (roles == null || roles.Count != 0)
            {
                if (roles != null && roles.Count > 0)
                    Plugin.Log.LogWarning("[EvtTalkBootstrap] 首次 ShowCurTxt 前 roles 已非空，跳过人物注入以避免重复 Cell。");
                return;
            }

            var cloths = t.Field("roleCloths").GetValue<Dictionary<int, int>>();
            cloths?.Clear();
            if (cloths != null && snapshot.BeforeCloths != null)
            {
                foreach (KeyValuePair<int, int> entry in snapshot.BeforeCloths)
                    cloths[entry.Key] = entry.Value;
            }

            var slots = t.Field("posRoles")
                .GetValue<Dictionary<TalkAxis, List<int>>>();
            slots?.Clear();
            if (slots != null && snapshot.BeforeSlots != null)
            {
                foreach (KeyValuePair<TalkAxis, List<int>> entry in snapshot.BeforeSlots)
                    slots[entry.Key] = new List<int>(entry.Value);
            }
            t.Field("talkingPos").SetValue(snapshot.BeforeTalkingPos);

            var order = new List<int>();
            if (snapshot.BeforeRoleOrder != null)
                order.AddRange(snapshot.BeforeRoleOrder);
            foreach (int personId in snapshot.BeforeCurrent.Keys)
            {
                if (!order.Contains(personId)) order.Add(personId);
            }

            context.IsInjectingRoles = true;
            try
            {
                foreach (int personId in order)
                {
                    if (!snapshot.BeforeCurrent.TryGetValue(
                            personId, out TalkPreviewRoleState state)) continue;
                    NewTalkRoleData role = t
                        .Method("GetRoleData", new[] { typeof(int), typeof(bool) })
                        .GetValue<NewTalkRoleData>(personId, false);
                    if (role == null) continue;

                    role.axis = state.Axis;
                    role.targetAxis = state.TargetAxis;
                    role.layer = state.Layer;
                    role.targetLayer = state.TargetLayer;
                    role.targetCloth = state.Cloth;
                    role.targetHair = state.Hair;
                    role.targetPose = state.Pose;
                    role.isShadow = state.Shadow == 1;
                    role.targetShadowChange = false;
                    role.targetScale = state.Scale;
                    role.targetAlpha = 1f;
                    role.targetFlip = false;
                    role.targetEmoji = -1;
                    role.phoneState = state.PhoneState;
                    role.delay = 0f;
                    role.moveTime = -1f;

                    t.Method("BindRoleDataWithCell", new[] { typeof(NewTalkRoleData) })
                        .GetValue(role);
                    SetRoleTransform(
                        view, role, state,
                        snapshot.BeforeShowCurrentScreen?.PhoneOpen == true);
                }
            }
            finally
            {
                context.IsInjectingRoles = false;
            }
        }

        private static void SetRoleTransform(
            PreviewTalkView view,
            NewTalkRoleData role,
            TalkPreviewRoleState state,
            bool phoneOpen)
        {
            if (role?.cell == null) return;
            var t = Traverse.Create(view);
            var posX = t.Field("posXList")
                .GetValue<Dictionary<TalkAxis, float>>();

            Vector3 target = Vector3.zero;
            if (state.SlotIndex >= 0 && posX != null
                && posX.TryGetValue(state.Axis, out float baseX))
            {
                int slot = state.SlotIndex;
                float slotX = slot == 0
                    ? baseX
                    : (slot % 2 != 0
                        ? baseX - ((slot + 1) / 2) * 300f
                        : baseX + ((slot + 1) / 2) * 300f);
                target.x = slotX + state.OffsetX;
                target.y = (state.TargetLayer == -1 ? -850f : 0f) + state.OffsetY;
            }
            target.z = role.cell.transform.anchoredPosition3D.z;

            role.targetAnchoredPos = target;
            role.initAnchoredPos = target;
            role.hasSetInitPos = true;
            role.cell.transform.anchoredPosition3D = target;

            float scale = float.IsNaN(state.Scale) || float.IsInfinity(state.Scale)
                ? 1f
                : state.Scale;
            role.cell.transform.localScale = new Vector3(
                state.Flipped ? -scale : scale, scale, scale);
            role.cell.canvasgroup_role.alpha = 1f;
            role.cell.l2d_role.SetAlpha(1f);
            role.cell.gameObject.SetActive(
                !phoneOpen || state.PhoneState != PhoneState.None);
        }

        private static void ApplyBackgroundFilter(
            PreviewTalkView view,
            TalkPreviewScreenState screen)
        {
            try
            {
                var t = Traverse.Create(view);
                UISprite[] backgrounds = t.Field("bgs").GetValue<UISprite[]>();
                int index = t.Field("curBgIdx").GetValue<int>();
                if (backgrounds == null || index < 0 || index >= backgrounds.Length
                    || backgrounds[index]?.gameObject == null) return;
                UIEffect effect = backgrounds[index].gameObject.GetComponent<UIEffect>();
                if (effect == null) return;

                effect.blurFactor = screen.Blur ? 1f : 0f;
                if (screen.ColorEffect == 4009)
                {
                    effect.effectMode = EffectMode.Sepia;
                    effect.effectFactor = 1f;
                }
                else if (screen.ColorEffect == 4010)
                {
                    effect.effectMode = EffectMode.Nega;
                    effect.effectFactor = 1f;
                }
                else
                {
                    effect.effectFactor = 0f;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[EvtTalkBootstrap.Filter] {e.Message}");
            }
        }

        private static void ApplyWakeEffect(
            PreviewTalkView view,
            TalkPreviewScreenState screen)
        {
            try
            {
                if (!screen.WakeEffectActive)
                {
                    view.img_screen_effect.gameObject.SetActive(false);
                    view.img_screen_effect.RecycleMat();
                    return;
                }

                view.img_screen_effect.color = Color.black;
                view.img_screen_effect.gameObject.SetActive(true);
                Material material = view.img_screen_effect.material;
                if (material == null
                    || material.shader.name != Cfg.ShaderCfgMap[7].url)
                    material = view.img_screen_effect.InitMat(7);
                material.SetFloat(
                    Cfg.ShaderCfgMap[1008].name,
                    1f - screen.WakeEffectValue);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[EvtTalkBootstrap.WakeEffect] {e.Message}");
            }
        }

        private static void ApplyPhone(
            PreviewTalkView view,
            TalkPreviewScreenState screen)
        {
            var t = Traverse.Create(view);
            t.Field("isPhoneing").SetValue(screen.PhoneOpen);
            var tempIds = t.Field("tmpRoleIds").GetValue<List<int>>();
            tempIds?.Clear();
            if (tempIds != null && screen.PhoneTemporaryRoleIds != null)
                tempIds.AddRange(screen.PhoneTemporaryRoleIds);

            if (!screen.PhoneOpen)
            {
                view.icon_bg_3.image.fillAmount = 0f;
                view.icon_bg_3.gameObject.SetActive(false);
                return;
            }

            var backgrounds = t.Field("bgCfgMap").GetValue<Dictionary<int, BgCfg>>();
            if (backgrounds != null && backgrounds.TryGetValue(
                    screen.PhoneBackgroundId, out BgCfg bg))
                view.icon_bg_3.SetTextureUrl(bg.GetBgUrl());

            view.icon_bg_3.image.DOKill();
            view.icon_bg_3.image.fillOrigin =
                screen.PhoneAxis == TalkAxis.Left ? 0 : 1;
            view.icon_bg_3.image.fillAmount = 0.5f;
            view.icon_bg_3.image.color = Color.white;
            view.icon_bg_3.gameObject.SetActive(true);
            t.Method("RefreshPhoneMidLinePos").GetValue();
        }

        private static void ApplyMusic(
            PreviewTalkView view,
            TalkPreviewScreenState screen)
        {
            var t = Traverse.Create(view);
            var audioMap = t.Field("audioCfgMap")
                .GetValue<Dictionary<int, AudioCfg>>();
            if (screen.BgmAudioId > 0 && audioMap != null
                && audioMap.TryGetValue(screen.BgmAudioId, out AudioCfg audio)
                && audio.type == 1)
            {
                EditorAudioRuntime.PlayPreviewMusic(screen.BgmAudioId, audioMap);
                t.Field("playEvtGroupBgm").SetValue(false);
            }
            else if (screen.GroupBgmConsumed)
            {
                t.Field("playEvtGroupBgm").SetValue(false);
            }
        }

        /// <summary>
        /// 返回 false 表示该 MiniCG 编号不在预览用的 CG 表里，本次什么都没做。
        /// 必须在任何隐藏/激活副作用之前判定：CGView.Refresh 用的是裸索引器
        /// cgCfgMap[_id].GetImgUrl()（CGView.cs:70-72），缺号直接
        /// KeyNotFoundException，而此时 isShowingCG/group_role/root_cg 已经被改过，
        /// 预览就永久停在半开黑框态（L3-2；作者手填错 4019 参数同理）。
        /// </summary>
        internal static bool ShowMiniCg(
            PreviewTalkView view,
            int cgId,
            Action callback)
        {
            var t = Traverse.Create(view);
            var cgCfgMap = t.Field("cgCfgMap").GetValue<Dictionary<int, CGCfg>>();
            if (cgCfgMap == null || !cgCfgMap.ContainsKey(cgId))
            {
                Plugin.Log.LogWarning(
                    $"[PreviewTalk.4019] MiniCG 编号 {cgId} 不在预览配置表中，已跳过。");
                try
                {
                    StoryGraphToastRouter.Show(
                        "MiniCG 编号 " + cgId + " 不在当前预览的 CG 配置表中，已跳过该画面指令"
                        + "（新增的 CG 请先保存一次；手填的 4019 请核对编号）。");
                }
                catch { }
                return false;
            }
            t.Field("isShowingCG").SetValue(true);
            view.canvasgroup_cg.DOKill();
            view.canvasgroup_cg.alpha = 1f;
            view.group_role.gameObject.SetActive(false);
            view.group_talk.gameObject.SetActive(false);

            CGView panel = t.Field("cgPanel").GetValue<CGView>();
            Action<BaseView> initialized = _ =>
            {
                t.Field("talkTxt").SetValue(panel.txtex_talk_cg);
                t.Field("talkGroup").SetValue(panel.mask_talk_cg.gameObject);
                t.Field("nameTxt").SetValue(panel.txt_name);
                t.Field("nextObj").SetValue(panel.root_next);
                t.Field("nameGroup").SetValue(panel.txt_name.gameObject);
                callback?.Invoke();
            };

            GameObject talkGroup = t.Method("GetTalkGroup").GetValue<GameObject>();
            talkGroup?.SetActive(false);
            view.root_cg.gameObject.SetActive(true);
            if (panel == null)
            {
                panel = new CGView();
                t.Field("cgPanel").SetValue(panel);
                view.AddChildView(panel);
                panel.loadCompCallback = obj =>
                    t.Method("OnPanelLoadComp", new[] { typeof(BaseView) }).GetValue(obj);
                panel.initCompCallback = initialized;
            }
            else
            {
                initialized(panel);
            }

            panel.parms = new object[3] { cgId, cgCfgMap, 1 };
            panel.Show();
            return true;
        }
    }

    [HarmonyPatch(typeof(PreviewTalkView), "OnOpen")]
    internal static class PreviewTalkBootstrapOpenPatch
    {
        private static void Prefix(PreviewTalkView __instance)
        {
            try
            {
                EditorAudioRuntime.BeginPreview(__instance);
                TalkPreviewAudioFix.CaptureBeforePreview(__instance);
                TalkPreviewBootstrapContext context = null;
                if (__instance.parms != null && __instance.parms.Length > 12)
                    context = __instance.parms[12] as TalkPreviewBootstrapContext;
                TalkPreviewBootstrapRuntime.Register(__instance, context);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtTalkBootstrap.OnOpen] {e}");
            }
        }
    }

    [HarmonyPatch(typeof(PreviewTalkView), "OnClose")]
    internal static class PreviewTalkBootstrapClosePatch
    {
        private static void Postfix(PreviewTalkView __instance)
        {
            try
            {
                EditorAudioRuntime.EndPreview(__instance);
                if (TalkPreviewBootstrapRuntime.TryGet(
                        __instance, out TalkPreviewBootstrapContext context))
                {
                    unchecked { context.Generation++; }
                }
                // PreviewTalkView 原版没有 NewTalkView.CloseView 的音频收尾：
                // 场景音、语音和循环事件 BGM 都会泄漏回编辑器。不能照抄
                // PlayBgm(false)，因为刚播放的事件音乐是循环的，等待“播放完”
                // 的回调可能永远不发生。先明确停掉预览通道，再强制恢复环境 BGM。
                AudioMgrEx.PauseSceneSound();
                AudioMgrEx.PauseNpcSound();
                AudioMgrEx.StopAllMusic();
                AudioMgrEx.PlayBgm(true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtTalkBootstrap.OnClose] {e}");
            }
            finally
            {
                try { TalkPreviewAudioFix.RestoreAfterPreview(__instance); }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning(
                        "[EvtTalkBootstrap.AudioRestore] " + e.Message);
                }
                TalkPreviewBootstrapRuntime.Unregister(__instance);
            }
        }
    }

    internal enum TalkPreviewVocalCommandKind
    {
        None,
        Pause,
        SetVolume,
        Play,
    }

    internal struct TalkPreviewVocalCommand
    {
        internal TalkPreviewVocalCommand(
            TalkPreviewVocalCommandKind kind, int audioId, float volume)
        {
            Kind = kind;
            AudioId = audioId;
            Volume = volume;
        }

        internal TalkPreviewVocalCommandKind Kind;
        internal int AudioId;
        internal float Volume;
    }

    /// <summary>
    /// PreviewTalkView.PlayAudio 漏掉了 NewTalkView 已实现的 vocals 通道。
    /// 解析与执行分离，既容忍作者输入中的短列表，也便于无游戏进程回归。
    /// </summary>
    internal static class TalkPreviewAudioFix
    {
        private sealed class PreviewAudioState
        {
            internal float NpcVolume;
        }

        private static readonly ConditionalWeakTable<
            PreviewTalkView, PreviewAudioState> PreviewStates = new();

        internal static void CaptureBeforePreview(PreviewTalkView view)
        {
            if (view == null) return;
            try
            {
                PreviewStates.Remove(view);
                float volume = AudioMgr.Ins.GetChannel(3).source.volume;
                PreviewStates.Add(view, new PreviewAudioState
                {
                    NpcVolume = volume,
                });
            }
            catch (Exception e)
            {
                // 音频管理器尚未初始化时只放弃音量快照，绝不能让预览上下文
                // 注册也随之中断。
                PreviewStates.Remove(view);
                Plugin.Log?.LogWarning(
                    "[EvtTalkBootstrap.AudioCapture] " + e.Message);
            }
        }

        internal static void RestoreAfterPreview(PreviewTalkView view)
        {
            if (view == null) return;
            if (PreviewStates.TryGetValue(view, out PreviewAudioState state))
            {
                AudioMgr.Ins.SetChannelVolume(3, state.NpcVolume);
                PreviewStates.Remove(view);
            }
        }

        internal static bool TryResolveVocal(
            IList<float> vocals,
            out TalkPreviewVocalCommand command,
            out string error)
        {
            command = new TalkPreviewVocalCommand(
                TalkPreviewVocalCommandKind.None, 0, -1f);
            error = null;
            if (vocals == null || vocals.Count == 0) return true;
            float rawId = vocals[0];
            if (float.IsNaN(rawId) || float.IsInfinity(rawId)
                || rawId < int.MinValue || rawId > int.MaxValue)
            {
                error = "语音编号不是有限整数范围内的数字。";
                return false;
            }
            float volume = vocals.Count > 1 ? vocals[1] : -1f;
            if (float.IsNaN(volume) || float.IsInfinity(volume))
            {
                error = "语音音量不是有限数字。";
                return false;
            }
            int id = (int)rawId;
            TalkPreviewVocalCommandKind kind = id == -1
                ? TalkPreviewVocalCommandKind.Pause
                : id == 0
                    ? TalkPreviewVocalCommandKind.SetVolume
                    : TalkPreviewVocalCommandKind.Play;
            command = new TalkPreviewVocalCommand(kind, id, volume);
            return true;
        }

        internal static void ApplyVocal(
            TalkPreviewVocalCommand command,
            IDictionary<int, AudioCfg> previewAudioMap)
        {
            switch (command.Kind)
            {
                case TalkPreviewVocalCommandKind.None:
                    return;
                case TalkPreviewVocalCommandKind.Pause:
                    AudioMgrEx.PauseNpcSound();
                    return;
                case TalkPreviewVocalCommandKind.SetVolume:
                    AudioMgr.Ins.SetChannelVolume(3, command.Volume);
                    return;
            }

            AudioCfg cfg;
            if (previewAudioMap != null
                && previewAudioMap.TryGetValue(command.AudioId, out cfg)
                && cfg != null && !string.IsNullOrWhiteSpace(cfg.url))
            {
                float volume = command.Volume != -1f
                    ? command.Volume
                    : cfg.volumn > 0f ? cfg.volumn : 1f;
                // 直接使用预览器收到的合并表，确保尚未进入全局 Cfg 的当前
                // Mod 自定义语音也能播放。
                AudioMgr.Ins.PlaySound(
                    3, AudioMgrEx.FormatUrl(cfg.url), volume,
                    false, null, 0.5f);
                return;
            }
            // 兜底沿用原生全局音频查找；不存在的编号只会静默不播放。
            AudioMgrEx.PlaySound(
                3, command.AudioId, false, null, 0.5f, command.Volume);
        }
    }

    [HarmonyPatch(typeof(PreviewTalkView), "PlayAudio")]
    internal static class PreviewTalkVocalPatch
    {
        private static void Postfix(PreviewTalkView __instance)
        {
            try
            {
                var traverse = Traverse.Create(__instance);
                TalkCfg cfg = traverse.Field("cfg").GetValue<TalkCfg>();
                TalkPreviewVocalCommand command;
                string error = null;
                if (cfg == null
                    || !TalkPreviewAudioFix.TryResolveVocal(
                        cfg.vocals, out command, out error))
                {
                    if (!string.IsNullOrEmpty(error))
                        Plugin.Log?.LogWarning(
                            "[EvtTalkBootstrap.Audio] " + error);
                    return;
                }
                Dictionary<int, AudioCfg> audioMap = traverse
                    .Field("audioCfgMap")
                    .GetValue<Dictionary<int, AudioCfg>>();
                TalkPreviewAudioFix.ApplyVocal(command, audioMap);
            }
            catch (Exception e)
            {
                // 预览辅助音频不能阻断正文、选项或关闭按钮。
                Plugin.Log?.LogError("[EvtTalkBootstrap.Audio] " + e);
            }
        }
    }

    [HarmonyPatch(typeof(PreviewTalkView), "RefreshTalk", typeof(int), typeof(bool))]
    internal static class PreviewTalkBootstrapRefreshPatch
    {
        private static void Prefix(
            PreviewTalkView __instance,
            int _talkId,
            bool _firstOpen,
            out bool __state)
        {
            TalkPreviewBootstrapRuntime.NotifyRefresh(__instance, _talkId);
            bool alreadyShowing = false;
            try
            {
                alreadyShowing = Traverse.Create(__instance)
                    .Field("isShowingCG").GetValue<bool>();
            }
            catch { }
            __state = alreadyShowing
                || TalkPreviewBootstrapRuntime.WasCgActiveBeforeStart(__instance);
            TalkPreviewBootstrapRuntime.PrimeBackground(
                __instance, _talkId, _firstOpen);
        }

        private static void Postfix(PreviewTalkView __instance, bool __state)
        {
            try
            {
                bool showingCg = Traverse.Create(__instance)
                    .Field("isShowingCG").GetValue<bool>();
                // 只有“进入本 Talk 前 CG 已经存在”时才隐藏人物。当前句刚执行
                // 4015 时仍让本句人物动作完成；下一 Talk 再按真实游戏隐藏。
                if (__state && showingCg)
                    __instance.group_role.gameObject.SetActive(false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtTalkBootstrap.Refresh] {e}");
            }
        }
    }

    [HarmonyPatch(typeof(PreviewTalkView), "ShowCurTxt")]
    internal static class PreviewTalkBootstrapShowPatch
    {
        private static bool Prefix(PreviewTalkView __instance)
        {
            return TalkPreviewBootstrapRuntime.BeforeFirstShow(__instance);
        }
    }

    /// <summary>
    /// 迷你 CG 会隐藏人物层；原 PreviewTalkView 的 HideCGComic 不知道这一点，
    /// 在 4017 或换景自动关闭后必须恢复人物层。
    /// </summary>
    [HarmonyPatch(typeof(PreviewTalkView), "HideCGComic")]
    internal static class PreviewTalkMiniCgHidePatch
    {
        private static void Postfix(PreviewTalkView __instance)
        {
            try
            {
                __instance.group_role.gameObject.SetActive(true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[PreviewTalk.4019.Hide] {e}");
            }
        }
    }

    /// <summary>补齐 PreviewTalkView 原版漏掉的 4019 迷你 CG 分支。</summary>
    [HarmonyPatch(typeof(PreviewTalkView), "PlayScreenEffect", typeof(Action))]
    internal static class PreviewTalkMiniCgPatch
    {
        private static bool Prefix(PreviewTalkView __instance, Action _callback)
        {
            try
            {
                TalkCfg cfg = Traverse.Create(__instance).Field("cfg").GetValue<TalkCfg>();
                if (cfg?.screenEffect == null || cfg.screenEffect.Count == 0)
                    return true;
                int code = (int)cfg.screenEffect[0];
                if (code == 4017)
                {
                    // NewTalkView 关闭 CG 时会恢复人物层；PreviewTalkView 原版漏了。
                    __instance.group_role.gameObject.SetActive(true);
                    return true;
                }
                if (code != 4019) return true;
                // 缺号/参数缺失都降级成「不显示 MiniCG，照常走文本流程」：原版
                // PlayScreenEffect 没有 4019 分支，放行它不会有任何补救动作。
                if (cfg.screenEffect.Count <= 1
                    || !TalkPreviewBootstrapRuntime.ShowMiniCg(
                        __instance, (int)cfg.screenEffect[1], _callback))
                    _callback?.Invoke();
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[PreviewTalk.4019] {e}");
                return true;
            }
        }
    }
}
