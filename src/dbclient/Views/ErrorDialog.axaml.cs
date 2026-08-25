using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using dbclient.Services;

namespace dbclient.Views;

public partial class ErrorDialog : Window
{
    public ErrorDialog()
    {
        InitializeComponent();
    }

    public ErrorDialog(string message, Exception? ex) : this()
    {
        MessageText.Text = message;
        LogPathText.Text = $"Details were written to {AppLogger.LogFilePath}";
        DetailText.Text = ex?.ToString() ?? "";
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OpenLogFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppLogger.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Could not open log folder: {ex.Message}");
        }
    }
}
