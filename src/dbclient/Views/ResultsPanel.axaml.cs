using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using dbclient.Data;
using dbclient.Data.Models;
using dbclient.Models;
using dbclient.Services;
using dbclient.ViewModels;

namespace dbclient.Views;

/// <summary>
/// Writable row wrapper so DataGrid two-way binding works on the indexer.
/// </summary>
public class ResultRow
{
    private readonly string?[] _values;
    public ResultRow(string?[] values) => _values = values;
    public int RowNumber { get; set; }
    public string? this[int i]
    {
        // Bounds-safe: while switching result sets the grid can briefly evaluate a column binding
        // (e.g. [2]) against a row from a different result set with fewer values.
        get => (uint)i < (uint)_values.Length ? _values[i] : null;
        set { if ((uint)i < (uint)_values.Length) _values[i] = value; }
    }
    public int Length => _values.Length;
    public string?[] ToArray() => (string?[])_values.Clone();
    public string?[] Values => _values;
}

public partial class ResultsPanel : UserControl
{
    private SessionTabViewModel? _currentVm;
    private List<ResultRow>? _originalRows;
    private List<ResultRow>? _currentRows;
    private List<ResultRow>? _allRows; // Unfiltered rows for search
    private string[]? _columnNames;
    private string?[] _columnTypes = [];
    private readonly HashSet<int> _dirtyRows = new();
    private (int Row, int Col, string? Value)? _editSnapshot;

    // NULL cells: display the literal "NULL" in italic muted text so they are distinct from empty strings.
    private static readonly IValueConverter NullDisplayConverter =
        new FuncValueConverter<string?, string>(v => v ?? UpdateSqlGenerator.NullLiteral);
    private static readonly IValueConverter NullFontStyleConverter =
        new FuncValueConverter<string?, FontStyle>(v => v == null ? FontStyle.Italic : FontStyle.Normal);

    /// <summary>Editing: a null shows as "NULL"; typing the literal NULL (any case) writes back a real null.</summary>
    private sealed class NullEditConverter : IValueConverter
    {
        public static readonly NullEditConverter Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value as string ?? UpdateSqlGenerator.NullLiteral;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is string str && str.Equals(UpdateSqlGenerator.NullLiteral, StringComparison.OrdinalIgnoreCase) ? null : value;
    }
    private EventHandler? _pinColumnWidthsHandler;

