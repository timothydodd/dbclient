using dbclient.Models;
using dbclient.Services;

namespace dbclient.Data.Tests;

[Collection("AppPaths")]
public class QueryHistoryTests
{
    private static void Reset()
    {
        AppPaths.EnsureDirectory();
        if (File.Exists(AppPaths.HistoryFile)) File.Delete(AppPaths.HistoryFile);
    }

    [Fact]
    public void Concurrent_adds_lose_nothing()
    {
        TestEnvironment.RequireRedirectedRoot();
        Reset();
        var svc = new QueryHistoryService();

        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 20 }, i =>
            svc.Add(new QueryHistoryEntry { Query = $"SELECT {i}", ConnectionId = "c", ExecutedAt = DateTime.UtcNow }));

        var all = svc.Load();
        Assert.Equal(20, all.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"SELECT {i}").Order(),
            all.Select(e => e.Query).Order());
        Assert.False(File.Exists(AppPaths.HistoryFile + ".tmp"));
    }

    [Fact]
    public void Newest_first_and_capped_at_100()
    {
        TestEnvironment.RequireRedirectedRoot();
        Reset();
        var svc = new QueryHistoryService();
        for (int i = 0; i < 105; i++)
            svc.Add(new QueryHistoryEntry { Query = $"q{i}", ConnectionId = i % 2 == 0 ? "a" : "b", Database = "db" });

        var all = svc.Load();
        Assert.Equal(100, all.Count);
        Assert.Equal("q104", all[0].Query);
        Assert.Equal("q5", all[^1].Query);

        Assert.All(svc.LoadForConnection("a"), e => Assert.Equal("a", e.ConnectionId));
        Assert.Single(svc.LoadForConnection("a", "q100"));
        Assert.Single(svc.Search("Q104"));
    }

    [Fact]
    public void Missing_or_corrupt_file_loads_empty()
    {
        TestEnvironment.RequireRedirectedRoot();
        Reset();
        Assert.Empty(new QueryHistoryService().Load());
        File.WriteAllText(AppPaths.HistoryFile, "not json");
        Assert.Empty(new QueryHistoryService().Load());
    }
}
