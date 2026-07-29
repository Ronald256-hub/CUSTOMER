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
        TimeoutSec = 45
        ErrorAction = "Stop"
        SkipHttpErrorCheck = $true
    }
    if ($Session) { $parameters.WebSession = $Session }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 50 -Compress
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
        Headers = $response.Headers
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

if ([string]::IsNullOrWhiteSpace($PortableZip)) {
    $zip = Get-ChildItem (Join-Path $PSScriptRoot "..\release") `
        -Filter "Nexus_POS_*_Portable.zip" -File |
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

$temporaryRoot = Join-Path $env:TEMP ("nexus-operator-experience-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Operator2026#Initial"
$privatePassword = "Nexus!Operator2026#Private"
$instanceId = [guid]::NewGuid().ToString("N")
$serverProcess = $null
$environmentNames = @(
    "NEXUS_DATA_DIR", "ROBO_DATA_DIR", "NEXUS_DOCUMENT_ROOT", "ROBO_DOCUMENT_ROOT",
    "NEXUS_ADMIN_USERNAME", "NEXUS_ADMIN_DISPLAY_NAME", "NEXUS_ADMIN_INITIAL_PASSWORD",
    "ROBO_ADMIN_INITIAL_PASSWORD", "NEXUS_INSTANCE_ID", "ASPNETCORE_ENVIRONMENT", "AllowedHosts",
    "NEXUS_TEST_BASE_URI", "NEXUS_TEST_USERNAME", "NEXUS_TEST_PASSWORD"
)
$previousEnvironment = @{}

try {
    New-Item -ItemType Directory -Force -Path $runtimeRoot, $dataRoot, $documentRoot | Out-Null
    Expand-Archive -LiteralPath $PortableZip -DestinationPath $runtimeRoot -Force

    $serverExe = Get-ChildItem $runtimeRoot -Recurse -Filter "Robo.Pos.Server.exe" -File |
        Select-Object -First 1
    if (-not $serverExe) {
        throw "Robo.Pos.Server.exe was not found in the portable package."
    }
    $serverDirectory = $serverExe.Directory.FullName

    foreach ($name in $environmentNames) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }

    [Environment]::SetEnvironmentVariable("NEXUS_DATA_DIR", $dataRoot, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_DATA_DIR", $dataRoot, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_DOCUMENT_ROOT", $documentRoot, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_DOCUMENT_ROOT", $documentRoot, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_USERNAME", "admin", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Operator Experience Administrator", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_INITIAL_PASSWORD", $initialPassword, "Process")
    [Environment]::SetEnvironmentVariable("ROBO_ADMIN_INITIAL_PASSWORD", $initialPassword, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_INSTANCE_ID", $instanceId, "Process")
    [Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Process")
    [Environment]::SetEnvironmentVariable("AllowedHosts", "localhost;127.0.0.1;[::1]", "Process")

    $port = Get-FreePort
    $baseUri = "http://127.0.0.1:$port"
    $serverProcess = Start-Process -FilePath $serverExe.FullName `
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
            throw "The server exited with code $($serverProcess.ExitCode)."
        }
        try {
            $health = Invoke-Json -Method GET -Uri "$baseUri/api/v3/health"
            if ($health.ok -and $health.instanceId -eq $instanceId) { break }
        }
        catch { }
    }

    if (-not $health -or -not $health.ok -or $health.schemaVersion -lt 16 -or ([version]$health.version) -lt ([version]"6.1.0")) {
        throw "Nexus did not start with version 6.1.0 or later and schema version 16 or later."
    }

    $service = Invoke-Json -Method GET -Uri "$baseUri/api/v3/service"
    foreach ($capability in @(
        "enterprise-operator-command-centre",
        "role-aware-module-navigation",
        "responsive-accessible-web-shell",
        "global-module-command-palette",
        "branch-short-glass-operational-report"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing operator-experience capability: $capability"
        }
    }

    $index = Invoke-Api -Method GET -Uri "$baseUri/"
    foreach ($required in @(
        '/experience.css',
        '/experience.js',
        'id="commandPalette"',
        'id="mobileMenuButton"',
        'id="shopContext"',
        'Nexus POS'
    )) {
        if ($index.Content -notlike "*$required*") {
            throw "The upgraded application shell is missing: $required"
        }
    }

    $experienceCss = Invoke-Api -Method GET -Uri "$baseUri/experience.css"
    $experienceJs = Invoke-Api -Method GET -Uri "$baseUri/experience.js"
    if ($experienceCss.Content.Length -lt 10000 -or $experienceJs.Content.Length -lt 20000) {
        throw "The operator experience assets were not served completely."
    }
    if ($experienceJs.Content -notlike '*Short-glass liquid monitor*' -or $experienceJs.Content -notlike '*renderCommandCentre*') {
        throw "The command-centre JavaScript is incomplete."
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/login" -Session $session -Body @{
        username = "admin"
        password = $initialPassword
    }
    if (-not $login.user.mustChangePassword) {
        throw "The initial administrator password was not marked for replacement."
    }

    $changed = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/change-password" -Session $session -Body @{
        currentPassword = $initialPassword
        newPassword = $privatePassword
    }
    if (-not $changed.changed) {
        throw "Administrator password replacement failed."
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json -Method POST -Uri "$baseUri/api/v3/auth/login" -Session $session -Body @{
        username = "admin"
        password = $privatePassword
    }
    if ($login.user.role -ne "admin") {
        throw "Administrator login failed."
    }

    $context = Invoke-Json -Method GET -Uri "$baseUri/api/v3/session/shop-context" -Session $session
    if ($context.shopCode -ne "MAIN") {
        throw "The operator experience gate did not start in MAIN."
    }

    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session -Body @{
        categoryId = $null
        sku = "NX-SG-001"
        barcode = "NXSG001"
        name = "Nexus Gate Short Glass"
        description = "Measured short-glass browser and reporting acceptance product"
        productType = "short_glass"
        stockUnit = "ml"
        saleUnit = "glass"
        bottleVolumeMl = 750
        glassSizeMl = 50
        unitsPerCrate = $null
        costPriceMinor = 1200
        sellingPriceMinor = 5000
        lowStockThreshold = 500
        openingStockBaseUnits = 3000
        allowNegativeStock = $false
        trackExpiry = $false
    }
    if ($product.productType -ne "short_glass" -or $product.quantityBaseUnits -ne 3000) {
        throw "Short-glass product creation failed."
    }

    $shift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/open" -Session $session -Body @{
        openingCashMinor = 100000
    }
    if ($shift.status -ne "open") {
        throw "Teller shift opening failed."
    }

    $sale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales" -Session $session -Body @{
        items = @(
            @{
                productId = $product.id
                quantity = 3
            }
        )
        paymentMethod = "cash"
        amountReceivedMinor = 20000
        issueInvoice = $false
        customerName = ""
        customerPhone = ""
        customerAddress = ""
        customerTaxNumber = ""
        notes = "Operator experience short-glass acceptance sale"
    }
    if (-not $sale.receiptNumber -or $sale.totalMinor -ne 15000 -or $sale.changeMinor -ne 5000) {
        throw "Short-glass acceptance sale failed."
    }

    $today = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    $journalBeforeReport = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/journals?scope=shop&limit=500" -Session $session
    $shortGlass = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/short-glass?fromDate=$today&toDate=$today" -Session $session

    if ($shortGlass.count -ne 1 -or $shortGlass.shopId -ne $context.shopId) {
        throw "The branch-scoped short-glass report returned an incorrect product count or shop."
    }

    $row = $shortGlass.products[0]
    if ($row.productId -ne $product.id -or $row.glassesSold -ne 3 -or $row.volumeDispensedMl -ne 150) {
        throw "Short-glass quantity-taken calculations are incorrect. Result: $($row | ConvertTo-Json -Depth 10 -Compress)"
    }
    if ($row.availableVolumeMl -ne 2850 -or $row.remainingGlasses -ne 57 -or $row.revenueMinor -ne 15000) {
        throw "Short-glass remaining-quantity or revenue calculations are incorrect. Result: $($row | ConvertTo-Json -Depth 10 -Compress)"
    }
    if ($row.bottleEquivalentsDispensed -ne 0.2 -or $row.remainingBottleEquivalents -ne 3.8) {
        throw "Short-glass bottle-equivalent calculations are incorrect."
    }

    $invalidPeriod = Invoke-Api -Method GET -Uri "$baseUri/api/v3/reports/short-glass?fromDate=2026-07-10&toDate=2026-07-01" -Session $session -ExpectedStatusCode 400
    if (($invalidPeriod.Data.error ?? "") -ne "invalid_short_glass_period") {
        throw "The short-glass report did not reject an invalid period."
    }

    $journalAfterReport = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/journals?scope=shop&limit=500" -Session $session
    if ($journalAfterReport.count -ne $journalBeforeReport.count) {
        throw "Reading the short-glass report unexpectedly changed the accounting ledger."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $session
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 16 -or -not $backup.sha256) {
        throw "The operator-experience database backup failed verification."
    }

    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw "Node.js is required for the Microsoft Edge browser validation."
    }

    [Environment]::SetEnvironmentVariable("NEXUS_TEST_BASE_URI", $baseUri, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_TEST_USERNAME", "admin", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_TEST_PASSWORD", $privatePassword, "Process")

    $browserScript = Join-Path $PSScriptRoot "VERIFY_OPERATOR_EXPERIENCE_BROWSER.mjs"
    & node $browserScript
    if ($LASTEXITCODE -ne 0) {
        throw "The Microsoft Edge operator-experience browser test failed with exit code $LASTEXITCODE."
    }

    Write-Host "Nexus POS 6.1 operator experience validation passed."
}
catch {
    Write-Error $_
    if (Test-Path $outputLog) {
        Write-Host "--- server output ---"
        Get-Content $outputLog -Tail 200
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server errors ---"
        Get-Content $errorLog -Tail 200
    }
    throw
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        $serverProcess.WaitForExit(10000) | Out-Null
    }

    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $previousEnvironment[$name],
            "Process")
    }

    if (Test-Path $temporaryRoot) {
        Remove-Item $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
