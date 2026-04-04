using System.Text.Json;

namespace dbclient.Models;

public class AppState
{
    public int Version { get; set; } = 1;
    public List<ConnectionConfig> SavedConnections { get; set; } = new();
    public List<ConnectionTabState> ConnectionTabs { get; set; } = new();
    public string? ActiveConnectionTabId { get; set; }
    public bool IsConnectionPanelOpen { get; set; } = true;
    public string Theme { get; set; } = "Dark";
}

public class ConnectionTabState
{
    public string Id { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string ActiveDatabase { get; set; } = "";
    public List<TabState> QueryTabs { get; set; } = new();
    public string? ActiveQueryTabId { get; set; }
}

public class TabState
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string QueryText { get; set; } = "";
    public int Order { get; set; }
}

public class StateService
{
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dbclient");
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
                // Ensure collections are never null (guards against corrupted state files)
                state.SavedConnections ??= new();
                state.ConnectionTabs ??= new();
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
            DecryptPasswords(state); // Restore in-memory values to plaintext
        }
        catch (Exception ex) { Services.AppLogger.Error("Failed to save state", ex); }
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
