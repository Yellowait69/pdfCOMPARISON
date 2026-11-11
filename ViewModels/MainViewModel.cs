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
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _suppressPresetCallback;
    private string _lastTidesRootPath = TidesCampaignService.DefaultNetworkRootPath;
    private string _lastTidesRelease = string.Empty;

    [ObservableProperty] private string _sourceDirectory = string.Empty;
    [ObservableProperty] private string _targetDirectory = string.Empty;
    [ObservableProperty] private string _outputDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotProcessing))]
    private bool _isProcessing;

    public bool IsNotProcessing => !IsProcessing;

    [ObservableProperty] private int _progressValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressBarMax))]
    private int _progressMax;

    public int ProgressBarMax => ProgressMax > 0 ? ProgressMax : 100;

    [ObservableProperty] private string _statusMessage = "Prêt. Sélectionnez deux dossiers ou deux fichiers PDF unitaires.";

    // --- Sélecteur de Profils Préconfigurés ---
    public IReadOnlyList<string> AvailablePresets => _configurationService.AvailablePresets;

    [ObservableProperty]
    private string _selectedPreset = ConfigurationService.PresetStandard;

    // --- Recherche & Filtrage Rapide ---
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _activeStatusFilter = "ALL";

    // --- Compteurs KPI de Synthèse Rapide ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyState))]
    private int _totalPairsCount;

    public bool IsEmptyState => TotalPairsCount == 0;
    [ObservableProperty] private int _identicalPairsCount;
    [ObservableProperty] private int _differentPairsCount;
    [ObservableProperty] private int _criticalPairsCount;
    [ObservableProperty] private int _issuesPairsCount;

    // --- Inspecteur Rapide des Différences ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPairWithDiffs))]
    private DocumentPair? _selectedPair;

    public bool HasSelectedPairWithDiffs => SelectedPair != null && SelectedPair.DiffBlocks.Count > 0;

    public ComparisonProfile ActiveProfile => _configurationService.ActiveProfile;

    public ObservableRangeCollection<DocumentPair> Pairs { get; } = new();
    public ICollectionView FilteredPairsView { get; }

    public MainViewModel()
    {
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

        _suppressPresetCallback = true;
        if (AvailablePresets.Contains(_configurationService.ActiveProfile.ProfileName))
        {
            SelectedPreset = _configurationService.ActiveProfile.ProfileName;
        }
        _suppressPresetCallback = false;

        FilteredPairsView = CollectionViewSource.GetDefaultView(Pairs);
        FilteredPairsView.Filter = FilterDocumentPair;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appData, "PDFComparisonPro");
        Directory.CreateDirectory(appFolder);
        _sessionFilePath = Path.Combine(appFolder, "last_session.json");

        OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "PDF_DiffReports");

        LoadSession();
        RefreshKpiCounters();
    }

    partial void OnSelectedPresetChanged(string value)
    {
        if (_suppressPresetCallback || string.IsNullOrWhiteSpace(value)) return;

        _configurationService.ApplyPreset(value);
        OnPropertyChanged(nameof(ActiveProfile));
        StatusMessage = $"Profil appliqué : {value} ({_configurationService.ActiveProfile.ExclusionZones.Count(z => z.IsEnabled)} zones, {_configurationService.ActiveProfile.RegexRules.Count(r => r.IsEnabled)} règles Regex).";
    }

    partial void OnSearchQueryChanged(string value)
    {
        FilteredPairsView.Refresh();
    }

    partial void OnActiveStatusFilterChanged(string value)
    {
        FilteredPairsView.Refresh();
    }

    [RelayCommand]
    private void SetStatusFilter(string filterMode)
    {
        ActiveStatusFilter = string.IsNullOrWhiteSpace(filterMode) ? "ALL" : filterMode.ToUpperInvariant();
    }

    private bool FilterDocumentPair(object obj)
    {
        if (obj is not DocumentPair pair) return false;

        bool matchesStatus = ActiveStatusFilter switch
        {
            "DIFFERENT" => pair.Status == CompareStatus.Different,
            "CRITICAL" => pair.Status == CompareStatus.Different && (pair.MaxSeverity == DiffSeverity.Critical || pair.CriticalCount > 0),
            "IDENTICAL" => pair.Status == CompareStatus.Identical,
            "ISSUES" => pair.Status is CompareStatus.MissingInTarget or CompareStatus.Error,
            _ => true
        };

        if (!matchesStatus) return false;

        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;

        string q = SearchQuery.Trim();
        if (pair.MatchKey.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            pair.SourcePath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            pair.ProductName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            pair.Channel.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            pair.DisplayProductBadge.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            pair.ErrorMessage.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return pair.DiffBlocks.Any(b =>
            (b.OldText != null && b.OldText.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
            (b.NewText != null && b.NewText.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
            (b.Category != null && b.Category.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private void RefreshKpiCounters()
    {
        TotalPairsCount = Pairs.Count;
        IdenticalPairsCount = Pairs.Count(p => p.Status == CompareStatus.Identical);
        DifferentPairsCount = Pairs.Count(p => p.Status == CompareStatus.Different);
        CriticalPairsCount = Pairs.Count(p => p.Status == CompareStatus.Different && (p.MaxSeverity == DiffSeverity.Critical || p.CriticalCount > 0));
        IssuesPairsCount = Pairs.Count(p => p.Status is CompareStatus.MissingInTarget or CompareStatus.Error);
        OnPropertyChanged(nameof(HasSelectedPairWithDiffs));
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

                if (!string.IsNullOrEmpty(data.OutputDirectory))
                {
                    OutputDirectory = data.OutputDirectory;
                }

                if (!string.IsNullOrWhiteSpace(data.LastTidesRootPath))
                {
                    _lastTidesRootPath = data.LastTidesRootPath;
                }

                if (!string.IsNullOrWhiteSpace(data.LastTidesRelease))
                {
                    _lastTidesRelease = data.LastTidesRelease;
                }

                if (data.Pairs != null)
                {
                    Pairs.ReplaceRange(data.Pairs);

                    if (Pairs.Count > 0)
                    {
                        StatusMessage = $"Session restaurée ({Pairs.Count} documents).";
                        SelectedPair = Pairs.FirstOrDefault(p => p.Status == CompareStatus.Different) ?? Pairs.FirstOrDefault();
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
    /// Action rapide en 1 clic : Approuve manuellement le document sélectionné comme Conforme.
    /// </summary>
    [RelayCommand]
    private void ApproveSelectedPair(DocumentPair? pair)
    {
        var target = pair ?? SelectedPair;
        if (target == null) return;

        target.Status = CompareStatus.Identical;
        target.DiffCount = 0;
        target.InsertionsCount = 0;
        target.DeletionsCount = 0;
        target.CriticalCount = 0;
        target.MaxSeverity = DiffSeverity.None;
        target.SsimScore = 100.0;
        target.ErrorMessage = "Approuvé manuellement (Validé Conforme)";
        target.DiffBlocks.Clear();

        RefreshKpiCounters();
        SaveSession();
        StatusMessage = $"Document '{target.MatchKey}' validé manuellement comme Conforme.";
    }

    /// <summary>
    /// Relance l'analyse complète d'un seul document en ~0.2 seconde (sans relancer tout le lot).
    /// </summary>
    [RelayCommand]
    private async Task RetestPairAsync(DocumentPair? pair)
    {
        var target = pair ?? SelectedPair;
        if (target == null || IsProcessing) return;

        if (string.IsNullOrWhiteSpace(target.SourcePath) || string.IsNullOrWhiteSpace(target.TargetPath) ||
            !File.Exists(target.SourcePath) || !File.Exists(target.TargetPath))
        {
            MessageBox.Show("Les fichiers PDF Source ou Cible de ce document sont introuvables.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsProcessing = true;
            StatusMessage = $"Re-test rapide de '{target.MatchKey}' avec les règles actuelles...";

            await _orchestrator.ProcessPairsAsync(new[] { target }, OutputDirectory, new Progress<int>());

            RefreshKpiCounters();
            SaveSession();
            StatusMessage = $"Re-test terminé pour '{target.MatchKey}' : {target.Status} ({target.DiffCount} écart(s), SSIM {target.SsimScore:0.0}%).";
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
    /// Ouvre la visionneuse visuelle interactive intégrée (Côte à Côte, Superposition Calque, Blink A/B, Heatmap SSIM).
    /// </summary>
    [RelayCommand]
    private void OpenVisualInspector(DocumentPair? pair)
    {
        var target = pair ?? SelectedPair;
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
    /// Action rapide en 1 clic : Crée une règle Regex d'exclusion à partir d'un bloc de différence et retire l'écart.
    /// </summary>
    [RelayCommand]
    private void IgnoreDiffBlockText(DiffSummaryBlock? block)
    {
        if (block == null) return;

        string textToIgnore = !string.IsNullOrWhiteSpace(block.NewText)
            ? block.NewText
            : (block.OldText ?? string.Empty);

        if (string.IsNullOrWhiteSpace(textToIgnore))
        {
            MessageBox.Show("Cet écart est purement visuel (image/mise en page) et ne contient pas de texte à exclure par Regex.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _configurationService.AddQuickIgnoreRegexRule(textToIgnore);

        if (SelectedPair != null && SelectedPair.DiffBlocks.Contains(block))
        {
            SelectedPair.DiffBlocks.Remove(block);
            SelectedPair.DiffCount = SelectedPair.DiffBlocks.Count;
            SelectedPair.CriticalCount = SelectedPair.DiffBlocks.Count(b => b.Severity == DiffSeverity.Critical);
            SelectedPair.MaxSeverity = SelectedPair.DiffBlocks.Count > 0
                ? SelectedPair.DiffBlocks.Max(b => b.Severity)
                : DiffSeverity.None;

            if (SelectedPair.DiffBlocks.Count == 0)
            {
                SelectedPair.Status = CompareStatus.Identical;
                SelectedPair.SsimScore = 100.0;
                SelectedPair.ErrorMessage = "Conforme (Écart exclu par règle rapide)";
            }
            else
            {
                SelectedPair.ErrorMessage = $"{SelectedPair.DiffCount} diff(s) • {SelectedPair.CriticalCount} critique(s)";
            }
        }

        RefreshKpiCounters();
        SaveSession();
        StatusMessage = $"Règle d'exclusion créée dans le profil actif pour ignorer : \"{(textToIgnore.Length > 40 ? textToIgnore.Substring(0, 40) + "..." : textToIgnore)}\".";
    }

    [RelayCommand]
    private void OpenProfileSettings()
    {
        try
        {
            var settingsWindow = new Views.ProfileSettingsWindow(_configurationService);
            if (Application.Current?.MainWindow != null)
            {
                settingsWindow.Owner = Application.Current.MainWindow;
            }
            settingsWindow.ShowDialog();
            OnPropertyChanged(nameof(ActiveProfile));
            StatusMessage = $"Profil actif : {_configurationService.ActiveProfile.ProfileName} ({_configurationService.ActiveProfile.ExclusionZones.Count(z => z.IsEnabled)} zones, {_configurationService.ActiveProfile.RegexRules.Count(r => r.IsEnabled)} règles Regex).";
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
        var target = pair ?? SelectedPair;
        if (target != null && target.HasReport && File.Exists(target.ReportPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(target.ReportPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot open the report: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (target != null && !File.Exists(target.ReportPath))
        {
            MessageBox.Show("The report file has been moved or deleted since the last session.", "File not found", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenSourcePdf(DocumentPair? pair)
    {
        var target = pair ?? SelectedPair;
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
        var target = pair ?? SelectedPair;
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

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new OpenFolderDialog { Title = "Sélectionner le dossier Source (Attendu)" };
        if (dialog.ShowDialog() == true)
        {
            SourceDirectory = dialog.FolderName;
            SaveSession();
        }
    }

    [RelayCommand]
    private void BrowseSourceFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Sélectionner un fichier PDF Source (Comparaison 1-vs-1)",
            Filter = "Fichiers PDF (*.pdf)|*.pdf"
        };
        if (dialog.ShowDialog() == true)
        {
            SourceDirectory = dialog.FileName;
            SaveSession();
        }
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        var dialog = new OpenFolderDialog { Title = "Sélectionner le dossier Cible (Obtenu)" };
        if (dialog.ShowDialog() == true)
        {
            TargetDirectory = dialog.FolderName;
            SaveSession();
        }
    }

    [RelayCommand]
    private void BrowseTargetFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Sélectionner un fichier PDF Cible (Comparaison 1-vs-1)",
            Filter = "Fichiers PDF (*.pdf)|*.pdf"
        };
        if (dialog.ShowDialog() == true)
        {
            TargetDirectory = dialog.FileName;
            SaveSession();
        }
    }

    [RelayCommand]
    private void OpenHtmlReport()
    {
        string htmlReportPath = Path.Combine(OutputDirectory, "Global_Synthesis_Report.html");

        if (File.Exists(htmlReportPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(htmlReportPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir le rapport interactif HTML : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            OpenOutputDirectory();
        }
    }

    [RelayCommand]
    private void OpenCsvReport()
    {
        string csvReportPath = Path.Combine(OutputDirectory, "Global_Audit_Report.csv");

        if (!File.Exists(csvReportPath) && Pairs.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(OutputDirectory);
                var sb = new StringBuilder();
                sb.AppendLine("Canal;Produit;Release;Document;Statut;Score SSIM (%);Page;Severite;Categorie;Action;Texte Attendu (Source);Texte Obtenu (Cible);Rapport Individuel");

                foreach (var p in Pairs)
                {
                    if (p.DiffBlocks.Count == 0)
                    {
                        sb.Append($"\"{p.Channel}\";\"{p.ProductName}\";\"{p.ReleaseName}\";\"{p.MatchKey}\";\"{p.Status}\";\"{p.SsimScore:0.00}\";1;\"Conforme\";\"Aucune\";\"Identique\";\"\";\"\";\"{p.ReportPath}\"")
                          .AppendLine();
                    }
                    else
                    {
                        foreach (var b in p.DiffBlocks)
                        {
                            string oldClean = (b.OldText ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
                            string newClean = (b.NewText ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
                            sb.Append($"\"{p.Channel}\";\"{p.ProductName}\";\"{p.ReleaseName}\";\"{p.MatchKey}\";\"{p.Status}\";\"{p.SsimScore:0.00}\";{b.PageNumber};\"{b.SeverityLabel}\";\"{b.Category}\";\"{b.ChangeTypeLabel}\";\"{oldClean}\";\"{newClean}\";\"{p.ReportPath}\"")
                              .AppendLine();
                        }
                    }
                }

                File.WriteAllText(csvReportPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors de la génération de l'export Excel/CSV : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        if (File.Exists(csvReportPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(csvReportPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir l'export CSV/Excel : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            MessageBox.Show("Aucun rapport CSV disponible. Lancez d'abord une comparaison.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private void OpenOutputDirectory()
    {
        string globalReportPath = Path.Combine(OutputDirectory, "Global_Synthesis_Report.pdf");

        if (File.Exists(globalReportPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(globalReportPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot open the global report: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (Directory.Exists(OutputDirectory))
        {
            Process.Start(new ProcessStartInfo(OutputDirectory) { UseShellExecute = true });
        }
        else
        {
            MessageBox.Show("The reports directory does not exist yet. Please run a comparison first.", "Directory not found", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private void ShareViaOutlook()
    {
        try
        {
            string subject = "PDF Comparison Reports";
            string body = $"Hello,\n\nThe PDF comparison reports have been generated.\nYou can access them in the following directory:\n{OutputDirectory}\n\nBest regards.";

            string mailtoUri = $"mailto:?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";

            Process.Start(new ProcessStartInfo(mailtoUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot open the default mail client: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

    /// <summary>
    /// Ouvre la fenêtre de Campagne Rapide Testing TIDES Life (\\sppdfc01\TIDES\Life : Bank puis Broker)
    /// et automatise la comparaison de tous les produits cochés.
    /// </summary>
    [RelayCommand]
    private async Task OpenTidesCampaignAsync()
    {
        if (IsProcessing) return;

        try
        {
            string defaultRelease = _tidesCampaignService.GetCurrentMonthReleaseName();
            var campaignWin = new Views.TidesCampaignWindow(_tidesCampaignService, _lastTidesRootPath, defaultRelease);
            if (Application.Current?.MainWindow != null)
            {
                campaignWin.Owner = Application.Current.MainWindow;
            }

            if (campaignWin.ShowDialog() != true || campaignWin.SelectedProductsToCompare.Count == 0)
            {
                return;
            }

            _lastTidesRootPath = campaignWin.SelectedRootPath;
            _lastTidesRelease = campaignWin.SelectedReleaseName;

            if (campaignWin.UseVisibleTextOnlyMode)
            {
                _suppressPresetCallback = true;
                _configurationService.ApplyTidesTestingProfile(
                    campaignWin.IgnoreRepeatingDates,
                    campaignWin.IgnoreHeaderFooter);
                SelectedPreset = ConfigurationService.PresetTidesTesting;
                _suppressPresetCallback = false;
                OnPropertyChanged(nameof(ActiveProfile));
            }

            await RunTidesCampaignInternalAsync(
                campaignWin.SelectedProductsToCompare,
                campaignWin.SelectedRootPath,
                campaignWin.SelectedReleaseName,
                campaignWin.WriteToProductOutputs);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur lors du lancement de la campagne TIDES : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RunTidesCampaignInternalAsync(
        List<TidesProductItem> selectedProducts,
        string rootPath,
        string releaseName,
        bool writeToProductOutputs)
    {
        IsProcessing = true;
        Pairs.Clear();
        SelectedPair = null;
        ProgressValue = 0;
        ActiveStatusFilter = "ALL";
        RefreshKpiCounters();

        int bankProductsCount = selectedProducts.Count(p => string.Equals(p.Channel, "Bank", StringComparison.OrdinalIgnoreCase));
        int brokerProductsCount = selectedProducts.Count(p => string.Equals(p.Channel, "Broker", StringComparison.OrdinalIgnoreCase));

        SourceDirectory = $"{rootPath} [Source • {bankProductsCount} Bank + {brokerProductsCount} Broker]";
        TargetDirectory = $"{rootPath} [{releaseName}\\Target]";

        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            StatusMessage = $"Préparation de la campagne TIDES ({releaseName}) : appairage de {selectedProducts.Count} produit(s) (Bank puis Broker)...";

            var matchedPairs = await Task.Run(
                () => _tidesCampaignService.BuildOrderedCampaignPairs(selectedProducts, _fileService),
                _cancellationTokenSource.Token);

            Pairs.ReplaceRange(matchedPairs);
            RefreshKpiCounters();

            var pairsToProcess = matchedPairs.Where(p => p.Status != CompareStatus.MissingInTarget).ToList();
            ProgressMax = pairsToProcess.Count;

            if (ProgressMax == 0)
            {
                StatusMessage = $"Aucune paire PDF valide trouvée dans les {selectedProducts.Count} produit(s) sélectionné(s).";
                return;
            }

            int bankDocsCount = pairsToProcess.Count(p => string.Equals(p.Channel, "Bank", StringComparison.OrdinalIgnoreCase));

            var progress = new Progress<int>(val =>
            {
                ProgressValue = val;
                string phaseLabel = val <= bankDocsCount ? "1/2 • BANK (Prioritaire)" : "2/2 • BROKER";
                StatusMessage = $"[{phaseLabel}] Comparaison en cours : document {val} / {ProgressMax}";
                RefreshKpiCounters();
            });

            StatusMessage = $"Comparaison TIDES ({releaseName}) en cours : {bankDocsCount} PDF Bank puis {ProgressMax - bankDocsCount} PDF Broker...";

            await _orchestrator.ProcessPairsAsync(
                pairsToProcess,
                OutputDirectory,
                progress,
                _cancellationTokenSource.Token,
                copyToProductOutput: writeToProductOutputs);

            if (writeToProductOutputs)
            {
                await Task.Run(() => _tidesCampaignService.ExportPerProductSummaries(Pairs), _cancellationTokenSource.Token);
            }

            // Conserver l'ordre Bank d'abord puis Broker, tout en plaçant les différences en tête de chaque canal
            var sortedCampaignPairs = Pairs
                .OrderBy(p => string.Equals(p.Channel, "Bank", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenByDescending(p => p.CriticalCount)
                .ThenByDescending(p => p.DiffCount)
                .ThenBy(p => p.ProductName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Pairs.ReplaceRange(sortedCampaignPairs);
            RefreshKpiCounters();

            SelectedPair = Pairs.FirstOrDefault(p => p.Status == CompareStatus.Different) ?? Pairs.FirstOrDefault();

            StatusMessage = $"Campagne TIDES ({releaseName}) terminée ! {DifferentPairsCount} différent(s) ({CriticalPairsCount} critique(s)) sur {ProgressMax} PDF ({bankProductsCount} produit(s) Bank, {brokerProductsCount} Broker).";
            SaveSession();

            MessageBox.Show(
                $"Campagne TIDES Life ({releaseName}) terminée avec succès !\n\n" +
                $"• Produits traités : {bankProductsCount} Bank (en 1er) + {brokerProductsCount} Broker\n" +
                $"• Documents conformes (100%) : {IdenticalPairsCount}\n" +
                $"• Documents avec vraies différences : {DifferentPairsCount} (dont {CriticalPairsCount} critique(s))\n" +
                $"• Fichiers manquants / erreurs : {IssuesPairsCount}\n" +
                $"• Rapports générés dans : {OutputDirectory}" +
                (writeToProductOutputs ? $"\n• Copiés également dans chaque dossier <Produit>\\{releaseName}\\Output" : ""),
                "Campagne Testing TIDES Terminée",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            RefreshKpiCounters();
            StatusMessage = "Campagne TIDES annulée par l'utilisateur.";
            MessageBox.Show("La campagne TIDES a été arrêtée.", "Annulé", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            RefreshKpiCounters();
            StatusMessage = $"Erreur campagne TIDES : {ex.Message}";
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
        if (string.IsNullOrWhiteSpace(SourceDirectory) || string.IsNullOrWhiteSpace(TargetDirectory))
        {
            MessageBox.Show("Veuillez sélectionner un dossier ou un fichier PDF pour Attendu et Obtenu.", "Chemins manquants", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool sourceExists = Directory.Exists(SourceDirectory) || File.Exists(SourceDirectory);
        bool targetExists = Directory.Exists(TargetDirectory) || File.Exists(TargetDirectory);

        if (!sourceExists || !targetExists)
        {
            MessageBox.Show("Un ou plusieurs chemins spécifiés (dossier ou fichier PDF) n'existent pas.", "Erreur de chemin", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        IsProcessing = true;
        Pairs.Clear();
        SelectedPair = null;
        ProgressValue = 0;
        ActiveStatusFilter = "ALL";
        RefreshKpiCounters();

        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            StatusMessage = "Analyse et appairage des fichiers PDF...";

            var matchedPairs = await Task.Run(() => _fileService.MatchFiles(SourceDirectory, TargetDirectory), _cancellationTokenSource.Token);

            Pairs.ReplaceRange(matchedPairs);
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

            await _orchestrator.ProcessPairsAsync(pairsToProcess, OutputDirectory, progress, _cancellationTokenSource.Token);

            StatusMessage = "Tri des résultats par criticité...";

            var sortedPairs = Pairs
                .OrderByDescending(p => p.CriticalCount)
                .ThenByDescending(p => p.DiffCount)
                .ToList();

            Pairs.ReplaceRange(sortedPairs);
            RefreshKpiCounters();

            SelectedPair = Pairs.FirstOrDefault(p => p.Status == CompareStatus.Different) ?? Pairs.FirstOrDefault();

            StatusMessage = $"Terminé ! {DifferentPairsCount} document(s) différent(s) ({CriticalPairsCount} avec alerte critique) sur {ProgressMax} comparés.";
            SaveSession();

            MessageBox.Show(
                $"Comparaison terminée avec succès !\n\n" +
                $"• Documents identiques : {IdenticalPairsCount}\n" +
                $"• Documents différents : {DifferentPairsCount} (dont {CriticalPairsCount} critique(s))\n" +
                $"• Rapports générés : PDF + Synthèse Web (.HTML) + Export Excel (.CSV)",
                "Comparaison Terminée",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            RefreshKpiCounters();
            StatusMessage = "Traitement annulé par l'utilisateur.";
            MessageBox.Show("Le processus de comparaison a été arrêté.", "Annulé", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
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
    /// PDFComparison.exe --source "C:\Ref" --target "C:\Cand" [--output "C:\Out"] [--preset "Audit Financier Strict (Montants & Tableaux)"] [--fail-on-critical]
    /// Code de sortie : 0 = Identique, 1 = Différences mineures/majeures, 2 = Différences critiques ou erreurs.
    /// </summary>
    public async Task<int> RunHeadlessCliAsync(string[] args)
    {
        string? src = null, tgt = null, outDir = null, preset = null;
        bool failOnlyOnCritical = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--source", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) src = args[++i];
            else if (string.Equals(a, "--target", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) tgt = args[++i];
            else if (string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) outDir = args[++i];
            else if (string.Equals(a, "--preset", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) preset = args[++i];
            else if (string.Equals(a, "--fail-on-critical", StringComparison.OrdinalIgnoreCase)) failOnlyOnCritical = true;
        }

        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt))
            return 2;

        if (!string.IsNullOrWhiteSpace(preset))
            _configurationService.ApplyPreset(preset);

        SourceDirectory = src;
        TargetDirectory = tgt;
        if (!string.IsNullOrWhiteSpace(outDir))
            OutputDirectory = outDir;

        var matchedPairs = _fileService.MatchFiles(SourceDirectory, TargetDirectory);
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
