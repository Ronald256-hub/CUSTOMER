CREATE TABLE IF NOT EXISTS finance_customers
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    customer_number          TEXT NOT NULL COLLATE NOCASE,
    name                     TEXT NOT NULL,
    phone                    TEXT NOT NULL DEFAULT '',
    email                    TEXT NOT NULL DEFAULT '',
    address                  TEXT NOT NULL DEFAULT '',
    tax_number               TEXT NOT NULL DEFAULT '',
    credit_limit_minor       INTEGER NOT NULL DEFAULT 0
                             CHECK (credit_limit_minor >= 0),
    payment_terms_days       INTEGER NOT NULL DEFAULT 30
                             CHECK (payment_terms_days BETWEEN 0 AND 3650),
    is_active                INTEGER NOT NULL DEFAULT 1
                             CHECK (is_active IN (0, 1)),
    version                  INTEGER NOT NULL DEFAULT 1
                             CHECK (version >= 1),
    created_by_user_id       TEXT NOT NULL,
    updated_by_user_id       TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    UNIQUE (organization_id, customer_number)
);

CREATE INDEX IF NOT EXISTS ix_finance_customers_org_name
    ON finance_customers(organization_id, is_active, name COLLATE NOCASE);

ALTER TABLE suppliers
ADD COLUMN organization_id TEXT NOT NULL DEFAULT 'default-organization';

ALTER TABLE suppliers
ADD COLUMN payment_terms_days INTEGER NOT NULL DEFAULT 30
CHECK (payment_terms_days BETWEEN 0 AND 3650);

ALTER TABLE suppliers
ADD COLUMN credit_limit_minor INTEGER NOT NULL DEFAULT 0
CHECK (credit_limit_minor >= 0);

ALTER TABLE suppliers
ADD COLUMN version INTEGER NOT NULL DEFAULT 1
CHECK (version >= 1);

CREATE INDEX IF NOT EXISTS ix_suppliers_org_name
    ON suppliers(organization_id, is_active, name COLLATE NOCASE);

ALTER TABLE sales
ADD COLUMN customer_id TEXT NULL;

CREATE INDEX IF NOT EXISTS ix_sales_customer_completed
    ON sales(customer_id, completed_at_utc, status);

