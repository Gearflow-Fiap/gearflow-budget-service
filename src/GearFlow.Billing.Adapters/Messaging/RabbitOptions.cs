using RabbitMQ.Client;

namespace GearFlow.Billing.Adapters.Messaging;

public sealed class RabbitOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string VirtualHost { get; set; } = "/";
    public bool Tls { get; set; }
    public string QueueType { get; set; } = "classic";
    public string CommandsExchange { get; set; } = "gearflow.commands.v1";
    public string EventsExchange { get; set; } = "gearflow.events.v1";
    public string DeadLetterExchange { get; set; } = "gearflow.deadletters.v1";
    public string CommandsQueue { get; set; } = "gearflow.billing.commands.v1";
    public string EventsQueue { get; set; } = "gearflow.os.billing.events.v1";
    public ConnectionFactory Factory() => new()
    {
        HostName = Host, Port = Port, UserName = Username, Password = Password, VirtualHost = VirtualHost,
        AutomaticRecoveryEnabled = false, RequestedHeartbeat = TimeSpan.FromSeconds(30),
        RequestedConnectionTimeout = TimeSpan.FromSeconds(5), ConsumerDispatchConcurrency = 1,
        Ssl = new SslOption { Enabled = Tls, ServerName = Host }
    };
}

public sealed class DeliveryException(string code, bool preserveSource = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool PreserveSource { get; } = preserveSource;
}
