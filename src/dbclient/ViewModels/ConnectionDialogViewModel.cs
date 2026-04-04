using dbclient.Models;

namespace dbclient.ViewModels;

public class ConnectionDialogViewModel : ViewModelBase
{
    private ConnectionType _connectionType = ConnectionType.SqlServer;
    private string _address = "";
    private string _user = "";
    private string _password = "";
    private string _port = "";
    private string _database = "";
    private string _fileName = "";
    private string _displayName = "";
    private bool _useSSH;
    private string _sshHost = "";
    private string _sshUser = "";
    private string _sshPassword = "";
    private int _sshRemotePort;
    private string _sshKeyFile = "";
    private int _connectionTimeout = 15;
    private int _commandTimeout = 30;
    private string _statusMessage = "";
    private bool _isTesting;

    public ConnectionType ConnectionType
    {
        get => _connectionType;
        set
        {
            if (SetField(ref _connectionType, value))
            {
                OnPropertyChanged(nameof(IsServerConnection));
                OnPropertyChanged(nameof(IsSqlite));
            }
        }
    }

    public bool IsServerConnection => ConnectionType != ConnectionType.Sqlite;
    public bool IsSqlite => ConnectionType == ConnectionType.Sqlite;

    public string Address { get => _address; set => SetField(ref _address, value); }
    public string User { get => _user; set => SetField(ref _user, value); }
    public string Password { get => _password; set => SetField(ref _password, value); }
    public string Port { get => _port; set => SetField(ref _port, value); }
    public string Database { get => _database; set => SetField(ref _database, value); }
    public string FileName { get => _fileName; set => SetField(ref _fileName, value); }
    public string DisplayName { get => _displayName; set => SetField(ref _displayName, value); }
    public bool UseSSH { get => _useSSH; set => SetField(ref _useSSH, value); }
    public string SshHost { get => _sshHost; set => SetField(ref _sshHost, value); }
    public string SshUser { get => _sshUser; set => SetField(ref _sshUser, value); }
    public string SshPassword { get => _sshPassword; set => SetField(ref _sshPassword, value); }
    public int SshRemotePort { get => _sshRemotePort; set => SetField(ref _sshRemotePort, value); }
    public string SshKeyFile { get => _sshKeyFile; set => SetField(ref _sshKeyFile, value); }
    public int ConnectionTimeout { get => _connectionTimeout; set => SetField(ref _connectionTimeout, value); }
    public int CommandTimeout { get => _commandTimeout; set => SetField(ref _commandTimeout, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public bool IsTesting { get => _isTesting; set => SetField(ref _isTesting, value); }

    public ConnectionConfig ToConfig() => new()
    {
        DisplayName = DisplayName,
        Type = ConnectionType,
        Address = Address,
        User = User,
        Password = Password,
        Port = Port,
        Database = Database,
        FileName = FileName,
        UseSSH = UseSSH,
        SshHost = SshHost,
        SshUser = SshUser,
        SshPassword = SshPassword,
        SshRemotePort = SshRemotePort,
        SshKeyFile = SshKeyFile,
        ConnectionTimeout = ConnectionTimeout,
        CommandTimeout = CommandTimeout,
    };

    public void LoadFrom(ConnectionConfig config)
    {
        DisplayName = config.DisplayName;
        ConnectionType = config.Type;
        Address = config.Address;
        User = config.User;
        Password = config.Password;
        Port = config.Port;
        Database = config.Database;
        FileName = config.FileName;
        UseSSH = config.UseSSH;
        SshHost = config.SshHost;
        SshUser = config.SshUser;
        SshPassword = config.SshPassword;
        SshRemotePort = config.SshRemotePort;
        SshKeyFile = config.SshKeyFile;
        ConnectionTimeout = config.ConnectionTimeout;
        CommandTimeout = config.CommandTimeout;
    }
}
