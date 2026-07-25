using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 原生人物页自带 PersonGrowCfg.examRank，但它只适合有 PersonCfg 的剧情人物，
    /// 不是普通考试同学的选择入口。保留高级字段，同时把标题和按钮改成明确的分流提示；
    /// 不再弹出内部字段长说明。
    /// </summary>
    internal static class PersonExamRankRedirectUtil
    {
        private sealed class State
        {
            internal object Button;
        }

        private static readonly ConditionalWeakTable<ModPersonEditView, State> States =
            new ConditionalWeakTable<ModPersonEditView, State>();

        internal static bool Configure(ModPersonEditView view)
        {
            if (view?.p_examrank == null || view.btn_examrank == null) return false;

            State state = States.GetValue(view, _ => new State());
            if (ReferenceEquals(state.Button, view.btn_examrank)) return false;

            Text title = view.p_examrank.Find("_txt")?.GetComponent<Text>();
            if (title != null) title.text = "剧情人物排名（高级）";

            Text buttonText = view.p_examrank.Find("btn_examrank/txt_btn")?.GetComponent<Text>();
            if (buttonText != null) buttonText.text = "同学入口";

            view.btn_examrank.AddClick(ShowClassmateEntryHint);
            state.Button = view.btn_examrank;
            return true;
        }

        private static void ShowClassmateEntryHint()
        {
            ToastHelper.Toast("普通考试榜同学请返回配置列表，打开“考试同学（对应年级）”。");
        }
    }

    [HarmonyPatch(typeof(ModPersonEditView), "InitUI")]
    internal static class PersonExamRankRedirectPatch
    {
        private static void Postfix(ModPersonEditView __instance)
        {
            try
            {
                if (PersonExamRankRedirectUtil.Configure(__instance))
                    Plugin.Log.LogInfo("[ClassmateCfgEditor] 人物页考试字段已改为高级入口提示。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ClassmateCfgEditor] 人物页入口提示初始化失败: {e}");
            }
        }
    }
}
