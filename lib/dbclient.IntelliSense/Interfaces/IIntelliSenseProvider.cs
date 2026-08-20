using dbclient.IntelliSense.Models;

namespace dbclient.IntelliSense.Interfaces;

public interface IIntelliSenseProvider
{
    Task<IList<CompletionItem>> GetCompletionsAsync(string sqlText, int caretPosition, CancellationToken cancellationToken = default);
    Task InitializeAsync(ISchemaProvider schemaProvider, CancellationToken cancellationToken = default);
    Task RefreshSchemaAsync(CancellationToken cancellationToken = default);
}

public interface ISchemaProvider
{
    Task<IList<DbTable>> GetTablesAsync(CancellationToken cancellationToken = default);
    Task<IList<DbColumn>> GetColumnsAsync(string tableName, CancellationToken cancellationToken = default);
    Task<IList<string>> GetKeywordsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Schema that unqualified table names resolve to (e.g. "dbo" on SQL Server).
    /// Tables outside this schema must be completed schema-qualified. Null when the
    /// dialect has no schema concept.
    /// </summary>
    string? DefaultSchema => null;
}

public interface ISqlParser
{
    SqlContext AnalyzeContext(string sqlText, int caretPosition);
    IList<string> ExtractTableNames(string sqlText);
    IDictionary<string, string> ExtractTableAliases(string sqlText);
}
