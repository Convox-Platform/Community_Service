ALTER TABLE meetings
    ADD COLUMN status SMALLINT NOT NULL DEFAULT 1,
    ADD COLUMN started_at TIMESTAMPTZ NULL,
    ADD COLUMN ended_at TIMESTAMPTZ NULL,
    ADD COLUMN recording_id TEXT NULL,
    ADD COLUMN activity_message_id BIGINT NULL;

ALTER TABLE meetings
    ADD CONSTRAINT ck_meetings_status CHECK (status IN (1, 2, 3, 4));

CREATE UNIQUE INDEX ux_meetings_recording_id
    ON meetings (recording_id)
    WHERE recording_id IS NOT NULL;

CREATE INDEX ix_meetings_community_status_start
    ON meetings (community_id, status, start_at, id);
