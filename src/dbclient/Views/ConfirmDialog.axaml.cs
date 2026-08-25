using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace dbclient.Views;

/// <summary>Reusable OK/Cancel confirmation dialog styled like the other app dialogs.</summary>
public partial class ConfirmDialog : Window
{
    public bool Result { get; private set; }

    public ConfirmDialog() : this("Confirm", "", "OK") { }

    public ConfirmDialog(string title, string message, string okText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
    }

    /// <summary>Show modally over <paramref name="owner"/>; returns true when the user chose the OK button.</summary>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string okText = "OK")
    {
        var dlg = new ConfirmDialog(title, message, okText);
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
