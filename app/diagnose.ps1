$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Windows.Forms
$AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$DataDir = Join-Path $env:LOCALAPPDATA "ROBO CASK TAP POS\Data"
$results = New-Object System.Collections.Generic.List[string]
$results.Add("ROBO CASK & TAP POS - DIAGNOSTIC REPORT")
$results.Add("Generated: $(Get-Date)")
$results.Add("")
$results.Add("PowerShell version: $($PSVersionTable.PSVersion)")
foreach ($file in @("launcher.ps1","server.ps1","web\index.html","web\app.js","web\styles.css")) {
    $path = Join-Path $AppRoot $file
    $results.Add("$file : " + $(if(Test-Path $path){"OK"}else{"MISSING"}))
}
$results.Add("Data folder: $DataDir")
try { New-Item -ItemType Directory -Force -Path $DataDir | Out-Null; $test=Join-Path $DataDir "write-test.tmp"; "ok" | Set-Content $test; Remove-Item $test; $results.Add("Data folder write test: OK") } catch { $results.Add("Data folder write test: FAILED - $($_.Exception.Message)") }
$edgePaths=@(); if(${env:ProgramFiles(x86)}){$edgePaths+=(Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe")}; if($env:ProgramFiles){$edgePaths+=(Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe")}; $edge=@($edgePaths | Where-Object {$_ -and (Test-Path $_)})
$results.Add("Microsoft Edge: " + $(if($edge.Count -gt 0){"OK"}else{"Not found - default browser will be used"}))
$results.Add("")
$results.Add("Attempting to start the software...")
$report = $results -join "`r`n"
$reportPath = Join-Path $DataDir "DIAGNOSTIC_REPORT.txt"
$report | Set-Content -Path $reportPath -Encoding UTF8
[System.Windows.Forms.MessageBox]::Show($report + "`r`n`r`nThe application will now be started.","ROBO POS Diagnostics",0,64) | Out-Null
& (Join-Path $AppRoot "launcher.ps1")
