ALTER TABLE purchases
ADD COLUMN shop_id TEXT NOT NULL DEFAULT 'main-shop';

ALTER TABLE expenses
ADD COLUMN shop_id TEXT NOT NULL DEFAULT 'main-shop';

CREATE INDEX IF NOT EXISTS ix_purchases_shop_received
    ON purchases(shop_id, received_at_utc, status);

CREATE INDEX IF NOT EXISTS ix_expenses_shop_date
    ON expenses(shop_id, expense_date, voided_at_utc);

CREATE TABLE IF NOT EXISTS accounting_operational_links
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    source_type              TEXT NOT NULL
                             CHECK (source_type IN ('sale', 'purchase', 'expense')),
    source_id                TEXT NOT NULL,
    posting_journal_id       TEXT NOT NULL,
    reversal_journal_id      TEXT NULL,
    posted_at_utc            TEXT NOT NULL,
    reversed_at_utc          TEXT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (posting_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (reversal_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    UNIQUE (organization_id, source_type, source_id),
    UNIQUE (posting_journal_id),
    UNIQUE (reversal_journal_id)
);

CREATE INDEX IF NOT EXISTS ix_accounting_operational_links_shop
    ON accounting_operational_links(organization_id, shop_id, source_type, posted_at_utc);

INSERT OR IGNORE INTO accounting_accounts
(
    id,
    organization_id,
    code,
    name,
    account_type,
    normal_balance,
    system_key,
    allow_manual_posting,
    is_active,
    version,
    created_at_utc,
    updated_at_utc
)
SELECT
    lower(hex(randomblob(16))),
    id,
    '1030',
    'Card Clearing',
    'asset',
    'debit',
    'card_clearing',
    0,
    1,
    1,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id,
    organization_id,
    code,
    name,
    account_type,
    normal_balance,
    system_key,
    allow_manual_posting,
    is_active,
    version,
    created_at_utc,
    updated_at_utc
)
SELECT
    lower(hex(randomblob(16))),
    id,
    '1040',
    'Other Payment Clearing',
    'asset',
    'debit',
    'other_payment_clearing',
    0,
    1,
    1,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

CREATE TRIGGER IF NOT EXISTS trg_purchase_shop_scope_insert
BEFORE INSERT ON purchases
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM shops
            WHERE id = NEW.shop_id
              AND is_active = 1
        )
        THEN RAISE(ABORT, 'purchase requires an active shop')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_expense_shop_scope_insert
BEFORE INSERT ON expenses
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM shops
            WHERE id = NEW.shop_id
              AND is_active = 1
        )
        THEN RAISE(ABORT, 'expense requires an active shop')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_operational_link_insert
BEFORE INSERT ON accounting_operational_links
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM shops AS shop
            WHERE shop.id = NEW.shop_id
              AND shop.organization_id = NEW.organization_id
        )
        THEN RAISE(ABORT, 'operational accounting link has invalid organization or shop scope')
    END;

    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM accounting_journals AS journal
            WHERE journal.id = NEW.posting_journal_id
              AND journal.organization_id = NEW.organization_id
              AND journal.shop_id = NEW.shop_id
              AND journal.source_type = 'system'
              AND journal.source_id = NEW.source_type || ':' || NEW.source_id
              AND journal.status IN ('posted', 'reversed')
        )
        THEN RAISE(ABORT, 'operational posting journal is invalid')
    END;

    SELECT CASE
        WHEN NEW.reversal_journal_id IS NOT NULL
         AND NOT EXISTS
        (
            SELECT 1
            FROM accounting_journals AS reversal
            WHERE reversal.id = NEW.reversal_journal_id
              AND reversal.organization_id = NEW.organization_id
              AND reversal.shop_id = NEW.shop_id
              AND reversal.source_type = 'reversal'
              AND reversal.reversal_of_journal_id = NEW.posting_journal_id
              AND reversal.status = 'posted'
        )
        THEN RAISE(ABORT, 'operational reversal journal is invalid')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_operational_link_update
