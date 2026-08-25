using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using dbclient.Data;
using dbclient.Views;

namespace dbclient.Services;

/// <summary>
/// Copy / export helpers for the results grid. NULL handling: a single copied null cell is written as the
/// literal <c>NULL</c> so it is distinguishable from an empty string; multi-cell / tab-separated copies and
/// CSV export write nulls as empty (standard); JSON export writes JSON null.
/// </summary>
public static class ResultsClipboard
{
    public static string?[] GetRowValues(object item)
    {
        if (item is ResultRow rr) return rr.Values;
        if (item is string?[] arr) return arr;
        return [];
    }

    // The first grid column is the row-number ("#") column; data columns start at grid index 1.
    private static int DataIndex(DataGrid grid, DataGridColumn col) => grid.Columns.IndexOf(col) - 1;

    private static IEnumerable<string> DataHeaders(DataGrid grid) =>
        grid.Columns.Skip(1).Select(c => c.Header?.ToString() ?? "");

    private static string SingleCellText(string? value) => value ?? UpdateSqlGenerator.NullLiteral;

    public static async Task HandleKeyDown(DataGrid grid, KeyEventArgs e, IClipboard? clipboard)
    {
        if (e.Key != Key.C || clipboard == null) return;

        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            await CopyWithHeaders(grid, clipboard);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            if (grid.SelectedItems.Count <= 1 && grid.CurrentColumn != null && grid.SelectedItem != null)
            {
                var row = GetRowValues(grid.SelectedItem);
                var dataIndex = DataIndex(grid, grid.CurrentColumn);
                if (dataIndex >= 0 && dataIndex < row.Length)
                {
                    await clipboard.SetTextAsync(SingleCellText(row[dataIndex]));
                    e.Handled = true;
                    return;
                }
            }

            if (grid.SelectedItems.Count > 1)
            {
                await CopyWithHeaders(grid, clipboard);
                e.Handled = true;
            }
        }
    }

    public static async Task CopyCell(DataGrid grid, IClipboard clipboard)
    {
        if (grid.CurrentColumn == null || grid.SelectedItem == null) return;

        var row = GetRowValues(grid.SelectedItem);
        var dataIndex = DataIndex(grid, grid.CurrentColumn);
        if (dataIndex < 0 || dataIndex >= row.Length) return;

        await clipboard.SetTextAsync(SingleCellText(row[dataIndex]));
    }

    public static async Task CopyWithHeaders(DataGrid grid, IClipboard clipboard)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', DataHeaders(grid)));

        foreach (var item in grid.SelectedItems)
        {
            var row = GetRowValues(item);
            sb.AppendLine(string.Join('\t', row.Select(v => v ?? "")));
        }

        await clipboard.SetTextAsync(sb.ToString());
    }

    public static async Task CopyAll(DataGrid grid, IClipboard clipboard)
    {
        if (grid.ItemsSource is not IList<ResultRow> rows || rows.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', DataHeaders(grid)));

        foreach (var row in rows)
            sb.AppendLine(string.Join('\t', Enumerable.Range(0, row.Length).Select(i => row[i] ?? "")));

        await clipboard.SetTextAsync(sb.ToString());
    }

    /// <summary>
    /// Copies the selected rows (or all rows when nothing is selected) as INSERT statements.
    /// <paramref name="quotedTable"/> is the already-quoted target table, or a placeholder like <c>&lt;table&gt;</c>.
    /// </summary>
    public static async Task CopyAsInsert(DataGrid grid, IClipboard clipboard, SqlDialect dialect,
        string quotedTable, string?[] columnTypes)
    {
        var rows = grid.SelectedItems.Count > 1
            ? grid.SelectedItems.Cast<object>().Select(GetRowValues).ToList()
            : (grid.ItemsSource as IEnumerable<ResultRow>)?.Select(r => r.Values).ToList() ?? [];
        if (rows.Count == 0) return;

        var headers = DataHeaders(grid).ToList();
        var columnList = string.Join(", ", headers.Select(h => SqlIdentifier.Quote(dialect, h)));

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var values = Enumerable.Range(0, headers.Count)
                .Select(i => UpdateSqlGenerator.Literal(i < row.Length ? row[i] : null, i < columnTypes.Length ? columnTypes[i] : null));
            sb.AppendLine($"INSERT INTO {quotedTable} ({columnList}) VALUES ({string.Join(", ", values)});");
        }

        await clipboard.SetTextAsync(sb.ToString());
    }

    public static async Task ExportCsv(DataGrid grid, IStorageProvider storageProvider)
    {
        if (grid.ItemsSource is not IList<ResultRow> rows || rows.Count == 0) return;

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to CSV",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV Files") { Patterns = ["*.csv"] }],
            SuggestedFileName = "export.csv"
        });

        if (file == null) return;

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', DataHeaders(grid).Select(CsvEscape)));

        foreach (var row in rows)
            sb.AppendLine(string.Join(',', Enumerable.Range(0, row.Length).Select(i => CsvEscape(row[i]))));

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(sb.ToString());
    }

    /// <summary>Exports all rows as a JSON array of objects keyed by column name; nulls become JSON null.</summary>
    public static async Task ExportJson(DataGrid grid, IStorageProvider storageProvider, string?[] columnTypes)
    {
        if (grid.ItemsSource is not IList<ResultRow> rows) return;

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to JSON",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON Files") { Patterns = ["*.json"] }],
            SuggestedFileName = "export.json"
        });

        if (file == null) return;

        var headers = DataHeaders(grid).ToList();

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (var row in rows)
        {
            writer.WriteStartObject();
            for (int i = 0; i < headers.Count; i++)
            {
                var value = i < row.Length ? row[i] : null;
                writer.WritePropertyName(headers[i]);
                if (value == null)
                    writer.WriteNullValue();
                else if (i < columnTypes.Length && UpdateSqlGenerator.IsNumericType(columnTypes[i])
                         && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    writer.WriteNumberValue(num);
                else
                    writer.WriteStringValue(value);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        await writer.FlushAsync();
    }

    private static string CsvEscape(string? value)
    {
        if (value == null) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
