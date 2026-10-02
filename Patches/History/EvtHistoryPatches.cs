using System;
using System.Collections.Generic;
using System.Reflection;
using Config;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    [HarmonyPatch(typeof(ModEvtEditView), "OnOpen")]
    internal static class EvtHistoryOpenPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ModEvtEditView __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            try { EvtEditorHistory.Attach(__instance); }
            catch (Exception e) { Plugin.Log?.LogError("[EditorHistory] 初始化失败：" + e); }
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "Select", typeof(TalkCfg))]
    internal static class EvtHistorySelectPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(ModEvtEditView __instance, TalkCfg _cfg, out string __state)
        {
            var history = EvtEditorHistory.Get(__instance);
            string before = null;
            history?.Safe(() => before = history.BeforeSelect(_cfg));
            __state = before;
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ModEvtEditView __instance, TalkCfg _cfg, string __state)
        {
            var history = EvtEditorHistory.Get(__instance);
            history?.Safe(() => history.AfterSelect(_cfg, __state));
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "OnEndEditTalk")]
    internal static class EvtHistoryBodySyncPatch
    {
        private static void Postfix(ModEvtEditView __instance, string _txt)
        {
            // 原版只在 Select 时给两种正文同时赋值。编辑完直接切换 CG 时，
            // 隐藏框还留着上次选中的旧正文；一旦失焦，旧内容就会覆盖新内容。
            __instance.inputex_talk?.SetTextWithoutNotify(_txt);
            __instance.inputex_talk_cg?.SetTextWithoutNotify(_txt);
        }
    }

    [HarmonyPatch]
    internal static class EvtHistoryActionPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "OnClickNew", "OnClickNext", "OnClickDelete", "OnClickName",
                "OnSelectBg", "OnSelectCg", "OnClickCGEnd", "OnEditOption", "OnDelOption" })
                yield return AccessTools.Method(typeof(ModEvtEditView), name);
        }

        [HarmonyPriority(Priority.First)]
        private static void Prefix(ModEvtEditView __instance)
        {
            var history = EvtEditorHistory.Get(__instance);
            history?.Safe(history.BeginAction);
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ModEvtEditView __instance)
        {
            var history = EvtEditorHistory.Get(__instance);
            history?.Safe(history.EndAction);
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "OnClickSave")]
    internal static class EvtHistorySavePatch
    {
        [HarmonyPriority(Priority.First + 100)]
        private static void Prefix(ModEvtEditView __instance)
        {
            var history = EvtEditorHistory.Get(__instance);
            history?.Safe(history.BeginSave);
        }

        [HarmonyPriority(Priority.Last - 100)]
        private static void Postfix(ModEvtEditView __instance, bool __runOriginal)
        {
            var history = EvtEditorHistory.Get(__instance);
            history?.Safe(() => history.EndSave(__runOriginal));
        }
    }

    [HarmonyPatch]
    internal static class EvtHistoryInputKeyPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TMP_InputField), "KeyPressed");
            yield return AccessTools.Method(typeof(InputField), "KeyPressed");
        }

        private static bool Prefix(Component __instance, Event evt)
        {
            var input = __instance.GetComponent<EvtHistoryInput>();
            bool ctrl = (evt.modifiers & EventModifiers.Control) != 0;
            bool shift = (evt.modifiers & EventModifiers.Shift) != 0;
            bool alt = (evt.modifiers & EventModifiers.Alt) != 0;
            if (input?.Owner == null)
            {
                // 搜索框不是剧情历史的一部分，但 Ctrl+S 仍是本页的保存键。
                if (!ctrl || evt.keyCode != KeyCode.S) return true;
                var owner = __instance.GetComponentInParent<EvtEditorHistory>();
                bool saved = false;
                owner?.Safe(() => saved = owner.Shortcut(evt.keyCode, ctrl, shift, alt));
                return !saved;
            }
            if (!input.IsFocused) return true;
            if ((ctrl && (evt.keyCode == KeyCode.A || evt.keyCode == KeyCode.X || evt.keyCode == KeyCode.V))
                || evt.keyCode == KeyCode.LeftArrow || evt.keyCode == KeyCode.RightArrow
                || evt.keyCode == KeyCode.UpArrow || evt.keyCode == KeyCode.DownArrow
                || evt.keyCode == KeyCode.Home || evt.keyCode == KeyCode.End)
                input.Owner.BreakInputGroup();
            // 跳过控件默认行为时返回默认 EditState.Continue（枚举值 0）。
            bool handled = false;
            input.Owner.Safe(() => handled = input.Owner.Shortcut(evt.keyCode, ctrl, shift, alt));
            return !handled;
        }
    }
}
