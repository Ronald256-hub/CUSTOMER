param(
    [switch]$Portable,
    [string]$DataDir = ""
)

$ErrorActionPreference = "Continue"

$AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageRoot = Split-Path -Parent $AppRoot
$RuntimeRoot = Join-Path $AppRoot "runtime"
$Launcher = Join-Path $AppRoot "launcher.ps1"
$ServerExe = Join-Path $RuntimeRoot "Robo.Pos.Server.exe"

if ([string]::IsNullOrWhiteSpace($DataDir)) {
    if ($Portable) {
        $DataDir = Join-Path $PackageRoot "portable-data"
    }
    else {
        $DataDir = Join-Path `
            $env:LOCALAPPDATA `
            "ROBO CASK TAP POS\Data"
    }
}

if ($Portable) {
    $DocumentRoot = Join-Path `
        $DataDir `
        "Audit Documents"
}
else {
    $CommonDocuments = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonDocuments
    )

    $DocumentRoot = Join-Path `
        $CommonDocuments `
        "ROBO CASK TAP POS\Audit Documents"
}

$DatabaseFile = Join-Path $DataDir "robo-pos.db"
$BackupRoot = Join-Path $DataDir "Backups"
$CredentialFile = Join-Path `
    $DataDir `
    "FIRST_LOGIN_CREDENTIALS.txt"

$NetworkModeFile = Join-Path `
    $DataDir `
    "shop-network.enabled"

$LauncherLog = Join-Path $DataDir "launcher.log"
$ServerOutputLog = Join-Path $DataDir "server-output.log"
$ServerErrorLog = Join-Path $DataDir "server-error.log"
$ReportPath = Join-Path $DataDir "DIAGNOSTIC_REPORT.txt"

$Results =
    New-Object System.Collections.Generic.List[string]

function Add-Result {
    param([string]$Text)

    $Results.Add($Text)
}

function Test-RequiredFile {
    param(
        [string]$RelativePath,
        [string]$BasePath = $RuntimeRoot
    )

    $fullPath = Join-Path $BasePath $RelativePath

    if (Test-Path $fullPath -PathType Leaf) {
        Add-Result "$RelativePath : OK"
        return $true
    }

    Add-Result "$RelativePath : MISSING"
    return $false
}

function Test-DirectoryWrite {
    param(
        [string]$Path,
        [string]$Label
    )

    try {
        New-Item `
            -ItemType Directory `
            -Force `
            -Path $Path |
            Out-Null

        $testFile = Join-Path `
            $Path `
            "robo-write-test.tmp"

        Set-Content `
            -Path $testFile `
            -Value "write-test" `
            -Encoding ASCII

        Remove-Item `
            $testFile `
            -Force

        Add-Result "$Label write test: OK"
        return $true
    }
    catch {
        Add-Result (
            "$Label write test: FAILED - " +
            $_.Exception.Message
        )

        return $false
    }
}

function Test-RoboHealth {
    param([int]$Port)

    try {
        $response = Invoke-WebRequest `
            -Uri "http://127.0.0.1:$Port/api/v3/health" `
            -UseBasicParsing `
            -TimeoutSec 2

        if (
            $response.StatusCode -eq 200 -and
            $response.Content -like "*ROBO CASK*" -and
            $response.Content -like "*schemaVersion*"
        ) {
            return $response.Content
        }
    }
    catch {
    }

    return $null
}

function Show-Report {
    param([string]$Report)

    try {
        Add-Type -AssemblyName System.Windows.Forms

        [System.Windows.Forms.MessageBox]::Show(
            $Report +
            "`r`n`r`nThe full report was saved to:`r`n" +
            $ReportPath,
            "ROBO POS Diagnostics",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
    }
    catch {
        Write-Host $Report
        Write-Host ""
        Write-Host "Saved to: $ReportPath"
    }
}

Add-Result "ROBO CASK & TAP POS - DIAGNOSTIC REPORT"
Add-Result "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Add-Result "Computer: $env:COMPUTERNAME"
Add-Result "Windows user: $env:USERNAME"
Add-Result "PowerShell version: $($PSVersionTable.PSVersion)"
Add-Result ""
Add-Result "APPLICATION FILES"

$ApplicationFilesOk = $true

if (-not (Test-RequiredFile `
        "launcher.ps1" `
        $AppRoot)) {
    $ApplicationFilesOk = $false
}

foreach ($requiredFile in @(
    "Robo.Pos.Server.exe",
    "Robo.Pos.Server.dll",
    "Robo.Pos.Server.deps.json",
    "Robo.Pos.Server.runtimeconfig.json",
    "wwwroot\index.html",
    "wwwroot\app.js",
    "wwwroot\business.js",
    "wwwroot\system-admin.js",
    "wwwroot\styles.css"
)) {
    if (-not (Test-RequiredFile $requiredFile)) {
        $ApplicationFilesOk = $false
    }
}

Add-Result ""
Add-Result "STORAGE"

Add-Result "Data folder: $DataDir"
Add-Result "Database file: $DatabaseFile"
Add-Result "Backup folder: $BackupRoot"
Add-Result "Audit documents: $DocumentRoot"

$DataWritable = Test-DirectoryWrite `
    $DataDir `
    "Data folder"

$DocumentsWritable = Test-DirectoryWrite `
    $DocumentRoot `
    "Audit-document folder"

try {
    New-Item `
        -ItemType Directory `
        -Force `
        -Path $BackupRoot |
        Out-Null

    $BackupWritable = Test-DirectoryWrite `
        $BackupRoot `
        "Backup folder"
}
catch {
    $BackupWritable = $false

    Add-Result (
        "Backup folder preparation: FAILED - " +
        $_.Exception.Message
    )
}

if (Test-Path $DatabaseFile -PathType Leaf) {
    $databaseInfo = Get-Item $DatabaseFile

    Add-Result (
        "SQLite database: PRESENT (" +
        $databaseInfo.Length +
        " bytes)"
    )
}
else {
    Add-Result "SQLite database: NOT YET CREATED"
}

if (Test-Path $CredentialFile -PathType Leaf) {
    Add-Result (
        "First-login credential file: PRESENT"
    )
}
else {
    Add-Result (
        "First-login credential file: NOT PRESENT"
    )
}

if (Test-Path $BackupRoot -PathType Container) {
    $backupFiles = @(
        Get-ChildItem `
            -Path $BackupRoot `
            -Filter "ROBO-POS-*.db" `
            -File `
            -ErrorAction SilentlyContinue
    )

    $sidecarFiles = @(
        Get-ChildItem `
            -Path $BackupRoot `
            -File `
            -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -like "*-wal" -or
            $_.Name -like "*-shm" -or
            $_.Name -like "*-journal"
        }
    )

    Add-Result "Verified database backups: $($backupFiles.Count)"
    Add-Result "Unexpected backup sidecars: $($sidecarFiles.Count)"
}

Add-Result ""
Add-Result "SHOP NETWORK"

if (Test-Path $NetworkModeFile -PathType Leaf) {
    Add-Result "Shop network mode: ENABLED"
    Add-Result "Firewall scope: Private network / Local subnet"

    try {
        $shopAddresses = @(
            Get-NetIPAddress `
                -AddressFamily IPv4 `
                -AddressState Preferred `
                -ErrorAction Stop |
            Where-Object {
                $_.IPAddress -ne "127.0.0.1" -and
                -not $_.IPAddress.StartsWith("169.254.")
            } |
            Select-Object `
                -ExpandProperty IPAddress `
                -Unique
        )

        foreach ($shopAddress in $shopAddresses) {
            Add-Result (
                "Possible teller address: http://" +
                $shopAddress +
                ":8765/"
            )
        }
    }
    catch {
        Add-Result (
            "Network address lookup warning: " +
            $_.Exception.Message
        )
    }
}
else {
    Add-Result "Shop network mode: DISABLED"
    Add-Result "Access scope: This computer only"
}

