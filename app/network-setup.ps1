param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Enable", "Disable")]
    [string]$Mode,

    [switch]$Portable,

    [string]$DataDir = ""
)

$ErrorActionPreference = "Stop"

$AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageRoot = Split-Path -Parent $AppRoot
$ServerExe = Join-Path $AppRoot "runtime\Robo.Pos.Server.exe"

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

$NetworkMarker = Join-Path `
    $DataDir `
    "shop-network.enabled"

$ServerPidFile = Join-Path `
    $DataDir `
    "server.pid"

$FirewallRuleName = (
    "ROBO CASK TAP POS - Shop Network"
)

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()

    $principal = New-Object `
        Security.Principal.WindowsPrincipal `
        $identity

    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}

function Restart-Elevated {
    $arguments = (
        "-NoProfile -ExecutionPolicy Bypass -STA " +
        "-File `"$PSCommandPath`" " +
        "-Mode $Mode " +
        "-DataDir `"$DataDir`""
    )

    if ($Portable) {
        $arguments += " -Portable"
    }

    Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList $arguments `
        -Verb RunAs |
        Out-Null
}

function Show-RoboMessage {
    param(
        [string]$Message,
        [string]$Title = "ROBO CASK & TAP POS"
    )

    try {
        Add-Type -AssemblyName System.Windows.Forms

        [System.Windows.Forms.MessageBox]::Show(
            $Message,
            $Title,
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
    }
    catch {
        Write-Host $Message
    }
}

function Stop-RoboServer {
    if (-not (Test-Path $ServerPidFile -PathType Leaf)) {
        return
    }

    try {
        $pidText = (
            Get-Content `
                -Path $ServerPidFile `
                -Encoding ASCII |
            Select-Object -First 1
        )

        [int]$serverPid = 0

        if (-not [int]::TryParse(
                $pidText,
                [ref]$serverPid
            )) {
            return
        }

        $process = Get-Process `
            -Id $serverPid `
            -ErrorAction SilentlyContinue

        if ($null -eq $process) {
            return
        }

        $expectedPath = [System.IO.Path]::GetFullPath(
            $ServerExe
        )

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
                $expectedPath,
                [StringComparison]::OrdinalIgnoreCase
            )
        ) {
            Stop-Process `
                -Id $process.Id `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }
    finally {
        Remove-Item `
            -Path $ServerPidFile `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

function Get-ShopIPv4Addresses {
    try {
        return @(
            Get-NetIPAddress `
                -AddressFamily IPv4 `
                -AddressState Preferred `
                -ErrorAction Stop |
            Where-Object {
                $_.IPAddress -ne "127.0.0.1" -and
                -not $_.IPAddress.StartsWith("169.254.") -and
                $_.PrefixOrigin -ne "WellKnown"
            } |
            Select-Object `
                -ExpandProperty IPAddress `
                -Unique
        )
    }
    catch {
        return @()
    }
}

try {
    if (-not (Test-IsAdministrator)) {
        Restart-Elevated
        exit 0
    }

    if (-not (Test-Path $ServerExe -PathType Leaf)) {
        throw (
            "The secure POS executable is missing:`r`n" +
            $ServerExe
        )
    }

    New-Item `
        -ItemType Directory `
        -Force `
        -Path $DataDir |
        Out-Null

    Stop-RoboServer

    if ($Mode -eq "Enable") {
        Set-Content `
            -Path $NetworkMarker `
            -Value (
                "Enabled=" +
                (Get-Date -Format "yyyy-MM-ddTHH:mm:ss")
            ) `
            -Encoding ASCII

        Remove-NetFirewallRule `
            -DisplayName $FirewallRuleName `
            -ErrorAction SilentlyContinue

        New-NetFirewallRule `
            -DisplayName $FirewallRuleName `
            -Description (
                "Allows ROBO CASK & TAP POS devices " +
                "on the private shop network."
            ) `
            -Direction Inbound `
            -Action Allow `
            -Program $ServerExe `
            -Protocol TCP `
            -LocalPort "8765-8775" `
            -Profile Private `
            -RemoteAddress LocalSubnet |
            Out-Null

        $addresses = Get-ShopIPv4Addresses

        $addressText = ""

        if ($addresses.Count -gt 0) {
            $lines = @()

            foreach ($address in $addresses) {
                $lines += (
                    "http://" +
                    $address +
                    ":8765/"
                )
            }

            $addressText = $lines -join "`r`n"
        }
        else {
            $addressText = (
                "Open Windows Settings, check the Wi-Fi IPv4 " +
                "address, then use port 8765."
            )
        }

        Show-RoboMessage (
            "Shop network access is now enabled." +
            "`r`n`r`n" +
            "Restart ROBO CASK & TAP POS." +
            "`r`n`r`n" +
            "Teller devices connected to the same private " +
            "Wi-Fi can open:" +
            "`r`n`r`n" +
            $addressText +
            "`r`n`r`n" +
            "Windows Firewall allows only devices on the " +
            "local private network."
        )
    }
    else {
        Remove-Item `
            -Path $NetworkMarker `
            -Force `
            -ErrorAction SilentlyContinue

        Remove-NetFirewallRule `
            -DisplayName $FirewallRuleName `
            -ErrorAction SilentlyContinue

        Show-RoboMessage (
            "Shop network access is now disabled." +
            "`r`n`r`n" +
            "Restart ROBO CASK & TAP POS." +
            "`r`n`r`n" +
            "The application will again accept connections " +
            "only from this computer."
        )
    }

    exit 0
}
catch {
    Show-RoboMessage (
        "The shop-network setting could not be changed." +
        "`r`n`r`n" +
        $_.Exception.Message
    )

    exit 1
}
