using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace dbclient.Views;

public partial class ApplyChangesDialog : Window
{
    public bool ShouldExecute { get; private set; }
    public string SqlText => SqlTextBox.Text ?? "";

    public ApplyChangesDialog() : this("") { }

    public ApplyChangesDialog(string sql)
    {
        InitializeComponent();
        SqlTextBox.Text = sql;
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
