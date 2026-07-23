using Dapper;
using Community_Service.Events;
using Npgsql;

namespace Community_Service.Data
{
    public class MeetingRepository
    {
        public const short Scheduled = 1;
        public const short Live = 2;
        public const short Ended = 3;
        public const short Cancelled = 4;

        private readonly NpgsqlDataSource _db;
        private readonly OutboxWriter _outbox;

        public MeetingRepository(NpgsqlDataSource db, OutboxWriter outbox)
        {
            _db = db;
            _outbox = outbox;
        }

        public async Task<MeetingEntity> CreateAsync(
            long communityId, long channelId, string name, string description, DateTime startAt,
            ulong actorUserId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var meeting = await conn.QuerySingleAsync<MeetingEntity>(
                @"INSERT INTO meetings (community_id, channel_id, name, description, start_at, status)
                  VALUES (@communityId, @channelId, @name, @description, @startAt, @status)
                  RETURNING *;",
                new { communityId, channelId, name, description, startAt, status = Scheduled }, tx);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.MeetingCreated(meeting, actorUserId));
            await tx.CommitAsync();
            return meeting;
        }

        public async Task<MeetingEntity?> GetByIdAsync(long id)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<MeetingEntity>(
                "SELECT * FROM meetings WHERE id = @id;", new { id });
        }

        public async Task<IReadOnlyList<MeetingEntity>> ListByCommunityAsync(long communityId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<MeetingEntity>(
                @"SELECT * FROM meetings
                  WHERE community_id = @communityId AND status IN (@scheduled, @live)
                  ORDER BY start_at, id;",
                new { communityId, scheduled = Scheduled, live = Live });
            return rows.AsList();
        }

        public async Task<MeetingEntity> UpdateAsync(
            long id, string? name, string? description, DateTime? startAt,
            ulong actorUserId, IEnumerable<string> changedFields)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var meeting = await conn.QuerySingleAsync<MeetingEntity>(
                @"UPDATE meetings
                  SET name        = COALESCE(@name, name),
                      description  = COALESCE(@description, description),
                      start_at     = COALESCE(@startAt, start_at)
                  WHERE id = @id
                  RETURNING *;",
                new { id, name, description, startAt }, tx);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.MeetingUpdated(meeting, actorUserId, changedFields));
            await tx.CommitAsync();
            return meeting;
        }

        public async Task<MeetingEntity> StartAsync(long id, string recordingId, ulong actorUserId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var meeting = await conn.QuerySingleOrDefaultAsync<MeetingEntity>(
                @"UPDATE meetings
                  SET status = @live, started_at = COALESCE(started_at, now()), recording_id = @recordingId
                  WHERE id = @id AND status = @scheduled
                  RETURNING *;",
                new { id, recordingId, live = Live, scheduled = Scheduled }, tx);
            if (meeting is null)
                throw new InvalidOperationException("Meeting cannot be started in its current state");
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.MeetingUpdated(meeting, actorUserId,
                    new[] { "status", "started_at", "recording_id" }));
            await tx.CommitAsync();
            return meeting;
        }

        public async Task<MeetingEntity> EndAsync(long id, ulong actorUserId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var meeting = await conn.QuerySingleOrDefaultAsync<MeetingEntity>(
                @"UPDATE meetings
                  SET status = @ended, ended_at = COALESCE(ended_at, now())
                  WHERE id = @id AND status = @live
                  RETURNING *;",
                new { id, ended = Ended, live = Live }, tx);
            if (meeting is null)
                throw new InvalidOperationException("Meeting cannot be ended in its current state");
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.MeetingUpdated(meeting, actorUserId,
                    new[] { "status", "ended_at" }));
            await tx.CommitAsync();
            return meeting;
        }

        public async Task<MeetingEntity> CancelAsync(long id, ulong actorUserId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var meeting = await conn.QuerySingleOrDefaultAsync<MeetingEntity>(
                @"UPDATE meetings SET status = @cancelled
                  WHERE id = @id AND status = @scheduled
                  RETURNING *;",
                new { id, cancelled = Cancelled, scheduled = Scheduled }, tx);
            if (meeting is null)
                throw new InvalidOperationException("Only scheduled meetings can be cancelled");
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.MeetingUpdated(meeting, actorUserId,
                    new[] { "status" }));
            await tx.CommitAsync();
            return meeting;
        }

        public async Task<MeetingEntity?> GetByRecordingIdAsync(string recordingId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<MeetingEntity>(
                "SELECT * FROM meetings WHERE recording_id = @recordingId;", new { recordingId });
        }

        public async Task SetActivityMessageIdAsync(long id, long messageId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "UPDATE meetings SET activity_message_id = @messageId WHERE id = @id;",
                new { id, messageId });
        }

        public async Task DeleteAsync(long id, ulong actorUserId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var meeting = await conn.QuerySingleAsync<MeetingEntity>(
                "SELECT * FROM meetings WHERE id = @id FOR UPDATE;", new { id }, tx);
            await conn.ExecuteAsync("DELETE FROM meetings WHERE id = @id;", new { id }, tx);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.MeetingDeleted(meeting, actorUserId));
            await tx.CommitAsync();
        }
    }
}
