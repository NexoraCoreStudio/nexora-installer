using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Nexora.Installer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool uninstall = Array.Exists(desktop.Args ?? Array.Empty<string>(),
                item => item.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
            desktop.MainWindow = new MainWindow(uninstall);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
