using Avalonia.Media;
using dbclient.Data.Models;
using dbclient.IntelliSense.Interfaces;

namespace dbclient.ViewModels;

public class SessionTabViewModel : ViewModelBase
{
    private string _title = "Query 1";
    private string _queryText = "";
    private string _queryTextToExecute = "";
    private bool _isDirty;
    private int _cursorLine = 1;
    private int _cursorColumn = 1;
    private string _rowCountText = "";
    private string _message = "";
    private bool _hasMessage;
    private IBrush _messageColor = Brushes.White;
    private List<ResultSet>? _resultData;
    private int _selectedResultIndex;
    private bool _isExecuting;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public CancellationTokenSource? ExecutionCts { get; set; }

    public IIntelliSenseProvider? IntelliSenseProvider { get; set; }

    public event EventHandler? ExecuteRequested;

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string QueryText
    {
        get => _queryText;
        set
        {
            if (SetField(ref _queryText, value))
                IsDirty = true;
        }
    }

    public string QueryTextToExecute
    {
        get => _queryTextToExecute;
        set => SetField(ref _queryTextToExecute, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        set => SetField(ref _isDirty, value);
    }

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
    /// Set query text without marking the tab as dirty (used for restoring state).
    /// </summary>
    public void SetInitialQueryText(string text)
    {
        _queryText = text;
        OnPropertyChanged(nameof(QueryText));
        IsDirty = false;
    }

    public event EventHandler<string>? QueryTextSet;

    public void SetQueryText(string text)
    {
        _queryText = text;
        OnPropertyChanged(nameof(QueryText));
        IsDirty = true;
        QueryTextSet?.Invoke(this, text);
    }

    public void RequestExecute()
    {
        ExecuteRequested?.Invoke(this, EventArgs.Empty);
    }
}
