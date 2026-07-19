ALTER TABLE community_members
    ADD COLUMN sort_order INT;

-- Сохраняем детерминированный порядок для существующих membership:
-- последнее присоединение оказывается выше более старых.
WITH ranked AS (
    SELECT id,
           ROW_NUMBER() OVER (
               PARTITION BY user_id
               ORDER BY joined_at DESC, id DESC
           ) - 1 AS position
    FROM community_members
)
UPDATE community_members AS member
SET sort_order = ranked.position::INT
FROM ranked
WHERE ranked.id = member.id;

ALTER TABLE community_members
    ALTER COLUMN sort_order SET NOT NULL,
    ALTER COLUMN sort_order SET DEFAULT 0;

CREATE INDEX ix_community_members_user_sort_order
    ON community_members (user_id, sort_order, id);
