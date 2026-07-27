ALTER TABLE stock_transfers
ADD COLUMN submitted_by_user_id TEXT NULL REFERENCES users(id);

ALTER TABLE stock_transfers
ADD COLUMN submitted_at_utc TEXT NULL;

ALTER TABLE stock_transfers
ADD COLUMN cancelled_by_user_id TEXT NULL REFERENCES users(id);

ALTER TABLE stock_transfers
ADD COLUMN cancellation_kind TEXT NULL
    CHECK (cancellation_kind IS NULL OR cancellation_kind IN ('cancelled', 'rejected'));

ALTER TABLE stock_transfers
ADD COLUMN updated_at_utc TEXT NULL;

UPDATE stock_transfers
SET updated_at_utc = COALESCE(
    received_at_utc,
    dispatched_at_utc,
    approved_at_utc,
    submitted_at_utc,
    cancelled_at_utc,
    created_at_utc)
WHERE updated_at_utc IS NULL;

ALTER TABLE stock_transfer_items
ADD COLUMN reserved_quantity_base_units INTEGER NOT NULL DEFAULT 0
    CHECK (reserved_quantity_base_units >= 0);

ALTER TABLE stock_transfer_items
ADD COLUMN dispatched_quantity_base_units INTEGER NOT NULL DEFAULT 0
    CHECK (dispatched_quantity_base_units >= 0);

ALTER TABLE stock_transfer_items
ADD COLUMN received_quantity_base_units INTEGER NOT NULL DEFAULT 0
    CHECK (received_quantity_base_units >= 0);

ALTER TABLE stock_transfer_items
ADD COLUMN damaged_quantity_base_units INTEGER NOT NULL DEFAULT 0
    CHECK (damaged_quantity_base_units >= 0);

ALTER TABLE stock_transfer_items
ADD COLUMN unit_cost_minor INTEGER NOT NULL DEFAULT 0
    CHECK (unit_cost_minor >= 0);

ALTER TABLE stock_transfer_items
ADD COLUMN discrepancy_reason TEXT NOT NULL DEFAULT '';

CREATE TABLE IF NOT EXISTS shop_transfer_sequences
(
    shop_id        TEXT PRIMARY KEY,
    next_value     INTEGER NOT NULL DEFAULT 1 CHECK (next_value >= 1),
    updated_at_utc TEXT NOT NULL,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE CASCADE
);

INSERT OR IGNORE INTO shop_transfer_sequences
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

CREATE TABLE IF NOT EXISTS stock_transfer_events
(
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    transfer_id        TEXT NOT NULL,
    event_type         TEXT NOT NULL,
    from_status        TEXT NULL,
    to_status          TEXT NOT NULL,
    details_json       TEXT NOT NULL DEFAULT '{}',
    performed_by_user_id TEXT NOT NULL,
    occurred_at_utc    TEXT NOT NULL,

    FOREIGN KEY (transfer_id)
        REFERENCES stock_transfers(id)
        ON DELETE CASCADE,

    FOREIGN KEY (performed_by_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_stock_transfer_events_transfer
    ON stock_transfer_events(transfer_id, occurred_at_utc, id);

CREATE INDEX IF NOT EXISTS ix_stock_transfers_status_updated
    ON stock_transfers(status, updated_at_utc);

CREATE TRIGGER IF NOT EXISTS trg_stock_transfer_item_quantities_insert
BEFORE INSERT ON stock_transfer_items
BEGIN
    SELECT CASE
        WHEN NEW.reserved_quantity_base_units > NEW.quantity_base_units
          OR NEW.dispatched_quantity_base_units > NEW.quantity_base_units
          OR NEW.received_quantity_base_units + NEW.damaged_quantity_base_units > NEW.quantity_base_units
        THEN RAISE(ABORT, 'invalid stock transfer item quantities')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_transfer_item_quantities_update
BEFORE UPDATE OF
    quantity_base_units,
    reserved_quantity_base_units,
    dispatched_quantity_base_units,
    received_quantity_base_units,
    damaged_quantity_base_units
ON stock_transfer_items
BEGIN
    SELECT CASE
        WHEN NEW.reserved_quantity_base_units > NEW.quantity_base_units
          OR NEW.dispatched_quantity_base_units > NEW.quantity_base_units
          OR NEW.received_quantity_base_units + NEW.damaged_quantity_base_units > NEW.dispatched_quantity_base_units
        THEN RAISE(ABORT, 'invalid stock transfer item quantities')
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
    8,
    'Audited inter-shop stock reservations, dispatch, transit and receiving',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);