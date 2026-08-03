using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Robo.Pos.Server.Sales;

public sealed record AuditDocumentLine(
    string ProductName,
    string Sku,
    long Quantity,
    string SaleUnit,
    int? UnitSizeMl,
    long UnitPriceMinor,
    long LineTotalMinor);

public sealed record AuditDocumentPayment(
    string PaymentMethod,
    long AmountMinor,
    string Reference);

public sealed record AuditDocumentSnapshot(
    string BusinessName,
    string BusinessAddress,
    string BusinessPhone,
    string BusinessEmail,
    string CurrencyCode,
    string ReceiptFooter,
    string SaleId,
    string ReceiptNumber,
    string? InvoiceNumber,
    string TellerName,
    string CustomerName,
    string CustomerPhone,
    string CustomerAddress,
    string CustomerTaxNumber,
    string PaymentMethod,
    long SubtotalMinor,
    long DiscountMinor,
    long TotalMinor,
    long AmountReceivedMinor,
    long ChangeMinor,
    string Notes,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<AuditDocumentLine> Items,
    IReadOnlyList<AuditDocumentPayment>? Payments = null);

public sealed record WrittenAuditFile(
    string DocumentType,
    string DocumentNumber,
    string FileFormat,
    string RelativePath,
    string FileSha256,
    long FileSizeBytes);

