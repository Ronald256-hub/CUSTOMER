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
        TimeoutSec = 30
        ErrorAction = "Stop"
        SkipHttpErrorCheck = $true
    }
    if ($Session) { $parameters.WebSession = $Session }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 40 -Compress
    }
    $response = Invoke-WebRequest @parameters
    $statusCode = [int]$response.StatusCode
    $content = [string]$response.Content
    if ($ExpectedStatusCode -gt 0 -and $statusCode -ne $ExpectedStatusCode) {
        throw "Expected HTTP $ExpectedStatusCode but received $statusCode from $Method $Uri. Body: $content"
    }
    if ($ExpectedStatusCode -eq 0 -and $statusCode -ge 400) {
        throw "HTTP $statusCode from $Method $Uri. Body: $content"
    }
    $data = if ([string]::IsNullOrWhiteSpace($content)) {
        $null
    }
    elseif ($response.Headers.'Content-Type' -like 'application/json*') {
        $content | ConvertFrom-Json
    }
    else { $content }
    [pscustomobject]@{ StatusCode = $statusCode; Data = $data; Content = $content }
}

function Invoke-Json {
    param([string]$Method, [string]$Uri, $Session, $Body)
    (Invoke-Api -Method $Method -Uri $Uri -Session $Session -Body $Body).Data
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Get-JournalByNumber {
    param([string]$BaseUri, $Session, [string]$JournalNumber)
    $listing = Invoke-Json GET "$BaseUri/api/v3/accounting/journals?scope=shop&limit=300" $Session
    $matches = @($listing.journals | Where-Object journalNumber -eq $JournalNumber)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one journal $JournalNumber but found $($matches.Count)."
    }
    Invoke-Json GET "$BaseUri/api/v3/accounting/journals/$($matches[0].id)" $Session
}

function Get-ReceivableForSale {
    param([string]$BaseUri, $Session, [string]$CustomerId, [string]$SaleId)
    $listing = Invoke-Json GET "$BaseUri/api/v3/finance/receivables?customerId=$CustomerId&limit=200" $Session
    $matches = @($listing.receivables | Where-Object saleId -eq $SaleId)
    if ($matches.Count -ne 1) {
        throw "Expected one receivable for sale $SaleId but found $($matches.Count)."
    }
    $matches[0]
}

function Post-CustomerReceipt {
    param(
        [string]$BaseUri,
        $Session,
        [string]$CustomerId,
        [string]$ReceivableId,
        [long]$Amount,
        [string]$Date,
        [string]$Reference
    )
    Invoke-Json POST "$BaseUri/api/v3/finance/customer-receipts" $Session @{
        customerId = $CustomerId
        receiptDate = $Date
        paymentMethod = "bank"
        reference = $Reference
        notes = "Credit return automated gate settlement"
        allocations = @(@{ itemId = $ReceivableId; amountMinor = $Amount })
    }
}

if ([string]::IsNullOrWhiteSpace($PortableZip)) {
    $zip = Get-ChildItem (Join-Path $PSScriptRoot "..\release") -Filter "Nexus_POS_*_Portable.zip" -File | Select-Object -First 1
    if (-not $zip) { throw "The portable Nexus POS release ZIP was not found." }
    $PortableZip = $zip.FullName
}
$PortableZip = [System.IO.Path]::GetFullPath($PortableZip)
if (-not (Test-Path $PortableZip -PathType Leaf)) {
    throw "Portable release ZIP does not exist: $PortableZip"
}

