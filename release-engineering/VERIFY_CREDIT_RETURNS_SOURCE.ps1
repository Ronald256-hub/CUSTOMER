$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$required = @{
    Migration = Join-Path $root 'src/Robo.Pos.Server/Data/Migrations/018_credit_sales_returns_customer_credits.sql'
    MigrationRunner = Join-Path $root 'src/Robo.Pos.Server/Data/CreditSalesReturnsMigration.cs'
    Bootstrap = Join-Path $root 'src/Robo.Pos.Server/Data/DatabaseBootstrap.cs'
    Models = Join-Path $root 'src/Robo.Pos.Server/Sales/CreditSalesReturnModels.cs'
    Service = Join-Path $root 'src/Robo.Pos.Server/Sales/CreditSalesReturnService.cs'
    Endpoints = Join-Path $root 'src/Robo.Pos.Server/Sales/CreditSalesReturnEndpoints.cs'
    Reporting = Join-Path $root 'src/Robo.Pos.Server/Sales/ShopSalesReportingService.cs'
    Program = Join-Path $root 'src/Robo.Pos.Server/Program.cs'
    Workspace = Join-Path $root 'src/Robo.Pos.Server/wwwroot/credit-returns.js'
    Navigation = Join-Path $root 'src/Robo.Pos.Server/wwwroot/credit-returns-navigation.js'
    Style = Join-Path $root 'src/Robo.Pos.Server/wwwroot/credit-returns.css'
    Index = Join-Path $root 'src/Robo.Pos.Server/wwwroot/index.html'
    Browser = Join-Path $root 'release-engineering/VERIFY_OPERATOR_EXPERIENCE_BROWSER.mjs'
    TransactionGate = Join-Path $root 'release-engineering/VERIFY_CREDIT_RETURNS.ps1'
    SalesReturnGate = Join-Path $root 'release-engineering/VERIFY_SALES_RETURNS.ps1'
}

foreach ($entry in $required.GetEnumerator()) {
    if (-not (Test-Path $entry.Value -PathType Leaf)) {
        throw "Required credit-control asset missing: $($entry.Key) -> $($entry.Value)"
    }
}

foreach ($path in @($required.Workspace, $required.Navigation, $required.Browser)) {
    node --check $path
    if ($LASTEXITCODE -ne 0) { throw "JavaScript parsing failed: $path" }
}

foreach ($path in @($required.TransactionGate, $required.SalesReturnGate)) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $path,
        [ref]$tokens,
        [ref]$errors
    ) | Out-Null
    if ($errors.Count -gt 0) {
        throw (($errors | ForEach-Object { "$path`:$($_.Extent.StartLineNumber): $($_.Message)" }) -join "`n")
    }
}

$text = @{}
foreach ($entry in $required.GetEnumerator()) {
    $text[$entry.Key] = Get-Content $entry.Value -Raw
}

foreach ($token in @(
    "'customer_credits'",
    "'credit_note', 'customer_credit'",
    'ALTER TABLE finance_customer_receipt_allocations',
    'ALTER TABLE finance_customer_receipts',
    'finance_credit_returns',
    'finance_credit_return_items',
    'finance_customer_credits',
    'finance_customer_credit_applications',
    'finance_credit_return_documents',
    'finance_customer_credit_balances',
    'system credit settlements are immutable',
    'credit return item exceeds the remaining sold quantity',
    'completed credit returns are immutable',
    'customer credit source records are immutable',
    'customer credit applications are immutable',
    'credit_sale_return',
    "18,",
    'Credit-sale returns, receivable adjustments and customer credit applications'
)) {
    if (-not $text.Migration.Contains($token)) {
        throw "Schema 18 migration missing invariant token: $token"
    }
}

foreach ($token in @(
    'public const int Version = 18',
    '018_credit_sales_returns_customer_credits.sql'
)) {
    if (-not $text.MigrationRunner.Contains($token)) {
        throw "Schema 18 migration runner missing token: $token"
    }
}
if (-not $text.Bootstrap.Contains('CreditSalesReturnsMigration.ApplyAsync')) {
    throw 'Database bootstrap does not apply schema 18.'
}

foreach ($token in @(
    'ListEligibleCreditSalesAsync',
    'GetReturnableCreditSaleAsync',
    'CreateCreditReturnAsync',
    'ApplyCustomerCreditAsync',
    'PostReceivableReductionAsync',
    'PostCreditAndStockJournalAsync',
    'InsertCustomerCreditAsync',
    'credit_return_quantity_exceeds_remaining',
    'customer_credit_insufficient',
    'credit_application_exceeds_receivable',
    'customer_receipt:',
    'credit_sale_return:',
    'customer_credits',
    'sale_return',
    'credit_sale.return.completed',
    'finance.customer_credit.applied'
)) {
    if (-not $text.Service.Contains($token)) {
        throw "Credit-return service missing token: $token"
    }
}

