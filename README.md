# StudentAgeEditorPlus

> StudentAge（学生时代）MOD 编辑器增强插件（BepInEx）
>
> 修复/增强编辑器 12 项缺陷，详见 [修复说明.md](修复说明.md)。

## 安装

### 方式一：一键安装器（推荐）

1. 下载 `install_plugin.bat`
2. 双击运行
3. 脚本会自动检测游戏目录、从 GitHub 下载最新版插件 DLL 并安装到位

> **首次使用需要先安装 BepInEx 前置**——如果没有安装，脚本会提示并给出下载链接。

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
