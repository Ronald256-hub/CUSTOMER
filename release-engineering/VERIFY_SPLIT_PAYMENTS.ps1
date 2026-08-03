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
        ErrorAction = 'Stop'
        SkipHttpErrorCheck = $true
    }
    if ($Session) { $parameters.WebSession = $Session }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 30 -Compress
    }
    $response = Invoke-WebRequest @parameters
    $statusCode = [int]$response.StatusCode
    $content = [string]$response.Content
    if ($ExpectedStatusCode -gt 0 -and $statusCode -ne $ExpectedStatusCode) {
        throw "Expected HTTP $ExpectedStatusCode but received $statusCode from $Method $Uri. Body: $content"
    }
    if ($ExpectedStatusCode -eq 0 -and $statusCode -ge 400) {
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
    return [pscustomobject]@{ StatusCode = $statusCode; Data = $data; Content = $content }
}

function Invoke-Json {
    param([string]$Method, [string]$Uri, $Session, $Body)
    return (Invoke-Api -Method $Method -Uri $Uri -Session $Session -Body $Body).Data
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

if ([string]::IsNullOrWhiteSpace($PortableZip)) {
    $zip = Get-ChildItem (Join-Path $PSScriptRoot '..\release') -Filter 'Nexus_POS_*_Portable.zip' -File | Select-Object -First 1
    if (-not $zip) { throw 'The portable Nexus POS release ZIP was not found.' }
    $PortableZip = $zip.FullName
}
$PortableZip = [System.IO.Path]::GetFullPath($PortableZip)
if (-not (Test-Path -LiteralPath $PortableZip -PathType Leaf)) {
    throw "Portable release ZIP does not exist: $PortableZip"
}

$temp = Join-Path $env:TEMP ("nexus-split-payments-" + [guid]::NewGuid().ToString('N'))
$runtime = Join-Path $temp 'runtime'
$data = Join-Path $temp 'data'
$documents = Join-Path $temp 'documents'
$outputLog = Join-Path $temp 'server-output.log'
$errorLog = Join-Path $temp 'server-error.log'
$initialPassword = 'Nexus!Split2026#Initial'
$privatePassword = 'Nexus!Split2026#Private'
$instanceId = [guid]::NewGuid().ToString('N')
$server = $null
$names = @(
    'NEXUS_DATA_DIR','ROBO_DATA_DIR','NEXUS_DOCUMENT_ROOT','ROBO_DOCUMENT_ROOT',
    'NEXUS_ADMIN_USERNAME','NEXUS_ADMIN_DISPLAY_NAME','NEXUS_ADMIN_INITIAL_PASSWORD',
    'ROBO_ADMIN_INITIAL_PASSWORD','NEXUS_INSTANCE_ID','ASPNETCORE_ENVIRONMENT','AllowedHosts'
)
$previous = @{}

try {
    New-Item -ItemType Directory -Force -Path $runtime,$data,$documents | Out-Null
    Expand-Archive -LiteralPath $PortableZip -DestinationPath $runtime -Force
    $exe = Get-ChildItem $runtime -Recurse -Filter 'Robo.Pos.Server.exe' -File | Select-Object -First 1
    if (-not $exe) { throw 'Robo.Pos.Server.exe was not found.' }

    foreach ($name in $names) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    [Environment]::SetEnvironmentVariable('NEXUS_DATA_DIR',$data,'Process')
    [Environment]::SetEnvironmentVariable('ROBO_DATA_DIR',$data,'Process')
    [Environment]::SetEnvironmentVariable('NEXUS_DOCUMENT_ROOT',$documents,'Process')
    [Environment]::SetEnvironmentVariable('ROBO_DOCUMENT_ROOT',$documents,'Process')
    [Environment]::SetEnvironmentVariable('NEXUS_ADMIN_USERNAME','admin','Process')
    [Environment]::SetEnvironmentVariable('NEXUS_ADMIN_DISPLAY_NAME','Split Payment Gate Administrator','Process')
    [Environment]::SetEnvironmentVariable('NEXUS_ADMIN_INITIAL_PASSWORD',$initialPassword,'Process')
    [Environment]::SetEnvironmentVariable('ROBO_ADMIN_INITIAL_PASSWORD',$initialPassword,'Process')
    [Environment]::SetEnvironmentVariable('NEXUS_INSTANCE_ID',$instanceId,'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT','Production','Process')
    [Environment]::SetEnvironmentVariable('AllowedHosts','localhost;127.0.0.1;[::1]','Process')

    $port = Get-FreePort
    $baseUri = "http://127.0.0.1:$port"
    $server = Start-Process -FilePath $exe.FullName -ArgumentList "--urls `"$baseUri`"" `
        -WorkingDirectory $exe.Directory.FullName -WindowStyle Hidden `
        -RedirectStandardOutput $outputLog -RedirectStandardError $errorLog -PassThru

    $health = $null
    for ($attempt=0; $attempt -lt 360; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($server.HasExited) { throw "Server exited with code $($server.ExitCode)." }
        try {
            $health = Invoke-Json GET "$baseUri/api/v3/health"
            if ($health.ok -and $health.instanceId -eq $instanceId) { break }
        }
        catch { }
    }
    if (-not $health -or -not $health.ok -or $health.schemaVersion -ne 19) {
        throw 'Nexus did not start on preserved schema 19.'
    }

    $service = Invoke-Json GET "$baseUri/api/v3/service"
    if ($service.version -ne '7.0.0') { throw 'Service version is not 7.0.0.' }
    foreach ($capability in @(
        'split-and-partial-payments','cash-change-netting','payment-reference-audit',
        'multi-tender-receipt-breakdown','manager-shift-reconciliation'
    )) {
        if ($service.capabilities -notcontains $capability) { throw "Missing capability: $capability" }
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json POST "$baseUri/api/v3/auth/login" $session @{
        username='admin'; password=$initialPassword
    }
    if (-not $login.user.mustChangePassword) { throw 'Initial password change was not required.' }
    $changed = Invoke-Json POST "$baseUri/api/v3/auth/change-password" $session @{
        currentPassword=$initialPassword; newPassword=$privatePassword
    }
    if (-not $changed.changed) { throw 'Password change failed.' }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Json POST "$baseUri/api/v3/auth/login" $session @{
        username='admin'; password=$privatePassword
    }
    if ($login.user.role -ne 'admin') { throw 'Administrator login failed.' }

    $category = Invoke-Json POST "$baseUri/api/v3/admin/inventory/categories" $session @{
        name='Split Payment Gate'; description='Automated multi-tender qualification'; displayOrder=1
    }
    $product = Invoke-Json POST "$baseUri/api/v3/admin/inventory/products" $session @{
        categoryId=$category.id
        sku='SPLIT-GATE-001'
        barcode='995000000070'
        name='Split Payment Gate Product'
        description='Used by Nexus POS 7.0 qualification'
        productType='standard'
        stockUnit='unit'
        saleUnit='unit'
        bottleVolumeMl=$null
        glassSizeMl=$null
        unitsPerCrate=$null
        costPriceMinor=4000
        sellingPriceMinor=10000
        lowStockThreshold=1
        openingStockBaseUnits=10
        allowNegativeStock=$false
        trackExpiry=$false
    }
    $shift = Invoke-Json POST "$baseUri/api/v3/shifts/open" $session @{ openingCashMinor=20000 }
    if ($shift.status -ne 'open') { throw 'Qualification shift did not open.' }

    $sale = Invoke-Json POST "$baseUri/api/v3/sales" $session @{
        items=@(@{ productId=$product.id; quantity=1 })
        paymentMethod='cash'
        amountReceivedMinor=11000
        payments=@(
            @{ paymentMethod='cash'; amountMinor=6000; reference='CASH-TENDER-001' },
            @{ paymentMethod='mobile_money'; amountMinor=5000; reference='MOMO-REF-7001' }
        )
        issueInvoice=$false
        customerName='Split Payment Customer'
        notes='Nexus POS 7.0 gate'
    }
    if ($sale.totalMinor -ne 10000 -or $sale.amountReceivedMinor -ne 11000 -or
        $sale.changeMinor -ne 1000 -or $sale.paymentMethod -ne 'split') {
        throw 'Split sale totals, tender or change are incorrect.'
    }
    $salePayments = @($sale.payments)
    if ($salePayments.Count -ne 2) { throw 'Split sale did not return two payment rows.' }
    $cash = $salePayments | Where-Object paymentMethod -eq 'cash'
    $momo = $salePayments | Where-Object paymentMethod -eq 'mobile_money'
    if ($cash.amountMinor -ne 5000 -or $momo.amountMinor -ne 5000 -or
        $momo.reference -ne 'MOMO-REF-7001') {
        throw 'Cash change was not netted or payment references were not retained.'
    }

    $receipt = Invoke-Json GET "$baseUri/api/v3/receipts/$($sale.saleId)" $session
    if ($receipt.paymentMethod -ne 'split' -or @($receipt.payments).Count -ne 2) {
        throw 'Receipt API does not expose the exact split-payment breakdown.'
    }
    $htmlDocument = @($receipt.documents | Where-Object fileFormat -eq 'html' | Select-Object -First 1)
    if ($htmlDocument.Count -ne 1) { throw 'Receipt HTML document was not generated.' }
    $htmlResponse = Invoke-Api GET "$baseUri/api/v3/receipts/$($sale.saleId)/documents/$($htmlDocument[0].id)" $session
    if ($htmlResponse.Content -notmatch 'Payment breakdown' -or
        $htmlResponse.Content -notmatch 'MOMO-REF-7001') {
        throw 'Immutable receipt document does not include the tender breakdown.'
    }

    $drawer = Invoke-Json GET "$baseUri/api/v3/cash-drawer/current" $session
    if ($drawer.cashSalesMinor -ne 5000 -or $drawer.expectedDrawerCashMinor -ne 25000) {
        throw 'Cash drawer did not use the net cash applied after change.'
    }

    $under = Invoke-Api POST "$baseUri/api/v3/sales" $session @{
        items=@(@{ productId=$product.id; quantity=1 })
        paymentMethod='cash'; amountReceivedMinor=9000
        payments=@(
            @{ paymentMethod='cash'; amountMinor=4000 },
            @{ paymentMethod='card'; amountMinor=5000 }
        )
    } 400
    if ($under.Data.error -ne 'insufficient_payment') { throw 'Combined underpayment guard failed.' }

    $duplicate = Invoke-Api POST "$baseUri/api/v3/sales" $session @{
        items=@(@{ productId=$product.id; quantity=1 })
        paymentMethod='cash'; amountReceivedMinor=10000
        payments=@(
            @{ paymentMethod='cash'; amountMinor=5000 },
            @{ paymentMethod='cash'; amountMinor=5000 }
        )
    } 400
    if ($duplicate.Data.error -ne 'duplicate_payment_method') { throw 'Duplicate method guard failed.' }

    $over = Invoke-Api POST "$baseUri/api/v3/sales" $session @{
        items=@(@{ productId=$product.id; quantity=1 })
        paymentMethod='card'; amountReceivedMinor=11000
        payments=@(
            @{ paymentMethod='card'; amountMinor=6000 },
            @{ paymentMethod='bank'; amountMinor=5000 }
        )
    } 400
    if ($over.Data.error -ne 'non_cash_overpayment') { throw 'Non-cash overpayment guard failed.' }

    $mixedCredit = Invoke-Api POST "$baseUri/api/v3/sales" $session @{
        items=@(@{ productId=$product.id; quantity=1 })
        paymentMethod='credit'; amountReceivedMinor=10000
        payments=@(
            @{ paymentMethod='credit'; amountMinor=5000 },
            @{ paymentMethod='cash'; amountMinor=5000 }
        )
    } 400
    if ($mixedCredit.Data.error -ne 'mixed_credit_payment_not_supported') {
        throw 'Mixed credit tender guard failed.'
    }

    $eligible = Invoke-Json GET "$baseUri/api/v3/sales/returns/eligible?limit=100" $session
    if (@($eligible.sales | Where-Object saleId -eq $sale.saleId).Count -ne 0) {
        throw 'Split sale leaked into the single-channel return queue.'
    }

    $legacy = Invoke-Json POST "$baseUri/api/v3/sales" $session @{
        items=@(@{ productId=$product.id; quantity=1 })
        paymentMethod='bank'
        amountReceivedMinor=10000
        issueInvoice=$false
        customerName='Legacy Payment Customer'
    }
    if ($legacy.paymentMethod -ne 'bank' -or @($legacy.payments).Count -ne 1) {
        throw 'Legacy single-payment request compatibility failed.'
    }

    $journals = Invoke-Json GET "$baseUri/api/v3/accounting/journals?scope=shop&limit=200" $session
    $splitJournal = @($journals.journals | Where-Object sourceId -eq "sale:$($sale.saleId)")
    if ($splitJournal.Count -ne 1 -or
        $splitJournal[0].totalDebitMinor -ne $splitJournal[0].totalCreditMinor) {
        throw 'Split sale did not create exactly one balanced accounting journal.'
    }

    $voided = Invoke-Json POST "$baseUri/api/v3/admin/sales/$($sale.saleId)/void" $session @{
        reason='Qualification void after split-payment verification'
    }
    if ($voided.status -ne 'voided') { throw 'Split sale void did not complete.' }

    Write-Host 'Nexus POS 7.0 live split-payment qualification passed.'
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($name in $names) {
        [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')
    }
    if (Test-Path $temp) {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
