$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$PackageRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$SourceApp = Join-Path $PackageRoot "app"
$DefaultInstall = Join-Path $env:LOCALAPPDATA "Programs\ROBO CASK TAP POS"
$DataDir = Join-Path $env:LOCALAPPDATA "ROBO CASK TAP POS\Data"
$script:Step = 0
$script:InstallComplete = $false

function New-Label($text,$x,$y,$w,$h,$size=10,$bold=$false,$color=[System.Drawing.Color]::FromArgb(24,33,47)) {
    $l=New-Object System.Windows.Forms.Label
    $l.Text=$text;$l.Location=New-Object System.Drawing.Point($x,$y);$l.Size=New-Object System.Drawing.Size($w,$h)
    $l.Font=New-Object System.Drawing.Font("Segoe UI",$size,$(if($bold){[System.Drawing.FontStyle]::Bold}else{[System.Drawing.FontStyle]::Regular}))
    $l.ForeColor=$color;$l.AutoEllipsis=$true
    return $l
}
function Stop-OldServers {
    try {
        Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*ROBO CASK TAP POS*server.ps1*" -or $_.CommandLine -like "*$DefaultInstall*server.ps1*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch {}
}
function New-Shortcut([string]$Path,[string]$Target,[string]$Arguments,[string]$WorkingDirectory,[string]$Icon) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$Icon,0"
    $shortcut.Description = "ROBO CASK & TAP Point of Sale"
    $shortcut.Save()
}
function Set-Status([string]$Text,[int]$Percent) {
    $statusLabel.Text=$Text;$progress.Value=[Math]::Max(0,[Math]::Min(100,$Percent));$form.Refresh()
}
function Install-Application {
    try {
        $backBtn.Enabled=$false;$nextBtn.Enabled=$false;$cancelBtn.Enabled=$false
        Set-Status "Checking installation package..." 5
        foreach($required in @("launcher.ps1","server.ps1","web\index.html","web\app.js","web\styles.css")){
            if(-not(Test-Path (Join-Path $SourceApp $required))){throw "The package is incomplete. Missing: $required"}
        }
        $installPath=$pathBox.Text.Trim()
        if([string]::IsNullOrWhiteSpace($installPath)){throw "Choose a valid installation folder."}
        Set-Status "Closing any previous ROBO POS service..." 15;Stop-OldServers
        Set-Status "Preparing application folders..." 25
        if(Test-Path $installPath){Remove-Item $installPath -Recurse -Force}
        New-Item -ItemType Directory -Force -Path $installPath | Out-Null
        New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $DataDir "Backups") | Out-Null
        Set-Status "Installing ROBO CASK & TAP POS..." 45
        Copy-Item (Join-Path $SourceApp "*") $installPath -Recurse -Force
        Set-Status "Creating Desktop and Start Menu shortcuts..." 68
        $powershell=(Get-Command powershell.exe).Source
        $icon=Join-Path $installPath "robo.ico"
        $desktop=[Environment]::GetFolderPath("Desktop")
        $startFolder=Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ROBO CASK & TAP POS"
        New-Item -ItemType Directory -Force -Path $startFolder | Out-Null
        $launchArgs="-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $installPath 'launcher.ps1')`""
        New-Shortcut (Join-Path $desktop "ROBO CASK & TAP POS.lnk") $powershell $launchArgs $installPath $icon
        New-Shortcut (Join-Path $startFolder "ROBO CASK & TAP POS.lnk") $powershell $launchArgs $installPath $icon
        $diagArgs="-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installPath 'diagnose.ps1')`""
        New-Shortcut (Join-Path $startFolder "Repair and Diagnose.lnk") $powershell $diagArgs $installPath $icon
        $uninstallArgs="-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installPath 'uninstall.ps1')`""
        New-Shortcut (Join-Path $startFolder "Uninstall ROBO POS.lnk") $powershell $uninstallArgs $installPath $icon
        Set-Status "Registering the application in Windows..." 82
        $uninstallKey="HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ROBOCaskTapPOS"
        New-Item -Path $uninstallKey -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name DisplayName -Value "ROBO CASK & TAP POS" -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value "2.0.0" -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name Publisher -Value "ROBO CASK & TAP" -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installPath -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value $icon -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name UninstallString -Value "$powershell $uninstallArgs" -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
        Set-Status "Verifying installed files..." 92
        foreach($required in @("launcher.ps1","server.ps1","web\index.html","web\app.js","web\styles.css")){
            if(-not(Test-Path (Join-Path $installPath $required))){throw "Verification failed. Missing installed file: $required"}
        }
        "2.0.0" | Set-Content (Join-Path $installPath "VERSION.txt") -Encoding ASCII
        Set-Status "Installation completed successfully." 100
        $script:InstallComplete=$true;$script:Step=3;Show-Step
    } catch {
        Set-Status "Installation failed." 0
        [System.Windows.Forms.MessageBox]::Show("ROBO CASK & TAP POS was not installed.`r`n`r`n$($_.Exception.Message)","Installation Error",0,16) | Out-Null
        $backBtn.Enabled=$true;$nextBtn.Enabled=$true;$cancelBtn.Enabled=$true
    }
}
function Show-Step {
    $content.Controls.Clear()
    $backBtn.Visible=$true;$nextBtn.Visible=$true;$cancelBtn.Visible=$true
    $backBtn.Enabled=$true;$nextBtn.Enabled=$true;$cancelBtn.Enabled=$true
    switch($script:Step){
        0 {
            $backBtn.Enabled=$false;$nextBtn.Text="Next >"
            $content.Controls.Add((New-Label "Welcome to ROBO CASK & TAP POS Setup" 35 35 610 50 22 $true))
            $content.Controls.Add((New-Label "This wizard installs the complete local sales and stock system. It does not require Python, Node.js, a database server or an internet connection." 38 100 590 70 11 $false))
            $box=New-Object System.Windows.Forms.Panel;$box.Location=New-Object System.Drawing.Point(38,190);$box.Size=New-Object System.Drawing.Size(585,155);$box.BackColor=[System.Drawing.Color]::FromArgb(248,250,252);$box.BorderStyle="FixedSingle"
            $box.Controls.Add((New-Label "Included" 18 15 150 25 11 $true))
            $box.Controls.Add((New-Label "• Baron administrator account and two teller accounts`r`n• Point of sale, stock, short-glass mapping and receipts`r`n• Teller shifts, expenses, reports, users and audit trail`r`n• Local backups and 80 mm receipt printing" 18 45 540 95 10 $false))
            $content.Controls.Add($box)
            $content.Controls.Add((New-Label "Click Next to choose where the application will be installed." 38 375 590 35 10 $false))
        }
        1 {
            $nextBtn.Text="Next >"
            $content.Controls.Add((New-Label "Choose Installation Location" 35 35 610 45 21 $true))
            $content.Controls.Add((New-Label "The application is installed for the current Windows user. Business data is kept separately so upgrades do not delete sales records." 38 88 590 55 10 $false))
            $content.Controls.Add((New-Label "Installation folder" 38 165 250 25 10 $true))
            $pathBox.Location=New-Object System.Drawing.Point(38,196);$pathBox.Size=New-Object System.Drawing.Size(480,31);$content.Controls.Add($pathBox)
            $browseBtn.Location=New-Object System.Drawing.Point(530,196);$browseBtn.Size=New-Object System.Drawing.Size(92,31);$content.Controls.Add($browseBtn)
            $content.Controls.Add((New-Label "Business data folder" 38 260 250 25 10 $true))
            $content.Controls.Add((New-Label $DataDir 38 290 585 35 10 $false [System.Drawing.Color]::FromArgb(102,112,133)))
            $content.Controls.Add((New-Label "No administrator password is required for this per-user installation." 38 350 585 35 10 $false))
        }
        2 {
            $nextBtn.Text="Install"
            $content.Controls.Add((New-Label "Ready to Install" 35 35 610 45 21 $true))
            $content.Controls.Add((New-Label "Setup is ready to install ROBO CASK & TAP POS with these settings:" 38 90 590 35 10 $false))
            $summary=New-Object System.Windows.Forms.TextBox;$summary.Location=New-Object System.Drawing.Point(38,140);$summary.Size=New-Object System.Drawing.Size(585,155);$summary.Multiline=$true;$summary.ReadOnly=$true;$summary.BackColor=[System.Drawing.Color]::White;$summary.Text="Application: ROBO CASK & TAP POS 2.0.0`r`nInstall folder: $($pathBox.Text)`r`nData folder: $DataDir`r`nDesktop shortcut: Yes`r`nStart Menu shortcuts: Yes`r`nExternal runtime required: None";$content.Controls.Add($summary)
            $statusLabel.Location=New-Object System.Drawing.Point(38,325);$statusLabel.Size=New-Object System.Drawing.Size(585,30);$statusLabel.Text="Click Install to begin.";$content.Controls.Add($statusLabel)
            $progress.Location=New-Object System.Drawing.Point(38,365);$progress.Size=New-Object System.Drawing.Size(585,24);$progress.Value=0;$content.Controls.Add($progress)
        }
        3 {
            $backBtn.Visible=$false;$cancelBtn.Visible=$false;$nextBtn.Text="Finish";$nextBtn.Enabled=$true
            $content.Controls.Add((New-Label "Installation Completed" 35 35 610 45 22 $true [System.Drawing.Color]::FromArgb(22,121,79)))
            $content.Controls.Add((New-Label "ROBO CASK & TAP POS has been installed and verified. A shortcut has been created on the Desktop." 38 95 585 60 11 $false))
            $info=New-Object System.Windows.Forms.Panel;$info.Location=New-Object System.Drawing.Point(38,175);$info.Size=New-Object System.Drawing.Size(585,145);$info.BackColor=[System.Drawing.Color]::FromArgb(248,250,252);$info.BorderStyle="FixedSingle"
            $info.Controls.Add((New-Label "First admin login" 18 18 240 25 11 $true))
            $info.Controls.Add((New-Label "Username:  baron`r`nPassword:  Baron@123`r`n`r`nThe password must be changed after first login." 18 50 530 85 10 $false))
            $content.Controls.Add($info)
            $launchCheck.Location=New-Object System.Drawing.Point(38,355);$launchCheck.Size=New-Object System.Drawing.Size(500,30);$launchCheck.Checked=$true;$launchCheck.Text="Launch ROBO CASK & TAP POS now";$content.Controls.Add($launchCheck)
        }
    }
}