BEFORE UPDATE ON accounting_operational_links
BEGIN
    SELECT CASE
        WHEN NEW.organization_id <> OLD.organization_id
          OR NEW.shop_id <> OLD.shop_id
          OR NEW.source_type <> OLD.source_type
          OR NEW.source_id <> OLD.source_id
          OR NEW.posting_journal_id <> OLD.posting_journal_id
          OR NEW.posted_at_utc <> OLD.posted_at_utc
        THEN RAISE(ABORT, 'operational accounting posting ownership is immutable')
    END;

    SELECT CASE
        WHEN OLD.reversal_journal_id IS NOT NULL
         AND
        (
            NEW.reversal_journal_id <> OLD.reversal_journal_id
            OR COALESCE(NEW.reversed_at_utc, '') <> COALESCE(OLD.reversed_at_utc, '')
        )
        THEN RAISE(ABORT, 'operational accounting reversal link is immutable')
    END;

    SELECT CASE
        WHEN NEW.reversal_journal_id IS NOT NULL
         AND NOT EXISTS
        (
            SELECT 1
            FROM accounting_journals AS reversal
            WHERE reversal.id = NEW.reversal_journal_id
              AND reversal.organization_id = NEW.organization_id
              AND reversal.shop_id = NEW.shop_id
              AND reversal.source_type = 'reversal'
              AND reversal.reversal_of_journal_id = NEW.posting_journal_id
              AND reversal.status = 'posted'
        )
        THEN RAISE(ABORT, 'operational reversal journal is invalid')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_operational_link_delete
