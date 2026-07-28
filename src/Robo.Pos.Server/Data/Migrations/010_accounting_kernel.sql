CREATE TABLE IF NOT EXISTS accounting_accounts
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    code                     TEXT NOT NULL COLLATE NOCASE,
    name                     TEXT NOT NULL,
    account_type             TEXT NOT NULL
                             CHECK (account_type IN ('asset', 'liability', 'equity', 'income', 'expense')),
    normal_balance           TEXT NOT NULL
                             CHECK (normal_balance IN ('debit', 'credit')),
    parent_account_id        TEXT NULL,
    system_key               TEXT NULL,
    allow_manual_posting     INTEGER NOT NULL DEFAULT 1
                             CHECK (allow_manual_posting IN (0, 1)),
    is_active                INTEGER NOT NULL DEFAULT 1
                             CHECK (is_active IN (0, 1)),
    version                  INTEGER NOT NULL DEFAULT 1
                             CHECK (version >= 1),
    created_by_user_id       TEXT NULL,
    updated_by_user_id       TEXT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (parent_account_id)
        REFERENCES accounting_accounts(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL,

    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL,

    UNIQUE (organization_id, code),
    UNIQUE (organization_id, system_key)
);

CREATE INDEX IF NOT EXISTS ix_accounting_accounts_org_type
    ON accounting_accounts(organization_id, account_type, is_active, code);

CREATE TABLE IF NOT EXISTS accounting_periods
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    name                     TEXT NOT NULL,
    start_date               TEXT NOT NULL
                             CHECK (length(start_date) = 10 AND date(start_date) = start_date),
    end_date                 TEXT NOT NULL
                             CHECK (length(end_date) = 10 AND date(end_date) = end_date),
    status                   TEXT NOT NULL DEFAULT 'open'
                             CHECK (status IN ('open', 'closed')),
    version                  INTEGER NOT NULL DEFAULT 1
                             CHECK (version >= 1),
    created_by_user_id       TEXT NULL,
    closed_by_user_id        TEXT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,
    closed_at_utc            TEXT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL,

    FOREIGN KEY (closed_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    CHECK (start_date <= end_date),
    UNIQUE (organization_id, start_date, end_date)
);

CREATE INDEX IF NOT EXISTS ix_accounting_periods_org_dates
    ON accounting_periods(organization_id, start_date, end_date, status);

