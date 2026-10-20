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

    private readonly Border[] _navBorders;
    private readonly TextBlock[] _navLabels;
    private readonly Control[] _pages;

    public MainWindow(bool uninstallMode)
    {
        InitializeComponent();
        _uninstallMode = uninstallMode;
        _navBorders = [NavStep0, NavStep1, NavStep2, NavStep3, NavStep4];
        _navLabels = [NavText0, NavText1, NavText2, NavText3, NavText4];
        _pages = [WelcomePage, DiagnosticPage, OptionsPage, ProgressPage, FinishedPage];
        FolderBox.Text = InstallerEngine.DefaultInstallDirectory;

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
                ? new SolidColorBrush(Color.Parse("#2F2356"))
                : Brushes.Transparent;
            _navLabels[i].Foreground = i == page
                ? new SolidColorBrush(Color.Parse("#F3E6FF"))
                : new SolidColorBrush(Color.Parse("#8796B3"));
        }

        BackButton.IsEnabled = !_busy && page is > 0 and < 3;
        BackButton.IsVisible = page != 4;
        NextButton.Content = page switch
        {
            2 => "Installer Nexora  →",
            3 => "Installation…",
            4 => "Terminer",
            _ => "Continuer  →"
        };
        NextButton.IsEnabled = !_busy;
        CancelButton.IsEnabled = !_busy;
        CancelButton.Content = page == 4 ? "Fermer" : "Annuler";
        if (page == 1) CheckDiagnostic();
    }

    private async void CheckDiagnostic()
    {
        bool windows = OperatingSystem.IsWindows();
        bool x64 = RuntimeInformation.OSArchitecture == Architecture.X64;
        bool payload = InstallerEngine.HasEmbeddedPayload();
        WindowsStatus.Text = windows ? "✓  Windows détecté" : "✕  Windows requis";
        ArchStatus.Text = x64 ? "✓  Architecture x64" : "✕  Architecture x64 requise";
        PayloadStatus.Text = payload ? "✓  Fichiers Nexora inclus dans l'installateur" : "✕  Fichiers Nexora absents : recréez le Setup";
        UsbIpStatus.Text = "USB/IP : vérification du contrôleur Windows…";
        UsbIpHint.Text = "Contrôle distinct du pilote et de l'utilitaire usbip.exe.";
        InstallUsbDriverButton.IsEnabled = false;
        NextButton.IsEnabled = windows && x64 && payload;
        if (!windows) return;
        var status = await UsbIpDriverSupport.CheckAsync();
        if (_page != 1 || _uninstallMode || _busy) return;
        UsbIpStatus.Text = status.ControllerWorking
            ? "✓  Pilote VHCI USB/IP opérationnel" +
              (string.IsNullOrWhiteSpace(status.Version) ? "" : " — version " + status.Version)
            : status.ControllerDetected
                ? "!  Contrôleur USB/IP détecté, mais en erreur"
                : "!  Pilote/contrôleur USB/IP non détecté";
        UsbIpHint.Text = status.Details + Environment.NewLine +
            (status.ClientPresent ? "Client usbip.exe : installé." :
                "Client usbip.exe : absent (nécessite le paquet officiel complet).") +
            (string.IsNullOrWhiteSpace(status.Signer) ? "" :
                Environment.NewLine + "Signataire : " + status.Signer);
        InstallUsbDriverButton.IsEnabled = !status.ControllerWorking;
    }

    private async void InstallUsbDriver_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _page != 1) return;
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Sélectionner le pilote USB/IP signé (.inf)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pilote Windows INF") { Patterns = ["*.inf"] }]
        });
        if (selected.Count == 0) return;
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
            if (exitCode != 0)
                throw new IOException("PnPUtil a terminé avec le code " + exitCode +
                                      ". Contrôlez le package signé et la compatibilité Windows.");
            await ShowErrorAsync("PnPUtil a terminé. Si le contrôleur reste absent, " +
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
            CheckDiagnostic();
        }
    }

    private async Task<bool> ConfirmDriverInstallAsync(string file, string signer)
    {
        var dialog = new Window
        {
            Title = "Nexora — Autorisation pilote",
            Width = 600, Height = 265, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#111A31"))
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
                    InstallStatus.Text = "Nexora est installé mais n'a pas pu être lancé : " + ex.Message;
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
            string installedPath = await Task.Run(() => InstallerEngine.Install(
                requested, desktopShortcut,
                startupShortcut, progress, Report));
            _installedFolder = installedPath;
            _installed = true;
            InstalledLocation.Text = installedPath;
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
            Height = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#111A31"))
        };
        var button = new Button { Content = "Compris", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        button.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(23),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15 },
                button
            }
        };
        await dialog.ShowDialog(this);
    }

    private async void Uninstall_Click(object? sender, RoutedEventArgs e)
    {
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
            CancelButton.IsEnabled = true;
        }
    }
}
