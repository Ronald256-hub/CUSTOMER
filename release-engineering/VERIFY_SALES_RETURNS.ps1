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
    $listing = Invoke-Json GET "$BaseUri/api/v3/accounting/journals?scope=shop&limit=200" $Session
    $summary = @($listing.journals | Where-Object journalNumber -eq $JournalNumber)
    if ($summary.Count -ne 1) { throw "Expected one journal $JournalNumber but found $($summary.Count)." }
    Invoke-Json GET "$BaseUri/api/v3/accounting/journals/$($summary[0].id)" $Session
}

function Assert-ReturnJournal {
    param([string]$BaseUri, $Session, $Return, [long]$ExpectedTotal, [int]$ExpectedLineCount)
    $journal = Get-JournalByNumber $BaseUri $Session ("SYS-" + $Return.returnNumber)
    if ($journal.status -ne "posted" -or $journal.sourceId -ne ("sale_return:" + $Return.id)) {
        throw "Return $($Return.returnNumber) does not have the expected posted journal."
    }
    if ($journal.totalDebitMinor -ne $ExpectedTotal -or
        $journal.totalCreditMinor -ne $ExpectedTotal -or
        $journal.lines.Count -ne $ExpectedLineCount) {
        throw "Return journal $($Return.returnNumber) is not balanced as expected."
    }
}

if ([string]::IsNullOrWhiteSpace($PortableZip)) {
    $zip = Get-ChildItem (Join-Path $PSScriptRoot "..\release") -Filter "Nexus_POS_*_Portable.zip" -File | Select-Object -First 1
    if (-not $zip) { throw "The portable Nexus POS release ZIP was not found." }
    $PortableZip = $zip.FullName
}
$PortableZip = [System.IO.Path]::GetFullPath($PortableZip)
if (-not (Test-Path $PortableZip -PathType Leaf)) { throw "Portable release ZIP does not exist: $PortableZip" }