CREATE TABLE IF NOT EXISTS accounting_journal_sequences
(
    organization_id          TEXT PRIMARY KEY,
    next_value               INTEGER NOT NULL DEFAULT 1
                             CHECK (next_value >= 1),
    updated_at_utc           TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS accounting_journals
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    journal_number           TEXT NOT NULL,
    journal_date             TEXT NOT NULL
                             CHECK (length(journal_date) = 10 AND date(journal_date) = journal_date),
    currency_code            TEXT NOT NULL,
    description              TEXT NOT NULL DEFAULT '',
    source_type              TEXT NOT NULL DEFAULT 'manual'
                             CHECK (source_type IN ('manual', 'opening', 'reversal', 'system')),
    source_id                TEXT NULL,
    status                   TEXT NOT NULL DEFAULT 'draft'
                             CHECK (status IN ('draft', 'posted', 'reversed')),
    reversal_of_journal_id   TEXT NULL,
    reversed_by_journal_id   TEXT NULL,
    total_debit_minor        INTEGER NOT NULL DEFAULT 0
                             CHECK (total_debit_minor >= 0),
    total_credit_minor       INTEGER NOT NULL DEFAULT 0
                             CHECK (total_credit_minor >= 0),
    version                  INTEGER NOT NULL DEFAULT 1
                             CHECK (version >= 1),
    created_by_user_id       TEXT NOT NULL,
    posted_by_user_id        TEXT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,
    posted_at_utc            TEXT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (reversal_of_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (reversed_by_journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (posted_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    CHECK (
        (source_type = 'reversal' AND reversal_of_journal_id IS NOT NULL)
        OR
        (source_type <> 'reversal' AND reversal_of_journal_id IS NULL)
    ),

    UNIQUE (organization_id, journal_number)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_accounting_journal_source
    ON accounting_journals(organization_id, source_type, source_id)
    WHERE source_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_accounting_journals_org_shop_date
    ON accounting_journals(organization_id, shop_id, journal_date, status);

CREATE TABLE IF NOT EXISTS accounting_journal_lines
(
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    journal_id               TEXT NOT NULL,
    line_number              INTEGER NOT NULL CHECK (line_number >= 1),
    account_id               TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    debit_minor              INTEGER NOT NULL DEFAULT 0
                             CHECK (debit_minor >= 0),
    credit_minor             INTEGER NOT NULL DEFAULT 0
                             CHECK (credit_minor >= 0),
    description              TEXT NOT NULL DEFAULT '',
    counterparty_type        TEXT NULL
                             CHECK (
                                 counterparty_type IS NULL OR
                                 counterparty_type IN ('customer', 'supplier', 'employee', 'other')
                             ),
    counterparty_id          TEXT NULL,

    FOREIGN KEY (journal_id)
        REFERENCES accounting_journals(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (account_id)
        REFERENCES accounting_accounts(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT,

    CHECK (
        (debit_minor > 0 AND credit_minor = 0)
        OR
        (credit_minor > 0 AND debit_minor = 0)
    ),

    UNIQUE (journal_id, line_number)
);

CREATE INDEX IF NOT EXISTS ix_accounting_lines_account
    ON accounting_journal_lines(account_id, journal_id);

CREATE INDEX IF NOT EXISTS ix_accounting_lines_shop
    ON accounting_journal_lines(shop_id, journal_id);

INSERT OR IGNORE INTO accounting_journal_sequences
(
    organization_id,
    next_value,
    updated_at_utc
)
SELECT
    id,
    1,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_periods
(
    id,
    organization_id,
    name,
    start_date,
    end_date,
    status,
    version,
    created_at_utc,
    updated_at_utc
)
SELECT
    lower(hex(randomblob(16))),
    id,
    strftime('%Y', 'now') || ' Financial Year',
    strftime('%Y-01-01', 'now'),
    strftime('%Y-12-31', 'now'),
    'open',
    1,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '1000', 'Cash on Hand', 'asset', 'debit',
       'cash_on_hand', 1, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '1010', 'Mobile Money Clearing', 'asset', 'debit',
       'mobile_money_clearing', 1, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '1020', 'Bank Account', 'asset', 'debit',
       'bank_account', 1, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '1100', 'Accounts Receivable', 'asset', 'debit',
       'accounts_receivable', 0, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '1200', 'Inventory', 'asset', 'debit',
       'inventory', 0, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '1300', 'Tax Receivable', 'asset', 'debit',
       'tax_receivable', 0, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '2000', 'Accounts Payable', 'liability', 'credit',
       'accounts_payable', 0, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '2100', 'Sales Tax Payable', 'liability', 'credit',
       'sales_tax_payable', 0, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '3000', 'Owner Equity', 'equity', 'credit',
       'owner_equity', 1, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '3100', 'Retained Earnings', 'equity', 'credit',
       'retained_earnings', 0, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '4000', 'Sales Revenue', 'income', 'credit',
       'sales_revenue', 0, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '4100', 'Other Income', 'income', 'credit',
       'other_income', 1, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '5000', 'Cost of Goods Sold', 'expense', 'debit',
       'cost_of_goods_sold', 0, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '6000', 'Operating Expenses', 'expense', 'debit',
       'operating_expenses', 1, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '6100', 'Inventory Loss and Damage', 'expense', 'debit',
       'inventory_loss', 0, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

INSERT OR IGNORE INTO accounting_accounts
(
    id, organization_id, code, name, account_type, normal_balance,
    system_key, allow_manual_posting, is_active, version,
    created_at_utc, updated_at_utc
)
SELECT lower(hex(randomblob(16))), id, '6200', 'Payroll Expense', 'expense', 'debit',
       'payroll_expense', 0, 1, 1,
       strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations;

CREATE TRIGGER IF NOT EXISTS trg_accounting_account_normal_insert
BEFORE INSERT ON accounting_accounts
BEGIN
    SELECT CASE
        WHEN (NEW.account_type IN ('asset', 'expense') AND NEW.normal_balance <> 'debit')
          OR (NEW.account_type IN ('liability', 'equity', 'income') AND NEW.normal_balance <> 'credit')
        THEN RAISE(ABORT, 'account normal balance does not match account type')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_account_normal_update
BEFORE UPDATE OF account_type, normal_balance ON accounting_accounts
BEGIN
    SELECT CASE
        WHEN (NEW.account_type IN ('asset', 'expense') AND NEW.normal_balance <> 'debit')
          OR (NEW.account_type IN ('liability', 'equity', 'income') AND NEW.normal_balance <> 'credit')
        THEN RAISE(ABORT, 'account normal balance does not match account type')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_account_parent_insert
BEFORE INSERT ON accounting_accounts
WHEN NEW.parent_account_id IS NOT NULL
BEGIN
    SELECT CASE
        WHEN NEW.parent_account_id = NEW.id
          OR NOT EXISTS
          (
              SELECT 1
              FROM accounting_accounts AS parent
              WHERE parent.id = NEW.parent_account_id
                AND parent.organization_id = NEW.organization_id
          )
        THEN RAISE(ABORT, 'account parent must belong to the same organization')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_account_parent_update
BEFORE UPDATE OF parent_account_id, organization_id ON accounting_accounts
WHEN NEW.parent_account_id IS NOT NULL
BEGIN
    SELECT CASE
        WHEN NEW.parent_account_id = NEW.id
          OR NOT EXISTS
          (
              SELECT 1
              FROM accounting_accounts AS parent
              WHERE parent.id = NEW.parent_account_id
                AND parent.organization_id = NEW.organization_id
          )
        THEN RAISE(ABORT, 'account parent must belong to the same organization')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_system_account_immutable
BEFORE UPDATE ON accounting_accounts
WHEN OLD.system_key IS NOT NULL
BEGIN
    SELECT CASE
        WHEN NEW.organization_id <> OLD.organization_id
          OR NEW.code <> OLD.code
          OR NEW.account_type <> OLD.account_type
          OR NEW.normal_balance <> OLD.normal_balance
          OR NEW.system_key <> OLD.system_key
          OR NEW.allow_manual_posting <> OLD.allow_manual_posting
          OR NEW.is_active <> 1
        THEN RAISE(ABORT, 'system accounting account structure is immutable')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_account_delete_guard
BEFORE DELETE ON accounting_accounts
BEGIN
    SELECT CASE
        WHEN OLD.system_key IS NOT NULL
          OR EXISTS
          (
              SELECT 1
              FROM accounting_journal_lines
              WHERE account_id = OLD.id
          )
        THEN RAISE(ABORT, 'accounting account cannot be deleted')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_period_overlap_insert
BEFORE INSERT ON accounting_periods
BEGIN
    SELECT CASE
        WHEN EXISTS
        (
            SELECT 1
            FROM accounting_periods AS existing
            WHERE existing.organization_id = NEW.organization_id
              AND NEW.start_date <= existing.end_date
              AND NEW.end_date >= existing.start_date
        )
        THEN RAISE(ABORT, 'accounting periods cannot overlap')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_period_overlap_update
BEFORE UPDATE OF organization_id, start_date, end_date ON accounting_periods
BEGIN
    SELECT CASE
        WHEN EXISTS
        (
            SELECT 1
            FROM accounting_periods AS existing
            WHERE existing.organization_id = NEW.organization_id
              AND existing.id <> OLD.id
              AND NEW.start_date <= existing.end_date
              AND NEW.end_date >= existing.start_date
        )
        THEN RAISE(ABORT, 'accounting periods cannot overlap')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_period_close
BEFORE UPDATE OF status ON accounting_periods
WHEN NEW.status <> OLD.status
BEGIN
    SELECT CASE
        WHEN NOT (OLD.status = 'open' AND NEW.status = 'closed')
        THEN RAISE(ABORT, 'invalid accounting period transition')
    END;

    SELECT CASE
        WHEN NEW.closed_by_user_id IS NULL OR NEW.closed_at_utc IS NULL
        THEN RAISE(ABORT, 'closed accounting period requires closing audit fields')
    END;

    SELECT CASE
        WHEN EXISTS
        (
            SELECT 1
            FROM accounting_journals AS journal
            WHERE journal.organization_id = NEW.organization_id
              AND journal.status = 'draft'
              AND journal.journal_date BETWEEN NEW.start_date AND NEW.end_date
        )
        THEN RAISE(ABORT, 'accounting period contains draft journals')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_closed_period_immutable
BEFORE UPDATE ON accounting_periods
WHEN OLD.status = 'closed'
BEGIN
    SELECT RAISE(ABORT, 'closed accounting period is immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_period_delete_guard
BEFORE DELETE ON accounting_periods
BEGIN
    SELECT RAISE(ABORT, 'accounting periods cannot be deleted');
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_journal_scope_immutable
BEFORE UPDATE ON accounting_journals
BEGIN
    SELECT CASE
        WHEN NEW.organization_id <> OLD.organization_id
          OR NEW.shop_id <> OLD.shop_id
          OR NEW.journal_number <> OLD.journal_number
          OR NEW.currency_code <> OLD.currency_code
          OR NEW.source_type <> OLD.source_type
          OR COALESCE(NEW.source_id, '') <> COALESCE(OLD.source_id, '')
          OR COALESCE(NEW.reversal_of_journal_id, '') <> COALESCE(OLD.reversal_of_journal_id, '')
          OR NEW.created_by_user_id <> OLD.created_by_user_id
          OR NEW.created_at_utc <> OLD.created_at_utc
        THEN RAISE(ABORT, 'accounting journal ownership and source are immutable')
    END;

    SELECT CASE
        WHEN OLD.status <> 'draft'
         AND
         (
             NEW.journal_date <> OLD.journal_date
             OR NEW.description <> OLD.description
             OR NEW.total_debit_minor <> OLD.total_debit_minor
             OR NEW.total_credit_minor <> OLD.total_credit_minor
             OR COALESCE(NEW.posted_by_user_id, '') <> COALESCE(OLD.posted_by_user_id, '')
             OR COALESCE(NEW.posted_at_utc, '') <> COALESCE(OLD.posted_at_utc, '')
         )
        THEN RAISE(ABORT, 'posted accounting journal is immutable')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_journal_status_machine
BEFORE UPDATE OF status ON accounting_journals
WHEN NEW.status <> OLD.status
BEGIN
    SELECT CASE
        WHEN NOT
        (
            (OLD.status = 'draft' AND NEW.status = 'posted')
            OR
            (OLD.status = 'posted' AND NEW.status = 'reversed')
        )
        THEN RAISE(ABORT, 'invalid accounting journal status transition')
    END;

    SELECT CASE
        WHEN NEW.status = 'posted'
         AND
         (
             NEW.posted_by_user_id IS NULL
             OR NEW.posted_at_utc IS NULL
             OR NOT EXISTS
             (
                 SELECT 1
                 FROM accounting_journal_lines
                 WHERE journal_id = NEW.id
                 LIMIT 1 OFFSET 1
             )
             OR NEW.total_debit_minor <= 0
             OR NEW.total_debit_minor <> NEW.total_credit_minor
             OR NEW.total_debit_minor <>
                (
                    SELECT COALESCE(SUM(debit_minor), 0)
                    FROM accounting_journal_lines
                    WHERE journal_id = NEW.id
                )
             OR NEW.total_credit_minor <>
                (
                    SELECT COALESCE(SUM(credit_minor), 0)
                    FROM accounting_journal_lines
                    WHERE journal_id = NEW.id
                )
         )
        THEN RAISE(ABORT, 'posted accounting journal must contain balanced lines')
    END;

    SELECT CASE
        WHEN NEW.status = 'posted'
         AND NOT EXISTS
         (
             SELECT 1
             FROM accounting_periods AS period
             WHERE period.organization_id = NEW.organization_id
               AND period.status = 'open'
               AND NEW.journal_date BETWEEN period.start_date AND period.end_date
         )
        THEN RAISE(ABORT, 'journal date is not inside an open accounting period')
    END;

    SELECT CASE
        WHEN NEW.status = 'posted'
         AND EXISTS
         (
             SELECT 1
             FROM accounting_journal_lines AS line
             INNER JOIN accounting_accounts AS account
                 ON account.id = line.account_id
             INNER JOIN shops AS shop
                 ON shop.id = line.shop_id
             WHERE line.journal_id = NEW.id
               AND
               (
                   line.shop_id <> NEW.shop_id
                   OR account.organization_id <> NEW.organization_id
                   OR shop.organization_id <> NEW.organization_id
                   OR account.is_active <> 1
                   OR (NEW.source_type = 'manual' AND account.allow_manual_posting <> 1)
               )
         )
        THEN RAISE(ABORT, 'journal lines violate organization, shop or account posting rules')
    END;

    SELECT CASE
        WHEN NEW.status = 'posted'
         AND NEW.source_type = 'reversal'
         AND
         (
             NEW.reversal_of_journal_id IS NULL
             OR NOT EXISTS
             (
                 SELECT 1
                 FROM accounting_journals AS original
                 WHERE original.id = NEW.reversal_of_journal_id
                   AND original.organization_id = NEW.organization_id
                   AND original.shop_id = NEW.shop_id
                   AND original.status = 'posted'
             )
             OR EXISTS
             (
                 SELECT 1
                 FROM
                 (
                     SELECT account_id,
                            SUM(debit_minor) AS debit_total,
                            SUM(credit_minor) AS credit_total
                     FROM accounting_journal_lines
                     WHERE journal_id = NEW.reversal_of_journal_id
                     GROUP BY account_id
                 ) AS original_total
                 LEFT JOIN
                 (
                     SELECT account_id,
                            SUM(debit_minor) AS debit_total,
                            SUM(credit_minor) AS credit_total
                     FROM accounting_journal_lines
                     WHERE journal_id = NEW.id
                     GROUP BY account_id
                 ) AS reversal_total
                   ON reversal_total.account_id = original_total.account_id
                 WHERE COALESCE(reversal_total.debit_total, 0) <> original_total.credit_total
                    OR COALESCE(reversal_total.credit_total, 0) <> original_total.debit_total
             )
             OR EXISTS
             (
                 SELECT 1
                 FROM
                 (
                     SELECT account_id,
                            SUM(debit_minor) AS debit_total,
                            SUM(credit_minor) AS credit_total
                     FROM accounting_journal_lines
                     WHERE journal_id = NEW.id
                     GROUP BY account_id
                 ) AS reversal_total
                 LEFT JOIN
                 (
                     SELECT account_id,
                            SUM(debit_minor) AS debit_total,
                            SUM(credit_minor) AS credit_total
                     FROM accounting_journal_lines
                     WHERE journal_id = NEW.reversal_of_journal_id
                     GROUP BY account_id
                 ) AS original_total
                   ON original_total.account_id = reversal_total.account_id
                 WHERE COALESCE(original_total.debit_total, 0) <> reversal_total.credit_total
                    OR COALESCE(original_total.credit_total, 0) <> reversal_total.debit_total
             )
         )
        THEN RAISE(ABORT, 'reversal journal must exactly reverse the original journal')
    END;

    SELECT CASE
        WHEN NEW.status = 'reversed'
         AND
         (
             NEW.reversed_by_journal_id IS NULL
             OR NOT EXISTS
             (
                 SELECT 1
                 FROM accounting_journals AS reversal
                 WHERE reversal.id = NEW.reversed_by_journal_id
                   AND reversal.reversal_of_journal_id = OLD.id
                   AND reversal.organization_id = OLD.organization_id
                   AND reversal.shop_id = OLD.shop_id
                   AND reversal.status = 'posted'
             )
         )
        THEN RAISE(ABORT, 'reversed journal requires a posted reversal journal')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_journal_delete_guard
BEFORE DELETE ON accounting_journals
WHEN OLD.status <> 'draft'
BEGIN
    SELECT RAISE(ABORT, 'posted accounting journals cannot be deleted');
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_line_insert_guard
BEFORE INSERT ON accounting_journal_lines
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM accounting_journals AS journal
            INNER JOIN accounting_accounts AS account
                ON account.id = NEW.account_id
            INNER JOIN shops AS shop
                ON shop.id = NEW.shop_id
            WHERE journal.id = NEW.journal_id
              AND journal.status = 'draft'
              AND journal.shop_id = NEW.shop_id
              AND journal.organization_id = account.organization_id
              AND journal.organization_id = shop.organization_id
              AND account.is_active = 1
              AND (journal.source_type <> 'manual' OR account.allow_manual_posting = 1)
        )
        THEN RAISE(ABORT, 'journal line requires a draft journal and valid account scope')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_line_update_guard
BEFORE UPDATE ON accounting_journal_lines
BEGIN
    SELECT CASE
        WHEN NEW.journal_id <> OLD.journal_id
        THEN RAISE(ABORT, 'journal line ownership is immutable')
    END;

    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM accounting_journals AS journal
            INNER JOIN accounting_accounts AS account
                ON account.id = NEW.account_id
            INNER JOIN shops AS shop
                ON shop.id = NEW.shop_id
            WHERE journal.id = OLD.journal_id
              AND journal.status = 'draft'
              AND journal.shop_id = NEW.shop_id
              AND journal.organization_id = account.organization_id
              AND journal.organization_id = shop.organization_id
              AND account.is_active = 1
              AND (journal.source_type <> 'manual' OR account.allow_manual_posting = 1)
        )
        THEN RAISE(ABORT, 'only valid draft journal lines can be updated')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_accounting_line_delete_guard
BEFORE DELETE ON accounting_journal_lines
BEGIN
    SELECT CASE
        WHEN NOT EXISTS
        (
            SELECT 1
            FROM accounting_journals
            WHERE id = OLD.journal_id
              AND status = 'draft'
        )
        THEN RAISE(ABORT, 'posted accounting journal lines are immutable')
    END;
END;

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    10,
    'Branch-scoped double-entry accounting kernel, periods, immutable posting, reversals and trial balance',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
