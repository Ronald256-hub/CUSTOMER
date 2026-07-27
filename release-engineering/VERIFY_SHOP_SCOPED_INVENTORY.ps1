param(
    [string]$PortableZip = ""
)

$ErrorActionPreference = "Stop"

function Convert-ToJsonBody {
    param([object]$Value)
    return ($Value | ConvertTo-Json -Depth 12 -Compress)
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

    $parameters = @{
        Method = $Method
        Uri = $Uri
        UseBasicParsing = $true
        TimeoutSec = 25
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
    if ([string]::IsNullOrWhiteSpace($response.Content)) {
        return $null
    }

    return ($response.Content | ConvertFrom-Json)
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

function New-AdminSession {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUri,
        [Parameter(Mandatory = $true)]
        [string]$Password
    )

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json -Method POST -Uri "$BaseUri/api/v3/auth/login" -Session $session -Body @{
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
    "nexus-shop-inventory-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!ShopScope2026#Initial"
$privatePassword = "Nexus!ShopScope2026#Private"
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
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Inventory Gate Administrator", "Process")
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
    $login = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/login" -Session $bootstrapSession -Body @{
        username = "admin"
        password = $initialPassword
    }
    if (-not $login.user.mustChangePassword) {
        throw "The initial administrator account did not require password replacement."
    }

    $changed = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/change-password" -Session $bootstrapSession -Body @{
        currentPassword = $initialPassword
        newPassword = $privatePassword
    }
    if (-not $changed.changed) {
        throw "The mandatory administrator password replacement failed."
    }

    $mainSession = New-AdminSession -BaseUri $baseUri -Password $privatePassword
    $branchSession = New-AdminSession -BaseUri $baseUri -Password $privatePassword

    $mainContext = Invoke-Json -Method GET -Uri "$baseUri/api/v3/session/shop-context" -Session $mainSession
    if ($mainContext.shopCode -ne "MAIN") {
        throw "The main session did not start in the MAIN shop."
    }

    $category = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/categories" -Session $mainSession -Body @{
        name = "Shop Isolation Category"
        description = "Automated branch-isolation validation"
        displayOrder = 1
    }

    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $mainSession -Body @{
        categoryId = $category.id
        sku = "SHOP-ISO-001"
        barcode = "991111111111"
        name = "Shop Isolation Product"
        description = "Automated shop stock test"
        productType = "standard"
        stockUnit = "unit"
        saleUnit = "unit"
        bottleVolumeMl = $null
        glassSizeMl = $null
        unitsPerCrate = $null
        costPriceMinor = 250
        sellingPriceMinor = 500
        lowStockThreshold = 1
        openingStockBaseUnits = 10
        allowNegativeStock = $false
        trackExpiry = $false
    }
    if ($product.availableBaseUnits -ne 10) {
        throw "MAIN opening stock was not initialized to 10."
    }

    $branch = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/shops" -Session $mainSession -Body @{
        code = "ISO-BRANCH"
        name = "Inventory Isolation Branch"
        address = "Automated Test Branch"
        phone = "+10000000000"
        email = "inventory-branch@example.invalid"
        taxNumber = "ISO-BRANCH-TAX"
        currencyCode = "USD"
        timezoneId = "UTC"
        isHeadOffice = $false
    }

    $branchInitialContext = Invoke-Json -Method GET -Uri "$baseUri/api/v3/session/shop-context" -Session $branchSession
    $branchContext = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/session/shop-context" -Session $branchSession -Body @{
        shopId = $branch.id
        expectedVersion = $branchInitialContext.version
    }

    $branchInventory = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products?search=SHOP-ISO-001" -Session $branchSession
    $branchProduct = @($branchInventory.products | Where-Object { $_.id -eq $product.id })[0]
    if (-not $branchProduct -or $branchProduct.availableBaseUnits -ne 0) {
        throw "The new branch inherited stock from MAIN. Shop isolation failed."
    }

    $adjustment = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products/$($product.id)/stock-adjustments" -Session $branchSession -Body @{
        movementType = "adjustment"
        quantityDeltaBaseUnits = 4
        newQuantityBaseUnits = $null
        reason = "Initial branch receiving test"
        expectedStockVersion = $branchProduct.stockVersion
    }
    if ($adjustment.balanceAfterBaseUnits -ne 4) {
        throw "Branch stock adjustment did not produce a balance of 4."
    }

    $shift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/open" -Session $branchSession -Body @{
        openingCashMinor = 0
    }
    if ($shift.status -ne "open" -or $shift.shopId -ne $branch.id) {
        throw "The test shift could not be opened at the branch."
    }

    $sale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales" -Session $branchSession -Body @{
        items = @(@{
            productId = $product.id
            quantity = 2
        })
        paymentMethod = "cash"
        amountReceivedMinor = 1000
        issueInvoice = $true
        customerName = "Branch Isolation Customer"
        customerPhone = ""
        customerAddress = ""
        customerTaxNumber = ""
        notes = "Shop-scoped inventory integration test"
    }
    if ($sale.totalMinor -ne 1000) {
        throw "The branch sale did not complete correctly."
    }

    $branchAfterSale = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products?search=SHOP-ISO-001" -Session $branchSession
    $branchProductAfterSale = @($branchAfterSale.products | Where-Object { $_.id -eq $product.id })[0]
    if ($branchProductAfterSale.availableBaseUnits -ne 2) {
        throw "The branch sale did not deduct exactly two units from branch stock."
    }

    $mainInventory = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products?search=SHOP-ISO-001" -Session $mainSession
    $mainProduct = @($mainInventory.products | Where-Object { $_.id -eq $product.id })[0]
    if ($mainProduct.availableBaseUnits -ne 10) {
        throw "A sale at the branch changed MAIN stock. Shop isolation failed."
    }

    $void = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/sales/$($sale.saleId)/void" -Session $mainSession -Body @{
        reason = "Automated branch sale reversal"
    }
    if ($void.status -ne "voided" -or $void.restoredBaseUnits -ne 2) {
        throw "The audited sale void did not restore the expected branch quantity."
    }

    $mainAfterVoid = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products?search=SHOP-ISO-001" -Session $mainSession
    $mainProductAfterVoid = @($mainAfterVoid.products | Where-Object { $_.id -eq $product.id })[0]
    if ($mainProductAfterVoid.availableBaseUnits -ne 10) {
        throw "Voiding the branch sale changed MAIN stock."
    }

    $branchAfterVoid = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products?search=SHOP-ISO-001" -Session $branchSession
    $branchProductAfterVoid = @($branchAfterVoid.products | Where-Object { $_.id -eq $product.id })[0]
    if ($branchProductAfterVoid.availableBaseUnits -ne 4) {
        throw "The sale void did not restore stock to the sale's owning branch."
    }

    $movements = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/stock-movements?productId=$($product.id)&limit=20" -Session $branchSession
    $movementTypes = @($movements.movements | ForEach-Object { $_.movementType })
    foreach ($requiredType in @("adjustment", "sale", "sale_void")) {
        if ($movementTypes -notcontains $requiredType) {
            throw "The branch stock ledger is missing movement type '$requiredType'."
        }
    }

    $closed = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/close" -Session $branchSession -Body @{
        countedCashMinor = 0
        notes = "Shop inventory isolation test"
    }
    if ($closed.status -ne "closed" -or $closed.cashVarianceMinor -ne 0) {
        throw "The shift did not reconcile after the voided branch sale."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $mainSession -Body @{}
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 7) {
        throw "Backup integrity or schema-version-7 verification failed."
    }

    Write-Host "Nexus POS shop-scoped inventory isolation gate: PASS"
    Write-Host "Validated separate MAIN and branch sessions, branch adjustment, sale deduction, cross-session void restoration, stock ledger history, shift reconciliation and backup integrity."
}
catch {
    Write-Host "Nexus POS shop-scoped inventory isolation gate: FAIL - $($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path $outputLog) {
        Write-Host "--- server-output.log ---"
        Get-Content $outputLog -Tail 250 -ErrorAction SilentlyContinue
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server-error.log ---"
        Get-Content $errorLog -Tail 250 -ErrorAction SilentlyContinue
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
