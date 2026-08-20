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
    private string? _defaultSchema;
    private readonly Dictionary<string, IList<DbColumn>> _columnCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheEntries = 500;

    public async Task InitializeAsync(ISchemaProvider schemaProvider, CancellationToken cancellationToken = default)
    {
        _schemaProvider = schemaProvider;
        _tables = await schemaProvider.GetTablesAsync(cancellationToken);
        _keywords = await schemaProvider.GetKeywordsAsync(cancellationToken);
        _defaultSchema = schemaProvider.DefaultSchema;
        _columnCache.Clear();
    }

    public async Task RefreshSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaProvider == null) return;
        _tables = await _schemaProvider.GetTablesAsync(cancellationToken);
        _keywords = await _schemaProvider.GetKeywordsAsync(cancellationToken);
        _defaultSchema = _schemaProvider.DefaultSchema;
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

            case SqlContextType.AfterGroupOrOrder:
                // "GROUP |" / "ORDER B|" — the only valid continuation is BY
                items.Add(new CompletionItem("BY", CompletionType.Keyword, 1));
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

        // No such table/alias — the prefix may be a schema ("dbo."): offer its tables
        if (items.Count == 0 && _tables != null && !prefix.Contains('.'))
        {
            foreach (var table in _tables)
            {
                if (string.Equals(table.Schema, prefix, StringComparison.OrdinalIgnoreCase))
                    items.Add(new CompletionItem(table.Name, CompletionType.Table, 1, $"{table.Schema}.{table.Name}"));
            }
        }
    }

    private async Task AddColumnsFromContext(List<CompletionItem> items, Dictionary<string, string> aliases, int basePriority, CancellationToken ct)
    {
        // Fetch each distinct table once (several aliases can point at the same table), and merge
        // same-named columns from different tables into one item that lists every source.
        var columnSources = new Dictionary<string, (DbColumn Column, List<string> Sources)>(StringComparer.OrdinalIgnoreCase);
        var seenTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in aliases)
        {
            var tableName = kvp.Value;
            if (!seenTables.Add(tableName))
                continue;

            var columns = await GetColumnsAsync(tableName, ct);
            foreach (var col in columns)
            {
                if (!columnSources.TryGetValue(col.Name, out var entry))
                {
                    entry = (col, new List<string>());
                    columnSources[col.Name] = entry;
                }
                entry.Sources.Add($"{tableName}.{col.Name}");
            }
        }

        foreach (var entry in columnSources.Values)
        {
            var desc = $"{string.Join(", ", entry.Sources)} ({entry.Column.DataType})";
            items.Add(new CompletionItem(entry.Column.Name, CompletionType.Column, basePriority, desc));
        }
    }

    private void AddTables(List<CompletionItem> items, int priority)
    {
        if (_tables == null) return;

        // Table names shared by several schemas must be inserted schema-qualified to be unambiguous
        var ambiguousNames = _tables
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(t => t.Schema ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in _tables)
        {
            var desc = string.IsNullOrEmpty(table.Schema) ? table.Name : $"{table.Schema}.{table.Name}";
            // Insert schema-qualified when the bare name is ambiguous, or when the table lives
            // outside the default schema (a bare name would resolve to the wrong schema and fail)
            var needsQualifying = !string.IsNullOrEmpty(table.Schema)
                && (ambiguousNames.Contains(table.Name)
                    || !string.Equals(table.Schema, _defaultSchema, StringComparison.OrdinalIgnoreCase));
            var text = needsQualifying ? $"{table.Schema}.{table.Name}" : table.Name;
            items.Add(new CompletionItem(text, CompletionType.Table, priority, desc));

            if (!string.IsNullOrEmpty(table.Schema))
                schemas.Add(table.Schema);
        }

        // Offer schema names too, so typing "partner" surfaces the schema alongside its tables;
        // a following "." then completes with that schema's tables
        foreach (var schema in schemas)
            items.Add(new CompletionItem(schema, CompletionType.Schema, priority, "Schema"));
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
