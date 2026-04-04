using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using dbclient.ViewModels;

namespace dbclient.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.IsConnectionPanelOpen))
                        UpdateConnectionPanel(vm.IsConnectionPanelOpen);
                };
                UpdateConnectionPanel(vm.IsConnectionPanelOpen);
            }
        };
    }

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

    private void CloseConnectionTab_Click(object? sender, RoutedEventArgs e)
    {
        ConnectionTabViewModel? tab = null;
        if (sender is MenuItem mi && mi.Tag is ConnectionTabViewModel mt) tab = mt;
        else if (sender is Button btn && btn.Tag is ConnectionTabViewModel bt) tab = bt;

        if (tab != null && DataContext is MainWindowViewModel vm)
            vm.CloseConnectionTab(tab);
    }

    private void UpdateConnectionPanel(bool isOpen)
    {
        var grid = this.FindControl<Grid>("EditorResultsGrid")?.Parent as Grid;
        if (grid?.ColumnDefinitions.Count >= 3)
        {
            grid.ColumnDefinitions[0].Width = isOpen ? new GridLength(250) : new GridLength(0);
            grid.ColumnDefinitions[0].MinWidth = isOpen ? 150 : 0;
            grid.ColumnDefinitions[1].Width = isOpen ? new GridLength(5) : new GridLength(0);
            ConnectionSplitter.IsVisible = isOpen;
        }
    }

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
