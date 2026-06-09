using System.Xml;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Search;
using AvaloniaEdit.Highlighting.Xshd;
using dbclient.IntelliSense;
using dbclient.IntelliSense.Interfaces;
using dbclient.ViewModels;

namespace dbclient.Views;

public partial class EditorView : UserControl
{
    private TextEditor? _editor;
    private CompletionWindow? _completionWindow;

    private IIntelliSenseProvider? CurrentProvider =>
        (DataContext as SessionTabViewModel)?.IntelliSenseProvider;
    private static IHighlightingDefinition? _sqlHighlighting;

    public EditorView()
    {
        InitializeComponent();

        _editor = this.FindControl<TextEditor>("Editor");

        if (_editor != null)
        {
            // Load SQL syntax highlighting
            _editor.SyntaxHighlighting = GetSqlHighlighting();
            ApplyThemeHighlightColors();
            Services.ThemeColors.ThemeChanged += OnThemeChanged;
            DetachedFromVisualTree += (_, _) => Services.ThemeColors.ThemeChanged -= OnThemeChanged;

            // Line numbers are enabled via the theme style, which populates the left margins after the
            // control attaches — add a gap between the line-number margin and the code text once present.
            _editor.AttachedToVisualTree += (_, _) => ApplyLineNumberPadding();

            _editor.TextArea.TextEntered += OnTextEntered;
            _editor.TextArea.TextEntering += OnTextEntering;
            _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

            SearchPanel.Install(_editor);

            _editor.TextChanged += (_, _) =>
            {
                if (DataContext is SessionTabViewModel vm)
                {
                    vm.QueryText = _editor.Text;
                }
            };
        }

        DataContextChanged += OnDataContextChanged;
    }

    private static IHighlightingDefinition? GetSqlHighlighting()
    {
        if (_sqlHighlighting != null) return _sqlHighlighting;

        try
        {
            using var stream = typeof(EditorView).Assembly
                .GetManifestResourceStream("dbclient.Assets.sql.xshd");
            if (stream != null)
            {
                using var reader = new XmlTextReader(stream);
                _sqlHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
        }
        catch
        {
            // Fall back to built-in TSQL if our custom one fails
            _sqlHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
        }

        return _sqlHighlighting;
    }

    private void ApplyLineNumberPadding()
    {
        if (_editor == null) return;
        // Gap between the line-number margin and the code: inset the TextView from the left.
        _editor.TextArea.TextView.Margin = new Thickness(10, 0, 0, 0);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyThemeHighlightColors();
        _editor?.TextArea.TextView.Redraw();
    }

    /// <summary>
    /// The shared SQL highlighting is loaded from a fixed xshd whose colors are tuned for dark
    /// backgrounds (comments/numbers/operators are light grays that disappear on white). Re-tint the
    /// named colors to suit the active theme so the editor stays readable in Light/Dracula too.
    /// </summary>
    private static void ApplyThemeHighlightColors()
    {
        if (_sqlHighlighting == null) return;
        var theme = App.Instance?.CurrentThemeName ?? "Dark";

        // (String, Comment, Keyword, Function, DataType, Number, Operator)
        var (str, comment, keyword, func, dataType, number, op) = theme switch
        {
            "Light"   => ("#A31515", "#008000", "#0033B3", "#795E26", "#267F99", "#098658", "#374151"),
            "Dracula" => ("#f1fa8c", "#6272a4", "#ff79c6", "#8be9fd", "#8be9fd", "#bd93f9", "#ff79c6"),
            _         => ("#D2BE3F", "#595959", "#558cb1", "#2B91AF", "#F9523D", "#c7c7c7", "#DBE6EC"),
        };

        SetHighlightColor("String", str);
        SetHighlightColor("Comment", comment);
        SetHighlightColor("Keyword", keyword);
        SetHighlightColor("Function", func);
        SetHighlightColor("DataType", dataType);
        SetHighlightColor("Number", number);
        SetHighlightColor("Operator", op);
    }

    private static void SetHighlightColor(string name, string hex)
    {
        var color = _sqlHighlighting?.GetNamedColor(name);
        if (color != null)
            color.Foreground = new SimpleHighlightingBrush(Avalonia.Media.Color.Parse(hex));
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SessionTabViewModel vm)
        {
            if (_editor != null && _editor.Text != vm.QueryText)
                _editor.Text = vm.QueryText;

            vm.ExecuteRequested += OnExecuteRequested;
            vm.QueryTextSet += (_, text) =>
            {
                if (_editor != null && _editor.Text != text)
                    _editor.Text = text;
            };
        }
    }

