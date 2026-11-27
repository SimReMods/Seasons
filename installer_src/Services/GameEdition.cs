namespace SeasonsInstaller.Services;

public static class GameEdition
{
    public const string Steam = "steam";
    public const string Ea = "ea";
    public const string CompleteCollection = "cc";

    public static readonly string[] All = [Steam, Ea, CompleteCollection];

    public static string DisplayName(string? edition) => edition switch
    {
        Steam => "Legacy Collection (Steam)",
        Ea => "Legacy Collection (EA app)",
        CompleteCollection => "Complete Collection / CD",
        _ => "unknown",
    };

    public static bool IsValid(string? edition) => edition is not null && All.Contains(edition);

    public static string Detect(string dir)
    {
        try
        {
            var path = Path.GetFullPath(dir).Replace('/', '\\');
            bool Has(string name) => File.Exists(Path.Combine(dir, name));

            if (Has("steam_api.dll") || Has("steam_api64.dll") || Has("steam_appid.txt") ||
                path.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase))
                return Steam;

            if (Directory.Exists(Path.Combine(dir, "__Installer")) ||
                path.Contains(@"\EA Games\The Sims Legacy", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(@"\The Sims Legacy", StringComparison.OrdinalIgnoreCase))
                return Ea;
        }
        catch (Exception)
        {
        }
        return CompleteCollection;
    }
}
