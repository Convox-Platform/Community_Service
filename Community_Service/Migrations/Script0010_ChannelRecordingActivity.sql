-- Карточка записи голосового канала вне митинга. Держит id системного сообщения,
-- чтобы фазы записи обновляли одну карточку, а не сыпали новые сообщения в чат.
-- published_phase — фаза, которую message-service действительно принял: пока она
-- отстаёт от phase, повторная доставка события обязана попробовать ещё раз.
CREATE TABLE channel_recording_activity (
    recording_id        TEXT PRIMARY KEY,
    community_id        BIGINT NOT NULL,
    channel_id          BIGINT NOT NULL,
    started_by          BIGINT NOT NULL DEFAULT 0,
    phase               TEXT NOT NULL,
    published_phase     TEXT NULL,
    activity_message_id BIGINT NULL,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX ix_channel_recording_activity_channel
    ON channel_recording_activity (channel_id, updated_at DESC);

-- Порядок фаз. События приходят из RabbitMQ и переживают повторную доставку,
-- поэтому карточка двигается только вперёд: запоздавший старт не должен возвращать
-- готовую запись в «идёт запись». failed ниже ready намеренно — повторная обработка
-- обязана уметь перевести провалившуюся запись в готовую.
CREATE FUNCTION channel_recording_phase_rank(phase TEXT) RETURNS INT
    LANGUAGE SQL IMMUTABLE AS $$
    SELECT CASE phase
               WHEN 'live' THEN 1
               WHEN 'processing' THEN 2
               WHEN 'failed' THEN 3
               WHEN 'ready' THEN 4
               ELSE 0
           END;
$$;