public sealed class AuditDocumentWriter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true
        };

    public AuditDocumentWriter()
    {
        RootPath = ResolveRootPath();
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public async Task<IReadOnlyList<WrittenAuditFile>>
        WriteAsync(
            AuditDocumentSnapshot snapshot,
            string documentType,
            CancellationToken cancellationToken = default)
    {
        string normalizedType =
            documentType.Trim().ToLowerInvariant();

        if (normalizedType is not ("receipt" or "invoice"))
        {
            throw new ArgumentException(
                "Document type must be receipt or invoice.",
                nameof(documentType));
        }

        string documentNumber =
            normalizedType == "invoice"
                ? snapshot.InvoiceNumber
                    ?? throw new InvalidOperationException(
                        "The sale has no invoice number.")
                : snapshot.ReceiptNumber;

        string folderName =
            normalizedType == "invoice"
                ? "Invoices"
                : "Receipts";

        string year =
            snapshot.CompletedAtUtc.Year
                .ToString("0000", CultureInfo.InvariantCulture);

        string month =
            snapshot.CompletedAtUtc.Month
                .ToString("00", CultureInfo.InvariantCulture);

        string directory =
            Path.Combine(
                RootPath,
                folderName,
                year,
                month);

        Directory.CreateDirectory(directory);

        byte[] jsonBytes =
            Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    new
                    {
                        documentType = normalizedType,
                        documentNumber,
                        snapshot
                    },
                    JsonOptions));

        byte[] htmlBytes =
            Encoding.UTF8.GetBytes(
                BuildHtml(
                    snapshot,
                    normalizedType,
                    documentNumber));

        byte[] pdfBytes =
            BuildPdf(
                BuildPdfLines(
                    snapshot,
                    normalizedType,
                    documentNumber));

        var files =
            new List<WrittenAuditFile>(3);

        files.Add(
            await SaveAsync(
                directory,
                folderName,
                year,
                month,
                documentNumber,
                "json",
                jsonBytes,
                normalizedType,
                cancellationToken));

        files.Add(
            await SaveAsync(
                directory,
                folderName,
                year,
                month,
                documentNumber,
                "html",
                htmlBytes,
                normalizedType,
                cancellationToken));

        files.Add(
            await SaveAsync(
                directory,
                folderName,
                year,
                month,
                documentNumber,
                "pdf",
                pdfBytes,
                normalizedType,
                cancellationToken));

        return files;
    }

    public string ResolveStoredPath(
        string relativePath)
    {
        string fullRoot =
            Path.GetFullPath(RootPath)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string candidate =
            Path.GetFullPath(
                Path.Combine(
                    RootPath,
                    relativePath));

        if (!candidate.StartsWith(
                fullRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The stored document path is invalid.");
        }

        return candidate;
    }

    private static string ResolveRootPath()
    {
        string? configured =
            Environment.GetEnvironmentVariable(
                "ROBO_DOCUMENT_ROOT");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(
                configured.Trim());
        }

        if (OperatingSystem.IsWindows())
        {
            string commonDocuments =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonDocuments);

            return Path.Combine(
                commonDocuments,
                "ROBO CASK TAP POS",
                "Audit Documents");
        }

        string? dataDirectory =
            Environment.GetEnvironmentVariable(
                "ROBO_DATA_DIR");

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            return Path.Combine(
                Path.GetFullPath(dataDirectory),
                "audit-documents");
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "audit-documents");
    }

    private static async Task<WrittenAuditFile>
        SaveAsync(
            string directory,
            string folderName,
            string year,
            string month,
            string documentNumber,
            string extension,
            byte[] content,
            string documentType,
            CancellationToken cancellationToken)
    {
        string filename =
            $"{documentNumber}.{extension}";

        string fullPath =
            Path.Combine(
                directory,
                filename);

        string temporaryPath =
            fullPath +
            ".tmp-" +
            Guid.NewGuid().ToString("N");

        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath,
                content,
                cancellationToken);

            File.Move(
                temporaryPath,
                fullPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        string hash =
            Convert.ToHexString(
                SHA256.HashData(content))
            .ToLowerInvariant();

        string relativePath =
            Path.Combine(
                    folderName,
                    year,
                    month,
                    filename)
                .Replace(
                    Path.DirectorySeparatorChar,
                    '/');

        return new WrittenAuditFile(
            DocumentType: documentType,
            DocumentNumber: documentNumber,
            FileFormat: extension,
            RelativePath: relativePath,
            FileSha256: hash,
            FileSizeBytes: content.LongLength);
    }

    private static string BuildHtml(
        AuditDocumentSnapshot snapshot,
        string documentType,
        string documentNumber)
    {
        string title =
            documentType == "invoice"
                ? "INVOICE"
                : "RECEIPT";

        var html =
            new StringBuilder();

        html.Append(
            """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>
            """);

        html.Append(
            Encode(
                $"{title} {documentNumber}"));

        html.Append(
            """
              </title>
              <style>
                body {
                  font-family: Arial, sans-serif;
                  max-width: 820px;
                  margin: 28px auto;
                  padding: 24px;
                  color: #111;
                }
                h1, h2, p { margin: 0; }
                .header {
                  display: flex;
                  justify-content: space-between;
                  gap: 24px;
                  border-bottom: 2px solid #111;
                  padding-bottom: 18px;
                  margin-bottom: 18px;
                }
                .right { text-align: right; }
                .customer {
                  margin: 18px 0;
                  padding: 14px;
                  background: #f4f4f4;
                }
                table {
                  width: 100%;
                  border-collapse: collapse;
                  margin-top: 18px;
                }
                th, td {
                  padding: 9px;
                  border-bottom: 1px solid #ccc;
                  text-align: left;
                }
                .number { text-align: right; }
                .totals {
                  margin-left: auto;
                  margin-top: 18px;
                  width: 340px;
                }
                .totals div {
                  display: flex;
                  justify-content: space-between;
                  padding: 6px 0;
                }
                .grand {
                  font-weight: bold;
                  font-size: 1.2rem;
                  border-top: 2px solid #111;
                }
                .footer {
                  margin-top: 35px;
                  padding-top: 15px;
                  border-top: 1px solid #aaa;
                  text-align: center;
                }
                @media print {
                  body {
                    margin: 0;
                    max-width: none;
                  }
                }
              </style>
            </head>
            <body>
              <div class="header">
                <div>
                  <h1>
            """);

        html.Append(
            Encode(snapshot.BusinessName));

        html.Append(
            """
                  </h1>
                  <p>
            """);

        html.Append(
            Encode(snapshot.BusinessAddress));

        html.Append("</p>");

        if (!string.IsNullOrWhiteSpace(
                snapshot.BusinessPhone))
        {
            html.Append("<p>Tel: ");
            html.Append(
                Encode(snapshot.BusinessPhone));
            html.Append("</p>");
        }

        if (!string.IsNullOrWhiteSpace(
                snapshot.BusinessEmail))
        {
            html.Append("<p>Email: ");
            html.Append(
                Encode(snapshot.BusinessEmail));
            html.Append("</p>");
        }

        html.Append(
            """
                </div>
                <div class="right">
                  <h2>
            """);

        html.Append(title);

        html.Append("</h2><p><strong>");
        html.Append(
            Encode(documentNumber));
        html.Append("</strong></p><p>");

        html.Append(
            Encode(
                snapshot.CompletedAtUtc
                    .ToLocalTime()
                    .ToString(
                        "dd MMM yyyy HH:mm",
                        CultureInfo.InvariantCulture)));

        html.Append("</p><p>Teller: ");
        html.Append(
            Encode(snapshot.TellerName));
        html.Append("</p></div></div>");

        if (!string.IsNullOrWhiteSpace(
                snapshot.CustomerName) ||
            !string.IsNullOrWhiteSpace(
                snapshot.CustomerPhone) ||
            !string.IsNullOrWhiteSpace(
                snapshot.CustomerAddress) ||
            !string.IsNullOrWhiteSpace(
                snapshot.CustomerTaxNumber))
        {
            html.Append(
                "<div class=\"customer\"><strong>Customer</strong>");

            if (!string.IsNullOrWhiteSpace(
                    snapshot.CustomerName))
            {
                html.Append("<p>");
                html.Append(
                    Encode(snapshot.CustomerName));
                html.Append("</p>");
            }

            if (!string.IsNullOrWhiteSpace(
                    snapshot.CustomerPhone))
            {
                html.Append("<p>Phone: ");
                html.Append(
                    Encode(snapshot.CustomerPhone));
                html.Append("</p>");
            }

            if (!string.IsNullOrWhiteSpace(
                    snapshot.CustomerAddress))
            {
                html.Append("<p>Address: ");
                html.Append(
                    Encode(snapshot.CustomerAddress));
                html.Append("</p>");
            }

            if (!string.IsNullOrWhiteSpace(
                    snapshot.CustomerTaxNumber))
            {
                html.Append("<p>Tax number: ");
                html.Append(
                    Encode(snapshot.CustomerTaxNumber));
                html.Append("</p>");
            }

            html.Append("</div>");
        }

        html.Append(
            """
            <table>
              <thead>
                <tr>
                  <th>Item</th>
                  <th>Quantity</th>
                  <th class="number">Unit Price</th>
                  <th class="number">Total</th>
                </tr>
              </thead>
              <tbody>
            """);

        foreach (AuditDocumentLine item
                 in snapshot.Items)
        {
            html.Append("<tr><td>");
            html.Append(
                Encode(item.ProductName));
            html.Append("<br><small>");
            html.Append(
                Encode(item.Sku));
            html.Append("</small></td><td>");

            html.Append(
                Encode(
                    QuantityDescription(item)));

            html.Append("</td><td class=\"number\">");
            html.Append(
                Encode(
                    Money(
                        item.UnitPriceMinor,
                        snapshot.CurrencyCode)));
            html.Append("</td><td class=\"number\">");
            html.Append(
                Encode(
                    Money(
                        item.LineTotalMinor,
                        snapshot.CurrencyCode)));
            html.Append("</td></tr>");
        }

        html.Append(
            """
              </tbody>
            </table>
            <div class="totals">
            """);

        AppendTotal(
            html,
            "Subtotal",
            snapshot.SubtotalMinor,
            snapshot.CurrencyCode);

        if (snapshot.DiscountMinor > 0)
        {
            AppendTotal(
                html,
                "Discount",
                snapshot.DiscountMinor,
                snapshot.CurrencyCode);
        }

        html.Append("<div class=\"grand\"><span>Total</span><span>");
        html.Append(
            Encode(
                Money(
                    snapshot.TotalMinor,
                    snapshot.CurrencyCode)));
        html.Append("</span></div>");

        AppendTotal(
            html,
            "Amount received",
            snapshot.AmountReceivedMinor,
            snapshot.CurrencyCode);

        AppendTotal(
            html,
            "Change",
            snapshot.ChangeMinor,
            snapshot.CurrencyCode);

        html.Append("</div><p>Payment: <strong>");
        html.Append(
            Encode(
                DisplayPaymentMethod(
                    snapshot.PaymentMethod)));
        html.Append("</strong></p>");

        if (snapshot.Payments is { Count: > 0 })
        {
            html.Append("<div class="customer"><strong>Payment breakdown</strong>");
            foreach (AuditDocumentPayment payment in snapshot.Payments)
            {
                html.Append("<p>");
                html.Append(Encode(DisplayPaymentMethod(payment.PaymentMethod)));
                html.Append(": <strong>");
                html.Append(Encode(Money(payment.AmountMinor, snapshot.CurrencyCode)));
                html.Append("</strong>");
                if (!string.IsNullOrWhiteSpace(payment.Reference))
                {
                    html.Append(" · Ref: ");
                    html.Append(Encode(payment.Reference));
                }
                html.Append("</p>");
            }
            html.Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(
                snapshot.Notes))
        {
            html.Append("<p>Notes: ");
            html.Append(
                Encode(snapshot.Notes));
            html.Append("</p>");
        }

        html.Append("<div class=\"footer\">");
        html.Append(
            Encode(snapshot.ReceiptFooter));
        html.Append("</div></body></html>");

        return html.ToString();
    }

    private static IReadOnlyList<string>
        BuildPdfLines(
            AuditDocumentSnapshot snapshot,
            string documentType,
            string documentNumber)
    {
        string title =
            documentType == "invoice"
                ? "INVOICE"
                : "RECEIPT";

        var lines =
            new List<string>
            {
                snapshot.BusinessName,
                snapshot.BusinessAddress
            };

        if (!string.IsNullOrWhiteSpace(
                snapshot.BusinessPhone))
        {
            lines.Add(
                $"Telephone: {snapshot.BusinessPhone}");
        }

        if (!string.IsNullOrWhiteSpace(
                snapshot.BusinessEmail))
        {
            lines.Add(
                $"Email: {snapshot.BusinessEmail}");
        }

        lines.Add(string.Empty);
        lines.Add($"{title}: {documentNumber}");
        lines.Add(
            "Date: " +
            snapshot.CompletedAtUtc
                .ToLocalTime()
                .ToString(
                    "dd MMM yyyy HH:mm",
                    CultureInfo.InvariantCulture));

        lines.Add(
            $"Teller: {snapshot.TellerName}");

        if (!string.IsNullOrWhiteSpace(
                snapshot.CustomerName))
        {
            lines.Add(
                $"Customer: {snapshot.CustomerName}");
        }

        if (!string.IsNullOrWhiteSpace(
                snapshot.CustomerPhone))
        {
            lines.Add(
                $"Customer phone: {snapshot.CustomerPhone}");
        }

        if (!string.IsNullOrWhiteSpace(
                snapshot.CustomerAddress))
        {
            lines.Add(
                $"Customer address: {snapshot.CustomerAddress}");
        }

        if (!string.IsNullOrWhiteSpace(
                snapshot.CustomerTaxNumber))
        {
            lines.Add(
                $"Customer tax number: {snapshot.CustomerTaxNumber}");
        }

        lines.Add(string.Empty);
        lines.Add(
            "ITEM | QTY | UNIT PRICE | LINE TOTAL");
        lines.Add(
            new string('-', 75));

        foreach (AuditDocumentLine item
                 in snapshot.Items)
        {
            lines.Add(
                $"{item.ProductName} ({item.Sku})");

            lines.Add(
                $"  {QuantityDescription(item)} | " +
                $"{Money(item.UnitPriceMinor, snapshot.CurrencyCode)} | " +
                $"{Money(item.LineTotalMinor, snapshot.CurrencyCode)}");
        }

        lines.Add(string.Empty);
        lines.Add(
            $"Subtotal: {Money(snapshot.SubtotalMinor, snapshot.CurrencyCode)}");

        if (snapshot.DiscountMinor > 0)
        {
            lines.Add(
                $"Discount: {Money(snapshot.DiscountMinor, snapshot.CurrencyCode)}");
        }

        lines.Add(
            $"TOTAL: {Money(snapshot.TotalMinor, snapshot.CurrencyCode)}");

        lines.Add(
            $"Amount received: {Money(snapshot.AmountReceivedMinor, snapshot.CurrencyCode)}");

        lines.Add(
            $"Change: {Money(snapshot.ChangeMinor, snapshot.CurrencyCode)}");

        lines.Add(
            $"Payment: {DisplayPaymentMethod(snapshot.PaymentMethod)}");

        if (snapshot.Payments is { Count: > 0 })
        {
            lines.Add("Payment breakdown:");
            foreach (AuditDocumentPayment payment in snapshot.Payments)
            {
                string reference = string.IsNullOrWhiteSpace(payment.Reference)
                    ? string.Empty
                    : $" | Ref: {payment.Reference}";
                lines.Add(
                    $"  {DisplayPaymentMethod(payment.PaymentMethod)}: " +
                    $"{Money(payment.AmountMinor, snapshot.CurrencyCode)}{reference}");
            }
        }

        if (!string.IsNullOrWhiteSpace(
                snapshot.Notes))
        {
            lines.Add(
                $"Notes: {snapshot.Notes}");
        }

        lines.Add(string.Empty);
        lines.Add(snapshot.ReceiptFooter);

        return lines;
    }

    private static byte[] BuildPdf(
        IReadOnlyList<string> sourceLines)
    {
        var wrappedLines =
            new List<string>();

        foreach (string line in sourceLines)
        {
            wrappedLines.AddRange(
                WrapLine(line, 95));
        }

        if (wrappedLines.Count == 0)
        {
            wrappedLines.Add(string.Empty);
        }

        string[][] pages =
            wrappedLines
                .Chunk(48)
                .Select(chunk => chunk.ToArray())
                .ToArray();

        int objectCount =
            3 + (pages.Length * 2);

        var objectOffsets =
            new long[objectCount + 1];

        using var stream =
            new MemoryStream();

        void WriteAscii(string value)
        {
            byte[] bytes =
                Encoding.ASCII.GetBytes(value);

            stream.Write(
                bytes,
                0,
                bytes.Length);
        }

        void WriteObject(
            int objectNumber,
            string body)
        {
            objectOffsets[objectNumber] =
                stream.Position;

            WriteAscii(
                objectNumber +
                " 0 obj\n" +
                body +
                "\nendobj\n");
        }

        WriteAscii(
            "%PDF-1.4\n%ROBO-POS\n");

        WriteObject(
            1,
            "<< /Type /Catalog /Pages 2 0 R >>");

        string pageReferences =
            string.Join(
                " ",
                Enumerable.Range(
                        0,
                        pages.Length)
                    .Select(index =>
                        (4 + index * 2) +
                        " 0 R"));

        WriteObject(
            2,
            "<< /Type /Pages /Kids [" +
            pageReferences +
            "] /Count " +
            pages.Length +
            " >>");

        WriteObject(
            3,
            "<< /Type /Font " +
            "/Subtype /Type1 " +
            "/BaseFont /Helvetica >>");

        for (int index = 0;
             index < pages.Length;
             index++)
        {
            int pageObjectNumber =
                4 + index * 2;

            int contentObjectNumber =
                pageObjectNumber + 1;

            string pageBody =
                "<< /Type /Page\n" +
                "   /Parent 2 0 R\n" +
                "   /MediaBox [0 0 595 842]\n" +
                "   /Resources " +
                "<< /Font << /F1 3 0 R >> >>\n" +
                "   /Contents " +
                contentObjectNumber +
                " 0 R\n" +
                ">>";

            WriteObject(
                pageObjectNumber,
                pageBody);

            var content =
                new StringBuilder();

            content.Append(
                "BT\n" +
                "/F1 10 Tf\n" +
                "40 800 Td\n" +
                "14 TL\n");

            foreach (string line
                     in pages[index])
            {
                content.Append('(');
                content.Append(
                    EscapePdfText(line));
                content.Append(
                    ") Tj\nT*\n");
            }

            content.Append("ET");

            byte[] contentBytes =
                Encoding.ASCII.GetBytes(
                    content.ToString());

            objectOffsets[contentObjectNumber] =
                stream.Position;

            WriteAscii(
                contentObjectNumber +
                " 0 obj\n" +
                "<< /Length " +
                contentBytes.Length +
                " >>\n" +
                "stream\n");

            stream.Write(
                contentBytes,
                0,
                contentBytes.Length);

            WriteAscii(
                "\nendstream\nendobj\n");
        }

        long crossReferencePosition =
            stream.Position;

        WriteAscii(
            "xref\n0 " +
            (objectCount + 1) +
            "\n");

        WriteAscii(
            "0000000000 65535 f \n");

        for (int objectNumber = 1;
             objectNumber <= objectCount;
             objectNumber++)
        {
            WriteAscii(
                objectOffsets[objectNumber]
                    .ToString(
                        "0000000000",
                        CultureInfo.InvariantCulture)
                +
                " 00000 n \n");
        }

        WriteAscii(
            "trailer\n" +
            "<< /Size " +
            (objectCount + 1) +
            " /Root 1 0 R >>\n" +
            "startxref\n" +
            crossReferencePosition +
            "\n%%EOF");

        return stream.ToArray();
    }

    private static IEnumerable<string>
        WrapLine(
            string line,
            int maximumLength)
    {
        if (line.Length <= maximumLength)
        {
            yield return line;
            yield break;
        }

        string remaining =
            line;

        while (remaining.Length >
               maximumLength)
        {
            int split =
                remaining.LastIndexOf(
                    ' ',
                    maximumLength);

            if (split <= 0)
            {
                split =
                    maximumLength;
            }

            yield return
                remaining[..split];

            remaining =
                remaining[split..]
                    .TrimStart();
        }

        yield return remaining;
    }

    private static string EscapePdfText(
        string value)
    {
        var builder =
            new StringBuilder(
                value.Length);

        foreach (char character in value)
        {
            char safeCharacter =
                character is >= ' ' and <= '~'
                    ? character
                    : '?';

            if (safeCharacter is
                '\\' or '(' or ')')
            {
                builder.Append('\\');
            }

            builder.Append(
                safeCharacter);
        }

        return builder.ToString();
    }

    private static string QuantityDescription(
        AuditDocumentLine item)
    {
        if (item.UnitSizeMl is > 0)
        {
            return
                $"{item.Quantity} {item.SaleUnit}" +
                $" × {item.UnitSizeMl}ml";
        }

        return
            $"{item.Quantity} {item.SaleUnit}";
    }

    private static void AppendTotal(
        StringBuilder html,
        string label,
        long amount,
        string currency)
    {
        html.Append("<div><span>");
        html.Append(
            Encode(label));
        html.Append("</span><span>");
        html.Append(
            Encode(
                Money(amount, currency)));
        html.Append("</span></div>");
    }

    private static string Money(
        long value,
        string currency)
    {
        return
            value.ToString(
                "N0",
                CultureInfo.InvariantCulture)
            + " "
            + currency;
    }

    private static string DisplayPaymentMethod(
        string paymentMethod)
    {
        return paymentMethod
            .Replace('_', ' ')
            .ToUpperInvariant();
    }

    private static string Encode(
        string? value)
    {
        return HtmlEncoder.Default.Encode(
            value ?? string.Empty);
    }
}
