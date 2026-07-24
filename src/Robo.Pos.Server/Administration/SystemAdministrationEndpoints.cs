using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Administration;

public static class SystemAdministrationEndpoints
{
    public static void MapSystemAdministrationEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/v3/admin/settings",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                SystemAdministrationService service,
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
                    return Results.Ok(
                        await service.GetSettingsAsync(
                            cancellationToken));
                }
                catch (SystemAdministrationException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPut(
            "/api/v3/admin/settings",
            async Task<IResult> (
                UpdateBusinessSettingsRequest request,
                HttpContext http,
                SessionService sessions,
                SystemAdministrationService service,
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
                    return Results.Ok(
                        await service.UpdateSettingsAsync(
                            access.User!,
                            request,
                            cancellationToken));
                }
                catch (SystemAdministrationException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/admin/backups",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                SystemAdministrationService service,
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
                    IReadOnlyList<BackupVerificationResult> backups =
                        await service.ListBackupsAsync(
                            cancellationToken);

                    return Results.Ok(new
                    {
                        backups,
                        count = backups.Count
                    });
                }
                catch (SystemAdministrationException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/admin/backups",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                SystemAdministrationService service,
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
                    BackupVerificationResult backup =
                        await service.CreateBackupAsync(
                            access.User!,
                            cancellationToken);

                    return Results.Created(
                        $"/api/v3/admin/backups/{backup.FileName}",
                        backup);
                }
                catch (SystemAdministrationException exception)
                {
                    return Error(exception);
                }
            });

        app.MapPost(
            "/api/v3/admin/backups/{fileName}/verify",
            async Task<IResult> (
                string fileName,
                HttpContext http,
                SessionService sessions,
                SystemAdministrationService service,
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
                    return Results.Ok(
                        await service.VerifyBackupAsync(
                            access.User!,
                            fileName,
                            cancellationToken));
                }
                catch (SystemAdministrationException exception)
                {
                    return Error(exception);
                }
            });

        app.MapGet(
            "/api/v3/admin/backups/{fileName}/download",
            async Task<IResult> (
                string fileName,
                HttpContext http,
                SessionService sessions,
                SystemAdministrationService service,
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
                    BackupDownloadResult backup =
                        await service.PrepareDownloadAsync(
                            access.User!,
                            fileName,
                            cancellationToken);

                    return Results.File(
                        backup.FullPath,
                        "application/vnd.sqlite3",
                        backup.FileName,
                        enableRangeProcessing: true);
                }
                catch (SystemAdministrationException exception)
                {
                    return Error(exception);
                }
            });
    }

    private static IResult Error(
        SystemAdministrationException exception)
    {
        return Results.Json(
            new
            {
                error = exception.ErrorCode,
                message = exception.Message
            },
            statusCode: exception.StatusCode);
    }
}
