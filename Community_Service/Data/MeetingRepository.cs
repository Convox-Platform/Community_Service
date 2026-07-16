using Dapper;
using Community_Service.Events;
using Npgsql;

namespace Community_Service.Data
{
    public class MeetingRepository
    {
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
                @"INSERT INTO meetings (community_id, channel_id, name, description, start_at)
                  VALUES (@communityId, @channelId, @name, @description, @startAt)
                  RETURNING *;",
                new { communityId, channelId, name, description, startAt }, tx);
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
                "SELECT * FROM meetings WHERE community_id = @communityId ORDER BY start_at, id;",
                new { communityId });
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
