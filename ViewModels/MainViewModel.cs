using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PDFComparison.Models;
using PDFComparison.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace PDFComparison.ViewModels;

public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    public void ReplaceRange(IEnumerable<T> collection)
    {
        if (collection == null) throw new ArgumentNullException(nameof(collection));

        Items.Clear();
        foreach (var item in collection)
        {
            Items.Add(item);
        }

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public class AppSessionData
{
    public string SourceDirectory { get; set; } = string.Empty;
    public string TargetDirectory { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string LastTidesRootPath { get; set; } = TidesCampaignService.DefaultNetworkRootPath;
    public string LastTidesRelease { get; set; } = string.Empty;
    public List<DocumentPair> Pairs { get; set; } = new();
}

public partial class MainViewModel : ObservableObject
{
    private readonly IConfigurationService _configurationService;
    private readonly PdfFileService _fileService;
    private readonly PdfComparisonOrchestrator _orchestrator;
    private readonly IGlobalSynthesisReportGenerator _globalReportGenerator;
    private readonly IPdfVisualRegressionService _visualRegressionService;
    private readonly ITidesCampaignService _tidesCampaignService;

    private readonly string _sessionFilePath;
    private readonly bool _hadSavedAppSettingsAtStartup;
    private CancellationTokenSource? _cancellationTokenSource;
    private string _lastTidesRootPath = TidesCampaignService.DefaultNetworkRootPath;
    private string _lastTidesRelease = string.Empty;

    [ObservableProperty] private string _sourceDirectory = string.Empty;
    [ObservableProperty] private string _targetDirectory = string.Empty;
    [ObservableProperty] private string _outputDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotProcessing))]
    [NotifyPropertyChangedFor(nameof(IsInitialEmptyState))]
    [NotifyPropertyChangedFor(nameof(IsAllConformState))]
    [NotifyPropertyChangedFor(nameof(IsNoIssuesState))]
    [NotifyPropertyChangedFor(nameof(IsProcessingEmptyState))]
    private bool _isProcessing;

    public bool IsNotProcessing => !IsProcessing;

    [ObservableProperty] private int _progressValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressBarMax))]
    private int _progressMax;

    public int ProgressBarMax => ProgressMax > 0 ? ProgressMax : 100;

    [ObservableProperty] private string _statusMessage = "Prêt. Glissez un dossier ou un PDF, ou lancez un bouton de scan automatique.";

    // --- Regroupement Canal / Produit / Différence (les documents Compatibles et Manquants ne s'affichent jamais dans le tableau principal) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllConformState))]
    [NotifyPropertyChangedFor(nameof(IsNoIssuesState))]
    private string _activeStatusFilter = "DIFFERENT";

    [ObservableProperty]
    private bool _isGroupedByProductDiff = true;

    // --- Compteurs KPI de Synthèse Rapide ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyState))]
    [NotifyPropertyChangedFor(nameof(IsInitialEmptyState))]
    [NotifyPropertyChangedFor(nameof(IsAllConformState))]
    [NotifyPropertyChangedFor(nameof(IsNoIssuesState))]
    [NotifyPropertyChangedFor(nameof(HasComparedDocuments))]
    [NotifyPropertyChangedFor(nameof(DocumentListButtonText))]
    [NotifyPropertyChangedFor(nameof(AllConformSubtitle))]
    private int _totalPairsCount;

    public bool IsEmptyState => TotalPairsCount == 0;
    public bool HasComparedDocuments => TotalPairsCount > 0;
    public bool HasGroupedDifferences => GroupedChannels.Count > 0;
    public bool IsInitialEmptyState => TotalPairsCount == 0 && !IsProcessing;
    public bool IsProcessingEmptyState => IsProcessing && GroupedChannels.Count == 0;
    public bool IsAllConformState => TotalPairsCount > 0 && !IsProcessing && GroupedChannels.Count == 0;
    public bool IsNoIssuesState => TotalPairsCount > 0 && !IsProcessing && ActiveStatusFilter == "ISSUES" && IssuesPairsCount == 0;

    public string DocumentListButtonText => TotalPairsCount > 0
        ? $"Liste des PDF ({TotalPairsCount})"
        : "Liste des PDF";

    public string AllConformSubtitle => IssuesPairsCount > 0
        ? $"{IdenticalPairsCount} PDF compatible(s) • {IssuesPairsCount} manquant(s). Cliquez sur « Liste des PDF » pour consulter le détail."
        : $"Les {IdenticalPairsCount} PDF comparé(s) sont 100 % compatibles.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllConformSubtitle))]
    private int _identicalPairsCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllConformState))]
    private int _differentPairsCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUniqueDiffsBadge))]
    [NotifyPropertyChangedFor(nameof(UniqueDiffsBadgeText))]
    private int _uniqueDiffsCount;

    public bool HasUniqueDiffsBadge => UniqueDiffsCount > 0;
    public string UniqueDiffsBadgeText => UniqueDiffsCount > 0
        ? $"{UniqueDiffsCount} diff{(UniqueDiffsCount > 1 ? "s" : "")} unique{(UniqueDiffsCount > 1 ? "s" : "")}"
        : string.Empty;

    [ObservableProperty] private int _criticalPairsCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoIssuesState))]
    [NotifyPropertyChangedFor(nameof(AllConformSubtitle))]
    private int _issuesPairsCount;

    // --- Inspecteur Rapide des Différences ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPairWithDiffs))]
    private DocumentPair? _selectedPair;

    public bool HasSelectedPairWithDiffs => SelectedPair != null && SelectedPair.DiffBlocks.Count > 0;

    public ComparisonProfile ActiveProfile => _configurationService.ActiveProfile;

    public ObservableRangeCollection<AutomatedScanProfile> ToolbarScanProfiles { get; } = new();
    public ObservableRangeCollection<DocumentPair> Pairs { get; } = new();
    public ObservableRangeCollection<DocumentPair> DisplayPairs { get; } = new();
    public ObservableRangeCollection<ChannelDiffGroupViewModel> GroupedChannels { get; } = new();
    public ICollectionView FilteredPairsView { get; }

    public MainViewModel()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appData, "PDFComparisonPro");
        Directory.CreateDirectory(appFolder);
        _sessionFilePath = Path.Combine(appFolder, "last_session.json");
        _hadSavedAppSettingsAtStartup = File.Exists(Path.Combine(appFolder, "app_settings.json"));

        _configurationService = new ConfigurationService();
        _fileService = new PdfFileService(_configurationService);

        var textNormalizer = new PdfTextNormalizerService();
        var watermarkFilter = new PdfWatermarkFilterService(_configurationService);
        var intelligentMasking = new PdfIntelligentMaskingService(_configurationService);

        var extractionService = new PdfExtractionService(
            watermarkFilter,
            intelligentMasking,
            textNormalizer,
            _configurationService
        );

        var layoutSanitizer = new PdfLayoutSanitizerService();
        var textSummaryService = new TextDiffSummaryService(_configurationService);

        var visualMatcherService = new VisualHighlightMatcherService(_configurationService);
        _visualRegressionService = new PdfVisualRegressionService(_configurationService);

        var diffAnalyzer = new PdfDiffAnalyzer(
            layoutSanitizer,
            textSummaryService,
            visualMatcherService,
            _visualRegressionService
        );

        var drawingService = new PdfDrawingService();
        var chartService = new PdfChartService();
        var inlineDiffService = new InlineDiffService();
        var htmlReportGen = new HtmlSynthesisReportGenerator(inlineDiffService);

        var individualReportGen = new IndividualReportGenerator(drawingService);
        _globalReportGenerator = new GlobalSynthesisReportGenerator(drawingService, chartService, inlineDiffService, _configurationService, htmlReportGen);

        _orchestrator = new PdfComparisonOrchestrator(
            extractionService,
            diffAnalyzer,
            individualReportGen,
            _globalReportGenerator
        );

        _tidesCampaignService = new TidesCampaignService();
        _lastTidesRelease = _tidesCampaignService.GetCurrentMonthReleaseName();

        FilteredPairsView = CollectionViewSource.GetDefaultView(DisplayPairs);
        FilteredPairsView.Filter = FilterDocumentPair;

        OutputDirectory = GetCleanDefaultReportDirectory();

        LoadSession();
        RefreshToolbarScanProfiles();
        RebuildDisplayPairs();
        RefreshKpiCounters();
    }

    private string GetCleanDefaultReportDirectory()
    {
        string configured = (_configurationService.UserSettings.DefaultReportOutputDirectory ?? string.Empty).Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "PDF_DiffReports");
    }

    public void RefreshToolbarScanProfiles()
    {
        if (_configurationService.UserSettings.ScanProfiles.Count == 0)
        {
            _configurationService.UserSettings.ScanProfiles.Add(AutomatedScanProfile.CreateDefaultEquipeTesting());
            _configurationService.SaveUserSettings();
        }

        ToolbarScanProfiles.ReplaceRange(
            _configurationService.UserSettings.ScanProfiles.Where(p => p.ShowInMainToolbar));
    }

    private string ResolveEffectiveRunOutputDirectory(AutomatedScanProfile? scanProfile, string? contextLabel)
    {
        var settings = _configurationService.UserSettings;
        string baseDir;

        if (scanProfile != null && !string.IsNullOrWhiteSpace(scanProfile.CustomReportOutputDirectory))
        {
            baseDir = scanProfile.CustomReportOutputDirectory.Trim().Trim('"');
        }
        else if (scanProfile == null && !settings.CreateSubfolderPerRun && !string.IsNullOrWhiteSpace(OutputDirectory))
        {
            baseDir = OutputDirectory.Trim().Trim('"');
            if (!string.Equals(settings.DefaultReportOutputDirectory, baseDir, StringComparison.OrdinalIgnoreCase))
            {
                settings.DefaultReportOutputDirectory = baseDir;
                _configurationService.SaveUserSettings();
            }
        }
        else
        {
            baseDir = GetCleanDefaultReportDirectory();
        }

        if (settings.CreateSubfolderPerRun)
        {
            string rawPrefix = string.IsNullOrWhiteSpace(contextLabel) ? "Comparaison" : contextLabel.Trim();
            string safePrefix = string.Join("_", rawPrefix.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(safePrefix)) safePrefix = "Comparaison";
            baseDir = Path.Combine(baseDir, $"{safePrefix}_{DateTime.Now:yyyyMMdd_HHmm}");
        }

        OutputDirectory = baseDir;
        return baseDir;
    }

    partial void OnActiveStatusFilterChanged(string value)
    {
        FilteredPairsView.Refresh();
    }

    partial void OnIsGroupedByProductDiffChanged(bool value)
    {
        SelectedPair = null;
        RebuildDisplayPairs();
        FilteredPairsView.Refresh();
    }

    [RelayCommand]
    private void CloseQuickInspector()
    {
        SelectedPair = null;
    }

    [RelayCommand]
    private void SetStatusFilter(string filterMode)
    {
        ActiveStatusFilter = "DIFFERENT";
    }

    [RelayCommand]
    private void ExpandAllGroups()
    {
        foreach (var ch in GroupedChannels)
        {
            ch.IsExpanded = true;
            foreach (var prod in ch.Products)
            {
                prod.IsExpanded = true;
                foreach (var diff in prod.Differences)
                {
                    diff.IsDetailsExpanded = true;
                }
            }
        }
    }

    [RelayCommand]
    private void CollapseAllGroups()
    {
        foreach (var ch in GroupedChannels)
        {
            ch.IsExpanded = true;
            foreach (var prod in ch.Products)
            {
                prod.IsExpanded = false;
                foreach (var diff in prod.Differences)
                {
                    diff.IsDetailsExpanded = false;
                }
            }
        }
    }

    [RelayCommand]
    private void OpenDocumentList(string? initialFilter)
    {
        try
        {
            var listWin = new Views.DocumentListWindow(Pairs, initialFilter ?? "ALL");
            if (Application.Current?.MainWindow != null)
            {
                listWin.Owner = Application.Current.MainWindow;
            }
            listWin.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d'ouvrir la liste des PDF : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenDiffGroupReport(GroupedDiffRowViewModel? diffRow)
    {
        if (diffRow?.PrimaryPdf == null) return;
        OpenSideBySideAtPage(diffRow.PrimaryPdf.Pair, diffRow.Block.PageNumber);
    }

    [RelayCommand]
    private void OpenImpactedPdfReport(ImpactedPdfItemViewModel? item)
    {
        if (item?.Pair == null) return;
        OpenSideBySideAtPage(item.Pair, item.FirstPageNumber);
    }

    private static void OpenSideBySideAtPage(DocumentPair pair, int pageNumber)
    {
        if (pair == null || string.IsNullOrEmpty(pair.ReportPath)) return;

        string dir = Path.GetDirectoryName(pair.ReportPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(pair.ReportPath);
        string sideBySideHtmlPath = Path.Combine(dir, $"{stem}_SideBySide.html");

        if (File.Exists(sideBySideHtmlPath))
        {
            try
            {
                string uriWithPage = new Uri(sideBySideHtmlPath).AbsoluteUri + $"#page={Math.Max(1, pageNumber)}";
                Process.Start(new ProcessStartInfo(uriWithPage) { UseShellExecute = true });
                return;
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo(sideBySideHtmlPath) { UseShellExecute = true });
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Impossible d'ouvrir la vue Côte à Côte : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        if (File.Exists(pair.ReportPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(pair.ReportPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir le rapport : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void ApproveDiffGroup(GroupedDiffRowViewModel? diffRow)
    {
        if (diffRow == null) return;

        string targetSig = ProductDifferenceGroupingService.GetBlockSignature(diffRow.Block);
        bool isZoneGroup = diffRow.Block.IsScrambledZoneGroup && diffRow.Block.HasPdfBounds;
        int affectedDocs = 0;

        foreach (var imp in diffRow.ImpactedPdfs)
        {
            var p = imp.Pair;
            var toRemove = p.DiffBlocks
                .Where(b =>
                    string.Equals(ProductDifferenceGroupingService.GetBlockSignature(b), targetSig, StringComparison.Ordinal) ||
                    (isZoneGroup && ProductDifferenceGroupingService.AreSameProductGeometricSpot(diffRow.Block, b)))
                .ToList();

            if (toRemove.Count > 0)
            {
                foreach (var r in toRemove) p.DiffBlocks.Remove(r);
                ApplyRemainingBlocksToPair(p, "Approuvé manuellement (Validé Compatible)");
                affectedDocs++;
            }
        }

        SelectedPair = null;
        RebuildDisplayPairs();
        RefreshKpiCounters();
        RegenerateGlobalReportsSafe();
        SaveSession();
        StatusMessage = $"Différence validée manuellement sur {affectedDocs} PDF ({diffRow.ProductName}).";
    }

    [RelayCommand]
    private void ApproveProductGroup(ProductDiffGroupViewModel? prodGroup)
    {
        if (prodGroup == null || prodGroup.ImpactedPairs.Count == 0) return;

        foreach (var p in prodGroup.ImpactedPairs)
        {
            p.DiffBlocks.Clear();
            ApplyRemainingBlocksToPair(p, "Approuvé manuellement (Validé Compatible)");
        }

        SelectedPair = null;
        RebuildDisplayPairs();
        RefreshKpiCounters();
        RegenerateGlobalReportsSafe();
        SaveSession();
        StatusMessage = $"Produit « {prodGroup.ProductName} » validé manuellement ({prodGroup.ImpactedPairs.Count} PDF marqués compatibles).";
    }

    [RelayCommand]
    private async Task RetestProductGroupAsync(ProductDiffGroupViewModel? prodGroup)
    {
        if (prodGroup == null || prodGroup.ImpactedPairs.Count == 0 || IsProcessing) return;

        var pairsToRetest = prodGroup.ImpactedPairs.ToList();
        if (pairsToRetest.Any(p => string.IsNullOrWhiteSpace(p.SourcePath) || string.IsNullOrWhiteSpace(p.TargetPath) ||
                                   !File.Exists(p.SourcePath) || !File.Exists(p.TargetPath)))
        {
            MessageBox.Show("Les fichiers PDF Source ou Cible de ce produit sont introuvables.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsProcessing = true;
            StatusMessage = $"Re-test du produit « {prodGroup.ProductName} » ({pairsToRetest.Count} PDF) avec les règles actuelles...";

            await _orchestrator.ProcessPairsAsync(pairsToRetest, OutputDirectory, new Progress<int>());

            SelectedPair = null;
            RebuildDisplayPairs();
            RefreshKpiCounters();
            RegenerateGlobalReportsSafe();
            SaveSession();
            StatusMessage = $"Re-test terminé pour « {prodGroup.ProductName} » ({pairsToRetest.Count} PDF vérifié(s)).";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur lors du re-test de « {prodGroup.ProductName} » : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void RebuildDisplayPairs(bool forceUngrouped = false)
    {
        if (Pairs.Count == 0)
        {
            DisplayPairs.Clear();
            GroupedChannels.Clear();
            NotifyGroupedTreeChanged();
            return;
        }

        if (!forceUngrouped && IsGroupedByProductDiff)
        {
            DisplayPairs.ReplaceRange(ProductDifferenceGroupingService.BuildGroupedDisplayPairs(Pairs));
        }
        else
        {
            DisplayPairs.ReplaceRange(Pairs.Where(p => p.Status == CompareStatus.Different));
        }

        GroupedChannels.ReplaceRange(ProductDifferenceGroupingService.BuildChannelProductDiffTree(Pairs));
        NotifyGroupedTreeChanged();
    }

    private void NotifyGroupedTreeChanged()
    {
        OnPropertyChanged(nameof(HasGroupedDifferences));
        OnPropertyChanged(nameof(IsAllConformState));
        OnPropertyChanged(nameof(IsProcessingEmptyState));
    }

    private void RegenerateGlobalReportsSafe()
    {
        try
        {
            var summaries = ProductDifferenceGroupingService.BuildSummariesFromPairs(Pairs);
            string targetOut = (OutputDirectory ?? string.Empty).Trim().Trim('"');
            if (Pairs.Count > 0 && !string.IsNullOrWhiteSpace(targetOut))
            {
                Directory.CreateDirectory(targetOut);
                _globalReportGenerator.GenerateGlobalSynthesisReport(summaries, targetOut);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Report refresh warning: {ex.Message}");
        }
    }

    private bool FilterDocumentPair(object obj)
    {
        return obj is DocumentPair pair && pair.Status == CompareStatus.Different;
    }

    private void RefreshKpiCounters()
    {
        TotalPairsCount = Pairs.Count;
        IdenticalPairsCount = Pairs.Count(p => p.Status == CompareStatus.Identical);
        DifferentPairsCount = Pairs.Count(p => p.Status == CompareStatus.Different);
        UniqueDiffsCount = ProductDifferenceGroupingService.CountTotalUniqueDifferences(Pairs);
        CriticalPairsCount = Pairs.Count(p => p.Status == CompareStatus.Different && (p.MaxSeverity == DiffSeverity.Critical || p.CriticalCount > 0));
        IssuesPairsCount = Pairs.Count(p => p.Status is CompareStatus.MissingInTarget or CompareStatus.Error);
        OnPropertyChanged(nameof(HasSelectedPairWithDiffs));
        OnPropertyChanged(nameof(HasGroupedDifferences));
        OnPropertyChanged(nameof(IsAllConformState));
        OnPropertyChanged(nameof(IsProcessingEmptyState));
        FilteredPairsView.Refresh();
    }

    private void SaveSession()
    {
        try
        {
            var data = new AppSessionData
            {
                SourceDirectory = this.SourceDirectory,
                TargetDirectory = this.TargetDirectory,
                OutputDirectory = this.OutputDirectory,
                LastTidesRootPath = this._lastTidesRootPath,
                LastTidesRelease = this._lastTidesRelease,
                Pairs = this.Pairs.ToList()
            };

            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

            string tempPath = _sessionFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _sessionFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Save error: {ex.Message}");
        }
    }

    private void LoadSession()
    {
        if (!File.Exists(_sessionFilePath)) return;

        try
        {
            string json = File.ReadAllText(_sessionFilePath);
            var data = JsonSerializer.Deserialize<AppSessionData>(json);

            if (data != null)
            {
                SourceDirectory = data.SourceDirectory ?? string.Empty;
                TargetDirectory = data.TargetDirectory ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(data.OutputDirectory))
                {
                    if (!_hadSavedAppSettingsAtStartup)
                    {
                        _configurationService.UserSettings.DefaultReportOutputDirectory = data.OutputDirectory;
                        _configurationService.SaveUserSettings();
                        OutputDirectory = data.OutputDirectory;
                    }
                    else if (_configurationService.UserSettings.CreateSubfolderPerRun)
                    {
                        OutputDirectory = data.OutputDirectory;
                    }
                }

                if (!string.IsNullOrWhiteSpace(data.LastTidesRootPath))
                {
                    _lastTidesRootPath = data.LastTidesRootPath;
                    var builtIn = _configurationService.UserSettings.ScanProfiles.FirstOrDefault(p => p.IsBuiltInTides);
                    if (!_hadSavedAppSettingsAtStartup && builtIn != null)
                    {
                        builtIn.RootPath = data.LastTidesRootPath;
                        _configurationService.SaveUserSettings();
                    }
                }

                if (!string.IsNullOrWhiteSpace(data.LastTidesRelease))
                {
                    _lastTidesRelease = data.LastTidesRelease;
                }

                if (data.Pairs != null)
                {
                    foreach (var p in data.Pairs)
                    {
                        string resolvedProd = p.EffectiveProductCode;
                        string resolvedChan = p.EffectiveChannel;
                        p.ProductName = resolvedProd;
                        p.Channel = resolvedChan;
                        foreach (var b in p.DiffBlocks)
                        {
                            if (!string.IsNullOrWhiteSpace(resolvedProd))
                            {
                                b.ProductCode = resolvedProd;
                            }
                        }
                    }

                    Pairs.ReplaceRange(data.Pairs);

                    if (Pairs.Count > 0)
                    {
                        RegenerateGlobalReportsSafe();
                        StatusMessage = $"Session restaurée ({Pairs.Count} documents). Cliquez sur une ligne pour ouvrir sa synthèse.";
                        SelectedPair = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Load error: {ex.Message}");
        }
    }

    /// <summary>
    /// Action rapide en 1 clic : Approuve manuellement le document (ou groupe de PDF) sélectionné comme Conforme.
    /// </summary>
    [RelayCommand]
    private void ApproveSelectedPair(DocumentPair? pair)
    {
        var target = pair ?? SelectedPair;
        if (target == null) return;

        if (target.GroupedSourcePairs.Count > 0)
        {
            var approvedSigs = target.DiffBlocks
                .Select(ProductDifferenceGroupingService.GetBlockSignature)
                .ToHashSet(StringComparer.Ordinal);
            var approvedZoneBlocks = target.DiffBlocks
                .Where(b => b.IsScrambledZoneGroup && b.HasPdfBounds)
                .ToList();

            foreach (var underlying in target.GroupedSourcePairs)
            {
                var remaining = underlying.DiffBlocks
                    .Where(b =>
                        !approvedSigs.Contains(ProductDifferenceGroupingService.GetBlockSignature(b)) &&
                        !approvedZoneBlocks.Any(zb => ProductDifferenceGroupingService.AreSameProductGeometricSpot(zb, b)))
                    .ToList();

                underlying.DiffBlocks.Clear();
                foreach (var r in remaining) underlying.DiffBlocks.Add(r);

                ApplyRemainingBlocksToPair(underlying, "Approuvé manuellement (Validé Conforme)");
            }
        }
        else
        {
            target.DiffBlocks.Clear();
            ApplyRemainingBlocksToPair(target, "Approuvé manuellement (Validé Conforme)");
        }

        SelectedPair = null;
        RebuildDisplayPairs();
        RefreshKpiCounters();
        RegenerateGlobalReportsSafe();
        SaveSession();
        StatusMessage = $"'{target.MatchKey}' validé manuellement comme Conforme.";
    }

    private static void ApplyRemainingBlocksToPair(DocumentPair p, string emptyReason)
    {
        p.DiffCount = p.DiffBlocks.Count;
        p.CriticalCount = p.DiffBlocks.Count(b => b.Severity == DiffSeverity.Critical);
        p.MaxSeverity = p.DiffBlocks.Count > 0 ? p.DiffBlocks.Max(b => b.Severity) : DiffSeverity.None;

        if (p.DiffBlocks.Count == 0)
        {
            p.Status = CompareStatus.Identical;
            p.InsertionsCount = 0;
            p.DeletionsCount = 0;
            p.SsimScore = 100.0;
            p.ErrorMessage = emptyReason;
        }
        else
        {
            p.ErrorMessage = $"{p.DiffCount} différence(s)";
        }
    }

    /// <summary>
    /// Relance l'analyse complète d'un document (ou des PDF regroupés de la ligne) avec les règles actuelles.
    /// </summary>
    [RelayCommand]
    private async Task RetestPairAsync(DocumentPair? pair)
    {
        var target = pair ?? SelectedPair;
        if (target == null || IsProcessing) return;

        var pairsToRetest = target.GroupedSourcePairs.Count > 0
            ? target.GroupedSourcePairs.ToList()
            : new List<DocumentPair> { target };

        if (pairsToRetest.Any(p => string.IsNullOrWhiteSpace(p.SourcePath) || string.IsNullOrWhiteSpace(p.TargetPath) ||
                                   !File.Exists(p.SourcePath) || !File.Exists(p.TargetPath)))
        {
            MessageBox.Show("Les fichiers PDF Source ou Cible de ce document sont introuvables.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsProcessing = true;
            StatusMessage = $"Re-test rapide de '{target.MatchKey}' ({pairsToRetest.Count} PDF) avec les règles actuelles...";

            await _orchestrator.ProcessPairsAsync(pairsToRetest, OutputDirectory, new Progress<int>());

            SelectedPair = null;
            RebuildDisplayPairs();
            RefreshKpiCounters();
            RegenerateGlobalReportsSafe();
            SaveSession();
            StatusMessage = $"Re-test terminé pour '{target.MatchKey}' ({pairsToRetest.Count} PDF vérifié(s)).";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur lors du re-test de '{target.MatchKey}' : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    /// <summary>
    /// Ouvre la visionneuse visuelle interactive intégrée.
    /// </summary>
    [RelayCommand]
    private void OpenVisualInspector(DocumentPair? pair)
    {
        var target = (pair ?? SelectedPair)?.ActiveRepresentativePair;
        if (target == null || string.IsNullOrWhiteSpace(target.SourcePath) || string.IsNullOrWhiteSpace(target.TargetPath))
            return;

        if (!File.Exists(target.SourcePath) || !File.Exists(target.TargetPath))
        {
            MessageBox.Show("Les fichiers PDF Source ou Cible n'existent plus sur le disque.", "Fichier introuvable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            int firstDiffPage = target.DiffBlocks.FirstOrDefault()?.PageNumber ?? 1;
            var viewer = new Views.VisualDiffViewerWindow(target, _visualRegressionService, firstDiffPage);
            if (Application.Current?.MainWindow != null)
            {
                viewer.Owner = Application.Current.MainWindow;
            }
            viewer.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d'ouvrir l'inspecteur visuel : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Action rapide en 1 clic : Crée une règle Regex d'exclusion persistante à partir d'un bloc de différence et retire l'écart sur tous les PDF concernés.
    /// </summary>
    [RelayCommand]
    private void IgnoreDiffBlockText(DiffSummaryBlock? block)
    {
        if (block == null) return;

        string oldRaw = (block.OldText ?? string.Empty).Trim();
        string newRaw = (block.NewText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(oldRaw) && string.IsNullOrWhiteSpace(newRaw))
        {
            MessageBox.Show("Cet écart ne contient pas de texte à exclure par règle Regex.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _configurationService.AddQuickIgnoreDiffBlockRule(block);

        string targetSig = ProductDifferenceGroupingService.GetBlockSignature(block);
        int affectedDocs = 0;

        foreach (var p in Pairs.Where(x => x.Status == CompareStatus.Different && x.DiffBlocks.Count > 0))
        {
            var matching = p.DiffBlocks
                .Where(b => string.Equals(ProductDifferenceGroupingService.GetBlockSignature(b), targetSig, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matching.Count > 0)
            {
                foreach (var m in matching) p.DiffBlocks.Remove(m);
                ApplyRemainingBlocksToPair(p, "Conforme (Écart exclu par règle Regex)");
                affectedDocs++;
            }
        }

        RebuildDisplayPairs();
        if (SelectedPair != null)
        {
            string prevKey = SelectedPair.ProductGroupKey;
            SelectedPair = DisplayPairs.FirstOrDefault(d => d.Status == CompareStatus.Different && d.ProductGroupKey == prevKey);
        }

        RefreshKpiCounters();
        RegenerateGlobalReportsSafe();
        SaveSession();

        string preview = !string.IsNullOrWhiteSpace(oldRaw) && !string.IsNullOrWhiteSpace(newRaw)
            ? $"{oldRaw} ➔ {newRaw}"
            : (!string.IsNullOrWhiteSpace(newRaw) ? newRaw : oldRaw);
        if (preview.Length > 45) preview = preview.Substring(0, 45) + "…";

        StatusMessage = $"Règle enregistrée de façon permanente ({affectedDocs} PDF mis à jour) : « {preview} ».";
    }

    /// <summary>
    /// Action rapide en 1 clic : Crée une zone d'exclusion géométrique ciblée sur le produit (ou globale si aucun produit n'est détecté)
    /// à l'emplacement exact de l'écart (idéal pour les données scramblées : adresses, noms, IBAN toujours situés au même endroit).
    /// </summary>
    [RelayCommand]
    private void IgnoreDiffBlockZone(DiffSummaryBlock? block)
    {
        if (block == null || !block.HasZoneRuleSupport)
        {
            MessageBox.Show("Cet écart ne contient pas de coordonnées géométriques exploitables.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string targetProduct = !string.IsNullOrWhiteSpace(block.ProductCode)
            ? block.ProductCode.Trim()
            : (SelectedPair?.EffectiveProductCode ?? string.Empty);

        var createdZone = _configurationService.AddQuickIgnoreZoneRule(block, targetProduct);
        if (createdZone == null) return;

        int affectedDocs = 0;
        foreach (var p in Pairs.Where(x => x.Status == CompareStatus.Different && x.DiffBlocks.Count > 0))
        {
            if (!createdZone.AppliesToProduct(p.EffectiveProductCode, p.EffectiveChannel, p.SourcePath, p.TargetPath))
                continue;

            var matching = p.DiffBlocks
                .Where(b =>
                    createdZone.AppliesToPage(b.PageNumber, Math.Max(b.PageNumber, 1)) &&
                    ((b.HasPdfBounds && createdZone.IntersectsPdfBox(b.PdfLeft, b.PdfBottom, b.PdfRight, b.PdfTop, b.PdfPageWidth, b.PdfPageHeight)) ||
                     ProductDifferenceGroupingService.AreSameProductGeometricSpot(block, b) ||
                     ProductDifferenceGroupingService.AreSameDifference(block, b)))
                .ToList();

            if (matching.Count > 0)
            {
                foreach (var m in matching) p.DiffBlocks.Remove(m);
                string scopeNote = createdZone.IsProductSpecificRule ? $"zone produit {createdZone.ProductFilter}" : "zone globale";
                ApplyRemainingBlocksToPair(p, $"Conforme (Exclu par {scopeNote})");
                affectedDocs++;
            }
        }

        RebuildDisplayPairs();
        if (SelectedPair != null)
        {
            string prevKey = SelectedPair.ProductGroupKey;
            SelectedPair = DisplayPairs.FirstOrDefault(d => d.Status == CompareStatus.Different && d.ProductGroupKey == prevKey);
        }

        RefreshKpiCounters();
        RegenerateGlobalReportsSafe();
        SaveSession();

        string scopeLabel = createdZone.IsProductSpecificRule
            ? $"pour le produit « {createdZone.ProductFilter} »"
            : "globalement";
        StatusMessage = $"Zone géométrique enregistrée (Page {block.PageNumber}) {scopeLabel} — {affectedDocs} PDF mis à jour.";
    }

    [RelayCommand]
    private void OpenAppSettings()
    {
        try
        {
            var settingsWin = new Views.AppSettingsWindow(_configurationService);
            if (Application.Current?.MainWindow != null)
            {
                settingsWin.Owner = Application.Current.MainWindow;
            }

            if (settingsWin.ShowDialog() == true)
            {
                RefreshToolbarScanProfiles();

                string updatedDefaultOut = GetCleanDefaultReportDirectory();
                if (!_configurationService.UserSettings.CreateSubfolderPerRun || string.IsNullOrWhiteSpace(OutputDirectory))
                {
                    OutputDirectory = updatedDefaultOut;
                    if (Pairs.Count > 0)
                    {
                        RegenerateGlobalReportsSafe();
                    }
                }

                SaveSession();
                StatusMessage = $"Paramètres enregistrés ({ToolbarScanProfiles.Count} bouton(s) de scan actif(s) • Rapports : {OutputDirectory}).";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d'ouvrir les paramètres : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenProfileSettings()
    {
        try
        {
            var knownProducts = Pairs
                .Select(p => p.EffectiveProductCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var contextPair = SelectedPair?.ActiveRepresentativePair
                              ?? Pairs.FirstOrDefault(p => p.Status == CompareStatus.Different)
                              ?? Pairs.FirstOrDefault(p => p.HasTargetFile || p.HasSourceFile);

            string? initialProduct = contextPair?.EffectiveProductCode;
            string? initialSamplePdf = contextPair?.TargetPath ?? contextPair?.SourcePath;

            var settingsWindow = new Views.ProfileSettingsWindow(_configurationService, knownProducts, initialProduct, initialSamplePdf);
            if (Application.Current?.MainWindow != null)
            {
                settingsWindow.Owner = Application.Current.MainWindow;
            }
            settingsWindow.ShowDialog();
            _configurationService.SyncActiveToGeneralIfNeeded();
            OnPropertyChanged(nameof(ActiveProfile));
            StatusMessage = $"Règles enregistrées ({_configurationService.ActiveProfile.RegexRules.Count(r => r.IsEnabled)} règle(s) métier/Regex active(s), {_configurationService.ActiveProfile.ExclusionZones.Count(z => z.IsEnabled)} zone(s) active(s)).";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d'ouvrir la configuration des exclusions : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenDocumentation()
    {
        try
        {
            var docWindow = new Views.DocumentationWindow();
            docWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d'ouvrir la documentation : {ex.Message}\n\nAvez-vous bien installé le package NuGet 'MdXaml' ?", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenReport(DocumentPair? pair)
    {
        var target = (pair ?? SelectedPair)?.ActiveRepresentativePair;
        if (target != null && !string.IsNullOrEmpty(target.ReportPath))
        {
            string dir = Path.GetDirectoryName(target.ReportPath) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(target.ReportPath);
            string sideBySideHtmlPath = Path.Combine(dir, $"{stem}_SideBySide.html");

            // Privilégier l'ouverture de la vue Côte-à-Côte au même niveau (Source à gauche | Target à droite)
            string pathToOpen = File.Exists(sideBySideHtmlPath) ? sideBySideHtmlPath : target.ReportPath;

            if (File.Exists(pathToOpen))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(pathToOpen) { UseShellExecute = true });
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Cannot open the report: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        if (target != null && !File.Exists(target.ReportPath))
        {
            MessageBox.Show("The report file has been moved or deleted since the last session.", "File not found", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenSourcePdf(DocumentPair? pair)
    {
        var target = (pair ?? SelectedPair)?.ActiveRepresentativePair;
        if (target != null && File.Exists(target.SourcePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(target.SourcePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir le PDF Source : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void OpenTargetPdf(DocumentPair? pair)
    {
        var target = (pair ?? SelectedPair)?.ActiveRepresentativePair;
        if (target != null && !string.IsNullOrEmpty(target.TargetPath) && File.Exists(target.TargetPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(target.TargetPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir le PDF Target : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// Sélecteur unifié qui permet de choisir soit un dossier entier soit un fichier PDF en une seule fenêtre.
    /// </summary>
    private static string? PickFolderOrPdf(string title, string? initialPath)
    {
        const string folderSentinel = "[Sélectionner ce dossier]";
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Dossier ou Fichier PDF (*.pdf)|*.pdf|Tous les fichiers (*.*)|*.*",
            ValidateNames = false,
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = folderSentinel
        };

        string cleaned = (initialPath ?? string.Empty).Trim().Trim('"');
        if (Directory.Exists(cleaned))
        {
            dialog.InitialDirectory = cleaned;
        }
        else if (File.Exists(cleaned))
        {
            string? dir = Path.GetDirectoryName(cleaned);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                dialog.InitialDirectory = dir;
            }
        }

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        string chosen = dialog.FileName;
        if (File.Exists(chosen))
        {
            return chosen;
        }

        string? folder = Path.GetDirectoryName(chosen);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            return folder;
        }

        return Directory.Exists(chosen) ? chosen : null;
    }

    [RelayCommand]
    private void BrowseSourcePath()
    {
        string? picked = PickFolderOrPdf("Source (Attendu) — Choisissez un dossier ou cliquez sur un PDF", SourceDirectory);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            SourceDirectory = picked;
            SaveSession();
        }
    }

    [RelayCommand]
    private void BrowseTargetPath()
    {
        string? picked = PickFolderOrPdf("Cible (Obtenu) — Choisissez un dossier ou cliquez sur un PDF", TargetDirectory);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            TargetDirectory = picked;
            SaveSession();
        }
    }

    [RelayCommand]
    private void BrowseOutputPath()
    {
        var dialog = new OpenFolderDialog { Title = "Sélectionner le dossier personnalisé des rapports" };
        if (Directory.Exists(OutputDirectory))
        {
            dialog.InitialDirectory = OutputDirectory;
        }
        if (dialog.ShowDialog() == true)
        {
            OutputDirectory = dialog.FolderName;
            _configurationService.UserSettings.DefaultReportOutputDirectory = dialog.FolderName;
            _configurationService.SaveUserSettings();

            if (Pairs.Count > 0)
            {
                RegenerateGlobalReportsSafe();
            }

            SaveSession();
            StatusMessage = $"Dossier des rapports personnalisé enregistré : {OutputDirectory}";
        }
    }

    [RelayCommand]
    private void OpenHtmlReport()
    {
        OutputDirectory = (OutputDirectory ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = GetCleanDefaultReportDirectory();
        }

        string htmlReportPath = Path.Combine(OutputDirectory, "Global_Synthesis_Report.html");

        if (Pairs.Count > 0)
        {
            RegenerateGlobalReportsSafe();
        }

        if (File.Exists(htmlReportPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(htmlReportPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir la Synthèse : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (Directory.Exists(OutputDirectory))
        {
            Process.Start(new ProcessStartInfo(OutputDirectory) { UseShellExecute = true });
        }
        else
        {
            MessageBox.Show("Aucune synthèse disponible pour le moment. Lancez d'abord une comparaison.", "Synthèse", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private void ShareViaOutlook()
    {
        try
        {
            var settings = _configurationService.UserSettings;
            string currentOut = (OutputDirectory ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(currentOut))
            {
                currentOut = GetCleanDefaultReportDirectory();
                OutputDirectory = currentOut;
            }
            else if (!settings.CreateSubfolderPerRun &&
                     !string.Equals(settings.DefaultReportOutputDirectory, currentOut, StringComparison.OrdinalIgnoreCase))
            {
                settings.DefaultReportOutputDirectory = currentOut;
                _configurationService.SaveUserSettings();
            }

            string htmlPath = Path.Combine(currentOut, "Global_Synthesis_Report.html");
            if (Pairs.Count > 0)
            {
                RegenerateGlobalReportsSafe();
            }

            string subject = string.IsNullOrWhiteSpace(settings.ShareMailSubject)
                ? "Synthèse Comparaison PDF"
                : settings.ShareMailSubject.Trim();

            string intro = string.IsNullOrWhiteSpace(settings.ShareMailIntro)
                ? "Bonjour,\n\nLa synthèse de comparaison PDF a été générée et mise à jour."
                : settings.ShareMailIntro.Trim();

            var bodyBuilder = new StringBuilder();
            bodyBuilder.AppendLine(intro);
            bodyBuilder.AppendLine();

            if (Pairs.Count > 0)
            {
                bodyBuilder.AppendLine(
                    $"Résumé : {IdenticalPairsCount} conforme(s) • {UniqueDiffsCount} différence(s) unique(s) (sur {DifferentPairsCount} PDF) • {IssuesPairsCount} manquant(s).");
                bodyBuilder.AppendLine();
            }

            if (File.Exists(htmlPath))
            {
                bodyBuilder.AppendLine($"• Synthèse interactive : {htmlPath}");
            }
            bodyBuilder.AppendLine($"• Dossier des rapports : {currentOut}");
            bodyBuilder.AppendLine();
            bodyBuilder.Append("Cordialement.");

            string cleanRecipients = string.Join(
                ";",
                (settings.ShareMailRecipients ?? string.Empty)
                    .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            string mailtoUri = $"mailto:{cleanRecipients}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(bodyBuilder.ToString())}";

            Process.Start(new ProcessStartInfo(mailtoUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d'ouvrir la messagerie : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void CancelComparison()
    {
        if (IsProcessing && _cancellationTokenSource != null)
        {
            StatusMessage = "Annulation du traitement en cours...";
            _cancellationTokenSource.Cancel();
        }
    }

    [RelayCommand]
    private Task OpenTidesCampaignAsync()
    {
        var defaultProfile = _configurationService.UserSettings.ScanProfiles.FirstOrDefault(p => p.IsBuiltInTides)
                             ?? _configurationService.UserSettings.ScanProfiles.FirstOrDefault();
        return OpenScanCampaignAsync(defaultProfile);
    }

    /// <summary>
    /// Ouvre la fenêtre d'un bouton de scan automatique (Équipe Testing ou bouton personnalisé)
    /// et lance la comparaison selon sa logique de dossiers, sa stratégie d'appairage PDF et ses règles.
    /// </summary>
    [RelayCommand]
    private async Task OpenScanCampaignAsync(AutomatedScanProfile? scanProfile)
    {
        if (IsProcessing) return;

        var profile = scanProfile
                      ?? _configurationService.UserSettings.ScanProfiles.FirstOrDefault()
                      ?? AutomatedScanProfile.CreateDefaultEquipeTesting();

        try
        {
            string fallbackRelease = !string.IsNullOrWhiteSpace(_lastTidesRelease)
                ? _lastTidesRelease
                : _tidesCampaignService.GetCurrentMonthReleaseName();

            var campaignWin = new Views.TidesCampaignWindow(
                _tidesCampaignService,
                profile,
                _configurationService,
                fallbackRelease);

            if (Application.Current?.MainWindow != null)
            {
                campaignWin.Owner = Application.Current.MainWindow;
            }

            bool confirmed = campaignWin.ShowDialog() == true;

            // Rafraîchir la barre d'outils au cas où l'utilisateur a modifié/créé des boutons via "Configurer"
            RefreshToolbarScanProfiles();

            if (!confirmed || campaignWin.SelectedProductsToCompare.Count == 0)
            {
                return;
            }

            // Mémoriser les derniers chemins et options sur ce bouton
            profile.RootPath = campaignWin.SelectedRootPath;
            if (profile.TopologyMode == ScanTopologyMode.MirrorTrees)
            {
                profile.SecondaryTargetRootPath = campaignWin.SelectedSecondaryTargetPath;
            }

            bool hasRelease = profile.TopologyMode == ScanTopologyMode.ProductSubfolders && profile.UseReleaseFolder;
            if (hasRelease && !string.IsNullOrWhiteSpace(campaignWin.SelectedReleaseName))
            {
                profile.DefaultReleaseName = campaignWin.SelectedReleaseName;
                if (profile.IsBuiltInTides)
                {
                    _lastTidesRelease = campaignWin.SelectedReleaseName;
                    _lastTidesRootPath = campaignWin.SelectedRootPath;
                }
            }

            profile.DefaultIgnoreDates = campaignWin.IgnoreRepeatingDates;
            profile.DefaultIgnoreHeaderFooter = campaignWin.IgnoreHeaderFooter;
            profile.DefaultWriteProductOutputs = campaignWin.WriteToProductOutputs;
            _configurationService.SaveUserSettings();

            // Activer les règles du bouton (règles dédiées ou règles générales)
            _configurationService.ActivateScanProfile(
                profile,
                campaignWin.IgnoreRepeatingDates,
                campaignWin.IgnoreHeaderFooter);
            OnPropertyChanged(nameof(ActiveProfile));

            await RunScanCampaignInternalAsync(
                profile,
                campaignWin.SelectedProductsToCompare,
                campaignWin.SelectedRootPath,
                campaignWin.SelectedSecondaryTargetPath,
                campaignWin.SelectedReleaseName,
                campaignWin.WriteToProductOutputs);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur lors du lancement de « {profile.ButtonName} » : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RunScanCampaignInternalAsync(
        AutomatedScanProfile scanProfile,
        List<TidesProductItem> selectedProducts,
        string rootPath,
        string secondaryTargetPath,
        string releaseName,
        bool writeToProductOutputs)
    {
        IsProcessing = true;
        Pairs.Clear();
        DisplayPairs.Clear();
        GroupedChannels.Clear();
        SelectedPair = null;
        ProgressValue = 0;
        ActiveStatusFilter = "DIFFERENT";
        RefreshKpiCounters();

        bool hasRelease = scanProfile.TopologyMode == ScanTopologyMode.ProductSubfolders &&
                          scanProfile.UseReleaseFolder &&
                          !string.IsNullOrWhiteSpace(releaseName);

        string runTitle = hasRelease
            ? $"{scanProfile.ButtonName} ({releaseName})"
            : scanProfile.ButtonName;

        string targetOutDir = ResolveEffectiveRunOutputDirectory(
            scanProfile,
            hasRelease ? $"{scanProfile.ButtonName}_{releaseName}" : scanProfile.ButtonName);

        if (scanProfile.TopologyMode == ScanTopologyMode.MirrorTrees)
        {
            SourceDirectory = rootPath;
            TargetDirectory = secondaryTargetPath;
        }
        else if (hasRelease)
        {
            SourceDirectory = $"{rootPath} [{scanProfile.SourceSubfolderTemplate} • {selectedProducts.Count} dossier(s)]";
            TargetDirectory = $"{rootPath} [{releaseName}]";
        }
        else
        {
            SourceDirectory = $"{rootPath} [{scanProfile.ButtonName} • {selectedProducts.Count} dossier(s)]";
            TargetDirectory = $"{rootPath} [Cible auto-détectée]";
        }

        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            StatusMessage = $"Préparation {runTitle} : appairage des PDF sur {selectedProducts.Count} dossier(s)/produit(s)...";

            var pairingOptions = scanProfile.ToPairingOptions(_configurationService.ActiveProfile.FileMatchingRegex);

            var matchedPairs = await Task.Run(
                () => _tidesCampaignService.BuildOrderedCampaignPairs(selectedProducts, _fileService, pairingOptions),
                _cancellationTokenSource.Token);

            Pairs.ReplaceRange(matchedPairs);
            RebuildDisplayPairs(forceUngrouped: true);
            RefreshKpiCounters();

            var pairsToProcess = matchedPairs.Where(p => p.Status != CompareStatus.MissingInTarget).ToList();
            ProgressMax = pairsToProcess.Count;

            if (ProgressMax == 0)
            {
                StatusMessage = $"Aucune paire PDF valide trouvée dans les {selectedProducts.Count} dossier(s) sélectionné(s).";
                return;
            }

            var progress = new Progress<int>(val =>
            {
                ProgressValue = val;
                StatusMessage = $"{runTitle} : document {val} / {ProgressMax}";
                RefreshKpiCounters();
            });

            StatusMessage = $"{runTitle} en cours : {ProgressMax} PDF à comparer...";

            await _orchestrator.ProcessPairsAsync(
                pairsToProcess,
                targetOutDir,
                progress,
                _cancellationTokenSource.Token,
                copyToProductOutput: writeToProductOutputs);

            if (writeToProductOutputs)
            {
                await Task.Run(() => _tidesCampaignService.ExportPerProductSummaries(Pairs), _cancellationTokenSource.Token);
            }

            var priorityByChannel = selectedProducts
                .GroupBy(p => p.Channel ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Min(x => x.SortPriority), StringComparer.OrdinalIgnoreCase);

            var sortedCampaignPairs = Pairs
                .OrderBy(p => priorityByChannel.TryGetValue(p.Channel ?? string.Empty, out int prio) ? prio : 99)
                .ThenByDescending(p => p.DiffCount)
                .ThenBy(p => p.ProductName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Pairs.ReplaceRange(sortedCampaignPairs);
            SelectedPair = null;
            RebuildDisplayPairs();
            RefreshKpiCounters();

            StatusMessage = $"{runTitle} terminée : {UniqueDiffsCount} différence(s) unique(s) sur {DifferentPairsCount} PDF • {IdenticalPairsCount} conforme(s).";
            SaveSession();

            MessageBox.Show(
                $"{runTitle} terminée !\n\n" +
                $"• Dossiers / Produits traités : {selectedProducts.Count}\n" +
                $"• Conformes : {IdenticalPairsCount}\n" +
                $"• Différences uniques : {UniqueDiffsCount} (sur {DifferentPairsCount} PDF)\n" +
                $"• Manquant : {IssuesPairsCount}\n" +
                $"• Dossier rapports : {OutputDirectory}",
                $"{scanProfile.ButtonName} — Terminé",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            RebuildDisplayPairs();
            RefreshKpiCounters();
            StatusMessage = $"{runTitle} annulée par l'utilisateur.";
            MessageBox.Show($"La comparaison « {scanProfile.ButtonName} » a été arrêtée.", "Annulé", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            RebuildDisplayPairs();
            RefreshKpiCounters();
            StatusMessage = $"Erreur {runTitle} : {ex.Message}";
            MessageBox.Show(ex.Message, "Erreur Critique", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private async Task StartComparisonAsync()
    {
        SourceDirectory = (SourceDirectory ?? string.Empty).Trim().Trim('"');
        TargetDirectory = (TargetDirectory ?? string.Empty).Trim().Trim('"');
        OutputDirectory = (OutputDirectory ?? string.Empty).Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(SourceDirectory) || string.IsNullOrWhiteSpace(TargetDirectory))
        {
            MessageBox.Show("Veuillez glisser, coller ou sélectionner un dossier ou un PDF pour Source et Cible.", "Chemins manquants", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool sourceExists = Directory.Exists(SourceDirectory) || File.Exists(SourceDirectory);
        bool targetExists = Directory.Exists(TargetDirectory) || File.Exists(TargetDirectory);

        if (!sourceExists || !targetExists)
        {
            MessageBox.Show("Un ou plusieurs chemins spécifiés (dossier ou fichier PDF) n'existent pas.", "Erreur de chemin", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Utiliser les règles générales pour une comparaison directe
        _configurationService.UseGeneralProfile();
        OnPropertyChanged(nameof(ActiveProfile));

        string targetOutDir = ResolveEffectiveRunOutputDirectory(scanProfile: null, contextLabel: null);

        IsProcessing = true;
        Pairs.Clear();
        DisplayPairs.Clear();
        GroupedChannels.Clear();
        SelectedPair = null;
        ProgressValue = 0;
        ActiveStatusFilter = "DIFFERENT";
        RefreshKpiCounters();

        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            StatusMessage = "Analyse et appairage automatique des PDF (même si les noms diffèrent)...";

            var pairingOptions = new PdfPairingOptions
            {
                Strategy = _configurationService.UserSettings.ManualComparePairingStrategy,
                CustomRegexPattern = _configurationService.ActiveProfile.FileMatchingRegex
            };

            var matchedPairs = await Task.Run(
                () => _fileService.MatchFiles(SourceDirectory, TargetDirectory, pairingOptions: pairingOptions),
                _cancellationTokenSource.Token);

            Pairs.ReplaceRange(matchedPairs);
            RebuildDisplayPairs(forceUngrouped: true);
            RefreshKpiCounters();

            var pairsToProcess = matchedPairs.Where(p => p.Status != CompareStatus.MissingInTarget).ToList();
            ProgressMax = pairsToProcess.Count;

            if (ProgressMax == 0)
            {
                StatusMessage = "Aucune paire valide trouvée pour la comparaison.";
                return;
            }

            StatusMessage = $"Comparaison de {ProgressMax} document(s)...";

            var progress = new Progress<int>(value =>
            {
                ProgressValue = value;
                StatusMessage = $"Analyse en cours : document {value} sur {ProgressMax}";
                RefreshKpiCounters();
            });

            await _orchestrator.ProcessPairsAsync(pairsToProcess, targetOutDir, progress, _cancellationTokenSource.Token);

            StatusMessage = "Regroupement des résultats...";

            var sortedPairs = Pairs
                .OrderByDescending(p => p.DiffCount)
                .ToList();

            Pairs.ReplaceRange(sortedPairs);
            SelectedPair = null;
            RebuildDisplayPairs();
            RefreshKpiCounters();

            StatusMessage = $"Terminé : {UniqueDiffsCount} différence(s) unique(s) sur {DifferentPairsCount} PDF • {IdenticalPairsCount} conforme(s).";
            SaveSession();

            MessageBox.Show(
                $"Comparaison terminée !\n\n" +
                $"• Conformes : {IdenticalPairsCount}\n" +
                $"• Différences uniques : {UniqueDiffsCount} (sur {DifferentPairsCount} PDF)\n" +
                $"• Manquant : {IssuesPairsCount}\n" +
                $"• Dossier rapports : {OutputDirectory}",
                "Comparaison Terminée",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            RebuildDisplayPairs();
            RefreshKpiCounters();
            StatusMessage = "Traitement annulé par l'utilisateur.";
            MessageBox.Show("Le processus de comparaison a été arrêté.", "Annulé", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            RebuildDisplayPairs();
            RefreshKpiCounters();
            StatusMessage = $"Erreur globale : {ex.Message}";
            MessageBox.Show(ex.Message, "Erreur Critique", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    /// <summary>
    /// Exécution en mode ligne de commande (CLI Headless) pour pipelines CI/CD :
    /// PDFComparison.exe --source "C:\Ref" --target "C:\Cand" [--output "C:\Out"] [--fail-on-critical]
    /// </summary>
    public async Task<int> RunHeadlessCliAsync(string[] args)
    {
        string? src = null, tgt = null, outDir = null;
        bool failOnlyOnCritical = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--source", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) src = args[++i];
            else if (string.Equals(a, "--target", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) tgt = args[++i];
            else if (string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) outDir = args[++i];
            else if (string.Equals(a, "--fail-on-critical", StringComparison.OrdinalIgnoreCase)) failOnlyOnCritical = true;
        }

        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt))
            return 2;

        SourceDirectory = src;
        TargetDirectory = tgt;
        OutputDirectory = !string.IsNullOrWhiteSpace(outDir) ? outDir : GetCleanDefaultReportDirectory();

        var matchedPairs = _fileService.MatchFiles(SourceDirectory, TargetDirectory, pairingOptions: PdfPairingOptions.DefaultSmart);
        var validPairs = matchedPairs.Where(p => p.Status != CompareStatus.MissingInTarget).ToList();

        await _orchestrator.ProcessPairsAsync(validPairs, OutputDirectory, new Progress<int>());

        int criticals = matchedPairs.Count(p => p.Status == CompareStatus.Different && p.CriticalCount > 0);
        int errors = matchedPairs.Count(p => p.Status is CompareStatus.Error or CompareStatus.MissingInTarget);
        int diffs = matchedPairs.Count(p => p.Status == CompareStatus.Different);

        if (criticals > 0 || errors > 0) return 2;
        if (diffs > 0 && !failOnlyOnCritical) return 1;
        return 0;
    }
}
