using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using PDFComparison.Models;
using PDFComparison.Services;

namespace PDFComparison.Views;

public partial class TidesCampaignWindow : Window
{
    private readonly ITidesCampaignService _campaignService;
    private readonly IConfigurationService? _configService;
    private readonly AutomatedScanProfile _scanProfile;
    private readonly ObservableCollection<TidesProductItem> _products = new();
    private readonly ICollectionView _productsView;
    private string _activeChannelFilter = "ALL";
    private bool _suppressComboEvent;

    public List<TidesProductItem> SelectedProductsToCompare { get; private set; } = new();
    public bool IgnoreRepeatingDates => ChkIgnoreDates.IsChecked == true;
    public bool IgnoreHeaderFooter => ChkIgnoreHeaderFooter.IsChecked == true;
    public bool WriteToProductOutputs => ChkWriteProductOutputs.IsChecked == true;
    public string SelectedReleaseName => CmbReleaseFolder.Text?.Trim() ?? string.Empty;
    public string SelectedRootPath => TxtRootPath.Text?.Trim() ?? string.Empty;
    public string SelectedSecondaryTargetPath => TxtSecondaryTargetPath.Text?.Trim() ?? string.Empty;

    public TidesCampaignWindow(ITidesCampaignService campaignService, string? initialRootPath = null, string? initialRelease = null)
        : this(
            campaignService,
            CreateAdHocTidesProfile(campaignService, initialRootPath, initialRelease),
            configService: null,
            fallbackRelease: initialRelease)
    {
    }

    public TidesCampaignWindow(
        ITidesCampaignService campaignService,
        AutomatedScanProfile scanProfile,
        IConfigurationService? configService = null,
        string? fallbackRelease = null)
    {
        InitializeComponent();
        _campaignService = campaignService ?? throw new ArgumentNullException(nameof(campaignService));
        _scanProfile = scanProfile ?? AutomatedScanProfile.CreateDefaultEquipeTesting();
        _configService = configService;

        BtnConfigureScan.Visibility = _configService != null ? Visibility.Visible : Visibility.Collapsed;

        ApplyProfileToHeaderAndOptions(fallbackRelease, initializeCheckboxes: true);

        _productsView = CollectionViewSource.GetDefaultView(_products);
        _productsView.Filter = FilterProductItem;
        GridProducts.ItemsSource = _productsView;

        Loaded += async (_, _) => await ScanProductsAsync();
    }

    private static AutomatedScanProfile CreateAdHocTidesProfile(
        ITidesCampaignService svc,
        string? initialRootPath,
        string? initialRelease)
    {
        var p = AutomatedScanProfile.CreateDefaultEquipeTesting();
        if (!string.IsNullOrWhiteSpace(initialRootPath))
        {
            p.RootPath = initialRootPath;
        }
        else
        {
            p.RootPath = svc.GetDefaultRootPath();
        }

        if (!string.IsNullOrWhiteSpace(initialRelease))
        {
            p.DefaultReleaseName = initialRelease;
        }
        return p;
    }

