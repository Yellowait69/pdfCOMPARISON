using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PDFComparison.Models;

public static partial class ProductCodeHelper
{
    private static readonly HashSet<string> SourceFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Source", "Sources", "Src", "Attendu", "Ref", "Reference", "Old", "Avant", "Prod"
    };

    private static readonly HashSet<string> TargetFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Target", "Targets", "Trg", "Cible", "Obtenu", "Cand", "Candidat", "New", "Apres", "Après", "Recette", "UAT"
    };

    private static readonly HashSet<string> OutputFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Output", "Outputs", "Rapport", "Rapports", "PDF_DiffReports"
    };

    private static readonly HashSet<string> StructuralOrGenericFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bank", "Banque", "Broker", "Brokers", "Courtier",
        "TIDES", "Life", "NonLife", "Non-Life",
        "Users", "Utilisateurs", "Downloads", "Téléchargements", "Telechargements",
        "Desktop", "Bureau", "Documents", "AppData", "Local", "Roaming", "Temp", "Tmp",
        "Standard", "Lot Principal", "Général", "General", "Lot", "Dossier", "Auto",
        "Docs", "PDF", "PDFs"
    };

    private static readonly HashSet<string> KnownBankProductCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SP.DH", "SP.EFP", "SP.FIB", "SP.FIP", "SP.FIX",
        "SP.PIP", "SP.SDR", "SP.SFP", "SP.SFP Private", "SP.SIP"
    };

    [GeneratedRegex(@"^(?:20\d{2}[_-]?R0?\d{1,2}|R0?\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseFolderSegmentRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(SP\.[A-Za-z0-9.]+)(?![A-Za-z0-9.])", RegexOptions.IgnoreCase)]
    private static partial Regex SpProductCodeRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(BALV[A-Z0-9]+(?:_[A-Za-z0-9]+){1,4}?)(?=_(?:FR|NL|DE|EN)(?![A-Za-z0-9])|_+\d|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TemplateFamilyCodeRegex();

    public static bool IsReleaseFolderName(string? segment)
    {
        return !string.IsNullOrWhiteSpace(segment) && ReleaseFolderSegmentRegex().IsMatch(segment.Trim());
    }

    public static bool IsSourceOrTargetOrOutputFolder(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        string s = segment.Trim();
        return SourceFolderNames.Contains(s) || TargetFolderNames.Contains(s) || OutputFolderNames.Contains(s);
    }

    private static bool IsBankSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        string s = segment.Trim();
        return s.Equals("Bank", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("Banque", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrokerSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        string s = segment.Trim();
        return s.Equals("Broker", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("Brokers", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("Courtier", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootOrStructuralSegment(string segment, int index, bool isUncPath)
    {
        if (string.IsNullOrWhiteSpace(segment)) return true;
        string s = segment.Trim();
        if (isUncPath && index == 0) return true; // Nom du serveur réseau UNC (ex: \\sppdfc01)
        if (s.EndsWith(":", StringComparison.Ordinal)) return true; // Lettre de lecteur (ex: C:)
        return StructuralOrGenericFolderNames.Contains(s);
    }

    public static bool IsGenericOrEmptyProduct(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string trimmed = name.Trim();
        if (trimmed.EndsWith(":", StringComparison.Ordinal)) return true;
        if (IsReleaseFolderName(trimmed)) return true;
        if (IsSourceOrTargetOrOutputFolder(trimmed)) return true;
        return StructuralOrGenericFolderNames.Contains(trimmed);
    }

    public static bool IsGlobalFilter(string? productFilter)
    {
        if (string.IsNullOrWhiteSpace(productFilter)) return true;
        string t = productFilter.Trim();
        return t == "*" ||
               t.Equals("Global", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("Globale", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("Tous", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("All", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatScopeBadge(string? productFilter)
    {
        return IsGlobalFilter(productFilter)
            ? "Globale (Tous)"
            : $"Produit : {productFilter!.Trim()}";
    }

    private static (bool IsUnc, string[] Segments) ExtractDirectorySegments(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return (false, Array.Empty<string>());

        string cleaned = rawPath.Trim().Trim('"');

        // Retirer un éventuel suffixe d'affichage de type " [2026_R11]" si ce n'est pas un vrai chemin disque
        int bracketIdx = cleaned.IndexOf(" [", StringComparison.Ordinal);
        if (bracketIdx > 0 && cleaned.EndsWith(']') && !Directory.Exists(cleaned) && !File.Exists(cleaned))
        {
            cleaned = cleaned[..bracketIdx].Trim();
        }

        // Si la chaîne n'est qu'un libellé (sans séparateur de dossier), aucun segment d'arborescence
        if (!cleaned.Contains('\\') && !cleaned.Contains('/'))
        {
            return (false, Array.Empty<string>());
        }

        bool isUnc = cleaned.StartsWith(@"\\", StringComparison.Ordinal) ||
                     cleaned.StartsWith("//", StringComparison.Ordinal);

        bool looksLikeFile = cleaned.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                             cleaned.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                             cleaned.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                             cleaned.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                             File.Exists(cleaned);

        string? dirPart = looksLikeFile ? Path.GetDirectoryName(cleaned) : cleaned;
        if (string.IsNullOrWhiteSpace(dirPart))
            return (isUnc, Array.Empty<string>());

        var segments = dirPart.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        return (isUnc, segments);
    }

    /// <summary>
    /// Remonte l'arborescence des chemins Source / Cible pour trouver le nom exact du dossier Produit
    /// (c'est-à-dire le dossier parent qui contient le sous-dossier Source et le sous-dossier de release ex: 2026_R11 / Target).
    /// </summary>
    public static string? TryResolveProductFromDirectoryTree(string? sourcePath, string? targetPath)
    {
        var (srcUnc, srcSegs) = ExtractDirectorySegments(sourcePath);
        var (trgUnc, trgSegs) = ExtractDirectorySegments(targetPath);

        // 1. Ancêtre commun le plus bas (LCA) entre Source et Cible lorsqu'ils divergent vers Source vs 2026_Rxx / Target
        if (srcSegs.Length > 0 && trgSegs.Length > 0 &&
            string.Equals(srcSegs[0], trgSegs[0], StringComparison.OrdinalIgnoreCase))
        {
            int commonLen = 0;
            int minLen = Math.Min(srcSegs.Length, trgSegs.Length);
            while (commonLen < minLen &&
                   string.Equals(srcSegs[commonLen], trgSegs[commonLen], StringComparison.OrdinalIgnoreCase))
            {
                commonLen++;
            }

            if (commonLen >= 1 && srcSegs.Length > commonLen && trgSegs.Length > commonLen)
            {
                string lcaFolder = srcSegs[commonLen - 1];
                string srcBranch = srcSegs[commonLen];
                string trgBranch = trgSegs[commonLen];

                bool branchesAreProductChildren =
                    SourceFolderNames.Contains(srcBranch) ||
                    TargetFolderNames.Contains(trgBranch) ||
                    IsReleaseFolderName(trgBranch) ||
                    SourceFolderNames.Contains(trgBranch) ||
                    TargetFolderNames.Contains(srcBranch) ||
                    IsReleaseFolderName(srcBranch);

                if (branchesAreProductChildren &&
                    !IsRootOrStructuralSegment(lcaFolder, commonLen - 1, srcUnc) &&
                    !IsReleaseFolderName(lcaFolder) &&
                    !IsSourceOrTargetOrOutputFolder(lcaFolder))
                {
                    return lcaFolder.Trim();
                }
            }
        }

        // 2. Remonter chaque chemin depuis le bas (Cible d'abord car elle contient 2026_Rxx\Target, puis Source)
        foreach (var (isUnc, segs) in new[] { (trgUnc, trgSegs), (srcUnc, srcSegs) })
        {
            if (segs.Length == 0) continue;

            // 2a. Remonter au-dessus d'un dossier de release (ex: ...\Bank\<Produit>\2026_R11\Target\doc.pdf)
            for (int i = segs.Length - 1; i >= 1; i--)
            {
                if (IsReleaseFolderName(segs[i]))
                {
                    int p = i - 1;
                    while (p >= 0 && (IsSourceOrTargetOrOutputFolder(segs[p]) || IsReleaseFolderName(segs[p])))
                    {
                        p--;
                    }

                    if (p >= 0 && !IsRootOrStructuralSegment(segs[p], p, isUnc))
                    {
                        return segs[p].Trim();
                    }
                }
            }

            // 2b. Remonter au-dessus d'un dossier Source / Target / Output (en sautant 2026_Rxx s'il est au-dessus)
            for (int i = segs.Length - 1; i >= 1; i--)
            {
                if (IsSourceOrTargetOrOutputFolder(segs[i]))
                {
                    int p = i - 1;
                    while (p >= 0 && (IsReleaseFolderName(segs[p]) || IsSourceOrTargetOrOutputFolder(segs[p])))
                    {
                        p--;
                    }

                    if (p >= 0 && !IsRootOrStructuralSegment(segs[p], p, isUnc))
                    {
                        return segs[p].Trim();
                    }
                }
            }

            // 2c. Sous-dossier direct de Bank ou Broker (ex: ...\Bank\<Produit>\doc.pdf)
            for (int i = segs.Length - 1; i >= 0; i--)
            {
                if ((IsBankSegment(segs[i]) || IsBrokerSegment(segs[i])) && i + 1 < segs.Length)
                {
                    int prodIdx = i + 1;
                    if ((segs[prodIdx].Equals("Life", StringComparison.OrdinalIgnoreCase) ||
                         segs[prodIdx].Equals("NonLife", StringComparison.OrdinalIgnoreCase)) &&
                        prodIdx + 1 < segs.Length)
                    {
                        prodIdx++;
                    }

                    string cand = segs[prodIdx];
                    if (!IsRootOrStructuralSegment(cand, prodIdx, isUnc) &&
                        !IsReleaseFolderName(cand) &&
                        !IsSourceOrTargetOrOutputFolder(cand))
                    {
                        return cand.Trim();
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Résout le canal uniquement parmi les deux seuls canaux métier existants : "Bank" ou "Broker",
    /// en remontant l'arborescence du chemin du fichier (avec repli sur le nom du fichier / produit si copié hors réseau).
    /// </summary>
    public static string ResolveEffectiveChannel(
        string? explicitChannel,
        string? sourcePath = null,
        string? targetPath = null,
        string? productHint = null)
    {
        // 1. Remonter l'arborescence des dossiers (Cible puis Source) de bas en haut
        foreach (string? rawPath in new[] { targetPath, sourcePath })
        {
            var (_, segs) = ExtractDirectorySegments(rawPath);
            for (int i = segs.Length - 1; i >= 0; i--)
            {
                if (IsBankSegment(segs[i])) return "Bank";
                if (IsBrokerSegment(segs[i])) return "Broker";
            }
        }

        // 2. Canal explicite valide ("Bank" ou "Broker" uniquement)
        if (!string.IsNullOrWhiteSpace(explicitChannel))
        {
            if (IsBankSegment(explicitChannel)) return "Bank";
            if (IsBrokerSegment(explicitChannel)) return "Broker";
        }

        // 3. Repli via préfixes du nom de fichier lorsque le dossier produit a été copié localement sans son parent Bank/Broker
        foreach (string? rawPath in new[] { targetPath, sourcePath })
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            string fn = Path.GetFileName(rawPath);
            if (fn.StartsWith("FBB_", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("_FBB_", StringComparison.OrdinalIgnoreCase))
            {
                return "Bank";
            }

            if (fn.StartsWith("BRO_", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("_BRO_", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("BALVAN", StringComparison.OrdinalIgnoreCase))
            {
                return "Broker";
            }
        }

        // 4. Repli via code produit connu
        string? candidateProd = !string.IsNullOrWhiteSpace(productHint)
            ? productHint.Trim()
            : TryResolveProductFromDirectoryTree(sourcePath, targetPath);

        if (!string.IsNullOrWhiteSpace(candidateProd))
        {
            if (KnownBankProductCodes.Contains(candidateProd)) return "Bank";
            if (candidateProd.Equals("SP.016", StringComparison.OrdinalIgnoreCase) ||
                candidateProd.Equals("SP.017", StringComparison.OrdinalIgnoreCase) ||
                candidateProd.Equals("016", StringComparison.OrdinalIgnoreCase) ||
                candidateProd.Equals("017", StringComparison.OrdinalIgnoreCase))
            {
                return "Broker";
            }
        }

        // Les deux seuls canaux possibles sont Bank ou Broker
        return "Bank";
    }

    public static string ResolveEffectiveProductCode(
        string? explicitProduct,
        string? channel = null,
        string? sourcePath = null,
        string? targetPath = null)
    {
        // 1. Priorité absolue : remonter l'arborescence du fichier vers le dossier contenant Source et 2026_Rxx / Target
        string? fromTree = TryResolveProductFromDirectoryTree(sourcePath, targetPath);
        if (!string.IsNullOrWhiteSpace(fromTree))
        {
            return fromTree;
        }

        // 2. Produit explicite valide (hors noms génériques / techniques)
        if (!IsGenericOrEmptyProduct(explicitProduct) &&
            !explicitProduct!.Contains(" / ", StringComparison.Ordinal))
        {
            return explicitProduct.Trim();
        }

        // 3. Remonter au dossier parent significatif si le chemin n'est pas un dossier temporaire système
        foreach (string? rawPath in new[] { targetPath, sourcePath })
        {
            var (isUnc, segs) = ExtractDirectorySegments(rawPath);
            if (segs.Length == 0) continue;

            bool isSystemTempPath = segs.Any(s =>
                s.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("Tmp", StringComparison.OrdinalIgnoreCase));

            if (!isSystemTempPath)
            {
                for (int i = segs.Length - 1; i >= 0; i--)
                {
                    if (!IsRootOrStructuralSegment(segs[i], i, isUnc) &&
                        !IsReleaseFolderName(segs[i]) &&
                        !IsSourceOrTargetOrOutputFolder(segs[i]))
                    {
                        return segs[i].Trim();
                    }
                }
            }
        }

        // 4. Dernier repli si le fichier est isolé hors arborescence (ex: dans Temp ou Downloads) : code SP.xxx éventuel
        foreach (string? rawPath in new[] { targetPath, sourcePath })
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            string fileName = Path.GetFileNameWithoutExtension(rawPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var spMatch = SpProductCodeRegex().Match(fileName);
                if (spMatch.Success)
                {
                    return spMatch.Groups[1].Value;
                }
            }
        }

        return !IsGenericOrEmptyProduct(explicitProduct) ? explicitProduct!.Trim() : string.Empty;
    }

    public static bool MatchesProductFilter(
        string? ruleProductFilter,
        string? docProductName,
        string? docChannel = null,
        string? sourcePath = null,
        string? targetPath = null)
    {
        if (IsGlobalFilter(ruleProductFilter)) return true;

        string effectiveProd = ResolveEffectiveProductCode(docProductName, docChannel, sourcePath, targetPath);
        string rawProd = (docProductName ?? string.Empty).Trim();
        string rawChannel = ResolveEffectiveChannel(docChannel, sourcePath, targetPath, effectiveProd);

        string? tplFromFile = null;
        foreach (string? p in new[] { targetPath, sourcePath })
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            string fn = Path.GetFileNameWithoutExtension(p);
            var tm = TemplateFamilyCodeRegex().Match(fn);
            if (tm.Success)
            {
                tplFromFile = tm.Groups[1].Value.Trim('_');
                break;
            }
        }

        var tokens = ruleProductFilter!
            .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0) return true;

        foreach (string rawToken in tokens)
        {
            string tok = rawToken.Trim().Trim('[', ']');
            if (IsGlobalFilter(tok)) return true;

            string normalizedTok = tok.Replace("•", "/").Replace("\\", "/");
            while (normalizedTok.Contains(" /")) normalizedTok = normalizedTok.Replace(" /", "/");
            while (normalizedTok.Contains("/ ")) normalizedTok = normalizedTok.Replace("/ ", "/");

            if (normalizedTok.Contains('/'))
            {
                var parts = normalizedTok.Split('/', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 &&
                    string.Equals(rawChannel, parts[0], StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(effectiveProd, parts[1], StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(rawProd, parts[1], StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            else
            {
                if ((!string.IsNullOrEmpty(effectiveProd) && string.Equals(effectiveProd, normalizedTok, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(rawProd) && string.Equals(rawProd, normalizedTok, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(tplFromFile) && string.Equals(tplFromFile, normalizedTok, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

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

    /// <summary>
    /// Vide = Règle Globale (tous les produits). Sinon code(s) produit séparés par virgule (ex: "SP.EFP" ou "SP.016, SP.017").
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGlobalRule))]
    [NotifyPropertyChangedFor(nameof(IsProductSpecificRule))]
    [NotifyPropertyChangedFor(nameof(DisplayScopeBadge))]
    private string _productFilter = string.Empty;

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

    /// <summary>
    /// Indique une zone personnalisée ajoutée par l'utilisateur (conservée lors des changements de profil).
    /// </summary>
    [ObservableProperty] private bool _isUserZone;

    [JsonIgnore]
    public bool IsGlobalRule => ProductCodeHelper.IsGlobalFilter(ProductFilter);

    [JsonIgnore]
    public bool IsProductSpecificRule => !IsGlobalRule;

    [JsonIgnore]
    public string DisplayScopeBadge => ProductCodeHelper.FormatScopeBadge(ProductFilter);

    public bool AppliesToProduct(string? productName, string? channel = null, string? sourcePath = null, string? targetPath = null)
    {
        if (!IsEnabled) return false;
        return ProductCodeHelper.MatchesProductFilter(ProductFilter, productName, channel, sourcePath, targetPath);
    }

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
    /// Pour les zones ciblées utilisateur / par produit, exige que le centre (ou >= 45% du mot) soit dans la zone
    /// afin de ne jamais mordre sur les jambages des lignes voisines.
    /// </summary>
    public bool IntersectsPdfBox(double wordLeft, double wordBottom, double wordRight, double wordTop, double pageWidth, double pageHeight)
    {
        if (!IsEnabled) return false;

        var (zLeft, zBottom, zRight, zTop) = ToPdfBounds(pageWidth, pageHeight);

        if (wordLeft > zRight || wordRight < zLeft || wordBottom > zTop || wordTop < zBottom)
            return false;

        if (!IsUserZone && IsGlobalRule)
            return true;

        double wordW = Math.Max(0.1, wordRight - wordLeft);
        double wordH = Math.Max(0.1, wordTop - wordBottom);
        double overlapX = Math.Max(0.0, Math.Min(wordRight, zRight) - Math.Max(wordLeft, zLeft));
        double overlapY = Math.Max(0.0, Math.Min(wordTop, zTop) - Math.Max(wordBottom, zBottom));

        double centerX = (wordLeft + wordRight) * 0.5;
        double centerY = (wordBottom + wordTop) * 0.5;

        bool matchX = (centerX >= zLeft && centerX <= zRight) || (overlapX / wordW >= 0.45);
        bool matchY = (centerY >= zBottom && centerY <= zTop) || (overlapY / wordH >= 0.45);
        return matchX && matchY;
    }

    public ExclusionZoneRule Clone() => new()
    {
        Id = Guid.NewGuid(),
        Name = Name,
        IsEnabled = IsEnabled,
        IsUserZone = IsUserZone,
        ProductFilter = ProductFilter,
        Scope = Scope,
        CustomPageStart = CustomPageStart,
        CustomPageEnd = CustomPageEnd,
        Unit = Unit,
        Origin = Origin,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        CollectDatesInZone = CollectDatesInZone
    };
}

public partial class RegexMaskingRule : ObservableObject
{
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private string _name = "Nouvelle règle";
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>
    /// Vide = Règle Globale (tous les produits). Sinon code(s) produit séparés par virgule (ex: "SP.EFP" ou "SP.017").
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGlobalRule))]
    [NotifyPropertyChangedFor(nameof(IsProductSpecificRule))]
    [NotifyPropertyChangedFor(nameof(DisplayScopeBadge))]
    private string _productFilter = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPatternInvalid))]
    private string _pattern = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReplacementEmpty))]
    private string _replacementMask = string.Empty;

    [ObservableProperty] private bool _ignoreCase = true;

    /// <summary>
    /// Nombre minimal d'occurrences dans le document pour déclencher le masquage (1 = toujours masquer, 2 = masquer si répété).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepeatRequirement))]
    [NotifyPropertyChangedFor(nameof(RepeatBadgeText))]
    private int _minOccurrences = 1;

    /// <summary>
    /// Indique une règle créée par l'utilisateur (conservée lors des relances et changements de profil).
    /// </summary>
    [ObservableProperty] private bool _isUserRule;

    /// <summary>
    /// Signature normalisée de l'écart d'origine (quand créée depuis un résultat de comparaison).
    /// </summary>
    [ObservableProperty] private string _ignoredDiffSignature = string.Empty;

    [JsonIgnore]
    public bool IsGlobalRule => ProductCodeHelper.IsGlobalFilter(ProductFilter);

    [JsonIgnore]
    public bool IsProductSpecificRule => !IsGlobalRule;

    [JsonIgnore]
    public string DisplayScopeBadge => ProductCodeHelper.FormatScopeBadge(ProductFilter);

    [JsonIgnore]
    public bool IsReplacementEmpty => string.IsNullOrEmpty(ReplacementMask);

    [JsonIgnore]
    public bool HasRepeatRequirement => MinOccurrences > 1;

    [JsonIgnore]
    public string RepeatBadgeText => MinOccurrences > 1 ? $"{MinOccurrences}x+" : string.Empty;

    [JsonIgnore]
    public bool IsPatternInvalid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Pattern)) return true;
            try
            {
                _ = new Regex(Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    public bool AppliesToProduct(string? productName, string? channel = null, string? sourcePath = null, string? targetPath = null)
    {
        if (!IsEnabled) return false;
        return ProductCodeHelper.MatchesProductFilter(ProductFilter, productName, channel, sourcePath, targetPath);
    }

    public RegexMaskingRule Clone() => new()
    {
        Id = Guid.NewGuid(),
        Name = Name,
        IsEnabled = IsEnabled,
        IsUserRule = IsUserRule,
        ProductFilter = ProductFilter,
        Pattern = Pattern,
        ReplacementMask = ReplacementMask,
        IgnoreCase = IgnoreCase,
        MinOccurrences = MinOccurrences,
        IgnoredDiffSignature = IgnoredDiffSignature
    };
}

public partial class ComparisonProfile : ObservableObject
{
    public const string DefaultDateRegexPattern = @"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b";

    public const string ExemplaireRoutingPattern =
        @"(?<![\p{L}\p{N}])(?:(?:exemplaires?|exemplaar|exemplaren|exemplare?n?|ausfertigung(?:en)?|esemplar[ei]|ejemplar(?:es)?|copie|kopie|copy)\s*(?:(?:d['’][a-zà-ÿ]+|du\s+[a-zà-ÿ]+|de\s+(?:la\s+|l['’])?[a-zà-ÿ]+|van\s+(?:de\s+)?[a-zà-ÿ]+|voor\s+(?:de\s+)?[a-zà-ÿ]+|für\s+(?:den\s+|die\s+)?[a-zà-ÿ]+|for\s+(?:the\s+)?[a-zà-ÿ]+|[a-zà-ÿ0-9-]+)\s*){0,4}/\s*[A-Z0-9]{2,12}(?:\s*/\s*[A-Z0-9]{1,12}){1,3}|(?<!(?:\b(?:un|une|een|ein|eine|en|in|deux|trois|twee|drie|zwei|drei)|\d)\s+)(?:exemplaires?|exemplaar|exemplaren|exemplare?|ausfertigung(?:en)?|esemplar[ei]|ejemplar(?:es)?|copie|kopie|copy)\s+(?:(?:pour|voor|für|for|destin[ée]e?\s+(?:au|à\s+la|à\s+l['’])|bestemd\s+voor)\s+)?(?:(?:le|la|les|l['’]|du|de\s+la|de\s+l['’]|de|het|den|die|der|the)\s+)?(?:ag(?:\s+insurance)?|assureur|compagnie|maatschappij|verzekeraar|versicherer|insurer|preneur(?:\s+d['’]assurance)?|souscripteur|verzekering(?:s)?nemer|versicherungsnehmer(?:in)?|policyholder|client|klant|kunde|customer|assur[ée]e?|verzekerde|versicherter?|insured|courtier|makelaar|vermittler|broker|interm[ée]diaire|tussenpersoon|agent|agence|kantoor|banque|bank|dossier|archive|archief)(?:\s*/\s*[A-Z0-9]{1,12}){0,3})(?![\p{L}\p{N}])";

    public const string DefaultClientKeywordsPattern =
        @"^(?:[Aa]uftraggeber(?:in)?|[Oo]pdrachtgever|[Ss]ouscripteur|[Pp]reneur|[Vv]erzekering(?:s)?nemer|[Vv]ersicherungsnehmer(?:in)?|[Aa]ssur[ée]e?|[Vv]erzekerde|[Vv]ersicherter?|[Tt]itulaire|[Cc]lient|[Kk]lant|[Kk]unde|Customer):?$";

    [ObservableProperty] private string _profileName = "Texte Visible à l'Œil Nu";

    // --- 1. Zones d'exclusion géométriques (X/Y) ---
    public ObservableCollection<ExclusionZoneRule> ExclusionZones { get; set; } = new();

    // --- 2. Règles d'expressions régulières dynamiques ---
    public ObservableCollection<RegexMaskingRule> RegexRules { get; set; } = new();

    // --- 3. Masquage intelligent des noms de clients ---
    [ObservableProperty] private bool _enableDynamicClientNameMasking = true;
    [ObservableProperty] private string _clientKeywordsPattern = DefaultClientKeywordsPattern;
    [ObservableProperty] private string _clientNameReplacementMask = "[NOM_IGNORE]";

    // --- 4. Filtres de filigranes et typographie ---
    [ObservableProperty] private string _exactWatermarkWordsCsv = "SPECIMEN, SPECIMENS, SPECIME, SPECIM, PECIMEN, ECIMEN, TOTEIN, ECIMIEN";
    [ObservableProperty] private string _largeWatermarkFragmentsCsv = "SPECIMEN, SPECIME, SPECIM, PECIMEN, ECIMEN, CIMEN, SPECI, IMEN, TOTEIN, TEST, ECIMIEN";
    [ObservableProperty] private double _watermarkFragmentMinFontSizePt = 16.0;
    [ObservableProperty] private double _maxAllowedFontSizePt = 42.0;
    [ObservableProperty] private double _minAllowedFontSizePt = 1.5;
    [ObservableProperty] private bool _ignoreWhiteOnWhiteText = true;

    // --- 5. Tolérances anti-faux-positifs (Alignement & Reflow) ---
    [ObservableProperty] private int _maxLineShiftForFullLine = 10;
    [ObservableProperty] private int _maxLineShiftForSequence = 8;
    [ObservableProperty] private int _maxLineShiftForSingleWord = 2;
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
            ProfileName = "Texte Visible à l'Œil Nu",
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
                    Name = "Marge technique gauche (28pt)",
                    IsEnabled = true,
                    Scope = PageScope.AllPages,
                    Unit = CoordinateUnit.PdfPoints,
                    Origin = ZoneOrigin.TopLeft,
                    X = 0,
                    Y = 0,
                    Width = 28,
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
                    Pattern = DefaultDateRegexPattern,
                    ReplacementMask = "[DATE_IGNORE]",
                    IgnoreCase = true,
                    MinOccurrences = 2
                },
                new RegexMaskingRule
                {
                    Name = "Mentions d'exemplaire multilingues (exemplaire(s) / exemplaar...)",
                    IsEnabled = true,
                    Pattern = ExemplaireRoutingPattern,
                    ReplacementMask = "",
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
            },
            EnableDynamicClientNameMasking = true,
            IgnoreWhiteOnWhiteText = true,
            MinAllowedFontSizePt = 1.5,
            MaxLineShiftForFullLine = 10,
            MaxLineShiftForSequence = 8,
            MaxLineShiftForSingleWord = 2,
            ClientKeywordsPattern = DefaultClientKeywordsPattern
        };
    }
}
