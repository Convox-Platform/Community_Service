using Dapper;
using Npgsql;

namespace Community_Service.Data
{
    public class RecordingActivityRepository
    {
        private readonly NpgsqlDataSource _db;

        public RecordingActivityRepository(NpgsqlDataSource db)
        {
            _db = db;
        }

        public async Task<RecordingActivityEntity?> GetAsync(string recordingId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<RecordingActivityEntity>(
                "SELECT * FROM channel_recording_activity WHERE recording_id = @recordingId;",
                new { recordingId });
        }

        /// <summary>
        /// Заводит или продвигает карточку. Возвращает null, если публиковать нечего:
        /// фаза старее сохранённой либо ровно та, что уже уехала в чат. Та же фаза при
        /// неуехавшей публикации проходит — так повторная доставка добивает карточку,
        /// если message-service лежал в прошлый раз. Порядок фаз задан в SQL
        /// (channel_recording_phase_rank), чтобы источник истины был один.
        /// </summary>
        public async Task<RecordingActivityEntity?> AdvanceAsync(
            string recordingId, long communityId, long channelId, long startedBy, string phase)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<RecordingActivityEntity>(
                @"INSERT INTO channel_recording_activity
                      (recording_id, community_id, channel_id, started_by, phase)
                  VALUES (@recordingId, @communityId, @channelId, @startedBy, @phase)
                  ON CONFLICT (recording_id) DO UPDATE
                      SET phase = EXCLUDED.phase,
                          updated_at = now()
                      WHERE channel_recording_phase_rank(EXCLUDED.phase)
                            > channel_recording_phase_rank(channel_recording_activity.phase)
                         OR (channel_recording_phase_rank(EXCLUDED.phase)
                             = channel_recording_phase_rank(channel_recording_activity.phase)
                             AND channel_recording_activity.published_phase
                                 IS DISTINCT FROM channel_recording_activity.phase)
                  RETURNING *;",
                new { recordingId, communityId, channelId, startedBy, phase });
        }

        /// <summary>Отмечает фазу как доехавшую до чата.</summary>
        public async Task MarkPublishedAsync(string recordingId, string phase, long messageId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                @"UPDATE channel_recording_activity
                  SET activity_message_id = @messageId,
                      published_phase = @phase
                  WHERE recording_id = @recordingId;",
                new { recordingId, phase, messageId });
        }
    }
}
