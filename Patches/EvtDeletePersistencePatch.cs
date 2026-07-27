using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Config;
using HarmonyLib;
using Newtonsoft.Json;
using Sdk;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 修复原版 ModEvtEditView 的删除持久化缺口。
    ///
    /// 原版 OnClickDelete 只从 talkCfgs/optionCfgs 内存容器移除；OnClickSave 随后把
    /// 当前项覆盖/追加回完整 JSON，却从不移除旧键。因此 UI 中消失的配置重开事件后
    /// 一定复活。这里在 OnOpen 后记录“本事件当时拥有的 Mod 配置 ID”，保存前只对
    /// 这些已存在、非内置且现在确实消失的 ID 执行剧情图同级别的引用检查和双文件事务。
    /// </summary>
    internal static class ModEvtDeletePersistence
    {
        private sealed class Baseline
        {
            internal string ModRoot;
            internal int EventId;
            internal List<int> Entries = new List<int>();
            internal bool EntriesKnown;
            internal HashSet<int> TalkIds = new HashSet<int>();
            internal HashSet<int> OptionIds = new HashSet<int>();
            internal HashSet<int> NativeTalkIds = new HashSet<int>();
            internal HashSet<int> NativeOptionIds = new HashSet<int>();
        }

        private static readonly ConditionalWeakTable<ModEvtEditView, Baseline> States =
            new ConditionalWeakTable<ModEvtEditView, Baseline>();

        internal static void Capture(ModEvtEditView view)
        {
            if (view == null) return;
            try
            {
                List<TalkCfg> talks;
                Dictionary<int, OptionCfg> options;
                int eventId;
                List<int> entries;
                bool entriesKnown;
                string error;
                string modRoot;
                if (!EvtStoryGraphViewAccess.TrySnapshot(
                        view, out talks, out options, out eventId,
                        out entries, out entriesKnown, out error)
                    || !EvtStoryGraphViewAccess.TryGetModRoot(
                        view, out modRoot, out error))
                {
                    Plugin.Log?.LogWarning(
                        "[EvtDeletePersistence.Capture] " + (error ?? "无法读取事件数据。"));
                    return;
                }

                Dictionary<int, TalkCfg> talkMap = ReadStrictMap<TalkCfg>(
                    ConfigPath<TalkCfg>(modRoot), value => value != null ? value.id : 0);
                Dictionary<int, OptionCfg> optionMap = ReadStrictMap<OptionCfg>(
                    ConfigPath<OptionCfg>(modRoot), value => value != null ? value.id : 0);
                var currentTalkIds = new HashSet<int>(
                    talks.Where(value => value != null).Select(value => value.id));
                var currentOptionIds = new HashSet<int>(options.Keys);
                HashSet<int> nativeTalkIds =
                    StoryGraphConfigProvenance.SnapshotNativeTalkIds(
                        talkMap.Keys);
                HashSet<int> nativeOptionIds =
                    StoryGraphConfigProvenance.SnapshotNativeOptionIds(
                        optionMap.Keys);
                var baseline = new Baseline
                {
                    ModRoot = modRoot,
                    EventId = eventId,
                    Entries = entries != null ? new List<int>(entries) : new List<int>(),
                    EntriesKnown = entriesKnown,
                    TalkIds = new HashSet<int>(talkMap.Keys.Where(id =>
                        currentTalkIds.Contains(id)
                        && !nativeTalkIds.Contains(id))),
                    OptionIds = new HashSet<int>(optionMap.Keys.Where(id =>
                        currentOptionIds.Contains(id)
                        && !nativeOptionIds.Contains(id))),
                    NativeTalkIds = nativeTalkIds,
                    NativeOptionIds = nativeOptionIds,
                };
                States.Remove(view);
                States.Add(view, baseline);
                Plugin.Log?.LogInfo(
                    "[EvtDeletePersistence] 已记录事件 " + eventId + " 的删除基线：Talk "
                    + baseline.TalkIds.Count + "，Option " + baseline.OptionIds.Count + "。" );
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[EvtDeletePersistence.Capture] " + e);
            }
        }

        internal static void BeforeOrdinarySave(ModEvtEditView view)
        {
            if (view == null) return;
            Baseline baseline;
            if (!States.TryGetValue(view, out baseline))
            {
                Capture(view);
                return;
            }

            try
            {
                List<TalkCfg> talks;
                Dictionary<int, OptionCfg> options;
                int eventId;
                List<int> entries;
                bool entriesKnown;
                string error;
                string modRoot;
                if (!EvtStoryGraphViewAccess.TrySnapshot(
                        view, out talks, out options, out eventId,
                        out entries, out entriesKnown, out error)
                    || !EvtStoryGraphViewAccess.TryGetModRoot(
                        view, out modRoot, out error))
                {
                    Warn("普通界面保存前无法读取事件数据，删除未持久化：" + error);
                    return;
                }
                if (!string.Equals(
                        NormalizeRoot(baseline.ModRoot), NormalizeRoot(modRoot),
                        StringComparison.OrdinalIgnoreCase)
                    || baseline.EventId != eventId)
                {
                    Capture(view);
                    return;
                }

                Dictionary<int, TalkCfg> talkMap = ReadStrictMap<TalkCfg>(
                    ConfigPath<TalkCfg>(modRoot), value => value != null ? value.id : 0);
                Dictionary<int, OptionCfg> optionMap = ReadStrictMap<OptionCfg>(
                    ConfigPath<OptionCfg>(modRoot), value => value != null ? value.id : 0);
                var currentTalkIds = new HashSet<int>(
                    talks.Where(value => value != null && value.id > 0)
                        .Select(value => value.id));
                var currentOptionIds = new HashSet<int>(
                    options.Where(pair => pair.Value != null && pair.Key > 0)
                        .Select(pair => pair.Key));
                var removedTalkIds = new HashSet<int>(baseline.TalkIds.Where(id =>
                    !currentTalkIds.Contains(id) && talkMap.ContainsKey(id)
                    && !baseline.NativeTalkIds.Contains(id)));
                var removedOptionIds = new HashSet<int>(baseline.OptionIds.Where(id =>
                    !currentOptionIds.Contains(id) && optionMap.ContainsKey(id)
                    && !baseline.NativeOptionIds.Contains(id)));

                if (removedTalkIds.Count == 0 && removedOptionIds.Count == 0)
                {
                    RememberExpectedCurrent(
                        baseline, talks, options, eventId, entries, entriesKnown, modRoot);
                    return;
                }
                if (entries != null && removedTalkIds.Overlaps(entries))
                {
                    int id = removedTalkIds.First(entries.Contains);
                    Warn("对话 " + id + " 是当前事件入口，不能从普通界面永久删除；"
                         + "请先在事件配置中更换入口。" );
                    return;
                }

                // 初始集 = 打开/最近保存时属于本事件且仍在 JSON 中的作者配置，
                // 再加当前仍在 JSON 的作者配置。这样图内刚保存的新节点也不会被误当成
                // 占用冲突，而事件之外的 JSON 记录不会进入删除集合。
                var initialTalkIds = new HashSet<int>(baseline.TalkIds);
                initialTalkIds.UnionWith(currentTalkIds.Where(id =>
                    talkMap.ContainsKey(id)
                    && !baseline.NativeTalkIds.Contains(id)));
                initialTalkIds.RemoveWhere(id => !talkMap.ContainsKey(id)
                    || baseline.NativeTalkIds.Contains(id));
                var initialOptionIds = new HashSet<int>(baseline.OptionIds);
                initialOptionIds.UnionWith(currentOptionIds.Where(id =>
                    optionMap.ContainsKey(id)
                    && !baseline.NativeOptionIds.Contains(id)));
                initialOptionIds.RemoveWhere(id => !optionMap.ContainsKey(id)
                    || baseline.NativeOptionIds.Contains(id));

                List<TalkCfg> initialTalks = initialTalkIds
                    .OrderBy(id => id).Select(id => talkMap[id]).ToList();
                Dictionary<int, OptionCfg> initialOptions = initialOptionIds
                    .OrderBy(id => id).ToDictionary(id => id, id => optionMap[id]);

                List<TalkCfg> currentOwnedTalks = talks
                    .Where(value => value != null && value.id > 0
                        && !baseline.NativeTalkIds.Contains(value.id))
                    .ToList();
                Dictionary<int, OptionCfg> currentOwnedOptions = options
                    .Where(pair => pair.Value != null && pair.Key > 0
                        && !baseline.NativeOptionIds.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                List<TalkCfg> sanitizedTalks =
                    StoryGraphEditSession.CloneTalks(currentOwnedTalks);
                Dictionary<int, OptionCfg> sanitizedOptions =
                    StoryGraphEditSession.CloneOptions(currentOwnedOptions);
                DisconnectReferences(
                    sanitizedTalks, sanitizedOptions,
                    removedTalkIds, removedOptionIds);

                var session = new StoryGraphEditSession(
                    initialTalks, initialOptions, eventId,
                    entries, entriesKnown, modRoot);
                if (!session.TrySynchronizeDraftForExternalSave(
                        sanitizedTalks, sanitizedOptions, out error)
                    || !StoryGraphEditPersistence.TrySave(session, out error))
                {
                    Warn("普通界面删除未写入配置文件：" + error);
                    return;
                }

                // 事务已经成功后才修改原编辑器对象；随后原版 OnClickSave 会读取同一批
                // 对象，因而不会把刚断开的入线重新写回文件。
                DisconnectReferences(
                    talks, options, removedTalkIds, removedOptionIds);
                RememberExpectedCurrent(
                    baseline, talks, options, eventId, entries, entriesKnown, modRoot);
                string message = "已永久删除 " + removedTalkIds.Count + " 个对话和 "
                               + removedOptionIds.Count + " 个选项，并安全断开当前事件内引用。";
                Plugin.Log?.LogInfo("[EvtDeletePersistence] " + message);
                try { ToastHelper.Toast(message); }
                catch { }
            }
            catch (Exception e)
            {
                Warn("普通界面删除持久化失败，原配置未主动移除："
                     + e.GetType().Name + ": " + e.Message);
                Plugin.Log?.LogError("[EvtDeletePersistence.Save] " + e);
            }
        }

        private static void RememberExpectedCurrent(
            Baseline baseline,
            IEnumerable<TalkCfg> talks,
            IDictionary<int, OptionCfg> options,
            int eventId,
            IEnumerable<int> entries,
            bool entriesKnown,
            string modRoot)
        {
            baseline.ModRoot = modRoot;
            baseline.EventId = eventId;
            baseline.Entries = entries != null
                ? new List<int>(entries) : new List<int>();
            baseline.EntriesKnown = entriesKnown;
            baseline.TalkIds = new HashSet<int>(
                (talks ?? Enumerable.Empty<TalkCfg>())
                    .Where(value => value != null && value.id > 0
                        && !baseline.NativeTalkIds.Contains(value.id))
                    .Select(value => value.id));
            baseline.OptionIds = new HashSet<int>(
                (options ?? new Dictionary<int, OptionCfg>())
                    .Where(pair => pair.Value != null && pair.Key > 0
                        && !baseline.NativeOptionIds.Contains(pair.Key))
                    .Select(pair => pair.Key));
        }

        private static void DisconnectReferences(
            IEnumerable<TalkCfg> talks,
            IDictionary<int, OptionCfg> options,
            ISet<int> removedTalkIds,
            ISet<int> removedOptionIds)
        {
            if (talks != null)
            {
                foreach (TalkCfg talk in talks)
                {
                    if (talk == null) continue;
                    if (removedTalkIds != null)
                        foreach (int removedTalkId in removedTalkIds)
                            StoryGraphEditSession.ClearTalkReferencesTo(
                                talk, removedTalkId);
                    RemoveIds(talk.option, removedOptionIds);
                }
            }
            if (options == null) return;
            foreach (OptionCfg option in options.Values)
            {
                if (option == null) continue;
                if (removedTalkIds != null)
                    foreach (int removedTalkId in removedTalkIds)
                        StoryGraphEditSession.ClearOptionReferencesTo(
                            option, removedTalkId);
            }
        }

        private static void RemoveIds(List<int> values, ISet<int> removed)
        {
            if (values == null || removed == null || removed.Count == 0) return;
            values.RemoveAll(removed.Contains);
        }

        private static Dictionary<int, T> ReadStrictMap<T>(
            string path, Func<T, int> valueId)
        {
            var result = new Dictionary<int, T>();
            if (!File.Exists(path)) return result;
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return result;
            Dictionary<string, T> raw =
                JsonConvert.DeserializeObject<Dictionary<string, T>>(json)
                ?? new Dictionary<string, T>();
            foreach (KeyValuePair<string, T> pair in raw)
            {
                int key;
                int id = pair.Value != null ? valueId(pair.Value) : 0;
                if (!int.TryParse(pair.Key, out key) || key <= 0 || id != key)
                    throw new InvalidDataException(
                        typeof(T).Name + " 存在键/内部 ID 不一致项：" + pair.Key + "/" + id);
                if (result.ContainsKey(id))
                    throw new InvalidDataException(
                        typeof(T).Name + " 存在重复内部 ID：" + id);
                result[id] = pair.Value;
            }
            return result;
        }

        private static string ConfigPath<T>(string modRoot)
        {
            string root = NormalizeRoot(modRoot);
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Mod 根目录为空。" );
            string path = Path.GetFullPath(Path.Combine(
                root, "Cfgs", "zh-cn", typeof(T).Name + ".json"));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("配置路径越出 Mod 根目录。" );
            return path;
        }

        private static string NormalizeRoot(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return Path.GetFullPath(value)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void Warn(string message)
        {
            Plugin.Log?.LogWarning("[EvtDeletePersistence] " + message);
            try { ToastHelper.Toast(message); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "OnOpen")]
    internal static class ModEvtDeleteBaselinePatch
    {
        private static void Postfix(ModEvtEditView __instance)
        {
            ModEvtDeletePersistence.Capture(__instance);
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "OnClickSave")]
    internal static class ModEvtDeleteSavePatch
    {
        private static bool Prefix(ModEvtEditView __instance)
        {
            // 事务日志滞留期间，原版全量保存会改写两份配置并破坏日志指纹，
            // 使自动恢复永久失败（配置被判定为外部修改而锁死）。此时必须整体
            // 拦下普通保存；守卫自身异常则放行（无法判定时不阻塞正常使用）。
            try
            {
                string modRoot;
                string error;
                if (EvtStoryGraphViewAccess.TryGetModRoot(
                        __instance, out modRoot, out error)
                    && StoryGraphEditPersistence.HasPendingTransaction(modRoot))
                {
                    string message = "存在未完成的剧情图保存事务，已阻止普通保存以免破坏自动恢复；"
                                   + "请关闭并重新打开事件完成恢复后再保存。";
                    Plugin.Log?.LogWarning("[EvtDeletePersistence] " + message);
                    try { ToastHelper.Toast(message); }
                    catch { }
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[EvtDeletePersistence.SaveGuard] " + e);
            }

            ModEvtDeletePersistence.BeforeOrdinarySave(__instance);
            return true;
        }
    }
}
