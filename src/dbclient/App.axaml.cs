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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
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
        Styles.Add(_theme);
        Services.ThemeColors.NotifyThemeChanged();
    }
}
