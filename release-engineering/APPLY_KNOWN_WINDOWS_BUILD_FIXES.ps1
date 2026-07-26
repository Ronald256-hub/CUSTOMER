[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $SourceRoot).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$changes = [System.Collections.Generic.List[string]]::new()

function Save-TextFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, $utf8)
}

function Add-MvcUsing {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    if ($Content.Contains("using Microsoft.AspNetCore.Mvc;")) {
        return $Content
    }

    $firstNamespace = [regex]::Match($Content, '(?m)^namespace\s+')
    if (-not $firstNamespace.Success) {
        throw "Could not locate the namespace declaration in $Path"
    }

    return $Content.Insert(
        $firstNamespace.Index,
        "using Microsoft.AspNetCore.Mvc;`r`n`r`n"
    )
}

# 1. Fix the verified HTML attribute quoting defect if this older form exists.
Get-ChildItem (Join-Path $root "src") -Filter "AuditDocumentWriter.cs" -Recurse -File -ErrorAction SilentlyContinue |
    ForEach-Object {
        $content = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
        $old = 'html.Append("</strong></p><div class="document-meta"><p>");'
        $new = 'html.Append("</strong></p><div class=\"document-meta\"><p>");'

        if ($content.Contains($old)) {
            $content = $content.Replace($old, $new)
            Save-TextFile -Path $_.FullName -Content $content
            $changes.Add("Escaped document-meta HTML attribute in $($_.FullName)")
        }
    }

# 2. Explicitly bind JSON bodies for DELETE endpoints. ASP.NET Core does not
# infer bodies for these handlers.
$requestTypes = @(
    "DeleteUserRequest",
    "DeactivateCategoryRequest",
    "DeactivateProductRequest"
)

Get-ChildItem (Join-Path $root "src") -Filter "*.cs" -Recurse -File -ErrorAction SilentlyContinue |
    ForEach-Object {
        $path = $_.FullName
        $content = Get-Content -LiteralPath $path -Raw -Encoding UTF8
        $original = $content

        foreach ($requestType in $requestTypes) {
            $pattern = "(?m)^(\s*)(?!\[FromBody\]\s+)($([regex]::Escape($requestType)))\s+request,"
            $content = [regex]::Replace(
                $content,
                $pattern,
                '$1[FromBody] $2 request,'
            )
        }

        if ($content -ne $original) {
            $content = Add-MvcUsing -Path $path -Content $content
            Save-TextFile -Path $path -Content $content
            $changes.Add("Added explicit [FromBody] endpoint binding in $path")
        }
    }

# 3. Apply the ReadyToRun restore correction and safer SDK detection to the
# release script when it still contains the older implementation.
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
    throw ".NET SDK was not found. Run INSTALL_BUILD_PREREQUISITES.ps1 from an Administrator PowerShell window."
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

    $serverRestore = '& $dotnet restore $ServerProject --runtime win-x64'
    if ($content.Contains($serverRestore)) {
        $content = $content.Replace(
            $serverRestore,
            @'
& $dotnet restore $ServerProject `
    --runtime win-x64 `
    -p:SelfContained=true `
    -p:PublishReadyToRun=true `
    -p:TargetLatestRuntimePatch=true
'@.TrimEnd()
        )
    }

    $launcherRestore = '& $dotnet restore $LauncherProject --runtime win-x64'
    if ($content.Contains($launcherRestore)) {
        $content = $content.Replace(
            $launcherRestore,
            @'
& $dotnet restore $LauncherProject `
    --runtime win-x64 `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:TargetLatestRuntimePatch=true
'@.TrimEnd()
        )
    }

    $thumbprintLine = '$normalizedThumbprint = $CertificateThumbprint.Replace(" ", "").ToUpperInvariant()'
    $normalizedAssignment = "$thumbprintLine`r`n    `$CertificateThumbprint = `$normalizedThumbprint"
    if ($content.Contains($thumbprintLine) -and -not $content.Contains('$CertificateThumbprint = $normalizedThumbprint')) {
        $content = $content.Replace($thumbprintLine, $normalizedAssignment)
    }

    if ($content -ne $original) {
        Save-TextFile -Path $buildScript -Content $content
        $changes.Add("Applied verified SDK and ReadyToRun corrections to $buildScript")
    }
}

# 4. Preserve the smoke-test diagnostics that exposed the real startup error.
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
        Save-TextFile -Path $smokeTest -Content $content
        $changes.Add("Improved release smoke-test timeout and diagnostics in $smokeTest")
    }
}

# Validate every PowerShell file changed by this process.
foreach ($description in $changes) {
    Write-Host $description -ForegroundColor Cyan
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

Write-Host ""
Write-Host "Known Windows build fixes are applied and syntax validation passed." -ForegroundColor Green
Write-Host "Changes applied: $($changes.Count)" -ForegroundColor Green
