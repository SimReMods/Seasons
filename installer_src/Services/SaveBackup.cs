namespace SeasonsInstaller.Services;

public static class SaveBackup
{
    public sealed record SaveFolder(string Source, string Label);

    public static string BackupRoot =>
        Environment.GetEnvironmentVariable("SEASONS_BACKUP_ROOT") is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "The Sims Seasons Backups");

    public static List<SaveFolder> Find(string game)
    {
        var found = new List<SaveFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUserData(string parent, string label)
        {
            try
            {
                if (!Directory.Exists(parent)) return;
                foreach (var dir in Directory.EnumerateDirectories(parent, "UserData*").Order(StringComparer.OrdinalIgnoreCase))
                {
                    var full = Path.GetFullPath(dir);
                    if (seen.Add(full) && Directory.EnumerateFileSystemEntries(full).Any())
                        found.Add(new SaveFolder(full, Path.Combine(label, Path.GetFileName(full))));
                }
            }
            catch (Exception)
            {
            }
        }

        game = Path.GetFullPath(game);
        AddUserData(game, "Game folder");

        var localAppData = Env("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData);
        var root = Path.GetPathRoot(game);
        if (localAppData is not null && !string.IsNullOrEmpty(root))
            AddUserData(Path.Combine(localAppData, "VirtualStore", game[root.Length..]), "VirtualStore");

        var profile = Env("USERPROFILE", Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } p
            ? Path.Combine(p, "Documents")
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var (baseDir, label) in new[]
                 {
                     (profile is null ? null : Path.Combine(profile, "Saved Games", "Electronic Arts"), "Saved Games"),
                     (Path.Combine(documents, "Electronic Arts"), "Documents"),
                     (Path.Combine(documents, "EA Games"), "Documents EA Games"),
                 })
        {
            if (baseDir is null || !Directory.Exists(baseDir)) continue;
            try
            {
                foreach (var gameDir in Directory.EnumerateDirectories(baseDir))
                    AddUserData(gameDir, Path.Combine(label, Path.GetFileName(gameDir)));
            }
            catch (Exception) { }
        }
        return found;
    }

    public static string? Run(string game, string reason, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var folders = Find(game);
        if (folders.Count == 0) return null;

        var files = folders
            .SelectMany(f => Directory.EnumerateFiles(f.Source, "*", SearchOption.AllDirectories)
                .Select(file => (Folder: f, File: file)))
            .ToList();
        long total = Math.Max(1, files.Sum(x => new FileInfo(x.File).Length)), done = 0;

        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var dest = Path.Combine(BackupRoot, $"{stamp} before {reason}");
        for (var n = 2; Directory.Exists(dest); n++) dest = Path.Combine(BackupRoot, $"{stamp} before {reason} ({n})");
        Directory.CreateDirectory(dest);

        var buffer = new byte[1 << 20];
        foreach (var (folder, file) in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(folder.Source, file);
            var target = Path.Combine(dest, folder.Label, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            progress.Report(new((double)done / total, "Backing up " + Path.Combine(folder.Label, rel)));
            using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dst.Write(buffer, 0, read);
                    done += read;
                }
            }
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            if (new FileInfo(target).Length != new FileInfo(file).Length)
                throw new IOException("Backup copy is incomplete: " + target);
        }

        File.WriteAllLines(Path.Combine(dest, "BACKUP-INFO.txt"),
        [
            $"The Sims saved games, backed up by The Sims: Seasons setup on {DateTime.Now:yyyy-MM-dd HH:mm} before {reason}.",
            $"Game folder: {game}",
            "",
            "To restore, copy a folder back over the original location (with The Sims closed):",
            .. folders.Select(f => $"  {f.Label}  <-  {f.Source}"),
        ]);
        progress.Report(new(1, "Saved games backed up."));
        return dest;
    }

    private static string? Env(string name, Environment.SpecialFolder fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
        : Environment.GetFolderPath(fallback) is { Length: > 0 } f ? f : null;
}
