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
        $parameters.Body = $Body | ConvertTo-Json -Depth 30 -Compress
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

function Get-JournalBySource {
    param(
        [string]$BaseUri,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [string]$SourceId
    )

    $listing = Invoke-Json -Method GET -Uri "$BaseUri/api/v3/accounting/journals?scope=shop&limit=500" -Session $Session
    $matches = @($listing.journals | Where-Object { $_.sourceId -eq $SourceId })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one journal for source $SourceId but found $($matches.Count)."
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

$temporaryRoot = Join-Path $env:TEMP ("nexus-advanced-procurement-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Procurement2026#Initial"
$privatePassword = "Nexus!Procurement2026#Private"
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
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Advanced Procurement Gate Administrator", "Process")
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
    if (-not $health -or -not $health.ok -or $health.schemaVersion -lt 13 -or $health.version -ne "5.7.0") {
        throw "Nexus did not start with version 5.7.0 and procurement schema version 13."
    }

    $service = Invoke-Json -Method GET -Uri "$baseUri/api/v3/service"
    foreach ($capability in @(
        "purchase-order-draft-submit-approval",
        "partial-goods-receipt-notes",
        "landed-cost-capitalisation",
        "batch-and-expiry-inventory",
        "audited-supplier-return-credits",
        "approved-branch-stock-counts",
        "reorder-policy-and-recommendations",
        "procurement-performance-reporting"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing procurement capability: $capability"
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
    if ($context.shopCode -ne "MAIN") { throw "The procurement gate did not start in MAIN." }

    $category = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/categories" -Session $session -Body @{
        name = "Advanced Procurement Gate"
        description = "Automated procurement and batch controls"
        displayOrder = 1
    }
    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session -Body @{
        categoryId = $category.id
        sku = "PROC-GATE-001"
        barcode = "998800000001"
        name = "Procurement Batch Test Product"
        description = "Used by the advanced procurement gate"
        productType = "standard"
        stockUnit = "unit"
        saleUnit = "unit"
        bottleVolumeMl = $null
        glassSizeMl = $null
        unitsPerCrate = $null
        costPriceMinor = 500
        sellingPriceMinor = 1000
        lowStockThreshold = 2
        openingStockBaseUnits = 0
        allowNegativeStock = $false
        trackExpiry = $true
    }
    if ($product.quantityBaseUnits -ne 0 -or -not $product.trackExpiry) {
        throw "The procurement gate product was not created with zero opening stock and expiry tracking."
    }

    $supplier = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/suppliers" -Session $session -Body @{
        name = "Advanced Procurement Supplier"
        phone = ""
        email = ""
        address = ""
        notes = "Procurement integration gate supplier"
    }

    $today = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    $expectedDate = (Get-Date).ToUniversalTime().AddDays(14).ToString("yyyy-MM-dd")
    $expiryDate = (Get-Date).ToUniversalTime().AddDays(180).ToString("yyyy-MM-dd")
    $order = Invoke-Json -Method POST -Uri "$baseUri/api/v3/procurement/purchase-orders" -Session $session -Body @{
        supplierId = $supplier.id
        orderDate = $today
        expectedDate = $expectedDate
        notes = "Advanced procurement lifecycle gate"
        items = @(@{
            productId = $product.id
            quantityBaseUnits = 10
            unitCostMinor = 500
        })
    }
    if ($order.status -ne "draft" -or $order.totalMinor -ne 5000 -or $order.lines.Count -ne 1 -or $order.version -ne 1) {
        throw "The purchase order draft is invalid."
    }

    $submitted = Invoke-Json -Method POST -Uri "$baseUri/api/v3/procurement/purchase-orders/$($order.id)/submit" -Session $session -Body @{
        expectedVersion = $order.version
    }
    if ($submitted.status -ne "submitted" -or $submitted.version -ne 2) {
        throw "The purchase order submission failed."
    }

    $approved = Invoke-Json -Method POST -Uri "$baseUri/api/v3/procurement/purchase-orders/$($order.id)/approve" -Session $session -Body @{
        expectedVersion = $submitted.version
    }
    if ($approved.status -ne "approved" -or $approved.version -ne 3) {
        throw "The purchase order approval failed."
    }

    $orderLineId = $approved.lines[0].id
    $firstReceipt = Invoke-Json -Method POST -Uri "$baseUri/api/v3/procurement/purchase-orders/$($order.id)/receive" -Session $session -Body @{
        supplierInvoiceNumber = "PROC-SUP-001-A"
        notes = "First partial GRN"
        items = @(@{
            purchaseOrderLineId = $orderLineId
            quantityBaseUnits = 4
            landedCostMinor = 400
            batchNumber = "PROC-BATCH-A"
            expiryDate = $expiryDate
        })
    }
    if ($firstReceipt.status -ne "posted" -or $firstReceipt.subtotalMinor -ne 2000 -or $firstReceipt.landedCostMinor -ne 400 -or $firstReceipt.totalMinor -ne 2400) {
        throw "The first partial goods receipt is invalid."
    }

    $partialOrder = Invoke-Json -Method GET -Uri "$baseUri/api/v3/procurement/purchase-orders/$($order.id)" -Session $session
    if ($partialOrder.status -ne "partially_received" -or $partialOrder.lines[0].receivedQuantityBaseUnits -ne 4) {
        throw "The purchase order did not retain the correct partial receipt state."
    }

    $overReceipt = Invoke-Api -Method POST -Uri "$baseUri/api/v3/procurement/purchase-orders/$($order.id)/receive" -Session $session -ExpectedStatusCode 409 -Body @{
        supplierInvoiceNumber = "PROC-OVER"
        notes = "Must fail"
        items = @(@{
            purchaseOrderLineId = $orderLineId
            quantityBaseUnits = 7
            landedCostMinor = 0
            batchNumber = "PROC-BATCH-OVER"
            expiryDate = $expiryDate
        })
    }
    if ($overReceipt.Data.error -ne "receipt_quantity_exceeds_order") {
        throw "The over-receipt request was not rejected by the quantity control."
    }

    $secondReceipt = Invoke-Json -Method POST -Uri "$baseUri/api/v3/procurement/purchase-orders/$($order.id)/receive" -Session $session -Body @{
        supplierInvoiceNumber = "PROC-SUP-001-B"
        notes = "Final GRN"
        items = @(@{
            purchaseOrderLineId = $orderLineId
            quantityBaseUnits = 6
            landedCostMinor = 600
            batchNumber = "PROC-BATCH-B"
            expiryDate = $expiryDate
        })
    }
    if ($secondReceipt.totalMinor -ne 3600) { throw "The final goods receipt total is invalid." }

    $completedOrder = Invoke-Json -Method GET -Uri "$baseUri/api/v3/procurement/purchase-orders/$($order.id)" -Session $session
    if ($completedOrder.status -ne "received" -or $completedOrder.landedCostMinor -ne 1000 -or $completedOrder.totalMinor -ne 6000 -or $completedOrder.lines[0].receivedQuantityBaseUnits -ne 10) {
        throw "The purchase order was not completed with the expected landed cost and quantities."
    }

    $firstPurchaseJournal = Get-JournalBySource -BaseUri $baseUri -Session $session -SourceId ("purchase:" + $firstReceipt.purchaseId)
    $secondPurchaseJournal = Get-JournalBySource -BaseUri $baseUri -Session $session -SourceId ("purchase:" + $secondReceipt.purchaseId)
    if ($firstPurchaseJournal.totalDebitMinor -ne 2400 -or $secondPurchaseJournal.totalDebitMinor -ne 3600) {
        throw "The GRNs did not create the expected landed-cost-capitalised purchase journals."
    }

    $payables = Invoke-Json -Method GET -Uri "$baseUri/api/v3/finance/payables?status=open&limit=50" -Session $session
    if ($payables.count -ne 2 -or $payables.outstandingMinor -ne 6000) {
        throw "The two GRNs did not create the expected supplier payable open items."
    }

    $batchesBeforeReturn = Invoke-Json -Method GET -Uri "$baseUri/api/v3/procurement/batches?productId=$($product.id)&status=active&expiringWithinDays=365&limit=20" -Session $session
    if ($batchesBeforeReturn.count -ne 2 -or $batchesBeforeReturn.availableQuantityBaseUnits -ne 10) {
        throw "The batch registry does not show both received batches and quantities."
    }

    $productAfterReceipt = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products?search=PROC-GATE-001" -Session $session
    $productSnapshot = @($productAfterReceipt.products | Where-Object { $_.id -eq $product.id })[0]
    if (-not $productSnapshot -or $productSnapshot.quantityBaseUnits -ne 10) {
        throw "Branch stock did not increase to ten units after the two GRNs."
    }

    $supplierReturn = Invoke-Json -Method POST -Uri "$baseUri/api/v3/procurement/goods-receipts/$($firstReceipt.id)/supplier-returns" -Session $session -Body @{
        reason = "Damaged units returned to supplier"
        items = @(@{
            goodsReceiptLineId = $firstReceipt.lines[0].id
            quantityBaseUnits = 2
        })
    }
    if ($supplierReturn.status -ne "posted" -or $supplierReturn.totalMinor -ne 1200 -or $supplierReturn.lines[0].quantityBaseUnits -ne 2) {
        throw "The supplier return did not post the expected quantity and value."
    }

    $returnJournal = Get-JournalBySource -BaseUri $baseUri -Session $session -SourceId ("supplier_return:" + $supplierReturn.id)
    if ($returnJournal.status -ne "posted" -or $returnJournal.totalDebitMinor -ne 1200 -or $returnJournal.totalCreditMinor -ne 1200 -or $returnJournal.lines.Count -ne 2) {
        throw "The supplier return credit journal is not balanced."
    }

    $batchesAfterReturn = Invoke-Json -Method GET -Uri "$baseUri/api/v3/procurement/batches?productId=$($product.id)&status=active&limit=20" -Session $session
    if ($batchesAfterReturn.availableQuantityBaseUnits -ne 8) {
        throw "The supplier return did not reduce the inventory batch quantities to eight."
    }

    $policy = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/procurement/reorder-policies/$($product.id)" -Session $session -Body @{
        productId = $product.id
        reorderPointBaseUnits = 8
        targetStockBaseUnits = 15
        leadTimeDays = 14
        preferredSupplierId = $supplier.id
        isActive = $true
        expectedVersion = $null
    }
    if ($policy.version -ne 1 -or $policy.targetStockBaseUnits -ne 15) {
        throw "The reorder policy was not created."
    }

    $recommendations = Invoke-Json -Method GET -Uri "$baseUri/api/v3/procurement/reorder-recommendations" -Session $session
    $recommendation = @($recommendations.recommendations | Where-Object { $_.productId -eq $product.id })[0]
    if (-not $recommendation -or $recommendation.availableBaseUnits -ne 8 -or $recommendation.onOrderBaseUnits -ne 0 -or $recommendation.suggestedOrderBaseUnits -ne 7) {
        throw "The reorder recommendation is incorrect after the supplier return."
    }

    $stockCount = Invoke-Json -Method POST -Uri "$baseUri/api/v3/procurement/stock-counts" -Session $session -Body @{
        notes = "Advanced procurement gate blind count"
    }
    if ($stockCount.status -ne "draft" -or $stockCount.lines.Count -ne 1 -or $stockCount.lines[0].systemQuantityBaseUnits -ne 8) {
        throw "The stock count snapshot is invalid."
    }

    $submittedCount = Invoke-Json -Method POST -Uri "$baseUri/api/v3/procurement/stock-counts/$($stockCount.id)/submit" -Session $session -Body @{
        expectedVersion = $stockCount.version
        lines = @(@{
            stockCountLineId = $stockCount.lines[0].id
            countedQuantityBaseUnits = 7
        })
    }
    if ($submittedCount.status -ne "submitted" -or $submittedCount.lines[0].varianceBaseUnits -ne -1) {
        throw "The stock count submission did not record the one-unit shortage."
    }

    $approvedCount = Invoke-Json -Method POST -Uri "$baseUri/api/v3/procurement/stock-counts/$($stockCount.id)/approve" -Session $session -Body @{
        expectedVersion = $submittedCount.version
        reason = "Verified one-unit physical shortage"
    }
    if ($approvedCount.status -ne "approved") {
        throw "The stock count approval failed."
    }

    $countJournal = Get-JournalBySource -BaseUri $baseUri -Session $session -SourceId ("stock_count:" + $stockCount.id)
    if ($countJournal.status -ne "posted" -or $countJournal.totalDebitMinor -ne 500 -or $countJournal.totalCreditMinor -ne 500 -or $countJournal.lines.Count -ne 2) {
        throw "The stock count shortage journal is not balanced."
    }

    $productAfterCount = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products?search=PROC-GATE-001" -Session $session
    $finalProduct = @($productAfterCount.products | Where-Object { $_.id -eq $product.id })[0]
    if (-not $finalProduct -or $finalProduct.quantityBaseUnits -ne 7) {
        throw "The approved stock count did not set branch stock to seven units."
    }

    $movements = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/stock-movements?productId=$($product.id)&limit=50" -Session $session
    $purchaseMovements = @($movements.movements | Where-Object { $_.movementType -eq "purchase" })
    $returnMovements = @($movements.movements | Where-Object { $_.movementType -eq "supplier_return" })
    $countMovements = @($movements.movements | Where-Object { $_.movementType -eq "stocktake" })
    if ($purchaseMovements.Count -ne 2 -or $returnMovements.Count -ne 1 -or $countMovements.Count -ne 1) {
        throw "The procurement stock movement audit trail is incomplete."
    }

    $trialBalance = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/trial-balance?scope=shop&fromDate=$today&toDate=$today" -Session $session
    if ($trialBalance.totalDebitMovementMinor -ne $trialBalance.totalCreditMovementMinor -or $trialBalance.totalDebitBalanceMinor -ne $trialBalance.totalCreditBalanceMinor) {
        throw "The trial balance is not balanced after procurement, return and stock count posting."
    }
    $accountsPayable = @($trialBalance.lines | Where-Object { $_.accountCode -eq "2000" })[0]
    $inventory = @($trialBalance.lines | Where-Object { $_.accountCode -eq "1200" })[0]
    if (-not $accountsPayable -or $accountsPayable.creditBalanceMinor -ne 4800) {
        throw "The payable ledger did not reflect the supplier return credit."
    }
    if (-not $inventory -or $inventory.debitBalanceMinor -ne 4300) {
        throw "The inventory ledger did not reflect receipts, return and stock-count shortage."
    }

    $summary = Invoke-Json -Method GET -Uri "$baseUri/api/v3/procurement/reports/summary?fromDate=$today&toDate=$today" -Session $session
    if ($summary.purchaseOrderCount -ne 1 -or $summary.goodsReceiptCount -ne 2 -or $summary.goodsReceivedValueMinor -ne 6000 -or $summary.landedCostMinor -ne 1000 -or $summary.supplierReturnCount -ne 1 -or $summary.supplierReturnValueMinor -ne 1200) {
        throw "The procurement summary is not reconcilable to the test lifecycle."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $session -Body @{}
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 13) {
        throw "Backup integrity or schema-version-13 verification failed."
    }

    Write-Host "Nexus POS advanced procurement and inventory gate: PASS"
    Write-Host "Validated purchase order approval, partial GRNs, landed cost, batches, expiry, supplier returns, reorder planning, stock counts, balanced journals and backup integrity."
}
catch {
    Write-Host "Nexus POS advanced procurement and inventory gate: FAIL - $($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path $outputLog) {
        Write-Host "--- server-output.log ---"
        Get-Content $outputLog -Tail 400 -ErrorAction SilentlyContinue
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server-error.log ---"
        Get-Content $errorLog -Tail 400 -ErrorAction SilentlyContinue
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
