CREATE TABLE IF NOT EXISTS organizations
(
    id                    TEXT PRIMARY KEY,
    name                  TEXT NOT NULL,
    legal_name            TEXT NOT NULL DEFAULT '',
    default_currency_code TEXT NOT NULL DEFAULT 'UGX',
    timezone_id           TEXT NOT NULL DEFAULT 'Africa/Kampala',
    created_at_utc        TEXT NOT NULL,
    updated_at_utc        TEXT NOT NULL
);

INSERT OR IGNORE INTO organizations
(
    id,
    name,
    legal_name,
    default_currency_code,
    timezone_id,
    created_at_utc,
    updated_at_utc
)
SELECT
    'default-organization',
    business_name,
    business_name,
    currency_code,
    'Africa/Kampala',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM business_settings
WHERE id = 1;

CREATE TABLE IF NOT EXISTS shops
(
    id                    TEXT PRIMARY KEY,
    organization_id       TEXT NOT NULL,
    code                  TEXT NOT NULL COLLATE NOCASE,
    name                  TEXT NOT NULL,
    address               TEXT NOT NULL DEFAULT '',
    phone                 TEXT NOT NULL DEFAULT '',
    email                 TEXT NOT NULL DEFAULT '',
    tax_number            TEXT NOT NULL DEFAULT '',
    currency_code         TEXT NOT NULL DEFAULT 'UGX',
    timezone_id           TEXT NOT NULL DEFAULT 'Africa/Kampala',
    is_head_office        INTEGER NOT NULL DEFAULT 0
                          CHECK (is_head_office IN (0, 1)),
    is_active             INTEGER NOT NULL DEFAULT 1
                          CHECK (is_active IN (0, 1)),
    version               INTEGER NOT NULL DEFAULT 1
                          CHECK (version >= 1),
    created_by_user_id    TEXT NULL,
    updated_by_user_id    TEXT NULL,
    created_at_utc        TEXT NOT NULL,
    updated_at_utc        TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL,

    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL,

    UNIQUE (organization_id, code)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_shops_head_office
    ON shops(organization_id)
    WHERE is_head_office = 1 AND is_active = 1;

CREATE INDEX IF NOT EXISTS ix_shops_active_name
    ON shops(is_active, name COLLATE NOCASE);

INSERT OR IGNORE INTO shops
(
    id,
    organization_id,
    code,
    name,
    address,
    phone,
    email,
    tax_number,
    currency_code,
    timezone_id,
    is_head_office,
    is_active,
    version,
    created_at_utc,
    updated_at_utc
)
SELECT
    'main-shop',
    'default-organization',
    'MAIN',
    business_name,
    address,
    phone,
    email,
    '',
    currency_code,
    'Africa/Kampala',
    1,
    1,
    1,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM business_settings
WHERE id = 1;

CREATE TABLE IF NOT EXISTS user_shop_access
(
    user_id             TEXT NOT NULL,
    shop_id             TEXT NOT NULL,
    access_level        TEXT NOT NULL
                        CHECK (
                            access_level IN
                            (
                                'manager',
                                'supervisor',
                                'teller',
                                'viewer'
                            )
                        ),
    is_primary          INTEGER NOT NULL DEFAULT 0
                        CHECK (is_primary IN (0, 1)),
    is_active           INTEGER NOT NULL DEFAULT 1
                        CHECK (is_active IN (0, 1)),
    assigned_by_user_id TEXT NULL,
    assigned_at_utc     TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,

    PRIMARY KEY (user_id, shop_id),

    FOREIGN KEY (user_id)
        REFERENCES users(id)
        ON DELETE CASCADE,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE CASCADE,

    FOREIGN KEY (assigned_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_user_primary_shop
    ON user_shop_access(user_id)
    WHERE is_primary = 1 AND is_active = 1;

CREATE INDEX IF NOT EXISTS ix_user_shop_access_shop
    ON user_shop_access(shop_id, is_active, access_level);

INSERT OR IGNORE INTO user_shop_access
(
    user_id,
    shop_id,
    access_level,
    is_primary,
    is_active,
    assigned_by_user_id,
    assigned_at_utc,
    updated_at_utc
)
SELECT
    id,
    'main-shop',
    CASE WHEN role = 'admin' THEN 'manager' ELSE 'teller' END,
    1,
    is_active,
    NULL,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM users;

CREATE TABLE IF NOT EXISTS shop_stock_balances
(
    shop_id                 TEXT NOT NULL,
    product_id              TEXT NOT NULL,
    quantity_base_units     INTEGER NOT NULL DEFAULT 0,
    reserved_base_units     INTEGER NOT NULL DEFAULT 0
                            CHECK (reserved_base_units >= 0),
    version                 INTEGER NOT NULL DEFAULT 1
                            CHECK (version >= 1),
    updated_at_utc          TEXT NOT NULL,

    PRIMARY KEY (shop_id, product_id),

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (product_id)
        REFERENCES products(id)
        ON DELETE RESTRICT
);

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
    'main-shop',
    product_id,
    quantity_base_units,
    reserved_base_units,
    version,
    updated_at_utc
FROM stock_balances;

CREATE INDEX IF NOT EXISTS ix_shop_stock_product
    ON shop_stock_balances(product_id, shop_id);

CREATE TABLE IF NOT EXISTS stock_transfers
(
    id                       TEXT PRIMARY KEY,
    transfer_number          TEXT NOT NULL UNIQUE,
    source_shop_id           TEXT NOT NULL,
    destination_shop_id      TEXT NOT NULL,
    status                   TEXT NOT NULL DEFAULT 'draft'
                             CHECK (
                                 status IN
                                 (
                                     'draft',
                                     'submitted',
                                     'approved',
                                     'in_transit',
                                     'received',
                                     'cancelled'
                                 )
                             ),
    notes                    TEXT NOT NULL DEFAULT '',
    created_by_user_id       TEXT NOT NULL,
    approved_by_user_id      TEXT NULL,
    dispatched_by_user_id    TEXT NULL,
    received_by_user_id      TEXT NULL,
    created_at_utc           TEXT NOT NULL,
    approved_at_utc          TEXT NULL,
    dispatched_at_utc        TEXT NULL,
    received_at_utc          TEXT NULL,
    cancelled_at_utc         TEXT NULL,
    cancellation_reason      TEXT NULL,
    version                  INTEGER NOT NULL DEFAULT 1
                             CHECK (version >= 1),

    FOREIGN KEY (source_shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (destination_shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (approved_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (dispatched_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (received_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    CHECK (source_shop_id != destination_shop_id)
);

CREATE INDEX IF NOT EXISTS ix_stock_transfers_source_status
    ON stock_transfers(source_shop_id, status, created_at_utc);

CREATE INDEX IF NOT EXISTS ix_stock_transfers_destination_status
    ON stock_transfers(destination_shop_id, status, created_at_utc);

CREATE TABLE IF NOT EXISTS stock_transfer_items
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    transfer_id              TEXT NOT NULL,
    product_id               TEXT NOT NULL,
    quantity_base_units      INTEGER NOT NULL
                             CHECK (quantity_base_units > 0),
    source_balance_before    INTEGER NULL,
    source_balance_after     INTEGER NULL,
    destination_before       INTEGER NULL,
    destination_after        INTEGER NULL,

    FOREIGN KEY (transfer_id)
        REFERENCES stock_transfers(id)
        ON DELETE CASCADE,

    FOREIGN KEY (product_id)
        REFERENCES products(id)
        ON DELETE RESTRICT,

    UNIQUE (transfer_id, product_id)
);

CREATE INDEX IF NOT EXISTS ix_stock_transfer_items_transfer
    ON stock_transfer_items(transfer_id);

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    4,
    'Organizations, shops, shop access, per-shop inventory and stock transfer foundation',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);