using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace dbclient.Views;

/// <summary>Options for a DACPAC export (schema only vs. schema + data).</summary>
public partial class DacpacExportDialog : Window
{
    public bool Confirmed { get; private set; }
    public bool IncludeData { get; private set; }

    public DacpacExportDialog()
    {
        InitializeComponent();
    }

    public DacpacExportDialog(string database) : this()
    {
        MessageText.Text = $"Export database \"{database}\" to a .dacpac file.";
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Export_Click(object? sender, RoutedEventArgs e)
    {
        IncludeData = IncludeDataBox.IsChecked == true;
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