    private void ApplyProfileToHeaderAndOptions(string? fallbackRelease = null, bool initializeCheckboxes = false)
    {
        string btnTitle = string.IsNullOrWhiteSpace(_scanProfile.ButtonName) ? "Équipe Testing" : _scanProfile.ButtonName;
        Title = btnTitle;
        TxtWindowTitle.Text = btnTitle;

        BdRulesBadge.Visibility = _scanProfile.UseDedicatedRules ? Visibility.Visible : Visibility.Collapsed;

        // Chemins initiaux
        if (!string.IsNullOrWhiteSpace(_scanProfile.RootPath))
        {
            TxtRootPath.Text = _scanProfile.RootPath;
        }
        else if (_scanProfile.IsBuiltInTides)
        {
            TxtRootPath.Text = _campaignService.GetDefaultRootPath();
        }
        else if (initializeCheckboxes)
        {
            TxtRootPath.Text = string.Empty;
        }

        if (_scanProfile.TopologyMode == ScanTopologyMode.MirrorTrees)
        {
            LblRootPath.Text = "Source :";
            ColSecondaryTarget.Width = new GridLength(1, GridUnitType.Star);
            PnlSecondaryTarget.Visibility = Visibility.Visible;
            TxtSecondaryTargetPath.Text = _scanProfile.SecondaryTargetRootPath ?? string.Empty;
            PnlReleaseSelector.Visibility = Visibility.Collapsed;
            BdReleaseBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            LblRootPath.Text = "Racine :";
            ColSecondaryTarget.Width = new GridLength(0);
            PnlSecondaryTarget.Visibility = Visibility.Collapsed;

            bool showRelease = _scanProfile.TopologyMode == ScanTopologyMode.ProductSubfolders && _scanProfile.UseReleaseFolder;
            PnlReleaseSelector.Visibility = showRelease ? Visibility.Visible : Visibility.Collapsed;
            BdReleaseBadge.Visibility = showRelease ? Visibility.Visible : Visibility.Collapsed;

            if (showRelease)
            {
                string defaultRelease = !string.IsNullOrWhiteSpace(_scanProfile.DefaultReleaseName)
                    ? _scanProfile.DefaultReleaseName
                    : (!string.IsNullOrWhiteSpace(fallbackRelease) ? fallbackRelease! : _campaignService.GetCurrentMonthReleaseName());

                _suppressComboEvent = true;
                if (CmbReleaseFolder.Items.Count == 0)
                {
                    foreach (var r in _campaignService.GetSuggestedReleaseNames(DateTime.Now.Year).OrderByDescending(x => x))
                    {
                        CmbReleaseFolder.Items.Add(r);
                    }
                }
                CmbReleaseFolder.Text = defaultRelease;
                TxtReleaseBadge.Text = $"Release : {defaultRelease}";
                _suppressComboEvent = false;
            }
        }

        if (initializeCheckboxes)
        {
            ChkIgnoreDates.IsChecked = _scanProfile.DefaultIgnoreDates;
            ChkIgnoreHeaderFooter.IsChecked = _scanProfile.DefaultIgnoreHeaderFooter;
            ChkWriteProductOutputs.IsChecked = _scanProfile.DefaultWriteProductOutputs;
        }

        bool canWriteOutputs = _scanProfile.TopologyMode != ScanTopologyMode.MirrorTrees &&
                               !string.IsNullOrWhiteSpace(_scanProfile.ProductOutputSubfolderTemplate);
        ChkWriteProductOutputs.Visibility = canWriteOutputs ? Visibility.Visible : Visibility.Collapsed;
        if (!canWriteOutputs)
        {
            ChkWriteProductOutputs.IsChecked = false;
        }
    }

