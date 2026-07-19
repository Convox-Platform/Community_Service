ALTER TABLE channels
    ADD COLUMN bitrate INT NULL;

UPDATE channels
SET bitrate = 64000
WHERE type = 2;

ALTER TABLE channels
    ADD CONSTRAINT ck_channels_bitrate_positive
        CHECK (bitrate IS NULL OR bitrate > 0),
    ADD CONSTRAINT ck_channels_bitrate_voice_only
        CHECK ((type = 2 AND bitrate IS NOT NULL) OR (type <> 2 AND bitrate IS NULL));
