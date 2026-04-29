namespace dbclient.Models;

public class ConnectionConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public ConnectionType Type { get; set; } = ConnectionType.SqlServer;
    public string Address { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Port { get; set; } = "";
    public string Database { get; set; } = "";

    // SQL Server auth: SqlLogin (default) or AzureDefault (uses az login / DefaultAzureCredential chain)
    public SqlAuthMode AuthMode { get; set; } = SqlAuthMode.SqlLogin;

    // SQLite
    public string FileName { get; set; } = "";

    // Timeout
    public int ConnectionTimeout { get; set; } = 15;
    public int CommandTimeout { get; set; } = 30;

    // SSH
    public bool UseSSH { get; set; }
    public string SshHost { get; set; } = "";
    public string SshUser { get; set; } = "";
    public string SshPassword { get; set; } = "";
    public int SshRemotePort { get; set; }
    public string SshKeyFile { get; set; } = "";

    public override string ToString() =>
        !string.IsNullOrEmpty(DisplayName) ? DisplayName
        : Type == ConnectionType.Sqlite ? System.IO.Path.GetFileName(FileName)
        : $"{Type}: {Address}";
}

public enum ConnectionType
{
    SqlServer,
    MySql,
    Sqlite
}

public enum SqlAuthMode
{
    SqlLogin,
    AzureDefault
}
