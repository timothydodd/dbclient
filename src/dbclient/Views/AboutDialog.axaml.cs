using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using dbclient.Services;

namespace dbclient.Views;

public partial class AboutDialog : Window
{
    public const string RepoUrl = "https://github.com/timothydodd/dbclient";
    public const string IssuesUrl = RepoUrl + "/issues";

    /// <summary>Single source of truth for the shortcut list shown in Help > Keyboard Shortcuts.</summary>
    public static readonly (string Section, string Keys, string Action)[] Shortcuts =
    {
        ("File",   "Ctrl+N",             "New query tab"),
        ("File",   "Ctrl+W",             "Close query tab"),
        ("File",   "Ctrl+Tab / Ctrl+Shift+Tab", "Next / previous query tab"),
        ("File",   "Ctrl+O",             "Open .sql file"),
        ("File",   "Ctrl+S",             "Save .sql file (Save As when the tab has no file)"),
        ("File",   "Ctrl+Shift+S",       "Save .sql file as..."),
        ("File",   "Alt+F4",             "Exit"),
        ("Query",  "F5 / Ctrl+Enter",    "Execute query (selection or whole editor)"),
        ("Query",  "Esc",                "Cancel running query"),
        ("Query",  "Ctrl+E",             "Explain query plan"),
        ("Query",  "Ctrl+Shift+F",       "Format SQL"),
        ("Editor", "Ctrl+/",             "Toggle line comment (-- )"),
        ("Editor", "Ctrl+F",             "Find in the focused pane (editor, results or schema tree)"),
        ("Editor", "Ctrl+= / Ctrl+-",    "Zoom editor font in / out"),
        ("Editor", "Ctrl+0",             "Reset editor font size"),
        ("Editor", "Alt+Z",              "Toggle word wrap"),
        ("View",   "Ctrl+Shift+E",       "Focus schema tree filter (Explore)"),
        ("View",   "Ctrl+Shift+R",       "Toggle results filter"),
        ("View",   "Ctrl+L",             "Toggle connection panel"),
        ("View",   "Ctrl+H",             "Toggle history panel"),
        ("View",   "Ctrl+T",             "Cycle theme (Dark / Dracula / Light)"),
        ("Help",   "F1",                 "Keyboard shortcuts"),
    };

    public AboutDialog() : this(showShortcuts: false) { }

    public AboutDialog(bool showShortcuts)
    {
        InitializeComponent();
        PopulateAbout();
        PopulateShortcuts();
        if (showShortcuts) ShowShortcuts_Click(null, null!);
        else ShowAbout_Click(null, null!);
    }

    public static Task ShowAsync(Window owner, bool showShortcuts = false) =>
        new AboutDialog(showShortcuts).ShowDialog(owner);

    private static string VersionString()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AboutDialog).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // Strip the "+<commit hash>" source-link suffix SDK builds append.
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "unknown";
    }

    private void PopulateAbout()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AboutDialog).Assembly;
        var product = asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "dbclient";
        var copyright = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

        ProductText.Text = product;
        VersionText.Text = $"Version {VersionString()}";
        RuntimeText.Text = RuntimeInformation.FrameworkDescription;
        OsText.Text = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        AvaloniaText.Text = typeof(AvaloniaObject).Assembly.GetName().Version?.ToString(3) ?? "";
        DataDirText.Text = AppLogger.LogDirectory;
        LogFileText.Text = AppLogger.LogFilePath;
        CopyrightText.Text = copyright;
    }

    private void PopulateShortcuts()
    {
        ShortcutsList.Children.Clear();
        string? section = null;
        foreach (var (sec, keys, action) in Shortcuts)
        {
            if (sec != section)
            {
                section = sec;
                ShortcutsList.Children.Add(new TextBlock
                {
                    Text = sec,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = ThemeColors.Get("AccentColor", "#558cb1"),
                    Margin = new Thickness(0, 10, 0, 4)
                });
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
            row.Children.Add(new TextBlock
            {
                Text = keys,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                Margin = new Thickness(0, 2, 12, 2)
            });
            var actionText = new TextBlock { Text = action, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2) };
            Grid.SetColumn(actionText, 1);
            row.Children.Add(actionText);
            ShortcutsList.Children.Add(row);
        }
    }

    private void ShowAbout_Click(object? sender, RoutedEventArgs e)
    {
        AboutPage.IsVisible = true;
        ShortcutsPage.IsVisible = false;
        AboutPageButton.Classes.Set("active", true);
        ShortcutsPageButton.Classes.Set("active", false);
        TitleText.Text = Title = "About dbclient";
    }

    private void ShowShortcuts_Click(object? sender, RoutedEventArgs e)
    {
        AboutPage.IsVisible = false;
        ShortcutsPage.IsVisible = true;
        AboutPageButton.Classes.Set("active", false);
        ShortcutsPageButton.Classes.Set("active", true);
        TitleText.Text = Title = "Keyboard Shortcuts";
    }

    private void OpenRepo_Click(object? sender, RoutedEventArgs e) => OpenExternal(RepoUrl);
    private void OpenIssues_Click(object? sender, RoutedEventArgs e) => OpenExternal(IssuesUrl);
    private void OpenLogFolder_Click(object? sender, RoutedEventArgs e) => OpenExternal(AppLogger.LogDirectory);

    private async void CopyInfo_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = $"{ProductText.Text} {VersionText.Text}\n{RuntimeText.Text}\n{OsText.Text}\nAvalonia {AvaloniaText.Text}\nLog: {LogFileText.Text}";
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Copy info failed", ex);
        }
    }

    /// <summary>Open a URL or folder with the platform default handler.</summary>
    public static void OpenExternal(string target)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", target);
            else
                Process.Start("xdg-open", target);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to open {target}", ex);
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
