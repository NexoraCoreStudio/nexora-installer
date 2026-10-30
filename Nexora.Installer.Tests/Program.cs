using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexora.Installer;

string root = Path.Combine(Path.GetTempPath(), "Nexora-Installer-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    Test("Paquet complet et empreintes correctes", () =>
    {
        using var zip = Package();
        var info = InstallerPreflight.Inspect(zip);
        Require(info.Files.Count == 4 && info.Bytes > 0, "Paquet incomplet");
    });
    Test("Paquet corrompu refuse avant installation", () =>
    {
        using var zip = Package(corrupt: true);
        Reject(() => InstallerPreflight.Inspect(zip));
    });
    Test("Runtime autonome manquant refuse", () =>
    {
        var files = Files(); files.Remove("coreclr.dll");
        using var zip = Package(files);
        Reject(() => InstallerPreflight.Inspect(zip));
    });
    Test("Manifeste absent refuse", () =>
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true)) archive.CreateEntry("Nexora.exe");
        zip.Position = 0;
        Reject(() => InstallerPreflight.Inspect(zip));
    });
    Test("Traversals, noms Windows et donnees utilisateur refuses", () =>
    {
        foreach (var path in new[] { "../outside.dll", "C:/outside.dll", "folder/../x", "NUL.dll", "foo./x", "foo /x", "Nexora.vault", "keys/private.bin", "certs/client.pfx", ".env", ".nexora-install-state.json" })
            Reject(() => InstallerPreflight.SafeRelative(path));
    });
    Test("Doublon de nom insensible a la casse refuse", () =>
    {
        using var zip = Package(duplicate: true);
        Reject(() => InstallerPreflight.Inspect(zip));
    });
    Test("Destination relative, systeme et racine refusees", () =>
    {
        foreach (var path in new[] { "relative", Path.GetPathRoot(root)!, Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"\\server\share\Nexora" })
            Reject(() => InstallerEngine.ValidateDestination(path));
    });
    Test("Dossier inconnu non remplace", () =>
    {
        var dir = NewDirectory(); File.WriteAllText(Path.Combine(dir, "personal.txt"), "preserve");
        Reject(() => InstallerEngine.ValidateDestination(dir));
        Require(File.ReadAllText(Path.Combine(dir, "personal.txt")) == "preserve", "Donnee modifiee");
    });
    Test("Etat corrompu et etat ciblant le coffre refuses", () =>
    {
        var dir = NewDirectory();
        File.WriteAllText(Path.Combine(dir, InstallerPreflight.StateName), "{}");
        Reject(() => InstallerEngine.ValidateDestination(dir));
        File.WriteAllText(Path.Combine(dir, InstallerPreflight.StateName), JsonSerializer.Serialize(new { Files = new[] { "Nexora.exe", "Nexora.vault" }, Shortcuts = Array.Empty<string>() }));
        Reject(() => InstallerEngine.ValidateDestination(dir));
    });
    Test("Installation neuve isolee et verification des fichiers", () =>
    {
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Install(dir);
        Require(File.Exists(Path.Combine(dir, "Nexora.exe")), "Executable absent");
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, InstallerPreflight.StateName)));
        Require(state.RootElement.GetProperty("Version").GetString() == InstallerEngine.DisplayVersion, "Version incorrecte");
        Require(state.RootElement.GetProperty("Shortcuts").GetArrayLength() == 0, "Integration Windows effectuee pendant les tests");
    });
    Test("Mise a jour conserve coffre et fichiers non geres", () =>
    {
        var dir = NewDirectory(); Install(dir);
        File.WriteAllText(Path.Combine(dir, "Nexora.vault"), "secret-test");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "notes-test");
        Install(dir, revision: 2);
        Require(File.ReadAllText(Path.Combine(dir, "Nexora.vault")) == "secret-test", "Coffre ecrase");
        Require(File.ReadAllText(Path.Combine(dir, "notes.txt")) == "notes-test", "Fichier inconnu modifie");
        Require(File.ReadAllText(Path.Combine(dir, "Nexora.dll")) == "revision-2", "Mise a jour absente");
    });
    Test("Verrou de fichier detecte avant copie", () =>
    {
        var dir = NewDirectory(); Install(dir);
        using var held = new FileStream(Path.Combine(dir, "Nexora.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
        Reject(() => Install(dir, revision: 2));
    });
    Test("Collision avec fichier non gere refusee", () =>
    {
        var dir = NewDirectory(); Install(dir);
        File.WriteAllText(Path.Combine(dir, "extra.dll"), "personal");
        var files = Files(); files.Add("extra.dll", Encoding.UTF8.GetBytes("new"));
        using var zip = Package(files);
        Reject(() => InstallerEngine.InstallFromStream(dir, false, false, new InlineProgress(), _ => { }, zip, false));
        Require(File.ReadAllText(Path.Combine(dir, "extra.dll")) == "personal", "Fichier non gere remplace");
    });
    Test("Rollback apres copie et ecriture de l'etat", () =>
    {
        var dir = NewDirectory(); Install(dir);
        byte[] before = File.ReadAllBytes(Path.Combine(dir, "Nexora.dll"));
        byte[] state = File.ReadAllBytes(Path.Combine(dir, InstallerPreflight.StateName));
        using var zip = Package(Files(2));
        Reject(() => InstallerEngine.InstallFromStream(dir, false, false, new InlineProgress(94), _ => { }, zip, false));
        Require(before.SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "Nexora.dll"))), "Rollback des binaires echoue");
        Require(state.SequenceEqual(File.ReadAllBytes(Path.Combine(dir, InstallerPreflight.StateName))), "Rollback de l'etat echoue");
    });
    Test("Echec neuf ne laisse pas de fichiers applicatifs", () =>
    {
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        using var zip = Package();
        Reject(() => InstallerEngine.InstallFromStream(dir, false, false, new InlineProgress(94), _ => { }, zip, false));
        Require(!File.Exists(Path.Combine(dir, "Nexora.exe")) && !File.Exists(Path.Combine(dir, InstallerPreflight.StateName)), "Fichiers partiellement installes");
    });
    Test("Espace insuffisant refuse avec marge de securite", () =>
    {
        Reject(() => InstallerPreflight.EnsureCapacity(64 * 1024 * 1024, 1, "test"));
        InstallerPreflight.EnsureCapacity(65 * 1024 * 1024, 1024 * 1024, "test");
    });
    Test("Jonction de destination et sous-dossier redirige refuses", () =>
    {
        string target = NewDirectory();
        string link = Path.Combine(root, "junction-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["NEXORA_TEST_LINK"] = link;
        start.Environment["NEXORA_TEST_TARGET"] = target;
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:NEXORA_TEST_LINK -Target $env:NEXORA_TEST_TARGET | Out-Null");
        using var process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Require(process.ExitCode == 0, "Creation du fixture de jonction impossible : " + error);
        try
        {
            Reject(() => InstallerEngine.ValidateDestination(link));
            Reject(() => InstallerPreflight.Target(root, Path.GetFileName(link) + "/Nexora.exe"));
        }
        finally { Directory.Delete(link); }
    });
    Console.WriteLine($"SUCCES : {passed} tests installateur. Aucun raccourci, registre ou pilote modifie.");
    return 0;
}
finally { Directory.Delete(root, true); }

