using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexora.Installer;

/// <summary>
/// Diagnostic USB/IP en lecture seule et installation explicite d'un INF + CAT signés.
/// Ne télécharge pas de pilotes et ne modifie jamais la politique de signature Windows.
/// </summary>
internal static class UsbIpDriverSupport
{
    internal sealed record DriverStatus(bool ControllerDetected, bool ControllerWorking,
        bool ClientPresent, string Version, string Signer, string Details);

    public static async Task<DriverStatus> CheckAsync()
    {
        if (!OperatingSystem.IsWindows())
            return new(false, false, false, "", "", "Windows requis.");

        bool cli = InstallerEngine.IsUsbIpCliPresent();
        const string script = """
            $d = Get-PnpDevice -ErrorAction SilentlyContinue |
                 Where-Object { $_.InstanceId -like 'ROOT\USB\*' -and $_.FriendlyName -match 'USBip|VHCI' } | Select-Object -First 1
            if ($null -eq $d) { 'NONE'; exit 0 }
            $v = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_DriverVersion' -ErrorAction SilentlyContinue
            $signed = Get-CimInstance -ClassName Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
                Where-Object { $_.DeviceID -eq $d.InstanceId } | Select-Object -First 1
            [pscustomobject]@{
                Present = $true
                Healthy = ($d.Status -eq 'OK')
                Name = $d.FriendlyName
                Status = $d.Status
                Version = [string]$v.Data
                Signer = [string]$signed.Signer
            } | ConvertTo-Json -Compress
            """;
        try
        {
            string output = (await RunPowerShellAsync(script)).Trim();
            if (output == "NONE")
                return new(false, false, cli, "", "", "Contrôleur USB/IP introuvable.");

            using var json = System.Text.Json.JsonDocument.Parse(output);
            var root = json.RootElement;
            bool healthy = root.GetProperty("Healthy").GetBoolean();
            string name = root.GetProperty("Name").GetString() ?? "Contrôleur USB/IP";
            string status = root.GetProperty("Status").GetString() ?? "Inconnu";
            string version = root.GetProperty("Version").GetString() ?? "";
            string signer = root.GetProperty("Signer").GetString() ?? "";
            return new(true, healthy, cli, version, signer, $"{name} : {status}");
        }
        catch (Exception ex)
        {
            return new(false, false, cli, "", "", "Diagnostic PnP indisponible : " + ex.Message);
        }
    }

    /// <summary>
    /// Valide qu'un paquet local paraît être USB/IP, référence un .cat présent et
    /// dispose d'une signature Authenticode acceptée par Windows. PnPUtil vérifiera
    /// aussi l'association du .cat au contenu INF pendant l'installation.
    /// </summary>
    public static async Task<string> VerifySignedPackageAsync(string infPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows requis.");
        string full = Path.GetFullPath(infPath);
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Choisissez un INF stocké sur le disque local.");
        if (!File.Exists(full) || !Path.GetExtension(full).Equals(".inf", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choisissez un fichier pilote .inf valide.");
        if (new FileInfo(full).Length > 5 * 1024 * 1024)
            throw new IOException("Ce fichier INF est anormalement volumineux.");

        string contents = await File.ReadAllTextAsync(full);
        if (!Regex.IsMatch(contents, @"usbip|vhci", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Le fichier INF ne correspond pas au contrôleur USB/IP attendu.");
        var catEntry = Regex.Matches(contents, @"^\s*CatalogFile(?:\.NTamd64)?\s*=\s*([^;\r\n]+)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Trim().Trim('"'))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (catEntry is null || catEntry.Contains('/') || catEntry.Contains('\\') || catEntry.StartsWith('.'))
            throw new InvalidDataException("Catalogue .cat signé introuvable dans le fichier INF.");
        string cat = Path.Combine(Path.GetDirectoryName(full)!, catEntry);
        if (!File.Exists(cat)) throw new IOException("Fichier de signature absent : " + catEntry);

        string quoted = cat.Replace("'", "''", StringComparison.Ordinal);
        string command = "$s = Get-AuthenticodeSignature -LiteralPath '" + quoted + "'; " +
                         "if ($s.Status -ne 'Valid' -or !$s.SignerCertificate) { Write-Output 'INVALID'; exit 4 }; " +
                         "Write-Output ('VALID|' + $s.SignerCertificate.Subject)";
        string validation = (await RunPowerShellAsync(command)).Trim();
        if (!validation.StartsWith("VALID|", StringComparison.Ordinal))
            throw new SecurityException("Windows ne valide pas la signature du catalogue du pilote.");
        return validation[6..];
    }

    public static async Task<int> InstallInfAsync(string infPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows requis.");
        string pnp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
        var start = new ProcessStartInfo(pnp) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("/add-driver");
        start.ArgumentList.Add(infPath);
        start.ArgumentList.Add("/install");
        using var process = Process.Start(start) ?? throw new IOException("PnPUtil n'a pas démarré.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<string> RunPowerShellAsync(string script)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encoded);
        using var proc = Process.Start(start) ?? throw new IOException("PowerShell indisponible.");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Interrogation Windows trop longue.");
        }
        string output = await stdout;
        string err = await stderr;
        if (proc.ExitCode != 0) throw new IOException("Vérification Windows impossible : " + err.Trim());
        return output;
    }
}
