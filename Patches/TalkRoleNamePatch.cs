using System.Collections.Generic;
using Config;
using HarmonyLib;
using View.Evt;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 原版 GetRoleName 的列表重载会把空字符串继续传给单人物重载，而后者只用
    /// null 判断是否需要按人物 ID 取名。因此编辑器常见的 roleName="" 会意外
    /// 把预览和正式剧情中的姓名栏清空。空白值在配置语义上应等同于“未覆盖”。
    /// </summary>
    internal static class TalkRoleNameUtil
    {
        internal static string NormalizeOverride(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    [HarmonyPatch(
        typeof(NewTalkView), "GetRoleName",
        typeof(int), typeof(string), typeof(Dictionary<int, PersonCfg>))]
    internal static class TalkRoleNameSinglePatch
    {
        private static void Prefix(ref string _roleName)
        {
            _roleName = TalkRoleNameUtil.NormalizeOverride(_roleName);
        }
    }

    [HarmonyPatch(
        typeof(NewTalkView), "GetRoleName",
        typeof(List<int>), typeof(string), typeof(Dictionary<int, PersonCfg>))]
    internal static class TalkRoleNameListPatch
    {
        private static void Prefix(ref string _roleName)
        {
            _roleName = TalkRoleNameUtil.NormalizeOverride(_roleName);
        }
    }
}
