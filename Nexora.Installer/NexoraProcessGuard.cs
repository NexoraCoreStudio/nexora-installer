using System.ComponentModel;
using System.Diagnostics;

namespace Nexora.Installer;

/// <summary>
/// Guards the actual Nexora installation directory instead of rejecting every
/// unrelated Nexora.exe process (e.g. a separately published DEV build).
/// Fails closed when a process path cannot be inspected.
/// Never terminates processes or disconnects security keys.
/// </summary>
internal static class NexoraProcessGuard
{
    public static void EnsureStopped(string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        string installedExecutable = Path.GetFullPath(Path.Combine(installDirectory, "Nexora.exe"));
        var candidates = Process.GetProcessesByName("Nexora");
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    if (process.HasExited) continue;
                    string? runningExecutable = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(runningExecutable))
                        throw new IOException(
                            $"Nexora (PID {process.Id}) est actif, mais son chemin ne peut pas être vérifié. " +
                            "Fermez toutes les instances Nexora avant de mettre à jour.");
                    if (Path.GetFullPath(runningExecutable).Equals(installedExecutable,
                            StringComparison.OrdinalIgnoreCase))
                        throw new IOException(
                            $"Nexora Production est encore actif (PID {process.Id}). " +
                            "Utilisez « Quitter Nexora » dans la zone de notification, puis réessayez. " +
                            "Chemin : " + runningExecutable);
                    // A different executable path is a separate DEV / portable instance.
                    // The installer must not stop or overwrite it.
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // Process exited between enumeration and inspection.
                }
                catch (Win32Exception ex)
                {
                    throw new IOException(
                        $"Impossible de vérifier l'exécutable du processus Nexora PID {process.Id}. " +
                        "Par sécurité, fermez cette instance avant de continuer.", ex);
                }
            }
        }
        finally
        {
            foreach (var process in candidates) process.Dispose();
        }
    }
}
