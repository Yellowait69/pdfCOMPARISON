using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Text.Json.Serialization;

namespace PDFComparison.Models;

public partial class DocumentPair : ObservableObject
{
    // Clé de correspondance (ex: le numéro du document extrait via Regex)
    [ObservableProperty]
    private string _matchKey = string.Empty;

    // Chemin du document original
    [ObservableProperty]
    private string _sourcePath = string.Empty;

    // Chemin du document cible (peut être null s'il n'a pas été trouvé)
    [ObservableProperty]
    private string? _targetPath;

    // Statut de la comparaison
    [ObservableProperty]
    private CompareStatus _status = CompareStatus.Pending;

    // Message d'erreur ou de succès détaillé
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // Nombre de différences détectées (utile pour trier la liste dans l'UI)
    [ObservableProperty]
    private int _diffCount;

    // Chemin du rapport PDF généré
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private string _reportPath = string.Empty;

    // Date et heure de fin de traitement
    [ObservableProperty]
    private DateTime? _completedTime;

    // Propriété calculée dynamiquement pour activer/désactiver le bouton "Ouvrir le PDF"
    // [JsonIgnore] empêche la sérialisation de cette propriété dans le fichier de sauvegarde
    [JsonIgnore]
    public bool HasReport => !string.IsNullOrEmpty(ReportPath);

    /// <summary>
    /// Constructeur vide requis par le désérialiseur JSON
    /// </summary>
    public DocumentPair()
    {
    }

    /// <summary>
    /// Constructeur principal utilisé par le PdfFileService lors du matching
    /// </summary>
    public DocumentPair(string matchKey, string sourcePath, string? targetPath)
    {
        MatchKey = matchKey;
        SourcePath = sourcePath;
        TargetPath = targetPath;

        // Si le fichier cible est introuvable dès la création, on met à jour le statut
        if (string.IsNullOrEmpty(TargetPath))
        {
            Status = CompareStatus.MissingInTarget;
            ErrorMessage = "Missing target file";
            DiffCount = -1; // -1 pour s'assurer qu'ils finissent en bas lors d'un tri décroissant
        }
    }
}