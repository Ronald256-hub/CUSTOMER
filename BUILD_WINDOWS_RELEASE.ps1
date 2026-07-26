[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "4.0.0",
    [string]$CertificateThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ServerProject = Join-Path $Root "src\Robo.Pos.Server\Robo.Pos.Server.csproj"
$LauncherProject = Join-Path $Root "src\Nexus.Pos.Launcher\Nexus.Pos.Launcher.csproj"
$AppRoot = Join-Path $Root "app"
$ServerOutput = Join-Path $AppRoot "runtime"
$LauncherOutput = Join-Path $AppRoot "launcher-runtime"
$ReleaseRoot = Join-Path $Root "release"

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $normalizedThumbprint = $CertificateThumbprint.Replace(" ", "").ToUpperInvariant()
    $CertificateThumbprint = $normalizedThumbprint
    if ($normalizedThumbprint -notmatch '^[A-F0-9]{40}$') {
        throw "CertificateThumbprint must contain a 40-character hexadecimal certificate thumbprint."
    }

    $updateSettingsPath = Join-Path $AppRoot "update-settings.json"
    $updateSettings = Get-Content $updateSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $updateSettings.requiredPublisherThumbprint = $normalizedThumbprint
    $updateSettings | ConvertTo-Json -Depth 8 | Set-Content $updateSettingsPath -Encoding UTF8
}

function Require-Command {
    param([string]$Name)
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) { throw "$Name is required but was not found." }
    return $command.Source
}

$dotnet = Require-Command "dotnet.exe"
$dotnetVersion = & $dotnet --version 2>&1
if ($LASTEXITCODE -ne 0 -or
    [string]::IsNullOrWhiteSpace([string]$dotnetVersion)) {
    throw ".NET SDK was not found. Run .\release-engineering\BOOTSTRAP_WINDOWS_RELEASE.ps1 so missing prerequisites can be installed automatically."
}

$versionText = ([string]$dotnetVersion).Trim().Split('-')[0]
$parsedVersion = $null
if (-not [version]::TryParse($versionText, [ref]$parsedVersion)) {
    throw "Unable to parse the installed .NET SDK version: $dotnetVersion"
}
if ($parsedVersion -lt [version]"10.0.0") {
    throw ".NET SDK 10.0 or later is required. Installed: $dotnetVersion"
}
Write-Host "Using .NET SDK $dotnetVersion" -ForegroundColor Green

New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null
Remove-Item $ServerOutput -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $LauncherOutput -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $ServerOutput | Out-Null
New-Item -ItemType Directory -Force -Path $LauncherOutput | Out-Null

& $dotnet restore $ServerProject `
    --runtime win-x64 `
    -p:SelfContained=true `
    -p:PublishReadyToRun=true `
    -p:TargetLatestRuntimePatch=true
if ($LASTEXITCODE -ne 0) { throw "Server restore failed." }
& $dotnet restore $LauncherProject `
    --runtime win-x64 `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:TargetLatestRuntimePatch=true
if ($LASTEXITCODE -ne 0) { throw "Launcher restore failed." }

& $dotnet build $ServerProject -c $Configuration -r win-x64 --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Server build failed." }
& $dotnet build $LauncherProject -c $Configuration -r win-x64 --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed." }

& $dotnet publish $ServerProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -o $ServerOutput `
    -p:PublishReadyToRun=true `
    -p:TargetLatestRuntimePatch=true `
    -p:TreatWarningsAsErrors=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Server publish failed." }
& $dotnet publish $LauncherProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -o $LauncherOutput `
    -p:PublishSingleFile=true `
    -p:TargetLatestRuntimePatch=true `
    -p:TreatWarningsAsErrors=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed." }

$required = @(
    (Join-Path $LauncherOutput "Nexus.Pos.Launcher.exe"),
    (Join-Path $ServerOutput "Robo.Pos.Server.exe"),
    (Join-Path $ServerOutput "Robo.Pos.Server.dll"),
    (Join-Path $ServerOutput "Robo.Pos.Server.deps.json"),
    (Join-Path $ServerOutput "Robo.Pos.Server.runtimeconfig.json"),
    (Join-Path $ServerOutput "wwwroot\index.html"),
    (Join-Path $ServerOutput "wwwroot\app.js"),
    (Join-Path $AppRoot "licenses\DotNet-Runtime-LICENSE.txt"),
    (Join-Path $AppRoot "licenses\Microsoft.Data.Sqlite-LICENSE.txt"),
    (Join-Path $AppRoot "docs\README_FIRST.txt"),
    (Join-Path $AppRoot "docs\SECURITY.md")
)
foreach ($path in $required) {
    if (-not (Test-Path $path -PathType Leaf)) { throw "Published release is missing $path" }
}

