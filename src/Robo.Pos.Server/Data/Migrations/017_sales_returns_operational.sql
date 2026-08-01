DROP TRIGGER IF EXISTS trg_sale_item_update_guard;

CREATE TRIGGER trg_sale_item_update_guard
BEFORE UPDATE OF
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
    line_total_minor
ON sale_items
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'sale'
      AND source_id = OLD.sale_id
)
BEGIN
    SELECT RAISE(ABORT, 'posted sale item financial values are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_item_return_counter_guard
BEFORE UPDATE OF returned_quantity ON sale_items
BEGIN
    SELECT CASE
        WHEN NEW.returned_quantity < OLD.returned_quantity
          OR NEW.returned_quantity > OLD.quantity
        THEN RAISE(ABORT, 'sale item returned quantity is invalid')
    END;

    SELECT CASE
        WHEN NEW.returned_quantity > OLD.returned_quantity
         AND NOT EXISTS
        (
            SELECT 1
            FROM sales_return_items AS return_item
            INNER JOIN sales_returns AS header
                ON header.id = return_item.return_id
            WHERE return_item.sale_item_id = OLD.id
              AND header.status = 'draft'
              AND OLD.returned_quantity + return_item.quantity = NEW.returned_quantity
        )
        THEN RAISE(ABORT, 'sale item return counter requires a matching draft return')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_shift_close_return_aware_cash
AFTER UPDATE OF status ON teller_shifts
WHEN OLD.status = 'open' AND NEW.status = 'closed'
BEGIN
    UPDATE teller_shifts
    SET expected_cash_minor =
        opening_cash_minor
        + COALESCE
        (
            (
                SELECT SUM(payment.amount_minor)
                FROM sale_payments AS payment
                INNER JOIN sales AS sale
                    ON sale.id = payment.sale_id
                WHERE sale.shift_id = NEW.id
                  AND sale.shop_id = NEW.shop_id
                  AND sale.status IN ('completed', 'partially_returned', 'returned')
                  AND payment.payment_method = 'cash'
            ),
            0
        )
        - COALESCE
        (
            (
                SELECT SUM(refund.refund_amount_minor)
                FROM sales_returns AS refund
                WHERE refund.shift_id = NEW.id
                  AND refund.shop_id = NEW.shop_id
                  AND refund.status = 'completed'
                  AND refund.refund_method = 'cash'
            ),
            0
        ),
        cash_variance_minor = counted_cash_minor -
        (
            opening_cash_minor
            + COALESCE
            (
                (
                    SELECT SUM(payment.amount_minor)
                    FROM sale_payments AS payment
                    INNER JOIN sales AS sale
                        ON sale.id = payment.sale_id
                    WHERE sale.shift_id = NEW.id
                      AND sale.shop_id = NEW.shop_id
                      AND sale.status IN ('completed', 'partially_returned', 'returned')
                      AND payment.payment_method = 'cash'
                ),
                0
            )
            - COALESCE
            (
                (
                    SELECT SUM(refund.refund_amount_minor)
                    FROM sales_returns AS refund
                    WHERE refund.shift_id = NEW.id
                      AND refund.shop_id = NEW.shop_id
                      AND refund.status = 'completed'
                      AND refund.refund_method = 'cash'
                ),
                0
            )
        )
    WHERE id = NEW.id;
END;

CREATE VIEW IF NOT EXISTS sales_return_reporting_rows AS
SELECT
    header.id AS return_id,
    header.organization_id,
    header.shop_id,
    header.sale_id,
    header.return_number,
    header.refund_method,
    header.refund_amount_minor,
    header.returned_cost_minor,
    header.restocked_cost_minor,
    header.completed_at_utc,
    header.status
FROM sales_returns AS header;

CREATE VIEW IF NOT EXISTS sales_return_loyalty_adjustments AS
SELECT
    header.id AS return_id,
    header.organization_id,
    header.shop_id,
    header.sale_id,
    sale.customer_id,
    header.approved_by_user_id,
    header.completed_at_utc,
    header.return_number,
    profile.current_points,
    profile.lifetime_points,
    CASE
        WHEN sale.customer_id IS NULL
          OR settings.is_enabled <> 1
          OR profile.loyalty_enrolled <> 1
          OR earn.points_delta IS NULL
        THEN 0
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM sale_items AS remaining
            WHERE remaining.sale_id = sale.id
              AND remaining.returned_quantity < remaining.quantity
        )
        THEN MAX(
            earn.points_delta - COALESCE
            (
                (
                    SELECT -SUM(existing.points_delta)
                    FROM crm_loyalty_ledger AS existing
                    WHERE existing.sale_id = sale.id
                      AND existing.entry_type = 'adjustment'
                      AND existing.reference_type = 'sale_return'
                ),
                0
            ),
            0
        )
        ELSE MIN
        (
            MAX(
                earn.points_delta - COALESCE
                (
                    (
                        SELECT -SUM(existing.points_delta)
                        FROM crm_loyalty_ledger AS existing
                        WHERE existing.sale_id = sale.id
                          AND existing.entry_type = 'adjustment'
                          AND existing.reference_type = 'sale_return'
                    ),
                    0
                ),
                0
            ),
            CAST(header.refund_amount_minor / settings.spend_minor_per_point AS INTEGER)
        )
    END AS points_to_reverse
