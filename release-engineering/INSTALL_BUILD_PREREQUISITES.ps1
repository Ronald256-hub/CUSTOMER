[CmdletBinding()]
param(
    [switch]$IncludeInnoSetup = $true,
    [switch]$IncludeSigningTools,
    [switch]$IncludeNodeJs = $true,
    [switch]$IncludeGit,
    [switch]$NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Refresh-ProcessPath {
    $machine = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $user = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:Path = "$machine;$user"
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

function Get-WingetCommand {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($winget) { return $winget.Source }

    try {
        Add-AppxPackage -RegisterByFamilyName -MainPackage `
            Microsoft.DesktopAppInstaller_8wekyb3d8bbwe `
            -ErrorAction Stop
    }
    catch {
        Write-Verbose "WinGet registration attempt did not complete: $($_.Exception.Message)"
    }

    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw "WinGet is required for automatic prerequisite installation. Install or update Microsoft App Installer, then run this script again."
    }

    return $winget.Source
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$DisplayName
    )

    $winget = Get-WingetCommand

    & $winget list --id $Id --exact --source winget `
        --accept-source-agreements --disable-interactivity *> $null

    if ($LASTEXITCODE -eq 0) {
        Write-Host "$DisplayName is already installed." -ForegroundColor Green
        return
    }

    Write-Host "Installing $DisplayName..." -ForegroundColor Cyan

    $arguments = @(
        "install", "--id", $Id, "--exact", "--source", "winget",
        "--accept-package-agreements", "--accept-source-agreements"
    )

    if ($NonInteractive) {
        $arguments += @("--silent", "--disable-interactivity")
    }

    & $winget @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$DisplayName installation failed with exit code $LASTEXITCODE."
    }

    Refresh-ProcessPath
}

if (-not [Environment]::Is64BitOperatingSystem) {
    throw "Nexus POS Windows release builds require 64-bit Windows."
}

if (-not (Test-Administrator)) {
    throw "Run PowerShell as Administrator so missing prerequisites can be installed safely."
}

Install-WingetPackage -Id "Microsoft.DotNet.SDK.10" -DisplayName ".NET 10 SDK"

if ($IncludeInnoSetup) {
    Install-WingetPackage -Id "JRSoftware.InnoSetup" -DisplayName "Inno Setup 6"
}

if ($IncludeNodeJs) {
    Install-WingetPackage -Id "OpenJS.NodeJS.LTS" -DisplayName "Node.js LTS"
}

if ($IncludeGit) {
    Install-WingetPackage -Id "Git.Git" -DisplayName "Git"
}

if ($IncludeSigningTools) {
    Install-WingetPackage -Id "Microsoft.WindowsSDK.10.0.28000" -DisplayName "Windows SDK signing tools"
}

Refresh-ProcessPath

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw ".NET SDK installation completed but dotnet.exe is not visible. Close PowerShell, open a new Administrator PowerShell window, and run the script again."
}

$versionText = (& $dotnet.Source --version 2>&1 | Out-String).Trim()
$parsed = $null
if (-not [version]::TryParse(($versionText -split '-')[0], [ref]$parsed) -or $parsed.Major -lt 10) {
    throw ".NET SDK 10 or later was not detected. Found: $versionText"
}

if ($IncludeInnoSetup -and -not (Find-InnoCompiler)) {
    throw "Inno Setup installation completed but ISCC.exe was not found. Reopen PowerShell and rerun this script."
}

if ($IncludeSigningTools -and -not (Find-SignTool)) {
    throw "Windows SDK installation completed but signtool.exe was not found. Reopen PowerShell and rerun this script."
}

$report = [ordered]@{
    generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    computerName = $env:COMPUTERNAME
    windowsVersion = [Environment]::OSVersion.Version.ToString()
    dotnetSdk = $versionText
    innoSetup = Find-InnoCompiler
    signTool = Find-SignTool
    node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
    git = (Get-Command git.exe -ErrorAction SilentlyContinue).Source
}

$reportPath = Join-Path $PSScriptRoot "windows-release-environment.json"
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host "Nexus POS release prerequisites are ready." -ForegroundColor Green
Write-Host "Environment report: $reportPath" -ForegroundColor Green
