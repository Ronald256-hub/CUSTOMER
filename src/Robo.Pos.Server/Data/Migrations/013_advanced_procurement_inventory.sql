CREATE TABLE IF NOT EXISTS procurement_document_sequences
(
    shop_id                  TEXT NOT NULL,
    document_type            TEXT NOT NULL
                             CHECK (document_type IN ('purchase_order', 'goods_receipt', 'supplier_return', 'stock_count')),
    prefix                   TEXT NOT NULL,
    next_value               INTEGER NOT NULL DEFAULT 1 CHECK (next_value >= 1),
    updated_at_utc           TEXT NOT NULL,

    PRIMARY KEY (shop_id, document_type),
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS procurement_purchase_orders
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    purchase_order_number    TEXT NOT NULL,
    supplier_id              TEXT NOT NULL,
    status                   TEXT NOT NULL DEFAULT 'draft'
                             CHECK (status IN ('draft', 'submitted', 'approved', 'partially_received', 'received', 'cancelled')),
    order_date               TEXT NOT NULL
                             CHECK (length(order_date) = 10 AND date(order_date) = order_date),
    expected_date            TEXT NULL
                             CHECK (expected_date IS NULL OR (length(expected_date) = 10 AND date(expected_date) = expected_date)),
    currency_code            TEXT NOT NULL,
    subtotal_minor           INTEGER NOT NULL DEFAULT 0 CHECK (subtotal_minor >= 0),
    landed_cost_minor        INTEGER NOT NULL DEFAULT 0 CHECK (landed_cost_minor >= 0),
    total_minor              INTEGER NOT NULL DEFAULT 0 CHECK (total_minor >= 0),
    notes                    TEXT NOT NULL DEFAULT '',
    cancellation_reason      TEXT NULL,
    version                  INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id       TEXT NOT NULL,
    submitted_by_user_id     TEXT NULL,
    approved_by_user_id      TEXT NULL,
    cancelled_by_user_id     TEXT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,
    submitted_at_utc         TEXT NULL,
    approved_at_utc          TEXT NULL,
    completed_at_utc         TEXT NULL,
    cancelled_at_utc         TEXT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (supplier_id) REFERENCES suppliers(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (submitted_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (approved_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (cancelled_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,

    UNIQUE (organization_id, purchase_order_number),
    CHECK (total_minor = subtotal_minor + landed_cost_minor)
);

CREATE INDEX IF NOT EXISTS ix_procurement_orders_shop_status
    ON procurement_purchase_orders(organization_id, shop_id, status, order_date);
CREATE INDEX IF NOT EXISTS ix_procurement_orders_supplier
    ON procurement_purchase_orders(organization_id, supplier_id, order_date);

CREATE TABLE IF NOT EXISTS procurement_purchase_order_lines
(
    id                       TEXT PRIMARY KEY,
    purchase_order_id        TEXT NOT NULL,
    line_number              INTEGER NOT NULL CHECK (line_number >= 1),
    product_id               TEXT NOT NULL,
    product_name_snapshot    TEXT NOT NULL,
    sku_snapshot             TEXT NOT NULL,
    ordered_quantity_base    INTEGER NOT NULL CHECK (ordered_quantity_base > 0),
    received_quantity_base   INTEGER NOT NULL DEFAULT 0 CHECK (received_quantity_base >= 0),
    returned_quantity_base   INTEGER NOT NULL DEFAULT 0 CHECK (returned_quantity_base >= 0),
    unit_cost_minor          INTEGER NOT NULL CHECK (unit_cost_minor >= 0),
    line_total_minor         INTEGER NOT NULL CHECK (line_total_minor >= 0),

    FOREIGN KEY (purchase_order_id) REFERENCES procurement_purchase_orders(id) ON DELETE RESTRICT,
    FOREIGN KEY (product_id) REFERENCES products(id) ON DELETE RESTRICT,
    UNIQUE (purchase_order_id, line_number),
    UNIQUE (purchase_order_id, product_id),
    CHECK (received_quantity_base <= ordered_quantity_base),
    CHECK (returned_quantity_base <= received_quantity_base),
    CHECK (line_total_minor = ordered_quantity_base * unit_cost_minor)
);

CREATE INDEX IF NOT EXISTS ix_procurement_order_lines_product
    ON procurement_purchase_order_lines(product_id, purchase_order_id);

CREATE TABLE IF NOT EXISTS procurement_goods_receipts
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    goods_receipt_number     TEXT NOT NULL,
    purchase_order_id        TEXT NOT NULL,
    purchase_id              TEXT NOT NULL,
    supplier_invoice_number  TEXT NOT NULL DEFAULT '',
    status                   TEXT NOT NULL DEFAULT 'posted'
                             CHECK (status IN ('posted', 'reversed')),
    subtotal_minor           INTEGER NOT NULL CHECK (subtotal_minor > 0),
    landed_cost_minor        INTEGER NOT NULL DEFAULT 0 CHECK (landed_cost_minor >= 0),
    total_minor              INTEGER NOT NULL CHECK (total_minor > 0),
    notes                    TEXT NOT NULL DEFAULT '',
    received_by_user_id      TEXT NOT NULL,
    reversed_by_user_id      TEXT NULL,
    received_at_utc          TEXT NOT NULL,
    reversed_at_utc          TEXT NULL,
    reversal_reason          TEXT NULL,
    version                  INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (purchase_order_id) REFERENCES procurement_purchase_orders(id) ON DELETE RESTRICT,
    FOREIGN KEY (purchase_id) REFERENCES purchases(id) ON DELETE RESTRICT,
    FOREIGN KEY (received_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (reversed_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (organization_id, goods_receipt_number),
    UNIQUE (purchase_id),
    CHECK (total_minor = subtotal_minor + landed_cost_minor)
);

CREATE INDEX IF NOT EXISTS ix_procurement_receipts_order
    ON procurement_goods_receipts(purchase_order_id, received_at_utc);

CREATE TABLE IF NOT EXISTS procurement_goods_receipt_lines
(
    id                       TEXT PRIMARY KEY,
    goods_receipt_id         TEXT NOT NULL,
    purchase_order_line_id   TEXT NOT NULL,
    product_id               TEXT NOT NULL,
    product_name_snapshot    TEXT NOT NULL,
    sku_snapshot             TEXT NOT NULL,
    quantity_base_units      INTEGER NOT NULL CHECK (quantity_base_units > 0),
    unit_cost_minor          INTEGER NOT NULL CHECK (unit_cost_minor >= 0),
    landed_cost_minor        INTEGER NOT NULL DEFAULT 0 CHECK (landed_cost_minor >= 0),
    effective_unit_cost_minor INTEGER NOT NULL CHECK (effective_unit_cost_minor >= 0),
    line_total_minor         INTEGER NOT NULL CHECK (line_total_minor > 0),
    batch_number             TEXT NOT NULL DEFAULT '',
    expiry_date              TEXT NULL
                             CHECK (expiry_date IS NULL OR (length(expiry_date) = 10 AND date(expiry_date) = expiry_date)),
    returned_quantity_base   INTEGER NOT NULL DEFAULT 0 CHECK (returned_quantity_base >= 0),

    FOREIGN KEY (goods_receipt_id) REFERENCES procurement_goods_receipts(id) ON DELETE RESTRICT,
    FOREIGN KEY (purchase_order_line_id) REFERENCES procurement_purchase_order_lines(id) ON DELETE RESTRICT,
    FOREIGN KEY (product_id) REFERENCES products(id) ON DELETE RESTRICT,
    UNIQUE (goods_receipt_id, purchase_order_line_id),
    CHECK (returned_quantity_base <= quantity_base_units),
    CHECK (line_total_minor = quantity_base_units * unit_cost_minor + landed_cost_minor)
);

CREATE TABLE IF NOT EXISTS inventory_batches
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    product_id               TEXT NOT NULL,
    goods_receipt_line_id    TEXT NOT NULL,
    batch_number             TEXT NOT NULL,
    expiry_date              TEXT NULL
                             CHECK (expiry_date IS NULL OR (length(expiry_date) = 10 AND date(expiry_date) = expiry_date)),
    received_quantity_base   INTEGER NOT NULL CHECK (received_quantity_base > 0),
    available_quantity_base  INTEGER NOT NULL CHECK (available_quantity_base >= 0),
    unit_cost_minor          INTEGER NOT NULL CHECK (unit_cost_minor >= 0),
    landed_cost_minor        INTEGER NOT NULL DEFAULT 0 CHECK (landed_cost_minor >= 0),
    status                   TEXT NOT NULL DEFAULT 'active'
                             CHECK (status IN ('active', 'depleted', 'expired', 'quarantined')),
    version                  INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    received_at_utc          TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (product_id) REFERENCES products(id) ON DELETE RESTRICT,
    FOREIGN KEY (goods_receipt_line_id) REFERENCES procurement_goods_receipt_lines(id) ON DELETE RESTRICT,
    UNIQUE (goods_receipt_line_id),
    CHECK (available_quantity_base <= received_quantity_base)
);

CREATE INDEX IF NOT EXISTS ix_inventory_batches_shop_expiry
    ON inventory_batches(organization_id, shop_id, status, expiry_date);
CREATE INDEX IF NOT EXISTS ix_inventory_batches_product
    ON inventory_batches(shop_id, product_id, status, received_at_utc);

CREATE TABLE IF NOT EXISTS procurement_supplier_returns
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    supplier_return_number   TEXT NOT NULL,
    purchase_order_id        TEXT NOT NULL,
    goods_receipt_id         TEXT NOT NULL,
    supplier_id              TEXT NOT NULL,
    status                   TEXT NOT NULL DEFAULT 'posted'
                             CHECK (status IN ('posted', 'reversed')),
    total_minor              INTEGER NOT NULL CHECK (total_minor > 0),
    reason                   TEXT NOT NULL,
    credit_journal_id        TEXT NOT NULL,
    returned_by_user_id      TEXT NOT NULL,
    returned_at_utc          TEXT NOT NULL,
    reversed_by_user_id      TEXT NULL,
    reversed_at_utc          TEXT NULL,
    reversal_reason          TEXT NULL,
    version                  INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (purchase_order_id) REFERENCES procurement_purchase_orders(id) ON DELETE RESTRICT,
    FOREIGN KEY (goods_receipt_id) REFERENCES procurement_goods_receipts(id) ON DELETE RESTRICT,
    FOREIGN KEY (supplier_id) REFERENCES suppliers(id) ON DELETE RESTRICT,
    FOREIGN KEY (credit_journal_id) REFERENCES accounting_journals(id) ON DELETE RESTRICT,
    FOREIGN KEY (returned_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (reversed_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (organization_id, supplier_return_number),
    UNIQUE (credit_journal_id)
);

CREATE INDEX IF NOT EXISTS ix_procurement_returns_supplier
    ON procurement_supplier_returns(organization_id, shop_id, supplier_id, returned_at_utc);

CREATE TABLE IF NOT EXISTS procurement_supplier_return_lines
(
    id                       TEXT PRIMARY KEY,
    supplier_return_id       TEXT NOT NULL,
    goods_receipt_line_id    TEXT NOT NULL,
    product_id               TEXT NOT NULL,
    product_name_snapshot    TEXT NOT NULL,
    sku_snapshot             TEXT NOT NULL,
    quantity_base_units      INTEGER NOT NULL CHECK (quantity_base_units > 0),
    unit_cost_minor          INTEGER NOT NULL CHECK (unit_cost_minor >= 0),
    line_total_minor         INTEGER NOT NULL CHECK (line_total_minor > 0),
    batch_id                 TEXT NULL,

    FOREIGN KEY (supplier_return_id) REFERENCES procurement_supplier_returns(id) ON DELETE RESTRICT,
    FOREIGN KEY (goods_receipt_line_id) REFERENCES procurement_goods_receipt_lines(id) ON DELETE RESTRICT,
    FOREIGN KEY (product_id) REFERENCES products(id) ON DELETE RESTRICT,
    FOREIGN KEY (batch_id) REFERENCES inventory_batches(id) ON DELETE RESTRICT,
    UNIQUE (supplier_return_id, goods_receipt_line_id),
    CHECK (line_total_minor = quantity_base_units * unit_cost_minor)
);

CREATE TABLE IF NOT EXISTS procurement_reorder_policies
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    product_id               TEXT NOT NULL,
    reorder_point_base       INTEGER NOT NULL CHECK (reorder_point_base >= 0),
    target_stock_base        INTEGER NOT NULL CHECK (target_stock_base >= 0),
    lead_time_days           INTEGER NOT NULL DEFAULT 0 CHECK (lead_time_days BETWEEN 0 AND 3650),
    preferred_supplier_id    TEXT NULL,
    is_active                INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    version                  INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    updated_by_user_id       TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (product_id) REFERENCES products(id) ON DELETE RESTRICT,
    FOREIGN KEY (preferred_supplier_id) REFERENCES suppliers(id) ON DELETE RESTRICT,
    FOREIGN KEY (updated_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (shop_id, product_id),
    CHECK (target_stock_base >= reorder_point_base)
);

CREATE TABLE IF NOT EXISTS procurement_stock_counts
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    stock_count_number       TEXT NOT NULL,
    status                   TEXT NOT NULL DEFAULT 'draft'
                             CHECK (status IN ('draft', 'submitted', 'approved', 'cancelled')),
    notes                    TEXT NOT NULL DEFAULT '',
    version                  INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id       TEXT NOT NULL,
    submitted_by_user_id     TEXT NULL,
    approved_by_user_id      TEXT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,
    submitted_at_utc         TEXT NULL,
    approved_at_utc          TEXT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (submitted_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (approved_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (organization_id, stock_count_number)
);

CREATE TABLE IF NOT EXISTS procurement_stock_count_lines
(
    id                       TEXT PRIMARY KEY,
    stock_count_id           TEXT NOT NULL,
    product_id               TEXT NOT NULL,
    product_name_snapshot    TEXT NOT NULL,
    sku_snapshot             TEXT NOT NULL,
    system_quantity_base     INTEGER NOT NULL,
    counted_quantity_base    INTEGER NULL CHECK (counted_quantity_base IS NULL OR counted_quantity_base >= 0),
    unit_cost_minor          INTEGER NOT NULL CHECK (unit_cost_minor >= 0),
    stock_version_snapshot   INTEGER NOT NULL CHECK (stock_version_snapshot >= 1),

    FOREIGN KEY (stock_count_id) REFERENCES procurement_stock_counts(id) ON DELETE RESTRICT,
    FOREIGN KEY (product_id) REFERENCES products(id) ON DELETE RESTRICT,
    UNIQUE (stock_count_id, product_id)
);

CREATE INDEX IF NOT EXISTS ix_procurement_stock_counts_shop
    ON procurement_stock_counts(organization_id, shop_id, status, created_at_utc);

CREATE TRIGGER IF NOT EXISTS trg_procurement_order_scope_insert
BEFORE INSERT ON procurement_purchase_orders
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1 FROM shops
        WHERE id = NEW.shop_id
          AND organization_id = NEW.organization_id
          AND is_active = 1
    ) THEN RAISE(ABORT, 'purchase order requires an active organization shop') END;

    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1 FROM suppliers
        WHERE id = NEW.supplier_id
          AND organization_id = NEW.organization_id
          AND is_active = 1
    ) THEN RAISE(ABORT, 'purchase order requires an active organization supplier') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_order_state_update
BEFORE UPDATE OF status ON procurement_purchase_orders
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.status = NEW.status
        OR (OLD.status = 'draft' AND NEW.status IN ('submitted', 'cancelled'))
        OR (OLD.status = 'submitted' AND NEW.status IN ('approved', 'cancelled'))
        OR (OLD.status = 'approved' AND NEW.status IN ('partially_received', 'received', 'cancelled'))
        OR (OLD.status = 'partially_received' AND NEW.status IN ('received', 'cancelled'))
    ) THEN RAISE(ABORT, 'invalid purchase order state transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_order_delete
BEFORE DELETE ON procurement_purchase_orders
BEGIN
    SELECT RAISE(ABORT, 'purchase orders are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_order_line_delete
BEFORE DELETE ON procurement_purchase_order_lines
WHEN (SELECT status FROM procurement_purchase_orders WHERE id = OLD.purchase_order_id) <> 'draft'
BEGIN
    SELECT RAISE(ABORT, 'submitted purchase order lines are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_grn_immutable_update
BEFORE UPDATE ON procurement_goods_receipts
WHEN OLD.status = 'posted' AND
(
    NEW.organization_id <> OLD.organization_id OR
    NEW.shop_id <> OLD.shop_id OR
    NEW.purchase_order_id <> OLD.purchase_order_id OR
    NEW.purchase_id <> OLD.purchase_id OR
    NEW.subtotal_minor <> OLD.subtotal_minor OR
    NEW.landed_cost_minor <> OLD.landed_cost_minor OR
    NEW.total_minor <> OLD.total_minor OR
    NEW.received_by_user_id <> OLD.received_by_user_id OR
    NEW.received_at_utc <> OLD.received_at_utc
)
BEGIN
    SELECT RAISE(ABORT, 'posted goods receipt financial values are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_grn_delete
BEFORE DELETE ON procurement_goods_receipts
BEGIN
    SELECT RAISE(ABORT, 'goods receipts are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_grn_line_update
BEFORE UPDATE ON procurement_goods_receipt_lines
WHEN NEW.returned_quantity_base = OLD.returned_quantity_base
BEGIN
    SELECT RAISE(ABORT, 'posted goods receipt lines are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_grn_line_delete
BEFORE DELETE ON procurement_goods_receipt_lines
BEGIN
    SELECT RAISE(ABORT, 'goods receipt lines are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_inventory_batch_available_guard
BEFORE UPDATE OF available_quantity_base ON inventory_batches
BEGIN
    SELECT CASE WHEN NEW.available_quantity_base < 0
        THEN RAISE(ABORT, 'batch available quantity cannot be negative') END;
    SELECT CASE WHEN NEW.available_quantity_base > NEW.received_quantity_base
        THEN RAISE(ABORT, 'batch available quantity exceeds received quantity') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_return_delete
BEFORE DELETE ON procurement_supplier_returns
BEGIN
    SELECT RAISE(ABORT, 'supplier returns are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_return_line_update
BEFORE UPDATE ON procurement_supplier_return_lines
BEGIN
    SELECT RAISE(ABORT, 'posted supplier return lines are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_return_line_delete
BEFORE DELETE ON procurement_supplier_return_lines
BEGIN
    SELECT RAISE(ABORT, 'supplier return lines are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_stock_count_state
BEFORE UPDATE OF status ON procurement_stock_counts
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.status = NEW.status
        OR (OLD.status = 'draft' AND NEW.status IN ('submitted', 'cancelled'))
        OR (OLD.status = 'submitted' AND NEW.status IN ('approved', 'cancelled'))
    ) THEN RAISE(ABORT, 'invalid stock count state transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_procurement_stock_count_delete
BEFORE DELETE ON procurement_stock_counts
BEGIN
    SELECT RAISE(ABORT, 'stock counts are permanent audit records');
END;

CREATE VIEW IF NOT EXISTS procurement_reorder_recommendations AS
WITH on_order AS
(
    SELECT
        order_header.shop_id,
        line.product_id,
        SUM(line.ordered_quantity_base - line.received_quantity_base) AS quantity_base
    FROM procurement_purchase_orders AS order_header
    INNER JOIN procurement_purchase_order_lines AS line
        ON line.purchase_order_id = order_header.id
    WHERE order_header.status IN ('submitted', 'approved', 'partially_received')
    GROUP BY order_header.shop_id, line.product_id
)
SELECT
    policy.organization_id,
    policy.shop_id,
    policy.product_id,
    product.name AS product_name,
    product.sku,
    COALESCE(balance.quantity_base_units - balance.reserved_base_units, 0) AS available_base_units,
    COALESCE(on_order.quantity_base, 0) AS on_order_base_units,
    policy.reorder_point_base,
    policy.target_stock_base,
    MAX(
        policy.target_stock_base -
        COALESCE(balance.quantity_base_units - balance.reserved_base_units, 0) -
        COALESCE(on_order.quantity_base, 0),
        0
    ) AS suggested_order_base_units,
    policy.lead_time_days,
    policy.preferred_supplier_id,
    COALESCE(supplier.name, '') AS preferred_supplier_name
FROM procurement_reorder_policies AS policy
INNER JOIN products AS product ON product.id = policy.product_id
LEFT JOIN shop_stock_balances AS balance
    ON balance.shop_id = policy.shop_id
   AND balance.product_id = policy.product_id
LEFT JOIN on_order
    ON on_order.shop_id = policy.shop_id
   AND on_order.product_id = policy.product_id
LEFT JOIN suppliers AS supplier ON supplier.id = policy.preferred_supplier_id
WHERE policy.is_active = 1
  AND COALESCE(balance.quantity_base_units - balance.reserved_base_units, 0) <= policy.reorder_point_base;

CREATE VIEW IF NOT EXISTS procurement_expiry_alerts AS
SELECT
    batch.organization_id,
    batch.shop_id,
    batch.id AS batch_id,
    batch.product_id,
    product.name AS product_name,
    product.sku,
    batch.batch_number,
    batch.expiry_date,
    batch.available_quantity_base,
    CAST(julianday(batch.expiry_date) - julianday(date('now')) AS INTEGER) AS days_to_expiry,
    batch.status
FROM inventory_batches AS batch
INNER JOIN products AS product ON product.id = batch.product_id
WHERE batch.available_quantity_base > 0
  AND batch.expiry_date IS NOT NULL;

INSERT OR IGNORE INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    13,
    'Advanced procurement, GRN, supplier returns, batches, counts and reorder planning',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
