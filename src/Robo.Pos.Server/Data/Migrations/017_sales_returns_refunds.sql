CREATE TABLE IF NOT EXISTS sales_return_sequences
(
    shop_id                  TEXT PRIMARY KEY,
    next_value               INTEGER NOT NULL DEFAULT 1
                             CHECK (next_value >= 1),
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE CASCADE
);

INSERT OR IGNORE INTO sales_return_sequences
(
    shop_id,
    next_value,
    updated_at_utc
)
SELECT
    id,
    1,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM shops;

CREATE TABLE IF NOT EXISTS sales_returns
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    sale_id                  TEXT NOT NULL,
    shift_id                 TEXT NOT NULL,
    return_number            TEXT NOT NULL COLLATE NOCASE,
    original_receipt_number  TEXT NOT NULL,
    status                   TEXT NOT NULL DEFAULT 'draft'
                             CHECK (status IN ('draft', 'completed')),
    refund_method            TEXT NOT NULL
                             CHECK (refund_method IN ('cash', 'mobile_money', 'card', 'bank')),
    refund_amount_minor      INTEGER NOT NULL
                             CHECK (refund_amount_minor > 0),
    returned_base_units      INTEGER NOT NULL DEFAULT 0
                             CHECK (returned_base_units >= 0),
    restocked_base_units     INTEGER NOT NULL DEFAULT 0
                             CHECK (restocked_base_units >= 0),
    returned_cost_minor      INTEGER NOT NULL DEFAULT 0
                             CHECK (returned_cost_minor >= 0),
    restocked_cost_minor     INTEGER NOT NULL DEFAULT 0
                             CHECK (restocked_cost_minor >= 0),
    reason                   TEXT NOT NULL,
    notes                    TEXT NOT NULL DEFAULT '',
    created_by_user_id       TEXT NOT NULL,
    approved_by_user_id      TEXT NOT NULL,
    completed_at_utc         TEXT NOT NULL,
    version                  INTEGER NOT NULL DEFAULT 1
                             CHECK (version >= 1),

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (sale_id)
        REFERENCES sales(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shift_id)
        REFERENCES teller_shifts(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (approved_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    UNIQUE (organization_id, return_number)
);

CREATE INDEX IF NOT EXISTS ix_sales_returns_sale_time
    ON sales_returns(sale_id, completed_at_utc, status);

CREATE INDEX IF NOT EXISTS ix_sales_returns_shop_time
    ON sales_returns(organization_id, shop_id, completed_at_utc, status);

CREATE INDEX IF NOT EXISTS ix_sales_returns_shift_method
    ON sales_returns(shift_id, refund_method, status);

CREATE TABLE IF NOT EXISTS sales_return_items
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    return_id                TEXT NOT NULL,
    sale_item_id             INTEGER NOT NULL,
    product_id               TEXT NOT NULL,
    product_name_snapshot    TEXT NOT NULL,
    sku_snapshot             TEXT NOT NULL,
    quantity                 INTEGER NOT NULL
                             CHECK (quantity > 0),
    sale_unit_snapshot       TEXT NOT NULL,
    unit_size_ml_snapshot    INTEGER NULL,
    unit_price_minor         INTEGER NOT NULL
                             CHECK (unit_price_minor >= 0),
    unit_cost_minor          INTEGER NOT NULL
                             CHECK (unit_cost_minor >= 0),
    refund_minor             INTEGER NOT NULL
                             CHECK (refund_minor > 0),
    base_units_returned      INTEGER NOT NULL
                             CHECK (base_units_returned > 0),
    cost_value_minor         INTEGER NOT NULL
                             CHECK (cost_value_minor >= 0),
    disposition              TEXT NOT NULL
                             CHECK (disposition IN ('restock', 'damaged')),
    base_units_restocked     INTEGER NOT NULL DEFAULT 0
                             CHECK (base_units_restocked >= 0),
    restocked_cost_minor     INTEGER NOT NULL DEFAULT 0
                             CHECK (restocked_cost_minor >= 0),

    FOREIGN KEY (return_id)
        REFERENCES sales_returns(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (sale_item_id)
        REFERENCES sale_items(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (product_id)
        REFERENCES products(id)
        ON DELETE RESTRICT,

    UNIQUE (return_id, sale_item_id)
);

CREATE INDEX IF NOT EXISTS ix_sales_return_items_sale_item
    ON sales_return_items(sale_item_id, return_id);

CREATE INDEX IF NOT EXISTS ix_sales_return_items_product
    ON sales_return_items(product_id, return_id);

CREATE TABLE IF NOT EXISTS sales_return_accounting_links
(
    return_id                TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    posting_journal_id       TEXT NOT NULL UNIQUE,
    posted_at_utc            TEXT NOT NULL,

    FOREIGN KEY (return_id)
        REFERENCES sales_returns(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (posting_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS sales_return_documents
(
    id                       TEXT PRIMARY KEY,
    return_id                TEXT NOT NULL,
    document_type            TEXT NOT NULL DEFAULT 'credit_note'
                             CHECK (document_type = 'credit_note'),
    document_number          TEXT NOT NULL,
    file_format              TEXT NOT NULL
                             CHECK (file_format IN ('json', 'html')),
    relative_path            TEXT NOT NULL,
    file_sha256              TEXT NOT NULL,
    file_size_bytes          INTEGER NOT NULL
                             CHECK (file_size_bytes >= 0),
    generated_by_user_id     TEXT NOT NULL,
    generated_at_utc         TEXT NOT NULL,

    FOREIGN KEY (return_id)
        REFERENCES sales_returns(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (generated_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    UNIQUE (return_id, file_format)
);

CREATE INDEX IF NOT EXISTS ix_sales_return_documents_number
    ON sales_return_documents(document_number);

CREATE TRIGGER IF NOT EXISTS trg_sales_return_scope_insert
BEFORE INSERT ON sales_returns
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM shops AS shop
            WHERE shop.id = NEW.shop_id
              AND shop.organization_id = NEW.organization_id
              AND shop.is_active = 1
        )
        THEN RAISE(ABORT, 'sales return requires an active shop in the organization')
    END;

    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM sales AS sale
            WHERE sale.id = NEW.sale_id
              AND sale.shop_id = NEW.shop_id
              AND sale.status IN ('completed', 'partially_returned')
        )
        THEN RAISE(ABORT, 'sales return requires a returnable sale in the same shop')
    END;

    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM teller_shifts AS shift
            WHERE shift.id = NEW.shift_id
              AND shift.shop_id = NEW.shop_id
              AND shift.status = 'open'
              AND shift.teller_user_id = NEW.created_by_user_id
        )
        THEN RAISE(ABORT, 'sales return requires the operator open shift')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_item_insert_guard
BEFORE INSERT ON sales_return_items
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM sales_returns AS header
            INNER JOIN sale_items AS item
                ON item.id = NEW.sale_item_id
               AND item.sale_id = header.sale_id
            WHERE header.id = NEW.return_id
              AND header.status = 'draft'
              AND item.product_id = NEW.product_id
        )
        THEN RAISE(ABORT, 'return item does not belong to the return sale')
    END;

    SELECT CASE
        WHEN
        (
            SELECT COALESCE(SUM(existing.quantity), 0)
            FROM sales_return_items AS existing
            INNER JOIN sales_returns AS existing_header
                ON existing_header.id = existing.return_id
            WHERE existing.sale_item_id = NEW.sale_item_id
              AND existing_header.status IN ('draft', 'completed')
        ) + NEW.quantity >
        (
            SELECT quantity
            FROM sale_items
            WHERE id = NEW.sale_item_id
        )
        THEN RAISE(ABORT, 'return quantity exceeds the sold quantity')
    END;

    SELECT CASE
        WHEN NEW.disposition = 'restock'
         AND NEW.base_units_restocked <> NEW.base_units_returned
        THEN RAISE(ABORT, 'restocked return must restore all returned base units')
    END;

    SELECT CASE
        WHEN NEW.disposition = 'damaged'
         AND (NEW.base_units_restocked <> 0 OR NEW.restocked_cost_minor <> 0)
        THEN RAISE(ABORT, 'damaged return cannot restore sellable inventory')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_complete_guard
BEFORE UPDATE OF status ON sales_returns
WHEN OLD.status = 'draft' AND NEW.status = 'completed'
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM sales_return_items
            WHERE return_id = NEW.id
        )
        THEN RAISE(ABORT, 'completed sales return requires at least one item')
    END;

    SELECT CASE
        WHEN NEW.refund_amount_minor <>
        (
            SELECT COALESCE(SUM(refund_minor), 0)
            FROM sales_return_items
            WHERE return_id = NEW.id
        )
        THEN RAISE(ABORT, 'sales return refund total does not match its items')
    END;

    SELECT CASE
        WHEN NEW.returned_base_units <>
        (
            SELECT COALESCE(SUM(base_units_returned), 0)
            FROM sales_return_items
            WHERE return_id = NEW.id
        )
         OR NEW.restocked_base_units <>
        (
            SELECT COALESCE(SUM(base_units_restocked), 0)
            FROM sales_return_items
            WHERE return_id = NEW.id
        )
         OR NEW.returned_cost_minor <>
        (
            SELECT COALESCE(SUM(cost_value_minor), 0)
            FROM sales_return_items
            WHERE return_id = NEW.id
        )
         OR NEW.restocked_cost_minor <>
        (
            SELECT COALESCE(SUM(restocked_cost_minor), 0)
            FROM sales_return_items
            WHERE return_id = NEW.id
        )
        THEN RAISE(ABORT, 'sales return quantity or cost totals do not match its items')
    END;

    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM sales_return_accounting_links
            WHERE return_id = NEW.id
        )
        THEN RAISE(ABORT, 'completed sales return requires a posted accounting journal')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_completed_immutable
BEFORE UPDATE ON sales_returns
WHEN OLD.status = 'completed'
BEGIN
    SELECT RAISE(ABORT, 'completed sales returns are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_delete_guard
BEFORE DELETE ON sales_returns
BEGIN
    SELECT RAISE(ABORT, 'sales returns cannot be deleted');
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_item_update_guard
BEFORE UPDATE ON sales_return_items
BEGIN
    SELECT RAISE(ABORT, 'sales return items are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_item_delete_guard
BEFORE DELETE ON sales_return_items
BEGIN
    SELECT RAISE(ABORT, 'sales return items cannot be deleted');
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_accounting_link_insert
BEFORE INSERT ON sales_return_accounting_links
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM sales_returns AS header
            INNER JOIN accounting_journals AS journal
                ON journal.id = NEW.posting_journal_id
            WHERE header.id = NEW.return_id
              AND header.organization_id = NEW.organization_id
              AND header.shop_id = NEW.shop_id
              AND journal.organization_id = NEW.organization_id
              AND journal.shop_id = NEW.shop_id
              AND journal.source_type = 'system'
              AND journal.source_id = 'sale_return:' || NEW.return_id
              AND journal.status = 'posted'
        )
        THEN RAISE(ABORT, 'sales return accounting journal is invalid')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_accounting_link_update_guard
BEFORE UPDATE ON sales_return_accounting_links
BEGIN
    SELECT RAISE(ABORT, 'sales return accounting links are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_accounting_link_delete_guard
BEFORE DELETE ON sales_return_accounting_links
BEGIN
    SELECT RAISE(ABORT, 'sales return accounting links cannot be deleted');
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_document_update_guard
BEFORE UPDATE ON sales_return_documents
BEGIN
    SELECT RAISE(ABORT, 'sales return documents are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_return_document_delete_guard
BEFORE DELETE ON sales_return_documents
BEGIN
    SELECT RAISE(ABORT, 'sales return documents cannot be deleted');
END;

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    17,
    'Controlled partial sales returns, stock disposition, refunds, credit notes and return accounting',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);