using System.Xml;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Search;
using AvaloniaEdit.Highlighting.Xshd;
using dbclient.IntelliSense.Interfaces;
using dbclient.ViewModels;

namespace dbclient.Views;

public partial class EditorView : UserControl
{
    private TextEditor? _editor;
    private CompletionWindow? _completionWindow;
    private SessionTabViewModel? _vm;
    private MainWindowViewModel? _mainVm;

    private IIntelliSenseProvider? CurrentProvider => _vm?.IntelliSenseProvider;
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
                if (_vm != null)
                    _vm.QueryText = _editor.Text;
            };
        }

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
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
        catch (Exception ex)
        {
            Services.AppLogger.Error("Failed to load sql.xshd; falling back to TSQL", ex);
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

    // ---- Editor settings (font size / word wrap) come from the window-level view model ----

    private void OnAttached(object? sender, EventArgs e)
    {
        try
        {
            var mainVm = TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;
            if (!ReferenceEquals(mainVm, _mainVm))
            {
                if (_mainVm != null) _mainVm.PropertyChanged -= OnMainVmPropertyChanged;
                _mainVm = mainVm;
                if (_mainVm != null) _mainVm.PropertyChanged += OnMainVmPropertyChanged;
            }
            ApplyEditorSettings();
        }
        catch (Exception ex)
        {
            Services.AppLogger.Error("EditorView attach failed", ex);
        }
    }

    private void OnDetached(object? sender, EventArgs e)
    {
        if (_mainVm != null) _mainVm.PropertyChanged -= OnMainVmPropertyChanged;
        _mainVm = null;
    }

    private void OnMainVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.EditorFontSize) or nameof(MainWindowViewModel.EditorWordWrap))
            ApplyEditorSettings();
    }

    private void ApplyEditorSettings()
    {
        if (_editor == null || _mainVm == null) return;
        _editor.FontSize = _mainVm.EditorFontSize;
        _editor.WordWrap = _mainVm.EditorWordWrap;
        _editor.HorizontalScrollBarVisibility = _mainVm.EditorWordWrap
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }

    // ---- DataContext (per query tab) ----

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from the previous tab so handlers don't accumulate across tab switches.
        if (_vm != null)
        {
            _vm.ExecuteRequested -= OnExecuteRequested;
            _vm.QueryTextSet -= OnQueryTextSet;
            _vm.EditorActionRequested -= OnEditorActionRequested;
        }

        _vm = DataContext as SessionTabViewModel;

        if (_vm != null)
        {
            if (_editor != null && _editor.Text != _vm.QueryText)
                _editor.Text = _vm.QueryText;

            _vm.ExecuteRequested += OnExecuteRequested;
            _vm.QueryTextSet += OnQueryTextSet;
            _vm.EditorActionRequested += OnEditorActionRequested;
        }
    }

    private void OnQueryTextSet(object? sender, string text)
    {
        if (_editor != null && _editor.Text != text)
            _editor.Text = text;
    }

    private void OnEditorActionRequested(object? sender, string action)
    {
        try
        {
            switch (action)
            {
                case "ToggleComment":
                    ToggleLineComment();
                    break;
            }
        }
        catch (Exception ex)
        {
            Services.AppLogger.Error($"Editor action '{action}' failed", ex);
        }
    }

    /// <summary>
    /// Ctrl+/: toggle a "-- " line comment on every line touched by the selection (or the caret line).
    /// If every non-blank line in the range is already commented, the comment prefix is removed.
    /// </summary>
    private void ToggleLineComment()
    {
        if (_editor == null) return;
        var doc = _editor.Document;
        var sel = _editor.TextArea.Selection;

        int startOffset, endOffset;
        if (sel.IsEmpty)
        {
            startOffset = endOffset = _editor.CaretOffset;
        }
        else
        {
            startOffset = Math.Min(sel.SurroundingSegment.Offset, sel.SurroundingSegment.EndOffset);
            endOffset = Math.Max(sel.SurroundingSegment.Offset, sel.SurroundingSegment.EndOffset);
            // A selection ending exactly at a line start shouldn't include that line.
            if (endOffset > startOffset && doc.GetLineByOffset(endOffset).Offset == endOffset)
                endOffset--;
        }

        var firstLine = doc.GetLineByOffset(startOffset).LineNumber;
        var lastLine = doc.GetLineByOffset(endOffset).LineNumber;

        var lines = new List<DocumentLine>();
        for (var n = firstLine; n <= lastLine; n++)
            lines.Add(doc.GetLineByNumber(n));

        var nonBlank = lines.Where(l => !string.IsNullOrWhiteSpace(doc.GetText(l))).ToList();
        var allCommented = nonBlank.Count > 0 && nonBlank.All(l => doc.GetText(l).TrimStart().StartsWith("--"));

        using (doc.RunUpdate())
        {
            // Walk backwards so earlier offsets stay valid.
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                var text = doc.GetText(line);
                if (allCommented)
                {
                    var idx = text.IndexOf("--", StringComparison.Ordinal);
                    if (idx < 0) continue;
                    var len = 2;
                    if (idx + 2 < text.Length && text[idx + 2] == ' ') len = 3;
                    doc.Remove(line.Offset + idx, len);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(text) && lines.Count > 1) continue;
                    var indent = text.Length - text.TrimStart().Length;
                    doc.Insert(line.Offset + indent, "-- ");
                }
            }
        }
    }

    private void OnExecuteRequested(object? sender, EventArgs e)
    {
        // Get selected text or full text
        if (_vm != null && _editor != null)
        {
            var text = string.IsNullOrEmpty(_editor.SelectedText)
                ? _editor.Text
                : _editor.SelectedText;
            _vm.QueryTextToExecute = text;
        }
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_vm != null && _editor != null)
        {
            _vm.CursorLine = _editor.TextArea.Caret.Line;
            _vm.CursorColumn = _editor.TextArea.Caret.Column;
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
        try
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
        catch (Exception ex)
        {
            Services.AppLogger.Error("OnTextEntered failed", ex);
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
