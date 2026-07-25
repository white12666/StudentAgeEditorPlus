using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// Logic 页不能把 Talk 与 Option 的 effect/effect2 当成同一种语义：
    /// Talk 的 effect 在正文结束时执行，effect2 只被少数原生小游戏读取；
    /// Option 则在选择时按 check 读取 effect/effect2。
    /// </summary>
    internal sealed class StoryGraphLogicInspectorPresentation
    {
        internal string Intro;
        internal string VisibilityConditionLabel;
        internal string VisibilityConditionPlaceholder;
        internal string ResultConditionLabel;
        internal string ResultConditionPlaceholder;
        internal string RoutesLabel;
        internal string SuccessRouteLabel;
        internal string FailureRouteLabel;
        internal string PrimaryLabel;
        internal string SecondaryLabel;
        internal string PrimaryPlaceholder;
        internal string SecondaryPlaceholder;
        internal bool ShowOptionModeSelector;
        internal bool IsConditionalOption;
        internal bool ShowVisibilityCondition;
        internal bool ShowResultCondition;
        internal bool ShowFailureRoute;
        internal bool ShowSecondary;

        internal static StoryGraphLogicInspectorPresentation Create(
            bool isTalk,
            bool hasMiniGame,
            bool hasFailureData)
        {
            // 兼容旧调用：过去 Option 页始终展开条件分支字段。
            return Create(isTalk, hasMiniGame, hasFailureData, !isTalk);
        }

        internal static StoryGraphLogicInspectorPresentation Create(
            bool isTalk,
            bool hasMiniGame,
            bool hasFailureData,
            bool optionConditional)
        {
            if (!isTalk)
            {
                if (!optionConditional)
                {
                    return new StoryGraphLogicInspectorPresentation
                    {
                        Intro = hasMiniGame
                            ? "先决定选项是否显示；玩家选择后执行效果并进入小游戏。小游戏接管最终去向，需要真假判断时再切换为“根据条件分支”。"
                            : "先决定选项是否显示；玩家选择后直接执行效果并进入下一句。需要两种结果时，再切换为“根据条件分支”。",
                        VisibilityConditionLabel =
                            "① 什么时候显示这个选项（可留空）",
                        VisibilityConditionPlaceholder =
                            "每行一个条件，例如：3, 1, 事件编号",
                        ResultConditionLabel =
                            "选择后判断（当前直接进入模式不使用）",
                        ResultConditionPlaceholder =
                            "切换为“根据条件分支”后填写",
                        RoutesLabel = hasMiniGame
                            ? "② 小游戏结束后去哪里"
                            : "② 玩家选择后去哪里",
                        SuccessRouteLabel = hasMiniGame
                            ? "小游戏成功（一个编号=通用；两个=男, 女）"
                            : "进入剧情（一个编号=通用；两个=男, 女）",
                        FailureRouteLabel =
                            "小游戏失败（一个编号=通用；两个=男, 女）",
                        PrimaryLabel = hasMiniGame
                            ? "③ 选择后 / 小游戏成功时的效果"
                            : "③ 玩家选择后执行的效果",
                        SecondaryLabel = "小游戏失败时可能执行的效果",
                        PrimaryPlaceholder =
                            "选择后执行；每行：效果码, 参数…",
                        SecondaryPlaceholder =
                            "仅部分小游戏失败时读取；每行：效果码, 参数…",
                        ShowOptionModeSelector = true,
                        IsConditionalOption = false,
                        ShowVisibilityCondition = true,
                        ShowResultCondition = false,
                        ShowFailureRoute = hasMiniGame,
                        ShowSecondary = hasMiniGame,
                    };
                }
                return new StoryGraphLogicInspectorPresentation
                {
                    Intro =
                        hasMiniGame
                            ? "先决定选项是否显示；玩家选择后再判断执行哪组效果。小游戏会接管最终去向，但选择后的判断仍会执行。"
                            : "先决定选项是否显示；玩家选择后再判断走哪条剧情并执行对应效果。条件留空时视为满足。",
                    VisibilityConditionLabel =
                        "① 什么时候显示这个选项（可留空）",
                    VisibilityConditionPlaceholder =
                        "每行一个条件，例如：3, 1, 事件编号",
                    ResultConditionLabel =
                        "② 玩家选择后判断什么（可留空）",
                    ResultConditionPlaceholder =
                        "每行一个条件；全部满足时走成立路线",
                    RoutesLabel = hasMiniGame
                        ? "③ 小游戏接管剧情去向"
                        : "③ 判断后去哪里",
                    SuccessRouteLabel = hasMiniGame
                        ? "小游戏成功（一个编号=通用；两个=男, 女）"
                        : "条件成立（一个编号=通用；两个=男, 女）",
                    FailureRouteLabel = hasMiniGame
                        ? "小游戏失败（一个编号=通用；两个=男, 女）"
                        : "条件不成立（一个编号=通用；两个=男, 女）",
                    PrimaryLabel = hasMiniGame
                        ? "④ 条件成立 / 小游戏成功时的效果"
                        : "④ 条件成立时执行的效果",
                    SecondaryLabel = hasMiniGame
                        ? "条件不成立 / 小游戏失败时的效果"
                        : "条件不成立时执行的效果",
                    PrimaryPlaceholder =
                        "条件成立时执行；每行：效果码, 参数…",
                    SecondaryPlaceholder =
                        "条件不成立时执行；每行：效果码, 参数…",
                    ShowOptionModeSelector = true,
                    IsConditionalOption = true,
                    ShowVisibilityCondition = true,
                    ShowResultCondition = true,
                    ShowFailureRoute = true,
                    ShowSecondary = true,
                };
            }

            if (hasMiniGame)
            {
                return new StoryGraphLogicInspectorPresentation
                {
                    Intro =
                        "小游戏会接管这句结束后的剧情去向；下面保留原判断，移除小游戏后会继续生效。部分玩法还会读取成功 / 失败效果。",
                    ResultConditionLabel =
                        "① 普通剧情判断（小游戏期间不决定去向）",
                    ResultConditionPlaceholder =
                        "每行一个条件，例如：3, 1, 事件编号",
                    RoutesLabel = "② 小游戏结束后去哪里",
                    SuccessRouteLabel =
                        "小游戏成功（一个编号=通用；两个=男, 女）",
                    FailureRouteLabel =
                        "小游戏失败（一个编号=通用；两个=男, 女）",
                    PrimaryLabel = "③ 本句结束 / 小游戏成功时的效果",
                    SecondaryLabel = "小游戏失败时可能执行的效果",
                    PrimaryPlaceholder =
                        "正文结束时执行；每行：效果码, 参数…",
                    SecondaryPlaceholder =
                        "仅部分小游戏失败时读取；每行：效果码, 参数…",
                    ShowResultCondition = true,
                    ShowFailureRoute = true,
                    ShowSecondary = true,
                };
            }

            if (hasFailureData)
            {
                return new StoryGraphLogicInspectorPresentation
                {
                    Intro =
                        "判断决定这句结束后走哪条剧情；效果在正文结束时执行。当前还保留了一组旧的失败效果，但普通对话不会读取它。",
                    ResultConditionLabel = "① 这句结束后判断什么（可留空）",
                    ResultConditionPlaceholder =
                        "每行一个条件；全部满足时走成立路线",
                    RoutesLabel = "② 判断后去哪里",
                    SuccessRouteLabel =
                        "条件成立（一个编号=通用；两个=男, 女）",
                    FailureRouteLabel =
                        "条件不成立（留空或 0=沿用成立路线）",
                    PrimaryLabel = "③ 本句正文结束时执行的效果",
                    SecondaryLabel = "保留的失败效果（普通对话不执行）",
                    PrimaryPlaceholder =
                        "正文结束时执行；每行：效果码, 参数…",
                    SecondaryPlaceholder =
                        "兼容既存数据；普通对话运行时不执行",
                    ShowResultCondition = true,
                    ShowFailureRoute = true,
                    ShowSecondary = true,
                };
            }

            return new StoryGraphLogicInspectorPresentation
            {
                Intro =
                    "判断决定这句结束后走哪条剧情；效果在正文结束时执行。判断留空时始终走成立路线。",
                ResultConditionLabel = "① 这句结束后判断什么（可留空）",
                ResultConditionPlaceholder =
                    "每行一个条件，例如：3, 1, 事件编号",
                RoutesLabel = "② 判断后去哪里",
                SuccessRouteLabel =
                    "条件成立（一个编号=通用；两个=男, 女）",
                FailureRouteLabel =
                    "条件不成立（留空或 0=沿用成立路线）",
                PrimaryLabel = "③ 本句正文结束时执行的效果",
                SecondaryLabel = "部分小游戏失败效果 effect2",
                PrimaryPlaceholder =
                    "正文结束时执行；每行：效果码, 参数…",
                SecondaryPlaceholder =
                    "普通对话不执行；仅部分小游戏失败时读取",
                ShowVisibilityCondition = false,
                ShowResultCondition = true,
                ShowFailureRoute = true,
                ShowSecondary = false,
            };
        }
    }

    /// <summary>
    /// Logic 页按照运行时真实顺序纵向排列：显示条件 → 选择后判断 →
    /// 剧情去向 → 效果。Option 比 Talk 多一段显示条件。
    /// </summary>
    internal sealed class StoryGraphLogicInspectorLayout
    {
        internal float IntroY;
        internal float IntroHeight;
        internal float ModeY;
        internal float ModeHeight;
        internal float VisibilityLabelY;
        internal float VisibilityInputY;
        internal float ResultLabelY;
        internal float ResultInputY;
        internal float ConditionPreviewY;
        internal float ConditionPreviewHeight;
        internal float HelpButtonY;
        internal float RoutesLabelY;
        internal float SuccessRouteY;
        internal float FailureRouteY;
        internal float PrimaryLabelY;
        internal float PrimaryInputY;
        internal float PrimaryInputHeight;
        internal float SecondaryLabelY;
        internal float SecondaryInputY;
        internal float SecondaryInputHeight;
        internal float FooterY;
        internal float PageHeight;

        internal static StoryGraphLogicInspectorLayout Create(
            bool showVisibility,
            bool showSecondary,
            float introHeight,
            float previewHeight)
        {
            return Create(
                showVisibility, false, true, true, showSecondary,
                introHeight, previewHeight);
        }

        internal static StoryGraphLogicInspectorLayout Create(
            bool showVisibility,
            bool showMode,
            bool showResult,
            bool showFailureRoute,
            bool showSecondary,
            float introHeight,
            float previewHeight)
        {
            introHeight = Math.Max(48f, introHeight);
            previewHeight = Math.Max(42f, previewHeight);
            var result = new StoryGraphLogicInspectorLayout();
            float cursor = -4f;
            result.IntroY = Take(ref cursor, introHeight, 10f);
            result.IntroHeight = introHeight;
            if (showMode)
            {
                result.ModeY = Take(ref cursor, 40f, 12f);
                result.ModeHeight = 40f;
            }
            else
            {
                result.ModeY = cursor;
                result.ModeHeight = 0f;
            }
            if (showVisibility)
            {
                result.VisibilityLabelY = Take(ref cursor, 22f, 6f);
                result.VisibilityInputY = Take(ref cursor, 84f, 10f);
            }
            else
            {
                result.VisibilityLabelY = cursor;
                result.VisibilityInputY = cursor;
            }
            if (showResult)
            {
                result.ResultLabelY = Take(ref cursor, 22f, 6f);
                result.ResultInputY = Take(ref cursor, 84f, 8f);
            }
            else
            {
                result.ResultLabelY = cursor;
                result.ResultInputY = cursor;
            }
            result.ConditionPreviewY = Take(ref cursor, previewHeight, 8f);
            result.ConditionPreviewHeight = previewHeight;
            result.HelpButtonY = Take(ref cursor, 36f, 14f);
            result.RoutesLabelY = Take(ref cursor, 22f, 6f);
            result.SuccessRouteY = Take(ref cursor, 62f, 6f);
            if (showFailureRoute)
                result.FailureRouteY = Take(ref cursor, 62f, 14f);
            else
                result.FailureRouteY = cursor;
            result.PrimaryLabelY = Take(ref cursor, 22f, 6f);
            result.PrimaryInputHeight = showSecondary ? 116f : 164f;
            result.PrimaryInputY = Take(
                ref cursor, result.PrimaryInputHeight, 10f);
            if (showSecondary)
            {
                result.SecondaryLabelY = Take(ref cursor, 22f, 6f);
                result.SecondaryInputHeight = 116f;
                result.SecondaryInputY = Take(
                    ref cursor, result.SecondaryInputHeight, 12f);
            }
            else
            {
                result.SecondaryLabelY = cursor;
                result.SecondaryInputY = cursor;
                result.SecondaryInputHeight = 0f;
            }
            result.FooterY = Take(ref cursor, 40f, 16f);
            result.PageHeight = Math.Max(680f, 0f - cursor);
            return result;
        }

        private static float Take(
            ref float cursor, float height, float bottomGap)
        {
            float top = cursor;
            cursor -= height + bottomGap;
            return top;
        }
    }

    /// <summary>把原生数字条件转成不依赖存档状态的作者向摘要。</summary>
    internal static class StoryGraphConditionText
    {
        internal static string Describe(IEnumerable<List<double>> values)
        {
            List<List<double>> rows = (values ?? Enumerable.Empty<List<double>>())
                .Where(row => row != null && row.Count > 0)
                .ToList();
            if (rows.Count == 0) return "始终满足（未设置条件）";
            const int maximum = 4;
            string result = string.Join("；", rows.Take(maximum)
                .Select(DescribeRow).ToArray());
            if (rows.Count > maximum)
                result += "；…另 " + (rows.Count - maximum) + " 条";
            return result;
        }

        private static string DescribeRow(List<double> row)
        {
            int type = (int)row[0];
            int subtype = row.Count > 1 ? (int)row[1] : 0;
            if (type == 0 && subtype == 1 && row.Count > 2)
                return "随机概率 " + Format(row[2] * 100d) + "%";
            if (type == 3 && row.Count > 2)
            {
                string target = Format(row[2]);
                switch (subtype)
                {
                    case 1: return "事件 " + target + " 已发生";
                    case -1: return "事件 " + target + " 未发生";
                    case 2: return "选项 " + target + " 已选择";
                    case -2: return "选项 " + target + " 未选择";
                    case 3: return "对话 " + target + " 已出现";
                    case -3: return "对话 " + target + " 未出现";
                    case 30: return "本回合已出现对话 " + target;
                    case -30: return "本回合未出现对话 " + target;
                }
            }
            string parameters = row.Count > 2
                ? string.Join(", ", row.Skip(2).Select(Format).ToArray())
                : "无额外参数";
            return TypeName(type) + " · 子类型 " + subtype
                   + " · " + parameters;
        }

        private static string TypeName(int type)
        {
            switch (type)
            {
                case 0: return "概率";
                case 1: return "年龄";
                case 2: return "日期";
                case 3: return "剧情记录";
                case 4: return "人物属性";
                case 5: return "技能";
                case 6: return "人物";
                case 7: return "关系";
                case 8: return "状态";
                case 10: return "知识";
                case 11: return "好感";
                case 12: return "行动";
                case 13: return "阅读";
                case 15: return "目标";
                case 22: return "NPC";
                case 23: return "社交媒体";
                case 31: return "谈判";
                case 34: return "小游戏记录";
                case 35: return "写作";
                case 38: return "自定义数据";
                case 42: return "篮球";
                case 52: return "关系阶段";
                case 60: return "物品";
                case 90: return "聚会";
                case 100: return "其他属性";
                case 101: return "界面状态";
                case 111: return "事件变量";
                case 200: return "结局";
                case 333: return "组合概率";
                case 999: return "特殊条件";
                default: return "条件类型 " + type;
            }
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// MiniGame 页直接面向剧情作者，不暴露运行时字段名。步骤编号是真实的
    /// 操作顺序：先选玩法，再补充该玩法的设置，最后连接结果剧情。
    /// </summary>
    internal sealed class StoryGraphMiniGameInspectorPresentation
    {
        internal string Intro;
        internal string InputLabel;
        internal string InputPlaceholder;
        internal string FlowLabel;
        internal string FlowBody;
        internal string SpecialLabel;
        internal string SpecialBody;

        internal static StoryGraphMiniGameInspectorPresentation Create(
            bool isTalk)
        {
            return new StoryGraphMiniGameInspectorPresentation
            {
                Intro = isTalk
                    ? "让玩家在这句对话结束后进入小游戏。按顺序完成下面三步；设置完整时会自动保存。"
                    : "让玩家选择这个选项后进入小游戏。按顺序完成下面三步；设置完整时会自动保存。",
                InputLabel = "② 按提示补充玩法设置",
                InputPlaceholder = "先在上方选择小游戏",
                FlowLabel = "③ 连接小游戏后的剧情",
                FlowBody =
                    "普通玩法有“成功”和“失败”两个结果。回到剧情图，把这两个出口分别连到后续对话；上方会提示哪个出口还未连接。",
                SpecialLabel = "讲价 / 连线：按成绩进入不同剧情",
                SpecialBody =
                    "这两种玩法不使用普通的成功、失败出口。选择后，请在上方依次填写每种成绩对应的对话编号；剧情图会直接画出这些连线。",
            };
        }
    }

    /// <summary>
    /// MiniGame 页的纵向布局。所有坐标由实际文本高度累计，说明换行后不会
    /// 覆盖下一控件，也不再用固定的大块空白来预留最坏情况。选择按钮放在
    /// 输入框之前，让阅读顺序与“选择 → 设置 → 连接”的作者流程一致。
    /// </summary>
    internal sealed class StoryGraphMiniGameInspectorLayout
    {
        internal float IntroY;
        internal float IntroHeight;
        internal float InputLabelY;
        internal float InputY;
        internal float SummaryY;
        internal float SummaryHeight;
        internal float FlowY;
        internal float FlowHeight;
        internal float ButtonsY;
        internal float CommonLabelY;
        internal float CommonBodyY;
        internal float CommonBodyHeight;
        internal float SpecialLabelY;
        internal float SpecialBodyY;
        internal float SpecialBodyHeight;
        internal float PageHeight;

        internal static StoryGraphMiniGameInspectorLayout Create(
            float introHeight,
            float summaryHeight,
            float flowHeight,
            float commonBodyHeight,
            float specialBodyHeight)
        {
            introHeight = Math.Max(44f, introHeight);
            summaryHeight = Math.Max(38f, summaryHeight);
            flowHeight = Math.Max(0f, flowHeight);
            commonBodyHeight = Math.Max(54f, commonBodyHeight);
            specialBodyHeight = Math.Max(54f, specialBodyHeight);

            var result = new StoryGraphMiniGameInspectorLayout();
            float cursor = -4f;
            result.IntroY = Take(ref cursor, introHeight, 12f);
            result.IntroHeight = introHeight;
            result.ButtonsY = Take(ref cursor, 40f, 14f);
            result.InputLabelY = Take(ref cursor, 22f, 6f);
            result.InputY = Take(ref cursor, 46f, 10f);
            result.SummaryY = Take(ref cursor, summaryHeight, 8f);
            result.SummaryHeight = summaryHeight;
            if (flowHeight > 0f)
            {
                result.FlowY = Take(ref cursor, flowHeight, 12f);
                result.FlowHeight = flowHeight;
            }
            else
            {
                result.FlowY = cursor;
                result.FlowHeight = 0f;
            }
            result.CommonLabelY = Take(ref cursor, 22f, 6f);
            result.CommonBodyY = Take(ref cursor, commonBodyHeight, 14f);
            result.CommonBodyHeight = commonBodyHeight;
            result.SpecialLabelY = Take(ref cursor, 22f, 6f);
            result.SpecialBodyY = Take(ref cursor, specialBodyHeight, 18f);
            result.SpecialBodyHeight = specialBodyHeight;
            result.PageHeight = Math.Max(560f, 0f - cursor);
            return result;
        }

        private static float Take(
            ref float cursor, float height, float bottomGap)
        {
            float top = cursor;
            cursor -= height + bottomGap;
            return top;
        }
    }

    /// <summary>
    /// Inspector summaries must never grow without a bound because names and
    /// native arrays can come from third-party workshop data.  Shortening is
    /// explicit, so authors know that the editable source still contains more.
    /// </summary>
    internal static class StoryGraphInspectorText
    {
        internal static string CompactCharacters(string value, int maximum)
        {
            string text = value ?? string.Empty;
            if (maximum < 24 || text.Length <= maximum) return text;
            const string suffix = "…（内容过长，已收起）";
            return text.Substring(0, Math.Max(1, maximum - suffix.Length))
                       .TrimEnd()
                   + suffix;
        }

        internal static string Ellipsize(string value, int maximum)
        {
            string text = value ?? string.Empty;
            if (maximum < 2 || text.Length <= maximum) return text;
            return text.Substring(0, maximum - 1).TrimEnd() + "…";
        }

        internal static string CompactLines(string value, int maximumLines)
        {
            string text = (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            string[] lines = text.Split('\n');
            if (maximumLines < 2 || lines.Length <= maximumLines) return text;
            int visible = maximumLines - 1;
            return string.Join("\n", lines.Take(visible).ToArray())
                   + "\n…（另 " + (lines.Length - visible) + " 行）";
        }

        internal static string JoinBounded(
            IEnumerable<string> items, int maximumItems, string unit)
        {
            List<string> values = (items ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (values.Count == 0) return string.Empty;
            int shown = Math.Max(1, Math.Min(maximumItems, values.Count));
            string result = string.Join("、", values.Take(shown).ToArray());
            if (shown < values.Count)
                result += "、…（另 " + (values.Count - shown)
                          + (string.IsNullOrEmpty(unit) ? " 项" : " " + unit)
                          + "）";
            return result;
        }
    }
}
