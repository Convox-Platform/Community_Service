using Dapper;
using Community_Service.Events;
using Community_Service.Ids;
using Npgsql;

namespace Community_Service.Data
{
    public enum DeleteCommunityStatus
    {
        Deleted,
        NotFound,
        NotOwner
    }

    public enum TransferCommunityOwnershipStatus
    {
        Transferred,
        NotFound,
        NotOwner,
        NewOwnerNotMember,
        AlreadyOwner
    }

    public readonly record struct TransferCommunityOwnershipResult(
        TransferCommunityOwnershipStatus Status,
        ulong PreviousOwnerUserId = 0,
        ulong NewOwnerUserId = 0);

    public class CommunityRepository
    {
        private readonly NpgsqlDataSource _db;
        private readonly OutboxWriter _outbox;
        private readonly SnowflakeIdGenerator _ids;

        public CommunityRepository(
            NpgsqlDataSource db,
            OutboxWriter outbox,
            SnowflakeIdGenerator ids)
        {
            _db = db;
            _outbox = outbox;
            _ids = ids;
        }

        // Создаёт комьюнити вместе с владельцем-участником, переданными участниками
        // и дефолтной структурой (1 категория + 1 текстовый канал) в одной транзакции.
        public async Task<CommunityEntity> CreateAsync(
            string name, string? avatar, string description,
            ulong ownerId, IEnumerable<ulong> memberIds)
        {
            var communityId = _ids.NextId();
            var channelId = _ids.NextId();
            var storedOwnerId = UInt64Storage.ToInt64(ownerId);
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var community = await conn.QuerySingleAsync<CommunityEntity>(
                @"INSERT INTO communities (id, name, avatar, description, owner_id)
                  VALUES (@communityId, @name, @avatar, @description, @ownerId)
                  RETURNING *;",
                new { communityId, name, avatar, description, ownerId = storedOwnerId }, tx);

            var members = new HashSet<ulong>(memberIds) { ownerId };
            var insertedMembers = new List<MemberEntity>(members.Count);
            foreach (var uid in members.OrderBy(uid => UInt64Storage.ToInt64(uid)))
            {
                var storedUserId = UInt64Storage.ToInt64(uid);
                var result = await MembershipOrderStore.AddAtTopAsync(
                    conn, tx, community.Id, storedUserId);
                if (result.Inserted)
                    insertedMembers.Add(result.Member);
            }

            var categoryId = await conn.QuerySingleAsync<long>(
                @"INSERT INTO categories (community_id, name, position)
                  VALUES (@cid, @name, 0) RETURNING id;",
                new { cid = community.Id, name = "General" }, tx);

            await conn.ExecuteAsync(
                @"INSERT INTO channels (id, community_id, category_id, name, type, position)
                  VALUES (@channelId, @cid, @catId, @name, @type, 0);",
                new { channelId, cid = community.Id, catId = categoryId, name = "general", type = (short)ChannelType.Text }, tx);

            community.MembersCount = members.Count;
            community.SortOrder = 0;
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.CommunityCreated(community, ownerId));
            foreach (var member in insertedMembers)
            {
                await _outbox.EnqueueAsync(conn, tx,
                    CommunityEventFactory.MemberJoined(member, ownerId, string.Empty));
            }
            await tx.CommitAsync();

            return community;
        }

