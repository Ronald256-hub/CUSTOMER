param([string]$PortableZip = "")

$ErrorActionPreference = "Stop"

function Invoke-Api {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [object]$Body,
        [int]$ExpectedStatusCode = 0
    )

    $parameters = @{
        Method = $Method
        Uri = $Uri
        UseBasicParsing = $true
        TimeoutSec = 25
        ErrorAction = "Stop"
        SkipHttpErrorCheck = $true
    }
    if ($Session) { $parameters.WebSession = $Session }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 24 -Compress
    }

    $response = Invoke-WebRequest @parameters
    $statusCode = [int]$response.StatusCode
    $content = [string]$response.Content
    if ($ExpectedStatusCode -gt 0) {
        if ($statusCode -ne $ExpectedStatusCode) {
            throw "Expected HTTP $ExpectedStatusCode but received $statusCode. Body: $content"
        }
    }
    elseif ($statusCode -ge 400) {
        throw "HTTP $statusCode from $Method $Uri. Body: $content"
    }

    $data = if ([string]::IsNullOrWhiteSpace($content)) {
        $null
    }
    elseif ($response.Headers.'Content-Type' -like 'application/json*') {
        $content | ConvertFrom-Json
    }
    else {
        $content
    }

    return [pscustomobject]@{
        StatusCode = $statusCode
        Data = $data
        Content = $content
    }
}

function Invoke-Json {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [object]$Body
    )
    return (Invoke-Api -Method $Method -Uri $Uri -Session $Session -Body $Body).Data
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Get-JournalByNumber {
    param(
        [string]$BaseUri,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [string]$JournalNumber
    )
    $listing = Invoke-Json -Method GET -Uri "$BaseUri/api/v3/accounting/journals?scope=shop&limit=300" -Session $Session
    $matches = @($listing.journals | Where-Object { $_.journalNumber -eq $JournalNumber })
    if ($matches.Count -ne 1) {
        throw "Expected one journal $JournalNumber but found $($matches.Count)."
    }
    return Invoke-Json -Method GET -Uri "$BaseUri/api/v3/accounting/journals/$($matches[0].id)" -Session $Session
}

