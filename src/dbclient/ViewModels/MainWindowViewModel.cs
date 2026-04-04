using System.Collections.ObjectModel;
using Avalonia.Media;
using dbclient.Models;
using dbclient.Services;

namespace dbclient.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private ConnectionTabViewModel? _selectedConnectionTab;
    private bool _isConnectionPanelOpen = true;
    private string _themeName = "Dark";
    private int _cursorLine = 1;
    private int _cursorColumn = 1;
    private readonly StateService _stateService = new();
    private readonly QueryHistoryService _historyService = new();
    private System.ComponentModel.PropertyChangedEventHandler? _selectedTabHandler;

    public ObservableCollection<ConnectionTabViewModel> ConnectionTabs { get; } = new();
    public ObservableCollection<ConnectionConfig> SavedConnections { get; } = new();

    public RelayCommand NewQueryTabCommand { get; }
    public RelayCommand CloseQueryTabCommand { get; }
    public RelayCommand ExecuteQueryCommand { get; }
    public RelayCommand CancelQueryCommand { get; }
    public RelayCommand FormatQueryCommand { get; }
    public RelayCommand ExplainQueryCommand { get; }
    public RelayCommand ToggleConnectionPanelCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }

    public MainWindowViewModel()
    {
        NewQueryTabCommand = new RelayCommand(() => SelectedConnectionTab?.NewQueryTab());
        CloseQueryTabCommand = new RelayCommand(p => SelectedConnectionTab?.CloseQueryTab(p as SessionTabViewModel));
        ExecuteQueryCommand = new RelayCommand(() => _ = SafeFireAndForget(ExecuteAsync()));
        CancelQueryCommand = new RelayCommand(() => SelectedConnectionTab?.SelectedQueryTab?.ExecutionCts?.Cancel());
        FormatQueryCommand = new RelayCommand(FormatCurrentQuery);
        ExplainQueryCommand = new RelayCommand(() => _ = SafeFireAndForget(SelectedConnectionTab?.ExplainQueryAsync()));
        ToggleConnectionPanelCommand = new RelayCommand(() => IsConnectionPanelOpen = !IsConnectionPanelOpen);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        LoadState();
    }

    public ConnectionTabViewModel? SelectedConnectionTab
    {
        get => _selectedConnectionTab;
        set
        {
            var oldTab = _selectedConnectionTab;
            if (SetField(ref _selectedConnectionTab, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ExecutionTimeText));

                // Select first query tab if none selected
                if (value != null && value.SelectedQueryTab == null && value.QueryTabs.Count > 0)
                    value.SelectedQueryTab = value.QueryTabs[0];

                if (value?.SelectedQueryTab != null)
                {
                    CursorLine = value.SelectedQueryTab.CursorLine;
                    CursorColumn = value.SelectedQueryTab.CursorColumn;
                }

                // Unsubscribe from previous tab's PropertyChanged
                if (_selectedTabHandler != null && oldTab != null)
                    oldTab.PropertyChanged -= _selectedTabHandler;

                if (value != null)
                {
                    _selectedTabHandler = (_, e) =>
                    {
                        if (e.PropertyName == nameof(ConnectionTabViewModel.StatusText))
                            OnPropertyChanged(nameof(StatusText));
                        if (e.PropertyName == nameof(ConnectionTabViewModel.ExecutionTimeText))
                            OnPropertyChanged(nameof(ExecutionTimeText));
                    };
                    value.PropertyChanged += _selectedTabHandler;
                }
                else
                {
                    _selectedTabHandler = null;
                }
            }
        }
    }

    public bool IsConnectionPanelOpen
    {
        get => _isConnectionPanelOpen;
        set => SetField(ref _isConnectionPanelOpen, value);
    }

    public string ThemeName
    {
        get => _themeName;
        set
        {
            if (SetField(ref _themeName, value))
                OnPropertyChanged(nameof(ThemeDisplayName));
        }
    }

    public string ThemeDisplayName => $"Theme: {ThemeName}";

    private void ToggleTheme()
    {
        ThemeName = ThemeName == "Dark" ? "Dracula" : "Dark";
        App.Instance?.SetTheme(ThemeName);
    }

    public string StatusText => SelectedConnectionTab?.StatusText ?? "No connection";
    public string ExecutionTimeText => SelectedConnectionTab?.ExecutionTimeText ?? "";

    public int CursorLine
    {
        get => _cursorLine;
        set => SetField(ref _cursorLine, value);
    }

    public int CursorColumn
    {
        get => _cursorColumn;
        set => SetField(ref _cursorColumn, value);
    }

    public async Task<ConnectionTabViewModel> OpenConnectionTabAsync(ConnectionConfig config)
    {
        // Check if a tab for this connection already exists
        var existing = ConnectionTabs.FirstOrDefault(t => t.Config.Id == config.Id);
        if (existing != null)
        {
            SelectedConnectionTab = existing;
            return existing;
        }

        // Save connection if new
        if (!SavedConnections.Any(c => c.Id == config.Id))
            SavedConnections.Add(config);

        var connTab = new ConnectionTabViewModel(config);
        SetupConnectionTabListeners(connTab);
        connTab.NewQueryTab();

        ConnectionTabs.Add(connTab);
        SelectedConnectionTab = connTab;

        await connTab.ConnectAsync();
        SaveState();

        return connTab;
    }

    public void CloseConnectionTab(ConnectionTabViewModel? tab)
    {
        tab ??= SelectedConnectionTab;
        if (tab == null) return;

        var index = ConnectionTabs.IndexOf(tab);
        ConnectionTabs.Remove(tab);

        if (ConnectionTabs.Count > 0)
            SelectedConnectionTab = ConnectionTabs[Math.Min(index, ConnectionTabs.Count - 1)];
        else
            SelectedConnectionTab = null;

        _ = tab.Connection?.DisposeAsync();
        SaveState();
    }

    private void SetupConnectionTabListeners(ConnectionTabViewModel connTab)
    {
        // Forward cursor position from the active query tab
        connTab.PropertyChanged += (_, e) =>
        {
            if (connTab != SelectedConnectionTab) return;
            if (e.PropertyName == nameof(ConnectionTabViewModel.SelectedQueryTab))
            {
                var qt = connTab.SelectedQueryTab;
                if (qt != null)
                {
                    CursorLine = qt.CursorLine;
                    CursorColumn = qt.CursorColumn;

                    qt.PropertyChanged += (_, qe) =>
                    {
                        if (connTab != SelectedConnectionTab || qt != connTab.SelectedQueryTab) return;
                        if (qe.PropertyName == nameof(SessionTabViewModel.CursorLine))
                            CursorLine = qt.CursorLine;
                        else if (qe.PropertyName == nameof(SessionTabViewModel.CursorColumn))
                            CursorColumn = qt.CursorColumn;
                    };
                }
            }
        };
    }

    private static async Task SafeFireAndForget(Task? task)
    {
        if (task == null) return;
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Services.AppLogger.Error("Background task failed", ex);
        }
    }

    private void FormatCurrentQuery()
    {
        var tab = SelectedConnectionTab?.SelectedQueryTab;
        if (tab == null || string.IsNullOrWhiteSpace(tab.QueryText)) return;
        tab.SetQueryText(SqlFormatter.Format(tab.QueryText));
    }

    public List<QueryHistoryEntry> GetHistory() => _historyService.Load();

    private async Task ExecuteAsync()
    {
        if (SelectedConnectionTab == null) return;

        var queryText = SelectedConnectionTab.SelectedQueryTab?.QueryText;
        await SelectedConnectionTab.ExecuteQueryAsync();

        if (!string.IsNullOrWhiteSpace(queryText))
        {
            _historyService.Add(new QueryHistoryEntry
            {
                Query = queryText!.Length > 1000 ? queryText[..1000] : queryText!,
                Database = SelectedConnectionTab.ActiveDatabase,
                Connection = SelectedConnectionTab.DisplayName,
                ExecutedAt = DateTime.Now
            });
        }

        SaveState();
    }

    public void SaveState()
    {
        var state = new AppState
        {
            Theme = ThemeName,
            IsConnectionPanelOpen = IsConnectionPanelOpen,
            SavedConnections = SavedConnections.ToList(),
            ActiveConnectionTabId = SelectedConnectionTab?.Id,
            ConnectionTabs = ConnectionTabs.Select(ct => new ConnectionTabState
            {
                Id = ct.Id,
                ConnectionId = ct.Config.Id,
                ActiveDatabase = ct.ActiveDatabase,
                ActiveQueryTabId = ct.SelectedQueryTab?.Id,
                QueryTabs = ct.QueryTabs.Select((qt, i) => new TabState
                {
                    Id = qt.Id,
                    Title = qt.Title,
                    QueryText = qt.QueryText,
                    Order = i
                }).ToList()
            }).ToList()
        };

        _stateService.SaveState(state);
    }

    private void LoadState()
    {
        var state = _stateService.LoadState();
        _themeName = state.Theme ?? "Dark";
        IsConnectionPanelOpen = state.IsConnectionPanelOpen;

        foreach (var conn in state.SavedConnections)
            SavedConnections.Add(conn);

        // Restore connection tabs
        foreach (var ctState in state.ConnectionTabs)
        {
            var config = SavedConnections.FirstOrDefault(c => c.Id == ctState.ConnectionId);
            if (config == null) continue;

            var connTab = new ConnectionTabViewModel(config)
            {
                Id = ctState.Id,
                ActiveDatabase = ctState.ActiveDatabase
            };
            SetupConnectionTabListeners(connTab);

            // Restore query tabs
            foreach (var qtState in ctState.QueryTabs.OrderBy(t => t.Order))
                connTab.NewQueryTab(qtState.Title, qtState.QueryText, qtState.Id);

            // Select the right query tab
            if (ctState.ActiveQueryTabId != null)
                connTab.SelectedQueryTab = connTab.QueryTabs.FirstOrDefault(t => t.Id == ctState.ActiveQueryTabId);
            connTab.SelectedQueryTab ??= connTab.QueryTabs.FirstOrDefault();

            ConnectionTabs.Add(connTab);

            // Reconnect in background
            _ = SafeFireAndForget(connTab.ConnectAsync());
        }

        // Select the right connection tab
        if (state.ActiveConnectionTabId != null)
            SelectedConnectionTab = ConnectionTabs.FirstOrDefault(t => t.Id == state.ActiveConnectionTabId);
        SelectedConnectionTab ??= ConnectionTabs.FirstOrDefault();
    }
}

public class ConnectionTreeNode : ViewModelBase
{
    private bool _isExpanded;

    public string Name { get; set; }
    public string Detail { get; set; }
    public ConnectionTreeNodeType NodeType { get; set; }
    public ObservableCollection<ConnectionTreeNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public string NameColor => Services.ThemeColors.GetColor(NodeType switch
    {
        ConnectionTreeNodeType.Database => "DatabaseNodeColor",
        ConnectionTreeNodeType.Folder => "FolderNodeColor",
        ConnectionTreeNodeType.Table => "TableNodeColor",
        ConnectionTreeNodeType.View => "ViewNodeColor",
        ConnectionTreeNodeType.StoredProcedure => "ProcNodeColor",
        ConnectionTreeNodeType.Column => "ColumnNodeColor",
        _ => "DefaultNodeColor"
    }, "#DBE6EC");

    public ConnectionTreeNode(string name, ConnectionTreeNodeType type, string detail = "")
    {
        Name = name;
        NodeType = type;
        Detail = detail;
    }
}

public enum ConnectionTreeNodeType
{
    Database,
    Folder,
    Table,
    View,
    StoredProcedure,
    Column
}
