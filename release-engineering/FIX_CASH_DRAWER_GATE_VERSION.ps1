$ErrorActionPreference = 'Stop'

$path = Join-Path (Split-Path $PSScriptRoot -Parent) 'release-engineering/VERIFY_CASH_DRAWER_RECONCILIATION.ps1'
$content = [System.IO.File]::ReadAllText($path)
$old = '    if ($service.version -ne "6.9.0") { throw "The service version is not 6.9.0." }'
$new = '    if ($service.version -ne "7.0.0") { throw "The service version is not 7.0.0." }'
$count = ([regex]::Matches($content, [regex]::Escape($old))).Count
if ($count -ne 1) {
    throw "Cash drawer transaction version assertion expected one match but found $count."
}

[System.IO.File]::WriteAllText(
    $path,
    $content.Replace($old, $new),
    [System.Text.UTF8Encoding]::new($false))

Write-Host 'Cash drawer transaction gate advanced to Nexus 7.0.0.'