$node = Get-Command node.exe -ErrorAction SilentlyContinue
if ($node) {
    Get-ChildItem (Join-Path $ServerOutput "wwwroot") -Filter "*.js" | ForEach-Object {
        & $node.Source --check $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "JavaScript syntax validation failed for $($_.Name)." }
    }
}

$smokeTest = Join-Path $Root "RELEASE_SMOKE_TEST.ps1"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $smokeTest -ServerExe (Join-Path $ServerOutput "Robo.Pos.Server.exe") -RuntimeRoot $ServerOutput
if ($LASTEXITCODE -ne 0) { throw "Automated release smoke test failed." }

function Find-SignTool {
    $candidate = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($candidate) { return $candidate.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $kits) {
        return Get-ChildItem $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like "*\x64\signtool.exe" } |
            Sort-Object FullName -Descending |
            Select-Object -ExpandProperty FullName -First 1
    }
    return $null
}

function Sign-File {
    param([string]$Path, [string]$SignTool)
    & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d "Nexus POS" $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $Path" }
    & $SignTool verify /pa /all $Path
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $Path" }
}

$signTool = $null
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $signTool = Find-SignTool
    if (-not $signTool) { throw "signtool.exe was not found." }
    Sign-File (Join-Path $ServerOutput "Robo.Pos.Server.exe") $signTool
    Sign-File (Join-Path $ServerOutput "Robo.Pos.Server.dll") $signTool
    Sign-File (Join-Path $LauncherOutput "Nexus.Pos.Launcher.exe") $signTool
}
else {
    Write-Warning "No certificate thumbprint was supplied. This build is unsigned and must not be distributed to customers."
}

$portableStage = Join-Path $env:TEMP ("nexus-pos-portable-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $portableStage | Out-Null
try {
    Copy-Item $AppRoot (Join-Path $portableStage "app") -Recurse -Force
    foreach ($file in @(
        "RUN_NEXUS_POS_PORTABLE.cmd",
        "REPAIR_AND_DIAGNOSE.cmd",
        "ENABLE_SHOP_NETWORK.cmd",
        "DISABLE_SHOP_NETWORK.cmd",
        "CONFIGURE_CLOUDFLARE_DOMAIN.cmd",
        "CHECK_FOR_UPDATES.cmd",
        "README_FIRST.txt",
        "LICENSE.txt",
        "PRIVACY_NOTICE_TEMPLATE.txt"
    )) {
        $source = Join-Path $Root $file
        if (Test-Path $source) { Copy-Item $source $portableStage -Force }
    }
    $portableZip = Join-Path $ReleaseRoot "Nexus_POS_${Version}_Portable.zip"
    Remove-Item $portableZip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $portableStage "*") -DestinationPath $portableZip -CompressionLevel Optimal
}
finally {
    Remove-Item $portableStage -Recurse -Force -ErrorAction SilentlyContinue
}

$installerPath = Join-Path $ReleaseRoot "Nexus_POS_Setup_${Version}.exe"
if (-not $SkipInstaller) {
    $isccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )
    $iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($iscc) {
        & $iscc (Join-Path $Root "setup\NexusPOS.iss")
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }
        if ($signTool -and (Test-Path $installerPath)) { Sign-File $installerPath $signTool }
    }
    else {
        Write-Warning "Inno Setup 6 was not found. Portable ZIP was built, but the installer EXE was skipped."
    }
}

$installHelperRoot = Join-Path $Root "release-engineering"
foreach ($helper in @("INSTALL_NEXUS_POS.ps1", "INSTALL_NEXUS_POS.cmd")) {
    $helperPath = Join-Path $installHelperRoot $helper
    if (Test-Path -LiteralPath $helperPath -PathType Leaf) {
        Copy-Item -LiteralPath $helperPath -Destination $ReleaseRoot -Force
    }
}

$hashList = Join-Path $ReleaseRoot "SHA256SUMS.txt"
Remove-Item $hashList -Force -ErrorAction SilentlyContinue
Get-ChildItem $ReleaseRoot -File | Where-Object { $_.Extension -in ".zip", ".exe" } | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)" | Add-Content $hashList -Encoding ASCII
}

Write-Host ""
Write-Host "Nexus POS release build completed successfully." -ForegroundColor Green
Write-Host "Output: $ReleaseRoot" -ForegroundColor Green
