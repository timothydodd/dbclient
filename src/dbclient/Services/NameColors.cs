using Avalonia.Media;

namespace dbclient.Services;

/// <summary>
/// Deterministic, theme-aware accent colors derived from a name (database / connection).
/// The hue comes from a stable hash of the name; saturation and lightness are tuned per theme
/// so the same database keeps its hue across themes while staying legible on each background.
/// </summary>
public static class NameColors
{
    public static IBrush ForName(string? name)
    {
        var (r, g, b) = RgbForName(name);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    public static (byte r, byte g, byte b) RgbForName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return (189, 147, 249); // default purple

        var hash = 0;
        foreach (var c in name)
            hash = unchecked(hash * 31 + char.ToLowerInvariant(c));

        var hue = Math.Abs(hash % 360) / 360.0;
        var (s, l) = (App.Instance?.CurrentThemeName ?? "Dark") switch
        {
            "Light" => (0.62, 0.40),   // darker, still saturated, on light chrome
            "Dracula" => (0.78, 0.70), // pastel-bright like the Dracula palette
            _ => (0.62, 0.62),         // Dark
        };
        var (r, g, b) = HslToRgb(hue, s, l);
        return ((byte)r, (byte)g, (byte)b);
    }

    private static (int r, int g, int b) HslToRgb(double h, double s, double l)
    {
        double r, g, b;
        if (s == 0) { r = g = b = l; }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }
        return ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