CREATE TABLE IF NOT EXISTS finance_receivable_items
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    customer_id              TEXT NOT NULL,
    sale_id                  TEXT NOT NULL,
    document_number          TEXT NOT NULL,
    document_date            TEXT NOT NULL
                             CHECK (length(document_date) = 10 AND date(document_date) = document_date),
    due_date                 TEXT NOT NULL
                             CHECK (length(due_date) = 10 AND date(due_date) = due_date),
    original_amount_minor    INTEGER NOT NULL
                             CHECK (original_amount_minor > 0),
    posting_journal_id       TEXT NOT NULL,
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

    FOREIGN KEY (sale_id)
        REFERENCES sales(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (posting_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    UNIQUE (organization_id, sale_id),
    UNIQUE (posting_journal_id)
);

CREATE INDEX IF NOT EXISTS ix_receivables_customer_due
    ON finance_receivable_items(organization_id, customer_id, shop_id, due_date);

CREATE TABLE IF NOT EXISTS finance_payable_items
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    supplier_id              TEXT NULL,
    purchase_id              TEXT NOT NULL,
    document_number          TEXT NOT NULL,
    supplier_invoice_number  TEXT NOT NULL DEFAULT '',
    document_date            TEXT NOT NULL
                             CHECK (length(document_date) = 10 AND date(document_date) = document_date),
    due_date                 TEXT NOT NULL
                             CHECK (length(due_date) = 10 AND date(due_date) = due_date),
    original_amount_minor    INTEGER NOT NULL
                             CHECK (original_amount_minor > 0),
    posting_journal_id       TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (supplier_id)
        REFERENCES suppliers(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (purchase_id)
        REFERENCES purchases(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (posting_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    UNIQUE (organization_id, purchase_id),
    UNIQUE (posting_journal_id)
);

CREATE INDEX IF NOT EXISTS ix_payables_supplier_due
    ON finance_payable_items(organization_id, supplier_id, shop_id, due_date);

CREATE TABLE IF NOT EXISTS finance_customer_receipts
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    customer_id              TEXT NOT NULL,
    receipt_number           TEXT NOT NULL,
    receipt_date             TEXT NOT NULL
                             CHECK (length(receipt_date) = 10 AND date(receipt_date) = receipt_date),
    payment_method           TEXT NOT NULL
                             CHECK (payment_method IN ('cash', 'mobile_money', 'card', 'bank')),
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

CREATE INDEX IF NOT EXISTS ix_customer_receipts_customer_date
    ON finance_customer_receipts(organization_id, customer_id, shop_id, receipt_date, status);

CREATE TABLE IF NOT EXISTS finance_customer_receipt_allocations
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

CREATE INDEX IF NOT EXISTS ix_customer_receipt_allocations_item
    ON finance_customer_receipt_allocations(receivable_item_id, receipt_id);

CREATE TABLE IF NOT EXISTS finance_supplier_payments
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    supplier_id              TEXT NOT NULL,
    payment_number           TEXT NOT NULL,
    payment_date             TEXT NOT NULL
                             CHECK (length(payment_date) = 10 AND date(payment_date) = payment_date),
    payment_method           TEXT NOT NULL
                             CHECK (payment_method IN ('cash', 'mobile_money', 'card', 'bank')),
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

    FOREIGN KEY (supplier_id)
        REFERENCES suppliers(id)
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

    UNIQUE (organization_id, payment_number),
    UNIQUE (posting_journal_id),
    UNIQUE (reversal_journal_id)
);

CREATE INDEX IF NOT EXISTS ix_supplier_payments_supplier_date
    ON finance_supplier_payments(organization_id, supplier_id, shop_id, payment_date, status);

CREATE TABLE IF NOT EXISTS finance_supplier_payment_allocations
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    payment_id               TEXT NOT NULL,
    payable_item_id          TEXT NOT NULL,
    amount_minor             INTEGER NOT NULL
                             CHECK (amount_minor > 0),

    FOREIGN KEY (payment_id)
        REFERENCES finance_supplier_payments(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (payable_item_id)
        REFERENCES finance_payable_items(id)
        ON DELETE RESTRICT,

    UNIQUE (payment_id, payable_item_id)
);

CREATE INDEX IF NOT EXISTS ix_supplier_payment_allocations_item
    ON finance_supplier_payment_allocations(payable_item_id, payment_id);

CREATE TRIGGER IF NOT EXISTS trg_finance_customer_scope_insert
BEFORE INSERT ON finance_customers
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM organizations
            WHERE id = NEW.organization_id
        )
        THEN RAISE(ABORT, 'customer organization is invalid')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_finance_customer_scope_update
BEFORE UPDATE ON finance_customers
BEGIN
    SELECT CASE
        WHEN NEW.organization_id <> OLD.organization_id
          OR NEW.customer_number <> OLD.customer_number
          OR NEW.created_by_user_id <> OLD.created_by_user_id
          OR NEW.created_at_utc <> OLD.created_at_utc
        THEN RAISE(ABORT, 'customer ownership is immutable')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_supplier_organization_insert
BEFORE INSERT ON suppliers
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM organizations
            WHERE id = NEW.organization_id
        )
        THEN RAISE(ABORT, 'supplier organization is invalid')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_supplier_organization_update
BEFORE UPDATE OF organization_id ON suppliers
WHEN NEW.organization_id <> OLD.organization_id
BEGIN
    SELECT RAISE(ABORT, 'supplier organization is immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_customer_scope_insert
BEFORE INSERT ON sales
WHEN NEW.customer_id IS NOT NULL
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM finance_customers AS customer
            INNER JOIN shops AS shop
                ON shop.id = COALESCE(NEW.shop_id, 'main-shop')
            WHERE customer.id = NEW.customer_id
              AND customer.organization_id = shop.organization_id
              AND customer.is_active = 1
        )
        THEN RAISE(ABORT, 'sale customer is invalid for the shop organization')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_customer_scope_update
