using System.Diagnostics;
using Microsoft.Win32;

namespace SeasonsInstaller.Services;

public static class SimsLocator
{
    public static string? Detect()
    {
        var found = AllInstalls().ToList();
        return found.FirstOrDefault(d => File.Exists(Path.Combine(d, ModInstaller.StateDirName, "state.json")))
               ?? found.FirstOrDefault();
    }

    public static IEnumerable<string> AllInstalls()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in Candidates())
        {
            var dir = Clean(candidate);
            if (dir is null || !seen.Add(dir)) continue;
            bool ok;
            try { ok = LooksLikeSimsFolder(dir); }
            catch (Exception) { ok = false; }
            if (ok) yield return dir;
        }
    }

    private static string? Clean(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"').TrimEnd('\\', '/');
        try { return Path.GetFullPath(path); }
        catch (Exception) { return null; }
    }

    public static bool IsGameRunning()
    {
        try
        {
            return Process.GetProcessesByName("Sims").Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool LooksLikeSimsFolder(string dir)
    {
        if (!Directory.Exists(Path.Combine(dir, "GameData"))) return false;
        if (!OperatingSystem.IsWindows()) return true;
        return FindGameExe(dir) is not null;
    }

    public static string? FindGameExe(string dir)
    {
        var exe = Path.Combine(dir, "Sims.exe");
        if (File.Exists(exe)) return exe;
        try
        {
            return Directory.EnumerateFiles(dir, "Sims*.exe")
                .Where(f => !Path.GetFileName(f).Contains("Setup", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Length)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool TryLaunch(string dir, out string? error)
    {
        error = null;
        var exe = FindGameExe(dir);
        if (exe is null)
        {
            error = "Sims.exe was not found in:\n" + dir;
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IEnumerable<string?> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                yield return ReadRegistry(hive, RegistryView.Registry64, @"SOFTWARE\Maxis\The Sims", "InstallPath");
                yield return ReadRegistry(hive, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Maxis\The Sims", "InstallPath");
                yield return ReadRegistry(hive, RegistryView.Registry32, @"SOFTWARE\Maxis\The Sims", "InstallPath");
            }

            foreach (var library in SteamLibraries())
                yield return Path.Combine(library, "steamapps", "common", "The Sims Legacy Collection");

            var programFiles = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"C:\Program Files",
                @"C:\Program Files (x86)",
            };
            foreach (var root in programFiles.Where(r => !string.IsNullOrEmpty(r)))
            {
                yield return Path.Combine(root, "EA Games", "The Sims Legacy");
                yield return Path.Combine(root, "Steam", "steamapps", "common", "The Sims Legacy Collection");
                yield return Path.Combine(root, "Maxis", "The Sims");
                yield return Path.Combine(root, "EA GAMES", "The Sims");
            }
            yield return @"C:\Games\The Sims";
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, "The Sims");
            yield return Path.Combine(home, "Games", "The Sims");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string?>
        {
            ReadRegistry(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Valve\Steam", "SteamPath"),
            ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
        };
        var result = new List<string>();
        foreach (var root in roots)
        {
            var dir = Clean(root);
            if (dir is null || result.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(dir);
            try
            {
                var vdf = Path.Combine(dir, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;

                foreach (var line in File.ReadLines(vdf))
                {
                    var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(p => p.Length > 0).ToArray();
                    if (parts.Length >= 2 && parts[0].Equals("path", StringComparison.OrdinalIgnoreCase))
                    {
                        var lib = Clean(parts[1].Replace(@"\\", @"\"));
                        if (lib is not null && !result.Contains(lib, StringComparer.OrdinalIgnoreCase)) result.Add(lib);
                    }
                }
            }
            catch (Exception)
            {
            }
        }
        return result;
    }

    private static string? ReadRegistry(RegistryHive hive, RegistryView view, string subKey, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
