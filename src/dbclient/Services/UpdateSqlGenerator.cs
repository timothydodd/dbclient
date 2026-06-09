using System.Text;
using System.Text.RegularExpressions;
using dbclient.Models;
using dbclient.ViewModels;
using dbclient.Views;

namespace dbclient.Services;

public static class UpdateSqlGenerator
{
    public static string Generate(
        string tableName,
        ConnectionType connType,
        HashSet<string> pkColumns,
        string[] columnNames,
        List<ResultRow> originalRows,
        List<ResultRow> currentRows,
        IEnumerable<int> dirtyRowIndices)
    {
        var sb = new StringBuilder();

        string Quote(string name) => connType switch
        {
            ConnectionType.MySql => $"`{name}`",
            ConnectionType.Sqlite => $"\"{name}\"",
            _ => $"[{name}]"
        };

        // Values are escaped for SQL literal embedding (script is shown in preview dialog before execution)
        string SqlValue(string? val) => val == null ? "NULL" : $"'{val.Replace("'", "''")}'";
        string WhereValue(string? val, string quotedCol) => val == null ? $"{quotedCol} IS NULL" : $"{quotedCol} = {SqlValue(val)}";

        var whereColumnIndices = new List<int>();
        if (pkColumns.Count > 0)
        {
            for (int i = 0; i < columnNames.Length; i++)
            {
                if (pkColumns.Contains(columnNames[i]))
                    whereColumnIndices.Add(i);
            }
        }

        if (whereColumnIndices.Count == 0)
        {
            for (int i = 0; i < columnNames.Length; i++)
                whereColumnIndices.Add(i);
        }

        var quotedTable = Quote(tableName);

        foreach (var rowIndex in dirtyRowIndices.OrderBy(i => i))
        {
            if (rowIndex >= originalRows.Count || rowIndex >= currentRows.Count) continue;

            var original = originalRows[rowIndex];
            var current = currentRows[rowIndex];

            var setClauses = new List<string>();
            for (int i = 0; i < columnNames.Length; i++)
            {
                if (original[i] != current[i])
                    setClauses.Add($"{Quote(columnNames[i])} = {SqlValue(current[i])}");
            }

            if (setClauses.Count == 0) continue;

            var whereClauses = new List<string>();
            foreach (var i in whereColumnIndices)
                whereClauses.Add(WhereValue(original[i], Quote(columnNames[i])));

            sb.AppendLine($"UPDATE {quotedTable} SET {string.Join(", ", setClauses)} WHERE {string.Join(" AND ", whereClauses)};");
        }

        return sb.ToString().TrimEnd();
    }

    public static HashSet<string> FindPrimaryKeyColumns(string tableName, ConnectionTabViewModel connTab)
        => FindPrimaryKeyColumns(tableName, null, connTab);

    /// <summary>
    /// Locate the table node anywhere in the schema tree (it may be nested under a Schema node when the
    /// database groups by schema) and return the names of its primary-key columns. When <paramref name="schema"/>
    /// is supplied, it disambiguates same-named tables across schemas.
    /// </summary>
    public static HashSet<string> FindPrimaryKeyColumns(string tableName, string? schema, ConnectionTabViewModel connTab)
    {
        var pkColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var tableNode = FindTableNode(connTab.ConnectionTree, tableName, schema);
        if (tableNode == null) return pkColumns;

        foreach (var colNode in tableNode.Children)
        {
            if (colNode.NodeType == ConnectionTreeNodeType.Column && colNode.Detail?.Contains(" PK") == true)
                pkColumns.Add(colNode.Name);
        }

        return pkColumns;
    }

    private static ConnectionTreeNode? FindTableNode(
        IEnumerable<ConnectionTreeNode> nodes, string tableName, string? schema)
    {
        ConnectionTreeNode? schemaMismatch = null;

        foreach (var node in nodes)
        {
            if (node.NodeType is ConnectionTreeNodeType.Table or ConnectionTreeNodeType.View
                && node.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
                // Prefer a schema match when we know it; otherwise remember as a fallback.
                if (string.IsNullOrEmpty(schema)
                    || string.IsNullOrEmpty(node.SchemaName)
                    || node.SchemaName.Equals(schema, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
                schemaMismatch ??= node;
                continue;
            }

            var found = FindTableNode(node.Children, tableName, schema);
            if (found != null) return found;
        }

        return schemaMismatch;
    }

    /// <summary>Parses just the table name from a query's FROM clause (schema stripped).</summary>
    public static string? ParseTableName(string queryText)
        => ParseTableRef(queryText)?.Table;

    /// <summary>Parses the FROM clause into its optional schema and table name.</summary>
    public static (string? Schema, string Table)? ParseTableRef(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return null;

        var match = Regex.Match(queryText, @"\bFROM\s+([`\[\""']?\w+[`\]\""']?(?:\s*\.\s*[`\[\""']?\w+[`\]\""']?)?)",
            RegexOptions.IgnoreCase);

        if (!match.Success) return null;

        var raw = match.Groups[1].Value;
        raw = raw.Replace("[", "").Replace("]", "").Replace("`", "").Replace("\"", "").Replace("'", "");

        if (raw.Contains('.'))
        {
            var parts = raw.Split('.');
            return (parts[^2].Trim(), parts[^1].Trim());
        }

        return (null, raw.Trim());
    }
}