Add-Result ""
Add-Result "LOCAL SERVER"

$RunningPort = $null
$HealthContent = $null

foreach ($candidate in 8765..8775) {
    $health = Test-RoboHealth $candidate

    if ($null -ne $health) {
        $RunningPort = $candidate
        $HealthContent = $health
        break
    }
}

if ($null -ne $RunningPort) {
    Add-Result "Secure server: RUNNING"
    Add-Result "Address: http://127.0.0.1:$RunningPort/"
    Add-Result "Health endpoint: HTTP 200"
    Add-Result "Health response: $HealthContent"
}
else {
    Add-Result "Secure server: NOT CURRENTLY RUNNING"
}

Add-Result ""
Add-Result "LOG FILES"

foreach ($logFile in @(
    $LauncherLog,
    $ServerOutputLog,
    $ServerErrorLog
)) {
    if (Test-Path $logFile -PathType Leaf) {
        $logInfo = Get-Item $logFile

        Add-Result (
            "$($logInfo.Name): PRESENT (" +
            $logInfo.Length +
            " bytes)"
        )
    }
    else {
        Add-Result (
            "$(Split-Path $logFile -Leaf): NOT PRESENT"
        )
    }
}

Add-Result ""
Add-Result "SUMMARY"

if (
    $ApplicationFilesOk -and
    $DataWritable -and
    $DocumentsWritable -and
    $BackupWritable
) {
    Add-Result "Core installation checks: PASSED"
}
else {
    Add-Result "Core installation checks: FAILED"
}

if ($null -ne $RunningPort) {
    Add-Result "Application availability: READY"
}
else {
    Add-Result (
        "Application availability: Run the secure launcher."
    )
}

try {
    New-Item `
        -ItemType Directory `
        -Force `
        -Path $DataDir |
        Out-Null

    $Report = $Results -join "`r`n"

    Set-Content `
        -Path $ReportPath `
        -Value $Report `
        -Encoding UTF8
}
catch {
    $Report = $Results -join "`r`n"

    $Report += (
        "`r`n`r`nThe report could not be saved: " +
        $_.Exception.Message
    )
}

Show-Report $Report

if (
    $ApplicationFilesOk -and
    $DataWritable -and
    $DocumentsWritable -and
    $BackupWritable -and
    $null -eq $RunningPort -and
    (Test-Path $Launcher -PathType Leaf)
) {
    try {
        Add-Type -AssemblyName System.Windows.Forms

        $answer = [System.Windows.Forms.MessageBox]::Show(
            "The secure POS server is not running." +
            "`r`n`r`nStart ROBO CASK & TAP POS now?",
            "ROBO POS Diagnostics",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Question
        )

        if (
            $answer -eq
            [System.Windows.Forms.DialogResult]::Yes
        ) {
            if ($Portable) {
                & $Launcher `
                    -Portable `
                    -DataDir $DataDir
            }
            else {
                & $Launcher `
                    -DataDir $DataDir
            }
        }
    }
    catch {
        Write-Host (
            "The launcher could not be started: " +
            $_.Exception.Message
        )
    }
}

if (
    -not $ApplicationFilesOk -or
    -not $DataWritable -or
    -not $DocumentsWritable -or
    -not $BackupWritable
) {
    exit 1
}

exit 0
