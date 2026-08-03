$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$required = @{
    Migration = Join-Path $root 'src/Robo.Pos.Server/Data/Migrations/019_cash_drawer_reconciliation.sql'
    MigrationRunner = Join-Path $root 'src/Robo.Pos.Server/Data/CashDrawerReconciliationMigration.cs'
    DatabaseBootstrap = Join-Path $root 'src/Robo.Pos.Server/Data/DatabaseBootstrap.cs'
    Models = Join-Path $root 'src/Robo.Pos.Server/Sales/CashDrawerModels.cs'
    Service = Join-Path $root 'src/Robo.Pos.Server/Sales/CashDrawerService.cs'
    Endpoints = Join-Path $root 'src/Robo.Pos.Server/Sales/CashDrawerEndpoints.cs'
    ShiftService = Join-Path $root 'src/Robo.Pos.Server/Sales/ShopShiftService.cs'
    Program = Join-Path $root 'src/Robo.Pos.Server/Program.cs'
    Workspace = Join-Path $root 'src/Robo.Pos.Server/wwwroot/cash-drawer.js'
    Navigation = Join-Path $root 'src/Robo.Pos.Server/wwwroot/cash-drawer-navigation.js'
    Style = Join-Path $root 'src/Robo.Pos.Server/wwwroot/cash-drawer.css'
    Index = Join-Path $root 'src/Robo.Pos.Server/wwwroot/index.html'
    Browser = Join-Path $root 'release-engineering/VERIFY_CASH_DRAWER_BROWSER.mjs'
    TransactionGate = Join-Path $root 'release-engineering/VERIFY_CASH_DRAWER_RECONCILIATION.ps1'
}

foreach ($entry in $required.GetEnumerator()) {
    if (-not (Test-Path $entry.Value -PathType Leaf)) {
        throw "Required cash-drawer asset missing: $($entry.Key) -> $($entry.Value)"
    }
}

foreach ($path in @($required.Workspace, $required.Navigation, $required.Browser)) {
    node --check $path
    if ($LASTEXITCODE -ne 0) {
        throw "JavaScript parsing failed: $path"
    }
}

$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $required.TransactionGate,
    [ref]$tokens,
    [ref]$errors
) | Out-Null
if ($errors.Count -gt 0) {
    throw (($errors | ForEach-Object {
        "VERIFY_CASH_DRAWER_RECONCILIATION.ps1:$($_.Extent.StartLineNumber): $($_.Message)"
    }) -join "`n")
}

$text = @{}
foreach ($entry in $required.GetEnumerator()) {
    $text[$entry.Key] = Get-Content $entry.Value -Raw
}

foreach ($token in @(
    'CREATE TABLE IF NOT EXISTS cash_drawer_movements',
    'CREATE TABLE IF NOT EXISTS shift_cash_counts',
    'CREATE TABLE IF NOT EXISTS shift_reconciliation_reviews',
    "CHECK (movement_type IN ('float_in', 'safe_drop'))",
    "CHECK (review_status IN ('pending', 'approved', 'rejected'))",
    'cash drawer movements are immutable',
    'cash drawer movements are permanent audit records',
    'cash counts are immutable',
    'invalid shift reconciliation review transition',
    'shift reconciliation reviews are permanent audit records',
    'CREATE TRIGGER IF NOT EXISTS trg_shift_close_cash_custody',
    'NEW.expected_cash_minor',
    'NEW.counted_cash_minor',
    'NEW.cash_variance_minor',
    "19,",
    'Cash drawer custody movements, denomination counts and shift reconciliation reviews'
)) {
    if (-not $text.Migration.Contains($token)) {
        throw "Cash-drawer migration missing invariant token: $token"
    }
}

$triggerStart = $text.Migration.IndexOf('CREATE TRIGGER IF NOT EXISTS trg_shift_close_cash_custody')
$schemaStart = $text.Migration.IndexOf('INSERT OR IGNORE INTO schema_versions', $triggerStart)
if ($triggerStart -lt 0 -or $schemaStart -lt 0) {
    throw 'The shift-close review trigger boundary could not be verified.'
}
$triggerText = $text.Migration.Substring($triggerStart, $schemaStart - $triggerStart)
if ($triggerText.Contains('UPDATE teller_shifts')) {
    throw 'Shift-close trigger must create the review only; custody calculation belongs in ShopShiftService.'
}
if (-not $triggerText.Contains('INSERT OR IGNORE INTO shift_reconciliation_reviews')) {
    throw 'Shift-close trigger does not create the immutable reconciliation review.'
}

foreach ($token in @(
    'public const int Version = 19',
    '019_cash_drawer_reconciliation.sql'
)) {
    if (-not $text.MigrationRunner.Contains($token)) {
        throw "Cash-drawer migration runner missing token: $token"
    }
}
if (-not $text.DatabaseBootstrap.Contains('CashDrawerReconciliationMigration.ApplyAsync')) {
    throw 'Database bootstrap does not apply schema version 19.'
}

