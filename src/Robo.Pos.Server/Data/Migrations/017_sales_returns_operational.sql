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