    public ResultsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid != null)
        {
            grid.Sorting += ResultsGrid_Sorting;
            grid.BeginningEdit += ResultsGrid_BeginningEdit;
            grid.CellEditEnded += ResultsGrid_CellEditEnded;
            grid.AddHandler(KeyDownEvent, ResultsGrid_KeyDown, RoutingStrategies.Tunnel);
        }

        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox != null)
            searchBox.TextChanged += (_, _) => FilterRows(searchBox.Text);

        // Ctrl+Shift+R toggles the results filter bar (Ctrl+F is left for the editor's search panel).
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.R && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                ToggleSearchBar();
                e.Handled = true;
            }
        };

        ThemeColors.ThemeChanged += OnThemeChanged;
        DetachedFromVisualTree += (_, _) => ThemeColors.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_currentVm != null) UpdateResultSets(_currentVm.ResultData);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVm != null)
            _currentVm.PropertyChanged -= OnVmPropertyChanged;

        _currentVm = DataContext as SessionTabViewModel;

        if (_currentVm != null)
        {
            _currentVm.PropertyChanged += OnVmPropertyChanged;
            UpdateResultSets(_currentVm.ResultData);
        }
        else
        {
            ClearGrid();
            UpdateResultSetTabs(0, 0);
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionTabViewModel.ResultData))
            UpdateResultSets(_currentVm?.ResultData);
        else if (e.PropertyName == nameof(SessionTabViewModel.SelectedResultIndex))
            UpdateGridForIndex(_currentVm?.ResultData, _currentVm?.SelectedResultIndex ?? 0);
    }

    private void UpdateResultSets(List<ResultSet>? data)
    {
        if (data == null || data.Count == 0)
        {
            ClearGrid();
            UpdateResultSetTabs(0, 0);
            return;
        }

        UpdateResultSetTabs(data.Count, _currentVm?.SelectedResultIndex ?? 0);
        UpdateGrid(data.ElementAtOrDefault(_currentVm?.SelectedResultIndex ?? 0));
    }

    private void UpdateGridForIndex(List<ResultSet>? data, int index)
    {
        if (data == null || index < 0 || index >= data.Count)
        {
            ClearGrid();
            return;
        }

        UpdateResultSetTabs(data.Count, index);
        UpdateGrid(data[index]);
    }

    private void UpdateResultSetTabs(int count, int selectedIndex)
    {
        var strip = this.FindControl<Border>("ResultSetTabStrip");
        var tabs = this.FindControl<StackPanel>("ResultSetTabs");
        if (strip == null || tabs == null) return;

        strip.IsVisible = count > 1;
        tabs.Children.Clear();

        for (int i = 0; i < count; i++)
        {
            var idx = i;
            var btn = new Button
            {
                Content = $"Result {i + 1}",
                FontSize = 11,
                Padding = new Thickness(8, 2),
                Background = i == selectedIndex
                    ? ThemeColors.Get("TabItemHover", "#3a3a3a")
                    : Brushes.Transparent,
                Foreground = i == selectedIndex
                    ? ThemeColors.NormalText
                    : ThemeColors.MutedText,
                BorderThickness = new Thickness(0),
            };
            btn.Click += (_, _) =>
            {
                if (_currentVm != null)
                    _currentVm.SelectedResultIndex = idx;
            };
            tabs.Children.Add(btn);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _currentVm?.ExecutionCts?.Cancel();
    }

    private void ClearGrid()
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid == null) return;
        grid.ItemsSource = null;
        grid.Columns.Clear();
        _allRows = null;
        SetTruncatedBanner(null);
        ClearChangeTracking();
    }

    private void SetTruncatedBanner(ResultSet? data)
    {
        var banner = this.FindControl<Border>("TruncatedBanner");
        var text = this.FindControl<TextBlock>("TruncatedText");
        if (banner == null) return;
        var truncated = data?.Truncated == true;
        banner.IsVisible = truncated;
        if (text != null && truncated)
            text.Text = $"Result truncated at {data!.Rows.Count:N0} rows \u2014 raise the limit under Query \u2192 Row limit";
    }

    private void ClearChangeTracking()
    {
        _originalRows = null;
        _currentRows = null;
        _columnNames = null;
        _columnTypes = [];
        _dirtyRows.Clear();
        _editSnapshot = null;
        UpdateApplyButtonVisibility();
    }

    /// <summary>The query that produced the current results (prefers the actually-executed text).</summary>
    private string ResolveQueryText() =>
        !string.IsNullOrWhiteSpace(_currentVm?.QueryTextToExecute)
            ? _currentVm!.QueryTextToExecute
            : _currentVm?.QueryText ?? "";

    private void UpdateGrid(ResultSet? data)
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid == null) return;

        if (data == null)
        {
            ClearGrid();
            return;
        }

        grid.AutoGenerateColumns = false;
        // Detach old rows before rebuilding columns, otherwise a new column binding (e.g. [2]) can be
        // evaluated against a still-attached row from the previous result set that has fewer values.
        grid.ItemsSource = null;
        grid.Columns.Clear();

        _columnNames = data.ColumnNames;
        _columnTypes = data.ColumnTypes ?? [];
        SetTruncatedBanner(data);

        // Row number column (frozen, read-only, muted)
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "#",
            IsReadOnly = true,
            Width = DataGridLength.Auto,
            CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ResultRow>((_, _) =>
            {
                var tb = new TextBlock();
                tb.Bind(TextBlock.TextProperty, new Binding("RowNumber"));
                tb.Foreground = ThemeColors.MutedText;
                tb.Opacity = 0.6;
                tb.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                return tb;
            })
        });
        grid.FrozenColumnCount = 1;

        // Rows are only editable when we can resolve a target table from the query — otherwise an
        // UPDATE can't be generated, so don't let the user edit cells at all.
        var editable = !string.IsNullOrEmpty(UpdateSqlGenerator.ParseTableName(ResolveQueryText()));

        for (int i = 0; i < _columnNames.Length; i++)
        {
            var typeBrush = ResolveTypeBrush(_columnTypes.ElementAtOrDefault(i));
            grid.Columns.Add(BuildDataColumn(i, _columnNames[i], typeBrush, editable));
        }

        // Zero-row results still render their headers (empty grid body).
        var rows = data.Rows.Select((r, idx) => new ResultRow(r) { RowNumber = idx + 1 }).ToList();

        _originalRows = rows.Select(r => new ResultRow(r.ToArray())).ToList();
        _currentRows = rows;
        _allRows = rows;
        _dirtyRows.Clear();
        UpdateApplyButtonVisibility();

        grid.ItemsSource = rows;
        PinColumnWidthsAfterLayout(grid);
    }

    /// <summary>
    /// A data column bound to the row's integer indexer. A template column is used (instead of
    /// <see cref="DataGridTextColumn"/>) so NULL cells can be rendered as italic muted "NULL", distinct from
    /// empty strings. The editing template writes back on every keystroke so the row already holds the new
    /// value when <see cref="DataGrid.CellEditEnded"/> fires.
    /// </summary>
    private static DataGridTemplateColumn BuildDataColumn(int index, string header, IBrush? typeBrush, bool editable)
    {
        var path = $"[{index}]";
        var nullBrush = ThemeColors.MutedText;
        var foregroundConverter = new FuncValueConverter<string?, object?>(v =>
            v == null ? nullBrush : (object?)typeBrush ?? AvaloniaProperty.UnsetValue);

        return new DataGridTemplateColumn
        {
            Header = header,
            IsReadOnly = !editable,
            CanUserSort = true,
            CanUserResize = true,
            CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ResultRow>((_, _) =>
            {
                var tb = new TextBlock
                {
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(12, 0),
                };
                tb.Bind(TextBlock.TextProperty, new Binding(path) { Converter = NullDisplayConverter });
                tb.Bind(TextBlock.FontStyleProperty, new Binding(path) { Converter = NullFontStyleConverter });
                tb.Bind(TextBlock.ForegroundProperty, new Binding(path) { Converter = foregroundConverter });
                return tb;
            }),
            CellEditingTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ResultRow>((_, _) =>
            {
                var box = new TextBox
                {
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 0),
                    MinHeight = 0,
                };
                box.Bind(TextBox.TextProperty, new Binding(path)
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    Converter = NullEditConverter.Instance
                });
                box.AttachedToVisualTree += (sender, _) =>
                {
                    if (sender is TextBox tbx) { tbx.Focus(); tbx.SelectAll(); }
                };
                return box;
            })
        };
    }

    /// <summary>
    /// Columns start as <see cref="DataGridLength.Auto"/> so they size to content, but Auto
    /// columns re-measure against whichever rows are currently realized — which makes them
    /// jump/reflow during horizontal (and vertical) scrolling. Once the grid has measured the
    /// initial view, pin each column to that width (absolute pixels) so it stays put. Row
    /// virtualization is unaffected.
    /// </summary>
    private void PinColumnWidthsAfterLayout(DataGrid grid)
    {
        // Drop any pending handler from a previous (rapid) result load.
        if (_pinColumnWidthsHandler != null)
            grid.LayoutUpdated -= _pinColumnWidthsHandler;

        _pinColumnWidthsHandler = (_, _) =>
        {
            // Wait until the freshly-added columns have actually been measured.
            if (grid.Columns.Count == 0 || grid.Columns[0].ActualWidth <= 0)
                return;

            grid.LayoutUpdated -= _pinColumnWidthsHandler;
            _pinColumnWidthsHandler = null;

            foreach (var col in grid.Columns)
            {
                var w = col.ActualWidth;
                if (w > 0)
                    col.Width = new DataGridLength(w);
            }
        };
        grid.LayoutUpdated += _pinColumnWidthsHandler;
    }

    // --- Change tracking ---

    private void ResultsGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        _editSnapshot = null;
        if (sender is not DataGrid grid || e.Row.DataContext is not ResultRow row) return;
        var col = grid.Columns.IndexOf(e.Column) - 1;
        if (col < 0) return;
        _editSnapshot = (e.Row.Index, col, row[col]);
    }

    private void ResultsGrid_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        var snapshot = _editSnapshot;
        _editSnapshot = null;

        if (e.EditAction == DataGridEditAction.Cancel)
        {
            // The editing template pushes every keystroke into the row, so undo it on Escape.
            if (snapshot is { } snap && e.Row.DataContext is ResultRow cancelled)
                cancelled[snap.Col] = snap.Value;
            return;
        }
        if (_originalRows == null || _currentRows == null) return;

        var rowIndex = e.Row.Index;
        if (rowIndex < 0 || rowIndex >= _originalRows.Count) return;

        var original = _originalRows[rowIndex];
        var current = _currentRows[rowIndex];

        bool isDirty = false;
        for (int i = 0; i < original.Length; i++)
        {
            if (original[i] != current[i])
            {
                isDirty = true;
                break;
            }
        }

        if (isDirty)
            _dirtyRows.Add(rowIndex);
        else
            _dirtyRows.Remove(rowIndex);

        UpdateApplyButtonVisibility();
    }

    private void UpdateApplyButtonVisibility()
    {
        var applyBtn = this.FindControl<Button>("ApplyBtn");
        var discardBtn = this.FindControl<Button>("DiscardBtn");
        var hasChanges = _dirtyRows.Count > 0;
        if (applyBtn != null) applyBtn.IsVisible = hasChanges;
        if (discardBtn != null) discardBtn.IsVisible = hasChanges;
    }

    private void ShowMessage(string text, IBrush color)
    {
        if (_currentVm == null) return;
        _currentVm.HasMessage = true;
        _currentVm.Message = text;
        _currentVm.MessageColor = color;
    }

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ApplyChangesAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Apply changes failed", ex);
            ShowMessage(ex.Message, ThemeColors.Error);
        }
    }

    private async Task ApplyChangesAsync()
    {
        if (_dirtyRows.Count == 0 || _originalRows == null || _currentRows == null || _columnNames == null)
            return;

        var connTab = GetConnectionTab();
        if (connTab == null) return;

        var queryText = ResolveQueryText();
        var tableRef = UpdateSqlGenerator.ParseTableRef(queryText);
        var tableName = tableRef?.Table;
        if (string.IsNullOrEmpty(tableName))
        {
            ShowMessage("Could not determine table name from query. Apply requires a SELECT ... FROM <table> query.", ThemeColors.Warning);
            return;
        }

        var pkColumns = UpdateSqlGenerator.FindPrimaryKeyColumns(tableName, tableRef?.Schema, connTab);
        var script = UpdateSqlGenerator.Generate(tableName, tableRef?.Schema, connTab.Config.Type, pkColumns,
            _columnNames, _columnTypes, _originalRows, _currentRows, _dirtyRows, queryText);

        if (script.IsError)
        {
            ShowMessage(script.Error!, ThemeColors.Warning);
            return;
        }

        var window = this.FindAncestorOfType<Window>();
        if (window == null) return;

        var dialog = new ApplyChangesDialog(script.Sql, script.ExpectedStatements);
        await dialog.ShowDialog(window);

        if (!dialog.ShouldExecute || connTab.Connection == null) return;

        var finalSql = dialog.SqlText;
        var edited = dialog.WasEdited;

        // Run under a cancellable token so Esc / the stop button work for the apply script too.
        using var cts = new CancellationTokenSource();
        var vm = _currentVm;
        if (vm != null)
        {
            vm.ExecutionCts?.Cancel();
            vm.ExecutionCts = cts;
            vm.IsExecuting = true;
        }

        try
        {
            var result = await connTab.Connection.ExecuteQueryAsync(connTab.ActiveDatabase, finalSql, cts.Token);
            if (result.IsError)
            {
                ShowMessage(result.ErrorMessage!, ThemeColors.Error);
                return;
            }

            if (!edited && result.AffectedRows != script.ExpectedStatements)
            {
                var guidance = connTab.Config.Type == ConnectionType.SqlServer
                    ? "The transaction was rolled back (row-count check failed)."
                    : "The script committed — review the table and run a compensating UPDATE, or ROLLBACK if the session is still open.";
                ShowMessage($"Warning: expected {script.ExpectedStatements} row(s) to change but the server reported {result.AffectedRows}. {guidance}",
                    ThemeColors.Warning);
                if (connTab.Config.Type == ConnectionType.SqlServer)
                    return; // rolled back — keep the edits pending so the user can retry / discard
            }
            else
            {
                ShowMessage(edited
                    ? $"{result.AffectedRows} row(s) affected (script was edited)."
                    : $"{result.AffectedRows} row(s) updated.", ThemeColors.Success);
            }

            _originalRows = _currentRows!.Select(r => new ResultRow(r.ToArray())).ToList();
            _dirtyRows.Clear();
            UpdateApplyButtonVisibility();
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Apply cancelled.", ThemeColors.Warning);
        }
        finally
        {
            if (vm != null)
            {
                if (ReferenceEquals(vm.ExecutionCts, cts)) vm.ExecutionCts = null;
                vm.IsExecuting = false;
            }
        }
    }

    private void Discard_Click(object? sender, RoutedEventArgs e)
    {
        if (_originalRows == null || _currentRows == null) return;

        foreach (var rowIndex in _dirtyRows)
        {
            if (rowIndex < _originalRows.Count && rowIndex < _currentRows.Count)
            {
                var original = _originalRows[rowIndex];
                var current = _currentRows[rowIndex];
                for (int i = 0; i < original.Length; i++)
                    current[i] = original[i];
            }
        }

        _dirtyRows.Clear();
        UpdateApplyButtonVisibility();

        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid != null)
        {
            var source = grid.ItemsSource;
            grid.ItemsSource = null;
            grid.ItemsSource = source;
        }
    }

    private ConnectionTabViewModel? GetConnectionTab()
    {
        var window = this.FindAncestorOfType<Window>();
        return (window?.DataContext as MainWindowViewModel)?.SelectedConnectionTab;
    }

    // --- Copy/Export ---

    private async void ResultsGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            var grid = this.FindControl<DataGrid>("ResultsGrid");
            if (grid == null) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            await ResultsClipboard.HandleKeyDown(grid, e, clipboard);
        }
        catch (Exception ex) { AppLogger.Error("ResultsGrid_KeyDown failed", ex); }
    }

    private async void CopyCell_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var grid = this.FindControl<DataGrid>("ResultsGrid");
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (grid != null && clipboard != null)
                await ResultsClipboard.CopyCell(grid, clipboard);
        }
        catch (Exception ex) { AppLogger.Error("Copy cell failed", ex); }
    }

    private async void CopySelected_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var grid = this.FindControl<DataGrid>("ResultsGrid");
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (grid != null && clipboard != null)
                await ResultsClipboard.CopyWithHeaders(grid, clipboard);
        }
        catch (Exception ex) { AppLogger.Error("Copy row failed", ex); }
    }

    private async void CopyAll_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var grid = this.FindControl<DataGrid>("ResultsGrid");
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (grid != null && clipboard != null)
                await ResultsClipboard.CopyAll(grid, clipboard);
        }
        catch (Exception ex) { AppLogger.Error("Copy all failed", ex); }
    }

    private async void CopyAsInsert_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var grid = this.FindControl<DataGrid>("ResultsGrid");
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (grid == null || clipboard == null) return;

            var connType = GetConnectionTab()?.Config.Type ?? ConnectionType.SqlServer;
            var dialect = UpdateSqlGenerator.DialectFor(connType);
            var tableRef = UpdateSqlGenerator.ParseTableRef(ResolveQueryText());
            var quotedTable = tableRef == null
                ? "<table>"
                : string.IsNullOrEmpty(tableRef.Value.Schema)
                    ? SqlIdentifier.Quote(dialect, tableRef.Value.Table)
                    : $"{SqlIdentifier.Quote(dialect, tableRef.Value.Schema)}.{SqlIdentifier.Quote(dialect, tableRef.Value.Table)}";

            await ResultsClipboard.CopyAsInsert(grid, clipboard, dialect, quotedTable, _columnTypes);
        }
        catch (Exception ex) { AppLogger.Error("Copy as INSERT failed", ex); }
    }

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var grid = this.FindControl<DataGrid>("ResultsGrid");
            var topLevel = TopLevel.GetTopLevel(this);
            if (grid != null && topLevel != null)
                await ResultsClipboard.ExportCsv(grid, topLevel.StorageProvider);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Export CSV failed", ex);
            ShowMessage($"Export failed: {ex.Message}", ThemeColors.Error);
        }
    }

    private async void ExportJson_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var grid = this.FindControl<DataGrid>("ResultsGrid");
            var topLevel = TopLevel.GetTopLevel(this);
            if (grid != null && topLevel != null)
                await ResultsClipboard.ExportJson(grid, topLevel.StorageProvider, _columnTypes);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Export JSON failed", ex);
            ShowMessage($"Export failed: {ex.Message}", ThemeColors.Error);
        }
    }

    // --- Search/Filter ---

    /// <summary>Shows/hides the results filter bar (bound to Ctrl+Shift+R by the main window / panel).</summary>
    public void ToggleSearchBar()
    {
        var searchBar = this.FindControl<Border>("SearchBar");
        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBar == null) return;

        searchBar.IsVisible = !searchBar.IsVisible;
        if (searchBar.IsVisible)
        {
            searchBox?.Focus();
        }
        else
        {
            if (searchBox != null) searchBox.Text = "";
            FilterRows(null);
        }
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox != null) searchBox.Text = "";
        FilterRows(null);
    }

    private void FilterRows(string? filter)
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid == null || _allRows == null) return;

        if (string.IsNullOrWhiteSpace(filter))
        {
            grid.ItemsSource = _allRows;
            return;
        }

        // Typing "null" matches NULL cells (they have no text to match otherwise).
        var matchNulls = filter.Trim().Equals(UpdateSqlGenerator.NullLiteral, StringComparison.OrdinalIgnoreCase);
        var filtered = _allRows.Where(row =>
        {
            for (int i = 0; i < row.Length; i++)
            {
                var v = row[i];
                if (v == null ? matchNulls : v.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }).ToList();

        grid.ItemsSource = filtered;
    }

    // --- Sorting ---

    private int _lastSortColumn = -1;
    private bool _lastSortAscending = true;

    private void ResultsGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        if (sender is not DataGrid grid || grid.ItemsSource is not List<ResultRow> rows)
            return;

        var colIndex = grid.Columns.IndexOf(e.Column) - 1; // -1 for row number column
        if (colIndex < 0) return;

        bool ascending;
        if (colIndex == _lastSortColumn)
            ascending = !_lastSortAscending;
        else
            ascending = true;

        _lastSortColumn = colIndex;
        _lastSortAscending = ascending;

        var sorted = rows.OrderBy(r =>
        {
            var val = colIndex < r.Length ? r[colIndex] : null;
            if (val == null) return (object?)null;
            if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
                return num;
            return val;
        }, new SmartComparer(ascending)).ToList();

        grid.ItemsSource = sorted;
    }

    private static IBrush? ResolveTypeBrush(string? dbTypeName)
    {
        if (string.IsNullOrEmpty(dbTypeName)) return null;
        var t = dbTypeName.ToLowerInvariant();

        // Boolean (before numeric: "bit" is treated as numeric by the literal formatter)
        if (t == "bit" || t.Contains("bool"))
            return ThemeColors.Get("DataTypeBoolean", "#bd93f9");

        // Numeric
        if (UpdateSqlGenerator.IsNumericType(t))
            return ThemeColors.Get("DataTypeNumeric", "#e6b07a");

        // Date / time
        if (t.Contains("date") || t.Contains("time") || t.Contains("timestamp"))
            return ThemeColors.Get("DataTypeDate", "#8be9fd");

        // Binary
        if (t.Contains("binary") || t.Contains("blob") || t.Contains("image"))
            return ThemeColors.Get("DataTypeBinary", "#9aa0a6");

        // String / unknown — use default cell foreground
        return null;
    }

    private class SmartComparer(bool ascending) : IComparer<object?>
    {
        public int Compare(object? x, object? y)
        {
            var result = (x, y) switch
            {
                // NULLs sort first (ascending)
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                (double a, double b) => a.CompareTo(b),
                (string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(x?.ToString(), y?.ToString(), StringComparison.OrdinalIgnoreCase)
            };
            return ascending ? result : -result;
        }
    }
}
