using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using dbclient.Services;

namespace dbclient.Views;

/// <summary>Collects the .dacpac path, target database name and publish options for a DACPAC import.</summary>
public partial class DacpacImportDialog : Window
{
    private static readonly FilePickerFileType DacpacFileType = new("DACPAC files") { Patterns = ["*.dacpac"] };
    private static readonly FilePickerFileType AllFileType = new("All files") { Patterns = ["*"] };

    private readonly HashSet<string> _existingDatabases = new(StringComparer.OrdinalIgnoreCase);

    public string? FilePath { get; private set; }
    public string? TargetDatabase { get; private set; }
    public bool BlockOnDataLoss { get; private set; } = true;
    public bool ScriptOnly { get; private set; }
    public bool Confirmed { get; private set; }

    public DacpacImportDialog()
    {
        InitializeComponent();
    }

    public DacpacImportDialog(string? defaultDatabase, IEnumerable<string> existingDatabases) : this()
    {
        foreach (var db in existingDatabases) _existingDatabases.Add(db);
        DatabaseBox.Text = defaultDatabase ?? "";
        Validate();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select DACPAC",
                AllowMultiple = false,
                FileTypeFilter = [DacpacFileType, AllFileType]
            });
            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(path)) return;

            FileBox.Text = path;
            // Default the target name from the file when the user hasn't typed one yet.
            if (string.IsNullOrWhiteSpace(DatabaseBox.Text))
                DatabaseBox.Text = Path.GetFileNameWithoutExtension(path);
            Validate();
        }
        catch (Exception ex) { AppLogger.Error("DACPAC browse failed", ex); }
    }

    private void Input_Changed(object? sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        var file = FileBox.Text?.Trim() ?? "";
        var db = DatabaseBox.Text?.Trim() ?? "";
        ImportButton.IsEnabled = file.Length > 0 && File.Exists(file) && db.Length > 0;
        ExistsWarning.IsVisible = db.Length > 0 && _existingDatabases.Contains(db);
    }

    private void Import_Click(object? sender, RoutedEventArgs e)
    {
        FilePath = FileBox.Text?.Trim();
        TargetDatabase = DatabaseBox.Text?.Trim();
        BlockOnDataLoss = BlockDataLossBox.IsChecked == true;
        ScriptOnly = ScriptOnlyBox.IsChecked == true;
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
