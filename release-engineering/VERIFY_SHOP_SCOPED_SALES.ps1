param(
    [string]$PortableZip = ""
)

$ErrorActionPreference = "Stop"

function Convert-ToJsonBody {
    param([object]$Value)
    return ($Value | ConvertTo-Json -Depth 16 -Compress)
}

function Invoke-JsonResponse {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Method,
        [Parameter(Mandatory = $true)]
        [string]$Uri,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [object]$Body,
        [int]$ExpectedStatusCode = 0
    )

    $parameters = @{
        Method = $Method
        Uri = $Uri
        UseBasicParsing = $true
        SkipHttpErrorCheck = $true
        TimeoutSec = 30
        ErrorAction = "Stop"
    }

    if ($Session) {
        $parameters.WebSession = $Session
    }

    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = Convert-ToJsonBody $Body
    }

    $response = Invoke-WebRequest @parameters
    $statusCode = [int]$response.StatusCode
    $data = $null

    if (-not [string]::IsNullOrWhiteSpace($response.Content)) {
        $data = $response.Content | ConvertFrom-Json
    }

    if ($ExpectedStatusCode -gt 0) {
        if ($statusCode -ne $ExpectedStatusCode) {
            throw "Expected HTTP $ExpectedStatusCode from $Method $Uri but received $statusCode. Body: $($response.Content)"
        }
    }
    elseif ($statusCode -lt 200 -or $statusCode -ge 300) {
        throw "HTTP $statusCode from $Method $Uri. Body: $($response.Content)"
    }

    return [pscustomobject]@{
        StatusCode = $statusCode
        Data = $data
    }
}

function Invoke-Json {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Method,
        [Parameter(Mandatory = $true)]
        [string]$Uri,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [object]$Body
    )

    return (Invoke-JsonResponse `
        -Method $Method `
        -Uri $Uri `
        -Session $Session `
        -Body $Body).Data
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function New-AuthenticatedSession {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUri,
        [Parameter(Mandatory = $true)]
        [string]$Password
    )

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json `
        -Method POST `
        -Uri "$BaseUri/api/v3/auth/login" `
        -Session $session `
        -Body @{
            username = "admin"
            password = $Password
        }

    if ($login.user.role -ne "admin") {
        throw "Administrator login failed."
    }

    return $session
}

if ([string]::IsNullOrWhiteSpace($PortableZip)) {
    $zip = Get-ChildItem (Join-Path $PSScriptRoot "..\release") `
        -Filter "Nexus_POS_*_Portable.zip" `
        -File |
        Select-Object -First 1

    if (-not $zip) {
        throw "The portable Nexus POS release ZIP was not found."
    }

    $PortableZip = $zip.FullName
}

$PortableZip = [System.IO.Path]::GetFullPath($PortableZip)
if (-not (Test-Path -LiteralPath $PortableZip -PathType Leaf)) {
    throw "Portable release ZIP does not exist: $PortableZip"
}

