$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$required = @{
    Migration = Join-Path $root 'src/Robo.Pos.Server/Data/Migrations/017_sales_returns_refunds.sql'
    OperationalMigration = Join-Path $root 'src/Robo.Pos.Server/Data/Migrations/017_sales_returns_operational.sql'
    MigrationRunner = Join-Path $root 'src/Robo.Pos.Server/Data/SalesReturnsMigration.cs'
    DatabaseBootstrap = Join-Path $root 'src/Robo.Pos.Server/Data/DatabaseBootstrap.cs'
    Models = Join-Path $root 'src/Robo.Pos.Server/Sales/SalesReturnModels.cs'
    Service = Join-Path $root 'src/Robo.Pos.Server/Sales/SalesReturnService.cs'
    Endpoints = Join-Path $root 'src/Robo.Pos.Server/Sales/SalesReturnEndpoints.cs'
    DocumentWriter = Join-Path $root 'src/Robo.Pos.Server/Sales/SalesReturnDocumentWriter.cs'
    Reporting = Join-Path $root 'src/Robo.Pos.Server/Sales/ShopSalesReportingService.cs'
    ShiftService = Join-Path $root 'src/Robo.Pos.Server/Sales/ShopShiftService.cs'
    Program = Join-Path $root 'src/Robo.Pos.Server/Program.cs'
    Workspace = Join-Path $root 'src/Robo.Pos.Server/wwwroot/sales-returns.js'
    Navigation = Join-Path $root 'src/Robo.Pos.Server/wwwroot/sales-returns-navigation.js'
    Style = Join-Path $root 'src/Robo.Pos.Server/wwwroot/sales-returns.css'
    Index = Join-Path $root 'src/Robo.Pos.Server/wwwroot/index.html'
    Browser = Join-Path $root 'release-engineering/VERIFY_OPERATOR_EXPERIENCE_BROWSER.mjs'
    TransactionGate = Join-Path $root 'release-engineering/VERIFY_SALES_RETURNS.ps1'
}

foreach ($entry in $required.GetEnumerator()) {
    if (-not (Test-Path $entry.Value -PathType Leaf)) {
        throw "Required sales-return asset missing: $($entry.Key) -> $($entry.Value)"
    }
}

node --check $required.Workspace
if ($LASTEXITCODE -ne 0) { throw 'Sales returns workspace JavaScript parsing failed.' }
node --check $required.Navigation
if ($LASTEXITCODE -ne 0) { throw 'Sales returns navigation JavaScript parsing failed.' }
node --check $required.Browser
if ($LASTEXITCODE -ne 0) { throw 'Microsoft Edge acceptance script parsing failed.' }

$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $required.TransactionGate,
    [ref]$tokens,
    [ref]$errors
) | Out-Null
if ($errors.Count -gt 0) {
    throw (($errors | ForEach-Object { "VERIFY_SALES_RETURNS.ps1:$($_.Extent.StartLineNumber): $($_.Message)" }) -join "`n")
}

$text = @{}
foreach ($entry in $required.GetEnumerator()) {
    $text[$entry.Key] = Get-Content $entry.Value -Raw
}

foreach ($token in @(
    'CREATE TABLE IF NOT EXISTS sales_returns',
    'CREATE TABLE IF NOT EXISTS sales_return_items',
    'CREATE TABLE IF NOT EXISTS sales_return_accounting_links',
    'CREATE TABLE IF NOT EXISTS sales_return_documents',
    'return quantity exceeds the sold quantity',
    'completed sales returns are immutable',
    'sales returns cannot be deleted',
    "'restock', 'damaged'",
    "17,",
    'Controlled partial sales returns'
)) {
    if (-not $text.Migration.Contains($token)) {
        throw "Sales-return migration missing invariant token: $token"
    }
}

foreach ($token in @(
    'DROP TRIGGER IF EXISTS trg_sale_item_update_guard',
    'posted sale item financial values are immutable',
    'sale item return counter requires a matching draft return',
    'trg_shift_close_return_aware_cash',
    'sales_return_loyalty_adjustments',
    'trg_sales_return_loyalty_adjustment',
    'DROP VIEW IF EXISTS crm_customer_sales_metrics',
    "sale.status IN ('completed', 'partially_returned', 'returned')"
)) {
    if (-not $text.OperationalMigration.Contains($token)) {
        throw "Sales-return operational migration missing token: $token"
    }
}

foreach ($token in @(
    'ResourceSuffixes',
    '017_sales_returns_refunds.sql',
    '017_sales_returns_operational.sql',
    'public const int Version = 17'
)) {
    if (-not $text.MigrationRunner.Contains($token)) {
        throw "Sales-return migration runner missing token: $token"
    }
}

if (-not $text.DatabaseBootstrap.Contains('SalesReturnsMigration.ApplyAsync')) {
    throw 'Database bootstrap does not apply the sales-return migration.'
}

