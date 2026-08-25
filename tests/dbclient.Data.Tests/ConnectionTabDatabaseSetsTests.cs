using dbclient.Models;
using dbclient.ViewModels;

namespace dbclient.Data.Tests;

/// <summary>Each connection keeps a separate set of query tabs per database.</summary>
public class ConnectionTabDatabaseSetsTests
{
    private static ConnectionTabViewModel NewConnTab() =>
        new(new ConnectionConfig { DisplayName = "test", Type = ConnectionType.Sqlite });

    [Fact]
    public void Switching_databases_swaps_to_a_separate_tab_set_and_back()
    {
        var ct = NewConnTab();
        var initial = ct.NewQueryTab();          // created before any database is active
        initial.QueryText = "select 1";

        ct.ActivateDatabaseTabs("alpha");
        Assert.Single(ct.QueryTabs);
        Assert.Same(initial, ct.QueryTabs[0]);   // orphan tab migrates into the first database
        Assert.Equal("alpha", initial.Database);

        var alpha2 = ct.NewQueryTab();
        Assert.Equal("alpha", alpha2.Database);
        Assert.Equal(2, ct.QueryTabs.Count);

        ct.ActivateDatabaseTabs("beta");
        Assert.Single(ct.QueryTabs);             // fresh set for beta
        Assert.Equal("beta", ct.QueryTabs[0].Database);
        Assert.DoesNotContain(initial, ct.QueryTabs);
        Assert.DoesNotContain(alpha2, ct.QueryTabs);

        ct.SelectedQueryTab = ct.QueryTabs[0];
        ct.ActivateDatabaseTabs("alpha");
        Assert.Equal(2, ct.QueryTabs.Count);     // alpha's set restored intact, selection remembered
        Assert.Contains(initial, ct.QueryTabs);
        Assert.Contains(alpha2, ct.QueryTabs);
        Assert.Same(alpha2, ct.SelectedQueryTab);
        Assert.Equal("select 1", initial.QueryText);

        Assert.Equal(3, ct.AllTabs().Count());
    }

    [Fact]
    public void State_round_trip_keeps_tabs_grouped_by_database()
    {
        var ct = NewConnTab();
        ct.RestoreTabsForDatabase("alpha", new[]
        {
            new TabState { Id = "a1", Title = "A1", QueryText = "select 'a1'", Order = 0 },
            new TabState { Id = "a2", Title = "A2", QueryText = "select 'a2'", Order = 1 },
        }, activeTabId: "a2");
        ct.RestoreTabsForDatabase("beta", new[]
        {
            new TabState { Id = "b1", Title = "B1", QueryText = "select 'b1'", Order = 0 },
        }, activeTabId: "b1");

        ct.ActivateDatabaseTabs("alpha");
        Assert.Equal(new[] { "a1", "a2" }, ct.QueryTabs.Select(t => t.Id));
        Assert.Equal("a2", ct.SelectedQueryTab?.Id);
        Assert.All(ct.QueryTabs, t => Assert.Equal("alpha", t.Database));

        var saved = ct.CollectAllTabStates().ToList();
        Assert.Equal(3, saved.Count);
        Assert.Equal(new[] { "alpha", "alpha", "beta" }, saved.OrderBy(s => s.Database).Select(s => s.Database));

        var active = ct.GetActiveTabIdsByDatabase();
        Assert.Equal("a2", active["alpha"]);
        Assert.Equal("b1", active["beta"]);
    }

    [Fact]
    public void Database_color_is_stable_for_a_name_and_differs_between_names()
    {
        var a = dbclient.Services.NameColors.RgbForName("Northwind");
        var b = dbclient.Services.NameColors.RgbForName("Northwind");
        var c = dbclient.Services.NameColors.RgbForName("AdventureWorks");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(dbclient.Services.NameColors.RgbForName("northwind"), a); // case-insensitive
    }
}