BEFORE DELETE ON accounting_operational_links
BEGIN
    SELECT RAISE(ABORT, 'operational accounting links are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_payment_overposting_guard
BEFORE INSERT ON sale_payments
BEGIN
    SELECT CASE
        WHEN
        (
            SELECT COALESCE(SUM(amount_minor), 0)
            FROM sale_payments
            WHERE sale_id = NEW.sale_id
        ) + NEW.amount_minor >
        (
            SELECT total_minor
            FROM sales
            WHERE id = NEW.sale_id
        )
        THEN RAISE(ABORT, 'sale payments exceed the sale total')
    END;

    SELECT CASE
        WHEN EXISTS
        (
            SELECT 1
            FROM accounting_operational_links
            WHERE source_type = 'sale'
              AND source_id = NEW.sale_id
        )
        THEN RAISE(ABORT, 'posted sale payments are immutable')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_payment_update_guard
BEFORE UPDATE ON sale_payments
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'sale'
      AND source_id = OLD.sale_id
)
BEGIN
    SELECT RAISE(ABORT, 'posted sale payments are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_payment_delete_guard
BEFORE DELETE ON sale_payments
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'sale'
      AND source_id = OLD.sale_id
)
BEGIN
    SELECT RAISE(ABORT, 'posted sale payments are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_item_update_guard
BEFORE UPDATE ON sale_items
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'sale'
      AND source_id = OLD.sale_id
)
BEGIN
    SELECT RAISE(ABORT, 'posted sale items are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_item_delete_guard
BEFORE DELETE ON sale_items
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'sale'
      AND source_id = OLD.sale_id
)
BEGIN
    SELECT RAISE(ABORT, 'posted sale items are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_financial_update_guard
BEFORE UPDATE OF
    shop_id,
    subtotal_minor,
    discount_minor,
    total_minor,
    amount_received_minor,
    change_minor,
    completed_at_utc
ON sales
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'sale'
      AND source_id = OLD.id
)
BEGIN
    SELECT RAISE(ABORT, 'posted sale financial values are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_purchase_item_insert_guard
BEFORE INSERT ON purchase_items
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'purchase'
      AND source_id = NEW.purchase_id
)
BEGIN
    SELECT RAISE(ABORT, 'posted purchase items are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_purchase_item_update_guard
BEFORE UPDATE ON purchase_items
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'purchase'
      AND source_id = OLD.purchase_id
)
BEGIN
    SELECT RAISE(ABORT, 'posted purchase items are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_purchase_item_delete_guard
BEFORE DELETE ON purchase_items
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'purchase'
      AND source_id = OLD.purchase_id
)
BEGIN
    SELECT RAISE(ABORT, 'posted purchase items are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_purchase_financial_update_guard
BEFORE UPDATE OF
    shop_id,
    supplier_id,
    status,
    subtotal_minor,
    total_minor,
    received_at_utc
ON purchases
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'purchase'
      AND source_id = OLD.id
)
BEGIN
    SELECT RAISE(ABORT, 'posted purchase financial values are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_expense_financial_update_guard
BEFORE UPDATE OF
    shop_id,
    category,
    description,
    amount_minor,
    payment_method,
    expense_date,
    recorded_by_user_id,
    created_at_utc
ON expenses
WHEN EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'expense'
      AND source_id = OLD.id
)
BEGIN
    SELECT RAISE(ABORT, 'posted expense financial values are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_accounting_post
AFTER INSERT ON sale_payments
WHEN
(
    SELECT status
    FROM sales
    WHERE id = NEW.sale_id
) = 'completed'
AND
(
    SELECT COALESCE(SUM(amount_minor), 0)
    FROM sale_payments
    WHERE sale_id = NEW.sale_id
) =
(
    SELECT total_minor
    FROM sales
    WHERE id = NEW.sale_id
)
AND NOT EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'sale'
      AND source_id = NEW.sale_id
)
BEGIN
    INSERT INTO accounting_journals
    (
        id,
        organization_id,
        shop_id,
        journal_number,
        journal_date,
        currency_code,
        description,
        source_type,
        source_id,
        status,
        total_debit_minor,
        total_credit_minor,
        version,
        created_by_user_id,
        created_at_utc,
        updated_at_utc
    )
    SELECT
        'sys-sale-' || sale.id,
        shop.organization_id,
        shop.id,
        'SYS-' || sale.receipt_number,
        substr(COALESCE(sale.completed_at_utc, sale.created_at_utc), 1, 10),
        shop.currency_code,
        'Automatic posting for sale ' || sale.receipt_number,
        'system',
        'sale:' || sale.id,
        'draft',
        sale.total_minor + COALESCE
        (
            (
                SELECT SUM(item.unit_cost_minor * item.quantity)
                FROM sale_items AS item
                WHERE item.sale_id = sale.id
            ),
            0
        ),
        sale.total_minor + COALESCE
        (
            (
                SELECT SUM(item.unit_cost_minor * item.quantity)
                FROM sale_items AS item
                WHERE item.sale_id = sale.id
            ),
            0
        ),
        1,
        sale.teller_user_id,
        COALESCE(sale.completed_at_utc, sale.created_at_utc),
        COALESCE(sale.completed_at_utc, sale.created_at_utc)
    FROM sales AS sale
    INNER JOIN shops AS shop
        ON shop.id = COALESCE(sale.shop_id, 'main-shop')
    WHERE sale.id = NEW.sale_id;

    INSERT INTO accounting_journal_lines
    (
        journal_id,
        line_number,
        account_id,
        shop_id,
        debit_minor,
        credit_minor,
        description
    )
    SELECT
        'sys-sale-' || payment.sale_id,
        CASE payment.payment_method
            WHEN 'cash' THEN 1
            WHEN 'mobile_money' THEN 2
            WHEN 'card' THEN 3
            WHEN 'bank' THEN 4
            WHEN 'credit' THEN 5
        END,
        account.id,
        COALESCE(sale.shop_id, 'main-shop'),
        SUM(payment.amount_minor),
        0,
        'Sale receipt through ' || payment.payment_method
    FROM sale_payments AS payment
    INNER JOIN sales AS sale
        ON sale.id = payment.sale_id
    INNER JOIN shops AS shop
        ON shop.id = COALESCE(sale.shop_id, 'main-shop')
    INNER JOIN accounting_accounts AS account
        ON account.organization_id = shop.organization_id
       AND account.system_key = CASE payment.payment_method
            WHEN 'cash' THEN 'cash_on_hand'
            WHEN 'mobile_money' THEN 'mobile_money_clearing'
            WHEN 'card' THEN 'card_clearing'
            WHEN 'bank' THEN 'bank_account'
            WHEN 'credit' THEN 'accounts_receivable'
        END
    WHERE payment.sale_id = NEW.sale_id
    GROUP BY payment.sale_id, payment.payment_method, account.id, sale.shop_id;

    INSERT INTO accounting_journal_lines
    (
        journal_id,
        line_number,
        account_id,
        shop_id,
        debit_minor,
        credit_minor,
        description
    )
    SELECT
        'sys-sale-' || sale.id,
        10,
        revenue.id,
        shop.id,
        0,
        sale.total_minor,
        'Sales revenue for ' || sale.receipt_number
    FROM sales AS sale
    INNER JOIN shops AS shop
        ON shop.id = COALESCE(sale.shop_id, 'main-shop')
    INNER JOIN accounting_accounts AS revenue
        ON revenue.organization_id = shop.organization_id
       AND revenue.system_key = 'sales_revenue'
    WHERE sale.id = NEW.sale_id;

    INSERT INTO accounting_journal_lines
    (
        journal_id,
        line_number,
        account_id,
        shop_id,
        debit_minor,
        credit_minor,
        description
    )
    SELECT
        'sys-sale-' || sale.id,
        20,
        cogs.id,
        shop.id,
        cost.total_cost_minor,
        0,
        'Cost of goods sold for ' || sale.receipt_number
    FROM sales AS sale
    INNER JOIN shops AS shop
        ON shop.id = COALESCE(sale.shop_id, 'main-shop')
    INNER JOIN
    (
        SELECT sale_id, SUM(unit_cost_minor * quantity) AS total_cost_minor
        FROM sale_items
        WHERE sale_id = NEW.sale_id
        GROUP BY sale_id
    ) AS cost
        ON cost.sale_id = sale.id
       AND cost.total_cost_minor > 0
    INNER JOIN accounting_accounts AS cogs
        ON cogs.organization_id = shop.organization_id
       AND cogs.system_key = 'cost_of_goods_sold'
    WHERE sale.id = NEW.sale_id;

    INSERT INTO accounting_journal_lines
    (
        journal_id,
        line_number,
        account_id,
        shop_id,
        debit_minor,
        credit_minor,
        description
    )
    SELECT
        'sys-sale-' || sale.id,
        21,
        inventory.id,
        shop.id,
        0,
        cost.total_cost_minor,
        'Inventory issued for ' || sale.receipt_number
    FROM sales AS sale
    INNER JOIN shops AS shop
        ON shop.id = COALESCE(sale.shop_id, 'main-shop')
    INNER JOIN
    (
        SELECT sale_id, SUM(unit_cost_minor * quantity) AS total_cost_minor
        FROM sale_items
        WHERE sale_id = NEW.sale_id
        GROUP BY sale_id
    ) AS cost
        ON cost.sale_id = sale.id
       AND cost.total_cost_minor > 0
    INNER JOIN accounting_accounts AS inventory
        ON inventory.organization_id = shop.organization_id
       AND inventory.system_key = 'inventory'
    WHERE sale.id = NEW.sale_id;

    UPDATE accounting_journals
    SET status = 'posted',
        posted_by_user_id =
        (
            SELECT teller_user_id
            FROM sales
            WHERE id = NEW.sale_id
        ),
        posted_at_utc =
        (
            SELECT COALESCE(completed_at_utc, created_at_utc)
            FROM sales
            WHERE id = NEW.sale_id
        ),
        updated_at_utc =
        (
            SELECT COALESCE(completed_at_utc, created_at_utc)
            FROM sales
            WHERE id = NEW.sale_id
        ),
        version = version + 1
    WHERE id = 'sys-sale-' || NEW.sale_id
      AND status = 'draft';

    INSERT INTO accounting_operational_links
    (
        organization_id,
        shop_id,
        source_type,
        source_id,
        posting_journal_id,
        posted_at_utc
    )
    SELECT
        shop.organization_id,
        shop.id,
        'sale',
        sale.id,
        'sys-sale-' || sale.id,
        COALESCE(sale.completed_at_utc, sale.created_at_utc)
    FROM sales AS sale
    INNER JOIN shops AS shop
        ON shop.id = COALESCE(sale.shop_id, 'main-shop')
    WHERE sale.id = NEW.sale_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_purchase_accounting_post
