using System.Text.Json;
using dbclient.Models;
using dbclient.Services;

namespace dbclient.Data.Tests;

[Collection("AppPaths")]
public class StateServiceTests
{
    private static void CleanStateFiles()
    {
        AppPaths.EnsureDirectory();
        foreach (var f in Directory.GetFiles(AppPaths.Root, "state.json*"))
            File.Delete(f);
    }

    private static AppState MakeState(string password = "s3cret-plain") => new()
    {
        Theme = "Dracula",
        MaxRows = 123,
        SavedConnections =
        {
            new ConnectionConfig
            {
                Id = "c1", DisplayName = "Conn One", Type = ConnectionType.MySql,
                Address = "db.example", User = "u", Password = password,
                UseSSH = true, SshPassword = "ssh-" + password, SshKeyPassphrase = "kp-" + password
            }
        },
        OpenConnectionIds = { "c1" },
        ActiveConnectionTabId = "tab1"
    };

    [Fact]
    public void Save_then_load_round_trips_and_never_writes_plaintext()
    {
        TestEnvironment.RequireRedirectedRoot();
        CleanStateFiles();

        var svc = new StateService();
        var state = MakeState();
        svc.SaveState(state);

        Assert.True(File.Exists(AppPaths.StateFile));
        Assert.False(File.Exists(AppPaths.StateFile + ".tmp"), "atomic write left a .tmp behind");

        var json = File.ReadAllText(AppPaths.StateFile);
        Assert.DoesNotContain("s3cret-plain", json);
        Assert.Contains("\"theme\": \"Dracula\"", json);
        Assert.Equal(1, JsonDocument.Parse(json).RootElement.GetProperty("version").GetInt32());

        // The live object handed in must not have been mutated by the save.
        Assert.Equal("s3cret-plain", state.SavedConnections[0].Password);

        var loaded = new StateService().LoadState();
        Assert.Equal("Dracula", loaded.Theme);
        Assert.Equal(123, loaded.MaxRows);
        Assert.Equal("tab1", loaded.ActiveConnectionTabId);
        var c = Assert.Single(loaded.SavedConnections);
        Assert.Equal("s3cret-plain", c.Password);
        Assert.Equal("ssh-s3cret-plain", c.SshPassword);
        Assert.Equal("kp-s3cret-plain", c.SshKeyPassphrase);
        Assert.Equal(ConnectionType.MySql, c.Type);
    }

    [Fact]
    public void Second_save_keeps_previous_as_bak()
    {
        TestEnvironment.RequireRedirectedRoot();
        CleanStateFiles();

        var svc = new StateService();
        svc.SaveState(new AppState { Theme = "First" });
        svc.SaveState(new AppState { Theme = "Second" });

        Assert.Contains("\"First\"", File.ReadAllText(AppPaths.StateFile + ".bak"));
        Assert.Contains("\"Second\"", File.ReadAllText(AppPaths.StateFile));
        Assert.False(File.Exists(AppPaths.StateFile + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_is_quarantined_and_bak_is_used()
    {
        TestEnvironment.RequireRedirectedRoot();
        CleanStateFiles();

        var svc = new StateService();
        svc.SaveState(new AppState { Theme = "Good" });
        svc.SaveState(new AppState { Theme = "Good" }); // now .bak exists with "Good"
        File.WriteAllText(AppPaths.StateFile, "{ this is not json");

        var loaded = new StateService().LoadState();

        Assert.Equal("Good", loaded.Theme);
        Assert.False(File.Exists(AppPaths.StateFile), "corrupt state.json should have been moved away");
        var quarantined = Directory.GetFiles(AppPaths.Root, "state.json.corrupt-*");
        var q = Assert.Single(quarantined);
        Assert.Equal("{ this is not json", File.ReadAllText(q));
    }

    [Fact]
    public void Corrupt_file_without_bak_yields_defaults()
    {
        TestEnvironment.RequireRedirectedRoot();
        CleanStateFiles();
        File.WriteAllText(AppPaths.StateFile, "null");

        var loaded = new StateService().LoadState();
        Assert.Equal("Dark", loaded.Theme);
        Assert.Empty(loaded.SavedConnections);
        Assert.Single(Directory.GetFiles(AppPaths.Root, "state.json.corrupt-*"));
    }

    [Fact]
    public void Missing_file_yields_defaults()
    {
        TestEnvironment.RequireRedirectedRoot();
        CleanStateFiles();
        var loaded = new StateService().LoadState();
        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal(100_000, loaded.MaxRows);
    }

    [Fact]
    public void Newer_version_file_is_backed_up_before_first_save()
    {
        TestEnvironment.RequireRedirectedRoot();
        CleanStateFiles();
        File.WriteAllText(AppPaths.StateFile, """{ "version": 99, "theme": "Future" }""");

        var svc = new StateService();
        var loaded = svc.LoadState();
        Assert.Equal("Future", loaded.Theme);
        Assert.Equal(99, loaded.Version);

        svc.SaveState(loaded);
        var newer = Assert.Single(Directory.GetFiles(AppPaths.Root, "state.json.newer-*"));
        Assert.Contains("\"Future\"", File.ReadAllText(newer));
        Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(AppPaths.StateFile)).RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void Connection_state_round_trip_and_migration()
    {
        TestEnvironment.RequireRedirectedRoot();
        var svc = new StateService();
        var id = "conn-" + Guid.NewGuid().ToString("N");
        svc.SaveConnectionState(new ConnectionTabState
        {
            Id = "t", ConnectionId = id, ActiveDatabase = "main",
            ActiveQueryTabId = "q1",
            QueryTabs = { new TabState { Id = "q1", Title = "Q", QueryText = "SELECT 1", Database = "" } }
        });

        var loaded = svc.LoadConnectionState(id)!;
        Assert.NotNull(loaded);
        Assert.Equal("main", loaded.QueryTabs[0].Database);          // migrated empty database
        Assert.Equal("q1", loaded.ActiveQueryTabByDatabase["main"]); // legacy id migrated into map

        svc.DeleteConnectionState(id);
        Assert.Null(svc.LoadConnectionState(id));
    }
}
