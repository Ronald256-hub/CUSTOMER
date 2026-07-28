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

function New-JournalBody {
    param(
        [string]$Date,
        [string]$Description,
        [string]$DebitAccountId,
        [string]$CreditAccountId,
        [long]$DebitAmount,
        [long]$CreditAmount
    )

    return @{
        journalDate = $Date
        description = $Description
        lines = @(
            @{
                accountId = $DebitAccountId
                debitMinor = $DebitAmount
                creditMinor = 0
                description = "Debit line"
            },
            @{
                accountId = $CreditAccountId
                debitMinor = 0
                creditMinor = $CreditAmount
                description = "Credit line"
            }
        )
    }
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

$temporaryRoot = Join-Path $env:TEMP ("nexus-accounting-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Accounting2026#Initial"
$privatePassword = "Nexus!Accounting2026#Private"
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
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "Accounting Gate Administrator", "Process")
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
    $minimumAccountingVersion = [version]"5.5.0"
    $runningVersion = [version]$health.version
    if (-not $health -or -not $health.ok -or $health.schemaVersion -lt 10 -or $runningVersion -lt $minimumAccountingVersion) {
        throw "Nexus did not start with version 5.5.0 or later and schema version 10 or later."
    }

    $service = Invoke-Json -Method GET -Uri "$baseUri/api/v3/service"
    foreach ($capability in @(
        "organization-chart-of-accounts",
        "branch-scoped-double-entry-journals",
        "immutable-posted-ledger",
        "audited-journal-reversals",
        "accounting-period-closing-controls",
        "shop-and-consolidated-trial-balance"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing accounting capability: $capability"
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

    $mainContext = Invoke-Json -Method GET -Uri "$baseUri/api/v3/session/shop-context" -Session $session
    if ($mainContext.shopCode -ne "MAIN") { throw "The accounting gate did not start in MAIN." }

    $accountResponse = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/accounts?includeInactive=true" -Session $session
    if ($accountResponse.count -lt 16) { throw "The seeded chart of accounts is incomplete." }
    $cash = @($accountResponse.accounts | Where-Object { $_.systemKey -eq "cash_on_hand" })[0]
    $equity = @($accountResponse.accounts | Where-Object { $_.systemKey -eq "owner_equity" })[0]
    $expenseAccount = @($accountResponse.accounts | Where-Object { $_.systemKey -eq "operating_expenses" })[0]
    if (-not $cash -or $cash.normalBalance -ne "debit" -or -not $equity -or $equity.normalBalance -ne "credit") {
        throw "The seeded chart of accounts has incorrect account types or normal balances."
    }

    $customAccount = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/accounts" -Session $session -Body @{
        code = "6990"; name = "Accounting Gate Expense"; accountType = "expense"; allowManualPosting = $true
    }
    if ($customAccount.code -ne "6990" -or $customAccount.normalBalance -ne "debit" -or $customAccount.version -ne 1) {
        throw "Custom chart-of-accounts creation failed."
    }
    $customAccount = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/accounting/accounts/$($customAccount.id)" -Session $session -Body @{
        expectedVersion = $customAccount.version
        name = "Accounting Gate Expense Updated"
        allowManualPosting = $true
        isActive = $true
    }
    if ($customAccount.version -ne 2 -or $customAccount.name -notlike "*Updated") {
        throw "Chart-of-accounts optimistic update failed."
    }

    $periodResponse = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/periods" -Session $session
    $period = @($periodResponse.periods | Where-Object { $_.status -eq "open" })[0]
    if (-not $period) { throw "The current open accounting period was not created." }

    $overlap = Invoke-Api -Method POST -Uri "$baseUri/api/v3/accounting/periods" -Session $session -ExpectedStatusCode 409 -Body @{
        name = "Overlapping gate period"; startDate = $period.startDate; endDate = $period.endDate
    }
    if (($overlap.Data.error ?? "") -ne "accounting_period_overlap") {
        throw "Overlapping accounting periods were not rejected."
    }

    $journalDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    $draft = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/journals" -Session $session -Body (
        New-JournalBody -Date $journalDate -Description "Initial capital gate" `
            -DebitAccountId $cash.id -CreditAccountId $equity.id -DebitAmount 100000 -CreditAmount 100000)
    if ($draft.status -ne "draft" -or $draft.totalDebitMinor -ne 100000 -or $draft.totalCreditMinor -ne 100000 -or $draft.lines.Count -ne 2) {
        throw "Balanced journal draft creation failed."
    }

    $posted = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/journals/$($draft.id)/post" -Session $session -Body @{
        expectedVersion = $draft.version
    }
    if ($posted.status -ne "posted" -or $posted.version -ne 2 -or -not $posted.postedAtUtc) {
        throw "Balanced journal posting failed."
    }

    $immutable = Invoke-Api -Method PUT -Uri "$baseUri/api/v3/accounting/journals/$($posted.id)" -Session $session -ExpectedStatusCode 409 -Body @{
        expectedVersion = $posted.version
        journalDate = $journalDate
        description = "Attempted mutation"
        lines = @(
            @{ accountId = $cash.id; debitMinor = 1; creditMinor = 0 },
            @{ accountId = $equity.id; debitMinor = 0; creditMinor = 1 }
        )
    }
    if (($immutable.Data.error ?? "") -ne "journal_not_draft") {
        throw "Posted journal mutation was not blocked."
    }

    $unbalanced = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/journals" -Session $session -Body (
        New-JournalBody -Date $journalDate -Description "Unbalanced gate" `
            -DebitAccountId $cash.id -CreditAccountId $equity.id -DebitAmount 500 -CreditAmount 400)
    $unbalancedPost = Invoke-Api -Method POST -Uri "$baseUri/api/v3/accounting/journals/$($unbalanced.id)/post" -Session $session -ExpectedStatusCode 409 -Body @{
        expectedVersion = $unbalanced.version
    }
    if (($unbalancedPost.Data.error ?? "") -ne "journal_not_balanced") {
        throw "Unbalanced journal posting was not rejected."
    }

    $balanced = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/accounting/journals/$($unbalanced.id)" -Session $session -Body @{
        expectedVersion = $unbalanced.version
        journalDate = $journalDate
        description = "Corrected balanced gate"
        lines = @(
            @{ accountId = $expenseAccount.id; debitMinor = 500; creditMinor = 0; description = "Expense" },
            @{ accountId = $cash.id; debitMinor = 0; creditMinor = 500; description = "Cash" }
        )
    }
    $balancedPosted = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/journals/$($balanced.id)/post" -Session $session -Body @{
        expectedVersion = $balanced.version
    }
    if ($balancedPosted.status -ne "posted") { throw "Corrected journal did not post." }

    $reversed = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/journals/$($posted.id)/reverse" -Session $session -Body @{
        expectedVersion = $posted.version
        reversalDate = $journalDate
        reason = "Accounting gate reversal test"
    }
    if ($reversed.original.status -ne "reversed" -or $reversed.reversal.status -ne "posted" -or $reversed.reversal.reversalOfJournalId -ne $posted.id) {
        throw "Audited journal reversal failed."
    }
    if ($reversed.reversal.lines[0].debitMinor -ne $posted.lines[0].creditMinor -or $reversed.reversal.lines[0].creditMinor -ne $posted.lines[0].debitMinor) {
        throw "The reversal journal did not invert the original ledger lines."
    }

    $mainTrial = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/trial-balance?scope=shop&fromDate=$($period.startDate)&toDate=$($period.endDate)" -Session $session
    if ($mainTrial.totalDebitMovementMinor -ne $mainTrial.totalCreditMovementMinor -or $mainTrial.totalDebitBalanceMinor -ne $mainTrial.totalCreditBalanceMinor) {
        throw "The MAIN trial balance is out of balance."
    }

    $branch = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/shops" -Session $session -Body @{
        code = "ACCT-BRANCH"; name = "Accounting Branch"; address = "Accounting gate"
        phone = "+256700000000"; email = "accounting@example.invalid"; taxNumber = "ACCT-TAX"
        currencyCode = $mainContext.currencyCode; timezoneId = "Africa/Kampala"; isHeadOffice = $false
    }
    $branchContext = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/session/shop-context" -Session $session -Body @{
        shopId = $branch.id; expectedVersion = $mainContext.version
    }
    if ($branchContext.shopCode -ne "ACCT-BRANCH") { throw "Accounting branch switch failed." }

    $branchDraft = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/journals" -Session $session -Body (
        New-JournalBody -Date $journalDate -Description "Branch opening gate" `
            -DebitAccountId $cash.id -CreditAccountId $equity.id -DebitAmount 25000 -CreditAmount 25000)
    $branchPosted = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/journals/$($branchDraft.id)/post" -Session $session -Body @{
        expectedVersion = $branchDraft.version
    }
    if ($branchPosted.shopId -ne $branch.id -or $branchPosted.status -ne "posted") {
        throw "Branch-scoped journal posting failed."
    }

    $branchTrial = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/trial-balance?scope=shop&fromDate=$($period.startDate)&toDate=$($period.endDate)" -Session $session
    if ($branchTrial.shopId -ne $branch.id -or $branchTrial.totalDebitMovementMinor -ne 25000 -or $branchTrial.totalCreditMovementMinor -ne 25000) {
        throw "Branch trial balance isolation failed."
    }
    $consolidatedTrial = Invoke-Json -Method GET -Uri "$baseUri/api/v3/reports/trial-balance?scope=consolidated&fromDate=$($period.startDate)&toDate=$($period.endDate)" -Session $session
    if ($consolidatedTrial.totalDebitMovementMinor -ne $consolidatedTrial.totalCreditMovementMinor -or $consolidatedTrial.totalDebitMovementMinor -le $branchTrial.totalDebitMovementMinor) {
        throw "Consolidated trial balance failed."
    }

    $mainContext = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/session/shop-context" -Session $session -Body @{
        shopId = $mainContext.shopId; expectedVersion = $branchContext.version
    }

    $closingDraft = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/journals" -Session $session -Body (
        New-JournalBody -Date $journalDate -Description "Period close gate draft" `
            -DebitAccountId $customAccount.id -CreditAccountId $cash.id -DebitAmount 100 -CreditAmount 100)
    $closeBlocked = Invoke-Api -Method POST -Uri "$baseUri/api/v3/accounting/periods/$($period.id)/close" -Session $session -ExpectedStatusCode 409 -Body @{
        expectedVersion = $period.version
    }
    if (($closeBlocked.Data.error ?? "") -ne "draft_journals_in_period") {
        throw "Period closing was not blocked by a draft journal."
    }

    $closingPosted = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/journals/$($closingDraft.id)/post" -Session $session -Body @{
        expectedVersion = $closingDraft.version
    }
    if ($closingPosted.status -ne "posted") { throw "The period-close gate journal did not post." }

    $closedPeriod = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/periods/$($period.id)/close" -Session $session -Body @{
        expectedVersion = $period.version
    }
    if ($closedPeriod.status -ne "closed" -or $closedPeriod.version -ne ($period.version + 1)) {
        throw "Accounting period close failed."
    }

    $afterCloseDraft = Invoke-Json -Method POST -Uri "$baseUri/api/v3/accounting/journals" -Session $session -Body (
        New-JournalBody -Date $journalDate -Description "Closed period posting gate" `
            -DebitAccountId $customAccount.id -CreditAccountId $cash.id -DebitAmount 50 -CreditAmount 50)
    $closedPost = Invoke-Api -Method POST -Uri "$baseUri/api/v3/accounting/journals/$($afterCloseDraft.id)/post" -Session $session -ExpectedStatusCode 409 -Body @{
        expectedVersion = $afterCloseDraft.version
    }
    if (($closedPost.Data.error ?? "") -ne "accounting_period_closed") {
        throw "Posting into a closed accounting period was not rejected."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $session -Body @{}
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 10) {
        throw "Backup integrity or schema-version-10 verification failed."
    }

    Write-Host "Nexus POS accounting kernel gate: PASS"
    Write-Host "Validated chart of accounts, balanced posting, immutable posted journals, exact reversals, branch isolation, consolidated trial balance, period close controls and schema-version-10 backup."
}
catch {
    Write-Host "Nexus POS accounting kernel gate: FAIL - $($_.Exception.Message)" -ForegroundColor Red
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
