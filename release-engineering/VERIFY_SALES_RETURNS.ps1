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
            throw "Expected HTTP $ExpectedStatusCode but received $statusCode from $Method $Uri. Body: $content"
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
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
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
    $listing = Invoke-Json -Method GET -Uri "$BaseUri/api/v3/accounting/journals?scope=shop&limit=200" -Session $Session
    $summary = @($listing.journals | Where-Object { $_.journalNumber -eq $JournalNumber })
    if ($summary.Count -ne 1) {
        throw "Expected exactly one journal $JournalNumber but found $($summary.Count)."
    }
    return Invoke-Json -Method GET -Uri "$BaseUri/api/v3/accounting/journals/$($summary[0].id)" -Session $Session
}

function Assert-ReturnJournal {
    param(
        [string]$BaseUri,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [object]$Return,
        [long]$ExpectedTotal,
        [int]$ExpectedLineCount
    )
    $journal = Get-JournalByNumber -BaseUri $BaseUri -Session $Session -JournalNumber ("SYS-" + $Return.returnNumber)
    if ($journal.status -ne "posted" -or $journal.sourceType -ne "system" -or $journal.sourceId -ne ("sale_return:" + $Return.id)) {
        throw "Return $($Return.returnNumber) does not have the expected posted system journal."
    }
    if ($journal.totalDebitMinor -ne $ExpectedTotal -or $journal.totalCreditMinor -ne $ExpectedTotal -or $journal.lines.Count -ne $ExpectedLineCount) {
        throw "Return journal $($Return.returnNumber) is not balanced with the expected line structure."
    }
}

if ([string]::IsNullOrWhiteSpace($PortableZip)) {
    $zip = Get-ChildItem (Join-Path $PSScriptRoot "..\release") -Filter "Nexus_POS_*_Portable.zip" -File | Select-Object -First 1
    if (-not $zip) { throw "The portable Nexus POS release ZIP was not found." }
    $PortableZip = $zip.FullName
}