$temporaryRoot = Join-Path $env:TEMP (
    "nexus-shop-sales-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!SalesScope2026#Initial"
$privatePassword = "Nexus!SalesScope2026#Private"
$instanceId = [guid]::NewGuid().ToString("N")
$serverProcess = $null

$environmentNames = @(
    "NEXUS_DATA_DIR",
    "ROBO_DATA_DIR",
    "NEXUS_DOCUMENT_ROOT",
    "ROBO_DOCUMENT_ROOT",
    "NEXUS_ADMIN_USERNAME",
    "NEXUS_ADMIN_DISPLAY_NAME",
    "NEXUS_ADMIN_INITIAL_PASSWORD",
    "ROBO_ADMIN_INITIAL_PASSWORD",
    "NEXUS_INSTANCE_ID",
    "ASPNETCORE_ENVIRONMENT",
    "AllowedHosts"
)
$previousEnvironment = @{}

try {
    New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $documentRoot | Out-Null
    Expand-Archive -LiteralPath $PortableZip -DestinationPath $runtimeRoot -Force

    $serverExe = Get-ChildItem $runtimeRoot `
        -Recurse `
        -Filter "Robo.Pos.Server.exe" `
        -File |
        Select-Object -First 1

    if (-not $serverExe) {
        throw "Robo.Pos.Server.exe was not found in the portable package."
    }

    $serverDirectory = $serverExe.Directory.FullName

    foreach ($name in $environmentNames) {
        $previousEnvironment[$name] =
            [Environment]::GetEnvironmentVariable($name, "Process")
    }

    [Environment]::SetEnvironmentVariable("NEXUS_DATA_DIR", $dataRoot, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_DATA_DIR", $dataRoot, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_DOCUMENT_ROOT", $documentRoot, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_DOCUMENT_ROOT", $documentRoot, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_USERNAME", "admin", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Sales Gate Administrator", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_INITIAL_PASSWORD", $initialPassword, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_ADMIN_INITIAL_PASSWORD", $initialPassword, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_INSTANCE_ID", $instanceId, "Process")
    [Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Process")
    [Environment]::SetEnvironmentVariable("AllowedHosts", "localhost;127.0.0.1;[::1]", "Process")

    $port = Get-FreePort
    $baseUri = "http://127.0.0.1:$port"
    $serverProcess = Start-Process `
        -FilePath $serverExe.FullName `
        -ArgumentList "--urls `"$baseUri`"" `
        -WorkingDirectory $serverDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outputLog `
        -RedirectStandardError $errorLog `
        -PassThru

    $health = $null
    for ($attempt = 0; $attempt -lt 360; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($serverProcess.HasExited) {
            throw "The server exited during startup with code $($serverProcess.ExitCode)."
        }

        try {
            $health = Invoke-Json -Method GET -Uri "$baseUri/api/v3/health"
            if ($health.ok -and $health.instanceId -eq $instanceId) {
                break
            }
        }
        catch {
        }
    }

    if (-not $health -or -not $health.ok -or $health.schemaVersion -lt 7) {
        throw "Nexus did not start with schema version 7 or later."
    }

    $bootstrapSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $bootstrapLogin = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/login" -Session $bootstrapSession -Body @{
        username = "admin"
        password = $initialPassword
    }
    if (-not $bootstrapLogin.user.mustChangePassword) {
        throw "The initial administrator account did not require password replacement."
    }

    $changed = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/change-password" -Session $bootstrapSession -Body @{
        currentPassword = $initialPassword
        newPassword = $privatePassword
    }
    if (-not $changed.changed) {
        throw "The mandatory administrator password replacement failed."
    }

    $mainSession = New-AuthenticatedSession -BaseUri $baseUri -Password $privatePassword
    $branchSession = New-AuthenticatedSession -BaseUri $baseUri -Password $privatePassword

    $mainContext = Invoke-Json -Method GET -Uri "$baseUri/api/v3/session/shop-context" -Session $mainSession
    if ($mainContext.shopCode -ne "MAIN") {
        throw "The main administration session did not initialize at MAIN."
    }

    $category = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/categories" -Session $mainSession -Body @{
        name = "Shop Sales Category"
        description = "Automated shop sales validation"
        displayOrder = 1
    }

    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $mainSession -Body @{
        categoryId = $category.id
        sku = "SHOP-SALES-001"
        barcode = "992222222222"
        name = "Shop Sales Product"
        description = "Automated shift receipt and report test"
        productType = "standard"
        stockUnit = "unit"
        saleUnit = "unit"
        bottleVolumeMl = $null
        glassSizeMl = $null
        unitsPerCrate = $null
        costPriceMinor = 200
        sellingPriceMinor = 500
        lowStockThreshold = 1
        openingStockBaseUnits = 20
        allowNegativeStock = $false
        trackExpiry = $false
    }

    $branch = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/shops" -Session $mainSession -Body @{
        code = "SALES-BRANCH"
        name = "Sales Isolation Branch"
        address = "Automated Branch Address"
        phone = "+10000000001"
        email = "sales-branch@example.invalid"
        taxNumber = "SALES-BRANCH-TAX"
        currencyCode = "USD"
        timezoneId = "UTC"
        isHeadOffice = $false
    }

    $branchStartContext = Invoke-Json -Method GET -Uri "$baseUri/api/v3/session/shop-context" -Session $branchSession
    $branchContext = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/session/shop-context" -Session $branchSession -Body @{
        shopId = $branch.id
        expectedVersion = $branchStartContext.version
    }

    $branchInventory = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products?search=SHOP-SALES-001" -Session $branchSession
    $branchProduct = @($branchInventory.products | Where-Object { $_.id -eq $product.id })[0]
    if (-not $branchProduct -or $branchProduct.availableBaseUnits -ne 0) {
        throw "The new branch did not start with an isolated zero stock balance."
    }

    $adjustment = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products/$($product.id)/stock-adjustments" -Session $branchSession -Body @{
        movementType = "adjustment"
        quantityDeltaBaseUnits = 10
        newQuantityBaseUnits = $null
        reason = "Branch sales gate opening stock"
        expectedStockVersion = $branchProduct.stockVersion
    }
    if ($adjustment.balanceAfterBaseUnits -ne 10) {
        throw "The branch opening adjustment did not produce ten units."
    }

    $branchShift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/open" -Session $branchSession -Body @{
        openingCashMinor = 0
    }
    if ($branchShift.status -ne "open" -or $branchShift.shopId -ne $branch.id) {
        throw "The teller shift was not opened in the selected branch."
    }

    $blockedSwitch = Invoke-JsonResponse -Method PUT -Uri "$baseUri/api/v3/session/shop-context" -Session $branchSession -ExpectedStatusCode 409 -Body @{
        shopId = $mainContext.shopId
        expectedVersion = $branchContext.version
    }
    if ($blockedSwitch.Data.error -ne "open_shift_shop_switch_blocked") {
        throw "Switching away from an open branch shift was not blocked correctly."
    }

    $branchSale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales" -Session $branchSession -Body @{
        items = @(@{
            productId = $product.id
            quantity = 2
        })
        paymentMethod = "cash"
        amountReceivedMinor = 1000
        issueInvoice = $true
        customerName = "Branch Sales Customer"
        customerPhone = ""
        customerAddress = ""
        customerTaxNumber = ""
        notes = "Shop-scoped sales integration test"
    }

    if ($branchSale.totalMinor -ne 1000 -or
        $branchSale.shopId -ne $branch.id -or
        $branchSale.receiptNumber -notlike "RCT-SALES-BRANCH-*" -or
        $branchSale.invoiceNumber -notlike "INV-SALES-BRANCH-*") {
        throw "The branch sale or branch-specific document numbering failed."
    }

    $branchReceipts = Invoke-Json -Method GET -Uri "$baseUri/api/v3/receipts?limit=20" -Session $branchSession
    if ($branchReceipts.shopId -ne $branch.id -or
        @($branchReceipts.receipts | Where-Object { $_.saleId -eq $branchSale.saleId }).Count -ne 1) {
        throw "The branch receipt was not visible in its owning shop."
    }

    $branchReceipt = Invoke-Json -Method GET -Uri "$baseUri/api/v3/receipts/$($branchSale.saleId)" -Session $branchSession
    if ($branchReceipt.shopId -ne $branch.id -or $branchReceipt.documents.Count -lt 6) {
        throw "The branch receipt details or immutable receipt documents are incomplete."
    }

    $reprint = Invoke-Json -Method POST -Uri "$baseUri/api/v3/receipts/$($branchSale.saleId)/reprint" -Session $branchSession
    if ($reprint.reprintVersion -lt 2 -or $reprint.documents.Count -lt 6) {
        throw "The audited branch receipt reprint was not recorded."
    }

    $from = [uri]::EscapeDataString("2020-01-01T00:00:00Z")
    $to = [uri]::EscapeDataString("2099-01-01T00:00:00Z")
    $branchReport = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/sales/summary?scope=shop&fromUtc=$from&toUtc=$to" -Session $branchSession
    if ($branchReport.shopId -ne $branch.id -or
        $branchReport.completedSalesCount -ne 1 -or
        $branchReport.grossSalesMinor -ne 1000 -or
        $branchReport.costOfGoodsSoldMinor -ne 400 -or
        $branchReport.grossProfitMinor -ne 600) {
        throw "The branch sales and profit summary is incorrect."
    }

    $closedBranchShift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/close" -Session $branchSession -Body @{
        countedCashMinor = 1000
        notes = "Shop sales branch reconciliation"
    }
    if ($closedBranchShift.status -ne "closed" -or
        $closedBranchShift.expectedCashMinor -ne 1000 -or
        $closedBranchShift.cashVarianceMinor -ne 0 -or
        $closedBranchShift.shopId -ne $branch.id) {
        throw "The branch shift cash reconciliation is incorrect."
    }

    $returnedToMain = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/session/shop-context" -Session $branchSession -Body @{
        shopId = $mainContext.shopId
        expectedVersion = $branchContext.version
    }
    if ($returnedToMain.shopCode -ne "MAIN") {
        throw "The session could not switch after closing the branch shift."
    }

    $mainReceiptsBeforeSale = Invoke-Json -Method GET -Uri "$baseUri/api/v3/receipts?limit=20" -Session $mainSession
    if (@($mainReceiptsBeforeSale.receipts | Where-Object { $_.saleId -eq $branchSale.saleId }).Count -ne 0) {
        throw "A branch receipt leaked into the MAIN receipt list."
    }

    $hiddenBranchReceipt = Invoke-JsonResponse -Method GET -Uri "$baseUri/api/v3/receipts/$($branchSale.saleId)" -Session $mainSession -ExpectedStatusCode 404
    if ($hiddenBranchReceipt.Data.error -ne "receipt_not_found") {
        throw "Cross-shop receipt access did not return the expected not-found response."
    }

    $mainReportBeforeSale = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/sales/summary?scope=shop&fromUtc=$from&toUtc=$to" -Session $mainSession
    if ($mainReportBeforeSale.completedSalesCount -ne 0) {
        throw "The branch sale leaked into the MAIN shop report."
    }

    $consolidatedBeforeMainSale = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/sales/summary?scope=consolidated&fromUtc=$from&toUtc=$to" -Session $mainSession
    if ($consolidatedBeforeMainSale.completedSalesCount -ne 1 -or
        $consolidatedBeforeMainSale.grossSalesMinor -ne 1000) {
        throw "The consolidated report did not include the branch sale."
    }

    $mainShift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/open" -Session $mainSession -Body @{
        openingCashMinor = 0
    }
    if ($mainShift.shopCode -ne "MAIN") {
        throw "The MAIN shift was not associated with MAIN."
    }

    $mainSale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales" -Session $mainSession -Body @{
        items = @(@{
            productId = $product.id
            quantity = 1
        })
        paymentMethod = "cash"
        amountReceivedMinor = 500
        issueInvoice = $false
        customerName = "Main Shop Customer"
        customerPhone = ""
        customerAddress = ""
        customerTaxNumber = ""
        notes = "MAIN sales isolation test"
    }

    if ($mainSale.receiptNumber -notlike "RCT-MAIN-*" -or
        $mainSale.receiptNumber -eq $branchSale.receiptNumber) {
        throw "MAIN and branch receipt numbering was not isolated."
    }

    $closedMainShift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/close" -Session $mainSession -Body @{
        countedCashMinor = 500
        notes = "MAIN sales reconciliation"
    }
    if ($closedMainShift.expectedCashMinor -ne 500 -or
        $closedMainShift.cashVarianceMinor -ne 0) {
        throw "The MAIN shift cash reconciliation is incorrect."
    }

    $consolidated = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/sales/summary?scope=consolidated&fromUtc=$from&toUtc=$to" -Session $mainSession
    if ($consolidated.completedSalesCount -ne 2 -or
        $consolidated.grossSalesMinor -ne 1500 -or
        $consolidated.costOfGoodsSoldMinor -ne 600 -or
        $consolidated.grossProfitMinor -ne 900 -or
        $consolidated.shops.Count -lt 2) {
        throw "The consolidated multi-shop sales report is incorrect."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $mainSession -Body @{}
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 7) {
        throw "Backup integrity or schema-version-7 verification failed."
    }

    Write-Host "Nexus POS shop-scoped sales gate: PASS"
    Write-Host "Validated branch shifts, switch protection, per-shop receipt numbering, receipt isolation, audited reprints, cash reconciliation, shop reports, consolidated reports and backup integrity."
}
catch {
    Write-Host "Nexus POS shop-scoped sales gate: FAIL - $($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path $outputLog) {
        Write-Host "--- server-output.log ---"
        Get-Content $outputLog -Tail 300 -ErrorAction SilentlyContinue
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server-error.log ---"
        Get-Content $errorLog -Tail 300 -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        $serverProcess.WaitForExit(5000) | Out-Null
    }

    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $previousEnvironment[$name],
            "Process")
    }

    Remove-Item -Path $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
