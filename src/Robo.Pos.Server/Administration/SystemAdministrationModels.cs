namespace Robo.Pos.Server.Administration;

public sealed record UpdateBusinessSettingsRequest(
    string BusinessName,
    string Address,
    string Phone,
    string Email,
    string ReceiptFooter);

public sealed record BusinessSettingsResult(
    string BusinessName,
    string Address,
    string Phone,
    string Email,
    string CurrencyCode,
    string ReceiptFooter,
    string DocumentRoot,
    string DatabasePath,
    string BackupRoot,
    bool ReceiptVerificationEnabled,
    DateTimeOffset UpdatedAtUtc);

public sealed record BackupVerificationResult(
    string FileName,
    DateTimeOffset CreatedAtUtc,
    long SizeBytes,
    string Sha256,
    bool IntegrityOk,
    string IntegrityMessage,
    int SchemaVersion);

public sealed record BackupDownloadResult(
    string FileName,
    string FullPath);

public sealed class SystemAdministrationException : Exception
{
    public SystemAdministrationException(
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
