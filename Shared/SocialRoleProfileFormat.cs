using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Config;

namespace StudentAgeSocialRoles
{
    // ═════════════════════════════════════════════════════════════════
    //  数据模型
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// 可社交角色资料模式。此源码同时编译进作者端 EditorPlus 与玩家端 Runtime，
    /// 保证两边始终使用同一个数据格式，而不引入额外共享 DLL。
    /// </summary>
    internal enum SocialRoleProfileMode
    {
        Student = 0,
        Teacher = 1,
        Adult = 2,
        Custom = 3,
    }

    /// <summary>
    /// 阶段触发条件类型。复用游戏原生持久化接口：
    /// EventHappened → CommonEvtMgr.HasEventHappended(evtId, cnt)
    /// OptionSelected → CommonEvtMgr.HasOptionSelected(optionId)
    /// EventValue → CommonEvtMgr.GetEvtSaveData(evtId, pos) >= target
    /// None = 始终满足（初始阶段）。
    /// </summary>
    internal enum StageTriggerType
    {
        None = 0,
        EventHappened = 1,
        OptionSelected = 2,
        EventValue = 3,
    }

    /// <summary>
    /// 单个资料阶段的触发条件。条件不满足时跳过该阶段。
    /// 作者只需填写触发类型和对应 ID；Runtime 调用原生接口判断。
    /// </summary>
    internal sealed class StageTrigger
    {
        public StageTriggerType Type;
        public int EvtId;       // 事件ID / 选项ID / 事件存档值的事件ID
        public int Pos;         // 事件存档值的槽位（仅 EventValue 使用）
        public int Count;       // 事件发生次数（EventHappened 使用，默认1）
        public float Target;    // 事件存档值目标（EventValue 使用）
    }

    /// <summary>
    /// 一个资料阶段：触发条件 + 该阶段的两栏内容。
    /// 阶段按列表顺序排列，Runtime 从后往前匹配第一个满足条件的阶段。
    /// 第一个阶段通常 Type=None（始终满足），作为默认/初始资料。
    /// </summary>
    internal sealed class ProfileStage
    {
        public StageTrigger Trigger;
        public string PrimaryLabel;
        public string PrimaryValue;
        public string SecondaryLabel;
        public string SecondaryValue;
        public bool ShowStudyRank;
    }

    /// <summary>
    /// 一个 NPC 的完整自定义资料。包含模式和一组阶段。
    /// V1 静态资料在加载时自动转换为一个 None 阶段。
    /// </summary>
    internal sealed class SocialRoleProfileData
    {
        public SocialRoleProfileMode Mode;
        public List<ProfileStage> Stages = new();

        public bool IsCustom => Mode != SocialRoleProfileMode.Student;

        /// <summary>
        /// 返回当前生效的阶段。从后往前匹配，第一个满足条件的阶段胜出。
        /// None/空触发条件在此处直接判定为满足，不经过 checker——
        /// 保证初始阶段在任何环境（包括编辑器占位 checker）下都可用。
        /// 无阶段时返回 null（调用方应回退原版显示）。
        /// </summary>
        public ProfileStage ResolveActiveStage(IStageConditionChecker checker)
        {
            if (Stages == null || Stages.Count == 0) return null;
            for (int i = Stages.Count - 1; i >= 0; i--)
            {
                var stage = Stages[i];
                if (stage == null) continue;
                if (stage.Trigger == null || stage.Trigger.Type == StageTriggerType.None) return stage;
                if (checker != null && checker.IsMatch(stage.Trigger)) return stage;
            }
            return null;
        }
    }

    /// <summary>
    /// 阶段条件检查器接口。Runtime 用真实 CommonEvtMgr 实现；
    /// 编辑器可用始终返回 false 的占位实现（预览初始阶段）。
    /// None/空触发条件由 ResolveActiveStage 直接判定，不会传入 checker。
    /// </summary>
    internal interface IStageConditionChecker
    {
        bool IsMatch(StageTrigger trigger);
    }

    // ═════════════════════════════════════════════════════════════════
    //  作者端 / 玩家端共同遵守的 Runtime 契约
    // ═════════════════════════════════════════════════════════════════

