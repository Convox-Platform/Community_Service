namespace Community_Service.Events;

public sealed class RabbitMqOptions
{
    public const string DefaultExchange = "community.events";

    public required Uri AmqpUrl { get; init; }
    public string ExchangeName { get; init; } = DefaultExchange;
}