AFTER UPDATE OF total_minor ON purchases
WHEN NEW.status = 'received'
 AND NEW.total_minor > 0
 AND OLD.total_minor <> NEW.total_minor
 AND NOT EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'purchase'
      AND source_id = NEW.id
)
BEGIN
    INSERT INTO accounting_journals
    (
        id,
        organization_id,
        shop_id,
        journal_number,
        journal_date,
        currency_code,
        description,
        source_type,
        source_id,
        status,
        total_debit_minor,
        total_credit_minor,
        version,
        created_by_user_id,
        created_at_utc,
        updated_at_utc
    )
    SELECT
        'sys-purchase-' || NEW.id,
        shop.organization_id,
        shop.id,
        'SYS-' || NEW.purchase_number,
        substr(COALESCE(NEW.received_at_utc, NEW.created_at_utc), 1, 10),
        shop.currency_code,
        'Automatic posting for purchase ' || NEW.purchase_number,
        'system',
        'purchase:' || NEW.id,
        'draft',
        NEW.total_minor,
        NEW.total_minor,
        1,
        NEW.received_by_user_id,
        COALESCE(NEW.received_at_utc, NEW.created_at_utc),
        COALESCE(NEW.received_at_utc, NEW.created_at_utc)
    FROM shops AS shop
    WHERE shop.id = NEW.shop_id;

    INSERT INTO accounting_journal_lines
    (
        journal_id,
        line_number,
        account_id,
        shop_id,
        debit_minor,
        credit_minor,
        description,
        counterparty_type,
        counterparty_id
    )
    SELECT
        'sys-purchase-' || NEW.id,
        1,
        inventory.id,
        NEW.shop_id,
        NEW.total_minor,
        0,
        'Inventory received on ' || NEW.purchase_number,
        CASE WHEN NEW.supplier_id IS NULL THEN NULL ELSE 'supplier' END,
        NEW.supplier_id
    FROM shops AS shop
    INNER JOIN accounting_accounts AS inventory
        ON inventory.organization_id = shop.organization_id
       AND inventory.system_key = 'inventory'
    WHERE shop.id = NEW.shop_id;

    INSERT INTO accounting_journal_lines
    (
        journal_id,
        line_number,
        account_id,
        shop_id,
        debit_minor,
        credit_minor,
        description,
        counterparty_type,
        counterparty_id
    )
    SELECT
        'sys-purchase-' || NEW.id,
        2,
        payable.id,
        NEW.shop_id,
        0,
        NEW.total_minor,
        'Supplier payable for ' || NEW.purchase_number,
        CASE WHEN NEW.supplier_id IS NULL THEN NULL ELSE 'supplier' END,
        NEW.supplier_id
    FROM shops AS shop
    INNER JOIN accounting_accounts AS payable
        ON payable.organization_id = shop.organization_id
       AND payable.system_key = 'accounts_payable'
    WHERE shop.id = NEW.shop_id;

    UPDATE accounting_journals
    SET status = 'posted',
        posted_by_user_id = NEW.received_by_user_id,
        posted_at_utc = COALESCE(NEW.received_at_utc, NEW.created_at_utc),
        updated_at_utc = COALESCE(NEW.received_at_utc, NEW.created_at_utc),
        version = version + 1
    WHERE id = 'sys-purchase-' || NEW.id
      AND status = 'draft';

    INSERT INTO accounting_operational_links
    (
        organization_id,
        shop_id,
        source_type,
        source_id,
        posting_journal_id,
        posted_at_utc
    )
    SELECT
        shop.organization_id,
        NEW.shop_id,
        'purchase',
        NEW.id,
        'sys-purchase-' || NEW.id,
        COALESCE(NEW.received_at_utc, NEW.created_at_utc)
    FROM shops AS shop
    WHERE shop.id = NEW.shop_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_expense_accounting_post
