using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using dbclient.Services;

namespace dbclient.Views;

/// <summary>
/// Runs a SqlPackage command and streams its console output. Cancel kills the process;
/// once the process exits the Cancel button becomes Close.
/// </summary>
public partial class SqlPackageDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _log = new();
    private bool _finished;

    /// <summary>True when SqlPackage exited with code 0.</summary>
    public bool Succeeded { get; private set; }

    public SqlPackageDialog()
    {
        InitializeComponent();
    }

    public SqlPackageDialog(string title, string message) : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
    }

    /// <summary>Shows the dialog, runs SqlPackage with <paramref name="args"/>, and returns true on exit code 0.</summary>
    public static async Task<bool> RunAsync(Window owner, string title, string message, IEnumerable<string> args)
    {
        var dlg = new SqlPackageDialog(title, message);
        dlg.Opened += (_, _) => _ = dlg.ExecuteAsync(args);
        await dlg.ShowDialog(owner);
        return dlg.Succeeded;
    }

    private async Task ExecuteAsync(IEnumerable<string> args)
    {
        try
        {
            var code = await SqlPackageService.RunAsync(args, AppendLine, _cts.Token);
            Succeeded = code == 0;
            Finish(Succeeded ? "Completed successfully." : $"SqlPackage exited with code {code}.");
        }
        catch (OperationCanceledException)
        {
            Finish("Cancelled.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("SqlPackage run failed", ex);
            AppendLine(ex.Message);
            Finish("Failed to start SqlPackage.");
        }
    }

    private void AppendLine(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _log.AppendLine(line);
            LogText.Text = _log.ToString();
            LogScroll.ScrollToEnd();
        });
    }

    private void Finish(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _finished = true;
            StatusText.Text = status;
            CancelButton.IsVisible = false;
            CloseButton.IsVisible = true;
        });
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void CloseOrCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_finished)
        {
            Close();
            return;
        }
        StatusText.Text = "Cancelling...";
        _cts.Cancel();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Don't leave an orphaned SqlPackage running if the window is closed mid-run.
        if (!_finished) _cts.Cancel();
        base.OnClosing(e);
    }
}