BEFORE UPDATE OF customer_id ON sales
WHEN COALESCE(NEW.customer_id, '') <> COALESCE(OLD.customer_id, '')
BEGIN
    SELECT CASE
        WHEN EXISTS
        (
            SELECT 1
            FROM accounting_operational_links
            WHERE source_type = 'sale'
              AND source_id = OLD.id
        )
        THEN RAISE(ABORT, 'posted sale customer is immutable')
    END;

    SELECT CASE
        WHEN NEW.customer_id IS NOT NULL
         AND NOT EXISTS
        (
            SELECT 1
            FROM finance_customers AS customer
            INNER JOIN shops AS shop
                ON shop.id = COALESCE(NEW.shop_id, 'main-shop')
            WHERE customer.id = NEW.customer_id
              AND customer.organization_id = shop.organization_id
              AND customer.is_active = 1
        )
        THEN RAISE(ABORT, 'sale customer is invalid for the shop organization')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_credit_sale_customer_required
BEFORE INSERT ON sale_payments
WHEN NEW.payment_method = 'credit'
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM sales AS sale
            INNER JOIN shops AS shop
                ON shop.id = COALESCE(sale.shop_id, 'main-shop')
            INNER JOIN finance_customers AS customer
                ON customer.id = sale.customer_id
               AND customer.organization_id = shop.organization_id
               AND customer.is_active = 1
            WHERE sale.id = NEW.sale_id
        )
        THEN RAISE(ABORT, 'credit sale requires an active customer account')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_receivable_after_sale_post
AFTER INSERT ON accounting_operational_links
WHEN NEW.source_type = 'sale'
 AND EXISTS
 (
     SELECT 1
     FROM sale_payments
     WHERE sale_id = NEW.source_id
       AND payment_method = 'credit'
 )
BEGIN
    INSERT OR IGNORE INTO finance_receivable_items
    (
        id,
        organization_id,
        shop_id,
        customer_id,
        sale_id,
        document_number,
        document_date,
        due_date,
        original_amount_minor,
        posting_journal_id,
        created_at_utc
    )
    SELECT
        'ar-sale-' || sale.id,
        NEW.organization_id,
        NEW.shop_id,
        sale.customer_id,
        sale.id,
        COALESCE(sale.invoice_number, sale.receipt_number),
        substr(COALESCE(sale.completed_at_utc, sale.created_at_utc), 1, 10),
        date(
            substr(COALESCE(sale.completed_at_utc, sale.created_at_utc), 1, 10),
            '+' || customer.payment_terms_days || ' days'),
        SUM(payment.amount_minor),
        NEW.posting_journal_id,
        NEW.posted_at_utc
    FROM sales AS sale
    INNER JOIN finance_customers AS customer
        ON customer.id = sale.customer_id
       AND customer.organization_id = NEW.organization_id
    INNER JOIN sale_payments AS payment
        ON payment.sale_id = sale.id
       AND payment.payment_method = 'credit'
    WHERE sale.id = NEW.source_id
    GROUP BY sale.id, customer.payment_terms_days;
END;

