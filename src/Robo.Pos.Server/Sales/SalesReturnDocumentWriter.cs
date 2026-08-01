using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Robo.Pos.Server.Sales;

public sealed record SalesReturnDocumentSnapshot(
    string BusinessName,
    string BusinessAddress,
    string BusinessPhone,
    string BusinessEmail,
    string CurrencyCode,
    string ReturnNumber,
    string OriginalReceiptNumber,
    string CustomerName,
    string RefundMethod,
    string Reason,
    string Notes,
    string ApprovedBy,
    DateTimeOffset CompletedAtUtc,
    long RefundAmountMinor,
    IReadOnlyList<SalesReturnLineRecord> Items);

public sealed record WrittenSalesReturnDocument(
    string DocumentType,
    string DocumentNumber,
    string FileFormat,
    string RelativePath,
    string FileSha256,
    long FileSizeBytes);

public sealed class SalesReturnDocumentWriter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    public SalesReturnDocumentWriter()
    {
        RootPath = ResolveRootPath();
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public async Task<IReadOnlyList<WrittenSalesReturnDocument>> WriteAsync(
        SalesReturnDocumentSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        string year = snapshot.CompletedAtUtc.Year.ToString("0000", CultureInfo.InvariantCulture);
        string month = snapshot.CompletedAtUtc.Month.ToString("00", CultureInfo.InvariantCulture);
        string folderName = "Credit Notes";
        string directory = Path.Combine(RootPath, folderName, year, month);
        Directory.CreateDirectory(directory);

        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { documentType = "credit_note", documentNumber = snapshot.ReturnNumber, snapshot },
            JsonOptions));
        byte[] html = Encoding.UTF8.GetBytes(BuildHtml(snapshot));

        return new[]
        {
            await SaveAsync(directory, folderName, year, month, snapshot.ReturnNumber, "json", json, cancellationToken),
            await SaveAsync(directory, folderName, year, month, snapshot.ReturnNumber, "html", html, cancellationToken)
        };
    }

    public string ResolveStoredPath(string relativePath)
    {
        string fullRoot = Path.GetFullPath(RootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The stored credit-note path is invalid.");
        }
        return candidate;
    }

    private static async Task<WrittenSalesReturnDocument> SaveAsync(
        string directory,
        string folderName,
        string year,
        string month,
        string documentNumber,
        string extension,
        byte[] content,
        CancellationToken cancellationToken)
    {
        string filename = $"{documentNumber}.{extension}";
        string fullPath = Path.Combine(directory, filename);
        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new WrittenSalesReturnDocument(
            "credit_note",
            documentNumber,
            extension,
            Path.Combine(folderName, year, month, filename)
                .Replace(Path.DirectorySeparatorChar, '/'),
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            content.LongLength);
    }

    private static string ResolveRootPath()
    {
        string? configured = Environment.GetEnvironmentVariable("ROBO_DOCUMENT_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());
        if (OperatingSystem.IsWindows())
        {
            string commonDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            return Path.Combine(commonDocuments, "ROBO CASK TAP POS", "Audit Documents");
        }
        string? dataDirectory = Environment.GetEnvironmentVariable("ROBO_DATA_DIR");
        return !string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(Path.GetFullPath(dataDirectory), "audit-documents")
            : Path.Combine(AppContext.BaseDirectory, "audit-documents");
    }

    private static string BuildHtml(SalesReturnDocumentSnapshot snapshot)
    {
        var html = new StringBuilder();
        html.Append("""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<style>
body{font-family:Arial,sans-serif;max-width:820px;margin:28px auto;padding:24px;color:#111}h1,h2,p{margin:0}.head{display:flex;justify-content:space-between;gap:24px;border-bottom:2px solid #111;padding-bottom:18px}.right{text-align:right}.notice{margin:18px 0;padding:14px;background:#f4f4f4}.reason{margin:18px 0;padding:14px;border:1px solid #aaa}table{width:100%;border-collapse:collapse;margin-top:18px}th,td{padding:9px;border-bottom:1px solid #ccc;text-align:left}.number{text-align:right}.total{margin-left:auto;margin-top:18px;width:340px;display:flex;justify-content:space-between;border-top:2px solid #111;padding-top:10px;font-size:1.15rem;font-weight:bold}.tag{font-size:.8rem;text-transform:uppercase;letter-spacing:.08em}@media print{body{margin:0;max-width:none}}
</style><title>Credit Note
""");
        html.Append(Encode(snapshot.ReturnNumber));
        html.Append("""
</title></head><body><div class="head"><div><h1>
""");
        html.Append(Encode(snapshot.BusinessName));
        html.Append("</h1><p>");
        html.Append(Encode(snapshot.BusinessAddress));
        html.Append("</p>");
        if (!string.IsNullOrWhiteSpace(snapshot.BusinessPhone)) html.Append($"<p>Tel: {Encode(snapshot.BusinessPhone)}</p>");
        if (!string.IsNullOrWhiteSpace(snapshot.BusinessEmail)) html.Append($"<p>Email: {Encode(snapshot.BusinessEmail)}</p>");
        html.Append("</div><div class=\"right\"><span class=\"tag\">Credit note</span><h2>");
        html.Append(Encode(snapshot.ReturnNumber));
        html.Append("</h2><p>Original receipt: ");
        html.Append(Encode(snapshot.OriginalReceiptNumber));
        html.Append("</p><p>");
        html.Append(Encode(snapshot.CompletedAtUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)));
        html.Append("</p></div></div><div class=\"notice\"><strong>Refund to customer</strong>");
        if (!string.IsNullOrWhiteSpace(snapshot.CustomerName)) html.Append($"<p>{Encode(snapshot.CustomerName)}</p>");
        html.Append($"<p>Method: {Encode(DisplayMethod(snapshot.RefundMethod))}</p><p>Approved by: {Encode(snapshot.ApprovedBy)}</p></div>");
        html.Append("<table><thead><tr><th>Returned item</th><th>Disposition</th><th>Quantity</th><th class=\"number\">Refund</th></tr></thead><tbody>");
        foreach (SalesReturnLineRecord item in snapshot.Items)
        {
            html.Append("<tr><td>");
            html.Append(Encode(item.ProductName));
            html.Append("<br><small>");
            html.Append(Encode(item.Sku));
            html.Append("</small></td><td>");
            html.Append(Encode(item.Disposition == "restock" ? "Returned to stock" : "Damaged / not resellable"));
            html.Append("</td><td>");
            html.Append(Encode($"{item.Quantity:N0} {item.SaleUnit}"));
            if (item.UnitSizeMl is > 0) html.Append(Encode($" ({item.UnitSizeMl:N0} ml each)"));
            html.Append("</td><td class=\"number\">");
            html.Append(Encode(Money(item.RefundMinor, snapshot.CurrencyCode)));
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table><div class=\"total\"><span>Total refund</span><span>");
        html.Append(Encode(Money(snapshot.RefundAmountMinor, snapshot.CurrencyCode)));
        html.Append("</span></div><div class=\"reason\"><strong>Reason</strong><p>");
        html.Append(Encode(snapshot.Reason));
        html.Append("</p>");
        if (!string.IsNullOrWhiteSpace(snapshot.Notes)) html.Append($"<p>Notes: {Encode(snapshot.Notes)}</p>");
        html.Append("</div><p>This credit note is linked permanently to the original receipt and audit trail.</p></body></html>");
        return html.ToString();
    }

    private static string Money(long amount, string currency) =>
        $"{amount.ToString("N0", CultureInfo.InvariantCulture)} {currency}";

    private static string DisplayMethod(string value) =>
        value.Replace('_', ' ').ToUpperInvariant();

    private static string Encode(string? value) =>
        HtmlEncoder.Default.Encode(value ?? string.Empty);
}
