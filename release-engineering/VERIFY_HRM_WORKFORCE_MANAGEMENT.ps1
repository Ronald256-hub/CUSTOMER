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

$temporaryRoot = Join-Path $env:TEMP ("nexus-hrm-" + [guid]::NewGuid().ToString("N"))
$runtimeRoot = Join-Path $temporaryRoot "runtime"
$dataRoot = Join-Path $temporaryRoot "data"
$documentRoot = Join-Path $temporaryRoot "documents"
$outputLog = Join-Path $temporaryRoot "server-output.log"
$errorLog = Join-Path $temporaryRoot "server-error.log"
$initialPassword = "Nexus!Hrm2026#Initial"
$privatePassword = "Nexus!Hrm2026#Private"
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
    [Environment]::SetEnvironmentVariable("NEXUS_ADMIN_DISPLAY_NAME", "HRM Gate Administrator", "Process")
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
    $minimumHrmVersion = [version]"5.9.0"
    $runningVersion = [version]$health.version
    if (-not $health -or -not $health.ok -or $health.schemaVersion -lt 15 -or $runningVersion -lt $minimumHrmVersion) {
        throw "Nexus did not start with version 5.9.0 or later and HRM schema version 15 or later."
    }

    $service = Invoke-Json -Method GET -Uri "$baseUri/api/v3/service"
    foreach ($capability in @(
        "organization-and-branch-workforce-master",
        "employee-login-account-linking",
        "department-and-position-management",
        "published-work-schedules",
        "audited-clock-in-and-clock-out",
        "attendance-approval-and-overtime",
        "leave-types-and-approval-workflow",
        "approved-leave-overlap-prevention",
        "payroll-period-calculation-and-approval",
        "employee-performance-reviews",
        "training-and-certification-records",
        "disciplinary-case-management",
        "workforce-dashboard-and-analytics"
    )) {
        if ($service.capabilities -notcontains $capability) {
            throw "Missing HRM capability: $capability"
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
    if ($context.shopCode -ne "MAIN") { throw "The HRM gate did not start in MAIN." }

    $journalBaseline = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/journals?scope=shop&limit=500" -Session $session

    $department = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/departments" -Session $session -Body @{
        code = "OPS"
        name = "Operations"
        description = "HRM workforce gate department"
    }
    if ($department.code -ne "OPS" -or $department.version -ne 1) {
        throw "Department creation failed."
    }

    $position = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/positions" -Session $session -Body @{
        departmentId = $department.id
        code = "TECH"
        title = "Senior Technician"
        description = "Technical workforce role"
        grade = "G5"
    }
    if ($position.departmentId -ne $department.id -or $position.code -ne "TECH") {
        throw "Position creation failed."
    }

    $today = (Get-Date).ToUniversalTime().Date
    $yesterday = $today.AddDays(-1)
    $tomorrow = $today.AddDays(1)
    $employee = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/employees" -Session $session -Body @{
        departmentId = $department.id
        positionId = $position.id
        userId = $null
        firstName = "Amina"
        lastName = "Nabirye"
        otherNames = "Grace"
        preferredName = "Amina"
        phone = "+256700555001"
        email = "amina.hrm@example.invalid"
        address = "Kampala"
        emergencyContactName = "Mariam Nabirye"
        emergencyContactPhone = "+256700555002"
        employmentType = "permanent"
        hireDate = $yesterday.AddMonths(-6).ToString("yyyy-MM-dd")
        endDate = $null
        status = "active"
        baseSalaryMinor = 1000000
        payFrequency = "monthly"
        standardHoursPerWeek = 45
        taxNumber = "HRM-TIN-001"
        nationalId = "HRM-NIN-001"
        bankName = "Test Bank"
        bankAccount = "000123456789"
        mobileMoneyNumber = "+256700555001"
        notes = "Automated HRM lifecycle employee"
        shopIds = @($context.shopId)
    }
    if ($employee.employeeNumber -notlike "EMP-*" -or $employee.homeShopId -ne $context.shopId -or $employee.shopAssignments.Count -ne 1) {
        throw "Employee creation or home-branch assignment failed."
    }

    $employees = Invoke-Json -Method GET -Uri "$baseUri/api/v3/hrm/employees?search=Amina&includeAllShops=true" -Session $session
    if ($employees.count -ne 1 -or $employees.employees[0].id -ne $employee.id) {
        throw "Employee search failed."
    }

    $schedule = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/schedules" -Session $session -Body @{
        employeeId = $employee.id
        workDate = $tomorrow.ToString("yyyy-MM-dd")
        startTime = "08:00"
        endTime = "17:00"
        breakMinutes = 60
        notes = "Published HRM gate schedule"
    }
    $publishedSchedule = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/schedules/$($schedule.id)/publish" -Session $session -Body @{
        expectedVersion = $schedule.version
    }
    if ($publishedSchedule.status -ne "published" -or $publishedSchedule.version -ne 2) {
        throw "Work schedule publication failed."
    }

    $clockInUtc = [DateTimeOffset]::new($yesterday.Year, $yesterday.Month, $yesterday.Day, 8, 0, 0, [TimeSpan]::Zero)
    $clockOutUtc = $clockInUtc.AddHours(11)
    $attendance = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/attendance/clock-in" -Session $session -Body @{
        employeeId = $employee.id
        clockInUtc = $clockInUtc.ToString("O")
        source = "manual"
        notes = "HRM gate clock in"
    }
    $duplicateClockIn = Invoke-Api -Method POST -Uri "$baseUri/api/v3/hrm/attendance/clock-in" -Session $session -ExpectedStatusCode 409 -Body @{
        employeeId = $employee.id
        clockInUtc = $clockInUtc.AddMinutes(1).ToString("O")
        source = "manual"
        notes = "Must fail"
    }
    if (($duplicateClockIn.Data.error ?? "") -ne "attendance_already_open") {
        throw "Duplicate open attendance was not rejected."
    }

    $completedAttendance = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/attendance/$($attendance.id)/clock-out" -Session $session -Body @{
        expectedVersion = $attendance.version
        clockOutUtc = $clockOutUtc.ToString("O")
        breakMinutes = 60
        notes = "HRM gate clock out"
    }
    if ($completedAttendance.status -ne "completed" -or $completedAttendance.workedMinutes -ne 600 -or $completedAttendance.overtimeMinutes -ne 60) {
        throw "Attendance duration or overtime calculation is incorrect."
    }
    $approvedAttendance = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/attendance/$($attendance.id)/approve" -Session $session -Body @{
        expectedVersion = $completedAttendance.version
        notes = "Attendance verified"
    }
    if ($approvedAttendance.status -ne "approved" -or -not $approvedAttendance.approvedAtUtc) {
        throw "Attendance approval failed."
    }

    $leaveType = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/leave-types" -Session $session -Body @{
        code = "ANNUAL"
        name = "Annual Leave"
        annualEntitlementDays = 21
        isPaid = $true
        requiresAttachment = $false
    }
    $leaveStart = $today.AddDays(10)
    $leaveEnd = $leaveStart.AddDays(2)
    $leave = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/leave-requests" -Session $session -Body @{
        employeeId = $employee.id
        leaveTypeId = $leaveType.id
        startDate = $leaveStart.ToString("yyyy-MM-dd")
        endDate = $leaveEnd.ToString("yyyy-MM-dd")
        requestedDays = 3
        reason = "Annual rest"
        attachmentReference = ""
    }
    $submittedLeave = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/leave-requests/$($leave.id)/submit" -Session $session -Body @{
        expectedVersion = $leave.version
        decisionNotes = ""
    }
    $approvedLeave = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/leave-requests/$($leave.id)/approve" -Session $session -Body @{
        expectedVersion = $submittedLeave.version
        decisionNotes = "Approved by HRM gate"
    }
    if ($approvedLeave.status -ne "approved" -or $approvedLeave.requestedDays -ne 3) {
        throw "Leave approval failed."
    }

    $overlap = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/leave-requests" -Session $session -Body @{
        employeeId = $employee.id
        leaveTypeId = $leaveType.id
        startDate = $leaveStart.AddDays(1).ToString("yyyy-MM-dd")
        endDate = $leaveEnd.AddDays(1).ToString("yyyy-MM-dd")
        requestedDays = 3
        reason = "Overlapping leave"
        attachmentReference = ""
    }
    $submittedOverlap = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/leave-requests/$($overlap.id)/submit" -Session $session -Body @{
        expectedVersion = $overlap.version
        decisionNotes = ""
    }
    $overlapApproval = Invoke-Api -Method POST -Uri "$baseUri/api/v3/hrm/leave-requests/$($overlap.id)/approve" -Session $session -ExpectedStatusCode 409 -Body @{
        expectedVersion = $submittedOverlap.version
        decisionNotes = "Must fail"
    }
    if (($overlapApproval.Data.error ?? "") -ne "leave_overlap") {
        throw "Overlapping approved leave was not rejected."
    }

    $payroll = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/payroll-periods" -Session $session -Body @{
        name = "HRM Gate Payroll"
        startDate = $yesterday.ToString("yyyy-MM-dd")
        endDate = $yesterday.ToString("yyyy-MM-dd")
        payDate = $today.ToString("yyyy-MM-dd")
    }
    $calculatedPayroll = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/payroll-periods/$($payroll.id)/calculate" -Session $session -Body @{
        expectedVersion = $payroll.version
        defaultAllowanceMinor = 100000
        defaultDeductionMinor = 50000
        overtimeRateMinorPerHour = 12000
    }
    if ($calculatedPayroll.status -ne "calculated" -or $calculatedPayroll.employeeCount -ne 1 -or $calculatedPayroll.grossPayMinor -ne 1112000 -or $calculatedPayroll.netPayMinor -ne 1062000) {
        throw "Payroll calculation totals are incorrect."
    }
    if ($calculatedPayroll.entries[0].workedMinutes -ne 600 -or $calculatedPayroll.entries[0].overtimePayMinor -ne 12000) {
        throw "Payroll attendance or overtime details are incorrect."
    }
    $approvedPayroll = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/payroll-periods/$($payroll.id)/approve" -Session $session -Body @{
        expectedVersion = $calculatedPayroll.version
    }
    if ($approvedPayroll.status -ne "approved" -or -not $approvedPayroll.approvedAtUtc) {
        throw "Payroll approval failed."
    }

    $review = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/performance-reviews" -Session $session -Body @{
        employeeId = $employee.id
        reviewPeriodStart = $yesterday.AddMonths(-3).ToString("yyyy-MM-dd")
        reviewPeriodEnd = $yesterday.ToString("yyyy-MM-dd")
        goals = "Improve diagnostic turnaround"
        achievements = ""
        improvementAreas = "Documentation"
    }
    $completedReview = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/performance-reviews/$($review.id)/complete" -Session $session -Body @{
        expectedVersion = $review.version
        overallRating = 4
        achievements = "Reduced diagnostic turnaround and improved quality"
        improvementAreas = "Continue improving documentation"
    }
    if ($completedReview.status -ne "completed" -or $completedReview.overallRating -ne 4) {
        throw "Performance review completion failed."
    }

    $training = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/training-records" -Session $session -Body @{
        employeeId = $employee.id
        title = "BS6 Diagnostics"
        provider = "Nexus Training Centre"
        startDate = $yesterday.ToString("yyyy-MM-dd")
        endDate = $today.ToString("yyyy-MM-dd")
        expiryDate = $today.AddDays(60).ToString("yyyy-MM-dd")
        costMinor = 250000
        notes = "Technical certification"
    }
    $completedTraining = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/training-records/$($training.id)/complete" -Session $session -Body @{
        expectedVersion = $training.version
        status = "completed"
        certificateReference = "CERT-HRM-001"
        notes = "Successfully completed"
    }
    if ($completedTraining.status -ne "completed" -or $completedTraining.certificateReference -ne "CERT-HRM-001") {
        throw "Training completion failed."
    }

    $disciplinary = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/disciplinary-cases" -Session $session -Body @{
        employeeId = $employee.id
        incidentDate = $yesterday.ToString("yyyy-MM-dd")
        category = "Procedure"
        severity = "minor"
        description = "Test case for controlled HRM resolution"
    }
    $resolvedDisciplinary = Invoke-Json -Method POST -Uri "$baseUri/api/v3/hrm/disciplinary-cases/$($disciplinary.id)/resolve" -Session $session -Body @{
        expectedVersion = $disciplinary.version
        status = "resolved"
        actionTaken = "Coaching completed and procedure reviewed"
    }
    if ($resolvedDisciplinary.status -ne "resolved" -or -not $resolvedDisciplinary.resolvedAtUtc) {
        throw "Disciplinary case resolution failed."
    }

    $employeeAfter = Invoke-Json -Method GET -Uri "$baseUri/api/v3/hrm/employees/$($employee.id)" -Session $session
    if ($employeeAfter.attendanceDayCount -ne 1 -or $employeeAfter.workedMinutes -ne 600 -or $employeeAfter.overtimeMinutes -ne 60 -or $employeeAfter.approvedLeaveDays -ne 3 -or $employeeAfter.pendingLeaveRequests -ne 1) {
        throw "Employee workforce metrics are incorrect."
    }

    $dashboard = Invoke-Json -Method GET -Uri "$baseUri/api/v3/hrm/dashboard" -Session $session
    if ($dashboard.activeEmployeeCount -ne 1 -or $dashboard.todayAttendanceCount -ne 0 -or $dashboard.publishedScheduleCountNext7Days -ne 1 -or $dashboard.openDisciplinaryCaseCount -ne 0 -or $dashboard.expiringTrainingCount90Days -ne 1 -or $dashboard.latestPayrollNetMinor -ne 1062000) {
        throw "The HRM dashboard is not reconcilable to the lifecycle."
    }

    $journalAfter = Invoke-Json -Method GET -Uri "$baseUri/api/v3/accounting/journals?scope=shop&limit=500" -Session $session
    if ($journalAfter.count -ne $journalBaseline.count) {
        throw "HRM payroll foundations created unintended accounting journals."
    }

    $backup = Invoke-Json -Method POST -Uri "$baseUri/api/v3/admin/backups" -Session $session -Body @{}
    if (-not $backup.integrityOk -or $backup.schemaVersion -lt 15) {
        throw "Backup integrity or schema-version-15 verification failed."
    }

    Write-Host "Nexus POS HRM and workforce management gate: PASS"
    Write-Host "Validated employee records, schedules, attendance, overtime, leave, overlap controls, payroll, performance, training, discipline, analytics, accounting isolation and backup integrity."
}
catch {
    Write-Host "Nexus POS HRM and workforce management gate: FAIL - $($_.Exception.Message)" -ForegroundColor Red
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
