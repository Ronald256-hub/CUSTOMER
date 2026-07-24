using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Sales;

public sealed class SalesService
{
    private static readonly HashSet<string>
        PaymentMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cash",
            "mobile_money",
            "card",
            "bank"
        };

    private readonly DatabaseBootstrap _database;
    private readonly AuditDocumentWriter _documents;

    public SalesService(
        DatabaseBootstrap database,
        AuditDocumentWriter documents)
    {
        _database = database;
        _documents = documents;
    }

    public async Task<ShiftRecord?>
        GetOpenShiftAsync(
            AuthenticatedUser user,
            CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            s.id,
            s.teller_user_id,
            u.display_name,
            s.status,
            s.opening_cash_minor,
            s.expected_cash_minor,
            s.counted_cash_minor,
            s.cash_variance_minor,
            s.opened_at_utc,
            s.closed_at_utc
        FROM teller_shifts AS s
        INNER JOIN users AS u
            ON u.id = s.teller_user_id
        WHERE s.teller_user_id = $userId
          AND s.status = 'open'
        LIMIT 1;
        """;

        command.Parameters.AddWithValue(
            "$userId",
            user.Id);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            return null;
        }

        return ReadShift(reader);
    }

    public async Task<ShiftRecord>
        OpenShiftAsync(
            AuthenticatedUser user,
            OpenShiftRequest request,
            CancellationToken cancellationToken = default)
    {
        if (request.OpeningCashMinor < 0)
        {
            throw Validation(
                "invalid_opening_cash",
                "Opening cash cannot be negative.");
        }

        string shiftId =
            Guid.NewGuid().ToString("N");

        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            await using var command =
                connection.CreateCommand();

            command.Transaction =
                transaction;

            command.CommandText =
            """
            INSERT INTO teller_shifts
            (
                id,
                teller_user_id,
                status,
                opening_cash_minor,
                opened_at_utc,
                notes
            )
            VALUES
            (
                $id,
                $userId,
                'open',
                $openingCash,
                $openedAtUtc,
                ''
            );
            """;

            command.Parameters.AddWithValue(
                "$id",
                shiftId);

            command.Parameters.AddWithValue(
                "$userId",
                user.Id);

            command.Parameters.AddWithValue(
                "$openingCash",
                request.OpeningCashMinor);

            command.Parameters.AddWithValue(
                "$openedAtUtc",
                now.ToString("O"));

            await command.ExecuteNonQueryAsync(
                cancellationToken);

            await WriteAuditAsync(
                connection,
                transaction,
                user,
                "shift.opened",
                "shift",
                shiftId,
                new
                {
                    openingCashMinor =
                        request.OpeningCashMinor
                },
                cancellationToken);

            await transaction.CommitAsync(
                cancellationToken);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "shift_already_open",
                "This user already has an open shift.");
        }

        return new ShiftRecord(
            shiftId,
            user.Id,
            user.DisplayName,
            "open",
            request.OpeningCashMinor,
            null,
            null,
            null,
            now,
            null);
    }

    public async Task<ShiftRecord>
        CloseShiftAsync(
            AuthenticatedUser user,
            CloseShiftRequest request,
            CancellationToken cancellationToken = default)
    {
        if (request.CountedCashMinor < 0)
        {
            throw Validation(
                "invalid_counted_cash",
                "Counted cash cannot be negative.");
        }

        string notes =
            request.Notes?.Trim()
            ?? string.Empty;

        if (notes.Length > 500)
        {
            throw Validation(
                "shift_notes_too_long",
                "Shift notes cannot exceed 500 characters.");
        }

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(
                cancellationToken);

        await using var find =
            connection.CreateCommand();

        find.Transaction =
            transaction;

        find.CommandText =
        """
        SELECT
            s.id,
            s.opening_cash_minor,
            s.opened_at_utc
        FROM teller_shifts AS s
        WHERE s.teller_user_id = $userId
          AND s.status = 'open'
        LIMIT 1;
        """;

        find.Parameters.AddWithValue(
            "$userId",
            user.Id);

        await using var reader =
            await find.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            throw Conflict(
                "no_open_shift",
                "There is no open shift to close.");
        }

        string shiftId =
            reader.GetString(0);

        long openingCash =
            reader.GetInt64(1);

        DateTimeOffset openedAt =
            DateTimeOffset.Parse(
                reader.GetString(2));

        await reader.DisposeAsync();

        await using var calculateCash =
            connection.CreateCommand();

        calculateCash.Transaction =
            transaction;

        calculateCash.CommandText =
        """
        SELECT COALESCE(SUM(p.amount_minor), 0)
        FROM sale_payments AS p
        INNER JOIN sales AS s
            ON s.id = p.sale_id
        WHERE s.shift_id = $shiftId
          AND s.status = 'completed'
          AND p.payment_method = 'cash';
        """;

        calculateCash.Parameters.AddWithValue(
            "$shiftId",
            shiftId);

        long cashSales =
            Convert.ToInt64(
                await calculateCash.ExecuteScalarAsync(
                    cancellationToken));

        long expectedCash =
            checked(
                openingCash +
                cashSales);

        long variance =
            checked(
                request.CountedCashMinor -
                expectedCash);

        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        await using var update =
            connection.CreateCommand();

        update.Transaction =
            transaction;

        update.CommandText =
        """
        UPDATE teller_shifts
        SET status = 'closed',
            expected_cash_minor = $expectedCash,
            counted_cash_minor = $countedCash,
            cash_variance_minor = $variance,
            closed_at_utc = $closedAtUtc,
            closed_by_user_id = $userId,
            notes = $notes
        WHERE id = $shiftId
          AND status = 'open';
        """;

        update.Parameters.AddWithValue(
            "$expectedCash",
            expectedCash);

        update.Parameters.AddWithValue(
            "$countedCash",
            request.CountedCashMinor);

        update.Parameters.AddWithValue(
            "$variance",
            variance);

        update.Parameters.AddWithValue(
            "$closedAtUtc",
            now.ToString("O"));

        update.Parameters.AddWithValue(
            "$userId",
            user.Id);

        update.Parameters.AddWithValue(
            "$notes",
            notes);

        update.Parameters.AddWithValue(
            "$shiftId",
            shiftId);

        await update.ExecuteNonQueryAsync(
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "shift.closed",
            "shift",
            shiftId,
            new
            {
                openingCash,
                cashSales,
                expectedCash,
                countedCash =
                    request.CountedCashMinor,
                variance
            },
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        return new ShiftRecord(
            shiftId,
            user.Id,
            user.DisplayName,
            "closed",
            openingCash,
            expectedCash,
            request.CountedCashMinor,
            variance,
            openedAt,
            now);
    }

    public async Task<CompleteSaleResult>
        CompleteSaleAsync(
            AuthenticatedUser user,
            CompleteSaleRequest request,
            CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SaleLineRequest>
            requestedLines =
            NormalizeLines(request.Items);

        string paymentMethod =
            request.PaymentMethod?
                .Trim()
                .ToLowerInvariant()
            ?? string.Empty;

        if (!PaymentMethods.Contains(
                paymentMethod))
        {
            throw Validation(
                "invalid_payment_method",
                "Use cash, mobile money, card or bank.");
        }

        ValidateCustomer(request);

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(
                cancellationToken);

        string shiftId =
            await FindOpenShiftIdAsync(
                connection,
                transaction,
                user.Id,
                cancellationToken);

        BusinessSnapshot business =
            await ReadBusinessAsync(
                connection,
                transaction,
                cancellationToken);

        var lines =
            new List<SaleLineDatabaseRecord>();

        long subtotal = 0;

        foreach (SaleLineRequest requested
                 in requestedLines)
        {
            SaleLineDatabaseRecord line =
                await ReadSaleLineAsync(
                    connection,
                    transaction,
                    requested,
                    cancellationToken);

            subtotal =
                checked(
                    subtotal +
                    line.LineTotalMinor);

            lines.Add(line);
        }

        if (subtotal <= 0)
        {
            throw Validation(
                "sale_total_must_be_positive",
                "The sale total must be greater than zero.");
        }

        long total =
            subtotal;

        if (paymentMethod == "cash")
        {
            if (request.AmountReceivedMinor <
                total)
            {
                throw Validation(
                    "insufficient_payment",
                    "The amount received is less than the sale total.");
            }
        }
        else if (request.AmountReceivedMinor !=
                 total)
        {
            throw Validation(
                "payment_amount_mismatch",
                "Non-cash payments must equal the exact sale total.");
        }

        long change =
            paymentMethod == "cash"
                ? request.AmountReceivedMinor -
                  total
                : 0;

        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        string saleId =
            Guid.NewGuid().ToString("N");

        string receiptNumber =
            await NextDocumentNumberAsync(
                connection,
                transaction,
                "receipt",
                now,
                cancellationToken);

        string? invoiceNumber =
            request.IssueInvoice
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
            saleId,
            receiptNumber,
            invoiceNumber,
            shiftId,
            user,
            request,
            paymentMethod,
            subtotal,
            total,
            change,
            now,
            cancellationToken);

        foreach (SaleLineDatabaseRecord line
                 in lines)
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
                saleId,
                user,
                line,
                now,
                cancellationToken);
        }

        await using var payment =
            connection.CreateCommand();

        payment.Transaction =
            transaction;

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

        payment.Parameters.AddWithValue(
            "$saleId",
            saleId);

        payment.Parameters.AddWithValue(
            "$paymentMethod",
            paymentMethod);

        payment.Parameters.AddWithValue(
            "$amount",
            total);

        payment.Parameters.AddWithValue(
            "$receivedAtUtc",
            now.ToString("O"));

        await payment.ExecuteNonQueryAsync(
            cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "sale.completed",
            "sale",
            saleId,
            new
            {
                receiptNumber,
                invoiceNumber,
                paymentMethod,
                totalMinor = total,
                itemCount = lines.Count
            },
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        var completedLines =
            lines.Select(
                    line =>
                        new CompletedSaleLine(
                            line.ProductId,
                            line.ProductName,
                            line.Sku,
                            line.Quantity,
                            line.SaleUnit,
                            line.UnitSizeMl,
                            line.UnitPriceMinor,
                            line.LineTotalMinor))
                .ToList();

        var snapshot =
            new AuditDocumentSnapshot(
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
                request.CustomerName?.Trim()
                    ?? string.Empty,
                request.CustomerPhone?.Trim()
                    ?? string.Empty,
                request.CustomerAddress?.Trim()
                    ?? string.Empty,
                request.CustomerTaxNumber?.Trim()
                    ?? string.Empty,
                paymentMethod,
                subtotal,
                0,
                total,
                request.AmountReceivedMinor,
                change,
                request.Notes?.Trim()
                    ?? string.Empty,
                now,
                lines.Select(
                        line =>
                            new AuditDocumentLine(
                                line.ProductName,
                                line.Sku,
                                line.Quantity,
                                line.SaleUnit,
                                line.UnitSizeMl,
                                line.UnitPriceMinor,
                                line.LineTotalMinor))
                    .ToList());

        var writtenFiles =
            new List<WrittenAuditFile>();

        writtenFiles.AddRange(
            await _documents.WriteAsync(
                snapshot,
                "receipt",
                cancellationToken));

        if (invoiceNumber is not null)
        {
            writtenFiles.AddRange(
                await _documents.WriteAsync(
                    snapshot,
                    "invoice",
                    cancellationToken));
        }

        IReadOnlyList<GeneratedSaleDocument>
            registeredDocuments =
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
            registeredDocuments);
    }

    public async Task<IReadOnlyList<ReceiptListItem>>
        ListReceiptsAsync(
            AuthenticatedUser user,
            int requestedLimit,
            CancellationToken cancellationToken = default)
    {
        int limit =
            Math.Clamp(
                requestedLimit,
                1,
                500);

        bool isAdmin =
            string.Equals(
                user.Role,
                "admin",
                StringComparison.OrdinalIgnoreCase);

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            s.id,
            s.receipt_number,
            s.invoice_number,
            u.display_name,
            s.status,
            s.total_minor,
            COALESCE(p.payment_method, ''),
            s.completed_at_utc,
            (
                SELECT COUNT(*)
                FROM sale_documents AS d
                WHERE d.sale_id = s.id
            )
        FROM sales AS s
        INNER JOIN users AS u
            ON u.id = s.teller_user_id
        LEFT JOIN sale_payments AS p
            ON p.sale_id = s.id
        WHERE
            $isAdmin = 1
            OR s.teller_user_id = $userId
        ORDER BY s.completed_at_utc DESC
        LIMIT $limit;
        """;

        command.Parameters.AddWithValue(
            "$isAdmin",
            isAdmin ? 1 : 0);

        command.Parameters.AddWithValue(
            "$userId",
            user.Id);

        command.Parameters.AddWithValue(
            "$limit",
            limit);

        var receipts =
            new List<ReceiptListItem>();

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            receipts.Add(
                new ReceiptListItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2)
                        ? null
                        : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetString(6),
                    DateTimeOffset.Parse(
                        reader.GetString(7)),
                    reader.GetInt32(8)));
        }

        return receipts;
    }

    public async Task<ReceiptDetails>
        GetReceiptAsync(
            AuthenticatedUser user,
            string saleId,
            CancellationToken cancellationToken = default)
    {
        bool isAdmin =
            string.Equals(
                user.Role,
                "admin",
                StringComparison.OrdinalIgnoreCase);

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var saleCommand =
            connection.CreateCommand();

        saleCommand.CommandText =
        """
        SELECT
            s.id,
            s.receipt_number,
            s.invoice_number,
            u.display_name,
            s.status,
            s.customer_name,
            s.customer_phone,
            s.customer_address,
            s.customer_tax_number,
            s.subtotal_minor,
            s.discount_minor,
            s.total_minor,
            s.amount_received_minor,
            s.change_minor,
            COALESCE(p.payment_method, ''),
            s.notes,
            s.completed_at_utc
        FROM sales AS s
        INNER JOIN users AS u
            ON u.id = s.teller_user_id
        LEFT JOIN sale_payments AS p
            ON p.sale_id = s.id
        WHERE s.id = $saleId
          AND
          (
              $isAdmin = 1
              OR s.teller_user_id = $userId
          )
        LIMIT 1;
        """;

        saleCommand.Parameters.AddWithValue(
            "$saleId",
            saleId);

        saleCommand.Parameters.AddWithValue(
            "$isAdmin",
            isAdmin ? 1 : 0);

        saleCommand.Parameters.AddWithValue(
            "$userId",
            user.Id);

        await using var saleReader =
            await saleCommand.ExecuteReaderAsync(
                cancellationToken);

        if (!await saleReader.ReadAsync(
                cancellationToken))
        {
            throw NotFound(
                "receipt_not_found",
                "The receipt could not be found.");
        }

        string receiptNumber =
            saleReader.GetString(1);

        string? invoiceNumber =
            saleReader.IsDBNull(2)
                ? null
                : saleReader.GetString(2);

        string tellerName =
            saleReader.GetString(3);

        string status =
            saleReader.GetString(4);

        string customerName =
            saleReader.GetString(5);

        string customerPhone =
            saleReader.GetString(6);

        string customerAddress =
            saleReader.GetString(7);

        string customerTaxNumber =
            saleReader.GetString(8);

        long subtotal =
            saleReader.GetInt64(9);

        long discount =
            saleReader.GetInt64(10);

        long total =
            saleReader.GetInt64(11);

        long amountReceived =
            saleReader.GetInt64(12);

        long change =
            saleReader.GetInt64(13);

        string paymentMethod =
            saleReader.GetString(14);

        string notes =
            saleReader.GetString(15);

        DateTimeOffset completedAt =
            DateTimeOffset.Parse(
                saleReader.GetString(16));

        await saleReader.DisposeAsync();

        IReadOnlyList<CompletedSaleLine> items =
            await ReadReceiptItemsAsync(
                connection,
                saleId,
                cancellationToken);

        IReadOnlyList<GeneratedSaleDocument>
            documents =
            await ReadDocumentsAsync(
                connection,
                saleId,
                cancellationToken);

        return new ReceiptDetails(
            saleId,
            receiptNumber,
            invoiceNumber,
            tellerName,
            status,
            customerName,
            customerPhone,
            customerAddress,
            customerTaxNumber,
            subtotal,
            discount,
            total,
            amountReceived,
            change,
            paymentMethod,
            notes,
            completedAt,
            items,
            documents);
    }

    public async Task<StoredDocumentFile>
        ResolveDocumentAsync(
            AuthenticatedUser user,
            string saleId,
            string documentId,
            CancellationToken cancellationToken = default)
    {
        bool isAdmin =
            string.Equals(
                user.Role,
                "admin",
                StringComparison.OrdinalIgnoreCase);

        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            d.relative_path,
            d.file_format,
            d.document_number
        FROM sale_documents AS d
        INNER JOIN sales AS s
            ON s.id = d.sale_id
        WHERE d.id = $documentId
          AND d.sale_id = $saleId
          AND
          (
              $isAdmin = 1
              OR s.teller_user_id = $userId
          )
        LIMIT 1;
        """;

        command.Parameters.AddWithValue(
            "$documentId",
            documentId);

        command.Parameters.AddWithValue(
            "$saleId",
            saleId);

        command.Parameters.AddWithValue(
            "$isAdmin",
            isAdmin ? 1 : 0);

        command.Parameters.AddWithValue(
            "$userId",
            user.Id);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            throw NotFound(
                "receipt_document_not_found",
                "The receipt document could not be found.");
        }

        string relativePath =
            reader.GetString(0);

        string format =
            reader.GetString(1);

        string documentNumber =
            reader.GetString(2);

        string fullPath =
            _documents.ResolveStoredPath(
                relativePath);

        if (!File.Exists(fullPath))
        {
            throw NotFound(
                "receipt_file_missing",
                "The saved receipt file is missing from the audit folder.");
        }

        string contentType =
            format switch
            {
                "pdf" =>
                    "application/pdf",
                "html" =>
                    "text/html; charset=utf-8",
                "json" =>
                    "application/json; charset=utf-8",
                _ =>
                    "application/octet-stream"
            };

        return new StoredDocumentFile(
            fullPath,
            contentType,
            $"{documentNumber}.{format}");
    }

    private static IReadOnlyList<SaleLineRequest>
        NormalizeLines(
            IReadOnlyList<SaleLineRequest>? items)
    {
        if (items is null ||
            items.Count == 0)
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

        foreach (SaleLineRequest item
                 in items)
        {
            if (string.IsNullOrWhiteSpace(
                    item.ProductId) ||
                item.Quantity <= 0)
            {
                throw Validation(
                    "invalid_sale_item",
                    "Every sale item requires a product and positive quantity.");
            }
        }

        return items
            .GroupBy(
                item => item.ProductId,
                StringComparer.Ordinal)
            .Select(
                group =>
                    new SaleLineRequest(
                        group.Key,
                        checked(
                            group.Sum(
                                item => item.Quantity))))
            .ToList();
    }

    private static void ValidateCustomer(
        CompleteSaleRequest request)
    {
        ValidateLength(
            request.CustomerName,
            150,
            "customer_name_too_long",
            "Customer name cannot exceed 150 characters.");

        ValidateLength(
            request.CustomerPhone,
            50,
            "customer_phone_too_long",
            "Customer phone cannot exceed 50 characters.");

        ValidateLength(
            request.CustomerAddress,
            250,
            "customer_address_too_long",
            "Customer address cannot exceed 250 characters.");

        ValidateLength(
            request.CustomerTaxNumber,
            100,
            "customer_tax_number_too_long",
            "Customer tax number cannot exceed 100 characters.");

        ValidateLength(
            request.Notes,
            500,
            "sale_notes_too_long",
            "Sale notes cannot exceed 500 characters.");

        if (request.IssueInvoice &&
            string.IsNullOrWhiteSpace(
                request.CustomerName))
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
        if ((value?.Trim().Length ?? 0) >
            maximumLength)
        {
            throw Validation(
                code,
                message);
        }
    }

    private static async Task<string>
        FindOpenShiftIdAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string userId,
            CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
        """
        SELECT id
        FROM teller_shifts
        WHERE teller_user_id = $userId
          AND status = 'open'
        LIMIT 1;
        """;

        command.Parameters.AddWithValue(
            "$userId",
            userId);

        object? result =
            await command.ExecuteScalarAsync(
                cancellationToken);

        if (result is null)
        {
            throw Conflict(
                "open_shift_required",
                "Open a shift before completing a sale.");
        }

        return Convert.ToString(result)!;
    }

    private static async Task<BusinessSnapshot>
        ReadBusinessAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

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
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
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

    private static async Task<SaleLineDatabaseRecord>
        ReadSaleLineAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SaleLineRequest requested,
            CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

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
        INNER JOIN stock_balances AS sb
            ON sb.product_id = p.id
        WHERE p.id = $productId
        LIMIT 1;
        """;

        command.Parameters.AddWithValue(
            "$productId",
            requested.ProductId);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            throw NotFound(
                "product_not_found",
                "A selected product could not be found.");
        }

        bool active =
            reader.GetInt32(11) == 1;

        if (!active)
        {
            throw Conflict(
                "product_inactive",
                "An inactive product cannot be sold.");
        }

        string saleUnit =
            reader.GetString(4);

        string stockUnit =
            reader.GetString(5);

        int? glassSizeMl =
            reader.IsDBNull(6)
                ? null
                : reader.GetInt32(6);

        int? unitsPerCrate =
            reader.IsDBNull(7)
                ? null
                : reader.GetInt32(7);

        long baseUnitsDeducted =
            CalculateBaseUnits(
                requested.Quantity,
                saleUnit,
                stockUnit,
                glassSizeMl,
                unitsPerCrate);

        long quantityBalance =
            reader.GetInt64(12);

        long reserved =
            reader.GetInt64(13);

        bool allowNegative =
            reader.GetInt32(10) == 1;

        long newBalance =
            checked(
                quantityBalance -
                baseUnitsDeducted);

        if (!allowNegative &&
            newBalance - reserved < 0)
        {
            throw Conflict(
                "insufficient_stock",
                $"Insufficient stock for {reader.GetString(1)}.");
        }

        long unitPrice =
            reader.GetInt64(9);

        long lineTotal =
            checked(
                requested.Quantity *
                unitPrice);

        return new SaleLineDatabaseRecord(
            ProductId: reader.GetString(0),
            ProductName: reader.GetString(1),
            Sku: reader.GetString(2),
            Barcode:
                reader.IsDBNull(3)
                    ? null
                    : reader.GetString(3),
            Quantity: requested.Quantity,
            SaleUnit: saleUnit,
            UnitSizeMl:
                saleUnit == "glass"
                    ? glassSizeMl
                    : null,
            BaseUnitsDeducted:
                baseUnitsDeducted,
            UnitCostMinor:
                reader.GetInt64(8),
            UnitPriceMinor:
                unitPrice,
            LineTotalMinor:
                lineTotal,
            CurrentStockBalance:
                quantityBalance,
            NewStockBalance:
                newBalance,
            StockVersion:
                reader.GetInt32(14));
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

            return checked(
                quantity *
                glassSizeMl.Value);
        }

        if (saleUnit == "crate" &&
            stockUnit != "crate")
        {
            if (unitsPerCrate is null or <= 0)
            {
                throw Validation(
                    "invalid_crate_configuration",
                    "The product has no valid units-per-crate setting.");
            }

            return checked(
                quantity *
                unitsPerCrate.Value);
        }

        return quantity;
    }

    private static async Task<string>
        NextDocumentNumberAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string documentType,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        await using var read =
            connection.CreateCommand();

        read.Transaction =
            transaction;

        read.CommandText =
        """
        SELECT prefix, next_value
        FROM document_sequences
        WHERE document_type = $documentType;
        """;

        read.Parameters.AddWithValue(
            "$documentType",
            documentType);

        await using var reader =
            await read.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            throw new SalesException(
                StatusCodes.Status500InternalServerError,
                "document_sequence_missing",
                "The document number sequence is missing.");
        }

        string prefix =
            reader.GetString(0);

        long nextValue =
            reader.GetInt64(1);

        await reader.DisposeAsync();

        await using var update =
            connection.CreateCommand();

        update.Transaction =
            transaction;

        update.CommandText =
        """
        UPDATE document_sequences
        SET next_value = next_value + 1,
            updated_at_utc = $updatedAtUtc
        WHERE document_type = $documentType
          AND next_value = $expectedValue;
        """;

        update.Parameters.AddWithValue(
            "$updatedAtUtc",
            now.ToString("O"));

        update.Parameters.AddWithValue(
            "$documentType",
            documentType);

        update.Parameters.AddWithValue(
            "$expectedValue",
            nextValue);

        int affected =
            await update.ExecuteNonQueryAsync(
                cancellationToken);

        if (affected != 1)
        {
            throw Conflict(
                "document_sequence_conflict",
                "Another sale was completed simultaneously. Try again.");
        }

        return
            $"{prefix}-" +
            $"{now:yyyyMMdd}-" +
            $"{nextValue:000000}";
    }

    private static async Task InsertSaleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string saleId,
        string receiptNumber,
        string? invoiceNumber,
        string shiftId,
        AuthenticatedUser user,
        CompleteSaleRequest request,
        string paymentMethod,
        long subtotal,
        long total,
        long change,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
        """
        INSERT INTO sales
        (
            id,
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

        command.Parameters.AddWithValue(
            "$id",
            saleId);

        command.Parameters.AddWithValue(
            "$receiptNumber",
            receiptNumber);

        command.Parameters.AddWithValue(
            "$invoiceNumber",
            invoiceNumber ??
            (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$shiftId",
            shiftId);

        command.Parameters.AddWithValue(
            "$userId",
            user.Id);

        command.Parameters.AddWithValue(
            "$customerName",
            request.CustomerName?.Trim()
            ?? string.Empty);

        command.Parameters.AddWithValue(
            "$customerPhone",
            request.CustomerPhone?.Trim()
            ?? string.Empty);

        command.Parameters.AddWithValue(
            "$customerAddress",
            request.CustomerAddress?.Trim()
            ?? string.Empty);

        command.Parameters.AddWithValue(
            "$customerTaxNumber",
            request.CustomerTaxNumber?.Trim()
            ?? string.Empty);

        command.Parameters.AddWithValue(
            "$subtotal",
            subtotal);

        command.Parameters.AddWithValue(
            "$total",
            total);

        command.Parameters.AddWithValue(
            "$amountReceived",
            request.AmountReceivedMinor);

        command.Parameters.AddWithValue(
            "$change",
            change);

        command.Parameters.AddWithValue(
            "$notes",
            request.Notes?.Trim()
            ?? string.Empty);

        command.Parameters.AddWithValue(
            "$createdAtUtc",
            now.ToString("O"));

        command.Parameters.AddWithValue(
            "$completedAtUtc",
            now.ToString("O"));

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static async Task InsertSaleLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string saleId,
        SaleLineDatabaseRecord line,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

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

        command.Parameters.AddWithValue(
            "$saleId",
            saleId);

        command.Parameters.AddWithValue(
            "$productId",
            line.ProductId);

        command.Parameters.AddWithValue(
            "$productName",
            line.ProductName);

        command.Parameters.AddWithValue(
            "$sku",
            line.Sku);

        command.Parameters.AddWithValue(
            "$barcode",
            line.Barcode ??
            (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$quantity",
            line.Quantity);

        command.Parameters.AddWithValue(
            "$saleUnit",
            line.SaleUnit);

        command.Parameters.AddWithValue(
            "$unitSizeMl",
            line.UnitSizeMl ??
            (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$baseUnitsDeducted",
            line.BaseUnitsDeducted);

        command.Parameters.AddWithValue(
            "$unitCost",
            line.UnitCostMinor);

        command.Parameters.AddWithValue(
            "$unitPrice",
            line.UnitPriceMinor);

        command.Parameters.AddWithValue(
            "$lineTotal",
            line.LineTotalMinor);

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static async Task DeductStockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string saleId,
        AuthenticatedUser user,
        SaleLineDatabaseRecord line,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var update =
            connection.CreateCommand();

        update.Transaction =
            transaction;

        update.CommandText =
        """
        UPDATE stock_balances
        SET quantity_base_units = $newBalance,
            version = version + 1,
            updated_at_utc = $updatedAtUtc
        WHERE product_id = $productId
          AND version = $expectedVersion;
        """;

        update.Parameters.AddWithValue(
            "$newBalance",
            line.NewStockBalance);

        update.Parameters.AddWithValue(
            "$updatedAtUtc",
            now.ToString("O"));

        update.Parameters.AddWithValue(
            "$productId",
            line.ProductId);

        update.Parameters.AddWithValue(
            "$expectedVersion",
            line.StockVersion);

        int affected =
            await update.ExecuteNonQueryAsync(
                cancellationToken);

        if (affected != 1)
        {
            throw Conflict(
                "stock_changed",
                $"Stock changed while selling {line.ProductName}. Reload and try again.");
        }

        await using var movement =
            connection.CreateCommand();

        movement.Transaction =
            transaction;

        movement.CommandText =
        """
        INSERT INTO stock_movements
        (
            product_id,
            movement_type,
            quantity_delta_base,
            balance_after_base,
            cost_value_minor,
            reference_type,
            reference_id,
            reason,
            performed_by_user_id,
            approved_by_user_id,
            occurred_at_utc
        )
        VALUES
        (
            $productId,
            'sale',
            $quantityDelta,
            $balanceAfter,
            $costValue,
            'sale',
            $saleId,
            'Completed sale',
            $userId,
            NULL,
            $occurredAtUtc
        );
        """;

        movement.Parameters.AddWithValue(
            "$productId",
            line.ProductId);

        movement.Parameters.AddWithValue(
            "$quantityDelta",
            -line.BaseUnitsDeducted);

        movement.Parameters.AddWithValue(
            "$balanceAfter",
            line.NewStockBalance);

        movement.Parameters.AddWithValue(
            "$costValue",
            checked(
                line.UnitCostMinor *
                line.Quantity));

        movement.Parameters.AddWithValue(
            "$saleId",
            saleId);

        movement.Parameters.AddWithValue(
            "$userId",
            user.Id);

        movement.Parameters.AddWithValue(
            "$occurredAtUtc",
            now.ToString("O"));

        await movement.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private async Task<IReadOnlyList<GeneratedSaleDocument>>
        RegisterDocumentsAsync(
            string saleId,
            AuthenticatedUser user,
            IReadOnlyList<WrittenAuditFile> files,
            DateTimeOffset generatedAtUtc,
            CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(
                _database.ConnectionString);

        await connection.OpenAsync(
            cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(
                cancellationToken);

        var results =
            new List<GeneratedSaleDocument>();

        foreach (WrittenAuditFile file
                 in files)
        {
            string id =
                Guid.NewGuid().ToString("N");

            await using var command =
                connection.CreateCommand();

            command.Transaction =
                transaction;

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

            command.Parameters.AddWithValue(
                "$id",
                id);

            command.Parameters.AddWithValue(
                "$saleId",
                saleId);

            command.Parameters.AddWithValue(
                "$documentType",
                file.DocumentType);

            command.Parameters.AddWithValue(
                "$documentNumber",
                file.DocumentNumber);

            command.Parameters.AddWithValue(
                "$fileFormat",
                file.FileFormat);

            command.Parameters.AddWithValue(
                "$relativePath",
                file.RelativePath);

            command.Parameters.AddWithValue(
                "$fileSha256",
                file.FileSha256);

            command.Parameters.AddWithValue(
                "$fileSizeBytes",
                file.FileSizeBytes);

            command.Parameters.AddWithValue(
                "$generatedByUserId",
                user.Id);

            command.Parameters.AddWithValue(
                "$generatedAtUtc",
                generatedAtUtc.ToString("O"));

            await command.ExecuteNonQueryAsync(
                cancellationToken);

            results.Add(
                new GeneratedSaleDocument(
                    id,
                    file.DocumentType,
                    file.DocumentNumber,
                    file.FileFormat,
                    file.RelativePath,
                    file.FileSha256,
                    file.FileSizeBytes));
        }

        await transaction.CommitAsync(
            cancellationToken);

        return results;
    }

    private static async Task<IReadOnlyList<CompletedSaleLine>>
        ReadReceiptItemsAsync(
            SqliteConnection connection,
            string saleId,
            CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

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

        command.Parameters.AddWithValue(
            "$saleId",
            saleId);

        var items =
            new List<CompletedSaleLine>();

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            items.Add(
                new CompletedSaleLine(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.IsDBNull(5)
                        ? null
                        : reader.GetInt32(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7)));
        }

        return items;
    }

    private static async Task<IReadOnlyList<GeneratedSaleDocument>>
        ReadDocumentsAsync(
            SqliteConnection connection,
            string saleId,
            CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

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
        ORDER BY document_type, file_format;
        """;

        command.Parameters.AddWithValue(
            "$saleId",
            saleId);

        var documents =
            new List<GeneratedSaleDocument>();

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            documents.Add(
                new GeneratedSaleDocument(
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

    private static ShiftRecord ReadShift(
        SqliteDataReader reader)
    {
        return new ShiftRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5)
                ? null
                : reader.GetInt64(5),
            reader.IsDBNull(6)
                ? null
                : reader.GetInt64(6),
            reader.IsDBNull(7)
                ? null
                : reader.GetInt64(7),
            DateTimeOffset.Parse(
                reader.GetString(8)),
            reader.IsDBNull(9)
                ? null
                : DateTimeOffset.Parse(
                    reader.GetString(9)));
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
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

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

        command.Parameters.AddWithValue(
            "$userId",
            user.Id);

        command.Parameters.AddWithValue(
            "$username",
            user.Username);

        command.Parameters.AddWithValue(
            "$eventType",
            eventType);

        command.Parameters.AddWithValue(
            "$entityType",
            entityType);

        command.Parameters.AddWithValue(
            "$entityId",
            entityId);

        command.Parameters.AddWithValue(
            "$detailsJson",
            JsonSerializer.Serialize(details));

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static SalesException Validation(
        string code,
        string message)
    {
        return new SalesException(
            StatusCodes.Status400BadRequest,
            code,
            message);
    }

    private static SalesException Conflict(
        string code,
        string message)
    {
        return new SalesException(
            StatusCodes.Status409Conflict,
            code,
            message);
    }

    private static SalesException NotFound(
        string code,
        string message)
    {
        return new SalesException(
            StatusCodes.Status404NotFound,
            code,
            message);
    }

    private sealed record BusinessSnapshot(
        string BusinessName,
        string Address,
        string Phone,
        string Email,
        string CurrencyCode,
        string ReceiptFooter);

    private sealed record SaleLineDatabaseRecord(
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
