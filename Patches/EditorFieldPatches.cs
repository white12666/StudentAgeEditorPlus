using System.Collections.Generic;
using System.Linq;
using Config;
using GenUI.Mod;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 读取 ModNormalEditView 的 protected 字段的小工具。
    /// </summary>
    internal static class EditViewAccess
    {
        public static System.Type CfgType(object view) =>
            Traverse.Create(view).Field("cfgType").GetValue<System.Type>();

        public static List<ModFieldItem> ModFields(object view) =>
            Traverse.Create(view).Field("modFields").GetValue<List<ModFieldItem>>();

        public static UIItemGroup PropertyGroup(object view) =>
            Traverse.Create(view).Field("itemgroup_property").GetValue<UIItemGroup>();

        public static object CurSelect(object view) =>
            Traverse.Create(view).Field("curSelect").GetValue();
    }

    /// <summary>
    /// 增强 A：事件编辑器里 mapId（场景）字段不驱动"进入场景触发"，只是筛选器，
    /// 容易让作者误以为设了它就能触发。这里给它的悬浮说明追加一句提示，引导用『类型』字段。
    /// </summary>
    [HarmonyPatch(typeof(ModNormalEditView), "InitFields")]
    internal static class MapIdHintPatch
    {
        private const string Marker = "【提示】";
        private const string Hint =
            "\n【提示】此字段仅用于筛选，不会让事件在进入该场景时触发。" +
            "要让事件在进入/离开某场景时触发，请改用上方『类型』字段选择「场景名(进入)」或「场景名(离开)」。";

        private static void Postfix(ModNormalEditView __instance)
        {
            try
            {
                if (EditViewAccess.CfgType(__instance) != typeof(EvtCfg)) return;
                var fields = EditViewAccess.ModFields(__instance);
                if (fields == null) return;
                foreach (var mf in fields)
                {
                    if (mf?.field?.Name != "mapId" || mf.attr == null) continue;
                    if (mf.attr.Description != null && mf.attr.Description.Contains(Marker)) return;
                    mf.attr.Description = (mf.attr.Description ?? "") + Hint;
                    return;
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MapIdHint] {e}"); }
        }
    }

    /// <summary>
    /// 修复 CG 编辑器（CGCfg 走通用编辑器 ModNormalEditView）"不显示分组和编号"：
    ///   - group(组别)：原本带 Hide=true 被隐藏 → 解除隐藏，可见可编辑。
    ///   - idx(编号)：原本无 [CfgProperty] 不在编辑器 → 注入一个字段项。
    /// 两者决定 CG 在收藏画廊里的分类(group)与排序编号(idx，>0 才显示成 001/002…)，
    /// group 0/1 还决定 CG 能否被当作背景选用。
    /// </summary>
    [HarmonyPatch(typeof(ModNormalEditView), "InitFields")]
    internal static class CgEditorFieldsPatch
    {
        private static void Postfix(ModNormalEditView __instance)
        {
            try
            {
                if (EditViewAccess.CfgType(__instance) != typeof(CGCfg)) return;
                var fields = EditViewAccess.ModFields(__instance);
                if (fields == null) return;

                bool changed = false;

                // 1) 解除 group 的隐藏，并补充面向 MOD 作者的说明
                var groupItem = fields.FirstOrDefault(m => m?.field?.Name == "group");
                if (groupItem != null && groupItem.attr != null && groupItem.attr.Hide)
                {
                    groupItem.attr.Hide = false;
                    groupItem.attr.Description =
                        "决定此 CG 在游戏收藏画廊里出现在哪个分类标签下。\n" +
                        "· 0 或 1：出现在前两个标签，且可被当作背景图或结局配图选用（推荐）\n" +
                        "· 2：出现在第三个标签，仅供收藏浏览\n" +
                        "· 3（默认）：出现在特殊分类，不按人物归类\n" +
                        "· 4：特殊分类，需配合 startTalks 实现解锁条件\n" +
                        "· 小于 0：不出现在画廊中（但事件仍可触发显示）";
                    changed = true;
                }

                // 2) 注入 idx(编号) 字段（原本无特性，不在编辑器）
                if (!fields.Any(m => m?.field?.Name == "idx"))
                {
                    var idxField = typeof(CGCfg).GetField("idx");
                    if (idxField != null)
                    {
                        fields.Add(new ModFieldItem
                        {
                            field = idxField,
                            attr = new CfgPropertyAttribute(
                                CfgPropertyType.Default, "编号",
                                "此 CG 在收藏画廊中的排列顺序。画廊按此数字从小到大排序。\n" +
                                "· 填 0 或留空：排在最前面，且不显示编号标签\n" +
                                "· 填大于 0 的数：按数字排序，并在缩略图上显示三位编号（如 001、002）\n" +
                                "建议同一分组内从 1 开始递增，与原版 CG 保持一致的编号风格。"),
                            range = null,
                            depend = null
                        });
                        changed = true;
                    }
                }

                // 3) 补充 gender(筛选) 的说明（原版描述来自内部文本，对 MOD 作者不够清晰）
                var genderItem = fields.FirstOrDefault(m => m?.field?.Name == "gender");
                if (genderItem != null && genderItem.attr != null)
                {
                    genderItem.attr.Description =
                        "此 CG 在收藏画廊中的人物性别筛选。\n" +
                        "· 0（默认/未知）：不限制，所有玩家都可见\n" +
                        "· 1（男）：仅在玩家选择男性主角时显示\n" +
                        "· 2（女）：仅在玩家选择女性主角时显示\n" +
                        "当 CG 的 urls 有多张图时，游戏还会根据玩家性别自动选择对应的图片（urls[0]=男，urls[1]=女）。";
                    changed = true;
                }


                if (changed)
                    EditViewAccess.PropertyGroup(__instance)?.SetDatas(fields, null);
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[CgEditorFields] {e}"); }
        }
    }

    /// <summary>
    /// 修复：物品编辑器不显示「持有上限(maxcount)」字段。
    ///
    /// 成因：ItemCfg.maxcount 带 Hide=true，通用编辑器(ModNormalEditView)跳过它；
    /// DefaultValue=1 意味着 MOD 作者新建物品时该值固定为 1。
    /// 对消耗品(type=1)影响最大——BagMgr.AddItem 用 maxcount 做 Clamp 上限，
    /// UseItem 每次减 1，maxcount=1 时玩家最多持有 1 个，买第 2 个被截断，用掉后变 0。
    /// 原版消耗品的 maxcount 各不相同（食物类=2，奖券=10 等），作者无法在编辑器设置此值。
    ///
    /// 修复：Postfix on InitFields（仅 ItemCfg），解除 maxcount 的隐藏并补充取值说明。
    /// 对玩家零依赖：产出的 mod 只是 maxcount 值，原版客户端正常读取。
    /// </summary>
    [HarmonyPatch(typeof(ModNormalEditView), "InitFields")]
    internal static class ItemMaxCountPatch
    {
        private static void Postfix(ModNormalEditView __instance)
        {
            try
            {
                if (EditViewAccess.CfgType(__instance) != typeof(ItemCfg)) return;
                var fields = EditViewAccess.ModFields(__instance);
                if (fields == null) return;

                var maxCountItem = fields.FirstOrDefault(m => m?.field?.Name == "maxcount");
                if (maxCountItem != null && maxCountItem.attr != null && maxCountItem.attr.Hide)
                {
                    maxCountItem.attr.Hide = false;
                    maxCountItem.attr.Description =
                        "物品的持有上限（背包里最多同时持有的数量）。\n" +
                        "· 消耗品：建议按需设置（如食物类常设为 2~10），默认 1 表示只能持有 1 个\n" +
                        "· 珍视物品 / 工具：通常为 1（装备型，不叠加）\n" +
                        "添加物品时若已有该物品，数量会被限制在此值以内。";
                    EditViewAccess.PropertyGroup(__instance)?.SetDatas(fields, null);
                    Plugin.Log.LogInfo("[ItemMaxCount] 已解除 maxcount 字段的隐藏。");
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[ItemMaxCount] {e}"); }
        }
    }

    /// <summary>
    /// QQ 空间内容编辑增强：
    ///   - KZoneContentCfg 本身没有显式 type 字段，游戏靠 id 是否以 99 开头区分“日志/说说”；
    ///     这里把这个隐式规则显示到左侧列表和 ID 字段名里，避免作者误判。
    ///   - 日志标题 title 是公开字段但没有 [CfgProperty]，原版通用编辑器不会显示；
    ///     这里注入“日志标题”字段，仅在日志条目中显示。
    ///   - QQ 空间正文允许用 \n 分段，编辑器里改成多行输入，并把旧数据里的字面量 \n 显示成真实换行。
    /// </summary>
    [HarmonyPatch(typeof(ModNormalEditView), "InitFields")]
    internal static class KZoneContentEditorFieldsPatch
    {
        private static void Postfix(ModNormalEditView __instance)
        {
            try
            {
                var cfgType = EditViewAccess.CfgType(__instance);
                var fields = EditViewAccess.ModFields(__instance);
                if (fields == null) return;

                bool changed = false;

                if (cfgType == typeof(KZoneContentCfg))
                {
                    var idItem = fields.FirstOrDefault(m => m?.field?.Name == "id");
                    if (idItem != null && idItem.attr != null)
                    {
                        idItem.attr.Name = "ID（决定类型）";
                        idItem.attr.Description =
                            "QQ 空间内容没有单独的 type 字段，游戏用 ID 前缀决定显示位置：\n" +
                            "· ID 以 99 开头：日志，会出现在 QQ 空间『日志』页，需要填写“日志标题”\n" +
                            "· 其它 ID：说说，会出现在 QQ 空间『说说』页，不使用“日志标题”\n" +
                            "原版日志常见格式为 9900301、9910101 等。";
                        changed = true;
                    }

                    var contentItem = fields.FirstOrDefault(m => m?.field?.Name == "content");
                    if (contentItem != null && contentItem.attr != null)
                    {
                        contentItem.attr.Name = "正文";
                        contentItem.attr.Description =
                            "QQ 空间正文。支持直接回车分段；也兼容原配置里写成 " + @"\n" + " 的分段。\n" +
                            "说说和日志都使用此字段作为正文内容。";


                        changed = true;
                    }

                    var roleItem = fields.FirstOrDefault(m => m?.field?.Name == "role");
                    if (roleItem != null && roleItem.attr != null)
                    {
                        roleItem.attr.Name = "发布者ID";
                        roleItem.attr.Description =
                            "这条 QQ 空间内容的发布者。填写人物 ID 后，下方会自动显示对应的人物名。\n" +
                            "0 表示主角/玩家；其它 ID 优先按 QQ 空间资料名显示，并附带人物本名。";
                        changed = true;
                    }

                    var thumbsItem = fields.FirstOrDefault(m => m?.field?.Name == "thumbs");
                    if (thumbsItem != null && thumbsItem.attr != null)
                    {
                        thumbsItem.attr.Description =
                            "自动点赞列表，格式：人物ID,延时秒；人物ID,延时秒……\n" +
                            "下方会自动解析每个点赞者对应的人物名。";
                        changed = true;
                    }

                    var commentsItem = fields.FirstOrDefault(m => m?.field?.Name == "comments");
                    if (commentsItem != null && commentsItem.attr != null)
                    {
                        commentsItem.attr.Description =
                            "动态发出后自动出现的评论，格式：评论ID,延时秒；评论ID,延时秒……\n" +
                            "评论内容在『空间动态评论』中配置，下方会自动显示评论摘要。";
                        changed = true;
                    }

                    var optionsItem = fields.FirstOrDefault(m => m?.field?.Name == "options");
                    if (optionsItem != null && optionsItem.attr != null)
                    {
                        optionsItem.attr.Description =
                            "玩家可选择的评论 ID，可填多个，用逗号分隔。\n" +
                            "这些评论同样在『空间动态评论』中配置，下方会自动显示评论摘要。";
                        changed = true;
                    }

                    if (!fields.Any(m => m?.field?.Name == "title"))
                    {
                        var titleField = typeof(KZoneContentCfg).GetField("title");
                        if (titleField != null)
                        {
                            fields.Insert(System.Math.Min(2, fields.Count), new ModFieldItem
                            {
                                field = titleField,
                                attr = new CfgPropertyAttribute(
                                    CfgPropertyType.Default,
                                    "日志标题",
                                    "仅日志使用：当 ID 以 99 开头时，此字段会显示为日志标题。\n" +
                                    "说说不会读取 title，标题字段会自动隐藏。"),
                                range = null,
                                depend = null
                            });
                            changed = true;
                        }
                    }

                    // 访问数在游戏发出该条空间内容时会读取，但原字段没有特性，通用编辑器默认不可见。
                    if (!fields.Any(m => m?.field?.Name == "visitCnt"))
                    {
                        var visitCntField = typeof(KZoneContentCfg).GetField("visitCnt");
                        if (visitCntField != null)
                        {
                            fields.Add(new ModFieldItem
                            {
                                field = visitCntField,
                                attr = new CfgPropertyAttribute(
                                    CfgPropertyType.Default,
                                    "浏览量",
                                    "发出该条 QQ 空间内容时的初始浏览量。\n" +
                                    "· 0：游戏自动随机生成\n" +
                                    "· 大于 0：使用指定浏览量\n" +
                                    "· -1：不显示浏览量"),
                                range = null,
                                depend = null
                            });
                            changed = true;
                        }
                    }
                }
                else if (cfgType == typeof(KZoneCommentCfg))
                {
                    var contentItem = fields.FirstOrDefault(m => m?.field?.Name == "content");
                    if (contentItem != null && contentItem.attr != null)
                    {
                        contentItem.attr.Name = "评论正文";
                        contentItem.attr.Description =
                            "QQ 空间评论内容。支持直接回车分段；也兼容原配置里写成 " + @"\n" + " 的分段。";

                        changed = true;
                    }

                    var rolesItem = fields.FirstOrDefault(m => m?.field?.Name == "roles");
                    if (rolesItem != null && rolesItem.attr != null)
                    {
                        rolesItem.attr.Name = "评论人物ID";
                        rolesItem.attr.Description =
                            "QQ 空间评论相关人物。通常第 1 个是评论者；第 2 个存在时表示回复/提到的对象。\n" +
                            "填写如 0,102 后，下方会自动显示对应的人物名。";
                        changed = true;
                    }

                    var parentItem = fields.FirstOrDefault(m => m?.field?.Name == "parent");
                    if (parentItem != null && parentItem.attr != null)
                    {
                        parentItem.attr.Description =
                            "若此评论直接回复动态，填 0。\n" +
                            "若此评论是回复某条评论，填写最上级评论 ID；下方会自动显示对应评论摘要。";
                        changed = true;
                    }

                    var commentsItem = fields.FirstOrDefault(m => m?.field?.Name == "comments");
                    if (commentsItem != null && commentsItem.attr != null)
                    {
                        commentsItem.attr.Name = "自带回复ID";
                        commentsItem.attr.Description =
                            "此评论出现后自动继续出现的回复，格式：评论ID,延时秒；评论ID,延时秒……\n" +
                            "下方会自动显示每条回复的评论者和正文摘要。";
                        changed = true;
                    }

                    var optionsItem = fields.FirstOrDefault(m => m?.field?.Name == "options");
                    if (optionsItem != null && optionsItem.attr != null)
                    {
                        optionsItem.attr.Name = "玩家回复ID";
                        optionsItem.attr.Description =
                            "玩家可选择的回复 ID，可填多个，用逗号分隔。\n" +
                            "下方会自动显示每条可选回复的评论者和正文摘要。";
                        changed = true;
                    }
                }
                else if (cfgType == typeof(KZoneProfileCfg))
                {
                    var idItem = fields.FirstOrDefault(m => m?.field?.Name == "id");
                    if (idItem != null && idItem.attr != null)
                    {
                        idItem.attr.Name = "资料ID（人物ID）";
                        idItem.attr.Description = "此空间资料对应的人物 ID。0 表示主角/玩家；下方会自动显示对应人物名。";
                        changed = true;
                    }

                    var bgmItem = fields.FirstOrDefault(m => m?.field?.Name == "bgm");
                    if (bgmItem != null && bgmItem.attr != null)
                    {
                        bgmItem.attr.Name = "背景音乐ID";
                        bgmItem.attr.Description = "空间主页背景音乐，对应 AudioCfg 的 ID。0 表示不指定音乐；下方会自动显示音乐名。";
                        changed = true;
                    }
                }

                if (changed)
                    EditViewAccess.PropertyGroup(__instance)?.SetDatas(fields, null);
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[KZoneContentEditorFields] {e}"); }
        }
    }

    [HarmonyPatch(typeof(ModNormalEditView), "OnRenderItem")]
    internal static class KZoneContentListDisplayPatch
    {
        private static void Postfix(ModNormalEditView __instance, UICell _cell)
        {
            try
            {
                if (EditViewAccess.CfgType(__instance) != typeof(KZoneContentCfg)) return;
                var cell = _cell as Cell_ModNormalEditItemUI;
                var cfg = cell?.data as KZoneContentCfg;
                if (cell == null || cfg == null) return;

                string type = KZoneEditorTextUtil.IsBlogId(cfg.id) ? "日志" : "说说";
                string summary = KZoneEditorTextUtil.IsBlogId(cfg.id)
                    ? KZoneEditorTextUtil.FirstNotEmpty(cfg.title, cfg.content)
                    : cfg.content;
                summary = KZoneEditorTextUtil.ToListSnippet(summary);
                cell.txtex_name.text = cfg.id == 0
                    ? $"[{type}]{summary}"
                    : $"[{cfg.id}][{type}]{summary}";
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[KZoneContentListDisplay] {e}"); }
        }
    }

    [HarmonyPatch(typeof(ModNormalEditView), "OnRenderProperty")]
    internal static class KZoneTextPropertyDisplayPatch
    {
        private static void Postfix(ModNormalEditView __instance, UICell _cell)
        {
            try
            {
                var cfgType = EditViewAccess.CfgType(__instance);
                var cell = _cell as Cell_ModNormalPropertyItemUI;
                var fieldItem = cell?.data as ModFieldItem;
                if (cell == null || fieldItem?.field == null) return;

                if (!KZoneEditorTextUtil.IsKZoneEditorType(cfgType))
                {
                    KZoneEditorTextUtil.HideRoleHint(cell);
                    KZoneEditorTextUtil.RestoreDefaultLayout(cell);
                    return;
                }

                string fieldName = fieldItem.field.Name;
                bool currentContentIsBlog = false;
                if (cfgType == typeof(KZoneContentCfg))
                {
                    var cur = EditViewAccess.CurSelect(__instance) as KZoneContentCfg;
                    bool isBlog = currentContentIsBlog = cur != null && KZoneEditorTextUtil.IsBlogId(cur.id);

                    if (fieldName == "id" && cur != null)
                        cell.txt_name.text = isBlog ? "ID（日志）" : "ID（说说）";

                    if (fieldName == "title")
                    {
                        cell.gameObject.SetActive(isBlog);
                        if (!isBlog) return;
                    }
                }

                bool isHintField = KZoneEditorTextUtil.IsInfoHintField(cfgType, fieldName);
                if (isHintField)
                {
                    bool extraGap = KZoneEditorTextUtil.UseExtraHintGap(cfgType, fieldName, currentContentIsBlog);
                    KZoneEditorTextUtil.ApplyRoleHint(cell, extraGap);
                    KZoneEditorTextUtil.UpdateInfoHint(cell, cfgType, fieldName);
                    KZoneEditorTextUtil.EnsureInfoHintRefreshOnEdit(__instance, cell);
                }
                else
                {
                    KZoneEditorTextUtil.HideRoleHint(cell);
                }

                bool isParagraphField = KZoneEditorTextUtil.IsParagraphField(cfgType, fieldName);
                if (!isParagraphField)
                {
                    if (!isHintField) KZoneEditorTextUtil.RestoreDefaultLayout(cell);
                    return;
                }
                if (!cell.input_value.gameObject.activeSelf) return;

                // 原版通用编辑器按单行输入框渲染；QQ 空间正文需要保留段落。
                KZoneEditorTextUtil.ApplyParagraphLayout(cell);
                cell.input_value.contentType = InputField.ContentType.Standard;
                cell.input_value.lineType = InputField.LineType.MultiLineNewline;
                if (cell.input_value.textComponent != null)
                {
                    cell.input_value.textComponent.horizontalOverflow = HorizontalWrapMode.Wrap;
                    cell.input_value.textComponent.verticalOverflow = VerticalWrapMode.Overflow;
                }

                string displayText = KZoneEditorTextUtil.ToEditorText(cell.input_value.text);
                if (displayText != cell.input_value.text)
                    cell.input_value.SetTextWithoutNotify(displayText);
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[KZoneTextPropertyDisplay] {e}"); }
        }
    }

    internal static class KZoneEditorTextUtil
    {
        public static bool IsBlogId(int id) => id.ToString().StartsWith("99");

        public static bool IsKZoneEditorType(System.Type cfgType)
        {
            return cfgType == typeof(KZoneContentCfg) ||
                   cfgType == typeof(KZoneCommentCfg) ||
                   cfgType == typeof(KZoneProfileCfg);
        }

        public static bool IsParagraphField(System.Type cfgType, string fieldName)
        {
            if (fieldName != "content") return false;
            return cfgType == typeof(KZoneContentCfg) || cfgType == typeof(KZoneCommentCfg);
        }

        public static bool IsInfoHintField(System.Type cfgType, string fieldName)
        {
            if (cfgType == typeof(KZoneContentCfg))
                return fieldName == "role" || fieldName == "thumbs" || fieldName == "comments" || fieldName == "options";
            if (cfgType == typeof(KZoneCommentCfg))
                return fieldName == "roles" || fieldName == "parent" || fieldName == "comments" || fieldName == "options";
            if (cfgType == typeof(KZoneProfileCfg))
                return fieldName == "id" || fieldName == "bgm";
            return false;
        }

        public static bool UseExtraHintGap(System.Type cfgType, string fieldName, bool currentContentIsBlog)
        {
            if (cfgType == typeof(KZoneContentCfg))
            {
                // 说说的发布者 ID 已验证刚好；日志发布者后面紧跟“日志标题”，需要额外留白。
                if (fieldName == "role") return currentContentIsBlog;
                // 这些字段后面都紧跟下一项，灰字摘要比人物名更长，使用和日志/评论人物一致的额外 spacer。
                return fieldName == "thumbs" || fieldName == "comments" || fieldName == "options";
            }

            if (cfgType == typeof(KZoneCommentCfg))
            {
                // roles→根部评论、parent→正文、comments→options、options→effect 都需要把下一项推开。
                return fieldName == "roles" || fieldName == "parent" || fieldName == "comments" || fieldName == "options";
            }

            if (cfgType == typeof(KZoneProfileCfg))
            {
                // 资料ID→名称、背景音乐→字体，默认 12px 不足以完整容纳灰字。
                return fieldName == "id" || fieldName == "bgm";
            }

            return false;
        }

        public static void ApplyParagraphLayout(Cell_ModNormalPropertyItemUI cell)
        {
            RememberDefaultLayout(cell);
            float cellHeight = Mathf.Max(GetStoredFloat(cell, "KZoneCellHeight", 0f), 120f);
            float inputHeight = Mathf.Max(GetStoredFloat(cell, "KZoneInputHeight", 0f), 88f);
            SetHeight(cell.transform, cellHeight);
            SetHeight(cell.input_value.transform as RectTransform, inputHeight);
            SetHeight(cell.Text, inputHeight - 8f);
            SetHeight(cell.Placeholder, inputHeight - 8f);
            SetLayoutHeight(cell, cellHeight);
        }

        public static void RestoreDefaultLayout(Cell_ModNormalPropertyItemUI cell)
        {
            if (cell == null) return;
            object cellHeight = cell.GetKeyObj<object>("KZoneCellHeight");
            object inputHeight = cell.GetKeyObj<object>("KZoneInputHeight");
            object textHeight = cell.GetKeyObj<object>("KZoneTextHeight");
            if (cellHeight is float ch) SetHeight(cell.transform, ch);
            if (inputHeight is float ih) SetHeight(cell.input_value.transform as RectTransform, ih);
            if (textHeight is float th)
            {
                SetHeight(cell.Text, th);
                SetHeight(cell.Placeholder, th);
            }
            RestoreLayoutHeight(cell);
        }

        private static void RememberDefaultLayout(Cell_ModNormalPropertyItemUI cell)
        {
            if (cell == null || cell.GetKeyObj<object>("KZoneCellHeight") != null) return;
            cell.SetKeyObj("KZoneCellHeight", cell.transform.rect.height);
            cell.SetKeyObj("KZoneInputHeight", (cell.input_value.transform as RectTransform)?.rect.height ?? 0f);
            cell.SetKeyObj("KZoneTextHeight", cell.Text != null ? cell.Text.rect.height : 0f);

            var layout = cell.gameObject.GetComponent<LayoutElement>();
            cell.SetKeyObj("KZoneHadLayoutElement", layout != null);
            if (layout != null)
            {
                cell.SetKeyObj("KZoneLayoutEnabled", layout.enabled);
                cell.SetKeyObj("KZoneLayoutIgnore", layout.ignoreLayout);
                cell.SetKeyObj("KZoneLayoutMinHeight", layout.minHeight);
                cell.SetKeyObj("KZoneLayoutPreferredHeight", layout.preferredHeight);
                cell.SetKeyObj("KZoneLayoutFlexibleHeight", layout.flexibleHeight);
            }
        }

        private static float GetStoredFloat(Cell_ModNormalPropertyItemUI cell, string key, float fallback)
        {
            object value = cell.GetKeyObj<object>(key);
            return value is float f ? f : fallback;
        }

        private static void SetHeight(RectTransform rt, float height)
        {
            if (rt != null && height > 0f)
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        }

        private static void SetLayoutHeight(Cell_ModNormalPropertyItemUI cell, float height)
        {
            if (cell == null || height <= 0f) return;
            var layout = cell.gameObject.GetComponent<LayoutElement>();
            if (layout == null) layout = cell.gameObject.AddComponent<LayoutElement>();
            layout.enabled = true;
            layout.ignoreLayout = false;
            layout.minHeight = height;
            layout.preferredHeight = height;
            layout.flexibleHeight = 0f;
            MarkParentLayoutDirty(cell);
        }

        private static void RestoreLayoutHeight(Cell_ModNormalPropertyItemUI cell)
        {
            if (cell == null) return;
            var layout = cell.gameObject.GetComponent<LayoutElement>();
            if (layout == null) return;

            object hadObj = cell.GetKeyObj<object>("KZoneHadLayoutElement");
            bool hadLayout = hadObj is bool b && b;
            if (!hadLayout)
            {
                UnityEngine.Object.Destroy(layout);
            }
            else
            {
                layout.enabled = cell.GetKeyObj<object>("KZoneLayoutEnabled") is bool enabled && enabled;
                layout.ignoreLayout = cell.GetKeyObj<object>("KZoneLayoutIgnore") is bool ignore && ignore;
                if (cell.GetKeyObj<object>("KZoneLayoutMinHeight") is float minHeight) layout.minHeight = minHeight;
                if (cell.GetKeyObj<object>("KZoneLayoutPreferredHeight") is float preferredHeight) layout.preferredHeight = preferredHeight;
                if (cell.GetKeyObj<object>("KZoneLayoutFlexibleHeight") is float flexibleHeight) layout.flexibleHeight = flexibleHeight;
            }
            MarkParentLayoutDirty(cell);
        }

        private static void MarkParentLayoutDirty(Cell_ModNormalPropertyItemUI cell)
        {
            var parent = cell?.transform?.parent as RectTransform;
            if (parent != null) LayoutRebuilder.MarkLayoutForRebuild(parent);
        }

        public static void ApplyRoleHint(Cell_ModNormalPropertyItemUI cell, bool addHalfFontGap = false)
        {
            if (cell == null) return;
            RememberDefaultLayout(cell);
            RestoreDefaultLayout(cell);
            var hint = GetOrCreateRoleHint(cell);
            hint.gameObject.SetActive(true);

            // 不再拉高“装 ID 的那一栏”本体；改为在该栏后面插入不可见 spacer。
            // 这样视觉上是“ID 栏和下一项之间的间隔变长”，而不是 ID 框本身变高。
            // 上一版间隔偏宽，这里整体缩短为一半。
            float spacerHeight = (24f + (addHalfFontGap ? hint.fontSize : 0f)) * 0.5f;
            cell.SetKeyObj("KZoneRoleGapSpacerBaseHeight", spacerHeight);
            SetRoleGapSpacer(cell, spacerHeight);
        }

        public static void HideRoleHint(Cell_ModNormalPropertyItemUI cell)
        {
            if (cell == null) return;
            var hint = cell.gameObject.transform.Find("KZoneRoleHint")?.GetComponent<Text>();
            if (hint != null) hint.gameObject.SetActive(false);
            RemoveRoleGapSpacer(cell);
        }

        private static void SetRoleGapSpacer(Cell_ModNormalPropertyItemUI cell, float height)
        {
            if (cell == null || height <= 0f) return;
            var parent = cell.transform.parent;
            if (parent == null) return;

            var spacer = cell.GetKeyObj<GameObject>("KZoneRoleGapSpacer");
            if (spacer == null)
            {
                spacer = new GameObject("KZoneRoleGapSpacer", typeof(RectTransform), typeof(LayoutElement));
                spacer.transform.SetParent(parent, false);
                cell.SetKeyObj("KZoneRoleGapSpacer", spacer);

                var cleanup = cell.gameObject.GetComponent<KZoneRoleGapCleanup>();
                if (cleanup == null) cleanup = cell.gameObject.AddComponent<KZoneRoleGapCleanup>();
                cleanup.spacer = spacer;
            }

            spacer.SetActive(true);
            spacer.transform.SetSiblingIndex(cell.transform.GetSiblingIndex() + 1);
            var spacerRt = spacer.transform as RectTransform;
            if (spacerRt != null)
                spacerRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

            var layout = spacer.GetComponent<LayoutElement>();
            layout.enabled = true;
            layout.ignoreLayout = false;
            layout.minHeight = height;
            layout.preferredHeight = height;
            layout.flexibleHeight = 0f;
            cell.SetKeyObj("KZoneRoleGapSpacerHeight", height);

            MarkParentLayoutDirty(cell);
        }

        private static void RemoveRoleGapSpacer(Cell_ModNormalPropertyItemUI cell)
        {
            if (cell == null) return;
            var spacer = cell.GetKeyObj<GameObject>("KZoneRoleGapSpacer");
            if (spacer != null) UnityEngine.Object.Destroy(spacer);
            cell.RemoveKeyObj("KZoneRoleGapSpacer");
            var cleanup = cell.gameObject.GetComponent<KZoneRoleGapCleanup>();
            if (cleanup != null) cleanup.spacer = null;
            MarkParentLayoutDirty(cell);
        }

        public static void UpdateInfoHint(Cell_ModNormalPropertyItemUI cell, System.Type cfgType, string fieldName)
        {
            if (cell == null) return;
            var fieldItem = cell.data as ModFieldItem;
            if (fieldItem?.field == null) return;
            var hint = GetOrCreateRoleHint(cell);

            string text = cell.input_value != null ? cell.input_value.text : null;
            string wrappedText = WrapHintText(ResolveInfoHint(cfgType, fieldName, text), 27);
            hint.text = wrappedText;
            ResizeRoleGapSpacerForHint(cell, hint);
        }

        private static string ResolveInfoHint(System.Type cfgType, string fieldName, string text)
        {
            if (cfgType == typeof(KZoneContentCfg))
            {
                if (fieldName == "role") return "对应人物：[" + ResolveKZoneRoleName(ParseSingleId(text, int.MinValue)) + "]";
                if (fieldName == "thumbs") return "对应点赞：[" + ResolveTimedRoles(text) + "]";
                if (fieldName == "comments") return "对应评论：[" + ResolveTimedComments(text) + "]";
                if (fieldName == "options") return "玩家评论：[" + ResolveCommentList(text) + "]";
            }

            if (cfgType == typeof(KZoneCommentCfg))
            {
                if (fieldName == "roles") return "对应人物：[" + ResolveRoleList(text) + "]";
                if (fieldName == "parent") return "根部评论：[" + ResolveParentComment(text) + "]";
                if (fieldName == "comments") return "自带回复：[" + ResolveTimedComments(text) + "]";
                if (fieldName == "options") return "玩家回复：[" + ResolveCommentList(text) + "]";
            }

            if (cfgType == typeof(KZoneProfileCfg))
            {
                if (fieldName == "id") return "资料人物：[" + ResolveKZoneRoleName(ParseSingleId(text, int.MinValue)) + "]";
                if (fieldName == "bgm") return "对应音乐：[" + ResolveAudioName(ParseSingleId(text, 0)) + "]";
            }

            return string.Empty;
        }

        public static void EnsureInfoHintRefreshOnEdit(ModNormalEditView view, Cell_ModNormalPropertyItemUI cell)
        {
            if (cell == null || cell.input_value == null) return;
            if (cell.GetKeyObj<object>("KZoneRoleHintListener") != null) return;
            cell.SetKeyObj("KZoneRoleHintListener", true);
            cell.input_value.onEndEdit.AddListener(delegate
            {
                try
                {
                    var cfgType = EditViewAccess.CfgType(view);
                    var fieldItem = cell.data as ModFieldItem;
                    if (fieldItem?.field != null && IsInfoHintField(cfgType, fieldItem.field.Name))
                        UpdateInfoHint(cell, cfgType, fieldItem.field.Name);
                }
                catch (System.Exception e) { Plugin.Log.LogError($"[KZoneInfoHintRefresh] {e}"); }
            });
        }

        private static Text GetOrCreateRoleHint(Cell_ModNormalPropertyItemUI cell)
        {
            var found = cell.gameObject.transform.Find("KZoneRoleHint");
            if (found != null)
                return found.GetComponent<Text>();

            var go = new GameObject("KZoneRoleHint", typeof(RectTransform), typeof(Text));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(cell.transform, false);

            var inputRt = cell.input_value.transform as RectTransform;
            if (inputRt != null)
            {
                rt.anchorMin = inputRt.anchorMin;
                rt.anchorMax = inputRt.anchorMax;
                rt.pivot = new Vector2(inputRt.pivot.x, 1f);
                rt.sizeDelta = new Vector2(inputRt.sizeDelta.x, 22f);
                rt.anchoredPosition = inputRt.anchoredPosition - new Vector2(0f, Mathf.Max(22f, inputRt.rect.height * 0.5f + 4f));
            }
            else
            {
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.sizeDelta = new Vector2(-20f, 22f);
                rt.anchoredPosition = new Vector2(0f, -22f);
            }

            var t = go.GetComponent<Text>();
            t.font = cell.txt_name != null ? cell.txt_name.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize = Mathf.Max(14, (cell.txt_name != null ? cell.txt_name.fontSize : 18) - 2);
            t.alignment = TextAnchor.MiddleLeft;
            t.color = new Color(0.45f, 0.45f, 0.45f, 1f);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        private static string WrapHintText(string text, int maxVisibleChars)
        {
            if (string.IsNullOrEmpty(text) || maxVisibleChars <= 0) return text;

            var result = new System.Text.StringBuilder(text.Length + text.Length / maxVisibleChars + 4);
            int lineVisibleChars = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') continue;
                if (c == '\n')
                {
                    result.Append('\n');
                    lineVisibleChars = 0;
                    continue;
                }

                string token;
                int visibleChars = 1;

                // 避免把 <sprite=...> 这类富文本标签从中间切断；按一个可见字符计数。
                if (c == '<')
                {
                    int end = text.IndexOf('>', i + 1);
                    if (end > i)
                    {
                        token = text.Substring(i, end - i + 1);
                        i = end;
                    }
                    else
                    {
                        token = c.ToString();
                    }
                }
                else
                {
                    token = c.ToString();
                }

                if (lineVisibleChars > 0 && lineVisibleChars + visibleChars > maxVisibleChars)
                {
                    result.Append('\n');
                    lineVisibleChars = 0;
                }

                result.Append(token);
                lineVisibleChars += visibleChars;
            }

            return result.ToString();
        }

        private static void ResizeRoleGapSpacerForHint(Cell_ModNormalPropertyItemUI cell, Text hint)
        {
            if (cell == null || hint == null) return;

            int lines = CountLines(hint.text);
            float lineHeight = Mathf.Max(hint.fontSize + 4f, 18f);
            float textHeight = Mathf.Max(22f, lines * lineHeight);
            float multilineTopOffset = lines > 1 ? lineHeight : 0f;

            // 多行时必须从输入框下缘开始向下排。若继续用 MiddleLeft，
            // Unity 会把整段文字按文本框高度居中，换行后看起来像“位置被修坏”。
            hint.alignment = lines > 1 ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
            PositionRoleHintBelowInput(cell, hint, textHeight, multilineTopOffset);

            object baseObj = cell.GetKeyObj<object>("KZoneRoleGapSpacerBaseHeight");
            if (baseObj == null)
                baseObj = cell.GetKeyObj<object>("KZoneRoleGapSpacerHeight");
            float baseHeight = baseObj is float f ? f : 0f;
            float targetHeight = lines > 1 ? Mathf.Max(baseHeight, textHeight + multilineTopOffset + 2f) : baseHeight;
            if (targetHeight > 0f)
                SetRoleGapSpacer(cell, targetHeight);
        }

        private static void PositionRoleHintBelowInput(Cell_ModNormalPropertyItemUI cell, Text hint, float height, float extraTopOffset)
        {
            if (cell == null || hint == null) return;

            var hintRt = hint.rectTransform;
            if (hintRt == null) return;

            var inputRt = cell.input_value != null ? cell.input_value.transform as RectTransform : null;
            if (inputRt != null)
            {
                float inputHeight = Mathf.Max(1f, inputRt.rect.height);
                hintRt.anchorMin = inputRt.anchorMin;
                hintRt.anchorMax = inputRt.anchorMax;
                hintRt.pivot = new Vector2(inputRt.pivot.x, 1f);
                hintRt.sizeDelta = new Vector2(inputRt.sizeDelta.x, height);
                hintRt.anchoredPosition = new Vector2(
                    inputRt.anchoredPosition.x,
                    inputRt.anchoredPosition.y - inputHeight * inputRt.pivot.y - 4f - extraTopOffset);
            }
            else
            {
                hintRt.anchorMin = new Vector2(0f, 0f);
                hintRt.anchorMax = new Vector2(1f, 0f);
                hintRt.pivot = new Vector2(0.5f, 1f);
                hintRt.sizeDelta = new Vector2(-20f, height);
                hintRt.anchoredPosition = new Vector2(0f, -22f - extraTopOffset);
            }
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            int lines = 1;
            foreach (char c in text)
                if (c == '\n') lines++;
            return lines;
        }

        private static int ParseSingleId(string text, int fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            text = text.Replace('，', ',').Trim();
            int comma = text.IndexOf(',');
            if (comma >= 0) text = text.Substring(0, comma);
            return int.TryParse(text.Trim(), out int id) ? id : fallback;
        }

        private static string ResolveRoleList(string text)
        {
            var ids = ParseIds(text);
            if (ids.Count == 0) return "空";
            var names = new List<string>();
            foreach (int id in ids)
                names.Add(ResolveKZoneRoleName(id));
            return string.Join("，", names.ToArray());
        }

        private static string ResolveTimedRoles(string text)
        {
            var rows = ParseIntRows(text);
            if (rows.Count == 0) return "空";
            var parts = new List<string>();
            foreach (var row in rows)
            {
                if (row.Count == 0) continue;
                int delay = row.Count > 1 ? row[1] : 0;
                parts.Add(row[0] + "=" + ResolveKZoneRoleName(row[0]) + "（" + DelayText(delay) + "）");
            }
            return string.Join("；", parts.ToArray());
        }

        private static string ResolveTimedComments(string text)
        {
            var rows = ParseIntRows(text);
            if (rows.Count == 0) return "空";
            var parts = new List<string>();
            foreach (var row in rows)
            {
                if (row.Count == 0) continue;
                int delay = row.Count > 1 ? row[1] : 0;
                parts.Add(ResolveCommentBrief(row[0]) + "（" + DelayText(delay) + "）");
            }
            return string.Join("；", parts.ToArray());
        }

        private static string ResolveCommentList(string text)
        {
            var ids = ParseIds(text);
            if (ids.Count == 0) return "空";
            var parts = new List<string>();
            foreach (int id in ids)
                parts.Add(ResolveCommentBrief(id));
            return string.Join("；", parts.ToArray());
        }

        private static string ResolveParentComment(string text)
        {
            int id = ParseSingleId(text, int.MinValue);
            if (id == int.MinValue) return "未填写";
            if (id == 0) return "直接回复动态";
            return ResolveCommentBrief(id);
        }

        private static List<List<int>> ParseIntRows(string text)
        {
            var rows = new List<List<int>>();
            if (string.IsNullOrWhiteSpace(text)) return rows;
            text = text.Replace('，', ',').Replace('；', ';');
            string[] rawRows = text.Split(new[] { ';', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawRow in rawRows)
            {
                var row = new List<int>();
                string[] parts = rawRow.Split(new[] { ',', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                    if (int.TryParse(part.Trim(), out int value)) row.Add(value);
                if (row.Count > 0) rows.Add(row);
            }
            return rows;
        }

        private static string DelayText(int delay)
        {
            if (delay == 0) return "立即";
            if (delay == -1) return "不自动";
            if (delay > 0) return delay + "秒后";
            return delay.ToString();
        }

        private static string ResolveCommentBrief(int id)
        {
            if (id == int.MinValue) return "未填写";
            if (id == 0) return "0=直接回复动态";
            if (Cfg.KZoneCommentCfgMap == null || !Cfg.KZoneCommentCfgMap.TryGetValue(id, out var cfg))
                return id + "=未知评论";

            string actor = cfg.roles != null && cfg.roles.Count > 0 ? ResolveKZoneRoleName(cfg.roles[0]) : "未知人物";
            if (cfg.roles != null && cfg.roles.Count > 1 && cfg.roles[1] >= 0)
                actor += "→" + ResolveKZoneRoleName(cfg.roles[1]);
            string content = ToListSnippet(cfg.content);
            return id + " " + actor + "：" + content;
        }

        private static string ResolveAudioName(int id)
        {
            if (id == 0) return "无";
            if (Cfg.AudioCfgMap != null && Cfg.AudioCfgMap.TryGetValue(id, out var audio))
            {
                string name = !string.IsNullOrEmpty(audio.name) ? audio.name : audio.url;
                return id + "=" + (string.IsNullOrEmpty(name) ? "未命名音乐" : name);
            }
            return id + "=未知音乐";
        }

        private static List<int> ParseIds(string text)
        {
            var ids = new List<int>();
            if (string.IsNullOrEmpty(text)) return ids;
            text = text.Replace('，', ',').Replace('；', ';');
            string[] parts = text.Split(new[] { ',', ';', '\n', '\r', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
                if (int.TryParse(part.Trim(), out int id)) ids.Add(id);
            return ids;
        }

        private static string ResolveKZoneRoleName(int id)
        {
            if (id == int.MinValue) return "未填写";
            if (id == -1) return "当前空间主人/对方";
            if (id == 0) return "主角/玩家";

            string profileName = null;
            if (Cfg.KZoneProfileCfgMap != null && Cfg.KZoneProfileCfgMap.TryGetValue(id, out var profile))
                profileName = profile.name;

            string personName = null;
            if (Cfg.PersonCfgMap != null && Cfg.PersonCfgMap.TryGetValue(id, out var person))
                personName = person.name;

            if (!string.IsNullOrEmpty(profileName) && !string.IsNullOrEmpty(personName) && profileName != personName)
                return profileName + "（" + personName + "）";
            if (!string.IsNullOrEmpty(profileName)) return profileName;
            if (!string.IsNullOrEmpty(personName)) return personName;
            return "未知ID:" + id;
        }

        public static string ToEditorText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n");
        }

        public static string FirstNotEmpty(params string[] values)
        {
            if (values == null) return null;
            foreach (var value in values)
                if (!string.IsNullOrEmpty(value)) return value;
            return null;
        }

        public static string ToListSnippet(string text)
        {
            text = ToEditorText(text);
            if (string.IsNullOrEmpty(text)) return "（空）";
            text = text.Replace("\r\n", " / ").Replace("\n", " / ").Trim();
            return text.Length > 36 ? text.Substring(0, 36) + "…" : text;
        }

    internal class KZoneRoleGapCleanup : MonoBehaviour
    {
        public GameObject spacer;

        private void OnDisable()
        {
            Cleanup();
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        private void Cleanup()
        {
            if (spacer != null)
            {
                UnityEngine.Object.Destroy(spacer);
                spacer = null;
            }
        }
    }

    }

}
