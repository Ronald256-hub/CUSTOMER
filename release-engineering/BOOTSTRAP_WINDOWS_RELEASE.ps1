[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$Configuration = "Release",
    [string]$CertificateThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SkipInstaller,
    [switch]$NoAutomaticPrerequisiteInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent $PSScriptRoot
}

$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$engineeringRoot = $PSScriptRoot
$prerequisiteScript = Join-Path $engineeringRoot "INSTALL_BUILD_PREREQUISITES.ps1"
$fixScript = Join-Path $engineeringRoot "APPLY_KNOWN_WINDOWS_BUILD_FIXES.ps1"
$buildScript = Join-Path $SourceRoot "BUILD_WINDOWS_RELEASE.ps1"

function Test-DotNet10Sdk {
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (-not $dotnet) { return $false }

    $sdks = & $dotnet.Source --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match '^10\.' } | Select-Object -First 1)
}

function Find-InnoCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    return $null
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path -LiteralPath $kits -PathType Container)) { return $null }

    return Get-ChildItem $kits -Filter signtool.exe -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\signtool.exe" } |
        Sort-Object FullName -Descending |
        Select-Object -ExpandProperty FullName -First 1
}

$needDotNet = -not (Test-DotNet10Sdk)
$needInno = -not $SkipInstaller -and -not (Find-InnoCompiler)
$needSigningTools = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and -not (Find-SignTool)

if ($needDotNet -or $needInno -or $needSigningTools) {
    if ($NoAutomaticPrerequisiteInstall) {
        $missing = [System.Collections.Generic.List[string]]::new()
        if ($needDotNet) { $missing.Add(".NET 10 SDK") }
        if ($needInno) { $missing.Add("Inno Setup 6") }
        if ($needSigningTools) { $missing.Add("Windows SDK SignTool") }
        throw "Missing release prerequisites: $($missing -join ', '). Run INSTALL_BUILD_PREREQUISITES.ps1 as Administrator."
    }

    if (-not (Test-Path -LiteralPath $prerequisiteScript -PathType Leaf)) {
        throw "Automatic prerequisite installer was not found: $prerequisiteScript"
    }

    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", ('"' + $prerequisiteScript + '"')
    )

    if (-not $needInno) { $arguments += "-IncludeInnoSetup:`$false" }
    if ($needSigningTools) { $arguments += "-IncludeSigningTools" }
    $arguments += "-NonInteractive"

    Write-Host "Installing missing Windows release prerequisites..." -ForegroundColor Cyan
    $process = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Automatic prerequisite installation failed with exit code $($process.ExitCode)."
    }

    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:Path = "$machinePath;$userPath"
}

if (-not (Test-DotNet10Sdk)) {
    throw ".NET 10 SDK is still unavailable. Close PowerShell, open a new window, and run this bootstrap again."
}

if (-not $SkipInstaller -and -not (Find-InnoCompiler)) {
    throw "Inno Setup 6 is still unavailable."
}

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and -not (Find-SignTool)) {
    throw "SignTool is still unavailable."
}

if (-not (Test-Path -LiteralPath $fixScript -PathType Leaf)) {
    throw "Known-fix script was not found: $fixScript"
}

Write-Host "Applying verified Windows fixes..." -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $fixScript -SourceRoot $SourceRoot
if ($LASTEXITCODE -ne 0) {
    throw "Known Windows build fixes failed."
}

if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    throw "BUILD_WINDOWS_RELEASE.ps1 was not found in $SourceRoot"
}

$buildArguments = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", $buildScript,
    "-Configuration", $Configuration,
    "-TimestampUrl", $TimestampUrl
)

if ($SkipInstaller) { $buildArguments += "-SkipInstaller" }
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $buildArguments += @("-CertificateThumbprint", $CertificateThumbprint)
}

Write-Host "Starting the controlled Nexus POS Windows release build..." -ForegroundColor Cyan
& powershell.exe @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Nexus POS Windows release build failed."
}

$releaseRoot = Join-Path $SourceRoot "release"
$hashManifest = Join-Path $releaseRoot "SHA256SUMS.txt"
if (-not (Test-Path -LiteralPath $hashManifest -PathType Leaf)) {
    throw "Release build completed without SHA256SUMS.txt."
}

$artifacts = Get-ChildItem $releaseRoot -File |
    Where-Object { $_.Extension -in ".zip", ".exe" }

if (-not $artifacts) {
    throw "Release build completed without a ZIP or installer artifact."
}

$report = [ordered]@{
    generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    sourceRoot = $SourceRoot
    configuration = $Configuration
    installerSkipped = [bool]$SkipInstaller
    signed = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
    artifacts = @($artifacts | ForEach-Object {
        [ordered]@{
            name = $_.Name
            sizeBytes = $_.Length
            sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}

$reportPath = Join-Path $releaseRoot "WINDOWS_RELEASE_REPORT.json"
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host "Nexus POS Windows release completed and verified." -ForegroundColor Green
Write-Host "Artifacts: $releaseRoot" -ForegroundColor Green
Write-Host "Report: $reportPath" -ForegroundColor Green
