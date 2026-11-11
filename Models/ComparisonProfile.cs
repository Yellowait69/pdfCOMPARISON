using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PDFComparison.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PageScope
{
    AllPages,
    FirstPageOnly,
    LastPageOnly,
    EvenPages,
    OddPages,
    CustomRange
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoordinateUnit
{
    /// <summary>Points PDF (72 DPI, ex: A4 Portrait = 595 x 842 pt).</summary>
    PdfPoints,
    /// <summary>Coordonnées relatives [0.0 .. 1.0] indépendantes des dimensions de la page.</summary>
    NormalizedRatio
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ZoneOrigin
{
    /// <summary>Origine en haut à gauche (idéal pour les en-têtes et le dessin à la souris).</summary>
    TopLeft,
    /// <summary>Origine en bas à gauche (repère natif PDF, idéal pour les pieds de page).</summary>
    BottomLeft
}

public partial class ExclusionZoneRule : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private string _name = "Nouvelle zone";
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private PageScope _scope = PageScope.AllPages;
    [ObservableProperty] private int? _customPageStart;
    [ObservableProperty] private int? _customPageEnd;
    [ObservableProperty] private CoordinateUnit _unit = CoordinateUnit.PdfPoints;
    [ObservableProperty] private ZoneOrigin _origin = ZoneOrigin.TopLeft;

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width = 595;
    [ObservableProperty] private double _height = 50;

    /// <summary>
    /// Si activé, les dates détectées dans cette zone d'exclusion (ex: en-tête) sont mémorisées
    /// pour être également masquées lorsqu'elles réapparaissent dans le corps du document.
    /// </summary>
    [ObservableProperty] private bool _collectDatesInZone;

    public bool AppliesToPage(int pageNumber, int totalPages)
    {
        if (!IsEnabled) return false;

        return Scope switch
        {
            PageScope.AllPages => true,
            PageScope.FirstPageOnly => pageNumber == 1,
            PageScope.LastPageOnly => pageNumber == totalPages,
            PageScope.EvenPages => pageNumber % 2 == 0,
            PageScope.OddPages => pageNumber % 2 != 0,
            PageScope.CustomRange => (!CustomPageStart.HasValue || pageNumber >= CustomPageStart.Value) &&
                                     (!CustomPageEnd.HasValue || pageNumber <= CustomPageEnd.Value),
            _ => true
        };
    }

    /// <summary>
    /// Convertit la zone dans le repère natif PDF (Origine Bottom-Left, en points PDF) pour une page donnée.
    /// </summary>
    public (double Left, double Bottom, double Right, double Top) ToPdfBounds(double pageWidth, double pageHeight)
    {
        double zoneLeftPt = Unit == CoordinateUnit.NormalizedRatio ? X * pageWidth : X;
        double zoneWidthPt = Unit == CoordinateUnit.NormalizedRatio ? Width * pageWidth : Width;
        double zoneYOffsetPt = Unit == CoordinateUnit.NormalizedRatio ? Y * pageHeight : Y;
        double zoneHeightPt = Unit == CoordinateUnit.NormalizedRatio ? Height * pageHeight : Height;

        double left = zoneLeftPt;
        double right = zoneLeftPt + zoneWidthPt;
        double bottom, top;

        if (Origin == ZoneOrigin.TopLeft)
        {
            top = pageHeight - zoneYOffsetPt;
            bottom = top - zoneHeightPt;
        }
        else
        {
            bottom = zoneYOffsetPt;
            top = bottom + zoneHeightPt;
        }

        return (left, bottom, right, top);
    }

    /// <summary>
    /// Vérifie si la boîte englobante d'un mot PDF est exclue par cette zone.
    /// </summary>
    public bool IntersectsPdfBox(double wordLeft, double wordBottom, double wordRight, double wordTop, double pageWidth, double pageHeight)
    {
        if (!IsEnabled) return false;

        var (zLeft, zBottom, zRight, zTop) = ToPdfBounds(pageWidth, pageHeight);

        return wordLeft < zRight &&
               wordRight > zLeft &&
               wordBottom < zTop &&
               wordTop > zBottom;
    }
}

public partial class RegexMaskingRule : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private string _name = "Nouvelle règle Regex";
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _pattern = string.Empty;
    [ObservableProperty] private string _replacementMask = "[MASKED]";
    [ObservableProperty] private bool _ignoreCase = true;

    /// <summary>
    /// Nombre minimal d'occurrences dans le document pour déclencher le masquage (1 = toujours masquer, 2 = masquer si répété).
    /// </summary>
    [ObservableProperty] private int _minOccurrences = 1;
}

public partial class ComparisonProfile : ObservableObject
{
    [ObservableProperty] private string _profileName = "Profil Standard";

    // --- 1. Zones d'exclusion géométriques (X/Y) ---
    public ObservableCollection<ExclusionZoneRule> ExclusionZones { get; set; } = new();

    // --- 2. Règles d'expressions régulières dynamiques ---
    public ObservableCollection<RegexMaskingRule> RegexRules { get; set; } = new();

    // --- 3. Masquage intelligent des noms de clients ---
    [ObservableProperty] private bool _enableDynamicClientNameMasking = false;
    [ObservableProperty] private string _clientKeywordsPattern = @"^([Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur|Client|Customer):?$";
    [ObservableProperty] private string _clientNameReplacementMask = "[NOM_IGNORE]";

