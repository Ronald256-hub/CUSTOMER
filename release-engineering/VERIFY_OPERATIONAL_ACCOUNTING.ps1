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
        $parameters.Body = $Body | ConvertTo-Json -Depth 20 -Compress
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

$temporaryRoot = Join-Path $env:TEMP ("nexus-operational-accounting-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Operational2026#Initial"
$privatePassword = "Nexus!Operational2026#Private"
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
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Operational Accounting Gate Administrator", "Process")
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
    if (-not $health -or -not $health.ok -or $health.schemaVersion -lt 11) {
        throw "Nexus did not start with operational-accounting schema version 11 or later."
    }

    $service = Invoke-Json -Method GET -Uri "$baseUri/api/v3/service"
    foreach ($capability in @(
        "atomic-sale-ledger-posting",
        "atomic-purchase-ledger-posting",
        "atomic-expense-ledger-posting",
        "automatic-operational-reversals",
        "immutable-operational-accounting-links"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing operational accounting capability: $capability"
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
    if ($context.shopCode -ne "MAIN") { throw "The operational accounting gate did not start in MAIN." }

    $category = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/categories" -Session $session -Body @{
        name = "Operational Accounting Gate"; description = "Automated ledger integration"; displayOrder = 1
    }
    $product = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/inventory/products" -Session $session -Body @{
        categoryId = $category.id
        sku = "OA-GATE-001"
        barcode = "990000000001"
        name = "Operational Accounting Test Product"
        description = "Used by the automated accounting gate"
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
    if ($product.costPriceMinor -ne 400 -or $product.sellingPriceMinor -ne 1000) {
        throw "The accounting gate product was not created with the expected prices."
    }

    $shift = Invoke-Json -Method POST -Uri "$baseUri/api/v3/shifts/open" -Session $session -Body @{
        openingCashMinor = 5000
    }
    if ($shift.status -ne "open") { throw "The teller shift did not open." }

    $sale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/sales" -Session $session -Body @{
        items = @(@{ productId = $product.id; quantity = 2 })
        paymentMethod = "cash"
        amountReceivedMinor = 2000
        issueInvoice = $false
        customerName = "Operational Accounting Customer"
        notes = "Atomic sale posting gate"
    }
    if ($sale.totalMinor -ne 2000) { throw "The test sale total is incorrect." }

    $saleJournal = Get-JournalByNumber -BaseUri $baseUri -Session $session -JournalNumber ("SYS-" + $sale.receiptNumber)
    if ($saleJournal.status -ne "posted" -or $saleJournal.sourceType -ne "system" -or $saleJournal.sourceId -ne ("sale:" + $sale.saleId)) {
        throw "The sale did not produce the expected posted system journal."
    }
    if ($saleJournal.totalDebitMinor -ne 2800 -or $saleJournal.totalCreditMinor -ne 2800 -or $saleJournal.lines.Count -ne 4) {
        throw "The sale journal does not contain balanced receipt, revenue, COGS and inventory entries."
    }

    $voidedSale = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/sales/$($sale.saleId)/void" -Session $session -Body @{
        reason = "Operational accounting gate sale reversal"
    }
    if ($voidedSale.status -ne "voided") { throw "The sale void failed." }

    $saleOriginalAfterVoid = Get-JournalByNumber -BaseUri $baseUri -Session $session -JournalNumber ("SYS-" + $sale.receiptNumber)
    $saleReversal = Get-JournalByNumber -BaseUri $baseUri -Session $session -JournalNumber ("SYS-VOID-" + $sale.receiptNumber)
    if ($saleOriginalAfterVoid.status -ne "reversed" -or $saleOriginalAfterVoid.reversedByJournalId -ne $saleReversal.id) {
        throw "The sale posting was not linked to its exact reversal."
    }
    if ($saleReversal.status -ne "posted" -or $saleReversal.sourceType -ne "reversal" -or $saleReversal.reversalOfJournalId -ne $saleOriginalAfterVoid.id) {
        throw "The sale reversal journal is invalid."
    }
    if ($saleReversal.totalDebitMinor -ne $saleOriginalAfterVoid.totalCreditMinor -or $saleReversal.totalCreditMinor -ne $saleOriginalAfterVoid.totalDebitMinor) {
        throw "The sale reversal totals do not exactly reverse the original."
    }

    $expenseDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    $expense = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/expenses" -Session $session -Body @{
        category = "Utilities"
        description = "Operational accounting gate expense"
        amountMinor = 1500
        paymentMethod = "cash"
        expenseDate = $expenseDate
    }
    $expenseJournal = Get-JournalByNumber -BaseUri $baseUri -Session $session -JournalNumber ("SYS-" + $expense.expenseNumber)
    if ($expenseJournal.status -ne "posted" -or $expenseJournal.sourceId -ne ("expense:" + $expense.id) -or $expenseJournal.totalDebitMinor -ne 1500 -or $expenseJournal.lines.Count -ne 2) {
        throw "The expense did not produce a balanced operating-expense journal."
    }

    $voidedExpense = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/expenses/$($expense.id)/void" -Session $session -Body @{
        reason = "Operational accounting gate expense reversal"
    }
    if (-not $voidedExpense.isVoided) { throw "The expense void failed." }
    $expenseOriginalAfterVoid = Get-JournalByNumber -BaseUri $baseUri -Session $session -JournalNumber ("SYS-" + $expense.expenseNumber)
    $expenseReversal = Get-JournalByNumber -BaseUri $baseUri -Session $session -JournalNumber ("SYS-VOID-" + $expense.expenseNumber)
    if ($expenseOriginalAfterVoid.status -ne "reversed" -or $expenseReversal.reversalOfJournalId -ne $expenseOriginalAfterVoid.id) {
        throw "The expense reversal linkage is invalid."
    }

    $supplier = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/suppliers" -Session $session -Body @{
        name = "Operational Accounting Supplier"
        phone = ""
        email = ""
        address = ""
        notes = "Accounting integration gate"
    }
    $purchase = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/purchases" -Session $session -Body @{
        supplierId = $supplier.id
        supplierInvoiceNumber = "OA-SUP-001"
        notes = "Automatic purchase posting gate"
        items = @(@{
            productId = $product.id
            quantityBaseUnits = 3
            unitCostMinor = 500
            batchNumber = "OA-BATCH-001"
            expiryDate = $null
        })
    }
    if ($purchase.totalMinor -ne 1500) { throw "The purchase total is incorrect." }
    $purchaseJournal = Get-JournalByNumber -BaseUri $baseUri -Session $session -JournalNumber ("SYS-" + $purchase.purchaseNumber)
    if ($purchaseJournal.status -ne "posted" -or $purchaseJournal.sourceId -ne ("purchase:" + $purchase.id) -or $purchaseJournal.totalDebitMinor -ne 1500 -or $purchaseJournal.lines.Count -ne 2) {
        throw "The purchase did not produce a balanced inventory and payable journal."
    }

    $journalListing = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/journals?scope=shop&limit=200" -Session $session
    $systemAndReversal = @($journalListing.journals | Where-Object { $_.sourceType -in @("system", "reversal") })
    if ($systemAndReversal.Count -ne 5) {
        throw "Operational events should have produced exactly three source journals and two reversal journals, but found $($systemAndReversal.Count)."
    }

    $trialBalance = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/trial-balance?scope=shop&fromDate=$expenseDate&toDate=$expenseDate" -Session $session
    if ($trialBalance.totalDebitMovementMinor -ne $trialBalance.totalCreditMovementMinor -or $trialBalance.totalDebitBalanceMinor -ne $trialBalance.totalCreditBalanceMinor) {
        throw "The operational accounting trial balance is not balanced."
    }
    $accountsPayable = @($trialBalance.lines | Where-Object { $_.accountCode -eq "2000" })[0]
    $inventory = @($trialBalance.lines | Where-Object { $_.accountCode -eq "1200" })[0]
    if (-not $accountsPayable -or $accountsPayable.creditBalanceMinor -ne 1500) {
        throw "The supplier payable balance is incorrect after the purchase posting."
    }
    if (-not $inventory -or $inventory.debitBalanceMinor -ne 1500) {
        throw "The inventory ledger balance is incorrect after sale and expense reversals plus purchase receipt."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $session -Body @{}
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 11) {
        throw "Backup integrity or schema-version-11 verification failed."
    }

    Write-Host "Nexus POS operational accounting integration gate: PASS"
    Write-Host "Validated atomic sale, purchase and expense posting; exact void reversals; immutable source linkage; balanced trial balance; and backup integrity."
}
catch {
    Write-Host "Nexus POS operational accounting integration gate: FAIL - $($_.Exception.Message)" -ForegroundColor Red
    if (Test-Path $outputLog) {
        Write-Host "--- server-output.log ---"
        Get-Content $outputLog -Tail 350 -ErrorAction SilentlyContinue
    }
    if (Test-Path $errorLog) {
        Write-Host "--- server-error.log ---"
        Get-Content $errorLog -Tail 350 -ErrorAction SilentlyContinue
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