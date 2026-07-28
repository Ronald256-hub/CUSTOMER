using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Finance;

public sealed partial class FinanceService
{
    public async Task<FinanceSettlementRecord> CreateCustomerReceiptAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateCustomerReceiptRequest request,
        CancellationToken cancellationToken = default)
    {
        string customerId = NormalizeId(request.CustomerId);
        string receiptDate = NormalizeDate(
            request.ReceiptDate,
            "invalid_receipt_date");
        string paymentMethod = NormalizePaymentMethod(request.PaymentMethod);
        string reference = OptionalText(request.Reference, 150);
        string notes = OptionalText(request.Notes, 500);
        IReadOnlyList<NormalizedAllocation> allocations =
            NormalizeAllocations(request.Allocations);
        long amountMinor = checked(allocations.Sum(item => item.AmountMinor));

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            cancellationToken);
        await EnsureOpenPeriodAsync(
            connection,
            transaction,
            context.OrganizationId,
            receiptDate,
            cancellationToken);

        string customerName = await RequireCustomerAsync(
            connection,
            transaction,
            context.OrganizationId,
            customerId,
            cancellationToken);
        await ValidateReceivableAllocationsAsync(
            connection,
            transaction,
            context,
            customerId,
            allocations,
            cancellationToken);

        string id = Guid.NewGuid().ToString("N");
        string number = $"CR-{NormalizeShopCode(context.ShopCode)}-{receiptDate.Replace("-", string.Empty)}-{id[..6].ToUpperInvariant()}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string journalId = Guid.NewGuid().ToString("N");

        try
        {
            await InsertCustomerReceiptAsync(
                connection,
                transaction,
                id,
                context,
                customerId,
                number,
                receiptDate,
                paymentMethod,
                amountMinor,
                reference,
                notes,
                user.Id,
                now,
                cancellationToken);
            foreach (NormalizedAllocation allocation in allocations)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                """
                INSERT INTO finance_customer_receipt_allocations
                (
                    receipt_id,
                    receivable_item_id,
                    amount_minor
                )
                VALUES
                (
                    $receiptId,
                    $itemId,
                    $amountMinor
                );
                """;
                insert.Parameters.AddWithValue("$receiptId", id);
                insert.Parameters.AddWithValue("$itemId", allocation.ItemId);
                insert.Parameters.AddWithValue("$amountMinor", allocation.AmountMinor);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            string paymentAccountId = await ResolveSystemAccountAsync(
                connection,
                transaction,
                context.OrganizationId,
                PaymentAccountKey(paymentMethod),
                cancellationToken);
            string receivableAccountId = await ResolveSystemAccountAsync(
                connection,
                transaction,
                context.OrganizationId,
                "accounts_receivable",
                cancellationToken);
            await InsertAndPostSettlementJournalAsync(
                connection,
                transaction,
                journalId,
                context,
                $"SYS-{number}",
                receiptDate,
                $"Customer receipt {number} from {customerName}",
                $"customer_receipt:{id}",
                paymentAccountId,
                receivableAccountId,
                amountMinor,
                "customer",
                customerId,
                user.Id,
                now,
                cancellationToken);

            await using var post = connection.CreateCommand();
            post.Transaction = transaction;
            post.CommandText =
            """
            UPDATE finance_customer_receipts
            SET status = 'posted',
                posting_journal_id = $journalId,
                posted_at_utc = $postedAtUtc
            WHERE id = $id
              AND status = 'draft';
            """;
            post.Parameters.AddWithValue("$journalId", journalId);
            post.Parameters.AddWithValue("$postedAtUtc", now.ToString("O"));
            post.Parameters.AddWithValue("$id", id);
            if (await post.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "customer_receipt_posting_failed",
                    "The customer receipt could not be posted.");
            }

            await WriteAuditAsync(
                connection,
                transaction,
                user,
                "finance.customer_receipt.posted",
                "customer_receipt",
                id,
                new
                {
                    number,
                    context.OrganizationId,
                    context.ShopId,
                    customerId,
                    amountMinor,
                    paymentMethod,
                    journalId,
                    allocationCount = allocations.Count
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "customer_receipt_control_failed",
                "The customer receipt failed a database finance control.");
        }

        return await GetCustomerReceiptAsync(
            user,
            context,
            id,
            cancellationToken);
    }

    public async Task<FinanceSettlementRecord> CreateSupplierPaymentAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateSupplierPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        string supplierId = NormalizeId(request.SupplierId);
        string paymentDate = NormalizeDate(
            request.PaymentDate,
            "invalid_payment_date");
        string paymentMethod = NormalizePaymentMethod(request.PaymentMethod);
        string reference = OptionalText(request.Reference, 150);
        string notes = OptionalText(request.Notes, 500);
        IReadOnlyList<NormalizedAllocation> allocations =
            NormalizeAllocations(request.Allocations);
        long amountMinor = checked(allocations.Sum(item => item.AmountMinor));

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            cancellationToken);
        await EnsureOpenPeriodAsync(
            connection,
            transaction,
            context.OrganizationId,
            paymentDate,
            cancellationToken);

        string supplierName = await RequireSupplierAsync(
            connection,
            transaction,
            context.OrganizationId,
            supplierId,
            cancellationToken);
        await ValidatePayableAllocationsAsync(
            connection,
            transaction,
            context,
            supplierId,
            allocations,
            cancellationToken);

        string id = Guid.NewGuid().ToString("N");
        string number = $"SP-{NormalizeShopCode(context.ShopCode)}-{paymentDate.Replace("-", string.Empty)}-{id[..6].ToUpperInvariant()}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string journalId = Guid.NewGuid().ToString("N");

        try
        {
            await InsertSupplierPaymentAsync(
                connection,
                transaction,
                id,
                context,
                supplierId,
                number,
                paymentDate,
                paymentMethod,
                amountMinor,
                reference,
                notes,
                user.Id,
                now,
                cancellationToken);
            foreach (NormalizedAllocation allocation in allocations)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                """
                INSERT INTO finance_supplier_payment_allocations
                (
                    payment_id,
                    payable_item_id,
                    amount_minor
                )
                VALUES
                (
                    $paymentId,
                    $itemId,
                    $amountMinor
                );
                """;
                insert.Parameters.AddWithValue("$paymentId", id);
                insert.Parameters.AddWithValue("$itemId", allocation.ItemId);
                insert.Parameters.AddWithValue("$amountMinor", allocation.AmountMinor);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            string payableAccountId = await ResolveSystemAccountAsync(
                connection,
                transaction,
                context.OrganizationId,
                "accounts_payable",
                cancellationToken);
            string paymentAccountId = await ResolveSystemAccountAsync(
                connection,
                transaction,
                context.OrganizationId,
                PaymentAccountKey(paymentMethod),
                cancellationToken);
            await InsertAndPostSettlementJournalAsync(
                connection,
                transaction,
                journalId,
                context,
                $"SYS-{number}",
                paymentDate,
                $"Supplier payment {number} to {supplierName}",
                $"supplier_payment:{id}",
                payableAccountId,
                paymentAccountId,
                amountMinor,
                "supplier",
                supplierId,
                user.Id,
                now,
                cancellationToken);

            await using var post = connection.CreateCommand();
            post.Transaction = transaction;
            post.CommandText =
            """
            UPDATE finance_supplier_payments
            SET status = 'posted',
                posting_journal_id = $journalId,
                posted_at_utc = $postedAtUtc
            WHERE id = $id
              AND status = 'draft';
            """;
            post.Parameters.AddWithValue("$journalId", journalId);
            post.Parameters.AddWithValue("$postedAtUtc", now.ToString("O"));
            post.Parameters.AddWithValue("$id", id);
            if (await post.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "supplier_payment_posting_failed",
                    "The supplier payment could not be posted.");
            }

            await WriteAuditAsync(
                connection,
                transaction,
                user,
                "finance.supplier_payment.posted",
                "supplier_payment",
                id,
                new
                {
                    number,
                    context.OrganizationId,
                    context.ShopId,
                    supplierId,
                    amountMinor,
                    paymentMethod,
                    journalId,
                    allocationCount = allocations.Count
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "supplier_payment_control_failed",
                "The supplier payment failed a database finance control.");
        }

        return await GetSupplierPaymentAsync(
            user,
            context,
            id,
            cancellationToken);
    }

    public Task<FinanceSettlementRecord> ReverseCustomerReceiptAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string receiptId,
        ReverseSettlementRequest request,
        CancellationToken cancellationToken = default) =>
        ReverseSettlementAsync(
            user,
            context,
            "customer_receipt",
            NormalizeId(receiptId),
            request,
            cancellationToken);

    public Task<FinanceSettlementRecord> ReverseSupplierPaymentAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string paymentId,
        ReverseSettlementRequest request,
        CancellationToken cancellationToken = default) =>
        ReverseSettlementAsync(
            user,
            context,
            "supplier_payment",
            NormalizeId(paymentId),
            request,
            cancellationToken);

    private async Task<FinanceSettlementRecord> ReverseSettlementAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string settlementType,
        string settlementId,
        ReverseSettlementRequest request,
        CancellationToken cancellationToken)
    {
        string reversalDate = NormalizeDate(
            request.ReversalDate,
            "invalid_reversal_date");
        string reason = RequiredText(
            request.Reason,
            500,
            "reversal_reason_required",
            "Enter the reason for reversing the settlement.");
        if (reason.Length < 5)
        {
            throw Validation(
                "reversal_reason_too_short",
                "The reversal reason must contain at least five characters.");
        }

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireFinanceAccessAsync(
            connection,
            transaction,
            user,
            context.ShopId,
            cancellationToken);
        await EnsureOpenPeriodAsync(
            connection,
            transaction,
            context.OrganizationId,
            reversalDate,
            cancellationToken);

        SettlementHeader settlement = await ReadSettlementHeaderAsync(
            connection,
            transaction,
            settlementType,
            settlementId,
            context,
            cancellationToken);
        if (settlement.Status != "posted" ||
            string.IsNullOrWhiteSpace(settlement.PostingJournalId))
        {
            throw Conflict(
                "settlement_not_posted",
                "Only a posted settlement can be reversed.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string reversalJournalId = Guid.NewGuid().ToString("N");
        try
        {
            await CreateExactReversalJournalAsync(
                connection,
                transaction,
                settlement.PostingJournalId,
                reversalJournalId,
                context,
                $"SYS-R-{settlement.Number}",
                reversalDate,
                reason,
                user.Id,
                now,
                cancellationToken);

            string table = settlementType == "customer_receipt"
                ? "finance_customer_receipts"
                : "finance_supplier_payments";
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                $"""
                UPDATE {table}
                SET status = 'reversed',
                    reversal_journal_id = $reversalJournalId,
                    reversed_by_user_id = $userId,
                    reversed_at_utc = $reversedAtUtc,
                    reversal_reason = $reason
                WHERE id = $id
                  AND status = 'posted';
                """;
            update.Parameters.AddWithValue("$reversalJournalId", reversalJournalId);
            update.Parameters.AddWithValue("$userId", user.Id);
            update.Parameters.AddWithValue("$reversedAtUtc", now.ToString("O"));
            update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$id", settlementId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "settlement_reversal_failed",
                    "The settlement changed before it could be reversed.");
            }

            await WriteAuditAsync(
                connection,
                transaction,
                user,
                $"finance.{settlementType}.reversed",
                settlementType,
                settlementId,
                new
                {
                    settlement.Number,
                    settlement.AmountMinor,
                    settlement.PostingJournalId,
                    reversalJournalId,
                    reversalDate,
                    reason
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict(
                "settlement_reversal_control_failed",
                "The settlement reversal failed a database finance control.");
        }

        return settlementType == "customer_receipt"
            ? await GetCustomerReceiptAsync(user, context, settlementId, cancellationToken)
            : await GetSupplierPaymentAsync(user, context, settlementId, cancellationToken);
    }

    private static async Task InsertCustomerReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        ActiveShopContextRecord context,
        string customerId,
        string number,
        string date,
        string paymentMethod,
        long amountMinor,
        string reference,
        string notes,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO finance_customer_receipts
        (
            id,
            organization_id,
            shop_id,
            customer_id,
            receipt_number,
            receipt_date,
            payment_method,
            amount_minor,
            reference,
            notes,
            status,
            created_by_user_id,
            created_at_utc
        )
        VALUES
        (
            $id,
            $organizationId,
            $shopId,
            $customerId,
            $number,
            $date,
            $paymentMethod,
            $amountMinor,
            $reference,
            $notes,
            'draft',
            $userId,
            $now
        );
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$customerId", customerId);
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$date", date);
        command.Parameters.AddWithValue("$paymentMethod", paymentMethod);
        command.Parameters.AddWithValue("$amountMinor", amountMinor);
        command.Parameters.AddWithValue("$reference", reference);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSupplierPaymentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        ActiveShopContextRecord context,
        string supplierId,
        string number,
        string date,
        string paymentMethod,
        long amountMinor,
        string reference,
        string notes,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO finance_supplier_payments
        (
            id,
            organization_id,
            shop_id,
            supplier_id,
            payment_number,
            payment_date,
            payment_method,
            amount_minor,
            reference,
            notes,
            status,
            created_by_user_id,
            created_at_utc
        )
        VALUES
        (
            $id,
            $organizationId,
            $shopId,
            $supplierId,
            $number,
            $date,
            $paymentMethod,
            $amountMinor,
            $reference,
            $notes,
            'draft',
            $userId,
            $now
        );
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$supplierId", supplierId);
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$date", date);
        command.Parameters.AddWithValue("$paymentMethod", paymentMethod);
        command.Parameters.AddWithValue("$amountMinor", amountMinor);
        command.Parameters.AddWithValue("$reference", reference);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAndPostSettlementJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string journalId,
        ActiveShopContextRecord context,
        string journalNumber,
        string journalDate,
        string description,
        string sourceId,
        string debitAccountId,
        string creditAccountId,
        long amountMinor,
        string counterpartyType,
        string counterpartyId,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText =
            """
            INSERT INTO accounting_journals
            (
                id,
                organization_id,
                shop_id,
                journal_number,
                journal_date,
                currency_code,
                description,
                source_type,
                source_id,
                status,
                total_debit_minor,
                total_credit_minor,
                version,
                created_by_user_id,
                created_at_utc,
                updated_at_utc
            )
            VALUES
            (
                $id,
                $organizationId,
                $shopId,
                $journalNumber,
                $journalDate,
                $currencyCode,
                $description,
                'system',
                $sourceId,
                'draft',
                $amountMinor,
                $amountMinor,
                1,
                $userId,
                $now,
                $now
            );
            """;
            header.Parameters.AddWithValue("$id", journalId);
            header.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            header.Parameters.AddWithValue("$shopId", context.ShopId);
            header.Parameters.AddWithValue("$journalNumber", journalNumber);
            header.Parameters.AddWithValue("$journalDate", journalDate);
            header.Parameters.AddWithValue("$currencyCode", context.CurrencyCode);
            header.Parameters.AddWithValue("$description", description);
            header.Parameters.AddWithValue("$sourceId", sourceId);
            header.Parameters.AddWithValue("$amountMinor", amountMinor);
            header.Parameters.AddWithValue("$userId", userId);
            header.Parameters.AddWithValue("$now", now.ToString("O"));
            await header.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertJournalLineAsync(
            connection,
            transaction,
            journalId,
            1,
            debitAccountId,
            context.ShopId,
            amountMinor,
            0,
            description,
            counterpartyType,
            counterpartyId,
            cancellationToken);
        await InsertJournalLineAsync(
            connection,
            transaction,
            journalId,
            2,
            creditAccountId,
            context.ShopId,
            0,
            amountMinor,
            description,
            counterpartyType,
            counterpartyId,
            cancellationToken);

        await using var post = connection.CreateCommand();
        post.Transaction = transaction;
        post.CommandText =
        """
        UPDATE accounting_journals
        SET status = 'posted',
            posted_by_user_id = $userId,
            posted_at_utc = $now,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $id
          AND status = 'draft'
          AND version = 1;
        """;
        post.Parameters.AddWithValue("$userId", userId);
        post.Parameters.AddWithValue("$now", now.ToString("O"));
        post.Parameters.AddWithValue("$id", journalId);
        if (await post.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "settlement_journal_posting_failed",
                "The settlement journal could not be posted.");
        }
    }

    private static async Task InsertJournalLineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string journalId,
        int lineNumber,
        string accountId,
        string shopId,
        long debitMinor,
        long creditMinor,
        string description,
        string counterpartyType,
        string counterpartyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO accounting_journal_lines
        (
            journal_id,
            line_number,
            account_id,
            shop_id,
            debit_minor,
            credit_minor,
            description,
            counterparty_type,
            counterparty_id
        )
        VALUES
        (
            $journalId,
            $lineNumber,
            $accountId,
            $shopId,
            $debitMinor,
            $creditMinor,
            $description,
            $counterpartyType,
            $counterpartyId
        );
        """;
        command.Parameters.AddWithValue("$journalId", journalId);
        command.Parameters.AddWithValue("$lineNumber", lineNumber);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$shopId", shopId);
        command.Parameters.AddWithValue("$debitMinor", debitMinor);
        command.Parameters.AddWithValue("$creditMinor", creditMinor);
        command.Parameters.AddWithValue("$description", OptionalText(description, 250));
        command.Parameters.AddWithValue("$counterpartyType", counterpartyType);
        command.Parameters.AddWithValue("$counterpartyId", counterpartyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CreateExactReversalJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string originalJournalId,
        string reversalJournalId,
        ActiveShopContextRecord context,
        string reversalNumber,
        string reversalDate,
        string reason,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        JournalSnapshot original = await ReadJournalSnapshotAsync(
            connection,
            transaction,
            originalJournalId,
            context,
            cancellationToken);
        if (original.Status != "posted")
        {
            throw Conflict(
                "settlement_journal_not_posted",
                "The settlement journal is not available for reversal.");
        }

        await using (var header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText =
            """
            INSERT INTO accounting_journals
            (
                id,
                organization_id,
                shop_id,
                journal_number,
                journal_date,
                currency_code,
                description,
                source_type,
                source_id,
                status,
                reversal_of_journal_id,
                total_debit_minor,
                total_credit_minor,
                version,
                created_by_user_id,
                created_at_utc,
                updated_at_utc
            )
            VALUES
            (
                $id,
                $organizationId,
                $shopId,
                $journalNumber,
                $journalDate,
                $currencyCode,
                $description,
                'reversal',
                $sourceId,
                'draft',
                $originalJournalId,
                $totalDebitMinor,
                $totalCreditMinor,
                1,
                $userId,
                $now,
                $now
            );
            """;
            header.Parameters.AddWithValue("$id", reversalJournalId);
            header.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            header.Parameters.AddWithValue("$shopId", context.ShopId);
            header.Parameters.AddWithValue("$journalNumber", reversalNumber);
            header.Parameters.AddWithValue("$journalDate", reversalDate);
            header.Parameters.AddWithValue("$currencyCode", context.CurrencyCode);
            header.Parameters.AddWithValue(
                "$description",
                OptionalText($"Reversal of {original.JournalNumber}: {reason}", 500));
            header.Parameters.AddWithValue("$sourceId", originalJournalId);
            header.Parameters.AddWithValue("$originalJournalId", originalJournalId);
            header.Parameters.AddWithValue("$totalDebitMinor", original.TotalCreditMinor);
            header.Parameters.AddWithValue("$totalCreditMinor", original.TotalDebitMinor);
            header.Parameters.AddWithValue("$userId", userId);
            header.Parameters.AddWithValue("$now", now.ToString("O"));
            await header.ExecuteNonQueryAsync(cancellationToken);
        }

        int lineNumber = 1;
        foreach (JournalLineSnapshot line in original.Lines)
        {
            await InsertJournalLineAsync(
                connection,
                transaction,
                reversalJournalId,
                lineNumber++,
                line.AccountId,
                context.ShopId,
                line.CreditMinor,
                line.DebitMinor,
                $"Reversal: {line.Description}",
                line.CounterpartyType ?? "other",
                line.CounterpartyId ?? originalJournalId,
                cancellationToken);
        }

        await using (var post = connection.CreateCommand())
        {
            post.Transaction = transaction;
            post.CommandText =
            """
            UPDATE accounting_journals
            SET status = 'posted',
                posted_by_user_id = $userId,
                posted_at_utc = $now,
                updated_at_utc = $now,
                version = version + 1
            WHERE id = $id
              AND status = 'draft'
              AND version = 1;
            """;
            post.Parameters.AddWithValue("$userId", userId);
            post.Parameters.AddWithValue("$now", now.ToString("O"));
            post.Parameters.AddWithValue("$id", reversalJournalId);
            if (await post.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "settlement_reversal_journal_failed",
                    "The reversal journal could not be posted.");
            }
        }

        await using var markOriginal = connection.CreateCommand();
        markOriginal.Transaction = transaction;
        markOriginal.CommandText =
        """
        UPDATE accounting_journals
        SET status = 'reversed',
            reversed_by_journal_id = $reversalJournalId,
            updated_at_utc = $now,
            version = version + 1
        WHERE id = $originalJournalId
          AND status = 'posted';
        """;
        markOriginal.Parameters.AddWithValue("$reversalJournalId", reversalJournalId);
        markOriginal.Parameters.AddWithValue("$now", now.ToString("O"));
        markOriginal.Parameters.AddWithValue("$originalJournalId", originalJournalId);
        if (await markOriginal.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(
                "settlement_original_journal_changed",
                "The original settlement journal changed before reversal.");
        }
    }

    private static async Task<JournalSnapshot> ReadJournalSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string journalId,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken)
    {
        string journalNumber;
        string status;
        long debit;
        long credit;
        await using (var header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText =
            """
            SELECT journal_number, status, total_debit_minor, total_credit_minor
            FROM accounting_journals
            WHERE id = $id
              AND organization_id = $organizationId
              AND shop_id = $shopId
            LIMIT 1;
            """;
            header.Parameters.AddWithValue("$id", journalId);
            header.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            header.Parameters.AddWithValue("$shopId", context.ShopId);
            await using var reader = await header.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw NotFound(
                    "settlement_journal_not_found",
                    "The settlement journal could not be found.");
            }
            journalNumber = reader.GetString(0);
            status = reader.GetString(1);
            debit = reader.GetInt64(2);
            credit = reader.GetInt64(3);
        }

        var lines = new List<JournalLineSnapshot>();
        await using (var lineCommand = connection.CreateCommand())
        {
            lineCommand.Transaction = transaction;
            lineCommand.CommandText =
            """
            SELECT
                account_id,
                debit_minor,
                credit_minor,
                description,
                counterparty_type,
                counterparty_id
            FROM accounting_journal_lines
            WHERE journal_id = $journalId
            ORDER BY line_number, id;
            """;
            lineCommand.Parameters.AddWithValue("$journalId", journalId);
            await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new JournalLineSnapshot(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        return new JournalSnapshot(
            journalNumber,
            status,
            debit,
            credit,
            lines);
    }

    private static async Task<string> RequireCustomerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string customerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT name
        FROM finance_customers
        WHERE id = $id
          AND organization_id = $organizationId
          AND is_active = 1
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", customerId);
        command.Parameters.AddWithValue("$organizationId", organizationId);
        string? name = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw NotFound(
                "customer_not_found",
                "The active customer account could not be found.");
        }
        return name;
    }

    private static async Task<string> RequireSupplierAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string organizationId,
        string supplierId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT name
        FROM suppliers
        WHERE id = $id
          AND organization_id = $organizationId
          AND is_active = 1
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", supplierId);
        command.Parameters.AddWithValue("$organizationId", organizationId);
        string? name = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw NotFound(
                "supplier_not_found",
                "The active supplier account could not be found.");
        }
        return name;
    }

    private static async Task ValidateReceivableAllocationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string customerId,
        IReadOnlyList<NormalizedAllocation> allocations,
        CancellationToken cancellationToken)
    {
        foreach (NormalizedAllocation allocation in allocations)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            SELECT
                item.original_amount_minor - COALESCE
                (
                    (
                        SELECT SUM(existing.amount_minor)
                        FROM finance_customer_receipt_allocations AS existing
                        INNER JOIN finance_customer_receipts AS receipt
                            ON receipt.id = existing.receipt_id
                        WHERE existing.receivable_item_id = item.id
                          AND receipt.status = 'posted'
                    ),
                    0
                )
            FROM finance_receivable_items AS item
            INNER JOIN accounting_journals AS source_journal
                ON source_journal.id = item.posting_journal_id
               AND source_journal.status = 'posted'
            WHERE item.id = $itemId
              AND item.organization_id = $organizationId
              AND item.shop_id = $shopId
              AND item.customer_id = $customerId
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("$itemId", allocation.ItemId);
            command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            command.Parameters.AddWithValue("$shopId", context.ShopId);
            command.Parameters.AddWithValue("$customerId", customerId);
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is null)
            {
                throw NotFound(
                    "receivable_item_not_found",
                    "A selected receivable is unavailable in the active branch.");
            }
            long outstanding = Convert.ToInt64(value);
            if (allocation.AmountMinor > outstanding)
            {
                throw Conflict(
                    "receivable_overallocation",
                    "A receipt allocation exceeds the outstanding receivable balance.");
            }
        }
    }

    private static async Task ValidatePayableAllocationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string supplierId,
        IReadOnlyList<NormalizedAllocation> allocations,
        CancellationToken cancellationToken)
    {
        foreach (NormalizedAllocation allocation in allocations)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            SELECT
                item.original_amount_minor - COALESCE
                (
                    (
                        SELECT SUM(existing.amount_minor)
                        FROM finance_supplier_payment_allocations AS existing
                        INNER JOIN finance_supplier_payments AS payment
                            ON payment.id = existing.payment_id
                        WHERE existing.payable_item_id = item.id
                          AND payment.status = 'posted'
                    ),
                    0
                )
            FROM finance_payable_items AS item
            INNER JOIN accounting_journals AS source_journal
                ON source_journal.id = item.posting_journal_id
               AND source_journal.status = 'posted'
            WHERE item.id = $itemId
              AND item.organization_id = $organizationId
              AND item.shop_id = $shopId
              AND item.supplier_id = $supplierId
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("$itemId", allocation.ItemId);
            command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            command.Parameters.AddWithValue("$shopId", context.ShopId);
            command.Parameters.AddWithValue("$supplierId", supplierId);
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is null)
            {
                throw NotFound(
                    "payable_item_not_found",
                    "A selected payable is unavailable in the active branch.");
            }
            long outstanding = Convert.ToInt64(value);
            if (allocation.AmountMinor > outstanding)
            {
                throw Conflict(
                    "payable_overallocation",
                    "A payment allocation exceeds the outstanding payable balance.");
            }
        }
    }

    private static async Task<SettlementHeader> ReadSettlementHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string settlementType,
        string settlementId,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken)
    {
        string table = settlementType == "customer_receipt"
            ? "finance_customer_receipts"
            : "finance_supplier_payments";
        string numberColumn = settlementType == "customer_receipt"
            ? "receipt_number"
            : "payment_number";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {numberColumn}, amount_minor, status, posting_journal_id
            FROM {table}
            WHERE id = $id
              AND organization_id = $organizationId
              AND shop_id = $shopId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", settlementId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound(
                "settlement_not_found",
                "The settlement could not be found in the active branch.");
        }
        return new SettlementHeader(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static string NormalizeShopCode(string value)
    {
        char[] characters = value
            .Trim()
            .ToUpperInvariant()
            .Where(character => char.IsLetterOrDigit(character) || character == '-')
            .Take(16)
            .ToArray();
        return characters.Length == 0 ? "SHOP" : new string(characters);
    }

    private sealed record SettlementHeader(
        string Number,
        long AmountMinor,
        string Status,
        string? PostingJournalId);

    private sealed record JournalSnapshot(
        string JournalNumber,
        string Status,
        long TotalDebitMinor,
        long TotalCreditMinor,
        IReadOnlyList<JournalLineSnapshot> Lines);

    private sealed record JournalLineSnapshot(
        string AccountId,
        long DebitMinor,
        long CreditMinor,
        string Description,
        string? CounterpartyType,
        string? CounterpartyId);
}