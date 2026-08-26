using Avalonia.Media;
using dbclient.Data.Models;
using dbclient.IntelliSense.Interfaces;

namespace dbclient.ViewModels;

public class SessionTabViewModel : ViewModelBase
{
    private string _title = "Query 1";
    private string _queryText = "";
    private string _queryTextToExecute = "";
    private int _cursorLine = 1;
    private int _cursorColumn = 1;
    private string _rowCountText = "";
    private string _message = "";
    private bool _hasMessage;
    private IBrush _messageColor = Brushes.White;
    private List<ResultSet>? _resultData;
    private int _selectedResultIndex;
    private bool _isExecuting;
    private string _messages = "";
    private string _statusText = "";
    private string _executionTimeText = "";
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public CancellationTokenSource? ExecutionCts { get; set; }

    public IIntelliSenseProvider? IntelliSenseProvider { get; set; }

    public event EventHandler? ExecuteRequested;

    /// <summary>Raised by editor-level commands (toggle comment, etc.) that need the live TextEditor.</summary>
    public event EventHandler<string>? EditorActionRequested;

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    private string _database = "";
    /// <summary>
    /// The database this query tab belongs to. Each connection keeps a separate set of query tabs per
    /// database (ConnectionTabViewModel stashes the sets and swaps them when the active database changes).
    /// </summary>
    public string Database
    {
        get => _database;
        set
        {
            if (SetField(ref _database, value))
            {
                OnPropertyChanged(nameof(HasDatabase));
                OnPropertyChanged(nameof(DatabaseColor));
            }
        }
    }

    public bool HasDatabase => !string.IsNullOrEmpty(_database);

    /// <summary>Theme-aware accent derived from the database name (same color as the database node in the tree).</summary>
    public IBrush DatabaseColor => Services.NameColors.ForName(_database);

    /// <summary>Re-evaluate name-derived brushes after a theme swap.</summary>
    public void RefreshThemeBrushes() => OnPropertyChanged(nameof(DatabaseColor));

    public string QueryText
    {
        get => _queryText;
        set => SetField(ref _queryText, value);
    }

    public string QueryTextToExecute
    {
        get => _queryTextToExecute;
        set => SetField(ref _queryTextToExecute, value);
    }

    /// <summary>
    /// Tabs are persistent (autosaved into app state), so there is no notion of "unsaved". Closing a tab
    /// should still prompt whenever it contains text, because that text would be lost.
    /// </summary>
    public bool ShouldConfirmClose => !string.IsNullOrWhiteSpace(_queryText);

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

    public string RowCountText
    {
        get => _rowCountText;
        set => SetField(ref _rowCountText, value);
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public bool HasMessage
    {
        get => _hasMessage;
        set => SetField(ref _hasMessage, value);
    }

    public IBrush MessageColor
    {
        get => _messageColor;
        set => SetField(ref _messageColor, value);
    }

    /// <summary>Informational messages from the server (PRINT, warnings), newline-joined. Empty when none.</summary>
    public string Messages
    {
        get => _messages;
        set
        {
            if (SetField(ref _messages, value))
                OnPropertyChanged(nameof(HasMessages));
        }
    }

    public bool HasMessages => !string.IsNullOrEmpty(_messages);

    /// <summary>Per-tab status shown in the status bar while this tab is selected (empty = show connection status).</summary>
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

    public List<ResultSet>? ResultData
    {
        get => _resultData;
        set
        {
            if (SetField(ref _resultData, value))
            {
                OnPropertyChanged(nameof(ResultSetCount));
                SelectedResultIndex = 0;
            }
        }
    }

    public int SelectedResultIndex
    {
        get => _selectedResultIndex;
        set => SetField(ref _selectedResultIndex, value);
    }

    public int ResultSetCount => _resultData?.Count ?? 0;

    public bool IsExecuting
    {
        get => _isExecuting;
        set => SetField(ref _isExecuting, value);
    }

    /// <summary>
    /// Set query text without raising QueryText change notifications to the editor (used for restoring state).
    /// </summary>
    public void SetInitialQueryText(string text)
    {
        _queryText = text;
        OnPropertyChanged(nameof(QueryText));
    }

    public event EventHandler<string>? QueryTextSet;

    public void SetQueryText(string text)
    {
        _queryText = text;
        OnPropertyChanged(nameof(QueryText));
        QueryTextSet?.Invoke(this, text);
    }

    public void RequestExecute()
    {
        ExecuteRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RequestEditorAction(string action)
    {
        EditorActionRequested?.Invoke(this, action);
    }
}
