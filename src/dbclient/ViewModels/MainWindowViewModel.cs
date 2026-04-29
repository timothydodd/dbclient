using System.Collections.ObjectModel;
using Avalonia.Media;
using dbclient.Models;
using dbclient.Services;

namespace dbclient.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private ConnectionTabViewModel? _selectedConnectionTab;
    private bool _isConnectionPanelOpen = true;
    private bool _isHistoryPanelOpen;
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
    public RelayCommand SaveCommand { get; }
    public RelayCommand ToggleConnectionPanelCommand { get; }
    public RelayCommand ToggleHistoryPanelCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }

    public MainWindowViewModel()
    {
        NewQueryTabCommand = new RelayCommand(() => SelectedConnectionTab?.NewQueryTab());
        CloseQueryTabCommand = new RelayCommand(p => SelectedConnectionTab?.CloseQueryTab(p as SessionTabViewModel));
        SaveCommand = new RelayCommand(SaveState);
        ExecuteQueryCommand = new RelayCommand(() => _ = SafeFireAndForget(ExecuteAsync()));
        CancelQueryCommand = new RelayCommand(() => SelectedConnectionTab?.SelectedQueryTab?.ExecutionCts?.Cancel());
        FormatQueryCommand = new RelayCommand(FormatCurrentQuery);
        ExplainQueryCommand = new RelayCommand(() => _ = SafeFireAndForget(SelectedConnectionTab?.ExplainQueryAsync()));
        ToggleConnectionPanelCommand = new RelayCommand(() => IsConnectionPanelOpen = !IsConnectionPanelOpen);
        ToggleHistoryPanelCommand = new RelayCommand(() => IsHistoryPanelOpen = !IsHistoryPanelOpen);
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

    public bool IsHistoryPanelOpen
    {
        get => _isHistoryPanelOpen;
        set => SetField(ref _isHistoryPanelOpen, value);
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
        ThemeName = ThemeName switch
        {
            "Dark" => "Dracula",
            "Dracula" => "Light",
            _ => "Dark"
        };
        App.Instance?.SetTheme(ThemeName);
    }

    public string StatusText => SelectedConnectionTab?.StatusText ?? "No connection";
    public object? HistoryChanged => null; // notification-only property
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

        // Restore saved session state for this connection
        var savedState = _stateService.LoadConnectionState(config.Id);

        var connTab = new ConnectionTabViewModel(config)
        {
            Id = savedState?.Id ?? Guid.NewGuid().ToString("N")
        };
        SetupConnectionTabListeners(connTab);

        if (savedState != null)
        {
            connTab.ActiveDatabase = savedState.ActiveDatabase;
            RestoreQueryTabsFromState(connTab, savedState);
        }
        else
        {
            connTab.NewQueryTab();
        }

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

        // Save this connection's session state before closing
        SaveConnectionState(tab);

        var index = ConnectionTabs.IndexOf(tab);
        ConnectionTabs.Remove(tab);

        if (ConnectionTabs.Count > 0)
            SelectedConnectionTab = ConnectionTabs[Math.Min(index, ConnectionTabs.Count - 1)];
        else
            SelectedConnectionTab = null;

        _ = tab.Connection?.DisposeAsync();
        SaveState();
    }

    public void DeleteSavedConnection(ConnectionConfig config)
    {
        SavedConnections.Remove(config);
        _stateService.DeleteConnectionState(config.Id);
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

    public List<QueryHistoryEntry> GetHistoryForActiveConnection(string? filter = null)
    {
        if (SelectedConnectionTab == null) return [];
        return _historyService.LoadForConnection(SelectedConnectionTab.Config.Id, filter);
    }

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
                ConnectionId = SelectedConnectionTab.Config.Id,
                ExecutedAt = DateTime.Now
            });
            OnPropertyChanged(nameof(HistoryChanged));
        }

        SaveState();
    }

    private void SaveConnectionState(ConnectionTabViewModel ct)
    {
        _stateService.SaveConnectionState(new ConnectionTabState
        {
            Id = ct.Id,
            ConnectionId = ct.Config.Id,
            ActiveDatabase = ct.ActiveDatabase,
            ActiveQueryTabId = ct.SelectedQueryTab?.Id,
            ActiveQueryTabByDatabase = ct.GetActiveTabIdsByDatabase(),
            QueryTabs = ct.CollectAllTabStates().ToList()
        });
    }

    private static void RestoreQueryTabsFromState(ConnectionTabViewModel connTab, ConnectionTabState state)
    {
        var grouped = state.QueryTabs.GroupBy(t => t.Database ?? "");
        foreach (var group in grouped)
        {
            var db = group.Key;
            state.ActiveQueryTabByDatabase.TryGetValue(db, out var activeId);
            connTab.RestoreTabsForDatabase(db, group, activeId);
        }

        // Show the saved active database's tabs immediately so the UI is
        // populated even if the connection is slow or fails.
        if (!string.IsNullOrEmpty(connTab.ActiveDatabase))
            connTab.ActivateDatabaseTabs(connTab.ActiveDatabase);
        else if (state.QueryTabs.Count == 0)
            connTab.NewQueryTab();
    }

    public void SaveState()
    {
        // Save each open connection's session state to its own file
        foreach (var ct in ConnectionTabs)
        {
            SaveConnectionState(ct);
            foreach (var qt in ct.AllTabs())
                qt.IsDirty = false;
        }

        // Save master state (connections list + which tabs are open)
        var state = new AppState
        {
            Theme = ThemeName,
            IsConnectionPanelOpen = IsConnectionPanelOpen,
            IsHistoryPanelOpen = IsHistoryPanelOpen,
            SavedConnections = SavedConnections.ToList(),
            ActiveConnectionTabId = SelectedConnectionTab?.Config.Id,
            OpenConnectionIds = ConnectionTabs.Select(ct => ct.Config.Id).ToList()
        };

        _stateService.SaveState(state);
    }

    private void LoadState()
    {
        var state = _stateService.LoadState();
        _themeName = state.Theme ?? "Dark";
        IsConnectionPanelOpen = state.IsConnectionPanelOpen;
        IsHistoryPanelOpen = state.IsHistoryPanelOpen;

        foreach (var conn in state.SavedConnections)
            SavedConnections.Add(conn);

        // Restore open connection tabs from their individual state files
        foreach (var connId in state.OpenConnectionIds)
        {
            var config = SavedConnections.FirstOrDefault(c => c.Id == connId);
            if (config == null) continue;

            var ctState = _stateService.LoadConnectionState(connId);

            var connTab = new ConnectionTabViewModel(config)
            {
                Id = ctState?.Id ?? Guid.NewGuid().ToString("N")
            };
            SetupConnectionTabListeners(connTab);

            if (ctState != null)
            {
                connTab.ActiveDatabase = ctState.ActiveDatabase;
                RestoreQueryTabsFromState(connTab, ctState);
            }
            else
            {
                connTab.NewQueryTab();
            }

            ConnectionTabs.Add(connTab);
            _ = SafeFireAndForget(connTab.ConnectAsync());
        }

        // Select the right connection tab
        if (state.ActiveConnectionTabId != null)
            SelectedConnectionTab = ConnectionTabs.FirstOrDefault(t => t.Config.Id == state.ActiveConnectionTabId);
        SelectedConnectionTab ??= ConnectionTabs.FirstOrDefault();
    }
}

public class ConnectionTreeNode : ViewModelBase
{
    private bool _isExpanded;

    public string Name { get; set; }
    public string Detail { get; set; }
    public string SchemaName { get; set; }
    public ConnectionTreeNodeType NodeType { get; set; }
    public ObservableCollection<ConnectionTreeNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public IBrush NameBrush => Services.ThemeColors.Get(
        NodeType == ConnectionTreeNodeType.Database ? "DatabaseNodeColor" : "TreeNodeColor",
        NodeType == ConnectionTreeNodeType.Database ? "#558cb1" : "#DBE6EC");

    public void RefreshThemeBrushes()
    {
        OnPropertyChanged(nameof(NameBrush));
        foreach (var child in Children) child.RefreshThemeBrushes();
    }

    public ConnectionTreeNode(string name, ConnectionTreeNodeType type, string detail = "", string schemaName = "")
    {
        Name = name;
        NodeType = type;
        Detail = detail;
        SchemaName = schemaName;
    }
}

public enum ConnectionTreeNodeType
{
    Database,
    Folder,
    Schema,
    Table,
    View,
    StoredProcedure,
    Column
}
