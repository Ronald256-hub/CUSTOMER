using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;

namespace Robo.Pos.Server.Security;

public sealed class UserAdministrationService
{
    private readonly DatabaseBootstrap _database;
    private readonly IPasswordHasher<PosUser> _passwordHasher;

    public UserAdministrationService(
        DatabaseBootstrap database,
        IPasswordHasher<PosUser> passwordHasher)
    {
        _database = database;
        _passwordHasher = passwordHasher;
    }

    public async Task<IReadOnlyList<UserAdministrationRecord>>
        ListAsync(
            CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            id,
            username,
            display_name,
            role,
            must_change_password,
            is_active,
            created_at_utc,
            updated_at_utc
        FROM users
        ORDER BY
            CASE role WHEN 'admin' THEN 0 ELSE 1 END,
            display_name COLLATE NOCASE,
            username COLLATE NOCASE;
        """;

        var results = new List<UserAdministrationRecord>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UserAdministrationRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5) == 1,
                DateTimeOffset.Parse(reader.GetString(6)),
                DateTimeOffset.Parse(reader.GetString(7))));
        }

        return results;
    }

    public async Task<CreateUserResult> CreateAsync(
        AuthenticatedUser administrator,
        CreateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        string username = NormalizeUsername(request.Username);
        string displayName = NormalizeDisplayName(request.DisplayName);
        string role = NormalizeRole(request.Role);
        string temporaryPassword = CreateTemporaryPassword();
        string userId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var passwordUser = new PosUser(
            userId,
            username,
            displayName,
            role);

        string passwordHash =
            _passwordHasher.HashPassword(
                passwordUser,
                temporaryPassword);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                """
                INSERT INTO users
                (
                    id,
                    username,
                    username_normalized,
                    display_name,
                    role,
                    password_hash,
                    must_change_password,
                    failed_login_attempts,
                    locked_until_utc,
                    is_active,
                    password_changed_utc,
                    created_at_utc,
                    updated_at_utc
                )
                VALUES
                (
                    $id,
                    $username,
                    $usernameNormalized,
                    $displayName,
                    $role,
                    $passwordHash,
                    1,
                    0,
                    NULL,
                    1,
                    NULL,
                    $now,
                    $now
                );
                """;

                command.Parameters.AddWithValue("$id", userId);
                command.Parameters.AddWithValue("$username", username);
                command.Parameters.AddWithValue(
                    "$usernameNormalized",
                    username.ToUpperInvariant());
                command.Parameters.AddWithValue(
                    "$displayName",
                    displayName);
                command.Parameters.AddWithValue("$role", role);
                command.Parameters.AddWithValue(
                    "$passwordHash",
                    passwordHash);
                command.Parameters.AddWithValue("$now", now.ToString("O"));

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var audit = connection.CreateCommand())
            {
                audit.Transaction = transaction;
                audit.CommandText =
                """
                INSERT INTO audit_logs
                (
                    occurred_at_utc,
                    user_id,
                    username,
                    event_type,
                    entity_type,
                    entity_id,
                    success,
                    details_json,
                    client_ip_hash
                )
                VALUES
                (
                    $now,
                    $administratorId,
                    $administratorUsername,
                    'user.created',
                    'user',
                    $targetUserId,
                    1,
                    $details,
                    NULL
                );
                """;

                audit.Parameters.AddWithValue("$now", now.ToString("O"));
                audit.Parameters.AddWithValue(
                    "$administratorId",
                    administrator.Id);
                audit.Parameters.AddWithValue(
                    "$administratorUsername",
                    administrator.Username);
                audit.Parameters.AddWithValue("$targetUserId", userId);
                audit.Parameters.AddWithValue(
                    "$details",
                    JsonSerializer.Serialize(new
                    {
                        username,
                        displayName,
                        role,
                        mustChangePassword = true
                    }));

                await audit.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw Error(
                StatusCodes.Status409Conflict,
                "username_exists",
                "That username is already in use.");
        }

        var user = new UserAdministrationRecord(
            userId,
            username,
            displayName,
            role,
            MustChangePassword: true,
            IsActive: true,
            now,
            now);

        return new CreateUserResult(user, temporaryPassword);
    }

    private static string NormalizeUsername(string? value)
    {
        string username = value?.Trim() ?? string.Empty;

        if (username.Length is < 3 or > 50 ||
            username.Any(character =>
                !(char.IsLetterOrDigit(character) ||
                  character is '.' or '_' or '-')))
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "invalid_username",
                "Username must contain 3 to 50 letters, numbers, dots, underscores or hyphens.");
        }

        return username;
    }

    private static string NormalizeDisplayName(string? value)
    {
        string displayName = value?.Trim() ?? string.Empty;

        if (displayName.Length is < 2 or > 100)
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "invalid_display_name",
                "Display name must contain 2 to 100 characters.");
        }

        return displayName;
    }

    private static string NormalizeRole(string? value)
    {
        string role = value?.Trim().ToLowerInvariant() ?? string.Empty;

        if (role is not ("admin" or "teller"))
        {
            throw Error(
                StatusCodes.Status400BadRequest,
                "invalid_role",
                "Role must be admin or teller.");
        }

        return role;
    }

    private static string CreateTemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#%*-_+?";
        string all = upper + lower + digits + symbols;

        var characters = new List<char>
        {
            RandomCharacter(upper),
            RandomCharacter(lower),
            RandomCharacter(digits),
            RandomCharacter(symbols)
        };

        while (characters.Count < 20)
        {
            characters.Add(RandomCharacter(all));
        }

        for (int index = characters.Count - 1; index > 0; index--)
        {
            int swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (characters[index], characters[swapIndex]) =
                (characters[swapIndex], characters[index]);
        }

        return new string([.. characters]);
    }

    private static char RandomCharacter(string source) =>
        source[RandomNumberGenerator.GetInt32(source.Length)];

    private static UserAdministrationException Error(
        int statusCode,
        string code,
        string message) =>
        new(statusCode, code, message);
}
