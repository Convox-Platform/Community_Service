namespace Community_Service.Models
{
    public abstract class Channel
    {
        public long Id { get; set; }
        public string? Name { get; set; }

        public string? Type { get; protected set; }

        public long ServerId { get; set; }
    }
}
