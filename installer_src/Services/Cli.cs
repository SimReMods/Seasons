namespace SeasonsInstaller.Services;

internal static class Cli
{
    public static int Run(string[] args)
    {
        try
        {
            var command = args.Length > 0 ? args[0] : "";
            var game = args.Length > 1 ? args[1] : null;
            var payload = Program.Payload;

            switch (command)
            {
                case "version":
                    if (payload is null) return Fail(Program.PayloadError ?? "no payload");
                    Console.WriteLine(payload.Version);
                    return 0;

                case "status" when game is not null:
                    var state = ModInstaller.ReadState(game);
                    Console.WriteLine("package: " + (payload?.Version ?? "none"));
                    Console.WriteLine("installed: " + (state?.Version ?? "none"));
                    Console.WriteLine("pending: " + ModInstaller.HasPendingOperation(game));
                    var ed = state?.Edition ?? GameEdition.Detect(game);
                    Console.WriteLine("edition: " + ed);
                    if (payload is not null) Console.WriteLine("mods: " + string.Join(",", payload.ModsFor(ed)));
                    if (payload is not null) Console.WriteLine("docs: " + string.Join(",", payload.DocsFor(ed).Select(d => d.Title).Where(t => t.Length > 0)));
                    if (payload is not null) Console.WriteLine("action: " + ModInstaller.GetAction(state, payload.Version));
                    return 0;

                case "install" when game is not null:
                    if (payload is null) return Fail(Program.PayloadError ?? "no payload");
                    var before = ModInstaller.ReadState(game);
                    var edition = args.Length > 2 ? args[2] : before?.Edition ?? GameEdition.Detect(game);
                    if (!GameEdition.IsValid(edition)) return Fail("edition must be steam, ea or cc", 2);
                    var action = ModInstaller.GetAction(before, payload.Version);
                    var failAfter = int.TryParse(Environment.GetEnvironmentVariable("SEASONS_TEST_FAIL_AFTER"), out var n) ? n : -1;
                    var crashAfter = int.TryParse(Environment.GetEnvironmentVariable("SEASONS_TEST_CRASH_AFTER"), out var c) ? c : -1;
                    var copied = 0;
                    var last = "";
                    var progress = new SyncProgress(p =>
                    {
                        if (p.Message.StartsWith("Copying ") && p.Message != last)
                        {
                            last = p.Message;
                            if (crashAfter >= 0 && copied >= crashAfter)
                                Environment.Exit(3);
                            if (failAfter >= 0 && copied >= failAfter)
                                throw new IOException("SEASONS_TEST_FAIL_AFTER triggered");
                            copied++;
                        }
                    });
                    ModInstaller.Apply(payload, game, edition, progress, CancellationToken.None);
                    Console.WriteLine($"{action}: {before?.Version ?? "none"} -> {payload.Version} ({edition})");
                    return 0;

                case "uninstall" when game is not null:
                    var removed = ModInstaller.Uninstall(game, new SyncProgress(_ => { }), CancellationToken.None);
                    Console.WriteLine(removed ? "uninstalled" : "not installed");
                    return 0;

                case "backup" when game is not null:
                    foreach (var f in SaveBackup.Find(game)) Console.WriteLine("found: " + f.Label + " <- " + f.Source);
                    Console.WriteLine("backup: " + (SaveBackup.Run(game, "test", new SyncProgress(_ => { }), CancellationToken.None) ?? "none"));
                    return 0;

                case "recover" when game is not null:
                    ModInstaller.Recover(game);
                    Console.WriteLine("recovered");
                    return 0;

                default:
                    return Fail("usage: --cli version | status|install|uninstall|recover <game folder>", 2);
            }
        }
        catch (Exception ex)
        {
            return Fail(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static int Fail(string message, int code = 1)
    {
        Console.Error.WriteLine(message);
        return code;
    }

    private sealed class SyncProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}