    internal static class SocialRoleRuntimeContract
    {
        // GUID 一经发布不得修改；EditorPlus、Runtime 和依赖清单都用它识别同一共享运行库。
        internal const string PluginGuid = "com.studentage.socialroleruntime";
        internal const string MinimumVersionForV2 = "0.2.0";
        internal const string MinimumSafeVersion = "0.2.2";
        internal const int DataFormatVersion = 2;
    }

    // ═════════════════════════════════════════════════════════════════
    //  工具
    // ═════════════════════════════════════════════════════════════════

    internal static class SocialRoleProfileUtil
    {
        public static bool IsSocialRole(PersonCfg cfg)
        {
            if (cfg?.init == null || cfg.init.Count == 0) return false;
            int type = cfg.init[0];
            // 原生人物编辑器：2=男女主均可社交，3=仅男主档，4=仅女主档。
            return type == 2 || type == 3 || type == 4;
        }

        /// <summary>
        /// 可社交角色的生日最终会被复制进玩家存档中的 Role.Birthday。
        /// 原版恋爱回合至少索引到 month，企鹅空间资料会索引 year/month/day，
        /// 所以作者数据必须是一个完整且真实存在的公历日期，不能只依赖资料页的显示兜底。
        /// </summary>
        public static bool TryValidateBirthday(IReadOnlyList<int> birthday, out string error)
        {
            error = null;
            if (birthday == null || birthday.Count < 3)
            {
                error = "生日必须完整填写为 年,月,日（例如 1995,1,14）";
                return false;
            }

            int year = birthday[0];
            int month = birthday[1];
            int day = birthday[2];
            if (year < 1 || year > 9999)
            {
                error = $"生日年份无效：{year}";
                return false;
            }
            if (month < 1 || month > 12)
            {
                error = $"生日月份无效：{month}";
                return false;
            }

            int daysInMonth;
            try
            {
                daysInMonth = DateTime.DaysInMonth(year, month);
            }
            catch
            {
                error = $"生日日期无效：{year},{month},{day}";
                return false;
            }

            if (day < 1 || day > daysInMonth)
            {
                error = $"生日日期无效：{year},{month},{day}";
                return false;
            }
            return true;
        }

        public static List<int> CopyBirthday(IReadOnlyList<int> birthday)
        {
            return birthday == null || birthday.Count < 3
                ? null
                : new List<int> { birthday[0], birthday[1], birthday[2] };
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  编解码 — V2 多阶段格式
    // ═════════════════════════════════════════════════════════════════
    //
    // V2 标记格式（单行，嵌入 PersonCfg.note）：
    //
    //   [SAEP_SOCIAL_V2|mode|stageCount|stage0|stage1|...]
    //
    // 每个 stage 是用分号 ; 分隔的字段串：
    //
    //   triggerType;evtId;pos;count;target;showRank;b64(primaryLabel);b64(primaryValue);b64(secondaryLabel);b64(secondaryValue)
    //
    // triggerType: 0=None 1=EventHappened 2=OptionSelected 3=EventValue
    //
    // V2 向后兼容 V1：TryRead 先找 V2 标记，找不到再找 V1。
    // V1 被读取后自动转换为一个 None 阶段的 SocialRoleProfileData。
    //
    // 标记外的原 note 文本会原样保留。

    internal static class SocialRoleProfileCodec
    {
        internal const string MarkerV2 = "[SAEP_SOCIAL_V2|";
        internal const string MarkerV1 = "[SAEP_SOCIAL_V1|";
        private const char MarkerSuffix = ']';
        private const char StageSep = '|';
        private const char FieldSep = ';';

        // ── 读取 ──────────────────────────────────────────────────

        public static bool TryRead(PersonCfg cfg, out SocialRoleProfileData data)
        {
            data = null;
            if (cfg == null || string.IsNullOrEmpty(cfg.note)) return false;

            // 先试 V2
            if (TryReadV2(cfg.note, out data)) return true;

            // 再试 V1（向后兼容）
            if (TryReadV1(cfg.note, out data)) return true;

            return false;
        }

        private static bool TryReadV2(string note, out SocialRoleProfileData data)
        {
            data = null;
            int start = note.IndexOf(MarkerV2, StringComparison.Ordinal);
            if (start < 0) return false;
            int end = note.IndexOf(MarkerSuffix, start + MarkerV2.Length);
            if (end < 0) return false;

            string body = note.Substring(start + MarkerV2.Length, end - start - MarkerV2.Length);
            string[] parts = body.Split(StageSep);
            if (parts.Length < 2) return false;

            try
            {
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int modeValue) || modeValue < 0 || modeValue > 3)
                    return false;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int stageCount) || stageCount < 0 || stageCount > 50)
                    return false;
                if (parts.Length != 2 + stageCount) return false;

