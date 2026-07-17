CREATE TABLE community_invite_codes
(
    code       VARCHAR(12) COLLATE "C" PRIMARY KEY,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT ck_community_invite_codes_code CHECK (code ~ '^[A-Za-z0-9]{1,12}$')
);

CREATE TABLE community_invites
(
    code            VARCHAR(12) COLLATE "C" PRIMARY KEY REFERENCES community_invite_codes (code),
    community_id    BIGINT      NOT NULL REFERENCES communities (id) ON DELETE CASCADE,
    creator_user_id BIGINT      NOT NULL,
    max_uses        INT         NULL CHECK (max_uses > 0),
    uses_count      INT         NOT NULL DEFAULT 0 CHECK (uses_count >= 0),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT ck_community_invites_code CHECK (code ~ '^[A-Za-z0-9]{1,12}$')
);

-- Intentionally no FK to community_invites: invite deletion must preserve usage attribution.
CREATE TABLE community_invite_users
(
    community_id    BIGINT      NOT NULL REFERENCES communities (id) ON DELETE CASCADE,
    invite_code     VARCHAR(12) COLLATE "C" NOT NULL,
    user_id         BIGINT      NOT NULL,
    first_joined_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_joined_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (invite_code, user_id),
    FOREIGN KEY (invite_code) REFERENCES community_invite_codes (code),
    CONSTRAINT ck_community_invite_users_code CHECK (invite_code ~ '^[A-Za-z0-9]{1,12}$')
);

CREATE INDEX ix_community_invites_community
    ON community_invites (community_id, created_at DESC, code);

CREATE INDEX ix_community_invite_users_attribution
    ON community_invite_users (community_id, user_id, last_joined_at DESC);
