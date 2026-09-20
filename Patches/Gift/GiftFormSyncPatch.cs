using System;
using System.Linq;
using Config;
using GenUI.Mod;
using HarmonyLib;
using Sdk;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    internal static class GiftFormInput
    {
        internal static void CommitFocused(ModNormalEditView view)
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            var input = selected != null ? selected.GetComponentInParent<InputField>() : null;
            if (input != null && input.isFocused && input.transform.IsChildOf(view.gameObject.transform))
            {
                input.DeactivateInputField();
                EventSystem.current?.SetSelectedGameObject(null);
            }
        }

        internal static bool TryHandle(ModNormalEditView view, Cell_ModNormalPropertyItemUI cell, string text)
        {
            if (!(EditViewAccess.CurSelect(view) is GiftEvtCfg gift) ||
                !(cell.data is ModFieldItem field)) return false;
            string name = field.field.Name;
            if (name != "npc" && name != "talkId" && name != "type") return false;
            if (!cell.input_value.gameObject.activeSelf) return true;
            string status;
            try
            {
                if (name == "npc")
                {
                    GiftFormSync.RemapNpcs(text, gift.npc, gift.talkId, gift.type,
                        out var npcs, out var talks, out var types);
                    gift.npc = npcs;
                    gift.talkId = talks;
                    gift.type = types;
                    status = npcs.Count == 0
                        ? "已清空 NPC 及对应剧情、物品设置。保存后此记录不触发送礼对白。"
                        : "已按 NPC 保留对应剧情和物品设置；新添加的 NPC 需要选择剧情。";
                }
                else if (name == "talkId")
                {
                    var talks = GiftFormSync.ParseTalks(text);
                    if (talks.Any(row => row.Count > 2 || row.Any(id => id < 0)))
                        throw new FormatException("每位 NPC 最多填写男主、女主两个非负首句 ID。");
                    gift.talkId = talks;
                    status = "已更新对白入口；仍有 NPC 时，需要绑定有效首句后才能保存。";
                }
                else
                {
                    var types = GiftFormSync.ParseIds(text);
                    if (types.Any(value => value != 0 && value != 1))
                        throw new FormatException("类型标记仅支持 0（消失）和 1（保留）。");
                    gift.type = types;
                    status = "已更新物品设置；留空使用默认值 0（赠出后消失）。";
                }
            }
            catch (FormatException e)
            {
                status = "未应用输入：" + e.Message;
                ToastHelper.Toast(status);
            }
            // 拒绝输入也刷新为原值，不能让看似成功的文本和实际数据不一致。
            view.itemgroup_property.Refresh();
            view.itemgroup_item.Refresh();
            view.txtex_desc.text = status;
            return true;
        }
    }

    [HarmonyPatch(typeof(ModNormalEditView), "OnCreateProperty")]
    internal static class GiftFormSyncPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ModNormalEditView __instance, UICell _cell)
        {
            if (!(_cell is Cell_ModNormalPropertyItemUI cell) || cell.input_value == null) return;
            // 原生处理器先改 npc 后便丢失旧对应关系，而且会忽略空输入。
            // 为这三个送礼字段接管提交；复用到其它表或字段时完整转发原监听链。
            var original = cell.input_value.onEndEdit;
            var submit = new InputField.SubmitEvent();
            submit.AddListener(text =>
            {
                if (!GiftFormInput.TryHandle(__instance, cell, text)) original.Invoke(text);
            });
            cell.input_value.onEndEdit = submit;
        }
    }
}
