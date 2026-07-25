using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Config;
using HarmonyLib;
using MessagePack;
using StudentAgeSocialRoles;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    internal sealed class SocialRolePersonSaveState
    {
        internal bool Saving;
    }

    internal static class SocialRolePersonSaveSafety
    {
        private static readonly ConditionalWeakTable<ModPersonEditView, SocialRolePersonSaveState> States = new();

        internal static bool TrySave(ModPersonEditView view, out string error)
        {
            error = null;
            SocialRolePersonSaveState state = States.GetOrCreateValue(view);
            if (state.Saving)
            {
                error = "人物配置正在保存，请勿重复点击。";
                return false;
            }

            state.Saving = true;
            try
            {
                CommitCurrentBirthdayInput(view);
                if (!TryBuildPayload(
                        view,
                        out List<PersonCfg> cfgs,
                        out string personJson,
                        out string growJson,
                        out string personPath,
                        out string growPath,
                        out error))
                    return false;

                if (!AtomicFilePairTransaction.SavePair(
                        personPath, personJson, growPath, growJson, out error))
                    return false;

                // 保持原编辑器行为：编号未完成的临时条目不进入文件，成功保存后也从列表移除。
                cfgs?.RemoveAll(cfg => cfg == null || cfg.id <= 0);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                state.Saving = false;
            }
        }

        internal static bool RecoverBeforeLoad(ModPersonEditView view, out bool recovered, out string error)
        {
            recovered = false;
            error = null;
            string modRoot = Traverse.Create(view).Field("modRoot").GetValue<string>();
            if (string.IsNullOrWhiteSpace(modRoot)) return true;

            string personPath = ModCtrl.GetModCfgFilePath<PersonCfg>(modRoot);
            string growPath = ModCtrl.GetModCfgFilePath<PersonGrowCfg>(modRoot);
            return AtomicFilePairTransaction.Recover(
                personPath, growPath, out recovered, out error);
        }

        private static void CommitCurrentBirthdayInput(ModPersonEditView view)
        {
            PersonCfg current = Traverse.Create(view).Field("curSelect").GetValue<PersonCfg>();
            if (current == null || view.input_birthday == null) return;

            string text = view.input_birthday.text;
            current.birthday = string.IsNullOrWhiteSpace(text)
                ? null
                : ModCtrl.StrToList<int>(text);
        }

        private static bool TryBuildPayload(
            ModPersonEditView view,
            out List<PersonCfg> cfgs,
            out string personJson,
            out string growJson,
            out string personPath,
            out string growPath,
            out string error)
        {
            var traverse = Traverse.Create(view);
            cfgs = traverse.Field("cfgs").GetValue<List<PersonCfg>>() ?? new List<PersonCfg>();
            var growCfgs = traverse.Field("growCfgs").GetValue<Dictionary<int, PersonGrowCfg>>();
            if (growCfgs == null)
            {
                growCfgs = new Dictionary<int, PersonGrowCfg>();
                traverse.Field("growCfgs").SetValue(growCfgs);
            }

            personPath = traverse.Field("cfgPath").GetValue<string>();
            growPath = traverse.Field("growCfgPath").GetValue<string>();
            personJson = null;
            growJson = null;
            error = null;

            var personMap = new Dictionary<string, PersonCfg>();
            foreach (PersonCfg person in cfgs.Where(item => item != null && item.id > 0))
            {
                string key = person.id.ToString();
                if (personMap.ContainsKey(key))
                {
                    error = $"人物编号重复：{person.id}。未写入任何文件。";
                    return false;
                }

                if (SocialRoleProfileUtil.IsSocialRole(person))
                {
                    if (!SocialRoleProfileUtil.TryValidateBirthday(person.birthday, out string birthdayError))
                    {
                        string displayName = string.IsNullOrWhiteSpace(person.name) ? "未命名人物" : person.name;
                        error = $"可社交角色 [{person.id}] {displayName}：{birthdayError}。"
                            + "生日会写入玩家存档，必须修正后才能保存。";
                        return false;
                    }

                    if (!growCfgs.TryGetValue(person.id, out PersonGrowCfg grow) || grow == null)
                    {
                        grow = new PersonGrowCfg { id = person.id };
                        growCfgs[person.id] = grow;
                    }
                    grow.id = person.id;
                    SocialRoleEditorUtil.EnsureSafeLists(grow);

                    if (grow.studyRank != null && Cfg.ScoreRankCfgMap != null)
                    {
                        for (int i = 0; i < grow.studyRank.Count; i++)
                        {
                            int rankId = grow.studyRank[i];
                            if (!Cfg.ScoreRankCfgMap.ContainsKey(rankId))
                            {
                                error = $"可社交角色 [{person.id}] 的成绩段位 ID {rankId} 不存在"
                                    + $"（studyRank 第 {i + 1} 项）。未写入任何文件。";
                                return false;
                            }
                        }
                    }
                }

                EnsureAllListFields(person);
                personMap.Add(key, person);
            }

            var growMap = new Dictionary<string, PersonGrowCfg>();
            foreach (PersonCfg person in personMap.Values)
            {
                if (!SocialRoleProfileUtil.IsSocialRole(person)) continue;
                PersonGrowCfg grow = growCfgs[person.id];
                EnsureAllListFields(grow);
                growMap.Add(person.id.ToString(), grow);
            }

            if (personMap.Count > 0)
                personJson = MessagePackSerializer.SerializeToJson(personMap);
            if (growMap.Count > 0)
                growJson = MessagePackSerializer.SerializeToJson(growMap);
            return true;
        }

        private static void EnsureAllListFields(object value)
        {
            if (value == null) return;
            foreach (FieldInfo field in value.GetType().GetFields())
            {
                if (!field.FieldType.IsGenericType
                    || field.FieldType.GetGenericTypeDefinition() != typeof(List<>)
                    || field.GetValue(value) != null)
                    continue;

                Type itemType = field.FieldType.GetGenericArguments()[0];
                object list = Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
                field.SetValue(value, list);
            }
        }
    }

    /// <summary>替换人物编辑器原本的直接覆盖保存，保证两张人物表成对提交。</summary>
    [HarmonyPatch(typeof(ModPersonEditView), "OnClickSave")]
    internal static class SocialRolePersonTransactionalSavePatch
    {
        private static bool Prefix(ModPersonEditView __instance)
        {
            if (SocialRolePersonSaveSafety.TrySave(__instance, out string error))
            {
                ToastHelper.Toast(932);
            }
            else
            {
                string message = "人物配置未保存：" + (error ?? "未知错误");
                Plugin.Log?.LogError("[PersonSave.Transaction] " + message);
                ToastHelper.Toast(message);
            }
            return false;
        }
    }

    /// <summary>原 InitCfg 读盘前恢复上次被强退打断的成对保存。</summary>
    [HarmonyPatch(typeof(ModPersonEditView), "InitCfg")]
    internal static class SocialRolePersonTransactionRecoveryPatch
    {
        private static void Prefix(ModPersonEditView __instance)
        {
            if (!SocialRolePersonSaveSafety.RecoverBeforeLoad(
                    __instance, out bool recovered, out string error))
            {
                string message = "人物配置存在未完成事务且自动恢复失败：" + error;
                Plugin.Log?.LogError("[PersonSave.Recover] " + message);
                ToastHelper.Toast(message);
                throw new InvalidOperationException(message);
            }

            if (recovered)
            {
                Plugin.Log?.LogWarning(
                    "[PersonSave.Recover] 检测到上次保存被中断，已将 PersonCfg/PersonGrowCfg 成对恢复。");
                ToastHelper.Toast("检测到上次人物保存被中断，已自动恢复两张配置表");
            }
        }
    }
}