$PortableZip = [System.IO.Path]::GetFullPath($PortableZip)
if (-not (Test-Path -LiteralPath $PortableZip -PathType Leaf)) {
    throw "Portable release ZIP does not exist: $PortableZip"
}

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
    Expand-Archive -LiteralPath $PortableZip -DestinationPath $runtimeRoot -Force
    $serverExe = Get-ChildItem $runtimeRoot -Recurse -Filter "Robo.Pos.Server.exe" -File | Select-Object -First 1
    if (-not $serverExe) { throw "Robo.Pos.Server.exe was not found in the portable package." }

    foreach ($name in $environmentNames) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }
    [Environment]::SetEnvironmentVariable("NEXUS_DATA_DIR", $dataRoot, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_DATA_DIR", $dataRoot, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_DOCUMENT_ROOT", $documentRoot, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_DOCUMENT_ROOT", $documentRoot, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_USERNAME", "admin", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Sales Returns Gate Administrator", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_INITIAL_PASSWORD", $initialPassword, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_ADMIN_INITIAL_PASSWORD", $initialPassword, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_INSTANCE_ID", $instanceId, "Process")
    [Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Process")
    [Environment]::SetEnvironmentVariable("AllowedHosts", "localhost;127.0.0.1;[::1]", "Process")

    $port = Get-FreePort
    $baseUri = "http://127.0.0.1:$port"
    $serverProcess = Start-Process -FilePath $serverExe.FullName `
        -ArgumentList "--urls `"$baseUri`"" -WorkingDirectory $serverExe.Directory.FullName `
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
    if (-not $health -or -not $health.ok -or $health.schemaVersion -ne 17) {
        throw "Nexus did not start with exact sales-return schema version 17."
    }

    $service = Invoke-Json -Method GET -Uri "$baseUri/api/v3/service"
    if ($service.version -ne "6.7.0") { throw "The service version is not 6.7.0." }
    foreach ($capability in @(
        "controlled-partial-sales-returns",
        "same-channel-customer-refunds",
        "return-stock-disposition",
        "immutable-sales-return-register",
        "automatic-sales-return-accounting",
        "printable-credit-notes",
        "return-aware-shift-reconciliation",
        "return-aware-sales-reporting"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing sales-return capability: $capability"
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
    if ($context.shopCode -ne "MAIN") { throw "The returns gate did not start in MAIN." }

    $category = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/categories" -Session $session -Body @{
        name = "Sales Returns Gate"; description = "Automated return controls"; displayOrder = 1
    }
    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session -Body @{
        categoryId = $category.id
        sku = "RETURN-GATE-001"
        barcode = "995000000001"
        name = "Sales Return Test Product"
        description = "Used by the automated returns gate"
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

    $shift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/open" -Session $session -Body @{ openingCashMinor = 5000 }
    if ($shift.status -ne "open") { throw "The returns gate shift did not open." }

    $sale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales" -Session $session -Body @{
        items = @(@{ productId = $product.id; quantity = 3 })
        paymentMethod = "cash"
        amountReceivedMinor = 3000
        issueInvoice = $false
        customerName = "Returns Gate Customer"
        notes = "Controlled sales return gate"
    }
    if ($sale.totalMinor -ne 3000) { throw "The test sale total is incorrect." }

    $eligible = Invoke-Json -Method GET -Uri "$baseUri/api/v3/sales/returns/eligible?limit=20" -Session $session
    $eligibleSale = @($eligible.sales | Where-Object { $_.saleId -eq $sale.saleId })
    if ($eligibleSale.Count -ne 1 -or $eligibleSale[0].remainingQuantity -ne 3 -or $eligibleSale[0].remainingAmountMinor -ne 3000) {
        throw "The completed sale was not exposed as exactly returnable."
    }

    $returnable = Invoke-Json -Method GET -Uri "$baseUri/api/v3/sales/$($sale.saleId)/returnable" -Session $session
    if ($returnable.items.Count -ne 1 -or $returnable.items[0].remainingQuantity -ne 3) {
        throw "Returnable sale details are incorrect."
    }
    $saleItemId = $returnable.items[0].saleItemId

    $first = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales/$($sale.saleId)/returns" -Session $session -Body @{
        items = @(@{ saleItemId = $saleItemId; quantity = 1; disposition = "restock" })
        refundMethod = "cash"
        reason = "Customer returned one resellable unit"
        notes = "First partial return"
    }
    if ($first.refundAmountMinor -ne 1000 -or $first.restockedBaseUnits -ne 1 -or $first.items[0].disposition -ne "restock" -or $first.documents.Count -ne 2) {
        throw "The first restock return is incorrect."
    }
    Assert-ReturnJournal -BaseUri $baseUri -Session $session -Return $first -ExpectedTotal 1400 -ExpectedLineCount 4

    $second = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales/$($sale.saleId)/returns" -Session $session -Body @{
        items = @(@{ saleItemId = $saleItemId; quantity = 1; disposition = "damaged" })
        refundMethod = "cash"
        reason = "Customer returned one damaged unit"
        notes = "Damaged stock must not be restored"
    }
    if ($second.refundAmountMinor -ne 1000 -or $second.restockedBaseUnits -ne 0 -or $second.items[0].disposition -ne "damaged") {
        throw "The damaged return is incorrect."
    }
    Assert-ReturnJournal -BaseUri $baseUri -Session $session -Return $second -ExpectedTotal 1000 -ExpectedLineCount 2

    $overReturn = Invoke-Api -Method POST -Uri "$baseUri/api/v3/sales/$($sale.saleId)/returns" -Session $session -ExpectedStatusCode 409 -Body @{
        items = @(@{ saleItemId = $saleItemId; quantity = 2; disposition = "restock" })
        refundMethod = "cash"
        reason = "This request intentionally exceeds the remaining quantity"
    }
    if ($overReturn.Data.error -ne "return_quantity_exceeds_remaining") {
        throw "The cumulative over-return guard did not return the expected conflict."
    }

    $third = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales/$($sale.saleId)/returns" -Session $session -Body @{
        items = @(@{ saleItemId = $saleItemId; quantity = 1; disposition = "restock" })
        refundMethod = "cash"
        reason = "Customer returned the final resellable unit"
        notes = "Completes the original sale return"
    }
    if ($third.refundAmountMinor -ne 1000 -or $third.restockedBaseUnits -ne 1) {
        throw "The final return is incorrect."
    }
    Assert-ReturnJournal -BaseUri $baseUri -Session $session -Return $third -ExpectedTotal 1400 -ExpectedLineCount 4

    $receipt = Invoke-Json -Method GET -Uri "$baseUri/api/v3/receipts/$($sale.saleId)" -Session $session
    if ($receipt.status -ne "returned") { throw "The original receipt was not marked fully returned." }

    $eligibleAfter = Invoke-Json -Method GET -Uri "$baseUri/api/v3/sales/returns/eligible?limit=20" -Session $session
    if (@($eligibleAfter.sales | Where-Object { $_.saleId -eq $sale.saleId }).Count -ne 0) {
        throw "A fully returned sale remained in the eligible receipt queue."
    }

    $history = Invoke-Json -Method GET -Uri "$baseUri/api/v3/sales/returns?limit=20" -Session $session
    $saleReturns = @($history.returns | Where-Object { $_.saleId -eq $sale.saleId })
    if ($saleReturns.Count -ne 3 -or ($saleReturns | Measure-Object refundAmountMinor -Sum).Sum -ne 3000) {
        throw "The immutable return history is incomplete."
    }

    $htmlDocument = @($first.documents | Where-Object { $_.fileFormat -eq "html" })
    if ($htmlDocument.Count -ne 1) { throw "The first return has no HTML credit note." }
    $creditNote = Invoke-Api -Method GET -Uri "$baseUri/api/v3/sales/returns/$($first.id)/documents/$($htmlDocument[0].id)" -Session $session
    if ($creditNote.Content -notmatch "Credit note" -or $creditNote.Content -notmatch [regex]::Escape($first.returnNumber)) {
        throw "The printable credit note does not identify the return."
    }

    $inventory = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products?search=RETURN-GATE-001" -Session $session
    $stock = @($inventory.products | Where-Object { $_.id -eq $product.id })
    if ($stock.Count -ne 1 -or $stock[0].quantityBaseUnits -ne 19) {
        throw "Return disposition did not produce the expected final sellable stock of 19 units."
    }

    $movements = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/stock-movements?productId=$($product.id)&limit=20" -Session $session
    $returnMovements = @($movements.movements | Where-Object { $_.movementType -eq "sale_return" })
    if ($returnMovements.Count -ne 2 -or ($returnMovements | Measure-Object quantityDeltaBase -Sum).Sum -ne 2) {
        throw "Only the two resellable units should have sale-return stock movements."
    }

    $fromUtc = [uri]::EscapeDataString((Get-Date).ToUniversalTime().AddDays(-1).ToString("O"))
    $toUtc = [uri]::EscapeDataString((Get-Date).ToUniversalTime().AddDays(1).ToString("O"))
    $report = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/sales/summary?scope=shop&fromUtc=$fromUtc&toUtc=$toUtc" -Session $session
    if ($report.grossSalesMinor -ne 3000 -or $report.returnedSalesMinor -ne 3000 -or $report.netSalesMinor -ne 0) {
        throw "Sales reporting is not netting the refunds correctly."
    }
    if ($report.grossCostOfGoodsSoldMinor -ne 1200 -or $report.restockedCostMinor -ne 800 -or $report.costOfGoodsSoldMinor -ne 400 -or $report.grossProfitMinor -ne -400) {
        throw "Return-aware COGS and gross profit are incorrect."
    }
    if ($report.returnCount -ne 3) { throw "The sales report return count is incorrect." }
    $cash = @($report.payments | Where-Object { $_.paymentMethod -eq "cash" })
    if ($cash.Count -ne 1 -or $cash[0].grossAmountMinor -ne 3000 -or $cash[0].refundedAmountMinor -ne 3000 -or $cash[0].amountMinor -ne 0) {
        throw "The cash payment mix is not netting refunds correctly."
    }

    $closed = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/close" -Session $session -Body @{
        countedCashMinor = 5000
        notes = "Returns gate exact cash reconciliation"
    }
    if ($closed.expectedCashMinor -ne 5000 -or $closed.cashVarianceMinor -ne 0) {
        throw "Shift cash reconciliation did not subtract the completed cash refunds."
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
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
