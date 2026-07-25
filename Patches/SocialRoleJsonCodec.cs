using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace StudentAgeSocialRoles
{
    // ═════════════════════════════════════════════════════════════════
    //  作者向 JSON 格式（导入/导出/手工编辑用，与 note 内部紧凑标记解耦）
    // ═════════════════════════════════════════════════════════════════
    //
    // {
    //   "mode": "teacher",                    // student / teacher / adult / custom
    //   "stages": [
    //     {
    //       "trigger": { "type": "none" },    // 初始阶段：无条件
    //       "primaryLabel": "学校",  "primaryValue": "鹅城一中",
    //       "secondaryLabel": "职务", "secondaryValue": "班主任",
    //       "showStudyRank": false
    //     },
    //     { "trigger": { "type": "event",  "evtId": 5001, "count": 1 }, "secondaryValue": "教导主任" },
    //     { "trigger": { "type": "option", "optionId": 302 },           "secondaryValue": "已停职" },
    //     { "trigger": { "type": "value",  "evtId": 5002, "pos": 0, "target": 3 } }
    //   ]
    // }
    //
    // 规则：
    // · 游戏内从最后一个阶段往前找，第一个满足条件的阶段生效；
    // · 省略的文本字段显示时回退到模式预设标题 / "—" 占位；
    // · evtId/optionId ≤ 0 表示条件未设置，该阶段永远不生效（允许存草稿）；
    // · showStudyRank 省略时视为 false。

    internal static class SocialRoleJsonCodec
    {
        private const int MaxStages = 50; // 与 note 标记编解码的上限一致

        private sealed class TriggerDto
        {
            [JsonProperty("type")] public string Type;
            [JsonProperty("evtId")] public int? EvtId;
            [JsonProperty("optionId")] public int? OptionId;
            [JsonProperty("pos")] public int? Pos;
            [JsonProperty("count")] public int? Count;
            [JsonProperty("target")] public float? Target;
        }

        private sealed class StageDto
        {
            [JsonProperty("trigger")] public TriggerDto Trigger;
            [JsonProperty("primaryLabel")] public string PrimaryLabel;
            [JsonProperty("primaryValue")] public string PrimaryValue;
            [JsonProperty("secondaryLabel")] public string SecondaryLabel;
            [JsonProperty("secondaryValue")] public string SecondaryValue;
            [JsonProperty("showStudyRank")] public bool? ShowStudyRank;
        }

        private sealed class ProfileDto
        {
            [JsonProperty("mode")] public string Mode;
            [JsonProperty("stages")] public List<StageDto> Stages;
        }

        private static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
        };

        // ── 导出 ──────────────────────────────────────────────────

        public static string ToJson(SocialRoleProfileData data)
        {
            var dto = new ProfileDto
            {
                Mode = ModeName(data?.Mode ?? SocialRoleProfileMode.Student),
                Stages = new List<StageDto>(),
            };

            if (data?.Stages != null)
            {
                foreach (var stage in data.Stages)
                {
                    if (stage == null) continue;
                    var t = stage.Trigger;
                    var trigDto = new TriggerDto { Type = TypeName(t?.Type ?? StageTriggerType.None) };
                    switch (t?.Type ?? StageTriggerType.None)
                    {
                        case StageTriggerType.EventHappened:
                            trigDto.EvtId = t.EvtId;
                            trigDto.Count = t.Count < 1 ? 1 : t.Count;
                            break;
                        case StageTriggerType.OptionSelected:
                            trigDto.OptionId = t.EvtId;
                            break;
                        case StageTriggerType.EventValue:
                            trigDto.EvtId = t.EvtId;
                            trigDto.Pos = t.Pos;
                            trigDto.Target = t.Target;
                            break;
                    }

                    dto.Stages.Add(new StageDto
                    {
                        Trigger = trigDto,
                        // 导出时五个内容字段总是写出（空串也写），保证再导入完全等价、便于对照编辑。
                        PrimaryLabel = stage.PrimaryLabel ?? string.Empty,
                        PrimaryValue = stage.PrimaryValue ?? string.Empty,
                        SecondaryLabel = stage.SecondaryLabel ?? string.Empty,
                        SecondaryValue = stage.SecondaryValue ?? string.Empty,
                        ShowStudyRank = stage.ShowStudyRank,
                    });
                }
            }

            return JsonConvert.SerializeObject(dto, Formatting.Indented, Settings);
        }

        // ── 导入 ──────────────────────────────────────────────────

        public static bool TryFromJson(string json, out SocialRoleProfileData data, out string error)
        {
            data = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "剪贴板为空";
                return false;
            }

            ProfileDto dto;
            try
            {
                dto = JsonConvert.DeserializeObject<ProfileDto>(json);
            }
            catch (Exception e)
            {
                error = "JSON语法错误：" + FirstLine(e.Message);
                return false;
            }

            if (dto == null)
            {
                error = "JSON内容为空";
                return false;
            }
            if (!TryParseMode(dto.Mode, out var mode))
            {
                error = $"mode 无效：{dto.Mode ?? "(缺失)"}（可用 student/teacher/adult/custom）";
                return false;
            }
            if (dto.Stages == null || dto.Stages.Count == 0)
            {
                error = "stages 缺失或为空，至少需要一个阶段";
                return false;
            }
            if (dto.Stages.Count > MaxStages)
            {
                error = $"阶段数量超过上限（{dto.Stages.Count} > {MaxStages}）";
                return false;
            }

            var stages = new List<ProfileStage>(dto.Stages.Count);
            for (int i = 0; i < dto.Stages.Count; i++)
            {
                var s = dto.Stages[i];
                if (s == null)
                {
                    error = $"第{i + 1}个阶段为 null";
                    return false;
                }

                var trigType = StageTriggerType.None;
                if (s.Trigger != null && !TryParseTriggerType(s.Trigger.Type, out trigType))
                {
                    error = $"第{i + 1}个阶段 trigger.type 无效：{s.Trigger.Type ?? "(缺失)"}（可用 none/event/option/value）";
                    return false;
                }

                var t = s.Trigger;
                stages.Add(new ProfileStage
                {
                    Trigger = new StageTrigger
                    {
                        Type = trigType,
                        // option 类型的 optionId 与 evtId 同槽存储；两个名字都接受。
                        EvtId = t?.EvtId ?? t?.OptionId ?? 0,
                        Pos = Math.Max(0, t?.Pos ?? 0),
                        Count = Math.Max(1, t?.Count ?? 1),
                        Target = t?.Target ?? 0f,
                    },
                    PrimaryLabel = s.PrimaryLabel ?? string.Empty,
                    PrimaryValue = s.PrimaryValue ?? string.Empty,
                    SecondaryLabel = s.SecondaryLabel ?? string.Empty,
                    SecondaryValue = s.SecondaryValue ?? string.Empty,
                    ShowStudyRank = s.ShowStudyRank ?? false,
                });
            }

            data = new SocialRoleProfileData { Mode = mode, Stages = stages };
            return true;
        }

        // ── 名称映射 ──────────────────────────────────────────────

        private static string ModeName(SocialRoleProfileMode mode) => mode switch
        {
            SocialRoleProfileMode.Teacher => "teacher",
            SocialRoleProfileMode.Adult => "adult",
            SocialRoleProfileMode.Custom => "custom",
            _ => "student",
        };

        private static bool TryParseMode(string raw, out SocialRoleProfileMode mode)
        {
            mode = SocialRoleProfileMode.Student;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "student": case "0": mode = SocialRoleProfileMode.Student; return true;
                case "teacher": case "1": mode = SocialRoleProfileMode.Teacher; return true;
                case "adult": case "2": mode = SocialRoleProfileMode.Adult; return true;
                case "custom": case "3": mode = SocialRoleProfileMode.Custom; return true;
                default: return false;
            }
        }

        private static string TypeName(StageTriggerType type) => type switch
        {
            StageTriggerType.EventHappened => "event",
            StageTriggerType.OptionSelected => "option",
            StageTriggerType.EventValue => "value",
            _ => "none",
        };

        private static bool TryParseTriggerType(string raw, out StageTriggerType type)
        {
            type = StageTriggerType.None;
            if (string.IsNullOrWhiteSpace(raw)) return true; // trigger 缺 type 视为 none
            switch (raw.Trim().ToLowerInvariant())
            {
                case "none": case "0": type = StageTriggerType.None; return true;
                case "event": case "eventhappened": case "1": type = StageTriggerType.EventHappened; return true;
                case "option": case "optionselected": case "2": type = StageTriggerType.OptionSelected; return true;
                case "value": case "eventvalue": case "3": type = StageTriggerType.EventValue; return true;
                default: return false;
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int idx = text.IndexOfAny(new[] { '\r', '\n' });
            return idx > 0 ? text.Substring(0, idx) : text;
        }

        /// <summary>触发条件的单行摘要，用于阶段下拉框选项。</summary>
        public static string TriggerSummary(StageTrigger trigger)
        {
            if (trigger == null || trigger.Type == StageTriggerType.None) return "初始（无条件）";
            if (trigger.EvtId <= 0) return "未设条件（不生效）";
            return trigger.Type switch
            {
                StageTriggerType.EventHappened => trigger.Count > 1
                    ? $"事件{trigger.EvtId}发生≥{trigger.Count}次"
                    : $"事件{trigger.EvtId}已发生",
                StageTriggerType.OptionSelected => $"选过选项{trigger.EvtId}",
                StageTriggerType.EventValue =>
                    $"事件{trigger.EvtId}存档[{trigger.Pos}]≥{trigger.Target.ToString("0.##", CultureInfo.InvariantCulture)}",
                _ => "初始（无条件）",
            };
        }
    }
}
