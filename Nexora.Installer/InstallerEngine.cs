using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Nexora.Installer;

/// <summary>
/// Installs the embedded, pre-published Nexora application in the current user's profile.
/// This class deliberately never installs drivers, elevates privileges, executes arbitrary
/// commands from the payload, or deletes credential storage.
/// </summary>
internal static class InstallerEngine
{
    private const string PayloadResource = "NexoraPayload.zip";
    private const string PayloadManifestName = "NexoraPayload.manifest.json";
    private const string InstalledStateName = ".nexora-install-state.json";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nexora";

    private sealed class PayloadManifest
    {
        public PayloadManifest() { }
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class InstalledState
    {
        public InstalledState() { }
        public string Version { get; set; } = "1.1";
        public List<string> Files { get; set; } = [];
        public List<string> Shortcuts { get; set; } = [];
    }

    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Nexora");

    public static bool HasEmbeddedPayload() =>
        typeof(InstallerEngine).Assembly.GetManifestResourceInfo(PayloadResource) != null;

    public static bool IsUsbIpCliPresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "USBip", "usbip.exe")
        ];
        if (candidates.Any(File.Exists)) return true;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try { if (File.Exists(Path.Combine(directory.Trim('"'), "usbip.exe"))) return true; }
            catch { /* Invalid PATH component */ }
        }
        return false;
    }

    public static string? GetInstalledDirectory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath);
        string? path = key?.GetValue("InstallLocation") as string;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, InstalledStateName))
            ? path
            : null;
    }

    public static string ValidateDestination(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows requis.");
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Windows x64 requis.");
        if (string.IsNullOrWhiteSpace(directory)) throw new IOException("Choisissez un dossier d'installation.");
        if (!Path.IsPathFullyQualified(directory))
            throw new IOException("Le dossier doit être un chemin absolu.");
        if (directory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Les dossiers réseau ne sont pas pris en charge.");

        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string root = Path.GetPathRoot(target) ?? "";
        if (string.Equals(target, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("La racine d'un disque ne peut pas être utilisée.");

        string win = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string pf = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        string pf86 = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        if (IsSameOrBelow(target, win) || IsSameOrBelow(target, pf) || IsSameOrBelow(target, pf86))
            throw new IOException("Choisissez un dossier utilisateur, et non un dossier système.");

        // Do not overwrite a directory owned by another application or an unknown prior installer.
        if (Directory.Exists(target)
            && Directory.EnumerateFileSystemEntries(target).Any()
            && !File.Exists(Path.Combine(target, InstalledStateName)))
            throw new IOException("Le dossier contient déjà des fichiers inconnus. Choisissez un nouveau dossier Nexora.");
        return target;
    }

    private static bool IsSameOrBelow(string path, string parent) =>
        path.Equals(Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string SafeRelative(string relative)
    {
        relative = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.Contains(':'))
            throw new InvalidDataException("Nom de fichier invalide dans le paquet.");
        string[] segments = relative.Split('/');
        if (segments.Any(s => s is "" or "." or ".."))
            throw new InvalidDataException("Chemin interdit dans le paquet : " + relative);
        return relative;
    }

    private static string SafeTarget(string root, string relative)
    {
        relative = SafeRelative(relative);
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Un chemin tente de sortir du dossier cible.");
        return target;
    }

    public static string Install(string folder, bool desktopShortcut, bool startupShortcut,
        IProgress<int> progress, Action<string> report)
    {
        string target = ValidateDestination(folder);
        if (Process.GetProcessesByName("Nexora").Length != 0)
            throw new IOException("Quittez Nexora depuis la zone de notification avant l'installation.");

        string temp = Path.Combine(Path.GetTempPath(), "NexoraInstaller-" + Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(temp, "staging");
        string backups = Path.Combine(temp, "backups");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backups);
        var restored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newlyCreated = new List<string>();
        var newShortcuts = new List<string>();
        string stateFile = Path.Combine(target, InstalledStateName);
        string? formerState = File.Exists(stateFile) ? File.ReadAllText(stateFile) : null;

        try
        {
            report("Lecture de l'archive intégrée…");
            progress.Report(3);
            using var payloadStream = typeof(InstallerEngine).Assembly.GetManifestResourceStream(PayloadResource)
                ?? throw new InvalidDataException("Archive NexoraPayload.zip absente. Recréez le setup.");
            using var zip = new ZipArchive(payloadStream, ZipArchiveMode.Read);
            var manifestEntry = zip.GetEntry(PayloadManifestName)
                ?? throw new InvalidDataException("Manifeste du paquet manquant.");
            PayloadManifest manifest;
            using (var reader = new StreamReader(manifestEntry.Open()))
            {
                manifest = JsonSerializer.Deserialize<PayloadManifest>(reader.ReadToEnd())
                    ?? throw new InvalidDataException("Manifeste invalide.");
            }
            if (manifest.Files.Count == 0 || !manifest.Files.ContainsKey("Nexora.exe"))
                throw new InvalidDataException("Le paquet Nexora est incomplet.");
            if (zip.Entries.Count != manifest.Files.Count + 1)
                throw new InvalidDataException("Le nombre de fichiers ne correspond pas au manifeste.");
            if (manifest.Files.Count > 10000)
                throw new InvalidDataException("Paquet anormalement volumineux.");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int extracted = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == PayloadManifestName) continue;
                string relative = SafeRelative(entry.FullName);
                if (!names.Add(relative) || !manifest.Files.TryGetValue(relative, out var expected))
                    throw new InvalidDataException("Fichier non déclaré ou dupliqué : " + relative);
                if (entry.Length > 1024L * 1024 * 1024)
                    throw new InvalidDataException("Fichier trop volumineux : " + relative);
                string path = SafeTarget(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var input = entry.Open())
                using (var output = File.Create(path)) input.CopyTo(output);
                using var file = File.OpenRead(path);
                var actual = Convert.ToHexString(SHA256.HashData(file));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Intégrité SHA-256 non valide : " + relative);
                extracted++;
                progress.Report(5 + (int)(45.0 * extracted / manifest.Files.Count));
            }
            if (extracted != manifest.Files.Count)
                throw new InvalidDataException("Certains fichiers sont absents du paquet.");
            report("Fichiers vérifiés avec SHA-256.");

            // Do not remove existing content, including the application's credential folders.
            Directory.CreateDirectory(target);
            var oldState = formerState is null
                ? null
                : JsonSerializer.Deserialize<InstalledState>(formerState);
            var installed = new InstalledState();
            int copied = 0;
            foreach (string relative in manifest.Files.Keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            {
                string source = SafeTarget(staging, relative);
                string dest = SafeTarget(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest))
                {
                    string backup = SafeTarget(backups, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(dest, backup, true);
                    restored[dest] = backup;
                }
                else newlyCreated.Add(dest);
                File.Copy(source, dest, true);
                installed.Files.Add(relative);
                copied++;
                progress.Report(50 + (int)(40.0 * copied / manifest.Files.Count));
            }
            report("Création des raccourcis Windows…");
            var exe = Path.Combine(target, "Nexora.exe");
            string startMenuShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Nexora", "Nexora.lnk");
            TryCreateShortcut(startMenuShortcut, exe, installed, newShortcuts, report);
            if (desktopShortcut)
                TryCreateShortcut(Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory), "Nexora.lnk"), exe,
                    installed, newShortcuts, report);
            if (startupShortcut)
                TryCreateShortcut(Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.Startup), "Nexora.lnk"), exe,
                    installed, newShortcuts, report);
            else
                // Only remove a startup shortcut if a previous Nexora Installer created it.
                if (oldState?.Shortcuts.Contains(Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.Startup), "Nexora.lnk"),
                    StringComparer.OrdinalIgnoreCase) == true)
                {
                    var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Nexora.lnk");
                    if (File.Exists(startup)) File.Delete(startup);
                }

            // Preserve previously created shortcuts when the options are unchanged.
            foreach (string previous in oldState?.Shortcuts ?? [])
            {
                if (File.Exists(previous) && IsExpectedShortcut(previous)
                    && !installed.Shortcuts.Contains(previous, StringComparer.OrdinalIgnoreCase))
                    installed.Shortcuts.Add(previous);
            }

            string stateJson = JsonSerializer.Serialize(installed,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(stateFile, stateJson);
            progress.Report(94);

            // A cached installer outside the application directory provides Add/Remove Programs
            // with a self-contained uninstaller, without locking app files during removal.
            string setupCache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NexoraInstaller", "Nexora-Setup.exe");
            string runningExe = Environment.ProcessPath ?? "";
            if (File.Exists(runningExe)
                && runningExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(runningExe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(setupCache)!);
                if (!runningExe.Equals(setupCache, StringComparison.OrdinalIgnoreCase))
                    File.Copy(runningExe, setupCache, true);
                RegisterUninstall(target, setupCache);
            }
            else report("Mode développement : entrée de désinstallation Windows non créée.");

            progress.Report(100);
            report("Nexora 1.1 installé avec succès.");
            return target;
        }
        catch
        {
            // Best-effort rollback of application binaries. No credential folder is touched.
            foreach (var entry in restored)
            {
                try { File.Copy(entry.Value, entry.Key, true); } catch { /* Keep original exception */ }
            }
            foreach (var file in newlyCreated)
            {
                try { File.Delete(file); } catch { /* Keep original exception */ }
            }
            foreach (var shortcut in newShortcuts)
            {
                try { File.Delete(shortcut); } catch { /* Keep original exception */ }
            }
            try
            {
                if (formerState != null) File.WriteAllText(stateFile, formerState);
                else if (File.Exists(stateFile)) File.Delete(stateFile);
            }
            catch { /* Keep original exception */ }
            throw;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* Temporary file cleanup may be deferred */ }
        }
    }

    private static void TryCreateShortcut(string path, string exe, InstalledState state,
        List<string> created, Action<string> report)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool existed = File.Exists(path);
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new IOException("WScript.Shell indisponible.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.Description = "Nexora 1.1 — Clé de sécurité virtuelle";
            link.IconLocation = exe + ",0";
            link.Save();
            state.Shortcuts.Add(path);
            if (!existed) created.Add(path);
        }
        catch (Exception e)
        {
            report("Raccourci facultatif non créé : " + e.Message);
        }
    }

    private static bool IsExpectedShortcut(string path)
    {
        if (!Path.GetFileName(path).Equals("Nexora.lnk", StringComparison.OrdinalIgnoreCase))
            return false;
        string folder = Path.GetDirectoryName(path) ?? "";
        return folder.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Nexora"),
                   StringComparison.OrdinalIgnoreCase)
               || folder.Equals(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                   StringComparison.OrdinalIgnoreCase)
               || folder.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void RegisterUninstall(string location, string cachedInstaller)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath)
            ?? throw new IOException("Impossible de créer l'entrée de désinstallation.");
        key.SetValue("DisplayName", "Nexora");
        key.SetValue("DisplayVersion", "1.1");
        key.SetValue("Publisher", "Nexora");
        key.SetValue("InstallLocation", location);
        key.SetValue("DisplayIcon", Path.Combine(location, "Nexora.exe"));
        key.SetValue("UninstallString", "\"" + cachedInstaller + "\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public static void Uninstall()
    {
        string dir = GetInstalledDirectory()
            ?? throw new FileNotFoundException("Installation Nexora introuvable.");
        if (Process.GetProcessesByName("Nexora").Length > 0)
            throw new IOException("Quittez Nexora avant la désinstallation.");
        string stateFile = Path.Combine(dir, InstalledStateName);
        var state = JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(stateFile))
            ?? throw new InvalidDataException("Manifeste d'installation illisible.");

        // Only remove files explicitly recorded at installation time.
        foreach (string relative in state.Files)
        {
            string file = SafeTarget(dir, relative);
            if (File.Exists(file)) File.Delete(file);
        }
        foreach (string shortcut in state.Shortcuts)
        {
            if (IsExpectedShortcut(shortcut) && File.Exists(shortcut)) File.Delete(shortcut);
        }
        File.Delete(stateFile);
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath, writable: true))
        {
            if (key is null) throw new IOException("Enregistrement de l'installation introuvable.");
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        // Intentionally do not remove directories: they can contain credential data.
    }
}
