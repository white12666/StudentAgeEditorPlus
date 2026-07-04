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
                        desc: "送礼事件的唯一编号，不可与已有事件重复。",
                        required: true),
                    F("item", "物品ID",
                        desc: "触发此送礼对话的物品，从下拉列表选择。\n列表同时包含物品和书籍，书籍以「（书）」后缀标注。",
                        required: true, range: typeof(ItemCfg)),
                    F("npc", "NPC ID",
                        desc: "可触发此送礼事件的NPC列表，可填多个，用英文逗号分隔。\n下方对话ID和类型标记均按此列表顺序一一对应。"),
                    F("cond", "前提条件",
                        type: CfgPropertyType.Condition,
                        desc: "触发此送礼事件需满足的条件。留空则无条件触发。"),
                    F("talkId", "对话ID",
                        desc: "送礼时播放的对话编号，按上方NPC顺序一一对应。\n不同NPC之间用英文分号 ; 分隔；同一NPC的多段对话用英文逗号 , 分隔。\n例如：\n· 一个NPC一段对话：322116001\n· 两个NPC各一段对话：322116001;10103001\n· 一个NPC两段对话：340101001,340101001"),
                    F("type", "类型标记",
                        desc: "送礼后物品是否从背包中消失。\n· 0：消失（赠予NPC）\n· 1：不消失\n留空等同于 0。多个NPC时用英文逗号按顺序对应。"),
                    F("redpoint", "红点提示",
                        desc: "送礼按钮上是否显示红点，提示玩家该物品有特殊送礼对话。\n· 1（默认）：显示\n· 0：不显示",
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
                OnRenderPropertyHook = (viewObj, cell, fieldItem) =>
                {
                    // 原 Patch D 逻辑：item 字段下拉框追加书籍
                    if (fieldItem.field.Name != "item") return;
                    if (!cell.dropdown_value.gameObject.activeSelf) return;
                    if (Cfg.BookCfgMap == null || Cfg.BookCfgMap.Count == 0) return;

                    var existing = cell.GetKeyObj<List<Dropdown.OptionData>>("options");
                    if (existing == null) return;

                    var existingIds = new HashSet<int>();
                    foreach (var opt in existing)
                        if (opt is ModPropertyOptionData mod) existingIds.Add(mod.id);

                    var nameFields = Traverse.Create(viewObj).Field("nameFields").GetValue<List<string>>();

                    FieldInfo bookNameField = null;
                    if (nameFields != null)
                    {
                        foreach (var nf in nameFields)
                        {
                            bookNameField = typeof(BookCfg).GetField(nf);
                            if (bookNameField != null) break;
                        }
                    }

                    int added = 0;
                    foreach (var entry in Cfg.BookCfgMap)
                    {
                        int id = entry.Key;
                        if (existingIds.Contains(id)) continue;
                        string name = bookNameField?.GetValue(entry.Value) as string;
                        existing.Add(new ModPropertyOptionData
                        {
                            id = id,
                            text = $"[{id}]{name ?? id.ToString()}（书）"
                        });
                        added++;
                    }

                    if (added > 0)
                    {
                        cell.dropdown_value.ClearOptions();
                        cell.dropdown_value.AddOptions(existing);
                        var curSelect = Traverse.Create(viewObj).Field("curSelect").GetValue<object>();
                        if (curSelect != null)
                        {
                            int itemId = (int)fieldItem.field.GetValue(curSelect);
                            int idx = existing.FindIndex(o => (o as ModPropertyOptionData)?.id == itemId);
                            if (idx >= 0) cell.dropdown_value.SetValueWithoutNotify(idx);
                        }
                        Plugin.Log.LogInfo($"[NativeCfg] 已追加 {added} 本书籍到物品下拉框。");
                    }
                },
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
                (typeof(ClassmateCfg),       "同学(小学)",    25060180uL),
                (typeof(Classmate2Cfg),      "同学(初中)",    25060181uL),
                (typeof(Classmate3LiKeCfg),  "同学(高中理科)", 25060182uL),
                (typeof(Classmate3WenKeCfg), "同学(高中文科)", 25060183uL),
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
                        F("id", "ID",
                            desc: "该同学条目的唯一编号，不可重复。\n原版从 1 开始递增，新增同学建议从较大的编号开始避免冲突。",
                            required: true),
                        F("name", "名称",
                            desc: "同学名称，游戏内随机分配同学时会显示此名称。",
                            required: true),
                        F("roleId", "人物ID",
                            desc: "该同学对应的人物ID，从下拉列表选择。\n决定同学的立绘和基础属性。留空则使用默认形象。",
                            range: typeof(PersonCfg)),
                        F("gender", "性别",
                            desc: "性别。1=男，2=女。\n需与对应人物的性别一致，否则可能出现立绘不匹配的情况。"),
                        F("weight", "权重",
                            desc: "随机分配时该同学的出现概率权重，数值越大越容易被选中。\n原版权重按指数递减（如 85亿→7.5亿→1.9亿），建议参考原版规律设置。"),
                        F("cond", "条件",
                            type: CfgPropertyType.Condition,
                            desc: "该同学出现需满足的条件。"),
                    },
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
