$ErrorActionPreference = "Stop"

try {
    Add-Type -AssemblyName System.Windows.Forms
}
catch {
}

$AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

$RuntimeRoot = Join-Path `
    $AppRoot `
    "runtime"

$ServerExe = Join-Path `
    $RuntimeRoot `
    "Robo.Pos.Server.exe"

$DataDir = Join-Path `
    $env:LOCALAPPDATA `
    "ROBO CASK TAP POS\Data"

$ServerPidFile = Join-Path `
    $DataDir `
    "server.pid"

$UninstallLog = Join-Path `
    $DataDir `
    "uninstall.log"

$CommonDocuments = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::CommonDocuments
)

$DocumentRoot = Join-Path `
    $CommonDocuments `
    "ROBO CASK TAP POS\Audit Documents"

$DesktopShortcut = Join-Path `
    ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::Desktop
    )) `
    "ROBO CASK & TAP POS.lnk"

$StartMenuFolder = Join-Path `
    $env:APPDATA `
    "Microsoft\Windows\Start Menu\Programs\ROBO CASK & TAP POS"

$UninstallRegistryKey = (
    "HKCU:\Software\Microsoft\Windows\" +
    "CurrentVersion\Uninstall\ROBOCaskTapPOS"
)

function Write-UninstallLog {
    param([string]$Message)

    try {
        New-Item `
            -ItemType Directory `
            -Force `
            -Path $DataDir |
            Out-Null

        Add-Content `
            -Path $UninstallLog `
            -Value "$(Get-Date -Format s) $Message" `
            -Encoding UTF8
    }
    catch {
        # Logging must never prevent removal.
    }
}

function Show-RoboMessage {
    param(
        [string]$Message,
        [string]$Title,
        [string]$Type = "Information"
    )

    try {
        $icon = switch ($Type) {
            "Error" {
                [System.Windows.Forms.MessageBoxIcon]::Error
            }

            "Warning" {
                [System.Windows.Forms.MessageBoxIcon]::Warning
            }

            default {
                [System.Windows.Forms.MessageBoxIcon]::Information
            }
        }

        [System.Windows.Forms.MessageBox]::Show(
            $Message,
            $Title,
            [System.Windows.Forms.MessageBoxButtons]::OK,
            $icon
        ) | Out-Null
    }
    catch {
        Write-Host $Message
    }
}

function Confirm-Uninstall {
    $message = (
        "Remove ROBO CASK & TAP POS from this computer?" +
        "`r`n`r`n" +
        "The installed application and shortcuts will be removed." +
        "`r`n`r`n" +
        "The following business information will be preserved:" +
        "`r`n" +
        "- SQLite database" +
        "`r`n" +
        "- Database backups" +
        "`r`n" +
        "- Receipts and invoices" +
        "`r`n" +
        "- Audit documents" +
        "`r`n" +
        "- Application logs" +
        "`r`n`r`n" +
        "Data folder:" +
        "`r`n" +
        $DataDir +
        "`r`n`r`n" +
        "Audit documents:" +
        "`r`n" +
        $DocumentRoot
    )

    try {
        $answer = [System.Windows.Forms.MessageBox]::Show(
            $message,
            "Uninstall ROBO CASK & TAP POS",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning,
            [System.Windows.Forms.MessageBoxDefaultButton]::Button2
        )

        return (
            $answer -eq
            [System.Windows.Forms.DialogResult]::Yes
        )
    }
    catch {
        Write-Host $message
        Write-Host ""
        $answer = Read-Host "Type REMOVE to continue"

        return $answer -eq "REMOVE"
    }
}

