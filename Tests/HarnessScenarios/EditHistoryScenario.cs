using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Config;
using HarmonyLib;
using Newtonsoft.Json;
using Sdk;
using StudentAgeHarness;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Hint;
using View.Mod;

namespace StudentAgeEditorPlus.HarnessScenarios
{
    /// <summary>
    /// 原版剧情编辑页的输入和撤销回归（旧 EditorHistoryGameTests/HistoryGameTests.cs 的 49 项检查）。
    /// 每个阶段一个步骤，失败时报告直接指出是哪一阶段的第几条检查。
    /// 键盘输入沿用旧测试的 TMP_InputField/InputField.ProcessEvent：输入框读的是 IMGUI 事件，InputSystem 的虚拟按键到不了。
    /// </summary>
    [HarnessScenario("editorplus-edit-history",
        Description = "剧情编辑页：聚焦输入、Ctrl+Z/Y、撤销重做按钮、保存与脏状态、增删对话/选项、CG、快捷键隔离、剧情图保存、重开重置历史",
        Tags = new[] { "editorplus" },
        RequiresPlugins = new[] { EditorPlusDriver.PluginGuid },
        TimeoutSec = 300,
        Order = 20)]
    public sealed class EditHistoryScenario : IHarnessScenario
    {
        private const string ExtensionKey = "editorplus.edit-history";
        private const int ExpectedChecks = 49;
        private const int EventId = 1987652;
        private const int First = EventId * 1000 + 1;
        private const string Original = "这是一段不该丢失的台词。误删以后需要完整恢复。";
        private const string Continued = "连续输入";
        private const string LegacyDraft = "未写完的参数";
        private const string GraphText = "剧情图同步后的正文";

        private Type guardType;
        private Type historyType;
        private string project;
        private string talkFile;
        private ModEvtEditView view;
        private Component history;
        private MonoBehaviour graph;
        private string saved;
        private int cgId;
        private object saveRow;

