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
using Microsoft.Win32;
using PDFComparison.Models;
using PDFComparison.Services;

namespace PDFComparison.Views;

public partial class TidesCampaignWindow : Window
{
    private readonly ITidesCampaignService _campaignService;
    private readonly ObservableCollection<TidesProductItem> _products = new();
    private readonly ICollectionView _productsView;
    private string _activeChannelFilter = "ALL";
    private bool _suppressComboEvent;

    public List<TidesProductItem> SelectedProductsToCompare { get; private set; } = new();
    public bool UseVisibleTextOnlyMode => ChkVisibleTextOnly.IsChecked == true;
    public bool IgnoreRepeatingDates => ChkIgnoreDates.IsChecked == true;
    public bool IgnoreHeaderFooter => ChkIgnoreHeaderFooter.IsChecked == true;
    public bool WriteToProductOutputs => ChkWriteProductOutputs.IsChecked == true;
    public string SelectedReleaseName => CmbReleaseFolder.Text?.Trim() ?? string.Empty;
    public string SelectedRootPath => TxtRootPath.Text?.Trim() ?? string.Empty;

    public TidesCampaignWindow(ITidesCampaignService campaignService, string? initialRootPath = null, string? initialRelease = null)
    {
        InitializeComponent();
        _campaignService = campaignService ?? throw new ArgumentNullException(nameof(campaignService));

        if (!string.IsNullOrWhiteSpace(initialRootPath))
        {
            TxtRootPath.Text = initialRootPath;
        }
        else
        {
            TxtRootPath.Text = _campaignService.GetDefaultRootPath();
        }

        string defaultRelease = !string.IsNullOrWhiteSpace(initialRelease)
            ? initialRelease!
            : _campaignService.GetCurrentMonthReleaseName();

        _suppressComboEvent = true;
        foreach (var r in _campaignService.GetSuggestedReleaseNames(DateTime.Now.Year).OrderByDescending(x => x))
        {
            CmbReleaseFolder.Items.Add(r);
        }
        CmbReleaseFolder.Text = defaultRelease;
        TxtReleaseBadge.Text = $"Release cible : {defaultRelease}";
        _suppressComboEvent = false;

        _productsView = CollectionViewSource.GetDefaultView(_products);
        _productsView.Filter = FilterProductItem;
        GridProducts.ItemsSource = _productsView;

        Loaded += async (_, _) => await ScanProductsAsync();
    }

    private bool FilterProductItem(object obj)
    {
        if (obj is not TidesProductItem item) return false;

        if (!string.Equals(_activeChannelFilter, "ALL", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.Channel, _activeChannelFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string q = TxtFilterProduct?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(q)) return true;

        return item.ProductName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               item.Channel.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               item.StatusText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               item.AvailableReleasesCsv.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ScanProductsAsync()
    {
        string root = TxtRootPath.Text?.Trim() ?? _campaignService.GetDefaultRootPath();
        string release = CmbReleaseFolder.Text?.Trim() ?? _campaignService.GetCurrentMonthReleaseName();

        TxtReleaseBadge.Text = $"Release cible : {release}";
        TxtDiscoveryStatus.Text = $"Scan en cours de '{root}' (Bank puis Broker) pour la release '{release}'...";
        BtnRefresh.IsEnabled = false;

        try
        {
            var discovery = await _campaignService.DiscoverProductsAsync(root, release);

            if (!string.IsNullOrWhiteSpace(discovery.ResolvedRootPath) &&
                !string.Equals(TxtRootPath.Text, discovery.ResolvedRootPath, StringComparison.OrdinalIgnoreCase))
            {
                TxtRootPath.Text = discovery.ResolvedRootPath;
            }

            _suppressComboEvent = true;
            string currentText = CmbReleaseFolder.Text ?? release;
            CmbReleaseFolder.Items.Clear();
            foreach (var r in discovery.DiscoveredReleases)
            {
                CmbReleaseFolder.Items.Add(r);
            }
            CmbReleaseFolder.Text = currentText;
            _suppressComboEvent = false;

            _products.Clear();
            foreach (var p in discovery.Products)
            {
                p.PropertyChanged += ProductItem_PropertyChanged;
                _products.Add(p);
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
        int bankChecked = checkedItems.Count(p => string.Equals(p.Channel, "Bank", StringComparison.OrdinalIgnoreCase));
        int brokerChecked = checkedItems.Count(p => string.Equals(p.Channel, "Broker", StringComparison.OrdinalIgnoreCase));
        int totalSourcePdfs = checkedItems.Sum(p => p.SourcePdfCount);

        TxtSelectionSummary.Text = $"{checkedItems.Count} produit(s) coché(s) ({bankChecked} Bank, {brokerChecked} Broker) • ~{totalSourcePdfs} PDF à comparer";
        TxtBtnStart.Text = checkedItems.Count > 0
            ? $"Lancer la Comparaison ({checkedItems.Count} produit(s) • ~{totalSourcePdfs} PDF)"
            : "Sélectionnez au moins un produit";
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await ScanProductsAsync();
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
            Title = "Sélectionner le dossier racine TIDES Life (ex: \\\\sppdfc01\\TIDES\\Life)"
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

    private void BtnCheckBankOnly_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _products)
        {
            p.IsSelected = string.Equals(p.Channel, "Bank", StringComparison.OrdinalIgnoreCase) && p.IsReady;
        }
        UpdateSelectionCounters();
    }

    private void BtnCheckBrokerOnly_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _products)
        {
            p.IsSelected = string.Equals(p.Channel, "Broker", StringComparison.OrdinalIgnoreCase) && p.IsReady;
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

    private void TxtFilterProduct_TextChanged(object sender, TextChangedEventArgs e)
    {
        _productsView?.Refresh();
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
            MessageBox.Show("Veuillez cocher au moins un produit (Bank ou Broker) à comparer.", "Aucun produit sélectionné", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var validSelected = selected.Where(p => p.HasSourceFolder && p.HasTargetFolder && p.SourcePdfCount > 0).ToList();
        if (validSelected.Count == 0)
        {
            MessageBox.Show(
                $"Aucun des produits cochés ne contient à la fois un dossier 'Source' avec des PDF et le dossier '{SelectedReleaseName}' sur '{SelectedRootPath}'.\n\n" +
                "Vérifiez que votre connexion au serveur \\\\sppdfc01\\TIDES\\Life est active ou choisissez une autre release (ex: 2026_R9, 2026_R10, 2026_R11).",
                "Dossiers Source / Target introuvables",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SelectedProductsToCompare = validSelected;
        DialogResult = true;
        Close();
    }
}
