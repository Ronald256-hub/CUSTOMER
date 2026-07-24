namespace Robo.Pos.Server.Security;

public static class AdminTellerResetEndpoints
{
    public static void MapAdminTellerResetEndpoints(
        this WebApplication app)
    {
        app.MapPost(
            "/api/v3/admin/users/{targetUserId}/reset-password",
            async Task<IResult> (
                string targetUserId,
                AdminResetTellerPasswordRequest request,
                HttpContext http,
                SessionService sessionService,
                AdminTellerResetService resetService,
                CancellationToken cancellationToken) =>
            {
                string? sessionToken =
                    http.Request.Cookies["robo_session"];

                SessionValidationResult session =
                    await sessionService.ValidateAsync(
                        sessionToken,
                        cancellationToken);

                if (session.Status !=
                        SessionValidationStatus.Success ||
                    session.User is null)
                {
                    return Results.Json(
                        new
                        {
                            error = "authentication_required",
                            message =
                                "A valid administrator login is required."
                        },
                        statusCode:
                            StatusCodes.Status401Unauthorized);
                }

                if (!string.Equals(
                        session.User.Role,
                        "admin",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(
                        new
                        {
                            error = "administrator_required",
                            message =
                                "Only Baron can reset teller passwords."
                        },
                        statusCode:
                            StatusCodes.Status403Forbidden);
                }

                AdminTellerResetResult result =
                    await resetService.ResetAsync(
                        session.User,
                        targetUserId,
                        request.AdministratorPassword,
                        request.Reason,
                        cancellationToken);

                if (result.Status ==
                    AdminTellerResetStatus.Success)
                {
                    return Results.Ok(new
                    {
                        reset = true,
                        targetUserId = result.TargetUserId,
                        username = result.Username,
                        displayName = result.DisplayName,
                        temporaryPassword =
                            result.TemporaryPassword,
                        mustChangePassword = true,
                        sessionsRevoked =
                            result.RevokedSessions,
                        resetAtUtc = result.ResetAtUtc,
                        warning =
                            "This temporary password is displayed only once."
                    });
                }

                int statusCode = result.Status switch
                {
                    AdminTellerResetStatus.AuthenticationRequired =>
                        StatusCodes.Status401Unauthorized,

                    AdminTellerResetStatus.AdministratorOnly =>
                        StatusCodes.Status403Forbidden,

                    AdminTellerResetStatus
                        .AdministratorPasswordChangeRequired =>
                        StatusCodes.Status403Forbidden,

                    AdminTellerResetStatus
                        .InvalidAdministratorPassword =>
                        StatusCodes.Status403Forbidden,

                    AdminTellerResetStatus.TargetUserNotFound =>
                        StatusCodes.Status404NotFound,

                    AdminTellerResetStatus.TargetDisabled =>
                        StatusCodes.Status409Conflict,

                    AdminTellerResetStatus.RateLimited =>
                        StatusCodes.Status429TooManyRequests,

                    AdminTellerResetStatus.CannotResetSelf =>
                        StatusCodes.Status400BadRequest,

                    AdminTellerResetStatus.TargetMustBeTeller =>
                        StatusCodes.Status400BadRequest,

                    AdminTellerResetStatus.ReasonRequired =>
                        StatusCodes.Status400BadRequest,

                    AdminTellerResetStatus.ReasonTooLong =>
                        StatusCodes.Status400BadRequest,

                    _ =>
                        StatusCodes.Status400BadRequest
                };

                return Results.Json(
                    new
                    {
                        error = result.ErrorCode ??
                            "teller_password_reset_failed",
                        message = result.Message ??
                            "The teller password could not be reset."
                    },
                    statusCode: statusCode);
            });
    }
}
