$ErrorActionPreference = "SilentlyContinue"
Add-Type -AssemblyName System.Windows.Forms
$AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$result = [System.Windows.Forms.MessageBox]::Show("Remove ROBO CASK & TAP POS from this computer?`r`n`r`nYou will be asked whether to keep the business data.","Uninstall ROBO POS",4,48)
if ($result -ne "Yes") { exit }
$keep = [System.Windows.Forms.MessageBox]::Show("Keep sales, products and backup data for a future reinstall?","Keep Business Data",4,32)
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*ROBO CASK TAP POS*server.ps1*" -or $_.CommandLine -like "*$AppRoot*server.ps1*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
$desktop = [Environment]::GetFolderPath("Desktop")
$start = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ROBO CASK & TAP POS"
Remove-Item (Join-Path $desktop "ROBO CASK & TAP POS.lnk") -Force
Remove-Item $start -Recurse -Force
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ROBOCaskTapPOS" -Recurse -Force
if ($keep -eq "No") { Remove-Item (Join-Path $env:LOCALAPPDATA "ROBO CASK TAP POS") -Recurse -Force }
$cmd = "timeout /t 2 /nobreak >nul & rmdir /s /q `"$AppRoot`""
Start-Process cmd.exe -ArgumentList "/c $cmd" -WindowStyle Hidden
[System.Windows.Forms.MessageBox]::Show("ROBO CASK & TAP POS has been removed." + $(if($keep -eq "Yes"){"`r`nThe business data was preserved."}else{""}),"Uninstall Complete",0,64) | Out-Null
