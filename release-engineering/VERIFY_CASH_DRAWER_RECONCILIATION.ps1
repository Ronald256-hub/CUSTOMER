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

if ([string]::IsNullOrWhiteSpace($PortableZip)) {
    $zip = Get-ChildItem (Join-Path $PSScriptRoot "..\release") -Filter "Nexus_POS_*_Portable.zip" -File | Select-Object -First 1
    if (-not $zip) { throw "The portable Nexus POS release ZIP was not found." }
    $PortableZip = $zip.FullName
}

$PortableZip = [System.IO.Path]::GetFullPath($PortableZip)
if (-not (Test-Path -LiteralPath $PortableZip -PathType Leaf)) {
    throw "Portable release ZIP does not exist: $PortableZip"
}

$temporaryRoot = Join-Path $env:TEMP ("nexus-cash-drawer-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Drawer2026#Initial"
$privatePassword = "Nexus!Drawer2026#Private"
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
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Cash Drawer Gate Administrator", "Process")
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
    if (-not $health -or -not $health.ok -or $health.schemaVersion -ne 19) {
        throw "Nexus did not start with exact cash-drawer schema version 19."
    }

    $service = Invoke-Json -Method GET -Uri "$baseUri/api/v3/service"
    if ($service.version -ne "7.0.0") { throw "The service version is not 7.0.0." }
    foreach ($capability in @(
        "cash-drawer-custody-controls",
        "audited-float-and-safe-drops",
        "denomination-cash-counts",
        "manager-shift-reconciliation",
        "immutable-cash-drawer-register",
        "audited-journal-reversals",
        "workforce-dashboard-and-analytics"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing cash-control or preserved platform capability: $capability"
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

    $category = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/categories" -Session $session -Body @{
        name = "Cash Drawer Gate"; description = "Automated drawer control"; displayOrder = 1
    }
    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session -Body @{
        categoryId = $category.id
        sku = "DRAWER-GATE-001"
        barcode = "995000000091"
        name = "Cash Drawer Gate Product"
        description = "Used by the cash drawer release gate"
        productType = "standard"
        stockUnit = "unit"
        saleUnit = "unit"
        bottleVolumeMl = $null
        glassSizeMl = $null
        unitsPerCrate = $null
        costPriceMinor = 1000
        sellingPriceMinor = 3000
        lowStockThreshold = 1
        openingStockBaseUnits = 10
        allowNegativeStock = $false
        trackExpiry = $false
    }

    $shift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/open" -Session $session -Body @{ openingCashMinor = 10000 }
    if ($shift.status -ne "open") { throw "The cash drawer gate shift did not open." }

    $sale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales" -Session $session -Body @{
        items = @(@{ productId = $product.id; quantity = 1 })
        paymentMethod = "cash"
        amountReceivedMinor = 3000
        issueInvoice = $false
        customerName = "Cash Drawer Gate Customer"
        notes = "Cash drawer release gate"
    }
    if ($sale.totalMinor -ne 3000) { throw "The cash drawer gate sale total is incorrect." }

    $journalsAfterSale = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/journals?scope=shop&limit=200" -Session $session
    $journalCountAfterSale = @($journalsAfterSale.journals).Count

    $float = Invoke-Json -Method POST -Uri "$baseUri/api/v3/cash-drawer/movements" -Session $session -Body @{
        movementType = "float_in"
        amountMinor = 2000
        reason = "Manager supplied additional change float"
        reference = "FLOAT-GATE-001"
    }
    if ($float.movementType -ne "float_in" -or $float.amountMinor -ne 2000) {
        throw "The float-in movement is incorrect."
    }

    $drop = Invoke-Json -Method POST -Uri "$baseUri/api/v3/cash-drawer/movements" -Session $session -Body @{
        movementType = "safe_drop"
        amountMinor = 4000
        reason = "Excess notes moved to the branch safe"
        reference = "SAFE-BAG-001"
    }
    if ($drop.movementType -ne "safe_drop" -or $drop.amountMinor -ne 4000) {
        throw "The safe-drop movement is incorrect."
    }

    $excessive = Invoke-Api -Method POST -Uri "$baseUri/api/v3/cash-drawer/movements" -Session $session -ExpectedStatusCode 409 -Body @{
        movementType = "safe_drop"
        amountMinor = 12000
        reason = "This request intentionally exceeds expected drawer cash"
        reference = "SAFE-BAG-OVER"
    }
    if ($excessive.Data.error -ne "safe_drop_exceeds_drawer") {
        throw "The excessive safe-drop guard did not return the expected conflict."
    }

    $drawer = Invoke-Json -Method GET -Uri "$baseUri/api/v3/cash-drawer/current" -Session $session
    if ($drawer.openingCashMinor -ne 10000 -or $drawer.cashSalesMinor -ne 3000 -or
        $drawer.floatInMinor -ne 2000 -or $drawer.safeDropMinor -ne 4000 -or
        $drawer.expectedDrawerCashMinor -ne 11000 -or $drawer.movements.Count -ne 2) {
        throw "The live drawer position does not reconcile to 11,000 UGX."
    }

    $interim = Invoke-Json -Method POST -Uri "$baseUri/api/v3/cash-drawer/counts" -Session $session -Body @{
        countType = "interim"
        denominations = @(
            @{ denominationMinor = 10000; quantity = 1 },
            @{ denominationMinor = 1000; quantity = 1 }
        )
        notes = "Interim count matches expected drawer cash"
    }
    if ($interim.totalMinor -ne 11000 -or $interim.countType -ne "interim") {
        throw "The interim denomination count is incorrect."
    }

    $closingCount = Invoke-Json -Method POST -Uri "$baseUri/api/v3/cash-drawer/counts" -Session $session -Body @{
        countType = "closing"
        denominations = @(
            @{ denominationMinor = 10000; quantity = 1 },
            @{ denominationMinor = 1000; quantity = 1 }
        )
        notes = "Closing denomination count"
    }
    if ($closingCount.totalMinor -ne 11000 -or $closingCount.countType -ne "closing") {
        throw "The closing denomination count is incorrect."
    }

    $journalsAfterDrawer = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/journals?scope=shop&limit=200" -Session $session
    if (@($journalsAfterDrawer.journals).Count -ne $journalCountAfterSale) {
        throw "Drawer custody movements or cash counts created an accounting journal."
    }

    [Environment]::SetEnvironmentVariable("NEXUS_TEST_BASE_URI", $baseUri, "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_TEST_USERNAME", "admin", "Process")
    [Environment]::SetEnvironmentVariable("NEXUS_TEST_PASSWORD", $privatePassword, "Process")
    & node (Join-Path $PSScriptRoot "VERIFY_CASH_DRAWER_BROWSER.mjs")
    if ($LASTEXITCODE -ne 0) { throw "Microsoft Edge cash drawer validation failed." }

    $closed = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/close" -Session $session -Body @{
        countedCashMinor = 11000
        notes = "Exact cash drawer gate reconciliation"
    }
    if ($closed.status -ne "closed" -or $closed.expectedCashMinor -ne 11000 -or
        $closed.countedCashMinor -ne 11000 -or $closed.cashVarianceMinor -ne 0) {
        throw "Shift closure did not include float and safe-drop custody movements exactly once."
    }

    $pending = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/cash-drawer/reconciliations?status=pending" -Session $session
    $review = @($pending | Where-Object { $_.shiftId -eq $shift.id })
    if ($review.Count -ne 1 -or $review[0].expectedCashMinor -ne 11000 -or
        $review[0].countedCashMinor -ne 11000 -or $review[0].varianceMinor -ne 0) {
        throw "Shift closure did not create the exact pending reconciliation review."
    }

    $approved = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/cash-drawer/reconciliations/$($shift.id)/review" -Session $session -Body @{
        decision = "approved"
        notes = "Count, custody movements and expected cash verified"
    }
    if ($approved.reviewStatus -ne "approved" -or $approved.reviewedByDisplayName -ne "Cash Drawer Gate Administrator") {
        throw "Manager reconciliation approval is incorrect."
    }

    $secondReview = Invoke-Api -Method POST -Uri "$baseUri/api/v3/admin/cash-drawer/reconciliations/$($shift.id)/review" -Session $session -ExpectedStatusCode 409 -Body @{
        decision = "rejected"
        notes = "This second decision must be rejected"
    }
    if ($secondReview.Data.error -ne "shift_review_unavailable") {
        throw "The one-way manager-review guard did not return the expected conflict."
    }

    $approvedList = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/cash-drawer/reconciliations?status=approved" -Session $session
    if (@($approvedList | Where-Object { $_.shiftId -eq $shift.id }).Count -ne 1) {
        throw "The approved reconciliation was not retained in the permanent register."
    }

    $journalsAfterReview = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/journals?scope=shop&limit=200" -Session $session
    if (@($journalsAfterReview.journals).Count -ne $journalCountAfterSale) {
        throw "Shift closure or manager reconciliation created a false accounting journal."
    }

    Write-Host "Cash drawer custody, safe-drop guard, denomination counts, exact shift closure, Edge workspace and manager reconciliation passed."
}
catch {
    Write-Error $_
    if (Test-Path $outputLog) {
        Write-Host "--- server output ---"
        Get-Content $outputLog -Tail 400
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server error ---"
        Get-Content $errorLog -Tail 400
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
