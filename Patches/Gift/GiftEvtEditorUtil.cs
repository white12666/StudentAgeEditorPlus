using System;
using System.Collections.Generic;
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
    internal sealed class GiftDropdownOptionData : ModPropertyOptionData
    {
        internal readonly string Field;
        internal readonly int Value;

        internal GiftDropdownOptionData(string field, int value, string text)
        {
            // 额外的原生监听器也不能把 int 写进 type 的 List<int>。
            id = -1;
            Field = field;
            Value = value;
            this.text = text;
        }
    }

    internal static class GiftEvtEditorUtil
    {
        private sealed class DropdownState
        {
            internal GiftEvtCfg Record;
            internal string Field;
        }

        private static readonly ConditionalWeakTable<Dropdown, DropdownState> DropdownStates =
            new ConditionalWeakTable<Dropdown, DropdownState>();

        internal static void OnRenderProperty(
            object viewObj,
            Cell_ModNormalPropertyItemUI cell,
            ModFieldItem fieldItem)
        {
            if (cell == null || fieldItem?.field == null) return;

            string name = fieldItem.field.Name;
            if (name == "item")
            {
                RenderItemProperty(viewObj, cell, fieldItem);
            }
        }

        private static void RenderItemProperty(
            object viewObj,
            Cell_ModNormalPropertyItemUI cell,
            ModFieldItem fieldItem)
        {
            // item 字段下拉框追加书籍
            if (!cell.dropdown_value.gameObject.activeSelf) return;
            if (Cfg.BookCfgMap == null || Cfg.BookCfgMap.Count == 0) return;

            var existing = cell.GetKeyObj<List<Dropdown.OptionData>>("options");
            if (existing == null) return;

            var existingIds = new HashSet<int>();
            foreach (var opt in existing)
            {
                if (opt is ModPropertyOptionData mod) existingIds.Add(mod.id);
            }

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
        }

        internal static bool RenderChoice(ModNormalEditView view, Cell_ModNormalPropertyItemUI cell,
            ModFieldItem field)
        {
            string name = field?.field?.Name;
            if (EditViewAccess.CfgType(view) != typeof(GiftEvtCfg) || (name != "redpoint" && name != "type"))
            {
                ReleaseChoice(cell);
                return false;
            }
            var gift = EditViewAccess.CurSelect(view) as GiftEvtCfg;
            Dropdown dropdown = cell.dropdown_value;
            var state = DropdownStates.GetValue(dropdown, _ => new DropdownState());
            if (!ReferenceEquals(state.Record, gift) || state.Field != name)
            {
                if (dropdown.transform.Find("Dropdown List") != null) dropdown.Hide();
                state.Record = gift;
                state.Field = name;
            }
            // 与原版公共字段头保持一致，但不先关闭正在使用的 Dropdown。
            cell.gameObject.SetActive(!field.attr.Hide);
            if (field.attr.Hide) return true;
            cell.txt_name.text = field.attr.Name;
            cell.txt_required.text = field.attr.Required ? DescCtrl.GetTxt(1158) : string.Empty;
            cell.btn_value.gameObject.SetActive(false);
            cell.input_value.contentType = InputField.ContentType.Standard;
            bool textMode = name == "type" && gift != null &&
                ((gift.npc?.Count ?? 0) > 1 || (gift.type?.Count ?? 0) > 1 ||
                 (gift.type?.Any(v => v != 0 && v != 1) ?? false));
            if (textMode)
            {
                dropdown.gameObject.SetActive(false);
                cell.input_value.gameObject.SetActive(true);
                cell.input_value.SetTextWithoutNotify(gift.type == null ? string.Empty : string.Join(",", gift.type));
                return true;
            }
            dropdown.gameObject.SetActive(true);
            cell.input_value.gameObject.SetActive(false);
            int current = name == "redpoint" ? gift?.redpoint ?? 1
                : gift?.type != null && gift.type.Count > 0 ? gift.type[0] : 0;
            var options = name == "redpoint" ? new List<Dropdown.OptionData>
            {
                new GiftDropdownOptionData(name, 1, "1：显示红点（默认）"),
                new GiftDropdownOptionData(name, 0, "0：不显示红点")
            } : new List<Dropdown.OptionData>
            {
                new GiftDropdownOptionData(name, 0, "0：消失（赠予NPC，默认）"),
                new GiftDropdownOptionData(name, 1, "1：保留（不消耗）")
            };
            if (name == "redpoint" && current != 0 && current != 1)
                options.Add(new GiftDropdownOptionData(name, current, $"[{current}]当前数值"));
            bool same = dropdown.options.Count == options.Count;
            for (int i = 0; same && i < options.Count; i++)
            {
                var old = dropdown.options[i] as GiftDropdownOptionData;
                var next = (GiftDropdownOptionData)options[i];
                same = old != null && old.Field == next.Field && old.Value == next.Value && old.text == next.text;
            }
            int selected = options.FindIndex(option => ((GiftDropdownOptionData)option).Value == current);
            if (!same)
            {
                if (dropdown.transform.Find("Dropdown List") != null) dropdown.Hide();
                dropdown.ClearOptions();
                dropdown.AddOptions(options);
            }
            else if (dropdown.value != selected && dropdown.transform.Find("Dropdown List") != null)
            {
                // 仅外部表单更新才走这里；不能留下属于旧记录的展开列表。
                dropdown.Hide();
            }
            cell.SetKeyObj("options", dropdown.options);
            dropdown.SetValueWithoutNotify(selected);
            return true;
        }

        internal static bool SelectChoice(ModNormalEditView view, Cell_ModNormalPropertyItemUI cell, int index)
        {
            if (!(EditViewAccess.CurSelect(view) is GiftEvtCfg gift) ||
                !(cell.data is ModFieldItem field) ||
                (field.field.Name != "type" && field.field.Name != "redpoint")) return false;
            Dropdown dropdown = cell.dropdown_value;
            if (!dropdown.gameObject.activeSelf || index < 0 || index >= dropdown.options.Count ||
                !(dropdown.options[index] is GiftDropdownOptionData option) ||
                option.Field != field.field.Name) return true;
            if (option.Field == "redpoint") gift.redpoint = option.Value;
            else
            {
                if (gift.npc == null || gift.npc.Count != 1 || (gift.type?.Count ?? 0) > 1)
                {
                    int actual = gift.type != null && gift.type.Count > 0 ? gift.type[0] : 0;
                    int selected = dropdown.options.FindIndex(o => o is GiftDropdownOptionData data && data.Value == actual);
                    dropdown.SetValueWithoutNotify(Math.Max(0, selected));
                    ToastHelper.Toast("单个 NPC 可用下拉设置；请先添加 NPC，多位 NPC 请分别设置。");
                    return true;
                }
                gift.type = new List<int> { option.Value };
            }
            // Dropdown.Set 已刷新标题；回调返回后由原生 OnSelectItem.Hide 收起，不重绘整表。
            return true;
        }

        private static void ReleaseChoice(Cell_ModNormalPropertyItemUI cell)
        {
            if (!DropdownStates.TryGetValue(cell.dropdown_value, out _)) return;
            if (cell.dropdown_value.transform.Find("Dropdown List") != null) cell.dropdown_value.Hide();
            cell.RemoveKeyObj("options");
            DropdownStates.Remove(cell.dropdown_value);
        }
    }

    [HarmonyPatch(typeof(ModNormalEditView), "OnRenderProperty")]
    internal static class GiftDropdownRenderPatch
    {
        private static bool Prefix(ModNormalEditView __instance, UICell _cell) =>
            !(_cell is Cell_ModNormalPropertyItemUI cell && cell.data is ModFieldItem field &&
              GiftEvtEditorUtil.RenderChoice(__instance, cell, field));
    }

    [HarmonyPatch(typeof(ModNormalEditView), "OnCreateProperty")]
    internal static class GiftDropdownSelectionPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ModNormalEditView __instance, UICell _cell)
        {
            if (!(_cell is Cell_ModNormalPropertyItemUI cell)) return;
            var native = cell.dropdown_value.onValueChanged;
            var changed = new Dropdown.DropdownEvent();
            changed.AddListener(index =>
            {
                if (!GiftEvtEditorUtil.SelectChoice(__instance, cell, index)) native.Invoke(index);
            });
            cell.dropdown_value.onValueChanged = changed;
        }
    }
}