CREATE TRIGGER IF NOT EXISTS trg_payable_after_purchase_post
AFTER INSERT ON accounting_operational_links
WHEN NEW.source_type = 'purchase'
BEGIN
    INSERT OR IGNORE INTO finance_payable_items
    (
        id,
        organization_id,
        shop_id,
        supplier_id,
        purchase_id,
        document_number,
        supplier_invoice_number,
        document_date,
        due_date,
        original_amount_minor,
        posting_journal_id,
        created_at_utc
    )
    SELECT
        'ap-purchase-' || purchase.id,
        NEW.organization_id,
        NEW.shop_id,
        purchase.supplier_id,
        purchase.id,
        purchase.purchase_number,
        purchase.supplier_invoice_number,
        substr(COALESCE(purchase.received_at_utc, purchase.created_at_utc), 1, 10),
        date(
            substr(COALESCE(purchase.received_at_utc, purchase.created_at_utc), 1, 10),
            '+' || COALESCE(supplier.payment_terms_days, 30) || ' days'),
        purchase.total_minor,
        NEW.posting_journal_id,
        NEW.posted_at_utc
    FROM purchases AS purchase
    LEFT JOIN suppliers AS supplier
        ON supplier.id = purchase.supplier_id
       AND supplier.organization_id = NEW.organization_id
    WHERE purchase.id = NEW.source_id
      AND purchase.total_minor > 0;
END;

INSERT OR IGNORE INTO finance_receivable_items
(
    id,
    organization_id,
    shop_id,
    customer_id,
    sale_id,
    document_number,
    document_date,
    due_date,
    original_amount_minor,
    posting_journal_id,
    created_at_utc
)
SELECT
    'ar-sale-' || sale.id,
    link.organization_id,
    link.shop_id,
    sale.customer_id,
    sale.id,
    COALESCE(sale.invoice_number, sale.receipt_number),
    substr(COALESCE(sale.completed_at_utc, sale.created_at_utc), 1, 10),
    date(
        substr(COALESCE(sale.completed_at_utc, sale.created_at_utc), 1, 10),
        '+' || customer.payment_terms_days || ' days'),
    SUM(payment.amount_minor),
    link.posting_journal_id,
    link.posted_at_utc
FROM accounting_operational_links AS link
INNER JOIN sales AS sale
    ON link.source_type = 'sale'
   AND sale.id = link.source_id
INNER JOIN finance_customers AS customer
    ON customer.id = sale.customer_id
   AND customer.organization_id = link.organization_id
INNER JOIN sale_payments AS payment
    ON payment.sale_id = sale.id
   AND payment.payment_method = 'credit'
GROUP BY sale.id;

INSERT OR IGNORE INTO finance_payable_items
(
    id,
    organization_id,
    shop_id,
    supplier_id,
    purchase_id,
    document_number,
    supplier_invoice_number,
    document_date,
    due_date,
    original_amount_minor,
    posting_journal_id,
    created_at_utc
)
SELECT
    'ap-purchase-' || purchase.id,
    link.organization_id,
    link.shop_id,
    purchase.supplier_id,
    purchase.id,
    purchase.purchase_number,
    purchase.supplier_invoice_number,
    substr(COALESCE(purchase.received_at_utc, purchase.created_at_utc), 1, 10),
    date(
        substr(COALESCE(purchase.received_at_utc, purchase.created_at_utc), 1, 10),
        '+' || COALESCE(supplier.payment_terms_days, 30) || ' days'),
    purchase.total_minor,
    link.posting_journal_id,
    link.posted_at_utc
FROM accounting_operational_links AS link
INNER JOIN purchases AS purchase
    ON link.source_type = 'purchase'
   AND purchase.id = link.source_id
LEFT JOIN suppliers AS supplier
    ON supplier.id = purchase.supplier_id
   AND supplier.organization_id = link.organization_id
WHERE purchase.total_minor > 0;

CREATE TRIGGER IF NOT EXISTS trg_customer_receipt_insert_scope
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

CREATE TRIGGER IF NOT EXISTS trg_customer_receipt_allocation_insert
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

