namespace Robo.Pos.Server.Security;

public sealed record LoginRequest(
    string Username,
    string Password);

public sealed record AuthenticatedUser(
    string Id,
    string Username,
    string DisplayName,
    string Role,
    bool MustChangePassword);

public enum LoginStatus
{
    Success,
    InvalidCredentials,
    Locked,
    Disabled
}

public sealed record LoginResult(
    LoginStatus Status,
    AuthenticatedUser? User = null,
    string? SessionToken = null,
    DateTimeOffset? ExpiresAtUtc = null,
    DateTimeOffset? LockedUntilUtc = null);
