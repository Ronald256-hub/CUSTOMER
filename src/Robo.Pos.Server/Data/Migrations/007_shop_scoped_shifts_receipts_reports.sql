ALTER TABLE teller_shifts
ADD COLUMN shop_id TEXT NULL REFERENCES shops(id);

UPDATE teller_shifts
SET shop_id = COALESCE(
    (
        SELECT sale.shop_id
        FROM sales AS sale
        WHERE sale.shift_id = teller_shifts.id
          AND sale.shop_id IS NOT NULL
        ORDER BY sale.created_at_utc
        LIMIT 1
    ),
    'main-shop')
WHERE shop_id IS NULL;

DROP INDEX IF EXISTS ux_teller_open_shift;

CREATE UNIQUE INDEX IF NOT EXISTS ux_teller_open_shift_shop
    ON teller_shifts(teller_user_id, shop_id)
    WHERE status = 'open';

CREATE INDEX IF NOT EXISTS ix_teller_shifts_shop_status_time
    ON teller_shifts(shop_id, status, opened_at_utc);

CREATE INDEX IF NOT EXISTS ix_teller_shifts_user_shop_status
    ON teller_shifts(teller_user_id, shop_id, status);

CREATE INDEX IF NOT EXISTS ix_sales_shop_teller_completed
    ON sales(shop_id, teller_user_id, completed_at_utc);

CREATE INDEX IF NOT EXISTS ix_sales_shop_receipt
    ON sales(shop_id, receipt_number);

CREATE TABLE IF NOT EXISTS shop_document_sequences
(
    shop_id         TEXT NOT NULL,
    document_type   TEXT NOT NULL
                    CHECK (document_type IN ('receipt', 'invoice')),
    prefix          TEXT NOT NULL,
    next_value      INTEGER NOT NULL DEFAULT 1
                    CHECK (next_value >= 1),
    updated_at_utc  TEXT NOT NULL,

    PRIMARY KEY (shop_id, document_type),

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT
);

INSERT OR IGNORE INTO shop_document_sequences
(
    shop_id,
    document_type,
    prefix,
    next_value,
    updated_at_utc
)
SELECT
    shop.id,
    document.document_type,
    CASE document.document_type
        WHEN 'receipt' THEN 'RCT-' || UPPER(REPLACE(REPLACE(shop.code, ' ', '-'), '/', '-'))
        ELSE 'INV-' || UPPER(REPLACE(REPLACE(shop.code, ' ', '-'), '/', '-'))
    END,
    1,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM shops AS shop
CROSS JOIN
(
    SELECT 'receipt' AS document_type
    UNION ALL
    SELECT 'invoice'
) AS document;

CREATE TRIGGER IF NOT EXISTS trg_teller_shift_shop_required_insert
BEFORE INSERT ON teller_shifts
WHEN NEW.shop_id IS NULL
     OR NOT EXISTS
        (
            SELECT 1
            FROM shops
            WHERE id = NEW.shop_id
              AND is_active = 1
        )
BEGIN
    SELECT RAISE(ABORT, 'A teller shift requires an active shop.');
END;

CREATE TRIGGER IF NOT EXISTS trg_teller_shift_shop_required_update
BEFORE UPDATE OF shop_id ON teller_shifts
WHEN NEW.shop_id IS NULL
     OR NOT EXISTS
        (
            SELECT 1
            FROM shops
            WHERE id = NEW.shop_id
        )
BEGIN
    SELECT RAISE(ABORT, 'A teller shift requires a valid shop.');
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_shift_shop_match_insert
BEFORE INSERT ON sales
WHEN NEW.shop_id IS NULL
     OR NOT EXISTS
        (
            SELECT 1
            FROM teller_shifts AS shift
            WHERE shift.id = NEW.shift_id
              AND shift.shop_id = NEW.shop_id
        )
BEGIN
    SELECT RAISE(ABORT, 'The sale shop must match the teller shift shop.');
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_shift_shop_match_update
BEFORE UPDATE OF shop_id, shift_id ON sales
WHEN NEW.shop_id IS NULL
     OR NOT EXISTS
        (
            SELECT 1
            FROM teller_shifts AS shift
            WHERE shift.id = NEW.shift_id
              AND shift.shop_id = NEW.shop_id
        )
BEGIN
    SELECT RAISE(ABORT, 'The sale shop must match the teller shift shop.');
END;

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    7,
    'Shop-scoped teller shifts, receipt sequences and sales reporting controls',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
