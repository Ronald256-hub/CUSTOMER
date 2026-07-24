namespace Robo.Pos.Server.Security;

public enum SessionValidationStatus
{
    Success,
    Missing,
    Invalid,
    Expired,
    Revoked,
    Disabled
}

public sealed record SessionValidationResult(
    SessionValidationStatus Status,
    AuthenticatedUser? User = null,
    string? SessionId = null,
    DateTimeOffset? ExpiresAtUtc = null);
