using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Config;
using GenUI.Mod;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sdk;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    // 只跟踪本次新建事件，不能把作者主动引用的跨事件入口当成自动占位。
    internal static class NativeStoryEntry
    {
        internal sealed class Draft
        {
            internal bool ExplicitEntry;
        }

        internal static readonly ConditionalWeakTable<EvtCfg, Draft> NewEvents =
            new ConditionalWeakTable<EvtCfg, Draft>();

        internal static HashSet<int> ReadSavedTalkIds(string modRoot)
        {
            var ids = new HashSet<int>();
            // 原版按 Lang 读取、按 zh-cn 保存；两处都检查，不能覆盖读列表外的已存条目。
            foreach (string lang in new[] { LocalizationMgr.Lang, "zh-cn" }.Distinct())
            {
                string path = Path.Combine(modRoot, "Cfgs", lang, "TalkCfg.json");
                if (!File.Exists(path)) continue;
                JObject map = JObject.Parse(File.ReadAllText(path),
                    new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                foreach (JProperty pair in map.Properties())
                {
                    if (!int.TryParse(pair.Name, out int key) || key <= 0 ||
                        !(pair.Value is JObject value) || (int?)value["id"] != key)
                        throw new InvalidDataException("TalkCfg.json 存在无效编号或键/ID 不一致。");
                    ids.Add(key);
                }
            }
            return ids;
        }

        internal static int Allocate(ModEvtEditView view)
        {
            var access = Traverse.Create(view);
            int eventId = access.Field("evtId").GetValue<int>();
            var current = access.Field("curSelect").GetValue<TalkCfg>();
            var talks = access.Field("talkCfgs").GetValue<List<TalkCfg>>();
            var used = ReadSavedTalkIds(access.Field("modRoot").GetValue<string>());
            if (talks != null) used.UnionWith(talks.Where(t => t != null).Select(t => t.id));
            if (Cfg.TalkCfgMap != null) used.UnionWith(Cfg.TalkCfgMap.Keys);
            return NativeStoryEntryPolicy.AllocateTalkId(eventId, current?.id ?? 0, used);
        }

        internal static void Warn(string message)
        {
            Plugin.Log.LogWarning("[NativeStoryEntry] " + message);
            ToastHelper.Toast(message);
        }
    }

    [HarmonyPatch(typeof(ModNormalEditView), "OnClickNewItem")]
    internal static class NativeStoryNewEventPatch
    {
        private static void Postfix(ModNormalEditView __instance)
        {
            if (EditViewAccess.CurSelect(__instance) is EvtCfg evt)
                NativeStoryEntry.NewEvents.GetValue(evt, _ => new NativeStoryEntry.Draft());
        }
    }

    [HarmonyPatch(typeof(ModNormalEditView), "OnCreateProperty")]
    internal static class NativeStoryExplicitEntryPatch
    {
        private static void Postfix(ModNormalEditView __instance, UICell _cell)
        {
            var cell = _cell as Cell_ModNormalPropertyItemUI;
            if (cell?.input_value == null) return;
            cell.input_value.onEndEdit.AddListener(_ =>
            {
                if (!(cell.data is ModFieldItem field) ||
                    !(EditViewAccess.CurSelect(__instance) is EvtCfg evt) ||
                    !NativeStoryEntry.NewEvents.TryGetValue(evt, out var draft)) return;
                if (field.field.Name == "talkId")
                    draft.ExplicitEntry = true;
                else if (!draft.ExplicitEntry &&
                    (field.field.Name == "id" || field.field.Name == "title"))
                {
                    int first = NativeStoryEntryPolicy.FirstTalkId(evt.id);
                    evt.talkId = first > 0 ? new List<int> { first } : new List<int>();
                    __instance.itemgroup_property.Refresh();
                }
            });
        }
    }

    [HarmonyPatch(typeof(ModNormalEditView), "OnClickEdit")]
    internal static class NativeStoryOpenEntryPatch
    {
        private static bool Prefix(ModNormalEditView __instance)
        {
            if (!(EditViewAccess.CurSelect(__instance) is EvtCfg evt)) return true;
            int first = NativeStoryEntryPolicy.FirstTalkId(evt.id);
            if (first < 0) return true; // 交给原版事件编号提示。
            bool isNew = NativeStoryEntry.NewEvents.TryGetValue(evt, out var draft);
            if (!NativeStoryEntryPolicy.UsesAutomaticEntry(isNew,
                draft?.ExplicitEntry ?? false, evt.talkId)) return true;
            evt.talkId = new List<int> { first };
            NativeStoryEntry.NewEvents.Remove(evt);
            __instance.itemgroup_property.Refresh();
            return true; // 原版先保存事件配置，再把入口传入剧情编辑器。
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "OnOpen")]
    internal static class NativeStoryFirstTalkPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                var access = Traverse.Create(__instance);
                int first = NativeStoryEntryPolicy.FirstTalkId(access.Field("evtId").GetValue<int>());
                var entries = __instance.parms != null && __instance.parms.Length > 2
                    ? __instance.parms[2] as List<int> : null;
                if (first < 0 || entries == null || entries.Count != 1 || entries[0] != first) return;
                var talks = access.Field("talkCfgs").GetValue<List<TalkCfg>>();
                if (talks == null || talks.Any(t => t != null && t.id == first) ||
                    (Cfg.TalkCfgMap != null && Cfg.TalkCfgMap.ContainsKey(first))) return;
                if (NativeStoryEntry.ReadSavedTalkIds(access.Field("modRoot").GetValue<string>()).Contains(first))
                    return;
                // 仅建立窗口草稿，仍由原生「保存」落盘，不借用或改号全局 [1]。
                var talk = new TalkCfg { id = first };
                talks.Insert(0, talk);
                __instance.itemgroup_list.SetDatas(talks);
                access.Method("Select", new[] { typeof(TalkCfg) }).GetValue(talk);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[NativeStoryEntry.Init] " + e);
                NativeStoryEntry.Warn("无法初始化剧情首句，请检查对话配置；未改写已有对白。");
            }
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "GetNewTalkId")]
    internal static class NativeStoryAllocateTalkPatch
    {
        private static bool Prefix(ModEvtEditView __instance, ref int __result)
        {
            __result = -1;
            try
            {
                __result = NativeStoryEntry.Allocate(__instance);
                if (__result < 0) NativeStoryEntry.Warn("当前事件的对话编号已用尽，未新增对白。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[NativeStoryEntry.Allocate] " + e);
                NativeStoryEntry.Warn("无法检查已保存的对白编号，未新增，避免覆盖原记录。");
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "OnClickNext")]
    internal static class NativeStoryNextCapacityPatch
    {
        private static bool Prefix(ModEvtEditView __instance)
        {
            var current = Traverse.Create(__instance).Field("curSelect").GetValue<TalkCfg>();
            if (current == null) return false;
            if (current.nextTalk != null && current.nextTalk.Count > 0) return true;
            // 原版「下一句」会先写入链接，再调用新增；失败时不能留下 -1 链接。
            try
            {
                if (NativeStoryEntry.Allocate(__instance) > 0) return true;
                NativeStoryEntry.Warn("当前事件的对话编号已用尽，未新增下一句。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[NativeStoryEntry.Next] " + e);
                NativeStoryEntry.Warn("无法检查已保存的对白编号，未修改下一句。");
            }
            return false;
        }
    }
}
