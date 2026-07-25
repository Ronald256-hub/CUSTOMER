namespace Robo.Pos.Server.Security;

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);

public enum PasswordChangeStatus
{
    Success,
    InvalidCurrentPassword,
    WeakPassword,
    SameAsCurrentPassword,
    ChangeTooSoon,
    UserNotFound,
    Disabled
}

public sealed record PasswordChangeResult(
    PasswordChangeStatus Status,
    string? ErrorCode = null,
    string? Message = null,
    int RevokedSessions = 0);
