using System;
using System.Collections.Generic;
using System.Reflection;
using Config;
using HarmonyLib;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 原版编辑器会把本体配置（不在本 mod JSON 里的对话、选项、事件）按引用直接放进编辑列表，
    /// 改的是全局 Cfg 表里的同一个实例：没保存的修改会留在本局游戏里，看起来像是保存了，
    /// 重启后才消失；预览和本局剧情也会读到半成品。这里在它们进入编辑页时换成深拷贝。
    /// </summary>
    internal static class BuiltInConfigIsolation
    {
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        internal static T Isolate<T>(T value, IDictionary<int, T> globalMap, int key) where T : class
        {
            T global;
            if (value == null || globalMap == null || !globalMap.TryGetValue(key, out global)
                || !ReferenceEquals(global, value))
                return value;
            return EditorChangeTracking.DeepCopy(value);
        }

        internal static void Warn(string scope, Exception e)
        {
            string message = "[BuiltInConfigIsolation] " + scope + "：复制本体配置失败，本次仍直接编辑原对象：" + e;
            if (Warned.Add(scope)) Plugin.Log?.LogError(message);
        }
    }

    /// <summary>事件对话页载入本事件段的本体对话与选项。</summary>
    [HarmonyPatch(typeof(ModEvtEditView), "LoadTalkOptionCfg")]
    internal static class BuiltInTalkOptionIsolationPatch
    {
        private static readonly FieldInfo TalksField = AccessTools.Field(typeof(ModEvtEditView), "talkCfgs");
        private static readonly FieldInfo OptionsField = AccessTools.Field(typeof(ModEvtEditView), "optionCfgs");

        // 在残影过滤等其它载入补丁之后执行，只复制最终留在列表里的对象。
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                int talks = 0;
                int options = 0;
                var talkList = TalksField?.GetValue(__instance) as List<TalkCfg>;
                if (talkList != null && Cfg.TalkCfgMap != null)
                {
                    for (int i = 0; i < talkList.Count; i++)
                    {
                        TalkCfg talk = talkList[i];
                        if (talk == null) continue;
                        TalkCfg copy = BuiltInConfigIsolation.Isolate(talk, Cfg.TalkCfgMap, talk.id);
                        if (ReferenceEquals(copy, talk)) continue;
                        talkList[i] = copy;
                        talks++;
                    }
                }
                var optionMap = OptionsField?.GetValue(__instance) as Dictionary<int, OptionCfg>;
                if (optionMap != null && Cfg.OptionCfgMap != null)
                {
                    foreach (int key in new List<int>(optionMap.Keys))
                    {
                        OptionCfg option = optionMap[key];
                        if (option == null) continue;
                        OptionCfg copy = BuiltInConfigIsolation.Isolate(option, Cfg.OptionCfgMap, option.id);
                        if (ReferenceEquals(copy, option))
                            copy = BuiltInConfigIsolation.Isolate(option, Cfg.OptionCfgMap, key);
                        if (ReferenceEquals(copy, option)) continue;
                        optionMap[key] = copy;
                        options++;
                    }
                }
                if (talks > 0 || options > 0)
                    Plugin.Log?.LogInfo("[BuiltInConfigIsolation] 事件编辑页载入本体配置副本：对话 "
                                        + talks + " 条、选项 " + options + " 个。");
            }
            catch (Exception e)
            {
                BuiltInConfigIsolation.Warn("事件对话页", e);
            }
        }
    }

    /// <summary>
    /// 选项弹窗直接改传入的对象。编辑页没载入的本体选项会回退成全局 Cfg.OptionCfgMap 里的
    /// 实例传进来；换成副本后，点「完成」时副本进入编辑页的选项表，关掉弹窗则全局不受影响。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtOptionView), "OnOpen")]
    internal static class BuiltInOptionPopupIsolationPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(ModEvtOptionView __instance)
        {
            try
            {
                object[] parms = __instance?.parms;
                if (parms == null || parms.Length < 3 || !(parms[2] is OptionCfg option)) return;
                OptionCfg copy = BuiltInConfigIsolation.Isolate(option, Cfg.OptionCfgMap, option.id);
                if (!ReferenceEquals(copy, option)) parms[2] = copy;
            }
            catch (Exception e)
            {
                BuiltInConfigIsolation.Warn("选项弹窗", e);
            }
        }
    }

    /// <summary>事件配置页从「浏览事件」加入本体事件。</summary>
    [HarmonyPatch(typeof(ModNormalEditView), "OnAddEvt")]
    internal static class BuiltInEventIsolationPatch
    {
        private static readonly FieldInfo CfgsField = AccessTools.Field(typeof(ModNormalEditView), "cfgs");

        private static void Postfix(ModNormalEditView __instance, List<int> ids)
        {
            try
            {
                if (ids == null || ids.Count == 0 || Cfg.EvtCfgMap == null) return;
                var cfgs = CfgsField?.GetValue(__instance) as List<object>;
                if (cfgs == null) return;
                // 只处理本次加入的编号：它们刚进列表，不可能是正在编辑的选中项。
                var added = new HashSet<int>(ids);
                int copied = 0;
                for (int i = 0; i < cfgs.Count; i++)
                {
                    var evt = cfgs[i] as EvtCfg;
                    if (evt == null || !added.Contains(evt.id)) continue;
                    EvtCfg copy = BuiltInConfigIsolation.Isolate(evt, Cfg.EvtCfgMap, evt.id);
                    if (ReferenceEquals(copy, evt)) continue;
                    cfgs[i] = copy;
                    copied++;
                }
                if (copied == 0) return;
                __instance.itemgroup_item.SetDatas(cfgs);
                Plugin.Log?.LogInfo("[BuiltInConfigIsolation] 加入本体事件副本 " + copied + " 个。");
            }
            catch (Exception e)
            {
                BuiltInConfigIsolation.Warn("事件配置页", e);
            }
        }
    }
}
