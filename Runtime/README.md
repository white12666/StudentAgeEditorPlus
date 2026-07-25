# StudentAgeSocialRoleRuntime

可社交角色自定义资料的**独立玩家端共享运行库**。

插件信息：

```text
GUID:    com.studentage.socialroleruntime
Version: 0.2.2
```

它只做三件事：

1. 读取 `PersonCfg.note` 中的 `[SAEP_SOCIAL_V2|...]` 标记，并兼容旧版 `[SAEP_SOCIAL_V1|...]`；
2. 根据存档进度选择资料阶段，按 NPC 覆盖社交资料页的两栏标题与内容；
3. 根据配置隐藏或显示成绩段位，并保护 `className/studyRank` 短数组不使资料页崩溃。
4. 在角色创建及旧档加载时修复历史版本可能写入 `RoleModel` 的空生日，避免恋人回合或企鹅空间资料反复崩溃。

本 DLL 不包含编辑器 UI，也不依赖 `StudentAgeEditorPlus.dll`。

## 谁需要安装

- 使用相关作品的普通玩家：需要本 Runtime，不需要 EditorPlus；
- 使用 EditorPlus 制作并预览自定义资料的作者：需要 EditorPlus 和本 Runtime；
- 不使用教师、成人或自定义资料的作品：不需要本 Runtime。

## 安装

将独立发布包解压到游戏根目录，最终路径应为：

```text
BepInEx/plugins/StudentAgeSocialRoleRuntime/StudentAgeSocialRoleRuntime.dll
```

通过 Steam 创意工坊分发时，应把 Runtime 发布为独立工坊项目。EditorPlus 和使用该资料格式的作品应将它设置为“必需物品”。

## 给 Mod 作者

作品只需要保存 EditorPlus 写入 `PersonCfg.note` 的资料数据，并声明对 Runtime 的依赖。长期方案中不要把 Runtime DLL 复制进每个作品目录，以免：

- 同一台机器出现多个相同 GUID 的副本；
- 不同作品携带不同版本；
- Runtime 修复后必须重新发布所有作品。

现行 StudentAgeModManager 暂不自动解析跨 Mod 依赖；当前请使用 Steam“必需物品”和作品说明表达依赖。过渡期无法声明独立依赖时，可以临时随作品携带同版本 DLL，但应在共享 Runtime 可用后移除。

## 构建和发布

在仓库根目录运行：

```powershell
.\package-release.ps1
```

会生成独立的：

```text
artifacts/StudentAgeSocialRoleRuntime-v0.2.2.zip
```

压缩包带有 Workshop Bridge 所需的 `workshop-plugin.json`，可作为 GitHub Release 资产或独立工坊项目内容。

## 依赖

- BepInEx 5；
- 游戏本体 `Assembly-CSharp.dll` 和 Unity 程序集。

Runtime 不依赖 Newtonsoft.Json，也不包含作者端 UI。

## 生命周期兼容

《学生时代》会在首个 Unity 场景建立时销毁过早创建的 BepInEx 插件组件，但游戏随后仍会继续加载 Mod、存档和 UI。Runtime 0.2.1 起不会在该阶段执行 `UnpatchSelf`；Harmony 资料补丁会继续保留，并在 `UIMgr.Init` 后输出存活诊断。

Runtime 0.2.2 起会迁移旧档中的不完整 NPC 生日。若作品配置已经修正，旧档采用作品中的日期并在玩家下次正常保存后固化；若作品配置仍无有效日期，只使用安全占位避免崩溃，并在日志中要求作者发布修正版。

## 缺少 Runtime 时

角色的关注、关系、事件和立绘仍然是原生配置，可以正常加载；只有教师、成人和自定义资料显示会退回游戏原版的统一学校/班级。

## 兼容与去重

插件 GUID 固定为：

```text
com.studentage.socialroleruntime
```

该 GUID 发布后不得修改。BepInEx 对重复 GUID 的处理只能作为过渡期安全网，不应依赖多个副本长期共存。请全局只保留一个独立 Runtime，并优先升级到作品声明的最低版本。

资料格式当前为 V2，继续兼容 V1。未来 Runtime 更新应优先保持旧格式可读；若必须做破坏性变更，应使用新的资料标记，而不是重新解释 V1/V2。