$temporaryRoot = Join-Path $env:TEMP ("nexus-credit-returns-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!CreditReturns2026#Initial"
$privatePassword = "Nexus!CreditReturns2026#Private"
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
    Expand-Archive $PortableZip $runtimeRoot -Force
    $serverExe = Get-ChildItem $runtimeRoot -Recurse -Filter "Robo.Pos.Server.exe" -File | Select-Object -First 1
    if (-not $serverExe) { throw "Robo.Pos.Server.exe was not found in the portable package." }

    foreach ($name in $environmentNames) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }
    $environment = @{
        NEXUS_DATA_DIR = $dataRoot; ROBO_DATA_DIR = $dataRoot
        NEXUS_DOCUMENT_ROOT = $documentRoot; ROBO_DOCUMENT_ROOT = $documentRoot
        NEXUS_ADMIN_USERNAME = "admin"; NEXUS_ADMIN_DISPLAY_NAME = "Credit Returns Gate Administrator"
        NEXUS_ADMIN_INITIAL_PASSWORD = $initialPassword; ROBO_ADMIN_INITIAL_PASSWORD = $initialPassword
        NEXUS_INSTANCE_ID = $instanceId; ASPNETCORE_ENVIRONMENT = "Production"
        AllowedHosts = "localhost;127.0.0.1;[::1]"
    }
    foreach ($entry in $environment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }

    $port = Get-FreePort
    $baseUri = "http://127.0.0.1:$port"
    $serverProcess = Start-Process $serverExe.FullName -ArgumentList "--urls `"$baseUri`"" `
        -WorkingDirectory $serverExe.Directory.FullName -WindowStyle Hidden `
        -RedirectStandardOutput $outputLog -RedirectStandardError $errorLog -PassThru

    $health = $null
    for ($attempt = 0; $attempt -lt 360; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($serverProcess.HasExited) { throw "The server exited with code $($serverProcess.ExitCode)." }
        try {
            $health = Invoke-Json GET "$baseUri/api/v3/health"
            if ($health.ok -and $health.instanceId -eq $instanceId) { break }
        } catch { }
    }
    if (-not $health -or $health.schemaVersion -ne 18) {
        throw "Nexus did not start with exact credit-control schema version 18."
    }

    $service = Invoke-Json GET "$baseUri/api/v3/service"
    if ($service.version -ne "6.8.0") { throw "The service version is not 6.8.0." }
    foreach ($capability in @(
        "credit-sale-return-receivable-adjustments",
        "overpaid-invoice-customer-credits",
        "customer-credit-liability-ledger",
        "customer-credit-applications",
        "non-cash-credit-note-settlements",
        "immutable-credit-return-register"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing credit-control capability: $capability"
        }
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json POST "$baseUri/api/v3/auth/login" $session @{
        username = "admin"; password = $initialPassword
    }
    if (-not $login.user.mustChangePassword) { throw "Initial password replacement was not required." }
    $changed = Invoke-Json POST "$baseUri/api/v3/auth/change-password" $session @{
        currentPassword = $initialPassword; newPassword = $privatePassword
    }
    if (-not $changed.changed) { throw "Administrator password replacement failed." }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json POST "$baseUri/api/v3/auth/login" $session @{
        username = "admin"; password = $privatePassword
    }
    if ($login.user.role -ne "admin") { throw "Administrator login failed." }

    $context = Invoke-Json GET "$baseUri/api/v3/session/shop-context" $session
    if ($context.shopCode -ne "MAIN") { throw "The credit-control gate did not start in MAIN." }

    $category = Invoke-Json POST "$baseUri/api/v3/admin/inventory/categories" $session @{
        name = "Credit Returns Gate"; description = "Automated credit account controls"; displayOrder = 1
    }
    $product = Invoke-Json POST "$baseUri/api/v3/admin/inventory/products" $session @{
        categoryId = $category.id
        sku = "CREDIT-RETURN-GATE-001"
        barcode = "996000000001"
        name = "Credit Return Gate Product"
        description = "Used by the automated credit-return gate"
        productType = "standard"
        stockUnit = "unit"
        saleUnit = "unit"
        bottleVolumeMl = $null
        glassSizeMl = $null
        unitsPerCrate = $null
        costPriceMinor = 400
        sellingPriceMinor = 1000
        lowStockThreshold = 2
        openingStockBaseUnits = 30
        allowNegativeStock = $false
        trackExpiry = $false
    }

    $customer = Invoke-Json POST "$baseUri/api/v3/finance/customers" $session @{
        name = "Credit Returns Gate Customer"
        phone = "0700000000"
        email = "credit-gate@example.invalid"
        address = "Kampala"
        taxNumber = ""
        creditLimitMinor = 100000
        paymentTermsDays = 30
    }

    $shift = Invoke-Json POST "$baseUri/api/v3/shifts/open" $session @{ openingCashMinor = 5000 }
    if ($shift.status -ne "open") { throw "The credit-control shift did not open." }
    $today = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")

    $saleA = Invoke-Json POST "$baseUri/api/v3/sales" $session @{
        items = @(@{ productId = $product.id; quantity = 3 })
        paymentMethod = "credit"
        amountReceivedMinor = 3000
        issueInvoice = $true
        customerId = $customer.id
        customerName = $customer.name
        notes = "Credit return source invoice"
    }
    if ($saleA.totalMinor -ne 3000 -or $saleA.paymentMethod -ne "credit") {
        throw "The source credit sale is incorrect."
    }
    $receivableA = Get-ReceivableForSale $baseUri $session $customer.id $saleA.saleId
    if ($receivableA.originalAmountMinor -ne 3000 -or $receivableA.outstandingAmountMinor -ne 3000) {
        throw "The source receivable is incorrect."
    }

    $receiptA = Post-CustomerReceipt $baseUri $session $customer.id $receivableA.id 2500 $today "CR-GATE-PARTIAL-A"
    if ($receiptA.amountMinor -ne 2500 -or $receiptA.status -ne "posted") {
        throw "The partial customer receipt was not posted."
    }
    $receivableA = Get-ReceivableForSale $baseUri $session $customer.id $saleA.saleId
    if ($receivableA.outstandingAmountMinor -ne 500) {
        throw "The source receivable should have 500 outstanding before return."
    }

    $eligible = Invoke-Json GET "$baseUri/api/v3/finance/credit-returns/eligible?limit=50" $session
    $eligibleA = @($eligible.sales | Where-Object saleId -eq $saleA.saleId)
    if ($eligibleA.Count -ne 1 -or
        $eligibleA[0].receivableOutstandingAmountMinor -ne 500 -or
        $eligibleA[0].remainingQuantity -ne 3) {
        throw "The source credit sale was not exposed as returnable."
    }

    $returnable = Invoke-Json GET "$baseUri/api/v3/finance/credit-returns/sales/$($saleA.saleId)" $session
    $saleItemId = $returnable.items[0].saleItemId
    $creditReturn = Invoke-Json POST "$baseUri/api/v3/finance/credit-returns/sales/$($saleA.saleId)" $session @{
        items = @(@{ saleItemId = $saleItemId; quantity = 1; disposition = "restock" })
        reason = "Customer returned one resellable unit from a partly paid credit invoice"
        notes = "Reduce the outstanding invoice and retain excess as customer credit"
    }
    if ($creditReturn.returnAmountMinor -ne 1000 -or
        $creditReturn.receivableReductionMinor -ne 500 -or
        $creditReturn.customerCreditMinor -ne 500 -or
        $creditReturn.restockedBaseUnits -ne 1 -or
        $creditReturn.documents.Count -ne 2) {
        throw "The credit return did not split receivableReductionMinor and customerCreditMinor correctly."
    }

    $overReturn = Invoke-Api POST "$baseUri/api/v3/finance/credit-returns/sales/$($saleA.saleId)" $session @{
        items = @(@{ saleItemId = $saleItemId; quantity = 3; disposition = "restock" })
        reason = "This request intentionally exceeds the remaining quantity"
    } 409
    if ($overReturn.Data.error -ne "credit_return_quantity_exceeds_remaining") {
        throw "The credit-return over-quantity guard failed."
    }

    $arJournal = Get-JournalByNumber $baseUri $session ("SYS-AR-" + $creditReturn.creditNoteNumber)
    if ($arJournal.status -ne "posted" -or
        $arJournal.totalDebitMinor -ne 500 -or
        $arJournal.totalCreditMinor -ne 500 -or
        $arJournal.lines.Count -ne 2) {
        throw "The credit-note receivable journal is invalid."
    }
    $returnJournal = Get-JournalByNumber $baseUri $session ("SYS-" + $creditReturn.creditNoteNumber)
    if ($returnJournal.status -ne "posted" -or
        $returnJournal.sourceId -ne ("credit_sale_return:" + $creditReturn.id) -or
        $returnJournal.totalDebitMinor -ne 900 -or
        $returnJournal.totalCreditMinor -ne 900 -or
        $returnJournal.lines.Count -ne 4) {
        throw "The excess customer-credit and restock journal is invalid."
    }

    $receivableA = Get-ReceivableForSale $baseUri $session $customer.id $saleA.saleId
    if ($receivableA.outstandingAmountMinor -ne 0 -or $receivableA.status -ne "settled") {
        throw "The returned credit invoice was not fully settled."
    }

    $credits = Invoke-Json GET "$baseUri/api/v3/finance/customer-credits?customerId=$($customer.id)&limit=50" $session
    $credit = @($credits.credits | Where-Object sourceCreditReturnId -eq $creditReturn.id)
    if ($credit.Count -ne 1 -or
        $credit[0].originalAmountMinor -ne 500 -or
        $credit[0].availableAmountMinor -ne 500 -or
        $credit[0].status -ne "open") {
        throw "The customer-credit liability was not created correctly."
    }

    $saleB = Invoke-Json POST "$baseUri/api/v3/sales" $session @{
        items = @(@{ productId = $product.id; quantity = 2 })
        paymentMethod = "credit"
        amountReceivedMinor = 2000
        issueInvoice = $true
        customerId = $customer.id
        customerName = $customer.name
        notes = "Small outstanding receivable for credit application guard"
    }
    $receivableB = Get-ReceivableForSale $baseUri $session $customer.id $saleB.saleId
    Post-CustomerReceipt $baseUri $session $customer.id $receivableB.id 1800 $today "CR-GATE-PARTIAL-B" | Out-Null
    $receivableB = Get-ReceivableForSale $baseUri $session $customer.id $saleB.saleId
    if ($receivableB.outstandingAmountMinor -ne 200) {
        throw "The second receivable should have 200 outstanding."
    }

    $tooLarge = Invoke-Api POST "$baseUri/api/v3/finance/customer-credit-applications" $session @{
        creditId = $credit[0].id
        receivableItemId = $receivableB.id
        applicationDate = $today
        amountMinor = 500
        notes = "This application intentionally exceeds the receivable"
    } 409
    if ($tooLarge.Data.error -ne "credit_application_exceeds_receivable") {
        throw "The credit_application_exceeds_receivable guard failed."
    }

    $applicationB = Invoke-Json POST "$baseUri/api/v3/finance/customer-credit-applications" $session @{
        creditId = $credit[0].id
        receivableItemId = $receivableB.id
        applicationDate = $today
        amountMinor = 200
        notes = "Settle the small second receivable"
    }
    if ($applicationB.amountMinor -ne 200 -or $applicationB.receivableDocumentNumber -ne $receivableB.documentNumber) {
        throw "The first customer-credit application is incorrect."
    }
    $applicationBJournal = Get-JournalByNumber $baseUri $session ("SYS-" + $applicationB.applicationNumber)
    if ($applicationBJournal.totalDebitMinor -ne 200 -or
        $applicationBJournal.totalCreditMinor -ne 200 -or
        $applicationBJournal.lines.Count -ne 2) {
        throw "The first customer-credit application journal is invalid."
    }

    $saleC = Invoke-Json POST "$baseUri/api/v3/sales" $session @{
        items = @(@{ productId = $product.id; quantity = 1 })
        paymentMethod = "credit"
        amountReceivedMinor = 1000
        issueInvoice = $true
        customerId = $customer.id
        customerName = $customer.name
        notes = "Third receivable for remaining customer credit"
    }
    $receivableC = Get-ReceivableForSale $baseUri $session $customer.id $saleC.saleId
    $applicationC = Invoke-Json POST "$baseUri/api/v3/finance/customer-credit-applications" $session @{
        creditId = $credit[0].id
        receivableItemId = $receivableC.id
        applicationDate = $today
        amountMinor = 300
        notes = "Apply the remaining customer credit"
    }
    if ($applicationC.amountMinor -ne 300) {
        throw "The remaining customer credit was not applied."
    }

    $creditsAfter = Invoke-Json GET "$baseUri/api/v3/finance/customer-credits?customerId=$($customer.id)&limit=50" $session
    $creditAfter = @($creditsAfter.credits | Where-Object id -eq $credit[0].id)
    if ($creditAfter.Count -ne 1 -or
        $creditAfter[0].appliedAmountMinor -ne 500 -or
        $creditAfter[0].availableAmountMinor -ne 0 -or
        $creditAfter[0].status -ne "applied") {
        throw "The customer credit did not close after both applications."
    }

    $insufficient = Invoke-Api POST "$baseUri/api/v3/finance/customer-credit-applications" $session @{
        creditId = $credit[0].id
        receivableItemId = $receivableC.id
        applicationDate = $today
        amountMinor = 1
        notes = "This application intentionally exceeds available customer credit"
    } 409
    if ($insufficient.Data.error -ne "customer_credit_insufficient") {
        throw "The customer_credit_insufficient guard failed."
    }

    $receivableB = Get-ReceivableForSale $baseUri $session $customer.id $saleB.saleId
    $receivableC = Get-ReceivableForSale $baseUri $session $customer.id $saleC.saleId
    if ($receivableB.outstandingAmountMinor -ne 0 -or
        $receivableC.outstandingAmountMinor -ne 700) {
        throw "Customer-credit applications did not update receivables correctly."
    }

    $applications = Invoke-Json GET "$baseUri/api/v3/finance/customer-credit-applications?customerId=$($customer.id)&limit=50" $session
    if ($applications.count -ne 2 -or $applications.appliedMinor -ne 500) {
        throw "The customer-credit application register is incomplete."
    }

    $htmlDocument = @($creditReturn.documents | Where-Object fileFormat -eq "html")
    $creditNote = Invoke-Api GET "$baseUri/api/v3/finance/credit-returns/$($creditReturn.id)/documents/$($htmlDocument[0].id)" $session
    if ($creditNote.Content -notmatch "Credit note" -or
        $creditNote.Content -notmatch [regex]::Escape($creditReturn.creditNoteNumber)) {
        throw "The Credit note document is invalid."
    }

    $inventory = Invoke-Json GET "$baseUri/api/v3/admin/inventory/products?search=CREDIT-RETURN-GATE-001" $session
    $stock = @($inventory.products | Where-Object id -eq $product.id)
    if ($stock.Count -ne 1 -or $stock[0].quantityBaseUnits -ne 25) {
        throw "Final sellable stock should be 25 units."
    }
    $movements = Invoke-Json GET "$baseUri/api/v3/admin/inventory/stock-movements?productId=$($product.id)&limit=30" $session
    $returnMovements = @($movements.movements | Where-Object referenceType -eq "credit_sales_return")
    if ($returnMovements.Count -ne 1 -or
        ($returnMovements | Measure-Object quantityDeltaBaseUnits -Sum).Sum -ne 1) {
        throw "Exactly one resellable credit-return unit should have a stock movement."
    }

    $fromUtc = [uri]::EscapeDataString((Get-Date).ToUniversalTime().AddDays(-1).ToString("O"))
    $toUtc = [uri]::EscapeDataString((Get-Date).ToUniversalTime().AddDays(1).ToString("O"))
    $report = Invoke-Json GET "$baseUri/api/v3/reports/sales/summary?scope=shop&fromUtc=$fromUtc&toUtc=$toUtc" $session
    if ($report.grossSalesMinor -ne 6000 -or
        $report.returnedSalesMinor -ne 1000 -or
        $report.netSalesMinor -ne 5000 -or
        $report.returnCount -ne 1) {
        throw "Credit-return sales reporting is incorrect."
    }
    if ($report.grossCostOfGoodsSoldMinor -ne 2400 -or
        $report.restockedCostMinor -ne 400 -or
        $report.costOfGoodsSoldMinor -ne 2000 -or
        $report.grossProfitMinor -ne 3000) {
        throw "Credit-return COGS reporting is incorrect."
    }
    $creditPayment = @($report.payments | Where-Object paymentMethod -eq "credit")
    if ($creditPayment.Count -ne 1 -or
        $creditPayment[0].grossAmountMinor -ne 6000 -or
        $creditPayment[0].refundedAmountMinor -ne 1000 -or
        $creditPayment[0].amountMinor -ne 5000) {
        throw "Credit payment reporting is incorrect."
    }

    $closed = Invoke-Json POST "$baseUri/api/v3/shifts/close" $session @{
        countedCashMinor = 5000
        notes = "Credit returns must not change expected cash"
    }
    if ($closed.expectedCashMinor -ne 5000 -or $closed.cashVarianceMinor -ne 0) {
        throw "Non-cash credit activity changed teller cash reconciliation."
    }

    Write-Host "Credit-sale returns, receivable adjustments, customer-credit liability, applications, stock, reporting and documents passed."
}
catch {
    Write-Error $_
    if (Test-Path $outputLog) {
        Write-Host "--- server output ---"
        Get-Content $outputLog -Tail 350
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server error ---"
        Get-Content $errorLog -Tail 350
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
    if (Test-Path $temporaryRoot) {
        Remove-Item $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
