# Builds the self-contained Windows apps and their Inno Setup installers.
# Usage:
#   powershell -ExecutionPolicy Bypass -File installer\windows\build.ps1
#   powershell -ExecutionPolicy Bypass -File installer\windows\build.ps1 -SkipPublish
#   powershell -ExecutionPolicy Bypass -File installer\windows\build.ps1 -Target client

[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [ValidateSet("all", "host", "client")]
    [string]$Target = "all",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$distDir = Join-Path $root "dist"

function Resolve-DotNet {
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT "dotnet.exe") }
    $candidates += @(
        "D:\dotnet\dotnet.exe",
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet\dotnet.exe")
    )
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path $candidate)) { continue }
        try {
            $sdks = & $candidate --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and ($sdks -match '(^|\s)8\.')) { return $candidate }
        }
        catch {
            # Try the next candidate.
        }
    }

    throw ".NET SDK 8 was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
}

function Publish-App([string]$project, [string]$publishDir) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Force $publishDir | Out-Null

    Write-Host "Publishing $project ..."
    & $script:dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

function Build-Installer([string]$iss, [string]$expected) {
    New-Item -ItemType Directory -Force $distDir | Out-Null
    Write-Host "Compiling $(Split-Path $iss -Leaf) with $script:iscc ..."
    & $script:iscc $iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE" }

    if (-not (Test-Path $expected)) { throw "Installer was not produced: $expected" }

    $file = Get-Item $expected
    $hash = (Get-FileHash $expected -Algorithm SHA256).Hash
    Write-Host ""
    Write-Host "Installer: $($file.FullName)"
    Write-Host ("Size:      {0:N1} MB" -f ($file.Length / 1MB))
    Write-Host "SHA256:    $hash"
    Write-Host ""
}

$dotnet = Resolve-DotNet
Write-Host "Using .NET SDK: $dotnet"

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php or run: winget install JRSoftware.InnoSetup"
}

# Host: owns the keyboard and mouse, listens for the client.
if ($Target -in @("all", "host")) {
    $publishDir = Join-Path $root "publish\win-x64"
    if (-not $SkipPublish) {
        Publish-App (Join-Path $root "src\OneDesk.Host.Win\OneDesk.Host.Win.csproj") $publishDir
    }

    $exe = Join-Path $publishDir "OneDesk.exe"
    if (-not (Test-Path $exe)) { throw "Published executable not found: $exe" }

    Build-Installer (Join-Path $PSScriptRoot "OneDesk.iss") (Join-Path $distDir "OneDesk-Setup-$Version.exe")
}

# Client: the second Windows machine, injects the forwarded input.
if ($Target -in @("all", "client")) {
    $publishDir = Join-Path $root "publish\win-x64-client"
    if (-not $SkipPublish) {
        Publish-App (Join-Path $root "src\OneDesk.Client.Win\OneDesk.Client.Win.csproj") $publishDir
    }

    $exe = Join-Path $publishDir "OneDeskClient.exe"
    if (-not (Test-Path $exe)) { throw "Published executable not found: $exe" }

    Build-Installer (Join-Path $PSScriptRoot "OneDeskClient.iss") (Join-Path $distDir "OneDeskClient-Setup-$Version.exe")
}
