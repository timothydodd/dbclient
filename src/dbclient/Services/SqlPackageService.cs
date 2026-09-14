using System.Diagnostics;
using System.Runtime.InteropServices;

namespace dbclient.Services;

/// <summary>
/// Locates and drives the SqlPackage command-line tool for DACPAC extract / publish / script.
/// SqlPackage is not bundled; the user installs it (e.g. <c>dotnet tool install -g microsoft.sqlpackage</c>)
/// and we shell out to it so the app stays free of the DacFx dependency.
/// </summary>
public static class SqlPackageService
{
    private static string? _cachedPath;
    private static bool _searched;

    public const string InstallHint = "Install with: dotnet tool install -g microsoft.sqlpackage";

    /// <summary>Full path to the SqlPackage executable, or null when not found. Cached after the first lookup.</summary>
    public static string? ExecutablePath
    {
        get
        {
            if (!_searched)
            {
                _cachedPath = Locate();
                _searched = true;
                AppLogger.Info(_cachedPath != null ? $"SqlPackage found at {_cachedPath}" : "SqlPackage not found");
            }
            return _cachedPath;
        }
    }

    public static bool IsAvailable => ExecutablePath != null;

    /// <summary>Forget the cached lookup so the next access searches again (e.g. after the user installs the tool).</summary>
    public static void ResetCache() => _searched = false;

    private static string? Locate()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string[] names = isWindows ? ["sqlpackage.exe", "SqlPackage.exe"] : ["sqlpackage", "SqlPackage"];

        var candidates = new List<string>();

        // 1. PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(dir);

        // 2. dotnet global tools
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, ".dotnet", "tools"));

        // 3. Well-known install folders
        if (isWindows)
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var sqlRoot = Path.Combine(root, "Microsoft SQL Server");
                if (Directory.Exists(sqlRoot))
                {
                    // e.g. C:\Program Files\Microsoft SQL Server\160\DAC\bin
                    foreach (var ver in SafeEnumerateDirectories(sqlRoot).OrderByDescending(d => d))
                        candidates.Add(Path.Combine(ver, "DAC", "bin"));
                }
                candidates.Add(Path.Combine(root, "SqlPackage"));
                candidates.Add(Path.Combine(root, "sqlpackage"));
            }
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localApp, "Programs", "sqlpackage"));
        }
        else
        {
            candidates.Add("/usr/local/bin");
            candidates.Add("/usr/bin");
            candidates.Add("/opt/sqlpackage");
            candidates.Add(Path.Combine(home, "sqlpackage"));
        }

        foreach (var dir in candidates)
        {
            foreach (var name in names)
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return []; }
    }

    /// <summary>Arguments for extracting a database into a .dacpac.</summary>
    public static List<string> ExtractArgs(string connectionString, string targetFile, bool includeData) =>
    [
        "/Action:Extract",
        $"/SourceConnectionString:{connectionString}",
        $"/TargetFile:{targetFile}",
        $"/p:ExtractAllTableData={(includeData ? "True" : "False")}",
        "/p:VerifyExtraction=False"
    ];

    /// <summary>
    /// Arguments for publishing a .dacpac. The target database is the connection string's Initial Catalog
    /// (SqlPackage creates it when it does not exist). /TargetDatabaseName cannot be combined with
    /// /TargetConnectionString, so it is deliberately not passed.
    /// </summary>
    public static List<string> PublishArgs(string sourceFile, string connectionString, bool blockOnDataLoss) =>
    [
        "/Action:Publish",
        $"/SourceFile:{sourceFile}",
        $"/TargetConnectionString:{connectionString}",
        $"/p:BlockOnPossibleDataLoss={(blockOnDataLoss ? "True" : "False")}"
    ];

    /// <summary>Arguments for generating the deployment T-SQL script without applying it. Target database comes from the connection string.</summary>
    public static List<string> ScriptArgs(string sourceFile, string connectionString, string outputFile, bool blockOnDataLoss) =>
    [
        "/Action:Script",
        $"/SourceFile:{sourceFile}",
        $"/TargetConnectionString:{connectionString}",
        $"/OutputPath:{outputFile}",
        $"/p:BlockOnPossibleDataLoss={(blockOnDataLoss ? "True" : "False")}"
    ];

    /// <summary>
    /// Runs SqlPackage with <paramref name="args"/>, streaming each stdout/stderr line to <paramref name="onOutput"/>.
    /// Returns the process exit code (0 = success). Killing the process on cancellation.
    /// </summary>
    public static async Task<int> RunAsync(IEnumerable<string> args, Action<string> onOutput, CancellationToken ct)
    {
        var exe = ExecutablePath ?? throw new InvalidOperationException("SqlPackage was not found. " + InstallHint);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Log the command with the connection string redacted (it may contain a password).
        AppLogger.Info("SqlPackage " + string.Join(" ", args.Select(Redact)));

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }
        return proc.ExitCode;
    }

    private static string Redact(string arg)
    {
        if (arg.Contains("ConnectionString:", StringComparison.OrdinalIgnoreCase))
        {
            var idx = arg.IndexOf(':');
            return arg[..(idx + 1)] + "<redacted>";
        }
        return arg;
    }
}
