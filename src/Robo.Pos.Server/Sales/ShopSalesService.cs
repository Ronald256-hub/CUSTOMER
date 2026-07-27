using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Inventory;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Sales;

public sealed class ShopSalesService
{
    private static readonly HashSet<string> PaymentMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cash",
            "mobile_money",
            "card",
            "bank"
        };

    private readonly DatabaseBootstrap _database;
    private readonly AuditDocumentWriter _documents;

    public ShopSalesService(
        DatabaseBootstrap database,
        AuditDocumentWriter documents)
    {
        _database = database;
        _documents = documents;
    }

    public async Task<CompleteSaleResult> CompleteSaleAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CompleteSaleRequest request,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SaleLineRequest> requestedLines =
            NormalizeLines(request.Items);

        string paymentMethod =
            request.PaymentMethod?.Trim().ToLowerInvariant()
            ?? string.Empty;

        if (!PaymentMethods.Contains(paymentMethod))
        {
            throw Validation(
                "invalid_payment_method",
                "Use cash, mobile money, card or bank.");
        }

        ValidateCustomer(request);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        string shiftId = await FindOpenShiftIdAsync(
            connection,
            transaction,
            user.Id,
            cancellationToken);

        BusinessSnapshot business = await ReadBusinessAsync(
            connection,
            transaction,
            cancellationToken);

        var lines = new List<ShopSaleLine>();
        long subtotal = 0;

        foreach (SaleLineRequest requested in requestedLines)
        {
            ShopSaleLine line = await ReadSaleLineAsync(
                connection,
                transaction,
                context.ShopId,
                requested,
                cancellationToken);

            subtotal = checked(subtotal + line.LineTotalMinor);
            lines.Add(line);
        }

        if (subtotal <= 0)
        {
            throw Validation(
                "sale_total_must_be_positive",
                "The sale total must be greater than zero.");
        }

        long total = subtotal;
        if (paymentMethod == "cash")
        {
            if (request.AmountReceivedMinor < total)
            {
                throw Validation(
                    "insufficient_payment",
                    "The amount received is less than the sale total.");
            }
        }
        else if (request.AmountReceivedMinor != total)
        {
            throw Validation(
                "payment_amount_mismatch",
                "Non-cash payments must equal the exact sale total.");
        }

        long change = paymentMethod == "cash"
            ? request.AmountReceivedMinor - total
            : 0;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string saleId = Guid.NewGuid().ToString("N");
        string receiptNumber = await NextDocumentNumberAsync(
            connection,
            transaction,
            "receipt",
            now,
            cancellationToken);
        string? invoiceNumber = request.IssueInvoice
            ? await NextDocumentNumberAsync(
                connection,
                transaction,
                "invoice",
                now,
                cancellationToken)
            : null;

        await InsertSaleAsync(
            connection,
            transaction,
            context.ShopId,
            saleId,
            receiptNumber,
            invoiceNumber,
            shiftId,
            user,
            request,
            subtotal,
            total,
            change,
            now,
            cancellationToken);

        foreach (ShopSaleLine line in lines)
        {
            await InsertSaleLineAsync(
                connection,
                transaction,
                saleId,
                line,
                cancellationToken);

            await DeductStockAsync(
                connection,
                transaction,
                context.ShopId,
                saleId,
                user,
                line,
                now,
                cancellationToken);
        }

        await using (var payment = connection.CreateCommand())
        {
            payment.Transaction = transaction;
            payment.CommandText =
            """
            INSERT INTO sale_payments
            (
                sale_id,
                payment_method,
                amount_minor,
                reference,
                received_at_utc
            )
            VALUES
            (
                $saleId,
                $paymentMethod,
                $amount,
                '',
                $receivedAtUtc
            );
            """;
            payment.Parameters.AddWithValue("$saleId", saleId);
            payment.Parameters.AddWithValue("$paymentMethod", paymentMethod);
            payment.Parameters.AddWithValue("$amount", total);
            payment.Parameters.AddWithValue("$receivedAtUtc", now.ToString("O"));
            await payment.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "sale.completed",
            "sale",
            saleId,
            new
            {
                organizationId = context.OrganizationId,
                shopId = context.ShopId,
                shopCode = context.ShopCode,
                receiptNumber,
                invoiceNumber,
                paymentMethod,
                totalMinor = total,
                itemCount = lines.Count
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        IReadOnlyList<CompletedSaleLine> completedLines = lines
            .Select(line => new CompletedSaleLine(
                line.ProductId,
                line.ProductName,
                line.Sku,
                line.Quantity,
                line.SaleUnit,
                line.UnitSizeMl,
                line.UnitPriceMinor,
                line.LineTotalMinor))
            .ToList();

        var snapshot = new AuditDocumentSnapshot(
            business.BusinessName,
            business.Address,
            business.Phone,
            business.Email,
            business.CurrencyCode,
            business.ReceiptFooter,
            saleId,
            receiptNumber,
            invoiceNumber,
            user.DisplayName,
            request.CustomerName?.Trim() ?? string.Empty,
            request.CustomerPhone?.Trim() ?? string.Empty,
            request.CustomerAddress?.Trim() ?? string.Empty,
            request.CustomerTaxNumber?.Trim() ?? string.Empty,
            paymentMethod,
            subtotal,
            0,
            total,
            request.AmountReceivedMinor,
            change,
            request.Notes?.Trim() ?? string.Empty,
            now,
            lines.Select(line => new AuditDocumentLine(
                    line.ProductName,
                    line.Sku,
                    line.Quantity,
                    line.SaleUnit,
                    line.UnitSizeMl,
                    line.UnitPriceMinor,
                    line.LineTotalMinor))
                .ToList());

        var writtenFiles = new List<WrittenAuditFile>();
        writtenFiles.AddRange(await _documents.WriteAsync(
            snapshot,
            "receipt",
            cancellationToken));

        if (invoiceNumber is not null)
        {
            writtenFiles.AddRange(await _documents.WriteAsync(
                snapshot,
                "invoice",
                cancellationToken));
        }

        IReadOnlyList<GeneratedSaleDocument> documents =
            await RegisterDocumentsAsync(
                saleId,
                user,
                writtenFiles,
                now,
                cancellationToken);

        return new CompleteSaleResult(
            saleId,
            receiptNumber,
            invoiceNumber,
            user.DisplayName,
            subtotal,
            total,
            request.AmountReceivedMinor,
            change,
            paymentMethod,
            now,
            completedLines,
            documents);
    }

    private static IReadOnlyList<SaleLineRequest> NormalizeLines(
        IReadOnlyList<SaleLineRequest>? items)
    {
        if (items is null || items.Count == 0)
        {
            throw Validation(
                "sale_items_required",
                "Add at least one product to the sale.");
        }

        if (items.Count > 100)
        {
            throw Validation(
                "too_many_sale_items",
                "A sale cannot contain more than 100 lines.");
        }

        foreach (SaleLineRequest item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ProductId) ||
                item.Quantity <= 0)
            {
                throw Validation(
                    "invalid_sale_item",
                    "Every sale item requires a product and positive quantity.");
            }
        }

        return items
            .GroupBy(item => item.ProductId, StringComparer.Ordinal)
            .Select(group => new SaleLineRequest(
                group.Key,
                checked(group.Sum(item => item.Quantity))))
            .ToList();
    }

    private static void ValidateCustomer(CompleteSaleRequest request)
    {
        ValidateLength(request.CustomerName, 150,
            "customer_name_too_long",
            "Customer name cannot exceed 150 characters.");
        ValidateLength(request.CustomerPhone, 50,
            "customer_phone_too_long",
            "Customer phone cannot exceed 50 characters.");
        ValidateLength(request.CustomerAddress, 250,
            "customer_address_too_long",
            "Customer address cannot exceed 250 characters.");
        ValidateLength(request.CustomerTaxNumber, 100,
            "customer_tax_number_too_long",
            "Customer tax number cannot exceed 100 characters.");
        ValidateLength(request.Notes, 500,
            "sale_notes_too_long",
            "Sale notes cannot exceed 500 characters.");

        if (request.IssueInvoice &&
            string.IsNullOrWhiteSpace(request.CustomerName))
        {
            throw Validation(
                "invoice_customer_required",
                "Enter the customer name before issuing an invoice.");
        }
    }

    private static void ValidateLength(
        string? value,
        int maximumLength,
        string code,
        string message)
    {
        if ((value?.Trim().Length ?? 0) > maximumLength)
        {
            throw Validation(code, message);
        }
    }

    private static async Task<string> FindOpenShiftIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT id
        FROM teller_shifts
        WHERE teller_user_id = $userId
          AND status = 'open'
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$userId", userId);

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            throw Conflict(
                "open_shift_required",
                "Open a shift before completing a sale.");
        }

        return Convert.ToString(result)!;
    }

    private static async Task<BusinessSnapshot> ReadBusinessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            business_name,
            address,
            phone,
            email,
            currency_code,
            receipt_footer
        FROM business_settings
        WHERE id = 1;
        """;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SalesException(
                StatusCodes.Status500InternalServerError,
                "business_settings_missing",
                "Business settings are missing.");
        }

        return new BusinessSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    private static async Task<ShopSaleLine> ReadSaleLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        SaleLineRequest requested,
        CancellationToken cancellationToken)
    {
        await using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText =
            """
            INSERT OR IGNORE INTO shop_stock_balances
            (
                shop_id,
                product_id,
                quantity_base_units,
                reserved_base_units,
                version,
                updated_at_utc
            )
            SELECT
                $shopId,
                id,
                0,
                0,
                1,
                $updatedAtUtc
            FROM products
            WHERE id = $productId;
            """;
            ensure.Parameters.AddWithValue("$shopId", shopId);
            ensure.Parameters.AddWithValue("$productId", requested.ProductId);
            ensure.Parameters.AddWithValue(
                "$updatedAtUtc",
                DateTimeOffset.UtcNow.ToString("O"));
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            p.id,
            p.name,
            p.sku,
            p.barcode,
            p.sale_unit,
            p.stock_unit,
            p.glass_size_ml,
            p.units_per_crate,
            p.cost_price_minor,
            p.selling_price_minor,
            p.allow_negative_stock,
            p.is_active,
            sb.quantity_base_units,
            sb.reserved_base_units,
            sb.version
        FROM products AS p
        INNER JOIN shop_stock_balances AS sb
            ON sb.product_id = p.id
           AND sb.shop_id = $shopId
        WHERE p.id = $productId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$productId", requested.ProductId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "product_not_found",
                "A selected product could not be found.");
        }

        if (reader.GetInt32(11) != 1)
        {
            throw Conflict(
                "product_inactive",
                "An inactive product cannot be sold.");
        }

        string saleUnit = reader.GetString(4);
        string stockUnit = reader.GetString(5);
        int? glassSizeMl = reader.IsDBNull(6) ? null : reader.GetInt32(6);
        int? unitsPerCrate = reader.IsDBNull(7) ? null : reader.GetInt32(7);
        long baseUnitsDeducted = CalculateBaseUnits(
            requested.Quantity,
            saleUnit,
            stockUnit,
            glassSizeMl,
            unitsPerCrate);
        long currentBalance = reader.GetInt64(12);
        long reserved = reader.GetInt64(13);
        bool allowNegative = reader.GetInt32(10) == 1;
        long newBalance = checked(currentBalance - baseUnitsDeducted);

        if (!allowNegative && newBalance - reserved < 0)
        {
            throw Conflict(
                "insufficient_stock",
                $"Insufficient stock for {reader.GetString(1)} at this shop.");
        }

        long unitPrice = reader.GetInt64(9);
        return new ShopSaleLine(
            ProductId: reader.GetString(0),
            ProductName: reader.GetString(1),
            Sku: reader.GetString(2),
            Barcode: reader.IsDBNull(3) ? null : reader.GetString(3),
            Quantity: requested.Quantity,
            SaleUnit: saleUnit,
            UnitSizeMl: saleUnit == "glass" ? glassSizeMl : null,
            BaseUnitsDeducted: baseUnitsDeducted,
            UnitCostMinor: reader.GetInt64(8),
            UnitPriceMinor: unitPrice,
            LineTotalMinor: checked(requested.Quantity * unitPrice),
            CurrentStockBalance: currentBalance,
            NewStockBalance: newBalance,
            StockVersion: reader.GetInt32(14));
    }

    private static long CalculateBaseUnits(
        long quantity,
        string saleUnit,
        string stockUnit,
        int? glassSizeMl,
        int? unitsPerCrate)
    {
        if (saleUnit == "glass")
        {
            if (glassSizeMl is null or <= 0)
            {
                throw Validation(
                    "invalid_glass_configuration",
                    "The product has no valid glass size.");
            }

            return checked(quantity * glassSizeMl.Value);
        }

        if (saleUnit == "crate" && stockUnit != "crate")
        {
            if (unitsPerCrate is null or <= 0)
            {
                throw Validation(
                    "invalid_crate_configuration",
                    "The product has no valid units-per-crate setting.");
            }

            return checked(quantity * unitsPerCrate.Value);
        }

        return quantity;
    }

    private static async Task<string> NextDocumentNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentType,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
        """
        SELECT prefix, next_value
        FROM document_sequences
        WHERE document_type = $documentType;
        """;
        read.Parameters.AddWithValue("$documentType", documentType);

        await using var reader =
            await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SalesException(
                StatusCodes.Status500InternalServerError,
                "document_sequence_missing",
                "The document number sequence is missing.");
        }

        string prefix = reader.GetString(0);
        long nextValue = reader.GetInt64(1);
        await reader.DisposeAsync();

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE document_sequences
        SET next_value = next_value + 1,
            updated_at_utc = $updatedAtUtc
        WHERE document_type = $documentType
          AND next_value = $expectedValue;
        """;
        update.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        update.Parameters.AddWithValue("$documentType", documentType);
        update.Parameters.AddWithValue("$expectedValue", nextValue);

        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "document_sequence_conflict",
                "Another sale was completed simultaneously. Try again.");
        }

        return $"{prefix}-{now:yyyyMMdd}-{nextValue:000000}";
    }

    private static async Task InsertSaleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        string saleId,
        string receiptNumber,
        string? invoiceNumber,
        string shiftId,
        AuthenticatedUser user,
        CompleteSaleRequest request,
        long subtotal,
        long total,
        long change,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO sales
        (
            id,
            shop_id,
            receipt_number,
            invoice_number,
            shift_id,
            teller_user_id,
            customer_name,
            customer_phone,
            customer_address,
            customer_tax_number,
            status,
            subtotal_minor,
            discount_minor,
            total_minor,
            amount_received_minor,
            change_minor,
            notes,
            created_at_utc,
            completed_at_utc
        )
        VALUES
        (
            $id,
            $shopId,
            $receiptNumber,
            $invoiceNumber,
            $shiftId,
            $userId,
            $customerName,
            $customerPhone,
            $customerAddress,
            $customerTaxNumber,
            'completed',
            $subtotal,
            0,
            $total,
            $amountReceived,
            $change,
            $notes,
            $createdAtUtc,
            $completedAtUtc
        );
        """;
        command.Parameters.AddWithValue("$id", saleId);
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$receiptNumber", receiptNumber);
        command.Parameters.AddWithValue(
            "$invoiceNumber",
            invoiceNumber ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$shiftId", shiftId);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue(
            "$customerName",
            request.CustomerName?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue(
            "$customerPhone",
            request.CustomerPhone?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue(
            "$customerAddress",
            request.CustomerAddress?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue(
            "$customerTaxNumber",
            request.CustomerTaxNumber?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$subtotal", subtotal);
        command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue(
            "$amountReceived",
            request.AmountReceivedMinor);
        command.Parameters.AddWithValue("$change", change);
        command.Parameters.AddWithValue(
            "$notes",
            request.Notes?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$completedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSaleLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string saleId,
        ShopSaleLine line,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO sale_items
        (
            sale_id,
            product_id,
            product_name_snapshot,
            sku_snapshot,
            barcode_snapshot,
            quantity,
            sale_unit_snapshot,
            unit_size_ml_snapshot,
            base_units_deducted,
            unit_cost_minor,
            unit_price_minor,
            discount_minor,
            line_total_minor,
            returned_quantity
        )
        VALUES
        (
            $saleId,
            $productId,
            $productName,
            $sku,
            $barcode,
            $quantity,
            $saleUnit,
            $unitSizeMl,
            $baseUnitsDeducted,
            $unitCost,
            $unitPrice,
            0,
            $lineTotal,
            0
        );
        """;
        command.Parameters.AddWithValue("$saleId", saleId);
        command.Parameters.AddWithValue("$productId", line.ProductId);
        command.Parameters.AddWithValue("$productName", line.ProductName);
        command.Parameters.AddWithValue("$sku", line.Sku);
        command.Parameters.AddWithValue(
            "$barcode",
            line.Barcode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$quantity", line.Quantity);
        command.Parameters.AddWithValue("$saleUnit", line.SaleUnit);
        command.Parameters.AddWithValue(
            "$unitSizeMl",
            line.UnitSizeMl ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseUnitsDeducted",
            line.BaseUnitsDeducted);
        command.Parameters.AddWithValue("$unitCost", line.UnitCostMinor);
        command.Parameters.AddWithValue("$unitPrice", line.UnitPriceMinor);
        command.Parameters.AddWithValue("$lineTotal", line.LineTotalMinor);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeductStockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string shopId,
        string saleId,
        AuthenticatedUser user,
        ShopSaleLine line,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE shop_stock_balances
        SET quantity_base_units = $newBalance,
            version = version + 1,
            updated_at_utc = $updatedAtUtc
        WHERE shop_id = $shopId
          AND product_id = $productId
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$newBalance", line.NewStockBalance);
        update.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        update.Parameters.AddWithValue("$shopId", shopId);
        update.Parameters.AddWithValue("$productId", line.ProductId);
        update.Parameters.AddWithValue("$expectedVersion", line.StockVersion);

        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "stock_changed",
                $"Stock changed while selling {line.ProductName}. Reload and try again.");
        }

        await ShopInventoryService.InsertMovementAsync(
            connection,
            transaction,
            shopId,
            line.ProductId,
            "sale",
            -line.BaseUnitsDeducted,
            line.NewStockBalance,
            checked(line.UnitCostMinor * line.Quantity),
            "sale",
            saleId,
            "Completed sale",
            user.Id,
            null,
            now,
            cancellationToken);
    }

    private async Task<IReadOnlyList<GeneratedSaleDocument>> RegisterDocumentsAsync(
        string saleId,
        AuthenticatedUser user,
        IReadOnlyList<WrittenAuditFile> files,
        DateTimeOffset generatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);

        var results = new List<GeneratedSaleDocument>();
        foreach (WrittenAuditFile file in files)
        {
            string id = Guid.NewGuid().ToString("N");
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
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
                0,
                1,
                $generatedByUserId,
                $generatedAtUtc
            );
            """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$saleId", saleId);
            command.Parameters.AddWithValue("$documentType", file.DocumentType);
            command.Parameters.AddWithValue("$documentNumber", file.DocumentNumber);
            command.Parameters.AddWithValue("$fileFormat", file.FileFormat);
            command.Parameters.AddWithValue("$relativePath", file.RelativePath);
            command.Parameters.AddWithValue("$fileSha256", file.FileSha256);
            command.Parameters.AddWithValue("$fileSizeBytes", file.FileSizeBytes);
            command.Parameters.AddWithValue("$generatedByUserId", user.Id);
            command.Parameters.AddWithValue(
                "$generatedAtUtc",
                generatedAtUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);

            results.Add(new GeneratedSaleDocument(
                id,
                file.DocumentType,
                file.DocumentNumber,
                file.FileFormat,
                file.RelativePath,
                file.FileSha256,
                file.FileSizeBytes));
        }

        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthenticatedUser user,
        string eventType,
        string entityType,
        string entityId,
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
            $eventType,
            $entityType,
            $entityId,
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
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$entityType", entityType);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SalesException Validation(
        string code,
        string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static SalesException Conflict(
        string code,
        string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private static SalesException NotFound(
        string code,
        string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private sealed record BusinessSnapshot(
        string BusinessName,
        string Address,
        string Phone,
        string Email,
        string CurrencyCode,
        string ReceiptFooter);

    private sealed record ShopSaleLine(
        string ProductId,
        string ProductName,
        string Sku,
        string? Barcode,
        long Quantity,
        string SaleUnit,
        int? UnitSizeMl,
        long BaseUnitsDeducted,
        long UnitCostMinor,
        long UnitPriceMinor,
        long LineTotalMinor,
        long CurrentStockBalance,
        long NewStockBalance,
        int StockVersion);
}
