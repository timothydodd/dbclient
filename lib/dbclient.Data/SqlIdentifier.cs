namespace dbclient.Data;

public enum SqlDialect
{
    SqlServer,
    MySql,
    Sqlite
}

/// <summary>Quotes identifiers for interpolation into SQL, escaping the closing delimiter.</summary>
public static class SqlIdentifier
{
    public static string Quote(SqlDialect dialect, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return dialect switch
        {
            SqlDialect.SqlServer => "[" + name.Replace("]", "]]") + "]",
            SqlDialect.MySql => "`" + name.Replace("`", "``") + "`",
            SqlDialect.Sqlite => "\"" + name.Replace("\"", "\"\"") + "\"",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
        };
    }
}
