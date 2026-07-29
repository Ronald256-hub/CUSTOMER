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
    else { $content }
    return [pscustomobject]@{ StatusCode = $statusCode; Data = $data; Content = $content }
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

$temporaryRoot = Join-Path $env:TEMP ("nexus-saas-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Saas2026#Initial"
$privatePassword = "Nexus!Saas2026#Private"
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
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "SaaS Gate Administrator", "Process")
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
    if (-not $health -or -not $health.ok -or $health.schemaVersion -lt 16 -or ([version]$health.version) -lt ([version]"6.0.0")) {
        throw "Nexus did not start with SaaS schema 16 and version 6.0.0 or later."
    }

    $service = Invoke-Json -Method GET -Uri "$baseUri/api/v3/service"
    foreach ($capability in @(
        "saas-plan-and-entitlement-management",
        "organization-subscription-lifecycle",
        "safe-tenant-onboarding",
        "usage-and-limit-snapshots",
        "optional-hard-shop-and-user-limits",
        "immutable-subscription-event-ledger",
        "external-billing-event-register",
        "platform-operator-access-control",
        "time-bound-support-access-grants",
        "audited-support-case-workflow",
        "tenant-health-snapshots",
        "platform-saas-operations-dashboard"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing SaaS capability: $capability"
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
    if ($context.shopCode -ne "MAIN") { throw "The SaaS gate did not start in MAIN." }

    $journalBaseline = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/journals?scope=shop&limit=500" -Session $session

    $subscription = Invoke-Json -Method GET -Uri "$baseUri/api/v3/saas/tenant/subscription" -Session $session
    if ($subscription.planCode -ne "ENTERPRISE" -or $subscription.status -ne "active" -or $subscription.version -ne 1) {
        throw "Existing organisation compatibility subscription bootstrap failed."
    }
    $entitlements = Invoke-Json -Method GET -Uri "$baseUri/api/v3/saas/tenant/entitlements" -Session $session
    if ($entitlements.count -lt 7 -or ($entitlements.entitlements.key -notcontains "max_active_shops")) {
        throw "Compatibility entitlements were not bootstrapped."
    }

    $plans = Invoke-Json -Method GET -Uri "$baseUri/api/v3/saas/platform/plans" -Session $session
    if ($plans.count -lt 1) { throw "The initial administrator was not bootstrapped as a platform owner." }

    $plan = Invoke-Json -Method POST -Uri "$baseUri/api/v3/saas/platform/plans" -Session $session -Body @{
        code = "SAASGATE"
        name = "SaaS Gate Plan"
        description = "Controlled SaaS operations acceptance plan"
        billingInterval = "monthly"
        priceMinor = 250000
        currencyCode = "UGX"
        trialDays = 14
        enforcementMode = "report_only"
        sortOrder = 10
    }
    if ($plan.code -ne "SAASGATE" -or $plan.version -ne 1) { throw "SaaS plan creation failed." }

    $shopEntitlement = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/saas/platform/plans/$($plan.id)/entitlements/max_active_shops" -Session $session -Body @{
        isEnabled = $true; limitValue = 2; configurationJson = "{}"
    }
    $userEntitlement = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/saas/platform/plans/$($plan.id)/entitlements/max_active_users" -Session $session -Body @{
        isEnabled = $true; limitValue = 3; configurationJson = "{}"
    }
    $crmEntitlement = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/saas/platform/plans/$($plan.id)/entitlements/crm" -Session $session -Body @{
        isEnabled = $true; limitValue = $null; configurationJson = "{}"
    }
    if ($shopEntitlement.limitValue -ne 2 -or $userEntitlement.limitValue -ne 3 -or -not $crmEntitlement.isEnabled) {
        throw "SaaS entitlement configuration failed."
    }

    $updatedSubscription = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/saas/platform/tenants/$($context.organizationId)/subscription" -Session $session -Body @{
        planId = $plan.id
        status = "active"
        currentPeriodStartsUtc = [DateTimeOffset]::UtcNow.ToString("O")
        currentPeriodEndsUtc = [DateTimeOffset]::UtcNow.AddMonths(1).ToString("O")
        externalCustomerReference = "CUST-SAAS-GATE"
        externalSubscriptionReference = "SUB-SAAS-GATE"
        notes = "SaaS lifecycle acceptance"
        expectedVersion = $subscription.version
    }
    if ($updatedSubscription.planCode -ne "SAASGATE" -or $updatedSubscription.version -ne 2) {
        throw "Subscription plan transition failed."
    }

    $newUser = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/users" -Session $session -Body @{
        username = "tenantowner"
        displayName = "Tenant Owner"
        role = "teller"
    }
    if (-not $newUser.user.id) { throw "Tenant owner user creation failed." }
    $assignment = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/admin/shops/$($context.shopId)/users/$($newUser.user.id)" -Session $session -Body @{
        accessLevel = "teller"; isPrimary = $false; isActive = $true
    }
    if ($assignment.isPrimary) { throw "Tenant owner primary-shop preparation failed." }

    $onboarded = Invoke-Json -Method POST -Uri "$baseUri/api/v3/saas/platform/tenants" -Session $session -Body @{
        organizationName = "SaaS Gate Tenant"
        legalName = "SaaS Gate Tenant Limited"
        currencyCode = "UGX"
        timezoneId = "Africa/Kampala"
        shopCode = "HQ"
        shopName = "SaaS Gate Head Office"
        shopAddress = "Kampala"
        shopPhone = "+256700000111"
        shopEmail = "saas-gate@example.invalid"
        ownerUserId = $newUser.user.id
        planId = $plan.id
    }
    if ($onboarded.planCode -ne "SAASGATE" -or $onboarded.activeShopCount -ne 1 -or $onboarded.activeUserCount -ne 1) {
        throw "Safe SaaS tenant onboarding failed."
    }

    $tenants = Invoke-Json -Method GET -Uri "$baseUri/api/v3/saas/platform/tenants" -Session $session
    if ($tenants.count -ne 2) { throw "Platform tenant listing failed after onboarding." }

    $usage = Invoke-Json -Method POST -Uri "$baseUri/api/v3/saas/tenant/usage-snapshots" -Session $session
    if ($usage.activeShopCount -ne 1 -or $usage.activeUserCount -ne 2 -or $usage.limitViolationsJson -ne "[]") {
        throw "Tenant usage and limit evaluation are incorrect."
    }
    $usageList = Invoke-Json -Method GET -Uri "$baseUri/api/v3/saas/tenant/usage-snapshots" -Session $session
    if ($usageList.count -ne 1 -or $usageList.snapshots[0].id -ne $usage.id) { throw "Usage snapshot listing failed." }

    $billingReference = "INV-SAAS-001"
    $billing = Invoke-Json -Method POST -Uri "$baseUri/api/v3/saas/platform/tenants/$($context.organizationId)/billing-events" -Session $session -Body @{
        eventType = "invoice"
        externalReference = $billingReference
        amountMinor = 250000
        currencyCode = "UGX"
        status = "pending"
        dueAtUtc = [DateTimeOffset]::UtcNow.AddDays(14).ToString("O")
        detailsJson = '{"source":"saas-gate"}'
    }
    if ($billing.amountMinor -ne 250000 -or $billing.status -ne "pending") { throw "Billing-event creation failed." }
    $duplicateBilling = Invoke-Api -Method POST -Uri "$baseUri/api/v3/saas/platform/tenants/$($context.organizationId)/billing-events" -Session $session -ExpectedStatusCode 409 -Body @{
        eventType = "invoice"; externalReference = $billingReference; amountMinor = 250000; currencyCode = "UGX"; status = "pending"
    }
    if (($duplicateBilling.Data.error ?? "") -ne "billing_reference_exists") { throw "Billing idempotency was not enforced." }
    $billingList = Invoke-Json -Method GET -Uri "$baseUri/api/v3/saas/tenant/billing-events" -Session $session
    if ($billingList.count -ne 1 -or $billingList.billingEvents[0].id -ne $billing.id) { throw "Tenant billing-event visibility failed." }

    $supportCase = Invoke-Json -Method POST -Uri "$baseUri/api/v3/saas/tenant/support-cases" -Session $session -Body @{
        shopId = $context.shopId
        category = "deployment"
        priority = "urgent"
        subject = "SaaS gate support lifecycle"
        description = "Verify support case opening, assignment, notes and resolution."
    }
    if ($supportCase.status -ne "open" -or $supportCase.priority -ne "urgent") { throw "Support-case opening failed." }
    $platformCases = Invoke-Json -Method GET -Uri "$baseUri/api/v3/saas/platform/support-cases?organizationId=$($context.organizationId)&status=open" -Session $session
    if ($platformCases.count -ne 1) { throw "Platform support queue failed." }
    $inProgress = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/saas/platform/support-cases/$($supportCase.id)" -Session $session -Body @{
        status = "in_progress"; assignedToUserId = $login.user.id; resolution = ""; note = "Investigation started"; expectedVersion = $supportCase.version
    }
    if ($inProgress.status -ne "in_progress" -or $inProgress.version -ne 2) { throw "Support assignment failed." }
    $note = Invoke-Json -Method POST -Uri "$baseUri/api/v3/saas/platform/support-cases/$($supportCase.id)/notes" -Session $session -Body @{
        note = "Diagnostics completed successfully."
    }
    if ($note.eventType -ne "note_added") { throw "Support note creation failed." }
    $resolved = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/saas/platform/support-cases/$($supportCase.id)" -Session $session -Body @{
        status = "resolved"; assignedToUserId = $login.user.id; resolution = "Validated and resolved"; note = "Resolution confirmed"; expectedVersion = $inProgress.version
    }
    if ($resolved.status -ne "resolved" -or -not $resolved.resolvedAtUtc) { throw "Support-case resolution failed." }
    $supportEvents = Invoke-Json -Method GET -Uri "$baseUri/api/v3/saas/tenant/support-cases/$($supportCase.id)/events" -Session $session
    if ($supportEvents.count -ne 4) { throw "Immutable support-case timeline is incomplete." }

    $grant = Invoke-Json -Method POST -Uri "$baseUri/api/v3/saas/tenant/support-grants" -Session $session -Body @{
        operatorUserId = $login.user.id
        accessScope = "diagnostics"
        reason = "SaaS acceptance diagnostics"
        expiresAtUtc = [DateTimeOffset]::UtcNow.AddHours(2).ToString("O")
    }
    if ($grant.accessScope -ne "diagnostics" -or $grant.version -ne 1) { throw "Support access grant failed." }
    $grants = Invoke-Json -Method GET -Uri "$baseUri/api/v3/saas/tenant/support-grants" -Session $session
    if ($grants.count -ne 1) { throw "Support access grant listing failed." }
    $revokedGrant = Invoke-Json -Method POST -Uri "$baseUri/api/v3/saas/tenant/support-grants/$($grant.id)/revoke" -Session $session -Body @{
        expectedVersion = $grant.version
    }
    if (-not $revokedGrant.revokedAtUtc -or $revokedGrant.version -ne 2) { throw "Support access revocation failed." }

    $hardPlan = Invoke-Json -Method PUT -Uri "$baseUri/api/v3/saas/platform/plans/$($plan.id)" -Session $session -Body @{
        name = $plan.name
        description = $plan.description
        status = "active"
        billingInterval = $plan.billingInterval
        priceMinor = $plan.priceMinor
        currencyCode = $plan.currencyCode
        trialDays = $plan.trialDays
        enforcementMode = "hard"
        sortOrder = $plan.sortOrder
        expectedVersion = $plan.version
    }
    if ($hardPlan.enforcementMode -ne "hard" -or $hardPlan.version -ne 2) { throw "Hard limit activation failed." }

    $secondShop = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/shops" -Session $session -Body @{
        code = "S2"; name = "Second Shop"; address = "Kampala"; phone = ""; email = ""; taxNumber = ""; currencyCode = "UGX"; timezoneId = "Africa/Kampala"; isHeadOffice = $false
    }
    if ($secondShop.code -ne "S2") { throw "Second shop creation under the limit failed." }
    $thirdShop = Invoke-Api -Method POST -Uri "$baseUri/api/v3/admin/shops" -Session $session -ExpectedStatusCode 409 -Body @{
        code = "S3"; name = "Third Shop"; address = "Kampala"; phone = ""; email = ""; taxNumber = ""; currencyCode = "UGX"; timezoneId = "Africa/Kampala"; isHeadOffice = $false
    }
    if ($thirdShop.StatusCode -ne 409) { throw "Hard active-shop limit was not enforced." }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $session
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 16) { throw "Schema 16 backup verification failed." }
    $tenantHealth = Invoke-Json -Method POST -Uri "$baseUri/api/v3/saas/tenant/health-snapshots" -Session $session
    if ($tenantHealth.healthStatus -ne "healthy" -or $tenantHealth.schemaVersion -lt 16 -or -not $tenantHealth.lastBackupAtUtc) {
        throw "Tenant health snapshot is incorrect."
    }

    $dashboard = Invoke-Json -Method GET -Uri "$baseUri/api/v3/saas/platform/dashboard" -Session $session
    if ($dashboard.tenantCount -ne 2 -or $dashboard.activeSubscriptionCount -ne 2 -or $dashboard.openSupportCaseCount -ne 0 -or $dashboard.activeSupportGrantCount -ne 0 -or $dashboard.pendingBillingMinor -ne 250000) {
        throw "Platform SaaS dashboard totals are incorrect."
    }

    $journalAfter = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/journals?scope=shop&limit=500" -Session $session
    if ($journalAfter.count -ne $journalBaseline.count) {
        throw "SaaS operational records created unintended accounting journals."
    }

    Write-Host "SaaS tenant operations verification passed."
}
catch {
    Write-Host "SaaS gate failed: $($_.Exception.Message)"
    if (Test-Path $outputLog) { Write-Host "--- server output ---"; Get-Content $outputLog -Tail 300 }
    if (Test-Path $errorLog) { Write-Host "--- server errors ---"; Get-Content $errorLog -Tail 300 }
    throw
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        $serverProcess.WaitForExit(10000) | Out-Null
    }
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }
    if (Test-Path $temporaryRoot) {
        Remove-Item $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
