CREATE TABLE IF NOT EXISTS crm_customer_profiles
(
    customer_id               TEXT PRIMARY KEY,
    organization_id           TEXT NOT NULL,
    customer_type             TEXT NOT NULL DEFAULT 'individual'
                              CHECK (customer_type IN ('individual', 'business')),
    company_name              TEXT NOT NULL DEFAULT '',
    contact_person            TEXT NOT NULL DEFAULT '',
    lifecycle_stage           TEXT NOT NULL DEFAULT 'customer'
                              CHECK (lifecycle_stage IN ('lead', 'prospect', 'customer', 'vip', 'dormant', 'blocked')),
    source                    TEXT NOT NULL DEFAULT 'manual',
    preferred_channel         TEXT NOT NULL DEFAULT 'phone'
                              CHECK (preferred_channel IN ('phone', 'email', 'sms', 'whatsapp', 'in_person', 'none')),
    marketing_opt_in          INTEGER NOT NULL DEFAULT 0 CHECK (marketing_opt_in IN (0, 1)),
    loyalty_enrolled          INTEGER NOT NULL DEFAULT 1 CHECK (loyalty_enrolled IN (0, 1)),
    loyalty_tier              TEXT NOT NULL DEFAULT 'standard'
                              CHECK (loyalty_tier IN ('standard', 'silver', 'gold', 'platinum')),
    current_points            INTEGER NOT NULL DEFAULT 0,
    lifetime_points           INTEGER NOT NULL DEFAULT 0 CHECK (lifetime_points >= 0),
    assigned_user_id          TEXT NULL,
    notes                     TEXT NOT NULL DEFAULT '',
    first_sale_at_utc         TEXT NULL,
    last_sale_at_utc          TEXT NULL,
    last_contact_at_utc       TEXT NULL,
    next_follow_up_at_utc     TEXT NULL,
    version                   INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_at_utc            TEXT NOT NULL,
    updated_at_utc            TEXT NOT NULL,

    FOREIGN KEY (customer_id) REFERENCES finance_customers(id) ON DELETE RESTRICT,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (assigned_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_crm_profiles_org_stage
    ON crm_customer_profiles(organization_id, lifecycle_stage, updated_at_utc);
CREATE INDEX IF NOT EXISTS ix_crm_profiles_follow_up
    ON crm_customer_profiles(organization_id, next_follow_up_at_utc);
CREATE INDEX IF NOT EXISTS ix_finance_customers_phone_lookup
    ON finance_customers(organization_id, phone COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS ix_finance_customers_email_lookup
    ON finance_customers(organization_id, email COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS crm_tags
(
    id                        TEXT PRIMARY KEY,
    organization_id           TEXT NOT NULL,
    name                      TEXT NOT NULL COLLATE NOCASE,
    description               TEXT NOT NULL DEFAULT '',
    is_active                 INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    version                   INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id        TEXT NOT NULL,
    created_at_utc            TEXT NOT NULL,
    updated_at_utc            TEXT NOT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (organization_id, name)
);

CREATE TABLE IF NOT EXISTS crm_customer_tags
(
    customer_id               TEXT NOT NULL,
    tag_id                    TEXT NOT NULL,
    assigned_by_user_id       TEXT NOT NULL,
    assigned_at_utc           TEXT NOT NULL,

    PRIMARY KEY (customer_id, tag_id),
    FOREIGN KEY (customer_id) REFERENCES finance_customers(id) ON DELETE RESTRICT,
    FOREIGN KEY (tag_id) REFERENCES crm_tags(id) ON DELETE RESTRICT,
    FOREIGN KEY (assigned_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_crm_customer_tags_tag
    ON crm_customer_tags(tag_id, customer_id);

CREATE TABLE IF NOT EXISTS crm_communications
(
    id                        TEXT PRIMARY KEY,
    organization_id           TEXT NOT NULL,
    shop_id                   TEXT NOT NULL,
    customer_id               TEXT NOT NULL,
    communication_type        TEXT NOT NULL
                              CHECK (communication_type IN ('call', 'email', 'sms', 'whatsapp', 'meeting', 'note', 'complaint')),
    direction                 TEXT NOT NULL
                              CHECK (direction IN ('inbound', 'outbound', 'internal')),
    subject                   TEXT NOT NULL DEFAULT '',
    details                   TEXT NOT NULL,
    outcome                   TEXT NOT NULL DEFAULT '',
    occurred_at_utc           TEXT NOT NULL,
    follow_up_at_utc          TEXT NULL,
    created_by_user_id        TEXT NOT NULL,
    created_at_utc            TEXT NOT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (customer_id) REFERENCES finance_customers(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_crm_communications_customer
    ON crm_communications(organization_id, customer_id, occurred_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_crm_communications_follow_up
    ON crm_communications(organization_id, shop_id, follow_up_at_utc);

CREATE TABLE IF NOT EXISTS crm_tasks
(
    id                        TEXT PRIMARY KEY,
    organization_id           TEXT NOT NULL,
    shop_id                   TEXT NOT NULL,
    customer_id               TEXT NULL,
    title                     TEXT NOT NULL,
    details                   TEXT NOT NULL DEFAULT '',
    priority                  TEXT NOT NULL DEFAULT 'normal'
                              CHECK (priority IN ('low', 'normal', 'high', 'urgent')),
    status                    TEXT NOT NULL DEFAULT 'open'
                              CHECK (status IN ('open', 'completed', 'cancelled')),
    due_at_utc                TEXT NOT NULL,
    assigned_to_user_id       TEXT NOT NULL,
    created_by_user_id        TEXT NOT NULL,
    completed_by_user_id      TEXT NULL,
    completion_notes          TEXT NULL,
    version                   INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_at_utc            TEXT NOT NULL,
    updated_at_utc            TEXT NOT NULL,
    completed_at_utc          TEXT NULL,
    cancelled_at_utc          TEXT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (customer_id) REFERENCES finance_customers(id) ON DELETE RESTRICT,
    FOREIGN KEY (assigned_to_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (completed_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_crm_tasks_due
    ON crm_tasks(organization_id, shop_id, status, due_at_utc);
CREATE INDEX IF NOT EXISTS ix_crm_tasks_customer
    ON crm_tasks(customer_id, status, due_at_utc);

CREATE TABLE IF NOT EXISTS crm_loyalty_settings
(
    organization_id           TEXT PRIMARY KEY,
    is_enabled                INTEGER NOT NULL DEFAULT 0 CHECK (is_enabled IN (0, 1)),
    spend_minor_per_point     INTEGER NOT NULL DEFAULT 1000 CHECK (spend_minor_per_point > 0),
    minimum_redeem_points     INTEGER NOT NULL DEFAULT 1 CHECK (minimum_redeem_points >= 1),
    silver_threshold_points   INTEGER NOT NULL DEFAULT 100 CHECK (silver_threshold_points >= 0),
    gold_threshold_points     INTEGER NOT NULL DEFAULT 500 CHECK (gold_threshold_points >= silver_threshold_points),
    platinum_threshold_points INTEGER NOT NULL DEFAULT 1000 CHECK (platinum_threshold_points >= gold_threshold_points),
    version                   INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    updated_by_user_id        TEXT NOT NULL,
    created_at_utc            TEXT NOT NULL,
    updated_at_utc            TEXT NOT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (updated_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS crm_loyalty_ledger
(
    id                        TEXT PRIMARY KEY,
    organization_id           TEXT NOT NULL,
    customer_id               TEXT NOT NULL,
    shop_id                   TEXT NULL,
    sale_id                   TEXT NULL,
    entry_type                TEXT NOT NULL
                              CHECK (entry_type IN ('earn', 'redeem', 'adjustment', 'reversal')),
    points_delta              INTEGER NOT NULL CHECK (points_delta <> 0),
    balance_after             INTEGER NOT NULL,
    reference_type            TEXT NOT NULL,
    reference_id              TEXT NOT NULL,
    reason                    TEXT NOT NULL,
    created_by_user_id        TEXT NOT NULL,
    created_at_utc            TEXT NOT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (customer_id) REFERENCES finance_customers(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (sale_id) REFERENCES sales(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_crm_loyalty_customer
    ON crm_loyalty_ledger(organization_id, customer_id, created_at_utc DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_loyalty_sale_earn
    ON crm_loyalty_ledger(sale_id, entry_type)
    WHERE sale_id IS NOT NULL AND entry_type = 'earn';
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_loyalty_sale_reversal
    ON crm_loyalty_ledger(sale_id, entry_type)
    WHERE sale_id IS NOT NULL AND entry_type = 'reversal';

CREATE TABLE IF NOT EXISTS crm_quotation_sequences
(
    shop_id                   TEXT PRIMARY KEY,
    prefix                    TEXT NOT NULL,
    next_value                INTEGER NOT NULL DEFAULT 1 CHECK (next_value >= 1),
    updated_at_utc            TEXT NOT NULL,

    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS crm_quotations
(
    id                        TEXT PRIMARY KEY,
    organization_id           TEXT NOT NULL,
    shop_id                   TEXT NOT NULL,
    quotation_number          TEXT NOT NULL,
    customer_id               TEXT NOT NULL,
    status                    TEXT NOT NULL DEFAULT 'draft'
                              CHECK (status IN ('draft', 'sent', 'accepted', 'rejected', 'expired', 'converted', 'cancelled')),
    quotation_date            TEXT NOT NULL
                              CHECK (length(quotation_date) = 10 AND date(quotation_date) = quotation_date),
    valid_until               TEXT NOT NULL
                              CHECK (length(valid_until) = 10 AND date(valid_until) = valid_until),
    currency_code             TEXT NOT NULL,
    subtotal_minor            INTEGER NOT NULL CHECK (subtotal_minor >= 0),
    discount_minor            INTEGER NOT NULL DEFAULT 0 CHECK (discount_minor >= 0),
    total_minor               INTEGER NOT NULL CHECK (total_minor >= 0),
    notes                     TEXT NOT NULL DEFAULT '',
    terms                     TEXT NOT NULL DEFAULT '',
    sale_id                   TEXT NULL,
    version                   INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id        TEXT NOT NULL,
    updated_by_user_id        TEXT NOT NULL,
    created_at_utc            TEXT NOT NULL,
    updated_at_utc            TEXT NOT NULL,
    sent_at_utc               TEXT NULL,
    accepted_at_utc           TEXT NULL,
    converted_at_utc          TEXT NULL,
    closed_at_utc             TEXT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (customer_id) REFERENCES finance_customers(id) ON DELETE RESTRICT,
    FOREIGN KEY (sale_id) REFERENCES sales(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (updated_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,

    UNIQUE (organization_id, quotation_number),
    UNIQUE (sale_id),
    CHECK (total_minor = subtotal_minor - discount_minor),
    CHECK (discount_minor <= subtotal_minor),
    CHECK (valid_until >= quotation_date)
);

CREATE INDEX IF NOT EXISTS ix_crm_quotations_customer
    ON crm_quotations(organization_id, customer_id, status, quotation_date DESC);
CREATE INDEX IF NOT EXISTS ix_crm_quotations_shop_status
    ON crm_quotations(organization_id, shop_id, status, valid_until);

CREATE TABLE IF NOT EXISTS crm_quotation_lines
(
    id                        TEXT PRIMARY KEY,
    quotation_id              TEXT NOT NULL,
    line_number               INTEGER NOT NULL CHECK (line_number >= 1),
    product_id                TEXT NOT NULL,
    product_name_snapshot     TEXT NOT NULL,
    sku_snapshot              TEXT NOT NULL,
    quantity                  INTEGER NOT NULL CHECK (quantity > 0),
    unit_price_minor          INTEGER NOT NULL CHECK (unit_price_minor >= 0),
    line_total_minor          INTEGER NOT NULL CHECK (line_total_minor >= 0),

    FOREIGN KEY (quotation_id) REFERENCES crm_quotations(id) ON DELETE RESTRICT,
    FOREIGN KEY (product_id) REFERENCES products(id) ON DELETE RESTRICT,
    UNIQUE (quotation_id, line_number),
    UNIQUE (quotation_id, product_id),
    CHECK (line_total_minor = quantity * unit_price_minor)
);

CREATE TRIGGER IF NOT EXISTS trg_crm_profile_scope_insert
BEFORE INSERT ON crm_customer_profiles
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1
        FROM finance_customers
        WHERE id = NEW.customer_id
          AND organization_id = NEW.organization_id
    ) THEN RAISE(ABORT, 'CRM customer profile ownership is invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_profile_scope_update
BEFORE UPDATE ON crm_customer_profiles
WHEN NEW.customer_id <> OLD.customer_id OR NEW.organization_id <> OLD.organization_id OR NEW.created_at_utc <> OLD.created_at_utc
BEGIN
    SELECT RAISE(ABORT, 'CRM customer profile ownership is immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_customer_profile_after_finance_insert
AFTER INSERT ON finance_customers
BEGIN
    INSERT OR IGNORE INTO crm_customer_profiles
    (
        customer_id, organization_id, customer_type, lifecycle_stage,
        source, created_at_utc, updated_at_utc
    )
    VALUES
    (
        NEW.id, NEW.organization_id, 'individual', 'customer',
        'finance', NEW.created_at_utc, NEW.updated_at_utc
    );
END;

INSERT OR IGNORE INTO crm_customer_profiles
(
    customer_id, organization_id, customer_type, lifecycle_stage,
    source, first_sale_at_utc, last_sale_at_utc, created_at_utc, updated_at_utc
)
SELECT
    customer.id,
    customer.organization_id,
    'individual',
    CASE
        WHEN customer.is_active = 0 THEN 'blocked'
        WHEN COUNT(sale.id) = 0 THEN 'prospect'
        ELSE 'customer'
    END,
    'finance-migration',
    MIN(sale.completed_at_utc),
    MAX(sale.completed_at_utc),
    customer.created_at_utc,
    customer.updated_at_utc
FROM finance_customers AS customer
LEFT JOIN sales AS sale
    ON sale.customer_id = customer.id
   AND sale.status = 'completed'
GROUP BY customer.id;

INSERT OR IGNORE INTO crm_loyalty_settings
(
    organization_id, is_enabled, spend_minor_per_point, minimum_redeem_points,
    silver_threshold_points, gold_threshold_points, platinum_threshold_points,
    version, updated_by_user_id, created_at_utc, updated_at_utc
)
SELECT
    organization.id, 0, 1000, 1, 100, 500, 1000,
    1,
    COALESCE(
        (SELECT id FROM users WHERE role = 'admin' ORDER BY created_at_utc LIMIT 1),
        (SELECT id FROM users ORDER BY created_at_utc LIMIT 1)
    ),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations AS organization
WHERE EXISTS (SELECT 1 FROM users);

CREATE TRIGGER IF NOT EXISTS trg_crm_communication_scope_insert
BEFORE INSERT ON crm_communications
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1
        FROM finance_customers AS customer
        INNER JOIN shops AS shop ON shop.id = NEW.shop_id
        WHERE customer.id = NEW.customer_id
          AND customer.organization_id = NEW.organization_id
          AND shop.organization_id = NEW.organization_id
    ) THEN RAISE(ABORT, 'CRM communication scope is invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_communication_after_insert
AFTER INSERT ON crm_communications
BEGIN
    UPDATE crm_customer_profiles
    SET last_contact_at_utc = CASE
            WHEN last_contact_at_utc IS NULL OR NEW.occurred_at_utc > last_contact_at_utc
            THEN NEW.occurred_at_utc ELSE last_contact_at_utc END,
        next_follow_up_at_utc = CASE
            WHEN NEW.follow_up_at_utc IS NOT NULL THEN NEW.follow_up_at_utc
            ELSE next_follow_up_at_utc END,
        updated_at_utc = NEW.created_at_utc,
        version = version + 1
    WHERE customer_id = NEW.customer_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_communication_delete
BEFORE DELETE ON crm_communications
BEGIN
    SELECT RAISE(ABORT, 'CRM communications are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_task_scope_insert
BEFORE INSERT ON crm_tasks
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1 FROM shops
        WHERE id = NEW.shop_id AND organization_id = NEW.organization_id
    ) THEN RAISE(ABORT, 'CRM task shop scope is invalid') END;

    SELECT CASE WHEN NEW.customer_id IS NOT NULL AND NOT EXISTS
    (
        SELECT 1 FROM finance_customers
        WHERE id = NEW.customer_id AND organization_id = NEW.organization_id
    ) THEN RAISE(ABORT, 'CRM task customer scope is invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_task_state_update
BEFORE UPDATE OF status ON crm_tasks
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.status = NEW.status
        OR (OLD.status = 'open' AND NEW.status IN ('completed', 'cancelled'))
    ) THEN RAISE(ABORT, 'invalid CRM task state transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_loyalty_ledger_delete
BEFORE DELETE ON crm_loyalty_ledger
BEGIN
    SELECT RAISE(ABORT, 'loyalty ledger entries are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_sale_activity_after_accounting_link
AFTER INSERT ON accounting_operational_links
WHEN NEW.source_type = 'sale'
BEGIN
    UPDATE crm_customer_profiles
    SET first_sale_at_utc = COALESCE(
            first_sale_at_utc,
            (SELECT completed_at_utc FROM sales WHERE id = NEW.source_id)),
        last_sale_at_utc = CASE
            WHEN last_sale_at_utc IS NULL OR
                 (SELECT completed_at_utc FROM sales WHERE id = NEW.source_id) > last_sale_at_utc
            THEN (SELECT completed_at_utc FROM sales WHERE id = NEW.source_id)
            ELSE last_sale_at_utc END,
        lifecycle_stage = CASE
            WHEN lifecycle_stage IN ('lead', 'prospect', 'dormant') THEN 'customer'
            ELSE lifecycle_stage END,
        updated_at_utc = NEW.posted_at_utc,
        version = version + 1
    WHERE customer_id = (SELECT customer_id FROM sales WHERE id = NEW.source_id)
      AND (SELECT customer_id FROM sales WHERE id = NEW.source_id) IS NOT NULL;

    INSERT OR IGNORE INTO crm_loyalty_ledger
    (
        id, organization_id, customer_id, shop_id, sale_id,
        entry_type, points_delta, balance_after,
        reference_type, reference_id, reason,
        created_by_user_id, created_at_utc
    )
    SELECT
        lower(hex(randomblob(16))),
        NEW.organization_id,
        sale.customer_id,
        NEW.shop_id,
        sale.id,
        'earn',
        CAST(sale.total_minor / settings.spend_minor_per_point AS INTEGER),
        profile.current_points + CAST(sale.total_minor / settings.spend_minor_per_point AS INTEGER),
        'sale',
        sale.id,
        'Automatic points from sale ' || sale.receipt_number,
        sale.teller_user_id,
        NEW.posted_at_utc
    FROM sales AS sale
    INNER JOIN crm_customer_profiles AS profile ON profile.customer_id = sale.customer_id
    INNER JOIN crm_loyalty_settings AS settings ON settings.organization_id = NEW.organization_id
    WHERE sale.id = NEW.source_id
      AND sale.customer_id IS NOT NULL
      AND settings.is_enabled = 1
      AND profile.loyalty_enrolled = 1
      AND CAST(sale.total_minor / settings.spend_minor_per_point AS INTEGER) > 0;

    UPDATE crm_customer_profiles
    SET current_points = current_points +
        COALESCE((SELECT points_delta FROM crm_loyalty_ledger WHERE sale_id = NEW.source_id AND entry_type = 'earn'), 0),
        lifetime_points = lifetime_points +
        COALESCE((SELECT points_delta FROM crm_loyalty_ledger WHERE sale_id = NEW.source_id AND entry_type = 'earn'), 0),
        loyalty_tier = CASE
            WHEN lifetime_points + COALESCE((SELECT points_delta FROM crm_loyalty_ledger WHERE sale_id = NEW.source_id AND entry_type = 'earn'), 0) >=
                 (SELECT platinum_threshold_points FROM crm_loyalty_settings WHERE organization_id = NEW.organization_id)
            THEN 'platinum'
            WHEN lifetime_points + COALESCE((SELECT points_delta FROM crm_loyalty_ledger WHERE sale_id = NEW.source_id AND entry_type = 'earn'), 0) >=
                 (SELECT gold_threshold_points FROM crm_loyalty_settings WHERE organization_id = NEW.organization_id)
            THEN 'gold'
            WHEN lifetime_points + COALESCE((SELECT points_delta FROM crm_loyalty_ledger WHERE sale_id = NEW.source_id AND entry_type = 'earn'), 0) >=
                 (SELECT silver_threshold_points FROM crm_loyalty_settings WHERE organization_id = NEW.organization_id)
            THEN 'silver'
            ELSE 'standard' END,
        updated_at_utc = NEW.posted_at_utc,
        version = version + 1
    WHERE customer_id = (SELECT customer_id FROM sales WHERE id = NEW.source_id)
      AND EXISTS (SELECT 1 FROM crm_loyalty_ledger WHERE sale_id = NEW.source_id AND entry_type = 'earn');
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_loyalty_sale_void
AFTER UPDATE OF status ON sales
WHEN OLD.status = 'completed' AND NEW.status = 'voided' AND NEW.customer_id IS NOT NULL
BEGIN
    INSERT OR IGNORE INTO crm_loyalty_ledger
    (
        id, organization_id, customer_id, shop_id, sale_id,
        entry_type, points_delta, balance_after,
        reference_type, reference_id, reason,
        created_by_user_id, created_at_utc
    )
    SELECT
        lower(hex(randomblob(16))),
        customer.organization_id,
        NEW.customer_id,
        NEW.shop_id,
        NEW.id,
        'reversal',
        -earn.points_delta,
        profile.current_points - earn.points_delta,
        'sale_void',
        NEW.id,
        'Automatic reversal for voided sale ' || NEW.receipt_number,
        NEW.voided_by_user_id,
        NEW.voided_at_utc
    FROM crm_loyalty_ledger AS earn
    INNER JOIN crm_customer_profiles AS profile ON profile.customer_id = NEW.customer_id
    INNER JOIN finance_customers AS customer ON customer.id = NEW.customer_id
    WHERE earn.sale_id = NEW.id AND earn.entry_type = 'earn';

    UPDATE crm_customer_profiles
    SET current_points = current_points -
        COALESCE((SELECT points_delta FROM crm_loyalty_ledger WHERE sale_id = NEW.id AND entry_type = 'earn'), 0),
        lifetime_points = MAX(
            lifetime_points - COALESCE((SELECT points_delta FROM crm_loyalty_ledger WHERE sale_id = NEW.id AND entry_type = 'earn'), 0),
            0),
        updated_at_utc = NEW.voided_at_utc,
        version = version + 1
    WHERE customer_id = NEW.customer_id
      AND EXISTS (SELECT 1 FROM crm_loyalty_ledger WHERE sale_id = NEW.id AND entry_type = 'reversal');
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_quotation_scope_insert
BEFORE INSERT ON crm_quotations
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1
        FROM finance_customers AS customer
        INNER JOIN shops AS shop ON shop.id = NEW.shop_id
        WHERE customer.id = NEW.customer_id
          AND customer.organization_id = NEW.organization_id
          AND shop.organization_id = NEW.organization_id
          AND customer.is_active = 1
    ) THEN RAISE(ABORT, 'quotation customer or shop scope is invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_quotation_state_update
BEFORE UPDATE OF status ON crm_quotations
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.status = NEW.status
        OR (OLD.status = 'draft' AND NEW.status IN ('sent', 'cancelled'))
        OR (OLD.status = 'sent' AND NEW.status IN ('accepted', 'rejected', 'expired', 'cancelled'))
        OR (OLD.status = 'accepted' AND NEW.status IN ('converted', 'cancelled'))
    ) THEN RAISE(ABORT, 'invalid quotation state transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_quotation_delete
BEFORE DELETE ON crm_quotations
BEGIN
    SELECT RAISE(ABORT, 'quotations are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_quotation_line_update
BEFORE UPDATE ON crm_quotation_lines
WHEN (SELECT status FROM crm_quotations WHERE id = OLD.quotation_id) <> 'draft'
BEGIN
    SELECT RAISE(ABORT, 'non-draft quotation lines are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_crm_quotation_line_delete
BEFORE DELETE ON crm_quotation_lines
WHEN (SELECT status FROM crm_quotations WHERE id = OLD.quotation_id) <> 'draft'
BEGIN
    SELECT RAISE(ABORT, 'non-draft quotation lines are immutable');
END;

CREATE VIEW IF NOT EXISTS crm_customer_sales_metrics AS
SELECT
    customer.organization_id,
    customer.id AS customer_id,
    COUNT(CASE WHEN sale.status = 'completed' THEN 1 END) AS completed_sale_count,
    COALESCE(SUM(CASE WHEN sale.status = 'completed' THEN sale.total_minor ELSE 0 END), 0) AS lifetime_spend_minor,
    COALESCE(AVG(CASE WHEN sale.status = 'completed' THEN sale.total_minor END), 0) AS average_sale_minor,
    MIN(CASE WHEN sale.status = 'completed' THEN sale.completed_at_utc END) AS first_sale_at_utc,
    MAX(CASE WHEN sale.status = 'completed' THEN sale.completed_at_utc END) AS last_sale_at_utc,
    COUNT(DISTINCT CASE WHEN sale.status = 'completed' THEN sale.shop_id END) AS shop_count
FROM finance_customers AS customer
LEFT JOIN sales AS sale ON sale.customer_id = customer.id
GROUP BY customer.organization_id, customer.id;

CREATE VIEW IF NOT EXISTS crm_customer_outstanding_balances AS
SELECT
    customer.organization_id,
    customer.id AS customer_id,
    COALESCE(SUM(
        receivable.original_amount_minor -
        COALESCE((
            SELECT SUM(allocation.amount_minor)
            FROM finance_customer_receipt_allocations AS allocation
            INNER JOIN finance_customer_receipts AS receipt
                ON receipt.id = allocation.receipt_id
               AND receipt.status = 'posted'
            WHERE allocation.receivable_item_id = receivable.id
        ), 0)
    ), 0) AS outstanding_minor
FROM finance_customers AS customer
LEFT JOIN finance_receivable_items AS receivable ON receivable.customer_id = customer.id
GROUP BY customer.organization_id, customer.id;

CREATE VIEW IF NOT EXISTS crm_customer_segments AS
SELECT
    customer.organization_id,
    customer.id AS customer_id,
    CASE
        WHEN customer.is_active = 0 OR profile.lifecycle_stage = 'blocked' THEN 'blocked'
        WHEN outstanding.outstanding_minor > 0 THEN 'debtor'
        WHEN metrics.completed_sale_count = 0 THEN 'prospect'
        WHEN metrics.last_sale_at_utc < datetime('now', '-90 days') THEN 'dormant'
        WHEN metrics.completed_sale_count >= 5 THEN 'loyal'
        WHEN metrics.first_sale_at_utc >= datetime('now', '-30 days') THEN 'new'
        ELSE 'active'
    END AS segment
FROM finance_customers AS customer
INNER JOIN crm_customer_profiles AS profile ON profile.customer_id = customer.id
INNER JOIN crm_customer_sales_metrics AS metrics ON metrics.customer_id = customer.id
INNER JOIN crm_customer_outstanding_balances AS outstanding ON outstanding.customer_id = customer.id;

INSERT OR IGNORE INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    14,
    'CRM customer profiles, engagement, loyalty, quotations and analytics',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