CREATE TRIGGER IF NOT EXISTS trg_customer_receipt_allocation_update
BEFORE UPDATE ON finance_customer_receipt_allocations
BEGIN
    SELECT RAISE(ABORT, 'customer receipt allocations are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_customer_receipt_allocation_delete
BEFORE DELETE ON finance_customer_receipt_allocations
BEGIN
    SELECT RAISE(ABORT, 'customer receipt allocations are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_customer_receipt_ownership_update
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

CREATE TRIGGER IF NOT EXISTS trg_customer_receipt_status
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

CREATE TRIGGER IF NOT EXISTS trg_customer_receipt_delete
BEFORE DELETE ON finance_customer_receipts
BEGIN
    SELECT RAISE(ABORT, 'customer receipts cannot be deleted');
END;

CREATE TRIGGER IF NOT EXISTS trg_supplier_payment_insert_scope
BEFORE INSERT ON finance_supplier_payments
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM shops AS shop
            INNER JOIN suppliers AS supplier
                ON supplier.id = NEW.supplier_id
               AND supplier.organization_id = shop.organization_id
               AND supplier.is_active = 1
            WHERE shop.id = NEW.shop_id
              AND shop.organization_id = NEW.organization_id
              AND shop.is_active = 1
        )
        THEN RAISE(ABORT, 'supplier payment scope is invalid')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_supplier_payment_allocation_insert
BEFORE INSERT ON finance_supplier_payment_allocations
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM finance_supplier_payments AS payment
            INNER JOIN finance_payable_items AS item
                ON item.id = NEW.payable_item_id
               AND item.organization_id = payment.organization_id
               AND item.shop_id = payment.shop_id
               AND item.supplier_id = payment.supplier_id
            INNER JOIN accounting_journals AS source_journal
                ON source_journal.id = item.posting_journal_id
               AND source_journal.status = 'posted'
            WHERE payment.id = NEW.payment_id
              AND payment.status = 'draft'
        )
        THEN RAISE(ABORT, 'supplier payment allocation scope is invalid')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_supplier_payment_allocation_update
BEFORE UPDATE ON finance_supplier_payment_allocations
BEGIN
    SELECT RAISE(ABORT, 'supplier payment allocations are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_supplier_payment_allocation_delete
BEFORE DELETE ON finance_supplier_payment_allocations
BEGIN
    SELECT RAISE(ABORT, 'supplier payment allocations are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_supplier_payment_ownership_update
BEFORE UPDATE ON finance_supplier_payments
BEGIN
    SELECT CASE
        WHEN NEW.organization_id <> OLD.organization_id
          OR NEW.shop_id <> OLD.shop_id
          OR NEW.supplier_id <> OLD.supplier_id
          OR NEW.payment_number <> OLD.payment_number
          OR NEW.payment_date <> OLD.payment_date
          OR NEW.payment_method <> OLD.payment_method
          OR NEW.amount_minor <> OLD.amount_minor
          OR NEW.reference <> OLD.reference
          OR NEW.notes <> OLD.notes
          OR NEW.created_by_user_id <> OLD.created_by_user_id
          OR NEW.created_at_utc <> OLD.created_at_utc
        THEN RAISE(ABORT, 'supplier payment ownership is immutable')
    END;

    SELECT CASE
        WHEN OLD.posting_journal_id IS NOT NULL
         AND COALESCE(NEW.posting_journal_id, '') <> OLD.posting_journal_id
        THEN RAISE(ABORT, 'supplier payment posting link is immutable')
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
        THEN RAISE(ABORT, 'supplier payment reversal link is immutable')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_supplier_payment_status
BEFORE UPDATE OF status ON finance_supplier_payments
WHEN NEW.status <> OLD.status
BEGIN
    SELECT CASE
        WHEN NOT
        (
            (OLD.status = 'draft' AND NEW.status = 'posted')
            OR
            (OLD.status = 'posted' AND NEW.status = 'reversed')
        )
        THEN RAISE(ABORT, 'invalid supplier payment status transition')
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
                 FROM finance_supplier_payment_allocations
                 WHERE payment_id = NEW.id
             ) <> NEW.amount_minor
             OR NOT EXISTS
             (
                 SELECT 1
                 FROM accounting_journals AS journal
                 WHERE journal.id = NEW.posting_journal_id
                   AND journal.organization_id = NEW.organization_id
                   AND journal.shop_id = NEW.shop_id
                   AND journal.source_type = 'system'
                   AND journal.source_id = 'supplier_payment:' || NEW.id
                   AND journal.status = 'posted'
                   AND journal.total_debit_minor = NEW.amount_minor
                   AND journal.total_credit_minor = NEW.amount_minor
             )
         )
        THEN RAISE(ABORT, 'posted supplier payment is incomplete')
    END;

    SELECT CASE
        WHEN NEW.status = 'posted'
         AND EXISTS
         (
             SELECT 1
             FROM finance_supplier_payment_allocations AS allocation
             INNER JOIN finance_payable_items AS item
                 ON item.id = allocation.payable_item_id
             WHERE allocation.payment_id = NEW.id
             GROUP BY allocation.payable_item_id, item.original_amount_minor
             HAVING SUM(allocation.amount_minor) >
                 item.original_amount_minor - COALESCE
                 (
                     (
                         SELECT SUM(other_allocation.amount_minor)
                         FROM finance_supplier_payment_allocations AS other_allocation
                         INNER JOIN finance_supplier_payments AS other_payment
                             ON other_payment.id = other_allocation.payment_id
                         WHERE other_allocation.payable_item_id = allocation.payable_item_id
                           AND other_payment.status = 'posted'
                           AND other_payment.id <> NEW.id
                     ),
                     0
                 )
         )
        THEN RAISE(ABORT, 'supplier payment allocation exceeds outstanding payable')
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
        THEN RAISE(ABORT, 'reversed supplier payment is incomplete')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_supplier_payment_delete
