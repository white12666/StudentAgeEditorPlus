using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GenUI.Main;
using HarmonyLib;
using Sdk;
using Sdk.PlatformAPI;
using StudentAgeHarness;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.HarnessScenarios
{
    /// <summary>
    /// 创作 Mod 页 BGM 按钮布局回归（旧 EditorAudioLayoutGameTests/ProjectLayoutTests.cs 的 31 项检查）。
    /// BGM 挂在“返回”下面、向左伸出，不进入 group_btn 的横向布局；显示、切换、重开都不改变原按钮的位置和尺寸。
    /// 检查数量 = 13 项固定检查 + 3 轮布局比较 × (1 + group_btn 原有子控件数)；原有 5 个子控件时为 31。
    /// </summary>
    [HarnessScenario("editorplus-bgm-layout",
        Description = "创作 Mod 页：BGM 按钮位于“返回”左侧且不改变原按钮行布局，点击只切换静音",
        Tags = new[] { "editorplus" },
        RequiresPlugins = new[] { EditorPlusDriver.PluginGuid },
        TimeoutSec = 240,
        Order = 10)]
    public sealed class BgmLayoutScenario : IHarnessScenario
    {
        private const string ExtensionKey = "editorplus.bgm-layout";
        private const string EvidenceFolder = "editorplus-bgm-layout";
        private const string BgmName = "EditorPlus_BgmButton";
        private const int CreativeTab = 20003;
        private const int MusicChannel = 1;
        private const int FixedChecks = 13;
        private const string ProjectTitle = "BGM 布局回归";
        private const string PackageId = "EditorPlusLayoutTest";

        private Type audioRuntime;
        private object audioPolicy;
        private bool? initialQuiet;
        private string project;
        private ModView shell;
        private ModPageUploadView page;
        private Button bgm;
        private bool? initialMute;
        private List<KeyValuePair<Transform, Rect>> native;
        private List<object> nativeLayout;
        private Rect icon;
        private Rect back;
        private string layoutFile;

        private int ExpectedChecks => native == null ? -1 : FixedChecks + 3 * (1 + native.Count);

        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            var d = new EditorPlusDriver(ctx);

            yield return HarnessStep.Routine("main_menu", () => ctx.Game.EnterMainMenu())
                .Then(() => AudioMgr.Ins != null, 30f, "AudioMgr 没有就绪。");

            yield return HarnessStep.Do("prepare", () =>
            {
                audioRuntime = EditorPlusDriver.PatchType("EditorAudioRuntime");
                audioPolicy = EditorPlusDriver.Field<object>(audioRuntime, null, "Policy");
                initialQuiet = Quiet;
                project = d.CreateProject("bgm-layout", out _);
            });

            // 真实入口：主菜单的创意工坊/Mod 按钮。
            yield return HarnessStep.Routine("open_mod_view", () => ctx.Game.ClickViewButton("EntryView", "btn_workshop", 15f))
                .Then(() => Shell != null, 15f, "点击主菜单 Mod 入口（btn_workshop）后 ModView 没有打开。");

            // 原版 OnGetMods 的异步回调会重新显示订阅页；等它结束再切页，避免订阅列表盖到创作表单上。
            yield return HarnessStep.Routine("subscriptions_loaded", () => SubscriptionsLoaded(d))
                .After(() => Shell != null && Shell.group_page.gameObject.activeSelf, 30f,
                    "订阅列表一直没有加载完（ModView.group_page 未显示）。");

            yield return HarnessStep.Routine("open_creative_tab", () =>
                {
                    var cell = shell.tabgroup_top.FindCell(CreativeTab) as Cell_DetailTopItemUI
                        ?? throw new InvalidOperationException("ModView 顶部找不到“创作 Mod”页签（" + CreativeTab + "）。");
                    return d.Click(cell.btn_click.btn, 15f);
                })
                .Then(() => UploadView != null && UploadView.viewState == ViewState.Opened, 15f,
                    "点击“创作 Mod”页签后 ModPageUploadView 没有打开。");

            yield return HarnessStep.Routine("select_project", SelectProject)
                .Then(() => BgmButtons().Any(), 10f, "选中作品后创作页没有出现 " + BgmName + "。");

            yield return HarnessStep.Do("bgm_structure", () =>
            {
                bgm = BgmButtons().Single();
                initialMute = MusicMuted;
                d.Check("使用真实的“创作 Mod”子页（hasBecomeChildView）", () => page.hasBecomeChildView);
                d.Check("BGM 挂在“返回”按钮下面，而不是 group_btn 横向布局里",
                    () => bgm.transform.parent == page.btn_cancel.gameObject.transform && bgm.transform.parent != page.group_btn,
                    () => "父节点 " + HarnessUi.PathOf(bgm.transform.parent));
                d.Check("BGM 设置了 LayoutElement.ignoreLayout", () => bgm.GetComponent<LayoutElement>().ignoreLayout);
            });

            yield return HarnessStep.Routine("native_baseline", () => NativeBaseline(ctx, d));

            yield return HarnessStep.Routine("bgm_shown", () => BgmShown(ctx, d));

            yield return HarnessStep.Routine("bgm_toggle", () => BgmToggle(ctx, d));

            yield return HarnessStep.Routine("bgm_restore", () => BgmRestore(d));

            yield return HarnessStep.Routine("return_hides_bgm", () => ReturnHidesBgm(d));

            yield return HarnessStep.Routine("reopen_project", () => ReopenProject(ctx, d));

            yield return HarnessStep.Do("check_count", () =>
                ctx.Assert(d.Checks == ExpectedChecks,
                    "检查数量不符：执行了 " + d.Checks + " 项，按 group_btn 原有 " + native.Count + " 个子控件应为 " + ExpectedChecks + " 项。"));

            yield return HarnessStep.Do("restore_audio", () =>
            {
                if (audioPolicy == null || initialQuiet == null) return;
                if (Quiet != initialQuiet.Value) EditorPlusDriver.CallStatic(audioRuntime, "Toggle");
                ctx.Assert(Quiet == initialQuiet.Value, "没能把 EditorPlus 的编辑器静音偏好恢复为 " + initialQuiet.Value + "。");
            }).Cleanup();

            yield return HarnessStep.Do("close_mod_view", () =>
            {
                ModView view = Shell;
                if (view != null) view.CloseView();
            }).Then(() => !UIMgr.IsViewOpened<ModView>(), 15f, "ModView 没有关闭。").Cleanup();

            // 这一步在界面关闭之后执行，只能使用之前保存下来的普通数据，不能再访问 Unity 对象。
            yield return HarnessStep.Do("record", () => ctx.Report.SetExtension(ExtensionKey, new
            {
                checks = d.Checks,
                expectedChecks = ExpectedChecks,
                complete = native != null && d.Checks == ExpectedChecks,
                editorPlusVersion = SafeVersion(),
                screen = Screen.width + "x" + Screen.height,
                groupButtonChildren = native?.Count,
                nativeLayout,
                bgm = native == null ? null : EditorPlusDriver.Describe(icon),
                returnButton = native == null ? null : EditorPlusDriver.Describe(back),
                initialMute,
                initialQuiet,
                layoutFile,
                passed = d.Passed.ToArray()
            })).Cleanup();
        }

        // ---- 状态 ----

        private static ModView Shell => UIMgr.GetView<ModView>(false) as ModView;

        private ModPageUploadView UploadView => shell == null ? null : EditorPlusDriver.Field<ModPageUploadView>(typeof(ModView), shell, "uploadView");

        private bool Quiet => EditorPlusDriver.Property<bool>(audioPolicy, "Quiet");

        private static bool MusicMuted => AudioMgr.Ins.GetChannel(MusicChannel).source.mute;

        private IEnumerable<Button> BgmButtons() =>
            page == null ? Enumerable.Empty<Button>() : page.gameObject.GetComponentsInChildren<Button>(true).Where(b => b.name == BgmName);

        private static string SafeVersion()
        {
            try { return EditorPlusDriver.AssemblyVersion; }
            catch { return null; }
        }

        // ---- 步骤 ----

        private IEnumerator SubscriptionsLoaded(EditorPlusDriver d)
        {
            shell = Shell;
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("切换页签前订阅页已加载完", () => shell.group_page.gameObject.activeSelf);
        }

        private IEnumerator SelectProject()
        {
            page = UploadView;
            yield return EditorPlusDriver.Pause(0.5f);
            OpenProject();
            yield return EditorPlusDriver.Pause(0.8f);
        }

        /// <summary>
        /// 原版只能通过系统文件夹对话框（“打开”）或输入框新建（“新建”）选作品；系统对话框无法自动化，
        /// 所以和旧测试一样直接调用 ModPageUploadView.Select 打开临时作品。
        /// </summary>
        private void OpenProject()
        {
            var manifest = new ModManifestData { title = ProjectTitle, metadata = new ModMetadata { packageId = PackageId } };
            EditorPlusDriver.Invoke(AccessTools.Method(typeof(ModPageUploadView), "Select", new[] { typeof(ModManifestData), typeof(string) }),
                page, new object[] { manifest, project });
        }

        private IEnumerator NativeBaseline(HarnessContext ctx, EditorPlusDriver d)
        {
            bgm.gameObject.SetActive(false);
            yield return EditorPlusDriver.Pause(0.3f);
            Canvas.ForceUpdateCanvases();
            native = page.group_btn.Cast<Transform>().Select(t => new KeyValuePair<Transform, Rect>(t, EditorPlusDriver.ScreenRect(t))).ToList();
            ctx.Assert(native.Count > 0, "group_btn 没有任何子控件，无法建立布局基线。");
            nativeLayout = native.Select(pair => (object)new { name = pair.Key.name, rect = EditorPlusDriver.Describe(pair.Value) }).ToList();
            layoutFile = d.WriteEvidence(EvidenceFolder, "native-layout.txt",
                native.Select(pair => pair.Key.name + " " + EditorPlusDriver.Format(pair.Value)));
            yield return ctx.Capture("native_row_without_bgm");
            yield return EditorPlusDriver.Pause(0.3f);
        }

        private IEnumerator BgmShown(HarnessContext ctx, EditorPlusDriver d)
        {
            bgm.gameObject.SetActive(true);
            yield return EditorPlusDriver.Pause(0.4f);
            SameLayout(d, "显示 BGM 后");
            icon = EditorPlusDriver.ScreenRect(bgm.transform);
            back = EditorPlusDriver.ScreenRect(page.btn_cancel.gameObject.transform);
            d.Check("BGM 严格位于“返回”左侧", () => icon.xMax < back.xMin,
                () => "BGM " + EditorPlusDriver.Format(icon) + "，返回 " + EditorPlusDriver.Format(back));
            d.Check("BGM 与“返回”垂直居中对齐（误差小于 0.1 像素）", () => Math.Abs(icon.center.y - back.center.y) < 0.1f,
                () => "BGM 中心 y=" + icon.center.y + "，返回中心 y=" + back.center.y);
            d.Check("BGM 完整位于屏幕内",
                () => icon.xMin >= 0 && icon.yMin >= 0 && icon.xMax <= Screen.width && icon.yMax <= Screen.height,
                () => EditorPlusDriver.Format(icon) + "，屏幕 " + Screen.width + "x" + Screen.height);
            d.Check("BGM 点击区不小于 40×40 像素", () => icon.width >= 40 && icon.height >= 40, () => EditorPlusDriver.Format(icon));
            yield return ctx.Capture("bgm_left_of_return");
            yield return EditorPlusDriver.Pause(0.3f);
        }

        private IEnumerator BgmToggle(HarnessContext ctx, EditorPlusDriver d)
        {
            yield return d.Click(bgm);
            yield return EditorPlusDriver.Pause(0.4f);
            d.Check("点击 BGM 切换了音乐声道静音", () => MusicMuted != initialMute.Value,
                () => "mute=" + MusicMuted + "，初始 " + initialMute);
            d.Check("点击 BGM 没有触发“返回”（作品信息仍显示）", () => page.group_info.gameObject.activeInHierarchy);
            SameLayout(d, "切换静音后");
            yield return ctx.Capture("bgm_toggled");
            yield return EditorPlusDriver.Pause(0.3f);
        }

        private IEnumerator BgmRestore(EditorPlusDriver d)
        {
            yield return d.Click(bgm);
            yield return EditorPlusDriver.Pause(0.4f);
            d.Check("再次点击 BGM 恢复原来的静音状态", () => MusicMuted == initialMute.Value,
                () => "mute=" + MusicMuted + "，初始 " + initialMute);
        }

        private IEnumerator ReturnHidesBgm(EditorPlusDriver d)
        {
            yield return d.Click(page.btn_cancel.btn);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("点击“返回”后 BGM 随作品控件一起隐藏", () => !bgm.gameObject.activeInHierarchy);
        }

        private IEnumerator ReopenProject(HarnessContext ctx, EditorPlusDriver d)
        {
            OpenProject();
            yield return EditorPlusDriver.Pause(0.4f);
            d.Check("重新打开作品不会重复创建 BGM 按钮", () => BgmButtons().Count() == 1, () => BgmButtons().Count() + " 个");
            SameLayout(d, "重新打开作品后");
            yield return ctx.Capture("project_reopened");
            yield return EditorPlusDriver.Pause(0.3f);
        }

        /// <summary>group_btn 的子控件数量和每个原有子控件的屏幕矩形都与隐藏 BGM 时的基线一致（平方误差小于 0.01）。</summary>
        private void SameLayout(EditorPlusDriver d, string label)
        {
            Canvas.ForceUpdateCanvases();
            d.Check(label + "：group_btn 直接子控件数量不变（" + native.Count + "）", () => page.group_btn.childCount == native.Count,
                () => page.group_btn.childCount.ToString());
            foreach (KeyValuePair<Transform, Rect> item in native)
            {
                Transform child = item.Key;
                Rect expected = item.Value;
                d.Check(label + "：" + child.name + " 的位置和尺寸不变", () =>
                {
                    Rect actual = EditorPlusDriver.ScreenRect(child);
                    return (actual.position - expected.position).sqrMagnitude < 0.01f && (actual.size - expected.size).sqrMagnitude < 0.01f;
                }, () => EditorPlusDriver.Format(EditorPlusDriver.ScreenRect(child)) + "，基线 " + EditorPlusDriver.Format(expected));
            }
        }
    }
}
