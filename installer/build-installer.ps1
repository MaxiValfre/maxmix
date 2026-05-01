<#
.SYNOPSIS
    Builds MaxMix.App and packages it as MaxMixSetup.exe via Inno Setup.

.DESCRIPTION
    Three-stage pipeline:
      1. Read <Version> from MaxMix.App.csproj (single source of truth).
      2. dotnet publish src/MaxMix.App  ->  installer/publish/
      3. ISCC.exe MaxMix.iss             ->  installer/output/MaxMixSetup.exe

    ISCC.exe is located in this order:
      - $env:PATH
      - HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1
      - C:\Program Files (x86)\Inno Setup 6\ISCC.exe
      - C:\Program Files\Inno Setup 6\ISCC.exe

    If ISCC.exe is not found, the script exits with a non-zero code and prints
    the Inno Setup download URL.

.PARAMETER Configuration
    MSBuild configuration. Default 'Release'.

.PARAMETER Runtime
    .NET RID. Default 'win-x64' (only RID currently supported by the .iss).

.PARAMETER SkipPublish
    Skip `dotnet publish`. Useful when iterating on the .iss script and
    installer/publish/ is already populated.

.PARAMETER SkipInstaller
    Skip ISCC.exe. Useful for validating the publish output alone.

.EXAMPLE
    .\installer\build-installer.ps1
    Full build: publish + installer compile.

.EXAMPLE
    .\installer\build-installer.ps1 -SkipInstaller
    Just validate the publish output (no Inno Setup needed).
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime       = 'win-x64',
    [switch] $SkipPublish,
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------------
# Paths
# ----------------------------------------------------------------------------
$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot    = Resolve-Path (Join-Path $scriptDir '..')
$csproj      = Join-Path $repoRoot 'src\MaxMix.App\MaxMix.App.csproj'
$publishDir  = Join-Path $scriptDir 'publish'
$outputDir   = Join-Path $scriptDir 'output'
$issScript   = Join-Path $scriptDir 'MaxMix.iss'

if (-not (Test-Path $csproj)) {
    Write-Error "MaxMix.App.csproj not found at: $csproj"
    exit 1
}

# ----------------------------------------------------------------------------
# Step 1: Read <Version> from .csproj
# ----------------------------------------------------------------------------
Write-Host "[1/3] Reading version from $($csproj | Split-Path -Leaf)..." -ForegroundColor Cyan
$rawVersion = & dotnet msbuild $csproj -getProperty:Version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to read <Version> from project. dotnet msbuild output:`n$rawVersion"
    exit 1
}
$version = ($rawVersion | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    Write-Error '<Version> in MaxMix.App.csproj is empty. Set it (e.g. <Version>0.1.0</Version>) and retry.'
    exit 1
}
Write-Host "      Version: $version" -ForegroundColor Green

# ----------------------------------------------------------------------------
# Step 2: dotnet publish
# ----------------------------------------------------------------------------
if ($SkipPublish) {
    Write-Host "[2/3] Skipping dotnet publish (-SkipPublish)." -ForegroundColor Yellow
    if (-not (Test-Path (Join-Path $publishDir 'MaxMix.exe'))) {
        Write-Error "Cannot skip publish: $publishDir\MaxMix.exe does not exist. Run without -SkipPublish first."
        exit 1
    }
} else {
    Write-Host "[2/3] dotnet publish -c $Configuration -r $Runtime ..." -ForegroundColor Cyan

    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDir | Out-Null

    & dotnet publish $csproj `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }

    $exePath = Join-Path $publishDir 'MaxMix.exe'
    if (-not (Test-Path $exePath)) {
        Write-Error "Publish completed but $exePath was not produced. Check the .csproj <AssemblyName>."
        exit 1
    }
    Write-Host "      Published to: $publishDir" -ForegroundColor Green
}

# ----------------------------------------------------------------------------
# Step 3: ISCC.exe
# ----------------------------------------------------------------------------
if ($SkipInstaller) {
    Write-Host "[3/3] Skipping installer compile (-SkipInstaller)." -ForegroundColor Yellow
    Write-Host "Done." -ForegroundColor Green
    exit 0
}

function Find-Iscc {
    # 1. PATH
    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # 2. Registry — Inno Setup 6 (per-machine)
    $regKeys = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($key in $regKeys) {
        if (Test-Path $key) {
            $installLocation = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
            if ($installLocation) {
                $candidate = Join-Path $installLocation 'ISCC.exe'
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }

    # 3. Default install paths
    $defaults = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    foreach ($path in $defaults) {
        if (Test-Path $path) { return $path }
    }

    return $null
}

Write-Host "[3/3] Locating ISCC.exe..." -ForegroundColor Cyan
$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host ''
    Write-Host 'ERROR: Inno Setup compiler (ISCC.exe) not found.' -ForegroundColor Red
    Write-Host 'Install Inno Setup 6 from: https://jrsoftware.org/isdl.php' -ForegroundColor Red
    Write-Host 'After installing, re-run this script. (Default install path is detected automatically.)' -ForegroundColor Red
    exit 1
}
Write-Host "      ISCC.exe: $iscc" -ForegroundColor Green

if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

Write-Host "      Compiling $($issScript | Split-Path -Leaf)..." -ForegroundColor Cyan
& $iscc `
    "/DMyAppVersion=$version" `
    "/DPublishDir=$publishDir" `
    "/DOutputDir=$outputDir" `
    $issScript
if ($LASTEXITCODE -ne 0) {
    Write-Error "ISCC.exe failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

$setupExe = Join-Path $outputDir 'MaxMixSetup.exe'
if (-not (Test-Path $setupExe)) {
    Write-Error "ISCC.exe completed but $setupExe was not produced."
    exit 1
}

Write-Host ''
Write-Host "Installer built successfully:" -ForegroundColor Green
Write-Host "  $setupExe" -ForegroundColor Green
