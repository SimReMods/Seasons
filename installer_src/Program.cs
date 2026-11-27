using System.Runtime.InteropServices;
using Avalonia;
using SeasonsInstaller.Services;

namespace SeasonsInstaller;

internal static partial class Program
{
    public static Payload? Payload { get; private set; }
    public static string? PayloadError { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Payload = Payload.TryOpen(out var error);
        PayloadError = error;

        if (args.Length > 0 && args[0] == "--cli")
            return Cli.Run(args[1..]);

        PrepareNativeLibraries();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void PrepareNativeLibraries()
    {
        if (!OperatingSystem.IsWindows() || Payload is null) return;
        var entries = Payload.RuntimeEntries.ToList();
        if (entries.Count == 0) return;

        var key = entries.Aggregate(17L, (h, e) => h * 31 + e.Crc32 + e.Length).ToString("x");
        var dir = Path.Combine(Path.GetTempPath(), "SeasonsSetup-runtime", key);
        Directory.CreateDirectory(dir);
        foreach (var entry in entries)
        {
            var path = Path.Combine(dir, entry.Name);
            if (File.Exists(path) && new FileInfo(path).Length == entry.Length) continue;
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            using (var src = entry.Open())
            using (var dst = File.Create(tmp))
                src.CopyTo(dst);
            try { File.Move(tmp, path, overwrite: true); }
            catch (IOException) { File.Delete(tmp); }
        }

        SetDllDirectoryW(dir);
        foreach (var entry in entries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            NativeLibrary.TryLoad(Path.Combine(dir, entry.Name), out _);
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectoryW(string path);
}
