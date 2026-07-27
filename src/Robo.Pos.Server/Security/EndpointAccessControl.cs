namespace Robo.Pos.Server.Security;

public sealed record EndpointAccessDecision(
    AuthenticatedUser? User,
    IResult? Failure,
    string? SessionId = null)
{
    public bool IsAllowed =>
        User is not null &&
        Failure is null &&
        !string.IsNullOrWhiteSpace(SessionId);
}

public static class EndpointAccessControl
{
    public static async Task<EndpointAccessDecision>
        RequireUserAsync(
            HttpContext http,
            SessionService sessions,
            CancellationToken cancellationToken,
            bool allowPasswordChangeRequired = false)
    {
        string? sessionToken =
            http.Request.Cookies["robo_session"];

        SessionValidationResult session =
            await sessions.ValidateAsync(
                sessionToken,
                cancellationToken);

        if (session.Status !=
                SessionValidationStatus.Success ||
            session.User is null ||
            string.IsNullOrWhiteSpace(session.SessionId))
        {
            return new EndpointAccessDecision(
                null,
                Results.Json(
                    new
                    {
                        error = "authentication_required",
                        message =
                            "A valid login session is required."
                    },
                    statusCode:
                        StatusCodes.Status401Unauthorized));
        }

        if (session.User.MustChangePassword &&
            !allowPasswordChangeRequired)
        {
            return new EndpointAccessDecision(
                null,
                Results.Json(
                    new
                    {
                        error = "password_change_required",
                        message =
                            "Create a private password before using the POS."
                    },
                    statusCode:
                        StatusCodes.Status403Forbidden));
        }

        return new EndpointAccessDecision(
            session.User,
            null,
            session.SessionId);
    }

    public static async Task<EndpointAccessDecision>
        RequireAdminAsync(
            HttpContext http,
            SessionService sessions,
            CancellationToken cancellationToken)
    {
        EndpointAccessDecision access =
            await RequireUserAsync(
                http,
                sessions,
                cancellationToken);

        if (!access.IsAllowed)
        {
            return access;
        }

        if (!string.Equals(
                access.User!.Role,
                "admin",
                StringComparison.OrdinalIgnoreCase))
        {
            return new EndpointAccessDecision(
                null,
                Results.Json(
                    new
                    {
                        error = "administrator_required",
                        message =
                            "Only an administrator can perform this operation."
                    },
                    statusCode:
                        StatusCodes.Status403Forbidden));
        }

        return access;
    }
}
