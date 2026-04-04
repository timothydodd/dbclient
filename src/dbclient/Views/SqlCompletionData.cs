using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using dbclient.IntelliSense.Models;

namespace dbclient.Views;

public class SqlCompletionData : ICompletionData
{
    private readonly CompletionItem _item;

    public SqlCompletionData(CompletionItem item)
    {
        _item = item;
    }

    public string Text => _item.Text;

    public object Content => CreateContent();

    public object? Description => _item.Description;

    public double Priority => _item.Priority;

    public IImage? Image => null;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, Text);
    }

    private object CreateContent()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };

        // Type indicator with color
        var (label, colorKey) = _item.Type switch
        {
            CompletionType.Keyword => ("K", "KeywordColor"),
            CompletionType.Table => ("T", "TableColor"),
            CompletionType.Column => ("C", "ColumnColor"),
            CompletionType.Alias => ("A", "AliasColor"),
            _ => ("?", "CompletionText")
        };

        var indicator = new Border
        {
            Background = Services.ThemeColors.Get(colorKey),
            CornerRadius = new Avalonia.CornerRadius(2),
            Width = 18,
            Height = 18,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var text = new TextBlock
        {
            Text = _item.Text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Services.ThemeColors.Get("CompletionText")
        };

        panel.Children.Add(indicator);
        panel.Children.Add(text);

        // Add type info if available
        if (!string.IsNullOrEmpty(_item.Description))
        {
            var desc = new TextBlock
            {
                Text = _item.Description,
                FontSize = 10,
                Foreground = Services.ThemeColors.Get("CompletionDescText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(8, 0, 0, 0)
            };
            panel.Children.Add(desc);
        }

        return panel;
    }
}