$form=New-Object System.Windows.Forms.Form
$form.Text="ROBO CASK & TAP POS Setup"
$form.Size=New-Object System.Drawing.Size(730,590)
$form.StartPosition="CenterScreen";$form.FormBorderStyle="FixedDialog";$form.MaximizeBox=$false;$form.MinimizeBox=$false
$form.BackColor=[System.Drawing.Color]::White
try{$form.Icon=New-Object System.Drawing.Icon((Join-Path $SourceApp "robo.ico"))}catch{}
$banner=New-Object System.Windows.Forms.Panel;$banner.Dock="Top";$banner.Height=70;$banner.BackColor=[System.Drawing.Color]::FromArgb(47,14,23)
$banner.Controls.Add((New-Label "ROBO CASK & TAP" 24 13 430 30 16 $true [System.Drawing.Color]::White))
$banner.Controls.Add((New-Label "Professional Point of Sale Installation Wizard" 25 40 460 20 9 $false [System.Drawing.Color]::FromArgb(226,211,215)))
$form.Controls.Add($banner)
$content=New-Object System.Windows.Forms.Panel;$content.Location=New-Object System.Drawing.Point(0,70);$content.Size=New-Object System.Drawing.Size(714,445);$form.Controls.Add($content)
$footer=New-Object System.Windows.Forms.Panel;$footer.Dock="Bottom";$footer.Height=58;$footer.BackColor=[System.Drawing.Color]::FromArgb(248,250,252);$footer.BorderStyle="FixedSingle";$form.Controls.Add($footer)
$backBtn=New-Object System.Windows.Forms.Button;$backBtn.Text="< Back";$backBtn.Location=New-Object System.Drawing.Point(382,13);$backBtn.Size=New-Object System.Drawing.Size(95,32);$footer.Controls.Add($backBtn)
$nextBtn=New-Object System.Windows.Forms.Button;$nextBtn.Text="Next >";$nextBtn.Location=New-Object System.Drawing.Point(485,13);$nextBtn.Size=New-Object System.Drawing.Size(100,32);$nextBtn.BackColor=[System.Drawing.Color]::FromArgb(122,31,43);$nextBtn.ForeColor=[System.Drawing.Color]::White;$nextBtn.FlatStyle="Flat";$footer.Controls.Add($nextBtn)
$cancelBtn=New-Object System.Windows.Forms.Button;$cancelBtn.Text="Cancel";$cancelBtn.Location=New-Object System.Drawing.Point(593,13);$cancelBtn.Size=New-Object System.Drawing.Size(95,32);$footer.Controls.Add($cancelBtn)
$pathBox=New-Object System.Windows.Forms.TextBox;$pathBox.Text=$DefaultInstall
$browseBtn=New-Object System.Windows.Forms.Button;$browseBtn.Text="Browse..."
$statusLabel=New-Label "" 0 0 100 20 10 $false
$progress=New-Object System.Windows.Forms.ProgressBar;$progress.Minimum=0;$progress.Maximum=100
$launchCheck=New-Object System.Windows.Forms.CheckBox

$browseBtn.Add_Click({$dialog=New-Object System.Windows.Forms.FolderBrowserDialog;$dialog.Description="Choose the ROBO POS installation folder";$dialog.SelectedPath=$pathBox.Text;if($dialog.ShowDialog()-eq "OK"){$pathBox.Text=Join-Path $dialog.SelectedPath "ROBO CASK TAP POS"}})
$backBtn.Add_Click({if($script:Step -gt 0){$script:Step--;Show-Step}})
$nextBtn.Add_Click({if($script:Step -lt 2){$script:Step++;Show-Step}elseif($script:Step -eq 2){Install-Application}else{if($launchCheck.Checked){Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $pathBox.Text 'launcher.ps1')`""};$form.Close()}})
$cancelBtn.Add_Click({if([System.Windows.Forms.MessageBox]::Show("Cancel the installation?","ROBO POS Setup",4,48)-eq "Yes"){$form.Close()}})
$form.Add_FormClosing({param($sender,$e) if(-not $script:InstallComplete -and -not $cancelBtn.Enabled){$e.Cancel=$true}})
Show-Step
[void]$form.ShowDialog()
