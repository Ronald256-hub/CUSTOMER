namespace Robo.Pos.Server.Security;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/v3/auth/me",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                CancellationToken cancellationToken) =>
            {
                SessionValidationResult result =
                    await sessions.ValidateAsync(
                        GetSessionToken(http),
                        cancellationToken);

                if (result.Status !=
                        SessionValidationStatus.Success ||
                    result.User is null)
                {
                    ClearSessionCookie(http);

                    return Unauthorized(result.Status);
                }

                return Results.Ok(new
                {
                    user = result.User,
                    expiresAtUtc = result.ExpiresAtUtc
                });
            });

        app.MapPost(
            "/api/v3/auth/logout",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                CancellationToken cancellationToken) =>
            {
                await sessions.RevokeAsync(
                    GetSessionToken(http),
                    cancellationToken);

                ClearSessionCookie(http);

                return Results.NoContent();
            });

        app.MapGet(
            "/api/v3/admin/access-check",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                CancellationToken cancellationToken) =>
            {
                SessionValidationResult result =
                    await sessions.ValidateAsync(
                        GetSessionToken(http),
                        cancellationToken);

                if (result.Status !=
                        SessionValidationStatus.Success ||
                    result.User is null)
                {
                    return Unauthorized(result.Status);
                }

                if (!string.Equals(
                        result.User.Role,
                        "admin",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(
                        new
                        {
                            error = "forbidden",
                            message =
                                "Administrator permission is required."
                        },
                        statusCode:
                            StatusCodes.Status403Forbidden);
                }

                return Results.Ok(new
                {
                    allowed = true,
                    permission = "administrator",
                    user = result.User
                });
            });

        app.MapGet(
            "/api/v3/teller/access-check",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                CancellationToken cancellationToken) =>
            {
                SessionValidationResult result =
                    await sessions.ValidateAsync(
                        GetSessionToken(http),
                        cancellationToken);

                if (result.Status !=
                        SessionValidationStatus.Success ||
                    result.User is null)
                {
                    return Unauthorized(result.Status);
                }

                bool allowed =
                    string.Equals(
                        result.User.Role,
                        "admin",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        result.User.Role,
                        "teller",
                        StringComparison.OrdinalIgnoreCase);

                if (!allowed)
                {
                    return Results.Json(
                        new
                        {
                            error = "forbidden",
                            message =
                                "Teller permission is required."
                        },
                        statusCode:
                            StatusCodes.Status403Forbidden);
                }

                return Results.Ok(new
                {
                    allowed = true,
                    permission = "teller",
                    user = result.User
                });
            });
    }

    private static string? GetSessionToken(
        HttpContext http)
    {
        return http.Request.Cookies["robo_session"];
    }

    private static IResult Unauthorized(
        SessionValidationStatus status)
    {
        return Results.Json(
            new
            {
                error = "authentication_required",
                message =
                    "A valid login session is required.",
                sessionStatus =
                    status.ToString().ToLowerInvariant()
            },
            statusCode:
                StatusCodes.Status401Unauthorized);
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
