CREATE TABLE IF NOT EXISTS stock_transfer_audit_lines
(
    id                                INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id                          INTEGER NULL,
    transfer_id                       TEXT NOT NULL,
    transfer_item_id                  INTEGER NOT NULL,
    product_id                        TEXT NOT NULL,
    snapshot_kind                     TEXT NOT NULL
                                      CHECK (snapshot_kind IN ('migration_baseline', 'receipt_event')),
    requested_quantity_base_units     INTEGER NOT NULL CHECK (requested_quantity_base_units > 0),
    reserved_quantity_base_units      INTEGER NOT NULL CHECK (reserved_quantity_base_units >= 0),
    dispatched_quantity_base_units    INTEGER NOT NULL CHECK (dispatched_quantity_base_units >= 0),
    cumulative_received_base_units    INTEGER NOT NULL CHECK (cumulative_received_base_units >= 0),
    cumulative_damaged_base_units     INTEGER NOT NULL CHECK (cumulative_damaged_base_units >= 0),
    received_delta_base_units         INTEGER NOT NULL CHECK (received_delta_base_units >= 0),
    damaged_delta_base_units          INTEGER NOT NULL CHECK (damaged_delta_base_units >= 0),
    outstanding_quantity_base_units   INTEGER NOT NULL CHECK (outstanding_quantity_base_units >= 0),
    unit_cost_minor                   INTEGER NOT NULL CHECK (unit_cost_minor >= 0),
    discrepancy_reason                TEXT NOT NULL DEFAULT '',
    source_balance_before             INTEGER NULL,
    source_balance_after              INTEGER NULL,
    destination_balance_before        INTEGER NULL,
    destination_balance_after         INTEGER NULL,
    captured_at_utc                   TEXT NOT NULL,

    FOREIGN KEY (event_id)
        REFERENCES stock_transfer_events(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (transfer_id)
        REFERENCES stock_transfers(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (transfer_item_id)
        REFERENCES stock_transfer_items(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (product_id)
        REFERENCES products(id)
        ON DELETE RESTRICT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_stock_transfer_audit_event_item
    ON stock_transfer_audit_lines(event_id, transfer_item_id)
    WHERE event_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_stock_transfer_audit_baseline_item
    ON stock_transfer_audit_lines(transfer_item_id)
    WHERE snapshot_kind = 'migration_baseline';

CREATE INDEX IF NOT EXISTS ix_stock_transfer_audit_transfer
    ON stock_transfer_audit_lines(transfer_id, captured_at_utc, id);

INSERT OR IGNORE INTO stock_transfer_audit_lines
(
    event_id,
    transfer_id,
    transfer_item_id,
    product_id,
    snapshot_kind,
    requested_quantity_base_units,
    reserved_quantity_base_units,
    dispatched_quantity_base_units,
    cumulative_received_base_units,
    cumulative_damaged_base_units,
    received_delta_base_units,
    damaged_delta_base_units,
    outstanding_quantity_base_units,
    unit_cost_minor,
    discrepancy_reason,
    source_balance_before,
    source_balance_after,
    destination_balance_before,
    destination_balance_after,
    captured_at_utc
)
SELECT
    NULL,
    item.transfer_id,
    item.id,
    item.product_id,
    'migration_baseline',
    item.quantity_base_units,
    item.reserved_quantity_base_units,
    item.dispatched_quantity_base_units,
    item.received_quantity_base_units,
    item.damaged_quantity_base_units,
    0,
    0,
    MAX(
        item.dispatched_quantity_base_units -
        item.received_quantity_base_units -
        item.damaged_quantity_base_units,
        0),
    item.unit_cost_minor,
    item.discrepancy_reason,
    item.source_balance_before,
    item.source_balance_after,
    item.destination_before,
    item.destination_after,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM stock_transfer_items AS item
WHERE item.reserved_quantity_base_units > 0
   OR item.dispatched_quantity_base_units > 0
   OR item.received_quantity_base_units > 0
   OR item.damaged_quantity_base_units > 0;

CREATE TRIGGER IF NOT EXISTS trg_shop_stock_reservation_insert
BEFORE INSERT ON shop_stock_balances
BEGIN
    SELECT CASE
        WHEN NEW.reserved_base_units > MAX(NEW.quantity_base_units, 0)
        THEN RAISE(ABORT, 'reserved stock exceeds on-hand stock')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_shop_stock_reservation_update
BEFORE UPDATE OF quantity_base_units, reserved_base_units
ON shop_stock_balances
BEGIN
    SELECT CASE
        WHEN NEW.reserved_base_units > MAX(NEW.quantity_base_units, 0)
        THEN RAISE(ABORT, 'reserved stock exceeds on-hand stock')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_transfer_status_machine
BEFORE UPDATE OF status ON stock_transfers
WHEN NEW.status <> OLD.status
BEGIN
    SELECT CASE
        WHEN NOT
        (
            (OLD.status = 'draft' AND NEW.status IN ('submitted', 'cancelled'))
            OR (OLD.status = 'submitted' AND NEW.status IN ('approved', 'cancelled'))
            OR (OLD.status = 'approved' AND NEW.status IN ('in_transit', 'cancelled'))
            OR (OLD.status = 'in_transit' AND NEW.status = 'received')
        )
        THEN RAISE(ABORT, 'invalid stock transfer status transition')
    END;

    SELECT CASE
        WHEN NEW.status = 'submitted'
         AND NOT EXISTS
         (
             SELECT 1
             FROM stock_transfer_items
             WHERE transfer_id = NEW.id
         )
        THEN RAISE(ABORT, 'stock transfer items are required before submission')
    END;

    SELECT CASE
        WHEN NEW.status = 'approved'
         AND
         (
             NOT EXISTS
             (
                 SELECT 1
                 FROM stock_transfer_items
                 WHERE transfer_id = NEW.id
             )
             OR EXISTS
             (
                 SELECT 1
                 FROM stock_transfer_items
                 WHERE transfer_id = NEW.id
                   AND reserved_quantity_base_units <> quantity_base_units
             )
         )
        THEN RAISE(ABORT, 'approved transfer must be fully reserved')
    END;

    SELECT CASE
        WHEN NEW.status = 'in_transit'
         AND EXISTS
         (
             SELECT 1
             FROM stock_transfer_items
             WHERE transfer_id = NEW.id
               AND
               (
                   reserved_quantity_base_units <> 0
                   OR dispatched_quantity_base_units <> quantity_base_units
               )
         )
        THEN RAISE(ABORT, 'in-transit transfer must be fully dispatched and unreserved')
    END;

    SELECT CASE
        WHEN NEW.status = 'received'
         AND EXISTS
         (
             SELECT 1
             FROM stock_transfer_items
             WHERE transfer_id = NEW.id
               AND dispatched_quantity_base_units <>
                   received_quantity_base_units + damaged_quantity_base_units
         )
        THEN RAISE(ABORT, 'received transfer has unaccounted quantities')
    END;

    SELECT CASE
        WHEN NEW.status = 'cancelled'
         AND OLD.status = 'approved'
         AND EXISTS
         (
             SELECT 1
             FROM stock_transfer_items
             WHERE transfer_id = NEW.id
               AND reserved_quantity_base_units <> 0
         )
        THEN RAISE(ABORT, 'approved transfer reservations must be released before cancellation')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_transfer_item_workflow_update
BEFORE UPDATE ON stock_transfer_items
BEGIN
    SELECT CASE
        WHEN NEW.transfer_id <> OLD.transfer_id
        THEN RAISE(ABORT, 'stock transfer item ownership is immutable')
    END;

    SELECT CASE
        WHEN
        (
            NEW.product_id <> OLD.product_id
            OR NEW.quantity_base_units <> OLD.quantity_base_units
        )
        AND
        (
            SELECT status
            FROM stock_transfers
            WHERE id = OLD.transfer_id
        ) <> 'draft'
        THEN RAISE(ABORT, 'submitted stock transfer items are immutable')
    END;

    SELECT CASE
        WHEN NEW.reserved_quantity_base_units > OLD.reserved_quantity_base_units
         AND
         (
             SELECT status
             FROM stock_transfers
             WHERE id = OLD.transfer_id
         ) <> 'submitted'
        THEN RAISE(ABORT, 'stock can only be reserved during approval')
    END;

    SELECT CASE
        WHEN NEW.reserved_quantity_base_units < OLD.reserved_quantity_base_units
         AND
         (
             SELECT status
             FROM stock_transfers
             WHERE id = OLD.transfer_id
         ) <> 'approved'
        THEN RAISE(ABORT, 'stock reservations can only be released before dispatch or cancellation')
    END;

    SELECT CASE
        WHEN NEW.dispatched_quantity_base_units < OLD.dispatched_quantity_base_units
        THEN RAISE(ABORT, 'dispatched stock cannot be reduced')
    END;

    SELECT CASE
        WHEN NEW.dispatched_quantity_base_units > OLD.dispatched_quantity_base_units
         AND
         (
             SELECT status
             FROM stock_transfers
             WHERE id = OLD.transfer_id
         ) <> 'approved'
        THEN RAISE(ABORT, 'stock can only enter transit during dispatch')
    END;

    SELECT CASE
        WHEN
        (
            NEW.received_quantity_base_units < OLD.received_quantity_base_units
            OR NEW.damaged_quantity_base_units < OLD.damaged_quantity_base_units
        )
        THEN RAISE(ABORT, 'received and discrepancy quantities cannot be reduced')
    END;

    SELECT CASE
        WHEN
        (
            NEW.received_quantity_base_units > OLD.received_quantity_base_units
            OR NEW.damaged_quantity_base_units > OLD.damaged_quantity_base_units
        )
        AND
        (
            SELECT status
            FROM stock_transfers
            WHERE id = OLD.transfer_id
        ) <> 'in_transit'
        THEN RAISE(ABORT, 'stock can only be received from an in-transit transfer')
    END;

    SELECT CASE
        WHEN NEW.unit_cost_minor <> OLD.unit_cost_minor
         AND
         (
             SELECT status
             FROM stock_transfers
             WHERE id = OLD.transfer_id
         ) <> 'submitted'
        THEN RAISE(ABORT, 'transfer unit cost is immutable after approval')
    END;

    SELECT CASE
        WHEN NEW.discrepancy_reason <> OLD.discrepancy_reason
         AND
         (
             SELECT status
             FROM stock_transfers
             WHERE id = OLD.transfer_id
         ) <> 'in_transit'
        THEN RAISE(ABORT, 'discrepancy reasons can only be recorded while receiving')
    END;

    SELECT CASE
        WHEN OLD.discrepancy_reason <> ''
         AND NEW.discrepancy_reason <> OLD.discrepancy_reason
         AND substr(
                 NEW.discrepancy_reason,
                 1,
                 length(OLD.discrepancy_reason) + 2) <>
             OLD.discrepancy_reason || '; '
        THEN RAISE(ABORT, 'discrepancy history is append-only')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_transfer_item_delete_guard
BEFORE DELETE ON stock_transfer_items
BEGIN
    SELECT CASE
        WHEN
        (
            SELECT status
            FROM stock_transfers
            WHERE id = OLD.transfer_id
        ) <> 'draft'
        THEN RAISE(ABORT, 'only draft stock transfer items can be deleted')
    END;
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_transfer_receipt_snapshot
AFTER INSERT ON stock_transfer_events
WHEN NEW.event_type IN ('transfer.partially_received', 'transfer.received')
BEGIN
    INSERT INTO stock_transfer_audit_lines
    (
        event_id,
        transfer_id,
        transfer_item_id,
        product_id,
        snapshot_kind,
        requested_quantity_base_units,
        reserved_quantity_base_units,
        dispatched_quantity_base_units,
        cumulative_received_base_units,
        cumulative_damaged_base_units,
        received_delta_base_units,
        damaged_delta_base_units,
        outstanding_quantity_base_units,
        unit_cost_minor,
        discrepancy_reason,
        source_balance_before,
        source_balance_after,
        destination_balance_before,
        destination_balance_after,
        captured_at_utc
    )
    SELECT
        NEW.id,
        item.transfer_id,
        item.id,
        item.product_id,
        'receipt_event',
        item.quantity_base_units,
        item.reserved_quantity_base_units,
        item.dispatched_quantity_base_units,
        item.received_quantity_base_units,
        item.damaged_quantity_base_units,
        item.received_quantity_base_units - COALESCE
        (
            (
                SELECT previous.cumulative_received_base_units
                FROM stock_transfer_audit_lines AS previous
                WHERE previous.transfer_item_id = item.id
                ORDER BY previous.id DESC
                LIMIT 1
            ),
            0
        ),
        item.damaged_quantity_base_units - COALESCE
        (
            (
                SELECT previous.cumulative_damaged_base_units
                FROM stock_transfer_audit_lines AS previous
                WHERE previous.transfer_item_id = item.id
                ORDER BY previous.id DESC
                LIMIT 1
            ),
            0
        ),
        MAX(
            item.dispatched_quantity_base_units -
            item.received_quantity_base_units -
            item.damaged_quantity_base_units,
            0),
        item.unit_cost_minor,
        item.discrepancy_reason,
        item.source_balance_before,
        item.source_balance_after,
        item.destination_before,
        item.destination_after,
        NEW.occurred_at_utc
    FROM stock_transfer_items AS item
    WHERE item.transfer_id = NEW.transfer_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_transfer_events_immutable_update
BEFORE UPDATE ON stock_transfer_events
BEGIN
    SELECT RAISE(ABORT, 'stock transfer events are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_transfer_events_immutable_delete
BEFORE DELETE ON stock_transfer_events
BEGIN
    SELECT RAISE(ABORT, 'stock transfer events are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_transfer_audit_lines_immutable_update
BEFORE UPDATE ON stock_transfer_audit_lines
BEGIN
    SELECT RAISE(ABORT, 'stock transfer audit lines are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_transfer_audit_lines_immutable_delete
BEFORE DELETE ON stock_transfer_audit_lines
BEGIN
    SELECT RAISE(ABORT, 'stock transfer audit lines are immutable');
END;

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    9,
    'Database-enforced stock transfer state machine and immutable line-level receipt audit',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
