ALTER TABLE channels
    ADD COLUMN activity_publish_channel_id BIGINT NULL
        REFERENCES channels (id) ON DELETE SET NULL;

ALTER TABLE channels
    ADD CONSTRAINT ck_channels_activity_publish_channel_voice_only
        CHECK (type = 2 OR activity_publish_channel_id IS NULL);
