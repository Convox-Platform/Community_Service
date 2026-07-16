CREATE TABLE communities
(
    id          BIGINT PRIMARY KEY,
    name        TEXT        NOT NULL,
    avatar      TEXT        NULL,
    description TEXT        NOT NULL DEFAULT '',
    owner_id    BIGINT      NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE community_members
(
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    community_id BIGINT      NOT NULL REFERENCES communities (id) ON DELETE CASCADE,
    user_id      BIGINT      NOT NULL,
    joined_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_community_members UNIQUE (community_id, user_id)
);

CREATE TABLE categories
(
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    community_id BIGINT NOT NULL REFERENCES communities (id) ON DELETE CASCADE,
    name         TEXT   NOT NULL,
    position     INT    NOT NULL DEFAULT 0
);

CREATE TABLE channels
(
    id           BIGINT PRIMARY KEY,
    community_id BIGINT   NOT NULL REFERENCES communities (id) ON DELETE CASCADE,
    -- NULL = канал вне категории; при удалении категории каналы становятся вне категории.
    category_id  BIGINT   NULL REFERENCES categories (id) ON DELETE SET NULL,
    name         TEXT     NOT NULL,
    type         SMALLINT NOT NULL,
    description  TEXT     NOT NULL DEFAULT '',
    position     INT      NOT NULL DEFAULT 0
);

CREATE TABLE meetings
(
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    community_id BIGINT      NOT NULL REFERENCES communities (id) ON DELETE CASCADE,
    channel_id   BIGINT      NOT NULL REFERENCES channels (id) ON DELETE CASCADE,
    name         TEXT        NOT NULL,
    description  TEXT        NOT NULL DEFAULT '',
    start_at     TIMESTAMPTZ NOT NULL
);

CREATE INDEX ix_community_members_user ON community_members (user_id);
CREATE INDEX ix_categories_community ON categories (community_id);
CREATE INDEX ix_channels_community ON channels (community_id);
CREATE INDEX ix_meetings_community ON meetings (community_id);
