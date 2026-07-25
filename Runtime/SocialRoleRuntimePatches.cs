using System;
using System.Runtime.CompilerServices;
using Config;
using HarmonyLib;
using Sdk;
using StudentAgeSocialRoles;
using TheEntity;
using UnityEngine;
using UnityEngine.UI;
using View.Main;

namespace StudentAgeSocialRoleRuntime
{
    internal sealed class SocialProfileLabelDefaults
    {
        public string Primary;
        public string Secondary;
    }

    internal static class SocialProfileRuntimeState
    {
        private static readonly ConditionalWeakTable<DetailSocialView, SocialProfileLabelDefaults> Defaults = new();

        public static SocialProfileLabelDefaults Get(DetailSocialView view)
        {
            if (Defaults.TryGetValue(view, out var value)) return value;
            value = new SocialProfileLabelDefaults
            {
                Primary = NonEmptyOr(view.school != null ? view.school.GetComponent<Text>()?.text : null, "学校："),
                Secondary = NonEmptyOr(view.classname != null ? view.classname.GetComponent<Text>()?.text : null, "班级："),
            };
            Defaults.Add(view, value);
            return value;
        }

        private static string NonEmptyOr(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }

    /// <summary>
    /// 用游戏原生 CommonEvtMgr 存档接口判断资料阶段的触发条件。
    /// 判定失败一律视为不满足，让 ResolveActiveStage 回退到更早的阶段。
    /// </summary>
    internal sealed class CommonEvtStageChecker : IStageConditionChecker
    {
        public static readonly CommonEvtStageChecker Instance = new();

