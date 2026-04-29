using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using dbclient.Models;
using dbclient.ViewModels;

namespace dbclient.Views;

public partial class HistoryPanel : UserControl
{
    private MainWindowViewModel? _vm;

    public HistoryPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Services.ThemeColors.ThemeChanged += OnThemeChanged;
        DetachedFromVisualTree += (_, _) => Services.ThemeColors.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => RefreshHistory();

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= Vm_PropertyChanged;

        _vm = DataContext as MainWindowViewModel;
        if (_vm == null) return;

        _vm.PropertyChanged += Vm_PropertyChanged;
        RefreshHistory();
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedConnectionTab)
            or nameof(MainWindowViewModel.HistoryChanged))
        {
            RefreshHistory();
        }
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshHistory();

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm != null)
            _vm.IsHistoryPanelOpen = false;
    }

    public void RefreshHistory()
    {
        if (_vm == null) return;

        var filter = SearchBox?.Text;
        var entries = _vm.GetHistoryForActiveConnection(filter);

        HistoryList.Children.Clear();

        if (entries.Count == 0)
        {
            HistoryList.Children.Add(new TextBlock
            {
                Text = _vm.SelectedConnectionTab == null ? "No connection selected" : "No history",
                FontSize = 11,
                Foreground = Services.ThemeColors.Get("SecondaryText", "#888888"),
                Margin = new Thickness(8, 12),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        foreach (var entry in entries)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 6),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Tag = entry
            };

            var truncated = entry.Query.Replace("\r", "").Replace("\n", " ");
            if (truncated.Length > 120) truncated = truncated[..117] + "...";

            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock
            {
                Text = truncated,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2
            });

            var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            meta.Children.Add(new TextBlock
            {
                Text = entry.Database,
                FontSize = 9,
                Foreground = Services.ThemeColors.Get("AccentColor", "#558cb1")
            });
            meta.Children.Add(new TextBlock
            {
                Text = FormatTime(entry.ExecutedAt),
                FontSize = 9,
                Foreground = Services.ThemeColors.Get("SecondaryText", "#888888")
            });
            content.Children.Add(meta);

            btn.Content = content;
            ToolTip.SetTip(btn, entry.Query);
            btn.Click += HistoryEntry_Click;

            HistoryList.Children.Add(btn);
        }
    }

    private void HistoryEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QueryHistoryEntry entry } || _vm == null) return;

        var tab = _vm.SelectedConnectionTab?.SelectedQueryTab;
        tab?.SetQueryText(entry.Query);
    }

    private static string FormatTime(DateTime dt)
    {
        var diff = DateTime.Now - dt;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return dt.ToString("MMM d");
    }
}
