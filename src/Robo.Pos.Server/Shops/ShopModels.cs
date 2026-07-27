namespace Robo.Pos.Server.Shops;

public sealed record ShopRecord(
    string Id,
    string OrganizationId,
    string Code,
    string Name,
    string Address,
    string Phone,
    string Email,
    string TaxNumber,
    string CurrencyCode,
    string TimezoneId,
    bool IsHeadOffice,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record AvailableShopRecord(
    string Id,
    string Code,
    string Name,
    string Address,
    string CurrencyCode,
    string TimezoneId,
    bool IsHeadOffice,
    string AccessLevel,
    bool IsPrimary);

public sealed record CreateShopRequest(
    string Code = "",
    string Name = "",
    string? Address = null,
    string? Phone = null,
    string? Email = null,
    string? TaxNumber = null,
    string CurrencyCode = "UGX",
    string TimezoneId = "Africa/Kampala",
    bool IsHeadOffice = false);

public sealed record UpdateShopRequest(
    string Name = "",
    string? Address = null,
    string? Phone = null,
    string? Email = null,
    string? TaxNumber = null,
    string CurrencyCode = "UGX",
    string TimezoneId = "Africa/Kampala",
    bool IsHeadOffice = false,
    bool IsActive = true,
    int ExpectedVersion = 1);

public sealed record AssignShopUserRequest(
    string AccessLevel = "teller",
    bool IsPrimary = false,
    bool IsActive = true);

public sealed record ShopUserAccessRecord(
    string UserId,
    string Username,
    string DisplayName,
    string Role,
    string AccessLevel,
    bool IsPrimary,
    bool IsActive,
    DateTimeOffset AssignedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed class ShopException : Exception
{
    public ShopException(
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