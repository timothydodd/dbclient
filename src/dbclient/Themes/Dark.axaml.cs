using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace dbclient.Themes;

public class DarkTheme : Styles
{
    public DarkTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
