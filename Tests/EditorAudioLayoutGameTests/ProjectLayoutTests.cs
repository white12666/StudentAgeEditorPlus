using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using Sdk;
using Sdk.PlatformAPI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using View.Main;
using View.Mod;

namespace StudentAgeEditorPlus.LayoutTests
{
    [BepInPlugin("com.studentage.editorplus.layouttests", "EditorPlus Layout Tests", "1.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        internal static string Output;
        private void Awake()
        {
            string arg = Environment.GetCommandLineArgs().FirstOrDefault(
                x => x.StartsWith("--saep-layout-qa=", StringComparison.Ordinal));
            if (arg == null) return;
            Output = arg.Substring("--saep-layout-qa=".Length);
            new Harmony("com.studentage.editorplus.layouttests").Patch(
                AccessTools.Method(typeof(UIMgr), "Init", Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(EnsureHost)));
            EnsureHost();
        }
        private static void EnsureHost()
        {
            if (FindObjectOfType<ProjectLayoutTests>() != null) return;
            var go = new GameObject("EditorPlusProjectLayoutTests");
            DontDestroyOnLoad(go);
            go.AddComponent<ProjectLayoutTests>();
        }
    }

    public class ProjectLayoutTests : MonoBehaviour
    {
        private IEnumerator routine;
        private float next;
        private float deadline;
        private ModView shell;
        private ModPageUploadView page;
        private Button bgm;
        private bool initialMute;
        private bool initialBackground;
        private Type audioRuntime;
        private object audioPolicy;
        private bool initialQuiet;
        private readonly List<string> results = new List<string>();
        private int capture;

        private void Start()
        {
            Directory.CreateDirectory(Plugin.Output);
            initialBackground = Application.runInBackground;
            Application.runInBackground = true;
            deadline = Time.realtimeSinceStartup + 150f;
            routine = Run();
        }

        private void Update()
        {
            if (routine == null || Time.realtimeSinceStartup < next) return;
            try
            {
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Layout test timeout");
                if (!routine.MoveNext()) { Finish(null); return; }
                next = Time.realtimeSinceStartup + (routine.Current is float seconds ? seconds : 0f);
            }
            catch (Exception e) { Finish(e); }
        }

        private void Check(string name, bool pass)
        {
            string text = (pass ? "PASS " : "FAIL ") + name;
            results.Add(text);
            File.AppendAllText(Path.Combine(Plugin.Output, "steps.txt"), text + Environment.NewLine);
            if (!pass) throw new Exception(name);
        }

        private void Capture(string name)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(Plugin.Output,
                (++capture).ToString("00") + "_" + name + ".png"));
        }

        private static Rect ScreenRect(Transform transform)
        {
            var corners = new Vector3[4];
            ((RectTransform)transform).GetWorldCorners(corners);
            Canvas canvas = transform.GetComponentInParent<Canvas>();
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
        }

        private Dictionary<Transform, Rect> Snapshot()
        {
            return page.group_btn.Cast<Transform>().ToDictionary(t => t, ScreenRect);
        }

        private void SameLayout(Dictionary<Transform, Rect> expected, string step)
        {
            Canvas.ForceUpdateCanvases();
            Check(step + ": original child count unchanged", page.group_btn.childCount == expected.Count);
            foreach (var item in expected)
            {
                Rect actual = ScreenRect(item.Key);
                Check(step + ": " + item.Key.name + " position/size unchanged",
                    (actual.position - item.Value.position).sqrMagnitude < 0.01f
                    && (actual.size - item.Value.size).sqrMagnitude < 0.01f);
            }
        }

