using System.Collections.ObjectModel;
using System.Data;
using Avalonia.Media;
using dbclient.Data.Connections;
using dbclient.Data.Models;
using dbclient.IntelliSense;
using dbclient.IntelliSense.Interfaces;
using dbclient.Models;
using dbclient.Services;
using dbclient.Views;

namespace dbclient.ViewModels;

public class ConnectionTabViewModel : ViewModelBase
{
    private SessionTabViewModel? _selectedQueryTab;
    private string _activeDatabase = "";
    private string _statusText = "Connecting...";
    private string _executionTimeText = "";
    private int _queryTabCounter;
    private string _treeFilter = "";

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ConnectionConfig Config { get; }
    public IDbConnectionProvider? Connection { get; private set; }
    public IIntelliSenseProvider IntelliSenseProvider { get; private set; }

    public ObservableCollection<SessionTabViewModel> QueryTabs { get; } = new();
    public ObservableCollection<string> AvailableDatabases { get; } = new();
    public ObservableCollection<ConnectionTreeNode> ConnectionTree { get; } = new();
    private readonly List<ConnectionTreeNode> _unfilteredTree = new();

    // Tabs for inactive databases. Active database tabs live in QueryTabs.
    // The "" key holds tabs that haven't been assigned to a database yet
    // (e.g., a fresh tab created before connecting); they migrate to the
    // first database that becomes active.
    private readonly Dictionary<string, List<SessionTabViewModel>> _stashedTabs = new();
    private readonly Dictionary<string, string?> _stashedSelectedTabId = new();
    private string _tabsActiveDb = "";

    public string TreeFilter
    {
        get => _treeFilter;
        set
        {
            if (SetField(ref _treeFilter, value))
                ApplyTreeFilter();
        }
    }

    private bool _filterTables = true;
    public bool FilterTables
    {
        get => _filterTables;
        set { if (SetField(ref _filterTables, value)) ApplyTreeFilter(); }
    }

    private bool _filterColumns;
    public bool FilterColumns
    {
        get => _filterColumns;
        set { if (SetField(ref _filterColumns, value)) ApplyTreeFilter(); }
    }

    public string DisplayName => Config.ToString();
    public IBrush TabColor { get; }

    public ConnectionTabViewModel(ConnectionConfig config)
    {
        Config = config;
        TabColor = ColorFromName(config.ToString());
        IntelliSenseProvider = new SqlIntelliSenseProvider();
    }

    public SessionTabViewModel? SelectedQueryTab
    {
        get => _selectedQueryTab;
        set => SetField(ref _selectedQueryTab, value);
    }

