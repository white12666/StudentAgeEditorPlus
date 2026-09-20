# 创作 Mod 页 BGM 布局回归

## 0.4.22 修复

`ModPageUploadView` 的 BGM 按钮不再作为 `group_btn` 的横向布局项插入。
它挂在原「返回」控件下方，锚定其左侧，并设置 `LayoutElement.ignoreLayout=true`。
不修改原按钮的父级、位置、尺寸、布局参数或顺序；点击 BGM 也不触发返回。

其它编辑页和音频控制逻辑不变。

## 验证结果（2026-09-17）

| 游戏渲染尺寸 | 结果 |
| --- | --- |
| 1024×656 | 31/31 |
| 1280×720 | 31/31 |
| 1920×1080 | 31/31 |

测试进入真实的 `ModView → 创作 Mod → ModPageUploadView` 子页，不使用独立全屏表单。
先等原生订阅列表异步加载完成，再切页，避免测试脚本的瞬时切换使旧回调重新显示订阅列表。

每一档都验证：

- 隐藏 BGM 时记录原按钮组全部直接子控件的屏幕矩形；
- 显示、切换及重开 BGM 后，原五个控件的位置和尺寸均保持一致（误差阈值 0.1 像素）；
- BGM 严格位于返回左边、垂直居中、位于屏幕内，点击区至少 40 像素；
- 真实 EventSystem 射线命中 BGM，自身点击切换静音而不触发返回；
- 返回时一同隐藏，重新打开无重复按钮；
- 三轮最终游戏日志均无 Exception / error。

Release 编译成功，保留原有 8 条 CS0649 警告；音频状态、剧情入口和送礼逻辑离线测试
分别通过 40、16、36 项。已部署 DLL 的 AssemblyVersion 为 0.4.22.0。

最终证据位于游戏根目录：

- `_harness_out/editor-bgm-layout-0422-45d819-final1024/`
- `_harness_out/editor-bgm-layout-0422-45d819-final720/`
- `_harness_out/editor-bgm-layout-0422-45d819-final1080/`

`01_native_row_without_bgm.png` 和 `02_bgm_left_of_return.png` 为同一窗口的对照；
`native-layout.txt`、`steps.txt`、`result.txt` 保存几何数据与断言。
未修改作品或存档、未上传或发布。游戏已关闭，临时测试 DLL 已移出插件目录，显示设置已按快照恢复。

## 本地复现

这是可选的实机测试插件，不随作者端发布，也不由普通构建自动部署。

1. 编译本项目，将 `StudentAgeEditorPlus.LayoutTests.dll` 临时放入独立 BepInEx 插件目录。
2. 使用新的绝对输出目录，给游戏添加 `--saep-layout-qa=<输出目录>` 参数。
3. 等待 `result.txt`；测试只在输出目录创建临时作品，不接触玩家的 Mods 或 Saves。
4. 完成后退出游戏并移除测试 DLL。

没有该命令行参数时测试插件不会执行。布局测试驱动当前版本为 1.0.1。
