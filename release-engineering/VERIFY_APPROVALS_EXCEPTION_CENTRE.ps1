$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$workspace = Join-Path $root 'src/Robo.Pos.Server/wwwroot/approvals-exception-centre.js'
$navigation = Join-Path $root 'src/Robo.Pos.Server/wwwroot/approvals-exception-navigation.js'
$style = Join-Path $root 'src/Robo.Pos.Server/wwwroot/approvals-exception-centre.css'
$index = Join-Path $root 'src/Robo.Pos.Server/wwwroot/index.html'
$browser = Join-Path $root 'release-engineering/VERIFY_OPERATOR_EXPERIENCE_BROWSER.mjs'

foreach ($path in @($workspace, $navigation, $style, $index, $browser)) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Required approvals-and-exception asset missing: $path"
    }
}

node --check $workspace
if ($LASTEXITCODE -ne 0) { throw 'Approvals and exception JavaScript parsing failed.' }
node --check $navigation
if ($LASTEXITCODE -ne 0) { throw 'Approvals and exception navigation parsing failed.' }
node --check $browser
if ($LASTEXITCODE -ne 0) { throw 'Microsoft Edge acceptance script parsing failed.' }

$js = Get-Content $workspace -Raw
$navigationText = Get-Content $navigation -Raw
$css = Get-Content $style -Raw
$html = Get-Content $index -Raw
$browserText = Get-Content $browser -Raw

$requiredWorkspace = @(
    'Approvals and exception centre',
    'Priority queue',
    'Detected items',
    'Urgent or high',
    'Maker-checker decisions',
    'Search queue',
    'Severity',
    'Export CSV',
    'Refresh centre',
    'Approval required',
    '/api/v3/admin/summary',
    '/api/v3/admin/inventory/products',
    '/api/v3/procurement/reorder-recommendations',
    '/api/v3/procurement/purchase-orders',
    '/api/v3/finance/receivables',
    '/api/v3/finance/payables',
    '/api/v3/crm/tasks',
    '/api/v3/crm/quotations',
    '/api/v3/hrm/attendance',
    '/api/v3/hrm/leave-requests',
    '/api/v3/hrm/payroll-periods',
    '/api/v3/saas/tenant/subscription',
    'severityRank',
    'daysUntil',
    'filteredItems',
    'NexusApprovalsExceptionCentre'
)
foreach ($token in $requiredWorkspace) {
    if (-not $js.Contains($token)) {
        throw "Approvals and exception workspace missing required token: $token"
    }
}

foreach ($writeToken in @(
    'method: "POST"',
    'method: "PUT"',
    'method: "PATCH"',
    'method: "DELETE"',
    'expectedVersion',
    '/approve',
    '/reject',
    '/complete'
)) {
    if ($js.Contains($writeToken)) {
        throw "Approvals and exception centre must remain read-only; forbidden token found: $writeToken"
    }
}

$requiredNavigation = @(
    'installApprovalsExceptionRoute',
    'ensureNavigationButton',
    'ensureCommandResult',
    'stopImmediatePropagation',
    'history.replaceState',
    'window.addEventListener("hashchange"',
    'NexusApprovalsExceptionCentre',
    'exceptions'
)
foreach ($token in $requiredNavigation) {
    if (-not $navigationText.Contains($token)) {
        throw "Approvals and exception route bridge missing required token: $token"
    }
}
foreach ($forbidden in @('HashChangeEvent', 'dispatchEvent(')) {
    if ($navigationText.Contains($forbidden)) {
        throw "Approvals and exception route bridge must not synthesize route events: $forbidden"
    }
}

foreach ($token in @(
    '.approvals-exception-workspace',
    '.exception-metrics',
    '.exception-card',
    '.exception-severity',
    '.exception-governance',
    '@media (max-width: 680px)',
    '@media (max-width: 430px)',
    '@media print'
)) {
    if (-not $css.Contains($token)) {
        throw "Approvals and exception CSS missing required token: $token"
    }
}

foreach ($token in @(
    '/approvals-exception-centre.css',
    '/approvals-exception-centre.js',
    '/approvals-exception-navigation.js'
)) {
    if (-not $html.Contains($token)) {
        throw "Approvals and exception asset is not wired in index.html: $token"
    }
}

$workspacePosition = $html.IndexOf('/approvals-exception-centre.js')
$navigationPosition = $html.IndexOf('/approvals-exception-navigation.js')
if ($workspacePosition -lt 0 -or $navigationPosition -lt 0 -or $workspacePosition -gt $navigationPosition) {
    throw 'Approvals workspace must load before its navigation bridge.'
}

foreach ($token in @(
    'approvals',
    'exceptions',
    'Approvals and exception centre',
    'Priority queue',
    'Refresh centre',
    'Export CSV',
    '#exceptionVisibleCount'
)) {
    if (-not $browserText.Contains($token)) {
        throw "Microsoft Edge journey missing approvals-and-exception assertion: $token"
    }
}

Write-Host 'Approvals and exception source, routing, read-only controls and browser assertions passed.'
