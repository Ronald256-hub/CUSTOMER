namespace Robo.Pos.Server.Security;

public static class PasswordEndpoints
{
    public static void MapPasswordEndpoints(
        this WebApplication app)
    {
        app.MapPost(
            "/api/v3/auth/change-password",
            async Task<IResult> (
                ChangePasswordRequest request,
                HttpContext http,
                SessionService sessionService,
                PasswordChangeService passwordChangeService,
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
                    ClearSessionCookie(http);

                    return Results.Json(
                        new
                        {
                            error = "authentication_required",
                            message =
                                "A valid login session is required."
                        },
                        statusCode:
                            StatusCodes.Status401Unauthorized);
                }

                PasswordChangeResult result =
                    await passwordChangeService.ChangeAsync(
                        session.User,
                        request.CurrentPassword,
                        request.NewPassword,
                        cancellationToken);

                if (result.Status ==
                    PasswordChangeStatus.Success)
                {
                    ClearSessionCookie(http);

                    return Results.Ok(new
                    {
                        changed = true,
                        sessionsRevoked =
                            result.RevokedSessions,
                        loginRequired = true
                    });
                }

                int statusCode = result.Status switch
                {
                    PasswordChangeStatus.InvalidCurrentPassword =>
                        StatusCodes.Status400BadRequest,

                    PasswordChangeStatus.WeakPassword =>
                        StatusCodes.Status400BadRequest,

                    PasswordChangeStatus.SameAsCurrentPassword =>
                        StatusCodes.Status409Conflict,

                    PasswordChangeStatus.ChangeTooSoon =>
                        StatusCodes.Status409Conflict,

                    PasswordChangeStatus.Disabled =>
                        StatusCodes.Status403Forbidden,

                    PasswordChangeStatus.UserNotFound =>
                        StatusCodes.Status404NotFound,

                    _ =>
                        StatusCodes.Status400BadRequest
                };

                return Results.Json(
                    new
                    {
                        error = result.ErrorCode ??
                            "password_change_failed",
                        message = result.Message ??
                            "The password could not be changed."
                    },
                    statusCode: statusCode);
            });
    }

    private static void ClearSessionCookie(
        HttpContext http)
    {
        http.Response.Cookies.Delete(
            "robo_session",
            new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
    }
}
