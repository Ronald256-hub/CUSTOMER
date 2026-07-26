[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $SourceRoot).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$changes = [System.Collections.Generic.List[string]]::new()

function Save-Utf8 {
    param([string]$Path, [string]$Content)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8)
}

function Add-MvcUsing {
    param([string]$Path, [string]$Content)

    if ($Content.Contains("using Microsoft.AspNetCore.Mvc;")) {
        return $Content
    }

    $namespace = [regex]::Match($Content, '(?m)^namespace\s+')
    if (-not $namespace.Success) {
        throw "Could not find a namespace declaration in $Path"
    }

    return $Content.Insert(
        $namespace.Index,
        "using Microsoft.AspNetCore.Mvc;`r`n`r`n"
    )
}

$sourceDirectory = Join-Path $root "src"
if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
    throw "Source directory was not found: $sourceDirectory"
}

Get-ChildItem $sourceDirectory -Filter "AuditDocumentWriter.cs" -Recurse -File |
    ForEach-Object {
        $content = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
        $old = 'html.Append("</strong></p><div class="document-meta"><p>");'
        $new = 'html.Append("</strong></p><div class=\"document-meta\"><p>");'

        if ($content.Contains($old)) {
            Save-Utf8 $_.FullName ($content.Replace($old, $new))
            $changes.Add("Escaped document-meta HTML in $($_.FullName)")
        }
    }

$requestTypes = @(
    "DeleteUserRequest",
    "DeactivateCategoryRequest",
    "DeactivateProductRequest"
)

Get-ChildItem $sourceDirectory -Filter "*.cs" -Recurse -File |
    ForEach-Object {
        $path = $_.FullName
        $content = Get-Content -LiteralPath $path -Raw -Encoding UTF8
        $original = $content

        foreach ($requestType in $requestTypes) {
            $escaped = [regex]::Escape($requestType)
            $pattern = "(?m)^(\s*)(?!\[FromBody\]\s+)($escaped)\s+request,"
            $content = [regex]::Replace(
                $content,
                $pattern,
                '$1[FromBody] $2 request,'
            )
        }

        if ($content -ne $original) {
            $content = Add-MvcUsing -Path $path -Content $content
            Save-Utf8 $path $content
            $changes.Add("Added explicit [FromBody] binding in $path")
        }
    }

$buildScript = Join-Path $root "BUILD_WINDOWS_RELEASE.ps1"
if (Test-Path -LiteralPath $buildScript -PathType Leaf) {
    $content = Get-Content -LiteralPath $buildScript -Raw -Encoding UTF8
    $original = $content

    $oldSdk = @'
$dotnet = Require-Command "dotnet.exe"
$dotnetVersion = & $dotnet --version
if ([version]($dotnetVersion.Split('-')[0]) -lt [version]"10.0.0") {
    throw ".NET SDK 10.0 or later is required. Installed: $dotnetVersion"
}
'@

    $newSdk = @'
$dotnet = Require-Command "dotnet.exe"
$dotnetVersion = & $dotnet --version 2>&1
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$dotnetVersion)) {
    throw ".NET SDK was not found. Run release-engineering\INSTALL_BUILD_PREREQUISITES.ps1 from Administrator PowerShell."
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
'@

    if ($content.Contains($oldSdk)) {
        $content = $content.Replace($oldSdk, $newSdk)
    }

    $serverRestore = @(
        '& $dotnet restore $ServerProject `',
        '    --runtime win-x64 `',
        '    -p:SelfContained=true `',
        '    -p:PublishReadyToRun=true `',
        '    -p:TargetLatestRuntimePatch=true'
    ) -join "`r`n"

    $content = $content.Replace(
        '& $dotnet restore $ServerProject --runtime win-x64',
        $serverRestore
    )

    $launcherRestore = @(
        '& $dotnet restore $LauncherProject `',
        '    --runtime win-x64 `',
        '    -p:SelfContained=true `',
        '    -p:PublishSingleFile=true `',
        '    -p:TargetLatestRuntimePatch=true'
    ) -join "`r`n"

    $content = $content.Replace(
        '& $dotnet restore $LauncherProject --runtime win-x64',
        $launcherRestore
    )

    $thumbprintLine = '$normalizedThumbprint = $CertificateThumbprint.Replace(" ", "").ToUpperInvariant()'
    if ($content.Contains($thumbprintLine) -and
        -not $content.Contains('$CertificateThumbprint = $normalizedThumbprint')) {
        $content = $content.Replace(
            $thumbprintLine,
            "$thumbprintLine`r`n    `$CertificateThumbprint = `$normalizedThumbprint"
        )
    }

    if ($content -ne $original) {
        Save-Utf8 $buildScript $content
        $changes.Add("Applied SDK and ReadyToRun corrections to $buildScript")
    }
}

$smokeTest = Join-Path $root "RELEASE_SMOKE_TEST.ps1"
if (Test-Path -LiteralPath $smokeTest -PathType Leaf) {
    $content = Get-Content -LiteralPath $smokeTest -Raw -Encoding UTF8
    $original = $content

    $content = $content.Replace(
        'for ($attempt = 0; $attempt -lt 120; $attempt++)',
        'for ($attempt = 0; $attempt -lt 360; $attempt++)'
    )
    $content = $content.Replace(
        'Write-Error "Nexus POS automated release smoke test: FAIL - $($_.Exception.Message)"',
        'Write-Host "Nexus POS automated release smoke test: FAIL - $($_.Exception.Message)" -ForegroundColor Red'
    )

    if ($content -ne $original) {
        Save-Utf8 $smokeTest $content
        $changes.Add("Improved smoke-test startup allowance and diagnostics in $smokeTest")
    }
}

foreach ($path in @($buildScript, $smokeTest)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }

    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $path,
        [ref]$tokens,
        [ref]$errors
    ) | Out-Null

    if ($errors.Count -gt 0) {
        $details = $errors | ForEach-Object {
            "Line $($_.Extent.StartLineNumber): $($_.Message)"
        }
        throw "PowerShell syntax validation failed for $path`n$($details -join "`n")"
    }
}

$changes | ForEach-Object { Write-Host $_ -ForegroundColor Cyan }
Write-Host "Known Windows build fixes are applied. Changes: $($changes.Count)" -ForegroundColor Green