foreach ($token in @(
    'ListEligibleSalesAsync',
    'GetReturnableSaleAsync',
    'CreateReturnAsync',
    'return_quantity_exceeds_remaining',
    'refund_method_mismatch',
    'credit_sale_return_requires_account_adjustment',
    'ApplyReturnedQuantityAsync',
    'RestoreStockAsync',
    'PostAccountingAsync',
    'sales_return_accounting_links',
    'sales_return_documents',
    'sale.return.completed',
    'partially_returned',
    'returned'
)) {
    if (-not $text.Service.Contains($token)) {
        throw "Sales-return service missing token: $token"
    }
}

foreach ($token in @(
    '/api/v3/sales/returns/eligible',
    '/api/v3/sales/{saleId}/returnable',
    '/api/v3/sales/{saleId}/returns',
    '/api/v3/sales/returns/{returnId}',
    '/api/v3/sales/returns/{returnId}/documents/{documentId}',
    'RequireAdminAsync'
)) {
    if (-not $text.Endpoints.Contains($token)) {
        throw "Sales-return endpoints missing token: $token"
    }
}

foreach ($token in @(
    'ReturnedSalesMinor',
    'RestockedCostMinor',
    'GrossCostOfGoodsSoldMinor',
    "sale.status IN ('completed', 'partially_returned', 'returned')",
    'sales_returns AS header',
    'checked(grossSales - returnedSales)',
    'checked(grossCost - restockedCost)'
)) {
    if (-not $text.Reporting.Contains($token)) {
        throw "Return-aware reporting missing token: $token"
    }
}

foreach ($token in @(
    "sale.status IN ('completed', 'partially_returned', 'returned')",
    "refund_method = 'cash'",
    'long cashRefunds',
    'openingCash + cashSales - cashRefunds',
    'cashRefunds,'
    'shift.closed'
)) {
    if (-not $text.ShiftService.Contains($token)) {
        throw "Return-aware shift reconciliation or audit missing token: $token"
    }
}

foreach ($token in @(
    'version = "6.7.0"',
    'controlled-partial-sales-returns',
    'same-channel-customer-refunds',
    'return-stock-disposition',
    'automatic-sales-return-accounting',
    'printable-credit-notes',
    'MapSalesReturnEndpoints',
    'AddSingleton<SalesReturnService>',
    'AddSingleton<SalesReturnDocumentWriter>'
)) {
    if (-not $text.Program.Contains($token)) {
        throw "Nexus 6.7 program registration missing token: $token"
    }
}

foreach ($token in @(
    'Sales returns and refunds',
    'Eligible receipts',
    'Recent credit notes',
    'Stock disposition',
    'Calculated refund',
    'Complete refund and credit note',
    '/api/v3/sales/returns/eligible',
    '/api/v3/sales/${encodeURIComponent(state.selected.saleId)}/returns'
)) {
    if (-not $text.Workspace.Contains($token)) {
        throw "Sales-return workspace missing token: $token"
    }
}

foreach ($writeMethod in @('method: "PUT"', 'method: "DELETE"', 'method: "PATCH"')) {
    if ($text.Workspace.Contains($writeMethod)) {
        throw "Sales-return workspace contains unsupported mutation method: $writeMethod"
    }
}

foreach ($token in @(
    'installSalesReturnsRoute',
    'history.replaceState',
    'stopImmediatePropagation',
    'window.addEventListener("hashchange"',
    'NexusSalesReturns',
    'sales-returns'
)) {
    if (-not $text.Navigation.Contains($token)) {
        throw "Sales-return route bridge missing token: $token"
    }
}
if ($text.Navigation.Contains('HashChangeEvent')) {
    throw 'Sales-return navigation must not synthesize hashchange events.'
}

foreach ($token in @(
    '.sales-returns-workspace',
    '.sr-layout',
    '.sr-line',
    '@media (max-width: 560px)',
    '@media print',
    'prefers-reduced-motion',
    'min-width: 0',
    'overflow: hidden'
)) {
    if (-not $text.Style.Contains($token)) {
        throw "Sales-return responsive styling missing token: $token"
    }
}

foreach ($token in @(
    '/sales-returns.css',
    '/sales-returns.js',
    '/sales-returns-navigation.js'
)) {
    if (-not $text.Index.Contains($token)) {
        throw "Sales-return asset is not wired into index.html: $token"
    }
}

foreach ($token in @(
    'Sales returns and refunds',
    'Eligible receipts',
    'Recent credit notes',
    'Accounting mode',
    'sales returns desktop workspace has horizontal overflow'
)) {
    if (-not $text.Browser.Contains($token)) {
        throw "Microsoft Edge journey missing sales-return assertion: $token"
    }
}

foreach ($token in @(
    'schemaVersion -ne 17',
    'return_quantity_exceeds_remaining',
    'Assert-ReturnJournal',
    'restockedBaseUnits',
    'quantityDeltaBaseUnits',
    'grossSalesMinor',
    'returnedSalesMinor',
    'grossCostOfGoodsSoldMinor',
    'cashVarianceMinor',
    'Credit note'
)) {
    if (-not $text.TransactionGate.Contains($token)) {
        throw "Sales-return transaction gate missing assertion token: $token"
    }
}

Write-Host 'Sales-return schema, service, accounting, reporting, cash audit, UI, routing and transaction assertions passed.'