AFTER INSERT ON expenses
WHEN NOT EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'expense'
      AND source_id = NEW.id
)
BEGIN
    INSERT INTO accounting_journals
    (
        id,
        organization_id,
        shop_id,
        journal_number,
        journal_date,
        currency_code,
        description,
        source_type,
        source_id,
        status,
        total_debit_minor,
        total_credit_minor,
        version,
        created_by_user_id,
        created_at_utc,
        updated_at_utc
    )
    SELECT
        'sys-expense-' || NEW.id,
        shop.organization_id,
        shop.id,
        'SYS-' || NEW.expense_number,
        NEW.expense_date,
        shop.currency_code,
        'Automatic posting for expense ' || NEW.expense_number,
        'system',
        'expense:' || NEW.id,
        'draft',
        NEW.amount_minor,
        NEW.amount_minor,
        1,
        NEW.recorded_by_user_id,
        NEW.created_at_utc,
        NEW.created_at_utc
    FROM shops AS shop
    WHERE shop.id = NEW.shop_id;

    INSERT INTO accounting_journal_lines
    (
        journal_id,
        line_number,
        account_id,
        shop_id,
        debit_minor,
        credit_minor,
        description
    )
    SELECT
        'sys-expense-' || NEW.id,
        1,
        expense_account.id,
        NEW.shop_id,
        NEW.amount_minor,
        0,
        NEW.category || ': ' || NEW.description
    FROM shops AS shop
    INNER JOIN accounting_accounts AS expense_account
        ON expense_account.organization_id = shop.organization_id
       AND expense_account.system_key = 'operating_expenses'
    WHERE shop.id = NEW.shop_id;

    INSERT INTO accounting_journal_lines
    (
        journal_id,
        line_number,
        account_id,
        shop_id,
        debit_minor,
        credit_minor,
        description
    )
    SELECT
        'sys-expense-' || NEW.id,
        2,
        payment_account.id,
        NEW.shop_id,
        0,
        NEW.amount_minor,
        'Expense payment through ' || NEW.payment_method
    FROM shops AS shop
    INNER JOIN accounting_accounts AS payment_account
        ON payment_account.organization_id = shop.organization_id
       AND payment_account.system_key = CASE NEW.payment_method
            WHEN 'cash' THEN 'cash_on_hand'
            WHEN 'mobile_money' THEN 'mobile_money_clearing'
            WHEN 'bank' THEN 'bank_account'
            ELSE 'other_payment_clearing'
        END
    WHERE shop.id = NEW.shop_id;

    UPDATE accounting_journals
    SET status = 'posted',
        posted_by_user_id = NEW.recorded_by_user_id,
        posted_at_utc = NEW.created_at_utc,
        updated_at_utc = NEW.created_at_utc,
        version = version + 1
    WHERE id = 'sys-expense-' || NEW.id
      AND status = 'draft';

    INSERT INTO accounting_operational_links
    (
        organization_id,
        shop_id,
        source_type,
        source_id,
        posting_journal_id,
        posted_at_utc
    )
    SELECT
        shop.organization_id,
        NEW.shop_id,
        'expense',
        NEW.id,
        'sys-expense-' || NEW.id,
        NEW.created_at_utc
    FROM shops AS shop
    WHERE shop.id = NEW.shop_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_sale_accounting_reverse
