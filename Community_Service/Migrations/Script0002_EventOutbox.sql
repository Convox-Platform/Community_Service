CREATE TABLE outbox_events
(
    event_id       UUID        PRIMARY KEY,
    correlation_id UUID        NOT NULL,
    event_type     TEXT        NOT NULL,
    payload        BYTEA       NOT NULL,
    occurred_at    TIMESTAMPTZ NOT NULL
);

CREATE TABLE outbox_destinations
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id        UUID        NOT NULL REFERENCES outbox_events (event_id) ON DELETE CASCADE,
    routing_key     TEXT        NOT NULL,
    published_at    TIMESTAMPTZ NULL,
    attempt_count   INT         NOT NULL DEFAULT 0,
    next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    lease_until     TIMESTAMPTZ NULL,
    last_error      TEXT        NULL,
    CONSTRAINT uq_outbox_destination UNIQUE (event_id, routing_key)
);

CREATE INDEX ix_outbox_destinations_pending
    ON outbox_destinations (next_attempt_at, id)
    WHERE published_at IS NULL;

CREATE INDEX ix_outbox_events_occurred_at ON outbox_events (occurred_at);
