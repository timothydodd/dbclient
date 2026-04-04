using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using dbclient.Data.Connections;
using dbclient.Models;
using dbclient.ViewModels;

namespace dbclient.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionConfig? Result { get; private set; }

    public ConnectionDialog()
    {
        InitializeComponent();
        DataContext = new ConnectionDialogViewModel();
    }

    private ConnectionDialogViewModel ViewModel => (ConnectionDialogViewModel)DataContext!;

    public void LoadExisting(ConnectionConfig config)
    {
        ViewModel.LoadFrom(config);
        Title = "Edit Connection";
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.IsTesting = true;
        ViewModel.StatusMessage = "Testing...";

        IDbConnectionProvider? provider = null;
        try
        {
            provider = CreateProvider(ViewModel.ToConfig());
            var database = ViewModel.ConnectionType == ConnectionType.Sqlite
                ? ViewModel.FileName
                : ViewModel.Database.Length > 0 ? ViewModel.Database : "master";

            using var con = await provider.GetConnectionAsync(database);
            ViewModel.StatusMessage = "Connection successful!";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            if (provider != null)
                await provider.DisposeAsync();
            ViewModel.IsTesting = false;
        }
    }

    private void Connect_Click(object? sender, RoutedEventArgs e)
    {
        Result = ViewModel.ToConfig();
        Close();
    }

    private async void BrowseSqlite_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select SQLite Database",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SQLite Database") { Patterns = ["*.db", "*.sqlite", "*.sqlite3", "*.s3db"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] },
            ]
        });

        if (files.Count > 0)
            ViewModel.FileName = files[0].Path.LocalPath;
    }

    public static IDbConnectionProvider CreateProvider(ConnectionConfig config)
    {
        switch (config.Type)
        {
            case ConnectionType.SqlServer:
                return new SqlServerConnection
                {
                    Address = config.Address,
                    User = config.User,
                    Password = config.Password,
                    Port = config.Port,
                    ConnectionTimeout = config.ConnectionTimeout,
                    CommandTimeout = config.CommandTimeout,
                    UseSSH = config.UseSSH,
                    SshHost = config.SshHost,
                    SshUser = config.SshUser,
                    SshPassword = config.SshPassword,
                    SshRemotePort = config.SshRemotePort,
                    SshKeyFile = config.SshKeyFile,
                };
            case ConnectionType.MySql:
                return new MySqlDbConnection
                {
                    Address = config.Address,
                    User = config.User,
                    Password = config.Password,
                    Port = config.Port,
                    ConnectionTimeout = config.ConnectionTimeout,
                    CommandTimeout = config.CommandTimeout,
                    UseSSH = config.UseSSH,
                    SshHost = config.SshHost,
                    SshUser = config.SshUser,
                    SshPassword = config.SshPassword,
                    SshRemotePort = config.SshRemotePort,
                    SshKeyFile = config.SshKeyFile,
                };
            case ConnectionType.Sqlite:
                return new SqliteDbConnection { FileName = config.FileName, CommandTimeout = config.CommandTimeout };
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}