FROM sales_returns AS header
INNER JOIN sales AS sale
    ON sale.id = header.sale_id
LEFT JOIN crm_customer_profiles AS profile
    ON profile.customer_id = sale.customer_id
LEFT JOIN crm_loyalty_settings AS settings
    ON settings.organization_id = header.organization_id
LEFT JOIN crm_loyalty_ledger AS earn
    ON earn.sale_id = sale.id
   AND earn.entry_type = 'earn'
WHERE header.status = 'completed';

CREATE TRIGGER IF NOT EXISTS trg_sales_return_loyalty_adjustment
AFTER UPDATE OF status ON sales_returns
WHEN OLD.status = 'draft' AND NEW.status = 'completed'
BEGIN
    INSERT INTO crm_loyalty_ledger
    (
        id, organization_id, customer_id, shop_id, sale_id,
        entry_type, points_delta, balance_after,
        reference_type, reference_id, reason,
        created_by_user_id, created_at_utc
    )
    SELECT
        lower(hex(randomblob(16))),
        adjustment.organization_id,
        adjustment.customer_id,
        adjustment.shop_id,
        adjustment.sale_id,
        'adjustment',
        -adjustment.points_to_reverse,
        adjustment.current_points - adjustment.points_to_reverse,
        'sale_return',
        adjustment.return_id,
        'Automatic loyalty adjustment for ' || adjustment.return_number,
        adjustment.approved_by_user_id,
        adjustment.completed_at_utc
    FROM sales_return_loyalty_adjustments AS adjustment
    WHERE adjustment.return_id = NEW.id
      AND adjustment.points_to_reverse > 0
      AND NOT EXISTS
      (
          SELECT 1
          FROM crm_loyalty_ledger
          WHERE reference_type = 'sale_return'
            AND reference_id = NEW.id
      );

    UPDATE crm_customer_profiles
    SET current_points = current_points +
        COALESCE
        (
            (
                SELECT points_delta
                FROM crm_loyalty_ledger
                WHERE reference_type = 'sale_return'
                  AND reference_id = NEW.id
            ),
            0
        ),
        lifetime_points = MAX
        (
            lifetime_points + COALESCE
            (
                (
                    SELECT points_delta
                    FROM crm_loyalty_ledger
                    WHERE reference_type = 'sale_return'
                      AND reference_id = NEW.id
                ),
                0
            ),
            0
        ),
        loyalty_tier = CASE
            WHEN MAX
            (
                lifetime_points + COALESCE
                (
                    (
                        SELECT points_delta
                        FROM crm_loyalty_ledger
                        WHERE reference_type = 'sale_return'
                          AND reference_id = NEW.id
                    ),
                    0
                ),
                0
            ) >=
            (
                SELECT platinum_threshold_points
                FROM crm_loyalty_settings
                WHERE organization_id = NEW.organization_id
            ) THEN 'platinum'
            WHEN MAX
            (
                lifetime_points + COALESCE
                (
                    (
                        SELECT points_delta
                        FROM crm_loyalty_ledger
                        WHERE reference_type = 'sale_return'
                          AND reference_id = NEW.id
                    ),
                    0
                ),
                0
            ) >=
            (
                SELECT gold_threshold_points
                FROM crm_loyalty_settings
                WHERE organization_id = NEW.organization_id
            ) THEN 'gold'
            WHEN MAX
            (
                lifetime_points + COALESCE
                (
                    (
                        SELECT points_delta
                        FROM crm_loyalty_ledger
                        WHERE reference_type = 'sale_return'
                          AND reference_id = NEW.id
                    ),
                    0
                ),
                0
            ) >=
            (
                SELECT silver_threshold_points
                FROM crm_loyalty_settings
                WHERE organization_id = NEW.organization_id
            ) THEN 'silver'
            ELSE 'standard'
        END,
        updated_at_utc = NEW.completed_at_utc,
        version = version + 1
    WHERE customer_id =
    (
        SELECT customer_id
        FROM sales
        WHERE id = NEW.sale_id
    )
      AND EXISTS
      (
          SELECT 1
          FROM crm_loyalty_ledger
          WHERE reference_type = 'sale_return'
            AND reference_id = NEW.id
      );
