namespace Community_Service.Models
{
    public class Server
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }

        public string? Image { get; set; }


        public List<Channel> Channels { get; set; } = new List<Channel>();

        public List<long> UsersId { get; set; } = new List<long>();
    }
}
