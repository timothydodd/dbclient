using dbclient.IntelliSense.Interfaces;
using dbclient.IntelliSense.Models;
using dbclient.IntelliSense.Parsing;

namespace dbclient.IntelliSense;

public class SqlIntelliSenseProvider : IIntelliSenseProvider
{
    private readonly ISqlParser _parser = new SqlParser();
    private ISchemaProvider? _schemaProvider;
    private IList<DbTable>? _tables;
    private IList<string>? _keywords;
    private readonly Dictionary<string, IList<DbColumn>> _columnCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheEntries = 500;

    public async Task InitializeAsync(ISchemaProvider schemaProvider, CancellationToken cancellationToken = default)
    {
        _schemaProvider = schemaProvider;
        _tables = await schemaProvider.GetTablesAsync(cancellationToken);
        _keywords = await schemaProvider.GetKeywordsAsync(cancellationToken);
        _columnCache.Clear();
    }

    public async Task RefreshSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaProvider == null) return;
        _tables = await _schemaProvider.GetTablesAsync(cancellationToken);
        _keywords = await _schemaProvider.GetKeywordsAsync(cancellationToken);
        _columnCache.Clear();
    }

    public async Task<IList<CompletionItem>> GetCompletionsAsync(string sqlText, int caretPosition, CancellationToken cancellationToken = default)
    {
        if (_schemaProvider == null || _tables == null || _keywords == null)
            return Array.Empty<CompletionItem>();

        var context = _parser.AnalyzeContext(sqlText, caretPosition);
        var items = new List<CompletionItem>();

        // Extract the partial word being typed for filtering
        var partialWord = GetPartialWord(sqlText, caretPosition);

        switch (context.Type)
        {
            case SqlContextType.ColumnAfterDot:
                await AddColumnsForPrefix(items, context.TablePrefix, context.TableAliases, cancellationToken);
                // Filter by any text typed after the dot
                var afterDot = GetTextAfterDot(sqlText, caretPosition);
                if (!string.IsNullOrEmpty(afterDot))
                    items = FilterItems(items, afterDot);
                break;

            case SqlContextType.FromClause:
            case SqlContextType.InsertInto:
            case SqlContextType.UpdateTable:
                AddTables(items, 1);
                AddKeywords(items, 5);
                items = FilterItems(items, partialWord);
                break;

            case SqlContextType.SelectList:
                // Only show columns from tables actually in the FROM clause
                if (context.TableAliases.Count > 0)
                {
                    await AddColumnsFromContext(items, context.TableAliases, 1, cancellationToken);
                    AddAliases(items, context.AvailableAliases, 2);
                }
                AddKeywords(items, 4);
                items = FilterItems(items, partialWord);
                break;

            case SqlContextType.WhereClause:
            case SqlContextType.JoinCondition:
                // Only show columns from tables actually in the FROM clause
                if (context.TableAliases.Count > 0)
                {
                    await AddColumnsFromContext(items, context.TableAliases, 1, cancellationToken);
                    AddAliases(items, context.AvailableAliases, 2);
                }
                AddKeywords(items, 4);
                items = FilterItems(items, partialWord);
                break;

            case SqlContextType.AfterTableName:
            case SqlContextType.AfterTableAlias:
                // Suggest JOIN keywords, WHERE, etc.
                items.Add(new CompletionItem("JOIN", CompletionType.Keyword, 1));
                items.Add(new CompletionItem("INNER JOIN", CompletionType.Keyword, 1));
                items.Add(new CompletionItem("LEFT JOIN", CompletionType.Keyword, 1));
                items.Add(new CompletionItem("RIGHT JOIN", CompletionType.Keyword, 1));
                items.Add(new CompletionItem("CROSS JOIN", CompletionType.Keyword, 1));
                items.Add(new CompletionItem("WHERE", CompletionType.Keyword, 2));
                items.Add(new CompletionItem("ORDER BY", CompletionType.Keyword, 3));
                items.Add(new CompletionItem("GROUP BY", CompletionType.Keyword, 3));
                items = FilterItems(items, partialWord);
                break;

            default: // General, Unknown
                AddKeywords(items, 1);
                items = FilterItems(items, partialWord);
                break;
        }

        // Deduplicate by text (case-insensitive)
        items = items
            .GroupBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(i => i.Priority).First())
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return items;
    }

    private async Task AddColumnsForPrefix(List<CompletionItem> items, string prefix, Dictionary<string, string> aliases, CancellationToken ct)
    {
        // Resolve prefix to table name via aliases
        var tableName = prefix;
        if (aliases.TryGetValue(prefix, out var resolved))
            tableName = resolved;

        var columns = await GetColumnsAsync(tableName, ct);
        foreach (var col in columns)
        {
            var desc = col.DataType;
            if (col.IsPrimaryKey) desc += " (PK)";
            if (col.IsNullable) desc += " NULL";
            items.Add(new CompletionItem(col.Name, CompletionType.Column, col.IsPrimaryKey ? 0 : 1, desc));
        }
    }

    private async Task AddColumnsFromContext(List<CompletionItem> items, Dictionary<string, string> aliases, int basePriority, CancellationToken ct)
    {
        var addedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in aliases)
        {
            var tableName = kvp.Value;
            var columns = await GetColumnsAsync(tableName, ct);
            foreach (var col in columns)
            {
                if (addedColumns.Add(col.Name))
                {
                    var desc = $"{tableName}.{col.Name} ({col.DataType})";
                    items.Add(new CompletionItem(col.Name, CompletionType.Column, basePriority, desc));
                }
            }
        }
    }

    private void AddTables(List<CompletionItem> items, int priority)
    {
        if (_tables == null) return;
        foreach (var table in _tables)
        {
            var desc = string.IsNullOrEmpty(table.Schema) ? table.Name : $"{table.Schema}.{table.Name}";
            items.Add(new CompletionItem(table.Name, CompletionType.Table, priority, desc));
        }
    }

    private static void AddAliases(List<CompletionItem> items, List<string> aliases, int priority)
    {
        foreach (var alias in aliases)
            items.Add(new CompletionItem(alias, CompletionType.Alias, priority, "Table alias"));
    }

    private void AddKeywords(List<CompletionItem> items, int priority)
    {
        if (_keywords == null) return;
        foreach (var keyword in _keywords)
            items.Add(new CompletionItem(keyword, CompletionType.Keyword, priority));
    }

    private async Task<IList<DbColumn>> GetColumnsAsync(string tableName, CancellationToken ct)
    {
        if (_columnCache.TryGetValue(tableName, out var cached))
            return cached;

        var columns = await _schemaProvider!.GetColumnsAsync(tableName, ct);

        if (_columnCache.Count >= MaxCacheEntries)
            _columnCache.Clear();

        _columnCache[tableName] = columns;
        return columns;
    }

    private static string GetPartialWord(string sqlText, int caretPosition)
    {
        var end = Math.Min(caretPosition, sqlText.Length);
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(sqlText[start - 1]) || sqlText[start - 1] == '_'))
            start--;

        // Don't count if the character before is a dot (that's ColumnAfterDot)
        if (start > 0 && sqlText[start - 1] == '.')
            return "";

        return sqlText[start..end];
    }

    private static string GetTextAfterDot(string sqlText, int caretPosition)
    {
        var end = Math.Min(caretPosition, sqlText.Length);
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(sqlText[start - 1]) || sqlText[start - 1] == '_'))
            start--;
        return sqlText[start..end];
    }

    private static List<CompletionItem> FilterItems(List<CompletionItem> items, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return items;

        var filtered = new List<CompletionItem>();
        foreach (var item in items)
        {
            // Exact prefix match (highest priority boost)
            if (item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(item);
            }
            // Substring match for 3+ characters
            else if (prefix.Length >= 3 && item.Text.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(new CompletionItem(item.Text, item.Type, item.Priority + 2, item.Description));
            }
        }

        return filtered;
    }
}
