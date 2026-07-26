[CmdletBinding()]
param(
    [string]$InstallerPath = "",
    [string]$InstallerUrl = "",
    [string]$ExpectedSha256 = "",
    [string]$RequiredPublisherThumbprint = "",
    [switch]$Silent,
    [switch]$AllowUnsignedTestBuild,
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Nexus POS is published self-contained for win-x64. Customer computers do not
# need the .NET SDK or a separately installed .NET runtime. Installing developer
# tooling on a customer's till would increase support risk and attack surface.

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Normalize-Thumbprint {
    param([string]$Thumbprint)
    return $Thumbprint.Replace(" ", "").ToUpperInvariant()
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected
    )

    $normalized = $Expected.Replace(" ", "").Trim().ToLowerInvariant()
    if ($normalized -notmatch '^[a-f0-9]{64}$') {
        throw "ExpectedSha256 must contain exactly 64 hexadecimal characters."
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $normalized) {
        throw "Installer SHA-256 mismatch. Expected $normalized, received $actual."
    }
}

function Assert-AuthenticodePublisher {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$RequiredThumbprint,
        [switch]$AllowUnsigned
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path

    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        if ($AllowUnsigned) {
            Write-Warning "The installer is unsigned or untrusted. This exception is allowed only for controlled development testing."
            return $null
        }

        throw "The Nexus POS installer does not have a valid trusted Authenticode signature. Status: $($signature.Status)."
    }

    if (-not [string]::IsNullOrWhiteSpace($RequiredThumbprint)) {
        $required = Normalize-Thumbprint $RequiredThumbprint
        if ($required -notmatch '^[A-F0-9]{40}$') {
            throw "RequiredPublisherThumbprint must contain exactly 40 hexadecimal characters."
        }

        $actual = Normalize-Thumbprint $signature.SignerCertificate.Thumbprint
        if ($actual -ne $required) {
            throw "Publisher certificate mismatch. Expected $required, received $actual."
        }
    }

    return $signature
}

function Find-NexusInstallLocation {
    $registryPaths = @(
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    $entry = Get-ItemProperty $registryPaths -ErrorAction SilentlyContinue |
        Where-Object {
            $_.DisplayName -like "Nexus POS*" -or
            $_.DisplayName -like "ROBO CASK & TAP POS*"
        } |
        Sort-Object DisplayVersion -Descending |
        Select-Object -First 1

    if ($entry -and -not [string]::IsNullOrWhiteSpace([string]$entry.InstallLocation)) {
        return ([string]$entry.InstallLocation).TrimEnd('\')
    }

    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA "Programs\Nexus POS"),
        (Join-Path $env:ProgramFiles "Nexus POS"),
        (Join-Path ${env:ProgramFiles(x86)} "Nexus POS")
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Container)) {
            return $candidate
        }
    }

    return $null
}

if (-not [Environment]::Is64BitOperatingSystem) {
    throw "Nexus POS requires 64-bit Windows."
}

$windowsVersion = [Environment]::OSVersion.Version
if ($windowsVersion.Major -lt 10 -or $windowsVersion.Build -lt 17763) {
    throw "Nexus POS requires Windows 10 version 1809 or later. Detected: $windowsVersion"
}

$systemDrive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($env:SystemDrive)'"
if ($systemDrive.FreeSpace -lt 2GB) {
    throw "At least 2 GB of free space is required on $($env:SystemDrive)."
}

if (-not (Test-Administrator)) {
    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add("-NoProfile")
    $arguments.Add("-ExecutionPolicy")
    $arguments.Add("Bypass")
    $arguments.Add("-File")
    $arguments.Add('"' + $PSCommandPath + '"')

    foreach ($pair in @(
        @{ Name = "InstallerPath"; Value = $InstallerPath },
        @{ Name = "InstallerUrl"; Value = $InstallerUrl },
        @{ Name = "ExpectedSha256"; Value = $ExpectedSha256 },
        @{ Name = "RequiredPublisherThumbprint"; Value = $RequiredPublisherThumbprint }
    )) {
        if (-not [string]::IsNullOrWhiteSpace($pair.Value)) {
            $arguments.Add("-$($pair.Name)")
            $arguments.Add('"' + $pair.Value.Replace('"', '\"') + '"')
        }
    }

    if ($Silent) { $arguments.Add("-Silent") }
    if ($AllowUnsignedTestBuild) { $arguments.Add("-AllowUnsignedTestBuild") }
    if ($NoLaunch) { $arguments.Add("-NoLaunch") }

    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}

