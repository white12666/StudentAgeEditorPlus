using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Components;
using Config;
using GenUI.Common;
using GenUI.Mod;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Common;
using View.Evt;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    // ═════════════════════════════════════════════════════════════════
    //  对话编辑器 / 选项编辑器：miniGame 字段编辑支持
    //
    //  TalkCfg.miniGame 和 OptionCfg.miniGame 是裸字段（无 [CfgProperty]），
    //  两个编辑器都是硬编码 UI，没有对应输入框，作者只能手改 JSON。
    //
    //  运行时链路（已逐行验证）：
    //    NewTalkView.NextTalk / CommonEvtMgr.SelectOption
    //      → JumpToMiniGame（此时对话界面被 SetActive(false) 隐藏）
    //      → FuncMgr.OpenMiniGame（switch 按编号打开小游戏界面）
    //      → 小游戏 CloseView 按胜负分支：
    //          胜利 → RunEffector(effect)  → 跳 nextTalk / talkId
    //          失败 → RunEffector(effect2) → 跳 nextTalk2 / talkId2
    //      → ShowTalk → EventMgr.Send(1) → NewTalkView.OnRefreshTalk
    //        （事件 1 处理器会 SetActive(true) 唤醒对话界面，链路无 bug）
    //
    //  ⚠ 每个小游戏界面的 CloseView / OnOpen 都是独立实现，支持面完全不同。
    //  已逐个审查全部 40+ 个界面，得出下方 TalkSupportedIds / OptionSupportedIds /
    //  NeedParamIds / ParamJumpIds 四张表，依据（节选）：
    //    · QuizMiniGameView(17)、NegotiationMatchMiniGameView(18)、
    //      HurdlingMiniGameView(19)、RunningPartyView(27)、Qte3MiniGameView(34)、
    //      PhotoboothView(29) 等的 CloseView 只处理 Option——从对话触发时
    //      结束后什么都不做，对话界面维持隐藏 → 黑屏卡死。
    //    · StudyCardMiniGameView(33) 的 OnOpen 无条件执行
    //      levels[typeId-1]（levels 只有 3 项，typeId 是对话/选项 ID）→ 必崩，
    //      对话和选项都不可用（它的 CloseView 有 Talk 分支，是死代码）。
    //    · DivinationMiniGameView(3) 结束走 EndGame 社交流程，不接对话链。
    //    · FightMiniGameView(21)、BrickGameView(10)、FingerKnifeMiniGameView(20)、
    //      PianoMiniGameView(24)、WeavingMinigameView(46)、DrawingMinigameView(48)、
    //      Qte2MiniGameView(16)、LineMatchMiniGameView(45)：OnOpen 对 parms[2]
    //      不判空直接 .Count / [0] → miniGame 只填编号（无参数）时 NRE 卡死。
    //    · Basketball1On1View(11)、BadmintonMiniGameView(26)：Talk/Option 分支
    //      直接读 list[0]、list[1] → 必须带 ≥2 个参数。
    //    · SentenceMiniGameView(13)：无参数时 cfgId=0 → SentenceMiniGameCfgMap[0]
    //      查表崩 → 必须带题目编号。
    //    · Qte2(16 讲价)、LineMatch(45 连线) 是"参数驱动跳转"型：
    //      CloseView 直接 ShowTalk(参数[成功次数])，nextTalk/nextTalk2 根本不读！
    //    · MusicMiniGameView(41) 有 cfg==null 兜底 → 无参数安全。
    //
    //  另有通用运行时坑（编辑器保存时校验提醒）：
    //    1. 失败分支的 fail() 无判空且从对话/选项进入时 fail 恒为 null：
    //       选项 talkId2 为空 → 失败即空引用卡死（OptionCfg.GetNextTalk2 不回退）；
    //       对话 nextTalk2 为空回退 nextTalk（胜负同路），nextTalk 也空则同样卡死。
    //    2. 对话同时配置 option 和 miniGame 时，玩家选完任一选项后
    //       CommonEvtMgr.SelectOption 会检查所在对话的 miniGame 并劫持跳转，
    //       选项自身的 talkId/talkId2 失效。
    //    3. 设了小游戏的对话/选项，其 effect 在胜利后会（再）执行一次、
    //       effect2 在失败后执行——属性变化写在本条上容易重复生效。
    //
    //  UI 布局（UnityPy 实测 prefab）：
    //    · 对话编辑器 group_content 没有布局组件，子控件全是绝对定位；
    //      group_highlight 在 (1897,-353) 尺寸 300x70，克隆体必须手动挪位，
    //      否则与「高亮人物」完全重叠。
    //    · 选项编辑器 group_content 带 VerticalLayoutGroup，自动排列无重叠。
    //
    //  对玩家零依赖：产出的 mod 是标准 JSON，原版客户端正常读取。
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// 小游戏字段的公共工具：支持表、写入、过滤、校验、克隆体清理。
    /// </summary>
    internal static class MiniGameUtil
    {
        // ── 支持表（依据每个小游戏界面 OnOpen/CloseView 的逐个审查） ──

        /// <summary>从「对话」触发后能正常接回对话链的编号。</summary>
        public static readonly HashSet<int> TalkSupportedIds = new HashSet<int>
        {
            5, 6, 7, 8, 9, 10, 11, 13, 14, 15, 16, 20, 21, 22, 23, 24, 26,
            32, 35, 41, 43, 45, 46, 48
        };

        /// <summary>从「选项」触发后能正常接回对话链的编号（对话表 + 仅选项可用的）。</summary>
        public static readonly HashSet<int> OptionSupportedIds = new HashSet<int>
        {
            5, 6, 7, 8, 9, 10, 11, 13, 14, 15, 16, 20, 21, 22, 23, 24, 26,
            32, 35, 41, 43, 45, 46, 48,
            // 以下只有选项分支（CloseView 只处理 Option）
            17, 18, 19, 27, 29, 34, 36, 37, 42, 44, 47
        };

        /// <summary>只填编号（不带参数）就会在打开时报错卡死的编号。</summary>
        public static readonly HashSet<int> NeedParamIds = new HashSet<int>
        {
            10, 11, 13, 16, 20, 21, 24, 26, 32, 45, 46, 48
        };

        /// <summary>"参数驱动跳转"型：参数本身是跳转表，胜负分支(nextTalk/nextTalk2)无效。</summary>
        public static readonly HashSet<int> ParamJumpIds = new HashSet<int> { 16, 45 };

        /// <summary>常用编号的参数提示。</summary>
        private static string ParamHint(int _id)
        {
            switch (_id)
            {
                case 11: return "参数=先手(0我先/1对方先),比赛编号，例 11,0,1";
                case 13: return "参数=造句题目编号（SentenceMiniGameCfg 表）";
                case 16: return "参数=4个对话ID，按砍价成功0~3次跳转，例 16,1001,1002,1003,1004";
                case 21: return "参数=对手的人物ID，例 21,301";
                case 26:
                case 32: return "参数=先手(0我先/1对方先),等级编号，例 26,0,1";
                case 45: return "参数=题库编号,连对0题跳转,连对1题跳转…";
                default: return "需要至少1个参数（通常为难度或配置编号），只填编号会报错";
            }
        }

        // ── 悬浮说明 ──

        private const string CommonIds =
            "只填编号即可用：5数独 6话术(辩论) 7速算 8成语 9拼图\n" +
            "14翻牌 15太阁复习 22手工 23灵感 35理综连线 41音游 43钓鱼\n" +
            "需要带参数：21打架(参数=对手人物ID，例 21,301)\n" +
            "11篮球、26羽毛球(参数=先手,等级编号，例 26,0,1) 13造句(参数=题目编号)\n" +
            "16讲价(砍价)较特殊：参数是4个对话ID，按砍价成功0~3次跳转，\n" +
            "例 16,1001,1002,1003,1004——它不走胜负分支，直接按参数跳转。";

        private const string CommonTail =
            "『效果』会在胜利后执行、『效果2』在失败后执行，\n" +
            "属性变化建议写在跳转后的对话里，避免重复生效。";

        /// <summary>对话编辑器的悬浮说明。</summary>
        public const string TalkDesc =
            "这句对话播放完后触发的小游戏，留空＝不触发。\n" +
            "第一个数字是小游戏编号，后面可加参数（用逗号分隔）。\n" +
            CommonIds + "\n" +
            "其余编号大多不能从对话触发（保存时会提示），17知识竞赛、\n" +
            "18辩论赛、19长跑、27跑步、34狂点蓄力只能挂在选项上。\n" +
            "胜负分支：勾选『有无分支』开关，\n" +
            "『条件成立→后续对话』＝胜利后跳转的对话ID\n" +
            "『条件不成立→后续对话』＝失败后跳转的对话ID\n" +
            "失败分支留空时，失败也会走胜利分支；两个都留空，失败后游戏会停住。\n" +
            "注意：这句对话不要同时挂选项，否则选项的跳转会被小游戏顶掉。\n" +
            CommonTail;

        /// <summary>选项编辑器的悬浮说明。</summary>
        public const string OptionDesc =
            "选中此选项后触发的小游戏，留空＝不触发。\n" +
            "第一个数字是小游戏编号，后面可加参数（用逗号分隔）。\n" +
            CommonIds + "\n" +
            "选项还可用：17知识竞赛 18辩论赛 19长跑 27跑步 34狂点蓄力\n" +
            "胜负分支：勾选『是否有分支』开关，\n" +
            "第一个『后续对话』框＝胜利后跳转的对话ID\n" +
            "第二个『后续对话』框＝失败后跳转的对话ID\n" +
            "注意：失败分支必须填写，留空时小游戏一旦失败游戏会停住。\n" +
            CommonTail;

        public static DescData? GetTalkDesc(int _type) => new DescData { txt = TalkDesc };
        public static DescData? GetOptionDesc(int _type) => new DescData { txt = OptionDesc };

        // ── 写入 / 校验 ──

        /// <summary>把输入框文本解析进 miniGame 列表（空文本＝清空）。</summary>
        public static List<double> Parse(List<double> _current, string _txt)
        {
            if (_txt.NotEmpty())
            {
                return ModCtrl.StrToList<double>(_txt);
            }
            _current?.Clear();
            return _current;
        }

        /// <summary>
        /// 按触发来源校验 miniGame 配置，返回警告文本；没问题返回 null。
        /// _isTalk：true=从对话触发，false=从选项触发。
        /// </summary>
        public static string Validate(List<double> _miniGame, bool _isTalk)
        {
            if (_miniGame.IsEmpty())
            {
                return null;
            }
            int id = (int)_miniGame[0];
            var supported = _isTalk ? TalkSupportedIds : OptionSupportedIds;
            if (!supported.Contains(id))
            {
                if (_isTalk && OptionSupportedIds.Contains(id))
                {
                    return $"小游戏 {id} 只能从选项触发：挂在对话上时结束后不会接回对话，游戏会停住。请把它挂到选项上";
                }
                return $"小游戏编号 {id} 无法从这里触发（会卡住或无法接回对话），请更换编号";
            }
            if (NeedParamIds.Contains(id) && _miniGame.Count < 2)
            {
                return $"小游戏 {id} 必须带参数，只填编号进入时会报错卡住。{ParamHint(id)}";
            }
            if (id == 16 && _miniGame.Count < 5)
            {
                return "讲价(16)需要4个对话ID参数：按砍价成功0~3次跳转，例 16,1001,1002,1003,1004；少填会在对应结果时卡住";
            }
            if (id == 45 && _miniGame.Count < 3)
            {
                return "连线(45)参数不足：第1个=题库编号，之后按连对题数依次填跳转对话ID";
            }
            return null;
        }

        /// <summary>
        /// 绑定输入框：onValueChanged 过滤非法字符并实时写入，onEndEdit 兜底写入并即时校验。
        /// _write 负责把文本写回目标 cfg；_isTalk 决定用哪张支持表校验。
        /// </summary>
        public static void Bind(InputField _input, bool _isTalk, Action<string> _write)
        {
            _input.onValueChanged.AddListener(_txt =>
            {
                try
                {
                    // 与原版数字列表框一致的字符过滤（只留数字、逗号、小数点、负号）
                    string filtered = ModCtrl.CheckValueChangeByList(_txt);
                    if (filtered != _txt)
                    {
                        _input.text = filtered; // 会再触发一次 onValueChanged，届时进入写入分支
                        return;
                    }
                    _write(_txt);
                }
                catch (Exception e) { Plugin.Log.LogError($"[MiniGameOnVal] {e}"); }
            });

            _input.onEndEdit.AddListener(_txt =>
            {
                try
                {
                    _write(_txt);
                    if (_txt.NotEmpty())
                    {
                        string warn = Validate(ModCtrl.StrToList<double>(_txt), _isTalk);
                        if (warn != null)
                        {
                            ToastHelper.Toast(warn);
                        }
                    }
                }
                catch (Exception e) { Plugin.Log.LogError($"[MiniGameOnEnd] {e}"); }
            });
        }

        /// <summary>
        /// 清掉克隆体里有害的组件副本：
        /// · Description——Instantiate 不复制委托字段，克隆出来的是空壳；
        /// · LocalizeStringEvent（Unity 本地化组件，prefab 实测挂在
        ///   group_highlight / group_option_effect 的标签 Text 上）——它会在
        ///   本地化表就绪或刷新时把标签文字重置回源文案（"高亮人物："/"效果"），
        ///   不删掉的话我们改的"小游戏"标签随时会被顶回去。
        /// </summary>
        public static void StripBadComponents(GameObject _clone)
        {
            foreach (var desc in _clone.GetComponentsInChildren<Description>(true))
            {
                UnityEngine.Object.DestroyImmediate(desc);
            }
            foreach (var comp in _clone.GetComponentsInChildren<Component>(true))
            {
                if (comp != null && comp.GetType().Name == "LocalizeStringEvent")
                {
                    UnityEngine.Object.DestroyImmediate(comp);
                }
            }
        }
    }

    /// <summary>注入的小游戏控件引用（克隆体根 / 标签 / 输入框），供后续校正。</summary>
    internal sealed class MiniGameWidget
    {
        public RectTransform root;
        public Text label;
        public InputField input;
    }

    // ───────────────────────────────────────────────────────────────────
    //  B. 对话编辑器 ModEvtEditView
    // ───────────────────────────────────────────────────────────────────

    internal static class EvtEditMiniGameState
    {
        private static readonly ConditionalWeakTable<ModEvtEditView, MiniGameWidget> _widgets = new();

        public static void Set(ModEvtEditView view, MiniGameWidget w) => _widgets.Add(view, w);
        public static bool TryGet(ModEvtEditView view, out MiniGameWidget w) => _widgets.TryGetValue(view, out w);
    }

    /// <summary>
    /// B1. InitUI Postfix：克隆 group_highlight 生成小游戏输入框。
    ///
    /// 注意：group_content 没有布局组件（prefab 实测），子控件全是绝对定位。
    /// 克隆体必须手动移到 group_highlight 正下方一格（高度 70），
    /// 否则会与「高亮人物」完全重叠——标签和输入文字叠在一起、原框被挡住点不到。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "InitUI")]
    internal static class EvtEditMiniGameInitPatch
    {
        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                var src = __instance.group_highlight;
                if (src == null) return;

                var parent = src.parent;
                if (parent == null) return;

                // 幂等
                if (parent.Find("group_minigame") != null) return;

                var clone = UnityEngine.Object.Instantiate(src.gameObject, parent, false);
                clone.name = "group_minigame";
                clone.transform.SetSiblingIndex(src.GetSiblingIndex() + 1);

                // 关键修复：挪到「高亮人物」正下方一格，避免绝对定位重叠
                var cloneRt = clone.GetComponent<RectTransform>();
                if (cloneRt != null)
                {
                    cloneRt.anchoredPosition = src.anchoredPosition + new Vector2(0f, -src.sizeDelta.y);
                }

                // 清掉克隆出来的失效 Description 和会重置标签文字的 LocalizeStringEvent
                MiniGameUtil.StripBadComponents(clone);

                // 标签：group_highlight 的标签 Text 挂在节点自身（"高亮人物："）
                var label = clone.GetComponent<Text>();
                if (label != null)
                {
                    label.text = "小游戏";
                }

                var input = clone.GetComponentInChildren<InputField>();
                if (input == null) return;

                input.contentType = InputField.ContentType.Standard;
                input.SetTextWithoutNotify("");

                // 悬浮说明
                clone.AddDescription(MiniGameUtil.GetTalkDesc);

                // 分支输入框的占位灰字：写明小游戏胜负语义（原版 prefab 里没有针对性提示）
                if (__instance.input_cond_true != null && __instance.input_cond_true.placeholder is Text pt1)
                {
                    pt1.text = "对话id（小游戏成功时进入）";
                }
                if (__instance.input_cond_false != null && __instance.input_cond_false.placeholder is Text pt2)
                {
                    pt2.text = "对话id（小游戏失败时进入）";
                }

                // 实时写入当前选中的对话（每次写入时取 curSelect，选中项会变）
                MiniGameUtil.Bind(input, _isTalk: true, _txt =>
                {
                    var curSelect = Traverse.Create(__instance).Field("curSelect").GetValue<TalkCfg>();
                    if (curSelect == null) return;
                    curSelect.miniGame = MiniGameUtil.Parse(curSelect.miniGame, _txt);
                });

                EvtEditMiniGameState.Set(__instance, new MiniGameWidget
                {
                    root = cloneRt,
                    label = label,
                    input = input
                });

                Plugin.Log.LogInfo("[EvtEditMiniGame] 小游戏输入框已注入。");
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtEditMiniGameInit] {e}"); }
        }
    }

    /// <summary>
    /// B2. Select Postfix：切换对话条目时加载 miniGame 值，
    /// 并对克隆体的位置与标签做幂等校正（双保险：即使有其他运行时逻辑
    /// 动过位置或文字，每次选中对话都会拉回正确状态）。
    /// 同时修正『有无分支』开关的显示：原版只按「判断」(check)是否非空
    /// 恢复勾选状态，靠小游戏走分支时判断是留空的——数据还在（游戏里分支
    /// 正常触发），但重进界面开关显示未勾、分支框被隐藏，作者会误以为配置丢失。
    /// SetTextWithoutNotify 不触发 onValueChanged，不会误写。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "Select")]
    internal static class EvtEditMiniGameSelectPatch
    {
        private static void Postfix(ModEvtEditView __instance, TalkCfg _cfg)
        {
            try
            {
                if (!EvtEditMiniGameState.TryGet(__instance, out var w) || w == null || w.input == null) return;

                // 位置校正：紧贴「高亮人物」正下方
                var src = __instance.group_highlight;
                if (src != null && w.root != null)
                {
                    w.root.anchoredPosition = src.anchoredPosition + new Vector2(0f, -src.sizeDelta.y);
                }
                // 标签校正：防止被残留的本地化逻辑改回"高亮人物："
                if (w.label != null && w.label.text != "小游戏")
                {
                    w.label.text = "小游戏";
                }

                if (_cfg == null)
                {
                    w.input.SetTextWithoutNotify("");
                    return;
                }

                // 开关显示校正：填了判断 / 挂了小游戏 / 填了失败分支，任一成立就应勾上。
                // isOn 从 false 变 true 时会触发原版 OnToggleCheck(true)，
                // 自动完成分支 UI 的显示与数值加载。
                bool shouldOn = _cfg.check.NotEmpty()
                                || _cfg.miniGame.NotEmpty()
                                || (_cfg.nextTalk2.NotEmpty() && _cfg.nextTalk2[0] != 0);
                if (shouldOn && __instance.toggle_has_check != null && !__instance.toggle_has_check.isOn)
                {
                    __instance.toggle_has_check.isOn = true;
                }

                w.input.SetTextWithoutNotify(ModCtrl.ListToStr(_cfg.miniGame));
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtEditMiniGameSelect] {e}"); }
        }
    }

    /// <summary>
    /// D1. 对话编辑器：勾选『有无分支』时给两个分支标签追加胜负提示。
    /// 原版每次 OnToggleCheck(true) 都会重设标签文字，所以在 Postfix 追加、按内容判重。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "OnToggleCheck")]
    internal static class EvtEditBranchHintPatch
    {
        private const string WinHint = "\n（设了小游戏时：胜利走这里）";
        private const string LoseHint = "\n（设了小游戏时：失败走这里）";

        private static void Postfix(ModEvtEditView __instance, bool _isOn)
        {
            try
            {
                if (!_isOn) return;
                if (__instance.txt_cond_true != null &&
                    !(__instance.txt_cond_true.text?.Contains("小游戏") ?? false))
                {
                    __instance.txt_cond_true.text = (__instance.txt_cond_true.text ?? "") + WinHint;
                }
                if (__instance.txt_cond_false != null &&
                    !(__instance.txt_cond_false.text?.Contains("小游戏") ?? false))
                {
                    __instance.txt_cond_false.text = (__instance.txt_cond_false.text ?? "") + LoseHint;
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtEditBranchHint] {e}"); }
        }
    }

    /// <summary>
    /// B3. 保存前校验：设了小游戏的对话若编号不可用 / 参数缺失 / 分支缺失 / 与选项冲突，
    /// Toast 提醒作者。只提醒不拦截（作者可以先存草稿），一次只报第一处，避免刷屏。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "OnClickSave")]
    internal static class EvtEditMiniGameSaveCheckPatch
    {
        private static void Prefix(ModEvtEditView __instance)
        {
            try
            {
                var talkCfgs = Traverse.Create(__instance).Field("talkCfgs").GetValue<List<TalkCfg>>();
                if (talkCfgs == null) return;

                foreach (var t in talkCfgs)
                {
                    if (t == null || t.miniGame.IsEmpty()) continue;
                    int gameId = (int)t.miniGame[0];

                    // 编号 / 参数校验（按"对话"支持表）
                    string warn = MiniGameUtil.Validate(t.miniGame, _isTalk: true);
                    if (warn != null)
                    {
                        ToastHelper.Toast($"对话 {t.id}：{warn}");
                        return;
                    }

                    // 对话同时挂了选项：选项的跳转会被小游戏劫持
                    if (t.option.NotEmpty())
                    {
                        ToastHelper.Toast($"对话 {t.id} 同时设了选项和小游戏：玩家选完任一选项都会先进小游戏，选项自己的后续对话会失效。建议把小游戏挂到具体选项上");
                        return;
                    }

                    // 讲价/连线按参数跳转，不使用胜负分支，跳过分支检查
                    if (MiniGameUtil.ParamJumpIds.Contains(gameId)) continue;

                    bool hasWin = t.nextTalk.NotEmpty() && t.nextTalk[0] != 0;
                    bool hasLose = t.nextTalk2.NotEmpty() && t.nextTalk2[0] != 0;
                    if (!hasWin && !hasLose)
                    {
                        ToastHelper.Toast($"对话 {t.id} 设了小游戏但没填任何后续对话：小游戏失败后游戏会停住，请勾选『有无分支』填上胜负分支");
                        return;
                    }
                    if (!hasLose)
                    {
                        ToastHelper.Toast($"提示：对话 {t.id} 没填失败分支（条件不成立→后续对话），小游戏胜负都会走同一段对话");
                        return;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtEditMiniGameSaveCheck] {e}"); }
        }
    }

    // ───────────────────────────────────────────────────────────────────
    //  C. 选项编辑器 ModEvtOptionView
    // ───────────────────────────────────────────────────────────────────

    internal static class EvtOptionMiniGameState
    {
        private static readonly ConditionalWeakTable<ModEvtOptionView, MiniGameWidget> _widgets = new();

        public static void Set(ModEvtOptionView view, MiniGameWidget w) => _widgets.Add(view, w);
        public static bool TryGet(ModEvtOptionView view, out MiniGameWidget w) => _widgets.TryGetValue(view, out w);
    }

    /// <summary>
    /// C1. InitUI Postfix：克隆 group_option_effect 生成小游戏输入框。
    /// 选项编辑器的 group_content 带 VerticalLayoutGroup，克隆体按 SiblingIndex 自动排列。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtOptionView), "InitUI")]
    internal static class EvtOptionMiniGameInitPatch
    {
        private static void Postfix(ModEvtOptionView __instance)
        {
            try
            {
                var groupContent = Traverse.Create(__instance).Field("group_content").GetValue<RectTransform>();
                if (groupContent == null) return;
                if (groupContent.Find("group_option_minigame") != null) return; // 幂等

                var srcRt = Traverse.Create(__instance).Field("group_option_effect").GetValue<RectTransform>();
                if (srcRt == null) return;

                var clone = UnityEngine.Object.Instantiate(srcRt.gameObject, groupContent, false);
                clone.name = "group_option_minigame";
                clone.transform.SetSiblingIndex(srcRt.GetSiblingIndex() + 1);
                clone.SetActive(true); // 不随『是否有分支』开关显隐，小游戏字段始终可编辑

                // 清掉克隆出来的失效 Description 和会重置标签文字的 LocalizeStringEvent
                MiniGameUtil.StripBadComponents(clone);

                // 标签：group_option_effect 下名为 _txt 的子节点（"效果"）
                var labelTr = clone.transform.Find("_txt");
                var label = labelTr != null ? labelTr.GetComponent<Text>() : clone.GetComponentInChildren<Text>();
                if (label != null)
                {
                    label.text = "小游戏";
                }

                // 克隆出来的「查阅」按钮没有任何点击逻辑（原版按钮的事件是运行时绑定的，
                // Instantiate 复制不了），留着点了没反应，直接隐藏。
                var deadBtn = clone.transform.Find("btn_option_effect");
                if (deadBtn != null)
                {
                    deadBtn.gameObject.SetActive(false);
                }

                var input = clone.GetComponentInChildren<InputField>();
                if (input == null) return;

                input.contentType = InputField.ContentType.Standard;
                input.SetTextWithoutNotify("");

                // 悬浮说明
                clone.AddDescription(MiniGameUtil.GetOptionDesc);

                EvtOptionMiniGameState.Set(__instance, new MiniGameWidget
                {
                    root = clone.GetComponent<RectTransform>(),
                    label = label,
                    input = input
                });

                Plugin.Log.LogInfo("[EvtOptionMiniGame] 小游戏输入框已注入。");
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtOptionMiniGameInit] {e}"); }
        }
    }

    /// <summary>
    /// C2. OnOpen Postfix：每次打开选项时加载 miniGame 值并重新绑定监听。
    /// cfg 是每次打开传入的对象，先清掉上次的监听再用新 cfg 绑定。
    /// 同时对标签文字做幂等校正（防止任何残留逻辑把"小游戏"改回"效果"）。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtOptionView), "OnOpen")]
    internal static class EvtOptionMiniGameOpenPatch
    {
        private static void Postfix(ModEvtOptionView __instance)
        {
            try
            {
                if (!EvtOptionMiniGameState.TryGet(__instance, out var w) || w == null || w.input == null) return;
                var cfg = Traverse.Create(__instance).Field("cfg").GetValue<OptionCfg>();
                if (cfg == null) return;

                // 标签校正
                if (w.label != null && w.label.text != "小游戏")
                {
                    w.label.text = "小游戏";
                }

                // 加载已有值（SetTextWithoutNotify 不触发监听）
                w.input.SetTextWithoutNotify(ModCtrl.ListToStr(cfg.miniGame));

                // 清掉上一次 OnOpen 绑定的监听（View 复用时闭包里是旧 cfg）
                w.input.onValueChanged.RemoveAllListeners();
                w.input.onEndEdit.RemoveAllListeners();

                MiniGameUtil.Bind(w.input, _isTalk: false, _txt =>
                {
                    cfg.miniGame = MiniGameUtil.Parse(cfg.miniGame, _txt);
                });

                // D2. 胜负分支输入框的占位灰字：写明小游戏胜负语义。
                // 原版 OnOpen 末尾会把 placeholder 重设成 "{evtId}XXX"，必须在其后覆盖。
                var talk1 = Traverse.Create(__instance).Field("input_talk_1").GetValue<InputField>();
                var talk2 = Traverse.Create(__instance).Field("input_talk_2").GetValue<InputField>();
                if (talk1?.placeholder is Text t1)
                {
                    t1.text = "对话id（小游戏成功时进入）";
                }
                if (talk2?.placeholder is Text t2)
                {
                    t2.text = "对话id（小游戏失败时进入）";
                }

                // 开关显示校正：原版只按「判断」(check)是否非空恢复勾选状态，
                // 靠小游戏走分支时判断是留空的——数据还在（游戏里分支正常触发），
                // 但重开界面开关显示未勾、分支框被隐藏，作者会误以为配置丢失。
                // 填了判断 / 挂了小游戏 / 填了失败分支，任一成立就应勾上；
                // isOn 从 false 变 true 会触发原版 OnToggleCheck(true) 完成 UI 切换。
                var toggleCheck = Traverse.Create(__instance).Field("toggle_check").GetValue<Toggle>();
                bool shouldOn = cfg.check.NotEmpty()
                                || cfg.miniGame.NotEmpty()
                                || (cfg.talkId2.NotEmpty() && cfg.talkId2[0] != 0);
                if (shouldOn && toggleCheck != null && !toggleCheck.isOn)
                {
                    toggleCheck.isOn = true;
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtOptionMiniGameOpen] {e}"); }
        }
    }

    /// <summary>
    /// C3. 点『完成』时校验：编号不可用 / 参数缺失 / 失败分支（talkId2）为空时提醒。
    /// 选项的失败分支为空是玩家侧硬卡死（运行时 fail() 没有判空、选项不回退）。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtOptionView), "OnClickFinish")]
    internal static class EvtOptionMiniGameFinishCheckPatch
    {
        private static void Prefix(ModEvtOptionView __instance)
        {
            try
            {
                var cfg = Traverse.Create(__instance).Field("cfg").GetValue<OptionCfg>();
                if (cfg == null || cfg.miniGame.IsEmpty()) return;
                int gameId = (int)cfg.miniGame[0];

                string warn = MiniGameUtil.Validate(cfg.miniGame, _isTalk: false);
                if (warn != null)
                {
                    ToastHelper.Toast(warn);
                    return;
                }

                // 讲价/连线按参数跳转，不使用胜负分支；
                // 大头贴(29)只跳胜利对话（talkId），不读 talkId2，也跳过失败分支检查
                if (MiniGameUtil.ParamJumpIds.Contains(gameId) || gameId == 29) return;

                bool hasLose = cfg.talkId2.NotEmpty() && cfg.talkId2[0] != 0;
                if (!hasLose)
                {
                    ToastHelper.Toast("此选项设了小游戏但没填失败分支：请勾选『是否有分支』，在第二个后续对话框填失败后的对话，否则小游戏失败后游戏会停住");
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[EvtOptionMiniGameFinishCheck] {e}"); }
        }
    }

    // ───────────────────────────────────────────────────────────────────
    //  E. 剧情预览 PreviewTalkView：小游戏可见化
    //
    //  编辑器的剧情预览是一个简化播放器，不接入真实游戏逻辑：
    //  效果只弹提示不执行、条件分支弹窗手动选，而 miniGame 字段被完全忽略
    //  （原版预览代码里没有任何小游戏逻辑，小游戏依赖存档数据也确实没法在
    //  编辑器里实战）。带来的两个盲区：
    //    1. 作者在预览里看不出一句对话/一个选项到底挂没挂小游戏；
    //    2. 对话挂了小游戏且没勾『有无分支』的 check 时，原版 NextTalk 直接
    //       走胜利分支，失败分支在预览里永远走不到。
    //  以下两个补丁把小游戏在预览中"可见化"：推进到挂小游戏的对话时弹
    //  胜负选择框（明确写出小游戏名），选项列表给挂小游戏的选项加标记。
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// E1. 预览推进：对话挂了小游戏时，弹"选择结果"确认框代替直接跳转，
    /// 让作者能确认小游戏已挂上、并能同时预览胜利/失败两条分支。
    /// </summary>
    [HarmonyPatch(typeof(PreviewTalkView), "NextTalk")]
    internal static class PreviewTalkMiniGamePatch
    {
        private static bool Prefix(PreviewTalkView __instance)
        {
            try
            {
                var cfg = Traverse.Create(__instance).Field("cfg").GetValue<TalkCfg>();
                if (cfg == null || cfg.miniGame.IsEmpty())
                {
                    return true; // 没挂小游戏，走原逻辑
                }

                int gameId = (int)cfg.miniGame[0];
                string name = Cfg.MinigameCfgMap.TryGetValue(gameId, out var mc) ? mc.name : $"编号{gameId}";

                // 讲价/连线按参数跳转，结果不止两种，预览无法用双按钮模拟
                if (MiniGameUtil.ParamJumpIds.Contains(gameId))
                {
                    ToastHelper.Toast($"此处会触发小游戏[{name}]，按成绩跳转到参数里填的对话，预览无法模拟，请在游戏中实测");
                    return true; // 走原逻辑（顺着 nextTalk 或结束）
                }

                int win = cfg.GetNextTalk();
                int lose = cfg.GetNextTalk2();
                if (win == 0 && lose == 0)
                {
                    ToastHelper.Toast($"此处会触发小游戏[{name}]，但还没填后续对话（实际游戏中失败会停住）");
                    return true;
                }

                HintHelper.ShowConfirm(
                    $"此处会触发小游戏[{name}]。\n预览不进入小游戏，请直接选择结果：",
                    delegate
                    {
                        __instance.RefreshTalk(win);
                        EventMgr.Send(10003);
                    },
                    delegate
                    {
                        __instance.RefreshTalk(lose);
                        EventMgr.Send(10003);
                    },
                    _showCloseBtn: true, null,
                    $"胜利→对话{win}",
                    $"失败→对话{lose}");
                return false; // 跳过原方法
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[PreviewTalkMiniGame] {e}");
                return true;
            }
        }
    }

    /// <summary>
    /// E2. 预览的选项列表：挂了小游戏的选项在文本后追加「｛小游戏：名称｝」标记，
    /// 作者一眼能确认配置生效。点击后原版弹的"请选择分支"两个按钮
    /// （对话X/对话Y）即对应小游戏的胜利/失败。
    /// </summary>
    [HarmonyPatch(typeof(PreviewTalkView), "OnOptionRender")]
    internal static class PreviewOptionMiniGameMarkPatch
    {
        private static void Postfix(UICell _cell)
        {
            try
            {
                var cell = _cell as Cell_CommonOptionItemUI;
                var data = cell?.data as CommonEvtOptionData;
                if (data?.cfg == null || data.cfg.miniGame.IsEmpty())
                {
                    return;
                }
                int gameId = (int)data.cfg.miniGame[0];
                string name = Cfg.MinigameCfgMap.TryGetValue(gameId, out var mc) ? mc.name : $"编号{gameId}";
                string txt = cell.txtex_content.text ?? "";
                if (!txt.Contains("｛小游戏"))
                {
                    cell.txtex_content.text = txt + $"  ｛小游戏：{name}｝";
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[PreviewOptionMiniGameMark] {e}"); }
        }
    }
}
