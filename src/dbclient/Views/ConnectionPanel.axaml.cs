using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using dbclient.Models;
using dbclient.ViewModels;

namespace dbclient.Views;

public partial class ConnectionPanel : UserControl
{
    private MainWindowViewModel? _vm;
    private System.ComponentModel.PropertyChangedEventHandler? _vmPropertyChangedHandler;
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _savedConnectionsHandler;

    public ConnectionPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from old VM
        if (_vm != null)
        {
            if (_vmPropertyChangedHandler != null)
                _vm.PropertyChanged -= _vmPropertyChangedHandler;
            if (_savedConnectionsHandler != null)
                _vm.SavedConnections.CollectionChanged -= _savedConnectionsHandler;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm == null) return;

        // When selected connection tab changes, rebind the tree
        _vmPropertyChangedHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.SelectedConnectionTab))
                BindTreeToActiveConnection();
        };
        _vm.PropertyChanged += _vmPropertyChangedHandler;

        BindTreeToActiveConnection();
        BuildSavedConnectionsList();

        // Rebuild saved connections list when collection changes
        _savedConnectionsHandler = (_, _) => BuildSavedConnectionsList();
        _vm.SavedConnections.CollectionChanged += _savedConnectionsHandler;
    }

    private ConnectionTabViewModel? _boundConnTab;

    private void BindTreeToActiveConnection()
    {
        var tree = this.FindControl<TreeView>("SchemaTree");
        if (tree == null) return;

        var connTab = _vm?.SelectedConnectionTab;

        // Unsubscribe from old
        if (_boundConnTab != null)
            _boundConnTab.ConnectionTree.CollectionChanged -= OnTreeCollectionChanged;

        _boundConnTab = connTab;
        tree.ItemsSource = connTab?.ConnectionTree;

        // Subscribe so we can force refresh if needed
        if (connTab != null)
            connTab.ConnectionTree.CollectionChanged += OnTreeCollectionChanged;
    }

    private void OnTreeCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Force re-bind the ItemsSource when the collection is rebuilt (Clear + Add pattern)
        var tree = this.FindControl<TreeView>("SchemaTree");
        if (tree != null && _boundConnTab != null && tree.ItemsSource != _boundConnTab.ConnectionTree)
            tree.ItemsSource = _boundConnTab.ConnectionTree;
    }

    private void BuildSavedConnectionsList()
    {
        var scrollViewer = this.FindControl<ScrollViewer>("SavedConnectionsList");
        if (scrollViewer == null || _vm == null) return;

        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(4) };

        foreach (var config in _vm.SavedConnections)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8),
                Background = Brushes.Transparent,
                Tag = config
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = Avalonia.Media.Geometry.Parse("M12 2C6.48 2 2 4.02 2 6.5v11C2 19.98 6.48 22 12 22s10-2.02 10-4.5v-11C22 4.02 17.52 2 12 2z"),
                Fill = Services.ThemeColors.Get("AccentColor", "#bd93f9"),
                Stretch = Stretch.Uniform,
                Width = 14, Height = 14,
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = config.ToString(),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            btn.Content = content;
            btn.Click += SavedConnection_Click;

            // Context menu
            var menu = new ContextMenu();
            var editItem = new MenuItem { Header = "Edit...", Tag = config };
            editItem.Click += EditConnection_Click;
            var deleteItem = new MenuItem { Header = "Delete", Tag = config };
            deleteItem.Click += DeleteConnection_Click;
            menu.Items.Add(editItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(deleteItem);
            btn.ContextMenu = menu;

            stack.Children.Add(btn);
        }

        scrollViewer.Content = stack;
    }

    private async void RefreshSchema_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedConnectionTab is { } connTab && !string.IsNullOrEmpty(connTab.ActiveDatabase))
            await connTab.SwitchDatabaseAsync(connTab.ActiveDatabase, force: true);
    }

    private void ToggleConnectionsOverlay_Click(object? sender, RoutedEventArgs e)
    {
        var overlay = this.FindControl<Border>("ConnectionsOverlay");
        if (overlay != null)
            overlay.IsVisible = !overlay.IsVisible;
    }

    private async void NewConnection_Click(object? sender, RoutedEventArgs e)
    {
        var window = this.FindAncestorOfType<Window>();
        if (window == null || _vm == null) return;

        var dialog = new ConnectionDialog();
        await dialog.ShowDialog(window);

        if (dialog.Result != null)
        {
            await _vm.OpenConnectionTabAsync(dialog.Result);
            var overlay = this.FindControl<Border>("ConnectionsOverlay");
            if (overlay != null) overlay.IsVisible = false;
        }
    }

    private async void SchemaTree_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.C && e.KeyModifiers == Avalonia.Input.KeyModifiers.Control)
        {
            var tree = this.FindControl<TreeView>("SchemaTree");
            if (tree?.SelectedItem is ConnectionTreeNode node)
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(node.Name);
                    e.Handled = true;
                }
            }
        }
    }

    private void TreeItem_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        var panel = sender as StackPanel;
        var node = panel?.Tag as ConnectionTreeNode;
        if (node == null || _vm?.SelectedConnectionTab == null) return;

        var connTab = _vm.SelectedConnectionTab;
        var menu = new ContextMenu();

        string QuoteName(string name) => connTab.Config.Type switch
        {
            ConnectionType.MySql => $"`{name}`",
            ConnectionType.Sqlite => $"\"{name}\"",
            _ => $"[{name}]"
        };

        void AddMenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        void InsertQuery(string sql)
        {
            if (connTab.QueryTabs.Count == 0) connTab.NewQueryTab();
            var tab = connTab.SelectedQueryTab ?? connTab.QueryTabs.First();
            connTab.SelectedQueryTab = tab;
            tab.SetQueryText(sql);
        }

        async void CopyToClipboard(string text)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(text);
        }

        switch (node.NodeType)
        {
            case ConnectionTreeNodeType.Database:
                AddMenuItem("Switch to Database", async () =>
                    await connTab.SwitchDatabaseAsync(node.Name));
                menu.Items.Add(new Separator());
                AddMenuItem("Copy Name", () => CopyToClipboard(node.Name));
                break;

            case ConnectionTreeNodeType.Table:
                var qt = QuoteName(node.Name);
                AddMenuItem("SELECT * FROM", () => InsertQuery($"SELECT * FROM {qt}"));
                AddMenuItem("SELECT TOP 100", () => InsertQuery(
                    connTab.Config.Type == ConnectionType.MySql
                        ? $"SELECT * FROM {qt} LIMIT 100"
                        : connTab.Config.Type == ConnectionType.Sqlite
                            ? $"SELECT * FROM {qt} LIMIT 100"
                            : $"SELECT TOP 100 * FROM {qt}"));
                AddMenuItem("SELECT COUNT(*)", () => InsertQuery($"SELECT COUNT(*) FROM {qt}"));
                menu.Items.Add(new Separator());
                AddMenuItem("INSERT INTO", () => InsertQuery($"INSERT INTO {qt} ()\nVALUES ()"));
                AddMenuItem("UPDATE", () => InsertQuery($"UPDATE {qt}\nSET \nWHERE "));
                AddMenuItem("DELETE FROM", () => InsertQuery($"DELETE FROM {qt}\nWHERE "));
                menu.Items.Add(new Separator());
                AddMenuItem("Script as CREATE TABLE", () => InsertQuery(ScriptCreateTable(node, connTab)));
                AddMenuItem("Script as DROP TABLE", () => InsertQuery($"DROP TABLE {qt}"));
                menu.Items.Add(new Separator());
                AddMenuItem("Copy Name", () => CopyToClipboard(node.Name));
                break;

            case ConnectionTreeNodeType.View:
                var qv = QuoteName(node.Name);
                AddMenuItem("SELECT * FROM", () => InsertQuery($"SELECT * FROM {qv}"));
                AddMenuItem("SELECT TOP 100", () => InsertQuery(
                    connTab.Config.Type == ConnectionType.MySql
                        ? $"SELECT * FROM {qv} LIMIT 100"
                        : connTab.Config.Type == ConnectionType.Sqlite
                            ? $"SELECT * FROM {qv} LIMIT 100"
                            : $"SELECT TOP 100 * FROM {qv}"));
                menu.Items.Add(new Separator());
                AddMenuItem("Script as DROP VIEW", () => InsertQuery($"DROP VIEW {qv}"));
                menu.Items.Add(new Separator());
                AddMenuItem("Copy Name", () => CopyToClipboard(node.Name));
                break;

            case ConnectionTreeNodeType.StoredProcedure:
                AddMenuItem("EXEC", () => InsertQuery(
                    connTab.Config.Type == ConnectionType.MySql
                        ? $"CALL {node.Name}()"
                        : $"EXEC {node.Name}"));
                menu.Items.Add(new Separator());
                AddMenuItem("Copy Name", () => CopyToClipboard(node.Name));
                break;

            case ConnectionTreeNodeType.Column:
                AddMenuItem("Copy Name", () => CopyToClipboard(node.Name));
                break;

            case ConnectionTreeNodeType.Folder:
                AddMenuItem("Refresh", async () =>
                    await connTab.SwitchDatabaseAsync(connTab.ActiveDatabase, force: true));
                break;
        }

        if (menu.Items.Count > 0)
        {
            menu.Open(panel);
            e.Handled = true;
        }
    }

    private void TreeItem_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        var node = (sender as StackPanel)?.Tag as ConnectionTreeNode;
        if (node == null || _vm?.SelectedConnectionTab == null) return;

        if (node.NodeType is not (ConnectionTreeNodeType.Table or ConnectionTreeNodeType.View)) return;

        var connTab = _vm.SelectedConnectionTab;
        var quoted = connTab.Config.Type switch
        {
            ConnectionType.MySql => $"`{node.Name}`",
            ConnectionType.Sqlite => $"\"{node.Name}\"",
            _ => $"[{node.Name}]"  // SQL Server
        };
        var query = $"SELECT * FROM {quoted}";

        if (connTab.QueryTabs.Count == 0)
            connTab.NewQueryTab();

        var tab = connTab.SelectedQueryTab ?? connTab.QueryTabs.First();
        connTab.SelectedQueryTab = tab;

        tab.SetQueryText(query);
        e.Handled = true;
    }

    private static string ScriptCreateTable(ConnectionTreeNode tableNode, ConnectionTabViewModel connTab)
    {
        string QuoteName(string n) => connTab.Config.Type switch
        {
            ConnectionType.MySql => $"`{n}`",
            ConnectionType.Sqlite => $"\"{n}\"",
            _ => $"[{n}]"
        };

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {QuoteName(tableNode.Name)} (");

        var columns = tableNode.Children.ToList();
        var pkColumns = new List<string>();

        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            var detail = col.Detail ?? "";
            var isPk = detail.Contains(" PK");
            var isNullable = detail.Contains(" NULL") && !isPk;
            var dataType = detail.Replace(" PK", "").Replace(" NULL", "").Trim();

            if (isPk) pkColumns.Add(col.Name);

            sb.Append($"    {QuoteName(col.Name)} {dataType}");
            if (!isNullable && !isPk) sb.Append(" NOT NULL");
            if (i < columns.Count - 1 || pkColumns.Count > 0) sb.Append(',');
            sb.AppendLine();
        }

        if (pkColumns.Count > 0)
            sb.AppendLine($"    PRIMARY KEY ({string.Join(", ", pkColumns.Select(QuoteName))})");

        sb.Append(");");
        return sb.ToString();
    }

    private async void SchemaTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TreeView tree && tree.SelectedItem is ConnectionTreeNode node
            && node.NodeType == ConnectionTreeNodeType.Database
            && _vm?.SelectedConnectionTab != null
            && node.Name != _vm.SelectedConnectionTab.ActiveDatabase)
        {
            await _vm.SelectedConnectionTab.SwitchDatabaseAsync(node.Name);
        }
    }

    private async void SavedConnection_Click(object? sender, RoutedEventArgs e)
    {
        var config = (sender as Button)?.Tag as ConnectionConfig;
        if (config != null && _vm != null)
        {
            await _vm.OpenConnectionTabAsync(config);
            var overlay = this.FindControl<Border>("ConnectionsOverlay");
            if (overlay != null) overlay.IsVisible = false;
        }
    }

    private async void EditConnection_Click(object? sender, RoutedEventArgs e)
    {
        var config = (sender as MenuItem)?.Tag as ConnectionConfig;
        if (config == null || _vm == null) return;

        var window = this.FindAncestorOfType<Window>();
        if (window == null) return;

        var dialog = new ConnectionDialog();
        dialog.LoadExisting(config);
        await dialog.ShowDialog(window);

        if (dialog.Result != null)
        {
            dialog.Result.Id = config.Id;
            var index = _vm.SavedConnections.IndexOf(config);
            if (index >= 0)
                _vm.SavedConnections[index] = dialog.Result;
            _vm.SaveState();
        }
    }

    private void DeleteConnection_Click(object? sender, RoutedEventArgs e)
    {
        var config = (sender as MenuItem)?.Tag as ConnectionConfig;
        if (config != null && _vm != null)
        {
            _vm.SavedConnections.Remove(config);
            _vm.SaveState();
        }
    }
}
