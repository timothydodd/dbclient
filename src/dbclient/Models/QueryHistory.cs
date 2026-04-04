using System.Text.Json;

namespace dbclient.Models;

public class QueryHistoryEntry
{
    public string Query { get; set; } = "";
    public string Database { get; set; } = "";
    public string Connection { get; set; } = "";
    public DateTime ExecutedAt { get; set; }
}

public class QueryHistoryService
{
    private static readonly string HistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dbclient", "history.json");
    private const int MaxEntries = 100;

    public List<QueryHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(HistoryFile))
            {
                var json = File.ReadAllText(HistoryFile);
                return JsonSerializer.Deserialize<List<QueryHistoryEntry>>(json) ?? [];
            }
        }
        catch (Exception ex) { Services.AppLogger.Error("Failed to load query history", ex); }
        return [];
    }

    public List<QueryHistoryEntry> Search(string filter)
    {
        var all = Load();
        if (string.IsNullOrWhiteSpace(filter)) return all;
        return all.Where(e =>
            e.Query.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            e.Database.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            e.Connection.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void Add(QueryHistoryEntry entry)
    {
        var history = Load();
        history.Insert(0, entry);
        if (history.Count > MaxEntries)
            history.RemoveRange(MaxEntries, history.Count - MaxEntries);
        Save(history);
    }

    private static void Save(List<QueryHistoryEntry> history)
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryFile);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(HistoryFile, json);
        }
        catch (Exception ex) { Services.AppLogger.Error("Failed to save query history", ex); }
    }
}
