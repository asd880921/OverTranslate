param(
    [string]$ProjectPath = ".\src\OverTranslate\OverTranslate.csproj",
    [string]$PublishDir = ".\src\OverTranslate\bin\Publish",
    [string]$OutputDir = ".\artifacts\releases",
    [string]$PackId = "OverTranslate",
    [string]$PackTitle = "OverTranslate",
    [string]$PackAuthors = "Hon.Lu",
    [string]$MainExe = "OverTranslate.exe",
    [string]$IconPath = ".\src\OverTranslate\icons\icon_256.ico",
    [string]$Channel = "win",
    [string]$PublishProfile = "FolderProfile",
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [string]$Version
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([string]$PathValue)
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PathValue))
}

function Clear-DirectoryContents {
    param([string]$DirectoryPath)

    if (-not (Test-Path $DirectoryPath)) {
        New-Item -ItemType Directory -Force -Path $DirectoryPath | Out-Null
        return
    }

    Get-ChildItem -LiteralPath $DirectoryPath -Force | Remove-Item -Recurse -Force
}

function Get-VersionFromCsproj {
    param([string]$CsprojPath)

    $csprojPath = Resolve-FullPath $CsprojPath
    if (-not (Test-Path $csprojPath)) {
        throw "找不到 csproj：$csprojPath"
    }

    [xml]$csproj = Get-Content $csprojPath
    $versionNode = $csproj.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionNode)) {
        throw "csproj 內沒有 <Version>。"
    }

    return $versionNode.Trim()
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-VersionFromCsproj -CsprojPath $ProjectPath
}

$projectFullPath = Resolve-FullPath $ProjectPath
$publishFullPath = Resolve-FullPath $PublishDir
$outputFullPath = Resolve-FullPath $OutputDir
$iconFullPath = Resolve-FullPath $IconPath
$mainExeFullPath = Join-Path $publishFullPath $MainExe
$appSettingsPublishPath = Join-Path $publishFullPath "appsettings.json"

if (-not (Test-Path $projectFullPath)) {
    throw "找不到專案檔：$projectFullPath"
}

if (-not $SkipPublish) {
    if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
        throw "找不到 dotnet。請先安裝 .NET SDK，或確認終端機環境變數已更新。"
    }

    Write-Host "先清空 Publish 資料夾..." -ForegroundColor Cyan
    Clear-DirectoryContents -DirectoryPath $publishFullPath

    Write-Host "先執行 dotnet publish..." -ForegroundColor Cyan
    Write-Host "Project    : $projectFullPath"
    Write-Host "Profile    : $PublishProfile"
    Write-Host "Config     : $Configuration"
    Write-Host "PublishDir : $publishFullPath"

    $publishArgs = @(
        "publish",
        $projectFullPath,
        "-c", $Configuration,
        "-p:PublishProfile=$PublishProfile",
        "-p:PublishDir=$publishFullPath"
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失敗，exit code: $LASTEXITCODE"
    }

    if (Test-Path $appSettingsPublishPath) {
        Remove-Item -LiteralPath $appSettingsPublishPath -Force
        Write-Host "已從 Publish 輸出移除 appsettings.json" -ForegroundColor Yellow
    }

    Write-Host ""
}

if (-not (Get-Command "vpk" -ErrorAction SilentlyContinue)) {
    throw "找不到 vpk。請先確認已安裝 Velopack CLI，或重新開啟終端機。"
}

if (-not (Test-Path $publishFullPath)) {
    throw "找不到 Publish 資料夾：$publishFullPath"
}

if (-not (Test-Path $mainExeFullPath)) {
    throw "找不到主程式：$mainExeFullPath"
}

if (-not (Test-Path $iconFullPath)) {
    throw "找不到 icon：$iconFullPath"
}

New-Item -ItemType Directory -Force -Path $outputFullPath | Out-Null

Write-Host "Velopack 打包開始..." -ForegroundColor Cyan
Write-Host "Version   : $Version"
Write-Host "PublishDir: $publishFullPath"
Write-Host "OutputDir : $outputFullPath"

$packArgs = @(
    "pack",
    "--packId", $PackId,
    "--packVersion", $Version,
    "--packDir", $publishFullPath,
    "--mainExe", $MainExe,
    "--packTitle", $PackTitle,
    "--packAuthors", $PackAuthors,
    "--icon", $iconFullPath,
    "--channel", $Channel,
    "--noPortable",
    "--outputDir", $outputFullPath
)

& vpk @packArgs
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack 失敗，exit code: $LASTEXITCODE"
}

Write-Host ""
Write-Host "打包完成，主要產物通常會在這裡：" -ForegroundColor Green
Write-Host "  Setup      : $outputFullPath\$PackId-$Channel-Setup.exe"
Write-Host "  Full pkg   : $outputFullPath\$PackId-$Version-full.nupkg"
Write-Host "  Releases   : $outputFullPath\releases.$Channel.json"
Write-Host ""
Write-Host "輸出資料夾內容：" -ForegroundColor Green
Get-ChildItem $outputFullPath | Select-Object Name, Length, LastWriteTime