        public IEnumerable<HarnessStep> Build(HarnessContext ctx)
        {
            var d = new EditorPlusDriver(ctx);

            yield return HarnessStep.Routine("main_menu", () => ctx.Game.EnterMainMenu());

            yield return HarnessStep.Do("prepare", () =>
            {
                guardType = EditorPlusDriver.PatchType("UnsavedEditGuard");
                historyType = EditorPlusDriver.PatchType("EvtEditorHistory");
                project = d.CreateProject("edit-history", out string cfgDirectory);
                talkFile = Path.Combine(cfgDirectory, "TalkCfg.json");
                File.WriteAllText(talkFile, JsonConvert.SerializeObject(new Dictionary<int, TalkCfg>
                {
                    [First] = new TalkCfg { id = First, content = Original },
                    [First + 1] = new TalkCfg { id = First + 1, content = "第二句台词" }
                }));
                File.WriteAllText(Path.Combine(cfgDirectory, "EvtCfg.json"), JsonConvert.SerializeObject(new Dictionary<int, EvtCfg>
                {
                    [EventId] = new EvtCfg { id = EventId, title = "撤销回归临时作品", talkId = new List<int> { First } }
                }));
            });

            yield return HarnessStep.Routine("open_editor", () => Open(d), 40f);
            yield return HarnessStep.Routine("header_layout", () => HeaderLayout(ctx, d));
            yield return HarnessStep.Routine("save_row_layout", () => SaveRowLayout(ctx));
            yield return HarnessStep.Routine("focused_typing", () => FocusedTyping(ctx, d));
            yield return HarnessStep.Routine("merge_and_buttons", () => MergeAndButtons(d));
            yield return HarnessStep.Routine("save_shortcut", () => SaveShortcut(d));
            yield return HarnessStep.Routine("legacy_input_field", () => LegacyInputField(d));
            yield return HarnessStep.Routine("delete_add_option", () => DeleteAddOption(d));
            yield return HarnessStep.Routine("cg_text", () => CgText(d));
            yield return HarnessStep.Routine("blocked_save", () => BlockedSave(d));
            yield return HarnessStep.Routine("modal_isolation", () => ModalIsolation(d))
                .Then(() => !UIMgr.IsViewOpened<CommonComfirmView>(), 10f, "测试用确认框没有关闭。")
                .ExpectingModal();
            yield return HarnessStep.Routine("story_graph", () => StoryGraph(d));
            yield return HarnessStep.Routine("reopen_resets_history", () => ReopenResetsHistory(ctx, d), 60f);

            yield return HarnessStep.Do("check_count", () =>
                ctx.Assert(d.Checks == ExpectedChecks, "检查数量不符：执行了 " + d.Checks + " 项，应为 " + ExpectedChecks + " 项。"));

            yield return HarnessStep.Do("restore_save_block", () =>
            {
                SaveBlocker.Remove();
                ctx.Assert(!SaveBlocker.Installed, "没能撤掉 ModEvtEditView.OnClickSave 上的测试补丁。");
            }).Cleanup();

            yield return HarnessStep.Do("close_editor", () =>
                {
                    if (UIMgr.IsViewOpened<CommonComfirmView>()) UIMgr.CloseView<CommonComfirmView>();
                    if (graph != null)
                    {
                        try { EditorPlusDriver.Call(graph, "Close"); }
                        catch (Exception) { }
                    }
                    ModEvtEditView open = OpenView;
                    if (open != null) EditorPlusDriver.CallStatic(guardType, "CloseWithoutPrompt", open, true, false);
                })
                .Then(() => !UIMgr.IsViewOpened<ModEvtEditView>() && !UIMgr.IsViewOpened<CommonComfirmView>(), 15f,
                    "剧情编辑页或确认框没有关闭。")
                .ExpectingModal()
                .Cleanup();

            yield return HarnessStep.Do("record", () => ctx.Report.SetExtension(ExtensionKey, new
            {
                checks = d.Checks,
                expectedChecks = ExpectedChecks,
                complete = d.Checks == ExpectedChecks,
                editorPlusVersion = SafeVersion(),
                screen = Screen.width + "x" + Screen.height,
                language = LocalizationMgr.Lang,
                eventId = EventId,
                talkIds = new[] { First, First + 1 },
                cgId,
                savedLength = saved?.Length,
                saveRow,
                passed = d.Passed.ToArray()
            })).Cleanup();
        }

        // ---- 状态 ----

        private static ModEvtEditView OpenView => UIMgr.GetView<ModEvtEditView>(false) as ModEvtEditView;

        private TalkCfg Current => EditorPlusDriver.Field<TalkCfg>(typeof(ModEvtEditView), view, "curSelect");

        private List<TalkCfg> Talks => EditorPlusDriver.Field<List<TalkCfg>>(typeof(ModEvtEditView), view, "talkCfgs");

        private Dictionary<int, OptionCfg> Options => EditorPlusDriver.Field<Dictionary<int, OptionCfg>>(typeof(ModEvtEditView), view, "optionCfgs");

        private TMP_InputField Body => view.inputex_talk;

        private Button Undo => view.gameObject.GetComponentsInChildren<Button>(true).Single(b => b.name == "EditorPlus_Undo");

        private Button Redo => view.gameObject.GetComponentsInChildren<Button>(true).Single(b => b.name == "EditorPlus_Redo");

        private InputField Search => view.gameObject.GetComponentsInChildren<InputField>().Single(f => f.name == "EditorPlus_SearchBar");

        private bool Dirty()
        {
            object[] args = { view, null };
            return (bool)EditorPlusDriver.Invoke(AccessTools.Method(guardType, "TryGetUnsavedChanges"), null, args);
        }

        private void Move(bool redo) => EditorPlusDriver.Call(history, "Move", redo);

        private bool Shortcut(KeyCode key, bool control, bool shift, bool alt) =>
            (bool)EditorPlusDriver.Call(history, "Shortcut", key, control, shift, alt);

