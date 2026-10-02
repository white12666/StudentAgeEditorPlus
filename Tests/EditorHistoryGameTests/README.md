# 原版剧情编辑页：输入和撤销回归

这是单独的测试插件，不随 EditorPlus 发布。未提供请求文件时完全休眠。

1. 关闭游戏，编译 `EditorHistoryGameTests.csproj`。
2. 将 `bin/Release/netstandard2.0/StudentAgeEditorPlus.HistoryTests.dll` 临时复制到独立插件目录。
3. 在游戏根目录 `_agenttmp/editor-history-qa/request.txt` 写入一个**尚不存在的绝对输出目录**。
4. 启动游戏。测试仅在该输出目录中创建作品，完成后输出 `steps.txt`、`result.txt`、截图和插件版本，并自动退出游戏。
5. 删除临时部署的测试 DLL。保留输出目录作为证据。

不读取或修改玩家存档，不上传作品。测试不更改系统剪贴板。

覆盖普通/CG 文本点击不全选、真正的输入控件按键事件、整段删除恢复、连续输入合并、按钮射线命中、
跨句选择、未提交非法字段、Ctrl+S、保存后撤销与脏状态、保存被补丁拦截、增删对话/选项、
模态窗口和剧情图快捷键隔离、真实剧情图保存回填、关闭重开历史重置。

离线历史测试另见 `../EditorHistoryTests/EditorHistoryTests.csproj`。

## 本轮结果

EditorPlus 0.4.32.0：49 项自动游戏检查通过（1444×824），证据在游戏根目录
`_agenttmp/editor-history-qa/run-0432-final/`。
25 项离线历史检查通过；原有未保存检查、剧情入口、送礼绑定、音频状态回归共 122 项通过。
本轮未安装 LaTeX 插件，未做两插件联测。

## 外部 Windows 键盘验收

请求文件第二行写 `external-keys` 可启用 `ExternalKeyboardProbe`，停留在独立作品供桌面驱动操作。
此模式不调用输入框 `ProcessEvent` 或历史的 `Move/Shortcut`；它只准备作品并将真实 UI/数据/IME 状态写入输出目录。
`state.json` 的 `sequence` 必须在操作后增长，不能把上一次状态当作成功证据。

桌面操作需事先获得用户前台键鼠授权。此环境的后台投递不能正确模拟 Unity 的 Ctrl 修饰键，不能拿后台成功返回冒充通过。
每次动作都通过 `cua-driver` 对明确的游戏 PID/窗口投递，并保存操作前后截图、驱动返回值和观察状态。
原始证据及经过确认的结论见 [KEYBOARD_QA_20260927.md](KEYBOARD_QA_20260927.md)。

场景切换可向输出目录 `command.txt` 写入 `modal`、`close-modal`、`cg`、`normal`；
这些仅用于场景准备，快捷键自身必须从桌面驱动进入。写入 `quit` 正常退出游戏。
