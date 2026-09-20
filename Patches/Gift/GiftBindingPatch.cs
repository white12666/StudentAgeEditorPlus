using System;
using System.Collections.Generic;
using System.Linq;
using Config;
using HarmonyLib;
using Sdk;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    [HarmonyPatch(typeof(ModNormalEditView), "OnOpen")]
    internal static class GiftBindingEntryPatch
    {
        private const string EntryName = "EditorPlus_GiftBindingEntry";

        private static void Postfix(ModNormalEditView __instance)
        {
            try
            {
                Transform existing = __instance.group_bottom.Find(EntryName);
                bool gift = EditViewAccess.CfgType(__instance) == typeof(GiftEvtCfg);
                if (existing != null) existing.gameObject.SetActive(gift);
                if (!gift) return;
                __instance.group_bottom.gameObject.SetActive(true);
                __instance.btn_edit_evt.gameObject.SetActive(false);
                __instance.btn_preview_evt.gameObject.SetActive(false);
                if (existing == null)
                {
                    // 原表单使用另一套 Canvas 比例，克隆原生按钮以保留可读字号和皮肤。
                    GameObject clone = UnityEngine.Object.Instantiate(
                        __instance.btn_edit_evt.gameObject, __instance.group_bottom, false);
                    clone.name = EntryName;
                    MiniGameUtil.StripBadComponents(clone);
                    var button = new UIButton(clone);
                    button.transform.sizeDelta = new Vector2(
                        Math.Max(380, button.transform.rect.width), button.transform.rect.height);
                    foreach (Text text in clone.GetComponentsInChildren<Text>(true)) text.text = "绑定送礼剧情…";
                    foreach (TMP_Text text in clone.GetComponentsInChildren<TMP_Text>(true)) text.text = "绑定送礼剧情…";
                    button.AddClick(() => GiftBindingWindow.Open(__instance));
                    clone.SetActive(true);
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[GiftBinding] 入口初始化失败：" + e); }
        }
    }

    [HarmonyPatch(typeof(ModNormalEditView), "OnClickSave")]
    internal static class GiftBindingSavePatch
    {
        private static bool Prefix(ModNormalEditView __instance)
        {
            if (EditViewAccess.CfgType(__instance) != typeof(GiftEvtCfg)) return true;
            try
            {
                GiftFormInput.CommitFocused(__instance);
                var traverse = Traverse.Create(__instance);
                var cfgs = traverse.Field("cfgs").GetValue<List<object>>();
                if (cfgs == null || cfgs.Count == 0) return true;
                GiftStoryCatalog catalog = GiftCatalogProvenance.Load(traverse.Field("modRoot").GetValue<string>());
                var seen = new HashSet<int>();
                foreach (GiftEvtCfg cfg in cfgs.OfType<GiftEvtCfg>())
                {
                    if (cfg.id == 0) continue; // 与原生保存忽略未编号条目的行为一致。
                    string error = !seen.Add(cfg.id) ? "送礼事件 ID 重复：" + cfg.id :
                        GiftBindingDraft.Create(cfg.npc, cfg.talkId, cfg.type).Validate(id => catalog.Talks.ContainsKey(id));
                    if (error != null)
                    {
                        __instance.txtex_desc.text = "送礼事件 " + cfg.id + " 未保存：\n" + error;
                        ToastHelper.Toast("未保存：送礼事件 " + cfg.id + " 的剧情绑定有误，请查看右侧说明。");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[GiftBinding] 保存检查失败：" + e);
                __instance.txtex_desc.text = "未保存：无法读取当前作品的剧情配置，请检查 TalkCfg.json / EvtCfg.json / PersonCfg.json。";
                ToastHelper.Toast("剧情配置读取失败，未保存送礼事件。");
                return false;
            }
        }
    }
}
