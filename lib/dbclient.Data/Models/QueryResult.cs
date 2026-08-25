namespace dbclient.Data.Models;

public class ResultSet
{
    public string[] ColumnNames { get; set; } = [];
    public string?[] ColumnTypes { get; set; } = [];
    public List<string?[]> Rows { get; set; } = [];
    /// <summary>True when the MaxRows cap stopped reading before the result set was exhausted.</summary>
    public bool Truncated { get; set; }
}

public class QueryResult
{
    public List<ResultSet>? Data { get; set; }
    public ResultSet? FirstResult => Data?.FirstOrDefault();
    public int AffectedRows { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ErrorCode { get; set; }
    public string? SqlState { get; set; }
    public int? ErrorLine { get; set; }
    public bool IsError => !string.IsNullOrEmpty(ErrorMessage);
    public TimeSpan ExecutionTime { get; set; }
    /// <summary>Server info messages (SQL Server PRINT / informational RAISERROR, MySQL warnings).</summary>
    public List<string> Messages { get; set; } = [];
}