    private bool FilterProductItem(object obj)
    {
        if (obj is not TidesProductItem item) return false;

        if (!string.Equals(_activeChannelFilter, "ALL", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.Channel, _activeChannelFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task ScanProductsAsync()
    {
        string root = TxtRootPath.Text?.Trim() ?? string.Empty;
        string secondaryTarget = TxtSecondaryTargetPath.Text?.Trim() ?? string.Empty;
        string release = CmbReleaseFolder.Text?.Trim() ?? _campaignService.GetCurrentMonthReleaseName();

        if (_scanProfile.TopologyMode == ScanTopologyMode.ProductSubfolders && _scanProfile.UseReleaseFolder)
        {
            TxtReleaseBadge.Text = $"Release : {release}";
            TxtDiscoveryStatus.Text = $"Scan en cours de '{root}' pour la release '{release}'...";
        }
        else if (_scanProfile.TopologyMode == ScanTopologyMode.MirrorTrees)
        {
            TxtDiscoveryStatus.Text = $"Scan en cours de '{root}' ➔ '{secondaryTarget}'...";
        }
        else
        {
            TxtDiscoveryStatus.Text = $"Scan automatique des sous-dossiers dans '{root}'...";
        }

        BtnRefresh.IsEnabled = false;

        try
        {
            var discovery = await _campaignService.DiscoverProductsForProfileAsync(
                _scanProfile,
                rootOverride: root,
                secondaryTargetOverride: secondaryTarget,
                releaseOverride: release);

            if (!string.IsNullOrWhiteSpace(discovery.ResolvedRootPath) &&
                !string.Equals(TxtRootPath.Text, discovery.ResolvedRootPath, StringComparison.OrdinalIgnoreCase))
            {
                TxtRootPath.Text = discovery.ResolvedRootPath;
            }

            if (_scanProfile.TopologyMode == ScanTopologyMode.ProductSubfolders &&
                _scanProfile.UseReleaseFolder &&
                discovery.DiscoveredReleases.Count > 0)
            {
                _suppressComboEvent = true;
                string currentText = CmbReleaseFolder.Text ?? release;
                CmbReleaseFolder.Items.Clear();
                foreach (var r in discovery.DiscoveredReleases)
                {
                    CmbReleaseFolder.Items.Add(r);
                }
                CmbReleaseFolder.Text = currentText;
                _suppressComboEvent = false;
            }

            _products.Clear();
            foreach (var p in discovery.Products)
            {
                p.PropertyChanged += ProductItem_PropertyChanged;
                _products.Add(p);
            }

            RebuildChannelFilterButtons(discovery.DiscoveredChannels);

            if (!string.Equals(_activeChannelFilter, "ALL", StringComparison.OrdinalIgnoreCase) &&
                !_products.Any(p => string.Equals(p.Channel, _activeChannelFilter, StringComparison.OrdinalIgnoreCase)))
            {
                _activeChannelFilter = "ALL";
            }

            _productsView.Refresh();
            TxtDiscoveryStatus.Text = discovery.StatusMessage;
            UpdateSelectionCounters();
        }
        catch (Exception ex)
        {
            TxtDiscoveryStatus.Text = $"Erreur lors du scan : {ex.Message}";
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
        }
    }

    private void RebuildChannelFilterButtons(List<string> discoveredChannels)
    {
        PanelChannelFilters.Children.Clear();

        var distinctChannels = discoveredChannels
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctChannels.Count == 0)
        {
            distinctChannels = _products
                .Select(p => p.Channel)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (distinctChannels.Count <= 1)
        {
            PanelChannelFilters.Visibility = Visibility.Collapsed;
            return;
        }

        PanelChannelFilters.Visibility = Visibility.Visible;
        var secondaryStyle = TryFindResource("SecondaryBtn") as Style;

        var allBtn = new Button
        {
            Content = "Tous",
            Tag = "ALL",
            Style = secondaryStyle,
            Margin = new Thickness(0, 0, 4, 0)
        };
        allBtn.Click += BtnFilterChannel_Click;
        PanelChannelFilters.Children.Add(allBtn);

        foreach (var ch in distinctChannels)
        {
            var chBtn = new Button
            {
                Content = ch,
                Tag = ch,
                Style = secondaryStyle,
                Margin = new Thickness(0, 0, 4, 0)
            };

            if (string.Equals(ch, "Bank", StringComparison.OrdinalIgnoreCase))
            {
                chBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));
            }
            else if (string.Equals(ch, "Broker", StringComparison.OrdinalIgnoreCase))
            {
                chBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x6D, 0x28, 0xD9));
            }

            chBtn.Click += BtnFilterChannel_Click;
            PanelChannelFilters.Children.Add(chBtn);
        }
    }

    private void ProductItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TidesProductItem.IsSelected))
        {
            UpdateSelectionCounters();
        }
    }

    private void UpdateSelectionCounters()
    {
        var checkedItems = _products.Where(p => p.IsSelected).ToList();
        int totalSourcePdfs = checkedItems.Sum(p => p.SourcePdfCount);

        int bankChecked = checkedItems.Count(p => string.Equals(p.Channel, "Bank", StringComparison.OrdinalIgnoreCase));
        int brokerChecked = checkedItems.Count(p => string.Equals(p.Channel, "Broker", StringComparison.OrdinalIgnoreCase));

        if (bankChecked + brokerChecked > 0 && bankChecked + brokerChecked == checkedItems.Count)
        {
            TxtSelectionSummary.Text = $"{checkedItems.Count} produit(s) coché(s) ({bankChecked} Bank, {brokerChecked} Broker) • ~{totalSourcePdfs} PDF à comparer";
        }
        else
        {
            TxtSelectionSummary.Text = $"{checkedItems.Count} dossier(s)/produit(s) coché(s) • ~{totalSourcePdfs} PDF à comparer";
        }

        TxtBtnStart.Text = checkedItems.Count > 0
            ? $"Lancer la Comparaison ({checkedItems.Count} • ~{totalSourcePdfs} PDF)"
            : "Sélectionnez au moins un dossier";
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await ScanProductsAsync();
    }

    private async void BtnConfigureScan_Click(object sender, RoutedEventArgs e)
    {
        if (_configService == null) return;

        // Conserver les chemins courants dans le profil avant d'ouvrir la fenêtre de configuration
        if (!string.IsNullOrWhiteSpace(TxtRootPath.Text))
        {
            _scanProfile.RootPath = TxtRootPath.Text.Trim();
        }
        if (_scanProfile.TopologyMode == ScanTopologyMode.MirrorTrees && !string.IsNullOrWhiteSpace(TxtSecondaryTargetPath.Text))
        {
            _scanProfile.SecondaryTargetRootPath = TxtSecondaryTargetPath.Text.Trim();
        }

        var settingsWin = new AppSettingsWindow(_configService, _scanProfile)
        {
            Owner = this
        };

        if (settingsWin.ShowDialog() == true)
        {
            ApplyProfileToHeaderAndOptions(SelectedReleaseName, initializeCheckboxes: true);
            await ScanProductsAsync();
        }
    }

    private async void CmbReleaseFolder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvent || !IsLoaded) return;
        if (CmbReleaseFolder.SelectedItem is string selectedRelease && !string.IsNullOrWhiteSpace(selectedRelease))
        {
            CmbReleaseFolder.Text = selectedRelease;
            await ScanProductsAsync();
        }
    }

    private async void BtnBrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = _scanProfile.TopologyMode == ScanTopologyMode.MirrorTrees
                ? "Sélectionner le dossier Source global"
                : $"Sélectionner le dossier racine pour « {_scanProfile.ButtonName} »"
        };

        if (Directory.Exists(TxtRootPath.Text))
        {
            dlg.InitialDirectory = TxtRootPath.Text;
        }

        if (dlg.ShowDialog(this) == true)
        {
            TxtRootPath.Text = dlg.FolderName;
            await ScanProductsAsync();
        }
    }

    private async void BtnBrowseSecondaryTarget_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Sélectionner le dossier Cible global"
        };

        if (Directory.Exists(TxtSecondaryTargetPath.Text))
        {
            dlg.InitialDirectory = TxtSecondaryTargetPath.Text;
        }

        if (dlg.ShowDialog(this) == true)
        {
            TxtSecondaryTargetPath.Text = dlg.FolderName;
            await ScanProductsAsync();
        }
    }

    private void BtnCheckReady_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _products)
        {
            p.IsSelected = p.IsReady;
        }
        UpdateSelectionCounters();
    }

    private void BtnCheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _products)
        {
            p.IsSelected = true;
        }
        UpdateSelectionCounters();
    }

    private void BtnUncheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _products)
        {
            p.IsSelected = false;
        }
        UpdateSelectionCounters();
    }

    private void BtnFilterChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string channelTag)
        {
            _activeChannelFilter = channelTag;
            _productsView.Refresh();
        }
    }

    private void ProductCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectionCounters();
    }

    private void BtnOpenProductFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TidesProductItem item)
        {
            string pathToOpen = Directory.Exists(item.ProductRootPath)
                ? item.ProductRootPath
                : TxtRootPath.Text;

            if (Directory.Exists(pathToOpen))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(pathToOpen) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Impossible d'ouvrir le dossier : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                MessageBox.Show($"Le dossier '{item.ProductRootPath}' n'est pas accessible actuellement.", "Dossier introuvable", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnStartCampaign_Click(object sender, RoutedEventArgs e)
    {
        var selected = _products.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Veuillez cocher au moins un dossier/produit à comparer.", "Aucune sélection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var validSelected = selected.Where(p => p.HasSourceFolder && p.HasTargetFolder && p.SourcePdfCount > 0).ToList();
        if (validSelected.Count == 0)
        {
            string detail = _scanProfile.TopologyMode == ScanTopologyMode.ProductSubfolders && _scanProfile.UseReleaseFolder
                ? $"Aucun des produits cochés ne contient à la fois un dossier Source avec des PDF et le dossier '{SelectedReleaseName}' sur '{SelectedRootPath}'."
                : "Aucun des éléments cochés ne contient à la fois un dossier Source avec des PDF et un dossier Cible correspondant.";

            MessageBox.Show(
                detail,
                "Dossiers Source / Cible introuvables",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SelectedProductsToCompare = validSelected;
        DialogResult = true;
        Close();
    }
}
