namespace WhatsappMessageSender.Services;

/// <summary>
/// Releases Chrome profile locks left behind when a prior worker or Chrome session did not shut down cleanly.
/// </summary>
internal static class ChromeProfileCleanup
{
    private static readonly string[] LockFileNames =
    [
        "SingletonLock",
        "SingletonCookie",
        "SingletonSocket",
        "lockfile"
    ];

    public static void PrepareProfile(string profilePath, bool clearLocks, bool killStaleProcesses)
    {
        Directory.CreateDirectory(profilePath);

        if (!clearLocks)
            return;

        if (killStaleProcesses)
        {
            Console.WriteLine("Checking for stale Chrome/chromedriver processes holding the profile lock…");
            KillProcesses("chromedriver");
        }

        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ClearLockFiles(profilePath);

            if (!IsProfileLocked(profilePath))
            {
                if (attempt > 1)
                    Console.WriteLine("Chrome profile lock released.");
                return;
            }

            if (attempt == maxAttempts)
                break;

            Console.WriteLine(
                $"Chrome profile still locked (attempt {attempt}/{maxAttempts}). Waiting 2s…");

            Thread.Sleep(TimeSpan.FromSeconds(2));

            if (killStaleProcesses && attempt >= 3)
            {
                Console.WriteLine("Stopping leftover chrome.exe processes that may hold the profile lock…");
                KillProcesses("chrome");
                KillProcesses("chromedriver");
            }
        }

        throw new InvalidOperationException(
            $"Chrome profile '{profilePath}' is locked by another process. " +
            "Stop the WhatsappMessageSender service, run: Get-Process chrome, chromedriver | Stop-Process -Force, " +
            "then start the service again. Ensure only one worker instance is running.");
    }

    private static bool IsProfileLocked(string profilePath)
    {
        foreach (var name in LockFileNames)
        {
            if (File.Exists(Path.Combine(profilePath, name)))
                return true;
        }

        var defaultProfile = Path.Combine(profilePath, "Default");
        return File.Exists(Path.Combine(defaultProfile, "lockfile"));
    }

    private static void ClearLockFiles(string profilePath)
    {
        foreach (var name in LockFileNames)
        {
            TryDeleteFile(Path.Combine(profilePath, name));
        }

        var defaultProfile = Path.Combine(profilePath, "Default");
        if (Directory.Exists(defaultProfile))
            TryDeleteFile(Path.Combine(defaultProfile, "lockfile"));
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
            Console.WriteLine($"Removed stale Chrome lock file: {path}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Could not remove Chrome lock file '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Could not remove Chrome lock file '{path}': {ex.Message}");
        }
    }

    private static void KillProcesses(string processName)
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
        {
            try
            {
                Console.WriteLine($"Stopping stale process {processName}.exe (pid {process.Id})…");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not stop {processName}.exe (pid {process.Id}): {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
