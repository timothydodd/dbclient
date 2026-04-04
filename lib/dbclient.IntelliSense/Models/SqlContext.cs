namespace dbclient.IntelliSense.Models;

public class SqlContext
{
    public SqlContextType Type { get; set; } = SqlContextType.Unknown;
    public string TablePrefix { get; set; } = string.Empty;
    public bool ExpectingColumnName { get; set; }
    public bool ExpectingTableName { get; set; }
    public bool ExpectingKeyword { get; set; }
    public string LastKeyword { get; set; } = string.Empty;
    public Dictionary<string, string> TableAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AvailableAliases { get; set; } = new();
    public string CurrentQuery { get; set; } = string.Empty;
    public List<string> AllTokens { get; set; } = new();
}

public enum SqlContextType
{
    Unknown,
    General,
    SelectList,
    FromClause,
    WhereClause,
    JoinCondition,
    InsertInto,
    UpdateTable,
    ColumnAfterDot,
    AfterTableName,
    AfterTableAlias
}

public enum CompletionContext
{
    Keywords,
    Tables,
    Columns,
    Aliases,
    All
}

public enum CompletionType
{
    Keyword,
    Table,
    Column,
    Alias
}