    public string ActiveDatabase
    {
        get => _activeDatabase;
        set => SetField(ref _activeDatabase, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string ExecutionTimeText
    {
        get => _executionTimeText;
        set => SetField(ref _executionTimeText, value);
    }

    private bool _hasConnectionError;
    public bool HasConnectionError
    {
        get => _hasConnectionError;
        set => SetField(ref _hasConnectionError, value);
    }

    private string _connectionError = "";
    public string ConnectionError
    {
        get => _connectionError;
        set => SetField(ref _connectionError, value);
    }

    public async Task ConnectAsync()
    {
        HasConnectionError = false;
        ConnectionError = "";

        try
        {
            Connection = ConnectionDialog.CreateProvider(Config);
            StatusText = $"Connecting to {DisplayName}...";

            var master = await Connection.LoadDatabasesAsync();
            AvailableDatabases.Clear();
            foreach (var db in master.Databases)
                AvailableDatabases.Add(db.Name);

            var targetDb = !string.IsNullOrEmpty(Config.Database)
                ? Config.Database
                : !string.IsNullOrEmpty(ActiveDatabase)
                    ? ActiveDatabase
                    : master.Databases.FirstOrDefault()?.Name ?? "";

            if (!string.IsNullOrEmpty(targetDb))
                await SwitchDatabaseAsync(targetDb, force: true);
            else
                StatusText = $"{Config.Type}: {DisplayName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Connection failed: {ex.Message}";
            ConnectionError = ex.Message;
            HasConnectionError = true;
        }
    }

    public async Task SwitchDatabaseAsync(string database, bool force = false)
    {
        if (Connection == null || (!force && database == ActiveDatabase)) return;

        ActivateDatabaseTabs(database);
        ActiveDatabase = database;
        StatusText = $"Loading {database}...";

        try
        {
            var schemaService = new SchemaService(Connection, database);
            IntelliSenseProvider = new SqlIntelliSenseProvider();
            await IntelliSenseProvider.InitializeAsync(schemaService);

            foreach (var tab in QueryTabs)
                tab.IntelliSenseProvider = IntelliSenseProvider;

            await BuildConnectionTreeAsync(database);
            StatusText = $"{Config.Type}: {database}";
        }
        catch (Exception ex)
        {
            StatusText = $"Schema load failed: {ex.Message}";
        }
    }

    public void ActivateDatabaseTabs(string database)
    {
        if (database == _tabsActiveDb) return;

        var prev = _tabsActiveDb;

        // Where do tabs currently in QueryTabs belong? If we have a previous
        // active db, stash under it. Otherwise (initial connect with unassigned
        // tabs), they are migrating into the new database.
        var carryOverKey = !string.IsNullOrEmpty(prev) ? prev : database;

        if (QueryTabs.Count > 0)
        {
            if (!_stashedTabs.TryGetValue(carryOverKey, out var bucket))
            {
                bucket = new List<SessionTabViewModel>();
                _stashedTabs[carryOverKey] = bucket;
            }
            foreach (var t in QueryTabs) bucket.Add(t);
            _stashedSelectedTabId[carryOverKey] = SelectedQueryTab?.Id;
        }

        // Migrate any unassigned (orphaned) tabs into the target database.
        if (!string.IsNullOrEmpty(database)
            && carryOverKey != ""
            && _stashedTabs.TryGetValue("", out var orphans))
        {
            if (!_stashedTabs.TryGetValue(database, out var into))
            {
                into = new List<SessionTabViewModel>();
                _stashedTabs[database] = into;
            }
            foreach (var t in orphans) into.Add(t);
            _stashedTabs.Remove("");

            if (!_stashedSelectedTabId.ContainsKey(database)
                && _stashedSelectedTabId.TryGetValue("", out var orphSel))
            {
                _stashedSelectedTabId[database] = orphSel;
            }
            _stashedSelectedTabId.Remove("");
        }

        QueryTabs.Clear();
        if (_stashedTabs.TryGetValue(database, out var newTabs))
        {
            foreach (var t in newTabs) QueryTabs.Add(t);
            _stashedTabs.Remove(database);
        }

        var selId = _stashedSelectedTabId.TryGetValue(database, out var s) ? s : null;
        _stashedSelectedTabId.Remove(database);
        SelectedQueryTab = (selId != null
            ? QueryTabs.FirstOrDefault(t => t.Id == selId)
            : null) ?? QueryTabs.FirstOrDefault();

        _tabsActiveDb = database;

        if (QueryTabs.Count == 0)
            NewQueryTab();
    }

    public void RestoreTabsForDatabase(string database, IEnumerable<TabState> tabStates, string? activeTabId)
    {
        var key = database ?? "";
        var list = new List<SessionTabViewModel>();
        foreach (var ts in tabStates.OrderBy(t => t.Order))
        {
            var tab = new SessionTabViewModel
            {
                Id = ts.Id,
                Title = ts.Title,
                IntelliSenseProvider = IntelliSenseProvider
            };
            tab.SetInitialQueryText(ts.QueryText);
            list.Add(tab);
        }
        if (list.Count == 0) return;

        if (_stashedTabs.TryGetValue(key, out var existing))
            existing.AddRange(list);
        else
            _stashedTabs[key] = list;

        if (activeTabId != null)
            _stashedSelectedTabId[key] = activeTabId;
    }

    public IEnumerable<TabState> CollectAllTabStates()
    {
        for (int i = 0; i < QueryTabs.Count; i++)
        {
            var t = QueryTabs[i];
            yield return new TabState
            {
                Id = t.Id,
                Title = t.Title,
                QueryText = t.QueryText,
                Database = _tabsActiveDb,
                Order = i
            };
        }

        foreach (var (db, tabs) in _stashedTabs)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                var t = tabs[i];
                yield return new TabState
                {
                    Id = t.Id,
                    Title = t.Title,
                    QueryText = t.QueryText,
                    Database = db,
                    Order = i
                };
            }
        }
    }

