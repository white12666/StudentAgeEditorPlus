using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Config;
using GenUI.Mod;
using HarmonyLib;
using Sdk;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    // ═════════════════════════════════════════════════════════════════
    //  通用原生 Cfg 编辑器框架
    //  让没有 [CfgClass] / [CfgProperty] 的原生配置类型出现在编辑器中。
    //  新增类型只需在 NativeCfgRegistry 构造里注册，无需改补丁代码。
    // ═════════════════════════════════════════════════════════════════

    // ───────────────────────────────────────────────────────────────────
    //  数据类
    // ───────────────────────────────────────────────────────────────────

    internal class FieldDef
    {
        public string Name;
        public CfgPropertyType PropType = CfgPropertyType.Default;
        public string DisplayName;
        public string Description;
        public bool Required;
        public Type RangeType;
        public int RangeStart = -1;
        public int RangeEnd = -1;
        public object DefaultValue;
    }

    internal class TypeDef
    {
        public Type CfgType;
        public string DisplayName;
        public ulong Order;
        public List<FieldDef> Fields;
        public Func<object, string> FormatItemName;
        public Action<object, Cell_ModNormalPropertyItemUI, ModFieldItem> OnRenderPropertyHook;
    }

    // ───────────────────────────────────────────────────────────────────
    //  注册表
    // ───────────────────────────────────────────────────────────────────

    internal static class NativeCfgRegistry
    {
        private static readonly Dictionary<Type, TypeDef> _map = new Dictionary<Type, TypeDef>();

        static NativeCfgRegistry()
        {
            RegisterGiftEvtCfg();
            RegisterTVCfg();
            RegisterTripSpotCfg();
            RegisterValueviewCfg();
            RegisterClassmateCfgs();
            RegisterModFaceCfg();
        }

        public static bool TryGet(Type type, out TypeDef def) => _map.TryGetValue(type, out def);
        public static IEnumerable<TypeDef> All => _map.Values;

        public static void Register(TypeDef def)
        {
            _map[def.CfgType] = def;
        }

        // ── 辅助：快速构造 FieldDef ──────────────────────────────

        private static FieldDef F(string name, string display,
            CfgPropertyType type = CfgPropertyType.Default,
            string desc = null, bool required = false,
            Type range = null, object def = null)
        {
            return new FieldDef
            {
                Name = name,
                DisplayName = display,
                PropType = type,
                Description = desc,
                Required = required,
                RangeType = range,
                DefaultValue = def,
            };
        }

        // ── 注册各类型 ────────────────────────────────────────────

        private static void RegisterGiftEvtCfg()
        {
            Register(new TypeDef
            {
                CfgType = typeof(GiftEvtCfg),
                DisplayName = "送礼触发事件",
                Order = 25042702uL,
                Fields = new List<FieldDef>
                {
                    F("id", "ID",
                        desc: "送礼事件的唯一编号，请填写未占用的正整数。\n不可与本作品、游戏原版或其它同时启用 Mod 的送礼事件 ID 重复；可以与剧情事件 ID、对白 ID 使用相同数字。",
                        required: true),
                    F("item", "物品ID",
                        desc: "触发此送礼对话的物品，从下拉列表选择。\n列表同时包含物品和书籍，书籍以「（书）」后缀标注。",
                        required: true, range: typeof(ItemCfg)),
                    F("npc", "NPC ID",
                        desc: "推荐点击底部「绑定送礼剧情」按姓名添加NPC。\n手填多个NPC用逗号分隔；删除或调整顺序时，已有剧情和物品设置会跟随对应NPC，新添加的NPC需要重新选剧情。\n清空此字段会同时清除所有绑定；保存后该记录不触发送礼对白。"),
                    F("cond", "前提条件",
                        type: CfgPropertyType.Condition,
                        desc: "触发此送礼事件需满足的条件。留空则无条件触发。"),
                    F("talkId", "对话ID（高级）",
                        desc: "推荐点击底部「绑定送礼剧情」，按标题选剧情自动回填，无需查号。\n\n手动填写说明：\n· 需填剧情【首句台词ID】（并非事件ID），后续会自动顺着连线播放。\n· 男女共用填 1 个；区分男女主填「男主首句,女主首句」（逗号区分性别，并非播下一句）。\n· 多个 NPC 用英文分号 ; 分隔。"),
                    F("type", "类型标记",
                        desc: "送礼后物品是否从背包中消失。\n· 0：消失（赠予NPC，默认）\n· 1：保留（物品不消耗）\n单个NPC时直接通过下拉选择；配置多位NPC时显示手填列表（英文逗号对应顺序），或推荐点击底部「绑定送礼剧情」可视化设置。"),
                    F("redpoint", "红点提示",
                        desc: "送礼按钮上是否显示红点，提示玩家该物品有特殊送礼对话。\n· 1（默认）：显示红点\n· 0：不显示红点",
                        def: 1),
                },
                FormatItemName = data =>
                {
                    var cfg = (GiftEvtCfg)data;
                    string itemName = null;
                    if (Cfg.ItemCfgMap != null && Cfg.ItemCfgMap.TryGetValue(cfg.item, out var itemCfg))
                        itemName = itemCfg.name;
                    else if (Cfg.BookCfgMap != null && Cfg.BookCfgMap.TryGetValue(cfg.item, out var bookCfg))
                        itemName = bookCfg.name;
                    string npcStr = (cfg.npc != null && cfg.npc.Count > 0)
                        ? string.Join(",", cfg.npc)
                        : "?";
                    string display = string.IsNullOrEmpty(itemName)
                        ? $"物品{cfg.item}"
                        : itemName;
                    return $"[{cfg.id}]{display}→NPC{npcStr}";
                },
                OnRenderPropertyHook = GiftEvtEditorUtil.OnRenderProperty,
            });
        }

        private static void RegisterTVCfg()
        {
            Register(new TypeDef
            {
                CfgType = typeof(TVCfg),
                DisplayName = "看电视",
                Order = 25060150uL,
                Fields = new List<FieldDef>
                {
                    F("id", "ID",
                        desc: "电视节目的唯一编号，不可与已有节目重复。",
                        required: true),
                    F("name", "名称",
                        desc: "节目名称，如《猫和小鼠》。",
                        required: true),
                    F("sub", "副标题",
                        desc: "节目的副标题，如\"之甜蜜的家\"。"),
                    F("groupName", "频道",
                        desc: "频道分组名称，决定该节目在电视界面出现在哪个频道标签下。\n原版频道如：少儿频道、科教频道、综合频道、体育频道等。"),
                    F("cond", "触发条件",
                        type: CfgPropertyType.Condition,
                        desc: "观看此节目需满足的条件，如年龄、心情等。留空则无条件限制。"),
                    F("effect", "观看效果",
                        type: CfgPropertyType.Effect,
                        desc: "观看后对属性产生的效果。"),
                    F("talks", "对话",
                        desc: "观看节目时弹出的对话文本列表，每行一条。\n原版节目通常有 2-4 条对话，用逗号分隔。"),
                },
            });
        }

        private static void RegisterTripSpotCfg()
        {
            Register(new TypeDef
            {
                CfgType = typeof(TripSpotCfg),
                DisplayName = "旅游景点",
                Order = 25060160uL,
                Fields = new List<FieldDef>
                {
                    F("id", "ID",
                        desc: "景点的唯一编号。\n原版 ID 按星级分段：1xx=一星，2xx=二星，3xx=三星，4xx=四星，5xx=五星。\n建议新增景点时沿用此规律，避免与已有景点冲突。",
                        required: true),
                    F("name", "名称",
                        desc: "景点名称，如\"南昆山\"。",
                        required: true),
                    F("type", "类型",
                        desc: "景点分类，从下拉列表选择。\n原版有三种：1=山岳、2=水景、3=历史人文。",
                        range: typeof(TripTypeCfg)),
                    F("star", "星级",
                        desc: "景点星级（1-5），决定旅游收益和等级。\n星级越高消耗越大、收益越好。3 星及以上景点可绑定触发事件。"),
                    F("effect", "效果",
                        type: CfgPropertyType.Effect,
                        desc: "旅游该景点产生的属性效果。"),
                    F("evtId", "触发事件",
                        desc: "旅游该景点时触发的事件ID，从下拉列表选择。\n3 星及以上景点才支持事件，1-2 星留空即可。",
                        range: typeof(EvtCfg)),
                },
            });

        }

        private static void RegisterValueviewCfg()
        {
            Register(new TypeDef
            {
                CfgType = typeof(ValueviewCfg),
                DisplayName = "价值观",
                Order = 25060170uL,
                Fields = new List<FieldDef>
                {
                    F("id", "ID",
                        desc: "价值观的唯一编号。\n原版 ID 为 5 位数，前 3 位是组别编码，后 2 位是等级序号。\n建议新增时参考原版规律，如 10101 = 第 101 组第 1 级。",
                        required: true),
                    F("name", "名称",
                        desc: "价值观名称，如\"心浮气躁\"。",
                        required: true),
                    F("lv", "等级",
                        desc: "价值观的等级层级，从 1 开始递增。\n同一组别内等级越高，效果越强。"),
                    F("group", "组别",
                        desc: "所属的属性分类，从下拉列表选择。\n同一组别的价值观互相排斥，玩家只能拥有其中一个。\n下拉项对应属性表，如心情(0)、智力(1)等。",
                        range: typeof(PersonAttrCfg)),
                    F("attrs", "属性",
                        desc: "该价值观影响的属性ID列表，用英文逗号分隔。\n填 0 表示不影响具体属性，仅作为价值观存在。"),
                    F("effect", "效果",
                        type: CfgPropertyType.Effect,
                        desc: "该价值观产生的属性效果。"),
                    F("desc", "描述",
                        desc: "价值观的详细描述文本，在游戏内价值观面板中显示。"),
                    F("icon", "图标",
                        type: CfgPropertyType.Image,
                        desc: "价值观图标路径。点击按钮可选择图片。"),
                    F("diss", "消解目标",
                        desc: "此价值观可被消解为的目标价值观，从下拉列表选择。\n留空(0)表示该价值观不可消解。",
                        range: typeof(ValueviewCfg)),
                    F("dissTxt", "消解文本",
                        desc: "消解此价值观时，游戏内显示的提示文本。"),
                    F("forgetTxt", "遗忘文本",
                        desc: "遗忘此价值观时，游戏内显示的提示文本。"),
                    F("upgrade", "升级目标",
                        desc: "此价值观升级后的目标价值观，从下拉列表选择。\n留空(0)表示该价值观不可升级。",
                        range: typeof(ValueviewCfg)),
                    F("upgradeTxt", "升级文本",
                        desc: "升级此价值观时，游戏内显示的提示文本。"),
                },
            });

        }

        private static void RegisterClassmateCfgs()
        {
            var classmateTypes = new[]
            {
                (typeof(ClassmateCfg),       "考试同学（小学）",    25060180uL),
                (typeof(Classmate2Cfg),      "考试同学（初中）",    25060181uL),
                (typeof(Classmate3LiKeCfg),  "考试同学（高中理科）", 25060182uL),
                (typeof(Classmate3WenKeCfg), "考试同学（高中文科）", 25060183uL),
            };

            foreach (var (cfgType, displayName, order) in classmateTypes)
            {
                Register(new TypeDef
                {
                    CfgType = cfgType,
                    DisplayName = displayName,
                    Order = order,
                    Fields = new List<FieldDef>
                    {
                        F("id", "选择要调整的同学",
                            desc: "从当前年级的考试同学表中选择。选择后会自动带出该同学的名称、人物、性别、权重和条件。",
                            required: true,
                            range: cfgType),
                        F("name", "榜单姓名",
                            desc: "考试榜上显示的姓名。选择同学后会自动填写，通常无需修改。",
                            required: true),
                        F("roleId", "关联剧情人物（可选）",
                            desc: "有独立 PersonCfg 的同学可关联人物；普通同学保持 0 即可。下拉也包含当前 Mod 尚未发布的人物。",
                            range: typeof(PersonCfg)),
                        F("gender", "性别",
                            desc: "1=男，2=女。选择同学后会自动填写。"),
                        F("weight", "排名倾向权重",
                            desc: "不是考试分数。数值越大，通常越容易排在当前排名段的前面；最终分数仍由实际名次生成。\n" +
                                  "建议在原值附近小幅调整，不要直接填写极大数字；填 0 表示不参加考试榜。"),
                        F("cond", "生效条件",
                            type: CfgPropertyType.Condition,
                            desc: "留空表示一直生效。也可按剧情启用，例如事件未发生填 3,-1,事件ID，" +
                                  "事件已发生填 3,1,事件ID。条件变化从下一场考试开始体现。"),
                    },
                    OnRenderPropertyHook = ClassmateCfgEditorUtil.OnRenderProperty,
                });
            }
        }

        private static void RegisterModFaceCfg()
        {
            Register(new TypeDef
            {
                CfgType = typeof(ModFaceCfg),
                DisplayName = "自定义立绘",
                Order = 25060190uL,
                Fields = new List<FieldDef>
                {
                    F("id", "ID",
                        desc: "立绘的唯一编号。\nID 编码规则：人物ID × 1000 + 服装ID × 100 + 表情ID。\n例如 114433000 = 人物11443 + 服装3 + 表情0。\n同一人物可有多套服装和表情，编号需遵守此规律才能在游戏内正确显示。",
                        required: true),
                    F("name", "名称",
                        desc: "立绘名称，仅用于编辑器内标识，游戏内不显示。",
                        required: true),
                    F("icon", "立绘图片",
                        type: CfgPropertyType.Image,
                        desc: "立绘主图，点击右侧按钮可选择图片。\n这是人物在对话、行动等场景中显示的大图。"),
                    F("icon_xx", "小头像",
                        type: CfgPropertyType.Image,
                        desc: "小头像图片路径，用于对话框、聊天列表等小尺寸显示场景。"),
                    F("photobooth", "大头贴",
                        desc: "大头贴照片路径，用于拍照小游戏中显示。"),
                },
            });
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  统一补丁
    // ═════════════════════════════════════════════════════════════════

    // ── Patch A：类型列表注入 ─────────────────────────────────────

    [HarmonyPatch(typeof(ModPageUploadView), "OnOpen")]
    internal static class NativeCfgTypeListPatch
    {
        private static void Postfix(ModPageUploadView __instance)
        {
            try
            {
                var allTypes = Traverse.Create(__instance).Field("allTypes").GetValue<List<Type>>();
                if (allTypes == null) return;

                foreach (var def in NativeCfgRegistry.All)
                {
                    if (allTypes.Contains(def.CfgType)) continue; // 幂等

                    // 按 Order 找到插入位置
                    int insertAt = allTypes.Count;
                    for (int i = 0; i < allTypes.Count; i++)
                    {
                        var attr = allTypes[i].GetCustomAttribute<CfgClassAttribute>(false);
                        if (attr != null && attr.Order > def.Order)
                        {
                            insertAt = i;
                            break;
                        }
                    }
                    allTypes.Insert(insertAt, def.CfgType);
                    Plugin.Log.LogInfo(
                        $"[NativeCfgTypeList] 已将 {def.CfgType.Name} 插入配置类型列表(位置 {insertAt})。");
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[NativeCfgTypeList] {e}"); }
        }
    }

    // ── Patch A2：列表渲染防崩溃 ──────────────────────────────────

    [HarmonyPatch(typeof(ModPageUploadView), "OnRenderItem")]
    internal static class NativeCfgRenderPatch
    {
        private static bool Prefix(ModPageUploadView __instance, UICell _cell)
        {
            try
            {
                var obj = _cell as Cell_ModCfgItemUI;
                if (obj == null) return true;

                var type = obj.data as Type;
                if (type == null || !NativeCfgRegistry.TryGet(type, out var def))
                    return true; // 不在注册表里，走原方法

                // 手动渲染（原方法取 CfgClassAttribute.Name 会 NullRef）
                obj.txt_name.text = def.DisplayName;
                var modTypes = Traverse.Create(__instance).Field("modTypes").GetValue<List<Type>>();
                bool flag = modTypes != null && modTypes.Contains(type);
                obj.btn_edit.gameObject.SetActive(flag);
                obj.btn_new.gameObject.SetActive(!flag);
                return false; // 跳过原方法
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[NativeCfgRender] {e}");
                return true; // 出错时走原方法
            }
        }
    }

    // ── Patch B：字段注入 ─────────────────────────────────────────

    [HarmonyPatch(typeof(ModNormalEditView), "InitFields")]
    internal static class NativeCfgFieldsPatch
    {
        private static void Postfix(ModNormalEditView __instance)
        {
            try
            {
                var cfgType = EditViewAccess.CfgType(__instance);
                if (cfgType == null || !NativeCfgRegistry.TryGet(cfgType, out var def))
                    return; // 不在注册表里，交给其他 Postfix

                var fields = EditViewAccess.ModFields(__instance);
                if (fields == null)
                {
                    fields = new List<ModFieldItem>();
                    Traverse.Create(__instance).Field("modFields").SetValue(fields);
                }
                if (fields.Count > 0) return; // 幂等

                foreach (var fd in def.Fields)
                {
                    var fieldInfo = cfgType.GetField(fd.Name);
                    if (fieldInfo == null)
                    {
                        Plugin.Log.LogError($"[NativeCfgFields] {cfgType.Name} 字段不存在: {fd.Name}");
                        continue;
                    }

                    var attr = new CfgPropertyAttribute(fd.PropType, fd.DisplayName, fd.Description);
                    if (fd.Required) attr.Required = true;
                    if (fd.DefaultValue != null) attr.DefaultValue = fd.DefaultValue;

                    CfgPropertyRangeAttribute range = null;
                    if (fd.RangeType != null)
                    {
                        if (fd.RangeStart >= 0 && fd.RangeEnd >= 0)
                            range = new CfgPropertyRangeAttribute(fd.RangeType, fd.RangeStart, fd.RangeEnd);
                        else
                            range = new CfgPropertyRangeAttribute(fd.RangeType);
                    }

                    fields.Add(new ModFieldItem
                    {
                        field = fieldInfo,
                        attr = attr,
                        range = range,
                        depend = null,
                    });
                }

                EditViewAccess.PropertyGroup(__instance)?.SetDatas(fields, null);
                Plugin.Log.LogInfo($"[NativeCfgFields] 已为 {cfgType.Name} 注入 {fields.Count} 个字段。");
            }
            catch (Exception e) { Plugin.Log.LogError($"[NativeCfgFields] {e}"); }
        }
    }

    // ── Patch C：左栏条目名称 ─────────────────────────────────────

    [HarmonyPatch(typeof(ModNormalEditView), "OnRenderItem")]
    internal static class NativeCfgItemNamePatch
    {
        private static void Postfix(ModNormalEditView __instance, UICell _cell)
        {
            try
            {
                var cfgType = EditViewAccess.CfgType(__instance);
                if (cfgType == null || !NativeCfgRegistry.TryGet(cfgType, out var def))
                    return;

                var cell = _cell as Cell_ModNormalEditItemUI;
                if (cell == null) return;

                var data = cell.data;
                if (data == null) return;

                string name;
                if (def.FormatItemName != null)
                {
                    name = def.FormatItemName(data);
                }
                else
                {
                    // 默认：[id]name
                    int id = (int)cfgType.GetField("id").GetValue(data);
                    string text = null;
                    var nameField = cfgType.GetField("name");
                    if (nameField != null && nameField.GetValue(data) is string s && !string.IsNullOrEmpty(s))
                        text = s;
                    name = id == 0 ? (text ?? "") : $"[{id}]{text ?? ""}";
                }
                cell.txtex_name.text = name;
            }
            catch (Exception e) { Plugin.Log.LogError($"[NativeCfgItemName] {e}"); }
        }
    }

    // ── Patch D：OnRenderProperty 钩子 ────────────────────────────

    [HarmonyPatch(typeof(ModNormalEditView), "OnRenderProperty")]
    internal static class NativeCfgOnRenderPropertyPatch
    {
        private static void Postfix(ModNormalEditView __instance, UICell _cell)
        {
            try
            {
                var cfgType = EditViewAccess.CfgType(__instance);
                if (cfgType == null || !NativeCfgRegistry.TryGet(cfgType, out var def))
                    return;
                if (def.OnRenderPropertyHook == null) return;

                var cell = _cell as Cell_ModNormalPropertyItemUI;
                if (cell == null) return;

                var fieldItem = cell.data as ModFieldItem;
                if (fieldItem == null) return;

                def.OnRenderPropertyHook(__instance, cell, fieldItem);
            }
            catch (Exception e) { Plugin.Log.LogError($"[NativeCfgRenderProp] {e}"); }
        }
    }
}
