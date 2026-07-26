param(
    [Parameter(Mandatory = $true)]
    [string]$ServerExe,

    [Parameter(Mandatory = $true)]
    [string]$RuntimeRoot
)

$ErrorActionPreference = "Stop"
$temporaryRoot = Join-Path $env:TEMP ("nexus-pos-smoke-" + [guid]::NewGuid().ToString("N"))
$dataDir = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Initial2026#Smoke"
$privatePassword = "Nexus!Private2026#Smoke"
$instanceId = [guid]::NewGuid().ToString("N")
$serverProcess = $null

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
        TimeoutSec = 20
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
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

try {
    if (-not (Test-Path $ServerExe -PathType Leaf)) {
        throw "Smoke-test server executable is missing: $ServerExe"
    }

    if (-not (Test-Path (Join-Path $RuntimeRoot "wwwroot\index.html") -PathType Leaf)) {
        throw "Smoke-test web interface is missing."
    }

    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    New-Item -ItemType Directory -Force -Path $documentRoot | Out-Null
    $port = Get-FreePort
    $baseUri = "http://127.0.0.1:$port"

    $environmentNames = @(
        "NEXUS_DATA_DIR",
        "NEXUS_DOCUMENT_ROOT",
        "NEXUS_ADMIN_USERNAME",
        "NEXUS_ADMIN_DISPLAY_NAME",
        "NEXUS_ADMIN_INITIAL_PASSWORD",
        "NEXUS_INSTANCE_ID",
        "ASPNETCORE_ENVIRONMENT",
        "AllowedHosts"
    )
    $previousEnvironment = @{}
    foreach ($name in $environmentNames) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }

    [Environment]::SetEnvironmentVariable("NEXUS_DATA_DIR", $dataDir, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_DOCUMENT_ROOT", $documentRoot, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_USERNAME", "admin", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Smoke Test Administrator", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_INITIAL_PASSWORD", $initialPassword, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_INSTANCE_ID", $instanceId, "Process")
    [Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Process")
    [Environment]::SetEnvironmentVariable("AllowedHosts", "localhost;127.0.0.1;[::1]", "Process")

    try {
        $serverProcess = Start-Process `
            -FilePath $ServerExe `
            -ArgumentList "--urls `"$baseUri`"" `
            -WorkingDirectory $RuntimeRoot `
            -WindowStyle Hidden `
            -RedirectStandardOutput $outputLog `
            -RedirectStandardError $errorLog `
            -PassThru
    }
    finally {
        foreach ($name in $environmentNames) {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
        }
    }

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

    if (-not $health -or -not $health.ok -or $health.instanceId -ne $instanceId) {
        throw "The server did not pass its health check."
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/login" -Session $session -Body @{
        username = "admin"
        password = $initialPassword
    }
    if ($login.user.role -ne "admin" -or -not $login.user.mustChangePassword) {
        throw "Initial administrator login returned unexpected account state."
    }

    $changed = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/change-password" -Session $session -Body @{
        currentPassword = $initialPassword
        newPassword = $privatePassword
    }
    if (-not $changed.changed -or -not $changed.loginRequired) {
        throw "Mandatory first-login password change failed."
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/login" -Session $session -Body @{
        username = "admin"
        password = $privatePassword
    }
    if ($login.user.role -ne "admin" -or $login.user.mustChangePassword) {
        throw "Administrator login after password change failed."
    }

    $settings = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/admin/settings" -Session $session -Body @{
        businessName = "Nexus POS Smoke Test"
        address = "Automated Release Validation"
        phone = "+000000000"
        email = "smoke@example.invalid"
        currencyCode = "USD"
        receiptFooter = "Automated test document."
    }
    if ($settings.businessName -ne "Nexus POS Smoke Test" -or $settings.currencyCode -ne "USD") {
        throw "Business white-label settings failed."
    }

    $category = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/categories" -Session $session -Body @{
        name = "Smoke Category"
        description = "Automated validation"
        displayOrder = 1
    }

    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session -Body @{
        categoryId = $category.id
        sku = "SMOKE-001"
        barcode = "990000000001"
        name = "Smoke Product"
        description = "Automated validation product"
        productType = "standard"
        stockUnit = "unit"
        saleUnit = "unit"
        bottleVolumeMl = $null
        glassSizeMl = $null
        unitsPerCrate = $null
        costPriceMinor = 250
        sellingPriceMinor = 500
        lowStockThreshold = 2
        openingStockBaseUnits = 10
        allowNegativeStock = $false
        trackExpiry = $false
    }
    if ($product.availableBaseUnits -ne 10) {
        throw "Product opening stock was not created correctly."
    }

    $createdUser = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/users" -Session $session -Body @{
        username = "smoketeller"
        displayName = "Smoke Teller"
        role = "teller"
    }
    if ($createdUser.user.role -ne "teller" -or [string]::IsNullOrWhiteSpace($createdUser.temporaryPassword)) {
        throw "Administrator teller creation failed."
    }

    $shift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/open" -Session $session -Body @{
        openingCashMinor = 0
    }
    if ($shift.status -ne "open") {
        throw "Shift opening failed."
    }

    $sale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales" -Session $session -Body @{
        items = @(@{
            productId = $product.id
            quantity = 2
        })
        paymentMethod = "cash"
        amountReceivedMinor = 1000
        issueInvoice = $true
        customerName = "Smoke Customer"
        customerPhone = ""
        customerAddress = ""
        customerTaxNumber = ""
        notes = "Automated release smoke test"
    }
    if ($sale.totalMinor -ne 1000 -or $sale.changeMinor -ne 0 -or $sale.documents.Count -lt 4) {
        throw "Sale completion or receipt/invoice generation failed."
    }

    $inventoryAfterSale = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session
    $productAfterSale = @($inventoryAfterSale.products | Where-Object { $_.id -eq $product.id })[0]
    if ($productAfterSale.availableBaseUnits -ne 8) {
        throw "Sale stock deduction failed."
    }

    $void = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/sales/$($sale.saleId)/void" -Session $session -Body @{
        reason = "Automated release smoke-test reversal"
    }
    if ($void.status -ne "voided" -or $void.restoredProductCount -ne 1) {
        throw "Audited sale void failed."
    }

    $inventoryAfterVoid = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session
    $productAfterVoid = @($inventoryAfterVoid.products | Where-Object { $_.id -eq $product.id })[0]
    if ($productAfterVoid.availableBaseUnits -ne 10) {
        throw "Sale void did not restore stock exactly once."
    }

    $receipt = Invoke-Json -Method GET -Uri "$baseUri/api/v3/receipts/$($sale.saleId)" -Session $session
    if ($receipt.status -ne "voided" -or [string]::IsNullOrWhiteSpace($receipt.voidReason)) {
        throw "Voided receipt metadata was not returned."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $session -Body @{}
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 3 -or [string]::IsNullOrWhiteSpace($backup.sha256)) {
        throw "Backup creation or integrity verification failed."
    }

    $closedShift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/close" -Session $session -Body @{
        countedCashMinor = 0
        notes = "Automated release smoke test"
    }
    if ($closedShift.status -ne "closed" -or $closedShift.cashVarianceMinor -ne 0) {
        throw "Shift closure after void failed."
    }

    $summary = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/summary" -Session $session
    if ($summary.completedSales -ne 0 -or $summary.totalSalesMinor -ne 0) {
        throw "Voided sale was incorrectly included in completed sales totals."
    }

    Write-Host "Nexus POS automated release smoke test: PASS"
    Write-Host "Validated health, password change, business profile, category, product, teller, shift, sale, documents, stock deduction, audited void, stock restoration, backup integrity, and reporting."
    exit 0
}
catch {
    Write-Host "Nexus POS automated release smoke test: FAIL - $($_.Exception.Message)" -ForegroundColor Red

    if (Test-Path $outputLog) {
        Write-Host "--- server-output.log ---"
        Get-Content $outputLog -Tail 200 -ErrorAction SilentlyContinue
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server-error.log ---"
        Get-Content $errorLog -Tail 200 -ErrorAction SilentlyContinue
    }

    $diagnosticsRoot = Join-Path `
        (Join-Path $PSScriptRoot "release") `
        ("smoke-diagnostics-" + (Get-Date -Format "yyyyMMdd-HHmmss"))

    New-Item -ItemType Directory -Force -Path $diagnosticsRoot |
        Out-Null

    if (Test-Path -LiteralPath $outputLog -PathType Leaf) {
        Copy-Item -LiteralPath $outputLog `
            -Destination (Join-Path $diagnosticsRoot "server-output.log") `
            -Force
    }
    if (Test-Path -LiteralPath $errorLog -PathType Leaf) {
        Copy-Item -LiteralPath $errorLog `
            -Destination (Join-Path $diagnosticsRoot "server-error.log") `
            -Force
    }

    $_.Exception.ToString() |
        Set-Content `
            -LiteralPath (Join-Path $diagnosticsRoot "smoke-test-error.txt") `
            -Encoding UTF8

    Write-Host "Smoke-test diagnostics saved to:" -ForegroundColor Yellow
    Write-Host $diagnosticsRoot -ForegroundColor Yellow
    exit 1
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        $serverProcess.WaitForExit(5000) | Out-Null
    }
    Remove-Item -Path $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
