namespace dbclient.Services;

/// <summary>
/// Single source of truth for the ~/.dbclient directory and safe (atomic, permission-restricted) file writes.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Data directory. Defaults to ~/.dbclient; the DBCLIENT_HOME environment variable overrides it
    /// (used by tests and for portable installs).
    /// </summary>
    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var overridePath = Environment.GetEnvironmentVariable("DBCLIENT_HOME");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dbclient");
    }

    public static string ConnectionsDir => Path.Combine(Root, "connections");
    public static string StateFile => Path.Combine(Root, "state.json");
    public static string HistoryFile => Path.Combine(Root, "history.json");
    public static string LogFile => Path.Combine(Root, "log.txt");
    public static string SaltFile => Path.Combine(Root, ".salt");

    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates ~/.dbclient (0700 on unix). Safe to call repeatedly.</summary>
    public static void EnsureDirectory() => EnsureDirectory(Root);

    /// <summary>Creates the given directory (0700 on unix). Safe to call repeatedly.</summary>
    public static void EnsureDirectory(string dir)
    {
        Directory.CreateDirectory(dir);
        RestrictDirectory(dir);
    }

    /// <summary>
    /// Writes text to <paramref name="path"/> atomically: writes to path + ".tmp", then moves over the
    /// target. Sets 0600 on unix. The parent directory is created if needed.
    /// </summary>
    public static void WriteTextAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            EnsureDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        RestrictFile(tmp);
        File.Move(tmp, path, overwrite: true);
        RestrictFile(path);
    }

    /// <summary>Writes raw bytes atomically (same semantics as <see cref="WriteTextAtomic"/>).</summary>
    public static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            EnsureDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        RestrictFile(tmp);
        File.Move(tmp, path, overwrite: true);
        RestrictFile(path);
    }

    /// <summary>chmod 0600 on unix; no-op on Windows.</summary>
    public static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, FileMode0600); } catch { /* best effort */ }
    }

    /// <summary>chmod 0700 on unix; no-op on Windows.</summary>
    public static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, DirMode); } catch { /* best effort */ }
    }
}
