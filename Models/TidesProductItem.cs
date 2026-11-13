using CommunityToolkit.Mvvm.ComponentModel;

namespace PDFComparison.Models;

/// <summary>
/// Représente un produit TIDES Life découvert dans \\sppdfc01\TIDES\Life\Bank ou \\sppdfc01\TIDES\Life\Broker.
/// </summary>
public partial class TidesProductItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Canal ("Bank" en priorité 1, "Broker" en priorité 2).</summary>
    [ObservableProperty]
    private string _channel = "Bank";

    /// <summary>Nom du dossier produit (ex: "SP.DH", "SP.EFP", "SP.SFP Private").</summary>
    [ObservableProperty]
    private string _productName = string.Empty;

    /// <summary>Chemin complet du produit (ex: \\sppdfc01\TIDES\Life\Bank\SP.DH).</summary>
    [ObservableProperty]
    private string _productRootPath = string.Empty;

    /// <summary>Nom du dossier de release cible (ex: "2026_R11" ou "2026_R9").</summary>
    [ObservableProperty]
    private string _releaseFolderName = string.Empty;

    /// <summary>Chemin résolu du dossier Source (ex: \\sppdfc01\TIDES\Life\Bank\SP.DH\Source).</summary>
    [ObservableProperty]
    private string _sourceDirectoryPath = string.Empty;

    /// <summary>Chemin résolu du dossier Target contenant les PDF de la release (ex: ...\2026_R11\Target ou ...\2026_R11).</summary>
    [ObservableProperty]
    private string _targetDirectoryPath = string.Empty;

    /// <summary>Chemin résolu du dossier Output de la release (ex: ...\2026_R11\Output).</summary>
    [ObservableProperty]
    private string _outputDirectoryPath = string.Empty;

    [ObservableProperty]
    private bool _hasSourceFolder;

    [ObservableProperty]
    private bool _hasTargetFolder;

    [ObservableProperty]
    private int _sourcePdfCount;

    [ObservableProperty]
    private int _targetPdfCount;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _availableReleasesCsv = string.Empty;

    public int SortPriority { get; set; }

    public int ChannelPriority => SortPriority > 0
        ? SortPriority
        : (string.Equals(Channel, "Bank", System.StringComparison.OrdinalIgnoreCase) ? 1 : 2);
}
