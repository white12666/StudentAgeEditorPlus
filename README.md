# StudentAgeEditorPlus

> StudentAge（学生时代）MOD 编辑器增强插件（BepInEx）
>
> 修复/增强编辑器 15 项功能，详见 [修复说明.md](修复说明.md)。

## 安装

本仓库包含两个独立产品：

- **StudentAgeEditorPlus**：作者端编辑器插件；
- **StudentAgeSocialRoleRuntime**：玩家端共享运行库，也供作者在游戏内预览自定义资料。

### Mod 作者

1. 安装 BepInEx 5；
2. 从 [Releases](https://github.com/white12666/StudentAgeEditorPlus/releases) 下载并解压 `StudentAgeEditorPlus-v*.zip` 到游戏根目录；
3. 如需预览教师、成人或自定义资料，再下载并解压独立的 `StudentAgeSocialRoleRuntime-v*.zip`；
4. 启动游戏。

缺少 Runtime 不会阻止 EditorPlus 加载，但日志会提示资料页预览不可用。

### 普通玩家

普通玩家不需要安装 `StudentAgeEditorPlus.dll`。只有当所玩的作品声明依赖时，才需要安装独立的 `StudentAgeSocialRoleRuntime`。

通过 Steam 创意工坊发布时，应把 Runtime 设为 EditorPlus 和相关作品的“必需物品”。现行 StudentAgeModManager 会负责工坊插件接入与本地开关，但订阅仍由 Steam 完成。

## 从源码构建

```powershell
dotnet build .\StudentAgeEditorPlus.csproj -c Release
```

本地开发构建会同时构建并分别部署：

- `BepInEx/plugins/StudentAgeEditorPlus/StudentAgeEditorPlus.dll`；
- `BepInEx/plugins/StudentAgeSocialRoleRuntime/StudentAgeSocialRoleRuntime.dll`。

只构建、不部署到当前游戏：

```powershell
dotnet build .\StudentAgeEditorPlus.csproj -c Release -p:DeployToGame=false
```

## 剧情图可视化编辑

事件对话编辑器的「剧情图」除了分支总览、搜索、折叠和小地图外，也提供独立的「编辑剧情」模式：

- 从对话的「下一句 / 备用 / 选项」端口或选项的「结果 / 备用」端口拖线；
- 拖到画布空白处可自动创建并连接新对话或新选项；末句还可一键生成原生「确定」结尾选项；
- 支持新增、复制、二次确认删除、右击端口逐条断线/整组清空、`Ctrl+Z` / `Ctrl+Y` 和 `Ctrl+S`；
- 删除节点、分组或注释框时保留幸存节点和当前视野；需要压缩空隙时再显式使用「自动整理」；
- 所有操作先进入深拷贝草稿，保存时通过可恢复事务更新 `TalkCfg.json` 与 `OptionCfg.json`，并保留 `.storygraph.bak`；
- 保存前按游戏真实分支规则预检缺失目标、小游戏出口、危险性别槽、演出越界和会卡死的自动循环；
- 新编号同时避开草稿、完整 Mod JSON 和内置配置；删除前从其它事件入口遍历共享剧情路径；
- 事件级选项、`maxoptions` 随机候选和事件小游戏覆盖关系也会进入总览；共享选项会显式标记；
- 多性别槽位不会被普通拖线静默覆盖；复杂字段和完整正文仍可回原表单编辑。
- 安装独立 StudentAgeLatex 时，“基础”页正文框下会实时预览 `$` 行内公式与
  `$$` 二维公式；未安装时该区域自动隐藏，剧情图其它功能不受影响。

可视化编辑只生成游戏原生配置，不需要玩家安装作者端插件。详细交互、限制和保存策略见 [修复说明.md](修复说明.md#14-剧情图事件分支可视化总览与节点编辑-已添加)。

## 发布新版本

```powershell
.\package-release.ps1
```

脚本会在 `artifacts/` 生成两个独立 ZIP 和 `SHA256SUMS.txt`：

- `StudentAgeEditorPlus-v<版本>.zip`；
- `StudentAgeSocialRoleRuntime-v<版本>.zip`。

两包均包含 `workshop-plugin.json` 和可直接解压到游戏根目录的文件结构。完整发布、工坊依赖和迁移流程见 [发布与依赖.md](发布与依赖.md)。

## 考试同学排名配置

成绩调整面向考试榜中的“同学条目”，入口不在「NPC人物」页面。打开作品后选择对应年级：

- 「考试同学（小学）」；
- 「考试同学（初中）」；
- 「考试同学（高中文科）」；
- 「考试同学（高中理科）」。

点击新增后，可直接从下拉列表选择要调整的同学；EditorPlus 会自动带出其姓名、关联人物、性别、原权重和条件。作者通常只需调整「排名倾向权重」或「生效条件」。普通同学不需要 PersonCfg，`关联剧情人物` 保持 0 即可。

NPC 人物页原生的 `examRank` 字段仍保留给高级剧情人物使用，但已明确标为「剧情人物排名（高级）」；点击旁边的「同学入口」只会给出一条简短路径提示，不再弹出内部字段长说明。

权重不是直接分数：它只影响同学在当前排名段中的先后倾向，最终分数仍由实际名次生成。保存结果是原生 `Classmate*Cfg.json`，普通玩家无需安装 EditorPlus。详细限制和剧情条件示例见《修复说明》。

## 可社交角色资料编辑器

在原生「NPC人物」编辑器中，将人物类型设为 `2/3/4` 后，会出现 EditorPlus 的社交资料字段：

- 学生：沿用原版逻辑，学校和班级随主角当前年级变化；
- 教师：可按 NPC 单独填写学校、职务；
- 成人：可按 NPC 单独填写单位、身份；
- 自定义：两栏标题和内容均可自由填写；
- 可选择隐藏非学生角色不合适的成绩段位图标；
- **资料阶段**：同一角色可配置多个资料阶段，按触发条件（事件已发生 / 选项已选择 / 事件存档值达标）随玩家剧情进度自动切换，例如升职后职务变化；支持在编辑器里增删阶段，或通过剪贴板导出/导入 JSON 批量编辑（格式见修复说明）。

扩展资料以带版本标记的形式保存在标准 `PersonCfg.note` 中，仍随 `PersonCfg.json` 一起打包。作者端 EditorPlus 只负责编辑和写入数据；玩家端显示由独立的轻量 `StudentAgeSocialRoleRuntime.dll` 负责。

### 作品依赖

如果作品使用教师、成人或自定义资料，请依赖独立的 `StudentAgeSocialRoleRuntime`：

- Steam 创意工坊：把 Runtime 工坊项目设置为作品的“必需物品”；
- GitHub / 手动分发：提示玩家单独安装 `StudentAgeSocialRoleRuntime-v*.zip`；
- 不再推荐把 Runtime DLL 复制进每个作品目录，以免出现多个副本和版本漂移。

玩家不需要安装 StudentAgeEditorPlus。如果 Runtime 缺失，角色仍可关注和触发事件，但资料页会退回原版统一的学校/班级显示。过渡期确实无法声明依赖时，可以临时随作品携带同版本 DLL，待独立 Runtime 发布后再迁移。

当前功能修正的是社交资料页。NPC 的内部 `Grade/GradeState`、阶段立绘选择、校服和成长结算仍沿用游戏原有逻辑；制作成年人时建议两个阶段都配置合适立绘，并避免依赖学生专属的考试/班级玩法。

## License

AGPL-3.0

### 开源协议声明

本 mod 基于 **AGPL-3.0** 协议开源，这是一份强 Copyleft（传染性）协议。通俗概括如下：

**你可以自由地：** 使用、修改本 mod，以及基于本 mod 的代码进行二次开发。

**但你必须遵守：**
- 若你**复制、修改本 mod 的代码，或将其代码用于你的项目**，在**分发**你的作品时，必须同样以 AGPL-3.0 协议开源，并提供完整源代码；
- **即使不公开分发文件**，若你将修改后的版本**部署在服务器上供玩家使用**，也必须向这些玩家提供修改后的源代码；
- 保留本 mod 的版权声明与协议文本。

**关于游戏本体：** 本 mod 未包含、修改或分发游戏本体的任何代码与文件，仅通过 Harmony 运行时补丁与反射调用同游戏交互。

以上为通俗概括，具体权利义务以 [AGPL-3.0 协议原文] 为准。

---

### 附加许可（Additional Permission，基于 AGPL-3.0 第 7 条）

作为本项目的创作者，本人在 AGPL-3.0 协议之外，额外授予**白雨工作室**及其工作人员（仅限用于该工作室的开发与运营工作）一份**免费、非独占、不可撤销**的许可：

允许其以任何形式（包括但不限于闭源、并入游戏本体、商业用途）复制、修改、引用本项目中**由本人创作的代码**，不受 AGPL-3.0 各项义务（包括开源与源代码提供义务）的约束。

**范围限定：**
1. 工作人员以个人名义、非为该工作室工作目的使用本项目代码时，不适用本附加许可，仍受 AGPL-3.0 约束；
2. 依据 AGPL-3.0 第 7 条，任何再分发者可以选择移除本附加许可文本，但这不影响白雨工作室已获得的权利。
