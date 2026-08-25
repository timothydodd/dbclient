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

    /// <summary>Max rows fetched per result set (0 = unlimited). Applied to every connection.</summary>
    public int MaxRows { get; set; } = 100_000;
    public double EditorFontSize { get; set; } = 14;
    public bool EditorWordWrap { get; set; }

    // Window geometry (null = never saved; use defaults)
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }
    public double? LeftPanelWidth { get; set; }
    /// <summary>Editor row height as a fraction of editor+results height (0..1).</summary>
    public double? EditorHeightRatio { get; set; }
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
    /// <summary>Backing .sql file when the tab was opened from / saved to disk.</summary>
    public string? FilePath { get; set; }
}

public class StateService
{
    /// <summary>Highest state file version this build understands.</summary>
    public const int CurrentVersion = 1;

    private static readonly string StateDir = Services.AppPaths.Root;
    private static readonly string ConnectionsDir = Services.AppPaths.ConnectionsDir;
    private static readonly string StateFile = Services.AppPaths.StateFile;
    private static readonly string StateBackupFile = Services.AppPaths.StateFile + ".bak";
    private static readonly object SaveLock = new();

    /// <summary>Set when the loaded file came from a newer app version; we back it up before the first save.</summary>
    private bool _newerVersionBackupPending;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppState LoadState()
    {
        if (!File.Exists(StateFile))
            return new AppState();

        var state = TryLoadStateFile(StateFile, out var corrupt);
        if (state == null && corrupt)
        {
            // Quarantine the bad file, then try the last known-good backup.
            try
            {
                var quarantined = Path.Combine(StateDir, $"state.json.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}");
                File.Move(StateFile, quarantined, overwrite: true);
                Services.AppLogger.Warn($"State file was unreadable; moved to {quarantined}");
            }
            catch (Exception ex) { Services.AppLogger.Error("Failed to quarantine corrupt state file", ex); }

            if (File.Exists(StateBackupFile))
            {
                state = TryLoadStateFile(StateBackupFile, out _);
                if (state != null)
                    Services.AppLogger.Info("Recovered state from state.json.bak");
            }
        }

        if (state == null)
            return new AppState();

        state.SavedConnections ??= new();
        state.OpenConnectionIds ??= new();

        if (state.Version > CurrentVersion)
        {
            Services.AppLogger.Warn($"State file version {state.Version} is newer than supported {CurrentVersion}; it will be backed up before any save");
            _newerVersionBackupPending = true;
        }
        else if (state.Version < CurrentVersion)
        {
            Migrate(state, state.Version);
            state.Version = CurrentVersion;
        }

        DecryptPasswords(state);
        return state;
    }

    /// <summary>Returns null if the file could not be parsed; <paramref name="corrupt"/> is true when an exception was thrown.</summary>
    private static AppState? TryLoadStateFile(string path, out bool corrupt)
    {
        corrupt = false;
        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
            if (state == null)
            {
                Services.AppLogger.Warn($"{Path.GetFileName(path)} deserialized to null");
                corrupt = true;
            }
            return state;
        }
        catch (Exception ex)
        {
            Services.AppLogger.Error($"Failed to load {Path.GetFileName(path)}", ex);
            corrupt = true;
            return null;
        }
    }

    /// <summary>Upgrades an older state file in place. Currently a no-op — add steps per version bump.</summary>
    private static void Migrate(AppState state, int fromVersion)
    {
        Services.AppLogger.Info($"Migrating state file from version {fromVersion} to {CurrentVersion}");
        // switch (fromVersion) { case 0: ...; goto case 1; }
    }

    public void SaveState(AppState state)
    {
        try
        {
            lock (SaveLock)
            {
                Services.AppPaths.EnsureDirectory();

                if (_newerVersionBackupPending && File.Exists(StateFile))
                {
                    var backup = Path.Combine(StateDir, $"state.json.newer-{DateTime.Now:yyyyMMdd-HHmmss}");
                    File.Copy(StateFile, backup, overwrite: true);
                    Services.AppPaths.RestrictFile(backup);
                    Services.AppLogger.Warn($"Backed up newer-version state file to {backup}");
                    _newerVersionBackupPending = false;
                }

                // Serialize a deep clone so the live ConnectionConfig objects are never mutated.
                var clone = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state, JsonOptions), JsonOptions)
                            ?? throw new InvalidOperationException("State clone failed");
                clone.Version = CurrentVersion;
                EncryptPasswords(clone);
                var json = JsonSerializer.Serialize(clone, JsonOptions);

                // Keep the previous good file as .bak, then write atomically.
                if (File.Exists(StateFile))
                {
                    File.Copy(StateFile, StateBackupFile, overwrite: true);
                    Services.AppPaths.RestrictFile(StateBackupFile);
                }
                Services.AppPaths.WriteTextAtomic(StateFile, json);
            }
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
            lock (SaveLock)
            {
                var file = Path.Combine(ConnectionsDir, $"{state.ConnectionId}.json");
                var json = JsonSerializer.Serialize(state, JsonOptions);
                Services.AppPaths.WriteTextAtomic(file, json);
            }
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

    /// <summary>Encrypts passwords on a clone. Throws if encryption fails (never writes plaintext).</summary>
    private static void EncryptPasswords(AppState state)
    {
        foreach (var conn in state.SavedConnections)
        {
            if (!string.IsNullOrEmpty(conn.Password) && !Services.CredentialProtector.IsEncrypted(conn.Password))
                conn.Password = Services.CredentialProtector.Encrypt(conn.Password);
            if (!string.IsNullOrEmpty(conn.SshPassword) && !Services.CredentialProtector.IsEncrypted(conn.SshPassword))
                conn.SshPassword = Services.CredentialProtector.Encrypt(conn.SshPassword);
            if (!string.IsNullOrEmpty(conn.SshKeyPassphrase) && !Services.CredentialProtector.IsEncrypted(conn.SshKeyPassphrase))
                conn.SshKeyPassphrase = Services.CredentialProtector.Encrypt(conn.SshKeyPassphrase);
        }
    }

    private static void DecryptPasswords(AppState state)
    {
        foreach (var conn in state.SavedConnections)
        {
            if (!Services.CredentialProtector.TryDecrypt(conn.Password, out var pw))
                Services.AppLogger.Warn($"Password for connection '{conn.DisplayName}' could not be decrypted; re-enter it in the connection dialog");
            conn.Password = pw;
            if (!Services.CredentialProtector.TryDecrypt(conn.SshPassword, out var sshPw))
                Services.AppLogger.Warn($"SSH password for connection '{conn.DisplayName}' could not be decrypted; re-enter it in the connection dialog");
            conn.SshPassword = sshPw;
            if (!Services.CredentialProtector.TryDecrypt(conn.SshKeyPassphrase, out var keyPass))
                Services.AppLogger.Warn($"SSH key passphrase for connection '{conn.DisplayName}' could not be decrypted; re-enter it in the connection dialog");
            conn.SshKeyPassphrase = keyPass;
        }
    }
}