if ([string]::IsNullOrWhiteSpace($PortableZip)) {
    $zip = Get-ChildItem (Join-Path $PSScriptRoot "..\release") `
        -Filter "Nexus_POS_*_Portable.zip" -File |
        Select-Object -First 1
    if (-not $zip) { throw "The portable Nexus POS release ZIP was not found." }
    $PortableZip = $zip.FullName
}

$PortableZip = [System.IO.Path]::GetFullPath($PortableZip)
if (-not (Test-Path -LiteralPath $PortableZip -PathType Leaf)) {
    throw "Portable release ZIP does not exist: $PortableZip"
}

$temporaryRoot = Join-Path $env:TEMP ("nexus-finance-settlements-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Finance2026#Initial"
$privatePassword = "Nexus!Finance2026#Private"
$instanceId = [guid]::NewGuid().ToString("N")
$serverProcess = $null
$environmentNames = @(
    "NEXUS_DATA_DIR", "ROBO_DATA_DIR", "NEXUS_DOCUMENT_ROOT", "ROBO_DOCUMENT_ROOT",
    "NEXUS_ADMIN_USERNAME", "NEXUS_ADMIN_DISPLAY_NAME", "NEXUS_ADMIN_INITIAL_PASSWORD",
    "ROBO_ADMIN_INITIAL_PASSWORD", "NEXUS_INSTANCE_ID", "ASPNETCORE_ENVIRONMENT", "AllowedHosts"
)
$previousEnvironment = @{}

try {
    New-Item -ItemType Directory -Force -Path $runtimeRoot, $dataRoot, $documentRoot | Out-Null
    Expand-Archive -LiteralPath $PortableZip -DestinationPath $runtimeRoot -Force
    $serverExe = Get-ChildItem $runtimeRoot -Recurse -Filter "Robo.Pos.Server.exe" -File | Select-Object -First 1
    if (-not $serverExe) { throw "Robo.Pos.Server.exe was not found in the portable package." }
    $serverDirectory = $serverExe.Directory.FullName

    foreach ($name in $environmentNames) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }
    [Environment]::SetEnvironmentVariable("NEXUS_DATA_DIR", $dataRoot, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_DATA_DIR", $dataRoot, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_DOCUMENT_ROOT", $documentRoot, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_DOCUMENT_ROOT", $documentRoot, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_USERNAME", "admin", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Finance Settlement Gate Administrator", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_INITIAL_PASSWORD", $initialPassword, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_ADMIN_INITIAL_PASSWORD", $initialPassword, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_INSTANCE_ID", $instanceId, "Process")
    [Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Process")
    [Environment]::SetEnvironmentVariable("AllowedHosts", "localhost;127.0.0.1;[::1]", "Process")

    $port = Get-FreePort
    $baseUri = "http://127.0.0.1:$port"
    $serverProcess = Start-Process -FilePath $serverExe.FullName `
        -ArgumentList "--urls `"$baseUri`"" -WorkingDirectory $serverDirectory `
        -WindowStyle Hidden -RedirectStandardOutput $outputLog -RedirectStandardError $errorLog -PassThru

    $health = $null
    for ($attempt = 0; $attempt -lt 360; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($serverProcess.HasExited) { throw "The server exited with code $($serverProcess.ExitCode)." }
        try {
            $health = Invoke-Json -Method GET -Uri "$baseUri/api/v3/health"
            if ($health.ok -and $health.instanceId -eq $instanceId) { break }
        }
        catch { }
    }
    $minimumFinanceVersion = [version]"5.6.0"
    $runningVersion = [version]$health.version
    if (-not $health -or -not $health.ok -or $health.schemaVersion -lt 12 -or $runningVersion -lt $minimumFinanceVersion) {
        throw "Nexus did not start with version 5.6.0 or later and finance schema version 12 or later."
    }

    $service = Invoke-Json -Method GET -Uri "$baseUri/api/v3/service"
    foreach ($capability in @(
        "customer-credit-accounts",
        "receivables-and-payables-open-items",
        "atomic-customer-receipt-posting",
        "atomic-supplier-payment-posting",
        "audited-settlement-reversals",
        "customer-and-supplier-statements",
        "receivables-and-payables-ageing",
        "ledger-derived-cashbook"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing finance capability: $capability"
        }
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/login" -Session $session -Body @{
        username = "admin"; password = $initialPassword
    }
    if (-not $login.user.mustChangePassword) { throw "Initial password replacement was not required." }
    $changed = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/change-password" -Session $session -Body @{
        currentPassword = $initialPassword; newPassword = $privatePassword
    }
    if (-not $changed.changed) { throw "Administrator password replacement failed." }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/login" -Session $session -Body @{
        username = "admin"; password = $privatePassword
    }
    if ($login.user.role -ne "admin") { throw "Administrator login failed." }

    $context = Invoke-Json -Method GET -Uri "$baseUri/api/v3/session/shop-context" -Session $session
    if ($context.shopCode -ne "MAIN") { throw "The finance gate did not start in MAIN." }

    $customer = Invoke-Json -Method POST -Uri "$baseUri/api/v3/finance/customers" -Session $session -Body @{
        name = "Finance Gate Customer"
        phone = "+256700000001"
        email = "customer@example.invalid"
        address = "Finance Gate Address"
        taxNumber = "FIN-CUS-TIN"
        creditLimitMinor = 10000
        paymentTermsDays = 30
    }
    if ($customer.customerNumber -notlike "CUS-*") { throw "Customer account creation failed." }

    $category = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/categories" -Session $session -Body @{
        name = "Finance Settlement Gate"; description = "Receivables and payables validation"; displayOrder = 1
    }
    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session -Body @{
        categoryId = $category.id
        sku = "FIN-GATE-001"
        barcode = "991000000001"
        name = "Finance Settlement Test Product"
        description = "Used by the automated settlement gate"
        productType = "standard"
        stockUnit = "unit"
        saleUnit = "unit"
        bottleVolumeMl = $null
        glassSizeMl = $null
        unitsPerCrate = $null
        costPriceMinor = 400
        sellingPriceMinor = 1000
        lowStockThreshold = 2
        openingStockBaseUnits = 20
        allowNegativeStock = $false
        trackExpiry = $false
    }
    $shift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/open" -Session $session -Body @{
        openingCashMinor = 5000
    }
    if ($shift.status -ne "open") { throw "The finance gate shift did not open." }

    $missingCustomer = Invoke-Api -Method POST -Uri "$baseUri/api/v3/sales" -Session $session -ExpectedStatusCode 400 -Body @{
        items = @(@{ productId = $product.id; quantity = 1 })
        paymentMethod = "credit"
        amountReceivedMinor = 1000
        issueInvoice = $true
        customerName = "Missing Customer"
    }
    if (($missingCustomer.Data.error ?? "") -ne "credit_customer_required") {
        throw "A credit sale without a customer account was not rejected."
    }

    $today = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    $sale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales" -Session $session -Body @{
        items = @(@{ productId = $product.id; quantity = 2 })
        paymentMethod = "credit"
        amountReceivedMinor = 2000
        issueInvoice = $true
        customerId = $customer.id
        customerName = $customer.name
        customerPhone = $customer.phone
        customerAddress = $customer.address
        customerTaxNumber = $customer.taxNumber
        notes = "Finance gate credit sale"
    }
    if ($sale.totalMinor -ne 2000 -or $sale.paymentMethod -ne "credit") {
        throw "The customer credit sale did not complete correctly."
    }

    $receivableResponse = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/receivables?customerId=$($customer.id)&status=open" -Session $session
    if ($receivableResponse.count -ne 1 -or $receivableResponse.outstandingMinor -ne 2000) {
        throw "The credit sale did not create the expected receivable."
    }
    $receivable = $receivableResponse.receivables[0]
    if ($receivable.documentNumber -ne $sale.invoiceNumber -or $receivable.status -ne "open") {
        throw "The receivable document identity is incorrect."
    }

    $saleJournal = Get-JournalByNumber -BaseUri $baseUri -Session $session -JournalNumber ("SYS-" + $sale.receiptNumber)
    $arDebit = @($saleJournal.lines | Where-Object { $_.accountCode -eq "1100" -and $_.debitMinor -eq 2000 })
    if ($saleJournal.status -ne "posted" -or $arDebit.Count -ne 1) {
        throw "The credit sale journal did not debit accounts receivable."
    }

    $overReceipt = Invoke-Api -Method POST -Uri "$baseUri/api/v3/finance/customer-receipts" -Session $session -ExpectedStatusCode 409 -Body @{
        customerId = $customer.id
        receiptDate = $today
        paymentMethod = "cash"
        reference = "OVER-RECEIPT"
        notes = "Must fail"
        allocations = @(@{ itemId = $receivable.id; amountMinor = 2500 })
    }
    if (($overReceipt.Data.error ?? "") -ne "receivable_overallocation") {
        throw "Receivable over-allocation was not rejected."
    }

    $receipt = Invoke-Json -Method POST -Uri "$baseUri/api/v3/finance/customer-receipts" -Session $session -Body @{
        customerId = $customer.id
        receiptDate = $today
        paymentMethod = "cash"
        reference = "CUS-PARTIAL-001"
        notes = "Partial customer settlement"
        allocations = @(@{ itemId = $receivable.id; amountMinor = 750 })
    }
    if ($receipt.status -ne "posted" -or $receipt.amountMinor -ne 750 -or $receipt.allocations.Count -ne 1) {
        throw "The partial customer receipt did not post."
    }
    $receiptJournal = Get-JournalByNumber -BaseUri $baseUri -Session $session -JournalNumber ("SYS-" + $receipt.number)
    if ($receiptJournal.sourceId -ne ("customer_receipt:" + $receipt.id) -or $receiptJournal.totalDebitMinor -ne 750) {
        throw "The customer receipt journal is invalid."
    }

    $partialReceivable = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/receivables?customerId=$($customer.id)&status=partial" -Session $session
    if ($partialReceivable.count -ne 1 -or $partialReceivable.outstandingMinor -ne 1250) {
        throw "The partial customer receipt did not reduce the receivable correctly."
    }
    $customerStatement = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/customers/$($customer.id)/statement?fromDate=$today&toDate=$today" -Session $session
    if ($customerStatement.closingBalanceMinor -ne 1250 -or @($customerStatement.lines | Where-Object { $_.entryType -eq "receipt" }).Count -ne 1) {
        throw "The customer statement is incorrect after partial settlement."
    }
    $receivablesAgeing = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/receivables-ageing?scope=shop&asOfDate=$today" -Session $session
    if ($receivablesAgeing.totalOutstandingMinor -ne 1250) {
        throw "Receivables ageing is incorrect after partial settlement."
    }

    $genericReceiptReversal = Invoke-Api -Method POST -Uri "$baseUri/api/v3/accounting/journals/$($receiptJournal.id)/reverse" -Session $session -ExpectedStatusCode 409 -Body @{
        expectedVersion = $receiptJournal.version
        reversalDate = $today
        reason = "Must use customer receipt workflow"
    }
    if (($genericReceiptReversal.Data.error ?? "") -ne "system_journal_reversal_requires_source_workflow") {
        throw "Generic reversal of a system settlement journal was not blocked."
    }

    $reversedReceipt = Invoke-Json -Method POST -Uri "$baseUri/api/v3/finance/customer-receipts/$($receipt.id)/reverse" -Session $session -Body @{
        reversalDate = $today
        reason = "Finance gate customer receipt reversal"
    }
    if ($reversedReceipt.status -ne "reversed" -or -not $reversedReceipt.reversalJournalId) {
        throw "The customer receipt reversal failed."
    }
    $restoredReceivable = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/receivables?customerId=$($customer.id)&status=open" -Session $session
    if ($restoredReceivable.outstandingMinor -ne 2000) {
        throw "Reversing the receipt did not restore the receivable."
    }
    $customerStatementAfterReversal = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/customers/$($customer.id)/statement?fromDate=$today&toDate=$today" -Session $session
    if ($customerStatementAfterReversal.closingBalanceMinor -ne 2000 -or @($customerStatementAfterReversal.lines | Where-Object { $_.entryType -eq "receipt_reversal" }).Count -ne 1) {
        throw "The customer statement did not retain the receipt reversal."
    }

    $supplier = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/suppliers" -Session $session -Body @{
        name = "Finance Gate Supplier"
        phone = "+256700000002"
        email = "supplier@example.invalid"
        address = "Supplier Gate Address"
        notes = "Finance settlement gate"
    }
    $purchase = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/purchases" -Session $session -Body @{
        supplierId = $supplier.id
        supplierInvoiceNumber = "FIN-SUP-001"
        notes = "Finance payable gate"
        items = @(@{
            productId = $product.id
            quantityBaseUnits = 3
            unitCostMinor = 500
            batchNumber = "FIN-BATCH-001"
            expiryDate = $null
        })
    }
    if ($purchase.totalMinor -ne 1500) { throw "The supplier purchase total is incorrect." }

    $payableResponse = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/payables?supplierId=$($supplier.id)&status=open" -Session $session
    if ($payableResponse.count -ne 1 -or $payableResponse.outstandingMinor -ne 1500) {
        throw "The purchase did not create the expected payable."
    }
    $payable = $payableResponse.payables[0]

    $overPayment = Invoke-Api -Method POST -Uri "$baseUri/api/v3/finance/supplier-payments" -Session $session -ExpectedStatusCode 409 -Body @{
        supplierId = $supplier.id
        paymentDate = $today
        paymentMethod = "cash"
        reference = "OVER-PAYMENT"
        notes = "Must fail"
        allocations = @(@{ itemId = $payable.id; amountMinor = 1600 })
    }
    if (($overPayment.Data.error ?? "") -ne "payable_overallocation") {
        throw "Payable over-allocation was not rejected."
    }

    $supplierPayment = Invoke-Json -Method POST -Uri "$baseUri/api/v3/finance/supplier-payments" -Session $session -Body @{
        supplierId = $supplier.id
        paymentDate = $today
        paymentMethod = "cash"
        reference = "SUP-PARTIAL-001"
        notes = "Partial supplier settlement"
        allocations = @(@{ itemId = $payable.id; amountMinor = 600 })
    }
    if ($supplierPayment.status -ne "posted" -or $supplierPayment.amountMinor -ne 600) {
        throw "The partial supplier payment did not post."
    }
    $supplierPaymentJournal = Get-JournalByNumber -BaseUri $baseUri -Session $session -JournalNumber ("SYS-" + $supplierPayment.number)
    if ($supplierPaymentJournal.sourceId -ne ("supplier_payment:" + $supplierPayment.id) -or $supplierPaymentJournal.totalDebitMinor -ne 600) {
        throw "The supplier payment journal is invalid."
    }

    $partialPayable = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/payables?supplierId=$($supplier.id)&status=partial" -Session $session
    if ($partialPayable.count -ne 1 -or $partialPayable.outstandingMinor -ne 900) {
        throw "The partial supplier payment did not reduce the payable correctly."
    }
    $supplierStatement = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/suppliers/$($supplier.id)/statement?fromDate=$today&toDate=$today" -Session $session
    if ($supplierStatement.closingBalanceMinor -ne 900 -or @($supplierStatement.lines | Where-Object { $_.entryType -eq "payment" }).Count -ne 1) {
        throw "The supplier statement is incorrect after partial payment."
    }
    $payablesAgeing = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/payables-ageing?scope=shop&asOfDate=$today" -Session $session
    if ($payablesAgeing.totalOutstandingMinor -ne 900) {
        throw "Payables ageing is incorrect after partial payment."
    }

    $genericPaymentReversal = Invoke-Api -Method POST -Uri "$baseUri/api/v3/accounting/journals/$($supplierPaymentJournal.id)/reverse" -Session $session -ExpectedStatusCode 409 -Body @{
        expectedVersion = $supplierPaymentJournal.version
        reversalDate = $today
        reason = "Must use supplier payment workflow"
    }
    if (($genericPaymentReversal.Data.error ?? "") -ne "system_journal_reversal_requires_source_workflow") {
        throw "Generic reversal of a system supplier payment journal was not blocked."
    }

    $reversedPayment = Invoke-Json -Method POST -Uri "$baseUri/api/v3/finance/supplier-payments/$($supplierPayment.id)/reverse" -Session $session -Body @{
        reversalDate = $today
        reason = "Finance gate supplier payment reversal"
    }
    if ($reversedPayment.status -ne "reversed" -or -not $reversedPayment.reversalJournalId) {
        throw "The supplier payment reversal failed."
    }
    $restoredPayable = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/payables?supplierId=$($supplier.id)&status=open" -Session $session
    if ($restoredPayable.outstandingMinor -ne 1500) {
        throw "Reversing the supplier payment did not restore the payable."
    }

    $cashbook = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/cashbook?scope=shop&fromDate=$today&toDate=$today&accountSystemKey=cash_on_hand&limit=100" -Session $session
    $settlementCashLines = @($cashbook.entries | Where-Object {
        $_.sourceId -in @(
            ("customer_receipt:" + $receipt.id),
            ("supplier_payment:" + $supplierPayment.id),
            $receiptJournal.id,
            $supplierPaymentJournal.id
        )
    })
    if ($settlementCashLines.Count -lt 4 -or ($settlementCashLines | Measure-Object -Property signedAmountMinor -Sum).Sum -ne 0) {
        throw "The cashbook does not contain balanced settlement and reversal movements."
    }

    $trialBalance = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/trial-balance?scope=shop&fromDate=$today&toDate=$today" -Session $session
    if ($trialBalance.totalDebitMovementMinor -ne $trialBalance.totalCreditMovementMinor -or $trialBalance.totalDebitBalanceMinor -ne $trialBalance.totalCreditBalanceMinor) {
        throw "The finance settlement trial balance is not balanced."
    }
    $accountsReceivable = @($trialBalance.lines | Where-Object { $_.accountCode -eq "1100" })[0]
    $accountsPayable = @($trialBalance.lines | Where-Object { $_.accountCode -eq "2000" })[0]
    if (-not $accountsReceivable -or $accountsReceivable.debitBalanceMinor -ne 2000) {
        throw "The accounts receivable balance is incorrect after receipt reversal."
    }
    if (-not $accountsPayable -or $accountsPayable.creditBalanceMinor -ne 1500) {
        throw "The accounts payable balance is incorrect after payment reversal."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $session -Body @{}
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 12) {
        throw "Backup integrity or schema-version-12 verification failed."
    }

    Write-Host "Nexus POS receivables, payables and cashbook gate: PASS"
    Write-Host "Validated customer credit, AR/AP open items, partial settlements, over-allocation controls, statements, ageing, cashbook, exact reversals, trial balance and backup integrity."
}
catch {
    Write-Host "Nexus POS receivables, payables and cashbook gate: FAIL - $($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path $outputLog) {
        Write-Host "--- server-output.log ---"
        Get-Content $outputLog -Tail 450 -ErrorAction SilentlyContinue
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server-error.log ---"
        Get-Content $errorLog -Tail 450 -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        $serverProcess.WaitForExit(5000) | Out-Null
    }
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }
    Remove-Item -Path $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
