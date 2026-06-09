using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
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

            desktop.ShutdownRequested += (_, _) =>
            {
                viewModel.SaveState();
            };
        }

        base.OnFrameworkInitializationCompleted();
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
