using System.Text.Json.Serialization;

namespace SeasonsInstaller.Services;

public sealed class ManifestFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Variant { get; set; }
}

public sealed class PayloadManifest
{
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Commit { get; set; }
    public string? BuiltAt { get; set; }
    public List<ManifestFile> Files { get; set; } = [];
}

public sealed class InstallState
{
    public int Schema { get; set; } = 1;
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Commit { get; set; }
    public DateTime InstalledAtUtc { get; set; }

    public string? Edition { get; set; }

    public List<ManifestFile> Files { get; set; } = [];

    public List<string> Backups { get; set; } = [];

    public List<string> CreatedDirs { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PayloadManifest))]
[JsonSerializable(typeof(InstallState))]
internal partial class JsonCtx : JsonSerializerContext;
