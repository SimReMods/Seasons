namespace SeasonsInstaller.Services;

internal static class PathUtil
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static void ValidateRelative(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains('\\') || rel.StartsWith('/') || rel.Contains(':') ||
            rel.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
            throw new InvalidDataException("Invalid path in package: " + rel);
    }

    public static string Resolve(string root, string rel)
    {
        ValidateRelative(rel);
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path points outside the target folder: " + rel);
        return full;
    }

    public static string ToRel(string root, string full) =>
        Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/');
}
