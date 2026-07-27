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
        [Parameter(Mandatory = $true)][string[]]$RequiredEntries,
        [string[]]$ForbiddenEntries = @()
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
        foreach ($forbidden in $ForbiddenEntries) {
            if ($entries.ContainsKey($forbidden.Replace("\", "/"))) {
                throw "压缩包混入了禁止随包分发的文件 '$forbidden': $Destination"
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
$editorVersion = Get-ProjectVersion $editorProject
$runtimeVersion = Get-ProjectVersion $runtimeProject

if (-not $SkipBuild) {
    Write-Host "[1/4] 构建作者端与玩家端（不部署到当前游戏）..." -ForegroundColor Cyan
    & dotnet build $editorProject -c $Configuration -p:DeployToGame=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build 失败，退出码: $LASTEXITCODE"
    }
}
else {
    Write-Host "[1/4] 跳过构建，使用现有 $Configuration 产物。" -ForegroundColor Yellow
}

$editorBinDir = Join-Path $repoRoot "bin\$Configuration"
$editorDll = Join-Path $editorBinDir "StudentAgeEditorPlus.dll"
$runtimeDll = Join-Path $repoRoot "Runtime\bin\$Configuration\StudentAgeSocialRoleRuntime.dll"

# 块级 LaTeX 公式渲染的运行时依赖，必须随作者端一起分发。
# StudentAgeTypeset.dll 是一方排版类库（LaTeX 引擎所在），经 ProjectReference 落
# $editorBinDir；CSharpMath/SkiaSharp 托管 dll 由 EditorPlus 的
# CopyLocalLockFileAssemblies（传递 NuGet 依赖）落进同一目录；原生 libSkiaSharp.dll
# (win-x64) 由 Typeset csproj 的 None(CopyToOutputDirectory) 项流转到位——
# 全部同源于 $editorBinDir，不依赖本机 NuGet 缓存路径。
$editorRuntimeDeps = @(
    "StudentAgeTypeset.dll",
    "CSharpMath.dll",
    "CSharpMath.Rendering.dll",
    "CSharpMath.Editor.dll",
    "CSharpMath.SkiaSharp.dll",
    "SkiaSharp.dll",
    "libSkiaSharp.dll"
)

# 构建输出目录里还有这四个：游戏 StudentAge_Data/Managed 已自带，随包再带会双重
# 加载冲突。所以只能白名单逐个拣取，绝不整目录拷；打包后再校验一次确实没混进去。
$editorForbiddenDeps = @(
    "System.Buffers.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll"
)

$editorPluginEntryRoot = "BepInEx/plugins/StudentAgeEditorPlus"
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
    $editorPluginStage = Join-Path $editorStage "BepInEx\plugins\StudentAgeEditorPlus"
    Copy-RequiredFile $editorDll (Join-Path $editorPluginStage "StudentAgeEditorPlus.dll")
    foreach ($dep in $editorRuntimeDeps) {
        Copy-RequiredFile (Join-Path $editorBinDir $dep) (Join-Path $editorPluginStage $dep)
    }

    Copy-RequiredFile $marker (Join-Path $runtimeStage "workshop-plugin.json")
    Copy-RequiredFile (Join-Path $repoRoot "Runtime\README.md") `
        (Join-Path $runtimeStage "README.md")
    Copy-RequiredFile $license (Join-Path $runtimeStage "LICENSE")
    Copy-RequiredFile $runtimeDll (Join-Path $runtimeStage `
        "BepInEx\plugins\StudentAgeSocialRoleRuntime\StudentAgeSocialRoleRuntime.dll")

    $editorPackage = Join-Path $OutputDirectory `
        "StudentAgeEditorPlus-v$editorVersion.zip"
    $runtimePackage = Join-Path $OutputDirectory `
        "StudentAgeSocialRoleRuntime-v$runtimeVersion.zip"

    Write-Host "[3/4] 生成并校验压缩包..." -ForegroundColor Cyan
    $editorRequiredEntries = @(
        "workshop-plugin.json",
        "README.md",
        "修复说明.md",
        "发布与依赖.md",
        "LICENSE",
        "$editorPluginEntryRoot/StudentAgeEditorPlus.dll"
    ) + @($editorRuntimeDeps | ForEach-Object { "$editorPluginEntryRoot/$_" })
    $editorForbiddenEntries = @(
        $editorForbiddenDeps | ForEach-Object { "$editorPluginEntryRoot/$_" }
    )
    New-VerifiedZip $editorStage $editorPackage `
        $editorRequiredEntries $editorForbiddenEntries
    New-VerifiedZip $runtimeStage $runtimePackage @(
        "workshop-plugin.json",
        "README.md",
        "LICENSE",
        "BepInEx/plugins/StudentAgeSocialRoleRuntime/StudentAgeSocialRoleRuntime.dll"
    )

    Write-Host "[4/4] 写入 SHA-256 校验值..." -ForegroundColor Cyan
    $hashFile = Join-Path $OutputDirectory "SHA256SUMS.txt"
    $hashLines = @($editorPackage, $runtimePackage) | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([System.IO.Path]::GetFileName($_))"
    }
    [System.IO.File]::WriteAllLines($hashFile, $hashLines, [System.Text.Encoding]::ASCII)

    Write-Host "发布产物已生成：" -ForegroundColor Green
    Write-Host "  $editorPackage"
    Write-Host "  $runtimePackage"
    Write-Host "  $hashFile"
}
finally {
    if ([System.IO.Directory]::Exists($stageRoot)) {
        [System.IO.Directory]::Delete($stageRoot, $true)
    }
}