foreach ($token in @(
    '/api/v3/finance/credit-returns/eligible',
    '/api/v3/finance/credit-returns/sales/{saleId}',
    '/api/v3/finance/credit-returns/{returnId}',
    '/api/v3/finance/customer-credits',
    '/api/v3/finance/customer-credit-applications',
    'RequireAdminAsync'
)) {
    if (-not $text.Endpoints.Contains($token)) {
        throw "Credit-control endpoints missing token: $token"
    }
}

foreach ($token in @(
    'finance_credit_returns AS header',
    "'credit' AS payment_method",
    'return_amount_minor',
    'checked(grossSales - returnedSales)',
    'checked(grossCost - restockedCost)'
)) {
    if (-not $text.Reporting.Contains($token)) {
        throw "Combined return reporting missing token: $token"
    }
}

$versionMatch = [regex]::Match($text.Program, 'version = "([^"]+)"')
if (-not $versionMatch.Success -or [version]$versionMatch.Groups[1].Value -lt [version]'6.8.0') {
    throw 'Nexus service version is older than the 6.8 credit-control baseline.'
}
foreach ($token in @(
    'credit-sale-return-receivable-adjustments',
    'overpaid-invoice-customer-credits',
    'customer-credit-liability-ledger',
    'customer-credit-applications',
    'non-cash-credit-note-settlements',
    'immutable-credit-return-register',
    'AddSingleton<CreditSalesReturnService>',
    'MapCreditSalesReturnEndpoints'
)) {
    if (-not $text.Program.Contains($token)) {
        throw "Credit-control registration missing token: $token"
    }
}

foreach ($token in @(
    'Credit returns and customer credits',
    'Eligible credit invoices',
    'Customer credit balances',
    'Apply customer credit',
    'Receivable reduction',
    'New customer credit',
    '/api/v3/finance/credit-returns/eligible',
    '/api/v3/finance/customer-credit-applications'
)) {
    if (-not $text.Workspace.Contains($token)) {
        throw "Credit-control workspace missing token: $token"
    }
}
foreach ($method in @('method: "PUT"', 'method: "DELETE"', 'method: "PATCH"')) {
    if ($text.Workspace.Contains($method)) {
        throw "Credit-control workspace contains unsupported mutation method: $method"
    }
}

foreach ($token in @(
    'installCreditReturnsRoute',
    'history.replaceState',
    'stopImmediatePropagation',
    'window.addEventListener("hashchange"',
    'NexusCreditReturns',
    'credit-returns'
)) {
    if (-not $text.Navigation.Contains($token)) {
        throw "Credit-control route bridge missing token: $token"
    }
}
if ($text.Navigation.Contains('HashChangeEvent')) {
    throw 'Credit-control navigation must not synthesize hashchange events.'
}

foreach ($token in @(
    '.credit-returns-workspace',
    '.cr-layout',
    '.cr-credit-grid',
    '.cr-line',
    '@media(max-width:560px)',
    '@media print',
    'prefers-reduced-motion',
    'min-width:0',
    'overflow:hidden'
)) {
    if (-not $text.Style.Replace(' ', '').Contains($token.Replace(' ', ''))) {
        throw "Credit-control responsive styling missing token: $token"
    }
}

foreach ($token in @(
    '/credit-returns.css',
    '/credit-returns.js',
    '/credit-returns-navigation.js'
)) {
    if (-not $text.Index.Contains($token)) {
        throw "Credit-control asset is not wired into index.html: $token"
    }
}

foreach ($token in @(
    'schemaVersion -lt 18',
    'receivableReductionMinor',
    'customerCreditMinor',
    'credit_return_quantity_exceeds_remaining',
    'customer_credit_insufficient',
    'credit_application_exceeds_receivable',
    'quantityDeltaBaseUnits',
    'paymentMethod -eq "credit"',
    'Credit note'
)) {
    if (-not $text.TransactionGate.Contains($token)) {
        throw "Credit-control transaction gate missing token: $token"
    }
}

if (-not $text.SalesReturnGate.Contains('schemaVersion -lt 17')) {
    throw 'The established sales-return regression is not forward compatible with schema 18 and later.'
}

Write-Host 'Credit-sale returns, receivable adjustments, customer credits, applications, UI and forward-compatibility assertions passed.'
