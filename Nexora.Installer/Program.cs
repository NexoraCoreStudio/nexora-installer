using System.Diagnostics;
using Avalonia;

namespace Nexora.Installer;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Read-only packaging smoke test, also available on the exported single-file Setup.
        if (args.Contains("--verify-package", StringComparer.Ordinal))
        {
            try
            {
                using var stream = InstallerEngine.OpenPayload();
                var package = InstallerPreflight.Inspect(stream);
                Console.WriteLine($"OK Nexora {InstallerEngine.DisplayVersion}: {package.Files.Count} fichiers, {package.Bytes} octets, SHA-256 vérifiés.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--apply-update", StringComparer.Ordinal))
        {
            Environment.ExitCode = UpdateInstallRunner.Run(args);
            return;
        }

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            WaitForNexoraToQuit(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void WaitForNexoraToQuit(string[] args)
    {
        string? value = args.FirstOrDefault(a =>
            a.StartsWith("--wait-parent-pid=", StringComparison.OrdinalIgnoreCase));
        if (value is null || !int.TryParse(value["--wait-parent-pid=".Length..], out int pid) || pid <= 0)
            return; // Uninstall launched normally through Windows Apps > Installed apps.

        try
        {
            using var parent = Process.GetProcessById(pid);
            if (parent.ProcessName.Equals("Nexora", StringComparison.OrdinalIgnoreCase))
                parent.WaitForExit(90_000);
        }
        catch (ArgumentException)
        {
            // Nexora may have exited before the installer started.
        }
    }

    private static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
