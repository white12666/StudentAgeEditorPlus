# EditorPlus 实机场景（StudentAge Harness）

本目录是 StudentAge Harness 的场景包（`netstandard2.0` 类库，不是 BepInEx 插件），
替代旧的两个游戏内测试插件：

| 场景 | 旧测试 | 检查数 |
| --- | --- | --- |
| `editorplus-bgm-layout` | `../EditorAudioLayoutGameTests/ProjectLayoutTests.cs` | 13 + 3 ×（1 + `group_btn` 原有子控件数）：5 个子控件时为 31，4 个时为 28 |
| `editorplus-edit-history` | `../EditorHistoryGameTests/HistoryGameTests.cs` | 49 |

每条旧检查都对应一条带编号的断言（`检查 #N 未通过：…`）。报告的扩展字段
`editorplus.bgm-layout` / `editorplus.edit-history` 记录 `checks`、`expectedChecks`、`complete`、
通过的检查列表、EditorPlus 版本、分辨率等，用来证明全部检查确实执行过。
场景失败时扩展字段仍会写入（收尾步骤），`checks` 是失败前已通过的条数。

## 运行

先编译 EditorPlus（`bin/Release/StudentAgeEditorPlus.dll`）和本场景包：

```powershell
dotnet build Tests\HarnessScenarios\StudentAgeEditorPlus.HarnessScenarios.csproj -c Release
cd Tests\HarnessScenarios
sah run -Config .\harness.json                          # startup + tag:editorplus，1920x1080
sah run -Config .\harness.json -Resolution 1280x720
sah run -Config .\harness.json -Resolution 1024x656
sah run -Config .\harness.json -Scenario editorplus-bgm-layout   # 只跑一个场景
```

旧的 BGM 布局测试在 1024×656、1280×720、1920×1080 三档都要通过，建议三档各跑一次（验收时加 `-Strict`）。
场景包按 `$(STUDENTAGE_HARNESS_HOME)\plugin\StudentAgeHarness.Core.dll` 引用 Harness，
未设置时回退到同级仓库 `..\..\..\StudentAgeHarness\src\StudentAgeHarness\bin\Release\StudentAgeHarness.Core.dll`。

运行在隔离的 profile 里：存档、偏好、本地 Mods 和 BepInEx 配置都是本次运行自己的副本，
临时作品由 `ctx.Fixtures.CreateLocalMod` 建在 `profile/Mods` 下，运行结束自动删除。
不接触玩家的 Mods、存档或真实配置，也不上传作品。

## editorplus-bgm-layout

| 步骤 | 内容 | 旧检查 |
| --- | --- | --- |
| `main_menu` / `prepare` | 进主菜单，读出编辑器静音偏好，建临时作品 | — |
| `open_mod_view` | **真实点击**主菜单 Mod 入口 `EntryView.btn_workshop` | — |
| `subscriptions_loaded` | 等原版订阅列表异步加载完（`group_page` 显示）再等 0.3 秒 | 1 |
| `open_creative_tab` | **真实点击**“创作 Mod”页签（`tabgroup_top` 中 20003 的 `btn_click`） | — |
| `select_project` | 调用 `ModPageUploadView.Select` 打开临时作品 | — |
| `bgm_structure` | 真实子页、BGM 挂在“返回”下、`ignoreLayout` | 2–4 |
| `native_baseline` | 隐藏 BGM，记录 `group_btn` 全部直接子控件的屏幕矩形，截图 `native_row_without_bgm` | — |
| `bgm_shown` | 显示 BGM 后数量与每个子控件位置/尺寸不变；BGM 在返回左侧、垂直居中（<0.1 px）、在屏幕内、≥40 px；截图 | 5–10、11–14 |
| `bgm_toggle` | **真实点击** BGM：切换音乐声道静音、不触发返回、布局不变；截图 | 15–16、17–22 |
| `bgm_restore` | 再点一次恢复静音状态 | 23 |
| `return_hides_bgm` | **真实点击**“返回”，BGM 随作品控件一起隐藏 | 24 |
| `reopen_project` | 重新打开作品不重复创建 BGM，布局不变；截图 | 25、26–31 |
| `check_count` | 执行条数等于 13 + 3 ×（1 + 子控件数） | — |
| 收尾 | 恢复编辑器静音偏好（`EditorAudioRuntime.Toggle`）、关闭 ModView、写报告扩展 | — |

编号按旧测试的 5 个子控件计算。隔离运行不加载 DND 编辑器插件，`group_btn` 只有 4 个原版子控件
（返回、打开文件夹、保存、上传），这时三轮布局比较各少 1 条，共 28 条。
所有布局检查都相对隐藏 BGM 时的基线，不写死数量；基线几何另存为 `fixtures/editorplus-bgm-layout/native-layout.txt`。

## editorplus-edit-history

