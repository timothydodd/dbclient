using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using dbclient.Views;

namespace dbclient.Services;

public static class ResultsClipboard
{
    public static string?[] GetRowValues(object item)
    {
        if (item is ResultRow rr) return rr.Values;
        if (item is string?[] arr) return arr;
        return [];
    }

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
                var colIndex = grid.Columns.IndexOf(grid.CurrentColumn);
                if (colIndex >= 0 && colIndex < row.Length)
                {
                    await clipboard.SetTextAsync(row[colIndex] ?? "");
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
        var colIndex = grid.Columns.IndexOf(grid.CurrentColumn);
        if (colIndex < 0 || colIndex >= row.Length) return;

        await clipboard.SetTextAsync(row[colIndex] ?? "");
    }

    public static async Task CopyWithHeaders(DataGrid grid, IClipboard clipboard)
    {
        var headers = string.Join('\t', grid.Columns.Select(c => c.Header?.ToString() ?? ""));
        var sb = new StringBuilder();
        sb.AppendLine(headers);

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

        var headers = string.Join('\t', grid.Columns.Select(c => c.Header?.ToString() ?? ""));
        var sb = new StringBuilder();
        sb.AppendLine(headers);

        foreach (var row in rows)
            sb.AppendLine(string.Join('\t', Enumerable.Range(0, row.Length).Select(i => row[i] ?? "")));

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
        sb.AppendLine(string.Join(',', grid.Columns.Select(c => CsvEscape(c.Header?.ToString()))));

        foreach (var row in rows)
            sb.AppendLine(string.Join(',', Enumerable.Range(0, row.Length).Select(i => CsvEscape(row[i]))));

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(sb.ToString());
    }

    private static string CsvEscape(string? value)
    {
        if (value == null) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
