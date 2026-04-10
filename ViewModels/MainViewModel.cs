using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PDFComparison.Models;
using PDFComparison.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PDFComparison.ViewModels;

// Local class to structure save data
public class AppSessionData
{
    public string SourceDirectory { get; set; } = string.Empty;
    public string TargetDirectory { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public List<DocumentPair> Pairs { get; set; } = new();
}

public partial class MainViewModel : ObservableObject
{
    private readonly PdfFileService _fileService;
    private readonly PdfComparisonOrchestrator _orchestrator;

    private readonly string _sessionFilePath; // Path of the save file
    private CancellationTokenSource? _cancellationTokenSource; // Pour annuler le traitement

    [ObservableProperty] private string _sourceDirectory = string.Empty;
    [ObservableProperty] private string _targetDirectory = string.Empty;
    [ObservableProperty] private string _outputDirectory = string.Empty;

    // Automatically notifies the UI to recalculate "IsNotProcessing" when "IsProcessing" changes
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotProcessing))]
    private bool _isProcessing;

    public bool IsNotProcessing => !IsProcessing;

    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMax;
    [ObservableProperty] private string _statusMessage = "Ready. Please select the directories.";

    public ObservableCollection<DocumentPair> Pairs { get; } = new();

    public MainViewModel()
    {
        _fileService = new PdfFileService();

        // ==============================================================
        // ASSEMBLAGE DE LA NOUVELLE ARCHITECTURE MODULAIRE
        // ==============================================================

        // 1. Services d'extraction de données et de nettoyage
        var textNormalizer = new PdfTextNormalizerService();
        var watermarkFilter = new PdfWatermarkFilterService();
        var intelligentMasking = new PdfIntelligentMaskingService();

        var extractionService = new PdfExtractionService(
            watermarkFilter,
            intelligentMasking,
            textNormalizer
        );

        // 2. Services d'analyse et de comparaison (Diff)
        var semanticService = new SemanticSimilarityService();
        var layoutSanitizer = new PdfLayoutSanitizerService();
        var textSummaryService = new TextDiffSummaryService();
        var visualMatcherService = new VisualHighlightMatcherService(semanticService);

        var diffAnalyzer = new PdfDiffAnalyzer(
            layoutSanitizer,
            textSummaryService,
            visualMatcherService
        );

        // 3. NOUVEAU : Services de génération de rapports découpés
        var drawingService = new PdfDrawingService();
        var chartService = new PdfChartService();
        var inlineDiffService = new InlineDiffService();

        var individualReportGen = new IndividualReportGenerator(drawingService);
        var globalReportGen = new GlobalSynthesisReportGenerator(drawingService, chartService, inlineDiffService);

        // NOUVEAU : Service d'image (PdfiumViewer) pour les captures visuelles
        var imageService = new PdfImageService();

        // 4. Orchestrateur principal mis à jour avec le service d'image
        _orchestrator = new PdfComparisonOrchestrator(
            extractionService,
            diffAnalyzer,
            individualReportGen,
            globalReportGen,
            imageService
        );
        // ==============================================================

        // Save folder in "AppData/Roaming/PDFComparisonPro"
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appData, "PDFComparisonPro");
        Directory.CreateDirectory(appFolder);
        _sessionFilePath = Path.Combine(appFolder, "last_session.json");

        // Default output directory on the Desktop
        OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "PDF_DiffReports");

        // Attempt to load previous session on startup
        LoadSession();
    }

    // ==========================================
    // SAVE AND LOAD METHODS
    // ==========================================
    private void SaveSession()
    {
        try
        {
            var data = new AppSessionData
            {
                SourceDirectory = this.SourceDirectory,
                TargetDirectory = this.TargetDirectory,
                OutputDirectory = this.OutputDirectory,
                Pairs = this.Pairs.ToList()
            };

            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

            // AMÉLIORATION : Sauvegarde atomique
            // On écrit dans un fichier temporaire puis on le déplace pour éviter toute corruption en cas de crash
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

                Pairs.Clear();
                if (data.Pairs != null)
                {
                    foreach (var pair in data.Pairs)
                    {
                        Pairs.Add(pair);
                    }

                    if (Pairs.Count > 0)
                    {
                        StatusMessage = $"Session restored ({Pairs.Count} documents).";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Load error: {ex.Message}");
        }
    }
    // ==========================================

    [RelayCommand]
    private void OpenReport(DocumentPair pair)
    {
        if (pair != null && pair.HasReport && File.Exists(pair.ReportPath))
        {
            try
            {
                // Opens the PDF file with the computer's default reader
                Process.Start(new ProcessStartInfo(pair.ReportPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot open the report: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (pair != null && !File.Exists(pair.ReportPath))
        {
             // Security added if the file was deleted or moved between two sessions
             MessageBox.Show("The report file has been moved or deleted since the last session.", "File not found", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new OpenFolderDialog { Title = "Select Source Directory" };
        if (dialog.ShowDialog() == true)
        {
            SourceDirectory = dialog.FolderName;
            SaveSession(); // Saves the path as soon as it is modified
        }
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        var dialog = new OpenFolderDialog { Title = "Select Target Directory" };
        if (dialog.ShowDialog() == true)
        {
            TargetDirectory = dialog.FolderName;
            SaveSession(); // Saves the path as soon as it is modified
        }
    }

    // ==========================================
    // NEW COMMANDS FOR OUTPUT FOLDER
    // ==========================================
    [RelayCommand]
    private void OpenOutputDirectory()
    {
        // Target the merged document generated by the new synthesis method
        string globalReportPath = Path.Combine(OutputDirectory, "Global_Synthesis_Report.pdf");

        if (File.Exists(globalReportPath))
        {
            try
            {
                // Open the grand synthesis PDF directly
                Process.Start(new ProcessStartInfo(globalReportPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Cannot open the global report: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (Directory.Exists(OutputDirectory))
        {
            // Security fallback: if the grand report doesn't exist yet, open the folder instead
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

            // mailto: creates a draft in Outlook (or default mail client) with pre-filled text
            string mailtoUri = $"mailto:?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";

            Process.Start(new ProcessStartInfo(mailtoUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot open the default mail client: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    // ==========================================

    [RelayCommand]
    private void CancelComparison()
    {
        if (IsProcessing && _cancellationTokenSource != null)
        {
            StatusMessage = "Cancelling processing...";
            _cancellationTokenSource.Cancel();
        }
    }

    [RelayCommand]
    private async Task StartComparisonAsync()
    {
        // 1. Basic checks
        if (string.IsNullOrWhiteSpace(SourceDirectory) || string.IsNullOrWhiteSpace(TargetDirectory))
        {
            MessageBox.Show("Please specify the source and target directories.", "Missing Directories", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(SourceDirectory) || !Directory.Exists(TargetDirectory))
        {
            MessageBox.Show("One or more specified directories do not exist.", "Path Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 2. State initialization
        IsProcessing = true;
        Pairs.Clear();
        ProgressValue = 0;

        // Initialisation du token d'annulation pour cette session de traitement
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            StatusMessage = "Analyzing and pairing files...";

            // Appel au nouveau PdfFileService pour le matching
            var matchedPairs = await Task.Run(() => _fileService.MatchFiles(SourceDirectory, TargetDirectory), _cancellationTokenSource.Token);

            // Update the UI list
            foreach (var pair in matchedPairs)
            {
                Pairs.Add(pair);
            }

            // Process only those that have a target file
            var pairsToProcess = matchedPairs.Where(p => p.Status != CompareStatus.MissingInTarget).ToList();
            ProgressMax = pairsToProcess.Count;

            if (ProgressMax == 0)
            {
                StatusMessage = "No valid pair found for comparison.";
                return;
            }

            // Clearer display of the start of comparison
            StatusMessage = $"Comparing {ProgressMax} documents...";

            var progress = new Progress<int>(value =>
            {
                ProgressValue = value;
                StatusMessage = $"Analyzing: document {value} of {ProgressMax}";
            });

            // 3. Appel au nouvel Orchestrateur en passant le jeton d'annulation
            await _orchestrator.ProcessPairsAsync(pairsToProcess, OutputDirectory, progress, _cancellationTokenSource.Token);

            // ==========================================
            // AUTOMATIC SORTING OF RESULTS
            // ==========================================
            StatusMessage = "Sorting results...";

            var sortedPairs = Pairs.OrderByDescending(p => p.DiffCount).ToList();

            Pairs.Clear();
            foreach (var p in sortedPairs)
            {
                Pairs.Add(p);
            }

            StatusMessage = "Processing completed successfully!";
            SaveSession();

            int diffCount = pairsToProcess.Count(p => p.Status == CompareStatus.Different);
            MessageBox.Show($"Comparison completed!\n{diffCount} documents have differences out of {ProgressMax} compared.",
                            "Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Processing was cancelled by the user.";
            MessageBox.Show("The comparison process was cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Global error: {ex.Message}";
            MessageBox.Show(ex.Message, "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }
}