AFTER UPDATE OF status ON sales
WHEN OLD.status = 'completed'
 AND NEW.status = 'voided'
 AND EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'sale'
      AND source_id = NEW.id
      AND reversal_journal_id IS NULL
)
BEGIN
    INSERT INTO accounting_journals
    (
        id,
        organization_id,
        shop_id,
        journal_number,
        journal_date,
        currency_code,
        description,
        source_type,
        source_id,
        status,
        reversal_of_journal_id,
        total_debit_minor,
        total_credit_minor,
        version,
        created_by_user_id,
        created_at_utc,
        updated_at_utc
    )
    SELECT
        'sys-sale-reversal-' || NEW.id,
        original.organization_id,
        original.shop_id,
        'SYS-VOID-' || NEW.receipt_number,
        substr(NEW.voided_at_utc, 1, 10),
        original.currency_code,
        'Automatic reversal of ' || original.journal_number || ': ' || COALESCE(NEW.void_reason, 'Sale void'),
        'reversal',
        original.id,
        'draft',
        original.id,
        original.total_debit_minor,
        original.total_credit_minor,
        1,
        NEW.voided_by_user_id,
        NEW.voided_at_utc,
        NEW.voided_at_utc
    FROM accounting_operational_links AS link
    INNER JOIN accounting_journals AS original
        ON original.id = link.posting_journal_id
    WHERE link.source_type = 'sale'
      AND link.source_id = NEW.id;

    INSERT INTO accounting_journal_lines
    (
        journal_id,
        line_number,
        account_id,
        shop_id,
        debit_minor,
        credit_minor,
        description,
        counterparty_type,
        counterparty_id
    )
    SELECT
        'sys-sale-reversal-' || NEW.id,
        line.line_number,
        line.account_id,
        line.shop_id,
        line.credit_minor,
        line.debit_minor,
        'Reversal: ' || line.description,
        line.counterparty_type,
        line.counterparty_id
    FROM accounting_operational_links AS link
    INNER JOIN accounting_journal_lines AS line
        ON line.journal_id = link.posting_journal_id
    WHERE link.source_type = 'sale'
      AND link.source_id = NEW.id;

    UPDATE accounting_journals
    SET status = 'posted',
        posted_by_user_id = NEW.voided_by_user_id,
        posted_at_utc = NEW.voided_at_utc,
        updated_at_utc = NEW.voided_at_utc,
        version = version + 1
    WHERE id = 'sys-sale-reversal-' || NEW.id
      AND status = 'draft';

    UPDATE accounting_journals
    SET status = 'reversed',
        reversed_by_journal_id = 'sys-sale-reversal-' || NEW.id,
        updated_at_utc = NEW.voided_at_utc,
        version = version + 1
    WHERE id =
    (
        SELECT posting_journal_id
        FROM accounting_operational_links
        WHERE source_type = 'sale'
          AND source_id = NEW.id
    )
      AND status = 'posted';

    UPDATE accounting_operational_links
    SET reversal_journal_id = 'sys-sale-reversal-' || NEW.id,
        reversed_at_utc = NEW.voided_at_utc
    WHERE source_type = 'sale'
      AND source_id = NEW.id
      AND reversal_journal_id IS NULL;
