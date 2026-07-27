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

$prerequisiteScript = Join-Path $PSScriptRoot "INSTALL_BUILD_PREREQUISITES.ps1"
$fixScript = Join-Path $PSScriptRoot "APPLY_KNOWN_WINDOWS_BUILD_FIXES.ps1"
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
    if (-not (Test-Path -LiteralPath $kits -PathType Container)) {
        return $null
    }

    return Get-ChildItem $kits -Filter signtool.exe -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\signtool.exe" } |
        Sort-Object FullName -Descending |
        Select-Object -ExpandProperty FullName -First 1
}

$needDotNet = -not (Test-DotNet10Sdk)
$needInno = -not $SkipInstaller -and -not (Find-InnoCompiler)
$needSignTool = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and
    -not (Find-SignTool)

if ($needDotNet -or $needInno -or $needSignTool) {
    if ($NoAutomaticPrerequisiteInstall) {
        $missing = [System.Collections.Generic.List[string]]::new()
        if ($needDotNet) { $missing.Add(".NET 10 SDK") }
        if ($needInno) { $missing.Add("Inno Setup 6") }
        if ($needSignTool) { $missing.Add("Windows SDK SignTool") }
        throw "Missing prerequisites: $($missing -join ', ')"
    }

    if (-not (Test-Path -LiteralPath $prerequisiteScript -PathType Leaf)) {
        throw "Prerequisite installer was not found: $prerequisiteScript"
    }

    $args = [System.Collections.Generic.List[string]]::new()
    $args.Add("-NoProfile")
    $args.Add("-ExecutionPolicy")
    $args.Add("Bypass")
    $args.Add("-File")
    $args.Add('"' + $prerequisiteScript + '"')
    $args.Add("-NonInteractive")

    if (-not $needInno) { $args.Add("-IncludeInnoSetup:`$false") }
    if ($needSignTool) { $args.Add("-IncludeSigningTools") }

    Write-Host "Installing missing release prerequisites..." -ForegroundColor Cyan
    $process = Start-Process powershell.exe -Verb RunAs -ArgumentList $args -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Prerequisite installation failed with exit code $($process.ExitCode)."
    }

    $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
        [Environment]::GetEnvironmentVariable("Path", "User")
}

if (-not (Test-DotNet10Sdk)) {
    throw ".NET 10 SDK is unavailable after prerequisite installation. Reopen PowerShell and retry."
}
if (-not $SkipInstaller -and -not (Find-InnoCompiler)) {
    throw "Inno Setup 6 is unavailable after prerequisite installation."
}
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -and
    -not (Find-SignTool)) {
    throw "SignTool is unavailable after prerequisite installation."
}

if (-not (Test-Path -LiteralPath $fixScript -PathType Leaf)) {
    throw "Verified source/build correction script was not found: $fixScript"
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $fixScript -SourceRoot $SourceRoot
if ($LASTEXITCODE -ne 0) {
    throw "Verified source/build corrections failed."
}

if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    throw "BUILD_WINDOWS_RELEASE.ps1 was not found in $SourceRoot"
}

$buildArgs = [System.Collections.Generic.List[string]]::new()
$buildArgs.Add("-NoProfile")
$buildArgs.Add("-ExecutionPolicy")
$buildArgs.Add("Bypass")
$buildArgs.Add("-File")
$buildArgs.Add($buildScript)
$buildArgs.Add("-Configuration")
$buildArgs.Add($Configuration)
$buildArgs.Add("-TimestampUrl")
$buildArgs.Add($TimestampUrl)

if ($SkipInstaller) { $buildArgs.Add("-SkipInstaller") }
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $buildArgs.Add("-CertificateThumbprint")
    $buildArgs.Add($CertificateThumbprint)
}

& powershell.exe @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "Nexus POS Windows release build failed."
}

$releaseRoot = Join-Path $SourceRoot "release"
$manifest = Join-Path $releaseRoot "SHA256SUMS.txt"
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw "Release SHA256SUMS.txt was not created."
}

$artifacts = @(Get-ChildItem $releaseRoot -File |
    Where-Object { $_.Extension -in ".zip", ".exe" })
if ($artifacts.Count -eq 0) {
    throw "No ZIP or installer artifact was created."
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
$report | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host "Nexus POS Windows release completed and verified." -ForegroundColor Green
Write-Host "Artifacts: $releaseRoot" -ForegroundColor Green
Write-Host "Report: $reportPath" -ForegroundColor Green
