using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Config;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>TalkCfg 中与演出编排相关的原生字段快照。</summary>
    internal sealed class StoryGraphPerformanceData
    {
        internal int BackgroundId;
        internal int AudioId;
        internal List<int> RoleIds = new List<int>();
        internal List<List<float>> RoleActions = new List<List<float>>();
        internal List<int> Highlights = new List<int>();
        internal List<float> ScreenEffect = new List<float>();
        internal List<float> Vocals = new List<float>();
        internal List<List<float>> SuccessEffects = new List<List<float>>();
        internal List<List<float>> FailureEffects = new List<List<float>>();

        internal static StoryGraphPerformanceData FromTalk(TalkCfg talk)
        {
            return new StoryGraphPerformanceData
            {
                BackgroundId = talk != null ? talk.bg : 0,
                AudioId = talk != null ? talk.audio : 0,
                RoleIds = Clone(talk != null ? talk.roleIds : null),
                RoleActions = CloneNested(talk != null ? talk.roles : null),
                Highlights = Clone(talk != null ? talk.highlights : null),
                ScreenEffect = Clone(talk != null ? talk.screenEffect : null),
                Vocals = Clone(talk != null ? talk.vocals : null),
                SuccessEffects = CloneNested(talk != null ? talk.effect : null),
                FailureEffects = CloneNested(talk != null ? talk.effect2 : null),
            };
        }

        internal StoryGraphPerformanceData Clone()
        {
            return new StoryGraphPerformanceData
            {
                BackgroundId = BackgroundId,
                AudioId = AudioId,
                RoleIds = Clone(RoleIds),
                RoleActions = CloneNested(RoleActions),
                Highlights = Clone(Highlights),
                ScreenEffect = Clone(ScreenEffect),
                Vocals = Clone(Vocals),
                SuccessEffects = CloneNested(SuccessEffects),
                FailureEffects = CloneNested(FailureEffects),
            };
        }

        internal void ApplyTo(TalkCfg talk)
        {
            if (talk == null) return;
            talk.bg = BackgroundId;
            talk.audio = AudioId;
            talk.roleIds = Clone(RoleIds);
            talk.roles = CloneNested(RoleActions);
            talk.highlights = Clone(Highlights);
            talk.screenEffect = Clone(ScreenEffect);
            talk.vocals = Clone(Vocals);
            talk.effect = CloneNested(SuccessEffects);
            talk.effect2 = CloneNested(FailureEffects);
        }

        internal bool ContentEquals(StoryGraphPerformanceData other)
        {
            return other != null
                && BackgroundId == other.BackgroundId
                && AudioId == other.AudioId
                && SequenceEqual(RoleIds, other.RoleIds)
                && NestedContentEquals(RoleActions, other.RoleActions)
                && SequenceEqual(Highlights, other.Highlights)
                && SequenceEqual(ScreenEffect, other.ScreenEffect)
                && SequenceEqual(Vocals, other.Vocals)
                && NestedContentEquals(SuccessEffects, other.SuccessEffects)
                && NestedContentEquals(FailureEffects, other.FailureEffects);
        }

        internal bool TryValidate(out string error)
        {
            error = null;
            if (!ValidateFinite(ScreenEffect, "屏幕效果", out error)
                || !ValidateFinite(Vocals, "语音参数", out error)
                || !TryValidateNested(RoleActions, "人物动作", 2, out error)
                || !TryValidateNested(SuccessEffects, "条件成立效果", 1, out error)
                || !TryValidateNested(FailureEffects, "条件失败效果", 1, out error))
                return false;
            if (ScreenEffect != null && ScreenEffect.Count > 0
                && !IsInteger(ScreenEffect[0]))
            {
                error = "屏幕效果的第一项必须是整数效果码。";
                return false;
            }
            for (int i = 0; RoleActions != null && i < RoleActions.Count; i++)
            {
                List<float> row = RoleActions[i];
                if (!IsInteger(row[0]) || !IsInteger(row[1]))
                {
                    error = "人物动作第 " + (i + 1)
                          + " 行的人物 ID 和动作码必须是整数。";
                    return false;
                }
            }
            return true;
        }

        internal static bool TryValidateNested(
            IList<List<float>> values, string label, int minColumns, out string error)
        {
            error = null;
            if (values == null) return true;
            for (int i = 0; i < values.Count; i++)
            {
                List<float> row = values[i];
                if (row == null || row.Count < minColumns)
                {
                    error = label + "第 " + (i + 1) + " 行至少需要 "
                          + minColumns + " 个数字。";
                    return false;
                }
                if (!ValidateFinite(row, label + "第 " + (i + 1) + " 行", out error))
                    return false;
            }
            return true;
        }

        private static bool ValidateFinite(
            IEnumerable<float> values, string label, out string error)
        {
            error = null;
            if (values == null) return true;
            foreach (float value in values)
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    error = label + "不能包含 NaN 或 Infinity。";
                    return false;
                }
            }
            return true;
        }

        private static bool IsInteger(float value)
        {
            return value >= int.MinValue && value <= int.MaxValue
                && Math.Abs(value - (float)Math.Round(value)) < 0.0001f;
        }

        internal static List<T> Clone<T>(IEnumerable<T> values)
        {
            return values != null ? new List<T>(values) : new List<T>();
        }

        internal static List<List<float>> CloneNested(IEnumerable<List<float>> values)
        {
            var result = new List<List<float>>();
            if (values == null) return result;
            foreach (List<float> row in values)
                result.Add(row != null ? new List<float>(row) : new List<float>());
            return result;
        }

        private static bool SequenceEqual<T>(IList<T> left, IList<T> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (!EqualityComparer<T>.Default.Equals(left[i], right[i])) return false;
            return true;
        }

        internal static bool NestedContentEquals(
            IList<List<float>> left, IList<List<float>> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (!SequenceEqual(left[i], right[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// TalkCfg / OptionCfg 的完整原生逻辑快照。Talk 使用 ResultConditions、
    /// SuccessTargets、FailureTargets；Option 额外使用 VisibilityConditions。
    /// effect/effect2 与条件、去向一起提交，保证一次输入只产生一条撤销记录。
    /// </summary>
    internal sealed class StoryGraphLogicData
    {
        internal List<List<double>> VisibilityConditions =
            new List<List<double>>();
        internal List<List<double>> ResultConditions =
            new List<List<double>>();
        internal List<int> SuccessTargets = new List<int>();
        internal List<int> FailureTargets = new List<int>();
        internal List<List<float>> SuccessEffects = new List<List<float>>();
        internal List<List<float>> FailureEffects = new List<List<float>>();

        internal static StoryGraphLogicData FromTalk(TalkCfg talk)
        {
            return new StoryGraphLogicData
            {
                ResultConditions = CloneDoubleNested(
                    talk != null ? talk.check : null),
                SuccessTargets = talk != null && talk.nextTalk != null
                    ? new List<int>(talk.nextTalk) : new List<int>(),
                FailureTargets = talk != null && talk.nextTalk2 != null
                    ? new List<int>(talk.nextTalk2) : new List<int>(),
                SuccessEffects = StoryGraphPerformanceData.CloneNested(
                    talk != null ? talk.effect : null),
                FailureEffects = StoryGraphPerformanceData.CloneNested(
                    talk != null ? talk.effect2 : null),
            };
        }

        internal static StoryGraphLogicData FromOption(OptionCfg option)
        {
            return new StoryGraphLogicData
            {
                VisibilityConditions = CloneDoubleNested(
                    option != null ? option.precondition : null),
                ResultConditions = CloneDoubleNested(
                    option != null ? option.check : null),
                SuccessTargets = option != null && option.talkId != null
                    ? new List<int>(option.talkId) : new List<int>(),
                FailureTargets = option != null && option.talkId2 != null
                    ? new List<int>(option.talkId2) : new List<int>(),
                SuccessEffects = StoryGraphPerformanceData.CloneNested(
                    option != null ? option.effect : null),
                FailureEffects = StoryGraphPerformanceData.CloneNested(
                    option != null ? option.effect2 : null),
            };
        }

        internal StoryGraphLogicData Clone()
        {
            return new StoryGraphLogicData
            {
                VisibilityConditions = CloneDoubleNested(VisibilityConditions),
                ResultConditions = CloneDoubleNested(ResultConditions),
                SuccessTargets = SuccessTargets != null
                    ? new List<int>(SuccessTargets) : new List<int>(),
                FailureTargets = FailureTargets != null
                    ? new List<int>(FailureTargets) : new List<int>(),
                SuccessEffects = StoryGraphPerformanceData.CloneNested(SuccessEffects),
                FailureEffects = StoryGraphPerformanceData.CloneNested(FailureEffects),
            };
        }

        internal bool ContentEquals(StoryGraphLogicData other)
        {
            return other != null
                && DoubleNestedContentEquals(
                    VisibilityConditions, other.VisibilityConditions)
                && DoubleNestedContentEquals(
                    ResultConditions, other.ResultConditions)
                && SequenceEqual(SuccessTargets, other.SuccessTargets)
                && SequenceEqual(FailureTargets, other.FailureTargets)
                && StoryGraphPerformanceData.NestedContentEquals(
                    SuccessEffects, other.SuccessEffects)
                && StoryGraphPerformanceData.NestedContentEquals(
                    FailureEffects, other.FailureEffects);
        }

        internal bool TryValidate(out string error)
        {
            return TryValidateConditions(
                       VisibilityConditions, "显示条件", out error)
                   && TryValidateConditions(
                       ResultConditions, "结果判断", out error)
                   && TryValidateTargets(
                       SuccessTargets, "成立去向", out error)
                   && TryValidateTargets(
                       FailureTargets, "不成立去向", out error)
                   && StoryGraphPerformanceData.TryValidateNested(
                       SuccessEffects, "条件成立效果", 1, out error)
                   && StoryGraphPerformanceData.TryValidateNested(
                       FailureEffects, "条件失败效果", 1, out error);
        }

        internal void ApplyTo(TalkCfg talk)
        {
            if (talk == null) return;
            talk.check = CloneDoubleNested(ResultConditions);
            talk.nextTalk = SuccessTargets != null
                ? new List<int>(SuccessTargets) : new List<int>();
            talk.nextTalk2 = FailureTargets != null
                ? new List<int>(FailureTargets) : new List<int>();
            talk.effect = StoryGraphPerformanceData.CloneNested(SuccessEffects);
            talk.effect2 = StoryGraphPerformanceData.CloneNested(FailureEffects);
        }

        internal void ApplyTo(OptionCfg option)
        {
            if (option == null) return;
            option.precondition = CloneDoubleNested(VisibilityConditions);
            option.check = CloneDoubleNested(ResultConditions);
            option.talkId = SuccessTargets != null
                ? new List<int>(SuccessTargets) : new List<int>();
            option.talkId2 = FailureTargets != null
                ? new List<int>(FailureTargets) : new List<int>();
            option.effect = StoryGraphPerformanceData.CloneNested(SuccessEffects);
            option.effect2 = StoryGraphPerformanceData.CloneNested(FailureEffects);
        }

        internal static List<List<double>> CloneDoubleNested(
            IEnumerable<List<double>> source)
        {
            return source == null
                ? new List<List<double>>()
                : source.Select(row => row != null
                    ? new List<double>(row) : null).ToList();
        }

        private static bool DoubleNestedContentEquals(
            IList<List<double>> left, IList<List<double>> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;
            for (int i = 0; i < left.Count; i++)
            {
                List<double> a = left[i];
                List<double> b = right[i];
                if (ReferenceEquals(a, b)) continue;
                if (a == null || b == null || !a.SequenceEqual(b)) return false;
            }
            return true;
        }

        private static bool SequenceEqual(
            IList<int> left, IList<int> right)
        {
            if (ReferenceEquals(left, right)) return true;
            return left != null && right != null && left.SequenceEqual(right);
        }

        private static bool TryValidateConditions(
            IList<List<double>> values, string label, out string error)
        {
            error = null;
            if (values == null) return true;
            for (int rowIndex = 0; rowIndex < values.Count; rowIndex++)
            {
                List<double> row = values[rowIndex];
                if (row == null || row.Count < 2)
                {
                    error = label + "第 " + (rowIndex + 1)
                          + " 行至少需要条件类型和子类型两个数字。";
                    return false;
                }
                for (int column = 0; column < row.Count; column++)
                {
                    double value = row[column];
                    if (double.IsNaN(value) || double.IsInfinity(value))
                    {
                        error = label + "第 " + (rowIndex + 1) + " 行第 "
                              + (column + 1) + " 项不是有限数字。";
                        return false;
                    }
                }
                if (Math.Truncate(row[0]) != row[0])
                {
                    error = label + "第 " + (rowIndex + 1)
                          + " 行的条件类型必须是整数。";
                    return false;
                }
            }
            return true;
        }

        private static bool TryValidateTargets(
            IEnumerable<int> values, string label, out string error)
        {
            error = null;
            if (values == null) return true;
            int index = 0;
            foreach (int value in values)
            {
                index++;
                if (value < 0)
                {
                    error = label + "第 " + index
                          + " 项只能为 0（无去向）或正数对话编号。";
                    return false;
                }
            }
            return true;
        }
    }

    internal static class StoryGraphPerformanceCodec
    {
        internal static string FormatIntList(IEnumerable<int> values)
        {
            return values == null ? string.Empty : string.Join(", ", values);
        }

        internal static string FormatFloatList(IEnumerable<float> values)
        {
            return values == null
                ? string.Empty
                : string.Join(", ", values.Select(FormatNumber).ToArray());
        }

        internal static string FormatNested(IEnumerable<List<float>> values)
        {
            if (values == null) return string.Empty;
            return string.Join("\n", values.Where(row => row != null)
                .Select(FormatFloatList).ToArray());
        }

        internal static string FormatNestedDouble(
            IEnumerable<List<double>> values)
        {
            if (values == null) return string.Empty;
            return string.Join("\n", values.Where(row => row != null)
                .Select(row => string.Join(", ", row.Select(value =>
                    value.ToString("R", CultureInfo.InvariantCulture)).ToArray()))
                .ToArray());
        }

        internal static bool TryParseIntList(
            string text, string label, out List<int> values, out string error)
        {
            values = new List<int>();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            string[] tokens = SplitRow(text.Replace('\n', ',').Replace('\r', ',')
                .Replace(';', ',').Replace('；', ','));
            for (int i = 0; i < tokens.Length; i++)
            {
                int value;
                if (!int.TryParse(tokens[i], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out value))
                {
                    error = label + "第 " + (i + 1) + " 项不是整数：" + tokens[i];
                    return false;
                }
                values.Add(value);
            }
            return true;
        }

        internal static bool TryParseFloatList(
            string text, string label, out List<float> values, out string error)
        {
            values = new List<float>();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            string[] tokens = SplitRow(text.Replace('\n', ',').Replace('\r', ',')
                .Replace(';', ',').Replace('；', ','));
            for (int i = 0; i < tokens.Length; i++)
            {
                float value;
                if (!float.TryParse(tokens[i], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value)
                    || float.IsNaN(value) || float.IsInfinity(value))
                {
                    error = label + "第 " + (i + 1) + " 项不是有限数字：" + tokens[i];
                    return false;
                }
                values.Add(value);
            }
            return true;
        }

        internal static bool TryParseNested(
            string text, string label, int minColumns,
            out List<List<float>> values, out string error)
        {
            values = new List<List<float>>();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            string normalized = text.Replace("；", ";")
                .Replace("\r\n", "\n").Replace('\r', '\n');
            string[] rows = normalized.Split(new[] { '\n', ';' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                string[] tokens = SplitRow(rows[rowIndex]);
                if (tokens.Length == 0) continue;
                if (tokens.Length < minColumns)
                {
                    error = label + "第 " + (rowIndex + 1) + " 行至少需要 "
                          + minColumns + " 个数字。";
                    return false;
                }
                var row = new List<float>();
                for (int column = 0; column < tokens.Length; column++)
                {
                    float value;
                    if (!float.TryParse(tokens[column], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out value)
                        || float.IsNaN(value) || float.IsInfinity(value))
                    {
                        error = label + "第 " + (rowIndex + 1) + " 行第 "
                              + (column + 1) + " 项不是有限数字：" + tokens[column];
                        return false;
                    }
                    row.Add(value);
                }
                values.Add(row);
            }
            return true;
        }

        internal static bool TryParseNestedDouble(
            string text, string label, int minColumns,
            out List<List<double>> values, out string error)
        {
            values = new List<List<double>>();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;
            string normalized = text.Replace("；", ";")
                .Replace("\r\n", "\n").Replace('\r', '\n');
            string[] rows = normalized.Split(new[] { '\n', ';' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                string[] tokens = SplitRow(rows[rowIndex]);
                if (tokens.Length == 0) continue;
                if (tokens.Length < minColumns)
                {
                    error = label + "第 " + (rowIndex + 1) + " 行至少需要 "
                          + minColumns + " 个数字。";
                    return false;
                }
                var row = new List<double>();
                for (int column = 0; column < tokens.Length; column++)
                {
                    double value;
                    if (!double.TryParse(tokens[column], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out value)
                        || double.IsNaN(value) || double.IsInfinity(value))
                    {
                        error = label + "第 " + (rowIndex + 1) + " 行第 "
                              + (column + 1) + " 项不是有限数字："
                              + tokens[column];
                        return false;
                    }
                    row.Add(value);
                }
                values.Add(row);
            }
            return true;
        }

        internal static string BuildTalkBadges(TalkCfg talk)
        {
            if (talk == null) return string.Empty;
            var badges = new List<string>();
            if (talk.miniGame != null && talk.miniGame.Count > 0)
            {
                int gameId;
                string error;
                badges.Add(MiniGameUtil.TryGetGameId(
                        talk.miniGame, out gameId, out error)
                    ? "小游戏:" + MiniGameUtil.GameName(gameId)
                    : "小游戏:无效");
            }
            int actionCount = talk.roles != null
                ? talk.roles.Count(row => row != null && row.Count >= 2) : 0;
            bool hasExpression = talk.roles != null && talk.roles.Any(row =>
                row != null && row.Count >= 2 && (int)row[1] == 3000);
            if (hasExpression) badges.Add("表情");
            if (actionCount > 0) badges.Add("人物动作×" + actionCount);
            if (talk.bg != 0) badges.Add("背景" + talk.bg);
            if (talk.screenEffect != null && talk.screenEffect.Count > 0)
            {
                string translated = TalkActionTranslator.TranslateScreenEffect(talk.screenEffect);
                if (!string.IsNullOrEmpty(translated))
                {
                    int bracket = translated.IndexOf(" (", StringComparison.Ordinal);
                    badges.Add(bracket > 0 ? translated.Substring(0, bracket) : translated);
                }
            }
            if (talk.audio > 0) badges.Add("声音" + talk.audio);
            if (talk.vocals != null && talk.vocals.Count > 0) badges.Add("语音");
            int effects = (talk.effect != null ? talk.effect.Count : 0)
                        + (talk.effect2 != null ? talk.effect2.Count : 0);
            if (effects > 0) badges.Add("逻辑效果×" + effects);
            return FormatBadges(badges, 5, 72);
        }

        internal static string BuildOptionBadges(OptionCfg option)
        {
            if (option == null) return string.Empty;
            int success = option.effect != null ? option.effect.Count : 0;
            int failure = option.effect2 != null ? option.effect2.Count : 0;
            var badges = new List<string>();
            if (option.miniGame != null && option.miniGame.Count > 0)
            {
                int gameId;
                string error;
                badges.Add(MiniGameUtil.TryGetGameId(
                        option.miniGame, out gameId, out error)
                    ? "小游戏:" + MiniGameUtil.GameName(gameId)
                    : "小游戏:无效");
            }
            if (success > 0) badges.Add("成功效果×" + success);
            if (failure > 0) badges.Add("失败效果×" + failure);
            return FormatBadges(badges, 3, 72);
        }

        private static string FormatBadges(
            IList<string> values, int maxBadges, int maxLength)
        {
            if (values == null || values.Count == 0) return string.Empty;
            var source = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (source.Count > maxBadges)
            {
                int extra = source.Count - (maxBadges - 1);
                source = source.Take(maxBadges - 1).ToList();
                source.Add("另" + extra + "项");
            }

            var tokens = source.Select(value => "[" + value.Trim() + "]").ToList();
            var result = new List<string>();
            int length = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                int required = token.Length + (result.Count > 0 ? 1 : 0);
                if (length + required <= maxLength)
                {
                    result.Add(token);
                    length += required;
                    continue;
                }
                string more = "[更多]";
                if (result.Count == 0)
                {
                    int keep = Math.Max(1, maxLength - 4);
                    result.Add(token.Length > keep
                        ? token.Substring(0, keep) + "…]"
                        : token);
                }
                else if (length + 1 + more.Length <= maxLength)
                    result.Add(more);
                break;
            }
            return string.Join(" ", result.ToArray());
        }

        private static string[] SplitRow(string value)
        {
            return (value ?? string.Empty)
                .Replace('，', ',')
                .Replace('|', ',')
                .Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string FormatNumber(float value)
        {
            // "R" 使用可往返格式，避免结构化页仅打开再应用就把未知原生参数
            // 从 0.1234567 悄悄舍入为 0.123。
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
