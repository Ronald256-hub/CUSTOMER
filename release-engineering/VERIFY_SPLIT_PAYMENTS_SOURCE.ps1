$ErrorActionPreference = "Stop"

function Assert-Contains {
    param([string]$Path, [string]$Pattern, [string]$Message)
    $content = Get-Content -LiteralPath $Path -Raw
    if ($content -notmatch $Pattern) { throw $Message }
}

$root = Split-Path $PSScriptRoot -Parent
$models = Join-Path $root 'src/Robo.Pos.Server/Sales/SalesModels.cs'
$service = Join-Path $root 'src/Robo.Pos.Server/Sales/ShopSaleCompletionService.cs'
$receipts = Join-Path $root 'src/Robo.Pos.Server/Sales/ShopReceiptService.cs'
$returns = Join-Path $root 'src/Robo.Pos.Server/Sales/SalesReturnService.cs'
$program = Join-Path $root 'src/Robo.Pos.Server/Program.cs'
$writer = Join-Path $root 'src/Robo.Pos.Server/Sales/AuditDocumentWriter.cs'
$index = Join-Path $root 'src/Robo.Pos.Server/wwwroot/index.html'
$javascript = Join-Path $root 'src/Robo.Pos.Server/wwwroot/split-payments.js'

Assert-Contains $models 'IReadOnlyList<SalePaymentRequest>\? Payments' 'CompleteSaleRequest has no split payment collection.'
Assert-Contains $service 'NormalizePayments\(request, total\)' 'Sale completion does not normalize split payments.'
Assert-Contains $service 'payment_plan_not_balanced' 'The exact payment reconciliation invariant is missing.'
Assert-Contains $service 'non_cash_overpayment' 'Non-cash overpayment protection is missing.'
Assert-Contains $service 'mixed_credit_payment_not_supported' 'Mixed credit tender protection is missing.'
Assert-Contains $service 'foreach \(NormalizedPayment payment in paymentPlan\.AppliedPayments\)' 'Payment rows are not inserted atomically.'
Assert-Contains $receipts 'ReadPaymentsAsync' 'Receipt payment breakdown is missing.'
Assert-Contains $receipts 'payments\.Count > 1 \? "split" : header\.PaymentMethod' 'Receipt details do not label multi-tender sales as split.'
Assert-Contains $returns 'split_sale_return_requires_allocation' 'Legacy return workflow is not protected from split tenders.'
Assert-Contains $writer 'Payment breakdown' 'Immutable receipt documents do not show payment breakdown.'
Assert-Contains $writer 'class=\\"customer\\"' 'Receipt payment breakdown HTML quoting is invalid.'
Assert-Contains $program 'version = "7\.0\.0"' 'The service version is not Nexus POS 7.0.0.'
Assert-Contains $program 'split-and-partial-payments' 'Split-payment capability is missing.'
Assert-Contains $index '/split-payments\.js' 'The split payment checkout is not loaded.'
Assert-Contains $javascript 'payments,' 'The checkout does not send the payment collection.'

$operationalMigration = Get-Content (Join-Path $root 'src/Robo.Pos.Server/Data/Migrations/011_operational_accounting_integration.sql') -Raw
if ($operationalMigration -notmatch 'SUM\(amount_minor\).*sale_payments' -and
    $operationalMigration -notmatch 'SUM\(payment\.amount_minor\)') {
    throw 'The cumulative payment posting trigger is missing.'
}
if ($operationalMigration -notmatch 'sale payments exceed the sale total') {
    throw 'The database overposting guard is missing.'
}

Write-Host 'Nexus POS 7.0 split-payment source and invariant gate passed.'
