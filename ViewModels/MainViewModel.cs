using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PDFComparison.Models;
using PDFComparison.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace PDFComparison.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly PdfProcessingService _processingService;

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
        _processingService = new PdfProcessingService();

        // Default output directory on the Desktop
        OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "PDF_DiffReports");
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new OpenFolderDialog { Title = "Select Source Directory" };
        if (dialog.ShowDialog() == true) SourceDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        var dialog = new OpenFolderDialog { Title = "Select Target Directory" };
        if (dialog.ShowDialog() == true) TargetDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog { Title = "Select Reports Directory" };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
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

        try
        {
            StatusMessage = "Analyzing and pairing files...";

            // Delegate I/O folder scanning to a background thread
            var matchedPairs = await Task.Run(() => _processingService.MatchFiles(SourceDirectory, TargetDirectory));

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

            // Affichage plus clair du début de la comparaison
            StatusMessage = $"Comparaison de {ProgressMax} documents en cours...";

            var progress = new Progress<int>(value =>
            {
                ProgressValue = value;
                // Mise à jour claire du compteur pour l'utilisateur
                StatusMessage = $"Analyse en cours : document {value} sur {ProgressMax}";
            });

            // 3. Launch heavy asynchronous processing
            await _processingService.ProcessPairsAsync(pairsToProcess, OutputDirectory, progress);

            // ==========================================
            // TRI AUTOMATIQUE DES RÉSULTATS
            // ==========================================
            StatusMessage = "Tri des résultats en cours...";

            // On trie la liste par nombre de différences décroissant (les plus modifiés en haut)
            var sortedPairs = Pairs.OrderByDescending(p => p.DiffCount).ToList();

            // On met à jour l'interface graphique avec la liste triée
            Pairs.Clear();
            foreach (var p in sortedPairs)
            {
                Pairs.Add(p);
            }
            // ==========================================

            StatusMessage = "Processing completed successfully!";

            // Final summary
            int diffCount = pairsToProcess.Count(p => p.Status == CompareStatus.Different);
            MessageBox.Show($"Comparison completed!\n{diffCount} documents présentent des différences sur {ProgressMax} comparés.",
                            "Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Global error: {ex.Message}";
            MessageBox.Show(ex.Message, "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }
}