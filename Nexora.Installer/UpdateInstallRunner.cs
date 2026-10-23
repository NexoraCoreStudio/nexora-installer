using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Nexora.Installer;

/// <summary>
/// Offline, non-elevated update mode of the existing trusted installer.
/// Called only after the application downloads and verifies the GitHub SHA-256.
/// </summary>
internal static class UpdateInstallRunner
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nexora";
    private const string InstallState = ".nexora-install-state.json";

    public static int Run(string[] args)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nexora", "Updates");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "last-install.log");
        void Log(string message)
        {
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows requis.");
            var rawPid = args.Single(a => a.StartsWith("--parent-pid=", StringComparison.Ordinal));
            if (!int.TryParse(rawPid["--parent-pid=".Length..], out var parentPid) || parentPid <= 0)
                throw new ArgumentException("PID du processus Nexora invalide.");
            var rawTarget = args.Single(a => a.StartsWith("--target=", StringComparison.Ordinal));
            var target = InstallerEngine.ValidateDestination(rawTarget["--target=".Length..]);
            var registered = InstallerEngine.GetInstalledDirectory();
            if (registered == null || !Path.GetFullPath(registered).Equals(target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Installation Nexora enregistrée introuvable ou dossier différent.");
            RejectReparsePoints(target);
            using var mutex = new Mutex(false, @"Local\NexoraInstallerUpdate13");
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException("Une autre mise à jour de Nexora est en cours.");
            try
            {
                // The old process must have exited before overwriting Nexora.exe or its DLLs.
                try
                {
                    using var process = Process.GetProcessById(parentPid);
                    if (!process.ProcessName.Equals("Nexora", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Le processus demandeur ne correspond pas à Nexora.");
                    if (!process.WaitForExit(120_000))
                        throw new TimeoutException("Nexora ne s'est pas arrêté dans le délai de sécurité.");
                }
                catch (ArgumentException) { /* The originating process has already exited. */ }
                if (Process.GetProcessesByName("Nexora").Length > 0)
                    throw new IOException("Nexora est toujours lancé. Installation refusée.");

                var (desktop, startup) = GetInstalledShortcutPreferences(target);
                Log("Début mise à jour Nexora 1.3.5 ; aucune action sur USB/IP ou le coffre.");
                string installed = InstallerEngine.Install(target, desktop, startup,
                    new Progress<int>(), Log);
                UpdateVersionMetadata(installed);
                Log("Installation terminée et version 1.3.5 enregistrée.");
                Process.Start(new ProcessStartInfo(Path.Combine(installed, "Nexora.exe"))
                {
                    UseShellExecute = true,
                    WorkingDirectory = installed
                });
                return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Log("ECHEC : " + ex);
            // Setup has no console: expose error immediately as well as in the persistent log.
            NativeErrorDialog.Show("Mise à jour Nexora impossible", ex.Message +
                "\n\nJournal : " + logPath);
            return 1;
        }
    }

    private static (bool Desktop, bool Startup) GetInstalledShortcutPreferences(string target)
    {
        var path = Path.Combine(target, InstallState);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var existing = doc.RootElement.GetProperty("Shortcuts").EnumerateArray()
            .Select(x => x.GetString()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Nexora.lnk");
        string startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Nexora.lnk");
        return (existing.Contains(desktop) && File.Exists(desktop),
                existing.Contains(startup) && File.Exists(startup));
    }

    private static void UpdateVersionMetadata(string folder)
    {
        // The legacy InstallerEngine currently serializes v1.2 by default. Update its
        // registry/state metadata after a successful 1.3 in-place installation.
        var path = Path.Combine(folder, InstallState);
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("État de l'installation illisible.");
        using var versionDoc = JsonDocument.Parse("\"1.3.5\"");
        doc["Version"] = versionDoc.RootElement.Clone();
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true)
            ?? throw new IOException("Enregistrement de désinstallation absent.");
        key.SetValue("DisplayVersion", "1.3.5");
    }

    public static void RepairInteractiveInstallVersion()
    {
        // The existing wizard delegates copying to the 1.2 InstallerEngine. After a
        // successful 1.3 wizard installation, correct its legacy metadata as well.
        var folder = InstallerEngine.GetInstalledDirectory();
        if (folder is null) return;
        var executable = Path.Combine(folder, "Nexora.exe");
        if (!File.Exists(executable)) return;
        var version = FileVersionInfo.GetVersionInfo(executable);
        if (version.FileMajorPart == 1 && version.FileMinorPart == 3 && version.FileBuildPart == 5)
            UpdateVersionMetadata(folder);
    }

    private static void RejectReparsePoints(string root)
    {
        // Prevent following symlinks/junctions from the destination or within binaries.
        string current = Path.GetFullPath(root);
        while (Directory.Exists(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Dossier d'installation redirigé : mise à jour annulée.");
            var parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current) break;
            current = parent;
        }
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(currentDirectory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Lien symbolique détecté dans l'installation Nexora.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static class NativeErrorDialog
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
        public static void Show(string title, string content) => MessageBoxW(0, content, title, 0x10);
    }
}
