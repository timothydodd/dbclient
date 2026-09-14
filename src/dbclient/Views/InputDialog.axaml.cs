using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace dbclient.Views;

/// <summary>Single-value text prompt styled like the other app dialogs.</summary>
public partial class InputDialog : Window
{
    public string? Result { get; private set; }

    public InputDialog() : this("Input", "", "", "OK") { }

    public InputDialog(string title, string label, string initialValue, string okText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        LabelText.Text = label;
        ValueBox.Text = initialValue;
        OkButton.Content = okText;
        Validate();
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    /// <summary>Show modally; returns the trimmed value, or null when cancelled.</summary>
    public static async Task<string?> ShowAsync(Window owner, string title, string label, string initialValue = "", string okText = "OK")
    {
        var dlg = new InputDialog(title, label, initialValue, okText);
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Value_Changed(object? sender, TextChangedEventArgs e) => Validate();

    private void Validate() => OkButton.IsEnabled = !string.IsNullOrWhiteSpace(ValueBox.Text);

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Result = ValueBox.Text?.Trim();
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
