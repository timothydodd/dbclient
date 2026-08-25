using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace dbclient.Views;

public partial class HostKeyDialog : Window
{
    /// <summary>True once the user clicked Trust (also returned as the dialog result).</summary>
    public bool Trusted { get; private set; }

    public HostKeyDialog()
    {
        InitializeComponent();
    }

    public HostKeyDialog(string host, int port, string keyType, string fingerprintSha256, string fingerprintMd5) : this()
    {
        HostText.Text = $"The authenticity of host '{host}' (port {port}) can't be established.";
        KeyTypeText.Text = $"{keyType} key fingerprint:";
        Sha256Text.Text = fingerprintSha256.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? fingerprintSha256 : $"SHA256:{fingerprintSha256}";
        Md5Text.Text = string.IsNullOrEmpty(fingerprintMd5) ? "" :
            fingerprintMd5.StartsWith("MD5:", StringComparison.OrdinalIgnoreCase) ? fingerprintMd5 : $"MD5:{fingerprintMd5}";
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Trust_Click(object? sender, RoutedEventArgs e)
    {
        Trusted = true;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
