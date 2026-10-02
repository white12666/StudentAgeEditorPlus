using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    internal sealed class EditorAudioViewLifetime : MonoBehaviour
    {
        private BaseView view;
        private bool entered;
        internal void Bind(BaseView owner)
        {
            view = owner;
            Enter();
        }
        private void Enter()
        {
            if (entered || view == null || view.viewState != ViewState.Opened) return;
            entered = true;
            EditorAudioRuntime.Enter(view);
        }
        private void OnEnable() { Enter(); }
        private void LateUpdate() { if (entered) EditorAudioRuntime.ApplyMute(); }
        private void OnDisable()
        {
            if (!entered) return;
            entered = false;
            EditorAudioRuntime.Leave(view);
        }
    }

    [HarmonyPatch]
    internal static class EditorAudioViewPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in new[] { typeof(ModPageUploadView), typeof(ModNormalEditView),
                typeof(ModPersonEditView), typeof(ModEvtEditView), typeof(ModEndingEditView) })
                yield return AccessTools.Method(type, "OnOpen");
        }

        private static void Postfix(BaseView __instance)
        {
            if (__instance.gameObject == null || __instance.viewState != ViewState.Opened) return;
            try
            {
                var lifetime = __instance.gameObject.GetComponent<EditorAudioViewLifetime>()
                    ?? __instance.gameObject.AddComponent<EditorAudioViewLifetime>();
                lifetime.Bind(__instance);
                if (__instance is ModPageUploadView project)
                {
                    AddBeforeReturn(project);
                    return;
                }
                var save = AccessTools.Field(__instance.GetType(), "btn_save")?.GetValue(__instance) as UIButton;
                if (save?.gameObject == null) return;
                Transform parent = save.gameObject.transform.parent;
                if (parent.Find("EditorPlus_BgmButton") != null) return;
                Font font = save.gameObject.GetComponentInChildren<Text>(true)?.font;
                if (__instance is ModEvtEditView || __instance is ModEndingEditView)
                {
                    AddBesideSave(save.gameObject, __instance.transform, font);
                    return;
                }
                var button = EditorAudioButton.Create(parent, __instance.transform, font);
                button.transform.SetSiblingIndex(save.gameObject.transform.GetSiblingIndex());
                if (parent.GetComponent<LayoutGroup>() == null)
                {
                    // 原版底栏为手工定位；紧靠操作区左侧，不移动已有保存/退出按钮。
                    var rt = (RectTransform)button.transform;
                    rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
                    rt.pivot = new Vector2(1f, 0.5f);
                    rt.anchoredPosition = new Vector2(-8f, 0f);
                }
            }
            catch (Exception e) { Plugin.Log?.LogError("[EditorAudio.UI] " + e); }
        }

        private static void AddBeforeReturn(ModPageUploadView view)
        {
            if (view.btn_cancel?.gameObject == null) return;
            Transform anchor = view.btn_cancel.gameObject.transform;
            if (anchor.Find("EditorPlus_BgmButton") != null) return;
            Font font = anchor.GetComponentInChildren<Text>(true)?.font;
            // 不插入 group_btn 的横向布局，也不移动任何原按钮。
            // 作为“返回”的独立子控件向左伸出，跟随其位置及页面显隐。
            var button = EditorAudioButton.Create(anchor, view.transform, font, false, true);
            button.GetComponent<LayoutElement>().ignoreLayout = true;
            var rect = (RectTransform)button.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = new Vector2(-12f, 0f);
        }

        private static void AddBesideSave(GameObject save, RectTransform host, Font font)
        {
            var original = (RectTransform)save.transform;
            Transform parent = original.parent;
            int sibling = original.GetSiblingIndex();
            var canvas = parent.GetComponentInParent<Canvas>()?.rootCanvas;
            float units = canvas != null ? Mathf.Max(1f, 1f / Mathf.Max(0.1f, canvas.scaleFactor)) : 1f;
            float height = original.rect.height;
            var row = new GameObject("EditorPlus_SaveAudioRow", typeof(RectTransform),
                typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var rect = (RectTransform)row.transform;
            rect.SetParent(parent, false);
            rect.SetSiblingIndex(sibling);
            rect.anchorMin = original.anchorMin;
            rect.anchorMax = original.anchorMax;
            rect.pivot = original.pivot;
            rect.sizeDelta = original.sizeDelta;
            var rowSize = row.GetComponent<LayoutElement>();
            rowSize.preferredHeight = rowSize.minHeight = height;
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 8f * units;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            // 复用原“保存”这一行的高度，而非增加右列高度，720p 不压住性别切换。
            var audio = EditorAudioButton.Create(rect, host, font, false, true);
            original.SetParent(rect, false);
            var saveSize = save.GetComponent<LayoutElement>() ?? save.AddComponent<LayoutElement>();
            saveSize.minWidth = 40f * units;
            saveSize.flexibleWidth = 1f;
            saveSize.preferredHeight = height;
            audio.FitBeside(rect, saveSize);
        }
    }

    [HarmonyPatch(typeof(PreviewTalkView), "OnOpen")]
    internal static class EditorPreviewIndicatorPatch
    {
        private static void Postfix(PreviewTalkView __instance)
        {
            if (__instance.gameObject == null || __instance.viewState != ViewState.Opened) return;
            if (__instance.transform.Find("EditorPlus_BgmButton") != null) return;
            var button = EditorAudioButton.Create(__instance.transform, __instance.transform, null, true);
            var rt = (RectTransform)button.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = Vector2.one;
            rt.anchoredPosition = new Vector2(-18f, -18f);
        }
    }
}