        public bool IsMatch(StageTrigger trigger)
        {
            if (trigger == null || trigger.Type == StageTriggerType.None) return true;
            try
            {
                CommonEvtMgr mgr = Singleton<CommonEvtMgr>.Ins;
                switch (trigger.Type)
                {
                    case StageTriggerType.EventHappened:
                        return mgr.HasEventHappended(trigger.EvtId, trigger.Count < 1 ? 1 : trigger.Count);
                    case StageTriggerType.OptionSelected:
                        return mgr.HasOptionSelected(trigger.EvtId);
                    case StageTriggerType.EventValue:
                        return mgr.GetEvtSaveData(trigger.EvtId, trigger.Pos) >= trigger.Target;
                    default:
                        return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    $"[SocialRoleStageChecker] 阶段条件判定失败(type={trigger.Type} evtId={trigger.EvtId}): {e.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 原版 GetClassName 无论查询哪个 NPC，都用主角 Grade/GradeState 和 GradeCfg.school。
    /// 带自定义资料的角色直接返回独立资料，并跳过原方法对 className 数组的强制索引。
    /// </summary>
    [HarmonyPatch(typeof(RoleMgr), nameof(RoleMgr.GetClassName))]
    internal static class SocialRoleClassNameRuntimePatch
    {
        private static bool Prefix(int _npcId, ref ValueTuple<string, string> __result)
        {
            PersonCfg person = null;
            SocialRoleProfileData data = null;
            try
            {
                if (_npcId == 0 || Cfg.PersonCfgMap == null
                    || !Cfg.PersonCfgMap.TryGetValue(_npcId, out person)
                    || !SocialRoleProfileUtil.IsSocialRole(person)
                    || !SocialRoleProfileCodec.TryRead(person, out data)
                    || !data.IsCustom)
                {
                    return true;
                }

                ProfileStage stage = data.ResolveActiveStage(CommonEvtStageChecker.Instance);
                __result = new ValueTuple<string, string>(
                    EmptyAsDash(stage?.PrimaryValue),
                    EmptyAsDash(stage?.SecondaryValue));
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[SocialRoleClassNameRuntime] {e}");
                // 已识别为自定义资料时不能回退原方法：原方法会强制索引 className，
                // 手编配置的短列表可能再次抛错。宁可显示占位符，也不要让资料页崩溃。
                if (person != null && SocialRoleProfileUtil.IsSocialRole(person)
                    && (data?.IsCustom == true
                        || (SocialRoleProfileCodec.TryRead(person, out var recovered) && recovered.IsCustom)))
                {
                    __result = new ValueTuple<string, string>("—", "—");
                    return false;
                }
                return true;
            }
        }

        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
        }
    }

    /// <summary>
    /// 自定义老师/成人资料页：替换两栏标题与内容、可隐藏成绩段位，
    /// 并避免原方法无条件索引 studyRank[GradeState]。
    /// </summary>
    [HarmonyPatch(typeof(DetailSocialView), "RefreshProfile")]
    internal static class SocialRoleProfileRuntimePatch
    {
        private static bool Prefix(DetailSocialView __instance)
        {
            bool customTarget = TryResolveCustomTarget(
                __instance, out Role role, out PersonCfg person, out SocialRoleProfileData data);
            ProfileStage stage = null;
            try
            {
                if (customTarget) stage = data.ResolveActiveStage(CommonEvtStageChecker.Instance);

                SocialProfileLabelDefaults defaults = SocialProfileRuntimeState.Get(__instance);
                Text primaryTitle = __instance.school?.GetComponent<Text>();
                Text secondaryTitle = __instance.classname?.GetComponent<Text>();

                // DetailSocialView 会复用同一实例切换 NPC，必须先恢复普通学生的标题和显隐。
                if (primaryTitle != null)
                    primaryTitle.text = string.IsNullOrEmpty(defaults.Primary) ? "学校：" : defaults.Primary;
                if (secondaryTitle != null)
                    secondaryTitle.text = string.IsNullOrEmpty(defaults.Secondary) ? "班级：" : defaults.Secondary;
                __instance.school?.gameObject.SetActive(true);
                __instance.classname?.gameObject.SetActive(true);

                if (!customTarget) return true;

                if (primaryTitle != null)
                    primaryTitle.text = FormatTitle(SocialRoleProfileCodec.ResolvePrimaryLabel(data, stage));
                if (secondaryTitle != null)
                    secondaryTitle.text = FormatTitle(SocialRoleProfileCodec.ResolveSecondaryLabel(data, stage));

                if (__instance.txt_rolename != null) __instance.txt_rolename.text = role.Name;
                if (__instance.txt_birthday != null) __instance.txt_birthday.text = SafeBirthday(role);
                if (__instance.txt_school != null) __instance.txt_school.text = EmptyAsDash(stage?.PrimaryValue);
                if (__instance.txt_classname != null) __instance.txt_classname.text = EmptyAsDash(stage?.SecondaryValue);
                __instance.icon_role?.SetTextureUrl(person.GetFullIcon());

                if (__instance.profiles_special != null)
                    __instance.profiles_special.gameObject.SetActive(false);
                if (__instance.group_profile != null)
                    __instance.group_profile.SetSizeY(340f);
                RefreshStudyRank(__instance, role, stage?.ShowStudyRank == true);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[SocialRoleProfileRuntime] {e}");
                if (!customTarget) return true;

                // 自定义目标不能回退原 RefreshProfile；短数组可能使原方法再次越界。
                RenderSafeFallback(__instance, role, person, data, stage);
                return false;
            }
        }

        private static bool TryResolveCustomTarget(
            DetailSocialView view,
            out Role role,
            out PersonCfg person,
            out SocialRoleProfileData data)
        {
            role = null;
            person = null;
            data = null;
            try
            {
                role = Traverse.Create(view).Field("role").GetValue<Role>();
                return role != null && Cfg.PersonCfgMap != null
                    && Cfg.PersonCfgMap.TryGetValue(role.id, out person)
                    && SocialRoleProfileUtil.IsSocialRole(person)
                    && SocialRoleProfileCodec.TryRead(person, out data)
                    && data.IsCustom;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"[SocialRoleProfileResolve] {e}");
                return false;
            }
        }

        private static void RenderSafeFallback(
            DetailSocialView view,
            Role role,
            PersonCfg person,
            SocialRoleProfileData data,
            ProfileStage stage)
        {
            try
            {
                Text primaryTitle = view.school?.GetComponent<Text>();
                Text secondaryTitle = view.classname?.GetComponent<Text>();
                if (primaryTitle != null)
                    primaryTitle.text = FormatTitle(SocialRoleProfileCodec.ResolvePrimaryLabel(data, stage));
                if (secondaryTitle != null)
                    secondaryTitle.text = FormatTitle(SocialRoleProfileCodec.ResolveSecondaryLabel(data, stage));
                if (view.txt_rolename != null) view.txt_rolename.text = role?.Name ?? person?.name ?? "—";
                if (view.txt_birthday != null) view.txt_birthday.text = role != null ? SafeBirthday(role) : "—";
                if (view.txt_school != null) view.txt_school.text = EmptyAsDash(stage?.PrimaryValue);
                if (view.txt_classname != null) view.txt_classname.text = EmptyAsDash(stage?.SecondaryValue);
                if (view.icon_studyrank != null) view.icon_studyrank.gameObject.SetActive(false);
                if (view.profiles_special != null) view.profiles_special.gameObject.SetActive(false);
            }
            catch (Exception fallbackError)
            {
                Plugin.Log?.LogError($"[SocialRoleProfileFallback] {fallbackError}");
            }
        }

        private static string FormatTitle(string value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "资料" : value.Trim();
            text = text.TrimEnd('：', ':');
            return text + "：";
        }

        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
        }

        private static string SafeBirthday(Role role)
        {
            try
            {
                return role.Birthday != null && role.Birthday.Count >= 2
                    ? Utils.ToBirthdayStr(role.Birthday)
                    : "—";
            }
            catch
            {
                return "—";
            }
        }

        private static void RefreshStudyRank(DetailSocialView view, Role role, bool show)
        {
            if (view.icon_studyrank == null) return;
            if (!show || Cfg.PersonGrowCfgMap == null
                || !Cfg.PersonGrowCfgMap.TryGetValue(role.id, out var grow)
                || grow.studyRank == null || grow.studyRank.Count == 0)
            {
                view.icon_studyrank.gameObject.SetActive(false);
                return;
            }

            int index = Mathf.Clamp(role.GradeState, 0, grow.studyRank.Count - 1);
            int rankId = grow.studyRank[index];
            if (Cfg.ScoreRankCfgMap == null || !Cfg.ScoreRankCfgMap.TryGetValue(rankId, out var rank))
            {
                view.icon_studyrank.gameObject.SetActive(false);
                return;
            }

            view.icon_studyrank.gameObject.SetActive(true);
            view.icon_studyrank.SetAtlasUrl(rank.icon);
        }
    }

    /// <summary>
    /// UIMgr.Init 发生在原版配置与启用 Mod 加载完成之后。若此日志出现，说明即使
    /// BepInEx 插件组件已在首轮场景清理中销毁，玩家端资料补丁仍然保持安装状态。
    /// </summary>
    [HarmonyPatch(typeof(UIMgr), "Init")]
    internal static class SocialRoleRuntimeReadyPatch
    {
        private static bool logged;

        private static void Postfix()
        {
            if (logged) return;
            logged = true;
            Plugin.Log?.LogInfo("[Lifecycle] Social Role Runtime 已跨过启动场景，资料补丁保持有效。");
        }
    }
}