        private void Select(int id) => EditorPlusDriver.Call(view, "Select", Talks.Single(t => t.id == id));

        /// <summary>重新挂一次历史组件，清空撤销栈，让每一阶段从干净的历史开始（与旧测试相同）。</summary>
        private void ResetHistory() => EditorPlusDriver.CallStatic(historyType, "Attach", view);

        private string DiskText(int id) => JsonConvert.DeserializeObject<Dictionary<int, TalkCfg>>(File.ReadAllText(talkFile))[id].content;

        private static string SafeVersion()
        {
            try { return EditorPlusDriver.AssemblyVersion; }
            catch { return null; }
        }

        private static string Show(string text) => text == null ? "(null)" : "“" + text + "”（" + text.Length + " 字）";

        // ---- 阶段 ----

        /// <summary>剧情编辑页没有可脚本化的原版入口链路（需从配置表页逐层进入），和旧测试一样直接用 UIMgr 打开临时事件。</summary>
        private IEnumerator Open(EditorPlusDriver d)
        {
            UIMgr.OpenView<ModEvtEditView>(UILayerType.None, null, project, EventId, new List<int> { First });
            float deadline = Time.realtimeSinceStartup + 20f;
            while ((view = OpenView) == null || view.viewState != ViewState.Opened)
            {
                if (Time.realtimeSinceStartup > deadline) throw new InvalidOperationException("ModEvtEditView 在 20 秒内没有打开。");
                yield return null;
            }
            yield return EditorPlusDriver.Pause(0.6f);
            history = view.gameObject.GetComponent(historyType);
            d.Check("剧情编辑页挂上了 EvtEditorHistory", () => history != null);
            Select(First);
            yield return EditorPlusDriver.Pause(0.5f);
        }

        private IEnumerator HeaderLayout(HarnessContext ctx, EditorPlusDriver d)
        {
            d.Check("普通正文和 CG 正文都关闭了聚焦全选", () => !Body.onFocusSelectAll && !view.inputex_talk_cg.onFocusSelectAll);
            d.Check("刚打开时撤销、重做按钮都不可用", () => !Undo.interactable && !Redo.interactable);
            Rect add = EditorPlusDriver.ScreenRect(view.btn_new.transform);
            Rect undo = EditorPlusDriver.ScreenRect(Undo.transform);
            Rect redo = EditorPlusDriver.ScreenRect(Redo.transform);
            d.Check("新增、撤销、重做按钮互不重叠", () => add.xMax < undo.xMin && undo.xMax < redo.xMin,
                () => "新增 " + EditorPlusDriver.Format(add) + "，撤销 " + EditorPlusDriver.Format(undo) + "，重做 " + EditorPlusDriver.Format(redo));
            Rect list = EditorPlusDriver.ScreenRect(view.scroll_left.transform);
            d.Check("按钮行不超出左侧列表宽度", () => redo.xMax <= list.xMax + 1,
                () => "重做 xMax=" + redo.xMax + "，列表 xMax=" + list.xMax);
            Rect search = EditorPlusDriver.ScreenRect(Search.transform);
            d.Check("搜索栏位于按钮行下方", () => search.yMax <= undo.yMin + 1,
                () => "搜索栏 yMax=" + search.yMax + "，撤销 yMin=" + undo.yMin);
            yield return ctx.Capture("01_header");
            yield return EditorPlusDriver.Pause(0.2f);
        }