    public Dictionary<string, string?> GetActiveTabIdsByDatabase()
    {
        var result = new Dictionary<string, string?>(_stashedSelectedTabId);
        if (!string.IsNullOrEmpty(_tabsActiveDb))
            result[_tabsActiveDb] = SelectedQueryTab?.Id;
        return result;
    }

    public IEnumerable<SessionTabViewModel> AllTabs()
    {
        foreach (var t in QueryTabs) yield return t;
        foreach (var (_, list) in _stashedTabs)
            foreach (var t in list) yield return t;
    }

    private async Task BuildConnectionTreeAsync(string activeDatabase)
    {
        ConnectionTree.Clear();
        _unfilteredTree.Clear();

        try
        {
            // Show all databases as top-level nodes
            foreach (var dbName in AvailableDatabases)
            {
                var dbNode = new ConnectionTreeNode(dbName, ConnectionTreeNodeType.Database);

                // Only load schema for the active database
                if (dbName == activeDatabase)
                {
                    dbNode.IsExpanded = true;
                    var schema = await Connection!.LoadDatabaseSchemaAsync(dbName);

                    // Determine if we need schema grouping (multiple distinct schemas)
                    var tableSchemas = schema.Tables.Select(t => t.Schema).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
                    var viewSchemas = schema.Views.Select(v => v.Schema).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
                    var procSchemas = schema.StoredProcedures.Select(p => p.Schema).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
                    var allSchemas = tableSchemas.Union(viewSchemas).Union(procSchemas).ToList();
                    var useSchemaGrouping = allSchemas.Count > 1;

                    if (schema.Tables.Count > 0)
                    {
                        var tablesNode = new ConnectionTreeNode("Tables", ConnectionTreeNodeType.Folder);
                        if (useSchemaGrouping && tableSchemas.Count > 0)
                        {
                            foreach (var schemaGroup in schema.Tables.GroupBy(t => t.Schema).OrderBy(g => g.Key))
                            {
                                var schemaNode = new ConnectionTreeNode(schemaGroup.Key, ConnectionTreeNodeType.Schema);
                                foreach (var table in schemaGroup.OrderBy(t => t.Name))
                                {
                                    var tableNode = new ConnectionTreeNode(table.Name, ConnectionTreeNodeType.Table, schemaName: table.Schema);
                                    AddColumnNodes(tableNode, table.Columns);
                                    schemaNode.Children.Add(tableNode);
                                }
                                tablesNode.Children.Add(schemaNode);
                            }
                        }
                        else
                        {
                            foreach (var table in schema.Tables.OrderBy(t => t.Name))
                            {
                                var tableNode = new ConnectionTreeNode(table.Name, ConnectionTreeNodeType.Table, schemaName: table.Schema);
                                AddColumnNodes(tableNode, table.Columns);
                                tablesNode.Children.Add(tableNode);
                            }
                        }
                        dbNode.Children.Add(tablesNode);
                    }

                    if (schema.Views.Count > 0)
                    {
                        var viewsNode = new ConnectionTreeNode("Views", ConnectionTreeNodeType.Folder);
                        if (useSchemaGrouping && viewSchemas.Count > 0)
                        {
                            foreach (var schemaGroup in schema.Views.GroupBy(v => v.Schema).OrderBy(g => g.Key))
                            {
                                var schemaNode = new ConnectionTreeNode(schemaGroup.Key, ConnectionTreeNodeType.Schema);
                                foreach (var view in schemaGroup.OrderBy(v => v.Name))
                                    schemaNode.Children.Add(new ConnectionTreeNode(view.Name, ConnectionTreeNodeType.View, schemaName: view.Schema));
                                viewsNode.Children.Add(schemaNode);
                            }
                        }
                        else
                        {
                            foreach (var view in schema.Views.OrderBy(v => v.Name))
                                viewsNode.Children.Add(new ConnectionTreeNode(view.Name, ConnectionTreeNodeType.View, schemaName: view.Schema));
                        }
                        dbNode.Children.Add(viewsNode);
                    }

                    if (schema.StoredProcedures.Count > 0)
                    {
                        var procsNode = new ConnectionTreeNode("Stored Procedures", ConnectionTreeNodeType.Folder);
                        if (useSchemaGrouping && procSchemas.Count > 0)
                        {
                            foreach (var schemaGroup in schema.StoredProcedures.GroupBy(p => p.Schema).OrderBy(g => g.Key))
                            {
                                var schemaNode = new ConnectionTreeNode(schemaGroup.Key, ConnectionTreeNodeType.Schema);
                                foreach (var proc in schemaGroup.OrderBy(p => p.Name))
                                    schemaNode.Children.Add(new ConnectionTreeNode(proc.Name, ConnectionTreeNodeType.StoredProcedure, schemaName: proc.Schema));
                                procsNode.Children.Add(schemaNode);
                            }
                        }
                        else
                        {
                            foreach (var proc in schema.StoredProcedures.OrderBy(p => p.Name))
                                procsNode.Children.Add(new ConnectionTreeNode(proc.Name, ConnectionTreeNodeType.StoredProcedure, schemaName: proc.Schema));
                        }
                        dbNode.Children.Add(procsNode);
                    }
                }

                _unfilteredTree.Add(dbNode);
            }
        }
        catch (Exception ex)
        {
            _unfilteredTree.Add(new ConnectionTreeNode($"Error: {ex.Message}", ConnectionTreeNodeType.Folder));
        }

        ApplyTreeFilter();
    }