foreach ($token in @(
    'GetCurrentAsync',
    'CreateMovementAsync',
    'RecordCountAsync',
    'ListReviewsAsync',
    'ReviewAsync',
    'safe_drop_exceeds_drawer',
    'cash_drawer.movement.completed',
    'cash_drawer.count.recorded',
    'cash_drawer.reconciliation.',
    'opening + cashSales - refunds + floatIn - safeDrop',
    'RequireAdministrator(user)',
    'shift_review_unavailable'
)) {
    if (-not $text.Service.Contains($token)) {
        throw "Cash-drawer service missing control token: $token"
    }
}

foreach ($token in @(
    '/api/v3/cash-drawer/current',
    '/api/v3/cash-drawer/movements',
    '/api/v3/cash-drawer/counts',
    '/api/v3/admin/cash-drawer/reconciliations',
    '/api/v3/admin/cash-drawer/reconciliations/{shiftId}/review',
    'RequireAdminAsync'
)) {
    if (-not $text.Endpoints.Contains($token)) {
        throw "Cash-drawer endpoint missing token: $token"
    }
}

foreach ($token in @(
    'cash_drawer_movements',
    "movement_type = 'float_in'",
    "movement_type = 'safe_drop'",
    'openingCash + cashSales - cashRefunds + floatIn - safeDrop',
    'floatIn,',
    'safeDrop,',
    'expectedCash,',
    'variance'
)) {
    if (-not $text.ShiftService.Contains($token)) {
        throw "Shift-close custody integration missing token: $token"
    }
}

foreach ($token in @(
    'version = "7.0.0"',
    'cash-drawer-custody-controls',
    'audited-float-and-safe-drops',
    'denomination-cash-counts',
    'manager-shift-reconciliation',
    'immutable-cash-drawer-register',
    'AddSingleton<CashDrawerService>',
    'MapCashDrawerEndpoints',
    'audited-journal-reversals',
    'shop-and-consolidated-trial-balance',
    'automatic-operational-reversals',
    'customer-and-supplier-statements',
    'procurement-performance-reporting',
    'customer-segmentation-and-dashboard',
    'workforce-dashboard-and-analytics',
    'optional-hard-shop-and-user-limits'
)) {
    if (-not $text.Program.Contains($token)) {
        throw "Nexus 7.0 registration or preserved platform capability missing: $token"
    }
}

foreach ($token in @(
    'Cash drawer and shift reconciliation',
    'Record drawer movement',
    'Count cash by denomination',
    'Shift reconciliation queue',
    '/api/v3/cash-drawer/current',
    '/api/v3/cash-drawer/movements',
    '/api/v3/cash-drawer/counts',
    '/api/v3/admin/cash-drawer/reconciliations',
    'Approve reconciliation',
    'Reject reconciliation'
)) {
    if (-not $text.Workspace.Contains($token)) {
        throw "Cash-drawer workspace missing token: $token"
    }
}
foreach ($writeMethod in @('method: "PUT"', 'method: "PATCH"', 'method: "DELETE"')) {
    if ($text.Workspace.Contains($writeMethod)) {
        throw "Cash-drawer workspace contains unsupported mutation method: $writeMethod"
    }
}

foreach ($token in @(
    'installCashDrawerRoute',
    'history.replaceState',
    'stopImmediatePropagation',
    'window.addEventListener("hashchange"',
    'NexusCashDrawer',
    'cash-drawer'
)) {
    if (-not $text.Navigation.Contains($token)) {
        throw "Cash-drawer route bridge missing token: $token"
    }
}
if ($text.Navigation.Contains('HashChangeEvent')) {
    throw 'Cash-drawer navigation must not synthesize hashchange events.'
}

foreach ($token in @(
    '.cash-drawer-workspace',
    '.cd-kpis',
    '.cd-control-grid',
    '.cd-review-actions',
    '@media (max-width: 560px)',
    '@media print',
    'prefers-reduced-motion',
    'min-width: 0',
    'overflow: hidden'
)) {
    if (-not $text.Style.Contains($token)) {
        throw "Cash-drawer responsive styling missing token: $token"
    }
}

foreach ($token in @(
    '/cash-drawer.css',
    '/cash-drawer.js',
    '/cash-drawer-navigation.js'
)) {
    if (-not $text.Index.Contains($token)) {
        throw "Cash-drawer asset is not wired into index.html: $token"
    }
}

foreach ($token in @(
    'Cash drawer and shift reconciliation',
    'Record drawer movement',
    'Count cash by denomination',
    'Shift reconciliation queue',
    '11,000 UGX',
    'cash drawer desktop workspace has horizontal overflow',
    'cash drawer mobile workspace has horizontal overflow'
)) {
    if (-not $text.Browser.Contains($token)) {
        throw "Microsoft Edge cash-drawer journey missing assertion: $token"
    }
}

foreach ($token in @(
    'schemaVersion -ne 19',
    'safe_drop_exceeds_drawer',
    'expectedDrawerCashMinor -ne 11000',
    'Drawer custody movements or cash counts created an accounting journal',
    'expectedCashMinor -ne 11000',
    'shift_review_unavailable',
    'false accounting journal',
    'VERIFY_CASH_DRAWER_BROWSER.mjs'
)) {
    if (-not $text.TransactionGate.Contains($token)) {
        throw "Cash-drawer transaction gate missing assertion token: $token"
    }
}

Write-Host 'Cash-drawer schema, custody math, immutable review, preserved capabilities, UI, routing and transaction assertions passed.'