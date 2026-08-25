using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
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
    private int _maxRows = 100_000;
    private double _editorFontSize = 14;
    private bool _editorWordWrap;
    private readonly StateService _stateService = new();
    private readonly QueryHistoryService _historyService = new();
    private System.ComponentModel.PropertyChangedEventHandler? _selectedTabHandler;
    private readonly DispatcherTimer _autosaveTimer;
    private bool _isShuttingDown;

    public const double MinFontSize = 8;
    public const double MaxFontSize = 40;
    public const double DefaultFontSize = 14;

    public ObservableCollection<ConnectionTabViewModel> ConnectionTabs { get; } = new();
    public ObservableCollection<ConnectionConfig> SavedConnections { get; } = new();

    // ---- Handlers the window installs (ViewModels never reference Window directly) ----

    /// <summary>Show a confirm dialog: (title, message) => true when the user accepted.</summary>
    public Func<string, string, Task<bool>>? ConfirmHandler { get; set; }
    /// <summary>Show an open-file picker for *.sql; returns the chosen path or null.</summary>
    public Func<Task<string?>>? PickOpenFileHandler { get; set; }
    /// <summary>Show a save-file picker for *.sql (suggested name) ; returns the chosen path or null.</summary>
    public Func<string, Task<string?>>? PickSaveFileHandler { get; set; }

    public RelayCommand NewQueryTabCommand { get; }
    public RelayCommand CloseQueryTabCommand { get; }
    public RelayCommand ExecuteQueryCommand { get; }
    public RelayCommand CancelQueryCommand { get; }
    public RelayCommand FormatQueryCommand { get; }
    public RelayCommand ExplainQueryCommand { get; }
    /// <summary>Save workspace/app state now (state also autosaves).</summary>
    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand SaveFileCommand { get; }
    public RelayCommand SaveFileAsCommand { get; }
    public RelayCommand ToggleConnectionPanelCommand { get; }
    public RelayCommand ToggleHistoryPanelCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand ToggleWordWrapCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomResetCommand { get; }
    public RelayCommand ToggleCommentCommand { get; }
    public RelayCommand NextQueryTabCommand { get; }
    public RelayCommand PrevQueryTabCommand { get; }
    public RelayCommand SetRowLimitCommand { get; }
    public RelayCommand CloseConnectionTabCommand { get; }

    public MainWindowViewModel()
    {
        NewQueryTabCommand = new RelayCommand(() => SelectedConnectionTab?.NewQueryTab());
        CloseQueryTabCommand = new RelayCommand(p => _ = SafeFireAndForget(SelectedConnectionTab?.CloseQueryTabAsync(p as SessionTabViewModel)));
        CloseConnectionTabCommand = new RelayCommand(p => _ = SafeFireAndForget(CloseConnectionTabAsync(p as ConnectionTabViewModel)));
        SaveCommand = new RelayCommand(SaveState);
        OpenFileCommand = new RelayCommand(() => _ = SafeFireAndForget(OpenFileAsync()));
        SaveFileCommand = new RelayCommand(() => _ = SafeFireAndForget(SaveFileAsync(saveAs: false)));
        SaveFileAsCommand = new RelayCommand(() => _ = SafeFireAndForget(SaveFileAsync(saveAs: true)));
        ExecuteQueryCommand = new RelayCommand(() => _ = SafeFireAndForget(ExecuteAsync()));
        CancelQueryCommand = new RelayCommand(() => SelectedConnectionTab?.SelectedQueryTab?.ExecutionCts?.Cancel());
        FormatQueryCommand = new RelayCommand(FormatCurrentQuery);
        ExplainQueryCommand = new RelayCommand(() => _ = SafeFireAndForget(SelectedConnectionTab?.ExplainQueryAsync()));
        ToggleConnectionPanelCommand = new RelayCommand(() => IsConnectionPanelOpen = !IsConnectionPanelOpen);
        ToggleHistoryPanelCommand = new RelayCommand(() => IsHistoryPanelOpen = !IsHistoryPanelOpen);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleWordWrapCommand = new RelayCommand(() => EditorWordWrap = !EditorWordWrap);
        ZoomInCommand = new RelayCommand(() => EditorFontSize += 1);
        ZoomOutCommand = new RelayCommand(() => EditorFontSize -= 1);
        ZoomResetCommand = new RelayCommand(() => EditorFontSize = DefaultFontSize);
        ToggleCommentCommand = new RelayCommand(() => SelectedConnectionTab?.SelectedQueryTab?.RequestEditorAction("ToggleComment"));
        NextQueryTabCommand = new RelayCommand(() => SelectedConnectionTab?.SelectNextQueryTab(+1));
        PrevQueryTabCommand = new RelayCommand(() => SelectedConnectionTab?.SelectNextQueryTab(-1));
        SetRowLimitCommand = new RelayCommand(p =>
        {
            if (p is int i) MaxRows = i;
            else if (p is string str && int.TryParse(str, out var parsed)) MaxRows = parsed;
        });

        // One app-wide autosave timer: restarted on every query-text change, fires once 3s after the last edit.
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autosaveTimer.Tick += (_, _) =>
        {
            _autosaveTimer.Stop();
            SaveState();
        };

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

    // ---- Editor settings (persisted) ----

    public double EditorFontSize
    {
        get => _editorFontSize;
        set
        {
            var clamped = Math.Clamp(value, MinFontSize, MaxFontSize);
            if (SetField(ref _editorFontSize, clamped))
                ScheduleAutosave();
        }
    }

    public bool EditorWordWrap
    {
        get => _editorWordWrap;
        set
        {
            if (SetField(ref _editorWordWrap, value))
                ScheduleAutosave();
        }
    }

    // ---- Row limit ----

    /// <summary>Row cap applied to every connection (0 = unlimited).</summary>
    public int MaxRows
    {
        get => _maxRows;
        set
        {
            if (value < 0) value = 0;
            if (!SetField(ref _maxRows, value)) return;
            foreach (var ct in ConnectionTabs)
                ct.MaxRows = value;
            OnPropertyChanged(nameof(IsRowLimit1K));
            OnPropertyChanged(nameof(IsRowLimit10K));
            OnPropertyChanged(nameof(IsRowLimit100K));
            OnPropertyChanged(nameof(IsRowLimitUnlimited));
            OnPropertyChanged(nameof(RowLimitDisplay));
            ScheduleAutosave();
        }
    }

    public bool IsRowLimit1K => _maxRows == 1_000;
    public bool IsRowLimit10K => _maxRows == 10_000;
    public bool IsRowLimit100K => _maxRows == 100_000;
    public bool IsRowLimitUnlimited => _maxRows == 0;
    public string RowLimitDisplay => _maxRows == 0 ? "Row limit: Unlimited" : $"Row limit: {_maxRows:N0}";

    // ---- Window geometry (set by MainWindow, persisted in SaveState) ----

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }
    public double? LeftPanelWidth { get; set; }
    public double? EditorHeightRatio { get; set; }

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

    /// <summary>Synchronous wrapper kept for existing callers (ConnectionPanel). Prompts, cancels, disposes.</summary>
    public void CloseConnectionTab(ConnectionTabViewModel? tab) => _ = SafeFireAndForget(CloseConnectionTabAsync(tab));

    public async Task CloseConnectionTabAsync(ConnectionTabViewModel? tab)
    {
        tab ??= SelectedConnectionTab;
        if (tab == null) return;

        // Prompt once if any open tab has something worth keeping.
        var dirty = tab.AllTabs().Where(t => t.ShouldConfirmClose).ToList();
        if (dirty.Count > 0 && ConfirmHandler != null)
        {
            var msg = dirty.Count == 1
                ? $"Close '{tab.DisplayName}'? Unsaved query in '{dirty[0].Title}' will be lost."
                : $"Close '{tab.DisplayName}'? Unsaved queries in {dirty.Count} tabs will be lost.";
            if (!await ConfirmHandler("Close Connection", msg)) return;
        }

        // Save this connection's session state before closing
        SaveConnectionState(tab);

        var index = ConnectionTabs.IndexOf(tab);
        ConnectionTabs.Remove(tab);

        if (ConnectionTabs.Count > 0)
            SelectedConnectionTab = ConnectionTabs[Math.Min(Math.Max(index, 0), ConnectionTabs.Count - 1)];
        else
            SelectedConnectionTab = null;

        SaveState();
        await TeardownConnectionAsync(tab);
    }

    private static async Task TeardownConnectionAsync(ConnectionTabViewModel tab)
    {
        tab.CancelAllQueries();
        try
        {
            if (tab.Connection != null)
                await tab.Connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to dispose connection {tab.DisplayName}", ex);
        }
    }

    /// <summary>
    /// Cancel every running query and dispose every connection, in parallel, each guarded.
    /// Called by App on shutdown after the final SaveState.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        _autosaveTimer.Stop();

        var tabs = ConnectionTabs.ToList();
        try
        {
            await Task.WhenAll(tabs.Select(TeardownConnectionAsync));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Shutdown teardown failed", ex);
        }
    }

    public void DeleteSavedConnection(ConnectionConfig config)
    {
        SavedConnections.Remove(config);
        _stateService.DeleteConnectionState(config.Id);
        SaveState();
    }

    private void SetupConnectionTabListeners(ConnectionTabViewModel connTab)
    {
        connTab.ConfirmHandler = (title, msg) => ConfirmHandler?.Invoke(title, msg) ?? Task.FromResult(true);
        connTab.MaxRows = MaxRows;
        connTab.TabTextChanged += (_, _) => ScheduleAutosave();

        // Forward cursor position from the active query tab. Keep a single handler on the
        // currently-selected query tab and unsubscribe it when the selection moves on.
        SessionTabViewModel? watched = null;
        System.ComponentModel.PropertyChangedEventHandler cursorHandler = (_, qe) =>
        {
            if (connTab != SelectedConnectionTab || watched == null || watched != connTab.SelectedQueryTab) return;
            if (qe.PropertyName == nameof(SessionTabViewModel.CursorLine))
                CursorLine = watched.CursorLine;
            else if (qe.PropertyName == nameof(SessionTabViewModel.CursorColumn))
                CursorColumn = watched.CursorColumn;
        };

        void Watch(SessionTabViewModel? qt)
        {
            if (watched != null) watched.PropertyChanged -= cursorHandler;
            watched = qt;
            if (qt != null) qt.PropertyChanged += cursorHandler;
        }

        Watch(connTab.SelectedQueryTab);

        connTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ConnectionTabViewModel.SelectedQueryTab)) return;
            Watch(connTab.SelectedQueryTab);
            if (connTab != SelectedConnectionTab) return;
            var qt = connTab.SelectedQueryTab;
            if (qt != null)
            {
                CursorLine = qt.CursorLine;
                CursorColumn = qt.CursorColumn;
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
            AppLogger.Error("Background task failed", ex);
        }
    }

    private void FormatCurrentQuery()
    {
        var tab = SelectedConnectionTab?.SelectedQueryTab;
        if (tab == null || string.IsNullOrWhiteSpace(tab.QueryText)) return;
        tab.SetQueryText(SqlFormatter.Format(tab.QueryText));
    }

    // ---- .sql file open / save ----

    public async Task OpenFileAsync()
    {
        if (PickOpenFileHandler == null) return;
        var path = await PickOpenFileHandler();
        if (string.IsNullOrEmpty(path)) return;
        await OpenFileAsync(path);
    }

    public async Task OpenFileAsync(string path)
    {
        var connTab = SelectedConnectionTab;
        if (connTab == null)
        {
            AppLogger.Warn("Open SQL ignored: no connection tab selected");
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to open {path}", ex);
            if (connTab.SelectedQueryTab != null)
            {
                connTab.SelectedQueryTab.HasMessage = true;
                connTab.SelectedQueryTab.Message = $"Could not open file: {ex.Message}";
                connTab.SelectedQueryTab.MessageColor = ThemeColors.Error;
            }
            return;
        }

        // Already open? Just switch to it.
        var open = connTab.QueryTabs.FirstOrDefault(t =>
            t.FilePath != null && string.Equals(Path.GetFullPath(t.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (open != null)
        {
            connTab.SelectedQueryTab = open;
            return;
        }

        // Reuse the current tab if it's an empty scratch tab, otherwise open a new one.
        var target = connTab.SelectedQueryTab;
        if (target == null || target.IsFileBacked || !string.IsNullOrWhiteSpace(target.QueryText))
            target = connTab.NewQueryTab();

        target.SetQueryText(text);          // syncs the editor
        target.MarkSavedToFile(path, text); // records disk text so IsDirty is false
        SaveState();
    }

    public async Task SaveFileAsync(bool saveAs)
    {
        var tab = SelectedConnectionTab?.SelectedQueryTab;
        if (tab == null) return;

        var path = tab.FilePath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            if (PickSaveFileHandler == null) return;
            var suggested = tab.IsFileBacked ? Path.GetFileName(tab.FilePath!) : $"{tab.Title}.sql";
            path = await PickSaveFileHandler(suggested);
            if (string.IsNullOrEmpty(path)) return;
        }

        try
        {
            var text = tab.QueryText;
            await File.WriteAllTextAsync(path, text);
            tab.MarkSavedToFile(path, text);
            tab.StatusText = $"Saved {Path.GetFileName(path)}";
            SaveState();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to save {path}", ex);
            tab.HasMessage = true;
            tab.Message = $"Could not save file: {ex.Message}";
            tab.MessageColor = ThemeColors.Error;
        }
    }

    public List<QueryHistoryEntry> GetHistory() => _historyService.Load();

    public List<QueryHistoryEntry> GetHistoryForActiveConnection(string? filter = null)
    {
        if (SelectedConnectionTab == null) return [];
        return _historyService.LoadForConnection(SelectedConnectionTab.Config.Id, filter);
    }

    private async Task ExecuteAsync()
    {
        // Capture the connection tab: the user may switch connections while the query runs.
        var connTab = SelectedConnectionTab;
        if (connTab == null) return;

        var queryText = connTab.SelectedQueryTab?.QueryText;
        await connTab.ExecuteQueryAsync();

        if (!string.IsNullOrWhiteSpace(queryText))
        {
            _historyService.Add(new QueryHistoryEntry
            {
                Query = queryText!.Length > 1000 ? queryText[..1000] : queryText!,
                Database = connTab.ActiveDatabase,
                Connection = connTab.DisplayName,
                ConnectionId = connTab.Config.Id,
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

    /// <summary>Restart the 3s autosave countdown (called on every query-text / setting change).</summary>
    public void ScheduleAutosave()
    {
        if (_isShuttingDown) return;
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    public void SaveState()
    {
        _autosaveTimer.Stop();

        // Save each open connection's session state to its own file
        foreach (var ct in ConnectionTabs)
            SaveConnectionState(ct);

        // Save master state (connections list + which tabs are open)
        var state = new AppState
        {
            Theme = ThemeName,
            IsConnectionPanelOpen = IsConnectionPanelOpen,
            IsHistoryPanelOpen = IsHistoryPanelOpen,
            SavedConnections = SavedConnections.ToList(),
            ActiveConnectionTabId = SelectedConnectionTab?.Config.Id,
            OpenConnectionIds = ConnectionTabs.Select(ct => ct.Config.Id).ToList(),
            MaxRows = MaxRows,
            EditorFontSize = EditorFontSize,
            EditorWordWrap = EditorWordWrap,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            WindowX = WindowX,
            WindowY = WindowY,
            WindowMaximized = WindowMaximized,
            LeftPanelWidth = LeftPanelWidth,
            EditorHeightRatio = EditorHeightRatio
        };

        _stateService.SaveState(state);
    }

    private void LoadState()
    {
        var state = _stateService.LoadState();
        _themeName = state.Theme ?? "Dark";
        IsConnectionPanelOpen = state.IsConnectionPanelOpen;
        IsHistoryPanelOpen = state.IsHistoryPanelOpen;
        _maxRows = state.MaxRows < 0 ? 0 : state.MaxRows;
        _editorFontSize = Math.Clamp(state.EditorFontSize <= 0 ? DefaultFontSize : state.EditorFontSize, MinFontSize, MaxFontSize);
        _editorWordWrap = state.EditorWordWrap;
        WindowWidth = state.WindowWidth;
        WindowHeight = state.WindowHeight;
        WindowX = state.WindowX;
        WindowY = state.WindowY;
        WindowMaximized = state.WindowMaximized;
        LeftPanelWidth = state.LeftPanelWidth;
        EditorHeightRatio = state.EditorHeightRatio;

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
    /// <summary>Column nodes only: set from DbColumn.IsPrimaryKey (structured; don't parse Detail).</summary>
    public bool IsPrimaryKey { get; set; }
    /// <summary>Column nodes only: set from DbColumn.IsNullable.</summary>
    public bool IsNullable { get; set; }
    /// <summary>Column nodes only: raw data type without PK/NULL tags.</summary>
    public string DataType { get; set; } = "";
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
