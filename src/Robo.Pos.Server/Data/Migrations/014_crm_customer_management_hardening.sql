DROP TRIGGER IF EXISTS trg_crm_loyalty_sale_void;

CREATE TRIGGER trg_crm_loyalty_sale_void
AFTER UPDATE OF status ON sales
WHEN OLD.status = 'completed' AND NEW.status = 'voided' AND NEW.customer_id IS NOT NULL
BEGIN
    INSERT OR IGNORE INTO crm_loyalty_ledger
    (
        id, organization_id, customer_id, shop_id, sale_id,
        entry_type, points_delta, balance_after,
        reference_type, reference_id, reason,
        created_by_user_id, created_at_utc
    )
    SELECT
        lower(hex(randomblob(16))),
        customer.organization_id,
        NEW.customer_id,
        NEW.shop_id,
        NEW.id,
        'reversal',
        -earn.points_delta,
        profile.current_points - earn.points_delta,
        'sale_void',
        NEW.id,
        'Automatic reversal for voided sale ' || NEW.receipt_number,
        NEW.voided_by_user_id,
        NEW.voided_at_utc
    FROM crm_loyalty_ledger AS earn
    INNER JOIN crm_customer_profiles AS profile ON profile.customer_id = NEW.customer_id
    INNER JOIN finance_customers AS customer ON customer.id = NEW.customer_id
    WHERE earn.sale_id = NEW.id AND earn.entry_type = 'earn';

    UPDATE crm_customer_profiles
    SET current_points = current_points -
        COALESCE((
            SELECT points_delta
            FROM crm_loyalty_ledger
            WHERE sale_id = NEW.id AND entry_type = 'earn'
        ), 0),
        lifetime_points = MAX(
            lifetime_points - COALESCE((
                SELECT points_delta
                FROM crm_loyalty_ledger
                WHERE sale_id = NEW.id AND entry_type = 'earn'
            ), 0),
            0),
        loyalty_tier = CASE
            WHEN MAX(
                lifetime_points - COALESCE((
                    SELECT points_delta
                    FROM crm_loyalty_ledger
                    WHERE sale_id = NEW.id AND entry_type = 'earn'
                ), 0),
                0
            ) >= COALESCE((
                SELECT platinum_threshold_points
                FROM crm_loyalty_settings
                WHERE organization_id = (
                    SELECT organization_id
                    FROM finance_customers
                    WHERE id = NEW.customer_id
                )
            ), 9223372036854775807)
            THEN 'platinum'
            WHEN MAX(
                lifetime_points - COALESCE((
                    SELECT points_delta
                    FROM crm_loyalty_ledger
                    WHERE sale_id = NEW.id AND entry_type = 'earn'
                ), 0),
                0
            ) >= COALESCE((
                SELECT gold_threshold_points
                FROM crm_loyalty_settings
                WHERE organization_id = (
                    SELECT organization_id
                    FROM finance_customers
                    WHERE id = NEW.customer_id
                )
            ), 9223372036854775807)
            THEN 'gold'
            WHEN MAX(
                lifetime_points - COALESCE((
                    SELECT points_delta
                    FROM crm_loyalty_ledger
                    WHERE sale_id = NEW.id AND entry_type = 'earn'
                ), 0),
                0
            ) >= COALESCE((
                SELECT silver_threshold_points
                FROM crm_loyalty_settings
                WHERE organization_id = (
                    SELECT organization_id
                    FROM finance_customers
                    WHERE id = NEW.customer_id
                )
            ), 9223372036854775807)
            THEN 'silver'
            ELSE 'standard'
        END,
        updated_at_utc = NEW.voided_at_utc,
        version = version + 1
    WHERE customer_id = NEW.customer_id
      AND EXISTS
      (
          SELECT 1
          FROM crm_loyalty_ledger
          WHERE sale_id = NEW.id AND entry_type = 'reversal'
      );
END;