        /// <summary>
        /// 右列“保存”这一行同时放着 EditorPlus 的 BGM 按钮（0.4.32 在 1024x656 会把“保存”挤出屏幕）。
        /// 不属于旧测试的 49 项，单独断言：“保存”完整可见且至少占半行，BGM 不重叠，两者都能被真实点击命中。
        /// </summary>
        private IEnumerator SaveRowLayout(HarnessContext ctx)
        {
            Canvas.ForceUpdateCanvases();
            yield return EditorPlusDriver.Pause(0.2f);
            Button save = view.btn_save.btn;
            Transform row = save.transform.parent;
            Button bgm = row.GetComponentsInChildren<Button>(true).SingleOrDefault(b => b.name == "EditorPlus_BgmButton");
            ctx.Assert(row.name == "EditorPlus_SaveAudioRow" && bgm != null,
                "“保存”没有和 BGM 按钮放在同一行（父节点 " + HarnessUi.PathOf(row) + "）。");
            Rect saveRect = EditorPlusDriver.ScreenRect(save.transform);
            Rect rowRect = EditorPlusDriver.ScreenRect(row);
            Rect bgmRect = EditorPlusDriver.ScreenRect(bgm.transform);
            bool iconOnly = !bgm.GetComponentsInChildren<Text>(true).First(t => t.name == "Status").enabled;
            saveRow = new
            {
                row = EditorPlusDriver.Describe(rowRect),
                save = EditorPlusDriver.Describe(saveRect),
                bgm = EditorPlusDriver.Describe(bgmRect),
                iconOnly
            };
            string where = "：保存 " + EditorPlusDriver.Format(saveRect) + "，BGM " + EditorPlusDriver.Format(bgmRect) +
                "，行 " + EditorPlusDriver.Format(rowRect) + "，屏幕 " + Screen.width + "x" + Screen.height;
            ctx.Assert(OnScreen(saveRect), "“保存”超出了屏幕" + where);
            ctx.Assert(saveRect.xMin >= rowRect.xMin - 0.5f && saveRect.xMax <= rowRect.xMax + 0.5f, "“保存”超出了所在的行" + where);
            ctx.Assert(saveRect.width >= 40f && saveRect.width >= rowRect.width * 0.5f - 1f, "“保存”被挤得不到半行或不足 40 像素" + where);
            ctx.Assert(OnScreen(bgmRect) && bgmRect.xMax <= saveRect.xMin + 0.5f, "BGM 超出屏幕或与“保存”重叠" + where);
            ctx.Assert(ctx.Ui.IsReachable(save) && ctx.Ui.IsReachable(bgm), "“保存”或 BGM 的中心点不能被真实点击命中" + where);
            yield return ctx.Capture("save_row");
        }

        private static bool OnScreen(Rect rect) =>
            rect.xMin >= 0f && rect.yMin >= 0f && rect.xMax <= Screen.width && rect.yMax <= Screen.height;

