using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PDFComparison.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScanTopologyMode
{
    /// <summary>
    /// Chaque produit est un sous-dossier contenant un dossier Source et un dossier Cible (avec ou sans Canaux / Release).
    /// Ex: Racine \ [Canal] \ Produit \ Source  vs  Racine \ [Canal] \ Produit \ [Release] \ Target
    /// </summary>
    ProductSubfolders,

    /// <summary>
    /// Deux grands dossiers séparés : un Dossier Source global et un Dossier Cible global contenant des sous-dossiers ou des PDF.
    /// Ex: Dossier_Attendu \ [Sous-dossier]  vs  Dossier_Obtenu \ [Sous-dossier]
    /// </summary>
    MirrorTrees,

    /// <summary>
    /// Explore automatiquement un dossier racine pour trouver tous les couples de sous-dossiers (Source/Target, Attendu/Obtenu, Ref/Cand...).
    /// </summary>
    AutoDiscoverPairs
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfPairingStrategy
{
    /// <summary>
    /// Appaire par nom exact/nettoyé, puis identifiant commun, puis similarité du contenu texte PDF (idéal quand les noms des PDF sont totalement différents).
    /// </summary>
    SmartAuto,

    /// <summary>
    /// Appaire le 1er PDF Source avec le 1er PDF Cible, le 2e avec le 2e, par ordre alphabétique.
    /// </summary>
    FolderOrder,

    /// <summary>
    /// Appaire uniquement via une clé extraite par expression régulière dans le nom du fichier.
    /// </summary>
    CustomRegexOnly,

    /// <summary>
    /// Appaire uniquement si le nom du fichier (hors date/release) correspond (idéal pour isoler strictement les PDF manquants sur TIDES).
    /// </summary>
    ExactOrStemOnly
}

public sealed class PdfPairingOptions
{
    public PdfPairingStrategy Strategy { get; init; } = PdfPairingStrategy.SmartAuto;
    public string? CustomRegexPattern { get; init; }

    public static PdfPairingOptions DefaultSmart { get; } = new() { Strategy = PdfPairingStrategy.SmartAuto };
    public static PdfPairingOptions StrictTides { get; } = new() { Strategy = PdfPairingStrategy.ExactOrStemOnly };
}

public partial class AutomatedScanProfile : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private string _buttonName = "Nouveau Scan";

    [ObservableProperty]
    private string _buttonColorHex = "#059669";

    [ObservableProperty]
    private bool _showInMainToolbar = true;

    [ObservableProperty]
    private bool _isBuiltInTides;

    // --- 1. Logique des dossiers Source & Cible ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProductSubfoldersMode))]
    [NotifyPropertyChangedFor(nameof(IsMirrorTreesMode))]
    [NotifyPropertyChangedFor(nameof(IsAutoDiscoverMode))]
    [NotifyPropertyChangedFor(nameof(LiveTopologyPreview))]
    private ScanTopologyMode _topologyMode = ScanTopologyMode.ProductSubfolders;

    public bool IsProductSubfoldersMode => TopologyMode == ScanTopologyMode.ProductSubfolders;
    public bool IsMirrorTreesMode => TopologyMode == ScanTopologyMode.MirrorTrees;
    public bool IsAutoDiscoverMode => TopologyMode == ScanTopologyMode.AutoDiscoverPairs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTopologyPreview))]
    private string _rootPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTopologyPreview))]
    private string _secondaryTargetRootPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTopologyPreview))]
    private string _channelsCsv = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTopologyPreview))]
    private bool _useReleaseFolder;

    [ObservableProperty]
    private string _defaultReleaseName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTopologyPreview))]
    private string _sourceSubfolderTemplate = "Source";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTopologyPreview))]
    private string _targetSubfolderTemplate = "Target";

    [ObservableProperty]
    private string _productOutputSubfolderTemplate = "Output";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTopologyPreview))]
    private string _autoSourceFolderNamesCsv = "Source, Attendu, Ref, Reference, Old, Avant, Prod";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTopologyPreview))]
    private string _autoTargetFolderNamesCsv = "Target, Cible, Obtenu, Cand, New, Apres, Recette, UAT";

    // --- 2. Dossier personnalisé de rapport (optionnel : vide = dossier global personnalisé) ---
    [ObservableProperty]
    private string _customReportOutputDirectory = string.Empty;

    // --- 3. Stratégie d'appairage des PDF ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomRegexStrategy))]
    private PdfPairingStrategy _pairingStrategy = PdfPairingStrategy.SmartAuto;

    public bool IsCustomRegexStrategy => PairingStrategy == PdfPairingStrategy.CustomRegexOnly;

    [ObservableProperty]
    private string _customFileMatchingRegex = @"(\d{6,12})";

    // --- 4. Options pré-cochées & Règles dédiées ---
    [ObservableProperty]
    private bool _defaultIgnoreDates = true;

    [ObservableProperty]
    private bool _defaultIgnoreHeaderFooter = true;

    [ObservableProperty]
    private bool _defaultWriteProductOutputs;

    [ObservableProperty]
    private bool _useDedicatedRules;

    [ObservableProperty]
    private ComparisonProfile _dedicatedProfile = ComparisonProfile.CreateDefault();

    [JsonIgnore]
    public string LiveTopologyPreview
    {
        get
        {
            string root = string.IsNullOrWhiteSpace(RootPath) ? @"C:\Dossier_Racine" : RootPath.TrimEnd('\\', '/');
            switch (TopologyMode)
            {
                case ScanTopologyMode.MirrorTrees:
                {
                    string trgRoot = string.IsNullOrWhiteSpace(SecondaryTargetRootPath) ? @"C:\Dossier_Cible" : SecondaryTargetRootPath.TrimEnd('\\', '/');
                    return $"Source : {root}\\<Sous-dossier>\\*.pdf   ➔   Cible : {trgRoot}\\<Sous-dossier>\\*.pdf";
                }
                case ScanTopologyMode.AutoDiscoverPairs:
                {
                    return $"Recherche auto dans {root}\\**\\ des couples ({AutoSourceFolderNamesCsv}) ➔ ({AutoTargetFolderNamesCsv})";
                }
                default:
                {
                    string chanSample = string.IsNullOrWhiteSpace(ChannelsCsv)
                        ? string.Empty
                        : "\\" + ChannelsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
                    string srcRel = string.IsNullOrWhiteSpace(SourceSubfolderTemplate) ? "Source" : SourceSubfolderTemplate;
                    string trgRel = string.IsNullOrWhiteSpace(TargetSubfolderTemplate) ? "Target" : TargetSubfolderTemplate;
                    if (UseReleaseFolder && !trgRel.Contains("{Release}", StringComparison.OrdinalIgnoreCase))
                    {
                        trgRel = "{Release}\\" + trgRel;
                    }
                    return $"Source : {root}{chanSample}\\<Produit>\\{srcRel}   ➔   Cible : {root}{chanSample}\\<Produit>\\{trgRel}";
                }
            }
        }
    }

    public PdfPairingOptions ToPairingOptions(string? fallbackRegex = null)
    {
        return new PdfPairingOptions
        {
            Strategy = PairingStrategy,
            CustomRegexPattern = !string.IsNullOrWhiteSpace(CustomFileMatchingRegex)
                ? CustomFileMatchingRegex
                : fallbackRegex
        };
    }

    public static AutomatedScanProfile CreateDefaultEquipeTesting()
    {
        return new AutomatedScanProfile
        {
            ButtonName = "Équipe Testing",
            ButtonColorHex = "#059669",
            ShowInMainToolbar = true,
            IsBuiltInTides = true,
            TopologyMode = ScanTopologyMode.ProductSubfolders,
            RootPath = @"\\sppdfc01\TIDES\Life",
            ChannelsCsv = "Bank, Broker",
            UseReleaseFolder = true,
            SourceSubfolderTemplate = "Source",
            TargetSubfolderTemplate = @"{Release}\Target",
            ProductOutputSubfolderTemplate = @"{Release}\Output",
            PairingStrategy = PdfPairingStrategy.ExactOrStemOnly,
            CustomFileMatchingRegex = @"([A-Z]{2}_\d+_\d+)\.pdf$",
            DefaultIgnoreDates = true,
            DefaultIgnoreHeaderFooter = true,
            DefaultWriteProductOutputs = true,
            UseDedicatedRules = false,
            DedicatedProfile = ComparisonProfile.CreateDefault()
        };
    }

    public static AutomatedScanProfile CreateNewCustom(int number, ComparisonProfile? seedRules = null)
    {
        var dedicated = ComparisonProfile.CreateDefault();
        dedicated.ProfileName = $"Règles — Scan #{number}";
        if (seedRules != null)
        {
            dedicated.ExclusionZones.Clear();
            foreach (var z in seedRules.ExclusionZones)
            {
                dedicated.ExclusionZones.Add(z.Clone());
            }

            dedicated.RegexRules.Clear();
            foreach (var r in seedRules.RegexRules)
            {
                dedicated.RegexRules.Add(r.Clone());
            }
        }

        string[] palette = { "#0284C7", "#7C3AED", "#D97706", "#0D9488", "#E11D48" };
        string color = palette[(Math.Max(1, number) - 1) % palette.Length];

        return new AutomatedScanProfile
        {
            ButtonName = $"Scan Auto #{number}",
            ButtonColorHex = color,
            ShowInMainToolbar = true,
            IsBuiltInTides = false,
            TopologyMode = ScanTopologyMode.AutoDiscoverPairs,
            RootPath = string.Empty,
            ChannelsCsv = string.Empty,
            UseReleaseFolder = false,
            SourceSubfolderTemplate = "Source",
            TargetSubfolderTemplate = "Target",
            ProductOutputSubfolderTemplate = "Output",
            PairingStrategy = PdfPairingStrategy.SmartAuto,
            DefaultIgnoreDates = true,
            DefaultIgnoreHeaderFooter = true,
            DefaultWriteProductOutputs = false,
            UseDedicatedRules = false,
            DedicatedProfile = dedicated
        };
    }
}

public partial class AppUserSettings : ObservableObject
{
    [ObservableProperty]
    private string _defaultReportOutputDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "PDF_DiffReports");

    [ObservableProperty]
    private bool _createSubfolderPerRun;

    [ObservableProperty]
    private string _shareMailRecipients = string.Empty;

    [ObservableProperty]
    private string _shareMailSubject = "Synthèse Comparaison PDF";

    [ObservableProperty]
    private string _shareMailIntro = "Bonjour,\n\nLa synthèse de comparaison PDF a été générée et mise à jour.";

    [ObservableProperty]
    private PdfPairingStrategy _manualComparePairingStrategy = PdfPairingStrategy.SmartAuto;

    public ObservableCollection<AutomatedScanProfile> ScanProfiles { get; set; } = new();

    public static AppUserSettings CreateDefault()
    {
        var settings = new AppUserSettings();
        settings.ScanProfiles.Add(AutomatedScanProfile.CreateDefaultEquipeTesting());
        return settings;
    }
}