string NewDirectory() { string dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
void Test(string name, Action action) { action(); passed++; Console.WriteLine("[OK] " + name); }
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or KeyNotFoundException or JsonException or UnauthorizedAccessException) { return; }
    throw new Exception("Operation dangereuse acceptee");
}
static Dictionary<string, byte[]> Files(int revision = 1) => new()
{
    ["Nexora.exe"] = new byte[] { 77, 90, 1 }, ["Nexora.dll"] = Encoding.UTF8.GetBytes("revision-" + revision),
    ["Nexora.runtimeconfig.json"] = Encoding.UTF8.GetBytes("{}"), ["coreclr.dll"] = new byte[] { 77, 90, 2 }
};
static MemoryStream Package(Dictionary<string, byte[]>? files = null, bool corrupt = false, bool duplicate = false)
{
    files ??= Files();
    var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
    {
        foreach (var file in files)
        {
            using var data = zip.CreateEntry(file.Key).Open(); data.Write(file.Value);
        }
        if (duplicate) { using var data = zip.CreateEntry("NEXORA.EXE").Open(); data.Write(new byte[] { 1 }); }
        var hashes = files.ToDictionary(f => f.Key, f => corrupt ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(f.Value)));
        using var manifest = zip.CreateEntry(InstallerPreflight.ManifestName).Open();
        JsonSerializer.Serialize(manifest, new { Files = hashes });
    }
    stream.Position = 0; return stream;
}
static void Install(string path, int revision = 1)
{
    using var zip = Package(Files(revision));
    InstallerEngine.InstallFromStream(path, false, false, new InlineProgress(), _ => { }, zip, false);
}
sealed class InlineProgress(int failAt = int.MaxValue) : IProgress<int>
{
    public void Report(int value) { if (value >= failAt) throw new IOException("Panne simulee pendant l'installation"); }
}
