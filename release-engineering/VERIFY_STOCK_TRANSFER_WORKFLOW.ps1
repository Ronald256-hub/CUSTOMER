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
        $parameters.Body = $Body | ConvertTo-Json -Depth 14 -Compress
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

function Get-Product {
    param(
        [string]$BaseUri,
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [string]$ProductId
    )
    $catalog = Invoke-Json -Method GET -Uri "$BaseUri/api/v3/admin/inventory/products?search=TRANSFER-001" -Session $Session
    return @($catalog.products | Where-Object { $_.id -eq $ProductId })[0]
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

$temporaryRoot = Join-Path $env:TEMP ("nexus-stock-transfer-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Transfer2026#Initial"
$privatePassword = "Nexus!Transfer2026#Private"
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
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Transfer Gate Administrator", "Process")
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
    if (-not $health -or -not $health.ok -or $health.schemaVersion -lt 8) {
        throw "Nexus did not start with schema version 8 or later."
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

    $mainContext = Invoke-Json -Method GET -Uri "$baseUri/api/v3/session/shop-context" -Session $session
    if ($mainContext.shopCode -ne "MAIN") { throw "The transfer gate did not start in MAIN." }

    $category = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/categories" -Session $session -Body @{
        name = "Transfer Gate Category"; description = "Automated transfer validation"; displayOrder = 1
    }
    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session -Body @{
        categoryId = $category.id; sku = "TRANSFER-001"; barcode = "998888888888"
        name = "Transfer Gate Product"; description = "Automated stock transfer test"
        productType = "standard"; stockUnit = "unit"; saleUnit = "unit"
        bottleVolumeMl = $null; glassSizeMl = $null; unitsPerCrate = $null
        costPriceMinor = 200; sellingPriceMinor = 500; lowStockThreshold = 2
        openingStockBaseUnits = 20; allowNegativeStock = $false; trackExpiry = $false
    }
    if ($product.availableBaseUnits -ne 20) { throw "MAIN opening stock was not 20." }

    $branch = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/shops" -Session $session -Body @{
        code = "TRANSFER-BRANCH"; name = "Transfer Receiving Branch"
        address = "Automated transfer branch"; phone = "+10000000000"
        email = "transfer-branch@example.invalid"; taxNumber = "TRANSFER-TAX"
        currencyCode = "USD"; timezoneId = "UTC"; isHeadOffice = $false
    }

    $draft = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers" -Session $session -Body @{
        destinationShopId = $branch.id; notes = "Primary transfer gate"
        items = @(@{ productId = $product.id; quantityBaseUnits = 7 })
    }
    if ($draft.status -ne "draft" -or $draft.requestedQuantityBaseUnits -ne 7 -or $draft.transferNumber -notlike "TRF-MAIN-*") {
        throw "Transfer draft creation or numbering failed."
    }

    $submitted = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($draft.id)/submit" -Session $session -Body @{
        expectedVersion = $draft.version; notes = "Submit for approval"
    }
    $approved = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($draft.id)/approve" -Session $session -Body @{
        expectedVersion = $submitted.version; notes = "Approved"
    }
    if ($approved.status -ne "approved" -or $approved.reservedQuantityBaseUnits -ne 7) {
        throw "Approval did not reserve seven units."
    }

    $mainProduct = Get-Product -BaseUri $baseUri -Session $session -ProductId $product.id
    if ($mainProduct.quantityBaseUnits -ne 20 -or $mainProduct.reservedBaseUnits -ne 7 -or $mainProduct.availableBaseUnits -ne 13) {
        throw "Approval did not reserve stock without deducting physical stock."
    }

    $dispatched = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($draft.id)/dispatch" -Session $session -Body @{
        expectedVersion = $approved.version; notes = "Vehicle departed"
    }
    if ($dispatched.status -ne "in_transit" -or $dispatched.dispatchedQuantityBaseUnits -ne 7 -or $dispatched.reservedQuantityBaseUnits -ne 0) {
        throw "Dispatch did not move the reservation into transit."
    }
    $mainProduct = Get-Product -BaseUri $baseUri -Session $session -ProductId $product.id
    if ($mainProduct.quantityBaseUnits -ne 13 -or $mainProduct.reservedBaseUnits -ne 0) {
        throw "Dispatch did not deduct exactly seven source units."
    }

    $branchContext = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/session/shop-context" -Session $session -Body @{
        shopId = $branch.id; expectedVersion = $mainContext.version
    }

    $partial = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($draft.id)/receive" -Session $session -Body @{
        expectedVersion = $dispatched.version; finalize = $false; notes = "First count"
        items = @(@{
            productId = $product.id; quantityReceivedBaseUnits = 5; quantityDamagedBaseUnits = 1
            discrepancyReason = "One unit damaged during transport"
        })
    }
    if ($partial.status -ne "in_transit" -or $partial.receivedQuantityBaseUnits -ne 5 -or $partial.damagedQuantityBaseUnits -ne 1 -or $partial.outstandingQuantityBaseUnits -ne 1) {
        throw "Partial receiving or discrepancy accounting failed."
    }

    $blocked = Invoke-Api -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($draft.id)/receive" -Session $session -ExpectedStatusCode 409 -Body @{
        expectedVersion = $partial.version; finalize = $true; notes = "Premature close"; items = @()
    }
    if ($blocked.Data.error -ne "transfer_quantities_outstanding") {
        throw "Finalization was not blocked with an outstanding quantity."
    }

    $received = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($draft.id)/receive" -Session $session -Body @{
        expectedVersion = $partial.version; finalize = $true; notes = "Final count"
        items = @(@{
            productId = $product.id; quantityReceivedBaseUnits = 1; quantityDamagedBaseUnits = 0
            discrepancyReason = ""
        })
    }
    if ($received.status -ne "received" -or $received.receivedQuantityBaseUnits -ne 6 -or $received.damagedQuantityBaseUnits -ne 1 -or $received.outstandingQuantityBaseUnits -ne 0) {
        throw "Final receiving totals are incorrect."
    }
    $branchProduct = Get-Product -BaseUri $baseUri -Session $session -ProductId $product.id
    if ($branchProduct.quantityBaseUnits -ne 6) { throw "Destination did not receive six accepted units." }

    $eventTypes = @($received.events | ForEach-Object { $_.eventType })
    foreach ($required in @(
        "transfer.created", "transfer.submitted", "transfer.approved",
        "transfer.dispatched", "transfer.partially_received", "transfer.received")) {
        if ($eventTypes -notcontains $required) { throw "Audit history is missing '$required'." }
    }

    $document = Invoke-Api -Method GET -Uri "$baseUri/api/v3/stock-transfers/$($draft.id)/document" -Session $session
    if ($document.Content -notmatch [regex]::Escape($received.transferNumber) -or $document.Content -notmatch "Transfer Receiving Branch") {
        throw "The printable transfer document is incomplete."
    }

    $from = [uri]::EscapeDataString((Get-Date).ToUniversalTime().AddDays(-1).ToString("O"))
    $to = [uri]::EscapeDataString((Get-Date).ToUniversalTime().AddDays(1).ToString("O"))
    $branchReport = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/stock-transfers?scope=shop&fromUtc=$from&toUtc=$to" -Session $session
    if ($branchReport.transferCount -ne 1 -or $branchReport.dispatchedQuantityBaseUnits -ne 7 -or $branchReport.receivedQuantityBaseUnits -ne 6 -or $branchReport.damagedQuantityBaseUnits -ne 1 -or $branchReport.inTransitQuantityBaseUnits -ne 0) {
        throw "The destination transfer report is incorrect."
    }

    $mainContext = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/session/shop-context" -Session $session -Body @{
        shopId = $mainContext.shopId; expectedVersion = $branchContext.version
    }

    $rejectDraft = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers" -Session $session -Body @{
        destinationShopId = $branch.id; notes = "Rejection path"
        items = @(@{ productId = $product.id; quantityBaseUnits = 2 })
    }
    $rejectSubmitted = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($rejectDraft.id)/submit" -Session $session -Body @{
        expectedVersion = $rejectDraft.version; notes = "Submit rejection"
    }
    $rejected = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($rejectDraft.id)/reject" -Session $session -Body @{
        expectedVersion = $rejectSubmitted.version; reason = "Destination cannot accept this delivery"
    }
    if ($rejected.status -ne "cancelled" -or $rejected.cancellationKind -ne "rejected") {
        throw "The rejection workflow failed."
    }

    $cancelDraft = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers" -Session $session -Body @{
        destinationShopId = $branch.id; notes = "Cancellation path"
        items = @(@{ productId = $product.id; quantityBaseUnits = 2 })
    }
    $cancelSubmitted = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($cancelDraft.id)/submit" -Session $session -Body @{
        expectedVersion = $cancelDraft.version; notes = "Submit cancellation"
    }
    $cancelApproved = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($cancelDraft.id)/approve" -Session $session -Body @{
        expectedVersion = $cancelSubmitted.version; notes = "Reserve before cancellation"
    }
    $reservedProduct = Get-Product -BaseUri $baseUri -Session $session -ProductId $product.id
    if ($reservedProduct.availableBaseUnits -ne 11 -or $reservedProduct.reservedBaseUnits -ne 2) {
        throw "The cancellation test did not reserve two units."
    }
    $cancelled = Invoke-Json -Method POST -Uri "$baseUri/api/v3/stock-transfers/$($cancelDraft.id)/cancel" -Session $session -Body @{
        expectedVersion = $cancelApproved.version; reason = "Source manager cancelled before dispatch"
    }
    if ($cancelled.status -ne "cancelled" -or $cancelled.cancellationKind -ne "cancelled") {
        throw "Approved transfer cancellation failed."
    }
    $releasedProduct = Get-Product -BaseUri $baseUri -Session $session -ProductId $product.id
    if ($releasedProduct.quantityBaseUnits -ne 13 -or $releasedProduct.reservedBaseUnits -ne 0 -or $releasedProduct.availableBaseUnits -ne 13) {
        throw "Cancellation did not release the reservation exactly once."
    }

    $consolidated = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/stock-transfers?scope=consolidated&fromUtc=$from&toUtc=$to" -Session $session
    if ($consolidated.transferCount -ne 3 -or $consolidated.receivedQuantityBaseUnits -ne 6 -or $consolidated.damagedQuantityBaseUnits -ne 1) {
        throw "The consolidated transfer report is incorrect."
    }

    $movements = Invoke-Json -Method GET -Uri "$baseUri/api/v3/admin/inventory/stock-movements?productId=$($product.id)&limit=50" -Session $session
    if (@($movements.movements | Where-Object { $_.movementType -eq "transfer_out" -and $_.referenceId -eq $draft.id }).Count -ne 1) {
        throw "The source ledger does not contain exactly one transfer_out movement."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $session -Body @{}
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 8) {
        throw "Backup integrity or schema-version-8 verification failed."
    }

    Write-Host "Nexus POS inter-shop stock transfer workflow gate: PASS"
    Write-Host "Validated reservation, dispatch, transit, partial receiving, discrepancies, final receiving, rejection, cancellation, reports, documents, ledgers and backup integrity."
}
catch {
    Write-Host "Nexus POS inter-shop stock transfer workflow gate: FAIL - $($_.Exception.Message)" -ForegroundColor Red
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
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }
    Remove-Item -Path $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}