        private static void Click(Button button)
        {
            if (button == null || !button.interactable || !button.gameObject.activeInHierarchy)
                throw new Exception("Button is not clickable");
            var evt = new PointerEventData(EventSystem.current)
                { button = PointerEventData.InputButton.Left, position = ScreenRect(button.transform).center };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(evt, hits);
            if (hits.Count == 0 || ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject) != button.gameObject)
                throw new Exception("BGM click is intercepted by another control");
            ExecuteEvents.Execute(button.gameObject, evt, ExecuteEvents.pointerClickHandler);
        }

        private IEnumerator Run()
        {
            while (!Ready()) yield return 0.2f;
            yield return 1f;
            audioRuntime = AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "StudentAgeEditorPlus")
                .GetType("StudentAgeEditorPlus.Patches.EditorAudioRuntime", true);
            audioPolicy = AccessTools.Field(audioRuntime, "Policy").GetValue(null);
            initialQuiet = (bool)AccessTools.Property(audioPolicy.GetType(), "Quiet").GetValue(audioPolicy);
            string project = Path.Combine(Plugin.Output, "project");
            Directory.CreateDirectory(Path.Combine(project, "Cfgs", LocalizationMgr.Lang));
            UIMgr.OpenView<ModView>();
            while (!UIMgr.IsViewOpened<ModView>()) yield return 0.1f;
            shell = (ModView)UIMgr.GetView<ModView>();
            // 原版 OnGetMods 的异步回调会重新显示订阅页；等它结束再切页，
            // 避免测试用瞬时切页使订阅列表盖到创作表单上。
            while (!shell.group_page.gameObject.activeSelf) yield return 0.1f;
            yield return 0.3f;
            Check("Subscription page finished loading before tab switch", shell.group_page.gameObject.activeSelf);
            // 使用真实顶层 Mod 窗口与“创作 Mod”子页，不能拿独立全屏子页冒充。
            shell.tabgroup_top.Select(20003);
            while ((page = Traverse.Create(shell).Field("uploadView").GetValue<ModPageUploadView>()) == null
                || page.viewState != ViewState.Opened) yield return 0.1f;
            yield return 0.5f;
            Traverse.Create(page).Method("Select", new[] { typeof(ModManifestData), typeof(string) })
                .GetValue(new ModManifestData { title = "BGM 布局回归",
                    metadata = new ModMetadata { packageId = "EditorPlusLayoutTest" } }, project);
            yield return 0.8f;
            bgm = page.gameObject.GetComponentsInChildren<Button>(true)
                .Single(b => b.name == "EditorPlus_BgmButton");
            initialMute = AudioMgr.Ins.GetChannel(1).source.mute;
            Check("Uses actual creative Mod child page", page.hasBecomeChildView);
            Check("BGM is parented to return, not the layout group",
                bgm.transform.parent == page.btn_cancel.gameObject.transform
                && bgm.transform.parent != page.group_btn);
            Check("BGM ignores automatic layout", bgm.GetComponent<LayoutElement>().ignoreLayout);
            bgm.gameObject.SetActive(false);
            yield return 0.3f;
            Canvas.ForceUpdateCanvases();
            var native = Snapshot();
            File.WriteAllLines(Path.Combine(Plugin.Output, "native-layout.txt"),
                native.Select(pair => pair.Key.name + " " + pair.Value));
            Capture("native_row_without_bgm");
            yield return 0.3f;
            bgm.gameObject.SetActive(true);
            yield return 0.4f;
            SameLayout(native, "BGM shown");
            Rect icon = ScreenRect(bgm.transform);
            Rect back = ScreenRect(page.btn_cancel.gameObject.transform);
            Check("BGM lies strictly left of return", icon.xMax < back.xMin);
            Check("BGM is vertically centered on return", Math.Abs(icon.center.y - back.center.y) < 0.1f);
            Check("BGM stays inside screen", icon.xMin >= 0 && icon.yMin >= 0
                && icon.xMax <= Screen.width && icon.yMax <= Screen.height);
            Check("BGM has at least a 40px click target", icon.width >= 40 && icon.height >= 40);
            Capture("bgm_left_of_return");
            yield return 0.3f;
            Click(bgm);
            yield return 0.4f;
            Check("BGM click switches mute", AudioMgr.Ins.GetChannel(1).source.mute != initialMute);
            Check("BGM click does not trigger Return", page.group_info.gameObject.activeInHierarchy);
            SameLayout(native, "After toggle");
            Capture("bgm_toggled");
            yield return 0.3f;
            Click(bgm);
            yield return 0.4f;
            Check("Second click restores mute preference", AudioMgr.Ins.GetChannel(1).source.mute == initialMute);
            Traverse.Create(page).Method("OnClickCancel").GetValue();
            yield return 0.3f;
            Check("Return hides BGM with project controls", !bgm.gameObject.activeInHierarchy);
            Traverse.Create(page).Method("Select", new[] { typeof(ModManifestData), typeof(string) })
                .GetValue(new ModManifestData { title = "BGM 布局回归",
                    metadata = new ModMetadata { packageId = "EditorPlusLayoutTest" } }, project);
            yield return 0.4f;
            Check("Reopening does not duplicate BGM",
                page.gameObject.GetComponentsInChildren<Button>(true).Count(b => b.name == "EditorPlus_BgmButton") == 1);
            SameLayout(native, "Project reopened");
            Capture("project_reopened");
            yield return 0.3f;
            File.WriteAllText(Path.Combine(Plugin.Output, "version.txt"),
                AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "StudentAgeEditorPlus")
                    .GetName().Version.ToString());
        }

        private static bool Ready()
        {
            try { return UIMgr.IsViewOpened<EntryView>() && AudioMgr.Ins != null; }
            catch { return false; }
        }

        private void Finish(Exception error)
        {
            routine = null;
            try
            {
                if (audioPolicy != null && (bool)AccessTools.Property(audioPolicy.GetType(), "Quiet")
                    .GetValue(audioPolicy) != initialQuiet)
                    AccessTools.Method(audioRuntime, "Toggle").Invoke(null, null);
                if (shell != null) shell.CloseView();
            }
            catch (Exception cleanup) { results.Add("CLEANUP " + cleanup); }
            Application.runInBackground = initialBackground;
            File.WriteAllText(Path.Combine(Plugin.Output, "result.txt"),
                (error == null ? "PASS" : "FAIL " + error) + Environment.NewLine + string.Join(Environment.NewLine, results));
        }
    }
}
