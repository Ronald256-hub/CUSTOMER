INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT
    lower(hex(randomblob(16))),
    organization.id,
    '2190-CC',
    'Customer Credits',
    'liability',
    'credit',
    'customer_credits',
    0,
    1,
    1,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations AS organization
WHERE NOT EXISTS
(
    SELECT 1
    FROM accounting_accounts AS existing
    WHERE existing.organization_id = organization.id
      AND existing.system_key = 'customer_credits'
);

DROP VIEW IF EXISTS crm_customer_segments;
DROP VIEW IF EXISTS crm_customer_outstanding_balances;

DROP TRIGGER IF EXISTS trg_customer_receipt_insert_scope;
DROP TRIGGER IF EXISTS trg_customer_receipt_allocation_insert;
DROP TRIGGER IF EXISTS trg_customer_receipt_allocation_update;
DROP TRIGGER IF EXISTS trg_customer_receipt_allocation_delete;
DROP TRIGGER IF EXISTS trg_customer_receipt_ownership_update;
DROP TRIGGER IF EXISTS trg_customer_receipt_status;
DROP TRIGGER IF EXISTS trg_customer_receipt_delete;
DROP TRIGGER IF EXISTS trg_customer_receipt_system_credit_reversal_guard;

ALTER TABLE finance_customer_receipt_allocations
RENAME TO finance_customer_receipt_allocations_v17;

ALTER TABLE finance_customer_receipts
RENAME TO finance_customer_receipts_v17;

