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

        }


        public override Task<ChannelResponse> DeleteChannel(ChannelRequest request, ServerCallContext context)
        {
            return base.DeleteChannel(request, context);
        }








        public ChannelService(DbConnection db,IdGenerator gen)
        {
            _db = db;
            _idGen = gen;
        }
        
    }
}