    // --- 4. Filtres de filigranes et typographie ---
    [ObservableProperty] private string _exactWatermarkWordsCsv = "SPECIMEN, SPECIMENS, SPECIME, SPECIM, PECIMEN, ECIMEN, TOTEIN, ECIMIEN";
    [ObservableProperty] private string _largeWatermarkFragmentsCsv = "SPECIMEN, SPECIME, SPECIM, PECIMEN, ECIMEN, CIMEN, SPECI, IMEN, TOTEIN, TEST, ECIMIEN";
    [ObservableProperty] private double _watermarkFragmentMinFontSizePt = 16.0;
    [ObservableProperty] private double _maxAllowedFontSizePt = 42.0;
    [ObservableProperty] private double _minAllowedFontSizePt = 1.0;
    [ObservableProperty] private bool _ignoreWhiteOnWhiteText = true;

    // --- 5. Tolérances anti-faux-positifs (Alignement & Reflow) ---
    [ObservableProperty] private int _maxLineShiftForFullLine = 12;
    [ObservableProperty] private int _maxLineShiftForSequence = 10;
    [ObservableProperty] private int _maxLineShiftForSingleWord = 3;
    [ObservableProperty] private decimal _localProximityMaxX = 480.0m;
    [ObservableProperty] private decimal _localProximityMinY = 36.0m;

    // --- 6. Régression Visuelle Perceptuelle (SSIM, YIQ & Anti-Aliasing) ---
    [ObservableProperty] private bool _enableVisualSsimEngine = true;
    [ObservableProperty] private int _visualRenderDpi = 120;
    [ObservableProperty] private double _perceptualYiqThreshold = 0.10;
    [ObservableProperty] private double _ssimSimilarityThreshold = 0.94;
    [ObservableProperty] private bool _ignoreAntiAliasingPixels = true;
    [ObservableProperty] private bool _enableSubPixelAutoAlignment = true;
    [ObservableProperty] private int _maxAutoAlignmentShiftPx = 6;
    [ObservableProperty] private int _minVisualClusterAreaPx = 16;
    [ObservableProperty] private bool _generateVisualSnapshotsInGlobalReport = true;

    // --- 7. Appairage de fichiers & Mots-clés critiques ---
    [ObservableProperty] private string _fileMatchingRegex = @"([A-Z]{2}_\d+_\d+)\.pdf$";
    [ObservableProperty] private string _criticalKeywordsRegex = @"(?i)\b(prix|pénalité|pénalités|résiliation|ttc|ht|garantie|article|euro|eur|taxe|montant|facture|prime|capital|rachat|versement|taux|frais|rente|police|bénéficiaire|iban|bic|price|penalty|termination|vat|warranty|tax|amount|invoice|bedrag|premie|totaal|kapitaal|kosten|afkoop|storting|overdracht|betaling|rekening|polis|begunstigde|waarborg|belasting|taks|intrest|rendement)\b";

    public static ComparisonProfile CreateDefault()
    {
        return new ComparisonProfile
        {
            ProfileName = "Profil Standard (Par défaut)",
            ExclusionZones = new ObservableCollection<ExclusionZoneRule>
            {
                new ExclusionZoneRule
                {
                    Name = "En-tête de page (Header 130pt)",
                    IsEnabled = true,
                    Scope = PageScope.AllPages,
                    Unit = CoordinateUnit.PdfPoints,
                    Origin = ZoneOrigin.TopLeft,
                    X = 0,
                    Y = 0,
                    Width = 2000,
                    Height = 130,
                    CollectDatesInZone = true
                },
                new ExclusionZoneRule
                {
                    Name = "Pied de page (Footer 40pt)",
                    IsEnabled = true,
                    Scope = PageScope.AllPages,
                    Unit = CoordinateUnit.PdfPoints,
                    Origin = ZoneOrigin.BottomLeft,
                    X = 0,
                    Y = 0,
                    Width = 2000,
                    Height = 40,
                    CollectDatesInZone = false
                },
                new ExclusionZoneRule
                {
                    Name = "Marge technique gauche (50pt)",
                    IsEnabled = true,
                    Scope = PageScope.AllPages,
                    Unit = CoordinateUnit.PdfPoints,
                    Origin = ZoneOrigin.TopLeft,
                    X = 0,
                    Y = 0,
                    Width = 50,
                    Height = 2000,
                    CollectDatesInZone = false
                }
            },
            RegexRules = new ObservableCollection<RegexMaskingRule>
            {
                new RegexMaskingRule
                {
                    Name = "Dates variables répétées",
                    IsEnabled = true,
                    Pattern = @"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b",
                    ReplacementMask = "[DATE_IGNORE]",
                    IgnoreCase = true,
                    MinOccurrences = 2
                },
                new RegexMaskingRule
                {
                    Name = "Numéros de facture / référence",
                    IsEnabled = false,
                    Pattern = @"\b(?:FACT|INV|REF)[-\s:/]*\d{4,12}\b",
                    ReplacementMask = "[REF_IGNORE]",
                    IgnoreCase = true,
                    MinOccurrences = 1
                },
                new RegexMaskingRule
                {
                    Name = "Ancres techniques de signature (#...#)",
                    IsEnabled = true,
                    Pattern = @"#[A-Z0-9_]+#",
                    ReplacementMask = "",
                    IgnoreCase = true,
                    MinOccurrences = 1
                }
            }
        };
    }
}
