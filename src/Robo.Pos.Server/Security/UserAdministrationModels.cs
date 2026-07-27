namespace Robo.Pos.Server.Security;

public sealed record CreateUserRequest(
    string Username,
    string DisplayName,
    string Role);

public sealed record UserAdministrationRecord(
    string Id,
    string Username,
    string DisplayName,
    string Role,
    bool MustChangePassword,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateUserResult(
    UserAdministrationRecord User,
    string TemporaryPassword);

public sealed class UserAdministrationException : Exception
{
    public UserAdministrationException(
        int statusCode,
        string errorCode,
        string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }

    public string ErrorCode { get; }
}
