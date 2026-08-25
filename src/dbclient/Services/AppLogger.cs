namespace dbclient.Services;

public static class AppLogger
{
    private static readonly string PrevLogFile = Path.Combine(AppPaths.Root, "log.prev.txt");
    private static readonly object Lock = new();
    private const long MaxLogSize = 1 * 1024 * 1024; // 1MB

    /// <summary>Directory containing the log file (~/.dbclient).</summary>
    public static string LogDirectory => AppPaths.Root;

    /// <summary>Full path of the current log file.</summary>
    public static string LogFilePath => AppPaths.LogFile;

    /// <summary>
    /// When true, <see cref="Debug"/> messages are written. Defaults to true when the
    /// DBCLIENT_DEBUG environment variable is "1".
    /// </summary>
    public static bool Verbose { get; set; } =
        Environment.GetEnvironmentVariable("DBCLIENT_DEBUG") == "1";

    public static void Debug(string message)
    {
        if (Verbose) Write("DEBUG", message);
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex != null ? $"{message}: {ex}" : message);

    public static void Fatal(string message, Exception? ex = null)
        => Write("FATAL", ex != null ? $"{message}: {ex}" : message);

    /// <summary>Writes a startup banner: version, OS, runtime, data directory.</summary>
    public static void LogStartupBanner()
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        Write("INFO", $"==== dbclient {version} starting | OS: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}) " +
                      $"| Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} " +
                      $"| Data dir: {AppPaths.Root} | PID: {Environment.ProcessId}");
    }

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);

        try
        {
            lock (Lock)
            {
                AppPaths.EnsureDirectory();
                TruncateIfNeeded();
                var isNew = !File.Exists(AppPaths.LogFile);
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
                if (isNew) AppPaths.RestrictFile(AppPaths.LogFile);
            }
        }
        catch
        {
            // Last resort: can't log to file, Debug.WriteLine already happened
        }
    }

    private static void TruncateIfNeeded()
    {
        try
        {
            if (!File.Exists(AppPaths.LogFile)) return;
            var info = new FileInfo(AppPaths.LogFile);
            if (info.Length <= MaxLogSize) return;

            File.Move(AppPaths.LogFile, PrevLogFile, overwrite: true);
            AppPaths.RestrictFile(PrevLogFile);
        }
        catch
        {
            // Best effort rotation
        }
    }
}
