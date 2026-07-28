using Microsoft.AspNetCore.Mvc;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Hrm;

public static class HrmEndpoints
{
    public static void MapHrmEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/v3/hrm");

        group.MapGet(
            "/departments",
            async Task<IResult> (
                bool? includeInactive,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(new
                    {
                        departments = await hrm.ListDepartmentsAsync(
                            user, context, includeInactive ?? false, cancellationToken)
                    }),
                    cancellationToken));

        group.MapPost(
            "/departments",
            async Task<IResult> (
                [FromBody] CreateDepartmentRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/hrm/departments",
                        await hrm.CreateDepartmentAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        group.MapPut(
            "/departments/{departmentId}",
            async Task<IResult> (
                string departmentId,
                [FromBody] UpdateDepartmentRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.UpdateDepartmentAsync(
                            user, context, departmentId, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/positions",
            async Task<IResult> (
                bool? includeInactive,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(new
                    {
                        positions = await hrm.ListPositionsAsync(
                            user, context, includeInactive ?? false, cancellationToken)
                    }),
                    cancellationToken));

        group.MapPost(
            "/positions",
            async Task<IResult> (
                [FromBody] CreatePositionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/hrm/positions",
                        await hrm.CreatePositionAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/employees",
            async Task<IResult> (
                string? search,
                string? status,
                bool? includeAllShops,
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<EmployeeRecord> employees =
                            await hrm.ListEmployeesAsync(
                                user, context, search, status,
                                includeAllShops ?? false, limit ?? 500,
                                cancellationToken);
                        return Results.Ok(new { employees, count = employees.Count });
                    },
                    cancellationToken));

        group.MapGet(
            "/employees/{employeeId}",
            async Task<IResult> (
                string employeeId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.GetEmployeeAsync(user, context, employeeId, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/employees",
            async Task<IResult> (
                [FromBody] CreateEmployeeRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        EmployeeRecord employee = await hrm.CreateEmployeeAsync(
                            user, context, request, cancellationToken);
                        return Results.Created($"/api/v3/hrm/employees/{employee.Id}", employee);
                    },
                    cancellationToken));

        group.MapPut(
            "/employees/{employeeId}",
            async Task<IResult> (
                string employeeId,
                [FromBody] UpdateEmployeeRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.UpdateEmployeeAsync(
                            user, context, employeeId, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/schedules",
            async Task<IResult> (
                string? fromDate,
                string? toDate,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<WorkScheduleRecord> schedules =
                            await hrm.ListWorkSchedulesAsync(
                                user, context, fromDate, toDate, cancellationToken);
                        return Results.Ok(new { schedules, count = schedules.Count });
                    },
                    cancellationToken));

        group.MapPost(
            "/schedules",
            async Task<IResult> (
                [FromBody] CreateWorkScheduleRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/hrm/schedules",
                        await hrm.CreateWorkScheduleAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/schedules/{scheduleId}/publish",
            async Task<IResult> (
                string scheduleId,
                [FromBody] WorkScheduleActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.PublishWorkScheduleAsync(
                            user, context, scheduleId, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/attendance",
            async Task<IResult> (
                string? fromDate,
                string? toDate,
                string? employeeId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<AttendanceRecord> attendance =
                            await hrm.ListAttendanceAsync(
                                user, context, fromDate, toDate, employeeId,
                                cancellationToken);
                        return Results.Ok(new { attendance, count = attendance.Count });
                    },
                    cancellationToken));

        group.MapPost(
            "/attendance/clock-in",
            async Task<IResult> (
                [FromBody] ClockInRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/hrm/attendance",
                        await hrm.ClockInAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/attendance/{attendanceId}/clock-out",
            async Task<IResult> (
                string attendanceId,
                [FromBody] ClockOutRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.ClockOutAsync(
                            user, context, attendanceId, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/attendance/{attendanceId}/approve",
            async Task<IResult> (
                string attendanceId,
                [FromBody] AttendanceActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.ApproveAttendanceAsync(
                            user, context, attendanceId, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/leave-types",
            async Task<IResult> (
                bool? includeInactive,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(new
                    {
                        leaveTypes = await hrm.ListLeaveTypesAsync(
                            user, context, includeInactive ?? false, cancellationToken)
                    }),
                    cancellationToken));

        group.MapPost(
            "/leave-types",
            async Task<IResult> (
                [FromBody] CreateLeaveTypeRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/hrm/leave-types",
                        await hrm.CreateLeaveTypeAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/leave-requests",
            async Task<IResult> (
                string? status,
                string? employeeId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<LeaveRequestRecord> requests =
                            await hrm.ListLeaveRequestsAsync(
                                user, context, status, employeeId, cancellationToken);
                        return Results.Ok(new { leaveRequests = requests, count = requests.Count });
                    },
                    cancellationToken));

        group.MapPost(
            "/leave-requests",
            async Task<IResult> (
                [FromBody] CreateLeaveRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/hrm/leave-requests",
                        await hrm.CreateLeaveRequestAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/leave-requests/{requestId}/submit",
            async Task<IResult> (
                string requestId,
                [FromBody] LeaveActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.SubmitLeaveRequestAsync(
                            user, context, requestId, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/leave-requests/{requestId}/approve",
            async Task<IResult> (
                string requestId,
                [FromBody] LeaveActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.ApproveLeaveRequestAsync(
                            user, context, requestId, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/leave-requests/{requestId}/reject",
            async Task<IResult> (
                string requestId,
                [FromBody] LeaveActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.RejectLeaveRequestAsync(
                            user, context, requestId, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/payroll-periods",
            async Task<IResult> (
                int? limit,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) =>
                    {
                        IReadOnlyList<PayrollPeriodRecord> periods =
                            await hrm.ListPayrollPeriodsAsync(
                                user, context, limit ?? 100, cancellationToken);
                        return Results.Ok(new { payrollPeriods = periods, count = periods.Count });
                    },
                    cancellationToken));

        group.MapPost(
            "/payroll-periods",
            async Task<IResult> (
                [FromBody] CreatePayrollPeriodRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/hrm/payroll-periods",
                        await hrm.CreatePayrollPeriodAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/payroll-periods/{periodId}/calculate",
            async Task<IResult> (
                string periodId,
                [FromBody] CalculatePayrollRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.CalculatePayrollAsync(
                            user, context, periodId, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/payroll-periods/{periodId}/approve",
            async Task<IResult> (
                string periodId,
                [FromBody] PayrollActionRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.ApprovePayrollAsync(
                            user, context, periodId, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/performance-reviews",
            async Task<IResult> (
                string? employeeId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(new
                    {
                        reviews = await hrm.ListPerformanceReviewsAsync(
                            user, context, employeeId, cancellationToken)
                    }),
                    cancellationToken));

        group.MapPost(
            "/performance-reviews",
            async Task<IResult> (
                [FromBody] CreatePerformanceReviewRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/hrm/performance-reviews",
                        await hrm.CreatePerformanceReviewAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/performance-reviews/{reviewId}/complete",
            async Task<IResult> (
                string reviewId,
                [FromBody] CompletePerformanceReviewRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.CompletePerformanceReviewAsync(
                            user, context, reviewId, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/training-records",
            async Task<IResult> (
                string? employeeId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(new
                    {
                        trainingRecords = await hrm.ListTrainingRecordsAsync(
                            user, context, employeeId, cancellationToken)
                    }),
                    cancellationToken));

        group.MapPost(
            "/training-records",
            async Task<IResult> (
                [FromBody] CreateTrainingRecordRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/hrm/training-records",
                        await hrm.CreateTrainingRecordAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/training-records/{trainingId}/complete",
            async Task<IResult> (
                string trainingId,
                [FromBody] CompleteTrainingRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.CompleteTrainingRecordAsync(
                            user, context, trainingId, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/disciplinary-cases",
            async Task<IResult> (
                string? employeeId,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(new
                    {
                        disciplinaryCases = await hrm.ListDisciplinaryCasesAsync(
                            user, context, employeeId, cancellationToken)
                    }),
                    cancellationToken));

        group.MapPost(
            "/disciplinary-cases",
            async Task<IResult> (
                [FromBody] CreateDisciplinaryCaseRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Created(
                        "/api/v3/hrm/disciplinary-cases",
                        await hrm.CreateDisciplinaryCaseAsync(user, context, request, cancellationToken)),
                    cancellationToken));

        group.MapPost(
            "/disciplinary-cases/{caseId}/resolve",
            async Task<IResult> (
                string caseId,
                [FromBody] ResolveDisciplinaryCaseRequest request,
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.ResolveDisciplinaryCaseAsync(
                            user, context, caseId, request, cancellationToken)),
                    cancellationToken));

        group.MapGet(
            "/dashboard",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                ShopContextService contexts,
                HrmService hrm,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    http, sessions, contexts,
                    async (user, context) => Results.Ok(
                        await hrm.GetDashboardAsync(user, context, cancellationToken)),
                    cancellationToken));
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext http,
        SessionService sessions,
        ShopContextService contexts,
        Func<AuthenticatedUser, ActiveShopContextRecord, Task<IResult>> action,
        CancellationToken cancellationToken)
    {
        EndpointAccessDecision access = await EndpointAccessControl.RequireUserAsync(
            http,
            sessions,
            cancellationToken);
        if (!access.IsAllowed)
        {
            return access.Failure!;
        }
        try
        {
            ActiveShopContextRecord context = await contexts.GetOrCreateAsync(
                access.User!,
                access.SessionId!,
                cancellationToken);
            return await action(access.User!, context);
        }
        catch (ShopContextException exception)
        {
            return Results.Json(
                new { error = exception.ErrorCode, message = exception.Message },
                statusCode: exception.StatusCode);
        }
        catch (HrmException exception)
        {
            return Results.Json(
                new { error = exception.ErrorCode, message = exception.Message },
                statusCode: exception.StatusCode);
        }
    }
}
