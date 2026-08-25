using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using dbclient.Data;
using dbclient.Models;
using dbclient.ViewModels;
using dbclient.Views;

namespace dbclient.Services;

/// <summary>
/// Result of <see cref="UpdateSqlGenerator.Generate"/>. When <see cref="Error"/> is set no SQL was produced
/// and the caller must not execute anything.
/// </summary>
public sealed class UpdateScript
{
    public string Sql { get; init; } = "";
    /// <summary>Number of UPDATE statements in the script; each is expected to affect exactly one row.</summary>
    public int ExpectedStatements { get; init; }
    public string? Error { get; init; }
    public bool IsError => Error != null;

    public static UpdateScript Fail(string error) => new() { Error = error };
}

public static class UpdateSqlGenerator
{
    /// <summary>Typing this literal into a grid cell means SQL NULL (case-insensitive).</summary>
    public const string NullLiteral = "NULL";

    public static SqlDialect DialectFor(ConnectionType type) => type switch
    {
        ConnectionType.MySql => SqlDialect.MySql,
        ConnectionType.Sqlite => SqlDialect.Sqlite,
        _ => SqlDialect.SqlServer
    };

    public static bool IsNullValue(string? value) =>
        value == null || value.Equals(NullLiteral, StringComparison.OrdinalIgnoreCase);

    public static bool IsNumericType(string? dbTypeName)
    {
        if (string.IsNullOrEmpty(dbTypeName)) return false;
        var t = dbTypeName.ToLowerInvariant();
        return t.Contains("int") || t.Contains("decimal") || t.Contains("numeric")
            || t.Contains("float") || t.Contains("double") || t.Contains("real")
            || t.Contains("money") || t.Contains("number") || t == "bit" || t.Contains("serial");
    }