BEFORE DELETE ON finance_supplier_payments
BEGIN
    SELECT RAISE(ABORT, 'supplier payments cannot be deleted');
END;

CREATE TRIGGER IF NOT EXISTS trg_receivable_item_update
BEFORE UPDATE ON finance_receivable_items
BEGIN
    SELECT RAISE(ABORT, 'receivable open items are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_receivable_item_delete
BEFORE DELETE ON finance_receivable_items
BEGIN
    SELECT RAISE(ABORT, 'receivable open items cannot be deleted');
END;

CREATE TRIGGER IF NOT EXISTS trg_payable_item_update
BEFORE UPDATE ON finance_payable_items
BEGIN
    SELECT RAISE(ABORT, 'payable open items are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_payable_item_delete
BEFORE DELETE ON finance_payable_items
BEGIN
    SELECT RAISE(ABORT, 'payable open items cannot be deleted');
END;

DROP VIEW IF EXISTS finance_cashbook_entries;

CREATE VIEW finance_cashbook_entries AS
SELECT
    journal.organization_id,
    journal.shop_id,
    shop.code AS shop_code,
    journal.id AS journal_id,
    journal.journal_number,
    journal.journal_date,
    journal.description AS journal_description,
    journal.source_type,
    journal.source_id,
    journal.status AS journal_status,
    line.id AS journal_line_id,
    account.id AS account_id,
    account.code AS account_code,
    account.name AS account_name,
    account.system_key,
    line.debit_minor,
    line.credit_minor,
    line.debit_minor - line.credit_minor AS signed_amount_minor,
    CASE
        WHEN line.debit_minor > 0 THEN 'receipt'
        ELSE 'payment'
    END AS direction,
    line.description AS line_description,
    line.counterparty_type,
    line.counterparty_id,
    journal.posted_at_utc
FROM accounting_journal_lines AS line
INNER JOIN accounting_journals AS journal
    ON journal.id = line.journal_id
INNER JOIN accounting_accounts AS account
    ON account.id = line.account_id
INNER JOIN shops AS shop
    ON shop.id = journal.shop_id
WHERE journal.status IN ('posted', 'reversed')
  AND account.system_key IN
  (
      'cash_on_hand',
      'mobile_money_clearing',
      'card_clearing',
      'bank_account',
      'other_payment_clearing'
  );

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    12,
    'Customer and supplier accounts, receivables, payables, settlements, ageing, statements and cashbook',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);