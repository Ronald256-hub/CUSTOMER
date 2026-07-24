CREATE TABLE IF NOT EXISTS business_settings
(
    id                              INTEGER PRIMARY KEY
                                    CHECK (id = 1),
    business_name                   TEXT NOT NULL,
    address                         TEXT NOT NULL DEFAULT '',
    phone                           TEXT NOT NULL DEFAULT '',
    email                           TEXT NOT NULL DEFAULT '',
    currency_code                   TEXT NOT NULL DEFAULT 'UGX',
    receipt_footer                  TEXT NOT NULL DEFAULT
                                    'Thank you for your business.',
    document_root                   TEXT NULL,
    receipt_verification_enabled    INTEGER NOT NULL DEFAULT 0
                                    CHECK (
                                        receipt_verification_enabled
                                        IN (0, 1)
                                    ),
    updated_by_user_id              TEXT NULL,
    updated_at_utc                  TEXT NOT NULL,

    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

INSERT OR IGNORE INTO business_settings
(
    id,
    business_name,
    address,
    currency_code,
    receipt_verification_enabled,
    updated_at_utc
)
VALUES
(
    1,
    'ROBO CASK & TAP',
    'Namugongo Road near TEXOL Fuel, Kampala, Uganda',
    'UGX',
    0,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);

CREATE TABLE IF NOT EXISTS categories
(
    id                 TEXT PRIMARY KEY,
    name               TEXT NOT NULL,
    name_normalized    TEXT NOT NULL COLLATE NOCASE UNIQUE,
    description        TEXT NOT NULL DEFAULT '',
    display_order      INTEGER NOT NULL DEFAULT 0,
    is_active          INTEGER NOT NULL DEFAULT 1
                       CHECK (is_active IN (0, 1)),
    created_by_user_id TEXT NOT NULL,
    updated_by_user_id TEXT NOT NULL,
    created_at_utc     TEXT NOT NULL,
    updated_at_utc     TEXT NOT NULL,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS products
(
    id                         TEXT PRIMARY KEY,
    category_id                TEXT NULL,
    sku                        TEXT NOT NULL COLLATE NOCASE UNIQUE,
    barcode                    TEXT NULL COLLATE NOCASE UNIQUE,
    name                       TEXT NOT NULL,
    description                TEXT NOT NULL DEFAULT '',

    product_type               TEXT NOT NULL DEFAULT 'standard'
                               CHECK (
                                   product_type IN
                                   (
                                       'standard',
                                       'bottle',
                                       'crate',
                                       'short_glass'
                                   )
                               ),

    stock_unit                 TEXT NOT NULL DEFAULT 'unit'
                               CHECK (
                                   stock_unit IN
                                   (
                                       'unit',
                                       'bottle',
                                       'crate',
                                       'ml'
                                   )
                               ),

    sale_unit                  TEXT NOT NULL DEFAULT 'unit'
                               CHECK (
                                   sale_unit IN
                                   (
                                       'unit',
                                       'bottle',
                                       'crate',
                                       'glass'
                                   )
                               ),

    bottle_volume_ml           INTEGER NULL
                               CHECK (
                                   bottle_volume_ml IS NULL OR
                                   bottle_volume_ml > 0
                               ),

    glass_size_ml              INTEGER NULL
                               CHECK (
                                   glass_size_ml IS NULL OR
                                   glass_size_ml > 0
                               ),

    units_per_crate            INTEGER NULL
                               CHECK (
                                   units_per_crate IS NULL OR
                                   units_per_crate > 0
                               ),

    cost_price_minor           INTEGER NOT NULL DEFAULT 0
                               CHECK (cost_price_minor >= 0),

    selling_price_minor        INTEGER NOT NULL DEFAULT 0
                               CHECK (selling_price_minor >= 0),

    low_stock_threshold        INTEGER NOT NULL DEFAULT 0
                               CHECK (low_stock_threshold >= 0),

    allow_negative_stock       INTEGER NOT NULL DEFAULT 0
                               CHECK (
                                   allow_negative_stock IN (0, 1)
                               ),

    track_expiry               INTEGER NOT NULL DEFAULT 0
                               CHECK (track_expiry IN (0, 1)),

    is_active                  INTEGER NOT NULL DEFAULT 1
                               CHECK (is_active IN (0, 1)),

    version                    INTEGER NOT NULL DEFAULT 1
                               CHECK (version >= 1),

    created_by_user_id         TEXT NOT NULL,
    updated_by_user_id         TEXT NOT NULL,
    created_at_utc             TEXT NOT NULL,
    updated_at_utc             TEXT NOT NULL,

    FOREIGN KEY (category_id)
        REFERENCES categories(id)
        ON DELETE SET NULL,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    CHECK
    (
        product_type != 'short_glass'
        OR
        (
            stock_unit = 'ml'
            AND sale_unit = 'glass'
            AND bottle_volume_ml IS NOT NULL
            AND glass_size_ml IS NOT NULL
        )
    )
);

CREATE INDEX IF NOT EXISTS ix_products_name
    ON products(name COLLATE NOCASE);

CREATE INDEX IF NOT EXISTS ix_products_category
    ON products(category_id, is_active);

CREATE INDEX IF NOT EXISTS ix_products_barcode
    ON products(barcode);

CREATE TABLE IF NOT EXISTS product_price_history
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    product_id               TEXT NOT NULL,
    previous_cost_minor      INTEGER NOT NULL,
    new_cost_minor           INTEGER NOT NULL,
    previous_selling_minor   INTEGER NOT NULL,
    new_selling_minor        INTEGER NOT NULL,
    reason                   TEXT NOT NULL,
    changed_by_user_id       TEXT NOT NULL,
    changed_at_utc           TEXT NOT NULL,

    FOREIGN KEY (product_id)
        REFERENCES products(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (changed_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_price_history_product
    ON product_price_history(product_id, changed_at_utc);

CREATE TABLE IF NOT EXISTS stock_balances
(
    product_id               TEXT PRIMARY KEY,
    quantity_base_units      INTEGER NOT NULL DEFAULT 0,
    reserved_base_units      INTEGER NOT NULL DEFAULT 0
                              CHECK (reserved_base_units >= 0),
    version                  INTEGER NOT NULL DEFAULT 1,
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (product_id)
        REFERENCES products(id)
        ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS stock_movements
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    product_id               TEXT NOT NULL,

    movement_type            TEXT NOT NULL
                             CHECK (
                                 movement_type IN
                                 (
                                     'opening',
                                     'purchase',
                                     'sale',
                                     'sale_void',
                                     'sale_return',
                                     'adjustment',
                                     'stocktake',
                                     'damage',
                                     'expiry',
                                     'spillage',
                                     'transfer_in',
                                     'transfer_out'
                                 )
                             ),

    quantity_delta_base      INTEGER NOT NULL,
    balance_after_base       INTEGER NOT NULL,
    cost_value_minor         INTEGER NOT NULL DEFAULT 0,

    reference_type           TEXT NULL,
    reference_id             TEXT NULL,
    reason                   TEXT NOT NULL DEFAULT '',

    performed_by_user_id     TEXT NOT NULL,
    approved_by_user_id      TEXT NULL,
    occurred_at_utc          TEXT NOT NULL,

    FOREIGN KEY (product_id)
        REFERENCES products(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (performed_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (approved_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_stock_movements_product
    ON stock_movements(product_id, occurred_at_utc);

CREATE INDEX IF NOT EXISTS ix_stock_movements_reference
    ON stock_movements(reference_type, reference_id);

CREATE TABLE IF NOT EXISTS suppliers
(
    id                       TEXT PRIMARY KEY,
    name                     TEXT NOT NULL,
    phone                    TEXT NOT NULL DEFAULT '',
    email                    TEXT NOT NULL DEFAULT '',
    address                  TEXT NOT NULL DEFAULT '',
    notes                    TEXT NOT NULL DEFAULT '',
    is_active                INTEGER NOT NULL DEFAULT 1
                             CHECK (is_active IN (0, 1)),
    created_by_user_id       TEXT NOT NULL,
    updated_by_user_id       TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS purchases
(
    id                       TEXT PRIMARY KEY,
    purchase_number          TEXT NOT NULL UNIQUE,
    supplier_id              TEXT NULL,
    supplier_invoice_number  TEXT NOT NULL DEFAULT '',
    status                   TEXT NOT NULL DEFAULT 'received'
                             CHECK (
                                 status IN
                                 (
                                     'draft',
                                     'received',
                                     'cancelled'
                                 )
                             ),
    subtotal_minor           INTEGER NOT NULL DEFAULT 0,
    total_minor              INTEGER NOT NULL DEFAULT 0,
    notes                    TEXT NOT NULL DEFAULT '',
    received_by_user_id      TEXT NOT NULL,
    received_at_utc          TEXT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (supplier_id)
        REFERENCES suppliers(id)
        ON DELETE SET NULL,

    FOREIGN KEY (received_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS purchase_items
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    purchase_id              TEXT NOT NULL,
    product_id               TEXT NOT NULL,
    product_name_snapshot    TEXT NOT NULL,
    sku_snapshot             TEXT NOT NULL,
    quantity_base_units      INTEGER NOT NULL
                             CHECK (quantity_base_units > 0),
    unit_cost_minor          INTEGER NOT NULL
                             CHECK (unit_cost_minor >= 0),
    line_total_minor         INTEGER NOT NULL
                             CHECK (line_total_minor >= 0),
    batch_number             TEXT NOT NULL DEFAULT '',
    expiry_date              TEXT NULL,

    FOREIGN KEY (purchase_id)
        REFERENCES purchases(id)
        ON DELETE CASCADE,

    FOREIGN KEY (product_id)
        REFERENCES products(id)
        ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_purchase_items_purchase
    ON purchase_items(purchase_id);

CREATE TABLE IF NOT EXISTS expenses
(
    id                       TEXT PRIMARY KEY,
    expense_number           TEXT NOT NULL UNIQUE,
    category                 TEXT NOT NULL,
    description              TEXT NOT NULL,
    amount_minor             INTEGER NOT NULL
                             CHECK (amount_minor > 0),
    payment_method           TEXT NOT NULL
                             CHECK (
                                 payment_method IN
                                 (
                                     'cash',
                                     'mobile_money',
                                     'bank',
                                     'other'
                                 )
                             ),
    expense_date             TEXT NOT NULL,
    recorded_by_user_id      TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,
    voided_at_utc            TEXT NULL,
    voided_by_user_id        TEXT NULL,
    void_reason              TEXT NULL,

    FOREIGN KEY (recorded_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (voided_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS teller_shifts
(
    id                       TEXT PRIMARY KEY,
    teller_user_id           TEXT NOT NULL,
    status                   TEXT NOT NULL DEFAULT 'open'
                             CHECK (
                                 status IN ('open', 'closed')
                             ),
    opening_cash_minor       INTEGER NOT NULL DEFAULT 0,
    expected_cash_minor      INTEGER NULL,
    counted_cash_minor       INTEGER NULL,
    cash_variance_minor      INTEGER NULL,
    opened_at_utc            TEXT NOT NULL,
    closed_at_utc            TEXT NULL,
    closed_by_user_id        TEXT NULL,
    notes                    TEXT NOT NULL DEFAULT '',

    FOREIGN KEY (teller_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (closed_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_teller_open_shift
    ON teller_shifts(teller_user_id)
    WHERE status = 'open';

CREATE TABLE IF NOT EXISTS sales
(
    id                       TEXT PRIMARY KEY,
    receipt_number           TEXT NOT NULL UNIQUE,
    invoice_number           TEXT NULL UNIQUE,
    shift_id                 TEXT NOT NULL,
    teller_user_id           TEXT NOT NULL,

    customer_name            TEXT NOT NULL DEFAULT '',
    customer_phone           TEXT NOT NULL DEFAULT '',
    customer_address         TEXT NOT NULL DEFAULT '',
    customer_tax_number      TEXT NOT NULL DEFAULT '',

    status                   TEXT NOT NULL DEFAULT 'completed'
                             CHECK (
                                 status IN
                                 (
                                     'suspended',
                                     'completed',
                                     'voided',
                                     'partially_returned',
                                     'returned'
                                 )
                             ),

    subtotal_minor           INTEGER NOT NULL
                             CHECK (subtotal_minor >= 0),
    discount_minor           INTEGER NOT NULL DEFAULT 0
                             CHECK (discount_minor >= 0),
    total_minor              INTEGER NOT NULL
                             CHECK (total_minor >= 0),
    amount_received_minor    INTEGER NOT NULL DEFAULT 0
                             CHECK (amount_received_minor >= 0),
    change_minor             INTEGER NOT NULL DEFAULT 0
                             CHECK (change_minor >= 0),

    notes                    TEXT NOT NULL DEFAULT '',
    created_at_utc           TEXT NOT NULL,
    completed_at_utc         TEXT NULL,

    voided_at_utc            TEXT NULL,
    voided_by_user_id        TEXT NULL,
    void_reason              TEXT NULL,

    FOREIGN KEY (shift_id)
        REFERENCES teller_shifts(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (teller_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (voided_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_sales_teller_date
    ON sales(teller_user_id, created_at_utc);

CREATE INDEX IF NOT EXISTS ix_sales_status_date
    ON sales(status, created_at_utc);

CREATE TABLE IF NOT EXISTS sale_items
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    sale_id                  TEXT NOT NULL,
    product_id               TEXT NOT NULL,

    product_name_snapshot    TEXT NOT NULL,
    sku_snapshot             TEXT NOT NULL,
    barcode_snapshot         TEXT NULL,

    quantity                 INTEGER NOT NULL
                             CHECK (quantity > 0),

    sale_unit_snapshot       TEXT NOT NULL,
    unit_size_ml_snapshot    INTEGER NULL,
    base_units_deducted      INTEGER NOT NULL
                             CHECK (base_units_deducted > 0),

    unit_cost_minor          INTEGER NOT NULL
                             CHECK (unit_cost_minor >= 0),

    unit_price_minor         INTEGER NOT NULL
                             CHECK (unit_price_minor >= 0),

    discount_minor           INTEGER NOT NULL DEFAULT 0
                             CHECK (discount_minor >= 0),

    line_total_minor         INTEGER NOT NULL
                             CHECK (line_total_minor >= 0),

    returned_quantity        INTEGER NOT NULL DEFAULT 0
                             CHECK (returned_quantity >= 0),

    FOREIGN KEY (sale_id)
        REFERENCES sales(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (product_id)
        REFERENCES products(id)
        ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_sale_items_sale
    ON sale_items(sale_id);

CREATE INDEX IF NOT EXISTS ix_sale_items_product
    ON sale_items(product_id);

CREATE TABLE IF NOT EXISTS sale_payments
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    sale_id                  TEXT NOT NULL,

    payment_method           TEXT NOT NULL
                             CHECK (
                                 payment_method IN
                                 (
                                     'cash',
                                     'mobile_money',
                                     'card',
                                     'bank',
                                     'credit'
                                 )
                             ),

    amount_minor             INTEGER NOT NULL
                             CHECK (amount_minor > 0),

    reference                TEXT NOT NULL DEFAULT '',
    received_at_utc          TEXT NOT NULL,

    FOREIGN KEY (sale_id)
        REFERENCES sales(id)
        ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_sale_payments_sale
    ON sale_payments(sale_id);

CREATE TABLE IF NOT EXISTS document_sequences
(
    document_type            TEXT PRIMARY KEY
                             CHECK (
                                 document_type IN
                                 (
                                     'receipt',
                                     'invoice',
                                     'purchase',
                                     'expense'
                                 )
                             ),
    prefix                   TEXT NOT NULL,
    next_value               INTEGER NOT NULL DEFAULT 1
                             CHECK (next_value >= 1),
    updated_at_utc           TEXT NOT NULL
);

INSERT OR IGNORE INTO document_sequences
(
    document_type,
    prefix,
    next_value,
    updated_at_utc
)
VALUES
    (
        'receipt',
        'RCT',
        1,
        strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    ),
    (
        'invoice',
        'INV',
        1,
        strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    ),
    (
        'purchase',
        'PUR',
        1,
        strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    ),
    (
        'expense',
        'EXP',
        1,
        strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    );

CREATE TABLE IF NOT EXISTS sale_documents
(
    id                       TEXT PRIMARY KEY,
    sale_id                  TEXT NOT NULL,

    document_type            TEXT NOT NULL
                             CHECK (
                                 document_type IN
                                 (
                                     'receipt',
                                     'invoice'
                                 )
                             ),

    document_number          TEXT NOT NULL,
    file_format              TEXT NOT NULL
                             CHECK (
                                 file_format IN
                                 (
                                     'html',
                                     'json',
                                     'pdf'
                                 )
                             ),

    relative_path            TEXT NOT NULL,
    file_sha256              TEXT NOT NULL,
    file_size_bytes          INTEGER NOT NULL
                             CHECK (file_size_bytes >= 0),

    is_reprint               INTEGER NOT NULL DEFAULT 0
                             CHECK (is_reprint IN (0, 1)),

    version                  INTEGER NOT NULL DEFAULT 1,
    generated_by_user_id     TEXT NOT NULL,
    generated_at_utc         TEXT NOT NULL,

    FOREIGN KEY (sale_id)
        REFERENCES sales(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (generated_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    UNIQUE
    (
        sale_id,
        document_type,
        file_format,
        version
    )
);

CREATE INDEX IF NOT EXISTS ix_sale_documents_number
    ON sale_documents(document_number);

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    3,
    'Products, pricing, stock, purchases, sales, payments and audit documents',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