    private void OnExecuteRequested(object? sender, EventArgs e)
    {
        // Get selected text or full text
        if (DataContext is SessionTabViewModel vm && _editor != null)
        {
            var text = string.IsNullOrEmpty(_editor.SelectedText)
                ? _editor.Text
                : _editor.SelectedText;
            vm.QueryTextToExecute = text;
        }
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (DataContext is SessionTabViewModel vm && _editor != null)
        {
            vm.CursorLine = _editor.TextArea.Caret.Line;
            vm.CursorColumn = _editor.TextArea.Caret.Column;
        }
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completionWindow != null && e.Text?.Length > 0)
        {
            var ch = e.Text[0];
            if (ch == '.')
            {
                // Dot typed: insert current selection, close window, then OnTextEntered will open column completions
                _completionWindow.CompletionList.RequestInsertion(e);
            }
            else if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                _completionWindow.CompletionList.RequestInsertion(e);
            }
        }
    }

    private async void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_editor == null || CurrentProvider == null || e.Text == null)
            return;

        var ch = e.Text.Length > 0 ? e.Text[0] : '\0';

        // Dot always triggers fresh completion (for table.column)
        if (ch == '.')
        {
            if (_completionWindow != null)
            {
                _completionWindow.Close();
                _completionWindow = null;
            }
            await ShowCompletionAsync();
            return;
        }

        bool shouldTrigger = char.IsLetterOrDigit(ch) || ch == '_';

        if (ch == ' ')
        {
            var textBeforeCursor = _editor.Text[..Math.Min(_editor.CaretOffset, _editor.Text.Length)];
            var trimmed = textBeforeCursor.TrimEnd();
            var lastWord = GetLastWord(trimmed);
            shouldTrigger = IsKeyword(lastWord);
        }

        if (shouldTrigger && _completionWindow == null)
        {
            await ShowCompletionAsync();
        }
    }

    private async Task ShowCompletionAsync()
    {
        if (_editor == null || CurrentProvider == null)
            return;

        try
        {
            var text = _editor.Text;
            var offset = _editor.CaretOffset;

            var items = await CurrentProvider.GetCompletionsAsync(text, offset);
            if (items.Count == 0)
                return;

            _completionWindow = new CompletionWindow(_editor.TextArea);

            var data = _completionWindow.CompletionList.CompletionData;

            // Calculate StartOffset: how much of the current word to replace
            var wordStart = offset;
            while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '_'))
                wordStart--;

            // Don't include the dot in the replacement
            if (wordStart > 0 && text[wordStart - 1] == '.')
            {
                // We're after a dot, only replace what's after the dot
            }

            _completionWindow.StartOffset = wordStart;

            foreach (var item in items)
                data.Add(new SqlCompletionData(item));

            _completionWindow.Show();
            _completionWindow.Closed += (_, _) => _completionWindow = null;
        }
        catch (Exception ex)
        {
            Services.AppLogger.Error("Completion failed", ex);
            _completionWindow = null;
        }
    }

    private static string GetLastWord(string text)
    {
        var end = text.Length;
        var start = end;
        while (start > 0 && char.IsLetterOrDigit(text[start - 1]))
            start--;
        return text[start..end];
    }

    private static bool IsKeyword(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        var upper = word.ToUpperInvariant();
        return upper is "SELECT" or "FROM" or "WHERE" or "JOIN" or "INNER" or "LEFT" or "RIGHT" or
                        "CROSS" or "FULL" or "ON" or "AND" or "OR" or "INSERT" or "INTO" or
                        "UPDATE" or "SET" or "DELETE" or "HAVING" or "ORDER" or "GROUP" or "BY" or
                        "AS" or "DISTINCT" or "TOP";
    }
}
