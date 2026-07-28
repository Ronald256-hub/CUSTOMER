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

$temporaryRoot = Join-Path $env:TEMP ("nexus-crm-gate-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Crm2026#Initial"
$privatePassword = "Nexus!Crm2026#Private"
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
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "CRM Gate Administrator", "Process")
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
    $minimumCrmVersion = [version]"5.8.0"
    $runningVersion = [version]$health.version
    if (-not $health -or -not $health.ok -or $health.schemaVersion -lt 14 -or $runningVersion -lt $minimumCrmVersion) {
        throw "Nexus did not start with version 5.8.0 or later and CRM schema version 14 or later."
    }

    $service = Invoke-Json -Method GET -Uri "$baseUri/api/v3/service"
    foreach ($capability in @(
        "unified-finance-and-crm-customer-master",
        "customer-lifecycle-and-tagging",
        "audited-customer-communications",
        "assigned-follow-up-tasks",
        "configurable-loyalty-programme",
        "automatic-sale-loyalty-accrual",
        "automatic-sale-void-loyalty-reversal",
        "controlled-loyalty-redemption",
        "branch-numbered-customer-quotations",
        "quotation-to-sale-reconciliation",
        "customer-commercial-timeline",
        "customer-segmentation-and-dashboard"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing CRM capability: $capability"
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
    if ($context.shopCode -ne "MAIN") { throw "The CRM gate did not start in MAIN." }

    $loyaltySettings = Invoke-Json -Method GET -Uri "$baseUri/api/v3/crm/loyalty/settings" -Session $session
    if ($loyaltySettings.isEnabled -or $loyaltySettings.version -ne 1) {
        throw "Fresh-install loyalty settings were not created disabled at version one."
    }

    $tag = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/tags" -Session $session -Body @{
        name = "CRM Gate VIP"
        description = "Automated CRM lifecycle tag"
    }
    if (-not $tag.id -or $tag.name -ne "CRM Gate VIP") { throw "CRM tag creation failed." }

    $customer = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/customers" -Session $session -Body @{
        name = "CRM Gate Customer"
        phone = "+256700800001"
        email = "crm-gate@example.invalid"
        address = "CRM Gate Address"
        taxNumber = "CRM-GATE-TIN"
        creditLimitMinor = 50000
        paymentTermsDays = 30
        customerType = "business"
        companyName = "CRM Gate Enterprises"
        contactPerson = "CRM Gate Contact"
        lifecycleStage = "prospect"
        source = "CRM integration gate"
        preferredChannel = "whatsapp"
        marketingOptIn = $true
        loyaltyEnrolled = $true
        assignedUserId = $login.user.id
        notes = "End-to-end customer management validation"
        tagIds = @($tag.id)
    }
    if ($customer.customerNumber -notlike "CUS-*" -or $customer.customerType -ne "business" -or $customer.tags.Count -ne 1) {
        throw "The unified finance and CRM customer was not created correctly."
    }
    if ($customer.customerVersion -ne 1 -or $customer.profileVersion -lt 2) {
        throw "The customer and CRM profile versions are invalid."
    }

    $duplicates = Invoke-Json -Method GET -Uri "$baseUri/api/v3/crm/customers/duplicates?phone=%2B256700800001&email=crm-gate%40example.invalid" -Session $session
    if ($duplicates.count -ne 1 -or $duplicates.candidates[0].id -ne $customer.id -or $duplicates.candidates[0].matchReason -ne "phone_and_email") {
        throw "CRM duplicate detection did not reconcile the phone and email identity."
    }

    $followUpAt = (Get-Date).ToUniversalTime().AddDays(2).ToString("o")
    $communication = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/communications" -Session $session -Body @{
        customerId = $customer.id
        communicationType = "whatsapp"
        direction = "outbound"
        subject = "CRM gate follow-up"
        details = "Shared the test quotation requirements with the customer."
        outcome = "Customer requested a formal quotation."
        occurredAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        followUpAtUtc = $followUpAt
    }
    if ($communication.communicationType -ne "whatsapp" -or -not $communication.followUpAtUtc) {
        throw "The audited WhatsApp communication was not recorded."
    }

    $task = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/tasks" -Session $session -Body @{
        customerId = $customer.id
        title = "Confirm CRM gate quotation"
        details = "Call the customer after the quotation is sent."
        priority = "high"
        dueAtUtc = (Get-Date).ToUniversalTime().AddDays(1).ToString("o")
        assignedToUserId = $login.user.id
    }
    if ($task.status -ne "open" -or $task.priority -ne "high" -or $task.version -ne 1) {
        throw "The assigned CRM follow-up task was not created."
    }

    $completedTask = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/tasks/$($task.id)/complete" -Session $session -Body @{
        expectedVersion = $task.version
        completionNotes = "Customer requirements confirmed."
    }
    if ($completedTask.status -ne "completed" -or $completedTask.version -ne 2) {
        throw "The CRM follow-up task did not complete correctly."
    }
    $staleTask = Invoke-Api -Method POST -Uri "$baseUri/api/v3/crm/tasks/$($task.id)/complete" -Session $session -ExpectedStatusCode 409 -Body @{
        expectedVersion = $task.version
        completionNotes = "Must fail"
    }
    if (($staleTask.Data.error ?? "") -ne "task_changed") {
        throw "A stale CRM task transition was not rejected."
    }

    $loyaltySettings = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/crm/loyalty/settings" -Session $session -Body @{
        expectedVersion = $loyaltySettings.version
        isEnabled = $true
        spendMinorPerPoint = 1000
        minimumRedeemPoints = 1
        silverThresholdPoints = 2
        goldThresholdPoints = 5
        platinumThresholdPoints = 20
    }
    if (-not $loyaltySettings.isEnabled -or $loyaltySettings.version -ne 2) {
        throw "The CRM loyalty programme was not enabled."
    }

    $manualPoints = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/customers/$($customer.id)/loyalty-adjustments" -Session $session -Body @{
        pointsDelta = 4
        reason = "CRM gate opening loyalty adjustment"
    }
    if ($manualPoints.pointsDelta -ne 4 -or $manualPoints.balanceAfter -ne 4) {
        throw "The audited opening loyalty adjustment failed."
    }

    $category = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/categories" -Session $session -Body @{
        name = "CRM Customer Gate"
        description = "CRM quotation and loyalty validation"
        displayOrder = 1
    }
    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session -Body @{
        categoryId = $category.id
        sku = "CRM-GATE-001"
        barcode = "997800000001"
        name = "CRM Gate Product"
        description = "Used by CRM quotation and loyalty validation"
        productType = "standard"
        stockUnit = "unit"
        saleUnit = "unit"
        bottleVolumeMl = $null
        glassSizeMl = $null
        unitsPerCrate = $null
        costPriceMinor = 2000
        sellingPriceMinor = 6000
        lowStockThreshold = 2
        openingStockBaseUnits = 10
        allowNegativeStock = $false
        trackExpiry = $false
    }

    $today = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    $validUntil = (Get-Date).ToUniversalTime().AddDays(14).ToString("yyyy-MM-dd")
    $quotation = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/quotations" -Session $session -Body @{
        customerId = $customer.id
        quotationDate = $today
        validUntil = $validUntil
        discountMinor = 0
        notes = "CRM gate quotation"
        terms = "Valid for fourteen days"
        lines = @(@{
            productId = $product.id
            quantity = 1
            unitPriceMinor = 6000
        })
    }
    if ($quotation.status -ne "draft" -or $quotation.totalMinor -ne 6000 -or $quotation.lines.Count -ne 1 -or $quotation.version -ne 1) {
        throw "The CRM quotation draft is invalid."
    }

    $sentQuotation = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/quotations/$($quotation.id)/send" -Session $session -Body @{
        expectedVersion = $quotation.version
    }
    if ($sentQuotation.status -ne "sent" -or $sentQuotation.version -ne 2) {
        throw "The CRM quotation was not sent."
    }
    $acceptedQuotation = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/quotations/$($quotation.id)/accept" -Session $session -Body @{
        expectedVersion = $sentQuotation.version
    }
    if ($acceptedQuotation.status -ne "accepted" -or $acceptedQuotation.version -ne 3) {
        throw "The CRM quotation was not accepted."
    }

    $shift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/open" -Session $session -Body @{
        openingCashMinor = 10000
    }
    if ($shift.status -ne "open") { throw "The CRM gate shift did not open." }

    $sale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales" -Session $session -Body @{
        items = @(@{ productId = $product.id; quantity = 1 })
        paymentMethod = "cash"
        amountReceivedMinor = 6000
        issueInvoice = $true
        customerId = $customer.id
        customerName = $customer.name
        customerPhone = $customer.phone
        customerAddress = $customer.address
        customerTaxNumber = $customer.taxNumber
        notes = "CRM gate quotation conversion sale"
    }
    if ($sale.totalMinor -ne 6000 -or $sale.paymentMethod -ne "cash") {
        throw "The CRM customer sale did not complete."
    }

    $convertedQuotation = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/quotations/$($quotation.id)/convert" -Session $session -Body @{
        expectedVersion = $acceptedQuotation.version
        saleId = $sale.saleId
    }
    if ($convertedQuotation.status -ne "converted" -or $convertedQuotation.saleId -ne $sale.saleId) {
        throw "The accepted quotation was not reconciled to the completed customer sale."
    }

    $customerAfterSale = Invoke-Json -Method GET -Uri "$baseUri/api/v3/crm/customers/$($customer.id)" -Session $session
    if ($customerAfterSale.metrics.completedSaleCount -ne 1 -or $customerAfterSale.metrics.lifetimeSpendMinor -ne 6000) {
        throw "CRM customer sales metrics did not update after the sale."
    }
    if ($customerAfterSale.currentPoints -ne 10 -or $customerAfterSale.lifetimePoints -ne 10 -or $customerAfterSale.loyaltyTier -ne "gold") {
        throw "Automatic sale loyalty accrual or tiering is incorrect."
    }

    $loyaltyLedger = Invoke-Json -Method GET -Uri "$baseUri/api/v3/crm/customers/$($customer.id)/loyalty-ledger?limit=50" -Session $session
    $earnEntries = @($loyaltyLedger.entries | Where-Object { $_.entryType -eq "earn" -and $_.saleId -eq $sale.saleId })
    if ($earnEntries.Count -ne 1 -or $earnEntries[0].pointsDelta -ne 6 -or $loyaltyLedger.netPoints -ne 10) {
        throw "The future sale did not create exactly one six-point loyalty earning entry."
    }

    $redemption = Invoke-Json -Method POST -Uri "$baseUri/api/v3/crm/customers/$($customer.id)/loyalty-redemptions" -Session $session -Body @{
        points = 2
        reason = "CRM gate loyalty redemption"
        reference = "CRM-REDEEM-001"
    }
    if ($redemption.pointsDelta -ne -2 -or $redemption.balanceAfter -ne 8) {
        throw "The controlled loyalty redemption did not reduce the balance to eight."
    }
    $overRedemption = Invoke-Api -Method POST -Uri "$baseUri/api/v3/crm/customers/$($customer.id)/loyalty-redemptions" -Session $session -ExpectedStatusCode 409 -Body @{
        points = 100
        reason = "Must fail due to insufficient points"
        reference = "CRM-REDEEM-OVER"
    }
    if (($overRedemption.Data.error ?? "") -ne "insufficient_loyalty_points") {
        throw "An excessive loyalty redemption was not rejected."
    }

    $timelineBeforeVoid = Invoke-Json -Method GET -Uri "$baseUri/api/v3/crm/customers/$($customer.id)/timeline?limit=100" -Session $session
    foreach ($entryType in @("communication", "task", "quotation", "sale", "loyalty")) {
        if (@($timelineBeforeVoid.timeline | Where-Object { $_.entryType -eq $entryType }).Count -lt 1) {
            throw "The customer commercial timeline is missing entry type $entryType."
        }
    }

    $voidedSale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/sales/$($sale.saleId)/void" -Session $session -Body @{
        reason = "CRM gate validates automatic loyalty reversal"
    }
    if ($voidedSale.status -ne "voided") { throw "The CRM gate sale was not voided." }

    $customerAfterVoid = Invoke-Json -Method GET -Uri "$baseUri/api/v3/crm/customers/$($customer.id)" -Session $session
    if ($customerAfterVoid.metrics.completedSaleCount -ne 0 -or $customerAfterVoid.metrics.lifetimeSpendMinor -ne 0) {
        throw "Voided sales remained in CRM customer commercial metrics."
    }
    if ($customerAfterVoid.currentPoints -ne 2 -or $customerAfterVoid.lifetimePoints -ne 4 -or $customerAfterVoid.loyaltyTier -ne "silver") {
        throw "Automatic sale-void loyalty reversal did not restore the correct points and tier."
    }

    $loyaltyAfterVoid = Invoke-Json -Method GET -Uri "$baseUri/api/v3/crm/customers/$($customer.id)/loyalty-ledger?limit=50" -Session $session
    $reversalEntries = @($loyaltyAfterVoid.entries | Where-Object { $_.entryType -eq "reversal" -and $_.saleId -eq $sale.saleId })
    if ($reversalEntries.Count -ne 1 -or $reversalEntries[0].pointsDelta -ne -6 -or $loyaltyAfterVoid.netPoints -ne 2) {
        throw "The voided sale did not create exactly one six-point loyalty reversal."
    }

    $dashboard = Invoke-Json -Method GET -Uri "$baseUri/api/v3/crm/dashboard" -Session $session
    if ($dashboard.activeCustomerCount -ne 1 -or $dashboard.openTaskCount -ne 0 -or $dashboard.openQuotationCount -ne 0 -or $dashboard.outstandingLoyaltyPoints -ne 2) {
        throw "The CRM dashboard did not reconcile the final customer, task, quotation and loyalty state."
    }
    $segments = Invoke-Json -Method GET -Uri "$baseUri/api/v3/crm/segments" -Session $session
    $prospectSegment = @($segments.segments | Where-Object { $_.segment -eq "prospect" })
    if ($prospectSegment.Count -ne 1 -or $prospectSegment[0].customerCount -ne 1) {
        throw "CRM segmentation did not classify the customer after the only sale was voided."
    }

    $timelineAfterVoid = Invoke-Json -Method GET -Uri "$baseUri/api/v3/crm/customers/$($customer.id)/timeline?limit=100" -Session $session
    if (@($timelineAfterVoid.timeline | Where-Object { $_.entryType -eq "sale_void" -and $_.sourceId -eq $sale.saleId }).Count -ne 1) {
        throw "The customer timeline did not retain the sale void."
    }

    $trialBalance = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/trial-balance?scope=shop&fromDate=$today&toDate=$today" -Session $session
    if ($trialBalance.totalDebitMovementMinor -ne $trialBalance.totalCreditMovementMinor -or $trialBalance.totalDebitBalanceMinor -ne $trialBalance.totalCreditBalanceMinor) {
        throw "The trial balance is not balanced after the customer sale and void."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $session -Body @{}
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 14) {
        throw "Backup integrity or CRM schema-version-14 verification failed."
    }

    Write-Host "Nexus POS CRM and customer management gate: PASS"
    Write-Host "Validated customer master, tags, duplicates, communications, tasks, quotations, loyalty accrual/redemption/void reversal, timeline, segmentation, dashboard, accounting and backup integrity."
}
catch {
    Write-Host "Nexus POS CRM and customer management gate: FAIL - $($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path $outputLog) {
        Write-Host "--- server-output.log ---"
        Get-Content $outputLog -Tail 500 -ErrorAction SilentlyContinue
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server-error.log ---"
        Get-Content $errorLog -Tail 500 -ErrorAction SilentlyContinue
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
