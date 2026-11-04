using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using GearFlow.Billing.Adapters.Messaging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GearFlow.Billing.IntegrationTests;

internal sealed class InfrastructureLease<T>(T value, IContainer? container = null, Func<Task>? cleanup = null) : IAsyncDisposable
{
    public T Value { get; } = value;
    public async ValueTask DisposeAsync()
    {
        try { if (container is not null) await container.DisposeAsync(); }
        finally { if (cleanup is not null) await cleanup(); }
    }
}

internal static class TestInfrastructure
{
    public static bool Containers => System.Environment.GetEnvironmentVariable("GEARFLOW_USE_TESTCONTAINERS") == "true";
    public static bool NativeMongo => !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("GEARFLOW_TEST_MONGOD"));
    public static async Task<InfrastructureLease<string>> MongoAsync()
    {
        if (System.Environment.GetEnvironmentVariable("GEARFLOW_TEST_MONGO") is { Length: > 0 } configured) return new(configured);
        if (NativeMongo) return await NativeMongoAsync();
        if (!Containers) throw new InvalidOperationException("Mongo test infrastructure was not enabled.");
        var container = new ContainerBuilder("mongo:7.0.16").WithPortBinding(27017, true)
            .WithCommand("mongod", "--replSet", "rs0", "--bind_ip_all")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Waiting for connections")).Build();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await container.StartAsync(deadline.Token);
            var init = await container.ExecAsync(["mongosh", "--quiet", "--eval", "rs.initiate({_id:'rs0',members:[{_id:0,host:'localhost:27017'}]})"], deadline.Token);
            if (init.ExitCode != 0) throw new InvalidOperationException("Test Mongo replica set initialization failed.");
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var primary = await container.ExecAsync(["mongosh", "--quiet", "--eval", "if(!db.hello().isWritablePrimary) quit(1)"], deadline.Token);
                if (primary.ExitCode == 0)
                    return new($"mongodb://{container.Hostname}:{container.GetMappedPublicPort(27017)}/?replicaSet=rs0&directConnection=true", container);
                await Task.Delay(500, deadline.Token);
            }
            throw new InvalidOperationException("Test Mongo primary did not become ready.");
        }
        catch { await container.DisposeAsync(); throw; }
    }
    private static async Task<InfrastructureLease<string>> NativeMongoAsync()
    {
        var executable = Path.GetFullPath(System.Environment.GetEnvironmentVariable("GEARFLOW_TEST_MONGOD")!);
        if (!File.Exists(executable)) throw new FileNotFoundException("Native Mongo executable not found.", executable);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var directory = Path.Combine(Path.GetDirectoryName(executable)!, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "--replSet", "rs0", "--bind_ip", "127.0.0.1", "--port", port.ToString(),
            "--dbpath", directory, "--logpath", Path.Combine(directory, "mongod.log"), "--wiredTigerCacheSizeGB", "0.25" }) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start native test Mongo.");
        var uri = $"mongodb://127.0.0.1:{port}/?directConnection=true";
        var settings = MongoClientSettings.FromConnectionString(uri); settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
        var client = new MongoClient(settings); var admin = client.GetDatabase("admin");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException($"Native Mongo exited; inspect {directory}.");
                try { await admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: deadline.Token); break; }
                catch (TimeoutException) { await Task.Delay(100, deadline.Token); }
            }
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument("replSetInitiate", new BsonDocument
            { ["_id"] = "rs0", ["members"] = new BsonArray { new BsonDocument { ["_id"] = 0, ["host"] = $"127.0.0.1:{port}" } } }), cancellationToken: deadline.Token);
            while (!(await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: deadline.Token)).GetValue("isWritablePrimary", false).AsBoolean)
                await Task.Delay(100, deadline.Token);
            return new(uri + "&replicaSet=rs0", cleanup: Stop);
        }
        catch { await Stop(); throw; }
        async Task Stop()
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            finally { process.Dispose(); }
        }
    }
    public static async Task<InfrastructureLease<RabbitOptions>> RabbitAsync()
    {
        if (System.Environment.GetEnvironmentVariable("GEARFLOW_TEST_RABBIT") is { Length: > 0 } configured)
        {
            var uri = new Uri(configured); var credentials = uri.UserInfo.Split(':', 2);
            if (uri.Scheme is not ("amqp" or "amqps") || credentials.Length != 2) throw new InvalidOperationException("Expected authenticated AMQP test URI.");
            return new(new RabbitOptions { Host = uri.Host, Port = uri.Port > 0 ? uri.Port : uri.Scheme == "amqps" ? 5671 : 5672, Tls = uri.Scheme == "amqps",
                Username = Uri.UnescapeDataString(credentials[0]), Password = Uri.UnescapeDataString(credentials[1]),
                VirtualHost = uri.AbsolutePath == "/" ? "/" : Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')) });
        }
        if (!Containers) throw new InvalidOperationException("Rabbit test infrastructure was not enabled.");
        var password = Guid.NewGuid().ToString("N");
        var container = new ContainerBuilder("rabbitmq:4.2.0-management").WithPortBinding(5672, true)
            .WithEnvironment("RABBITMQ_DEFAULT_USER", "billingtest").WithEnvironment("RABBITMQ_DEFAULT_PASS", password)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete")).Build();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await container.StartAsync(deadline.Token);
            return new(new RabbitOptions { Host = container.Hostname, Port = container.GetMappedPublicPort(5672), Username = "billingtest", Password = password }, container);
        }
        catch { await container.DisposeAsync(); throw; }
    }
}
