using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
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
    private readonly HashSet<int> _dirtyRows = new();
    private EventHandler? _pinColumnWidthsHandler;

    public ResultsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid != null)
        {
            grid.Sorting += ResultsGrid_Sorting;
            grid.CellEditEnded += ResultsGrid_CellEditEnded;
            grid.AddHandler(KeyDownEvent, ResultsGrid_KeyDown, RoutingStrategies.Tunnel);
        }

        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox != null)
            searchBox.TextChanged += (_, _) => FilterRows(searchBox.Text);

        // Ctrl+F to toggle search bar
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
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
        ClearChangeTracking();
    }

    private void ClearChangeTracking()
    {
        _originalRows = null;
        _currentRows = null;
        _columnNames = null;
        _dirtyRows.Clear();
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

        if (data == null || data.Rows.Count == 0)
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

        // Row number column (frozen, read-only, muted)
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "#",
            IsReadOnly = true,
            Width = DataGridLength.Auto,
            CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ResultRow>((_, _) =>
            {
                var tb = new TextBlock();
                tb.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("RowNumber"));
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
            var col = new DataGridTextColumn
            {
                Header = _columnNames[i],
                Binding = new Avalonia.Data.Binding($"[{i}]"),
                IsReadOnly = !editable
            };

            var typeBrush = ResolveTypeBrush(data.ColumnTypes.ElementAtOrDefault(i));
            if (typeBrush != null)
                col.Foreground = typeBrush;

            grid.Columns.Add(col);
        }

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

    private void ResultsGrid_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel) return;
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

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (_dirtyRows.Count == 0 || _originalRows == null || _currentRows == null || _columnNames == null)
            return;

        var connTab = GetConnectionTab();
        if (connTab == null) return;

        var tableRef = UpdateSqlGenerator.ParseTableRef(ResolveQueryText());
        var tableName = tableRef?.Table;
        if (string.IsNullOrEmpty(tableName))
        {
            if (_currentVm != null)
            {
                _currentVm.HasMessage = true;
                _currentVm.Message = "Could not determine table name from query. Apply requires a SELECT ... FROM <table> query.";
                _currentVm.MessageColor = ThemeColors.Warning;
            }
            return;
        }

        var pkColumns = UpdateSqlGenerator.FindPrimaryKeyColumns(tableName, tableRef?.Schema, connTab);
        var sql = UpdateSqlGenerator.Generate(tableName, connTab.Config.Type, pkColumns,
            _columnNames, _originalRows, _currentRows, _dirtyRows);

        var window = this.FindAncestorOfType<Window>();
        if (window == null) return;

        var dialog = new ApplyChangesDialog(sql);
        await dialog.ShowDialog(window);

        if (dialog.ShouldExecute && connTab.Connection != null)
        {
            var finalSql = dialog.SqlText;
            try
            {
                var result = await connTab.Connection.ExecuteQueryAsync(connTab.ActiveDatabase, finalSql);
                if (result.IsError)
                {
                    if (_currentVm != null)
                    {
                        _currentVm.HasMessage = true;
                        _currentVm.Message = result.ErrorMessage!;
                        _currentVm.MessageColor = ThemeColors.Error;
                    }
                }
                else
                {
                    _originalRows = _currentRows!.Select(r => new ResultRow(r.ToArray())).ToList();
                    _dirtyRows.Clear();
                    UpdateApplyButtonVisibility();

                    if (_currentVm != null)
                    {
                        _currentVm.HasMessage = true;
                        _currentVm.Message = $"{result.AffectedRows} row(s) updated.";
                        _currentVm.MessageColor = ThemeColors.Success;
                    }
                }
            }
            catch (Exception ex)
            {
                if (_currentVm != null)
                {
                    _currentVm.HasMessage = true;
                    _currentVm.Message = ex.Message;
                    _currentVm.MessageColor = ThemeColors.Error;
                }
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
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        await ResultsClipboard.HandleKeyDown(grid, e, clipboard);
    }

    private async void CopyCell_Click(object? sender, RoutedEventArgs e)
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (grid != null && clipboard != null)
            await ResultsClipboard.CopyCell(grid, clipboard);
    }

    private async void CopySelected_Click(object? sender, RoutedEventArgs e)
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (grid != null && clipboard != null)
            await ResultsClipboard.CopyWithHeaders(grid, clipboard);
    }

    private async void CopyAll_Click(object? sender, RoutedEventArgs e)
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (grid != null && clipboard != null)
            await ResultsClipboard.CopyAll(grid, clipboard);
    }

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e)
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        var topLevel = TopLevel.GetTopLevel(this);
        if (grid != null && topLevel != null)
            await ResultsClipboard.ExportCsv(grid, topLevel.StorageProvider);
    }

    // --- Search/Filter ---

    private void ToggleSearchBar()
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

        var filtered = _allRows.Where(row =>
        {
            for (int i = 0; i < row.Length; i++)
            {
                if (row[i]?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
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

        // Numeric
        if (t.Contains("int") || t.Contains("decimal") || t.Contains("numeric")
            || t.Contains("float") || t.Contains("double") || t.Contains("real")
            || t.Contains("money") || t.Contains("number"))
            return ThemeColors.Get("DataTypeNumeric", "#e6b07a");

        // Date / time
        if (t.Contains("date") || t.Contains("time") || t.Contains("timestamp"))
            return ThemeColors.Get("DataTypeDate", "#8be9fd");

        // Boolean
        if (t == "bit" || t.Contains("bool"))
            return ThemeColors.Get("DataTypeBoolean", "#bd93f9");

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
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                (double a, double b) => a.CompareTo(b),
                (string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(x?.ToString(), y?.ToString(), StringComparison.OrdinalIgnoreCase)
            };
            return ascending ? result : -result;
        }
    }
}
