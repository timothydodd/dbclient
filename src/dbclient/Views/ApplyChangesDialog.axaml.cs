using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace dbclient.Views;

public partial class ApplyChangesDialog : Window
{
    public bool ShouldExecute { get; private set; }
    public string SqlText => SqlTextBox.Text ?? "";

    private readonly string _generatedSql;

    /// <summary>True when the user changed the generated script before executing.</summary>
    public bool WasEdited => !string.Equals(SqlText, _generatedSql, StringComparison.Ordinal);

    public ApplyChangesDialog() : this("", 0) { }

    public ApplyChangesDialog(string sql, int expectedStatements)
    {
        InitializeComponent();
        _generatedSql = sql;
        SqlTextBox.Text = sql;
        ExpectedText.Text = expectedStatements > 0
            ? $"{expectedStatements} UPDATE statement(s), each expected to affect exactly 1 row."
            : "";
        SqlTextBox.TextChanged += (_, _) => EditedWarning.IsVisible = WasEdited;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Execute_Click(object? sender, RoutedEventArgs e)
    {
        ShouldExecute = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        ShouldExecute = false;
        Close();
    }
}