        private IEnumerator FocusedTyping(HarnessContext ctx, EditorPlusDriver d)
        {
            yield return d.Click(Body);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("点击正文只放置光标，不全选段落",
                () => Body.isFocused && Body.selectionStringAnchorPosition == Body.selectionStringFocusPosition,
                () => "focused=" + Body.isFocused + "，选区 " + Body.selectionStringAnchorPosition + ".." + Body.selectionStringFocusPosition);
            EditorPlusDriver.Key(Body, KeyCode.None, EventModifiers.None, '好');
            yield return EditorPlusDriver.Pause(0.15f);
            d.Check("输入一个字不会覆盖原段落", () => Body.text.Length == Original.Length + 1, () => Show(Body.text));
            EditorPlusDriver.Key(Body, KeyCode.Z, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("聚焦状态下 Ctrl+Z 恢复原段落并保持焦点", () => Body.text == Original && Body.isFocused,
                () => Show(Body.text) + "，focused=" + Body.isFocused);
            EditorPlusDriver.Key(Body, KeyCode.Y, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("Ctrl+Y 重做刚输入的字", () => Body.text.Length == Original.Length + 1, () => Show(Body.text));
            EditorPlusDriver.Key(Body, KeyCode.Z, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(0.2f);
            EditorPlusDriver.Key(Body, KeyCode.A, EventModifiers.Control);
            EditorPlusDriver.Key(Body, KeyCode.Backspace);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("Ctrl+A 加退格真的删掉整段", () => Body.text == "", () => Show(Body.text));
            EditorPlusDriver.Key(Body, KeyCode.Z, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("不失焦也能用 Ctrl+Z 找回整段", () => Body.text == Original, () => Show(Body.text));
            EditorPlusDriver.Key(Body, KeyCode.Z, EventModifiers.Control | EventModifiers.Shift);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("Ctrl+Shift+Z 重做删除", () => Body.text == "", () => Show(Body.text));
            EditorPlusDriver.Key(Body, KeyCode.Z, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(0.2f);
            yield return ctx.Capture("02_recovered_paragraph");
            EditorPlusDriver.Commit();
            yield return EditorPlusDriver.Pause(0.3f);
            ResetHistory();
        }

        private IEnumerator MergeAndButtons(EditorPlusDriver d)
        {
            yield return d.Click(Body);
            yield return EditorPlusDriver.Pause(0.2f);
            EditorPlusDriver.Key(Body, KeyCode.End, EventModifiers.Control);
            foreach (char c in Continued) EditorPlusDriver.Key(Body, KeyCode.None, EventModifiers.None, c);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("连续输入追加在段落末尾", () => Body.text == Original + Continued, () => Show(Body.text));
            yield return d.Click(Undo);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("点击“撤销”一步撤掉整段连续输入", () => Body.text == Original, () => Show(Body.text));
            yield return d.Click(Redo);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("点击“重做”恢复连续输入", () => Body.text == Original + Continued, () => Show(Body.text));
            EditorPlusDriver.Commit();
            yield return EditorPlusDriver.Pause(0.2f);
            Select(First + 1);
            yield return EditorPlusDriver.Pause(0.3f);
            Move(false);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("切换选中的对话后撤销，回到被改的那一句并恢复正文", () => Current.id == First && Body.text == Original,
                () => "当前 " + Current?.id + "，正文 " + Show(Body.text));
            Move(true);
            yield return EditorPlusDriver.Pause(0.3f);
            EditorPlusDriver.Commit();
            Select(First);
            yield return EditorPlusDriver.Pause(0.3f);
            ResetHistory();
        }

        /// <summary>保存键必须先提交仍聚焦的正文，不能保存少最后几个字的旧数据。</summary>
        private IEnumerator SaveShortcut(EditorPlusDriver d)
        {
            yield return d.Click(Body);
            yield return EditorPlusDriver.Pause(0.2f);
            EditorPlusDriver.Key(Body, KeyCode.End, EventModifiers.Control);
            EditorPlusDriver.Key(Body, KeyCode.None, EventModifiers.None, '存');
            yield return EditorPlusDriver.Pause(0.2f);
            saved = Body.text;
            EditorPlusDriver.Key(Body, KeyCode.S, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(1.5f);
            yield return EditorPlusDriver.WaitFor(() => DiskText(First) == saved && !Dirty(), 5f);
            d.Check("Ctrl+S 写入的是聚焦中的最新正文", () => DiskText(First) == saved, () => "磁盘 " + Show(DiskText(First)) + "，期望 " + Show(saved));
            d.Check("保存后草稿是干净的", () => !Dirty());
            Move(false);
            yield return EditorPlusDriver.Pause(0.3f);
            EditorPlusDriver.Commit();
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("保存后历史仍在；撤销后标记为未保存", () => Body.text != saved && Dirty(), () => Show(Body.text) + "，dirty=" + Dirty());
            Move(true);
            yield return EditorPlusDriver.Pause(0.3f);
            EditorPlusDriver.Commit();
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("重做回到已保存的正文后又是干净的", () => Body.text == saved && !Dirty(), () => Show(Body.text) + "，dirty=" + Dirty());
            ResetHistory();
        }

        /// <summary>普通 InputField 的中间态也能撤销，不先触发原版的数字/列表解析。</summary>
        private IEnumerator LegacyInputField(EditorPlusDriver d)
        {
            InputField action = view.input_action;
            yield return d.Click(action);
            yield return EditorPlusDriver.Pause(0.2f);
            action.text = LegacyDraft;
            yield return EditorPlusDriver.Pause(0.2f);
            EditorPlusDriver.Key(action, KeyCode.Z, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("普通输入框撤销不会先提交不合法的中间文字", () => action.text == "", () => Show(action.text));
            EditorPlusDriver.Key(action, KeyCode.Y, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("普通输入框重做恢复未提交的原文", () => action.text == LegacyDraft, () => Show(action.text));
            EditorPlusDriver.Key(action, KeyCode.Z, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(0.2f);
            EditorPlusDriver.Commit();
            yield return EditorPlusDriver.Pause(0.2f);
            ResetHistory();
        }

        /// <summary>整句删除、新增和选项是独立步骤，撤销不要求当前仍选中一句。</summary>
        private IEnumerator DeleteAddOption(EditorPlusDriver d)
        {
            Select(First + 1);
            yield return EditorPlusDriver.Pause(0.2f);
            yield return d.Click(view.btn_delete.btn);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("删除对话移除了这条记录", () => Talks.Count == 1 && Current == null, () => "共 " + Talks.Count + " 句，当前 " + Current?.id);
            yield return d.Click(Undo);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("撤销删除恢复记录并重新选中", () => Talks.Count == 2 && Current.id == First + 1, () => "共 " + Talks.Count + " 句，当前 " + Current?.id);
            yield return d.Click(Redo);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("没有文字焦点时也能重做删除", () => Talks.Count == 1, () => "共 " + Talks.Count + " 句");
            yield return d.Click(Undo);
            yield return EditorPlusDriver.Pause(0.2f);
            int count = Talks.Count;
            yield return d.Click(view.btn_new.btn);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("新增对话多出一条记录", () => Talks.Count == count + 1, () => "共 " + Talks.Count + " 句，新增前 " + count);
            d.Check("撤销后做了新操作会清空重做", () => !Redo.interactable);
            yield return d.Click(Undo);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("撤销新增移除记录和自动连线", () => Talks.Count == count && Current.nextTalk.IsEmpty(),
                () => "共 " + Talks.Count + " 句，nextTalk=" + (Current?.nextTalk == null ? "null" : string.Join(",", Current.nextTalk)));

            // 选项编辑窗口本身不在测试范围内，和旧测试一样直接调用它的回调提交一个选项。
            EditorPlusDriver.Call(view, "OnEditOption", new OptionCfg { id = EventId * 100 + 1, content = "临时选项" });
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("选项已加入", () => Options.Count == 1 && Current.option.Count == 1);
            Move(false);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("撤销选项同时恢复选项表和对话上的引用", () => Options.Count == 0 && Current.option.IsEmpty());
            Move(true);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("重做选项同时恢复两者", () => Options.Count == 1 && Current.option.Count == 1);
        }

        /// <summary>CG 正文和普通正文共用历史，撤销/重做按钮始终留在左上角。</summary>
        private IEnumerator CgText(EditorPlusDriver d)
        {
            Select(First);
            yield return EditorPlusDriver.Pause(0.2f);
            cgId = Cfg.CGCfgMap.Keys.First();
            EditorPlusDriver.Call(view, "OnSelectCg", cgId);
            yield return EditorPlusDriver.Pause(0.4f);
            d.Check("进入了 CG 正文模式", () => view.inputex_talk_cg.gameObject.activeInHierarchy);
            TMP_InputField cg = view.inputex_talk_cg;
            d.Check("切到 CG 时保留最新提交的普通正文", () => cg.text == Current.content, () => Show(cg.text) + "，数据 " + Show(Current.content));
            string cgOriginal = cg.text;
            yield return d.Click(cg);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("点击 CG 正文不会全选", () => cg.selectionStringAnchorPosition == cg.selectionStringFocusPosition,
                () => "选区 " + cg.selectionStringAnchorPosition + ".." + cg.selectionStringFocusPosition);
            EditorPlusDriver.Key(cg, KeyCode.A, EventModifiers.Control);
            EditorPlusDriver.Key(cg, KeyCode.Delete);
            yield return EditorPlusDriver.Pause(0.2f);
            EditorPlusDriver.Key(cg, KeyCode.Z, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("CG 正文 Ctrl+Z 找回删除的文字", () => cg.text == cgOriginal, () => Show(cg.text));
            EditorPlusDriver.Commit();
            yield return EditorPlusDriver.Pause(0.2f);
            // 丢弃这次 CG 操作，后面的保存用不带 CG 的临时剧情。
            Current.screenEffect = null;
            Select(First);
            yield return EditorPlusDriver.Pause(0.3f);
            ResetHistory();
        }

        /// <summary>别的插件拒绝保存时仍可继续编辑和撤销，不能误清脏标记；搜索框聚焦时 Ctrl+S 也要保存。</summary>
        private IEnumerator BlockedSave(EditorPlusDriver d)
        {
            SaveBlocker.Install();
            yield return d.Click(Body);
            yield return EditorPlusDriver.Pause(0.2f);
            EditorPlusDriver.Key(Body, KeyCode.End, EventModifiers.Control);
            EditorPlusDriver.Key(Body, KeyCode.None, EventModifiers.None, '拦');
            yield return EditorPlusDriver.Pause(0.2f);
            SaveBlocker.Block = true;
            try
            {
                EditorPlusDriver.Key(Body, KeyCode.S, EventModifiers.Control);
            }
            catch
            {
                SaveBlocker.Block = false;
                throw;
            }
            yield return EditorPlusDriver.Pause(0.4f);
            SaveBlocker.Block = false;
            d.Check("保存被拦截时磁盘不变、草稿仍是未保存", () => Dirty() && DiskText(First) == saved,
                () => "dirty=" + Dirty() + "，磁盘 " + Show(DiskText(First)));
            Move(false);
            yield return EditorPlusDriver.Pause(0.3f);
            EditorPlusDriver.Commit();
            yield return EditorPlusDriver.Pause(0.2f);
            d.Check("保存被拦截后撤销仍可用", () => Body.text == saved, () => Show(Body.text));

            InputField search = Search;
            yield return d.Click(search);
            yield return EditorPlusDriver.Pause(0.2f);
            search.text = "台词";
            yield return EditorPlusDriver.Pause(0.2f);
            EditorPlusDriver.Key(search, KeyCode.S, EventModifiers.Control);
            yield return EditorPlusDriver.Pause(1.2f);
            d.Check("搜索框聚焦时 Ctrl+S 也会保存", () => !Dirty());
            search.text = "";
            yield return EditorPlusDriver.Pause(0.2f);
        }

        /// <summary>确认框显示时，快捷键不能穿透到下面的剧情页。</summary>
        private IEnumerator ModalIsolation(EditorPlusDriver d)
        {
            HintHelper.ShowConfirm("EditorPlus 快捷键隔离测试", null, null, true);
            yield return EditorPlusDriver.Pause(0.6f);
            d.Check("确认框打开时表单的撤销快捷键被拦住", () => !Shortcut(KeyCode.Z, true, false, false),
                () => "CommonComfirmView 打开=" + UIMgr.IsViewOpened<CommonComfirmView>());
            UIMgr.CloseView<CommonComfirmView>();
            yield return EditorPlusDriver.Pause(0.3f);
        }

        private IEnumerator StoryGraph(EditorPlusDriver d)
        {
            graph = view.gameObject.GetComponents<MonoBehaviour>().Single(c => c.GetType().Name == "StoryGraphWindow");
            EditorPlusDriver.Call(graph, "Open");
            yield return EditorPlusDriver.Pause(0.6f);
            d.Check("剧情图打开时表单的撤销快捷键被拦住", () => !Shortcut(KeyCode.Z, true, false, false));
            Select(First + 1);
            EditorPlusDriver.Call(graph, "EnterEditMode");
            yield return EditorPlusDriver.Pause(0.4f);
            object session = EditorPlusDriver.Field<object>(graph.GetType(), graph, "_editSession");
            d.Check("真的进入了剧情图编辑会话", () => session != null);
            List<TalkCfg> graphTalks = EditorPlusDriver.Property<List<TalkCfg>>(session, "Talks");
            TalkCfg graphTalk = graphTalks.Single(t => t.id == First + 1);
            object[] updateArgs = { graphTalk, GraphText, graphTalk.roleName, graphTalk.showTxt, null };
            MethodInfo update = AccessTools.Method(session.GetType(), "TryUpdateTalkFields");
            bool updated = (bool)EditorPlusDriver.Invoke(update, session, updateArgs);
            d.Check("在剧情图草稿里修改正文成功", () => updated, () => updateArgs[4] as string);
            EditorPlusDriver.Call(graph, "SaveEditSession");
            yield return EditorPlusDriver.Pause(0.7f);
            yield return EditorPlusDriver.WaitFor(() => DiskText(First + 1) == GraphText, 5f);
            d.Check("剧情图保存写入了磁盘", () => DiskText(First + 1) == GraphText, () => Show(DiskText(First + 1)));
            EditorPlusDriver.Call(graph, "Close");
            yield return EditorPlusDriver.Pause(0.4f);
            Move(false);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("剧情图同步回表单可以一步撤销", () => Talks.Single(t => t.id == First + 1).content != GraphText,
                () => Show(Talks.Single(t => t.id == First + 1).content));
            Move(true);
            yield return EditorPlusDriver.Pause(0.3f);
            d.Check("剧情图同步回表单可以重做", () => Talks.Single(t => t.id == First + 1).content == GraphText,
                () => Show(Talks.Single(t => t.id == First + 1).content));
        }

        /// <summary>每次重新打开都从磁盘建立新的历史，不把关闭前的历史带进来。</summary>
        private IEnumerator ReopenResetsHistory(HarnessContext ctx, EditorPlusDriver d)
        {
            EditorPlusDriver.CallStatic(guardType, "CloseWithoutPrompt", view, true, false);
            yield return EditorPlusDriver.Pause(0.5f);
            yield return Open(d);
            d.Check("重新打开后撤销、重做都不可用", () => !Undo.interactable && !Redo.interactable);
            d.Check("重新打开读到的是已保存的正文，而不是丢弃的历史", () => Body.text == saved, () => Show(Body.text) + "，期望 " + Show(saved));
            yield return ctx.Capture("03_final");
            yield return EditorPlusDriver.Pause(0.3f);
        }
    }

    /// <summary>模拟“别的插件拒绝保存”：在 ModEvtEditView.OnClickSave 前加一个可开关的前缀补丁，只用本场景包自己的 Harmony ID。</summary>
    internal static class SaveBlocker
    {
        internal static bool Block;

        private static MethodInfo Target => AccessTools.Method(typeof(ModEvtEditView), "OnClickSave");

        internal static bool Installed
        {
            get
            {
                Patches info = Harmony.GetPatchInfo(Target);
                return info != null && info.Prefixes.Any(p => p.owner == EditorPlusDriver.HarmonyId);
            }
        }

        internal static void Install()
        {
            Block = false;
            if (Installed) return;
            new Harmony(EditorPlusDriver.HarmonyId).Patch(Target, prefix: new HarmonyMethod(typeof(SaveBlocker), nameof(AllowSave)));
        }

        internal static void Remove()
        {
            Block = false;
            if (Installed) new Harmony(EditorPlusDriver.HarmonyId).Unpatch(Target, HarmonyPatchType.All, EditorPlusDriver.HarmonyId);
        }

        private static bool AllowSave() => !Block;
    }
}
