using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Config;
using HarmonyLib;
using Sdk;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 受未保存提醒保护的四个原版编辑页：事件对话、通用配置、人物、结局。
    /// 集中读取各页的内存字段，生成快照，并调用原版保存。
    /// </summary>
    internal static class NativeEditorPages
    {
        private const string TalkKind = "对话";
        private const string OptionKind = "选项";
        // 通用配置页的名称已经写在提醒开头，改动数量只说“记录”，避免“送礼触发事件修改 1 条”这种重复。
        private const string RecordKind = "记录";
        private const string PersonKind = "人物";
        private const string GrowKind = "人物成长";
        private const string EndingPartKind = "结局段落";
        private const string EndingOptionKind = "结局选项";

        private static readonly FieldInfo EvtTalksField = AccessTools.Field(typeof(ModEvtEditView), "talkCfgs");
        private static readonly FieldInfo EvtOptionsField = AccessTools.Field(typeof(ModEvtEditView), "optionCfgs");
        private static readonly FieldInfo NormalCfgsField = AccessTools.Field(typeof(ModNormalEditView), "cfgs");
        private static readonly FieldInfo NormalTypeField = AccessTools.Field(typeof(ModNormalEditView), "cfgType");
        private static readonly FieldInfo NormalFieldsField = AccessTools.Field(typeof(ModNormalEditView), "modFields");
        private static readonly FieldInfo PersonCfgsField = AccessTools.Field(typeof(ModPersonEditView), "cfgs");
        private static readonly FieldInfo PersonGrowsField = AccessTools.Field(typeof(ModPersonEditView), "growCfgs");
        private static readonly FieldInfo EndingPartsField = AccessTools.Field(typeof(ModEndingEditView), "partCfgs");
        private static readonly FieldInfo EndingOptionsField = AccessTools.Field(typeof(ModEndingEditView), "optionCfgs");

        internal static bool IsGuarded(BaseView view) =>
            view is ModEvtEditView || view is ModNormalEditView
            || view is ModPersonEditView || view is ModEndingEditView;

        internal static string DisplayName(BaseView view)
        {
            if (view is ModEvtEditView) return "事件对话";
            if (view is ModPersonEditView) return "人物";
            if (view is ModEndingEditView) return "结局";
            if (view is ModNormalEditView normal) return NormalKind(normal);
            return "编辑页";
        }

        internal static EditorSnapshot Capture(BaseView view)
        {
            var snapshot = new EditorSnapshot();
            if (view is ModEvtEditView evt)
            {
                foreach (TalkCfg talk in Read<List<TalkCfg>>(EvtTalksField, evt, "talkCfgs"))
                    snapshot.Add(TalkKind, talk == null ? "null" : Id(talk.id), talk,
                        talk == null ? null : new TalkCfg { id = talk.id });
                foreach (KeyValuePair<int, OptionCfg> pair in
                         Read<Dictionary<int, OptionCfg>>(EvtOptionsField, evt, "optionCfgs"))
                    snapshot.Add(OptionKind, Id(pair.Key), pair.Value, new OptionCfg { id = pair.Key });
            }
            else if (view is ModNormalEditView normal)
            {
                Type type = NormalType(normal);
                FieldInfo idField = type.GetField("id");
                var modFields = NormalFieldsField?.GetValue(normal) as List<ModFieldItem>;
                foreach (object record in Read<List<object>>(NormalCfgsField, normal, "cfgs"))
                    snapshot.Add(RecordKind, RecordId(idField, record), record,
                        NewNormalRecord(type, idField, modFields, record));
            }
            else if (view is ModPersonEditView person)
            {
                foreach (PersonCfg cfg in Read<List<PersonCfg>>(PersonCfgsField, person, "cfgs"))
                    snapshot.Add(PersonKind, cfg == null ? "null" : Id(cfg.id), cfg,
                        cfg == null ? null : NewPerson(cfg.id));
                foreach (KeyValuePair<int, PersonGrowCfg> pair in
                         Read<Dictionary<int, PersonGrowCfg>>(PersonGrowsField, person, "growCfgs"))
                    snapshot.Add(GrowKind, Id(pair.Key), pair.Value, new PersonGrowCfg { id = pair.Key });
            }
            else if (view is ModEndingEditView ending)
            {
                foreach (EndingPartCfg part in Read<List<EndingPartCfg>>(EndingPartsField, ending, "partCfgs"))
                    snapshot.Add(EndingPartKind, part == null ? "null" : Id(part.id), part,
                        part == null ? null : new EndingPartCfg { id = part.id, type = 1 });
                foreach (KeyValuePair<int, EndingOptionCfg> pair in
                         Read<Dictionary<int, EndingOptionCfg>>(EndingOptionsField, ending, "optionCfgs"))
                    snapshot.Add(EndingOptionKind, Id(pair.Key), pair.Value, new EndingOptionCfg { id = pair.Key });
            }
            else
            {
                throw new NotSupportedException(view.GetType().Name + " 不在未保存提醒范围内。");
            }
            return snapshot;
        }

        /// <summary>原版保存会静默丢弃的有内容记录数（事件/配置/结局编号为 0，人物编号 ≤ 0）。</summary>
        internal static int CountDroppedOnSave(BaseView view)
        {
            int count = 0;
            if (view is ModEvtEditView evt)
            {
                foreach (TalkCfg talk in Read<List<TalkCfg>>(EvtTalksField, evt, "talkCfgs"))
                    if (talk != null && talk.id == 0 && !SameAs(talk, new TalkCfg())) count++;
            }
            else if (view is ModNormalEditView normal)
            {
                Type type = NormalType(normal);
                FieldInfo idField = type.GetField("id");
                if (idField == null || idField.FieldType != typeof(int)) return 0;
                var modFields = NormalFieldsField?.GetValue(normal) as List<ModFieldItem>;
                foreach (object record in Read<List<object>>(NormalCfgsField, normal, "cfgs"))
                {
                    if (record == null || (int)idField.GetValue(record) != 0) continue;
                    object blank = NewNormalRecord(type, idField, modFields, record);
                    if (blank == null || !SameAs(record, blank)) count++;
                }
            }
            else if (view is ModPersonEditView person)
            {
                foreach (PersonCfg cfg in Read<List<PersonCfg>>(PersonCfgsField, person, "cfgs"))
                    if (cfg != null && cfg.id <= 0 && !SameAs(cfg, NewPerson(cfg.id))) count++;
            }
            else if (view is ModEndingEditView ending)
            {
                foreach (EndingPartCfg part in Read<List<EndingPartCfg>>(EndingPartsField, ending, "partCfgs"))
                    if (part != null && part.id == 0 && !SameAs(part, new EndingPartCfg { type = 1 })) count++;
            }
            return count;
        }

        internal static string TalkKey(TalkCfg talk) => EditorSnapshot.KeyOf(TalkKind, Id(talk.id));

        internal static EditorRecordState TalkState(TalkCfg talk) =>
            new EditorRecordState(TalkKind, EditorChangeTracking.Fingerprint(talk),
                SameAs(talk, new TalkCfg { id = talk.id }));

        /// <summary>快照按编号定位对话；同一编号出现多次时无法确定对应哪一条。</summary>
        internal static bool HasUniqueTalkId(ModEvtEditView view, TalkCfg talk)
        {
            int count = 0;
            foreach (TalkCfg item in Read<List<TalkCfg>>(EvtTalksField, view, "talkCfgs"))
                if (item != null && item.id == talk.id && ++count > 1) return false;
            return count == 1;
        }

        /// <summary>走原版「保存」按钮的同一入口，其它插件挂在保存上的检查照常生效。</summary>
        internal static void InvokeSave(BaseView view)
        {
            if (view is ModEvtEditView evt)
            {
                var history = EvtEditorHistory.Get(evt);
                history?.Safe(history.PrepareSaveRequest);
            }
            MethodInfo save = AccessTools.Method(view.GetType(), "OnClickSave", Type.EmptyTypes);
            if (save == null) throw new MissingMethodException(view.GetType().Name, "OnClickSave");
            save.Invoke(view, null);
        }

        private static string NormalKind(ModNormalEditView view)
        {
            var type = NormalTypeField?.GetValue(view) as Type;
            if (type == null) return "配置";
            // 与创作页配置列表显示的名字一致：本插件补进列表的类型用注册表里的中文名。
            if (NativeCfgRegistry.TryGet(type, out TypeDef def) && !string.IsNullOrEmpty(def.DisplayName))
                return def.DisplayName;
            var attr = type.GetCustomAttribute<CfgClassAttribute>(false);
            return attr != null && !string.IsNullOrEmpty(attr.Name) ? attr.Name : type.Name;
        }

        private static Type NormalType(ModNormalEditView view)
        {
            var type = NormalTypeField?.GetValue(view) as Type;
            if (type == null) throw new InvalidOperationException("读不到配置类型（cfgType）。");
            return type;
        }

        // 与原版 OnClickNewItem 一致：按字段默认值新建，再沿用记录自己的编号。
        private static object NewNormalRecord(Type type, FieldInfo idField,
            List<ModFieldItem> modFields, object record)
        {
            if (record == null) return null;
            try
            {
                object fresh = Activator.CreateInstance(type);
                if (modFields != null)
                {
                    foreach (ModFieldItem item in modFields)
                        if (item?.attr?.DefaultValue != null && item.field != null)
                            item.field.SetValue(fresh, item.attr.DefaultValue);
                }
                if (idField != null) idField.SetValue(fresh, idField.GetValue(record));
                return fresh;
            }
            catch (Exception)
            {
                // 建不出对照模板只影响“没动过的新记录”识别，此时按普通新增处理。
                return null;
            }
        }

        // 与原版 ModPersonEditView.OnClickNewItem 的默认值一致。
        private static PersonCfg NewPerson(int id) => new PersonCfg
        {
            id = id,
            urlParm = new List<float> { 0f, 0f, 1f },
            urlParm2 = new List<float> { 0f, 0f, 1f },
            init = new List<int> { 0 },
            bubbleParm = new List<float> { 1100f },
            bubbleParm2 = new List<float> { 1100f },
        };

        private static bool SameAs(object record, object blank) =>
            string.Equals(EditorChangeTracking.Fingerprint(record),
                EditorChangeTracking.Fingerprint(blank), StringComparison.Ordinal);

        private static string RecordId(FieldInfo idField, object record)
        {
            if (record == null) return "null";
            if (idField == null) return "?";
            return Convert.ToString(idField.GetValue(record), CultureInfo.InvariantCulture);
        }

        private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

        private static T Read<T>(FieldInfo field, object owner, string name) where T : class, new()
        {
            if (field == null) throw new MissingFieldException(owner.GetType().Name, name);
            return field.GetValue(owner) as T ?? new T();
        }
    }
}
