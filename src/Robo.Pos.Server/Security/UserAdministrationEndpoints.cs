namespace Robo.Pos.Server.Security;

public static class UserAdministrationEndpoints
{
    public static void MapUserAdministrationEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/v3/admin/users",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                UserAdministrationService service,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireAdminAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                IReadOnlyList<UserAdministrationRecord> users =
                    await service.ListAsync(cancellationToken);

                return Results.Ok(new
                {
                    users,
                    count = users.Count
                });
            });

        app.MapPost(
            "/api/v3/admin/users",
            async Task<IResult> (
                CreateUserRequest request,
                HttpContext http,
                SessionService sessions,
                UserAdministrationService service,
                CancellationToken cancellationToken) =>
            {
                EndpointAccessDecision access =
                    await EndpointAccessControl.RequireAdminAsync(
                        http,
                        sessions,
                        cancellationToken);

                if (!access.IsAllowed)
                {
                    return access.Failure!;
                }

                try
                {
                    CreateUserResult result =
                        await service.CreateAsync(
                            access.User!,
                            request,
                            cancellationToken);

                    return Results.Created(
                        $"/api/v3/admin/users/{result.User.Id}",
                        new
                        {
                            user = result.User,
                            temporaryPassword = result.TemporaryPassword,
                            warning =
                                "The temporary password is displayed only once and must be changed at first login."
                        });
                }
                catch (UserAdministrationException exception)
                {
                    return Results.Json(
                        new
                        {
                            error = exception.ErrorCode,
                            message = exception.Message
                        },
                        statusCode: exception.StatusCode);
                }
            });
    }
}
