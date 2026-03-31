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

using System.Windows.Controls;



namespace PDFComparison.ViewModels;



public partial class MainViewModel : ObservableObject

{

    private readonly PdfProcessingService _processingService;



    [ObservableProperty] private string _sourceDirectory = string.Empty;

    [ObservableProperty] private string _targetDirectory = string.Empty;

    [ObservableProperty] private string _outputDirectory = string.Empty;



    // Notifie automatiquement l'UI de recalculer "IsNotProcessing" quand "IsProcessing" change

    [ObservableProperty]

    [NotifyPropertyChangedFor(nameof(IsNotProcessing))]

    private bool _isProcessing;



    public bool IsNotProcessing => !IsProcessing;



    [ObservableProperty] private int _progressValue;

    [ObservableProperty] private int _progressMax;

    [ObservableProperty] private string _statusMessage = "Prêt. Veuillez sélectionner les dossiers.";



    public ObservableCollection<DocumentPair> Pairs { get; } = new();



    public MainViewModel()

    {

        _processingService = new PdfProcessingService();



        // Dossier de sortie par défaut sur le bureau

        OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RapportsDiff_PDF");

    }



    [RelayCommand]

    private void BrowseSource()

    {

        var dialog = new OpenFolderDialog { Title = "Sélectionner le dossier Source" };

        if (dialog.ShowDialog() == true) SourceDirectory = dialog.FolderName;

    }



    [RelayCommand]

    private void BrowseTarget()

    {

        var dialog = new OpenFolderDialog { Title = "Sélectionner le dossier Target" };

        if (dialog.ShowDialog() == true) TargetDirectory = dialog.FolderName;

    }



    [RelayCommand]

    private void BrowseOutput()

    {

        var dialog = new OpenFolderDialog { Title = "Sélectionner le dossier des Rapports" };

        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;

    }



    [RelayCommand]

    private async Task StartComparisonAsync()

    {

        // 1. Vérifications de base

        if (string.IsNullOrWhiteSpace(SourceDirectory) || string.IsNullOrWhiteSpace(TargetDirectory))

        {

            MessageBox.Show("Veuillez spécifier les dossiers source et target.", "Dossiers manquants", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;

        }



        if (!Directory.Exists(SourceDirectory) || !Directory.Exists(TargetDirectory))

        {

            MessageBox.Show("Un ou plusieurs dossiers spécifiés n'existent pas.", "Erreur de chemin", MessageBoxButton.OK, MessageBoxImage.Error);

            return;

        }



        // 2. Initialisation de l'état

        IsProcessing = true;

        Pairs.Clear();

        ProgressValue = 0;



        try

        {

            StatusMessage = "Analyse et appairage des fichiers...";



            // On délègue l'I/O du scan de dossier à un thread de fond

            var matchedPairs = await Task.Run(() => _processingService.MatchFiles(SourceDirectory, TargetDirectory));



            // Mise à jour de la liste UI

            foreach (var pair in matchedPairs)

            {

                Pairs.Add(pair);

            }



            // On ne traite que ceux qui ont un fichier cible

            var pairsToProcess = matchedPairs.Where(p => p.Status != CompareStatus.MissingInTarget).ToList();

            ProgressMax = pairsToProcess.Count;



            if (ProgressMax == 0)

            {

                StatusMessage = "Aucune paire valide trouvée pour la comparaison.";

                return;

            }



            StatusMessage = $"Comparaison en cours de {ProgressMax} documents (Multithreading)...";



            var progress = new Progress<int>(value =>

            {

                ProgressValue = value;

                StatusMessage = $"Traitement : {value} / {ProgressMax}";

            });



            // 3. Lancement du traitement lourd asynchrone

            await _processingService.ProcessPairsAsync(pairsToProcess, OutputDirectory, progress);



            StatusMessage = "Traitement terminé avec succès !";



            // Résumé à la fin

            int diffCount = pairsToProcess.Count(p => p.Status == CompareStatus.Different);

            MessageBox.Show($"Comparaison terminée !\n{diffCount} différences trouvées sur {ProgressMax} documents comparés.",

                            "Terminé", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        catch (Exception ex)

        {

            StatusMessage = $"Erreur globale : {ex.Message}";

            MessageBox.Show(ex.Message, "Erreur Critique", MessageBoxButton.OK, MessageBoxImage.Error);

        }

        finally

        {

            IsProcessing = false;

        }

    }

}