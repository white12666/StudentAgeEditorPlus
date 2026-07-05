@echo off
chcp 65001 >nul 2>&1
title StudentAgeEditorPlus 插件安装器
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c = Get-Content -Path '%~f0' -Raw -Encoding UTF8; $mk = '##' + 'PSSTART' + '##'; $p = $c.IndexOf($mk); if ($p -lt 0) { Write-Host 'Error: marker not found' -ForegroundColor Red; exit 1 }; $code = $c.Substring($p + $mk.Length); $sb = [ScriptBlock]::Create($code); & $sb" || pause
exit /b

##PSSTART##
# ===== StudentAgeEditorPlus Plugin Installer (PowerShell) =====
# Force TLS 1.2 for GitHub API compatibility on older systems
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ErrorActionPreference = "Stop"

# --- Configuration ---
$RepoOwner = "white12666"
$RepoName = "StudentAgeEditorPlus"
$GameName = "StudentAge"
$GameExe = "StudentAge.exe"
$PluginFolder = "StudentAgeEditorPlus"
$DllName = "StudentAgeEditorPlus.dll"

Write-Host ""
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "  StudentAgeEditorPlus 插件安装器" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

# ── Detect Steam path ──
function Get-SteamPath {
    $regPaths = @(
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam",
        "HKCU:\SOFTWARE\Valve\Steam"
    )
    foreach ($reg in $regPaths) {
        try {
            $val = (Get-ItemProperty -Path $reg -Name "InstallPath" -ErrorAction Stop).InstallPath
            if ($val -and (Test-Path $val)) { return $val }
        } catch {}
    }
    $defaults = @(
        "C:\Program Files (x86)\Steam",
        "C:\Program Files\Steam",
        "D:\Steam",
        "E:\Steam"
    )
    foreach ($d in $defaults) {
        if (Test-Path "$d\steam.exe") { return $d }
    }
    return $null
}

# ── Find game directory ──
function Find-GameDir {
    param([string]$SteamPath)
    $vdfPath = Join-Path $SteamPath "steamapps\libraryfolders.vdf"
    $libraryDirs = @($SteamPath)
    if (Test-Path $vdfPath) {
        $content = Get-Content $vdfPath -Raw
        $pathMatches = [regex]::Matches($content, '"path"\s+"([^"]+)"')
        foreach ($m in $pathMatches) {
            $path = $m.Groups[1].Value -replace '\\\\', '\'
            if (($path -notin $libraryDirs) -and (Test-Path $path)) {
                $libraryDirs += $path
            }
        }
    }
    foreach ($lib in $libraryDirs) {
        $gameDir = Join-Path $lib "steamapps\common\$GameName"
        if (Test-Path (Join-Path $gameDir $GameExe)) {
            return $gameDir
        }
    }
    return $null
}

# ── Main ──

$steamPath = Get-SteamPath
if (-not $steamPath) {
    Write-Host "[X] 未找到 Steam 安装路径" -ForegroundColor Red
    Write-Host "    请确认 Steam 已正确安装" -ForegroundColor Yellow
    exit 1
}
Write-Host "[OK] Steam 路径: $steamPath" -ForegroundColor Green

$gameDir = Find-GameDir -SteamPath $steamPath
if (-not $gameDir) {
    Write-Host "[X] 未找到 $GameName 游戏目录" -ForegroundColor Red
    Write-Host "    请确认游戏已通过 Steam 正确安装" -ForegroundColor Yellow
    exit 1
}
Write-Host "[OK] 游戏目录: $gameDir" -ForegroundColor Green
Write-Host ""

# ── Check BepInEx ──
$winhttp = Join-Path $gameDir "winhttp.dll"
if (-not (Test-Path $winhttp)) {
    Write-Host "[!] 未检测到 BepInEx 前置 (winhttp.dll 不存在)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "    本插件需要 BepInEx 前置才能运行。" -ForegroundColor White
    Write-Host "    请先下载并运行 BepInEx 安装包:" -ForegroundColor White
    Write-Host ""
    Write-Host "    https://github.com/$RepoOwner/$RepoName/releases" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "    下载 BepInEx-Installer.zip, 解压后运行 install.bat" -ForegroundColor White
    Write-Host "    安装完成后再运行本安装器。" -ForegroundColor White
    Write-Host ""
    $choice = Read-Host "是否在浏览器中打开下载页面? (Y/n)"
    if ($choice -ne 'n' -and $choice -ne 'N') {
        Start-Process "https://github.com/$RepoOwner/$RepoName/releases"
    }
    exit 1
}

