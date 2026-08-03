using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public sealed record ReceiptReprintResult(
    string SaleId,
    string ReceiptNumber,
    string ShopId,
    string ShopCode,
    int ReprintVersion,
    DateTimeOffset ReprintedAtUtc,
    IReadOnlyList<GeneratedSaleDocument> Documents);

public sealed class ShopReceiptService
{
    private readonly DatabaseBootstrap _database;
    private readonly AuditDocumentWriter _documents;

    public ShopReceiptService(
        DatabaseBootstrap database,
        AuditDocumentWriter documents)
    {
        _database = database;
        _documents = documents;
    }

    public async Task<IReadOnlyList<ReceiptListItem>> ListReceiptsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(requestedLimit, 1, 500);
        bool viewAll = CanViewAllShopSales(user, context);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            sale.id,
            sale.receipt_number,
            sale.invoice_number,
            user.display_name,
            sale.status,
            sale.total_minor,
            CASE
                WHEN
                (
                    SELECT COUNT(*)
                    FROM sale_payments AS payment
                    WHERE payment.sale_id = sale.id
                ) > 1 THEN 'split'
                ELSE COALESCE(
                    (
                        SELECT payment.payment_method
                        FROM sale_payments AS payment
                        WHERE payment.sale_id = sale.id
                        ORDER BY payment.id
                        LIMIT 1
                    ),
                    '')
            END,
            COALESCE(sale.completed_at_utc, sale.created_at_utc),
            (
                SELECT COUNT(*)
                FROM sale_documents AS document
                WHERE document.sale_id = sale.id
                  AND document.is_reprint = 0
            ),
            shop.id,
            shop.code,
            shop.name
        FROM sales AS sale
        INNER JOIN users AS user
            ON user.id = sale.teller_user_id
        INNER JOIN shops AS shop
            ON shop.id = sale.shop_id
        WHERE sale.shop_id = $shopId
          AND ($viewAll = 1 OR sale.teller_user_id = $userId)
        ORDER BY COALESCE(sale.completed_at_utc, sale.created_at_utc) DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$viewAll", viewAll ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$limit", limit);

        var receipts = new List<ReceiptListItem>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            receipts.Add(new ReceiptListItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7)),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11)));
        }

        return receipts;
    }

    public async Task<ReceiptDetails> GetReceiptAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string saleId,
        CancellationToken cancellationToken = default)
    {
        string normalizedSaleId = NormalizeSaleId(saleId);
        bool viewAll = CanViewAllShopSales(user, context);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        ReceiptHeader header;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT
                sale.id,
                sale.receipt_number,
                sale.invoice_number,
                user.display_name,
                sale.status,
                sale.customer_name,
                sale.customer_phone,
                sale.customer_address,
                sale.customer_tax_number,
                sale.subtotal_minor,
                sale.discount_minor,
                sale.total_minor,
                sale.amount_received_minor,
                sale.change_minor,
                COALESCE(
                    (
                        SELECT payment.payment_method
                        FROM sale_payments AS payment
                        WHERE payment.sale_id = sale.id
                        ORDER BY payment.id
                        LIMIT 1
                    ),
                    ''),
                sale.notes,
                COALESCE(sale.completed_at_utc, sale.created_at_utc),
                shop.id,
                shop.code,
                shop.name
            FROM sales AS sale
            INNER JOIN users AS user
                ON user.id = sale.teller_user_id
            INNER JOIN shops AS shop
                ON shop.id = sale.shop_id
            WHERE sale.id = $saleId
              AND sale.shop_id = $shopId
              AND ($viewAll = 1 OR sale.teller_user_id = $userId)
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("$saleId", normalizedSaleId);
            command.Parameters.AddWithValue("$shopId", context.ShopId);
            command.Parameters.AddWithValue("$viewAll", viewAll ? 1 : 0);
            command.Parameters.AddWithValue("$userId", user.Id);

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw NotFound(
                    "receipt_not_found",
                    "The receipt could not be found in the active shop.");
            }

            header = new ReceiptHeader(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetString(14),
                reader.GetString(15),
                DateTimeOffset.Parse(reader.GetString(16)),
                reader.GetString(17),
                reader.GetString(18),
                reader.GetString(19));
        }

        IReadOnlyList<CompletedSaleLine> items =
            await ReadItemsAsync(
                connection,
                normalizedSaleId,
                cancellationToken);

        IReadOnlyList<GeneratedSaleDocument> documents =
            await ReadOriginalDocumentsAsync(
                connection,
                normalizedSaleId,
                cancellationToken);

        IReadOnlyList<CompletedSalePayment> payments =
            await ReadPaymentsAsync(
                connection,
                normalizedSaleId,
                cancellationToken);

        return new ReceiptDetails(
            header.SaleId,
            header.ReceiptNumber,
            header.InvoiceNumber,
            header.TellerName,
            header.Status,
            header.CustomerName,
            header.CustomerPhone,
            header.CustomerAddress,
            header.CustomerTaxNumber,
            header.SubtotalMinor,
            header.DiscountMinor,
            header.TotalMinor,
            header.AmountReceivedMinor,
            header.ChangeMinor,
            payments.Count > 1 ? "split" : header.PaymentMethod,
            header.Notes,
            header.CompletedAtUtc,
            items,
            documents,
            header.ShopId,
            header.ShopCode,
            header.ShopName,
            payments);
    }

    public async Task<StoredDocumentFile> ResolveDocumentAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string saleId,
        string documentId,
        CancellationToken cancellationToken = default)
    {
        string normalizedSaleId = NormalizeSaleId(saleId);
        string normalizedDocumentId = documentId?.Trim() ?? string.Empty;
        if (normalizedDocumentId.Length == 0 || normalizedDocumentId.Length > 100)
        {
            throw NotFound(
                "receipt_document_not_found",
                "The receipt document could not be found.");
        }

        bool viewAll = CanViewAllShopSales(user, context);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            document.relative_path,
            document.file_format,
            document.document_number
        FROM sale_documents AS document
        INNER JOIN sales AS sale
            ON sale.id = document.sale_id
        WHERE document.id = $documentId
          AND document.sale_id = $saleId
          AND sale.shop_id = $shopId
          AND ($viewAll = 1 OR sale.teller_user_id = $userId)
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$documentId", normalizedDocumentId);
        command.Parameters.AddWithValue("$saleId", normalizedSaleId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$viewAll", viewAll ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "receipt_document_not_found",
                "The receipt document could not be found in the active shop.");
        }

        string relativePath = reader.GetString(0);
        string format = reader.GetString(1);
        string documentNumber = reader.GetString(2);
        string fullPath = _documents.ResolveStoredPath(relativePath);

        if (!File.Exists(fullPath))
        {
            throw NotFound(
                "receipt_file_missing",
                "The saved receipt file is missing from the audit folder.");
        }

        string contentType = format switch
        {
            "pdf" => "application/pdf",
            "html" => "text/html; charset=utf-8",
            "json" => "application/json; charset=utf-8",
            _ => "application/octet-stream"
        };

        return new StoredDocumentFile(
            fullPath,
            contentType,
            $"{documentNumber}.{format}");
    }

    public async Task<ReceiptReprintResult> RecordReprintAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string saleId,
        CancellationToken cancellationToken = default)
    {
        string normalizedSaleId = NormalizeSaleId(saleId);
        bool viewAll = CanViewAllShopSales(user, context);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        string receiptNumber;
        await using (var authorization = connection.CreateCommand())
        {
            authorization.Transaction = transaction;
            authorization.CommandText =
            """
            SELECT receipt_number
            FROM sales
            WHERE id = $saleId
              AND shop_id = $shopId
              AND ($viewAll = 1 OR teller_user_id = $userId)
            LIMIT 1;
            """;
            authorization.Parameters.AddWithValue("$saleId", normalizedSaleId);
            authorization.Parameters.AddWithValue("$shopId", context.ShopId);
            authorization.Parameters.AddWithValue("$viewAll", viewAll ? 1 : 0);
            authorization.Parameters.AddWithValue("$userId", user.Id);

            object? result =
                await authorization.ExecuteScalarAsync(cancellationToken);
            if (result is null)
            {
                throw NotFound(
                    "receipt_not_found",
                    "The receipt could not be found in the active shop.");
            }

            receiptNumber = Convert.ToString(result)!;
        }

        var sourceDocuments = new List<ReprintSource>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            """
            SELECT
                document_type,
                document_number,
                file_format,
                relative_path,
                file_sha256,
                file_size_bytes,
                (
                    SELECT COALESCE(MAX(existing.version), 0) + 1
                    FROM sale_documents AS existing
                    WHERE existing.sale_id = source.sale_id
                      AND existing.document_type = source.document_type
                      AND existing.file_format = source.file_format
                )
            FROM sale_documents AS source
            WHERE source.sale_id = $saleId
              AND source.is_reprint = 0
            ORDER BY source.document_type, source.file_format;
            """;
            read.Parameters.AddWithValue("$saleId", normalizedSaleId);

            await using var reader =
                await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sourceDocuments.Add(new ReprintSource(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetInt32(6)));
            }
        }

        if (sourceDocuments.Count == 0)
        {
            throw Conflict(
                "receipt_documents_missing",
                "The receipt has no immutable documents available for reprint.");
        }

        var registered = new List<GeneratedSaleDocument>();
        int highestVersion = 1;

        foreach (ReprintSource source in sourceDocuments)
        {
            string id = Guid.NewGuid().ToString("N");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
            """
            INSERT INTO sale_documents
            (
                id,
                sale_id,
                document_type,
                document_number,
                file_format,
                relative_path,
                file_sha256,
                file_size_bytes,
                is_reprint,
                version,
                generated_by_user_id,
                generated_at_utc
            )
            VALUES
            (
                $id,
                $saleId,
                $documentType,
                $documentNumber,
                $fileFormat,
                $relativePath,
                $fileSha256,
                $fileSizeBytes,
                1,
                $version,
                $userId,
                $generatedAtUtc
            );
            """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$saleId", normalizedSaleId);
            insert.Parameters.AddWithValue("$documentType", source.DocumentType);
            insert.Parameters.AddWithValue("$documentNumber", source.DocumentNumber);
            insert.Parameters.AddWithValue("$fileFormat", source.FileFormat);
            insert.Parameters.AddWithValue("$relativePath", source.RelativePath);
            insert.Parameters.AddWithValue("$fileSha256", source.FileSha256);
            insert.Parameters.AddWithValue("$fileSizeBytes", source.FileSizeBytes);
            insert.Parameters.AddWithValue("$version", source.Version);
            insert.Parameters.AddWithValue("$userId", user.Id);
            insert.Parameters.AddWithValue("$generatedAtUtc", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);

            highestVersion = Math.Max(highestVersion, source.Version);
            registered.Add(new GeneratedSaleDocument(
                id,
                source.DocumentType,
                source.DocumentNumber,
                source.FileFormat,
                source.RelativePath,
                source.FileSha256,
                source.FileSizeBytes));
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            normalizedSaleId,
            new
            {
                context.OrganizationId,
                context.ShopId,
                context.ShopCode,
                receiptNumber,
                reprintVersion = highestVersion,
                documentCount = registered.Count
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new ReceiptReprintResult(
            normalizedSaleId,
            receiptNumber,
            context.ShopId,
            context.ShopCode,
            highestVersion,
            now,
            registered);
    }

    public async Task EnsureSaleInOrganizationAsync(
        string organizationId,
        string saleId,
        CancellationToken cancellationToken = default)
    {
        string normalizedSaleId = NormalizeSaleId(saleId);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT COUNT(1)
        FROM sales AS sale
        INNER JOIN shops AS shop
            ON shop.id = sale.shop_id
        WHERE sale.id = $saleId
          AND shop.organization_id = $organizationId;
        """;
        command.Parameters.AddWithValue("$saleId", normalizedSaleId);
        command.Parameters.AddWithValue("$organizationId", organizationId);

        int count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
        if (count == 0)
        {
            throw NotFound(
                "sale_not_found",
                "The requested sale could not be found in this business.");
        }
    }

    private static bool CanViewAllShopSales(
        AuthenticatedUser user,
        ActiveShopContextRecord context) =>
        string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
        || context.AccessLevel is "manager" or "supervisor" or "viewer";

    private static string NormalizeSaleId(string saleId)
    {
        string normalized = saleId?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > 100)
        {
            throw NotFound(
                "receipt_not_found",
                "The receipt could not be found.");
        }

        return normalized;
    }

    private static async Task<IReadOnlyList<CompletedSaleLine>> ReadItemsAsync(
        SqliteConnection connection,
        string saleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            product_id,
            product_name_snapshot,
            sku_snapshot,
            quantity,
            sale_unit_snapshot,
            unit_size_ml_snapshot,
            unit_price_minor,
            line_total_minor
        FROM sale_items
        WHERE sale_id = $saleId
        ORDER BY id;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);

        var items = new List<CompletedSaleLine>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new CompletedSaleLine(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return items;
    }


    private static async Task<IReadOnlyList<CompletedSalePayment>> ReadPaymentsAsync(
        SqliteConnection connection,
        string saleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT payment_method, amount_minor, reference
        FROM sale_payments
        WHERE sale_id = $saleId
        ORDER BY id;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);

        var payments = new List<CompletedSalePayment>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            payments.Add(new CompletedSalePayment(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2)));
        }

        return payments;
    }

    private static async Task<IReadOnlyList<GeneratedSaleDocument>>
        ReadOriginalDocumentsAsync(
            SqliteConnection connection,
            string saleId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            id,
            document_type,
            document_number,
            file_format,
            relative_path,
            file_sha256,
            file_size_bytes
        FROM sale_documents
        WHERE sale_id = $saleId
          AND is_reprint = 0
        ORDER BY document_type, file_format;
        """;
        command.Parameters.AddWithValue("$saleId", saleId);

        var documents = new List<GeneratedSaleDocument>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new GeneratedSaleDocument(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6)));
        }

        return documents;
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string saleId,
        object details,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO audit_logs
        (
            occurred_at_utc,
            user_id,
            username,
            event_type,
            entity_type,
            entity_id,
            success,
            details_json,
            client_ip_hash
        )
        VALUES
        (
            $occurredAtUtc,
            $userId,
            $username,
            'receipt.reprinted',
            'sale',
            $saleId,
            1,
            $detailsJson,
            NULL
        );
        """;
        command.Parameters.AddWithValue(
            "$occurredAtUtc",
            DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$saleId", saleId);
        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SalesException NotFound(
        string code,
        string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private static SalesException Conflict(
        string code,
        string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private sealed record ReceiptHeader(
        string SaleId,
        string ReceiptNumber,
        string? InvoiceNumber,
        string TellerName,
        string Status,
        string CustomerName,
        string CustomerPhone,
        string CustomerAddress,
        string CustomerTaxNumber,
        long SubtotalMinor,
        long DiscountMinor,
        long TotalMinor,
        long AmountReceivedMinor,
        long ChangeMinor,
        string PaymentMethod,
        string Notes,
        DateTimeOffset CompletedAtUtc,
        string ShopId,
        string ShopCode,
        string ShopName);

    private sealed record ReprintSource(
        string DocumentType,
        string DocumentNumber,
        string FileFormat,
        string RelativePath,
        string FileSha256,
        long FileSizeBytes,
        int Version);
}
