using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Administration;

public static class AdminReferenceEndpoints
{
    public static void MapAdminReferenceEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/v3/admin/inventory/categories",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                DatabaseBootstrap database,
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

                await using var connection =
                    new SqliteConnection(database.ConnectionString);

                await connection.OpenAsync(cancellationToken);

                await using var command =
                    connection.CreateCommand();

                command.CommandText =
                """
                SELECT
                    id,
                    name,
                    description,
                    display_order,
                    is_active
                FROM categories
                ORDER BY
                    display_order,
                    name COLLATE NOCASE;
                """;

                var categories = new List<object>();

                await using var reader =
                    await command.ExecuteReaderAsync(
                        cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    categories.Add(new
                    {
                        id = reader.GetString(0),
                        name = reader.GetString(1),
                        description = reader.GetString(2),
                        displayOrder = reader.GetInt32(3),
                        isActive = reader.GetInt32(4) == 1
                    });
                }

                return Results.Ok(new
                {
                    categories,
                    count = categories.Count
                });
            });

        app.MapGet(
            "/api/v3/admin/users",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                DatabaseBootstrap database,
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

                await using var connection =
                    new SqliteConnection(database.ConnectionString);

                await connection.OpenAsync(cancellationToken);

                await using var command =
                    connection.CreateCommand();

                command.CommandText =
                """
                SELECT
                    id,
                    username,
                    display_name,
                    role,
                    is_active,
                    must_change_password,
                    failed_login_attempts,
                    locked_until_utc,
                    created_at_utc
                FROM users
                ORDER BY
                    CASE role
                        WHEN 'admin' THEN 0
                        ELSE 1
                    END,
                    display_name COLLATE NOCASE;
                """;

                var users = new List<object>();

                await using var reader =
                    await command.ExecuteReaderAsync(
                        cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    users.Add(new
                    {
                        id = reader.GetString(0),
                        username = reader.GetString(1),
                        displayName = reader.GetString(2),
                        role = reader.GetString(3),
                        isActive = reader.GetInt32(4) == 1,
                        mustChangePassword =
                            reader.GetInt32(5) == 1,
                        failedLoginAttempts =
                            reader.GetInt32(6),
                        lockedUntilUtc =
                            reader.IsDBNull(7)
                                ? null
                                : reader.GetString(7),
                        createdAtUtc =
                            reader.GetString(8)
                    });
                }

                return Results.Ok(new
                {
                    users,
                    count = users.Count
                });
            });

        app.MapGet(
            "/api/v3/admin/summary",
            async Task<IResult> (
                HttpContext http,
                SessionService sessions,
                DatabaseBootstrap database,
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

                await using var connection =
                    new SqliteConnection(database.ConnectionString);

                await connection.OpenAsync(cancellationToken);

                await using var command =
                    connection.CreateCommand();

                command.CommandText =
                """
                SELECT
                    (
                        SELECT COUNT(*)
                        FROM products
                        WHERE is_active = 1
                    ),
                    (
                        SELECT COUNT(*)
                        FROM products AS p
                        INNER JOIN stock_balances AS s
                            ON s.product_id = p.id
                        WHERE p.is_active = 1
                          AND
                          (
                              s.quantity_base_units
                              - s.reserved_base_units
                          ) <= p.low_stock_threshold
                    ),
                    (
                        SELECT COUNT(*)
                        FROM sales
                        WHERE status = 'completed'
                    ),
                    (
                        SELECT COALESCE(SUM(total_minor), 0)
                        FROM sales
                        WHERE status = 'completed'
                    ),
                    (
                        SELECT COUNT(*)
                        FROM users
                        WHERE is_active = 1
                    ),
                    (
                        SELECT COUNT(*)
                        FROM teller_shifts
                        WHERE status = 'open'
                    ),
                    (
                        SELECT COUNT(*)
                        FROM sale_documents
                    );
                """;

                await using var reader =
                    await command.ExecuteReaderAsync(
                        cancellationToken);

                await reader.ReadAsync(cancellationToken);

                return Results.Ok(new
                {
                    activeProducts = reader.GetInt32(0),
                    lowStockProducts = reader.GetInt32(1),
                    completedSales = reader.GetInt32(2),
                    totalSalesMinor = reader.GetInt64(3),
                    activeUsers = reader.GetInt32(4),
                    openShifts = reader.GetInt32(5),
                    savedDocuments = reader.GetInt32(6),
                    currencyCode = "UGX"
                });
            });
    }
}
