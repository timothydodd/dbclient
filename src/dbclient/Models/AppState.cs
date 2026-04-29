using System.Text.Json;

namespace dbclient.Models;

public class AppState
{
    public int Version { get; set; } = 1;
    public List<ConnectionConfig> SavedConnections { get; set; } = new();
    public List<string> OpenConnectionIds { get; set; } = new();
    public string? ActiveConnectionTabId { get; set; }
    public bool IsConnectionPanelOpen { get; set; } = true;
    public bool IsHistoryPanelOpen { get; set; }
    public string Theme { get; set; } = "Dark";
}

public class ConnectionTabState
{
    public string Id { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string ActiveDatabase { get; set; } = "";
    public List<TabState> QueryTabs { get; set; } = new();
    public string? ActiveQueryTabId { get; set; }
    public Dictionary<string, string?> ActiveQueryTabByDatabase { get; set; } = new();
}

public class TabState
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string QueryText { get; set; } = "";
    public int Order { get; set; }
    public string Database { get; set; } = "";
}

public class StateService
{
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dbclient");
    private static readonly string ConnectionsDir = Path.Combine(StateDir, "connections");
    private static readonly string StateFile = Path.Combine(StateDir, "state.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppState LoadState()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                var json = File.ReadAllText(StateFile);
                var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
                if (state == null)
                {
                    Services.AppLogger.Warn("State file deserialized to null, using defaults");
                    return new AppState();
                }
                state.SavedConnections ??= new();
                state.OpenConnectionIds ??= new();
                DecryptPasswords(state);
                return state;
            }
        }
        catch (Exception ex) { Services.AppLogger.Error("Failed to load state", ex); }
        return new AppState();
    }

    public void SaveState(AppState state)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            EncryptPasswords(state);
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(StateFile, json);
            DecryptPasswords(state);
        }
        catch (Exception ex) { Services.AppLogger.Error("Failed to save state", ex); }
    }

    public ConnectionTabState? LoadConnectionState(string connectionId)
    {
        try
        {
            var file = Path.Combine(ConnectionsDir, $"{connectionId}.json");
            if (File.Exists(file))
            {
                var json = File.ReadAllText(file);
                var state = JsonSerializer.Deserialize<ConnectionTabState>(json, JsonOptions);
                if (state != null)
                {
                    state.QueryTabs ??= new();
                    state.ActiveQueryTabByDatabase ??= new();

                    // Migrate tabs without a database to the connection's ActiveDatabase
                    // (which represents the first/last-used database for this connection).
                    foreach (var t in state.QueryTabs)
                        if (string.IsNullOrEmpty(t.Database))
                            t.Database = state.ActiveDatabase;

                    // Migrate legacy ActiveQueryTabId into the per-database map
                    if (!string.IsNullOrEmpty(state.ActiveQueryTabId)
                        && !string.IsNullOrEmpty(state.ActiveDatabase)
                        && !state.ActiveQueryTabByDatabase.ContainsKey(state.ActiveDatabase))
                    {
                        state.ActiveQueryTabByDatabase[state.ActiveDatabase] = state.ActiveQueryTabId;
                    }

                    return state;
                }
            }
        }
        catch (Exception ex) { Services.AppLogger.Error($"Failed to load connection state {connectionId}", ex); }
        return null;
    }

    public void SaveConnectionState(ConnectionTabState state)
    {
        try
        {
            Directory.CreateDirectory(ConnectionsDir);
            var file = Path.Combine(ConnectionsDir, $"{state.ConnectionId}.json");
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(file, json);
        }
        catch (Exception ex) { Services.AppLogger.Error($"Failed to save connection state {state.ConnectionId}", ex); }
    }

    public void DeleteConnectionState(string connectionId)
    {
        try
        {
            var file = Path.Combine(ConnectionsDir, $"{connectionId}.json");
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex) { Services.AppLogger.Error($"Failed to delete connection state {connectionId}", ex); }
    }

    private static void EncryptPasswords(AppState state)
    {
        foreach (var conn in state.SavedConnections)
        {
            if (!string.IsNullOrEmpty(conn.Password) && !Services.CredentialProtector.IsEncrypted(conn.Password))
                conn.Password = Services.CredentialProtector.Encrypt(conn.Password);
            if (!string.IsNullOrEmpty(conn.SshPassword) && !Services.CredentialProtector.IsEncrypted(conn.SshPassword))
                conn.SshPassword = Services.CredentialProtector.Encrypt(conn.SshPassword);
        }
    }

    private static void DecryptPasswords(AppState state)
    {
        foreach (var conn in state.SavedConnections)
        {
            conn.Password = Services.CredentialProtector.Decrypt(conn.Password);
            conn.SshPassword = Services.CredentialProtector.Decrypt(conn.SshPassword);
        }
    }
}
