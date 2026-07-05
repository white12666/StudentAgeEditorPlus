# StudentAgeEditorPlus

> StudentAge（学生时代）MOD 编辑器增强插件（BepInEx）
>
> 修复/增强编辑器 12 项缺陷，详见 [修复说明.md](修复说明.md)。

## 安装

### 方式一：一键安装器（推荐）

1. 下载 `install_plugin.bat`
2. 双击运行
3. 脚本会自动检测游戏目录、从 GitHub 下载最新版插件 DLL 并安装到位


### 方式二：手动安装

1. 从 [Releases](https://github.com/white12666/StudentAgeEditorPlus/releases) 下载最新的 `StudentAgeEditorPlus.dll`
2. 放入游戏目录下的 `BepInEx/plugins/StudentAgeEditorPlus/`
3. 启动游戏

## 构建从源码

```bash
dotnet build -c Release
```

产物自动部署到 `BepInEx/plugins/StudentAgeEditorPlus/StudentAgeEditorPlus.dll`。

## 发布新版本

1. `dotnet build -c Release`
2. 在 GitHub 创建新 Release（建议 tag 格式 `v0.x.x`）
3. 上传 `bin/Release/StudentAgeEditorPlus.dll` 作为 Release Asset
4. 安装器会自动拉取最新 Release 的 DLL

## License

AGPL-3.0

## 开源协议声明

本 mod 基于 **AGPL-3.0** 协议开源，这是一份强 Copyleft（传染性）协议。通俗概括如下：

**你可以自由地：** 使用、修改本 mod，以及基于本 mod 的代码进行二次开发。

**但你必须遵守：**
- 若你**复制、修改本 mod 的代码，或将其代码用于你的项目**，在**分发**你的作品时，必须同样以 AGPL-3.0 协议开源，并提供完整源代码；
- **即使不公开分发文件**，若你将修改后的版本**部署在服务器上供玩家使用**，也必须向这些玩家提供修改后的源代码；
- 保留本 mod 的版权声明与协议文本。

**关于游戏本体：** 本 mod 未包含、修改或分发游戏本体的任何代码与文件，仅通过 Harmony 运行时补丁与反射调用同游戏交互。

以上为通俗概括，具体权利义务以 [AGPL-3.0 协议原文] 为准。

---

## 二、官方豁免（附加许可）

## 附加许可（Additional Permission，基于 AGPL-3.0 第 7 条）

作为本项目的创作者，本人在 AGPL-3.0 协议之外，额外授予**白雨工作室**及其工作人员（仅限用于该工作室的开发与运营工作）一份**免费、非独占、不可撤销**的许可：

允许其以任何形式（包括但不限于闭源、并入游戏本体、商业用途）复制、修改、引用本项目中**由本人创作的代码**，不受 AGPL-3.0 各项义务（包括开源与源代码提供义务）的约束。

**范围限定：**
1. 工作人员以个人名义、非为该工作室工作目的使用本项目代码时，不适用本附加许可，仍受 AGPL-3.0 约束；
2. 依据 AGPL-3.0 第 7 条，任何再分发者可以选择移除本附加许可文本，但这不影响白雨工作室已获得的权利。

