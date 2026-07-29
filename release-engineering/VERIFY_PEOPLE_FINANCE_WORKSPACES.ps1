$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $root 'src/Robo.Pos.Server/wwwroot/people-finance-workspaces.js'
$style = Join-Path $root 'src/Robo.Pos.Server/wwwroot/people-finance-workspaces.css'
$index = Join-Path $root 'src/Robo.Pos.Server/wwwroot/index.html'
$browser = Join-Path $root 'release-engineering/VERIFY_OPERATOR_EXPERIENCE_BROWSER.mjs'

foreach ($path in @($script, $style, $index, $browser)) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Required CRM/finance/HRM workspace asset missing: $path"
    }
}

node --check $script
if ($LASTEXITCODE -ne 0) { throw 'CRM/finance/HRM JavaScript parsing failed.' }
node --check $browser
if ($LASTEXITCODE -ne 0) { throw 'Extended Microsoft Edge browser script parsing failed.' }

$js = Get-Content $script -Raw
$css = Get-Content $style -Raw
$html = Get-Content $index -Raw
$browserText = Get-Content $browser -Raw

$requiredJs = @(
    'CRM transactional workspace',
    'Create customer profile',
    '/api/v3/crm/customers',
    '/api/v3/crm/tasks',
    'Receivables, payables and cashbook',
    '/api/v3/finance/customer-receipts',
    '/api/v3/finance/supplier-payments',
    'People, attendance, leave and payroll',
    '/api/v3/hrm/attendance/clock-in',
    '/api/v3/hrm/leave-requests',
    '/api/v3/hrm/payroll-periods',
    'expectedVersion',
    'allocations:'
)
foreach ($token in $requiredJs) {
    if (-not $js.Contains($token)) {
        throw "CRM/finance/HRM JavaScript missing required token: $token"
    }
}

foreach ($token in @('.pfh-workspace', '.pfh-grid', '.pfh-status', '@media (max-width: 620px)', 'prefers-reduced-motion')) {
    if (-not $css.Contains($token)) {
        throw "CRM/finance/HRM CSS missing required token: $token"
    }
}

foreach ($token in @('/people-finance-workspaces.css', '/people-finance-workspaces.js')) {
    if (-not $html.Contains($token)) {
        throw "CRM/finance/HRM asset is not wired in index.html: $token"
    }
}

foreach ($token in @('CRM transactional workspace', 'Customer profiles', 'Schedule follow-up', 'Receivables, payables and cashbook', 'Open supplier obligations', 'Posted cash movement', 'People, attendance, leave and payroll', 'Today’s attendance', 'Leave requests', 'Payroll periods')) {
    if (-not $browserText.Contains($token)) {
        throw "Microsoft Edge journey missing workspace assertion: $token"
    }
}

Write-Host 'CRM, finance and HRM workspace source validation passed.'
