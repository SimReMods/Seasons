using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace SeasonsInstaller.Services;

internal static partial class Sounds
{
    public const string Startup = "startup";
    public const string Click = "click";

    private static readonly Dictionary<string, GCHandle> Pinned = new();
    private static readonly Dictionary<string, string> TempFiles = new();

    public static void Play(string name)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                PlaySound(GetPinned(name), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
            else if (OperatingSystem.IsMacOS())
                PlayWithTool(GetTempFile(name), "afplay");
            else if (OperatingSystem.IsLinux())
                PlayWithTool(GetTempFile(name), "paplay", "aplay");
        }
        catch (Exception)
        {
        }
    }

    private static byte[] Load(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://SeasonsSetup/Assets/{name}.wav"));
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static IntPtr GetPinned(string name)
    {
        if (!Pinned.TryGetValue(name, out var handle))
            Pinned[name] = handle = GCHandle.Alloc(Load(name), GCHandleType.Pinned);
        return handle.AddrOfPinnedObject();
    }

    private static string GetTempFile(string name)
    {
        if (!TempFiles.TryGetValue(name, out var path))
        {
            path = Path.Combine(Path.GetTempPath(), $"SeasonsSetup_{name}.wav");
            File.WriteAllBytes(path, Load(name));
            TempFiles[name] = path;
        }
        return path;
    }

    private static void PlayWithTool(string path, params string[] players)
    {
        foreach (var player in players)
        {
            try
            {
                var psi = new ProcessStartInfo(player) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(path);
                Process.Start(psi)?.Dispose();
                return;
            }
            catch (Exception)
            {
            }
        }
    }

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