| 步骤 | 内容 | 旧检查 |
| --- | --- | --- |
| `prepare` | 在临时作品 `Cfgs/<当前语言>/` 写 `TalkCfg.json`、`EvtCfg.json`（事件 1987652，对话 1987652001/002） | — |
| `open_editor` | `UIMgr.OpenView<ModEvtEditView>` 打开临时事件，历史组件已挂上 | 1 |
| `header_layout` | 关闭聚焦全选、撤销重做初始不可用、按钮不重叠、不超出列表、搜索栏在下方；截图 `01_header` | 2–6 |
| `save_row_layout` | 新增（0.4.33 回归，不计入 49 项）：右列「保存」与 BGM 同排时，「保存」完整在屏内、不出本行、至少占半行且不少于 40 像素，BGM 不重叠，两者都能被真实点击命中；几何写入报告扩展 `saveRow`，截图 `save_row`。0.4.32 在 1024×656 下此步失败 | — |
| `focused_typing` | 点击只放光标、输入、Ctrl+Z/Ctrl+Y、Ctrl+A 退格、找回整段、Ctrl+Shift+Z；截图 `02_recovered_paragraph` | 7–13 |
| `merge_and_buttons` | 连续输入合并、**真实点击**撤销/重做按钮、换选中句后撤销 | 14–17 |
| `save_shortcut` | Ctrl+S 保存聚焦中的最新正文、保存后干净、撤销变脏、重做回干净 | 18–21 |
| `legacy_input_field` | 普通 InputField 未提交中间态的撤销/重做 | 22–23 |
| `delete_add_option` | **真实点击**删除/新增/撤销/重做；选项的应用、撤销、重做 | 24–32 |
| `cg_text` | CG 正文模式、保留正文、点击不全选、Ctrl+Z 恢复 | 33–36 |
| `blocked_save` | 保存被拦截时磁盘不变且仍未保存、之后仍可撤销、搜索框聚焦时 Ctrl+S 也保存 | 37–39 |
| `modal_isolation` | 确认框打开时快捷键不穿透（声明 `ExpectingModal`） | 40 |
| `story_graph` | 剧情图打开时快捷键不穿透、编辑会话、草稿修改、保存写盘、同步回表单的撤销/重做 | 41–46 |
| `reopen_resets_history` | 不提示直接关闭后重开：历史已挂、撤销重做不可用、读到已保存正文；截图 `03_final` | 47–49 |
| `check_count` | 执行条数等于 49 | — |
| 收尾 | 撤掉本包的 Harmony 补丁并清除拦截标志、关闭剧情图/确认框/剧情编辑页（不弹未保存提示）、写报告扩展 | — |

## 真实操作与内部 API

真实操作（`ctx.Ui.ClickWhenReachable`：等目标可交互、射线命中目标自身后发送 pointerDown/Up/Click，被挡住就失败）：
主菜单 Mod 入口、“创作 Mod”页签、BGM 按钮、“返回”、正文/CG/普通输入框/搜索框的点击、撤销、重做、删除、新增按钮。

作为显式准备步骤保留旧测试的内部调用：

- `ModPageUploadView.Select`：原版只能经系统文件夹对话框或新建输入框选作品，系统对话框无法自动化。
- `UIMgr.OpenView<ModEvtEditView>`：剧情编辑页需要从配置表页逐层进入，旧测试同样直接打开。
- 键盘：`TMP_InputField/InputField.ProcessEvent(new Event{…})`。输入框读 IMGUI 事件，InputSystem 的虚拟按键
  （`ctx.Input`）到不了这条路径；Harness 的 `TypeIntoInput` 也不支持中文字符和组合键。
- `EvtEditorHistory.Move/Shortcut/Attach`、`ModEvtEditView.Select/OnEditOption/OnSelectCg`、
  `StoryGraphWindow.Open/EnterEditMode/SaveEditSession/Close`、`StoryGraphEditSession.TryUpdateTalkFields`、
  `UnsavedEditGuard.TryGetUnsavedChanges/CloseWithoutPrompt`、`HintHelper.ShowConfirm`：与旧测试一致。
- 拦截保存：在 `ModEvtEditView.OnClickSave` 前加可开关的前缀补丁，Harmony ID 为
  `com.studentage.editorplus.harnessscenarios`，收尾时只撤掉这个 ID 的补丁。
- BGM 基线：和旧测试一样临时 `SetActive(false/true)` BGM 按钮来比较有无 BGM 时的原按钮布局。

## 与旧测试的差异

- 进入 ModView 和“创作 Mod”页签改为真实点击（旧测试调用 `UIMgr.OpenView<ModView>` 和 `tabgroup_top.Select(20003)`）。
- “返回”改为真实点击 `btn_cancel`（旧测试直接调用私有 `OnClickCancel`，按钮绑定的就是它）。
- 点击遮挡判定由 Harness 完成：最上层射线命中必须是目标或其子节点（旧测试要求点击处理者就是目标本身）。
- 保存检查（18–19、44）在旧的固定等待之后再轮询最多 5 秒，避免慢盘时误报；判定条件不变。
- 不再设置 `Application.runInBackground`、不再自己写 `steps.txt/result.txt/version.txt`、不调用 `Application.Quit`；
  结果、截图和版本都在 Harness 报告里。
- 每个场景之后 Harness 回主菜单；场景自己关闭打开的界面并恢复静音偏好和补丁。

## 没有迁移的内容

- `ExternalKeyboardProbe`（请求文件第二行 `external-keys`）：需要人在真实 Windows 键盘/输入法上操作，
  并由桌面驱动对游戏窗口投递按键，属于人工验收，不能在 Harness 的无人值守运行中自动完成。
  相关记录见 `../EditorHistoryGameTests/KEYBOARD_QA_20260927.md`。
- 旧测试的请求文件 / 命令行参数触发方式：Harness 用 `harness.json` 和场景名选择。