END;

CREATE TRIGGER IF NOT EXISTS trg_expense_accounting_reverse
AFTER UPDATE OF voided_at_utc ON expenses
WHEN OLD.voided_at_utc IS NULL
 AND NEW.voided_at_utc IS NOT NULL
 AND EXISTS
(
    SELECT 1
    FROM accounting_operational_links
    WHERE source_type = 'expense'
      AND source_id = NEW.id
      AND reversal_journal_id IS NULL
)
BEGIN
    INSERT INTO accounting_journals
    (
        id,
        organization_id,
        shop_id,
        journal_number,
        journal_date,
        currency_code,
        description,
        source_type,
        source_id,
        status,
        reversal_of_journal_id,
        total_debit_minor,
        total_credit_minor,
        version,
        created_by_user_id,
        created_at_utc,
        updated_at_utc
    )
    SELECT
        'sys-expense-reversal-' || NEW.id,
        original.organization_id,
        original.shop_id,
        'SYS-VOID-' || NEW.expense_number,
        substr(NEW.voided_at_utc, 1, 10),
        original.currency_code,
        'Automatic reversal of ' || original.journal_number || ': ' || COALESCE(NEW.void_reason, 'Expense void'),
        'reversal',
        original.id,
        'draft',
        original.id,
        original.total_debit_minor,
        original.total_credit_minor,
        1,
        NEW.voided_by_user_id,
        NEW.voided_at_utc,
        NEW.voided_at_utc
    FROM accounting_operational_links AS link
    INNER JOIN accounting_journals AS original
        ON original.id = link.posting_journal_id
    WHERE link.source_type = 'expense'
      AND link.source_id = NEW.id;

    INSERT INTO accounting_journal_lines
    (
        journal_id,
        line_number,
        account_id,
        shop_id,
        debit_minor,
        credit_minor,
        description,
        counterparty_type,
        counterparty_id
    )
    SELECT
        'sys-expense-reversal-' || NEW.id,
        line.line_number,
        line.account_id,
        line.shop_id,
        line.credit_minor,
        line.debit_minor,
        'Reversal: ' || line.description,
        line.counterparty_type,
        line.counterparty_id
    FROM accounting_operational_links AS link
    INNER JOIN accounting_journal_lines AS line
        ON line.journal_id = link.posting_journal_id
    WHERE link.source_type = 'expense'
      AND link.source_id = NEW.id;

    UPDATE accounting_journals
    SET status = 'posted',
        posted_by_user_id = NEW.voided_by_user_id,
        posted_at_utc = NEW.voided_at_utc,
        updated_at_utc = NEW.voided_at_utc,
        version = version + 1
    WHERE id = 'sys-expense-reversal-' || NEW.id
      AND status = 'draft';

    UPDATE accounting_journals
    SET status = 'reversed',
        reversed_by_journal_id = 'sys-expense-reversal-' || NEW.id,
        updated_at_utc = NEW.voided_at_utc,
        version = version + 1
    WHERE id =
    (
        SELECT posting_journal_id
        FROM accounting_operational_links
        WHERE source_type = 'expense'
          AND source_id = NEW.id
    )
      AND status = 'posted';

    UPDATE accounting_operational_links
    SET reversal_journal_id = 'sys-expense-reversal-' || NEW.id,
        reversed_at_utc = NEW.voided_at_utc
    WHERE source_type = 'expense'
      AND source_id = NEW.id
      AND reversal_journal_id IS NULL;
END;

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    11,
    'Atomic operational accounting for sales, purchases, expenses and audited void reversals',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);