using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PDFComparison.Models;
using PDFComparison.Services;

namespace PDFComparison.Views;

public partial class AppSettingsWindow : Window
{
    private readonly IConfigurationService _configService;
    private bool _suppressSelectionCombos;

    public AppUserSettings Settings => _configService.UserSettings;

    public AppSettingsWindow(IConfigurationService configService, AutomatedScanProfile? initialProfileToSelect = null)
    {
        InitializeComponent();
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        DataContext = this;

        if (initialProfileToSelect != null)
        {
            MainTabs.SelectedIndex = 1;
            ListScanProfiles.SelectedItem = Settings.ScanProfiles.FirstOrDefault(p => p.Id == initialProfileToSelect.Id)
                                            ?? Settings.ScanProfiles.FirstOrDefault();
        }
        else
        {
            ListScanProfiles.SelectedItem = Settings.ScanProfiles.FirstOrDefault();
        }

        UpdateMailPreview();
    }

    private AutomatedScanProfile? CurrentScanProfile => ListScanProfiles.SelectedItem as AutomatedScanProfile;

    private void ListScanProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = CurrentScanProfile;
        PanelSelectedProfile.DataContext = selected;
        PanelSelectedProfile.IsEnabled = selected != null;
        BtnDeleteProfile.IsEnabled = selected != null && Settings.ScanProfiles.Count > 1;

        if (selected == null) return;

