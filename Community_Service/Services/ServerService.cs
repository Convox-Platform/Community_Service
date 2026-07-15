using Community_Service.Models;
using Dapper;
using Grpc.Core;
using IdGen;
using Microsoft.AspNetCore.Authorization;
using System.Data.Common;
using System.Security.Claims;

namespace Community_Service.Services
{
    public class ServerService: Server_Service.Server_ServiceBase
    {
        private readonly DbConnection _db;
        private readonly IdGenerator _idGen;
        [Authorize]
        public override async Task<ServiceResponse> CreateServer(ServiceRequest request, ServerCallContext context)
        {
            
            try
            {
                string sqlDetect = @"SELECT Id FROM Servers WHERE Name = @Name";
                var serverExists = await _db.QueryFirstOrDefaultAsync<long?>(sqlDetect, new { Name = request.Name });

                if(serverExists != null)
                {
                    throw new RpcException(new Status(StatusCode.AlreadyExists, "Server already exists"));
                }

                string sql = @"INSERT INTO Servers (Id, Name, Description, Image, UsersId)
                VALUES (@Id, @Name, @Description, @Image, ARRAY[@UserId]::BIGINT[])";

                var id = _idGen.CreateId();

                var Server = await _db.QuerySingleAsync<Server>(sql, new
                {
                    Id = id,
                    Name = request.Name,
                    Description = request.Description,
                    Image = request.Image,
                    UserId = context.GetHttpContext().User.FindFirstValue(ClaimTypes.NameIdentifier)
                });

                return new ServiceResponse
                {
                    Id = id,
                    Name = Server.Name,
                    Description = Server.Description,
                    Image = Server.Image,
                    Users = { Server.UsersId }
                };
            }
            catch (Exception e)
            {
                
                throw new RpcException(new Status(StatusCode.Internal, e.Message));
            }

        }

        public override async Task<LittelResponse> DeleteServer(ServiceDeleteRequest request, ServerCallContext context)
        {

            string sqlDetect = @"SELECT name FROM Servers WHERE Id = @Id";

            var name = await _db.QueryFirstOrDefaultAsync<string>(sqlDetect, new { Id = request.Id });

            string command = $"Delete {name}";

            if(command == request.Comand)
            {
               var sqlDelete = @"DELETE FROM Servers WHERE Id = @Id";
                await _db.ExecuteAsync(sqlDelete, new { Id = request.Id });
                return new LittelResponse
                {
                    Message = "Server deleted successfully"
                };
            }

            return new LittelResponse
            {
                Message = "Incorrect command"
            };
        }

        public override async Task<ServiceResponse> UpdateServer(ServiceRequest request, ServerCallContext context)
        {
            string sql = @"UPDATE Servers SET Name = @Name, Description = @Description, Image = @Image WHERE Id = @Id";
            try
            {
                var Server = await _db.QuerySingleAsync<Server>(sql, new
                {
                    Id = request.Id,
                    Name = request.Name,
                    Description = request.Description,
                    Image = request.Image,
                });

                return new ServiceResponse
                {
                    Id = Server.Id,
                    Name = Server.Name,
                    Description = Server.Description,
                    Image = Server.Image,
                    Users = { Server.UsersId }
                };

            }
            catch (Exception e)
            {
                throw new RpcException(new Status(StatusCode.Internal, e.Message));
            }
            

            
        }

        public override async Task<ServiceResponse> GetServer(ServiceDeleteRequest request, ServerCallContext context)
        {
            var sql = @"SELECT * FROM Servers WHERE Id = @Id";
            var server = await _db.QuerySingleAsync<Server>(sql, new { Id = request.Id });
            if (server is null)
            {
                throw new RpcException(
                    new Status(StatusCode.NotFound, "Server not found"));
            }

            const string textChannelsSql = """
            SELECT *
            FROM TextChannels
            WHERE ServerId = @ServerId
            """;

            const string voiceChannelsSql = """
            SELECT *
            FROM VoiceChannels
            WHERE ServerId = @ServerId
            """;

            var textChannels = await _db.QueryAsync<TextChannel>(
                textChannelsSql,
                new { ServerId = request.Id });

            var voiceChannels = await _db.QueryAsync<VoiceChannel>(
                voiceChannelsSql,
                new { ServerId = request.Id });

            var response = new ServiceResponse
            {
                Id = server.Id,
                Name = server.Name,
                Description = server.Description,
                Image = server.Image,
                Users = { server.UsersId }
            };
            Channel[] channel = textChannels.Cast<Channel>().Concat(voiceChannels.Cast<Channel>()).ToArray();
            foreach (var item in channel)
            {
                response.Channels.Add(new ChannelResponse { Id = item.Id, Name = item.Name, Type = item.Type, ServerId = item.ServerId });
            }

            return response;
        }

        public override async Task<ServiceResponse> AddUser(AddUserRequest request, ServerCallContext context)
        {
            const string selectSql = """
            SELECT *
            FROM Servers
            WHERE Id = @Id
            """;

            var server = await _db.QuerySingleOrDefaultAsync<Server>(
                selectSql,
                new { Id = request.ServerId });

            if (server is null)
            {
                throw new RpcException(
                    new Status(StatusCode.NotFound, "Server not found"));
            }
            try
            {
                if (!server.UsersId.Contains(request.UserId))
                {
                    const string updateSql = """
                        UPDATE Servers
                        SET UsersId = array_append(UsersId, @UserId)
                        WHERE Id = @ServerId
                        """;

                    await _db.ExecuteAsync(updateSql, new
                    {
                        ServerId = request.ServerId,
                        UserId = request.UserId
                    });

                    server.UsersId.Add(request.UserId);

                    return new ServiceResponse
                    {
                        Id = server.Id,
                        Name = server.Name ,
                        Description = server.Description ,
                        Image = server.Image,
                        Users = { server.UsersId }
                    };
                }

            }
            catch (Exception e)
            {
                throw new RpcException(new Status(StatusCode.Internal, e.Message));
            }

            return new ServiceResponse
            {
                Id = server.Id,
                Name = server.Name,
                Description = server.Description,
                Image = server.Image,
                Users = { server.UsersId }
            };

            


        }









        public ServerService(DbConnection db,IdGenerator gen)
        {
            _db = db;
            _idGen = gen;
        }
    }
}
