param(
    [switch]$Portable,
    [string]$DataDir = ""
)

$ErrorActionPreference = "Stop"
$AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = if ($Portable) { Join-Path (Split-Path -Parent $AppRoot) "portable-data" } else { Join-Path $env:LOCALAPPDATA "ROBO CASK TAP POS\Data" }
}
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$LogFile = Join-Path $DataDir "launcher.log"

function Write-LaunchLog([string]$Message) {
    try { Add-Content -Path $LogFile -Value "$(Get-Date -Format s) $Message" -Encoding UTF8 } catch {}
}
function Test-RoboServer([int]$Port) {
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/health" -UseBasicParsing -TimeoutSec 1
        return ($r.StatusCode -eq 200 -and $r.Content -like "*ROBO CASK*POS*")
    } catch { return $false }
}
function Test-PortOpen([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync("127.0.0.1", $Port)
        if (-not $task.Wait(250)) { return $false }
        return $client.Connected
    } catch { return $false } finally { $client.Dispose() }
}
function Show-Error([string]$Message) {
    try { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show($Message,"ROBO CASK & TAP POS",0,16) | Out-Null } catch {}
}

try {
    $serverScript = Join-Path $AppRoot "server.ps1"
    if (-not (Test-Path $serverScript)) { throw "The local service file is missing: $serverScript" }

    $port = $null
    foreach ($candidate in 8765..8775) {
        if (Test-RoboServer $candidate) { $port = $candidate; break }
        if (-not (Test-PortOpen $candidate)) { $port = $candidate; break }
    }
    if ($null -eq $port) { throw "No free local port was available between 8765 and 8775." }

    if (-not (Test-RoboServer $port)) {
        $arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$serverScript`" -Port $port -DataDir `"$DataDir`""
        Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -WindowStyle Hidden | Out-Null
        $ready = $false
        foreach ($attempt in 1..40) {
            Start-Sleep -Milliseconds 250
            if (Test-RoboServer $port) { $ready = $true; break }
        }
        if (-not $ready) { throw "The local ROBO POS service did not start. Open $LogFile and server.log for details." }
    }

    $url = "http://127.0.0.1:$port/"
    $candidatePaths = @()
    if (${env:ProgramFiles(x86)}) { $candidatePaths += (Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe") }
    if ($env:ProgramFiles) { $candidatePaths += (Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe") }
    if ($env:LOCALAPPDATA) { $candidatePaths += (Join-Path $env:LOCALAPPDATA "Microsoft\Edge\Application\msedge.exe") }
    $edgeCandidates = @($candidatePaths | Where-Object { $_ -and (Test-Path $_) })

    if ($edgeCandidates.Count -gt 0) {
        Start-Process -FilePath $edgeCandidates[0] -ArgumentList "--app=$url --start-maximized --disable-features=msEdgeSidebarV2" | Out-Null
    } else {
        Start-Process $url | Out-Null
    }
    Write-LaunchLog "Application opened on $url with DataDir=$DataDir"
} catch {
    Write-LaunchLog "ERROR $($_.Exception.ToString())"
    Show-Error ("ROBO CASK & TAP POS could not start.`r`n`r`n" + $_.Exception.Message + "`r`n`r`nRun REPAIR AND DIAGNOSE from the installation folder.")
    exit 1
}