        _suppressSelectionCombos = true;
        SelectComboByTag(CmbTopologyMode, selected.TopologyMode.ToString());
        SelectComboByTag(CmbPairingStrategy, selected.PairingStrategy.ToString());
        _suppressSelectionCombos = false;
    }

    private static void SelectComboByTag(ComboBox combo, string tagValue)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tagValue, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void CmbTopologyMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionCombos || CurrentScanProfile == null) return;
        if (CmbTopologyMode.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<ScanTopologyMode>(item.Tag?.ToString(), out var parsed))
        {
            CurrentScanProfile.TopologyMode = parsed;
        }
    }

    private void CmbPairingStrategy_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionCombos || CurrentScanProfile == null) return;
        if (CmbPairingStrategy.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<PdfPairingStrategy>(item.Tag?.ToString(), out var parsed))
        {
            CurrentScanProfile.PairingStrategy = parsed;
        }
    }

    private void BtnPickColor_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentScanProfile != null && sender is Button btn && btn.Tag is string hex)
        {
            CurrentScanProfile.ButtonColorHex = hex;
        }
    }

    private void BtnAddScanProfile_Click(object sender, RoutedEventArgs e)
    {
        var created = AutomatedScanProfile.CreateNewCustom(Settings.ScanProfiles.Count + 1, _configService.GeneralProfile);
        Settings.ScanProfiles.Add(created);
        ListScanProfiles.SelectedItem = created;
        BtnDeleteProfile.IsEnabled = Settings.ScanProfiles.Count > 1;
    }

    private void BtnDuplicateScanProfile_Click(object sender, RoutedEventArgs e)
    {
        var src = CurrentScanProfile;
        if (src == null) return;

        var copy = AutomatedScanProfile.CreateNewCustom(Settings.ScanProfiles.Count + 1, src.DedicatedProfile);
        copy.ButtonName = $"{src.ButtonName} (Copie)";
        copy.ButtonColorHex = src.ButtonColorHex;
        copy.ShowInMainToolbar = true;
        copy.TopologyMode = src.TopologyMode;
        copy.RootPath = src.RootPath;
        copy.SecondaryTargetRootPath = src.SecondaryTargetRootPath;
        copy.ChannelsCsv = src.ChannelsCsv;
        copy.UseReleaseFolder = src.UseReleaseFolder;
        copy.DefaultReleaseName = src.DefaultReleaseName;
        copy.SourceSubfolderTemplate = src.SourceSubfolderTemplate;
        copy.TargetSubfolderTemplate = src.TargetSubfolderTemplate;
        copy.ProductOutputSubfolderTemplate = src.ProductOutputSubfolderTemplate;
        copy.AutoSourceFolderNamesCsv = src.AutoSourceFolderNamesCsv;
        copy.AutoTargetFolderNamesCsv = src.AutoTargetFolderNamesCsv;
        copy.CustomReportOutputDirectory = src.CustomReportOutputDirectory;
        copy.PairingStrategy = src.PairingStrategy;
        copy.CustomFileMatchingRegex = src.CustomFileMatchingRegex;
        copy.DefaultIgnoreDates = src.DefaultIgnoreDates;
        copy.DefaultIgnoreHeaderFooter = src.DefaultIgnoreHeaderFooter;
        copy.DefaultWriteProductOutputs = src.DefaultWriteProductOutputs;
        copy.UseDedicatedRules = src.UseDedicatedRules;

        Settings.ScanProfiles.Add(copy);
        ListScanProfiles.SelectedItem = copy;
        BtnDeleteProfile.IsEnabled = Settings.ScanProfiles.Count > 1;
    }

    private void BtnDeleteScanProfile_Click(object sender, RoutedEventArgs e)
    {
        var target = CurrentScanProfile;
        if (target == null || Settings.ScanProfiles.Count <= 1) return;

        var confirm = MessageBox.Show(
            $"Supprimer le bouton « {target.ButtonName} » ?",
            "Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            Settings.ScanProfiles.Remove(target);
            ListScanProfiles.SelectedItem = Settings.ScanProfiles.FirstOrDefault();
            BtnDeleteProfile.IsEnabled = Settings.ScanProfiles.Count > 1;
        }
    }

    private void BtnExportScanProfile_Click(object sender, RoutedEventArgs e)
    {
        var current = CurrentScanProfile;
        if (current == null) return;

        string safeName = string.Join("_", current.ButtonName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var dlg = new SaveFileDialog
        {
            Title = "Partager / Exporter ce bouton de scan (dossiers + règles incluses)",
            Filter = "Configuration Bouton JSON (*.json)|*.json",
            FileName = $"Bouton_{safeName}.json"
        };

        if (dlg.ShowDialog(this) == true)
        {
            _configService.ExportScanProfile(current, dlg.FileName);
            MessageBox.Show($"Bouton « {current.ButtonName} » exporté avec succès vers :\n{dlg.FileName}", "Export terminé", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnImportScanProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Importer un bouton de scan partagé (.json)",
            Filter = "Configuration Bouton JSON (*.json)|*.json"
        };

        if (dlg.ShowDialog(this) == true)
        {
            if (_configService.ImportScanProfile(dlg.FileName, out var imported, out string err) && imported != null)
            {
                Settings.ScanProfiles.Add(imported);
                ListScanProfiles.SelectedItem = imported;
                _configService.SaveUserSettings();
                BtnDeleteProfile.IsEnabled = Settings.ScanProfiles.Count > 1;
            }
            else
            {
                MessageBox.Show($"Impossible d'importer ce fichier :\n{err}", "Erreur d'import", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnBrowseDefaultReportDir_Click(object sender, RoutedEventArgs e)
    {
        string? picked = PickFolder("Choisir le dossier par défaut de création des rapports", Settings.DefaultReportOutputDirectory);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            Settings.DefaultReportOutputDirectory = picked;
            UpdateMailPreview();
        }
    }

    private void BtnBrowseCustomReportDir_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentScanProfile == null) return;
        string? picked = PickFolder($"Choisir le dossier des rapports pour « {CurrentScanProfile.ButtonName} »", CurrentScanProfile.CustomReportOutputDirectory);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            CurrentScanProfile.CustomReportOutputDirectory = picked;
        }
    }

    private void BtnBrowseProfileRoot_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentScanProfile == null) return;
        string? picked = PickFolder("Sélectionner le dossier racine à scanner", CurrentScanProfile.RootPath);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            CurrentScanProfile.RootPath = picked;
        }
    }

    private void BtnBrowseSecondaryTarget_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentScanProfile == null) return;
        string? picked = PickFolder("Sélectionner le dossier Cible global", CurrentScanProfile.SecondaryTargetRootPath);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            CurrentScanProfile.SecondaryTargetRootPath = picked;
        }
    }

    private void BtnEditDedicatedRules_Click(object sender, RoutedEventArgs e)
    {
        var current = CurrentScanProfile;
        if (current == null) return;

        current.DedicatedProfile ??= ComparisonProfile.CreateDefault();
        current.DedicatedProfile.ProfileName = $"Règles — {current.ButtonName}";

        var scopedSvc = _configService.CreateScopedServiceForProfile(current.DedicatedProfile);
        var rulesWin = new ProfileSettingsWindow(scopedSvc)
        {
            Owner = this,
            Title = $"Règles & Exclusions dédiées — {current.ButtonName}"
        };
        rulesWin.ShowDialog();
        _configService.SaveUserSettings();
    }

    private void BtnCopyFromGeneralRules_Click(object sender, RoutedEventArgs e)
    {
        var current = CurrentScanProfile;
        if (current == null) return;

        current.DedicatedProfile ??= ComparisonProfile.CreateDefault();
        ConfigurationService.CopyProfileValues(_configService.GeneralProfile, current.DedicatedProfile);
        current.DedicatedProfile.ProfileName = $"Règles — {current.ButtonName}";
        _configService.SaveUserSettings();

        MessageBox.Show(
            $"Les zones et règles générales ont été copiées dans le bouton « {current.ButtonName} ».",
            "Règles copiées",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ReportSettings_TextChanged(object sender, TextChangedEventArgs e) => UpdateMailPreview();
    private void ReportSettings_CheckChanged(object sender, RoutedEventArgs e) => UpdateMailPreview();

    private void UpdateMailPreview()
    {
        if (TxtMailPreview == null || _configService == null) return;

        string dir = (Settings.DefaultReportOutputDirectory ?? string.Empty).Trim().Trim('"');
        if (Settings.CreateSubfolderPerRun)
        {
            dir = Path.Combine(dir, $"Run_{DateTime.Now:yyyyMMdd_HHmm}");
        }
        string htmlFile = Path.Combine(dir, "Global_Synthesis_Report.html");

        string toLine = string.IsNullOrWhiteSpace(Settings.ShareMailRecipients)
            ? "(À renseigner dans Outlook)"
            : Settings.ShareMailRecipients;

        TxtMailPreview.Text =
            $"À : {toLine}\n" +
            $"Objet : {Settings.ShareMailSubject}\n\n" +
            $"{Settings.ShareMailIntro}\n\n" +
            $"• Synthèse interactive : {htmlFile}\n" +
            $"• Dossier des rapports : {dir}";
    }

    private string? PickFolder(string title, string? current)
    {
        var dlg = new OpenFolderDialog { Title = title };
        string clean = (current ?? string.Empty).Trim().Trim('"');
        if (Directory.Exists(clean))
        {
            dlg.InitialDirectory = clean;
        }
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    }

    private void BtnSaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is IInputElement el)
        {
            FocusManager.SetFocusedElement(this, el);
        }

        _configService.SaveActiveProfile();
        _configService.SaveUserSettings();

        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
        }

        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
