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
