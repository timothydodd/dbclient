using System.Globalization;

namespace dbclient.Data.Models;

/// <summary>
/// Converts raw ADO.NET cell values to display strings using the invariant culture.
/// DBNull / null become <c>null</c> so the UI can render NULL distinctly.
/// </summary>
public static class CellFormatter
{
    private const int MaxBinaryPreviewBytes = 32;

    public static string? Format(object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return null;
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case DateTime dt:
                return FormatDateTime(dt);
            case DateTimeOffset dto:
                return FormatDateTime(dto.DateTime) + FormatOffset(dto.Offset);
            case DateOnly d:
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeOnly t:
                return t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            case TimeSpan ts:
                return ts.ToString("c", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case double dbl:
                return dbl.ToString("R", CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case Guid g:
                return g.ToString("D");
            case byte[] bytes:
                return FormatBytes(bytes);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }

    private static string FormatDateTime(DateTime dt)
    {
        var s = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var ticks = dt.Ticks % TimeSpan.TicksPerSecond;
        if (ticks != 0)
            s += "." + ticks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return s;
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return $" {sign}{offset.Hours:D2}:{offset.Minutes:D2}";
    }

    private static string FormatBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return "0x";
        var preview = bytes.Length > MaxBinaryPreviewBytes ? bytes.AsSpan(0, MaxBinaryPreviewBytes) : bytes.AsSpan();
        var hex = "0x" + Convert.ToHexString(preview);
        return bytes.Length > MaxBinaryPreviewBytes
            ? $"{hex}… ({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes)"
            : hex;
    }
}
