using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Data;
using Robo.Pos.Server.Security;

namespace Robo.Pos.Server.Business;

public sealed class BusinessOperationsService
{
    private static readonly HashSet<string> ExpensePaymentMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cash",
            "mobile_money",
            "bank",
            "other"
        };

    private readonly DatabaseBootstrap _database;

    public BusinessOperationsService(
        DatabaseBootstrap database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<SupplierResult>>
        ListSuppliersAsync(
            bool includeInactive,
            CancellationToken cancellationToken = default)
    {
        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            id,
            name,
            phone,
            email,
            address,
            notes,
            is_active,
            created_at_utc,
            updated_at_utc
        FROM suppliers
        WHERE $includeInactive = 1
           OR is_active = 1
        ORDER BY name COLLATE NOCASE;
        """;

        command.Parameters.AddWithValue(
            "$includeInactive",
            includeInactive ? 1 : 0);

        var suppliers = new List<SupplierResult>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            suppliers.Add(ReadSupplier(reader));
        }

        return suppliers;
    }

    public async Task<SupplierResult>
        CreateSupplierAsync(
            AuthenticatedUser user,
            CreateSupplierRequest request,
            CancellationToken cancellationToken = default)
    {
        string name = RequiredText(
            request.Name,
            150,
            "supplier_name_required",
            "Enter the supplier name.");

        string phone = OptionalText(request.Phone, 50);
        string email = OptionalText(request.Email, 150);
        string address = OptionalText(request.Address, 250);
        string notes = OptionalText(request.Notes, 500);

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
        """
        INSERT INTO suppliers
        (
            id,
            name,
            phone,
            email,
            address,
            notes,
            is_active,
            created_by_user_id,
            updated_by_user_id,
            created_at_utc,
            updated_at_utc
        )
        VALUES
        (
            $id,
            $name,
            $phone,
            $email,
            $address,
            $notes,
            1,
            $userId,
            $userId,
            $now,
            $now
        );
        """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$phone", phone);
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$address", address);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "supplier.created",
            "supplier",
            id,
            new { name },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new SupplierResult(
            id,
            name,
            phone,
            email,
            address,
            notes,
            true,
            now,
            now);
    }

    public async Task<SupplierResult>
        UpdateSupplierAsync(
            AuthenticatedUser user,
            string supplierId,
            UpdateSupplierRequest request,
            CancellationToken cancellationToken = default)
    {
        string name = RequiredText(
            request.Name,
            150,
            "supplier_name_required",
            "Enter the supplier name.");

        string phone = OptionalText(request.Phone, 50);
        string email = OptionalText(request.Email, 150);
        string address = OptionalText(request.Address, 250);
        string notes = OptionalText(request.Notes, 500);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
        """
        UPDATE suppliers
        SET name = $name,
            phone = $phone,
            email = $email,
            address = $address,
            notes = $notes,
            is_active = $isActive,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE id = $id;
        """;

        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$phone", phone);
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$address", address);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue(
            "$isActive",
            request.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", supplierId);

        int affected =
            await command.ExecuteNonQueryAsync(cancellationToken);

        if (affected != 1)
        {
            throw NotFound(
                "supplier_not_found",
                "The supplier could not be found.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "supplier.updated",
            "supplier",
            supplierId,
            new
            {
                name,
                request.IsActive
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new SupplierResult(
            supplierId,
            name,
            phone,
            email,
            address,
            notes,
            request.IsActive,
            now,
            now);
    }

    public async Task<PurchaseResult>
        ReceivePurchaseAsync(
            AuthenticatedUser user,
            ReceivePurchaseRequest request,
            CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PurchaseItemRequest> items =
            ValidatePurchaseItems(request.Items);

        string? supplierId =
            string.IsNullOrWhiteSpace(request.SupplierId)
                ? null
                : request.SupplierId.Trim();

        string supplierInvoiceNumber =
            OptionalText(request.SupplierInvoiceNumber, 100);

        string notes = OptionalText(request.Notes, 500);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        string supplierName =
            await ResolveSupplierAsync(
                connection,
                transaction,
                supplierId,
                cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        string purchaseId = Guid.NewGuid().ToString("N");

        string purchaseNumber =
            await NextDocumentNumberAsync(
                connection,
                transaction,
                "purchase",
                now,
                cancellationToken);

        var purchaseItems = new List<PurchaseItemResult>();
        long subtotal = 0;

        foreach (PurchaseItemRequest item in items)
        {
            ProductForPurchase product =
                await ReadProductForPurchaseAsync(
                    connection,
                    transaction,
                    item.ProductId,
                    cancellationToken);

            string batchNumber =
                OptionalText(item.BatchNumber, 100);

            string? expiryDate =
                ValidateOptionalDate(item.ExpiryDate);

            long lineTotal =
                checked(
                    item.QuantityBaseUnits *
                    item.UnitCostMinor);

            subtotal = checked(subtotal + lineTotal);

            await using var insertItem =
                connection.CreateCommand();

            insertItem.Transaction = transaction;

            insertItem.CommandText =
            """
            INSERT INTO purchase_items
            (
                purchase_id,
                product_id,
                product_name_snapshot,
                sku_snapshot,
                quantity_base_units,
                unit_cost_minor,
                line_total_minor,
                batch_number,
                expiry_date
            )
            VALUES
            (
                $purchaseId,
                $productId,
                $productName,
                $sku,
                $quantity,
                $unitCost,
                $lineTotal,
                $batchNumber,
                $expiryDate
            );
            """;

            insertItem.Parameters.AddWithValue(
                "$purchaseId",
                purchaseId);

            insertItem.Parameters.AddWithValue(
                "$productId",
                product.Id);

            insertItem.Parameters.AddWithValue(
                "$productName",
                product.Name);

            insertItem.Parameters.AddWithValue(
                "$sku",
                product.Sku);

            insertItem.Parameters.AddWithValue(
                "$quantity",
                item.QuantityBaseUnits);

            insertItem.Parameters.AddWithValue(
                "$unitCost",
                item.UnitCostMinor);

            insertItem.Parameters.AddWithValue(
                "$lineTotal",
                lineTotal);

            insertItem.Parameters.AddWithValue(
                "$batchNumber",
                batchNumber);

            insertItem.Parameters.AddWithValue(
                "$expiryDate",
                expiryDate ?? (object)DBNull.Value);

            await insertItem.ExecuteNonQueryAsync(cancellationToken);

            long newBalance =
                checked(
                    product.CurrentStock +
                    item.QuantityBaseUnits);

            await using var updateStock =
                connection.CreateCommand();

            updateStock.Transaction = transaction;

            updateStock.CommandText =
            """
            UPDATE stock_balances
            SET quantity_base_units = $newBalance,
                version = version + 1,
                updated_at_utc = $now
            WHERE product_id = $productId
              AND version = $expectedVersion;
            """;

            updateStock.Parameters.AddWithValue(
                "$newBalance",
                newBalance);

            updateStock.Parameters.AddWithValue(
                "$now",
                now.ToString("O"));

            updateStock.Parameters.AddWithValue(
                "$productId",
                product.Id);

            updateStock.Parameters.AddWithValue(
                "$expectedVersion",
                product.StockVersion);

            int updated =
                await updateStock.ExecuteNonQueryAsync(
                    cancellationToken);

            if (updated != 1)
            {
                throw Conflict(
                    "stock_changed",
                    $"Stock changed while receiving {product.Name}. Try again.");
            }

            await InsertStockMovementAsync(
                connection,
                transaction,
                product.Id,
                item.QuantityBaseUnits,
                newBalance,
                lineTotal,
                purchaseId,
                purchaseNumber,
                user.Id,
                now,
                cancellationToken);

            purchaseItems.Add(
                new PurchaseItemResult(
                    product.Id,
                    product.Name,
                    product.Sku,
                    item.QuantityBaseUnits,
                    item.UnitCostMinor,
                    lineTotal,
                    batchNumber,
                    expiryDate));
        }

        await using var insertPurchase =
            connection.CreateCommand();

        insertPurchase.Transaction = transaction;

        insertPurchase.CommandText =
        """
        INSERT INTO purchases
        (
            id,
            purchase_number,
            supplier_id,
            supplier_invoice_number,
            status,
            subtotal_minor,
            total_minor,
            notes,
            received_by_user_id,
            received_at_utc,
            created_at_utc,
            updated_at_utc
        )
        VALUES
        (
            $id,
            $purchaseNumber,
            $supplierId,
            $supplierInvoiceNumber,
            'received',
            $subtotal,
            $total,
            $notes,
            $userId,
            $now,
            $now,
            $now
        );
        """;

        insertPurchase.Parameters.AddWithValue(
            "$id",
            purchaseId);

        insertPurchase.Parameters.AddWithValue(
            "$purchaseNumber",
            purchaseNumber);

        insertPurchase.Parameters.AddWithValue(
            "$supplierId",
            supplierId ?? (object)DBNull.Value);

        insertPurchase.Parameters.AddWithValue(
            "$supplierInvoiceNumber",
            supplierInvoiceNumber);

        insertPurchase.Parameters.AddWithValue(
            "$subtotal",
            subtotal);

        insertPurchase.Parameters.AddWithValue(
            "$total",
            subtotal);

        insertPurchase.Parameters.AddWithValue(
            "$notes",
            notes);

        insertPurchase.Parameters.AddWithValue(
            "$userId",
            user.Id);

        insertPurchase.Parameters.AddWithValue(
            "$now",
            now.ToString("O"));

        await insertPurchase.ExecuteNonQueryAsync(cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "purchase.received",
            "purchase",
            purchaseId,
            new
            {
                purchaseNumber,
                supplierId,
                totalMinor = subtotal,
                itemCount = purchaseItems.Count
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new PurchaseResult(
            purchaseId,
            purchaseNumber,
            supplierId,
            supplierName,
            supplierInvoiceNumber,
            "received",
            subtotal,
            subtotal,
            notes,
            user.DisplayName,
            now,
            purchaseItems);
    }

    public async Task<IReadOnlyList<PurchaseResult>>
        ListPurchasesAsync(
            int requestedLimit,
            CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(requestedLimit, 1, 500);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            p.id,
            p.purchase_number,
            p.supplier_id,
            COALESCE(s.name, ''),
            p.supplier_invoice_number,
            p.status,
            p.subtotal_minor,
            p.total_minor,
            p.notes,
            u.display_name,
            p.received_at_utc
        FROM purchases AS p
        LEFT JOIN suppliers AS s
            ON s.id = p.supplier_id
        INNER JOIN users AS u
            ON u.id = p.received_by_user_id
        ORDER BY p.created_at_utc DESC
        LIMIT $limit;
        """;

        command.Parameters.AddWithValue("$limit", limit);

        var purchases = new List<PurchaseResult>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            purchases.Add(
                new PurchaseResult(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2)
                        ? null
                        : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    DateTimeOffset.Parse(reader.GetString(10)),
                    Array.Empty<PurchaseItemResult>()));
        }

        return purchases;
    }

    public async Task<ExpenseResult>
        CreateExpenseAsync(
            AuthenticatedUser user,
            CreateExpenseRequest request,
            CancellationToken cancellationToken = default)
    {
        string category = RequiredText(
            request.Category,
            100,
            "expense_category_required",
            "Select an expense category.");

        string description = RequiredText(
            request.Description,
            250,
            "expense_description_required",
            "Enter an expense description.");

        if (request.AmountMinor <= 0)
        {
            throw Validation(
                "invalid_expense_amount",
                "The expense amount must be greater than zero.");
        }

        string paymentMethod =
            request.PaymentMethod
                .Trim()
                .ToLowerInvariant();

        if (!ExpensePaymentMethods.Contains(paymentMethod))
        {
            throw Validation(
                "invalid_expense_payment_method",
                "Use cash, mobile money, bank or other.");
        }

        string expenseDate =
            ValidateRequiredDate(
                request.ExpenseDate,
                "invalid_expense_date");

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        string expenseNumber =
            await NextDocumentNumberAsync(
                connection,
                transaction,
                "expense",
                now,
                cancellationToken);

        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
        """
        INSERT INTO expenses
        (
            id,
            expense_number,
            category,
            description,
            amount_minor,
            payment_method,
            expense_date,
            recorded_by_user_id,
            created_at_utc
        )
        VALUES
        (
            $id,
            $expenseNumber,
            $category,
            $description,
            $amount,
            $paymentMethod,
            $expenseDate,
            $userId,
            $now
        );
        """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue(
            "$expenseNumber",
            expenseNumber);
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue(
            "$description",
            description);
        command.Parameters.AddWithValue(
            "$amount",
            request.AmountMinor);
        command.Parameters.AddWithValue(
            "$paymentMethod",
            paymentMethod);
        command.Parameters.AddWithValue(
            "$expenseDate",
            expenseDate);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "expense.recorded",
            "expense",
            id,
            new
            {
                expenseNumber,
                category,
                amountMinor = request.AmountMinor,
                paymentMethod
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new ExpenseResult(
            id,
            expenseNumber,
            category,
            description,
            request.AmountMinor,
            paymentMethod,
            expenseDate,
            user.DisplayName,
            now,
            false,
            null,
            null);
    }

    public async Task<IReadOnlyList<ExpenseResult>>
        ListExpensesAsync(
            bool includeVoided,
            int requestedLimit,
            CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(requestedLimit, 1, 1000);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            e.id,
            e.expense_number,
            e.category,
            e.description,
            e.amount_minor,
            e.payment_method,
            e.expense_date,
            u.display_name,
            e.created_at_utc,
            e.voided_at_utc,
            e.void_reason
        FROM expenses AS e
        INNER JOIN users AS u
            ON u.id = e.recorded_by_user_id
        WHERE $includeVoided = 1
           OR e.voided_at_utc IS NULL
        ORDER BY
            e.expense_date DESC,
            e.created_at_utc DESC
        LIMIT $limit;
        """;

        command.Parameters.AddWithValue(
            "$includeVoided",
            includeVoided ? 1 : 0);

        command.Parameters.AddWithValue("$limit", limit);

        var expenses = new List<ExpenseResult>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            expenses.Add(
                new ExpenseResult(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    DateTimeOffset.Parse(reader.GetString(8)),
                    !reader.IsDBNull(9),
                    reader.IsDBNull(9)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(9)),
                    reader.IsDBNull(10)
                        ? null
                        : reader.GetString(10)));
        }

        return expenses;
    }

    public async Task<ExpenseResult>
        VoidExpenseAsync(
            AuthenticatedUser user,
            string expenseId,
            VoidExpenseRequest request,
            CancellationToken cancellationToken = default)
    {
        string reason = RequiredText(
            request.Reason,
            250,
            "void_reason_required",
            "Enter the reason for voiding the expense.");

        if (reason.Length < 5)
        {
            throw Validation(
                "void_reason_too_short",
                "The void reason must contain at least five characters.");
        }

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        ExpenseResult existing =
            await ReadExpenseAsync(
                connection,
                transaction,
                expenseId,
                cancellationToken);

        if (existing.IsVoided)
        {
            throw Conflict(
                "expense_already_voided",
                "This expense has already been voided.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
        """
        UPDATE expenses
        SET voided_at_utc = $now,
            voided_by_user_id = $userId,
            void_reason = $reason
        WHERE id = $id
          AND voided_at_utc IS NULL;
        """;

        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$id", expenseId);

        int affected =
            await command.ExecuteNonQueryAsync(cancellationToken);

        if (affected != 1)
        {
            throw Conflict(
                "expense_void_conflict",
                "The expense changed before it could be voided.");
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "expense.voided",
            "expense",
            expenseId,
            new
            {
                existing.ExpenseNumber,
                existing.AmountMinor,
                reason
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return existing with
        {
            IsVoided = true,
            VoidedAtUtc = now,
            VoidReason = reason
        };
    }

    public async Task<BusinessReportResult>
        GetReportAsync(
            string? requestedFrom,
            string? requestedTo,
            CancellationToken cancellationToken = default)
    {
        (string from, string to) =
            ResolveDateRange(requestedFrom, requestedTo);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        long salesCount;
        long revenue;
        long cost;
        long expenses;
        long purchases;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT
                COUNT(*),
                COALESCE(SUM(total_minor), 0)
            FROM sales
            WHERE status IN ('completed', 'partially_returned')
              AND substr(completed_at_utc, 1, 10)
                  BETWEEN $from AND $to;
            """;

            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);

            await reader.ReadAsync(cancellationToken);

            salesCount = reader.GetInt64(0);
            revenue = reader.GetInt64(1);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT COALESCE(
                SUM(
                    si.unit_cost_minor
                    * (si.quantity - si.returned_quantity)
                ),
                0
            )
            FROM sale_items AS si
            INNER JOIN sales AS s
                ON s.id = si.sale_id
            WHERE s.status IN ('completed', 'partially_returned')
              AND substr(s.completed_at_utc, 1, 10)
                  BETWEEN $from AND $to;
            """;

            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);

            cost = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT COALESCE(SUM(amount_minor), 0)
            FROM expenses
            WHERE voided_at_utc IS NULL
              AND expense_date BETWEEN $from AND $to;
            """;

            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);

            expenses = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            SELECT COALESCE(SUM(total_minor), 0)
            FROM purchases
            WHERE status = 'received'
              AND substr(received_at_utc, 1, 10)
                  BETWEEN $from AND $to;
            """;

            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);

            purchases = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken));
        }

        IReadOnlyList<ProductReportResult> topProducts =
            await ReadTopProductsAsync(
                connection,
                from,
                to,
                cancellationToken);

        IReadOnlyList<TellerReportResult> tellerPerformance =
            await ReadTellerPerformanceAsync(
                connection,
                from,
                to,
                cancellationToken);

        long grossProfit = checked(revenue - cost);
        long netProfit = checked(grossProfit - expenses);

        return new BusinessReportResult(
            from,
            to,
            salesCount,
            revenue,
            cost,
            grossProfit,
            expenses,
            netProfit,
            purchases,
            topProducts,
            tellerPerformance);
    }

    public async Task<byte[]>
        BuildSalesCsvAsync(
            string? requestedFrom,
            string? requestedTo,
            CancellationToken cancellationToken = default)
    {
        (string from, string to) =
            ResolveDateRange(requestedFrom, requestedTo);

        await using var connection =
            new SqliteConnection(_database.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            s.receipt_number,
            COALESCE(s.invoice_number, ''),
            s.completed_at_utc,
            u.display_name,
            COALESCE(p.payment_method, ''),
            s.status,
            s.total_minor,
            COALESCE(
                (
                    SELECT SUM(
                        si.unit_cost_minor
                        * (si.quantity - si.returned_quantity)
                    )
                    FROM sale_items AS si
                    WHERE si.sale_id = s.id
                ),
                0
            )
        FROM sales AS s
        INNER JOIN users AS u
            ON u.id = s.teller_user_id
        LEFT JOIN sale_payments AS p
            ON p.sale_id = s.id
        WHERE substr(s.completed_at_utc, 1, 10)
              BETWEEN $from AND $to
        ORDER BY s.completed_at_utc;
        """;

        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        var csv = new StringBuilder();

        csv.AppendLine(
            "Receipt,Invoice,Date,Teller,Payment,Status," +
            "Revenue UGX,Cost UGX,Gross Profit UGX");

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            long revenue = reader.GetInt64(6);
            long cost = reader.GetInt64(7);

            string[] values =
            {
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                revenue.ToString(CultureInfo.InvariantCulture),
                cost.ToString(CultureInfo.InvariantCulture),
                (revenue - cost).ToString(
                    CultureInfo.InvariantCulture)
            };

            csv.AppendLine(
                string.Join(",", values.Select(EscapeCsv)));
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static IReadOnlyList<PurchaseItemRequest>
        ValidatePurchaseItems(
            IReadOnlyList<PurchaseItemRequest>? items)
    {
        if (items is null || items.Count == 0)
        {
            throw Validation(
                "purchase_items_required",
                "Add at least one product to the purchase.");
        }

        if (items.Count > 100)
        {
            throw Validation(
                "too_many_purchase_items",
                "A purchase cannot contain more than 100 lines.");
        }

        if (items
            .GroupBy(item => item.ProductId)
            .Any(group => group.Count() > 1))
        {
            throw Validation(
                "duplicate_purchase_product",
                "Each product may appear only once in a purchase.");
        }

        foreach (PurchaseItemRequest item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ProductId) ||
                item.QuantityBaseUnits <= 0 ||
                item.UnitCostMinor < 0)
            {
                throw Validation(
                    "invalid_purchase_item",
                    "Every purchase line needs a product, positive quantity and valid cost.");
            }
        }

        return items;
    }

    private static async Task<string>
        ResolveSupplierAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string? supplierId,
            CancellationToken cancellationToken)
    {
        if (supplierId is null)
        {
            return string.Empty;
        }

        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
        """
        SELECT name, is_active
        FROM suppliers
        WHERE id = $id
        LIMIT 1;
        """;

        command.Parameters.AddWithValue("$id", supplierId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "supplier_not_found",
                "The selected supplier could not be found.");
        }

        if (reader.GetInt32(1) != 1)
        {
            throw Conflict(
                "supplier_inactive",
                "The selected supplier is inactive.");
        }

        return reader.GetString(0);
    }

    private static async Task<ProductForPurchase>
        ReadProductForPurchaseAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string productId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
        """
        SELECT
            p.id,
            p.name,
            p.sku,
            p.is_active,
            s.quantity_base_units,
            s.version
        FROM products AS p
        INNER JOIN stock_balances AS s
            ON s.product_id = p.id
        WHERE p.id = $id
        LIMIT 1;
        """;

        command.Parameters.AddWithValue("$id", productId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "product_not_found",
                "A purchase product could not be found.");
        }

        if (reader.GetInt32(3) != 1)
        {
            throw Conflict(
                "product_inactive",
                $"{reader.GetString(1)} is inactive.");
        }

        return new ProductForPurchase(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(4),
            reader.GetInt32(5));
    }

    private static async Task InsertStockMovementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string productId,
        long quantity,
        long balanceAfter,
        long costValue,
        string purchaseId,
        string purchaseNumber,
        string userId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
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
            'purchase',
            $quantity,
            $balanceAfter,
            $costValue,
            'purchase',
            $purchaseId,
            $reason,
            $userId,
            $userId,
            $occurredAtUtc
        );
        """;

        command.Parameters.AddWithValue("$productId", productId);
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue(
            "$balanceAfter",
            balanceAfter);
        command.Parameters.AddWithValue("$costValue", costValue);
        command.Parameters.AddWithValue("$purchaseId", purchaseId);
        command.Parameters.AddWithValue(
            "$reason",
            $"Received purchase {purchaseNumber}");
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue(
            "$occurredAtUtc",
            occurredAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string>
        NextDocumentNumberAsync(
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
        WHERE document_type = $type;
        """;

        read.Parameters.AddWithValue("$type", documentType);

        await using var reader =
            await read.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new BusinessOperationsException(
                StatusCodes.Status500InternalServerError,
                "document_sequence_missing",
                "The document number sequence is missing.");
        }

        string prefix = reader.GetString(0);
        long value = reader.GetInt64(1);

        await reader.DisposeAsync();

        await using var update = connection.CreateCommand();

        update.Transaction = transaction;

        update.CommandText =
        """
        UPDATE document_sequences
        SET next_value = next_value + 1,
            updated_at_utc = $now
        WHERE document_type = $type
          AND next_value = $value;
        """;

        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$type", documentType);
        update.Parameters.AddWithValue("$value", value);

        int affected =
            await update.ExecuteNonQueryAsync(cancellationToken);

        if (affected != 1)
        {
            throw Conflict(
                "document_sequence_conflict",
                "Another record was created simultaneously. Try again.");
        }

        return
            $"{prefix}-{now:yyyyMMdd}-{value:000000}";
    }

    private static async Task<ExpenseResult>
        ReadExpenseAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string expenseId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
        """
        SELECT
            e.id,
            e.expense_number,
            e.category,
            e.description,
            e.amount_minor,
            e.payment_method,
            e.expense_date,
            u.display_name,
            e.created_at_utc,
            e.voided_at_utc,
            e.void_reason
        FROM expenses AS e
        INNER JOIN users AS u
            ON u.id = e.recorded_by_user_id
        WHERE e.id = $id
        LIMIT 1;
        """;

        command.Parameters.AddWithValue("$id", expenseId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "expense_not_found",
                "The expense could not be found.");
        }

        return new ExpenseResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            !reader.IsDBNull(9),
            reader.IsDBNull(9)
                ? null
                : DateTimeOffset.Parse(reader.GetString(9)),
            reader.IsDBNull(10)
                ? null
                : reader.GetString(10));
    }

    private static async Task<IReadOnlyList<ProductReportResult>>
        ReadTopProductsAsync(
            SqliteConnection connection,
            string from,
            string to,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            si.product_name_snapshot,
            si.sku_snapshot,
            SUM(si.quantity - si.returned_quantity),
            SUM(si.line_total_minor),
            SUM(
                si.unit_cost_minor
                * (si.quantity - si.returned_quantity)
            )
        FROM sale_items AS si
        INNER JOIN sales AS s
            ON s.id = si.sale_id
        WHERE s.status IN ('completed', 'partially_returned')
          AND substr(s.completed_at_utc, 1, 10)
              BETWEEN $from AND $to
        GROUP BY
            si.product_name_snapshot,
            si.sku_snapshot
        ORDER BY SUM(si.line_total_minor) DESC
        LIMIT 20;
        """;

        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        var products = new List<ProductReportResult>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            long revenue = reader.GetInt64(3);
            long cost = reader.GetInt64(4);

            products.Add(
                new ProductReportResult(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    revenue,
                    cost,
                    revenue - cost));
        }

        return products;
    }

    private static async Task<IReadOnlyList<TellerReportResult>>
        ReadTellerPerformanceAsync(
            SqliteConnection connection,
            string from,
            string to,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            u.display_name,
            COUNT(*),
            COALESCE(SUM(s.total_minor), 0)
        FROM sales AS s
        INNER JOIN users AS u
            ON u.id = s.teller_user_id
        WHERE s.status IN ('completed', 'partially_returned')
          AND substr(s.completed_at_utc, 1, 10)
              BETWEEN $from AND $to
        GROUP BY
            s.teller_user_id,
            u.display_name
        ORDER BY SUM(s.total_minor) DESC;
        """;

        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        var tellers = new List<TellerReportResult>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            tellers.Add(
                new TellerReportResult(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2)));
        }

        return tellers;
    }

    private static (string From, string To)
        ResolveDateRange(
            string? requestedFrom,
            string? requestedTo)
    {
        DateOnly today =
            DateOnly.FromDateTime(DateTime.UtcNow);

        DateOnly defaultFrom =
            new(today.Year, today.Month, 1);

        string from =
            string.IsNullOrWhiteSpace(requestedFrom)
                ? defaultFrom.ToString("yyyy-MM-dd")
                : ValidateRequiredDate(
                    requestedFrom,
                    "invalid_report_from");

        string to =
            string.IsNullOrWhiteSpace(requestedTo)
                ? today.ToString("yyyy-MM-dd")
                : ValidateRequiredDate(
                    requestedTo,
                    "invalid_report_to");

        if (string.CompareOrdinal(from, to) > 0)
        {
            throw Validation(
                "invalid_report_range",
                "The report start date cannot be after the end date.");
        }

        return (from, to);
    }

    private static string ValidateRequiredDate(
        string value,
        string errorCode)
    {
        if (!DateOnly.TryParseExact(
                value?.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly date))
        {
            throw Validation(
                errorCode,
                "Use the date format YYYY-MM-DD.");
        }

        return date.ToString("yyyy-MM-dd");
    }

    private static string? ValidateOptionalDate(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ValidateRequiredDate(
            value,
            "invalid_expiry_date");
    }

    private static string RequiredText(
        string? value,
        int maximumLength,
        string errorCode,
        string message)
    {
        string result = value?.Trim() ?? string.Empty;

        if (result.Length == 0)
        {
            throw Validation(errorCode, message);
        }

        if (result.Length > maximumLength)
        {
            throw Validation(
                errorCode,
                $"The value cannot exceed {maximumLength} characters.");
        }

        return result;
    }

    private static string OptionalText(
        string? value,
        int maximumLength)
    {
        string result = value?.Trim() ?? string.Empty;

        if (result.Length > maximumLength)
        {
            throw Validation(
                "value_too_long",
                $"The value cannot exceed {maximumLength} characters.");
        }

        return result;
    }

    private static SupplierResult ReadSupplier(
        SqliteDataReader reader)
    {
        return new SupplierResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6) == 1,
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)));
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
            $now,
            $userId,
            $username,
            $eventType,
            $entityType,
            $entityId,
            1,
            $details,
            NULL
        );
        """;

        command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToString("O"));

        command.Parameters.AddWithValue("$userId", user.Id);
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
            "$details",
            JsonSerializer.Serialize(details));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string EscapeCsv(string value)
    {
        return
            "\"" +
            value.Replace("\"", "\"\"") +
            "\"";
    }

    private static BusinessOperationsException Validation(
        string code,
        string message) =>
        new(
            StatusCodes.Status400BadRequest,
            code,
            message);

    private static BusinessOperationsException NotFound(
        string code,
        string message) =>
        new(
            StatusCodes.Status404NotFound,
            code,
            message);

    private static BusinessOperationsException Conflict(
        string code,
        string message) =>
        new(
            StatusCodes.Status409Conflict,
            code,
            message);

    private sealed record ProductForPurchase(
        string Id,
        string Name,
        string Sku,
        long CurrentStock,
        int StockVersion);
}
