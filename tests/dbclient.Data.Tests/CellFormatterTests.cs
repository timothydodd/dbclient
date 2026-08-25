using System.Globalization;
using dbclient.Data.Models;

namespace dbclient.Data.Tests;

public class CellFormatterTests
{
    private static T WithCulture<T>(string name, Func<T> f)
    {
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(name);
        try { return f(); }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public void Null_and_DBNull_format_to_null()
    {
        Assert.Null(CellFormatter.Format(null));
        Assert.Null(CellFormatter.Format(DBNull.Value));
    }

    [Fact]
    public void String_passes_through()
        => Assert.Equal("O'Brien", CellFormatter.Format("O'Brien"));

    [Fact]
    public void DateTime_without_fraction()
        => Assert.Equal("2024-03-05 07:08:09", CellFormatter.Format(new DateTime(2024, 3, 5, 7, 8, 9)));

    [Fact]
    public void DateTime_with_fraction_trims_trailing_zeros()
    {
        var dt = new DateTime(2024, 3, 5, 7, 8, 9).AddMilliseconds(120);
        Assert.Equal("2024-03-05 07:08:09.12", CellFormatter.Format(dt));

        var ticks = new DateTime(2024, 3, 5, 7, 8, 9).AddTicks(1234567);
        Assert.Equal("2024-03-05 07:08:09.1234567", CellFormatter.Format(ticks));
    }

    [Fact]
    public void DateTimeOffset_includes_offset()
    {
        var dto = new DateTimeOffset(2024, 3, 5, 7, 8, 9, TimeSpan.FromHours(-5.5));
        Assert.Equal("2024-03-05 07:08:09 -05:30", CellFormatter.Format(dto));

        var utc = new DateTimeOffset(2024, 3, 5, 7, 8, 9, TimeSpan.Zero);
        Assert.Equal("2024-03-05 07:08:09 +00:00", CellFormatter.Format(utc));
    }

    [Fact]
    public void Decimal_and_double_are_culture_invariant()
    {
        var (dec, dbl, flt) = WithCulture("de-DE", () =>
            (CellFormatter.Format(1234.5m), CellFormatter.Format(0.1 + 0.2), CellFormatter.Format(1.5f)));
        Assert.Equal("1234.5", dec);
        Assert.Equal("0.30000000000000004", dbl);
        Assert.Equal("1.5", flt);
    }

    [Fact]
    public void Integer_types_are_culture_invariant()
    {
        var s = WithCulture("de-DE", () => CellFormatter.Format(1234567L));
        Assert.Equal("1234567", s);
    }

    [Fact]
    public void Bool_lowercase()
    {
        Assert.Equal("true", CellFormatter.Format(true));
        Assert.Equal("false", CellFormatter.Format(false));
    }

    [Fact]
    public void Guid_D_format()
    {
        var g = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", CellFormatter.Format(g));
    }

    [Fact]
    public void Bytes_short_is_hex()
    {
        Assert.Equal("0x", CellFormatter.Format(Array.Empty<byte>()));
        Assert.Equal("0x01FF", CellFormatter.Format(new byte[] { 1, 255 }));
        var exactly32 = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        Assert.Equal("0x" + new string('A', 0) + string.Concat(Enumerable.Repeat("AB", 32)), CellFormatter.Format(exactly32));
    }

    [Fact]
    public void Bytes_over_32_truncated_with_suffix()
    {
        var bytes = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        var s = CellFormatter.Format(bytes)!;
        Assert.StartsWith("0x" + Convert.ToHexString(bytes.AsSpan(0, 32)), s);
        Assert.EndsWith("… (40 bytes)", s);
    }

    [Fact]
    public void DateOnly_TimeOnly_TimeSpan()
    {
        Assert.Equal("2024-03-05", CellFormatter.Format(new DateOnly(2024, 3, 5)));
        Assert.Equal("07:08:09", CellFormatter.Format(new TimeOnly(7, 8, 9)));
        Assert.Equal("07:08:09.5", CellFormatter.Format(new TimeOnly(7, 8, 9, 500)));
        Assert.Equal("1.02:03:04", CellFormatter.Format(new TimeSpan(1, 2, 3, 4)));
    }
}
