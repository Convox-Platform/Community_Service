using Community_Service.Models;
using Dapper;
using Grpc.Core;
using IdGen;
using System.Data.Common;

namespace Community_Service.Services
{
    public class ChannelService:Channel_Service.Channel_ServiceBase
    {
        private readonly DbConnection _db;
        private readonly IdGenerator _idGen;

        public override async Task<ChannelResponse> CreateChannel(ChannelRequest request, ServerCallContext context)
        {
            try
            {
                if (request.Type == "Text")
                {
                    string sql = @"INSERT INTO TextChannels (Id, Name, Type, ServerId) VALUES (@Id, @Name, @Type, @ServerId)";

                    var id = _idGen.CreateId();
                    var channel = await _db.QuerySingleAsync<TextChannel>(sql, new
                    {
                        Id = id,
                        Name = request.Name,
                        Type = request.Type,
                        ServerId = request.ServerId
                    });

                    return new ChannelResponse
                    {
                        Id = channel.Id,
                        Name = channel.Name,
                        Type = channel.Type,
                        ServerId = channel.ServerId
                    };
                }
                else if (request.Type == "Voice")
                {
                    string sql = @"INSERT INTO VoiceChannels (Id, Name, Type, ServerId) VALUES (@Id, @Name, @Type, @ServerId)";
                    var id = _idGen.CreateId();
                    var channel = await _db.QuerySingleAsync<VoiceChannel>(sql, new
                    {
                        Id = id,
                        Name = request.Name,
                        Type = request.Type,
                        ServerId = request.ServerId
                    });

                    return new ChannelResponse
                    {
                        Id = channel.Id,
                        Name = channel.Name,
                        Type = channel.Type,
                        ServerId = channel.ServerId
                    };


                }
                else
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid channel type"));
                
            }
            catch (Exception e)
            {
                throw new RpcException(new Status(StatusCode.Internal, e.Message));
            }
            
        }

        public override async Task<ChannelResponse> UpdateChannel(ChannelRequest request, ServerCallContext context)
        {
            try
            {
                var channel = null as Channel;
                if (request.Type == "Text")
                {
                    string sql = @"UPDATE TextChannels SET Name = @Name WHERE Id = @Id";

                    channel = await _db.QuerySingleAsync<TextChannel>(sql, new
                    {
                        Name = request.Name,
                    });
                }
                else if (request.Type == "Voice")
                {
                    string sql = @"UPDATE VoiceChannels SET Name = @Name WHERE Id = @Id";
                    channel = await _db.QuerySingleAsync<VoiceChannel>(sql, new
                    {
                        Name = request.Name
                    });
                }
                else
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid channel type"));

                return new ChannelResponse
                {
                    Id = channel.Id,
                    Name = channel.Name,
                    Type = channel.Type,
                    ServerId = channel.ServerId
                };

            }
            catch (Exception e)
            {
                throw new RpcException(new Status(StatusCode.Internal, e.Message));
            }
        }

        public override async Task<LittelChannelResponse> DeleteChannel(ChannelDeleteRequest request, ServerCallContext context)
        {
            try
            {
                string sqlDetect = request.Type == "Text" ? @"SELECT name FROM TextChannels WHERE Id = @Id" : @"SELECT name FROM VoiceChannels WHERE Id = @Id";
                var channelName = await _db.QueryFirstOrDefaultAsync<string>(sqlDetect, new { Id = request.Id });
                if (channelName == null)
                    throw new RpcException(new Status(StatusCode.NotFound, "Channel not found"));
                string command = $"Delete {channelName}";

                if (command == request.Comand)
                {
                    string sqlDelete = request.Type == "Text" ? @"DELETE FROM TextChannels WHERE Id = @Id" : @"DELETE FROM VoiceChannels WHERE Id = @Id";
                    await _db.ExecuteAsync(sqlDelete, new { Id = request.Id });
                    return new LittelChannelResponse
                    {
                        Message = "Channel deleted successfully"
                    };
                }

                return new LittelChannelResponse
                {
                    Message = "Incorrect command"
                };
            }
            catch
            {
                throw new RpcException(new Status(StatusCode.Internal, "Error deleting channel"));
            }
            
        }

      




        public ChannelService(DbConnection db,IdGenerator gen)
        {
            _db = db;
            _idGen = gen;
        }
        
    }
}
