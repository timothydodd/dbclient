using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace dbclient.Data.Connections;

/// <summary>Details of a server host key presented during SSH connection.</summary>
public record SshHostKeyInfo(string Host, int Port, string KeyType, string FingerprintSha256, string FingerprintMd5);

public class SshTunnel : IDisposable
{
    private SshClient? _client;
    private ForwardedPortLocal? _port;

    /// <summary>
    /// Called when a host presents a key that is not in any known-hosts file.
    /// Return true to trust the key (it is then persisted to ~/.dbclient/known_hosts).
    /// When null, unknown host keys are rejected.
    /// </summary>
    public static Func<SshHostKeyInfo, bool>? UnknownHostKeyHandler { get; set; }

    public static string KnownHostsPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dbclient", "known_hosts");

    private static readonly string UserSshKnownHosts =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    private static readonly object KnownHostsLock = new();

    public int LocalPort { get; private set; }
    public bool IsConnected => _client?.IsConnected == true && _port?.IsStarted == true;

    private readonly string _host;
    private readonly int _sshPort;
    private readonly uint _remotePort;
    private readonly ConnectionInfo _connectionInfo;
    private Exception? _hostKeyError;

    public SshTunnel(string host, int sshPort, string user, string password, string? keyFile, uint remotePort,
        string? keyPassphrase = null)
    {
        _host = host;
        _sshPort = sshPort;
        _remotePort = remotePort;

        AuthenticationMethod auth;
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            var file = string.IsNullOrEmpty(keyPassphrase)
                ? new PrivateKeyFile(keyFile)
                : new PrivateKeyFile(keyFile, keyPassphrase);
            auth = new PrivateKeyAuthenticationMethod(user, file);
        }
        else
        {
            auth = new PasswordAuthenticationMethod(user, password);
        }

        _connectionInfo = new ConnectionInfo(host, sshPort, user, auth);
    }

    /// <summary>Establishes the SSH connection and port forward. Blocking; call from a worker thread.</summary>
    public void Connect()
    {
        try
        {
            _client = new SshClient(_connectionInfo);
            _client.HostKeyReceived += OnHostKeyReceived;
            _port = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", _remotePort);

            try
            {
                _client.Connect();
            }
            catch (Exception ex) when (_hostKeyError != null)
            {
                throw new SshConnectionException(_hostKeyError.Message, ex);
            }

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

    public Task ConnectAsync(CancellationToken ct = default) =>
        Task.Run(Connect, ct);

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var keyBase64 = Convert.ToBase64String(e.HostKey);
        var sha256 = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
        var md5 = "MD5:" + string.Join(":", MD5.HashData(e.HostKey).Select(b => b.ToString("x2")));
        var info = new SshHostKeyInfo(_host, _sshPort, e.HostKeyName, sha256, md5);

        var stored = FindStoredKeys(_host, _sshPort, e.HostKeyName);
        if (stored.Count > 0)
        {
            if (stored.Contains(keyBase64))
            {
                e.CanTrust = true;
                return;
            }

            _hostKeyError = new SshConnectionException(
                $"HOST KEY CHANGED for {_host}:{_sshPort} ({e.HostKeyName}). " +
                $"The key presented ({sha256}) does not match the stored key. " +
                $"This could indicate a man-in-the-middle attack. If the server key was legitimately changed, " +
                $"remove the entry for {_host}:{_sshPort} from {KnownHostsPath} and try again.");
            e.CanTrust = false;
            return;
        }

        var handler = UnknownHostKeyHandler;
        if (handler == null)
        {
            _hostKeyError = new SshConnectionException(
                $"Unknown SSH host key for {_host}:{_sshPort} ({e.HostKeyName}, {sha256}) and no handler is configured to accept it.");
            e.CanTrust = false;
            return;
        }

        bool trust;
        try
        {
            trust = handler(info);
        }
        catch (Exception ex)
        {
            _hostKeyError = new SshConnectionException($"Host key verification failed for {_host}:{_sshPort}: {ex.Message}", ex);
            e.CanTrust = false;
            return;
        }

        if (!trust)
        {
            _hostKeyError = new SshConnectionException(
                $"SSH host key for {_host}:{_sshPort} ({e.HostKeyName}, {sha256}) was rejected.");
            e.CanTrust = false;
            return;
        }

        AppendKnownHost(_host, _sshPort, e.HostKeyName, keyBase64);
        e.CanTrust = true;
    }

    /// <summary>
    /// Returns all stored keys (base64) for the host/port/keytype from ~/.dbclient/known_hosts and ~/.ssh/known_hosts.
    /// </summary>
    private static HashSet<string> FindStoredKeys(string host, int port, string keyType)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        lock (KnownHostsLock)
        {
            ReadKnownHosts(KnownHostsPath, host, port, keyType, keys);
            ReadKnownHosts(UserSshKnownHosts, host, port, keyType, keys);
        }
        return keys;
    }

    private static void ReadKnownHosts(string path, string host, int port, string keyType, HashSet<string> keys)
    {
        if (!File.Exists(path)) return;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return; }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('@')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var hostField = parts[0];
            if (hostField.StartsWith("|1|", StringComparison.Ordinal)) continue; // hashed entry - skip

            if (!string.Equals(parts[1], keyType, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var pattern in hostField.Split(','))
            {
                if (HostMatches(pattern, host, port))
                {
                    keys.Add(parts[2]);
                    break;
                }
            }
        }
    }

    private static bool HostMatches(string pattern, string host, int port)
    {
        // dbclient format: host:port ; OpenSSH: host (port 22) or [host]:port
        if (pattern.StartsWith('['))
        {
            var close = pattern.IndexOf(']');
            if (close < 0) return false;
            var h = pattern.Substring(1, close - 1);
            var rest = pattern[(close + 1)..];
            if (!rest.StartsWith(':') || !int.TryParse(rest[1..], out var p)) return false;
            return p == port && string.Equals(h, host, StringComparison.OrdinalIgnoreCase);
        }

        var idx = pattern.LastIndexOf(':');
        if (idx > 0 && int.TryParse(pattern[(idx + 1)..], out var p2))
            return p2 == port && string.Equals(pattern[..idx], host, StringComparison.OrdinalIgnoreCase);

        return port == 22 && string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendKnownHost(string host, int port, string keyType, string keyBase64)
    {
        lock (KnownHostsLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(KnownHostsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var line = $"{host}:{port} {keyType} {keyBase64}";
                var needsNewline = File.Exists(KnownHostsPath) && new FileInfo(KnownHostsPath).Length > 0
                                   && !File.ReadAllText(KnownHostsPath).EndsWith('\n');
                File.AppendAllText(KnownHostsPath, (needsNewline ? Environment.NewLine : "") + line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Persisting is best-effort; the connection is still trusted for this session.
            }
        }
    }

    public void Dispose()
    {
        if (_client != null) _client.HostKeyReceived -= OnHostKeyReceived;
        _port?.Dispose();
        _client?.Dispose();
        _port = null;
        _client = null;
    }
}
