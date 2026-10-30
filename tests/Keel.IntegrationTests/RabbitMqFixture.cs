using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Keel.TestSupport;

namespace Keel.IntegrationTests;

/// <summary>
/// A real RabbitMQ broker in a container for the message-broker sink tests (ADR-0055): the official image (the broker is MPL-2.0, the image is only a test dependency and never
/// ships), a plain AMQP listener and a TLS listener with a throwaway CA (server certificate for "localhost", client certificates verified when presented), the management plugin off
/// and the SSL-certificate-login plugin on (SASL EXTERNAL). Host ports are fixed so the broker can be stopped and started again on the same address. When no broker can be started
/// (no Docker, or the image cannot be pulled offline) <see cref="Unavailable"/> carries the reason and the tests skip; they are never reported as passed.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string Image = "rabbitmq:4.1-alpine";
    public const string User = "keel";
    public string Password { get; } = "pw-" + Guid.NewGuid().ToString("N");
    public int AmqpPort { get; } = FreePort();
    public int TlsPort { get; } = FreePort();
    public string? Unavailable { get; private set; }
    public SyslogTestPki Pki { get; } = new("Keel broker test CA");
    public string CaFile { get; private set; } = "";
    public string ClientPem { get; private set; } = "";
    public string WorkDir { get; } = Directory.CreateTempSubdirectory("keel-rmq-").FullName;
    private IContainer? _container;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask InitializeAsync()
    {
        try
        {
            using var server = Pki.Issue("localhost", server: true, "localhost");
            using var client = Pki.Issue("keel-client", server: false);
            CaFile = Path.Combine(WorkDir, "ca.pem");
            await File.WriteAllTextAsync(CaFile, Pki.CaPem);
            var (clientCert, clientKey) = SyslogTestPki.WritePem(client, WorkDir, "client");
            ClientPem = Path.Combine(WorkDir, "client.pem");
            await File.WriteAllTextAsync(ClientPem, (await File.ReadAllTextAsync(clientCert)).TrimEnd() + "\n" + (await File.ReadAllTextAsync(clientKey)).TrimEnd() + "\n");
            var (serverCert, serverKey) = SyslogTestPki.WritePem(server, WorkDir, "server");

            const string conf = """
                listeners.tcp.default = 5672
                listeners.ssl.default = 5671
                ssl_options.cacertfile = /etc/rabbitmq/certs/ca.pem
                ssl_options.certfile = /etc/rabbitmq/certs/server.pem
                ssl_options.keyfile = /etc/rabbitmq/certs/server.key
                ssl_options.verify = verify_peer
                ssl_options.fail_if_no_peer_cert = false
                ssl_options.versions.1 = tlsv1.3
                ssl_options.versions.2 = tlsv1.2
                auth_mechanisms.1 = PLAIN
                auth_mechanisms.2 = EXTERNAL
                ssl_cert_login_from = common_name
                loopback_users = none
                """;
            _container = new ContainerBuilder(Image)
                .WithEnvironment("RABBITMQ_DEFAULT_USER", User)
                .WithEnvironment("RABBITMQ_DEFAULT_PASS", Password)
                .WithResourceMapping(Encoding.UTF8.GetBytes(conf.Replace("\r\n", "\n", StringComparison.Ordinal)), "/etc/rabbitmq/rabbitmq.conf")
                .WithResourceMapping(Encoding.UTF8.GetBytes("[rabbitmq_auth_mechanism_ssl].\n"), "/etc/rabbitmq/enabled_plugins")
                .WithResourceMapping(await File.ReadAllBytesAsync(CaFile), "/etc/rabbitmq/certs/ca.pem")
                .WithResourceMapping(await File.ReadAllBytesAsync(serverCert), "/etc/rabbitmq/certs/server.pem")
                .WithResourceMapping(await File.ReadAllBytesAsync(serverKey), "/etc/rabbitmq/certs/server.key")
                .WithPortBinding(AmqpPort, 5672)
                .WithPortBinding(TlsPort, 5671)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
                .Build();
            await _container.StartAsync();
            // The user SASL EXTERNAL maps the client certificate's common name to.
            var add = await _container.ExecAsync(["rabbitmqctl", "add_user", "keel-client", "unused-" + Guid.NewGuid().ToString("N")]);
            if (add.ExitCode != 0) throw new InvalidOperationException("Could not create the certificate user: " + add.Stderr + add.Stdout);
            var perm = await _container.ExecAsync(["rabbitmqctl", "set_permissions", "-p", "/", "keel-client", ".*", ".*", ".*"]);
            if (perm.ExitCode != 0) throw new InvalidOperationException("Could not grant the certificate user access: " + perm.Stderr + perm.Stdout);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Unavailable = $"No RabbitMQ container could be started ({Image}): {e.GetType().Name}: {e.Message}";
        }
    }

    public async Task StopBrokerAsync() => await _container!.StopAsync();

    /// <summary>Starts the stopped broker again and waits until it accepts AMQP connections on the same port.</summary>
    public async Task StartBrokerAsync()
    {
        await _container!.StartAsync();
        for (var i = 0; i < 120; i++)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, AmqpPort);
                var logs = await _container.GetLogsAsync();
                if (logs.Stdout.Split("Server startup complete").Length > 2) return;
            }
            catch (SocketException) { /* not yet */ }
            await Task.Delay(500);
        }
        throw new TimeoutException("The broker did not come back.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        Pki.Dispose();
        try { Directory.Delete(WorkDir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
