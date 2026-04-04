using System.Diagnostics;

namespace dbclient.Services;

public static class AppLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dbclient");
    private static readonly string LogFile = Path.Combine(LogDir, "log.txt");
    private static readonly string PrevLogFile = Path.Combine(LogDir, "log.prev.txt");
    private static readonly object Lock = new();
    private const long MaxLogSize = 1 * 1024 * 1024; // 1MB

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        var text = ex != null ? $"{message}: {ex.Message}" : message;
        Write("ERROR", text);
    }

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        Debug.WriteLine(line);

        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(LogDir);
                TruncateIfNeeded();
                File.AppendAllText(LogFile, line + Environment.NewLine);
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
            if (!File.Exists(LogFile)) return;
            var info = new FileInfo(LogFile);
            if (info.Length <= MaxLogSize) return;

            if (File.Exists(PrevLogFile))
                File.Delete(PrevLogFile);
            File.Move(LogFile, PrevLogFile);
        }
        catch
        {
            // Best effort rotation
        }
    }
}