    /// <summary>
    /// Formats a grid value as a SQL literal: NULL for null / the NULL literal, an unquoted number only when the
    /// column type is numeric and the text parses as a number, otherwise a single-quoted string with quotes doubled.
    /// </summary>
    public static string Literal(string? value, string? dbTypeName)
    {
        if (IsNullValue(value)) return "NULL";
        if (IsNumericType(dbTypeName))
        {
            var v = value!.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return "1";
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return "0";
            if (decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                || double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return v;
        }
        return "'" + value!.Replace("'", "''") + "'";
    }

    /// <summary>
    /// Checks that the query is a plain single-table SELECT the editor can safely write back to.
    /// Returns a human-readable reason when it is not.
    /// </summary>
    public static string? ValidateEditableQuery(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return "No query text.";
        var stripped = Regex.Replace(queryText, @"--[^\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);

        if (Regex.IsMatch(stripped, @"\bJOIN\b", RegexOptions.IgnoreCase))
            return "the query contains a JOIN";
        if (Regex.IsMatch(stripped, @"\(\s*SELECT\b", RegexOptions.IgnoreCase))
            return "the query contains a subquery";
        if (Regex.IsMatch(stripped, @"\b(UNION|INTERSECT|EXCEPT|GROUP\s+BY|DISTINCT)\b", RegexOptions.IgnoreCase))
            return "the query aggregates or combines rows";

        var m = Regex.Match(stripped, @"\bSELECT\b(.*?)\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return "the query is not a SELECT ... FROM";
        var selectList = m.Groups[1].Value;
        selectList = Regex.Replace(selectList, @"^\s*TOP\s*(\(\s*\d+\s*\)|\d+)", "", RegexOptions.IgnoreCase);
        if (selectList.Contains('('))
            return "the select list contains a function or expression";
        if (Regex.IsMatch(selectList, @"\s+AS\s+", RegexOptions.IgnoreCase)
            || Regex.IsMatch(selectList, @"[\w\]`""]\s+[\w\[`""]+\s*(,|$)"))
            return "the select list contains aliased columns";
        if (Regex.IsMatch(selectList, @"[+/|]|\w\s*[\-*]\s*\w"))
            return "the select list contains a computed column";

        return null;
    }

    public static UpdateScript Generate(
        string tableName,
        string? schema,
        ConnectionType connType,
        HashSet<string> pkColumns,
        string[] columnNames,
        string?[] columnTypes,
        List<ResultRow> originalRows,
        List<ResultRow> currentRows,
        IEnumerable<int> dirtyRowIndices,
        string queryText)
    {
        var displayTable = string.IsNullOrEmpty(schema) ? tableName : $"{schema}.{tableName}";

        var invalid = ValidateEditableQuery(queryText);
        if (invalid != null)
            return UpdateScript.Fail($"Cannot apply changes: {invalid}. Edits are only supported on plain single-table SELECT queries.");

        if (pkColumns.Count == 0)
            return UpdateScript.Fail($"Cannot apply changes: no primary key found for {displayTable}. Edits are only supported on tables with a primary key.");

        var whereColumnIndices = new List<int>();
        foreach (var pk in pkColumns)
        {
            var idx = Array.FindIndex(columnNames, c => c.Equals(pk, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                return UpdateScript.Fail($"Cannot apply changes: primary key column '{pk}' of {displayTable} is not in the result set. Include every primary key column in the SELECT.");
            whereColumnIndices.Add(idx);
        }

        var dialect = DialectFor(connType);
        string Q(string name) => SqlIdentifier.Quote(dialect, name);
        string? TypeAt(int i) => i < columnTypes.Length ? columnTypes[i] : null;

        var quotedTable = string.IsNullOrEmpty(schema) ? Q(tableName) : $"{Q(schema)}.{Q(tableName)}";

        var statements = new List<string>();
        foreach (var rowIndex in dirtyRowIndices.OrderBy(i => i))
        {
            if (rowIndex >= originalRows.Count || rowIndex >= currentRows.Count) continue;

            var original = originalRows[rowIndex];
            var current = currentRows[rowIndex];

            var setClauses = new List<string>();
            for (int i = 0; i < columnNames.Length; i++)
            {
                if (original[i] != current[i])
                    setClauses.Add($"{Q(columnNames[i])} = {Literal(current[i], TypeAt(i))}");
            }
            if (setClauses.Count == 0) continue;

            var whereClauses = new List<string>();
            foreach (var i in whereColumnIndices)
            {
                var col = Q(columnNames[i]);
                whereClauses.Add(IsNullValue(original[i])
                    ? $"{col} IS NULL"
                    : $"{col} = {Literal(original[i], TypeAt(i))}");
            }

            statements.Add($"UPDATE {quotedTable} SET {string.Join(", ", setClauses)} WHERE {string.Join(" AND ", whereClauses)};");
        }

        if (statements.Count == 0)
            return UpdateScript.Fail("No changes to apply.");

        var sb = new StringBuilder();
        switch (dialect)
        {
            case SqlDialect.SqlServer:
                sb.AppendLine("SET XACT_ABORT ON;");
                sb.AppendLine("BEGIN TRAN;");
                foreach (var st in statements)
                {
                    sb.AppendLine(st);
                    sb.AppendLine("IF @@ROWCOUNT <> 1 THROW 50000, 'Expected 1 row', 1;");
                }
                sb.AppendLine("COMMIT;");
                break;
            case SqlDialect.MySql:
                sb.AppendLine("START TRANSACTION;");
                foreach (var st in statements) sb.AppendLine(st);
                sb.AppendLine("COMMIT;");
                break;
            default:
                sb.AppendLine("BEGIN;");
                foreach (var st in statements) sb.AppendLine(st);
                sb.AppendLine("COMMIT;");
                break;
        }

        return new UpdateScript { Sql = sb.ToString().TrimEnd(), ExpectedStatements = statements.Count };
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
            if (colNode.NodeType == ConnectionTreeNodeType.Column && IsPrimaryKeyNode(colNode))
                pkColumns.Add(colNode.Name);
        }

        return pkColumns;
    }

    /// <summary>Column nodes carry a structured <see cref="ConnectionTreeNode.IsPrimaryKey"/> flag set from DbColumn.</summary>
    public static bool IsPrimaryKeyNode(ConnectionTreeNode node) =>
        node.NodeType == ConnectionTreeNodeType.Column && node.IsPrimaryKey;

    private static ConnectionTreeNode? FindTableNode(
        IEnumerable<ConnectionTreeNode> nodes, string tableName, string? schema)
    {
        ConnectionTreeNode? schemaMismatch = null;

        foreach (var node in nodes)
        {
            if (node.NodeType is ConnectionTreeNodeType.Table or ConnectionTreeNodeType.View
                && node.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
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