$temporaryRoot = Join-Path $env:TEMP ("nexus-sales-returns-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Returns2026#Initial"
$privatePassword = "Nexus!Returns2026#Private"
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
    if (-not $serverExe) { throw "Robo.Pos.Server.exe was not found." }

    foreach ($name in $environmentNames) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }
    $environment = @{
        NEXUS_DATA_DIR = $dataRoot; ROBO_DATA_DIR = $dataRoot
        NEXUS_DOCUMENT_ROOT = $documentRoot; ROBO_DOCUMENT_ROOT = $documentRoot
        NEXUS_ADMIN_USERNAME = "admin"; NEXUS_ADMIN_DISPLAY_NAME = "Sales Returns Gate Administrator"
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
    if (-not $health -or $health.schemaVersion -lt 17) {
        throw "Nexus did not start with sales-return schema version 17 or later."
    }

    $service = Invoke-Json GET "$baseUri/api/v3/service"
    if ([version]$service.version -lt [version]"6.7.0") {
        throw "The service version is older than Nexus POS 6.7."
    }
    foreach ($capability in @(
        "controlled-partial-sales-returns", "same-channel-customer-refunds",
        "return-stock-disposition", "immutable-sales-return-register",
        "automatic-sales-return-accounting", "printable-credit-notes",
        "return-aware-shift-reconciliation", "return-aware-sales-reporting"
    )) {
        if ($service.capabilities -notcontains $capability) { throw "Missing capability: $capability" }
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json POST "$baseUri/api/v3/auth/login" $session @{ username = "admin"; password = $initialPassword }
    if (-not $login.user.mustChangePassword) { throw "Initial password replacement was not required." }
    $changed = Invoke-Json POST "$baseUri/api/v3/auth/change-password" $session @{
        currentPassword = $initialPassword; newPassword = $privatePassword
    }
    if (-not $changed.changed) { throw "Password replacement failed." }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json POST "$baseUri/api/v3/auth/login" $session @{ username = "admin"; password = $privatePassword }
    if ($login.user.role -ne "admin") { throw "Administrator login failed." }

    $category = Invoke-Json POST "$baseUri/api/v3/admin/inventory/categories" $session @{
        name = "Sales Returns Gate"; description = "Automated return controls"; displayOrder = 1
    }
    $product = Invoke-Json POST "$baseUri/api/v3/admin/inventory/products" $session @{
        categoryId = $category.id; sku = "RETURN-GATE-001"; barcode = "995000000001"
        name = "Sales Return Test Product"; description = "Automated returns gate"
        productType = "standard"; stockUnit = "unit"; saleUnit = "unit"
        bottleVolumeMl = $null; glassSizeMl = $null; unitsPerCrate = $null
        costPriceMinor = 400; sellingPriceMinor = 1000; lowStockThreshold = 2
        openingStockBaseUnits = 20; allowNegativeStock = $false; trackExpiry = $false
    }
    $shift = Invoke-Json POST "$baseUri/api/v3/shifts/open" $session @{ openingCashMinor = 5000 }
    if ($shift.status -ne "open") { throw "The shift did not open." }

    $sale = Invoke-Json POST "$baseUri/api/v3/sales" $session @{
        items = @(@{ productId = $product.id; quantity = 3 })
        paymentMethod = "cash"; amountReceivedMinor = 3000; issueInvoice = $false
        customerName = "Returns Gate Customer"; notes = "Controlled sales return gate"
    }
    if ($sale.totalMinor -ne 3000) { throw "The test sale total is incorrect." }

    $eligible = Invoke-Json GET "$baseUri/api/v3/sales/returns/eligible?limit=20" $session
    $eligibleSale = @($eligible.sales | Where-Object saleId -eq $sale.saleId)
    if ($eligibleSale.Count -ne 1 -or $eligibleSale[0].remainingQuantity -ne 3) {
        throw "The sale is not exactly returnable."
    }
    $returnable = Invoke-Json GET "$baseUri/api/v3/sales/$($sale.saleId)/returnable" $session
    $saleItemId = $returnable.items[0].saleItemId

    $first = Invoke-Json POST "$baseUri/api/v3/sales/$($sale.saleId)/returns" $session @{
        items = @(@{ saleItemId = $saleItemId; quantity = 1; disposition = "restock" })
        refundMethod = "cash"; reason = "Customer returned one resellable unit"; notes = "First partial return"
    }
    if ($first.refundAmountMinor -ne 1000 -or $first.restockedBaseUnits -ne 1 -or $first.documents.Count -ne 2) {
        throw "The first return is incorrect."
    }
    Assert-ReturnJournal $baseUri $session $first 1400 4

    $second = Invoke-Json POST "$baseUri/api/v3/sales/$($sale.saleId)/returns" $session @{
        items = @(@{ saleItemId = $saleItemId; quantity = 1; disposition = "damaged" })
        refundMethod = "cash"; reason = "Customer returned one damaged unit"; notes = "Do not restore damaged stock"
    }
    if ($second.refundAmountMinor -ne 1000 -or $second.restockedBaseUnits -ne 0) {
        throw "The damaged return is incorrect."
    }
    Assert-ReturnJournal $baseUri $session $second 1000 2

    $overReturn = Invoke-Api POST "$baseUri/api/v3/sales/$($sale.saleId)/returns" $session @{
        items = @(@{ saleItemId = $saleItemId; quantity = 2; disposition = "restock" })
        refundMethod = "cash"; reason = "This intentionally exceeds the remaining quantity"
    } 409
    if ($overReturn.Data.error -ne "return_quantity_exceeds_remaining") {
        throw "The over-return guard failed."
    }

    $third = Invoke-Json POST "$baseUri/api/v3/sales/$($sale.saleId)/returns" $session @{
        items = @(@{ saleItemId = $saleItemId; quantity = 1; disposition = "restock" })
        refundMethod = "cash"; reason = "Customer returned the final resellable unit"; notes = "Final return"
    }
    if ($third.refundAmountMinor -ne 1000 -or $third.restockedBaseUnits -ne 1) {
        throw "The final return is incorrect."
    }
    Assert-ReturnJournal $baseUri $session $third 1400 4

    $receipt = Invoke-Json GET "$baseUri/api/v3/receipts/$($sale.saleId)" $session
    if ($receipt.status -ne "returned") { throw "The receipt was not marked returned." }
    $eligibleAfter = Invoke-Json GET "$baseUri/api/v3/sales/returns/eligible?limit=20" $session
    if (@($eligibleAfter.sales | Where-Object saleId -eq $sale.saleId).Count -ne 0) {
        throw "The returned sale remains eligible."
    }

    $history = Invoke-Json GET "$baseUri/api/v3/sales/returns?limit=20" $session
    $saleReturns = @($history.returns | Where-Object saleId -eq $sale.saleId)
    if ($saleReturns.Count -ne 3 -or
        ($saleReturns | Measure-Object refundAmountMinor -Sum).Sum -ne 3000) {
        throw "The return history is incomplete."
    }

    $htmlDocument = @($first.documents | Where-Object fileFormat -eq "html")
    $creditNote = Invoke-Api GET "$baseUri/api/v3/sales/returns/$($first.id)/documents/$($htmlDocument[0].id)" $session
    if ($creditNote.Content -notmatch "Credit note" -or
        $creditNote.Content -notmatch [regex]::Escape($first.returnNumber)) {
        throw "The Credit note is invalid."
    }

    $inventory = Invoke-Json GET "$baseUri/api/v3/admin/inventory/products?search=RETURN-GATE-001" $session
    $stock = @($inventory.products | Where-Object id -eq $product.id)
    if ($stock.Count -ne 1 -or $stock[0].quantityBaseUnits -ne 19) {
        throw "Final sellable stock is not 19 units."
    }

    $movements = Invoke-Json GET "$baseUri/api/v3/admin/inventory/stock-movements?productId=$($product.id)&limit=20" $session
    $returnMovements = @($movements.movements | Where-Object movementType -eq "sale_return")
    if ($returnMovements.Count -ne 2 -or
        ($returnMovements | Measure-Object quantityDeltaBaseUnits -Sum).Sum -ne 2) {
        throw "Only the two resellable units should have sale-return stock movements."
    }

    $fromUtc = [uri]::EscapeDataString((Get-Date).ToUniversalTime().AddDays(-1).ToString("O"))
    $toUtc = [uri]::EscapeDataString((Get-Date).ToUniversalTime().AddDays(1).ToString("O"))
    $report = Invoke-Json GET "$baseUri/api/v3/reports/sales/summary?scope=shop&fromUtc=$fromUtc&toUtc=$toUtc" $session
    if ($report.grossSalesMinor -ne 3000 -or
        $report.returnedSalesMinor -ne 3000 -or
        $report.netSalesMinor -ne 0) {
        throw "Refund reporting is incorrect."
    }
    if ($report.grossCostOfGoodsSoldMinor -ne 1200 -or
        $report.restockedCostMinor -ne 800 -or
        $report.costOfGoodsSoldMinor -ne 400 -or
        $report.grossProfitMinor -ne -400) {
        throw "Return-aware COGS is incorrect."
    }
    if ($report.returnCount -ne 3) { throw "Return count is incorrect." }
    $cash = @($report.payments | Where-Object paymentMethod -eq "cash")
    if ($cash.Count -ne 1 -or
        $cash[0].grossAmountMinor -ne 3000 -or
        $cash[0].refundedAmountMinor -ne 3000 -or
        $cash[0].amountMinor -ne 0) {
        throw "Cash payment reporting is incorrect."
    }

    $closed = Invoke-Json POST "$baseUri/api/v3/shifts/close" $session @{
        countedCashMinor = 5000; notes = "Returns gate exact cash reconciliation"
    }
    if ($closed.expectedCashMinor -ne 5000 -or $closed.cashVarianceMinor -ne 0) {
        throw "Shift cash reconciliation is incorrect."
    }

    Write-Host "Sales returns, stock disposition, refund accounting, reporting, cash reconciliation and credit notes passed."
}
catch {
    Write-Error $_
    if (Test-Path $outputLog) {
        Write-Host "--- server output ---"
        Get-Content $outputLog -Tail 300
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server error ---"
        Get-Content $errorLog -Tail 300
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
