using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sdk;

namespace StudentAgeEditorPlus.Patches
{
    internal sealed class GiftCatalogSources
    {
        internal Dictionary<int, TalkCfg> Talks;
        internal Dictionary<int, EvtCfg> Events;
        internal Dictionary<int, PersonCfg> People;
    }

    internal sealed class GiftStoryCatalog
    {
        internal readonly Dictionary<int, GiftStoryChoice> Talks = new Dictionary<int, GiftStoryChoice>();
        internal readonly Dictionary<int, string> Npcs = new Dictionary<int, string>();

        internal static GiftStoryCatalog Load(string modRoot, GiftCatalogSources sources = null,
            IEnumerable<string> otherModCfgDirs = null)
        {
            string cfgDir = Path.Combine(modRoot, "Cfgs", LocalizationMgr.Lang);
            var talks = Copy(sources != null ? sources.Talks : Cfg.TalkCfgMap);
            var events = Copy(sources != null ? sources.Events : Cfg.EvtCfgMap);
            var people = Copy(sources != null ? sources.People : Cfg.PersonCfgMap);
            var otherTalks = new Dictionary<int, TalkCfg>();
            var otherEvents = new Dictionary<int, EvtCfg>();
            var otherPeople = new Dictionary<int, PersonCfg>();
            foreach (string otherDir in otherModCfgDirs ?? Enumerable.Empty<string>())
            {
                // 原生 ModCtrl 对启用 Mod 采用先加载者优先；本作品的旧工坊副本由调用方排除。
                MergeLocal(otherDir, "TalkCfg", otherTalks, false);
                MergeLocal(otherDir, "EvtCfg", otherEvents, false);
                MergeLocal(otherDir, "PersonCfg", otherPeople, false);
            }
            Overlay(talks, otherTalks);
            Overlay(events, otherEvents);
            Overlay(people, otherPeople);
            HashSet<int> localTalks = MergeLocal(cfgDir, "TalkCfg", talks);
            MergeLocal(cfgDir, "EvtCfg", events);
            MergeLocal(cfgDir, "PersonCfg", people);

            var titles = new Dictionary<int, List<string>>();
            foreach (EvtCfg evt in events.Values.Where(value => value != null))
            {
                if (evt.talkId == null) continue;
                for (int slot = 0; slot < Math.Min(2, evt.talkId.Count); slot++)
                {
                    int id = evt.talkId[slot];
                    if (id <= 0) continue;
                    if (!titles.TryGetValue(id, out var names)) titles[id] = names = new List<string>();
                    string title = string.IsNullOrWhiteSpace(evt.title) ? "事件 " + evt.id : evt.title;
                    if (evt.talkId.Count > 1) title += slot == 0 ? "（男主入口）" : "（女主入口）";
                    if (!names.Contains(title)) names.Add(title);
                }
            }

            var result = new GiftStoryCatalog();
            foreach (var pair in talks)
            {
                if (pair.Value == null || pair.Key <= 0) continue;
                titles.TryGetValue(pair.Key, out var names);
                result.Talks[pair.Key] = new GiftStoryChoice
                {
                    Id = pair.Key,
                    Title = names != null ? string.Join(" / ", names) : "对话片段",
                    Content = pair.Value.content,
                    IsLocal = localTalks.Contains(pair.Key),
                    IsEntry = names != null,
                };
            }
            foreach (var pair in people)
                if (pair.Value != null && pair.Key > 0)
                    result.Npcs[pair.Key] = pair.Value.name;
            return result;
        }

        private static Dictionary<int, T> Copy<T>(Dictionary<int, T> source) =>
            source != null ? new Dictionary<int, T>(source) : new Dictionary<int, T>();

        private static void Overlay<T>(Dictionary<int, T> target, Dictionary<int, T> source)
        {
            foreach (var pair in source) target[pair.Key] = pair.Value;
        }

        internal static bool IsCurrentMod(ulong currentId, string currentPackage, ulong loadedId, string loadedPackage) =>
            (currentId != 0 && currentId == loadedId) ||
            (!string.IsNullOrWhiteSpace(currentPackage) &&
                string.Equals(currentPackage, loadedPackage, StringComparison.Ordinal));

        private static HashSet<int> MergeLocal<T>(string cfgDir, string name, Dictionary<int, T> target, bool overwrite = true)
        {
            var localIds = new HashSet<int>();
            string path = Path.Combine(cfgDir, name + ".json");
            if (!File.Exists(path)) return localIds;
            // 解析失败不可回退到旧的已加载配置，否则作者会绑定到过期内容。
            JObject data = JObject.Parse(File.ReadAllText(path),
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            foreach (JProperty property in data.Properties())
            {
                if (!int.TryParse(property.Name, out int id) || id <= 0 ||
                    !(property.Value is JObject obj) || (int?)obj["id"] != id)
                    throw new JsonException(name + ".json 中存在无效编号或键/id 不一致的记录。");
                T value = obj.ToObject<T>();
                if (overwrite || !target.ContainsKey(id)) target[id] = value;
                localIds.Add(id);
            }
            return localIds;
        }

        internal string NpcName(int id) =>
            Npcs.TryGetValue(id, out string name) && !string.IsNullOrEmpty(name) ? name : "NPC " + id;

        internal static string Compact(string text, int limit)
        {
            text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= limit ? text : text.Substring(0, limit) + "…";
        }
    }
}