    private void ApplyTreeFilter()
    {
        ConnectionTree.Clear();

        var alternatives = ParseFilter(_treeFilter);
        if (alternatives.Count == 0)
        {
            foreach (var node in _unfilteredTree) ConnectionTree.Add(node);
            return;
        }

        foreach (var node in _unfilteredTree)
        {
            var clone = FilterNode(node, alternatives, _filterTables, _filterColumns);
            if (clone != null) ConnectionTree.Add(clone);
        }
    }

    // "foo bar|baz" → [[foo, bar], [baz]]: (foo AND bar) OR (baz). Empty if no real tokens.
    private static List<List<string>> ParseFilter(string raw)
    {
        var result = new List<List<string>>();
        foreach (var alt in raw.Split('|'))
        {
            var tokens = alt.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Count > 0) result.Add(tokens);
        }
        return result;
    }

    private static bool MatchesName(string name, List<List<string>> alternatives) =>
        alternatives.Any(alt => alt.All(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)));

    private static ConnectionTreeNode? FilterNode(ConnectionTreeNode node, List<List<string>> alternatives, bool searchTables, bool searchColumns)
    {
        // Determine if this node itself matches based on scope settings
        bool selfMatches = node.NodeType switch
        {
            ConnectionTreeNodeType.Table or ConnectionTreeNodeType.View or ConnectionTreeNodeType.StoredProcedure
                => searchTables && MatchesName(node.Name, alternatives),
            ConnectionTreeNodeType.Column
                => searchColumns && MatchesName(node.Name, alternatives),
            _ => false
        };

        var matchedChildren = new List<ConnectionTreeNode>();
        foreach (var child in node.Children)
        {
            // Skip column children entirely when not searching columns
            if (!searchColumns && child.NodeType == ConnectionTreeNodeType.Column) continue;
            var c = FilterNode(child, alternatives, searchTables, searchColumns);
            if (c != null) matchedChildren.Add(c);
        }

        if (!selfMatches && matchedChildren.Count == 0) return null;

        // Containers always expanded so matches are reachable.
        // Tables/views expand only when a child column matched (i.e., column scope is active).
        bool expanded = node.NodeType switch
        {
            ConnectionTreeNodeType.Database
                or ConnectionTreeNodeType.Folder
                or ConnectionTreeNodeType.Schema => true,
            ConnectionTreeNodeType.Table or ConnectionTreeNodeType.View or ConnectionTreeNodeType.StoredProcedure
                => matchedChildren.Count > 0,
            _ => false
        };

        var clone = new ConnectionTreeNode(node.Name, node.NodeType, node.Detail, node.SchemaName)
        {
            IsExpanded = expanded
        };
        foreach (var c in matchedChildren) clone.Children.Add(c);
        return clone;
    }

    private static void AddColumnNodes(ConnectionTreeNode tableNode, List<DbColumn> columns)
    {
        foreach (var col in columns)
        {
            var pkTag = col.IsPrimaryKey ? " PK" : "";
            var nullTag = col.IsNullable ? " NULL" : "";
            tableNode.Children.Add(new ConnectionTreeNode(
                col.Name, ConnectionTreeNodeType.Column, $"{col.DataType}{pkTag}{nullTag}"));
        }
    }

    public SessionTabViewModel NewQueryTab(string? title = null, string queryText = "", string? id = null)
    {
        _queryTabCounter++;
        var tab = new SessionTabViewModel
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Title = title ?? $"Query {_queryTabCounter}",
            IntelliSenseProvider = IntelliSenseProvider
        };
        tab.SetInitialQueryText(queryText);

        QueryTabs.Add(tab);
        SelectedQueryTab = tab;
        return tab;
    }

    public void CloseQueryTab(SessionTabViewModel? tab)
    {
        tab ??= SelectedQueryTab;
        if (tab == null) return;

        var index = QueryTabs.IndexOf(tab);
        QueryTabs.Remove(tab);

        if (QueryTabs.Count > 0)
            SelectedQueryTab = QueryTabs[Math.Min(index, QueryTabs.Count - 1)];
        else
            NewQueryTab();
    }

    public void CloseOtherQueryTabs(SessionTabViewModel tab)
    {
        var others = QueryTabs.Where(t => t != tab).ToList();
        foreach (var t in others)
            QueryTabs.Remove(t);
        SelectedQueryTab = tab;
    }

    public void CloseQueryTabsToRight(SessionTabViewModel tab)
    {
        var index = QueryTabs.IndexOf(tab);
        if (index < 0) return;
        var toRemove = QueryTabs.Skip(index + 1).ToList();
        foreach (var t in toRemove)
            QueryTabs.Remove(t);
        SelectedQueryTab = tab;
    }

    public void CloseQueryTabsToLeft(SessionTabViewModel tab)
    {
        var index = QueryTabs.IndexOf(tab);
        if (index <= 0) return;
        var toRemove = QueryTabs.Take(index).ToList();
        foreach (var t in toRemove)
            QueryTabs.Remove(t);
        SelectedQueryTab = tab;
    }

    public async Task ExecuteQueryAsync()
    {
        if (SelectedQueryTab == null) return;

        SelectedQueryTab.RequestExecute();

        var queryText = !string.IsNullOrWhiteSpace(SelectedQueryTab.QueryTextToExecute)
            ? SelectedQueryTab.QueryTextToExecute
            : SelectedQueryTab.QueryText;

        if (string.IsNullOrWhiteSpace(queryText))
        {
            SelectedQueryTab.HasMessage = true;
            SelectedQueryTab.Message = "No query to execute.";
            SelectedQueryTab.MessageColor = Services.ThemeColors.Warning;
            return;
        }

        if (Connection == null)
        {
            SelectedQueryTab.HasMessage = true;
            SelectedQueryTab.Message = "Not connected.";
            SelectedQueryTab.MessageColor = Services.ThemeColors.Warning;
            return;
        }

        SelectedQueryTab.HasMessage = true;
        SelectedQueryTab.Message = "Executing...";
        SelectedQueryTab.MessageColor = Services.ThemeColors.Info;
        SelectedQueryTab.ResultData = null;

        var cts = new CancellationTokenSource();
        SelectedQueryTab.ExecutionCts = cts;
        SelectedQueryTab.IsExecuting = true;

        try
        {
            var result = await Connection.ExecuteQueryAsync(ActiveDatabase, queryText, cts.Token);

            if (result.IsError)
            {
                var errorMsg = result.ErrorMessage!;
                if (result.ErrorCode.HasValue)
                    errorMsg = $"[Error {result.ErrorCode}] {errorMsg}";
                if (result.ErrorLine.HasValue && result.ErrorLine > 0)
                    errorMsg += $"\n(Line {result.ErrorLine})";

                SelectedQueryTab.HasMessage = true;
                SelectedQueryTab.Message = errorMsg;
                SelectedQueryTab.MessageColor = Services.ThemeColors.Error;
                SelectedQueryTab.RowCountText = "";
            }
            else if (result.Data != null && result.Data.Count > 0 && result.Data.Any(d => d.Rows.Count > 0))
            {
                SelectedQueryTab.ResultData = result.Data;
                var totalRows = result.Data.Sum(d => d.Rows.Count);

                SelectedQueryTab.RowCountText = result.Data.Count > 1
                    ? $"{totalRows} rows ({result.Data.Count} result sets)"
                    : $"{totalRows} rows";
                SelectedQueryTab.HasMessage = false;
                SelectedQueryTab.Message = "";
            }
            else
            {
                SelectedQueryTab.HasMessage = true;
                SelectedQueryTab.Message = $"{result.AffectedRows} row(s) affected.";
                SelectedQueryTab.MessageColor = Services.ThemeColors.Success;
                SelectedQueryTab.RowCountText = $"{result.AffectedRows} affected";
                SelectedQueryTab.ResultData = null;
            }

            ExecutionTimeText = $"{result.ExecutionTime.TotalMilliseconds:F0}ms";
        }
        catch (OperationCanceledException)
        {
            SelectedQueryTab.HasMessage = true;
            SelectedQueryTab.Message = "Query cancelled.";
            SelectedQueryTab.MessageColor = Services.ThemeColors.Warning;
            SelectedQueryTab.RowCountText = "";
        }
        catch (Exception ex)
        {
            SelectedQueryTab.HasMessage = true;
            SelectedQueryTab.Message = ex.Message;
            SelectedQueryTab.MessageColor = Services.ThemeColors.Error;
        }
        finally
        {
            SelectedQueryTab.IsExecuting = false;
            SelectedQueryTab.ExecutionCts = null;
            cts.Dispose();
        }
    }

    public async Task ExplainQueryAsync()
    {
        if (SelectedQueryTab == null) return;

        var queryText = !string.IsNullOrWhiteSpace(SelectedQueryTab.QueryTextToExecute)
            ? SelectedQueryTab.QueryTextToExecute
            : SelectedQueryTab.QueryText;

        if (string.IsNullOrWhiteSpace(queryText)) return;

        var explainSql = Config.Type switch
        {
            ConnectionType.Sqlite => $"EXPLAIN QUERY PLAN {queryText}",
            ConnectionType.SqlServer => $"SET SHOWPLAN_TEXT ON;\n{queryText}\nSET SHOWPLAN_TEXT OFF;",
            _ => $"EXPLAIN {queryText}"
        };

        // Temporarily swap query text, execute, then restore
        var original = SelectedQueryTab.QueryText;
        SelectedQueryTab.SetQueryText(explainSql);
        await ExecuteQueryAsync();
        SelectedQueryTab.SetQueryText(original);
    }

    // Deterministic color from connection name
    private static IBrush ColorFromName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return new SolidColorBrush(Color.FromRgb(189, 147, 249)); // default purple

        var hash = 0;
        foreach (var c in name)
            hash = hash * 31 + c;

        var hue = Math.Abs(hash % 360);
        var (r, g, b) = HslToRgb(hue / 360.0, 0.65, 0.6);
        return new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
    }

    private static (int r, int g, int b) HslToRgb(double h, double s, double l)
    {
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }
        return ((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
