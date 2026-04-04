using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace dbclient.Services;

public static class ThemeColors
{
    public static IBrush Get(string key, string fallback = "#ffffff")
    {
        if (Application.Current?.TryFindResource(key, out var resource) == true
            && resource is IBrush brush)
            return brush;
        return Brush.Parse(fallback);
    }

    public static string GetColor(string key, string fallback = "#ffffff")
    {
        if (Application.Current?.TryFindResource(key, out var resource) == true
            && resource is ISolidColorBrush brush)
            return brush.Color.ToString();
        return fallback;
    }

    public static IBrush Error => Get("ErrorColor", "#ff5555");
    public static IBrush Success => Get("SuccessColor", "#50fa7b");
    public static IBrush Warning => Get("WarningColor", "#ffb86c");
    public static IBrush Info => Get("InfoColor", "#6272a4");
    public static IBrush NormalText => Get("NormalText", "#DBE6EC");
    public static IBrush MutedText => Get("MutedText", "#888888");
}
