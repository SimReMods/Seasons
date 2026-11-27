using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SeasonsInstaller.Services;

public sealed class Payload : IDisposable
{
    public const string Magic = "SEASONS-PAYLOAD1";
    public const string SidecarName = "payload.zip";
    private const int TrailerSize = 8 + 16;

    private readonly Stream _stream;

    private Payload(Stream stream, ZipArchive archive, PayloadManifest manifest, string source)
    {
        _stream = stream;
        Archive = archive;
        Manifest = manifest;
        Source = source;
    }

    public ZipArchive Archive { get; }
    public PayloadManifest Manifest { get; }
    public string Source { get; }
    public string Version => Manifest.Version;

    public bool HasVariants => Manifest.Files.Any(f => f.Variant is not null);

    public List<ManifestFile> FilesFor(string? edition)
    {
        if (HasVariants && edition is null) throw new InvalidOperationException("Game edition required.");
        return Manifest.Files.Where(f => f.Variant is null || string.Equals(f.Variant, edition, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string EntryName(ManifestFile f) =>
        (f.Variant is null ? "files/" : "variants/" + f.Variant + "/") + f.Path;

    public ZipArchiveEntry GetFileEntry(ManifestFile f) =>
        Archive.GetEntry(EntryName(f)) ?? throw new InvalidDataException("Package is missing " + EntryName(f));

    public IEnumerable<ZipArchiveEntry> RuntimeEntries =>
        Archive.Entries.Where(e => e.FullName.StartsWith("runtime/", StringComparison.Ordinal) && e.Name.Length > 0);

    public IEnumerable<string> Editions => Manifest.Files.Where(f => f.Variant is not null).Select(f => f.Variant!).Distinct();

    public List<string> ModsFor(string edition) => FilesFor(edition)
        .Where(f => f.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(f => f.Path[5..]).ToList();

    public List<(string Title, string Text)> DocsFor(string? edition)
    {
        var result = new List<(string, string)>();
        if (ReadDoc("INSTALL.txt") is { } main) result.Add(("", main));
        var editions = edition is null ? GameEdition.All : [edition];
        foreach (var ed in editions)
            foreach (var e in Archive.Entries
                         .Where(e => e.FullName.StartsWith("docs/" + ed + "/", StringComparison.Ordinal) && e.Name.Length > 0)
                         .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                if (ReadDoc(ed + "/" + e.Name) is { } text)
                    result.Add((edition is null ? $"{e.Name} - {GameEdition.DisplayName(ed)}" : e.Name, text));
        return result;
    }

    public string? ReadDoc(string name)
    {
        var entry = Archive.GetEntry("docs/" + name);
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static Payload? TryOpen(out string? error)
    {
        error = null;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null && TryOpenAppended(exe) is { } appended) return appended;

            foreach (var dir in new[] { Path.GetDirectoryName(exe), AppContext.BaseDirectory })
            {
                if (dir is null) continue;
                var sidecar = Path.Combine(dir, SidecarName);
                if (File.Exists(sidecar))
                {
                    var fs = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    return Open(fs, sidecar);
                }
            }
            error = "The installer does not contain the game files (no payload found).";
        }
        catch (Exception ex)
        {
            error = "The installer package is damaged: " + ex.Message;
        }
        return null;
    }

    private static Payload? TryOpenAppended(string exePath)
    {
        var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            if (fs.Length < TrailerSize) { fs.Dispose(); return null; }
            var trailer = new byte[TrailerSize];
            fs.Seek(-TrailerSize, SeekOrigin.End);
            fs.ReadExactly(trailer);
            if (Encoding.ASCII.GetString(trailer, 8, 16) != Magic) { fs.Dispose(); return null; }

            var zipLength = BitConverter.ToInt64(trailer, 0);
            var start = fs.Length - TrailerSize - zipLength;
            if (zipLength <= 0 || start < 0) throw new InvalidDataException("bad payload trailer");
            return Open(new SubStream(fs, start, zipLength), exePath);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static Payload Open(Stream stream, string source)
    {
        var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("manifest.json missing");
        PayloadManifest manifest;
        using (var s = entry.Open())
            manifest = JsonSerializer.Deserialize(s, JsonCtx.Default.PayloadManifest)
                       ?? throw new InvalidDataException("manifest.json is empty");

        if (string.IsNullOrWhiteSpace(manifest.Version)) throw new InvalidDataException("manifest has no version");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.Files)
        {
            PathUtil.ValidateRelative(f.Path);
            if (f.Variant is not null && !GameEdition.IsValid(f.Variant))
                throw new InvalidDataException("unknown edition in manifest: " + f.Variant);
            if (!seen.Add(f.Variant + "|" + f.Path)) throw new InvalidDataException("duplicate file in manifest: " + f.Path);
            if (f.Variant is null) common.Add(f.Path);
            var e = archive.GetEntry(EntryName(f)) ?? throw new InvalidDataException("missing in package: " + EntryName(f));
            if (e.Length != f.Size) throw new InvalidDataException("size mismatch in package: " + EntryName(f));
        }
        var both = manifest.Files.FirstOrDefault(f => f.Variant is not null && common.Contains(f.Path));
        if (both is not null) throw new InvalidDataException("file is both common and edition-specific: " + both.Path);
        return new Payload(stream, archive, manifest, source);
    }

    public void Dispose()
    {
        Archive.Dispose();
        _stream.Dispose();
    }

    private sealed class SubStream(Stream inner, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, length); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = length - _position;
            if (remaining <= 0) return 0;
            if (buffer.Length > remaining) buffer = buffer[..(int)remaining];
            inner.Position = start + _position;
            var n = inner.Read(buffer);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
