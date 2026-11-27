using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SeasonsInstaller.Services;

public readonly record struct InstallProgress(double Fraction, string Message);

public enum InstallAction { Install, Upgrade, Reinstall, Downgrade }

public static class ModInstaller
{
    public const string StateDirName = "SeasonsSetup";
    private const string TmpSuffix = ".seasons-tmp";

    public static string StateDir(string game) => Path.Combine(game, StateDirName);
    private static string StatePath(string game) => Path.Combine(StateDir(game), "state.json");
    private static string BackupDir(string game) => Path.Combine(StateDir(game), "backup");
    private static string TxnDir(string game) => Path.Combine(StateDir(game), "txn");
    private static string JournalPath(string game) => Path.Combine(TxnDir(game), "journal.log");
    private static string OldDir(string game) => Path.Combine(TxnDir(game), "old");

    public static InstallState? ReadState(string game)
    {
        var path = StatePath(game);
        if (!File.Exists(path)) return null;
        using var s = File.OpenRead(path);
        return JsonSerializer.Deserialize(s, JsonCtx.Default.InstallState);
    }

    public static bool HasPendingOperation(string game) => File.Exists(JournalPath(game));

    public static InstallAction GetAction(InstallState? state, string newVersion)
    {
        if (state is null) return InstallAction.Install;
        var cmp = VersionUtil.Compare(newVersion, state.Version);
        return cmp > 0 ? InstallAction.Upgrade : cmp < 0 ? InstallAction.Downgrade : InstallAction.Reinstall;
    }

    public static long BytesNeeded(Payload payload, InstallState? state, string? edition)
    {
        var old = OwnedMap(state);
        return payload.FilesFor(edition)
            .Where(f => !old.TryGetValue(f.Path, out var o) || !SameContent(o, f))
            .Sum(f => f.Size);
    }

    public static void Apply(Payload payload, string game, string? edition, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        game = Path.GetFullPath(game);
        Recover(game);

        var state = ReadState(game);
        var oldOwned = OwnedMap(state);
        var newFiles = payload.FilesFor(edition);
        var newSet = newFiles.Select(f => f.Path).ToHashSet(PathUtil.Comparer);
        var backups = new HashSet<string>(state?.Backups ?? [], PathUtil.Comparer);
        var createdDirs = new HashSet<string>(state?.CreatedDirs ?? [], PathUtil.Comparer);

        using var txn = Transaction.Begin(game, "apply " + payload.Version);
        try
        {
            var obsolete = oldOwned.Keys.Where(p => !newSet.Contains(p)).ToList();
            foreach (var rel in obsolete)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new(0, "Removing " + rel));
                txn.SaveOld(rel);
                if (backups.Remove(rel)) txn.RestoreBackup(rel);
            }

            long total = Math.Max(1, BytesNeeded(payload, state, edition)), done = 0;
            var buffer = new byte[1 << 20];
            foreach (var f in newFiles)
            {
                ct.ThrowIfCancellationRequested();
                var dest = PathUtil.Resolve(game, f.Path);

                if (oldOwned.TryGetValue(f.Path, out var old))
                {
                    if (SameContent(old, f) && FileMatches(dest, f))
                        continue;
                    txn.SaveOld(f.Path);
                }
                else if (File.Exists(dest))
                {
                    if (backups.Contains(f.Path)) txn.SaveOld(f.Path);
                    else
                    {
                        txn.Backup(f.Path);
                        backups.Add(f.Path);
                    }
                }

                foreach (var dir in txn.EnsureDirectory(Path.GetDirectoryName(dest)!))
                    createdDirs.Add(dir);

                txn.WriteFile(f, payload.GetFileEntry(f), buffer, read =>
                {
                    done += read;
                    progress.Report(new((double)done / total, "Copying " + f.Path));
                }, ct);
            }

            foreach (var dir in createdDirs.OrderByDescending(d => d.Length).ToList())
                if (txn.RemoveDirIfEmpty(dir)) createdDirs.Remove(dir);

