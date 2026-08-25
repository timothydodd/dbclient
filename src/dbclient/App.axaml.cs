using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using dbclient.Data.Connections;
using dbclient.Services;
using dbclient.Themes;
using dbclient.ViewModels;
using dbclient.Views;

namespace dbclient;

public partial class App : Application
{
    private Styles? _theme;

    public static App? Instance => Current as App;

    /// <summary>Name of the currently applied theme ("Dark", "Light", "Dracula").</summary>
    public string CurrentThemeName { get; private set; } = "Dark";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
#if DEBUG
        // Avalonia 12: DevTools moved to AvaloniaUI.DiagnosticsSupport; the new AttachDeveloperTools()
        // extension targets the Application (not a Window). Opens the visual inspector with F12.
        this.AttachDeveloperTools();
#endif

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();

            // Apply saved theme (must happen after ViewModel loads state)
            SetTheme(viewModel.ThemeName);

            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };

            // Unhandled exceptions on the UI thread: log, save state, show a dialog, keep running.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                AppLogger.Error("Unhandled UI exception", e.Exception);
                try { viewModel.SaveState(); } catch { /* best effort */ }
                e.Handled = true;
                ShowErrorDialog(desktop.MainWindow, e.Exception);
            };

            // SSH unknown host key prompt. The lib invokes this on a background thread, so blocking
            // on the dispatcher here is safe.
            SshTunnel.UnknownHostKeyHandler = info =>
                Dispatcher.UIThread.InvokeAsync(() => ConfirmHostKeyAsync(desktop.MainWindow, info))
                    .GetAwaiter().GetResult();

            desktop.ShutdownRequested += (_, _) =>
            {
                try { viewModel.SaveState(); }
                catch (Exception ex) { AppLogger.Error("SaveState on shutdown failed", ex); }

                try
                {
                    var shutdown = viewModel.ShutdownAsync();
                    if (Task.WhenAny(shutdown, Task.Delay(3000)).GetAwaiter().GetResult() != shutdown)
                        AppLogger.Warn("ShutdownAsync did not complete within 3s; exiting anyway");
                    else if (shutdown.IsFaulted)
                        AppLogger.Error("ShutdownAsync failed", shutdown.Exception);
                }
                catch (Exception ex) { AppLogger.Error("Shutdown failed", ex); }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowErrorDialog(Window? owner, Exception ex)
    {
        try
        {
            var dialog = new ErrorDialog(
                $"Something went wrong: {ex.Message}\n\nThe application will keep running, but you may want to save your work.",
                ex);
            if (owner != null && owner.IsVisible)
                _ = dialog.ShowDialog(owner);
            else
                dialog.Show();
        }
        catch (Exception dialogEx)
        {
            AppLogger.Error("Failed to show error dialog", dialogEx);
        }
    }

    private static async Task<bool> ConfirmHostKeyAsync(Window? owner, SshHostKeyInfo info)
    {
        try
        {
            var dialog = new HostKeyDialog(info.Host, info.Port, info.KeyType, info.FingerprintSha256, info.FingerprintMd5);
            bool? result = owner != null && owner.IsVisible
                ? await dialog.ShowDialog<bool?>(owner)
                : await ShowStandaloneAsync(dialog);
            var trusted = result == true;
            AppLogger.Info($"SSH host key for {info.Host}:{info.Port} ({info.KeyType}, {info.FingerprintSha256}) {(trusted ? "trusted" : "rejected")} by user");
            return trusted;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Host key prompt failed; rejecting host", ex);
            return false;
        }
    }

    private static Task<bool?> ShowStandaloneAsync(HostKeyDialog dialog)
    {
        var tcs = new TaskCompletionSource<bool?>();
        dialog.Closed += (_, _) => tcs.TrySetResult(dialog.Trusted);
        dialog.Show();
        return tcs.Task;
    }

    public void SetTheme(string themeName)
    {
        if (_theme != null)
            Styles.Remove(_theme);

        _theme = themeName switch
        {
            "Dracula" => new DraculaTheme(),
            "Light" => new LightTheme(),
            _ => new DarkTheme()
        };
        CurrentThemeName = themeName switch
        {
            "Dracula" => "Dracula",
            "Light" => "Light",
            _ => "Dark"
        };
        Styles.Add(_theme);
        Services.ThemeColors.NotifyThemeChanged();
    }
}
