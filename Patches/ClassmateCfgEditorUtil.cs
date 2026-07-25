using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Config;
using GenUI.Mod;
using HarmonyLib;
using Sdk;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// ClassmateCfg 的同学选择与人物下拉增强。
    ///
    /// “选择要调整的同学”直接列出当前年级考试表，选中后复制原条目的姓名、人物、
    /// 性别、权重和条件；作者不必去 NPC 人物页查找并不存在的普通同学。关联人物
    /// 下拉另行合并当前 Mod 尚未启用的 PersonCfg。所有结果仍写回原生 ClassmateCfg，
    /// 玩家端由原版 ModCtrl 正常加载。
    /// </summary>
    internal static class ClassmateCfgEditorUtil
    {
        private sealed class LocalPersonCache
        {
            internal string Path;
            internal DateTime WriteTimeUtc;
            internal Dictionary<int, PersonCfg> Persons = new Dictionary<int, PersonCfg>();
        }

        private sealed class SelectorState { }

        private static readonly ConditionalWeakTable<ModNormalEditView, LocalPersonCache> Caches =
            new ConditionalWeakTable<ModNormalEditView, LocalPersonCache>();

        private static readonly ConditionalWeakTable<Dropdown, SelectorState> IdSelectorStates =
            new ConditionalWeakTable<Dropdown, SelectorState>();

        internal static void OnRenderProperty(
            object viewObject,
            Cell_ModNormalPropertyItemUI cell,
            ModFieldItem fieldItem)
        {
            if (!(viewObject is ModNormalEditView view) || cell == null || fieldItem?.field == null)
                return;

            if (fieldItem.field.Name == "id")
            {
                RenderClassmateSelector(view, cell, fieldItem);
                return;
            }

            if (fieldItem.field.Name != "roleId" || !cell.dropdown_value.gameObject.activeSelf)
                return;

            RenderRoleSelector(view, cell, fieldItem);
        }

        private static void RenderRoleSelector(
            ModNormalEditView view,
            Cell_ModNormalPropertyItemUI cell,
            ModFieldItem fieldItem)
        {
            object current = EditViewAccess.CurSelect(view);
            int currentRoleId = 0;
            if (current != null && fieldItem.field.GetValue(current) is int value)
                currentRoleId = value;

            // 不原地修改 ModNormalEditView 缓存的 options。属性 Cell 可能复用，且当前
            // Mod 的 PersonCfg.json 会在编辑过程中增删；每次都从权威数据重建，才能
            // 避免“当前 Mod”旧项、悬空引用和被改名的全局项永久累积在缓存中。
            var personOptions = new SortedDictionary<int, string>();
            if (Cfg.PersonCfgMap != null)
            {
                foreach (KeyValuePair<int, PersonCfg> pair in Cfg.PersonCfgMap)
                {
                    if (pair.Key <= 0) continue;
                    personOptions[pair.Key] = FormatPersonOption(pair.Key, pair.Value, local: false);
                }
            }

            Dictionary<int, PersonCfg> localPersons = LoadLocalPersons(view);
            foreach (KeyValuePair<int, PersonCfg> pair in localPersons)
            {
                // roleId=0 在 ClassmateCfg 中固定表示“不绑定人物”，不能被本地
                // PersonCfg[0] 的主角名称覆盖。
                if (pair.Key <= 0) continue;
                personOptions[pair.Key] = FormatPersonOption(pair.Key, pair.Value, local: true);
            }

            var options = new List<Dropdown.OptionData>
            {
                new ModPropertyOptionData
                {
                    id = -1,
                    text = "-- " + DescCtrl.GetTxt(1139) + " --"
                },
                new ModPropertyOptionData
                {
                    id = 0,
                    text = "[0]不绑定人物（普通同学）"
                }
            };
            foreach (KeyValuePair<int, string> pair in personOptions)
                options.Add(new ModPropertyOptionData { id = pair.Key, text = pair.Value });

            // 对旧 JSON 或缺失 PersonCfg 的引用也保留一个可见选项，避免打开后
            // 下拉显示成 -1，作者无意操作时误以为原值已经丢失。
            if (!options.OfType<ModPropertyOptionData>().Any(option => option.id == currentRoleId))
            {
                options.Add(new ModPropertyOptionData
                {
                    id = currentRoleId,
                    text = $"[{currentRoleId}]当前引用（人物表中未找到）"
                });
            }

            cell.SetKeyObj("options", options);
            cell.dropdown_value.ClearOptions();
            cell.dropdown_value.AddOptions(options);

            int selected = options.FindIndex(option =>
                option is ModPropertyOptionData data && data.id == currentRoleId);
            if (selected >= 0)
                cell.dropdown_value.SetValueWithoutNotify(selected);
        }

        private static void RenderClassmateSelector(
            ModNormalEditView view,
            Cell_ModNormalPropertyItemUI cell,
            ModFieldItem fieldItem)
        {
            if (!cell.dropdown_value.gameObject.activeSelf) return;

            Type cfgType = EditViewAccess.CfgType(view);
            IDictionary map = GetCfgMap(cfgType);
            object current = EditViewAccess.CurSelect(view);
            int currentId = GetId(current, cfgType);

            var usedIds = new HashSet<int>();
            List<object> localRows = Traverse.Create(view).Field("cfgs").GetValue<List<object>>();
            if (localRows != null)
            {
                foreach (object row in localRows)
                {
                    if (row == null || ReferenceEquals(row, current)) continue;
                    int id = GetId(row, cfgType);
                    if (id > 0) usedIds.Add(id);
                }
            }

            var rows = new List<(int Id, object Value)>();
            if (map != null)
            {
                foreach (DictionaryEntry entry in map)
                {
                    if (!(entry.Key is int id) || id <= 0 || usedIds.Contains(id)) continue;
                    rows.Add((id, entry.Value));
                }
                rows.Sort((a, b) => a.Id.CompareTo(b.Id));
            }

            var options = new List<Dropdown.OptionData>
            {
                new ModPropertyOptionData { id = -1, text = "-- 选择考试榜同学 --" }
            };
            foreach ((int id, object value) in rows)
            {
                string name = cfgType.GetField("name")?.GetValue(value) as string;
                options.Add(new ModPropertyOptionData
                {
                    id = id,
                    text = $"[{id}]{(string.IsNullOrWhiteSpace(name) ? "未命名同学" : name)}"
                });
            }

            if (currentId > 0 && !options.OfType<ModPropertyOptionData>()
                    .Any(option => option.id == currentId))
            {
                string currentName = cfgType.GetField("name")?.GetValue(current) as string;
                options.Add(new ModPropertyOptionData
                {
                    id = currentId,
                    text = $"[{currentId}]{(string.IsNullOrWhiteSpace(currentName) ? "当前自定义条目" : currentName)}"
                });
            }

            cell.SetKeyObj("options", options);
            cell.dropdown_value.ClearOptions();
            cell.dropdown_value.AddOptions(options);

            int selected = options.FindIndex(option =>
                option is ModPropertyOptionData data && data.id == currentId);
            cell.dropdown_value.SetValueWithoutNotify(selected >= 0 ? selected : 0);

            IdSelectorStates.GetValue(cell.dropdown_value, _ =>
            {
                cell.dropdown_value.onValueChanged.AddListener(
                    index => ApplySelectedClassmate(view, cell, index));
                return new SelectorState();
            });
        }

        private static void ApplySelectedClassmate(
            ModNormalEditView view,
            Cell_ModNormalPropertyItemUI cell,
            int index)
        {
            try
            {
                if (!(cell.data is ModFieldItem activeField) || activeField.field?.Name != "id")
                    return;

                List<Dropdown.OptionData> options =
                    cell.GetKeyObj<List<Dropdown.OptionData>>("options");
                if (options == null || index < 0 || index >= options.Count
                    || !(options[index] is ModPropertyOptionData selected) || selected.id <= 0)
                    return;

                Type cfgType = EditViewAccess.CfgType(view);
                IDictionary map = GetCfgMap(cfgType);
                if (map == null || !map.Contains(selected.id)) return;

                object source = map[selected.id];
                object current = EditViewAccess.CurSelect(view);
                if (source == null || current == null) return;

                foreach (string fieldName in new[] { "name", "roleId", "gender", "weight", "cond" })
                {
                    FieldInfo field = cfgType.GetField(fieldName);
                    if (field == null) continue;
                    field.SetValue(current, CloneFieldValue(field.GetValue(source)));
                }

                EditViewAccess.PropertyGroup(view)?.Refresh();
                Traverse.Create(view).Field("itemgroup_item")
                    .GetValue<UIItemGroup>()?.Refresh();
                ToastHelper.Toast($"已载入考试同学：[{selected.id}]" +
                                  (cfgType.GetField("name")?.GetValue(current) as string));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ClassmateCfgEditor] 载入同学条目失败: {e}");
            }
        }

        private static object CloneFieldValue(object value)
        {
            if (!(value is List<List<double>> conditions)) return value;
            return conditions.Select(row => row == null ? null : new List<double>(row)).ToList();
        }

        private static int GetId(object row, Type cfgType)
        {
            if (row == null || cfgType == null) return 0;
            object value = cfgType.GetField("id")?.GetValue(row);
            return value is int id ? id : 0;
        }

        private static IDictionary GetCfgMap(Type cfgType)
        {
            if (cfgType == null) return null;
            PropertyInfo property = typeof(Cfg).GetProperty(
                cfgType.Name + "Map", BindingFlags.Static | BindingFlags.Public);
            return property?.GetValue(null) as IDictionary;
        }

        private static string FormatPersonOption(int id, PersonCfg person, bool local)
        {
            string name = string.IsNullOrWhiteSpace(person?.name)
                ? "未命名人物"
                : person.name;
            return local ? $"[{id}]{name}（当前 Mod）" : $"[{id}]{name}";
        }

        private static Dictionary<int, PersonCfg> LoadLocalPersons(ModNormalEditView view)
        {
            LocalPersonCache cache = Caches.GetValue(view, _ => new LocalPersonCache());
            string modRoot = Traverse.Create(view).Field("modRoot").GetValue<string>();
            if (string.IsNullOrEmpty(modRoot)) return cache.Persons;

            string path = Path.Combine(
                modRoot, "Cfgs", LocalizationMgr.Lang, "PersonCfg.json");
            DateTime writeTime = File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;

            if (string.Equals(cache.Path, path, StringComparison.OrdinalIgnoreCase)
                && cache.WriteTimeUtc == writeTime)
                return cache.Persons;

            cache.Path = path;
            cache.WriteTimeUtc = writeTime;
            cache.Persons = new Dictionary<int, PersonCfg>();

            if (!File.Exists(path)) return cache.Persons;

            try
            {
                cache.Persons = ModCtrl.LoadModCfgMap<PersonCfg>(modRoot, out _)
                    ?? new Dictionary<int, PersonCfg>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(
                    $"[ClassmateCfgEditor] 读取当前 Mod 的 PersonCfg.json 失败，" +
                    $"人物下拉仅显示全局配置：{e.Message}");
            }

            return cache.Persons;
        }
    }
}