        public async Task<CommunityEntity?> GetByIdAsync(long id)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<CommunityEntity>(
                @"SELECT c.*, (SELECT COUNT(*) FROM community_members m WHERE m.community_id = c.id) AS members_count
                  FROM communities c WHERE c.id = @id;",
                new { id });
        }

        public async Task<IReadOnlyList<CommunityEntity>> ListByUserAsync(ulong userId)
        {
            var storedUserId = UInt64Storage.ToInt64(userId);
            await using var conn = await _db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<CommunityEntity>(
                @"SELECT c.*, cm.sort_order,
                         (SELECT COUNT(*) FROM community_members m WHERE m.community_id = c.id) AS members_count
                  FROM communities c
                  JOIN community_members cm ON cm.community_id = c.id
                  WHERE cm.user_id = @userId
                  ORDER BY cm.sort_order, cm.id;",
                new { userId = storedUserId });
            return rows.AsList();
        }

        public async Task<ulong?> GetOwnerIdAsync(long communityId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            var ownerId = await conn.QuerySingleOrDefaultAsync<long?>(
                "SELECT owner_id FROM communities WHERE id = @communityId;",
                new { communityId });
            return ownerId is { } value ? UInt64Storage.ToUInt64(value) : null;
        }

        public async Task<CommunityEntity> UpdateAsync(
            long communityId,
            string? name,
            bool updateAvatar,
            string? avatar,
            string? description,
            ulong actorUserId,
            IEnumerable<string> changedFields)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await conn.QuerySingleAsync<CommunityEntity>(
                "SELECT * FROM communities WHERE id = @communityId FOR UPDATE;",
                new { communityId }, tx);

            await conn.ExecuteAsync(
                @"UPDATE communities
                  SET name = COALESCE(@name, name),
                      avatar = CASE
                          WHEN @updateAvatar THEN NULLIF(@avatar, '')
                          ELSE avatar
                      END,
                      description = COALESCE(@description, description)
                  WHERE id = @communityId;",
                new { communityId, name, updateAvatar, avatar, description }, tx);

            var updated = await conn.QuerySingleAsync<CommunityEntity>(
                @"SELECT c.*,
                         (SELECT COUNT(*) FROM community_members m WHERE m.community_id = c.id) AS members_count
                  FROM communities c
                  WHERE c.id = @communityId;",
                new { communityId }, tx);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.CommunityUpdated(updated, actorUserId, changedFields));
            await tx.CommitAsync();
            return updated;
        }

        public async Task<DeleteCommunityStatus> DeleteAsync(
            long communityId,
            ulong actorUserId)
        {
            var storedActorUserId = UInt64Storage.ToInt64(actorUserId);
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var community = await conn.QuerySingleOrDefaultAsync<CommunityEntity>(
                @"SELECT c.*,
                         (SELECT COUNT(*) FROM community_members m WHERE m.community_id = c.id) AS members_count
                  FROM communities c
                  WHERE c.id = @communityId
                  FOR UPDATE;",
                new { communityId },
                tx);
            if (community is null)
                return DeleteCommunityStatus.NotFound;
            if (community.OwnerId != storedActorUserId)
                return DeleteCommunityStatus.NotOwner;

            var members = (await conn.QueryAsync<MemberEntity>(
                @"SELECT * FROM community_members
                  WHERE community_id = @communityId
                  ORDER BY user_id
                  FOR UPDATE;",
                new { communityId },
                tx)).AsList();

            foreach (var member in members)
            {
                await _outbox.EnqueueAsync(
                    conn,
                    tx,
                    CommunityEventFactory.MemberLeft(member, actorUserId));
            }

            await _outbox.EnqueueAsync(
                conn,
                tx,
                CommunityEventFactory.CommunityDeleted(
                    community,
                    actorUserId,
                    members.Select(member => UInt64Storage.ToUInt64(member.UserId)).ToArray()));

            await conn.ExecuteAsync(
                "DELETE FROM communities WHERE id = @communityId;",
                new { communityId },
                tx);

            foreach (var storedUserId in members.Select(member => member.UserId).Distinct())
                await MembershipOrderStore.NormalizeAsync(conn, tx, storedUserId);

            await tx.CommitAsync();
            return DeleteCommunityStatus.Deleted;
        }

        public async Task<TransferCommunityOwnershipResult> TransferOwnershipAsync(
            long communityId,
            ulong actorUserId,
            ulong newOwnerUserId)
        {
            var storedActorUserId = UInt64Storage.ToInt64(actorUserId);
            var storedNewOwnerUserId = UInt64Storage.ToInt64(newOwnerUserId);
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var community = await conn.QuerySingleOrDefaultAsync<CommunityEntity>(
                @"SELECT * FROM communities
                  WHERE id = @communityId
                  FOR UPDATE;",
                new { communityId },
                tx);
            if (community is null)
                return new TransferCommunityOwnershipResult(
                    TransferCommunityOwnershipStatus.NotFound);
            if (community.OwnerId != storedActorUserId)
                return new TransferCommunityOwnershipResult(
                    TransferCommunityOwnershipStatus.NotOwner);
            if (community.OwnerId == storedNewOwnerUserId)
                return new TransferCommunityOwnershipResult(
                    TransferCommunityOwnershipStatus.AlreadyOwner,
                    actorUserId,
                    newOwnerUserId);

            var newOwnerMembershipId = await conn.QuerySingleOrDefaultAsync<long?>(
                @"SELECT id FROM community_members
                  WHERE community_id = @communityId AND user_id = @newOwnerUserId
                  FOR SHARE;",
                new { communityId, newOwnerUserId = storedNewOwnerUserId },
                tx);
            if (newOwnerMembershipId is null)
                return new TransferCommunityOwnershipResult(
                    TransferCommunityOwnershipStatus.NewOwnerNotMember);

            await conn.ExecuteAsync(
                @"UPDATE communities
                  SET owner_id = @newOwnerUserId
                  WHERE id = @communityId;",
                new { communityId, newOwnerUserId = storedNewOwnerUserId },
                tx);

            community.OwnerId = storedNewOwnerUserId;
            await _outbox.EnqueueAsync(
                conn,
                tx,
                CommunityEventFactory.CommunityOwnershipTransferred(
                    community,
                    actorUserId,
                    actorUserId,
                    newOwnerUserId));

            await tx.CommitAsync();
            return new TransferCommunityOwnershipResult(
                TransferCommunityOwnershipStatus.Transferred,
                actorUserId,
                newOwnerUserId);
        }

    }
}
