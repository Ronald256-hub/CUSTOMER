param(
    [switch]$Portable,
    [string]$DataDir = ""
)

$ErrorActionPreference = "Stop"

$AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageRoot = Split-Path -Parent $AppRoot
$RuntimeRoot = Join-Path $AppRoot "runtime"
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

$CredentialFile = Join-Path `
    $DataDir `
    "FIRST_LOGIN_CREDENTIALS.txt"

$LauncherLog = Join-Path $DataDir "launcher.log"
$ServerOutputLog = Join-Path $DataDir "server-output.log"
$ServerErrorLog = Join-Path $DataDir "server-error.log"
$ServerPidFile = Join-Path $DataDir "server.pid"

function Write-LaunchLog {
    param([string]$Message)

    try {
        Add-Content `
            -Path $LauncherLog `
            -Value "$(Get-Date -Format s) $Message" `
            -Encoding UTF8
    }
    catch {
        # Logging must never prevent the application from opening.
    }
}

function Show-RoboError {
    param([string]$Message)

    try {
        Add-Type -AssemblyName System.Windows.Forms

        [System.Windows.Forms.MessageBox]::Show(
            $Message,
            "ROBO CASK & TAP POS",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
    }
    catch {
        Write-Host $Message
    }
}

function Show-RoboInformation {
    param([string]$Message)

    try {
        Add-Type -AssemblyName System.Windows.Forms

        [System.Windows.Forms.MessageBox]::Show(
            $Message,
            "ROBO CASK & TAP POS",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
    }
    catch {
        Write-Host $Message
    }
}

function Test-RoboServer {
    param([int]$Port)

    try {
        $response = Invoke-WebRequest `
            -Uri "http://127.0.0.1:$Port/api/v3/health" `
            -UseBasicParsing `
            -TimeoutSec 2

        return (
            $response.StatusCode -eq 200 -and
            $response.Content -like "*ROBO CASK*" -and
            $response.Content -like "*schemaVersion*"
        )
    }
    catch {
        return $false
    }
}

