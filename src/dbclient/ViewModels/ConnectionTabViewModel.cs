using System.Collections.ObjectModel;
using System.Data;
using Avalonia.Media;
using dbclient.Data.Connections;
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

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ConnectionConfig Config { get; }
    public IDbConnectionProvider? Connection { get; private set; }
    public IIntelliSenseProvider IntelliSenseProvider { get; private set; }

    public ObservableCollection<SessionTabViewModel> QueryTabs { get; } = new();
    public ObservableCollection<string> AvailableDatabases { get; } = new();
    public ObservableCollection<ConnectionTreeNode> ConnectionTree { get; } = new();

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

    public async Task ConnectAsync()
    {
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
        }
    }

    public async Task SwitchDatabaseAsync(string database, bool force = false)
    {
        if (Connection == null || (!force && database == ActiveDatabase)) return;

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

    private async Task BuildConnectionTreeAsync(string activeDatabase)
    {
        ConnectionTree.Clear();

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

                    if (schema.Tables.Count > 0)
                    {
                        var tablesNode = new ConnectionTreeNode("Tables", ConnectionTreeNodeType.Folder);
                        foreach (var table in schema.Tables.OrderBy(t => t.Name))
                        {
                            var tableNode = new ConnectionTreeNode(table.Name, ConnectionTreeNodeType.Table);
                            foreach (var col in table.Columns)
                            {
                                var pkTag = col.IsPrimaryKey ? " PK" : "";
                                var nullTag = col.IsNullable ? " NULL" : "";
                                tableNode.Children.Add(new ConnectionTreeNode(
                                    col.Name, ConnectionTreeNodeType.Column, $"{col.DataType}{pkTag}{nullTag}"));
                            }
                            tablesNode.Children.Add(tableNode);
                        }
                        dbNode.Children.Add(tablesNode);
                    }

                    if (schema.Views.Count > 0)
                    {
                        var viewsNode = new ConnectionTreeNode("Views", ConnectionTreeNodeType.Folder);
                        foreach (var view in schema.Views.OrderBy(v => v.Name))
                            viewsNode.Children.Add(new ConnectionTreeNode(view.Name, ConnectionTreeNodeType.View));
                        dbNode.Children.Add(viewsNode);
                    }

                    if (schema.StoredProcedures.Count > 0)
                    {
                        var procsNode = new ConnectionTreeNode("Stored Procedures", ConnectionTreeNodeType.Folder);
                        foreach (var proc in schema.StoredProcedures.OrderBy(p => p.Name))
                            procsNode.Children.Add(new ConnectionTreeNode(proc.Name, ConnectionTreeNodeType.StoredProcedure));
                        dbNode.Children.Add(procsNode);
                    }
                }

                ConnectionTree.Add(dbNode);
            }
        }
        catch (Exception ex)
        {
            ConnectionTree.Add(new ConnectionTreeNode($"Error: {ex.Message}", ConnectionTreeNodeType.Folder));
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
