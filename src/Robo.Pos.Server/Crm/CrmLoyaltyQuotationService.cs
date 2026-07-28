using Microsoft.Data.Sqlite;
using Robo.Pos.Server.Security;
using Robo.Pos.Server.Shops;

namespace Robo.Pos.Server.Crm;

public sealed partial class CrmService
{
    public async Task<LoyaltySettingsRecord> GetLoyaltySettingsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        return await ReadLoyaltySettingsAsync(
            connection,
            null,
            context.OrganizationId,
            cancellationToken);
    }

    public async Task<LoyaltySettingsRecord> UpdateLoyaltySettingsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        UpdateLoyaltySettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user, "change loyalty programme settings");
        if (request.ExpectedVersion < 1 || request.SpendMinorPerPoint <= 0 ||
            request.MinimumRedeemPoints < 1 || request.SilverThresholdPoints < 0 ||
            request.GoldThresholdPoints < request.SilverThresholdPoints ||
            request.PlatinumThresholdPoints < request.GoldThresholdPoints)
        {
            throw Validation(
                "invalid_loyalty_settings",
                "The loyalty settings or tier thresholds are invalid.");
        }

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
            """
            UPDATE crm_loyalty_settings
            SET is_enabled = $enabled,
                spend_minor_per_point = $spend,
                minimum_redeem_points = $minimum,
                silver_threshold_points = $silver,
                gold_threshold_points = $gold,
                platinum_threshold_points = $platinum,
                version = version + 1,
                updated_by_user_id = $userId,
                updated_at_utc = $now
            WHERE organization_id = $organizationId
              AND version = $expectedVersion;
            """;
            command.Parameters.AddWithValue("$enabled", request.IsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$spend", request.SpendMinorPerPoint);
            command.Parameters.AddWithValue("$minimum", request.MinimumRedeemPoints);
            command.Parameters.AddWithValue("$silver", request.SilverThresholdPoints);
            command.Parameters.AddWithValue("$gold", request.GoldThresholdPoints);
            command.Parameters.AddWithValue("$platinum", request.PlatinumThresholdPoints);
            command.Parameters.AddWithValue("$userId", user.Id);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            command.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict(
                    "loyalty_settings_changed",
                    "The loyalty settings changed. Reload and try again.");
            }
        }

        await using (var tiers = connection.CreateCommand())
        {
            tiers.Transaction = transaction;
            tiers.CommandText =
            """
            UPDATE crm_customer_profiles
            SET loyalty_tier = CASE
                    WHEN lifetime_points >= $platinum THEN 'platinum'
                    WHEN lifetime_points >= $gold THEN 'gold'
                    WHEN lifetime_points >= $silver THEN 'silver'
                    ELSE 'standard' END,
                version = version + 1,
                updated_at_utc = $now
            WHERE organization_id = $organizationId;
            """;
            tiers.Parameters.AddWithValue("$silver", request.SilverThresholdPoints);
            tiers.Parameters.AddWithValue("$gold", request.GoldThresholdPoints);
            tiers.Parameters.AddWithValue("$platinum", request.PlatinumThresholdPoints);
            tiers.Parameters.AddWithValue("$now", now.ToString("O"));
            tiers.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            await tiers.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "crm.loyalty.settings.updated",
            "loyalty_settings",
            context.OrganizationId,
            new
            {
                request.IsEnabled,
                request.SpendMinorPerPoint,
                request.MinimumRedeemPoints,
                request.SilverThresholdPoints,
                request.GoldThresholdPoints,
                request.PlatinumThresholdPoints,
                previousVersion = request.ExpectedVersion
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetLoyaltySettingsAsync(user, context, cancellationToken);
    }

    public async Task<IReadOnlyList<LoyaltyLedgerRecord>> ListLoyaltyLedgerAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string customerId,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(customerId);
        int limit = Math.Clamp(requestedLimit, 1, 2000);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
        """
        SELECT
            ledger.id,
            ledger.customer_id,
            customer.name,
            ledger.shop_id,
            COALESCE(shop.code, ''),
            ledger.sale_id,
            ledger.entry_type,
            ledger.points_delta,
            ledger.balance_after,
            ledger.reference_type,
            ledger.reference_id,
            ledger.reason,
            creator.display_name,
            ledger.created_at_utc
        FROM crm_loyalty_ledger AS ledger
        INNER JOIN finance_customers AS customer ON customer.id = ledger.customer_id
        LEFT JOIN shops AS shop ON shop.id = ledger.shop_id
        INNER JOIN users AS creator ON creator.id = ledger.created_by_user_id
        WHERE ledger.organization_id = $organizationId
          AND ledger.customer_id = $customerId
        ORDER BY ledger.created_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$customerId", id);
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<LoyaltyLedgerRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new LoyaltyLedgerRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                DateTimeOffset.Parse(reader.GetString(13))));
        }
        return records;
    }

    public async Task<LoyaltyLedgerRecord> AdjustLoyaltyPointsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string customerId,
        LoyaltyAdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireAdministrator(user, "adjust loyalty points");
        if (request.PointsDelta == 0)
        {
            throw Validation("loyalty_points_required", "The loyalty adjustment cannot be zero.");
        }
        string reason = RequiredText(
            request.Reason,
            500,
            "loyalty_reason_required",
            "Enter the reason for the loyalty adjustment.");
        return await PostManualLoyaltyEntryAsync(
            user,
            context,
            NormalizeId(customerId),
            request.PointsDelta,
            "adjustment",
            reason,
            "manual_adjustment",
            Guid.NewGuid().ToString("N"),
            cancellationToken);
    }

    public async Task<LoyaltyLedgerRecord> RedeemLoyaltyPointsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string customerId,
        LoyaltyRedemptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Points <= 0)
        {
            throw Validation("loyalty_points_required", "Enter a positive number of points to redeem.");
        }
        string reason = RequiredText(
            request.Reason,
            500,
            "loyalty_reason_required",
            "Enter the reason for the loyalty redemption.");
        string reference = OptionalText(request.Reference, 100);
        if (reference.Length == 0)
        {
            reference = Guid.NewGuid().ToString("N");
        }
        return await PostManualLoyaltyEntryAsync(
            user,
            context,
            NormalizeId(customerId),
            -request.Points,
            "redeem",
            reason,
            "redemption",
            reference,
            cancellationToken);
    }

    private async Task<LoyaltyLedgerRecord> PostManualLoyaltyEntryAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string customerId,
        long pointsDelta,
        string entryType,
        string reason,
        string referenceType,
        string referenceId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        await RequireCustomerAsync(
            connection,
            transaction,
            context.OrganizationId,
            customerId,
            includeInactive: false,
            cancellationToken);
        LoyaltySettingsRecord settings = await ReadLoyaltySettingsAsync(
            connection,
            transaction,
            context.OrganizationId,
            cancellationToken);
        if (!settings.IsEnabled)
        {
            throw Conflict("loyalty_programme_disabled", "Enable the loyalty programme before changing points.");
        }
        if (entryType == "redeem" && -pointsDelta < settings.MinimumRedeemPoints)
        {
            throw Validation(
                "loyalty_minimum_redemption",
                $"Redeem at least {settings.MinimumRedeemPoints} points.");
        }

        long currentPoints;
        bool enrolled;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            """
            SELECT current_points, loyalty_enrolled
            FROM crm_customer_profiles
            WHERE customer_id = $customerId
              AND organization_id = $organizationId;
            """;
            read.Parameters.AddWithValue("$customerId", customerId);
            read.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw NotFound("crm_profile_missing", "The customer CRM profile could not be found.");
            }
            currentPoints = reader.GetInt64(0);
            enrolled = reader.GetInt32(1) == 1;
        }
        if (!enrolled)
        {
            throw Conflict("customer_not_enrolled", "The customer is not enrolled in the loyalty programme.");
        }
        long newBalance = checked(currentPoints + pointsDelta);
        if (newBalance < 0)
        {
            throw Conflict("insufficient_loyalty_points", "The customer does not have enough loyalty points.");
        }

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
            """
            INSERT INTO crm_loyalty_ledger
            (
                id, organization_id, customer_id, shop_id, sale_id,
                entry_type, points_delta, balance_after,
                reference_type, reference_id, reason,
                created_by_user_id, created_at_utc
            )
            VALUES
            (
                $id, $organizationId, $customerId, $shopId, NULL,
                $entryType, $pointsDelta, $balanceAfter,
                $referenceType, $referenceId, $reason,
                $userId, $now
            );
            """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            insert.Parameters.AddWithValue("$customerId", customerId);
            insert.Parameters.AddWithValue("$shopId", context.ShopId);
            insert.Parameters.AddWithValue("$entryType", entryType);
            insert.Parameters.AddWithValue("$pointsDelta", pointsDelta);
            insert.Parameters.AddWithValue("$balanceAfter", newBalance);
            insert.Parameters.AddWithValue("$referenceType", referenceType);
            insert.Parameters.AddWithValue("$referenceId", referenceId);
            insert.Parameters.AddWithValue("$reason", reason);
            insert.Parameters.AddWithValue("$userId", user.Id);
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE crm_customer_profiles
            SET current_points = $balance,
                lifetime_points = CASE
                    WHEN $pointsDelta > 0 THEN lifetime_points + $pointsDelta
                    ELSE lifetime_points END,
                loyalty_tier = CASE
                    WHEN lifetime_points + CASE WHEN $pointsDelta > 0 THEN $pointsDelta ELSE 0 END >= $platinum THEN 'platinum'
                    WHEN lifetime_points + CASE WHEN $pointsDelta > 0 THEN $pointsDelta ELSE 0 END >= $gold THEN 'gold'
                    WHEN lifetime_points + CASE WHEN $pointsDelta > 0 THEN $pointsDelta ELSE 0 END >= $silver THEN 'silver'
                    ELSE 'standard' END,
                version = version + 1,
                updated_at_utc = $now
            WHERE customer_id = $customerId
              AND organization_id = $organizationId;
            """;
            update.Parameters.AddWithValue("$balance", newBalance);
            update.Parameters.AddWithValue("$pointsDelta", pointsDelta);
            update.Parameters.AddWithValue("$silver", settings.SilverThresholdPoints);
            update.Parameters.AddWithValue("$gold", settings.GoldThresholdPoints);
            update.Parameters.AddWithValue("$platinum", settings.PlatinumThresholdPoints);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$customerId", customerId);
            update.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("loyalty_balance_changed", "The customer loyalty balance changed. Try again.");
            }
        }

        await WriteAuditAsync(
            connection,
            transaction,
            user,
            $"crm.loyalty.{entryType}",
            "loyalty_ledger",
            id,
            new { customerId, pointsDelta, newBalance, reason, referenceType, referenceId },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListLoyaltyLedgerAsync(user, context, customerId, 2000, cancellationToken))
            .Single(item => item.Id == id);
    }

    public async Task<IReadOnlyList<QuotationRecord>> ListQuotationsAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string? customerId,
        string? requestedStatus,
        int requestedLimit,
        CancellationToken cancellationToken = default)
    {
        string customer = string.IsNullOrWhiteSpace(customerId)
            ? string.Empty
            : NormalizeId(customerId);
        string status = requestedStatus?.Trim().ToLowerInvariant() ?? string.Empty;
        string[] statuses = ["draft", "sent", "accepted", "rejected", "expired", "converted", "cancelled"];
        if (status.Length > 0 && !statuses.Contains(status, StringComparer.Ordinal))
        {
            throw Validation("invalid_quotation_status", "The quotation status is invalid.");
        }
        int limit = Math.Clamp(requestedLimit, 1, 1000);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await ExpireQuotationsAsync(connection, null, context.OrganizationId, context.ShopId, cancellationToken);
        IReadOnlyList<QuotationSnapshot> snapshots = await ReadQuotationSnapshotsAsync(
            connection,
            null,
            context,
            quotationId: null,
            customer,
            status,
            limit,
            cancellationToken);
        return snapshots
            .Select(snapshot => ToQuotationRecord(snapshot, Array.Empty<QuotationLineRecord>()))
            .ToList();
    }

    public async Task<QuotationRecord> GetQuotationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string quotationId,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(quotationId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await RequireReadAccessAsync(connection, null, user, context.ShopId, cancellationToken);
        await ExpireQuotationsAsync(connection, null, context.OrganizationId, context.ShopId, cancellationToken);
        return await ReadQuotationRecordAsync(connection, null, context, id, cancellationToken);
    }

    public async Task<QuotationRecord> CreateQuotationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        CreateQuotationRequest request,
        CancellationToken cancellationToken = default)
    {
        string customerId = NormalizeId(request.CustomerId);
        string quotationDate = string.IsNullOrWhiteSpace(request.QuotationDate)
            ? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
            : NormalizeDate(request.QuotationDate, "invalid_quotation_date");
        string validUntil = NormalizeDate(request.ValidUntil, "invalid_quotation_validity");
        if (string.CompareOrdinal(validUntil, quotationDate) < 0)
        {
            throw Validation("invalid_quotation_validity", "Quotation validity cannot end before its date.");
        }
        string notes = OptionalText(request.Notes, 2000);
        string terms = OptionalText(request.Terms, 2000);
        IReadOnlyList<NormalizedQuotationLine> requestedLines = NormalizeQuotationLines(request.Lines);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        await RequireCustomerAsync(
            connection,
            transaction,
            context.OrganizationId,
            customerId,
            includeInactive: false,
            cancellationToken);

        IReadOnlyList<PreparedQuotationLine> lines = await PrepareQuotationLinesAsync(
            connection,
            transaction,
            requestedLines,
            cancellationToken);
        long subtotal = checked(lines.Sum(line => line.LineTotalMinor));
        if (request.DiscountMinor < 0 || request.DiscountMinor > subtotal)
        {
            throw Validation("invalid_quotation_discount", "The quotation discount is invalid.");
        }
        long total = subtotal - request.DiscountMinor;
        if (total <= 0)
        {
            throw Validation("quotation_total_invalid", "The quotation total must be greater than zero.");
        }

        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string number = await NextQuotationNumberAsync(
            connection,
            transaction,
            context,
            now,
            cancellationToken);
        await InsertQuotationHeaderAsync(
            connection,
            transaction,
            id,
            number,
            context,
            customerId,
            quotationDate,
            validUntil,
            subtotal,
            request.DiscountMinor,
            total,
            notes,
            terms,
            user.Id,
            now,
            cancellationToken);
        await ReplaceQuotationLinesAsync(
            connection,
            transaction,
            id,
            lines,
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "crm.quotation.created",
            "quotation",
            id,
            new { number, customerId, subtotalMinor = subtotal, request.DiscountMinor, totalMinor = total },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetQuotationAsync(user, context, id, cancellationToken);
    }

    public async Task<QuotationRecord> UpdateQuotationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string quotationId,
        UpdateQuotationRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(quotationId);
        string customerId = NormalizeId(request.CustomerId);
        string quotationDate = NormalizeDate(request.QuotationDate, "invalid_quotation_date");
        string validUntil = NormalizeDate(request.ValidUntil, "invalid_quotation_validity");
        if (string.CompareOrdinal(validUntil, quotationDate) < 0 || request.ExpectedVersion < 1)
        {
            throw Validation("invalid_quotation_update", "The quotation version or validity is invalid.");
        }
        string notes = OptionalText(request.Notes, 2000);
        string terms = OptionalText(request.Terms, 2000);
        IReadOnlyList<NormalizedQuotationLine> requestedLines = NormalizeQuotationLines(request.Lines);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        QuotationHeader header = await RequireQuotationHeaderAsync(
            connection,
            transaction,
            context,
            id,
            cancellationToken);
        if (header.Status != "draft" || header.Version != request.ExpectedVersion)
        {
            throw Conflict("quotation_changed", "Only the current draft quotation can be edited.");
        }
        await RequireCustomerAsync(
            connection,
            transaction,
            context.OrganizationId,
            customerId,
            includeInactive: false,
            cancellationToken);
        IReadOnlyList<PreparedQuotationLine> lines = await PrepareQuotationLinesAsync(
            connection,
            transaction,
            requestedLines,
            cancellationToken);
        long subtotal = checked(lines.Sum(line => line.LineTotalMinor));
        if (request.DiscountMinor < 0 || request.DiscountMinor > subtotal || subtotal - request.DiscountMinor <= 0)
        {
            throw Validation("invalid_quotation_discount", "The quotation discount or total is invalid.");
        }
        long total = subtotal - request.DiscountMinor;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
            """
            UPDATE crm_quotations
            SET customer_id = $customerId,
                quotation_date = $quotationDate,
                valid_until = $validUntil,
                subtotal_minor = $subtotal,
                discount_minor = $discount,
                total_minor = $total,
                notes = $notes,
                terms = $terms,
                version = version + 1,
                updated_by_user_id = $userId,
                updated_at_utc = $now
            WHERE id = $id
              AND organization_id = $organizationId
              AND shop_id = $shopId
              AND status = 'draft'
              AND version = $expectedVersion;
            """;
            update.Parameters.AddWithValue("$customerId", customerId);
            update.Parameters.AddWithValue("$quotationDate", quotationDate);
            update.Parameters.AddWithValue("$validUntil", validUntil);
            update.Parameters.AddWithValue("$subtotal", subtotal);
            update.Parameters.AddWithValue("$discount", request.DiscountMinor);
            update.Parameters.AddWithValue("$total", total);
            update.Parameters.AddWithValue("$notes", notes);
            update.Parameters.AddWithValue("$terms", terms);
            update.Parameters.AddWithValue("$userId", user.Id);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$organizationId", context.OrganizationId);
            update.Parameters.AddWithValue("$shopId", context.ShopId);
            update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("quotation_changed", "The quotation changed. Reload and try again.");
            }
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM crm_quotation_lines WHERE quotation_id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await ReplaceQuotationLinesAsync(connection, transaction, id, lines, cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            user,
            "crm.quotation.updated",
            "quotation",
            id,
            new { header.Number, subtotalMinor = subtotal, request.DiscountMinor, totalMinor = total },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetQuotationAsync(user, context, id, cancellationToken);
    }

    public Task<QuotationRecord> SendQuotationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string quotationId,
        QuotationActionRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionQuotationAsync(user, context, quotationId, request.ExpectedVersion, "draft", "sent", cancellationToken);

    public Task<QuotationRecord> AcceptQuotationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string quotationId,
        QuotationActionRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionQuotationAsync(user, context, quotationId, request.ExpectedVersion, "sent", "accepted", cancellationToken);

    public Task<QuotationRecord> RejectQuotationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string quotationId,
        QuotationActionRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionQuotationAsync(user, context, quotationId, request.ExpectedVersion, "sent", "rejected", cancellationToken);

    public async Task<QuotationRecord> CancelQuotationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string quotationId,
        QuotationActionRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(quotationId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        QuotationHeader header = await RequireQuotationHeaderAsync(connection, transaction, context, id, cancellationToken);
        if (header.Status is not ("draft" or "sent" or "accepted") || header.Version != request.ExpectedVersion)
        {
            throw Conflict("quotation_changed", "The quotation cannot be cancelled in its current state.");
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE crm_quotations
        SET status = 'cancelled',
            closed_at_utc = $now,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE id = $id
          AND status = $status
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$userId", user.Id);
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$status", header.Status);
        update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("quotation_changed", "The quotation changed. Reload and try again.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "crm.quotation.cancelled", "quotation", id,
            new { header.Number, previousVersion = request.ExpectedVersion }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetQuotationAsync(user, context, id, cancellationToken);
    }

    public async Task<QuotationRecord> ConvertQuotationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string quotationId,
        ConvertQuotationRequest request,
        CancellationToken cancellationToken = default)
    {
        string id = NormalizeId(quotationId);
        string saleId = NormalizeId(request.SaleId);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        QuotationHeader header = await RequireQuotationHeaderAsync(connection, transaction, context, id, cancellationToken);
        if (header.Status != "accepted" || header.Version != request.ExpectedVersion)
        {
            throw Conflict("quotation_changed", "Only the current accepted quotation can be converted.");
        }
        await using (var sale = connection.CreateCommand())
        {
            sale.Transaction = transaction;
            sale.CommandText =
            """
            SELECT COUNT(1)
            FROM sales
            WHERE id = $saleId
              AND shop_id = $shopId
              AND customer_id = $customerId
              AND status = 'completed';
            """;
            sale.Parameters.AddWithValue("$saleId", saleId);
            sale.Parameters.AddWithValue("$shopId", context.ShopId);
            sale.Parameters.AddWithValue("$customerId", header.CustomerId);
            if (Convert.ToInt32(await sale.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                throw Conflict(
                    "quotation_sale_mismatch",
                    "The completed sale must belong to the quotation customer and active branch.");
            }
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE crm_quotations
        SET status = 'converted',
            sale_id = $saleId,
            converted_at_utc = $now,
            closed_at_utc = $now,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE id = $id
          AND status = 'accepted'
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$saleId", saleId);
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$userId", user.Id);
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$expectedVersion", request.ExpectedVersion);
        try
        {
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw Conflict("quotation_changed", "The quotation changed. Reload and try again.");
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Conflict("sale_already_linked", "This sale is already linked to another quotation.");
        }
        await WriteAuditAsync(
            connection, transaction, user, "crm.quotation.converted", "quotation", id,
            new { header.Number, saleId }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetQuotationAsync(user, context, id, cancellationToken);
    }

    private async Task<QuotationRecord> TransitionQuotationAsync(
        AuthenticatedUser user,
        ActiveShopContextRecord context,
        string quotationId,
        int expectedVersion,
        string expectedStatus,
        string newStatus,
        CancellationToken cancellationToken)
    {
        string id = NormalizeId(quotationId);
        if (expectedVersion < 1)
        {
            throw Validation("invalid_quotation_version", "The expected quotation version is invalid.");
        }
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await RequireWriteAccessAsync(connection, transaction, user, context.ShopId, cancellationToken);
        QuotationHeader header = await RequireQuotationHeaderAsync(connection, transaction, context, id, cancellationToken);
        if (header.Status != expectedStatus || header.Version != expectedVersion)
        {
            throw Conflict("quotation_changed", $"The quotation must be {expectedStatus} before it can be {newStatus}.");
        }
        if (newStatus == "sent" && string.CompareOrdinal(header.ValidUntil, DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")) < 0)
        {
            throw Conflict("quotation_expired", "An expired draft quotation cannot be sent.");
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE crm_quotations
        SET status = $newStatus,
            sent_at_utc = CASE WHEN $newStatus = 'sent' THEN $now ELSE sent_at_utc END,
            accepted_at_utc = CASE WHEN $newStatus = 'accepted' THEN $now ELSE accepted_at_utc END,
            closed_at_utc = CASE WHEN $newStatus IN ('rejected', 'cancelled') THEN $now ELSE closed_at_utc END,
            version = version + 1,
            updated_by_user_id = $userId,
            updated_at_utc = $now
        WHERE id = $id
          AND status = $expectedStatus
          AND version = $expectedVersion;
        """;
        update.Parameters.AddWithValue("$newStatus", newStatus);
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$userId", user.Id);
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$expectedStatus", expectedStatus);
        update.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("quotation_changed", "The quotation changed. Reload and try again.");
        }
        await WriteAuditAsync(
            connection, transaction, user, $"crm.quotation.{newStatus}", "quotation", id,
            new { header.Number, previousVersion = expectedVersion }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetQuotationAsync(user, context, id, cancellationToken);
    }

    private static IReadOnlyList<NormalizedQuotationLine> NormalizeQuotationLines(
        IReadOnlyList<QuotationLineRequest>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            throw Validation("quotation_lines_required", "Add at least one quotation line.");
        }
        if (lines.Count > 100)
        {
            throw Validation("too_many_quotation_lines", "A quotation cannot contain more than 100 lines.");
        }
        var result = new List<NormalizedQuotationLine>();
        foreach (IGrouping<string, QuotationLineRequest> group in
                 lines.GroupBy(line => NormalizeId(line.ProductId), StringComparer.Ordinal))
        {
            long quantity = checked(group.Sum(line => line.Quantity));
            List<long?> prices = group.Select(line => line.UnitPriceMinor).Distinct().ToList();
            if (quantity <= 0 || prices.Count != 1 || prices[0] < 0)
            {
                throw Validation(
                    "invalid_quotation_line",
                    "Each product requires positive quantity and one non-negative optional unit price.");
            }
            result.Add(new NormalizedQuotationLine(group.Key, quantity, prices[0]));
        }
        return result;
    }

    private static async Task<IReadOnlyList<PreparedQuotationLine>> PrepareQuotationLinesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<NormalizedQuotationLine> requestedLines,
        CancellationToken cancellationToken)
    {
        var result = new List<PreparedQuotationLine>();
        foreach (NormalizedQuotationLine requested in requestedLines)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            SELECT id, name, sku, selling_price_minor, is_active
            FROM products
            WHERE id = $id
            LIMIT 1;
            """;
            command.Parameters.AddWithValue("$id", requested.ProductId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw NotFound("product_not_found", "A quotation product could not be found.");
            }
            if (reader.GetInt32(4) != 1)
            {
                throw Conflict("product_inactive", $"{reader.GetString(1)} is inactive.");
            }
            long unitPrice = requested.UnitPriceMinor ?? reader.GetInt64(3);
            result.Add(new PreparedQuotationLine(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                requested.Quantity,
                unitPrice,
                checked(requested.Quantity * unitPrice)));
        }
        return result;
    }

    private static async Task InsertQuotationHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string number,
        ActiveShopContextRecord context,
        string customerId,
        string quotationDate,
        string validUntil,
        long subtotal,
        long discount,
        long total,
        string notes,
        string terms,
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        INSERT INTO crm_quotations
        (
            id, organization_id, shop_id, quotation_number, customer_id,
            status, quotation_date, valid_until, currency_code,
            subtotal_minor, discount_minor, total_minor, notes, terms,
            version, created_by_user_id, updated_by_user_id,
            created_at_utc, updated_at_utc
        )
        VALUES
        (
            $id, $organizationId, $shopId, $number, $customerId,
            'draft', $quotationDate, $validUntil, $currencyCode,
            $subtotal, $discount, $total, $notes, $terms,
            1, $userId, $userId, $now, $now
        );
        """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$customerId", customerId);
        command.Parameters.AddWithValue("$quotationDate", quotationDate);
        command.Parameters.AddWithValue("$validUntil", validUntil);
        command.Parameters.AddWithValue("$currencyCode", context.CurrencyCode);
        command.Parameters.AddWithValue("$subtotal", subtotal);
        command.Parameters.AddWithValue("$discount", discount);
        command.Parameters.AddWithValue("$total", total);
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$terms", terms);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceQuotationLinesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string quotationId,
        IReadOnlyList<PreparedQuotationLine> lines,
        CancellationToken cancellationToken)
    {
        int lineNumber = 1;
        foreach (PreparedQuotationLine line in lines)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
            """
            INSERT INTO crm_quotation_lines
            (
                id, quotation_id, line_number, product_id,
                product_name_snapshot, sku_snapshot,
                quantity, unit_price_minor, line_total_minor
            )
            VALUES
            (
                $id, $quotationId, $lineNumber, $productId,
                $productName, $sku,
                $quantity, $unitPrice, $lineTotal
            );
            """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$quotationId", quotationId);
            command.Parameters.AddWithValue("$lineNumber", lineNumber++);
            command.Parameters.AddWithValue("$productId", line.ProductId);
            command.Parameters.AddWithValue("$productName", line.ProductName);
            command.Parameters.AddWithValue("$sku", line.Sku);
            command.Parameters.AddWithValue("$quantity", line.Quantity);
            command.Parameters.AddWithValue("$unitPrice", line.UnitPriceMinor);
            command.Parameters.AddWithValue("$lineTotal", line.LineTotalMinor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<string> NextQuotationNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string prefix = $"QTN-{NormalizeShopCode(context.ShopCode)}";
        await using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText =
            """
            INSERT OR IGNORE INTO crm_quotation_sequences
            (shop_id, prefix, next_value, updated_at_utc)
            VALUES ($shopId, $prefix, 1, $now);
            """;
            ensure.Parameters.AddWithValue("$shopId", context.ShopId);
            ensure.Parameters.AddWithValue("$prefix", prefix);
            ensure.Parameters.AddWithValue("$now", now.ToString("O"));
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }
        string storedPrefix;
        long nextValue;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
            "SELECT prefix, next_value FROM crm_quotation_sequences WHERE shop_id = $shopId;";
            read.Parameters.AddWithValue("$shopId", context.ShopId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw Conflict("quotation_sequence_missing", "The quotation sequence could not be found.");
            }
            storedPrefix = reader.GetString(0);
            nextValue = reader.GetInt64(1);
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
        """
        UPDATE crm_quotation_sequences
        SET next_value = next_value + 1,
            updated_at_utc = $now
        WHERE shop_id = $shopId
          AND next_value = $expected;
        """;
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$shopId", context.ShopId);
        update.Parameters.AddWithValue("$expected", nextValue);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict("quotation_sequence_conflict", "Another quotation was created simultaneously. Try again.");
        }
        return $"{storedPrefix}-{now:yyyyMMdd}-{nextValue:000000}";
    }

    private static string NormalizeShopCode(string value)
    {
        char[] normalized = value.Trim().ToUpperInvariant()
            .Where(character => char.IsLetterOrDigit(character) || character == '-')
            .Take(16)
            .ToArray();
        return normalized.Length == 0 ? "SHOP" : new string(normalized);
    }

    private static async Task ExpireQuotationsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string organizationId,
        string shopId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        UPDATE crm_quotations
        SET status = 'expired',
            closed_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
            updated_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
            version = version + 1
        WHERE organization_id = $organizationId
          AND shop_id = $shopId
          AND status = 'sent'
          AND valid_until < date('now');
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        command.Parameters.AddWithValue("$shopId", shopId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<QuotationSnapshot>> ReadQuotationSnapshotsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ActiveShopContextRecord context,
        string? quotationId,
        string customerId,
        string status,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT
            quotation.id,
            quotation.quotation_number,
            quotation.organization_id,
            quotation.shop_id,
            shop.code,
            quotation.customer_id,
            customer.customer_number,
            customer.name,
            quotation.status,
            quotation.quotation_date,
            quotation.valid_until,
            quotation.currency_code,
            quotation.subtotal_minor,
            quotation.discount_minor,
            quotation.total_minor,
            quotation.notes,
            quotation.terms,
            quotation.sale_id,
            quotation.version,
            creator.display_name,
            quotation.created_at_utc,
            quotation.updated_at_utc,
            quotation.sent_at_utc,
            quotation.accepted_at_utc,
            quotation.converted_at_utc,
            quotation.closed_at_utc
        FROM crm_quotations AS quotation
        INNER JOIN shops AS shop ON shop.id = quotation.shop_id
        INNER JOIN finance_customers AS customer ON customer.id = quotation.customer_id
        INNER JOIN users AS creator ON creator.id = quotation.created_by_user_id
        WHERE quotation.organization_id = $organizationId
          AND quotation.shop_id = $shopId
          AND ($quotationId IS NULL OR quotation.id = $quotationId)
          AND ($customerId = '' OR quotation.customer_id = $customerId)
          AND ($status = '' OR quotation.status = $status)
        ORDER BY quotation.quotation_date DESC, quotation.created_at_utc DESC
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        command.Parameters.AddWithValue("$quotationId", quotationId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$customerId", customerId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", limit);
        var records = new List<QuotationSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new QuotationSnapshot(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetString(15),
                reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17), reader.GetInt32(18),
                reader.GetString(19), DateTimeOffset.Parse(reader.GetString(20)),
                DateTimeOffset.Parse(reader.GetString(21)),
                reader.IsDBNull(22) ? null : DateTimeOffset.Parse(reader.GetString(22)),
                reader.IsDBNull(23) ? null : DateTimeOffset.Parse(reader.GetString(23)),
                reader.IsDBNull(24) ? null : DateTimeOffset.Parse(reader.GetString(24)),
                reader.IsDBNull(25) ? null : DateTimeOffset.Parse(reader.GetString(25))));
        }
        return records;
    }

    private static async Task<QuotationRecord> ReadQuotationRecordAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ActiveShopContextRecord context,
        string quotationId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<QuotationSnapshot> snapshots = await ReadQuotationSnapshotsAsync(
            connection, transaction, context, quotationId, string.Empty, string.Empty, 1, cancellationToken);
        if (snapshots.Count != 1)
        {
            throw NotFound("quotation_not_found", "The quotation could not be found in the active branch.");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT id, line_number, product_id, product_name_snapshot,
               sku_snapshot, quantity, unit_price_minor, line_total_minor
        FROM crm_quotation_lines
        WHERE quotation_id = $quotationId
        ORDER BY line_number;
        """;
        command.Parameters.AddWithValue("$quotationId", quotationId);
        var lines = new List<QuotationLineRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new QuotationLineRecord(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));
        }
        return ToQuotationRecord(snapshots[0], lines);
    }

    private static QuotationRecord ToQuotationRecord(
        QuotationSnapshot snapshot,
        IReadOnlyList<QuotationLineRecord> lines) =>
        new(
            snapshot.Id, snapshot.Number, snapshot.OrganizationId, snapshot.ShopId, snapshot.ShopCode,
            snapshot.CustomerId, snapshot.CustomerNumber, snapshot.CustomerName, snapshot.Status,
            snapshot.QuotationDate, snapshot.ValidUntil, snapshot.CurrencyCode, snapshot.SubtotalMinor,
            snapshot.DiscountMinor, snapshot.TotalMinor, snapshot.Notes, snapshot.Terms, snapshot.SaleId,
            snapshot.Version, snapshot.CreatedByName, snapshot.CreatedAtUtc, snapshot.UpdatedAtUtc,
            snapshot.SentAtUtc, snapshot.AcceptedAtUtc, snapshot.ConvertedAtUtc, snapshot.ClosedAtUtc,
            string.CompareOrdinal(snapshot.ValidUntil, DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")) < 0,
            lines);

    private static async Task<QuotationHeader> RequireQuotationHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveShopContextRecord context,
        string quotationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT id, quotation_number, customer_id, status, valid_until, version
        FROM crm_quotations
        WHERE id = $id
          AND organization_id = $organizationId
          AND shop_id = $shopId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$id", quotationId);
        command.Parameters.AddWithValue("$organizationId", context.OrganizationId);
        command.Parameters.AddWithValue("$shopId", context.ShopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound("quotation_not_found", "The quotation could not be found in the active branch.");
        }
        return new QuotationHeader(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetInt32(5));
    }

    private static async Task<LoyaltySettingsRecord> ReadLoyaltySettingsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        SELECT organization_id, is_enabled, spend_minor_per_point,
               minimum_redeem_points, silver_threshold_points,
               gold_threshold_points, platinum_threshold_points,
               version, updated_at_utc
        FROM crm_loyalty_settings
        WHERE organization_id = $organizationId
        LIMIT 1;
        """;
        command.Parameters.AddWithValue("$organizationId", organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw NotFound("loyalty_settings_missing", "The loyalty programme settings could not be found.");
        }
        return new LoyaltySettingsRecord(
            reader.GetString(0), reader.GetInt32(1) == 1, reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
            reader.GetInt32(7), DateTimeOffset.Parse(reader.GetString(8)));
    }

    private sealed record NormalizedQuotationLine(string ProductId, long Quantity, long? UnitPriceMinor);
    private sealed record PreparedQuotationLine(
        string ProductId,
        string ProductName,
        string Sku,
        long Quantity,
        long UnitPriceMinor,
        long LineTotalMinor);
    private sealed record QuotationHeader(
        string Id,
        string Number,
        string CustomerId,
        string Status,
        string ValidUntil,
        int Version);
    private sealed record QuotationSnapshot(
        string Id,
        string Number,
        string OrganizationId,
        string ShopId,
        string ShopCode,
        string CustomerId,
        string CustomerNumber,
        string CustomerName,
        string Status,
        string QuotationDate,
        string ValidUntil,
        string CurrencyCode,
        long SubtotalMinor,
        long DiscountMinor,
        long TotalMinor,
        string Notes,
        string Terms,
        string? SaleId,
        int Version,
        string CreatedByName,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? SentAtUtc,
        DateTimeOffset? AcceptedAtUtc,
        DateTimeOffset? ConvertedAtUtc,
        DateTimeOffset? ClosedAtUtc);
}
