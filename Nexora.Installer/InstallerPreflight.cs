using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexora.Installer;

internal sealed record PackageInfo(IReadOnlyDictionary<string, string> Files, long Bytes);

/// <summary>Shared by the diagnostic screen and the installation worker.</summary>
internal static class InstallerPreflight
{
    internal const string ManifestName = "NexoraPayload.manifest.json";
    internal const string StateName = ".nexora-install-state.json";
    internal const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    private const long ReserveBytes = 64L * 1024 * 1024;

    internal static string SafeRelative(string path)
    {
        path = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') || path.Length > 220)
            throw new InvalidDataException("Chemin de fichier interdit dans le paquet.");
        foreach (string segment in path.Split('/'))
        {
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Nom Windows invalide dans le paquet : " + path);
        }
        string name = Path.GetFileName(path);
        if (name.Equals("Nexora.vault", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(StateName, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            new[] { ".vault", ".nxbackup", ".pfx", ".p12", ".key" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) ||
            path.Split('/').Any(p => new[] { "keys", "credentials", "certs" }.Contains(p, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Le paquet tente de remplacer des données utilisateur : " + path);
        return path;
    }

    internal static PackageInfo Inspect(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("Manifeste du paquet absent.");
        if (manifestEntry.Length > 2 * 1024 * 1024) throw new InvalidDataException("Manifeste trop volumineux.");
        using var input = manifestEntry.Open();
        using var json = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 8 });
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in json.RootElement.GetProperty("Files").EnumerateObject())
        {
            string path = SafeRelative(entry.Name);
            string hash = entry.Value.GetString() ?? "";
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") || !files.TryAdd(path, hash))
                throw new InvalidDataException("Empreinte invalide ou fichier dupliqué : " + path);
        }
        if (files.Count is < 1 or > 10000 || zip.Entries.Count != files.Count + 1)
            throw new InvalidDataException("Nombre de fichiers du paquet invalide.");
        foreach (string required in new[] { "Nexora.exe", "Nexora.dll", "Nexora.runtimeconfig.json", "coreclr.dll" })
            if (!files.ContainsKey(required)) throw new InvalidDataException("Paquet autonome incomplet : " + required);

        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[] buffer = new byte[128 * 1024];
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == ManifestName) continue;
            string path = SafeRelative(entry.FullName);
            if (!seen.Add(path) || !files.TryGetValue(path, out string? expected))
                throw new InvalidDataException("Fichier absent du manifeste ou dupliqué : " + path);
            total = checked(total + entry.Length);
            if (entry.Length <= 0 || entry.Length > 1024L * 1024 * 1024 || total > MaximumBytes)
                throw new InvalidDataException("Taille du paquet non prise en charge.");
            using var data = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            int read;
            while ((read = data.Read(buffer)) > 0)
            {
                received += read;
                if (received > entry.Length) throw new InvalidDataException("Taille décompressée incorrecte.");
                hash.AppendData(buffer, 0, read);
            }
            if (received != entry.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Vérification SHA-256 échouée : " + path);
        }
        return new(files, total);
    }

    internal static void RejectRedirectedPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            // GetAttributes also detects file links and broken links, unlike Directory.Exists alone.
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Lien ou dossier redirigé refusé : " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    internal static string Target(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, SafeRelative(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Chemin hors du dossier d'installation.");
        RejectRedirectedPath(full);
        return full;
    }

    internal static void ValidateExistingState(string root)
    {
        string state = Path.Combine(root, StateName);
        RejectRedirectedPath(state);
        if (!File.Exists(state)) return;
        if (new FileInfo(state).Length > 2 * 1024 * 1024) throw new InvalidDataException("État d'installation trop volumineux.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(state));
        var paths = json.RootElement.GetProperty("Files");
        if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() is < 1 or > 10000)
            throw new InvalidDataException("État d'installation invalide.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.EnumerateArray())
            if (!names.Add(SafeRelative(path.GetString() ?? ""))) throw new InvalidDataException("État d'installation dupliqué.");
        if (!names.Contains("Nexora.exe")) throw new InvalidDataException("État d'installation incomplet.");
        if (json.RootElement.GetProperty("Shortcuts").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Liste de raccourcis invalide.");
    }

    internal static void CheckStorage(string target, PackageInfo package)
    {
        var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string state = Path.Combine(target, StateName);
        if (File.Exists(state))
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(state));
            foreach (var path in json.RootElement.GetProperty("Files").EnumerateArray()) previous.Add(path.GetString()!);
        }
        long backups = 0;
        foreach (var relative in package.Files.Keys)
        {
            string path = Target(target, relative);
            if (Directory.Exists(path)) throw new IOException("Un dossier occupe un chemin de fichier : " + relative);
            if (File.Exists(path))
            {
                if (!previous.Contains(relative)) throw new IOException("Un fichier non géré serait remplacé : " + relative);
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("Fichier en lecture seule : " + relative);
                using var unlocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                backups = checked(backups + unlocked.Length);
            }
        }
        // Sum requirements per volume: extraction and rollback may share the target drive.
        var required = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        Add(target, package.Bytes);
        Add(Path.GetTempPath(), checked(package.Bytes + backups));
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexora", "InstallerCache");
        Add(cache, Environment.ProcessPath is { } exe && File.Exists(exe) ? new FileInfo(exe).Length : 0);
        foreach (var volume in required)
        {
            var drive = new DriveInfo(volume.Key);
            if (!drive.IsReady || drive.DriveType == DriveType.Network) throw new IOException("Volume local indisponible : " + volume.Key);
            EnsureCapacity(drive.AvailableFreeSpace, volume.Value, volume.Key);
        }
        ProbeWrite(target);
        ProbeWrite(Path.GetTempPath());
        ProbeWrite(cache);

        void Add(string path, long bytes)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path))!;
            required[root] = checked(required.GetValueOrDefault(root) + bytes);
        }
    }

    internal static void EnsureCapacity(long available, long needed, string volume)
    {
        if (available < checked(needed + ReserveBytes))
            throw new IOException($"Espace insuffisant sur {volume} : {Math.Ceiling((needed + ReserveBytes) / 1048576d):N0} Mo nécessaires.");
    }

    private static void ProbeWrite(string folder)
    {
        string? current = Path.GetFullPath(folder);
        while (current is not null && !Directory.Exists(current)) current = Path.GetDirectoryName(current);
        if (current is null) throw new IOException("Dossier parent introuvable.");
        RejectRedirectedPath(current);
        using var probe = new FileStream(Path.Combine(current, ".nexora-write-test-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        probe.WriteByte(0);
        probe.Flush(true);
    }
}
