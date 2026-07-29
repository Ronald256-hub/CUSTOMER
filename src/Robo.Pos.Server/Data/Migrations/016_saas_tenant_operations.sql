CREATE TABLE IF NOT EXISTS saas_plans
(
    id                    TEXT PRIMARY KEY,
    code                  TEXT NOT NULL COLLATE NOCASE UNIQUE,
    name                  TEXT NOT NULL,
    description           TEXT NOT NULL DEFAULT '',
    status                TEXT NOT NULL DEFAULT 'active'
                          CHECK (status IN ('active', 'retired')),
    billing_interval      TEXT NOT NULL DEFAULT 'monthly'
                          CHECK (billing_interval IN ('monthly', 'annual', 'custom')),
    price_minor           INTEGER NOT NULL DEFAULT 0
                          CHECK (price_minor >= 0),
    currency_code         TEXT NOT NULL DEFAULT 'UGX',
    trial_days            INTEGER NOT NULL DEFAULT 0
                          CHECK (trial_days >= 0),
    enforcement_mode      TEXT NOT NULL DEFAULT 'report_only'
                          CHECK (enforcement_mode IN ('report_only', 'hard')),
    sort_order            INTEGER NOT NULL DEFAULT 0,
    version               INTEGER NOT NULL DEFAULT 1
                          CHECK (version >= 1),
    created_by_user_id    TEXT NULL,
    updated_by_user_id    TEXT NULL,
    created_at_utc        TEXT NOT NULL,
    updated_at_utc        TEXT NOT NULL,

    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL,
    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

INSERT OR IGNORE INTO saas_plans
(
    id, code, name, description, status, billing_interval,
    price_minor, currency_code, trial_days, enforcement_mode,
    sort_order, version, created_at_utc, updated_at_utc
)
VALUES
(
    'enterprise-unlimited',
    'ENTERPRISE',
    'Enterprise Unlimited',
    'Default compatibility plan for existing Nexus POS organisations.',
    'active',
    'custom',
    0,
    'UGX',
    0,
    'report_only',
    100,
    1,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);

CREATE TABLE IF NOT EXISTS saas_plan_entitlements
(
    plan_id               TEXT NOT NULL,
    entitlement_key       TEXT NOT NULL COLLATE NOCASE,
    is_enabled            INTEGER NOT NULL DEFAULT 1
                          CHECK (is_enabled IN (0, 1)),
    limit_value           INTEGER NULL
                          CHECK (limit_value IS NULL OR limit_value >= 0),
    configuration_json    TEXT NOT NULL DEFAULT '{}',
    updated_by_user_id    TEXT NULL,
    updated_at_utc        TEXT NOT NULL,

    PRIMARY KEY (plan_id, entitlement_key),

    FOREIGN KEY (plan_id)
        REFERENCES saas_plans(id)
        ON DELETE CASCADE,
    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

INSERT OR IGNORE INTO saas_plan_entitlements
(plan_id, entitlement_key, is_enabled, limit_value, configuration_json, updated_at_utc)
VALUES
('enterprise-unlimited', 'accounting', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('enterprise-unlimited', 'procurement', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('enterprise-unlimited', 'crm', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('enterprise-unlimited', 'hrm', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('enterprise-unlimited', 'multi_shop', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('enterprise-unlimited', 'max_active_shops', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('enterprise-unlimited', 'max_active_users', 1, NULL, '{}', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TABLE IF NOT EXISTS saas_subscriptions
(
    id                         TEXT PRIMARY KEY,
    organization_id            TEXT NOT NULL UNIQUE,
    plan_id                    TEXT NOT NULL,
    status                     TEXT NOT NULL DEFAULT 'active'
                               CHECK (status IN
                               ('trialing', 'active', 'past_due', 'suspended', 'cancelled')),
    started_at_utc             TEXT NOT NULL,
    trial_ends_at_utc          TEXT NULL,
    current_period_starts_utc  TEXT NULL,
    current_period_ends_utc    TEXT NULL,
    grace_ends_at_utc          TEXT NULL,
    external_customer_ref      TEXT NOT NULL DEFAULT '',
    external_subscription_ref  TEXT NOT NULL DEFAULT '',
    notes                      TEXT NOT NULL DEFAULT '',
    version                    INTEGER NOT NULL DEFAULT 1
                               CHECK (version >= 1),
    updated_by_user_id         TEXT NULL,
    created_at_utc             TEXT NOT NULL,
    updated_at_utc             TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,
    FOREIGN KEY (plan_id)
        REFERENCES saas_plans(id)
        ON DELETE RESTRICT,
    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

INSERT OR IGNORE INTO saas_subscriptions
(
    id, organization_id, plan_id, status, started_at_utc,
    current_period_starts_utc, version, created_at_utc, updated_at_utc
)
SELECT
    'subscription-' || organization.id,
    organization.id,
    'enterprise-unlimited',
    'active',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    1,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM organizations organization;

CREATE INDEX IF NOT EXISTS ix_saas_subscriptions_status
    ON saas_subscriptions(status, plan_id, updated_at_utc);

CREATE TABLE IF NOT EXISTS saas_subscription_events
(
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    subscription_id    TEXT NOT NULL,
    organization_id    TEXT NOT NULL,
    event_type         TEXT NOT NULL,
    previous_status    TEXT NULL,
    new_status         TEXT NULL,
    details_json       TEXT NOT NULL DEFAULT '{}',
    actor_user_id      TEXT NULL,
    occurred_at_utc    TEXT NOT NULL,

    FOREIGN KEY (subscription_id)
        REFERENCES saas_subscriptions(id)
        ON DELETE RESTRICT,
    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,
    FOREIGN KEY (actor_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS ix_saas_subscription_events_organization
    ON saas_subscription_events(organization_id, occurred_at_utc DESC);

CREATE TRIGGER IF NOT EXISTS tr_saas_subscription_events_immutable_update
BEFORE UPDATE ON saas_subscription_events
BEGIN
    SELECT RAISE(ABORT, 'saas_subscription_events_are_immutable');
END;

CREATE TRIGGER IF NOT EXISTS tr_saas_subscription_events_immutable_delete
BEFORE DELETE ON saas_subscription_events
BEGIN
    SELECT RAISE(ABORT, 'saas_subscription_events_are_immutable');
END;

CREATE TABLE IF NOT EXISTS saas_feature_overrides
(
    organization_id      TEXT NOT NULL,
    entitlement_key      TEXT NOT NULL COLLATE NOCASE,
    is_enabled            INTEGER NULL
                          CHECK (is_enabled IS NULL OR is_enabled IN (0, 1)),
    limit_value           INTEGER NULL
                          CHECK (limit_value IS NULL OR limit_value >= 0),
    reason                TEXT NOT NULL DEFAULT '',
    expires_at_utc        TEXT NULL,
    version               INTEGER NOT NULL DEFAULT 1
                          CHECK (version >= 1),
    updated_by_user_id    TEXT NULL,
    updated_at_utc        TEXT NOT NULL,

    PRIMARY KEY (organization_id, entitlement_key),

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE CASCADE,
    FOREIGN KEY (updated_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS saas_usage_snapshots
(
    id                         TEXT PRIMARY KEY,
    organization_id            TEXT NOT NULL,
    captured_at_utc            TEXT NOT NULL,
    active_shop_count          INTEGER NOT NULL DEFAULT 0 CHECK (active_shop_count >= 0),
    active_user_count          INTEGER NOT NULL DEFAULT 0 CHECK (active_user_count >= 0),
    employee_count             INTEGER NOT NULL DEFAULT 0 CHECK (employee_count >= 0),
    customer_count             INTEGER NOT NULL DEFAULT 0 CHECK (customer_count >= 0),
    completed_sales_30d        INTEGER NOT NULL DEFAULT 0 CHECK (completed_sales_30d >= 0),
    purchase_orders_30d        INTEGER NOT NULL DEFAULT 0 CHECK (purchase_orders_30d >= 0),
    database_size_bytes        INTEGER NOT NULL DEFAULT 0 CHECK (database_size_bytes >= 0),
    limit_violations_json      TEXT NOT NULL DEFAULT '[]',
    captured_by_user_id        TEXT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE CASCADE,
    FOREIGN KEY (captured_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS ix_saas_usage_snapshots_organization
    ON saas_usage_snapshots(organization_id, captured_at_utc DESC);

CREATE TABLE IF NOT EXISTS saas_billing_events
(
    id                    TEXT PRIMARY KEY,
    organization_id       TEXT NOT NULL,
    subscription_id       TEXT NOT NULL,
    event_type            TEXT NOT NULL
                          CHECK (event_type IN
                          ('invoice', 'payment', 'credit', 'refund', 'adjustment')),
    external_reference    TEXT NOT NULL DEFAULT '',
    amount_minor          INTEGER NOT NULL DEFAULT 0,
    currency_code         TEXT NOT NULL DEFAULT 'UGX',
    status                TEXT NOT NULL DEFAULT 'pending'
                          CHECK (status IN ('pending', 'paid', 'failed', 'voided')),
    due_at_utc            TEXT NULL,
    occurred_at_utc       TEXT NOT NULL,
    details_json          TEXT NOT NULL DEFAULT '{}',
    created_by_user_id    TEXT NULL,
    created_at_utc        TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,
    FOREIGN KEY (subscription_id)
        REFERENCES saas_subscriptions(id)
        ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS ix_saas_billing_events_organization
    ON saas_billing_events(organization_id, occurred_at_utc DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ux_saas_billing_external_reference
    ON saas_billing_events(organization_id, external_reference)
    WHERE external_reference != '';

CREATE TABLE IF NOT EXISTS saas_platform_operators
(
    user_id               TEXT PRIMARY KEY,
    operator_role         TEXT NOT NULL
                          CHECK (operator_role IN ('owner', 'operator', 'support', 'read_only')),
    is_active             INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    version               INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    assigned_by_user_id   TEXT NULL,
    assigned_at_utc       TEXT NOT NULL,
    updated_at_utc        TEXT NOT NULL,

    FOREIGN KEY (user_id)
        REFERENCES users(id)
        ON DELETE CASCADE,
    FOREIGN KEY (assigned_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS saas_support_access_grants
(
    id                    TEXT PRIMARY KEY,
    organization_id       TEXT NOT NULL,
    operator_user_id      TEXT NOT NULL,
    access_scope          TEXT NOT NULL
                          CHECK (access_scope IN ('read_only', 'diagnostics', 'support')),
    reason                TEXT NOT NULL,
    expires_at_utc        TEXT NOT NULL,
    revoked_at_utc        TEXT NULL,
    revoked_by_user_id    TEXT NULL,
    version               INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id    TEXT NOT NULL,
    created_at_utc        TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE CASCADE,
    FOREIGN KEY (operator_user_id)
        REFERENCES users(id)
        ON DELETE CASCADE,
    FOREIGN KEY (revoked_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL,
    FOREIGN KEY (created_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_saas_support_grants_active
    ON saas_support_access_grants(organization_id, operator_user_id, expires_at_utc)
    WHERE revoked_at_utc IS NULL;

CREATE TABLE IF NOT EXISTS saas_support_cases
(
    id                    TEXT PRIMARY KEY,
    case_number           TEXT NOT NULL UNIQUE,
    organization_id       TEXT NOT NULL,
    shop_id               TEXT NULL,
    opened_by_user_id     TEXT NOT NULL,
    assigned_to_user_id   TEXT NULL,
    category              TEXT NOT NULL DEFAULT 'general',
    priority              TEXT NOT NULL DEFAULT 'normal'
                          CHECK (priority IN ('low', 'normal', 'high', 'urgent')),
    status                TEXT NOT NULL DEFAULT 'open'
                          CHECK (status IN ('open', 'in_progress', 'waiting', 'resolved', 'closed')),
    subject               TEXT NOT NULL,
    description           TEXT NOT NULL,
    resolution            TEXT NOT NULL DEFAULT '',
    version               INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_at_utc        TEXT NOT NULL,
    updated_at_utc        TEXT NOT NULL,
    resolved_at_utc       TEXT NULL,
    closed_at_utc         TEXT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,
    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE SET NULL,
    FOREIGN KEY (opened_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,
    FOREIGN KEY (assigned_to_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS ix_saas_support_cases_tenant
    ON saas_support_cases(organization_id, status, priority, updated_at_utc DESC);

CREATE TABLE IF NOT EXISTS saas_support_case_events
(
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    support_case_id       TEXT NOT NULL,
    event_type            TEXT NOT NULL,
    previous_status       TEXT NULL,
    new_status            TEXT NULL,
    note                  TEXT NOT NULL DEFAULT '',
    actor_user_id         TEXT NULL,
    occurred_at_utc       TEXT NOT NULL,

    FOREIGN KEY (support_case_id)
        REFERENCES saas_support_cases(id)
        ON DELETE RESTRICT,
    FOREIGN KEY (actor_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS ix_saas_support_case_events_case
    ON saas_support_case_events(support_case_id, occurred_at_utc);

CREATE TRIGGER IF NOT EXISTS tr_saas_support_case_events_immutable_update
BEFORE UPDATE ON saas_support_case_events
BEGIN
    SELECT RAISE(ABORT, 'saas_support_case_events_are_immutable');
END;

CREATE TRIGGER IF NOT EXISTS tr_saas_support_case_events_immutable_delete
BEFORE DELETE ON saas_support_case_events
BEGIN
    SELECT RAISE(ABORT, 'saas_support_case_events_are_immutable');
END;

CREATE TABLE IF NOT EXISTS saas_tenant_health_snapshots
(
    id                    TEXT PRIMARY KEY,
    organization_id       TEXT NOT NULL,
    health_status         TEXT NOT NULL
                          CHECK (health_status IN ('healthy', 'warning', 'critical')),
    schema_version        INTEGER NOT NULL DEFAULT 0 CHECK (schema_version >= 0),
    database_size_bytes   INTEGER NOT NULL DEFAULT 0 CHECK (database_size_bytes >= 0),
    active_shop_count     INTEGER NOT NULL DEFAULT 0 CHECK (active_shop_count >= 0),
    active_user_count     INTEGER NOT NULL DEFAULT 0 CHECK (active_user_count >= 0),
    open_support_count    INTEGER NOT NULL DEFAULT 0 CHECK (open_support_count >= 0),
    last_backup_at_utc    TEXT NULL,
    details_json          TEXT NOT NULL DEFAULT '{}',
    captured_by_user_id   TEXT NULL,
    captured_at_utc       TEXT NOT NULL,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE CASCADE,
    FOREIGN KEY (captured_by_user_id)
        REFERENCES users(id)
        ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS ix_saas_health_snapshots_organization
    ON saas_tenant_health_snapshots(organization_id, captured_at_utc DESC);

CREATE TRIGGER IF NOT EXISTS tr_saas_active_shop_limit_insert
BEFORE INSERT ON shops
WHEN NEW.is_active = 1
 AND EXISTS
 (
    SELECT 1
    FROM saas_subscriptions subscription
    JOIN saas_plans plan ON plan.id = subscription.plan_id
    JOIN saas_plan_entitlements entitlement
      ON entitlement.plan_id = plan.id
     AND entitlement.entitlement_key = 'max_active_shops'
    WHERE subscription.organization_id = NEW.organization_id
      AND subscription.status IN ('trialing', 'active', 'past_due')
      AND plan.enforcement_mode = 'hard'
      AND entitlement.is_enabled = 1
      AND entitlement.limit_value IS NOT NULL
      AND
      (
          SELECT COUNT(1)
          FROM shops existing_shop
          WHERE existing_shop.organization_id = NEW.organization_id
            AND existing_shop.is_active = 1
      ) >= entitlement.limit_value
 )
BEGIN
    SELECT RAISE(ABORT, 'saas_active_shop_limit_exceeded');
END;

CREATE TRIGGER IF NOT EXISTS tr_saas_active_shop_limit_update
BEFORE UPDATE OF is_active ON shops
WHEN OLD.is_active = 0 AND NEW.is_active = 1
 AND EXISTS
 (
    SELECT 1
    FROM saas_subscriptions subscription
    JOIN saas_plans plan ON plan.id = subscription.plan_id
    JOIN saas_plan_entitlements entitlement
      ON entitlement.plan_id = plan.id
     AND entitlement.entitlement_key = 'max_active_shops'
    WHERE subscription.organization_id = NEW.organization_id
      AND subscription.status IN ('trialing', 'active', 'past_due')
      AND plan.enforcement_mode = 'hard'
      AND entitlement.is_enabled = 1
      AND entitlement.limit_value IS NOT NULL
      AND
      (
          SELECT COUNT(1)
          FROM shops existing_shop
          WHERE existing_shop.organization_id = NEW.organization_id
            AND existing_shop.is_active = 1
      ) >= entitlement.limit_value
 )
BEGIN
    SELECT RAISE(ABORT, 'saas_active_shop_limit_exceeded');
END;

CREATE TRIGGER IF NOT EXISTS tr_saas_active_user_limit_insert
BEFORE INSERT ON user_shop_access
WHEN NEW.is_active = 1
 AND NOT EXISTS
 (
     SELECT 1
     FROM user_shop_access existing_access
     JOIN shops existing_shop ON existing_shop.id = existing_access.shop_id
     JOIN shops new_shop ON new_shop.id = NEW.shop_id
     WHERE existing_access.user_id = NEW.user_id
       AND existing_access.is_active = 1
       AND existing_shop.organization_id = new_shop.organization_id
 )
 AND EXISTS
 (
    SELECT 1
    FROM shops target_shop
    JOIN saas_subscriptions subscription
      ON subscription.organization_id = target_shop.organization_id
    JOIN saas_plans plan ON plan.id = subscription.plan_id
    JOIN saas_plan_entitlements entitlement
      ON entitlement.plan_id = plan.id
     AND entitlement.entitlement_key = 'max_active_users'
    WHERE target_shop.id = NEW.shop_id
      AND subscription.status IN ('trialing', 'active', 'past_due')
      AND plan.enforcement_mode = 'hard'
      AND entitlement.is_enabled = 1
      AND entitlement.limit_value IS NOT NULL
      AND
      (
          SELECT COUNT(DISTINCT access.user_id)
          FROM user_shop_access access
          JOIN shops access_shop ON access_shop.id = access.shop_id
          JOIN users access_user ON access_user.id = access.user_id
          WHERE access_shop.organization_id = target_shop.organization_id
            AND access.is_active = 1
            AND access_user.is_active = 1
      ) >= entitlement.limit_value
 )
BEGIN
    SELECT RAISE(ABORT, 'saas_active_user_limit_exceeded');
END;

INSERT OR IGNORE INTO schema_versions
(version, description, applied_at_utc)
VALUES
(
    16,
    'SaaS tenant operations, plans, subscriptions, usage, support and health',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
