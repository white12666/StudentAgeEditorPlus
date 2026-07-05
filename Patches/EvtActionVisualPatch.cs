using System;
using System.Collections.Generic;
using System.Text;
using Config;
using GenUI.Common;
using GenUI.Mod;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 改进：事件对话编辑器的"动作指令"缺少可视化。
    ///
    /// 作者反馈：动作指令只能填一串数字（如 1,3004,300），既看不出每个数字
    /// 是什么意思，也看不出人物移动 300 到底是多远，只能反复开剧情预览确认。
    ///
    /// 代码层面成因（反编译核实）：
    ///   - input_action 是纯文本框，内容为 TalkCfg.roles 的数字串序列化，
    ///     动作码含义（TalkAnimeDefine，30+ 种）编辑器完全不翻译；
    ///   - 编辑器小舞台（RefreshRoles/OnRenderRole）只解析进场（type 1）、
    ///     退场（type 2）和换装（3006），横移 3004 / 纵移 3008 等其余动作
    ///     在舞台上没有任何呈现，人物钉死在固定槽位；
    ///   - 原版预览只能从事件第一句开始播（PreviewTalkView.OnOpen 写死取
    ///     talkId[0]），调中间某句的动画要从头点过去。
    ///
    /// 本补丁做四件事（纯显示层，不改数据格式与存档）：
    ///   1. 动作翻译悬浮提示：鼠标移到动作指令输入框上时弹出提示框，
    ///      把数字串翻译成人话。迭代记录：初版是输入框下方常驻灰字，被紧邻
    ///      的"屏幕效果"框遮住（用户实测反馈）→ 改为悬浮；第二版向上弹出，
    ///      内容多时溢出屏幕上边缘（用户实测反馈）→ 改为向下弹出 + 屏幕
    ///      边缘保护（提示框置顶渲染，盖住下方控件无妨，鼠标正悬在输入框上）。
    ///   2. 屏幕效果悬浮提示：input_screeneffect 同样加悬浮翻译（用户要求）。
    ///   3. 舞台位移示意：当前选中的对话若带横移/纵移指令，舞台上对应
    ///      人物直接站到移动后的位置。只反映当前这句对话的移动
    ///     （用户确认的设计取舍，不沿剧情链累计历史移动）。
    ///   4. "预览本句"按钮：从当前选中的对话直接开始播放剧情预览
    ///     （传入编辑器内存中的最新数据，未保存的修改也能立即预览）。
    ///
    /// 关键换算：编辑器舞台 = 真实对话画面的 0.7 倍缩放
    ///（证据：真实同侧多人间距 300px（NewTalkView.UpdatePosRoles）
    ///  ↔ 编辑器槽位间距 210px；立绘 localScale 也是 0.7）。
    /// 因此舞台位移 = 指令偏移 × 0.7。
    /// </summary>
    internal static class TalkActionTranslator
    {
        /// <summary>动作/效果码 → 中文名（对照 Config.TalkAnimeDefine 硬编码，配置表无名称字段）。</summary>
        private static readonly Dictionary<int, string> ActionNames = new Dictionary<int, string>
        {
            { 1001, "放置进场" },
            { 1002, "淡入进场" },
            { 1003, "底部升起进场" },
            { 2001, "退场" },
            { 2002, "淡出退场" },
            { 3000, "换姿势" },
            { 3001, "跳跃" },
            { 3002, "抖动" },
            { 3003, "放大" },
            { 3004, "横向移动" },
            { 3005, "转身" },
            { 3006, "换装" },
            { 3007, "瞬间转身" },
            { 3008, "纵向移动" },
            { 3009, "表情" },
            { 3010, "摇头" },
            { 3011, "点头" },
            { 3012, "变剪影" },
            { 3013, "取消剪影" },
            { 3014, "换发型" },
            { 4001, "屏幕震动" },
            { 4002, "屏幕模糊" },
            { 4003, "清除屏幕效果" },
            { 4004, "屏幕物品" },
            { 4005, "屏幕表情" },
            { 4006, "等待" },
            { 4007, "打开手机界面" },
            { 4008, "关闭手机界面" },
            { 4009, "泛黄滤镜" },
            { 4010, "底片反色" },
            { 4011, "苏醒效果" },
            { 4012, "白屏闪光" },
            { 4013, "彩带庆祝" },
            { 4014, "礼物弹窗" },
            { 4015, "显示CG" },
            { 4016, "漫画分格" },
            { 4017, "关闭CG" },
            { 4018, "屏幕图片" },
            { 4019, "迷你CG" },
            { 5001, "纸条" },
            { 5002, "歌词" },
        };

        /// <summary>把整个 roles 列表翻译成多行"人名：动作(参数)"。</summary>
        public static string Translate(List<List<float>> roles, Dictionary<int, PersonCfg> personCfgs)
        {
            if (roles == null || roles.Count == 0) return null;

            var sb = new StringBuilder();
            foreach (var entry in roles)
            {
                if (entry == null || entry.Count == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(TranslateEntry(entry, personCfgs));
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// 翻译屏幕效果字段（screenEffect：[效果码, 参数...]，一条对话只有一条）。
        /// 参数语义对照 NewTalkView 中对 cfg.screenEffect 的各分支处理。
        /// </summary>
        public static string TranslateScreenEffect(List<float> se)
        {
            if (se == null || se.Count == 0) return null;

            int code = (int)se[0];
            if (!ActionNames.TryGetValue(code, out string name))
                return $"未知效果({code})";

            float P(int i) => se.Count > 1 + i ? se[1 + i] : float.NaN;
            bool Has(int i) => se.Count > 1 + i;

            var parts = new List<string>();
            switch (code)
            {
                case 4001: // 震动：[次数]
                    parts.Add($"{(Has(0) ? Mathf.Max((int)P(0), 1) : 1)}次");
                    break;
                case 4002: // 模糊：[程度0~1]
                    if (Has(0)) parts.Add($"程度{Num(P(0))}");
                    break;
                case 4004: // 屏幕物品：[物品/书籍编号, 消息图编号]
                    if (Has(0)) parts.Add($"物品/书籍{(int)P(0)}");
                    if (Has(1)) parts.Add($"消息图{(int)P(1)}");
                    break;
                case 4007: // 手机界面：[背景编号, 人物编号...]
                    if (Has(0)) parts.Add($"背景{(int)P(0)}");
                    for (int i = 1; Has(i); i++) parts.Add($"人物{(int)P(i)}");
                    break;
                case 4015: // 显示CG / 迷你CG：[CG编号]
                case 4019:
                    if (Has(0)) parts.Add($"CG编号{(int)P(0)}");
                    break;
                case 4016: // 漫画分格：[漫画编号, 图数]
                    if (Has(0)) parts.Add($"漫画{(int)P(0)}");
                    if (Has(1)) parts.Add($"图数{(int)P(1)}");
                    break;
                case 4018: // 屏幕图片：[背景图编号]
                    if (Has(0)) parts.Add($"图片编号{(int)P(0)}");
                    break;
                default:
                    for (int i = 0; Has(i); i++) parts.Add(Num(P(i)));
                    break;
            }
            return parts.Count > 0 ? $"{name} ({string.Join("、", parts)})" : name;
        }

        private static string TranslateEntry(List<float> entry, Dictionary<int, PersonCfg> personCfgs)
        {
            int personId = (int)entry[0];
            string person = ResolvePersonName(personId, personCfgs);

            if (entry.Count < 2)
                return $"{person}：（指令不完整）";

            int actionId = (int)entry[1];
            if (!ActionNames.TryGetValue(actionId, out string actionName))
                return $"{person}：未知动作({actionId})";

            string parms = DescribeParams(actionId, entry);
            return string.IsNullOrEmpty(parms)
                ? $"{person}：{actionName}"
                : $"{person}：{actionName} {parms}";
        }

        /// <summary>
        /// 按动作类型解释参数。参数语义对照 NewTalkView.HelpCheckRoleAction
        ///（反编译 L2440-2716），只解释高频动作，其余原样列出数字。
        /// </summary>
        private static string DescribeParams(int actionId, List<float> entry)
        {
            // entry: [人物ID, 动作码, p0, p1, ...]
            float P(int i) => entry.Count > 2 + i ? entry[2 + i] : float.NaN;
            bool Has(int i) => entry.Count > 2 + i;

            var parts = new List<string>();
            switch (actionId)
            {
                case 1001: // 放置：[层级, 方位, 延迟, 入场方向, 抖动]
                case 1002: // 淡入：[层级, 方位, 延迟]
                case 1003: // 底部升起：[层级, 方位, 延迟]
                    if (Has(0) && (int)P(0) != 0) parts.Add($"层级{(int)P(0)}");
                    if (Has(1)) parts.Add($"站{AxisName((int)P(1))}");
                    if (Has(2) && P(2) > 0f) parts.Add($"延迟{Num(P(2))}秒");
                    break;
                case 2001: // 退场：[退场方位, 延迟]
                    if (Has(0) && (int)P(0) != 0) parts.Add($"从{AxisName((int)P(0))}退");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    break;
                case 2002: // 淡出：[延迟]
                    if (Has(0) && P(0) > 0f) parts.Add($"延迟{Num(P(0))}秒");
                    break;
                case 3004: // 横移：[偏移, 延迟, 抖动时长, 移动用时]
                    parts.Add(Has(0) ? $"向{(P(0) >= 0f ? "右" : "左")}{Num(Mathf.Abs(P(0)))}" : "偏移0");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    if (Has(2) && P(2) > 0f) parts.Add($"抖动{Num(P(2))}秒");
                    if (Has(3) && P(3) > 0f) parts.Add($"用时{Num(P(3))}秒");
                    break;
                case 3008: // 纵移：[偏移, 延迟, 抖动时长]
                    parts.Add(Has(0) ? $"向{(P(0) >= 0f ? "上" : "下")}{Num(Mathf.Abs(P(0)))}" : "偏移0");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    if (Has(2) && P(2) > 0f) parts.Add($"抖动{Num(P(2))}秒");
                    break;
                case 3001: // 跳跃：[次数, 延迟, 力度]
                    if (Has(0)) parts.Add($"{Mathf.Max((int)P(0), 1)}次");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    if (Has(2)) parts.Add($"力度{Num(P(2))}");
                    break;
                case 3002: // 抖动：[时长, 延迟]
                    if (Has(0)) parts.Add($"{Num(P(0))}秒");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    break;
                case 3003: // 放大：[倍数(0=默认1.1), 延迟]
                    parts.Add($"{Num(Has(0) && P(0) != 0f ? P(0) : 1.1f)}倍");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    break;
                case 3000: if (Has(0)) parts.Add($"姿势{(int)P(0)}"); break;
                case 3006: if (Has(0)) parts.Add($"服装{(int)P(0)}"); break;
                case 3014: if (Has(0)) parts.Add($"发型{(int)P(0)}"); break;
                case 3009: // 表情：[编号, 延迟]
                    if (Has(0)) parts.Add($"编号{(int)P(0)}");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    break;
                case 3005: // 转身：[延迟]
                case 3007:
                    if (Has(0) && P(0) > 0f) parts.Add($"延迟{Num(P(0))}秒");
                    break;
                default:
                    // 其余动作：原样列出参数，至少能对上动作名
                    for (int i = 0; Has(i); i++) parts.Add(Num(P(i)));
                    break;
            }
            return parts.Count > 0 ? $"({string.Join("、", parts)})" : null;
        }

        /// <summary>
        /// 累加某人物在当前对话中所有横移/纵移指令的偏移量（真实画面像素）。
        /// 供舞台位移示意使用。
        /// </summary>
        public static Vector2 GetTalkOffset(TalkCfg talk, int personId)
        {
            var offset = Vector2.zero;
            if (talk?.roles == null) return offset;

            foreach (var entry in talk.roles)
            {
                if (entry == null || entry.Count < 3) continue;
                if ((int)entry[0] != personId) continue;
                int actionId = (int)entry[1];
                if (actionId == 3004) offset.x += entry[2];
                else if (actionId == 3008) offset.y += entry[2];
            }
            return offset;
        }

        private static string ResolvePersonName(int personId, Dictionary<int, PersonCfg> personCfgs)
        {
            if (personCfgs != null && personCfgs.TryGetValue(personId, out var cfg) &&
                !string.IsNullOrEmpty(cfg?.name))
                return cfg.name;
            return $"人物{personId}";
        }

        /// <summary>TalkAxis：1=左 2=右 3=中（View.Evt.TalkAxis 枚举，已核实）。</summary>
        private static string AxisName(int axis)
        {
            switch (axis)
            {
                case 1: return "左侧";
                case 2: return "右侧";
                case 3: return "中间";
                case -1: return "场外";
                default: return $"方位{axis}";
            }
        }

        /// <summary>数字格式化：去掉多余小数位（300 而非 300.0）。</summary>
        private static string Num(float v) => v.ToString("0.##");
    }

    /// <summary>
    /// 功能 1+2：动作指令 / 屏幕效果的悬浮翻译。
    /// InitUI 时给两个输入框注册鼠标进入/移出回调（游戏自带的
    /// Sdk.AddMouseEnter/AddMouseExit 扩展，原版未占用这两个输入框的回调）：
    /// 悬浮 → 输入框下方弹出提示框显示翻译（置顶渲染 + 屏幕边缘保护）；
    /// 移出 → 隐藏。onEndEdit 后若提示框正显示着，内容同步刷新。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "InitUI")]
    internal static class EvtActionHintInitPatch
    {
        private const string TooltipObjName = "EvtActionTooltip";

        /// <summary>提示框（挂在编辑器根节点下，视图关闭时随之销毁）。</summary>
        private static GameObject _tooltip;
        private static Text _tooltipText;

        /// <summary>提示框当前锚定的输入框（null = 未显示），用于 onEndEdit 时判断是否要刷新内容。</summary>
        private static InputField _hoverInput;

        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                var view = __instance;
                var actionInput = view.input_action;
                var screenInput = view.input_screeneffect;

                // ── 动作指令：悬浮翻译 + 编辑后刷新舞台位移 ──
                if (actionInput != null)
                {
                    actionInput.gameObject.AddMouseEnter(() => ShowFor(view, actionInput));
                    actionInput.gameObject.AddMouseExit(HideTooltip);

                    // 追加监听（原版 OnEndEditAction 已先注册，UnityEvent 按注册
                    // 顺序执行，这里拿到的是解析后的最新 roles）
                    actionInput.onEndEdit.AddListener(_ =>
                    {
                        try
                        {
                            // 原版编辑动作指令后不刷新舞台，这里补上，
                            // 让位移示意（EvtStageOffsetPatch）即时生效
                            var curSelect = Traverse.Create(view).Field("curSelect").GetValue<TalkCfg>();
                            if (curSelect != null)
                                Traverse.Create(view).Method("RefreshRoles").GetValue();

                            if (_hoverInput == actionInput)
                                ShowFor(view, actionInput);
                        }
                        catch (Exception e) { Plugin.Log.LogError($"[EvtActionTooltip.onEndEdit] {e}"); }
                    });
                }

                // ── 屏幕效果：悬浮翻译 ──
                if (screenInput != null)
                {
                    screenInput.gameObject.AddMouseEnter(() => ShowFor(view, screenInput));
                    screenInput.gameObject.AddMouseExit(HideTooltip);
                    screenInput.onEndEdit.AddListener(_ =>
                    {
                        try
                        {
                            if (_hoverInput == screenInput)
                                ShowFor(view, screenInput);
                        }
                        catch (Exception e) { Plugin.Log.LogError($"[EvtScreenTooltip.onEndEdit] {e}"); }
                    });
                }

                Plugin.Log.LogInfo("[EvtActionVisual] 动作指令/屏幕效果悬浮翻译已挂载。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtActionHintInit] {e}");
            }
        }

        /// <summary>切换选中对话（含删除后 Select(null)）时隐藏提示框，避免残留旧内容。</summary>
        internal static void OnSelectChanged()
        {
            HideTooltip();
        }

        private static void ShowFor(ModEvtEditView view, InputField input)
        {
            try
            {
                var t = Traverse.Create(view);
                var curSelect = t.Field("curSelect").GetValue<TalkCfg>();
                if (curSelect == null) return;

                string text;
                try
                {
                    if (input == view.input_action)
                    {
                        var personCfgs = t.Field("personCfgs").GetValue<Dictionary<int, PersonCfg>>();
                        text = TalkActionTranslator.Translate(curSelect.roles, personCfgs)
                               ?? "（当前对话没有动作指令）";
                    }
                    else
                    {
                        text = TalkActionTranslator.TranslateScreenEffect(curSelect.screenEffect)
                               ?? "（当前对话没有屏幕效果）";
                    }
                }
                catch (Exception)
                {
                    // 数字串解析出问题不应打断编辑器
                    text = "内容格式有误，无法解析";
                }

                var tip = GetOrCreateTooltip(view);
                if (tip == null) return;

                _tooltipText.text = text;

                // 背景按文字实际尺寸收缩（preferredWidth/Height 由 TextGenerator
                // 直接计算，不依赖布局组件——动态对象上 ContentSizeFitter 不稳定）
                var bgRt = (RectTransform)tip.transform;
                bgRt.sizeDelta = new Vector2(
                    _tooltipText.preferredWidth + 24f,
                    _tooltipText.preferredHeight + 16f);

                PositionBelow(bgRt, input);

                tip.transform.SetAsLastSibling(); // 置顶渲染，盖在所有控件之上
                tip.SetActive(true);
                _hoverInput = input;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtActionTooltip.Show] {e}");
            }
        }

        /// <summary>
        /// 提示框定位：贴在输入框左下角的下方 6px（向下弹出——上一版向上弹出
        /// 时内容多会溢出屏幕上边缘）。再按编辑器根节点的矩形做边缘保护：
        /// 底部放不下就上抬，右侧超界就左移。全程在根节点本地坐标系计算，
        /// 不受分辨率与画布缩放影响。
        /// </summary>
        private static void PositionBelow(RectTransform bgRt, InputField input)
        {
            var rootRt = bgRt.parent as RectTransform;
            if (rootRt == null) return;

            var inputRt = (RectTransform)input.transform;
            var corners = new Vector3[4];
            inputRt.GetWorldCorners(corners); // 0=左下 1=左上 2=右上 3=右下

            var canvas = input.GetComponentInParent<Canvas>();
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                ? canvas.worldCamera : null;

            Vector2 screenPt = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRt, screenPt, cam, out var local);
            local.y -= 6f;

            // 边缘保护（提示框 pivot 为左上角）
            Rect rr = rootRt.rect;
            Vector2 size = bgRt.sizeDelta;
            if (local.y - size.y < rr.yMin) local.y = rr.yMin + size.y; // 底部放不下 → 上抬
            if (local.x + size.x > rr.xMax) local.x = rr.xMax - size.x; // 右侧超界 → 左移
            if (local.x < rr.xMin) local.x = rr.xMin;

            bgRt.localPosition = new Vector3(local.x, local.y, 0f);
        }

        private static void HideTooltip()
        {
            _hoverInput = null;
            if (_tooltip != null)
                _tooltip.SetActive(false);
        }

        /// <summary>
        /// 提示框 = 深色半透明背景 Image + 文字 Text，挂在编辑器根节点下
        ///（跟随视图销毁）。整体不参与鼠标点击判定，避免遮住输入框后
        /// 触发"移出"回调造成闪烁。
        /// </summary>
        private static GameObject GetOrCreateTooltip(ModEvtEditView view)
        {
            if (_tooltip != null) return _tooltip;

            // group_content 的父节点即编辑器视图根节点
            var root = view.group_content != null ? view.group_content.parent : null;
            if (root == null) return null;

            var existing = root.Find(TooltipObjName);
            if (existing != null)
            {
                _tooltip = existing.gameObject;
                _tooltipText = existing.GetComponentInChildren<Text>();
                return _tooltip;
            }

            var go = new GameObject(TooltipObjName, typeof(RectTransform), typeof(Image));
            var bgRt = go.GetComponent<RectTransform>();
            bgRt.SetParent(root, false);
            bgRt.pivot = new Vector2(0f, 1f); // 左上角为基准，向右下展开
            bgRt.anchorMin = bgRt.anchorMax = new Vector2(0.5f, 0.5f);

            var bg = go.GetComponent<Image>();
            bg.color = new Color(0.08f, 0.08f, 0.1f, 0.95f);
            bg.raycastTarget = false;

            var textGo = new GameObject("text", typeof(RectTransform), typeof(Text));
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.SetParent(bgRt, false);
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(12f, 8f);
            textRt.offsetMax = new Vector2(-12f, -8f);

            var refText = view.input_action != null ? view.input_action.textComponent : null;
            var txt = textGo.GetComponent<Text>();
            txt.font = refText != null ? refText.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.fontSize = refText != null ? refText.fontSize : 18;
            txt.alignment = TextAnchor.UpperLeft;
            txt.color = new Color(0.92f, 0.92f, 0.92f, 1f);
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;

            go.SetActive(false);
            _tooltip = go;
            _tooltipText = txt;
            return _tooltip;
        }
    }

    /// <summary>
    /// 功能 3：舞台位移示意。
    /// OnRenderRole 渲染完立绘后，若当前对话给该人物填了横移/纵移指令，
    /// 就把立绘从槽位基准位置挪到移动终点（偏移 × 0.7 舞台缩放比）。
    /// 无移动指令时归零还原——立绘对象会被复用，必须每次重置。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "OnRenderRole", typeof(Cell_ModEvtRoleItemUI))]
    internal static class EvtStageOffsetPatch
    {
        /// <summary>编辑器舞台相对真实对话画面的缩放比（间距 300→210px、立绘 scale 0.7，已核实）。</summary>
        private const float StageScale = 0.7f;

        /// <summary>各立绘对象的基准位置（key = GameObject.GetInstanceID）。</summary>
        private static readonly Dictionary<int, Vector2> BasePos = new Dictionary<int, Vector2>();

        private static void Postfix(ModEvtEditView __instance, Cell_ModEvtRoleItemUI _cell)
        {
            try
            {
                var keyObj = _cell.GetKeyObj<Cell_NewTalkRoleItemUI>("role");
                if (keyObj == null || keyObj.transform == null) return;

                // 首次见到该立绘对象时记下基准位置（创建后位置固定，可安全缓存）
                int key = keyObj.gameObject.GetInstanceID();
                if (!BasePos.TryGetValue(key, out var basePos))
                {
                    basePos = keyObj.transform.anchoredPosition;
                    BasePos[key] = basePos;
                }

                var offset = Vector2.zero;
                if (_cell.data != null)
                {
                    var curSelect = Traverse.Create(__instance).Field("curSelect").GetValue<TalkCfg>();
                    if (curSelect != null)
                        offset = TalkActionTranslator.GetTalkOffset(curSelect, (int)_cell.data) * StageScale;
                }

                keyObj.transform.anchoredPosition = basePos + offset;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtStageOffset] {e}");
            }
        }
    }

    /// <summary>
    /// 功能 4："预览本句"按钮。
    /// 原版预览入口在外层事件编辑页，且永远从事件第一句开始播
    ///（PreviewTalkView.OnOpen 取 talkId[0]），调中间某句的动画极其低效。
    /// 本补丁在对话编辑器右侧按钮列（group_btn，带 VerticalLayoutGroup，
    /// 克隆按钮会被自动排版）克隆"保存"按钮生成"预览本句"：
    /// 点击后把编辑器内存中的最新数据（含未保存修改）传给 PreviewTalkView，
    /// 并把起始句设为当前选中的对话。
    ///
    /// 数据来源（反编译核实）：
    ///   - talkCfgs/optionCfgs：编辑器内存里本事件的最新数据；
    ///   - personCfgs/audioCfgs：编辑器加载时已合并原版配置，直接传；
    ///   - customBgCfgs/customCGCfgs：仅含 mod 自定义项，需与原版合并后传
    ///    （PreviewTalkView 收到非空表后不再回退原版表）；
    ///   - audioCfgs 是懒加载（LoadAudioCfg），传之前先确保已加载。
    ///
    /// 已知限制（游戏机制决定）：从中间某句开播，更早对话里进场的人物
    /// 不会出现在画面里。首次使用时 Toast 提示一次。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "InitUI")]
    internal static class EvtTalkPreviewPatch
    {
        private const string BtnObjName = "btn_preview_talk";

        private static GameObject _previewBtn;
        private static bool _limitHintShown;

        private const string BtnLabel = "预览本句";

        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                var view = __instance;
                var template = view.btn_save;
                if (template == null || template.gameObject == null) return;

                var parent = template.gameObject.transform.parent;
                if (parent == null) return;

                // 视图重开时按钮可能已存在（随视图销毁重建）
                var existing = parent.Find(BtnObjName);
                GameObject go;
                if (existing != null)
                {
                    go = existing.gameObject;
                }
                else
                {
                    go = UnityEngine.Object.Instantiate(template.gameObject, parent);
                    go.name = BtnObjName;

                    // 克隆体的标签 Text 挂着 LocalizeStringEvent（本地化组件），
                    // 会在本地化表就绪/刷新时把文字重置回源文案"保存"——
                    // 必须先删掉（复用小游戏字段补丁踩过同一个坑的清理方法），
                    // 再设置标签才不会被顶回去。
                    MiniGameUtil.StripBadComponents(go);

                    var label = go.GetComponentInChildren<Text>();
                    if (label != null) label.text = BtnLabel;

                    // 清掉克隆自模板的点击回调（运行时监听不随 Instantiate 复制，
                    // 这里是双保险，防止误触"保存"逻辑）
                    var unityBtn = go.GetComponent<Button>();
                    if (unityBtn != null) unityBtn.onClick.RemoveAllListeners();

                    go.transform.SetAsLastSibling(); // 排在按钮列最底部
                }

                var ub = new UIButton(go);
                ub.AddClick(() => OpenPreview(view));

                _previewBtn = go;
                go.SetActive(false); // 选中对话后才显示（EvtTalkPreviewSelectPatch 控制）

                Plugin.Log.LogInfo("[EvtActionVisual] 预览本句按钮已创建。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtTalkPreviewInit] {e}");
            }
        }

        /// <summary>选中状态变化时联动按钮显隐（与原版 btn_delete 的显隐逻辑保持一致）。</summary>
        internal static void SetVisible(bool visible)
        {
            if (_previewBtn == null) return;
            _previewBtn.SetActive(visible);

            // 标签双保险：万一有其他本地化/界面刷新逻辑把文字改回去，
            // 每次显示时重设一遍（参照小游戏字段补丁的双保险做法）
            if (visible)
            {
                var label = _previewBtn.GetComponentInChildren<Text>();
                if (label != null && label.text != BtnLabel) label.text = BtnLabel;
            }
        }

        private static void OpenPreview(ModEvtEditView view)
        {
            try
            {
                var t = Traverse.Create(view);
                var curSelect = t.Field("curSelect").GetValue<TalkCfg>();
                if (curSelect == null)
                {
                    ToastHelper.Toast("请先在左侧选择一条对话");
                    return;
                }

                var talkList = t.Field("talkCfgs").GetValue<List<TalkCfg>>();
                if (talkList == null || talkList.Count == 0) return;

                // List → Dictionary（编辑中可能出现重复 id，后者覆盖前者，避免异常）
                var talkMap = new Dictionary<int, TalkCfg>();
                foreach (var talk in talkList)
                {
                    if (talk != null) talkMap[talk.id] = talk;
                }

                // 从中间一句开播的两个缺口（用户实测反馈：时而没背景、人物占位乱）：
                //   ① 背景继承：mod 通常只在第一句/换场景句填 bg，其余 bg=0 表示
                //      沿用上一句 → 从中间开播时播放器找不到背景；
                //   ② 前文人物：更早对话里进场的人物没被播到，本句动作作用在
                //      "不在场"的人身上，站位错乱。
                // 修复：把起始句换成一个"补齐了上下文"的副本（复用编辑器自身的
                // FindBgId/FindRoles 回溯逻辑），编辑器内存里的真实数据不动。
                talkMap[curSelect.id] = BuildStartTalkWithContext(t, curSelect);

                var optionMap = Merge(t.Field("optionCfgs").GetValue<Dictionary<int, OptionCfg>>(), Cfg.OptionCfgMap);
                var personMap = t.Field("personCfgs").GetValue<Dictionary<int, PersonCfg>>(); // 已含原版
                var bgMap = Merge(t.Field("customBgCfgs").GetValue<Dictionary<int, BgCfg>>(), Cfg.BgCfgMap);
                var cgMap = Merge(t.Field("customCGCfgs").GetValue<Dictionary<int, CGCfg>>(), Cfg.CGCfgMap);

                // audioCfgs 懒加载，先确保加载（内部已含原版合并）
                t.Method("LoadAudioCfg").GetValue();
                var audioMap = t.Field("audioCfgs").GetValue<Dictionary<int, AudioCfg>>();

                int gradeState = t.Field("gradeState").GetValue<int>();
                var gender = t.Field("gender").GetValue<GenderDefine>();

                // parms 结构与 ModPreviewTipsView.OnClickOK 一致（后 3 项 face/item/book
                // 不传，PreviewTalkView 会自动回退原版配置表）；
                // 起始句列表只放当前对话 → OnOpen 取 list[0] 即从这句开播
                UIMgr.OpenView<PreviewTalkView>(UILayerType.None, null, new object[9]
                {
                    talkMap, optionMap, new List<int> { curSelect.id },
                    gradeState, gender, personMap, bgMap, cgMap, audioMap
                });

                if (!_limitHintShown)
                {
                    _limitHintShown = true;
                    ToastHelper.Toast("从当前对话开始预览，点击画面会继续往后播放，右键随时关闭；前文人物已自动补齐站位（服装/表情按默认显示）");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtTalkPreview] {e}");
            }
        }

        /// <summary>合并配置表：custom 优先，原版补缺（与游戏 mod 合并的"先到先得"语义一致）。</summary>
        private static Dictionary<int, T> Merge<T>(Dictionary<int, T> custom, Dictionary<int, T> global)
        {
            var result = new Dictionary<int, T>();
            if (custom != null)
            {
                foreach (var kvp in custom) result[kvp.Key] = kvp.Value;
            }
            if (global != null)
            {
                foreach (var kvp in global)
                {
                    if (!result.ContainsKey(kvp.Key)) result.Add(kvp.Key, kvp.Value);
                }
            }
            return result;
        }

        /// <summary>
        /// 生成起始句的"补齐上下文"副本：
        ///   - bg<=0 时用编辑器的 FindBgId(talkId) 回溯继承背景；
        ///   - 用编辑器的 FindRoles(talkId) 算出此刻在场的人物及方位，为
        ///     "不是本句进场"的人合成 [人物, 1001放置, 层级1, 方位] 指令，
        ///     插在本句原有动作之前（瞬间站定，无入场动画）。
        /// 仅修改传给预览器的副本；roles 列表为浅拷贝 + 前插，原有条目
        /// 与编辑器共享引用但双方都只读，安全。
        /// 已知残留：前文人物的服装/表情/累计位移不还原，只补站位。
        /// </summary>
        private static TalkCfg BuildStartTalkWithContext(Traverse t, TalkCfg cur)
        {
            var copy = new TalkCfg
            {
                audio = cur.audio,
                bg = cur.bg,
                check = cur.check,
                content = cur.content,
                effect = cur.effect,
                effect2 = cur.effect2,
                highlights = cur.highlights,
                id = cur.id,
                maxoptions = cur.maxoptions,
                miniGame = cur.miniGame,
                nextTalk = cur.nextTalk,
                nextTalk2 = cur.nextTalk2,
                option = cur.option,
                replace = cur.replace,
                roleIds = cur.roleIds,
                roleName = cur.roleName,
                screenEffect = cur.screenEffect,
                showTxt = cur.showTxt,
                time = cur.time,
                vocals = cur.vocals,
                roles = cur.roles != null
                    ? new List<List<float>>(cur.roles)
                    : new List<List<float>>(),
            };

            // ① 背景继承
            try
            {
                if (copy.bg <= 0)
                {
                    int inherited = t.Method("FindBgId", new[] { typeof(int) }).GetValue<int>(cur.id);
                    if (inherited > 0) copy.bg = inherited;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[EvtTalkPreview] 背景回溯失败: {e.Message}"); }

            // ② 前文在场人物补齐
            try
            {
                var inScene = t.Method("FindRoles", new[] { typeof(int) })
                    .GetValue<Dictionary<int, TalkAxis>>(cur.id);
                if (inScene != null && inScene.Count > 0)
                {
                    // 本句自己进场的人物不合成（否则"先放置又淡入"动作重复）
                    var entersThisTalk = new HashSet<int>();
                    if (cur.roles != null)
                    {
                        foreach (var e in cur.roles)
                        {
                            if (e == null || e.Count < 2) continue;
                            int code = (int)e[1];
                            bool isEnter = code == 1001 || code == 1002 || code == 1003;
                            if (Cfg.TalkAnimeCfgMap.TryGetValue(code, out var ac))
                                isEnter = ac.type == 1; // 配置表为准（type 1 = 进场类）
                            if (isEnter) entersThisTalk.Add((int)e[0]);
                        }
                    }

                    int insertAt = 0;
                    foreach (var kvp in inScene)
                    {
                        if (entersThisTalk.Contains(kvp.Key)) continue;
                        copy.roles.Insert(insertAt++, new List<float>
                        {
                            kvp.Key, 1001f, 1f, (float)kvp.Value
                        });
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[EvtTalkPreview] 人物回溯失败: {e.Message}"); }

            return copy;
        }
    }

    /// <summary>
    /// 选中对话变化时：隐藏悬浮提示框（防旧内容残留）+ 联动"预览本句"按钮显隐。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "Select", typeof(TalkCfg))]
    internal static class EvtTalkPreviewSelectPatch
    {
        private static void Postfix(TalkCfg _cfg)
        {
            try
            {
                EvtActionHintInitPatch.OnSelectChanged();
                EvtTalkPreviewPatch.SetVisible(_cfg != null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtTalkPreviewSelect] {e}");
            }
        }
    }
}
