using Renci.SshNet;

namespace dbclient.Data.Connections;

public class SshTunnel : IDisposable
{
    private SshClient? _client;
    private ForwardedPortLocal? _port;

    public int LocalPort { get; }

    public SshTunnel(string host, int sshPort, string user, string password, string? keyFile, uint remotePort)
    {
        try
        {
            AuthenticationMethod auth;
            if (!string.IsNullOrWhiteSpace(keyFile))
            {
                var file = new PrivateKeyFile(keyFile);
                auth = new PrivateKeyAuthenticationMethod(user, file);
            }
            else
            {
                auth = new PasswordAuthenticationMethod(user, password);
            }

            var connectionInfo = new ConnectionInfo(host, sshPort, user, auth);
            _client = new SshClient(connectionInfo);
            _port = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", remotePort);

            _client.Connect();
            _client.AddForwardedPort(_port);
            _port.Start();

            LocalPort = (int)_port.BoundPort;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _port?.Dispose();
        _client?.Dispose();
    }
}
