CREATE TABLE IF NOT EXISTS cash_drawer_movements
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    shift_id                 TEXT NOT NULL,
    movement_number          TEXT NOT NULL,
    movement_type            TEXT NOT NULL
                             CHECK (movement_type IN ('float_in', 'safe_drop')),
    amount_minor             INTEGER NOT NULL CHECK (amount_minor > 0),
    reason                   TEXT NOT NULL,
    reference                TEXT NOT NULL DEFAULT '',
    status                   TEXT NOT NULL DEFAULT 'completed'
                             CHECK (status = 'completed'),
    created_by_user_id       TEXT NOT NULL,
    approved_by_user_id      TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (shift_id) REFERENCES teller_shifts(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (approved_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,

    UNIQUE (organization_id, movement_number)
);

CREATE INDEX IF NOT EXISTS ix_cash_drawer_movements_shift
    ON cash_drawer_movements(shift_id, created_at_utc);
CREATE INDEX IF NOT EXISTS ix_cash_drawer_movements_shop
    ON cash_drawer_movements(organization_id, shop_id, created_at_utc DESC);

CREATE TABLE IF NOT EXISTS shift_cash_counts
(
    id                       TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    shift_id                 TEXT NOT NULL,
    count_type               TEXT NOT NULL
                             CHECK (count_type IN ('interim', 'closing')),
    total_minor              INTEGER NOT NULL CHECK (total_minor >= 0),
    denominations_json       TEXT NOT NULL,
    notes                    TEXT NOT NULL DEFAULT '',
    counted_by_user_id       TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,

    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (shift_id) REFERENCES teller_shifts(id) ON DELETE RESTRICT,
    FOREIGN KEY (counted_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_shift_cash_counts_shift
    ON shift_cash_counts(shift_id, created_at_utc DESC);

CREATE TABLE IF NOT EXISTS shift_reconciliation_reviews
(
    shift_id                 TEXT PRIMARY KEY,
    organization_id          TEXT NOT NULL,
    shop_id                  TEXT NOT NULL,
    review_status            TEXT NOT NULL DEFAULT 'pending'
                             CHECK (review_status IN ('pending', 'approved', 'rejected')),
    expected_cash_minor      INTEGER NOT NULL CHECK (expected_cash_minor >= 0),
    counted_cash_minor       INTEGER NOT NULL CHECK (counted_cash_minor >= 0),
    variance_minor           INTEGER NOT NULL,
    review_notes             TEXT NOT NULL DEFAULT '',
    reviewed_by_user_id      TEXT NULL,
    created_at_utc           TEXT NOT NULL,
    reviewed_at_utc          TEXT NULL,

    FOREIGN KEY (shift_id) REFERENCES teller_shifts(id) ON DELETE RESTRICT,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (reviewed_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_shift_reconciliation_reviews_status
    ON shift_reconciliation_reviews(organization_id, shop_id, review_status, created_at_utc DESC);

CREATE TRIGGER IF NOT EXISTS trg_cash_drawer_movement_scope_insert
BEFORE INSERT ON cash_drawer_movements
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1
        FROM teller_shifts AS shift
        INNER JOIN shops AS shop ON shop.id = shift.shop_id
        WHERE shift.id = NEW.shift_id
          AND shift.shop_id = NEW.shop_id
          AND shift.status = 'open'
          AND shop.organization_id = NEW.organization_id
    ) THEN RAISE(ABORT, 'cash drawer movement requires an open shift in the active branch') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_cash_drawer_movement_update
BEFORE UPDATE ON cash_drawer_movements
BEGIN
    SELECT RAISE(ABORT, 'cash drawer movements are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_cash_drawer_movement_delete
BEFORE DELETE ON cash_drawer_movements
BEGIN
    SELECT RAISE(ABORT, 'cash drawer movements are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_shift_cash_count_scope_insert
BEFORE INSERT ON shift_cash_counts
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1
        FROM teller_shifts AS shift
        INNER JOIN shops AS shop ON shop.id = shift.shop_id
        WHERE shift.id = NEW.shift_id
          AND shift.shop_id = NEW.shop_id
          AND shop.organization_id = NEW.organization_id
          AND (NEW.count_type = 'closing' OR shift.status = 'open')
    ) THEN RAISE(ABORT, 'cash count is outside the shift branch scope') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_shift_cash_count_update
BEFORE UPDATE ON shift_cash_counts
BEGIN
    SELECT RAISE(ABORT, 'cash counts are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_shift_cash_count_delete
BEFORE DELETE ON shift_cash_counts
BEGIN
    SELECT RAISE(ABORT, 'cash counts are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_shift_review_state
BEFORE UPDATE OF review_status ON shift_reconciliation_reviews
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.review_status = NEW.review_status
        OR (OLD.review_status = 'pending' AND NEW.review_status IN ('approved', 'rejected'))
    ) THEN RAISE(ABORT, 'invalid shift reconciliation review transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_shift_review_delete
BEFORE DELETE ON shift_reconciliation_reviews
BEGIN
    SELECT RAISE(ABORT, 'shift reconciliation reviews are permanent audit records');
END;

CREATE TRIGGER IF NOT EXISTS trg_shift_close_cash_custody
AFTER UPDATE OF status ON teller_shifts
WHEN OLD.status = 'open' AND NEW.status = 'closed'
BEGIN
    INSERT OR IGNORE INTO shift_reconciliation_reviews
    (
        shift_id, organization_id, shop_id, review_status,
        expected_cash_minor, counted_cash_minor, variance_minor,
        created_at_utc
    )
    SELECT
        NEW.id,
        shop.organization_id,
        NEW.shop_id,
        'pending',
        NEW.expected_cash_minor,
        NEW.counted_cash_minor,
        NEW.cash_variance_minor,
        NEW.closed_at_utc
    FROM shops AS shop
    WHERE shop.id = NEW.shop_id;
END;

INSERT OR IGNORE INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    19,
    'Cash drawer custody movements, denomination counts and shift reconciliation reviews',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