$temporaryDownload = $null

try {
    if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
        $InstallerPath = Get-ChildItem $PSScriptRoot -Filter "Nexus_POS_Setup_*.exe" -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -ExpandProperty FullName -First 1
    }

    if ([string]::IsNullOrWhiteSpace($InstallerPath) -or
        -not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
        if ([string]::IsNullOrWhiteSpace($InstallerUrl)) {
            throw "No Nexus POS installer was found beside this script. Supply -InstallerPath or a trusted HTTPS -InstallerUrl."
        }

        $uri = [Uri]$InstallerUrl
        if ($uri.Scheme -ne "https") {
            throw "InstallerUrl must use HTTPS."
        }

        if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) {
            throw "ExpectedSha256 is required when the installer is downloaded."
        }

        $temporaryDownload = Join-Path $env:TEMP ("Nexus_POS_Setup_" + [guid]::NewGuid().ToString("N") + ".exe")
        Write-Host "Downloading the verified Nexus POS installer..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $uri -OutFile $temporaryDownload -UseBasicParsing
        $InstallerPath = $temporaryDownload
    }

    $InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path

    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        Assert-Sha256 -Path $InstallerPath -Expected $ExpectedSha256
    }

    $signature = Assert-AuthenticodePublisher `
        -Path $InstallerPath `
        -RequiredThumbprint $RequiredPublisherThumbprint `
        -AllowUnsigned:$AllowUnsignedTestBuild

    $logDirectory = Join-Path $env:ProgramData "Nexus POS\Install Logs"
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $logPath = Join-Path $logDirectory ("install-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")

    $installerArguments = [System.Collections.Generic.List[string]]::new()
    $installerArguments.Add("/SP-")
    $installerArguments.Add("/NORESTART")
    $installerArguments.Add('/LOG="' + $logPath + '"')

    if ($Silent) {
        $installerArguments.Add("/VERYSILENT")
        $installerArguments.Add("/SUPPRESSMSGBOXES")
    }

    Write-Host "Installing Nexus POS..." -ForegroundColor Cyan
    $process = Start-Process -FilePath $InstallerPath -ArgumentList $installerArguments -Wait -PassThru
    if ($process.ExitCode -notin 0, 3010) {
        throw "Nexus POS installer failed with exit code $($process.ExitCode). Review $logPath"
    }

    $installLocation = Find-NexusInstallLocation
    if ([string]::IsNullOrWhiteSpace($installLocation)) {
        throw "The installer completed but the Nexus POS installation directory could not be verified. Review $logPath"
    }

    $launcher = @(
        (Join-Path $installLocation "app\launcher-runtime\Nexus.Pos.Launcher.exe"),
        (Join-Path $installLocation "launcher-runtime\Nexus.Pos.Launcher.exe"),
        (Join-Path $installLocation "Nexus.Pos.Launcher.exe")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1

    if (-not $launcher) {
        throw "Nexus POS was installed, but the launcher is missing from $installLocation. Run the installer again in Repair mode."
    }

    if (-not [string]::IsNullOrWhiteSpace($RequiredPublisherThumbprint)) {
        Assert-AuthenticodePublisher -Path $launcher -RequiredThumbprint $RequiredPublisherThumbprint | Out-Null
    }

    $report = [ordered]@{
        installedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        installer = $InstallerPath
        installerSha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        publisher = if ($signature) { $signature.SignerCertificate.Subject } else { "unsigned-test-build" }
        installLocation = $installLocation
        launcher = $launcher
        installerLog = $logPath
        rebootRequired = $process.ExitCode -eq 3010
    }

    $reportPath = Join-Path $logDirectory ("install-report-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8

    Write-Host ""
    Write-Host "Nexus POS installation was verified successfully." -ForegroundColor Green
    Write-Host "Installation: $installLocation" -ForegroundColor Green
    Write-Host "Report: $reportPath" -ForegroundColor Green

    if (-not $NoLaunch -and $process.ExitCode -ne 3010) {
        Start-Process -FilePath $launcher
    }

    if ($process.ExitCode -eq 3010) {
        Write-Warning "Windows must be restarted before Nexus POS is launched."
    }
}
finally {
    if ($temporaryDownload -and (Test-Path -LiteralPath $temporaryDownload -PathType Leaf)) {
        Remove-Item -LiteralPath $temporaryDownload -Force -ErrorAction SilentlyContinue
    }
}
