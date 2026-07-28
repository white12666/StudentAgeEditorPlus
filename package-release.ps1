[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipBuild,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot "artifacts")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-ProjectVersion {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw -Encoding UTF8
    $versions = @(
        @($project.Project.PropertyGroup.Version) |
            ForEach-Object { [string]$_ } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($versions.Count -ne 1) {
        throw "无法从项目文件读取唯一版本号: $ProjectPath"
    }
    return $versions[0].Trim()
}

function Reset-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Directory]::Exists($Path)) {
        [System.IO.Directory]::Delete($Path, $true)
    }
    [System.IO.Directory]::CreateDirectory($Path) | Out-Null
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not [System.IO.File]::Exists($Source)) {
        throw "发布所需文件不存在: $Source"
    }
    $parent = [System.IO.Path]::GetDirectoryName($Destination)
    if (-not [string]::IsNullOrEmpty($parent)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    [System.IO.File]::Copy($Source, $Destination, $true)
}

function New-VerifiedZip {
    param(
        [Parameter(Mandatory = $true)][string]$StageDirectory,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string[]]$RequiredEntries
    )

    if ([System.IO.File]::Exists($Destination)) {
        [System.IO.File]::Delete($Destination)
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # 不使用 Windows PowerShell 5 的 Compress-Archive：它会写入反斜杠路径，
    # 且中文文件名可能缺少 UTF-8 标志。ZipArchive 可生成标准的正斜杠条目。
    $stageRoot = [System.IO.Path]::GetFullPath($StageDirectory)
    if (-not $stageRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString())) {
        $stageRoot += [System.IO.Path]::DirectorySeparatorChar
    }

    $archive = [System.IO.Compression.ZipFile]::Open(
        $Destination, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = Get-ChildItem -LiteralPath $StageDirectory -Recurse -File |
            Sort-Object FullName
        foreach ($file in $files) {
            $entryName = $file.FullName.Substring($stageRoot.Length).Replace("\", "/")
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Destination)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $entries[$entry.FullName.Replace("\", "/")] = $true
        }
        foreach ($required in $RequiredEntries) {
            if (-not $entries.ContainsKey($required.Replace("\", "/"))) {
                throw "压缩包缺少必要文件 '$required': $Destination"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$repoRoot = $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$editorProject = Join-Path $repoRoot "StudentAgeEditorPlus.csproj"
$runtimeProject = Join-Path $repoRoot "Runtime\StudentAgeSocialRoleRuntime.csproj"
# LaTeX 插件是独立仓库（_modsrc/StudentAgeTypeset/src/plugin）的可选伴侣：
# 这里一并打包，作者端 + 玩家端 + LaTeX 三产物同版本号发放。缺源时跳过。
$latexProject = Join-Path $repoRoot "..\StudentAgeTypeset\src\plugin\StudentAgeLatex.csproj"
$latexProject = [System.IO.Path]::GetFullPath($latexProject)
$hasLatex = [System.IO.File]::Exists($latexProject)
$editorVersion = Get-ProjectVersion $editorProject
$runtimeVersion = Get-ProjectVersion $runtimeProject
$latexVersion = if ($hasLatex) { Get-ProjectVersion $latexProject } else { $null }

if (-not $SkipBuild) {
    Write-Host "[1/4] 构建作者端与玩家端（不部署到当前游戏）..." -ForegroundColor Cyan
    & dotnet build $editorProject -c $Configuration -p:DeployToGame=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build 失败，退出码: $LASTEXITCODE"
    }
    if ($hasLatex) {
        Write-Host "      构建 LaTeX 伴侣插件..." -ForegroundColor Cyan
        & dotnet build $latexProject -c $Configuration -p:DeployToGame=false
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build (StudentAgeLatex) 失败，退出码: $LASTEXITCODE"
        }
    }
}
else {
    Write-Host "[1/4] 跳过构建，使用现有 $Configuration 产物。" -ForegroundColor Yellow
}

$editorDll = Join-Path $repoRoot "bin\$Configuration\StudentAgeEditorPlus.dll"
$runtimeDll = Join-Path $repoRoot "Runtime\bin\$Configuration\StudentAgeSocialRoleRuntime.dll"
$marker = Join-Path $repoRoot "Packaging\workshop-plugin.json"
$license = Join-Path $repoRoot "LICENSE"
$fixGuide = Join-Path $repoRoot "修复说明.md"
$distributionGuide = Join-Path $repoRoot "发布与依赖.md"

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$stageRoot = Join-Path $OutputDirectory ".staging"
Reset-Directory $stageRoot

$editorStage = Join-Path $stageRoot "editor"
$runtimeStage = Join-Path $stageRoot "runtime"
Reset-Directory $editorStage
Reset-Directory $runtimeStage

try {
    Write-Host "[2/4] 组装两个独立产品目录..." -ForegroundColor Cyan

    Copy-RequiredFile $marker (Join-Path $editorStage "workshop-plugin.json")
    Copy-RequiredFile (Join-Path $repoRoot "README.md") (Join-Path $editorStage "README.md")
    Copy-RequiredFile $fixGuide (Join-Path $editorStage "修复说明.md")
    Copy-RequiredFile $distributionGuide (Join-Path $editorStage "发布与依赖.md")
    Copy-RequiredFile $license (Join-Path $editorStage "LICENSE")
    Copy-RequiredFile $editorDll (Join-Path $editorStage `
        "BepInEx\plugins\StudentAgeEditorPlus\StudentAgeEditorPlus.dll")

    Copy-RequiredFile $marker (Join-Path $runtimeStage "workshop-plugin.json")
    Copy-RequiredFile (Join-Path $repoRoot "Runtime\README.md") `
        (Join-Path $runtimeStage "README.md")
    Copy-RequiredFile $license (Join-Path $runtimeStage "LICENSE")
    Copy-RequiredFile $runtimeDll (Join-Path $runtimeStage `
        "BepInEx\plugins\StudentAgeSocialRoleRuntime\StudentAgeSocialRoleRuntime.dll")

    # LaTeX 伴侣插件（可选）：插件本体 + 引擎库 + NuGet 六件套 + 原生 libSkiaSharp，
    # 与 csproj 部署白名单严格同源。
    $latexStage = $null
    if ($hasLatex) {
        $latexStage = Join-Path $stageRoot "latex"
        Reset-Directory $latexStage
        $latexBin = Join-Path $repoRoot "..\StudentAgeTypeset\src\plugin\bin\$Configuration"
        $latexBin = [System.IO.Path]::GetFullPath($latexBin)
        Copy-RequiredFile $marker (Join-Path $latexStage "workshop-plugin.json")
        Copy-RequiredFile $license (Join-Path $latexStage "LICENSE")
        $latexOutDir = Join-Path $latexStage "BepInEx\plugins\StudentAgeLatex"
        foreach ($name in @(
            "StudentAgeLatex.dll", "StudentAgeTypeset.dll",
            "CSharpMath.dll", "CSharpMath.Rendering.dll",
            "CSharpMath.SkiaSharp.dll", "CSharpMath.Editor.dll",
            "SkiaSharp.dll", "libSkiaSharp.dll")) {
            Copy-RequiredFile (Join-Path $latexBin $name) `
                (Join-Path $latexOutDir $name)
        }
    }

    $editorPackage = Join-Path $OutputDirectory `
        "StudentAgeEditorPlus-v$editorVersion.zip"
    $runtimePackage = Join-Path $OutputDirectory `
        "StudentAgeSocialRoleRuntime-v$runtimeVersion.zip"
    $latexPackage = if ($hasLatex) {
        Join-Path $OutputDirectory "StudentAgeLatex-v$latexVersion.zip"
    } else { $null }

    Write-Host "[3/4] 生成并校验压缩包..." -ForegroundColor Cyan
    New-VerifiedZip $editorStage $editorPackage @(
        "workshop-plugin.json",
        "README.md",
        "修复说明.md",
        "发布与依赖.md",
        "LICENSE",
        "BepInEx/plugins/StudentAgeEditorPlus/StudentAgeEditorPlus.dll"
    )
    New-VerifiedZip $runtimeStage $runtimePackage @(
        "workshop-plugin.json",
        "README.md",
        "LICENSE",
        "BepInEx/plugins/StudentAgeSocialRoleRuntime/StudentAgeSocialRoleRuntime.dll"
    )
    if ($hasLatex) {
        New-VerifiedZip $latexStage $latexPackage @(
            "workshop-plugin.json",
            "LICENSE",
            "BepInEx/plugins/StudentAgeLatex/StudentAgeLatex.dll",
            "BepInEx/plugins/StudentAgeLatex/StudentAgeTypeset.dll",
            "BepInEx/plugins/StudentAgeLatex/libSkiaSharp.dll"
        )
    }

    Write-Host "[4/4] 写入 SHA-256 校验值..." -ForegroundColor Cyan
    $hashFile = Join-Path $OutputDirectory "SHA256SUMS.txt"
    $packages = @($editorPackage, $runtimePackage)
    if ($hasLatex) { $packages += $latexPackage }
    $hashLines = $packages | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([System.IO.Path]::GetFileName($_))"
    }
    [System.IO.File]::WriteAllLines($hashFile, $hashLines, [System.Text.Encoding]::ASCII)

    Write-Host "发布产物已生成：" -ForegroundColor Green
    Write-Host "  $editorPackage"
    Write-Host "  $runtimePackage"
    if ($hasLatex) { Write-Host "  $latexPackage" }
    Write-Host "  $hashFile"
}
finally {
    if ([System.IO.Directory]::Exists($stageRoot)) {
        [System.IO.Directory]::Delete($stageRoot, $true)
    }
}