            var newState = new InstallState
            {
                Product = payload.Manifest.Product,
                Version = payload.Version,
                Commit = payload.Manifest.Commit,
                InstalledAtUtc = DateTime.UtcNow,
                Edition = payload.HasVariants ? edition : null,
                Files = newFiles.ToList(),
                Backups = backups.Order(PathUtil.Comparer).ToList(),
                CreatedDirs = createdDirs.Order(PathUtil.Comparer).ToList(),
            };
            txn.Commit(newState, payload.ReadDoc("INSTALL.txt"));
            progress.Report(new(1, "Done."));
        }
        catch
        {
            progress.Report(new(0, "Rolling back..."));
            txn.Rollback();
            throw;
        }
    }

    public static bool Uninstall(string game, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        game = Path.GetFullPath(game);
        Recover(game);
        var state = ReadState(game);
        if (state is null) return false;

        var backups = new HashSet<string>(state.Backups, PathUtil.Comparer);
        using var txn = Transaction.Begin(game, "uninstall " + state.Version);
        try
        {
            int i = 0, n = Math.Max(1, state.Files.Count);
            foreach (var f in state.Files)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new((double)i++ / n, "Removing " + f.Path));
                txn.SaveOld(f.Path);
                if (backups.Remove(f.Path)) txn.RestoreBackup(f.Path);
            }

            foreach (var rel in backups) txn.RestoreBackup(rel);

            foreach (var dir in state.CreatedDirs.OrderByDescending(d => d.Length))
                txn.RemoveDirIfEmpty(dir);

            txn.Commit(newState: null, readme: null);
            progress.Report(new(1, "Done."));
            return true;
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    public static void Recover(string game)
    {
        game = Path.GetFullPath(game);
        if (!File.Exists(JournalPath(game))) return;
        Transaction.Resume(game).RecoverFromJournal();
    }

    private static Dictionary<string, ManifestFile> OwnedMap(InstallState? state) =>
        (state?.Files ?? []).ToDictionary(f => f.Path, PathUtil.Comparer);

    private static bool SameContent(ManifestFile a, ManifestFile b) =>
        a.Size == b.Size && string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool FileMatches(string path, ManifestFile f)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != f.Size) return false;
        using var s = info.OpenRead();
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(s)), f.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void SafeDelete(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static void SafeMove(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to, overwrite: false);
    }

    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    private sealed class Transaction : IDisposable
    {
        private readonly string _game;
        private StreamWriter? _journal;
        private bool _finished;

        private Transaction(string game) => _game = game;

        public static Transaction Begin(string game, string description)
        {
            var t = new Transaction(game);
            DeleteTree(TxnDir(game));
            Directory.CreateDirectory(TxnDir(game));
            t._journal = new StreamWriter(new FileStream(JournalPath(game), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            t.Log("BEGIN", description);
            return t;
        }

        public static Transaction Resume(string game) => new(game);

        private void Log(string op, string arg)
        {
            _journal!.Write(op + "\t" + arg + "\n");
            _journal.Flush();
            ((FileStream)_journal.BaseStream).Flush(flushToDisk: true);
        }

        private string Game(string rel) => PathUtil.Resolve(_game, rel);
        private string Old(string rel) => PathUtil.Resolve(OldDir(_game), rel);
        private string Bak(string rel) => PathUtil.Resolve(BackupDir(_game), rel);

        public void SaveOld(string rel)
        {
            var path = Game(rel);
            if (!File.Exists(path)) return;
            Log("SAVEOLD", rel);
            SafeDelete(Old(rel));
            SafeMove(path, Old(rel));
        }

        public void Backup(string rel)
        {
            SafeDelete(Bak(rel));
            Log("BACKUP", rel);
            SafeMove(Game(rel), Bak(rel));
        }

        public void RestoreBackup(string rel)
        {
            if (!File.Exists(Bak(rel)) || File.Exists(Game(rel))) return;
            Log("RESTORE", rel);
            SafeMove(Bak(rel), Game(rel));
        }

        public IEnumerable<string> EnsureDirectory(string fullDir)
        {
            var missing = new Stack<string>();
            for (var d = fullDir; !Directory.Exists(d); d = Path.GetDirectoryName(d)!)
                missing.Push(d);
            var created = new List<string>();
            while (missing.Count > 0)
            {
                var d = missing.Pop();
                var rel = PathUtil.ToRel(_game, d);
                Log("MKDIR", rel);
                Directory.CreateDirectory(d);
                created.Add(rel);
            }
            return created;
        }

        public bool RemoveDirIfEmpty(string rel)
        {
            var dir = Game(rel);
            if (!Directory.Exists(dir)) return true;
            if (Directory.EnumerateFileSystemEntries(dir).Any()) return false;
            Log("RMDIR", rel);
            Directory.Delete(dir);
            return true;
        }

        public void WriteFile(ManifestFile f, System.IO.Compression.ZipArchiveEntry entry, byte[] buffer, Action<int> onRead, CancellationToken ct)
        {
            var dest = Game(f.Path);
            var tmp = dest + TmpSuffix;
            Log("WRITE", f.Path);
            SafeDelete(tmp);
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var src = entry.Open())
                using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buffer, 0, read);
                        sha.AppendData(buffer, 0, read);
                        onRead(read);
                    }
                    dst.Flush(flushToDisk: true);
                }
                var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
                if (!string.Equals(hash, f.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The installer package is damaged (checksum mismatch for " + f.Path + ").");
            }
            File.Move(tmp, dest, overwrite: false);
        }

        public void Commit(InstallState? newState, string? readme)
        {
            var stateDir = StateDir(_game);
            if (newState is not null)
            {
                var tmp = StatePath(_game) + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, newState, JsonCtx.Default.InstallState);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, StatePath(_game), overwrite: true);
                Log("COMMIT", "apply");

                try
                {
                    File.WriteAllText(Path.Combine(stateDir, "version.txt"), newState.Version + Environment.NewLine);
                    if (readme is not null) File.WriteAllText(Path.Combine(stateDir, "INSTALL.txt"), readme);
                }
                catch (IOException) { }
            }
            else
            {
                Log("COMMIT", "uninstall");
            }
            Finish(uninstalled: newState is null);
        }

        private void Finish(bool uninstalled)
        {
            _journal?.Dispose();
            _journal = null;
            _finished = true;
            if (uninstalled)
            {
                DeleteTree(StateDir(_game));
            }
            else
            {
                DeleteTree(TxnDir(_game));
                RemoveEmptyDirs(BackupDir(_game));
            }
        }

        public void Rollback()
        {
            if (_finished) return;
            _journal?.Dispose();
            _journal = null;
            RecoverFromJournal();
        }

        public void RecoverFromJournal()
        {
            var lines = File.Exists(JournalPath(_game))
                ? File.ReadAllLines(JournalPath(_game), Encoding.UTF8).Select(l => l.Split('\t', 2)).Where(p => p.Length == 2).ToList()
                : [];
            var commit = lines.FirstOrDefault(l => l[0] == "COMMIT");
            if (commit is not null)
            {
                Finish(uninstalled: commit[1] == "uninstall");
                return;
            }

            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var (op, rel) = (lines[i][0], lines[i][1]);
                switch (op)
                {
                    case "WRITE":
                        SafeDelete(Game(rel) + TmpSuffix);
                        SafeDelete(Game(rel));
                        break;
                    case "SAVEOLD":
                        if (File.Exists(Old(rel)) && !File.Exists(Game(rel))) SafeMove(Old(rel), Game(rel));
                        break;
                    case "BACKUP":
                        if (File.Exists(Bak(rel)) && !File.Exists(Game(rel))) SafeMove(Bak(rel), Game(rel));
                        break;
                    case "RESTORE":
                        if (File.Exists(Game(rel)) && !File.Exists(Bak(rel))) SafeMove(Game(rel), Bak(rel));
                        break;
                    case "MKDIR":
                        try { if (Directory.Exists(Game(rel)) && !Directory.EnumerateFileSystemEntries(Game(rel)).Any()) Directory.Delete(Game(rel)); }
                        catch (IOException) { }
                        break;
                    case "RMDIR":
                        Directory.CreateDirectory(Game(rel));
                        break;
                }
            }
            _finished = true;
            DeleteTree(TxnDir(_game));

            if (!File.Exists(StatePath(_game))) DeleteTree(StateDir(_game));
        }

        private static void RemoveEmptyDirs(string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }

        public void Dispose() => _journal?.Dispose();
    }
}

public static class VersionUtil
{
    public static bool IsPreRelease(string version) => Split(version).Pre.Length > 0;

    public static int Compare(string a, string b)
    {
        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);
        for (var i = 0; i < 4; i++)
        {
            var c = Part(na, i).CompareTo(Part(nb, i));
            if (c != 0) return c;
        }
        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1;
        if (pb.Length == 0) return -1;
        return NaturalCompare(pa, pb);
    }

    private static int Part(int[] parts, int i) => i < parts.Length ? parts[i] : 0;

    private static (int[] Core, string Pre) Split(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-');
        var core = dash < 0 ? v : v[..dash];
        var pre = dash < 0 ? "" : v[(dash + 1)..];
        var parts = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        return (parts, pre);
    }

    private static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i; while (i < a.Length && char.IsDigit(a[i])) i++;
                var sj = j; while (j < b.Length && char.IsDigit(b[j])) j++;
                var c = decimal.Parse(a[si..i]).CompareTo(decimal.Parse(b[sj..j]));
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
