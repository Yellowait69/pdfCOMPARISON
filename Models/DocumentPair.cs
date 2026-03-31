using CommunityToolkit.Mvvm.ComponentModel;

namespace PDFComparison.Models;

public partial class DocumentPair : ObservableObject
{
    public string MatchKey { get; }
    public string SourcePath { get; }
    public string? TargetPath { get; }

    [ObservableProperty]
    private CompareStatus _status = CompareStatus.Pending;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // Propriété pour stocker le nombre d'erreurs/différences
    // Permet de trier les documents pour afficher ceux avec le plus d'erreurs en premier
    [ObservableProperty]
    private int _diffCount;

    // NOUVEAU : Chemin du rapport PDF généré (côte à côte)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private string _reportPath = string.Empty;

    // NOUVEAU : Booléen automatique pour activer/désactiver le bouton "Ouvrir PDF"
    public bool HasReport => !string.IsNullOrEmpty(ReportPath);

    public DocumentPair(string matchKey, string sourcePath, string? targetPath)
    {
        MatchKey = matchKey;
        SourcePath = sourcePath;
        TargetPath = targetPath;

        if (string.IsNullOrEmpty(TargetPath))
        {
            Status = CompareStatus.MissingInTarget;
            ErrorMessage = "Fichier cible manquant";
            DiffCount = -1; // -1 pour s'assurer de les placer à la fin lors d'un tri décroissant
        }
    }
}