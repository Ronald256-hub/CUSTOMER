ALTER TABLE sales
ADD COLUMN shop_id TEXT NULL REFERENCES shops(id);

UPDATE sales
SET shop_id = 'main-shop'
WHERE shop_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_sales_shop_status_completed
    ON sales(shop_id, status, completed_at_utc);

ALTER TABLE stock_movements
ADD COLUMN shop_id TEXT NULL REFERENCES shops(id);

UPDATE stock_movements
SET shop_id = 'main-shop'
WHERE shop_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_stock_movements_shop_product_time
    ON stock_movements(shop_id, product_id, occurred_at_utc);

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
    shop.id,
    product.id,
    CASE
        WHEN shop.id = 'main-shop'
            THEN COALESCE(balance.quantity_base_units, 0)
        ELSE 0
    END,
    CASE
        WHEN shop.id = 'main-shop'
            THEN COALESCE(balance.reserved_base_units, 0)
        ELSE 0
    END,
    CASE
        WHEN shop.id = 'main-shop'
            THEN COALESCE(balance.version, 1)
        ELSE 1
    END,
    COALESCE(
        balance.updated_at_utc,
        strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
FROM shops AS shop
CROSS JOIN products AS product
LEFT JOIN stock_balances AS balance
    ON balance.product_id = product.id;

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    6,
    'Shop-scoped stock balances, movements and sale ownership',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