END;

DROP VIEW IF EXISTS crm_customer_segments;
DROP VIEW IF EXISTS crm_customer_sales_metrics;

CREATE VIEW crm_customer_sales_metrics AS
WITH sale_metrics AS
(
    SELECT
        sale.customer_id,
        COUNT(*) AS completed_sale_count,
        COALESCE(SUM(sale.total_minor), 0) AS gross_spend_minor,
        MIN(sale.completed_at_utc) AS first_sale_at_utc,
        MAX(sale.completed_at_utc) AS last_sale_at_utc,
        COUNT(DISTINCT sale.shop_id) AS shop_count
    FROM sales AS sale
    WHERE sale.customer_id IS NOT NULL
      AND sale.status IN ('completed', 'partially_returned', 'returned')
    GROUP BY sale.customer_id
),
return_metrics AS
(
    SELECT
        sale.customer_id,
        COALESCE(SUM(header.refund_amount_minor), 0) AS returned_spend_minor
    FROM sales_returns AS header
    INNER JOIN sales AS sale
        ON sale.id = header.sale_id
    WHERE header.status = 'completed'
      AND sale.customer_id IS NOT NULL
    GROUP BY sale.customer_id
)
SELECT
    customer.organization_id,
    customer.id AS customer_id,
    COALESCE(sales.completed_sale_count, 0) AS completed_sale_count,
    MAX(
        COALESCE(sales.gross_spend_minor, 0) - COALESCE(refunds.returned_spend_minor, 0),
        0
    ) AS lifetime_spend_minor,
    CASE
        WHEN COALESCE(sales.completed_sale_count, 0) = 0 THEN 0
        ELSE MAX
        (
            COALESCE(sales.gross_spend_minor, 0) - COALESCE(refunds.returned_spend_minor, 0),
            0
        ) / sales.completed_sale_count
    END AS average_sale_minor,
    sales.first_sale_at_utc,
    sales.last_sale_at_utc,
    COALESCE(sales.shop_count, 0) AS shop_count
FROM finance_customers AS customer
LEFT JOIN sale_metrics AS sales
    ON sales.customer_id = customer.id
LEFT JOIN return_metrics AS refunds
    ON refunds.customer_id = customer.id;

CREATE VIEW crm_customer_segments AS
SELECT
    customer.organization_id,
    customer.id AS customer_id,
    CASE
        WHEN customer.is_active = 0 OR profile.lifecycle_stage = 'blocked' THEN 'blocked'
        WHEN outstanding.outstanding_minor > 0 THEN 'debtor'
        WHEN metrics.completed_sale_count = 0 THEN 'prospect'
        WHEN metrics.last_sale_at_utc < datetime('now', '-90 days') THEN 'dormant'
        WHEN metrics.completed_sale_count >= 5 THEN 'loyal'
        WHEN metrics.first_sale_at_utc >= datetime('now', '-30 days') THEN 'new'
        ELSE 'active'
    END AS segment
FROM finance_customers AS customer
INNER JOIN crm_customer_profiles AS profile
    ON profile.customer_id = customer.id
INNER JOIN crm_customer_sales_metrics AS metrics
    ON metrics.customer_id = customer.id
INNER JOIN crm_customer_outstanding_balances AS outstanding
    ON outstanding.customer_id = customer.id;
