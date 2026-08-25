using System.Runtime.CompilerServices;

namespace dbclient.Data.Tests;

/// <summary>
/// Redirects the process HOME to a throw-away directory before any test code touches
/// <c>AppPaths.Root</c> (a static, get-only property computed once from the user profile).
/// On Unix, <c>Environment.GetFolderPath(UserProfile)</c> honours HOME, so every file-based
/// service (StateService, QueryHistoryService, CredentialProtector salt, AppLogger) is
/// isolated. On Windows the profile folder comes from the shell API and cannot be redirected
/// via an env var, so the file-based tests skip themselves there.
/// </summary>
internal static class TestEnvironment
{
    public static readonly string Home = Path.Combine(Path.GetTempPath(), "dbclient-tests-" + Guid.NewGuid().ToString("N"));

    public static bool CanRedirectHome => true; // DBCLIENT_HOME is honoured on every OS

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable("HOME", Home);
        Environment.SetEnvironmentVariable("USERPROFILE", Home);
        Environment.SetEnvironmentVariable("DBCLIENT_HOME", Path.Combine(Home, ".dbclient"));
    }

    /// <summary>Asserts that AppPaths.Root actually landed under the redirected HOME; skips otherwise.</summary>
    public static void RequireRedirectedRoot()
    {
        Assert.SkipUnless(CanRedirectHome, "DBCLIENT_HOME redirection unavailable");
        Assert.SkipUnless(dbclient.Services.AppPaths.Root.StartsWith(Home, StringComparison.Ordinal),
            $"AppPaths.Root ({dbclient.Services.AppPaths.Root}) was initialised before HOME was redirected");
    }
}

/// <summary>Serialises tests that share the process-wide ~/.dbclient files.</summary>
[CollectionDefinition("AppPaths", DisableParallelization = true)]
public class AppPathsCollection { }