CREATE TABLE finance_customer_receipts
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    customer_id              TEXT NOT NULL,
    receipt_number           TEXT NOT NULL,
    receipt_date             TEXT NOT NULL
                             CHECK (length(receipt_date) = 10 AND date(receipt_date) = receipt_date),
    payment_method           TEXT NOT NULL
                             CHECK (payment_method IN
                             (
                                 'cash', 'mobile_money', 'card', 'bank',
                                 'credit_note', 'customer_credit'
                             )),
    amount_minor             INTEGER NOT NULL
                             CHECK (amount_minor > 0),
    reference                TEXT NOT NULL DEFAULT '',
    notes                    TEXT NOT NULL DEFAULT '',
    status                   TEXT NOT NULL DEFAULT 'draft'
                             CHECK (status IN ('draft', 'posted', 'reversed')),
    posting_journal_id       TEXT NULL,
    reversal_journal_id      TEXT NULL,
    created_by_user_id       TEXT NOT NULL,
    reversed_by_user_id      TEXT NULL,
    created_at_utc           TEXT NOT NULL,
    posted_at_utc            TEXT NULL,
    reversed_at_utc          TEXT NULL,
    reversal_reason          TEXT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (customer_id)
        REFERENCES finance_customers(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (posting_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (reversal_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (reversed_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    UNIQUE (organization_id, receipt_number),
    UNIQUE (posting_journal_id),
    UNIQUE (reversal_journal_id)
);

CREATE TABLE finance_customer_receipt_allocations
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    receipt_id               TEXT NOT NULL,
    receivable_item_id       TEXT NOT NULL,
    amount_minor             INTEGER NOT NULL
                             CHECK (amount_minor > 0),

    FOREIGN KEY (receipt_id)
        REFERENCES finance_customer_receipts(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (receivable_item_id)
        REFERENCES finance_receivable_items(id)
        ON DELETE RESTRICT,

    UNIQUE (receipt_id, receivable_item_id)
);

INSERT INTO finance_customer_receipts
(
    id, organization_id, shop_id, customer_id, receipt_number,
    receipt_date, payment_method, amount_minor, reference, notes,
    status, posting_journal_id, reversal_journal_id,
    created_by_user_id, reversed_by_user_id,
    created_at_utc, posted_at_utc, reversed_at_utc, reversal_reason
)
SELECT
    id, organization_id, shop_id, customer_id, receipt_number,
    receipt_date, payment_method, amount_minor, reference, notes,
    status, posting_journal_id, reversal_journal_id,
    created_by_user_id, reversed_by_user_id,
    created_at_utc, posted_at_utc, reversed_at_utc, reversal_reason
FROM finance_customer_receipts_v17;

INSERT INTO finance_customer_receipt_allocations
(
    id, receipt_id, receivable_item_id, amount_minor
)
SELECT
    id, receipt_id, receivable_item_id, amount_minor
FROM finance_customer_receipt_allocations_v17;

DROP TABLE finance_customer_receipt_allocations_v17;
DROP TABLE finance_customer_receipts_v17;

CREATE INDEX IF NOT EXISTS ix_customer_receipts_customer_date
    ON finance_customer_receipts
       (organization_id, customer_id, shop_id, receipt_date, status);

CREATE INDEX IF NOT EXISTS ix_customer_receipt_allocations_item
    ON finance_customer_receipt_allocations
       (receivable_item_id, receipt_id);

CREATE TRIGGER trg_customer_receipt_insert_scope
BEFORE INSERT ON finance_customer_receipts
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM shops AS shop
            INNER JOIN finance_customers AS customer
                ON customer.id = NEW.customer_id
               AND customer.organization_id = shop.organization_id
               AND customer.is_active = 1
            WHERE shop.id = NEW.shop_id
              AND shop.organization_id = NEW.organization_id
              AND shop.is_active = 1
        )
        THEN RAISE(ABORT, 'customer receipt scope is invalid')
    END;
END;

CREATE TRIGGER trg_customer_receipt_allocation_insert
BEFORE INSERT ON finance_customer_receipt_allocations
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM finance_customer_receipts AS receipt
            INNER JOIN finance_receivable_items AS item
                ON item.id = NEW.receivable_item_id
               AND item.organization_id = receipt.organization_id
               AND item.shop_id = receipt.shop_id
               AND item.customer_id = receipt.customer_id
            INNER JOIN accounting_journals AS source_journal
                ON source_journal.id = item.posting_journal_id
               AND source_journal.status = 'posted'
            WHERE receipt.id = NEW.receipt_id
              AND receipt.status = 'draft'
        )
        THEN RAISE(ABORT, 'customer receipt allocation scope is invalid')
    END;
END;

CREATE TRIGGER trg_customer_receipt_allocation_update
BEFORE UPDATE ON finance_customer_receipt_allocations
BEGIN
    SELECT RAISE(ABORT, 'customer receipt allocations are immutable');
END;

CREATE TRIGGER trg_customer_receipt_allocation_delete
BEFORE DELETE ON finance_customer_receipt_allocations
BEGIN
    SELECT RAISE(ABORT, 'customer receipt allocations are immutable');
END;

CREATE TRIGGER trg_customer_receipt_ownership_update
BEFORE UPDATE ON finance_customer_receipts
BEGIN
    SELECT CASE
        WHEN NEW.organization_id <> OLD.organization_id
          OR NEW.shop_id <> OLD.shop_id
          OR NEW.customer_id <> OLD.customer_id
          OR NEW.receipt_number <> OLD.receipt_number
          OR NEW.receipt_date <> OLD.receipt_date
          OR NEW.payment_method <> OLD.payment_method
          OR NEW.amount_minor <> OLD.amount_minor
          OR NEW.reference <> OLD.reference
          OR NEW.notes <> OLD.notes
          OR NEW.created_by_user_id <> OLD.created_by_user_id
          OR NEW.created_at_utc <> OLD.created_at_utc
        THEN RAISE(ABORT, 'customer receipt ownership is immutable')
    END;

    SELECT CASE
        WHEN OLD.posting_journal_id IS NOT NULL
         AND COALESCE(NEW.posting_journal_id, '') <> OLD.posting_journal_id
        THEN RAISE(ABORT, 'customer receipt posting link is immutable')
    END;

    SELECT CASE
        WHEN OLD.reversal_journal_id IS NOT NULL
         AND
         (
             COALESCE(NEW.reversal_journal_id, '') <> OLD.reversal_journal_id
             OR COALESCE(NEW.reversed_at_utc, '') <> COALESCE(OLD.reversed_at_utc, '')
             OR COALESCE(NEW.reversal_reason, '') <> COALESCE(OLD.reversal_reason, '')
             OR COALESCE(NEW.reversed_by_user_id, '') <> COALESCE(OLD.reversed_by_user_id, '')
         )
        THEN RAISE(ABORT, 'customer receipt reversal link is immutable')
    END;
END;

CREATE TRIGGER trg_customer_receipt_system_credit_reversal_guard
BEFORE UPDATE OF status ON finance_customer_receipts
WHEN OLD.status = 'posted'
 AND NEW.status = 'reversed'
 AND OLD.payment_method IN ('credit_note', 'customer_credit')
BEGIN
    SELECT RAISE(ABORT, 'system credit settlements are immutable');
END;

CREATE TRIGGER trg_customer_receipt_status
BEFORE UPDATE OF status ON finance_customer_receipts
WHEN NEW.status <> OLD.status
BEGIN
    SELECT CASE
        WHEN NOT
        (
            (OLD.status = 'draft' AND NEW.status = 'posted')
            OR
            (OLD.status = 'posted' AND NEW.status = 'reversed')
        )
        THEN RAISE(ABORT, 'invalid customer receipt status transition')
    END;

    SELECT CASE
        WHEN NEW.status = 'posted'
         AND
         (
             NEW.posting_journal_id IS NULL
             OR NEW.posted_at_utc IS NULL
             OR
             (
                 SELECT COALESCE(SUM(amount_minor), 0)
                 FROM finance_customer_receipt_allocations
                 WHERE receipt_id = NEW.id
             ) <> NEW.amount_minor
             OR NOT EXISTS
             (
                 SELECT 1
                 FROM accounting_journals AS journal
                 WHERE journal.id = NEW.posting_journal_id
                   AND journal.organization_id = NEW.organization_id
                   AND journal.shop_id = NEW.shop_id
                   AND journal.source_type = 'system'
                   AND journal.source_id = 'customer_receipt:' || NEW.id
                   AND journal.status = 'posted'
                   AND journal.total_debit_minor = NEW.amount_minor
                   AND journal.total_credit_minor = NEW.amount_minor
             )
         )
        THEN RAISE(ABORT, 'posted customer receipt is incomplete')
    END;

    SELECT CASE
        WHEN NEW.status = 'posted'
         AND EXISTS
         (
             SELECT 1
             FROM finance_customer_receipt_allocations AS allocation
             INNER JOIN finance_receivable_items AS item
                 ON item.id = allocation.receivable_item_id
             WHERE allocation.receipt_id = NEW.id
             GROUP BY allocation.receivable_item_id, item.original_amount_minor
             HAVING SUM(allocation.amount_minor) >
                 item.original_amount_minor - COALESCE
                 (
                     (
                         SELECT SUM(other_allocation.amount_minor)
                         FROM finance_customer_receipt_allocations AS other_allocation
                         INNER JOIN finance_customer_receipts AS other_receipt
                             ON other_receipt.id = other_allocation.receipt_id
                         WHERE other_allocation.receivable_item_id = allocation.receivable_item_id
                           AND other_receipt.status = 'posted'
                           AND other_receipt.id <> NEW.id
                     ),
                     0
                 )
         )
        THEN RAISE(ABORT, 'customer receipt allocation exceeds outstanding receivable')
    END;

    SELECT CASE
        WHEN NEW.status = 'reversed'
         AND
         (
             NEW.reversal_journal_id IS NULL
             OR NEW.reversed_at_utc IS NULL
             OR NEW.reversed_by_user_id IS NULL
             OR length(trim(COALESCE(NEW.reversal_reason, ''))) < 5
             OR NOT EXISTS
             (
                 SELECT 1
                 FROM accounting_journals AS original
                 INNER JOIN accounting_journals AS reversal
                     ON reversal.id = NEW.reversal_journal_id
                    AND reversal.reversal_of_journal_id = original.id
                 WHERE original.id = NEW.posting_journal_id
                   AND original.status = 'reversed'
                   AND reversal.status = 'posted'
                   AND reversal.organization_id = NEW.organization_id
                   AND reversal.shop_id = NEW.shop_id
             )
         )
        THEN RAISE(ABORT, 'reversed customer receipt is incomplete')
    END;
END;

CREATE TRIGGER trg_customer_receipt_delete
BEFORE DELETE ON finance_customer_receipts
BEGIN
    SELECT RAISE(ABORT, 'customer receipts cannot be deleted');
END;

CREATE TABLE finance_credit_return_sequences
(
    shop_id                  TEXT PRIMARY KEY,
    next_value               INTEGER NOT NULL DEFAULT 1
                             CHECK (next_value >= 1),
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE CASCADE
);

CREATE TABLE finance_customer_credit_application_sequences
(
    shop_id                  TEXT PRIMARY KEY,
    next_value               INTEGER NOT NULL DEFAULT 1
                             CHECK (next_value >= 1),
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE CASCADE
);

INSERT OR IGNORE INTO finance_credit_return_sequences
(shop_id, next_value, updated_at_utc)
SELECT id, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM shops;

INSERT OR IGNORE INTO finance_customer_credit_application_sequences
(shop_id, next_value, updated_at_utc)
SELECT id, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM shops;

CREATE TABLE finance_credit_returns
(
    id                          TEXT PRIMARY KEY,
    organization_id             TEXT NOT NULL,
    shop_id                     TEXT NOT NULL,
    sale_id                     TEXT NOT NULL,
    shift_id                    TEXT NOT NULL,
    customer_id                 TEXT NOT NULL,
    receivable_item_id          TEXT NOT NULL,
    credit_note_number          TEXT NOT NULL COLLATE NOCASE,
    original_receipt_number     TEXT NOT NULL,
    status                      TEXT NOT NULL DEFAULT 'draft'
                                CHECK (status IN ('draft', 'completed')),
    return_amount_minor         INTEGER NOT NULL
                                CHECK (return_amount_minor > 0),
    receivable_reduction_minor  INTEGER NOT NULL DEFAULT 0
                                CHECK (receivable_reduction_minor >= 0),
    customer_credit_minor       INTEGER NOT NULL DEFAULT 0
                                CHECK (customer_credit_minor >= 0),
    returned_base_units         INTEGER NOT NULL DEFAULT 0
                                CHECK (returned_base_units >= 0),
    restocked_base_units        INTEGER NOT NULL DEFAULT 0
                                CHECK (restocked_base_units >= 0),
    returned_cost_minor         INTEGER NOT NULL DEFAULT 0
                                CHECK (returned_cost_minor >= 0),
    restocked_cost_minor        INTEGER NOT NULL DEFAULT 0
                                CHECK (restocked_cost_minor >= 0),
    settlement_receipt_id       TEXT NULL UNIQUE,
    return_journal_id           TEXT NULL UNIQUE,
    reason                      TEXT NOT NULL,
    notes                       TEXT NOT NULL DEFAULT '',
    created_by_user_id          TEXT NOT NULL,
    approved_by_user_id         TEXT NOT NULL,
    completed_at_utc            TEXT NOT NULL,
    version                     INTEGER NOT NULL DEFAULT 1
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

    FOREIGN KEY (customer_id)
        REFERENCES finance_customers(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (receivable_item_id)
        REFERENCES finance_receivable_items(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (settlement_receipt_id)
        REFERENCES finance_customer_receipts(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (return_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (approved_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    UNIQUE (organization_id, credit_note_number),
    CHECK
    (
        return_amount_minor =
        receivable_reduction_minor + customer_credit_minor
    )
);

CREATE INDEX ix_finance_credit_returns_sale_time
    ON finance_credit_returns(sale_id, completed_at_utc, status);

CREATE INDEX ix_finance_credit_returns_customer_time
    ON finance_credit_returns
       (organization_id, customer_id, shop_id, completed_at_utc, status);

CREATE TABLE finance_credit_return_items
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
        REFERENCES finance_credit_returns(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (sale_item_id)
        REFERENCES sale_items(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (product_id)
        REFERENCES products(id)
        ON DELETE RESTRICT,

    UNIQUE (return_id, sale_item_id)
);

CREATE INDEX ix_finance_credit_return_items_sale_item
    ON finance_credit_return_items(sale_item_id, return_id);

CREATE TABLE finance_customer_credits
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    customer_id              TEXT NOT NULL,
    source_credit_return_id  TEXT NOT NULL UNIQUE,
    credit_number            TEXT NOT NULL,
    original_amount_minor    INTEGER NOT NULL
                             CHECK (original_amount_minor > 0),
    posting_journal_id       TEXT NOT NULL UNIQUE,
    created_at_utc           TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (customer_id)
        REFERENCES finance_customers(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (source_credit_return_id)
        REFERENCES finance_credit_returns(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (posting_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    UNIQUE (organization_id, credit_number)
);

CREATE TABLE finance_customer_credit_applications
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    customer_id              TEXT NOT NULL,
    credit_id                TEXT NOT NULL,
    receipt_id               TEXT NOT NULL UNIQUE,
    receivable_item_id       TEXT NOT NULL,
    application_number       TEXT NOT NULL,
    application_date         TEXT NOT NULL
                             CHECK (length(application_date) = 10 AND date(application_date) = application_date),
    amount_minor             INTEGER NOT NULL
                             CHECK (amount_minor > 0),
    posting_journal_id       TEXT NOT NULL UNIQUE,
    notes                    TEXT NOT NULL DEFAULT '',
    created_by_user_id       TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (customer_id)
        REFERENCES finance_customers(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (credit_id)
        REFERENCES finance_customer_credits(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (receipt_id)
        REFERENCES finance_customer_receipts(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (receivable_item_id)
        REFERENCES finance_receivable_items(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (posting_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    UNIQUE (organization_id, application_number)
);

CREATE INDEX ix_customer_credit_applications_credit
    ON finance_customer_credit_applications(credit_id, created_at_utc);

CREATE INDEX ix_customer_credit_applications_receivable
    ON finance_customer_credit_applications(receivable_item_id, created_at_utc);

CREATE TABLE finance_credit_return_documents
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
        REFERENCES finance_credit_returns(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (generated_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    UNIQUE (return_id, file_format)
);

CREATE VIEW finance_customer_credit_balances AS
SELECT
    credit.id,
    credit.organization_id,
    credit.shop_id,
    credit.customer_id,
    credit.source_credit_return_id,
    credit.credit_number,
    credit.original_amount_minor,
    COALESCE(SUM(application.amount_minor), 0) AS applied_amount_minor,
    credit.original_amount_minor -
        COALESCE(SUM(application.amount_minor), 0) AS available_amount_minor,
    CASE
        WHEN COALESCE(SUM(application.amount_minor), 0) = 0 THEN 'open'
        WHEN COALESCE(SUM(application.amount_minor), 0) < credit.original_amount_minor
            THEN 'partial'
        ELSE 'applied'
    END AS status,
    credit.posting_journal_id,
    credit.created_at_utc
FROM finance_customer_credits AS credit
LEFT JOIN finance_customer_credit_applications AS application
    ON application.credit_id = credit.id
GROUP BY credit.id;

CREATE TRIGGER trg_finance_credit_return_scope_insert
BEFORE INSERT ON finance_credit_returns
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM sales AS sale
            INNER JOIN sale_payments AS payment
                ON payment.sale_id = sale.id
               AND payment.payment_method = 'credit'
            INNER JOIN finance_receivable_items AS receivable
                ON receivable.id = NEW.receivable_item_id
               AND receivable.sale_id = sale.id
               AND receivable.customer_id = sale.customer_id
               AND receivable.organization_id = NEW.organization_id
               AND receivable.shop_id = NEW.shop_id
            INNER JOIN shops AS shop
                ON shop.id = sale.shop_id
               AND shop.organization_id = NEW.organization_id
            WHERE sale.id = NEW.sale_id
              AND sale.shop_id = NEW.shop_id
              AND sale.customer_id = NEW.customer_id
              AND sale.status IN ('completed', 'partially_returned')
              AND shop.is_active = 1
        )
        THEN RAISE(ABORT, 'credit return requires a posted credit sale and receivable in the active shop')
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
        THEN RAISE(ABORT, 'credit return requires the operator open shift')
    END;
END;

CREATE TRIGGER trg_finance_credit_return_item_insert
BEFORE INSERT ON finance_credit_return_items
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM finance_credit_returns AS header
            INNER JOIN sale_items AS sale_item
                ON sale_item.id = NEW.sale_item_id
               AND sale_item.sale_id = header.sale_id
               AND sale_item.product_id = NEW.product_id
            WHERE header.id = NEW.return_id
              AND header.status = 'draft'
              AND NEW.quantity <= sale_item.quantity - sale_item.returned_quantity
        )
        THEN RAISE(ABORT, 'credit return item exceeds the remaining sold quantity')
    END;
END;

CREATE TRIGGER trg_finance_credit_return_ownership_update
BEFORE UPDATE ON finance_credit_returns
BEGIN
    SELECT CASE
        WHEN NEW.organization_id <> OLD.organization_id
          OR NEW.shop_id <> OLD.shop_id
          OR NEW.sale_id <> OLD.sale_id
          OR NEW.shift_id <> OLD.shift_id
          OR NEW.customer_id <> OLD.customer_id
          OR NEW.receivable_item_id <> OLD.receivable_item_id
          OR NEW.credit_note_number <> OLD.credit_note_number
          OR NEW.original_receipt_number <> OLD.original_receipt_number
          OR NEW.return_amount_minor <> OLD.return_amount_minor
          OR NEW.receivable_reduction_minor <> OLD.receivable_reduction_minor
          OR NEW.customer_credit_minor <> OLD.customer_credit_minor
          OR NEW.returned_base_units <> OLD.returned_base_units
          OR NEW.restocked_base_units <> OLD.restocked_base_units
          OR NEW.returned_cost_minor <> OLD.returned_cost_minor
          OR NEW.restocked_cost_minor <> OLD.restocked_cost_minor
          OR NEW.reason <> OLD.reason
          OR NEW.notes <> OLD.notes
          OR NEW.created_by_user_id <> OLD.created_by_user_id
          OR NEW.approved_by_user_id <> OLD.approved_by_user_id
          OR NEW.completed_at_utc <> OLD.completed_at_utc
        THEN RAISE(ABORT, 'credit return ownership and values are immutable')
    END;

    SELECT CASE
        WHEN OLD.status = 'completed'
         AND
         (
             NEW.status <> OLD.status
             OR COALESCE(NEW.settlement_receipt_id, '') <>
                COALESCE(OLD.settlement_receipt_id, '')
             OR COALESCE(NEW.return_journal_id, '') <>
                COALESCE(OLD.return_journal_id, '')
             OR NEW.version <> OLD.version
         )
        THEN RAISE(ABORT, 'completed credit returns are immutable')
    END;
END;

CREATE TRIGGER trg_finance_credit_return_status
BEFORE UPDATE OF status ON finance_credit_returns
WHEN NEW.status <> OLD.status
BEGIN
    SELECT CASE
        WHEN NOT (OLD.status = 'draft' AND NEW.status = 'completed')
        THEN RAISE(ABORT, 'invalid credit return status transition')
    END;

    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM finance_credit_return_items
            WHERE return_id = NEW.id
        )
        OR
        (
            SELECT COALESCE(SUM(refund_minor), 0)
            FROM finance_credit_return_items
            WHERE return_id = NEW.id
        ) <> NEW.return_amount_minor
        OR
        (
            SELECT COALESCE(SUM(base_units_returned), 0)
            FROM finance_credit_return_items
            WHERE return_id = NEW.id
        ) <> NEW.returned_base_units
        OR
        (
            SELECT COALESCE(SUM(base_units_restocked), 0)
            FROM finance_credit_return_items
            WHERE return_id = NEW.id
        ) <> NEW.restocked_base_units
        OR
        (
            SELECT COALESCE(SUM(cost_value_minor), 0)
            FROM finance_credit_return_items
            WHERE return_id = NEW.id
        ) <> NEW.returned_cost_minor
        OR
        (
            SELECT COALESCE(SUM(restocked_cost_minor), 0)
            FROM finance_credit_return_items
            WHERE return_id = NEW.id
        ) <> NEW.restocked_cost_minor
        THEN RAISE(ABORT, 'completed credit return line totals are incomplete')
    END;

    SELECT CASE
        WHEN NEW.receivable_reduction_minor > 0
         AND NOT EXISTS
         (
             SELECT 1
             FROM finance_customer_receipts AS receipt
             INNER JOIN finance_customer_receipt_allocations AS allocation
                 ON allocation.receipt_id = receipt.id
                AND allocation.receivable_item_id = NEW.receivable_item_id
                AND allocation.amount_minor = NEW.receivable_reduction_minor
             WHERE receipt.id = NEW.settlement_receipt_id
               AND receipt.organization_id = NEW.organization_id
               AND receipt.shop_id = NEW.shop_id
               AND receipt.customer_id = NEW.customer_id
               AND receipt.payment_method = 'credit_note'
               AND receipt.amount_minor = NEW.receivable_reduction_minor
               AND receipt.status = 'posted'
         )
        THEN RAISE(ABORT, 'credit return receivable settlement is incomplete')
    END;

    SELECT CASE
        WHEN NEW.receivable_reduction_minor = 0
         AND NEW.settlement_receipt_id IS NOT NULL
        THEN RAISE(ABORT, 'credit return cannot link an empty receivable settlement')
    END;

    SELECT CASE
        WHEN NEW.customer_credit_minor > 0
         AND NOT EXISTS
         (
             SELECT 1
             FROM finance_customer_credits AS credit
             INNER JOIN accounting_journals AS journal
                 ON journal.id = credit.posting_journal_id
                AND journal.status = 'posted'
             WHERE credit.source_credit_return_id = NEW.id
               AND credit.organization_id = NEW.organization_id
               AND credit.shop_id = NEW.shop_id
               AND credit.customer_id = NEW.customer_id
               AND credit.original_amount_minor = NEW.customer_credit_minor
         )
        THEN RAISE(ABORT, 'credit return customer credit is incomplete')
    END;

    SELECT CASE
        WHEN NEW.customer_credit_minor + NEW.restocked_cost_minor > 0
         AND NOT EXISTS
         (
             SELECT 1
             FROM accounting_journals AS journal
             WHERE journal.id = NEW.return_journal_id
               AND journal.organization_id = NEW.organization_id
               AND journal.shop_id = NEW.shop_id
               AND journal.source_type = 'system'
               AND journal.source_id = 'credit_sale_return:' || NEW.id
               AND journal.status = 'posted'
               AND journal.total_debit_minor =
                   NEW.customer_credit_minor + NEW.restocked_cost_minor
               AND journal.total_credit_minor =
                   NEW.customer_credit_minor + NEW.restocked_cost_minor
         )
        THEN RAISE(ABORT, 'credit return accounting journal is incomplete')
    END;

    SELECT CASE
        WHEN NEW.customer_credit_minor + NEW.restocked_cost_minor = 0
         AND NEW.return_journal_id IS NOT NULL
        THEN RAISE(ABORT, 'credit return cannot link an empty return journal')
    END;
END;

CREATE TRIGGER trg_finance_credit_return_delete
BEFORE DELETE ON finance_credit_returns
BEGIN
    SELECT RAISE(ABORT, 'credit returns cannot be deleted');
END;

CREATE TRIGGER trg_finance_credit_return_item_update
BEFORE UPDATE ON finance_credit_return_items
BEGIN
    SELECT RAISE(ABORT, 'credit return lines are immutable');
END;

CREATE TRIGGER trg_finance_credit_return_item_delete
BEFORE DELETE ON finance_credit_return_items
BEGIN
    SELECT RAISE(ABORT, 'credit return lines cannot be deleted');
END;

CREATE TRIGGER trg_finance_customer_credit_update
BEFORE UPDATE ON finance_customer_credits
BEGIN
    SELECT RAISE(ABORT, 'customer credit source records are immutable');
END;

CREATE TRIGGER trg_finance_customer_credit_delete
BEFORE DELETE ON finance_customer_credits
BEGIN
    SELECT RAISE(ABORT, 'customer credit source records cannot be deleted');
END;

CREATE TRIGGER trg_finance_customer_credit_application_insert
BEFORE INSERT ON finance_customer_credit_applications
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM finance_customer_credit_balances AS balance
            INNER JOIN finance_receivable_items AS receivable
                ON receivable.id = NEW.receivable_item_id
               AND receivable.organization_id = NEW.organization_id
               AND receivable.shop_id = NEW.shop_id
               AND receivable.customer_id = NEW.customer_id
            INNER JOIN finance_customer_receipts AS receipt
                ON receipt.id = NEW.receipt_id
               AND receipt.organization_id = NEW.organization_id
               AND receipt.shop_id = NEW.shop_id
               AND receipt.customer_id = NEW.customer_id
               AND receipt.payment_method = 'customer_credit'
               AND receipt.amount_minor = NEW.amount_minor
               AND receipt.status = 'posted'
               AND receipt.posting_journal_id = NEW.posting_journal_id
            INNER JOIN finance_customer_receipt_allocations AS allocation
                ON allocation.receipt_id = receipt.id
               AND allocation.receivable_item_id = receivable.id
               AND allocation.amount_minor = NEW.amount_minor
            WHERE balance.id = NEW.credit_id
              AND balance.organization_id = NEW.organization_id
              AND balance.shop_id = NEW.shop_id
              AND balance.customer_id = NEW.customer_id
              AND balance.available_amount_minor >= NEW.amount_minor
        )
        THEN RAISE(ABORT, 'customer credit application scope or available balance is invalid')
    END;
END;

CREATE TRIGGER trg_finance_customer_credit_application_update
BEFORE UPDATE ON finance_customer_credit_applications
BEGIN
    SELECT RAISE(ABORT, 'customer credit applications are immutable');
END;

CREATE TRIGGER trg_finance_customer_credit_application_delete
BEFORE DELETE ON finance_customer_credit_applications
BEGIN
    SELECT RAISE(ABORT, 'customer credit applications cannot be deleted');
END;

CREATE TRIGGER trg_finance_credit_return_document_update
BEFORE UPDATE ON finance_credit_return_documents
BEGIN
    SELECT RAISE(ABORT, 'credit return documents are immutable');
END;

CREATE TRIGGER trg_finance_credit_return_document_delete
BEFORE DELETE ON finance_credit_return_documents
BEGIN
    SELECT RAISE(ABORT, 'credit return documents cannot be deleted');
END;

DROP TRIGGER IF EXISTS trg_sale_item_return_counter_guard;

CREATE TRIGGER trg_sale_item_return_counter_guard
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
         AND NOT EXISTS
        (
            SELECT 1
            FROM finance_credit_return_items AS return_item
            INNER JOIN finance_credit_returns AS header
                ON header.id = return_item.return_id
            WHERE return_item.sale_item_id = OLD.id
              AND header.status = 'draft'
              AND OLD.returned_quantity + return_item.quantity = NEW.returned_quantity
        )
        THEN RAISE(ABORT, 'sale item return counter requires a matching draft return')
    END;
END;

CREATE VIEW finance_credit_return_loyalty_adjustments AS
SELECT
    header.id AS return_id,
    header.organization_id,
    header.shop_id,
    header.sale_id,
    sale.customer_id,
    header.approved_by_user_id,
    header.completed_at_utc,
    header.credit_note_number,
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
        THEN MAX
        (
            earn.points_delta - COALESCE
            (
                (
                    SELECT -SUM(existing.points_delta)
                    FROM crm_loyalty_ledger AS existing
                    WHERE existing.sale_id = sale.id
                      AND existing.entry_type = 'adjustment'
                      AND existing.reference_type IN
                          ('sale_return', 'credit_sale_return')
                ),
                0
            ),
            0
        )
        ELSE MIN
        (
            MAX
            (
                earn.points_delta - COALESCE
                (
                    (
                        SELECT -SUM(existing.points_delta)
                        FROM crm_loyalty_ledger AS existing
                        WHERE existing.sale_id = sale.id
                          AND existing.entry_type = 'adjustment'
                          AND existing.reference_type IN
                              ('sale_return', 'credit_sale_return')
                    ),
                    0
                ),
                0
            ),
            CAST(header.return_amount_minor / settings.spend_minor_per_point AS INTEGER)
        )
    END AS points_to_reverse
FROM finance_credit_returns AS header
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

CREATE TRIGGER trg_finance_credit_return_loyalty_adjustment
AFTER UPDATE OF status ON finance_credit_returns
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
        'credit_sale_return',
        adjustment.return_id,
        'Automatic loyalty adjustment for ' || adjustment.credit_note_number,
        adjustment.approved_by_user_id,
        adjustment.completed_at_utc
    FROM finance_credit_return_loyalty_adjustments AS adjustment
    WHERE adjustment.return_id = NEW.id
      AND adjustment.points_to_reverse > 0
      AND NOT EXISTS
      (
          SELECT 1
          FROM crm_loyalty_ledger
          WHERE reference_type = 'credit_sale_return'
            AND reference_id = NEW.id
      );

    UPDATE crm_customer_profiles
    SET current_points = current_points + COALESCE
        (
            (
                SELECT points_delta
                FROM crm_loyalty_ledger
                WHERE reference_type = 'credit_sale_return'
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
                    WHERE reference_type = 'credit_sale_return'
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
                        WHERE reference_type = 'credit_sale_return'
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
                        WHERE reference_type = 'credit_sale_return'
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
                        WHERE reference_type = 'credit_sale_return'
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
    WHERE customer_id = NEW.customer_id
      AND EXISTS
      (
          SELECT 1
          FROM crm_loyalty_ledger
          WHERE reference_type = 'credit_sale_return'
            AND reference_id = NEW.id
      );
END;

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
return_events AS
(
    SELECT sale_id, refund_amount_minor AS amount_minor
    FROM sales_returns
    WHERE status = 'completed'

    UNION ALL

    SELECT sale_id, return_amount_minor
    FROM finance_credit_returns
    WHERE status = 'completed'
),
return_metrics AS
(
    SELECT
        sale.customer_id,
        COALESCE(SUM(event.amount_minor), 0) AS returned_spend_minor
    FROM return_events AS event
    INNER JOIN sales AS sale
        ON sale.id = event.sale_id
    WHERE sale.customer_id IS NOT NULL
    GROUP BY sale.customer_id
)
SELECT
    customer.organization_id,
    customer.id AS customer_id,
    COALESCE(sales.completed_sale_count, 0) AS completed_sale_count,
    MAX
    (
        COALESCE(sales.gross_spend_minor, 0) -
        COALESCE(refunds.returned_spend_minor, 0),
        0
    ) AS lifetime_spend_minor,
    CASE
        WHEN COALESCE(sales.completed_sale_count, 0) = 0 THEN 0
        ELSE MAX
        (
            COALESCE(sales.gross_spend_minor, 0) -
            COALESCE(refunds.returned_spend_minor, 0),
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

CREATE VIEW crm_customer_outstanding_balances AS
SELECT
    customer.organization_id,
    customer.id AS customer_id,
    COALESCE(SUM
    (
        receivable.original_amount_minor - COALESCE
        (
            (
                SELECT SUM(allocation.amount_minor)
                FROM finance_customer_receipt_allocations AS allocation
                INNER JOIN finance_customer_receipts AS receipt
                    ON receipt.id = allocation.receipt_id
                   AND receipt.status = 'posted'
                WHERE allocation.receivable_item_id = receivable.id
            ),
            0
        )
    ), 0) AS outstanding_minor
FROM finance_customers AS customer
LEFT JOIN finance_receivable_items AS receivable
    ON receivable.customer_id = customer.id
GROUP BY customer.organization_id, customer.id;

CREATE VIEW crm_customer_segments AS
SELECT
    customer.organization_id,
    customer.id AS customer_id,
    CASE
        WHEN customer.is_active = 0 OR profile.lifecycle_stage = 'blocked'
            THEN 'blocked'
        WHEN outstanding.outstanding_minor > 0 THEN 'debtor'
        WHEN metrics.completed_sale_count = 0 THEN 'prospect'
        WHEN metrics.last_sale_at_utc < datetime('now', '-90 days')
            THEN 'dormant'
        WHEN metrics.completed_sale_count >= 5 THEN 'loyal'
        WHEN metrics.first_sale_at_utc >= datetime('now', '-30 days')
            THEN 'new'
        ELSE 'active'
    END AS segment
FROM finance_customers AS customer
INNER JOIN crm_customer_profiles AS profile
    ON profile.customer_id = customer.id
INNER JOIN crm_customer_sales_metrics AS metrics
    ON metrics.customer_id = customer.id
INNER JOIN crm_customer_outstanding_balances AS outstanding
    ON outstanding.customer_id = customer.id;

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    18,
    'Credit-sale returns, receivable adjustments and customer credit applications',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);