function Test-PortOpen {
    param([int]$Port)

    $client = New-Object System.Net.Sockets.TcpClient

    try {
        $task = $client.ConnectAsync(
            "127.0.0.1",
            $Port
        )

        if (-not $task.Wait(300)) {
            return $false
        }

        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

$script:RandomGenerator =
    [System.Security.Cryptography.RandomNumberGenerator]::Create()

function Get-CryptoRandomIndex {
    param([int]$Maximum)

    if ($Maximum -le 0) {
        throw "The random selection range is invalid."
    }

    $bytes = New-Object byte[] 4
    $script:RandomGenerator.GetBytes($bytes)

    $value = [BitConverter]::ToUInt32(
        $bytes,
        0
    )

    return [int]($value % [uint32]$Maximum)
}

function New-TemporaryPassword {
    $upper = "ABCDEFGHJKLMNPQRSTUVWXYZ"
    $lower = "abcdefghijkmnopqrstuvwxyz"
    $digits = "23456789"
    $symbols = "!@#%*-_+?"
    $all = $upper + $lower + $digits + $symbols

    $characters =
        New-Object System.Collections.Generic.List[char]

    $characters.Add(
        $upper[
            Get-CryptoRandomIndex $upper.Length
        ]
    )

    $characters.Add(
        $lower[
            Get-CryptoRandomIndex $lower.Length
        ]
    )

    $characters.Add(
        $digits[
            Get-CryptoRandomIndex $digits.Length
        ]
    )

    $characters.Add(
        $symbols[
            Get-CryptoRandomIndex $symbols.Length
        ]
    )

    while ($characters.Count -lt 20) {
        $characters.Add(
            $all[
                Get-CryptoRandomIndex $all.Length
            ]
        )
    }

    for (
        $index = $characters.Count - 1;
        $index -gt 0;
        $index--
    ) {
        $swapIndex =
            Get-CryptoRandomIndex ($index + 1)

        $temporary = $characters[$index]
        $characters[$index] = $characters[$swapIndex]
        $characters[$swapIndex] = $temporary
    }

    return -join $characters
}

function Read-CredentialValue {
    param(
        [string]$Path,
        [string]$Name
    )

    $prefix = "$Name="

    $line = Get-Content `
        -Path $Path `
        -Encoding UTF8 |
        Where-Object {
            $_.StartsWith(
                $prefix,
                [StringComparison]::Ordinal
            )
        } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "The credential file is incomplete."
    }

    return $line.Substring($prefix.Length)
}

function Protect-CredentialFile {
    param([string]$Path)

    try {
        $identity = $env:USERNAME

        if (-not [string]::IsNullOrWhiteSpace(
                $env:USERDOMAIN
            )) {
            $identity =
                "$($env:USERDOMAIN)\$($env:USERNAME)"
        }

        & icacls.exe `
            $Path `
            /inheritance:r `
            /grant:r `
            "${identity}:(R,W)" |
            Out-Null
    }
    catch {
        Write-LaunchLog (
            "Credential file permission warning: " +
            $_.Exception.Message
        )
    }
}

function Open-ApplicationWindow {
    param([string]$Url)

    $candidates = @()

    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path `
            ${env:ProgramFiles(x86)} `
            "Microsoft\Edge\Application\msedge.exe"
    }

    if ($env:ProgramFiles) {
        $candidates += Join-Path `
            $env:ProgramFiles `
            "Microsoft\Edge\Application\msedge.exe"
    }

    if ($env:LOCALAPPDATA) {
        $candidates += Join-Path `
            $env:LOCALAPPDATA `
            "Microsoft\Edge\Application\msedge.exe"
    }

    $edge = $candidates |
        Where-Object {
            $_ -and
            (Test-Path $_ -PathType Leaf)
        } |
        Select-Object -First 1

    if ($edge) {
        Start-Process `
            -FilePath $edge `
            -ArgumentList (
                "--app=`"$Url`" " +
                "--start-maximized " +
                "--disable-features=msEdgeSidebarV2"
            ) |
            Out-Null
    }
    else {
        Start-Process $Url | Out-Null
    }
}

try {
    if (-not (Test-Path $ServerExe -PathType Leaf)) {
        throw (
            "The secure application executable is missing:`r`n" +
            "$ServerExe"
        )
    }

    if (-not (Test-Path `
            (Join-Path $RuntimeRoot "wwwroot\index.html") `
            -PathType Leaf)) {
        throw "The secure application interface is missing."
    }

    New-Item `
        -ItemType Directory `
        -Force `
        -Path $DataDir |
        Out-Null

    New-Item `
        -ItemType Directory `
        -Force `
        -Path $DocumentRoot |
        Out-Null

    $CreatedCredentialFile = $false

    if (Test-Path $CredentialFile -PathType Leaf) {
        $AdminPassword = Read-CredentialValue `
            $CredentialFile `
            "baron"

        $TellerOnePassword = Read-CredentialValue `
            $CredentialFile `
            "teller1"

        $TellerTwoPassword = Read-CredentialValue `
            $CredentialFile `
            "teller2"
    }
    elseif (-not (Test-Path $DatabaseFile -PathType Leaf)) {
        $AdminPassword = New-TemporaryPassword
        $TellerOnePassword = New-TemporaryPassword
        $TellerTwoPassword = New-TemporaryPassword

        $credentialText = @"
ROBO CASK & TAP POS
FIRST LOGIN CREDENTIALS

Generated: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")

Administrator
Username: baron
baron=$AdminPassword

Teller One
Username: teller1
teller1=$TellerOnePassword

Teller Two
Username: teller2
teller2=$TellerTwoPassword

Each user must change the temporary password after first login.
Keep this file private. After all passwords have been changed,
this file may be deleted.
"@

        Set-Content `
            -Path $CredentialFile `
            -Value $credentialText `
            -Encoding UTF8

        Protect-CredentialFile $CredentialFile

        $CreatedCredentialFile = $true
    }
    else {
        # Existing databases already contain their users.
        # Random process-only values satisfy startup configuration
        # without creating misleading replacement credentials.
        $AdminPassword = New-TemporaryPassword
        $TellerOnePassword = New-TemporaryPassword
        $TellerTwoPassword = New-TemporaryPassword
    }

    $script:RandomGenerator.Dispose()

    $env:ROBO_DATA_DIR = $DataDir
    $env:ROBO_DOCUMENT_ROOT = $DocumentRoot
    $env:ROBO_ADMIN_INITIAL_PASSWORD = $AdminPassword
    $env:ROBO_TELLER1_INITIAL_PASSWORD = $TellerOnePassword
    $env:ROBO_TELLER2_INITIAL_PASSWORD = $TellerTwoPassword
    $env:ASPNETCORE_ENVIRONMENT = "Production"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

    $Port = $null
    $ExistingServer = $false

    foreach ($candidate in 8765..8775) {
        if (Test-RoboServer $candidate) {
            $Port = $candidate
            $ExistingServer = $true
            break
        }

        if (-not (Test-PortOpen $candidate)) {
            $Port = $candidate
            break
        }
    }

    if ($null -eq $Port) {
        throw (
            "No free local port was available " +
            "between 8765 and 8775."
        )
    }

    if (-not $ExistingServer) {
        Remove-Item `
            $ServerOutputLog `
            -Force `
            -ErrorAction SilentlyContinue

        Remove-Item `
            $ServerErrorLog `
            -Force `
            -ErrorAction SilentlyContinue

        $process = Start-Process `
            -FilePath $ServerExe `
            -ArgumentList (
                "--urls " +
                "`"http://127.0.0.1:$Port`""
            ) `
            -WorkingDirectory $RuntimeRoot `
            -WindowStyle Hidden `
            -RedirectStandardOutput $ServerOutputLog `
            -RedirectStandardError $ServerErrorLog `
            -PassThru

        Set-Content `
            -Path $ServerPidFile `
            -Value $process.Id `
            -Encoding ASCII

        $Ready = $false

        foreach ($attempt in 1..80) {
            Start-Sleep -Milliseconds 250

            if (Test-RoboServer $Port) {
                $Ready = $true
                break
            }

            if ($process.HasExited) {
                break
            }
        }

        if (-not $Ready) {
            $exitInformation = ""

            if ($process.HasExited) {
                $exitInformation =
                    "`r`nProcess exit code: " +
                    $process.ExitCode
            }

            throw (
                "The secure local POS server did not start." +
                $exitInformation +
                "`r`n`r`nReview:`r`n" +
                $ServerErrorLog
            )
        }
    }

    $Url = "http://127.0.0.1:$Port/"

    Write-LaunchLog (
        "Secure application opened on $Url. " +
        "DataDir=$DataDir"
    )

    if ($CreatedCredentialFile) {
        Show-RoboInformation (
            "Strong temporary first-login credentials " +
            "have been created.`r`n`r`n" +
            "They will now open in Notepad.`r`n`r`n" +
            "Keep the file private and require every " +
            "user to change their password."
        )

        Start-Process `
            -FilePath "notepad.exe" `
            -ArgumentList "`"$CredentialFile`"" |
            Out-Null
    }

    Open-ApplicationWindow $Url
}
catch {
    try {
        $script:RandomGenerator.Dispose()
    }
    catch {
    }

    Write-LaunchLog (
        "ERROR " +
        $_.Exception.ToString()
    )

    Show-RoboError (
        "ROBO CASK & TAP POS could not start." +
        "`r`n`r`n" +
        $_.Exception.Message +
        "`r`n`r`nRun Repair and Diagnose."
    )

    exit 1
}
