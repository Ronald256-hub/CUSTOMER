namespace Robo.Pos.Server.Shops;

public sealed record ActiveShopContextRecord(
    string OrganizationId,
    string OrganizationName,
    string ShopId,
    string ShopCode,
    string ShopName,
    string CurrencyCode,
    string TimezoneId,
    bool IsHeadOffice,
    string AccessLevel,
    DateTimeOffset SelectedAtUtc,
    int Version);

public sealed record SetActiveShopContextRequest(
    string ShopId = "",
    int? ExpectedVersion = null);

public sealed class ShopContextException : Exception
{
    public ShopContextException(
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