Write-Host "[OK] BepInEx 已安装" -ForegroundColor Green
Write-Host ""

# ── Fetch latest release from GitHub ──
Write-Host "正在获取最新版本信息..." -ForegroundColor Cyan

$apiUrl = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
try {
    $release = Invoke-RestMethod -Uri $apiUrl -Headers @{"User-Agent" = "StudentAgeEditorPlus-Installer"} -TimeoutSec 15
} catch {
    Write-Host "[X] 无法获取版本信息, 请检查网络连接" -ForegroundColor Red
    Write-Host "    你也可以手动下载: https://github.com/$RepoOwner/$RepoName/releases" -ForegroundColor Yellow
    exit 1
}

$version = $release.tag_name
$releaseName = $release.name
Write-Host "[OK] 最新版本: $version" -ForegroundColor Green
if ($releaseName -and $releaseName -ne $version) {
    Write-Host "    $releaseName" -ForegroundColor DarkGray
}

# ── Find DLL asset ──
$dllAsset = $null
foreach ($asset in $release.assets) {
    if ($asset.name -eq $DllName) {
        $dllAsset = $asset
        break
    }
}
# Fallback: any .dll file
if (-not $dllAsset) {
    foreach ($asset in $release.assets) {
        if ($asset.name -like "*.dll") {
            $dllAsset = $asset
            break
        }
    }
}

if (-not $dllAsset) {
    Write-Host "[X] Release 中未找到 DLL 文件" -ForegroundColor Red
    Write-Host "    请确认 Release $version 已上传插件 DLL" -ForegroundColor Yellow
    exit 1
}

Write-Host "[OK] 插件文件: $($dllAsset.name) ($([math]::Round($dllAsset.size / 1KB)) KB)" -ForegroundColor Green
Write-Host ""

# ── Check existing plugin ──
$pluginPath = Join-Path $gameDir "BepInEx\plugins\$PluginFolder"
$dllPath = Join-Path $pluginPath $DllName

if (Test-Path $dllPath) {
    Write-Host "[!] 检测到已安装的插件" -ForegroundColor Yellow
    $choice = Read-Host "是否覆盖更新? (Y/n)"
    if ($choice -eq 'n' -or $choice -eq 'N') {
        Write-Host "已取消。" -ForegroundColor Yellow
        Read-Host "按回车键退出"
        exit 0
    }
}

# ── Check if game is running ──
$gameProcess = Get-Process -Name "StudentAge" -ErrorAction SilentlyContinue
if ($gameProcess) {
    Write-Host "[!] 检测到游戏正在运行" -ForegroundColor Yellow
    Write-Host "    游戏运行时 DLL 被占用, 无法更新。" -ForegroundColor White
    Write-Host "    请先关闭游戏再运行本安装器。" -ForegroundColor White
    exit 1
}

# ── Download DLL ──
Write-Host ""
Write-Host "正在下载插件..." -ForegroundColor Cyan

$tempDll = Join-Path $env:TEMP "StudentAgeEditorPlus_download.dll"
try {
    Invoke-WebRequest -Uri $dllAsset.browser_download_url -OutFile $tempDll -TimeoutSec 60
} catch {
    Write-Host "[X] 下载失败: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# ── Install ──
Write-Host "正在安装..." -ForegroundColor Cyan

if (-not (Test-Path $pluginPath)) {
    New-Item -ItemType Directory -Path $pluginPath -Force | Out-Null
}

try {
    Copy-Item $tempDll $dllPath -Force
} catch {
    Write-Host "[X] 安装失败: $($_.Exception.Message)" -ForegroundColor Red
    Remove-Item $tempDll -Force -ErrorAction SilentlyContinue
    exit 1
}

Remove-Item $tempDll -Force -ErrorAction SilentlyContinue

# ── Done ──
if (Test-Path $dllPath) {
    Write-Host ""
    Write-Host "=========================================" -ForegroundColor Green
    Write-Host "  安装成功!" -ForegroundColor Green
    Write-Host "=========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "  版本: $version" -ForegroundColor White
    Write-Host "  位置: $dllPath" -ForegroundColor White
    Write-Host ""
    Write-Host "  下一步:" -ForegroundColor White
    Write-Host "    1. 启动游戏" -ForegroundColor White
    Write-Host "    2. 进入 MOD 编辑器即可使用增强功能" -ForegroundColor White
    Write-Host ""
} else {
    Write-Host "[X] 安装可能失败, 请检查文件权限" -ForegroundColor Red
}

Read-Host "按回车键退出"
exit 0
