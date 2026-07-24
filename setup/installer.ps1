$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$PackageRoot = Split-Path -Parent (
    Split-Path -Parent $MyInvocation.MyCommand.Path
)

$SourceApp = Join-Path $PackageRoot "app"

$InstallPath = Join-Path `
    $env:LOCALAPPDATA `
    "Programs\ROBO CASK TAP POS"

$DataDir = Join-Path `
    $env:LOCALAPPDATA `
    "ROBO CASK TAP POS\Data"

$BackupRoot = Join-Path `
    $DataDir `
    "Backups"

$CommonDocuments = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::CommonDocuments
)

$DocumentRoot = Join-Path `
    $CommonDocuments `
    "ROBO CASK TAP POS\Audit Documents"

$StartMenuFolder = Join-Path `
    $env:APPDATA `
    "Microsoft\Windows\Start Menu\Programs\ROBO CASK & TAP POS"

$DesktopShortcut = Join-Path `
    ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::Desktop
    )) `
    "ROBO CASK & TAP POS.lnk"

$UninstallRegistryKey = (
    "HKCU:\Software\Microsoft\Windows\" +
    "CurrentVersion\Uninstall\ROBOCaskTapPOS"
)

$ApplicationVersion = "3.0.0"

$SetupLog = Join-Path `
    $DataDir `
    "setup.log"

$StageRoot = Join-Path `
    $env:TEMP `
    (
        "robo-pos-install-" +
        [Guid]::NewGuid().ToString("N")
    )

$PreviousInstall = $null

function Write-SetupLog {
    param([string]$Message)

    try {
        New-Item `
            -ItemType Directory `
            -Force `
            -Path $DataDir |
            Out-Null

        Add-Content `
            -Path $SetupLog `
            -Value "$(Get-Date -Format s) $Message" `
            -Encoding UTF8
    }
    catch {
        # Logging must never prevent installation.
    }
}

