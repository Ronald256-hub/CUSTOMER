$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$workspace = Join-Path $root 'src/Robo.Pos.Server/wwwroot/executive-intelligence.js'
$navigation = Join-Path $root 'src/Robo.Pos.Server/wwwroot/executive-intelligence-navigation.js'
$style = Join-Path $root 'src/Robo.Pos.Server/wwwroot/executive-intelligence.css'
$index = Join-Path $root 'src/Robo.Pos.Server/wwwroot/index.html'
$browser = Join-Path $root 'release-engineering/VERIFY_OPERATOR_EXPERIENCE_BROWSER.mjs'

foreach ($path in @($workspace, $navigation, $style, $index, $browser)) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Required executive-intelligence asset missing: $path"
    }
}

node --check $workspace
if ($LASTEXITCODE -ne 0) { throw 'Executive intelligence JavaScript parsing failed.' }
node --check $navigation
if ($LASTEXITCODE -ne 0) { throw 'Executive intelligence navigation parsing failed.' }
node --check $browser
if ($LASTEXITCODE -ne 0) { throw 'Microsoft Edge acceptance script parsing failed.' }

$js = Get-Content $workspace -Raw
$navigationText = Get-Content $navigation -Raw
$css = Get-Content $style -Raw
$html = Get-Content $index -Raw
$browserText = Get-Content $browser -Raw

$requiredWorkspace = @(
    'Executive intelligence control tower',
    'Business performance pulse',
    'Payment mix',
    'Risk radar',
    'Short-glass quantity and revenue watch',
    'Customer growth pulse',
    'Workforce readiness',
    '/api/v3/reports/sales/summary',
    '/api/v3/admin/summary',
    '/api/v3/admin/inventory/products',
    '/api/v3/reports/short-glass',
    '/api/v3/finance/receivables',
    '/api/v3/finance/payables',
    '/api/v3/finance/cashbook',
    '/api/v3/procurement/reorder-recommendations',
    '/api/v3/crm/dashboard',
    '/api/v3/hrm/dashboard',
    'Export intelligence CSV',
    'Reporting scope',
    'Last 7 days'
)
foreach ($token in $requiredWorkspace) {
    if (-not $js.Contains($token)) {
        throw "Executive intelligence workspace missing required token: $token"
    }
}

foreach ($writeToken in @('method: "POST"', 'method: "PUT"', 'method: "DELETE"', 'expectedVersion')) {
    if ($js.Contains($writeToken)) {
        throw "Executive intelligence must remain read-only; forbidden token found: $writeToken"
    }
}

$requiredNavigation = @(
    'installExecutiveIntelligenceRoute',
    'ensureNavigationButton',
    'ensureCommandResult',
    'stopImmediatePropagation',
    'history.replaceState',
    'window.addEventListener("hashchange"',
    'NexusExecutiveIntelligence',
    'intelligence'
)
foreach ($token in $requiredNavigation) {
    if (-not $navigationText.Contains($token)) {
        throw "Executive intelligence route bridge missing required token: $token"
    }
}
if ($navigationText.Contains('HashChangeEvent')) {
    throw 'Executive intelligence route bridge must not synthesize hashchange events.'
}

foreach ($token in @(
    '.executive-intelligence-workspace',
    '.ei-metrics',
    '.ei-branch-row',
    '.ei-short-row',
    '@media (max-width: 760px)',
    '@media print',
    'prefers-reduced-motion'
)) {
    if (-not $css.Contains($token)) {
        throw "Executive intelligence CSS missing required token: $token"
    }
}

foreach ($token in @(
    '/executive-intelligence.css',
    '/executive-intelligence.js',
    '/executive-intelligence-navigation.js'
)) {
    if (-not $html.Contains($token)) {
        throw "Executive intelligence asset is not wired in index.html: $token"
    }
}

foreach ($token in @(
    'Executive intelligence control tower',
    'Business performance pulse',
    'Payment mix',
    'Risk radar',
    'Short-glass quantity and revenue watch',
    'Reporting scope',
    'Last 7 days',
    'Export intelligence CSV',
    'Print control tower'
)) {
    if (-not $browserText.Contains($token)) {
        throw "Microsoft Edge journey missing executive-intelligence assertion: $token"
    }
}

Write-Host 'Executive intelligence source, routing, read-only controls and browser assertions passed.'
