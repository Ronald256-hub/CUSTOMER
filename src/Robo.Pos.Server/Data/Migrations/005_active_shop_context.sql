CREATE TABLE IF NOT EXISTS session_shop_contexts
(
    session_id          TEXT PRIMARY KEY,
    user_id             TEXT NOT NULL,
    organization_id     TEXT NOT NULL,
    shop_id             TEXT NOT NULL,
    selected_at_utc     TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    version             INTEGER NOT NULL DEFAULT 1
                        CHECK (version >= 1),

    FOREIGN KEY (session_id)
        REFERENCES sessions(id)
        ON DELETE CASCADE,

    FOREIGN KEY (user_id)
        REFERENCES users(id)
        ON DELETE CASCADE,

    FOREIGN KEY (organization_id)
        REFERENCES organizations(id)
        ON DELETE RESTRICT,

    FOREIGN KEY (shop_id)
        REFERENCES shops(id)
        ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_session_shop_context_user
    ON session_shop_contexts(user_id, shop_id);

CREATE INDEX IF NOT EXISTS ix_session_shop_context_shop
    ON session_shop_contexts(shop_id, updated_at_utc);

INSERT OR IGNORE INTO session_shop_contexts
(
    session_id,
    user_id,
    organization_id,
    shop_id,
    selected_at_utc,
    updated_at_utc,
    version
)
SELECT
    session.id,
    session.user_id,
    shop.organization_id,
    shop.id,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    1
FROM sessions AS session
INNER JOIN shops AS shop
    ON shop.id =
       (
           SELECT access.shop_id
           FROM user_shop_access AS access
           INNER JOIN shops AS available_shop
               ON available_shop.id = access.shop_id
              AND available_shop.is_active = 1
           WHERE access.user_id = session.user_id
             AND access.is_active = 1
           ORDER BY
               access.is_primary DESC,
               available_shop.is_head_office DESC,
               available_shop.name COLLATE NOCASE
           LIMIT 1
       );

INSERT INTO schema_versions
(
    version,
    description,
    applied_at_utc
)
VALUES
(
    5,
    'Explicit session-level organization and active-shop context',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
