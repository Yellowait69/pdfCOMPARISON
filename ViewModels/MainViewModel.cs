using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using PdfComparer.Models;
using PdfComparer.Services;
using System.IO;

namespace PdfComparer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly PdfProcessingService _processingService;

    [ObservableProperty] private string _sourceDirectory = string.Empty;
    [ObservableProperty] private string _targetDirectory = string.Empty;
    [ObservableProperty] private string _outputDirectory = string.Empty;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMax;
    [ObservableProperty] private string _statusMessage = "Prêt.";

    public ObservableCollection<DocumentPair> Pairs { get; } = new();

    public MainViewModel()
    {
        _processingService = new PdfProcessingService();

        // Valeurs par défaut pour le test
        OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "PdfDiffReports");
    }

    [RelayCommand]
    private async Task StartComparisonAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceDirectory) || string.IsNullOrWhiteSpace(TargetDirectory))
        {
            StatusMessage = "Veuillez spécifier les dossiers source et target.";
            return;
        }

        IsProcessing = true;
        Pairs.Clear();
        ProgressValue = 0;

        try
        {
            StatusMessage = "Analyse des dossiers...";
            var matchedPairs = await Task.Run(() => _processingService.MatchFiles(SourceDirectory, TargetDirectory));

            foreach (var pair in matchedPairs) Pairs.Add(pair);

            var pairsToProcess = matchedPairs.Where(p => p.Status != CompareStatus.MissingInTarget).ToList();
            ProgressMax = pairsToProcess.Count;

            if (ProgressMax == 0)
            {
                StatusMessage = "Aucune paire trouvée.";
                return;
            }

            StatusMessage = "Comparaison en cours (Multithreading)...";

            var progress = new Progress<int>(value =>
            {
                ProgressValue = value;
                StatusMessage = $"Traitement : {value} / {ProgressMax}";
            });

            await _processingService.ProcessPairsAsync(matchedPairs, OutputDirectory, progress);

            StatusMessage = "Terminé avec succès !";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erreur globale : {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }
}