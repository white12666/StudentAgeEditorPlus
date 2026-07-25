using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Config;
using GenUI.Mod;
using HarmonyLib;
using Sdk;
using StudentAgeSocialRoles;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using View.Common;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{

    internal sealed class SocialRoleEditorWidget
    {
        public RectTransform ModeRoot;
        public Dropdown ModeDropdown;

        // ── 阶段管理 ──
        public RectTransform StageSelectRoot;
        public Dropdown StageSelectDropdown;
        public RectTransform StageOpsRoot;

        // ── 触发条件 ──
        public RectTransform TrigTypeRoot;
        public Dropdown TrigTypeDropdown;
        public RectTransform TrigIdRoot;
        public InputField TrigIdInput;
        public Text TrigIdLabel;
        public RectTransform TrigParamRoot;
        public InputField TrigParamInput;
        public Text TrigParamLabel;
        public RectTransform TrigTargetRoot;
        public InputField TrigTargetInput;

        // ── 阶段内容 ──
        public RectTransform PrimaryLabelRoot;
        public InputField PrimaryLabelInput;
        public RectTransform PrimaryValueRoot;
        public InputField PrimaryValueInput;
        public RectTransform SecondaryLabelRoot;
        public InputField SecondaryLabelInput;
        public RectTransform SecondaryValueRoot;
        public InputField SecondaryValueInput;
        public RectTransform StudyRankRoot;
        public Dropdown StudyRankDropdown;

        public int StageIndex;
        public int LastPersonId = int.MinValue;
        public bool Refreshing;
    }

    internal static class SocialRoleEditorState
    {
        private static readonly ConditionalWeakTable<ModPersonEditView, SocialRoleEditorWidget> Widgets = new();

        public static void Set(ModPersonEditView view, SocialRoleEditorWidget widget)
        {
            Widgets.Remove(view);
            Widgets.Add(view, widget);
        }

        public static bool TryGet(ModPersonEditView view, out SocialRoleEditorWidget widget)
        {
            return Widgets.TryGetValue(view, out widget);
        }
    }

    internal static class SocialRoleEditorUtil
    {
        private static readonly string[] ModeOptions =
        {
            "学生（沿用主角学校/年级）",
            "教师（学校/职务）",
            "成人（单位/身份）",
            "自定义两栏资料",
        };

        private static readonly string[] RankOptions =
        {
            "隐藏成绩图标",
            "显示成绩图标",
        };

        private static readonly string[] TriggerTypeOptions =
        {
            "初始（无条件）",
            "事件已发生",
            "选项已选择",
            "事件存档值达标",
        };

        private const int MaxStages = 50; // 与 note 标记编解码上限一致

        public static bool IsSocialRole(PersonCfg cfg)
        {
            return SocialRoleProfileUtil.IsSocialRole(cfg);
        }

        public static PersonCfg GetCurrentPerson(ModPersonEditView view)
        {
            return Traverse.Create(view).Field("curSelect").GetValue<PersonCfg>();
        }

        public static PersonGrowCfg GetCurrentGrow(ModPersonEditView view)
        {
            return Traverse.Create(view).Field("curPersonGrowCfg").GetValue<PersonGrowCfg>();
        }

        /// <summary>读取当前人物资料；无标记时返回默认值（不写回）。保证 Stages 至少一个。</summary>
        private static SocialRoleProfileData ReadData(PersonCfg person)
        {
            SocialRoleProfileData data = SocialRoleProfileCodec.TryRead(person, out var loaded)
                ? loaded
                : SocialRoleProfileCodec.NewDefault();
            if (data.Stages == null) data.Stages = new List<ProfileStage>();
            if (data.Stages.Count == 0)
            {
                data.Stages.Add(new ProfileStage
                {
                    Trigger = new StageTrigger { Type = StageTriggerType.None },
                    ShowStudyRank = false,
                    PrimaryLabel = string.Empty,
                    PrimaryValue = string.Empty,
                    SecondaryLabel = string.Empty,
                    SecondaryValue = string.Empty,
                });
            }
            return data;
        }

        // ═════════════════════════════════════════════════════════
        //  UI 构建
        // ═════════════════════════════════════════════════════════

        public static SocialRoleEditorWidget Create(ModPersonEditView view)
        {
            Transform parent = view.Content;
            int sibling = view.p_classname.GetSiblingIndex() + 1;

            RectTransform modeRoot = CloneRow(view.p_init, parent, "p_social_profile_mode", sibling++);
            Dropdown modeDropdown = modeRoot.GetComponentInChildren<Dropdown>(true);
            ConfigureDropdown(modeRoot, modeDropdown, "社交资料类型", ModeOptions,
                "学生模式继续使用游戏原版资料：所有 NPC 都跟随主角当前学校和年级。\n"
                + "教师、成人或自定义模式会为这个 NPC 单独显示两栏资料，不再强制套用主角的学校/班级。");

            RectTransform stageRoot = CloneRow(view.p_init, parent, "p_social_stage_select", sibling++);
            Dropdown stageDropdown = stageRoot.GetComponentInChildren<Dropdown>(true);
            ConfigureDropdown(stageRoot, stageDropdown, "编辑阶段", new[] { "阶段1 · 初始资料" },
                "一个角色的资料可以有多个阶段，随玩家剧情进度变化。\n"
                + "游戏里从最后一个阶段往前检查，第一个满足触发条件的阶段生效；越靠后的阶段优先级越高。\n"
                + "第一阶段建议保持「初始（无条件）」作为默认资料，否则条件全不满足时资料页会显示为空。");

            RectTransform opsRoot = CreateOpsRow(view, parent, sibling++);

            RectTransform trigTypeRoot = CloneRow(view.p_init, parent, "p_social_trigger_type", sibling++);
            Dropdown trigTypeDropdown = trigTypeRoot.GetComponentInChildren<Dropdown>(true);
            ConfigureDropdown(trigTypeRoot, trigTypeDropdown, "触发条件", TriggerTypeOptions,
                "当前编辑阶段的生效条件：\n"
                + "· 初始（无条件）——始终满足，作为默认资料；\n"
                + "· 事件已发生——某事件在本存档播放过指定次数；\n"
                + "· 选项已选择——玩家在对话里选过某个选项；\n"
                + "· 事件存档值达标——事件动作里记录的存档值达到目标（高级用法）。");

            RectTransform trigIdRoot = CloneRow(view.p_name, parent, "p_social_trigger_id", sibling++);
            InputField trigIdInput = trigIdRoot.GetComponentInChildren<InputField>(true);
            ConfigureInput(trigIdRoot, trigIdInput, "事件ID", "填事件或选项的数字ID",
                "触发条件引用的事件ID或选项ID。\n填 0 或留空表示条件未设置，该阶段永远不生效（可先存草稿）。",
                InputField.ContentType.IntegerNumber);
            Text trigIdLabel = FindLabel(trigIdRoot);

            RectTransform trigParamRoot = CloneRow(view.p_name, parent, "p_social_trigger_param", sibling++);
            InputField trigParamInput = trigParamRoot.GetComponentInChildren<InputField>(true);
            ConfigureInput(trigParamRoot, trigParamInput, "发生次数≥", "默认1",
                "「事件已发生」：要求事件至少发生的次数，默认 1。\n"
                + "「事件存档值」：读取存档值的槽位下标，从 0 开始。",
                InputField.ContentType.IntegerNumber);
            Text trigParamLabel = FindLabel(trigParamRoot);

            RectTransform trigTargetRoot = CloneRow(view.p_name, parent, "p_social_trigger_target", sibling++);
            InputField trigTargetInput = trigTargetRoot.GetComponentInChildren<InputField>(true);
            ConfigureInput(trigTargetRoot, trigTargetInput, "目标值≥", "存档值达到此数时生效",
                "「事件存档值达标」专用：该槽位的存档值 ≥ 目标值时条件满足。",
                InputField.ContentType.DecimalNumber);

            RectTransform primaryLabelRoot = CloneRow(view.p_name, parent, "p_social_primary_label", sibling++);
            InputField primaryLabelInput = primaryLabelRoot.GetComponentInChildren<InputField>(true);
            ConfigureInput(primaryLabelRoot, primaryLabelInput, "第一栏标题", "例如：学校、单位、组织",
                "社交资料页第一栏左侧的标题。末尾冒号会由插件自动补上。留空时按资料类型使用预设标题。");

            RectTransform primaryValueRoot = CloneRow(view.p_name, parent, "p_social_primary_value", sibling++);
            InputField primaryValueInput = primaryValueRoot.GetComponentInChildren<InputField>(true);
            ConfigureInput(primaryValueRoot, primaryValueInput, "第一栏内容", "例如：鹅城一中、鹅城教育局",
                "社交资料页第一栏显示的具体内容。此值按 NPC 和阶段单独保存，不跟随主角年级变化。");

            RectTransform secondaryLabelRoot = CloneRow(view.p_name, parent, "p_social_secondary_label", sibling++);
            InputField secondaryLabelInput = secondaryLabelRoot.GetComponentInChildren<InputField>(true);
            ConfigureInput(secondaryLabelRoot, secondaryLabelInput, "第二栏标题", "例如：职务、身份、班级",
                "社交资料页第二栏左侧的标题。教师模式通常填“职务”，成人模式通常填“身份”。");

            RectTransform secondaryValueRoot = CloneRow(view.p_name, parent, "p_social_secondary_value", sibling++);
            InputField secondaryValueInput = secondaryValueRoot.GetComponentInChildren<InputField>(true);
            ConfigureInput(secondaryValueRoot, secondaryValueInput, "第二栏内容", "例如：语文老师、编辑",
                "社交资料页第二栏显示的具体内容。");

            RectTransform rankRoot = CloneRow(view.p_init, parent, "p_social_study_rank", sibling);
            Dropdown rankDropdown = rankRoot.GetComponentInChildren<Dropdown>(true);
            ConfigureDropdown(rankRoot, rankDropdown, "成绩图标", RankOptions,
                "非学生角色通常应隐藏成绩段位图标。选择显示时，仍使用下方 PersonGrowCfg 的段位配置。此开关按阶段保存。");

            var widget = new SocialRoleEditorWidget
            {
                ModeRoot = modeRoot,
                ModeDropdown = modeDropdown,
                StageSelectRoot = stageRoot,
                StageSelectDropdown = stageDropdown,
                StageOpsRoot = opsRoot,
                TrigTypeRoot = trigTypeRoot,
                TrigTypeDropdown = trigTypeDropdown,
                TrigIdRoot = trigIdRoot,
                TrigIdInput = trigIdInput,
                TrigIdLabel = trigIdLabel,
                TrigParamRoot = trigParamRoot,
                TrigParamInput = trigParamInput,
                TrigParamLabel = trigParamLabel,
                TrigTargetRoot = trigTargetRoot,
                TrigTargetInput = trigTargetInput,
                PrimaryLabelRoot = primaryLabelRoot,
                PrimaryLabelInput = primaryLabelInput,
                PrimaryValueRoot = primaryValueRoot,
                PrimaryValueInput = primaryValueInput,
                SecondaryLabelRoot = secondaryLabelRoot,
                SecondaryLabelInput = secondaryLabelInput,
                SecondaryValueRoot = secondaryValueRoot,
                SecondaryValueInput = secondaryValueInput,
                StudyRankRoot = rankRoot,
                StudyRankDropdown = rankDropdown,
            };

            // 阶段操作按钮（克隆自 btn_save，见 CreateOpsRow）
            WireOpsButtons(view, widget, opsRoot);

            modeDropdown.onValueChanged.AddListener(value => OnModeChanged(view, widget, value));
            stageDropdown.onValueChanged.AddListener(value => OnStageSelected(view, widget, value));
            trigTypeDropdown.onValueChanged.AddListener(value => OnTriggerTypeChanged(view, widget, value));

            // 触发条件数字框：输入时实时保存，结束编辑后刷新阶段摘要
            trigIdInput.onValueChanged.AddListener(_ => WriteCurrent(view, widget));
            trigIdInput.onEndEdit.AddListener(_ => Refresh(view));
            trigParamInput.onValueChanged.AddListener(_ => WriteCurrent(view, widget));
            trigParamInput.onEndEdit.AddListener(_ => Refresh(view));
            trigTargetInput.onValueChanged.AddListener(_ => WriteCurrent(view, widget));
            trigTargetInput.onEndEdit.AddListener(_ => Refresh(view));

            rankDropdown.onValueChanged.AddListener(_ => WriteCurrent(view, widget));
            primaryLabelInput.onValueChanged.AddListener(_ => WriteCurrent(view, widget));
            primaryValueInput.onValueChanged.AddListener(_ => WriteCurrent(view, widget));
            secondaryLabelInput.onValueChanged.AddListener(_ => WriteCurrent(view, widget));
            secondaryValueInput.onValueChanged.AddListener(_ => WriteCurrent(view, widget));

            return widget;
        }

        private static RectTransform CloneRow(RectTransform source, Transform parent, string name, int sibling)
        {
            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, parent, false);
            clone.name = name;
            clone.transform.SetSiblingIndex(sibling);
            MiniGameUtil.StripBadComponents(clone);
            clone.SetActive(true);
            return clone.GetComponent<RectTransform>();
        }

        /// <summary>克隆一行并清掉输入框，作为按钮行容器。</summary>
        private static RectTransform CreateOpsRow(ModPersonEditView view, Transform parent, int sibling)
        {
            RectTransform row = CloneRow(view.p_name, parent, "p_social_stage_ops", sibling);
            InputField input = row.GetComponentInChildren<InputField>(true);
            if (input != null) UnityEngine.Object.DestroyImmediate(input.gameObject);
            // 行内如有布局组会和手动锚点打架，直接移除（行本身在 Content 的纵向布局里不受影响）。
            foreach (var group in row.GetComponents<LayoutGroup>())
                UnityEngine.Object.DestroyImmediate(group);
            SetLabel(row, "阶段操作");
            AddDescription(row, "阶段操作",
                "新增阶段：在列表末尾添加一个阶段（越靠后优先级越高），标题和成绩图标设置沿用当前阶段。\n"
                + "删除阶段：删除当前正在编辑的阶段，至少保留一个。\n"
                + "导出JSON：把该角色全部阶段以JSON复制到剪贴板，可粘贴到文本编辑器修改或复制给其它角色。\n"
                + "导入JSON：从剪贴板读取JSON并整体替换该角色的资料阶段。字段格式见修复说明。");
            return row;
        }

        private static void WireOpsButtons(ModPersonEditView view, SocialRoleEditorWidget widget, RectTransform opsRoot)
        {
            if (view.btn_save == null || view.btn_save.gameObject == null)
            {
                Plugin.Log.LogWarning("[SocialRoleEditor] 找不到 btn_save 模板，阶段操作按钮未创建（可用JSON路径替代）。");
                return;
            }

            CreateRowButton(view, opsRoot, "btn_social_stage_add", "新增阶段", 0.30f, 0.465f,
                () => OnAddStage(view, widget));
            CreateRowButton(view, opsRoot, "btn_social_stage_del", "删除阶段", 0.475f, 0.64f,
                () => OnDeleteStage(view, widget));
            CreateRowButton(view, opsRoot, "btn_social_json_export", "导出JSON", 0.65f, 0.815f,
                () => OnExportJson(view, widget));
            CreateRowButton(view, opsRoot, "btn_social_json_import", "导入JSON", 0.825f, 0.99f,
                () => OnImportJson(view, widget));
        }

        private static void CreateRowButton(
            ModPersonEditView view,
            RectTransform row,
            string name,
            string label,
            float xMin,
            float xMax,
            UnityAction onClick)
        {
            GameObject go = UnityEngine.Object.Instantiate(view.btn_save.gameObject, row, false);
            go.name = name;
            MiniGameUtil.StripBadComponents(go);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(xMin, 0.1f);
            rect.anchorMax = new Vector2(xMax, 0.9f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;

            Text txt = go.GetComponentInChildren<Text>(true);
            if (txt != null)
            {
                txt.text = label;
                txt.resizeTextForBestFit = true;
                txt.resizeTextMinSize = 10;
                txt.resizeTextMaxSize = 26;
            }

            // 清掉克隆自模板的点击回调（运行时监听不随 Instantiate 复制，此处双保险）
            Button unityBtn = go.GetComponent<Button>();
            if (unityBtn != null) unityBtn.onClick.RemoveAllListeners();

            var ub = new UIButton(go);
            ub.AddClick(onClick);
            go.SetActive(true);
        }

        private static void ConfigureDropdown(
            RectTransform root,
            Dropdown dropdown,
            string label,
            IReadOnlyList<string> options,
            string description)
        {
            SetLabel(root, label);
            dropdown.onValueChanged.RemoveAllListeners();
            dropdown.ClearOptions();
            dropdown.AddOptions(new List<string>(options));
            dropdown.SetValueWithoutNotify(0);
            AddDescription(root, label, description);
        }

        private static void ConfigureInput(
            RectTransform root,
            InputField input,
            string label,
            string placeholder,
            string description,
            InputField.ContentType contentType = InputField.ContentType.Standard)
        {
            SetLabel(root, label);
            input.onValueChanged.RemoveAllListeners();
            input.onEndEdit.RemoveAllListeners();
            input.contentType = contentType;
            input.characterLimit = 0;
            input.SetTextWithoutNotify(string.Empty);
            if (input.placeholder is Text placeholderText) placeholderText.text = placeholder;
            AddDescription(root, label, description);
        }

        private static void SetLabel(RectTransform root, string text)
        {
            Text label = FindLabel(root);
            if (label != null) label.text = text;
        }

        private static Text FindLabel(RectTransform root)
        {
            Transform labelTransform = root.Find("_txt");
            return labelTransform != null
                ? labelTransform.GetComponent<Text>()
                : root.GetComponentInChildren<Text>(true);
        }

        private static void AddDescription(RectTransform root, string title, string text)
        {
            root.gameObject.AddDescription(_ => new DescData
            {
                code = 1000,
                title = title,
                txt = text,
            });
        }

        // ═════════════════════════════════════════════════════════
        //  刷新
        // ═════════════════════════════════════════════════════════

        public static void Refresh(ModPersonEditView view)
        {
            if (!SocialRoleEditorState.TryGet(view, out var widget)) return;
            PersonCfg person = GetCurrentPerson(view);
            bool social = IsSocialRole(person);

            widget.Refreshing = true;
            try
            {
                if (person != null && person.id != widget.LastPersonId)
                {
                    widget.StageIndex = 0;
                    widget.LastPersonId = person.id;
                }

                widget.ModeRoot.gameObject.SetActive(social);
                if (!social)
                {
                    SetCustomRowsActive(widget, false);
                    // 暂时切换人物类型不能销毁作者资料。Runtime 本身还会检查 init[0]，
                    // 所以标记留在 note 中时完全惰性；以后切回 2/3/4 可无损恢复全部阶段。
                }
                else
                {
                    PersonGrowCfg grow = GetCurrentGrow(view);
                    bool padded = EnsureSafeLists(grow);
                    // 原 RefreshGrowUI 在本 Postfix 之前已经把空列表渲染进输入框；
                    // 仅在本次确实补齐防越界槽位时同步文本，避免无意义覆盖作者正在编辑的内容。
                    if (padded && grow != null)
                    {
                        view.input_classname.SetTextWithoutNotify(ModCtrl.ListToStr(grow.className));
                        view.input_studyrank.SetTextWithoutNotify(ModCtrl.ListToStr(grow.studyRank));
                    }

                    SocialRoleProfileData data = ReadData(person);
                    widget.StageIndex = Mathf.Clamp(widget.StageIndex, 0, data.Stages.Count - 1);
                    ProfileStage stage = data.Stages[widget.StageIndex];

                    widget.ModeDropdown.SetValueWithoutNotify((int)data.Mode);
                    RebuildStageOptions(widget, data);

                    StageTriggerType trigType = stage?.Trigger?.Type ?? StageTriggerType.None;
                    widget.TrigTypeDropdown.SetValueWithoutNotify((int)trigType);
                    widget.TrigIdInput.SetTextWithoutNotify(
                        stage?.Trigger != null && stage.Trigger.EvtId > 0
                            ? stage.Trigger.EvtId.ToString()
                            : string.Empty);
                    widget.TrigParamInput.SetTextWithoutNotify(FormatParam(stage?.Trigger));
                    widget.TrigTargetInput.SetTextWithoutNotify(
                        trigType == StageTriggerType.EventValue && stage?.Trigger != null
                            ? stage.Trigger.Target.ToString("0.##", CultureInfo.InvariantCulture)
                            : string.Empty);

                    widget.StudyRankDropdown.SetValueWithoutNotify(stage?.ShowStudyRank == true ? 1 : 0);
                    widget.PrimaryLabelInput.SetTextWithoutNotify(stage?.PrimaryLabel ?? string.Empty);
                    widget.PrimaryValueInput.SetTextWithoutNotify(stage?.PrimaryValue ?? string.Empty);
                    widget.SecondaryLabelInput.SetTextWithoutNotify(stage?.SecondaryLabel ?? string.Empty);
                    widget.SecondaryValueInput.SetTextWithoutNotify(stage?.SecondaryValue ?? string.Empty);

                    bool custom = data.IsCustom;
                    SetCustomRowsActive(widget, custom);
                    if (custom) UpdateTriggerRows(widget, trigType);
                    if (view.p_classname != null) view.p_classname.gameObject.SetActive(!custom);
                    if (view.p_studyrank != null) view.p_studyrank.gameObject.SetActive(!custom);
                }
            }
            finally
            {
                widget.Refreshing = false;
            }

            if (view.Content != null)
                LayoutRebuilder.MarkLayoutForRebuild(view.Content);
        }

        private static string FormatParam(StageTrigger trigger)
        {
            if (trigger == null) return string.Empty;
            return trigger.Type switch
            {
                StageTriggerType.EventHappened => (trigger.Count < 1 ? 1 : trigger.Count).ToString(),
                StageTriggerType.EventValue => trigger.Pos.ToString(),
                _ => string.Empty,
            };
        }

        private static void RebuildStageOptions(SocialRoleEditorWidget widget, SocialRoleProfileData data)
        {
            var options = new List<string>(data.Stages.Count);
            for (int i = 0; i < data.Stages.Count; i++)
                options.Add($"阶段{i + 1} · {SocialRoleJsonCodec.TriggerSummary(data.Stages[i]?.Trigger)}");
            widget.StageSelectDropdown.ClearOptions();
            widget.StageSelectDropdown.AddOptions(options);
            widget.StageSelectDropdown.SetValueWithoutNotify(widget.StageIndex);
            widget.StageSelectDropdown.RefreshShownValue();
        }

        /// <summary>按触发类型切换 ID/参数/目标值三行的显隐和标签。</summary>
        private static void UpdateTriggerRows(SocialRoleEditorWidget widget, StageTriggerType type)
        {
            bool showId = type != StageTriggerType.None;
            bool showParam = type == StageTriggerType.EventHappened || type == StageTriggerType.EventValue;
            bool showTarget = type == StageTriggerType.EventValue;

            widget.TrigIdRoot.gameObject.SetActive(showId);
            widget.TrigParamRoot.gameObject.SetActive(showParam);
            widget.TrigTargetRoot.gameObject.SetActive(showTarget);

            if (widget.TrigIdLabel != null)
                widget.TrigIdLabel.text = type == StageTriggerType.OptionSelected ? "选项 ID" : "事件 ID";
            if (widget.TrigParamLabel != null)
                widget.TrigParamLabel.text = type == StageTriggerType.EventValue ? "存档槽位" : "发生次数≥";
        }

        private static void SetCustomRowsActive(SocialRoleEditorWidget widget, bool active)
        {
            widget.StageSelectRoot.gameObject.SetActive(active);
            widget.StageOpsRoot.gameObject.SetActive(active);
            widget.TrigTypeRoot.gameObject.SetActive(active);
            if (!active)
            {
                widget.TrigIdRoot.gameObject.SetActive(false);
                widget.TrigParamRoot.gameObject.SetActive(false);
                widget.TrigTargetRoot.gameObject.SetActive(false);
            }
            widget.PrimaryLabelRoot.gameObject.SetActive(active);
            widget.PrimaryValueRoot.gameObject.SetActive(active);
            widget.SecondaryLabelRoot.gameObject.SetActive(active);
            widget.SecondaryValueRoot.gameObject.SetActive(active);
            widget.StudyRankRoot.gameObject.SetActive(active);
        }

        // ═════════════════════════════════════════════════════════
        //  事件处理
        // ═════════════════════════════════════════════════════════

        private static void OnModeChanged(
            ModPersonEditView view,
            SocialRoleEditorWidget widget,
            int modeValue)
        {
            if (widget.Refreshing) return;
            var newMode = (SocialRoleProfileMode)Mathf.Clamp(modeValue, 0, 3);
            var oldMode = SocialRoleProfileMode.Student;
            PersonCfg person = GetCurrentPerson(view);
            if (SocialRoleProfileCodec.TryRead(person, out var oldData)) oldMode = oldData.Mode;

            string oldPrimaryPreset = PresetPrimary(oldMode);
            string oldSecondaryPreset = PresetSecondary(oldMode);
            string primary = widget.PrimaryLabelInput.text;
            string secondary = widget.SecondaryLabelInput.text;

            // 只有空标题或仍是旧模式默认标题时才自动替换，避免覆盖作者手填内容。
            if (string.IsNullOrWhiteSpace(primary) || primary == oldPrimaryPreset)
                widget.PrimaryLabelInput.SetTextWithoutNotify(PresetPrimary(newMode));
            if (string.IsNullOrWhiteSpace(secondary) || secondary == oldSecondaryPreset)
                widget.SecondaryLabelInput.SetTextWithoutNotify(PresetSecondary(newMode));
            if (oldMode == SocialRoleProfileMode.Student && newMode != SocialRoleProfileMode.Student)
                widget.StudyRankDropdown.SetValueWithoutNotify(0);

            WriteCurrent(view, widget);
            Refresh(view);
        }

        private static void OnStageSelected(ModPersonEditView view, SocialRoleEditorWidget widget, int index)
        {
            if (widget.Refreshing) return;
            widget.StageIndex = Mathf.Max(0, index);
            Refresh(view);
        }

        private static void OnTriggerTypeChanged(ModPersonEditView view, SocialRoleEditorWidget widget, int typeValue)
        {
            if (widget.Refreshing) return;
            PersonCfg person = GetCurrentPerson(view);
            if (!IsSocialRole(person)) return;

            // 只改类型，不把参数框里旧含义的数字写进新类型的槽位（例如次数误写成槽位）。
            SocialRoleProfileData data = ReadData(person);
            widget.StageIndex = Mathf.Clamp(widget.StageIndex, 0, data.Stages.Count - 1);
            ProfileStage stage = data.Stages[widget.StageIndex];
            stage.Trigger ??= new StageTrigger();
            stage.Trigger.Type = (StageTriggerType)Mathf.Clamp(typeValue, 0, 3);
            if (stage.Trigger.Count < 1) stage.Trigger.Count = 1;
            person.note = SocialRoleProfileCodec.Write(person.note, data);

            Refresh(view);
        }

        private static void OnAddStage(ModPersonEditView view, SocialRoleEditorWidget widget)
        {
            try
            {
                PersonCfg person = GetCurrentPerson(view);
                if (!IsSocialRole(person)) return;

                SocialRoleProfileData data = ReadData(person);
                if (data.Stages.Count >= MaxStages)
                {
                    ToastHelper.Toast($"阶段数量已达上限（{MaxStages}）");
                    return;
                }

                ProfileStage current = data.Stages[Mathf.Clamp(widget.StageIndex, 0, data.Stages.Count - 1)];
                data.Stages.Add(new ProfileStage
                {
                    // 默认给一个"事件已发生"的空条件：填上事件ID前不会生效，可安全存草稿。
                    Trigger = new StageTrigger { Type = StageTriggerType.EventHappened, EvtId = 0, Count = 1 },
                    PrimaryLabel = current?.PrimaryLabel ?? string.Empty,
                    PrimaryValue = string.Empty,
                    SecondaryLabel = current?.SecondaryLabel ?? string.Empty,
                    SecondaryValue = string.Empty,
                    ShowStudyRank = current?.ShowStudyRank ?? false,
                });
                widget.StageIndex = data.Stages.Count - 1;
                person.note = SocialRoleProfileCodec.Write(person.note, data);

                Refresh(view);
                ToastHelper.Toast($"已新增阶段 {data.Stages.Count}，填写事件或选项 ID 后才会生效");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[SocialRoleAddStage] {e}");
            }
        }

        private static void OnDeleteStage(ModPersonEditView view, SocialRoleEditorWidget widget)
        {
            try
            {
                PersonCfg person = GetCurrentPerson(view);
                if (!IsSocialRole(person)) return;

                SocialRoleProfileData data = ReadData(person);
                if (data.Stages.Count <= 1)
                {
                    ToastHelper.Toast("至少保留一个阶段作为初始资料");
                    return;
                }

                int idx = Mathf.Clamp(widget.StageIndex, 0, data.Stages.Count - 1);
                data.Stages.RemoveAt(idx);
                widget.StageIndex = Mathf.Max(0, idx - 1);
                person.note = SocialRoleProfileCodec.Write(person.note, data);

                Refresh(view);
                ToastHelper.Toast($"已删除阶段{idx + 1}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[SocialRoleDeleteStage] {e}");
            }
        }

        private static void OnExportJson(ModPersonEditView view, SocialRoleEditorWidget widget)
        {
            try
            {
                PersonCfg person = GetCurrentPerson(view);
                if (!IsSocialRole(person)) return;

                SocialRoleProfileData data = ReadData(person);
                GUIUtility.systemCopyBuffer = SocialRoleJsonCodec.ToJson(data);
                ToastHelper.Toast($"已将 {data.Stages.Count} 个阶段的资料 JSON 复制到剪贴板");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[SocialRoleExportJson] {e}");
                ToastHelper.Toast("导出失败，详见 BepInEx 日志");
            }
        }

        private static void OnImportJson(ModPersonEditView view, SocialRoleEditorWidget widget)
        {
            try
            {
                PersonCfg person = GetCurrentPerson(view);
                if (!IsSocialRole(person)) return;

                string json = GUIUtility.systemCopyBuffer;
                if (!SocialRoleJsonCodec.TryFromJson(json, out var data, out string error))
                {
                    ToastHelper.Toast("导入失败：" + error);
                    return;
                }

                person.note = SocialRoleProfileCodec.Write(person.note, data);
                widget.StageIndex = 0;
                Refresh(view);
                ToastHelper.Toast($"已导入 {data.Stages.Count} 个阶段（{ModeLabel(data.Mode)}）");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[SocialRoleImportJson] {e}");
                ToastHelper.Toast("导入失败，详见 BepInEx 日志");
            }
        }

        private static string ModeLabel(SocialRoleProfileMode mode) => mode switch
        {
            SocialRoleProfileMode.Teacher => "教师",
            SocialRoleProfileMode.Adult => "成人",
            SocialRoleProfileMode.Custom => "自定义",
            _ => "学生",
        };

        private static string PresetPrimary(SocialRoleProfileMode mode)
        {
            return mode switch
            {
                SocialRoleProfileMode.Teacher => "学校",
                SocialRoleProfileMode.Adult => "单位",
                SocialRoleProfileMode.Custom => "资料一",
                _ => "学校",
            };
        }

        private static string PresetSecondary(SocialRoleProfileMode mode)
        {
            return mode switch
            {
                SocialRoleProfileMode.Teacher => "职务",
                SocialRoleProfileMode.Adult => "身份",
                SocialRoleProfileMode.Custom => "资料二",
                _ => "班级",
            };
        }

        // ═════════════════════════════════════════════════════════
        //  写入
        // ═════════════════════════════════════════════════════════

        private static void WriteCurrent(ModPersonEditView view, SocialRoleEditorWidget widget)
        {
            if (widget.Refreshing) return;
            PersonCfg person = GetCurrentPerson(view);
            if (!IsSocialRole(person)) return;

            // 读取现有资料再更新：UI 只编辑当前选中阶段，其余阶段原样保留。
            SocialRoleProfileData data = ReadData(person);
            data.Mode = (SocialRoleProfileMode)Mathf.Clamp(widget.ModeDropdown.value, 0, 3);

            widget.StageIndex = Mathf.Clamp(widget.StageIndex, 0, data.Stages.Count - 1);
            ProfileStage stage = data.Stages[widget.StageIndex];
            stage.Trigger ??= new StageTrigger { Type = StageTriggerType.None };

            var trigType = (StageTriggerType)Mathf.Clamp(widget.TrigTypeDropdown.value, 0, 3);
            stage.Trigger.Type = trigType;
            if (trigType != StageTriggerType.None)
                stage.Trigger.EvtId = ParseInt(widget.TrigIdInput.text);
            switch (trigType)
            {
                case StageTriggerType.EventHappened:
                    stage.Trigger.Count = Mathf.Max(1, ParseInt(widget.TrigParamInput.text, 1));
                    break;
                case StageTriggerType.EventValue:
                    stage.Trigger.Pos = Mathf.Max(0, ParseInt(widget.TrigParamInput.text));
                    stage.Trigger.Target = ParseFloat(widget.TrigTargetInput.text);
                    break;
            }

            stage.ShowStudyRank = widget.StudyRankDropdown.value == 1;
            stage.PrimaryLabel = widget.PrimaryLabelInput.text?.Trim();
            stage.PrimaryValue = widget.PrimaryValueInput.text?.Trim();
            stage.SecondaryLabel = widget.SecondaryLabelInput.text?.Trim();
            stage.SecondaryValue = widget.SecondaryValueInput.text?.Trim();

            person.note = SocialRoleProfileCodec.Write(person.note, data);
        }

        private static int ParseInt(string text, int fallback = 0)
        {
            return int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : fallback;
        }

        private static float ParseFloat(string text)
        {
            return float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v
                : 0f;
        }

        // ═════════════════════════════════════════════════════════
        //  原版数组防越界
        // ═════════════════════════════════════════════════════════

        /// <summary>
        /// 原版社交页会直接索引 className[0..3] 和 studyRank[GradeState]；
        /// 原人物编辑器新建的 PersonGrowCfg 却默认都是空列表。这里仅补齐防越界所需槽位，
        /// 不替作者决定属性、人格、特质等玩法数值。
        /// </summary>
        public static bool EnsureSafeLists(PersonGrowCfg grow)
        {
            if (grow == null) return false;
            bool changed = false;
            changed |= EnsureLength(ref grow.className, 4, -1);
            changed |= EnsureLength(ref grow.studyRank, 4, 0);
            if (grow.attr != null && grow.attr.Count > 0)
                changed |= EnsureLength(ref grow.attr, 3, 0f);
            if (grow.personalitys != null && grow.personalitys.Count > 0)
                changed |= EnsureLength(ref grow.personalitys, 8, 0f);
            return changed;
        }

        private static bool EnsureLength<T>(ref List<T> list, int length, T value)
        {
            bool changed = false;
            if (list == null)
            {
                list = new List<T>();
                changed = true;
            }
            while (list.Count < length)
            {
                list.Add(value);
                changed = true;
            }
            return changed;
        }
    }

    /// <summary>在原生人物编辑器中注入“可社交角色资料”字段。</summary>
    [HarmonyPatch(typeof(ModPersonEditView), "InitUI")]
    internal static class SocialRoleEditorInitPatch
    {
        private static void Postfix(ModPersonEditView __instance)
        {
            try
            {
                if (__instance.Content == null || __instance.p_init == null || __instance.p_name == null
                    || __instance.p_classname == null || __instance.p_studyrank == null)
                    return;
                if (__instance.Content.Find("p_social_profile_mode") != null) return;

                SocialRoleEditorWidget widget = SocialRoleEditorUtil.Create(__instance);
                SocialRoleEditorState.Set(__instance, widget);
                SocialRoleEditorUtil.Refresh(__instance);
                Plugin.Log.LogInfo("[SocialRoleEditor] 已向人物编辑器注入可社交角色资料字段（含阶段编辑）。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[SocialRoleEditorInit] {e}");
            }
        }
    }

    [HarmonyPatch(typeof(ModPersonEditView), "Select", typeof(PersonCfg))]
    internal static class SocialRoleEditorSelectPatch
    {
        private static void Postfix(ModPersonEditView __instance)
        {
            try { SocialRoleEditorUtil.Refresh(__instance); }
            catch (Exception e) { Plugin.Log.LogError($"[SocialRoleEditorSelect] {e}"); }
        }
    }

    [HarmonyPatch(typeof(ModPersonEditView), "RefreshGrowUI")]
    internal static class SocialRoleEditorVisibilityPatch
    {
        private static void Postfix(ModPersonEditView __instance)
        {
            try { SocialRoleEditorUtil.Refresh(__instance); }
            catch (Exception e) { Plugin.Log.LogError($"[SocialRoleEditorVisibility] {e}"); }
        }
    }

    /// <summary>让配置列表明确显示：NPC人物页面同时就是 Plus 的可社交角色编辑器。</summary>
    [HarmonyPatch(typeof(ModPageUploadView), "OnRenderItem")]
    internal static class SocialRoleEditorEntryNamePatch
    {
        private static void Postfix(UICell _cell)
        {
            try
            {
                if (_cell is not Cell_ModCfgItemUI cell || cell.data is not Type type) return;
                if (type == typeof(PersonCfg)) cell.txt_name.text = "NPC 人物 / 可社交角色（增强）";
            }
            catch (Exception e) { Plugin.Log.LogError($"[SocialRoleEditorEntryName] {e}"); }
        }
    }

}
