using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Nexora.Installer;

public partial class MainWindow : Window
{
    private readonly bool _uninstallMode;
    private int _page;
    private bool _busy;
    private bool _installed;
    private string? _installedFolder;
    private bool _checking;
    private bool _systemReady;
    private bool _usbReady;
    private bool _closed;

    private readonly Border[] _navBorders;
    private readonly TextBlock[] _navLabels;
    private readonly Control[] _pages;

    public MainWindow() : this(false) { }

    public MainWindow(bool uninstallMode)
    {
        InitializeComponent();
        VersionBadge.Text = "v" + InstallerEngine.DisplayVersion + " · Windows x64";
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) => _closed = true;
        _uninstallMode = uninstallMode;
        _navBorders = [NavStep0, NavStep1, NavStep2, NavStep3, NavStep4];
        _navLabels = [NavText0, NavText1, NavText2, NavText3, NavText4];
        _pages = [WelcomePage, DiagnosticPage, OptionsPage, ProgressPage, FinishedPage];
        FolderBox.Text = InstallerEngine.GetInstalledDirectory() ?? InstallerEngine.DefaultInstallDirectory;

        if (uninstallMode)
        {
            Title = "Nexora — Désinstallation";
            foreach (var panel in _navBorders) panel.IsVisible = false;
            foreach (var panel in _pages) panel.IsVisible = false;
            UninstallPage.IsVisible = true;
            NextButton.IsVisible = false;
            BackButton.IsVisible = false;
            var path = InstallerEngine.GetInstalledDirectory();
            UninstallStatus.Text = path == null
                ? "Installation Nexora introuvable pour cet utilisateur."
                : "Installation détectée : " + path;
            UninstallButton.IsEnabled = path != null;
        }
        else
        {
            ShowPage(0);
        }
    }

    private void ShowPage(int page)
    {
        if (_uninstallMode) return;
        _page = page;
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].IsVisible = i == page;
            _navBorders[i].Background = i == page
                ? new SolidColorBrush(Color.Parse("#294D7E"))
                : Brushes.Transparent;
            _navLabels[i].Foreground = i == page
                ? Brushes.White
                : new SolidColorBrush(Color.Parse("#BBC9DF"));
        }

        BackButton.IsEnabled = !_busy && page is > 0 and < 3;
        BackButton.Content = "Retour";
        BackButton.IsVisible = page != 4;
        NextButton.Content = page switch
        {
            2 => "Vérifier et installer →",
            3 => "Installation…",
            4 => "Terminer",
            _ => "Continuer  →"
        };
        NextButton.IsEnabled = !_busy;
        CancelButton.IsEnabled = !_busy;
        CancelButton.Content = page == 4 ? "Fermer" : "Annuler";
        if (page == 1) _ = CheckDiagnosticAsync();
    }

    private async Task CheckDiagnosticAsync()
    {
        if (_checking || _closed || _busy) { UpdateDiagnosticGate(); return; }
        _checking = true;
        _systemReady = false;
        _usbReady = false;
        RetryDiagnosticButton.IsEnabled = false;
        InstallUsbDriverButton.IsEnabled = false;
        UpdateDiagnosticGate();
        string target = FolderBox.Text?.Trim() ?? "";
        bool windows = OperatingSystem.IsWindows();
        bool x64 = RuntimeInformation.OSArchitecture == Architecture.X64;
        WindowsStatus.Text = windows ? "✓  Windows détecté — " + Environment.OSVersion.Version : "✕  Windows requis";
        ArchStatus.Text = x64 ? "✓  Architecture x64" : "✕  Architecture x64 requise";
        PayloadStatus.Text = "Intégrité du paquet : vérification SHA-256…";
        DestinationStatus.Text = "Dossier : vérification…";
        SpaceStatus.Text = "Espace disque et accès : en attente";
        ExistingStatus.Text = "Installation existante : en attente";
        UsbIpStatus.Text = "USB/IP : vérification du contrôleur Windows…";
        try
        {
            PackageInfo? package = null;
            try
            {
                package = await Task.Run(() => { using var stream = InstallerEngine.OpenPayload(); return InstallerPreflight.Inspect(stream); });
                PayloadStatus.Text = $"✓  {package.Files.Count} fichiers vérifiés · {package.Bytes / 1048576d:N1} Mo décompressés";
            }
            catch (Exception ex) { PayloadStatus.Text = "✕  Paquet : " + ex.Message; }
            try
            {
                await Task.Run(() =>
                {
                    InstallerEngine.ValidateDestination(target);
                    InstallerEngine.CheckRegisteredLocation(target);
                    NexoraProcessGuard.EnsureStopped(target);
                    if (package is not null) InstallerPreflight.CheckStorage(target, package);
                });
                DestinationStatus.Text = "✓  Dossier local valide : " + target;
                SpaceStatus.Text = package is null ? "—  Espace non vérifié : paquet invalide" : "✓  Espace disponible, fichiers accessibles et test d’écriture réussis";
                ExistingStatus.Text = File.Exists(Path.Combine(target, InstallerPreflight.StateName))
                    ? "✓  Installation reconnue · mise à jour des fichiers, coffre conservé"
                    : "✓  Nouvelle installation · aucun dossier inconnu remplacé";
                _systemReady = windows && x64 && package is not null;
            }
            catch (Exception ex)
            {
                DestinationStatus.Text = "✕  Préparation : " + ex.Message;
                SpaceStatus.Text = "—  Corrigez le point bloquant et relancez le contrôle";
                ExistingStatus.Text = "—  Installation suspendue";
            }
            var status = await UsbIpDriverSupport.CheckAsync();
            _usbReady = status.ControllerWorking && status.ClientPresent;
            UsbIpStatus.Text = _usbReady ? "✓  Contrôleur et client USB/IP disponibles" : "!  USB/IP incomplet — connexion des clés indisponible";
            UsbIpHint.Text = status.Details + Environment.NewLine +
                (status.ClientPresent ? "Client trouvé au chemin utilisé par Nexora : Program Files\\USBip." : "Client attendu : " + InstallerEngine.UsbIpCliPath) +
                (string.IsNullOrWhiteSpace(status.Version) ? "" : "\nVersion pilote : " + status.Version);
        }
        catch (Exception ex) { DiagnosticSummary.Text = "Vérification impossible : " + ex.Message; }
        finally
        {
            _checking = false;
            if (!_closed)
            {
                RetryDiagnosticButton.IsEnabled = true;
                InstallUsbDriverButton.IsEnabled = windows && !_usbReady;
                UpdateDiagnosticGate();
            }
        }
    }

    private void UpdateDiagnosticGate()
    {
        if (NextButton is null || DiagnosticSummary is null) return;
        bool ready = !_checking && _systemReady && (_usbReady || DeferredUsbCheckBox.IsChecked == true);
        if (_page == 1) NextButton.IsEnabled = !_busy && ready;
        DiagnosticSummary.Text = _checking ? "Vérifications en cours…" : !_systemReady
            ? "Un point bloquant empêche l’installation. Corrigez-le puis relancez les vérifications."
            : ready ? (_usbReady ? "Votre environnement est prêt." : "Installation de l’application uniquement : USB/IP reste à configurer.")
            : "Installez les prérequis USB/IP ou choisissez explicitement de les configurer plus tard.";
        DiagnosticSummary.Foreground = new SolidColorBrush(Color.Parse(ready ? "#16704C" : "#98420D"));
    }

    private void DeferredUsb_Changed(object? sender, RoutedEventArgs e) => UpdateDiagnosticGate();
    private async void RetryDiagnostic_Click(object? sender, RoutedEventArgs e) => await CheckDiagnosticAsync();
    private void ConfigureDestination_Click(object? sender, RoutedEventArgs e)
    {
        if (!_busy && !_checking) ShowPage(2);
    }

    private async void InstallUsbDriver_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _checking || _page != 1) return;
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Sélectionner le pilote USB/IP signé (.inf)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pilote Windows INF") { Patterns = ["*.inf"] }]
        });
        if (selected.Count == 0) return;
        _busy = true;
        NextButton.IsEnabled = BackButton.IsEnabled = CancelButton.IsEnabled = false;
        string path = selected[0].Path.LocalPath;
        try
        {
            InstallUsbDriverButton.IsEnabled = false;
            UsbIpStatus.Text = "USB/IP : vérification du catalogue signé…";
            string signer = await UsbIpDriverSupport.VerifySignedPackageAsync(path);
            bool accepted = await ConfirmDriverInstallAsync(path, signer);
            if (!accepted) return;

            UsbIpStatus.Text = "USB/IP : attente de la confirmation administrateur Windows…";
            int exitCode = await UsbIpDriverSupport.InstallInfAsync(path);
            if (exitCode is not (0 or 3010))
                throw new IOException("PnPUtil a terminé avec le code " + exitCode +
                                      ". Contrôlez le package signé et la compatibilité Windows.");
            await ShowErrorAsync((exitCode == 3010 ? "Un redémarrage Windows est nécessaire. " : "PnPUtil a terminé. ") + "Si le contrôleur reste absent, " +
                "un paquet officiel complet peut être nécessaire pour créer le périphérique VHCI " +
                "et installer usbip.exe. Aucun pilote n'a été supprimé.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            await ShowErrorAsync("Autorisation administrateur refusée. Aucune modification effectuée.");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Impossible d'installer le pilote : " + ex.Message);
        }
        finally
        {
            _busy = false;
            ShowPage(1);
        }
    }

    private async Task<bool> ConfirmDriverInstallAsync(string file, string signer)
    {
        var dialog = new Window
        {
            Title = "Nexora — Autorisation pilote",
            Width = 600, Height = 265, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var no = new Button { Content = "Annuler", MinWidth = 100 };
        var yes = new Button { Content = "Installer (UAC)", MinWidth = 140 };
        no.Click += (_, _) => dialog.Close(false);
        yes.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22), Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Installer ce pilote Windows signé ?",
                                FontSize = 19, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Fichier : " + file + Environment.NewLine +
                                       "Signature : " + signer,
                                TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                new TextBlock { Text = "Windows demandera les droits administrateur. " +
                                       "Nexora ne désactive pas la vérification des signatures.",
                                TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                                 HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                 Spacing = 12, Children = { no, yes } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private async void Next_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_page == 1 && (_checking || !_systemReady || (!_usbReady && DeferredUsbCheckBox.IsChecked != true))) return;
        if (_page == 4)
        {
            if (_installed && LaunchCheckbox.IsChecked == true && _installedFolder != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(
                        Path.Combine(_installedFolder, "Nexora.exe")) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    await ShowErrorAsync("Nexora est installé mais n'a pas pu être lancé : " + ex.Message);
                    return;
                }
            }
            Close();
            return;
        }
        if (_page == 2)
        {
            await StartInstallationAsync();
            return;
        }
        if (_page == 3 && !_installed)
        {
            ShowPage(2);
            return;
        }
        ShowPage(Math.Min(_page + 1, 4));
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (!_busy && _page == 3 && !_installed) ShowPage(2);
        else if (!_busy && _page is > 0 and < 3) ShowPage(_page - 1);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Dossier de destination pour Nexora",
            AllowMultiple = false
        });
        if (picked.Count > 0)
        {
            FolderBox.Text = picked[0].Path.LocalPath;
        }
    }

    private async Task StartInstallationAsync()
    {
        string requested = FolderBox.Text?.Trim() ?? "";
        try
        {
            InstallerEngine.ValidateDestination(requested);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
            return;
        }

        _busy = true;
        ShowPage(3);
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        InstallProgress.Value = 0;
        PercentageText.Text = "0 %";
        LogText.Text = "";
        InstallStatus.Text = "Vérification de l'archive intégrée…";

        // Progress can be reported from a ThreadPool thread. Always dispatch UI updates
        // explicitly; do not rely on the caller's SynchronizationContext.
        var progress = new Progress<int>(value =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                InstallProgress.Value = value;
                PercentageText.Text = value + " %";
            });
        });
        void Report(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                InstallStatus.Text = message;
                LogText.Text += message + Environment.NewLine;
            });
        }

        // Avalonia controls must only be accessed from the UI thread. Snapshot their
        // values BEFORE starting the background installation worker.
        bool desktopShortcut = DesktopCheckbox.IsChecked == true;
        bool startupShortcut = StartupCheckbox.IsChecked == true;

        try
        {
            if (DeferredUsbCheckBox.IsChecked != true)
            {
                var driver = await UsbIpDriverSupport.CheckAsync();
                if (!driver.ControllerWorking || !driver.ClientPresent)
                    throw new IOException("USB/IP n’est plus prêt. Revenez aux vérifications ou choisissez l’installation de l’application uniquement.");
            }
            string installedPath = await Task.Run(() => InstallerEngine.Install(
                requested, desktopShortcut,
                startupShortcut, progress, Report));
            _installedFolder = installedPath;
            _installed = true;
            InstalledLocation.Text = installedPath;
            FinishedReadiness.Text = DeferredUsbCheckBox.IsChecked == true
                ? "USB/IP a été différé : configurez le pilote et le client avant de connecter une clé virtuelle."
                : "Prérequis USB/IP contrôlés. Vérifiez ensuite un enregistrement et une connexion FIDO2.";
            _busy = false;
            ShowPage(4);
        }
        catch (Exception ex)
        {
            _busy = false;
            InstallStatus.Text = "Erreur d'installation : " + ex.Message;
            LogText.Text += "ERREUR : " + ex.Message + Environment.NewLine;
            NextButton.Content = "Revoir configuration";
            NextButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            BackButton.IsEnabled = true;
            BackButton.Content = "Configuration";
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new Window
        {
            Title = "Nexora — Attention",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 550,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var button = new Button { Content = "Compris", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        button.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(23),
            Spacing = 18,
            Children =
            {
                new ScrollViewer { MaxHeight = 380, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15 } },
                button
            }
        };
        await dialog.ShowDialog(this);
    }

    private async void Uninstall_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        UninstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        UninstallStatus.Text = "Désinstallation en cours…";
        try
        {
            await Task.Run(InstallerEngine.Uninstall);
            UninstallStatus.Text = "Nexora a été désinstallé. Les données personnelles sont conservées.";
        }
        catch (Exception ex)
        {
            UninstallStatus.Text = "Erreur : " + ex.Message;
            UninstallButton.IsEnabled = true;
        }
        finally
        {
            _busy = false;
            CancelButton.IsEnabled = true;
        }
    }

    private async void SaveReport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Compte rendu d’installation", SuggestedFileName = "Nexora-installation.txt",
                DefaultExtension = "txt", FileTypeChoices = [new FilePickerFileType("Texte") { Patterns = ["*.txt"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync($"Nexora {InstallerEngine.DisplayVersion}\n{DateTimeOffset.Now:O}\n{InstalledLocation.Text}\n{FinishedReadiness.Text}\n\n{LogText.Text}");
        }
        catch (Exception ex) { await ShowErrorAsync("Enregistrement impossible : " + ex.Message); }
    }
}