function Show-RoboMessage {
    param(
        [string]$Message,
        [string]$Title,
        [string]$Type = "Information"
    )

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

function Confirm-Installation {
    $existingText = ""

    if (Test-Path $InstallPath -PathType Container) {
        $existingText = (
            "`r`n`r`nAn existing installation will be upgraded. " +
            "Its business database, backups and audit documents " +
            "will remain untouched."
        )
    }

    $message = (
        "Install ROBO CASK & TAP POS $ApplicationVersion?" +
        "`r`n`r`n" +
        "Application folder:" +
        "`r`n" +
        $InstallPath +
        "`r`n`r`n" +
        "Business database and backups:" +
        "`r`n" +
        $DataDir +
        "`r`n`r`n" +
        "Receipts, invoices and audit documents:" +
        "`r`n" +
        $DocumentRoot +
        "`r`n`r`n" +
        "This is a per-user installation and does not require " +
        "a separate .NET installation." +
        $existingText
    )

    $answer = [System.Windows.Forms.MessageBox]::Show(
        $message,
        "ROBO CASK & TAP POS Setup",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Information,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button1
    )

    return (
        $answer -eq
        [System.Windows.Forms.DialogResult]::Yes
    )
}

function Test-SafeInstallPath {
    $fullInstallPath = [System.IO.Path]::GetFullPath(
        $InstallPath
    ).TrimEnd("\")

    $fullDataPath = [System.IO.Path]::GetFullPath(
        $DataDir
    ).TrimEnd("\")

    $fullDocumentPath = [System.IO.Path]::GetFullPath(
        $DocumentRoot
    ).TrimEnd("\")

    if ($fullInstallPath.Length -lt 12) {
        throw "The installation path is not safe."
    }

    if (
        [string]::Equals(
            $fullInstallPath,
            $fullDataPath,
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        throw (
            "The program folder cannot be the same as " +
            "the business-data folder."
        )
    }

    if (
        [string]::Equals(
            $fullInstallPath,
            $fullDocumentPath,
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        throw (
            "The program folder cannot be the same as " +
            "the audit-document folder."
        )
    }
}

function Test-SourcePackage {
    $requiredFiles = @(
        "launcher.ps1",
        "diagnose.ps1",
        "uninstall.ps1",
        "robo.ico",
        "runtime\Robo.Pos.Server.exe",
        "runtime\Robo.Pos.Server.dll",
        "runtime\Robo.Pos.Server.deps.json",
        "runtime\Robo.Pos.Server.runtimeconfig.json",
        "runtime\wwwroot\index.html",
        "runtime\wwwroot\app.js",
        "runtime\wwwroot\business.js",
        "runtime\wwwroot\system-admin.js",
        "runtime\wwwroot\styles.css"
    )

    foreach ($relativePath in $requiredFiles) {
        $path = Join-Path $SourceApp $relativePath

        if (-not (Test-Path $path -PathType Leaf)) {
            throw (
                "The installation package is incomplete." +
                "`r`n`r`nMissing file:" +
                "`r`n" +
                $relativePath
            )
        }
    }
}

function Stop-InstalledServer {
    $serverPidFile = Join-Path `
        $DataDir `
        "server.pid"

    $expectedExecutable = Join-Path `
        $InstallPath `
        "runtime\Robo.Pos.Server.exe"

    $expectedFullPath = [System.IO.Path]::GetFullPath(
        $expectedExecutable
    )

    if (Test-Path $serverPidFile -PathType Leaf) {
        try {
            $pidText = (
                Get-Content `
                    -Path $serverPidFile `
                    -Encoding ASCII `
                    -ErrorAction Stop |
                Select-Object -First 1
            )

            [int]$serverPid = 0

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
                        [string]::Equals(
                            [System.IO.Path]::GetFullPath(
                                $processPath
                            ),
                            $expectedFullPath,
                            [StringComparison]::OrdinalIgnoreCase
                        )
                    ) {
                        Stop-Process `
                            -Id $process.Id `
                            -Force `
                            -ErrorAction Stop

                        Write-SetupLog (
                            "Stopped secure server process " +
                            $process.Id
                        )
                    }
                }
            }
        }
        catch {
            Write-SetupLog (
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
            [string]::Equals(
                [System.IO.Path]::GetFullPath(
                    $_.ExecutablePath
                ),
                $expectedFullPath,
                [StringComparison]::OrdinalIgnoreCase
            )
        } |
        ForEach-Object {
            Stop-Process `
                -Id $_.ProcessId `
                -Force `
                -ErrorAction SilentlyContinue

            Write-SetupLog (
                "Stopped secure server process " +
                $_.ProcessId
            )
        }
    }
    catch {
        Write-SetupLog (
            "Process scan warning: " +
            $_.Exception.Message
        )
    }

    Remove-Item `
        -Path $serverPidFile `
        -Force `
        -ErrorAction SilentlyContinue
}

function Copy-SecureApplication {
    New-Item `
        -ItemType Directory `
        -Force `
        -Path $StageRoot |
        Out-Null

    Copy-Item `
        -Path (Join-Path $SourceApp "*") `
        -Destination $StageRoot `
        -Recurse `
        -Force

    $stageExecutable = Join-Path `
        $StageRoot `
        "runtime\Robo.Pos.Server.exe"

    if (-not (Test-Path $stageExecutable -PathType Leaf)) {
        throw "The secure runtime was not staged correctly."
    }

    Stop-InstalledServer

    $installParent = Split-Path -Parent $InstallPath

    New-Item `
        -ItemType Directory `
        -Force `
        -Path $installParent |
        Out-Null

    if (Test-Path $InstallPath -PathType Container) {
        $script:PreviousInstall = (
            $InstallPath +
            ".previous-" +
            (Get-Date -Format "yyyyMMddHHmmss")
        )

        Move-Item `
            -Path $InstallPath `
            -Destination $script:PreviousInstall `
            -Force
    }

    Move-Item `
        -Path $StageRoot `
        -Destination $InstallPath

    if (-not (
        Test-Path `
            (Join-Path `
                $InstallPath `
                "runtime\Robo.Pos.Server.exe") `
            -PathType Leaf
    )) {
        throw "The installed secure runtime could not be verified."
    }

}

function New-RoboShortcut {
    param(
        [string]$Path,
        [string]$Target,
        [string]$Arguments,
        [string]$WorkingDirectory,
        [string]$Icon,
        [string]$Description
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)

    $shortcut.TargetPath = $Target
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$Icon,0"
    $shortcut.Description = $Description

    $shortcut.Save()
}

function Register-Application {
    $powershellPath = (
        Get-Command powershell.exe
    ).Source

    $launcherScript = Join-Path `
        $InstallPath `
        "launcher.ps1"

    $diagnosticScript = Join-Path `
        $InstallPath `
        "diagnose.ps1"

    $uninstallScript = Join-Path `
        $InstallPath `
        "uninstall.ps1"

    $iconPath = Join-Path `
        $InstallPath `
        "robo.ico"

    $launchArguments = (
        "-NoProfile -ExecutionPolicy Bypass " +
        "-STA -WindowStyle Hidden -File " +
        "`"$launcherScript`""
    )

    $diagnosticArguments = (
        "-NoProfile -ExecutionPolicy Bypass " +
        "-STA -File `"$diagnosticScript`""
    )

    $uninstallArguments = (
        "-NoProfile -ExecutionPolicy Bypass " +
        "-STA -File `"$uninstallScript`""
    )

    New-Item `
        -ItemType Directory `
        -Force `
        -Path $StartMenuFolder |
        Out-Null

    New-RoboShortcut `
        $DesktopShortcut `
        $powershellPath `
        $launchArguments `
        $InstallPath `
        $iconPath `
        "Open ROBO CASK & TAP POS"

    New-RoboShortcut `
        (Join-Path `
            $StartMenuFolder `
            "ROBO CASK & TAP POS.lnk") `
        $powershellPath `
        $launchArguments `
        $InstallPath `
        $iconPath `
        "Open ROBO CASK & TAP POS"

    New-RoboShortcut `
        (Join-Path `
            $StartMenuFolder `
            "Repair and Diagnose.lnk") `
        $powershellPath `
        $diagnosticArguments `
        $InstallPath `
        $iconPath `
        "Run ROBO POS diagnostics"

    New-RoboShortcut `
        (Join-Path `
            $StartMenuFolder `
            "Uninstall ROBO POS.lnk") `
        $powershellPath `
        $uninstallArguments `
        $InstallPath `
        $iconPath `
        "Remove ROBO CASK & TAP POS"

    New-Item `
        -Path $UninstallRegistryKey `
        -Force |
        Out-Null

    $uninstallCommand = (
        "`"$powershellPath`" " +
        $uninstallArguments
    )

    $estimatedSize = [int](
        (
            Get-ChildItem `
                -Path $InstallPath `
                -File `
                -Recurse |
            Measure-Object `
                -Property Length `
                -Sum
        ).Sum / 1KB
    )

    $registryValues = @{
        DisplayName = "ROBO CASK & TAP POS"
        DisplayVersion = $ApplicationVersion
        Publisher = "ROBO CASK & TAP"
        InstallLocation = $InstallPath
        DisplayIcon = $iconPath
        UninstallString = $uninstallCommand
        QuietUninstallString = $uninstallCommand
        InstallDate = (Get-Date -Format "yyyyMMdd")
    }

    foreach ($entry in $registryValues.GetEnumerator()) {
        New-ItemProperty `
            -Path $UninstallRegistryKey `
            -Name $entry.Key `
            -Value $entry.Value `
            -PropertyType String `
            -Force |
            Out-Null
    }

    New-ItemProperty `
        -Path $UninstallRegistryKey `
        -Name EstimatedSize `
        -Value $estimatedSize `
        -PropertyType DWord `
        -Force |
        Out-Null

    New-ItemProperty `
        -Path $UninstallRegistryKey `
        -Name NoModify `
        -Value 1 `
        -PropertyType DWord `
        -Force |
        Out-Null

    New-ItemProperty `
        -Path $UninstallRegistryKey `
        -Name NoRepair `
        -Value 1 `
        -PropertyType DWord `
        -Force |
        Out-Null
}

function Write-InstallationInformation {
    $versionFile = Join-Path `
        $InstallPath `
        "VERSION.txt"

    $informationFile = Join-Path `
        $InstallPath `
        "INSTALLATION.txt"

    @"
ROBO CASK & TAP POS
Version: $ApplicationVersion
Runtime: Windows x64 self-contained
Database: SQLite
"@ |
    Set-Content `
        -Path $versionFile `
        -Encoding UTF8

    @"
ROBO CASK & TAP POS

Application folder:
$InstallPath

SQLite database and backups:
$DataDir

Receipts, invoices and audit documents:
$DocumentRoot

Temporary first-login credentials are generated securely when the
application creates a new database for the first time.

Every user must change the temporary password after first login.
"@ |
    Set-Content `
        -Path $informationFile `
        -Encoding UTF8
}

function Complete-PreviousInstallationRemoval {
    if (
        $script:PreviousInstall -and
        (Test-Path $script:PreviousInstall)
    ) {
        Remove-Item `
            -Path $script:PreviousInstall `
            -Recurse `
            -Force

        Write-SetupLog (
            "Previous application files were removed " +
            "after successful installation."
        )

        $script:PreviousInstall = $null
    }
}

function Restore-PreviousInstallation {
    try {
        if (
            $script:PreviousInstall -and
            (Test-Path $script:PreviousInstall)
        ) {
            if (Test-Path $InstallPath) {
                Remove-Item `
                    -Path $InstallPath `
                    -Recurse `
                    -Force
            }

            Move-Item `
                -Path $script:PreviousInstall `
                -Destination $InstallPath `
                -Force

            Write-SetupLog (
                "Previous application files were restored."
            )

            $script:PreviousInstall = $null
        }
        elseif (Test-Path $InstallPath) {
            Remove-Item `
                -Path $InstallPath `
                -Recurse `
                -Force `
                -ErrorAction SilentlyContinue

            Write-SetupLog (
                "Incomplete fresh installation files were removed."
            )
        }
    }
    catch {
        Write-SetupLog (
            "Previous-install restoration failed: " +
            $_.Exception.Message
        )
    }
}

try {
    if (-not (Confirm-Installation)) {
        Write-SetupLog "Installation cancelled."
        exit 0
    }

    Write-SetupLog (
        "Installation started. Version=" +
        $ApplicationVersion
    )

    Test-SafeInstallPath
    Test-SourcePackage

    New-Item `
        -ItemType Directory `
        -Force `
        -Path $DataDir |
        Out-Null

    New-Item `
        -ItemType Directory `
        -Force `
        -Path $BackupRoot |
        Out-Null

    New-Item `
        -ItemType Directory `
        -Force `
        -Path $DocumentRoot |
        Out-Null

    Copy-SecureApplication
    Register-Application
    Write-InstallationInformation
    Complete-PreviousInstallationRemoval

    Write-SetupLog (
        "Installation completed successfully. " +
        "ApplicationPath=$InstallPath"
    )

    $launchAnswer = [System.Windows.Forms.MessageBox]::Show(
        (
            "ROBO CASK & TAP POS $ApplicationVersion was " +
            "installed successfully." +
            "`r`n`r`n" +
            "Your secure first-login credentials will be " +
            "generated when the new database starts." +
            "`r`n`r`n" +
            "Launch the application now?"
        ),
        "Installation Completed",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Information,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button1
    )

    if (
        $launchAnswer -eq
        [System.Windows.Forms.DialogResult]::Yes
    ) {
        $installedLauncher = Join-Path `
            $InstallPath `
            "launcher.ps1"

        Start-Process `
            -FilePath "powershell.exe" `
            -ArgumentList (
                "-NoProfile -ExecutionPolicy Bypass " +
                "-STA -WindowStyle Hidden -File " +
                "`"$installedLauncher`""
            ) |
            Out-Null
    }

    exit 0
}
catch {
    Write-SetupLog (
        "ERROR " +
        $_.Exception.ToString()
    )

    Restore-PreviousInstallation

    Remove-Item `
        -Path $StageRoot `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue

    Show-RoboMessage `
        (
            "ROBO CASK & TAP POS could not be installed." +
            "`r`n`r`n" +
            $_.Exception.Message +
            "`r`n`r`n" +
            "The business database, backups, receipts and " +
            "audit documents were not deleted."
        ) `
        "Installation Error" `
        "Error"

    exit 1
}
finally {
    Remove-Item `
        -Path $StageRoot `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue
}
