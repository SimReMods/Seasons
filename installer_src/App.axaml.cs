using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SeasonsInstaller;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            var args = desktop.Args ?? [];
            var i = Array.IndexOf(args, "--screenshot");
            if (i >= 0 && i + 1 < args.Length)
                window.Opened += async (_, _) =>
                {
                    await window.SaveScreenshotAsync(args[i + 1]);
                    desktop.Shutdown();
                };
            else
            {
                window.Opened += (_, _) => Services.Sounds.Play(Services.Sounds.Startup);

                Avalonia.Controls.Button.ClickEvent.AddClassHandler<Avalonia.Controls.Button>(
                    (_, _) => Services.Sounds.Play(Services.Sounds.Click));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
