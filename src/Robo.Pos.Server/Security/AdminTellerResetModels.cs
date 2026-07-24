namespace Robo.Pos.Server.Security;

public sealed record AdminResetTellerPasswordRequest(
    string AdministratorPassword,
    string Reason);

public enum AdminTellerResetStatus
{
    Success,
    AuthenticationRequired,
    AdministratorOnly,
    AdministratorPasswordChangeRequired,
    InvalidAdministratorPassword,
    TargetUserNotFound,
    TargetMustBeTeller,
    CannotResetSelf,
    TargetDisabled,
    ReasonRequired,
    ReasonTooLong,
    RateLimited
}

public sealed record AdminTellerResetResult(
    AdminTellerResetStatus Status,
    string? ErrorCode = null,
    string? Message = null,
    string? TargetUserId = null,
    string? Username = null,
    string? DisplayName = null,
    string? TemporaryPassword = null,
    int RevokedSessions = 0,
    DateTimeOffset? ResetAtUtc = null);
