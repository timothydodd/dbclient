using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using dbclient.Services;
using dbclient.ViewModels;

namespace dbclient.Views;

public partial class MainWindow : Window
{
    // Drag-to-reorder state
    private SessionTabViewModel? _draggedTab;
    private Grid? _draggedElement;
    private Point _dragStartPoint;
    private bool _isDragging;
    private int _draggedOriginalIndex;
    private int _currentDropIndex;
    private const double DragThreshold = 8;

    public RelayCommand FocusTreeFilterCommand { get; }
    public RelayCommand ToggleResultsFilterCommand { get; }
    public RelayCommand ShowShortcutsCommand { get; }

    private bool _geometryRestored;

    public MainWindow()
    {
        InitializeComponent();

        FocusTreeFilterCommand = new RelayCommand(FocusTreeFilter);
        ToggleResultsFilterCommand = new RelayCommand(ToggleResultsFilter);
        ShowShortcutsCommand = new RelayCommand(() => _ = ShowAboutAsync(showShortcuts: true));

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // ViewModels never touch Windows: install the UI-side handlers they call out to.
                vm.ConfirmHandler = (title, msg) => ConfirmDialog.ShowAsync(this, title, msg, "Close");
                vm.PickOpenFileHandler = PickOpenFileAsync;
                vm.PickSaveFileHandler = PickSaveFileAsync;

                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.IsConnectionPanelOpen))
                        UpdateConnectionPanel(vm.IsConnectionPanelOpen);
                    if (e.PropertyName == nameof(MainWindowViewModel.IsHistoryPanelOpen))
                        UpdateHistoryPanel(vm.IsHistoryPanelOpen);
                };
                UpdateConnectionPanel(vm.IsConnectionPanelOpen);
                UpdateHistoryPanel(vm.IsHistoryPanelOpen);
                RestoreGeometry(vm);
            }
        };

        Opened += (_, _) =>
        {
            // Splitter ratios need the layout to exist; apply once after the first show.
            if (ViewModel is { } vm) RestoreSplitters(vm);
        };

        Closing += (_, _) =>
        {
            try
            {
                if (ViewModel is { } vm)
                {
                    CaptureGeometry(vm);
                    vm.SaveState();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to save window state on close", ex);
            }
        };
    }

    #region Window geometry

    private void RestoreGeometry(MainWindowViewModel vm)
    {
        if (_geometryRestored) return;
        _geometryRestored = true;

        try
        {
            if (vm.WindowWidth is > 200 && vm.WindowHeight is > 150)
            {
                Width = vm.WindowWidth.Value;
                Height = vm.WindowHeight.Value;
            }

            if (vm.WindowX.HasValue && vm.WindowY.HasValue)
            {
                var pos = new PixelPoint(vm.WindowX.Value, vm.WindowY.Value);
                // Only honor the saved position when it lands on a currently-attached screen;
                // otherwise (monitor unplugged etc.) fall back to centering.
                var onScreen = Screens.All.Any(sc =>
                    sc.WorkingArea.Contains(pos + new PixelVector(40, 20)));
                if (onScreen)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = pos;
                }
            }

            if (vm.WindowMaximized)
                WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to restore window geometry", ex);
        }
    }

    private void RestoreSplitters(MainWindowViewModel vm)
    {
        try
        {
            if (vm.LeftPanelWidth is > 100 && vm.IsConnectionPanelOpen)
                MainGrid.ColumnDefinitions[0].Width = new GridLength(vm.LeftPanelWidth.Value);

            if (vm.EditorHeightRatio is > 0.05 and < 0.95)
            {
                var r = vm.EditorHeightRatio.Value;
                EditorResultsGrid.RowDefinitions[1].Height = new GridLength(r, GridUnitType.Star);
                EditorResultsGrid.RowDefinitions[3].Height = new GridLength(1 - r, GridUnitType.Star);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to restore splitter sizes", ex);
        }
    }

    /// <summary>Copy the live window/splitter sizes into the view model (persisted by SaveState).</summary>
    public void CaptureGeometry(MainWindowViewModel vm)
    {
        var maximized = WindowState == WindowState.Maximized || WindowState == WindowState.FullScreen;
        vm.WindowMaximized = maximized;
        if (!maximized && WindowState != WindowState.Minimized)
        {
            vm.WindowWidth = Width;
            vm.WindowHeight = Height;
            vm.WindowX = Position.X;
            vm.WindowY = Position.Y;
        }

        if (vm.IsConnectionPanelOpen && MainGrid.ColumnDefinitions[0].ActualWidth > 0)
            vm.LeftPanelWidth = MainGrid.ColumnDefinitions[0].ActualWidth;

        var editorH = EditorResultsGrid.RowDefinitions[1].ActualHeight;
        var resultsH = EditorResultsGrid.RowDefinitions[3].ActualHeight;
        if (editorH + resultsH > 0)
            vm.EditorHeightRatio = editorH / (editorH + resultsH);
    }

    #endregion

    #region File pickers / dialogs

    private static readonly FilePickerFileType SqlFileType = new("SQL files") { Patterns = ["*.sql"] };
    private static readonly FilePickerFileType AllFileType = new("All files") { Patterns = ["*"] };

    private async Task<string?> PickOpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL file",
            AllowMultiple = false,
            FileTypeFilter = [SqlFileType, AllFileType]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save SQL file",
            SuggestedFileName = suggestedName,
            DefaultExtension = "sql",
            ShowOverwritePrompt = true,
            FileTypeChoices = [SqlFileType, AllFileType]
        });
        return file?.TryGetLocalPath();
    }

    private async Task ShowAboutAsync(bool showShortcuts)
    {
        try { await AboutDialog.ShowAsync(this, showShortcuts); }
        catch (Exception ex) { AppLogger.Error("About dialog failed", ex); }
    }

    private void About_Click(object? sender, RoutedEventArgs e) => _ = ShowAboutAsync(showShortcuts: false);
    private void Shortcuts_Click(object? sender, RoutedEventArgs e) => _ = ShowAboutAsync(showShortcuts: true);
    private void OpenLogFolder_Click(object? sender, RoutedEventArgs e) => AboutDialog.OpenExternal(AppLogger.LogDirectory);
    private void OpenRepo_Click(object? sender, RoutedEventArgs e) => AboutDialog.OpenExternal(AboutDialog.RepoUrl);

    /// <summary>Ctrl+Shift+E: open the connection panel if needed and focus the schema tree filter box.</summary>
    private void FocusTreeFilter()
    {
        if (ViewModel is { } vm && !vm.IsConnectionPanelOpen)
            vm.IsConnectionPanelOpen = true;
        var box = ConnectionPanelControl.FindControl<TextBox>("FilterBox");
        box?.Focus();
        box?.SelectAll();
    }

    /// <summary>Ctrl+Shift+R: show the results filter bar and focus its search box.</summary>
    private void ToggleResultsFilter()
    {
        var results = this.GetVisualDescendants().OfType<ResultsPanel>().FirstOrDefault();
        if (results == null) return;
        var bar = results.FindControl<Border>("SearchBar");
        var box = results.FindControl<TextBox>("SearchBox");
        if (bar == null || box == null) return;
        if (bar.IsVisible && box.IsFocused)
        {
            bar.IsVisible = false;
            return;
        }
        bar.IsVisible = true;
        box.Focus();
        box.SelectAll();
    }

    private void FocusTreeFilter_Click(object? sender, RoutedEventArgs e) => FocusTreeFilter();
    private void ToggleResultsFilter_Click(object? sender, RoutedEventArgs e) => ToggleResultsFilter();

    #endregion

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void NewConnection_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.IsConnectionPanelOpen = true;
            UpdateConnectionPanel(true);
        }

        var overlay = ConnectionPanelControl.FindControl<Border>("ConnectionsOverlay");
        if (overlay != null)
            overlay.IsVisible = true;
    }

    private void UpdateConnectionPanel(bool isOpen)
    {
        var grid = this.FindControl<Grid>("EditorResultsGrid")?.Parent as Grid;
        if (grid?.ColumnDefinitions.Count >= 5)
        {
            grid.ColumnDefinitions[0].Width = isOpen ? new GridLength(250) : new GridLength(0);
            grid.ColumnDefinitions[0].MinWidth = isOpen ? 150 : 0;
            grid.ColumnDefinitions[1].Width = isOpen ? new GridLength(5) : new GridLength(0);
            ConnectionSplitter.IsVisible = isOpen;
        }
    }

    private void UpdateHistoryPanel(bool isOpen)
    {
        var grid = this.FindControl<Grid>("EditorResultsGrid")?.Parent as Grid;
        if (grid?.ColumnDefinitions.Count >= 5)
        {
            grid.ColumnDefinitions[4].Width = isOpen ? new GridLength(280) : new GridLength(0);
            grid.ColumnDefinitions[4].MinWidth = isOpen ? 150 : 0;
            grid.ColumnDefinitions[3].Width = isOpen ? new GridLength(5) : new GridLength(0);
            HistorySplitter.IsVisible = isOpen;

            if (isOpen)
                HistoryPanelControl.RefreshHistory();
        }
    }

    #region Tab Context Menu

    private static async void FireAndForget(Task? task)
    {
        if (task == null) return;
        try { await task; }
        catch (Exception ex) { AppLogger.Error("Tab operation failed", ex); }
    }

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: SessionTabViewModel tab })
            FireAndForget(ViewModel?.SelectedConnectionTab?.CloseQueryTabAsync(tab));
    }

    private void CloseOthers_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: SessionTabViewModel tab })
            FireAndForget(ViewModel?.SelectedConnectionTab?.CloseOtherQueryTabs(tab));
    }

    private void CloseToRight_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: SessionTabViewModel tab })
            FireAndForget(ViewModel?.SelectedConnectionTab?.CloseQueryTabsToRight(tab));
    }

    private void CloseToLeft_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: SessionTabViewModel tab })
            FireAndForget(ViewModel?.SelectedConnectionTab?.CloseQueryTabsToLeft(tab));
    }

    private void RenameTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: SessionTabViewModel tab }) return;

        // Find the TextBlock for this tab in the tab strip and start inline rename
        var listBox = this.FindControl<ListBox>("TabStrip");
        if (listBox == null) return;

        foreach (var item in listBox.GetVisualDescendants())
        {
            if (item is TextBlock tb && tb.Tag is SessionTabViewModel tbTab && tbTab == tab)
            {
                StartInlineRename(tb, tab);
                break;
            }
        }
    }

    #endregion

    #region Tab Drag Reorder

    private void TabItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Grid grid && grid.Tag is SessionTabViewModel tab)
        {
            if (e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
            {
                _draggedTab = tab;
                _draggedElement = grid;
                _dragStartPoint = e.GetPosition(this);
                _isDragging = false;
                var connTab = ViewModel?.SelectedConnectionTab;
                _draggedOriginalIndex = connTab?.QueryTabs.IndexOf(tab) ?? -1;
                _currentDropIndex = _draggedOriginalIndex;
            }
        }
    }

    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedTab == null || _draggedElement == null)
            return;

        var currentPoint = e.GetPosition(this);
        var diff = currentPoint - _dragStartPoint;

        if (!_isDragging && (Math.Abs(diff.X) > DragThreshold || Math.Abs(diff.Y) > DragThreshold))
            StartDrag();

        if (_isDragging)
        {
            UpdateDragGhostPosition(currentPoint);
            UpdateDropIndicator(currentPoint);
        }
    }

    private void Window_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging && _draggedTab != null && ViewModel?.SelectedConnectionTab is { } connTab)
        {
            var sourceIndex = connTab.QueryTabs.IndexOf(_draggedTab);
            var targetIndex = _currentDropIndex;

            if (sourceIndex < targetIndex)
                targetIndex--;

            if (sourceIndex != targetIndex && sourceIndex >= 0
                && targetIndex >= 0 && targetIndex < connTab.QueryTabs.Count)
            {
                connTab.QueryTabs.Move(sourceIndex, targetIndex);
                ViewModel.SaveState();
            }

            connTab.SelectedQueryTab = _draggedTab;
        }

        EndDrag();
    }

    private void StartDrag()
    {
        _isDragging = true;
        DragGhostCanvas.IsVisible = true;
        DragGhostText.Text = _draggedTab?.Title ?? "";

        if (_draggedElement != null)
            _draggedElement.Opacity = 0.3;
    }

    private void UpdateDragGhostPosition(Point pos)
    {
        Canvas.SetLeft(DragGhost, pos.X + 10);
        Canvas.SetTop(DragGhost, pos.Y - 10);
    }

    private void UpdateDropIndicator(Point cursorPos)
    {
        var listBox = this.FindControl<ListBox>("TabStrip");
        if (listBox == null || ViewModel?.SelectedConnectionTab == null) return;

        var tabs = ViewModel.SelectedConnectionTab.QueryTabs;
        var containers = new List<(ListBoxItem item, int index)>();

        for (int i = 0; i < tabs.Count; i++)
        {
            if (listBox.ContainerFromIndex(i) is ListBoxItem container)
                containers.Add((container, i));
        }

        _currentDropIndex = tabs.Count; // default: drop at end

        foreach (var (container, index) in containers)
        {
            var tabPos = container.TranslatePoint(new Point(0, 0), this);
            if (tabPos == null) continue;

            var midX = tabPos.Value.X + container.Bounds.Width / 2;
            if (cursorPos.X < midX)
            {
                _currentDropIndex = index;
                break;
            }
        }

        UpdateTabMargins(containers);
    }

    private void UpdateTabMargins(List<(ListBoxItem item, int index)> containers)
    {
        foreach (var (container, index) in containers)
        {
            if (_draggedTab != null && ViewModel?.SelectedConnectionTab?.QueryTabs[index] == _draggedTab)
            {
                container.Margin = new Thickness(-container.Bounds.Width, 0, 0, 0);
                container.Opacity = 0;
                continue;
            }

            container.Opacity = 1;
            container.Margin = index == _currentDropIndex
                ? new Thickness(60, 0, 0, 0)
                : new Thickness(0);
        }
    }

    private void EndDrag()
    {
        DragGhostCanvas.IsVisible = false;

        if (_draggedElement != null)
            _draggedElement.Opacity = 1;

        // Reset all tab margins
        var listBox = this.FindControl<ListBox>("TabStrip");
        if (listBox != null && ViewModel?.SelectedConnectionTab != null)
        {
            var tabs = ViewModel.SelectedConnectionTab.QueryTabs;
            for (int i = 0; i < tabs.Count; i++)
            {
                if (listBox.ContainerFromIndex(i) is ListBoxItem container)
                {
                    container.Margin = new Thickness(0);
                    container.Opacity = 1;
                }
            }
        }

        _draggedTab = null;
        _draggedElement = null;
        _isDragging = false;
    }

    #endregion

    #region History Menu

    private void HistoryMenu_SubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem historyMenu || DataContext is not MainWindowViewModel vm) return;

        historyMenu.Items.Clear();
        var history = vm.GetHistory();

        if (history.Count == 0)
        {
            historyMenu.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
            return;
        }

        // Search box at top of history menu
        var searchBox = new TextBox
        {
            Watermark = "Search history...",
            FontSize = 11,
            Margin = new Thickness(4),
            MinWidth = 300
        };
        var searchItem = new MenuItem { Header = searchBox, StaysOpenOnClick = true };
        historyMenu.Items.Add(searchItem);
        historyMenu.Items.Add(new Separator());

        void PopulateHistory(string? filter)
        {
            // Remove items after the separator (index 2+)
            while (historyMenu.Items.Count > 2)
                historyMenu.Items.RemoveAt(historyMenu.Items.Count - 1);

            var filtered = string.IsNullOrWhiteSpace(filter)
                ? history
                : history.Where(entry =>
                    entry.Query.Contains(filter!, StringComparison.OrdinalIgnoreCase) ||
                    entry.Database.Contains(filter!, StringComparison.OrdinalIgnoreCase) ||
                    entry.Connection.Contains(filter!, StringComparison.OrdinalIgnoreCase)).ToList();

            if (filtered.Count == 0)
            {
                historyMenu.Items.Add(new MenuItem { Header = "(no matches)", IsEnabled = false });
                return;
            }

            foreach (var entry in filtered.Take(20))
            {
                var truncated = entry.Query.Replace("\r", "").Replace("\n", " ");
                if (truncated.Length > 80) truncated = truncated[..77] + "...";

                var item = new MenuItem
                {
                    Header = truncated,
                    Tag = entry.Query,
                };
                ToolTip.SetTip(item, $"{entry.Connection} / {entry.Database}\n{entry.ExecutedAt:g}");
                item.Click += (_, _) =>
                {
                    var tab = vm.SelectedConnectionTab?.SelectedQueryTab;
                    tab?.SetQueryText(entry.Query);
                };
                historyMenu.Items.Add(item);
            }
        }

        searchBox.TextChanged += (_, _) => PopulateHistory(searchBox.Text);
        PopulateHistory(null);
    }

    #endregion

    #region Tab Rename

    private void TabTitle_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock textBlock || textBlock.Tag is not SessionTabViewModel tab) return;
        StartInlineRename(textBlock, tab);
        e.Handled = true;
    }

    private void StartInlineRename(TextBlock textBlock, SessionTabViewModel tab)
    {
        var parent = textBlock.Parent as StackPanel;
        if (parent == null) return;

        var editBox = new TextBox
        {
            Text = tab.Title,
            FontSize = 11,
            MinWidth = 60,
            Padding = new Thickness(2, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            SelectionStart = 0,
            SelectionEnd = tab.Title.Length
        };

        var index = parent.Children.IndexOf(textBlock);
        parent.Children[index] = editBox;
        editBox.Focus();
        editBox.SelectAll();

        void Commit()
        {
            var newTitle = editBox.Text?.Trim();
            if (!string.IsNullOrEmpty(newTitle))
                tab.Title = newTitle;

            if (parent.Children.Contains(editBox))
                parent.Children[parent.Children.IndexOf(editBox)] = textBlock;
        }

        editBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Return) { Commit(); ke.Handled = true; }
            else if (ke.Key == Key.Escape)
            {
                if (parent.Children.Contains(editBox))
                    parent.Children[parent.Children.IndexOf(editBox)] = textBlock;
                ke.Handled = true;
            }
        };
        editBox.LostFocus += (_, _) => Commit();
    }

    #endregion

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();
}