                var stages = new List<ProfileStage>(stageCount);
                for (int i = 0; i < stageCount; i++)
                {
                    if (!TryParseStage(parts[2 + i], out var stage)) return false;
                    stages.Add(stage);
                }

                data = new SocialRoleProfileData
                {
                    Mode = (SocialRoleProfileMode)modeValue,
                    Stages = stages,
                };
                return true;
            }
            catch
            {
                data = null;
                return false;
            }
        }

        private static bool TryParseStage(string raw, out ProfileStage stage)
        {
            stage = null;
            if (string.IsNullOrEmpty(raw)) return false;
            string[] f = raw.Split(FieldSep);
            if (f.Length != 10) return false;

            try
            {
                if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int trigType) || trigType < 0 || trigType > 3)
                    return false;

                stage = new ProfileStage
                {
                    Trigger = new StageTrigger
                    {
                        Type = (StageTriggerType)trigType,
                        EvtId = int.Parse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture),
                        Pos = int.Parse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture),
                        Count = Mathf_Max1(int.Parse(f[3], NumberStyles.Integer,
                            CultureInfo.InvariantCulture)),
                        Target = float.Parse(f[4], NumberStyles.Float,
                            CultureInfo.InvariantCulture),
                    },
                    ShowStudyRank = f[5] == "1",
                    PrimaryLabel = Decode(f[6]),
                    PrimaryValue = Decode(f[7]),
                    SecondaryLabel = Decode(f[8]),
                    SecondaryValue = Decode(f[9]),
                };
                return true;
            }
            catch
            {
                stage = null;
                return false;
            }
        }

        private static int Mathf_Max1(int v) => v < 1 ? 1 : v;

        private static bool TryReadV1(string note, out SocialRoleProfileData data)
        {
            data = null;
            int start = note.IndexOf(MarkerV1, StringComparison.Ordinal);
            if (start < 0) return false;
            int end = note.IndexOf(MarkerSuffix, start + MarkerV1.Length);
            if (end < 0) return false;

            string body = note.Substring(start + MarkerV1.Length, end - start - MarkerV1.Length);
            string[] parts = body.Split(StageSep);
            if (parts.Length != 6) return false;

            try
            {
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int modeValue) || modeValue < 0 || modeValue > 3)
                    return false;

                // V1 → 转换为单个 None 阶段
                data = new SocialRoleProfileData
                {
                    Mode = (SocialRoleProfileMode)modeValue,
                    Stages = new List<ProfileStage>
                    {
                        new ProfileStage
                        {
                            Trigger = new StageTrigger { Type = StageTriggerType.None },
                            ShowStudyRank = parts[1] == "1",
                            PrimaryLabel = Decode(parts[2]),
                            PrimaryValue = Decode(parts[3]),
                            SecondaryLabel = Decode(parts[4]),
                            SecondaryValue = Decode(parts[5]),
                        }
                    },
                };
                return true;
            }
            catch
            {
                data = null;
                return false;
            }
        }

        // ── 写入（始终写 V2）──────────────────────────────────────

        public static string Write(string originalNote, SocialRoleProfileData data)
        {
            string noteWithoutMarker = RemoveAllMarkers(originalNote);
            if (data == null) return noteWithoutMarker;

            var sb = new StringBuilder();
            sb.Append(MarkerV2);
            sb.Append(((int)data.Mode).ToString(CultureInfo.InvariantCulture)).Append(StageSep);
            sb.Append((data.Stages?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
            if (data.Stages != null)
            {
                foreach (var stage in data.Stages)
                {
                    sb.Append(StageSep);
                    AppendStage(sb, stage);
                }
            }
            sb.Append(MarkerSuffix);

            return string.IsNullOrWhiteSpace(noteWithoutMarker)
                ? sb.ToString()
                : noteWithoutMarker.TrimEnd() + "\n" + sb.ToString();
        }

        private static void AppendStage(StringBuilder sb, ProfileStage stage)
        {
            var t = stage?.Trigger;
            sb.Append(((int)(t?.Type ?? StageTriggerType.None)).ToString(
                CultureInfo.InvariantCulture)).Append(FieldSep);
            sb.Append((t?.EvtId ?? 0).ToString(CultureInfo.InvariantCulture)).Append(FieldSep);
            sb.Append((t?.Pos ?? 0).ToString(CultureInfo.InvariantCulture)).Append(FieldSep);
            sb.Append((t?.Count ?? 1).ToString(CultureInfo.InvariantCulture)).Append(FieldSep);
            sb.Append((t?.Target ?? 0f).ToString("R", CultureInfo.InvariantCulture)).Append(FieldSep);
            sb.Append(stage?.ShowStudyRank == true ? "1" : "0").Append(FieldSep);
            sb.Append(Encode(stage?.PrimaryLabel)).Append(FieldSep);
            sb.Append(Encode(stage?.PrimaryValue)).Append(FieldSep);
            sb.Append(Encode(stage?.SecondaryLabel)).Append(FieldSep);
            sb.Append(Encode(stage?.SecondaryValue));
        }

        // ── 标记清理 ──────────────────────────────────────────────

        private static string RemoveAllMarkers(string note)
        {
            if (string.IsNullOrEmpty(note)) return string.Empty;
            string result = note;
            result = RemoveOneMarker(result, MarkerV2);
            result = RemoveOneMarker(result, MarkerV1);
            return result.Trim();
        }

        private static string RemoveOneMarker(string text, string marker)
        {
            while (true)
            {
                int start = text.IndexOf(marker, StringComparison.Ordinal);
                if (start < 0) break;
                int end = text.IndexOf(MarkerSuffix, start + marker.Length);
                if (end < 0) break;
                text = text.Remove(start, end - start + 1);
            }
            return text;
        }

        // ── Base64 ────────────────────────────────────────────────

        private static string Encode(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        // ── 默认值 / 标题解析 ─────────────────────────────────────

        public static SocialRoleProfileData NewDefault()
        {
            return new SocialRoleProfileData
            {
                Mode = SocialRoleProfileMode.Student,
                Stages = new List<ProfileStage>
                {
                    new ProfileStage
                    {
                        Trigger = new StageTrigger { Type = StageTriggerType.None },
                        ShowStudyRank = true,
                        PrimaryLabel = "学校",
                        PrimaryValue = string.Empty,
                        SecondaryLabel = "班级",
                        SecondaryValue = string.Empty,
                    }
                },
            };
        }

        /// <summary>
        /// 返回指定阶段的默认第一栏标题。若阶段标签非空则用它，否则按模式返回预设。
        /// </summary>
        public static string ResolvePrimaryLabel(SocialRoleProfileData data, ProfileStage stage)
        {
            if (!string.IsNullOrWhiteSpace(stage?.PrimaryLabel)) return stage.PrimaryLabel.Trim();
            return data?.Mode switch
            {
                SocialRoleProfileMode.Teacher => "学校",
                SocialRoleProfileMode.Adult => "单位",
                SocialRoleProfileMode.Custom => "资料一",
                _ => "学校",
            };
        }

        public static string ResolveSecondaryLabel(SocialRoleProfileData data, ProfileStage stage)
        {
            if (!string.IsNullOrWhiteSpace(stage?.SecondaryLabel)) return stage.SecondaryLabel.Trim();
            return data?.Mode switch
            {
                SocialRoleProfileMode.Teacher => "职务",
                SocialRoleProfileMode.Adult => "身份",
                SocialRoleProfileMode.Custom => "资料二",
                _ => "班级",
            };
        }

        // ── V1 兼容：供旧调用方使用的单阶段访问 ──────────────────

        /// <summary>
        /// 返回第一个阶段（V1 兼容）或 null。仅供不需要阶段逻辑的简化调用。
        /// </summary>
        public static ProfileStage FirstStage(SocialRoleProfileData data)
        {
            return data?.Stages != null && data.Stages.Count > 0 ? data.Stages[0] : null;
        }

        /// <summary>
        /// V1 兼容：返回第一个阶段的 ShowStudyRank。
        /// </summary>
        public static bool FirstShowStudyRank(SocialRoleProfileData data)
        {
            return FirstStage(data)?.ShowStudyRank ?? true;
        }
    }
}