function Stop-RoboServer {
    $expectedPath = $null

    try {
        $expectedPath = [System.IO.Path]::GetFullPath(
            $ServerExe
        )
    }
    catch {
    }

    if (Test-Path $ServerPidFile -PathType Leaf) {
        try {
            $pidText = (
                Get-Content `
                    -Path $ServerPidFile `
                    -Encoding ASCII `
                    -ErrorAction Stop |
                Select-Object -First 1
            )

            $serverPid = 0

            if (
                [int]::TryParse(
                    $pidText,
                    [ref]$serverPid
                )
            ) {
                $process = Get-Process `
                    -Id $serverPid `
                    -ErrorAction SilentlyContinue

                if ($null -ne $process) {
                    $processPath = $null

                    try {
                        $processPath = $process.Path
                    }
                    catch {
                    }

                    if (
                        $processPath -and
                        $expectedPath -and
                        [string]::Equals(
                            [System.IO.Path]::GetFullPath(
                                $processPath
                            ),
                            $expectedPath,
                            [StringComparison]::OrdinalIgnoreCase
                        )
                    ) {
                        Stop-Process `
                            -Id $process.Id `
                            -Force `
                            -ErrorAction Stop

                        Write-UninstallLog (
                            "Stopped secure server process " +
                            $process.Id
                        )
                    }
                }
            }
        }
        catch {
            Write-UninstallLog (
                "PID shutdown warning: " +
                $_.Exception.Message
            )
        }
    }

    try {
        Get-CimInstance `
            Win32_Process `
            -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ExecutablePath -and
            $expectedPath -and
            [string]::Equals(
                [System.IO.Path]::GetFullPath(
                    $_.ExecutablePath
                ),
                $expectedPath,
                [StringComparison]::OrdinalIgnoreCase
            )
        } |
        ForEach-Object {
            Stop-Process `
                -Id $_.ProcessId `
                -Force `
                -ErrorAction SilentlyContinue

            Write-UninstallLog (
                "Stopped secure server process " +
                $_.ProcessId
            )
        }
    }
    catch {
        Write-UninstallLog (
            "Process scan warning: " +
            $_.Exception.Message
        )
    }

    Remove-Item `
        -Path $ServerPidFile `
        -Force `
        -ErrorAction SilentlyContinue
}

function Remove-ApplicationRegistration {
    Remove-Item `
        -Path $DesktopShortcut `
        -Force `
        -ErrorAction SilentlyContinue

    Remove-Item `
        -Path $StartMenuFolder `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue

    Remove-Item `
        -Path $UninstallRegistryKey `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue
}

function Schedule-ApplicationFolderRemoval {
    $cleanupFile = Join-Path `
        $env:TEMP `
        (
            "robo-pos-uninstall-" +
            [Guid]::NewGuid().ToString("N") +
            ".cmd"
        )

    $cleanupContent = @"
@echo off
timeout /t 3 /nobreak >nul
rmdir /s /q "$AppRoot"
del /f /q "%~f0"
"@

    Set-Content `
        -Path $cleanupFile `
        -Value $cleanupContent `
        -Encoding ASCII

    Start-Process `
        -FilePath $cleanupFile `
        -WindowStyle Hidden |
        Out-Null
}

try {
    if (-not (Confirm-Uninstall)) {
        Write-UninstallLog "Uninstallation cancelled."
        exit 0
    }

    Write-UninstallLog (
        "Uninstallation started. " +
        "ApplicationRoot=$AppRoot"
    )

    Stop-RoboServer
    Remove-ApplicationRegistration
    Schedule-ApplicationFolderRemoval

    Write-UninstallLog (
        "Application removal scheduled. " +
        "Business data and audit documents preserved."
    )

    Show-RoboMessage `
        (
            "ROBO CASK & TAP POS has been removed." +
            "`r`n`r`n" +
            "Your SQLite database, backups, receipts, " +
            "invoices and audit documents were preserved." +
            "`r`n`r`n" +
            "Business data:" +
            "`r`n" +
            $DataDir +
            "`r`n`r`n" +
            "Audit documents:" +
            "`r`n" +
            $DocumentRoot
        ) `
        "Uninstallation Completed"

    exit 0
}
catch {
    Write-UninstallLog (
        "ERROR " +
        $_.Exception.ToString()
    )

    Show-RoboMessage `
        (
            "ROBO CASK & TAP POS could not be removed." +
            "`r`n`r`n" +
            $_.Exception.Message +
            "`r`n`r`n" +
            "Your business data was not deleted."
        ) `
        "Uninstallation Error" `
        "Error"

    exit 1
}
