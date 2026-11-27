using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SeasonsInstaller.Services;

namespace SeasonsInstaller;

public partial class MainWindow : Window
{
    private const string ProductName = "The Sims: Seasons";

    private const string WebsiteUrl = "https://simremods.github.io";

    private static readonly Point[] ArcSlots =
    [
        new(224.5, 209.5), new(299.5, 252.5), new(362, 307), new(404.5, 369), new(427.5, 431),
    ];
    private static readonly Point ArcCentre = new(52, 553);
    private static Payload? _payload => Program.Payload;
    private string? _simsDir;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        WebsiteButton.IsVisible = !string.IsNullOrWhiteSpace(WebsiteUrl);
        LayoutMenu();
        VersionText.Text = _payload is null ? "" : "v" + _payload.Version;
        _simsDir = SimsLocator.Detect();
        RefreshState();
    }

    public async Task SaveScreenshotAsync(string path)
    {
        await Task.Delay(500);
        var size = new PixelSize((int)Width, (int)Height);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        bitmap.Render((Visual)Content!);
        using var file = File.Create(path);
        bitmap.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private void RefreshState()
    {
        InstallButton.Label = _payload is null || _simsDir is null
            ? "Install"
            : ActionLabel(ModInstaller.GetAction(SafeReadState(_simsDir), _payload.Version));
        InstallButton.IsEnabled = !_busy && _payload is not null;
        PlayButton.IsEnabled = !_busy && _simsDir is not null && SimsLocator.FindGameExe(_simsDir) is not null;
        ReadmeButton.IsEnabled = !_busy;
        ExitButton.IsEnabled = !_busy;
    }

    private void LayoutMenu()
    {
        var buttons = new[] { InstallButton, PlayButton, WebsiteButton, ReadmeButton, ExitButton }
            .Where(b => b.IsVisible).ToArray();

        var polar = ArcSlots.Select(p =>
        {
            var d = p - ArcCentre;
            return (Angle: Math.Atan2(d.Y, d.X), Radius: Math.Sqrt(d.X * d.X + d.Y * d.Y));
        }).ToArray();

        for (var i = 0; i < buttons.Length; i++)
        {
            Point centre;
            if (buttons.Length == ArcSlots.Length)
                centre = ArcSlots[i];
            else
            {
                var t = buttons.Length == 1 ? 0 : (double)i / (buttons.Length - 1) * (polar.Length - 1);
                var k = Math.Min((int)t, polar.Length - 2);
                var f = t - k;
                var angle = polar[k].Angle + (polar[k + 1].Angle - polar[k].Angle) * f;
                var radius = polar[k].Radius + (polar[k + 1].Radius - polar[k].Radius) * f;
                centre = ArcCentre + new Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
            }

            var disc = buttons[i].DiscCentre;
            Canvas.SetLeft(buttons[i], Math.Round(centre.X - Controls.SimsButton.LabelColumnWidth - disc.X));
            Canvas.SetTop(buttons[i], Math.Round(centre.Y - disc.Y));
        }
    }

    private void OnDragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_payload is null)
        {
            await AlertAsync("Installer damaged", Program.PayloadError ?? "The installer does not contain the game files.");
            return;
        }

        InstallAction action;
        string edition;
        string? editionOverride = null;
        var pickFolder = true;
        while (true)
        {
            if (pickFolder)
            {
                if (!await ChooseGameFolderAsync()) return;
                editionOverride = null;
                pickFolder = false;
            }
            InstallState? state;
            try { state = ModInstaller.ReadState(_simsDir!); }
            catch (Exception ex)
            {
                await AlertAsync("Installation record damaged", $"{ModInstaller.StateDir(_simsDir!)}\\state.json could not be read:\n{ex.Message}");
                return;
            }
            action = ModInstaller.GetAction(state, _payload.Version);
            edition = editionOverride ?? (GameEdition.IsValid(state?.Edition) ? state!.Edition! : GameEdition.Detect(_simsDir!));
            var verb = ActionLabel(action);
            var what = state is null
                ? $"Install {ProductName} {_payload.Version} into:"
                : action switch
                {
                    InstallAction.Upgrade => $"Version {state.Version} is installed. Upgrade to {_payload.Version}? Folder:",
                    InstallAction.Downgrade => $"A NEWER version ({state.Version}) is installed. Replace it with {_payload.Version}? Folder:",
                    _ => $"Version {state.Version} is already installed. Reinstall it (repairs changed or missing files)? Folder:",
                };
            var text = $"{what}\n{_simsDir}";
            if (_payload.HasVariants)
            {
                var mods = _payload.ModsFor(edition);
                text += $"\n\nGame version: {GameEdition.DisplayName(edition)}" +
                        (mods.Count > 0 ? $"\nMods: {string.Join(", ", mods.Select(m => Path.GetFileNameWithoutExtension(m)))}" : "\nNo mods are available for this game version.");
            }

            var buttons = new List<string> { verb };
            if (state is not null) buttons.Add("Uninstall");
            buttons.Add("Change folder");
            if (_payload.HasVariants) buttons.Add("Game version...");
            buttons.Add("Cancel");
            var choice = buttons[await ShowDialogAsync(state is null ? "Install" : verb, text, buttons.ToArray())];

            if (choice == verb) break;
            if (choice == "Uninstall") { OnUninstall(sender, e); return; }
            if (choice == "Change folder")
            {
                pickFolder = true;
                continue;
            }
            if (choice == "Game version...")
            {
                var names = GameEdition.All.Select(GameEdition.DisplayName).Append("Cancel").ToArray();
                var picked = await ShowDialogAsync("Game version",
                    "Which version of The Sims is installed in this folder? Seasons needs the matching mod file.", names);
                if (picked < GameEdition.All.Length) editionOverride = GameEdition.All[picked];
                continue;
            }
            return;
        }
        var target = _simsDir!;

        if (!SimsLocator.LooksLikeSimsFolder(target) &&
            !await ConfirmAsync("Is this the right folder?",
                $"\"{target}\" does not look like a Sims folder (expected Sims.exe and GameData).\n\nInstall here anyway?"))
            return;

        if (!Directory.Exists(Path.Combine(target, "ExpansionPack7")) &&
            !await ConfirmAsync("Expansion packs missing?",
                "The Sims: Makin' Magic (ExpansionPack7) was not found in this folder. " +
                "Seasons is meant to be installed on top of the complete game with all expansion packs.\n\nContinue anyway?"))
            return;

        if (VersionUtil.IsPreRelease(_payload.Version) &&
            await ShowDialogAsync("Beta version",
                $"This is beta version ({_payload.Version}). There might be bugs.\n\n" +
                "Your saved games will be backed up first, so you can go back to them.", "Continue", "Cancel") != 0)
            return;

        if (SimsLocator.IsGameRunning())
        {
            await AlertAsync("The Sims is running", "Please close The Sims before continuing.");
            return;
        }

        var needed = ModInstaller.BytesNeeded(_payload, SafeReadState(target), edition) + 32L * 1024 * 1024;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(target));
            if (root is not null)
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (free < needed)
                {
                    await AlertAsync("Not enough disk space",
                        $"Installing needs about {needed / (1024 * 1024)} MB free; only {free / (1024 * 1024)} MB is available.");
                    return;
                }
            }
        }
        catch (Exception) {  }

        var payload = _payload;
        var backup = await BackupSavesAsync(target, ActionLabel(action).ToLowerInvariant() + " " + payload.Version);
        if (!backup.Proceed) return;
        await RunBusyAsync(p => ModInstaller.Apply(payload, target, edition, p, CancellationToken.None),
            action switch { InstallAction.Upgrade => "Upgrade complete", InstallAction.Reinstall => "Reinstall complete", _ => "Installation complete" },
            $"{ProductName} {payload.Version} is installed.\n\n" + backup.Summary +
            "Replaced Maxis files were backed up to:\n" + ModInstaller.StateDir(target) +
            "\n\nRun setup again and choose Uninstall to restore them.",
            "Installation failed");
    }

    private async void OnUninstall(object? sender, RoutedEventArgs e)
    {
        if (_busy || _simsDir is null) return;
        var target = _simsDir;
        if (SimsLocator.IsGameRunning())
        {
            await AlertAsync("The Sims is running", "Please close The Sims before continuing.");
            return;
        }
        if (!await ConfirmAsync("Uninstall?",
                $"Remove {ProductName} from\n{target}\n\nOriginal Maxis files that were replaced will be restored."))
            return;

        var backup = await BackupSavesAsync(target, "uninstall");
        if (!backup.Proceed) return;
        await RunBusyAsync(p => ModInstaller.Uninstall(target, p, CancellationToken.None),
            "Uninstall complete", ProductName + " has been removed.\n\n" + backup.Summary.TrimEnd(), "Uninstall failed");
    }

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (_simsDir is null) return;
        if (SimsLocator.TryLaunch(_simsDir, out var error))
            Close();
        else
            await AlertAsync("Could not start the game", error ?? "Unknown error.");
    }

    private async void OnReadme(object? sender, RoutedEventArgs e)
    {
        string? edition = null;
        if (_simsDir is not null)
            edition = SafeReadState(_simsDir)?.Edition is { } ed && GameEdition.IsValid(ed) ? ed : GameEdition.Detect(_simsDir);
        var docs = _payload?.DocsFor(edition)
            .Where(d => !string.IsNullOrWhiteSpace(d.Text))
            .Select(d => d.Title.Length == 0 ? d.Text : $"=== {d.Title} ===\n\n{d.Text}")
            .ToList();
        var text = docs is { Count: > 0 } ? string.Join("\n\n----------------------------------------\n\n", docs) : "The package readme is not available.";
        if (edition is not null && _payload is { HasVariants: true })
            text = $"Game: {GameEdition.DisplayName(edition)} ({_simsDir})\nMods: {string.Join(", ", _payload.ModsFor(edition))}\n\n" + text;
        var intro = "Install copies the package into your The Sims folder, merging with the existing folders. " +
                    "Any Maxis file it replaces is backed up first, and Uninstall puts everything back.\n\n";
        await ShowDialogAsync("More Information", intro + text, "Close");
    }

    private void OnWebsite(object? sender, RoutedEventArgs e) => OpenUrl(WebsiteUrl);

    private async void OnMaxis(object? sender, RoutedEventArgs e) => await new AboutWindow().ShowDialog(this);

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) {  }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private static string ActionLabel(InstallAction action) => action switch
    {
        InstallAction.Upgrade => "Upgrade",
        InstallAction.Reinstall => "Reinstall",
        InstallAction.Downgrade => "Downgrade",
        _ => "Install",
    };

    private static InstallState? SafeReadState(string dir)
    {
        try { return ModInstaller.ReadState(dir); }
        catch (Exception) { return null; }
    }

    private async Task<bool> ChooseGameFolderAsync()
    {
        var installs = await Task.Run(() => SimsLocator.AllInstalls().ToList());
        if (_simsDir is not null && !installs.Contains(_simsDir, StringComparer.OrdinalIgnoreCase) && Directory.Exists(_simsDir))
            installs.Insert(0, _simsDir);

        var choices = installs.Select(dir =>
        {
            var title = GameEdition.DisplayName(GameEdition.Detect(dir));
            var state = SafeReadState(dir);
            if (state is not null) title += $"  -  Seasons {state.Version} installed";
            return (title, dir);
        }).ToList();

        var text = installs.Count == 0
            ? "Setup could not find The Sims automatically.\nClick Browse to choose your game folder (the one containing Sims.exe)."
            : "Where should Seasons be installed? Choose a copy of The Sims, or Browse for another folder.";

        var (choice, button) = await ShowChoiceAsync("Choose The Sims", text, choices, _simsDir, "Browse...", "Cancel");
        if (choice >= 0)
        {
            _simsDir = installs[choice];
            RefreshState();
            return true;
        }
        return button == 0 && await PickFolderAsync();
    }

    private async Task<bool> PickFolderAsync()
    {
        IStorageFolder? start = null;
        if (_simsDir is not null)
            start = await StorageProvider.TryGetFolderFromPathAsync(_simsDir);

        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose your The Sims folder (contains Sims.exe)",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        var path = result.Count > 0 ? result[0].TryGetLocalPath() : null;
        if (path is null) return false;

        _simsDir = path;
        RefreshState();
        return true;
    }

    private async Task<(bool Proceed, string Summary)> BackupSavesAsync(string game, string reason)
    {
        _busy = true;
        RefreshState();
        BusyTitle.Text = "Backing up saved games...";
        StatusText.Text = "";
        Progress.Value = 0;
        BusyOverlay.IsVisible = true;
        var progress = new Progress<InstallProgress>(p =>
        {
            Progress.Value = p.Fraction;
            StatusText.Text = p.Message;
        });
        try
        {
            var dir = await Task.Run(() => SaveBackup.Run(game, reason, progress, CancellationToken.None));
            return (true, dir is null
                ? "No saved games were found to back up.\n\n"
                : "Your saved games were backed up to:\n" + dir + "\n\n");
        }
        catch (Exception ex)
        {
            BusyOverlay.IsVisible = false;
            var go = await ConfirmAsync("Backup failed",
                "Your saved games could not be backed up:\n" + ex.Message + "\n\nContinue WITHOUT a backup?");
            return (go, "Saved games were NOT backed up.\n\n");
        }
        finally
        {
            BusyOverlay.IsVisible = false;
            _busy = false;
            RefreshState();
        }
    }

    private async Task RunBusyAsync(Func<IProgress<InstallProgress>, object?> work,
        string doneTitle, string doneText, string failTitle)
    {
        _busy = true;
        RefreshState();
        BusyTitle.Text = doneTitle.StartsWith("Uninstall") ? "Uninstalling..." : "Installing...";
        StatusText.Text = "";
        BusyOverlay.IsVisible = true;
        Progress.Value = 0;
        var progress = new Progress<InstallProgress>(p =>
        {
            Progress.Value = p.Fraction;
            StatusText.Text = p.Message;
        });

        Exception? failure = null;
        try { await Task.Run(() => work(progress)); }
        catch (Exception ex) { failure = ex; }

        _busy = false;
        BusyOverlay.IsVisible = false;
        RefreshState();

        if (failure is null)
            await AlertAsync(doneTitle, doneText);
        else if (failure is UnauthorizedAccessException)
            await AlertAsync(failTitle, failure.Message + "\n\nTry running setup as administrator.");
        else
            await AlertAsync(failTitle, failure.Message +
                (failure is IOException ? "\n\nIf The Sims or another program is using these files, close it and try again." : "") +
                "\n\nNothing was changed: every file was put back as it was.");
    }

    private Task RunBusyAsync(Action<IProgress<InstallProgress>> work, string doneTitle, string doneText, string failTitle) =>
        RunBusyAsync(p => { work(p); return null; }, doneTitle, doneText, failTitle);

    private Task AlertAsync(string title, string text) => ShowDialogAsync(title, text, "OK");

    private async Task<bool> ConfirmAsync(string title, string text) =>
        await ShowDialogAsync(title, text, "Yes", "No") == 0;

    private async Task<int> ShowDialogAsync(string title, string text, params string[] buttons) =>
        (await ShowChoiceAsync(title, text, [], null, buttons)).Button;

    private Task<(int Choice, int Button)> ShowChoiceAsync(string title, string text,
        IReadOnlyList<(string Title, string Detail)> choices, string? highlight, params string[] buttons)
    {
        var tcs = new TaskCompletionSource<(int, int)>();
        DialogTitle.Text = title;
        DialogText.Text = text;
        DialogChoices.Children.Clear();
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var selected = highlight is not null && string.Equals(choices[i].Detail, highlight, StringComparison.OrdinalIgnoreCase);
            var item = new Button
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Thickness(12, 8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = Avalonia.Media.Brush.Parse(selected ? "#14307A" : "#8FA8D0"),
                Background = Avalonia.Media.Brush.Parse("#FFFFFF"),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = choices[i].Title, FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 14, Foreground = Avalonia.Media.Brush.Parse("#000086") },
                        new TextBlock { Text = choices[i].Detail, FontSize = 11, Foreground = Avalonia.Media.Brush.Parse("#33405C"), TextTrimming = Avalonia.Media.TextTrimming.PrefixCharacterEllipsis },
                    },
                },
            };
            item.Click += (_, _) =>
            {
                Overlay.IsVisible = false;
                tcs.TrySetResult((index, -1));
            };
            DialogChoices.Children.Add(item);
        }
        DialogButtons.Children.Clear();
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = new Button { Content = buttons[i] };
            if (this.TryFindResource("PillButton", out var theme) && theme is Avalonia.Styling.ControlTheme ct)
                button.Theme = ct;
            button.Click += (_, _) =>
            {
                Overlay.IsVisible = false;
                tcs.TrySetResult((-1, index));
            };
            DialogButtons.Children.Add(button);
        }
        Overlay.IsVisible = true;
        ((DialogChoices.Children.FirstOrDefault() ?? DialogButtons.Children[0]) as Button)?.Focus();
        return tcs.Task;
    }
}
