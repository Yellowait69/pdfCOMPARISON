using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PDFComparison.Models;

namespace PDFComparison.Services;

public class TidesDiscoveryResult
{
    public bool IsRootReachable { get; set; }
    public string ResolvedRootPath { get; set; } = string.Empty;
    public string ResolvedSecondaryTargetPath { get; set; } = string.Empty;
    public string ActiveReleaseName { get; set; } = string.Empty;
    public List<TidesProductItem> Products { get; set; } = new();
    public List<string> DiscoveredReleases { get; set; } = new();
    public List<string> DiscoveredChannels { get; set; } = new();
    public string StatusMessage { get; set; } = string.Empty;
}

public interface ITidesCampaignService
{
    string GetDefaultRootPath();
    string GetCurrentMonthReleaseName(DateTime? referenceDate = null);
    List<string> GetSuggestedReleaseNames(int year);
    Task<TidesDiscoveryResult> DiscoverProductsAsync(string rootPath, string releaseFolderName, CancellationToken ct = default);
    Task<TidesDiscoveryResult> DiscoverProductsForProfileAsync(
        AutomatedScanProfile profile,
        string? rootOverride = null,
        string? secondaryTargetOverride = null,
        string? releaseOverride = null,
        CancellationToken ct = default);
    List<DocumentPair> BuildOrderedCampaignPairs(
        IEnumerable<TidesProductItem> selectedProducts,
        PdfFileService fileService,
        PdfPairingOptions? pairingOptions = null);
    void ExportPerProductSummaries(IEnumerable<DocumentPair> allPairs);
}

public partial class TidesCampaignService : ITidesCampaignService
{
    public const string DefaultNetworkRootPath = @"\\sppdfc01\TIDES\Life";

    private static readonly string[] KnownBankProducts =
    {
        "SP.DH", "SP.EFP", "SP.FIB", "SP.FIP", "SP.FIX",
        "SP.PIP", "SP.SDR", "SP.SFP", "SP.SFP Private", "SP.SIP"
    };

    [GeneratedRegex(@"^20\d{2}_R\d{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseFolderPattern();

    [GeneratedRegex(@"^(?<year>20\d{2})_R0?(?<month>\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex YearMonthReleaseRegex();

    public string GetDefaultRootPath() => DefaultNetworkRootPath;

    public string GetCurrentMonthReleaseName(DateTime? referenceDate = null)
    {
        var now = referenceDate ?? DateTime.Now;
        return $"{now.Year}_R{now.Month}";
    }

    public List<string> GetSuggestedReleaseNames(int year)
    {
        var list = new List<string>(12);
        for (int m = 1; m <= 12; m++)
        {
            list.Add($"{year}_R{m}");
        }
        return list;
    }

    public async Task<TidesDiscoveryResult> DiscoverProductsAsync(string rootPath, string releaseFolderName, CancellationToken ct = default)
    {
        var defaultProfile = AutomatedScanProfile.CreateDefaultEquipeTesting();
        return await DiscoverProductsForProfileAsync(defaultProfile, rootPath, null, releaseFolderName, ct);
    }

    public async Task<TidesDiscoveryResult> DiscoverProductsForProfileAsync(
        AutomatedScanProfile profile,
        string? rootOverride = null,
        string? secondaryTargetOverride = null,
        string? releaseOverride = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return await Task.Run(
            () => DiscoverInternal(profile, rootOverride, secondaryTargetOverride, releaseOverride, ct),
            ct);
    }

    private TidesDiscoveryResult DiscoverInternal(
        AutomatedScanProfile profile,
        string? rootOverride,
        string? secondaryTargetOverride,
        string? releaseOverride,
        CancellationToken ct)
    {
        return profile.TopologyMode switch
        {
            ScanTopologyMode.MirrorTrees => DiscoverMirrorTrees(profile, rootOverride, secondaryTargetOverride, ct),
            ScanTopologyMode.AutoDiscoverPairs => DiscoverAutoPairs(profile, rootOverride, ct),
            _ => DiscoverProductSubfolders(profile, rootOverride, releaseOverride, ct)
        };
    }

    // =========================================================================
    // MODE 1 : PAR SOUS-DOSSIER PRODUIT (ÉQUIPE TESTING TIDES OU PERSONNALISÉ)
    // =========================================================================
    private TidesDiscoveryResult DiscoverProductSubfolders(
        AutomatedScanProfile profile,
        string? rootOverride,
        string? releaseOverride,
        CancellationToken ct)
    {
        string rootPath = (!string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : profile.RootPath)?.Trim().Trim('"') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rootPath) && profile.IsBuiltInTides)
        {
            rootPath = DefaultNetworkRootPath;
        }

        string targetRelease = !string.IsNullOrWhiteSpace(releaseOverride)
            ? releaseOverride.Trim()
            : (!string.IsNullOrWhiteSpace(profile.DefaultReleaseName) ? profile.DefaultReleaseName.Trim() : GetCurrentMonthReleaseName());

        var result = new TidesDiscoveryResult
        {
            ResolvedRootPath = rootPath,
            ActiveReleaseName = profile.UseReleaseFolder ? targetRelease : string.Empty
        };

        var discoveredReleasesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profile.IsBuiltInTides)
        {
            foreach (var r in GetSuggestedReleaseNames(DateTime.Now.Year))
                discoveredReleasesSet.Add(r);
        }
        if (profile.UseReleaseFolder && !string.IsNullOrWhiteSpace(targetRelease))
        {
            discoveredReleasesSet.Add(targetRelease);
        }

        var configuredChannels = (profile.ChannelsCsv ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Si l'utilisateur a sélectionné directement l'un des sous-dossiers de canal (ex: ...\Bank), remonter au parent
        if (!string.IsNullOrWhiteSpace(rootPath) && configuredChannels.Count > 0)
        {
            string folderNameOnly = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (configuredChannels.Any(c => string.Equals(c, folderNameOnly, StringComparison.OrdinalIgnoreCase)))
            {
                string? parent = Path.GetDirectoryName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    rootPath = parent;
                    result.ResolvedRootPath = rootPath;
                }
            }
        }

        bool rootExists = !string.IsNullOrWhiteSpace(rootPath) && SafeDirectoryExists(rootPath);
        result.IsRootReachable = rootExists;

        if (!rootExists)
        {
            if (profile.IsBuiltInTides)
            {
                foreach (var prodName in KnownBankProducts)
                {
                    string prodPath = Path.Combine(rootPath, "Bank", prodName);
                    result.Products.Add(new TidesProductItem
                    {
                        IsSelected = false,
                        Channel = "Bank",
                        SortPriority = 1,
                        ProductName = prodName,
                        ProductRootPath = prodPath,
                        ReleaseFolderName = targetRelease,
                        SourceDirectoryPath = Path.Combine(prodPath, "Source"),
                        TargetDirectoryPath = Path.Combine(prodPath, targetRelease, "Target"),
                        OutputDirectoryPath = Path.Combine(prodPath, targetRelease, "Output"),
                        IsReady = false,
                        StatusText = $"Dossier inaccessible ({rootPath})"
                    });
                }
            }

            result.DiscoveredReleases = SortReleasesList(discoveredReleasesSet);
            result.DiscoveredChannels = configuredChannels;
            result.StatusMessage = string.IsNullOrWhiteSpace(rootPath)
                ? "Veuillez choisir un dossier racine puis cliquer sur Scanner."
                : $"Le chemin '{rootPath}' n'est pas accessible actuellement.";
            return result;
        }

        if (configuredChannels.Count > 0)
        {
            string effectiveChannelsParent = rootPath;
            if (configuredChannels.All(ch => string.IsNullOrEmpty(FindChildDirectoryIgnoreCase(rootPath, ch))))
            {
                string? lifeSub = FindChildDirectoryIgnoreCase(rootPath, "Life");
                if (!string.IsNullOrEmpty(lifeSub) &&
                    configuredChannels.Any(ch => !string.IsNullOrEmpty(FindChildDirectoryIgnoreCase(lifeSub, ch))))
                {
                    effectiveChannelsParent = lifeSub;
                    result.ResolvedRootPath = lifeSub;
                }
            }

            for (int idx = 0; idx < configuredChannels.Count; idx++)
            {
                ct.ThrowIfCancellationRequested();
                string channel = ProductCodeHelper.ResolveEffectiveChannel(configuredChannels[idx]);
                string? channelDir = FindChildDirectoryIgnoreCase(effectiveChannelsParent, configuredChannels[idx]);
                if (string.IsNullOrEmpty(channelDir)) continue;

                foreach (var prodDir in SafeEnumerateDirectories(channelDir))
                {
                    ct.ThrowIfCancellationRequested();
                    result.Products.Add(InspectProductSubfolderItem(profile, channel, idx + 1, prodDir, targetRelease, discoveredReleasesSet));
                }
            }
        }

        // Si l'utilisateur a pointé directement un dossier Produit (contenant Source / 2026_Rxx) ou un parent de produits
        if (result.Products.Count == 0)
        {
            if (FindChildDirectoryIgnoreCase(rootPath, "Source") != null)
            {
                string singleCh = ProductCodeHelper.ResolveEffectiveChannel(null, rootPath, null);
                result.Products.Add(InspectProductSubfolderItem(profile, singleCh, 1, rootPath, targetRelease, discoveredReleasesSet));
            }
            else
            {
                foreach (var prodDir in SafeEnumerateDirectories(rootPath))
                {
                    ct.ThrowIfCancellationRequested();
                    string dName = Path.GetFileName(prodDir);
                    if (configuredChannels.Any(c => string.Equals(c, dName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string prodChannel = ProductCodeHelper.ResolveEffectiveChannel(null, prodDir, null, dName);
                    result.Products.Add(InspectProductSubfolderItem(profile, prodChannel, 1, prodDir, targetRelease, discoveredReleasesSet));
                }
            }
        }

        result.DiscoveredReleases = SortReleasesList(discoveredReleasesSet);
        result.DiscoveredChannels = result.Products
            .Select(p => p.Channel)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int readyCount = result.Products.Count(p => p.IsReady);
        result.StatusMessage = profile.UseReleaseFolder
            ? $"Scan terminé : {result.Products.Count} dossier(s) produit trouvé(s) • {readyCount} prêt(s) pour '{targetRelease}'."
            : $"Scan terminé : {result.Products.Count} dossier(s) produit trouvé(s) • {readyCount} prêt(s) à comparer.";
        return result;
    }

    private TidesProductItem InspectProductSubfolderItem(
        AutomatedScanProfile profile,
        string channel,
        int sortPriority,
        string productDir,
        string requestedRelease,
        HashSet<string> discoveredReleasesSet)
    {
        string productName = Path.GetFileName(productDir);
        var item = new TidesProductItem
        {
            Channel = channel,
            SortPriority = sortPriority,
            ProductName = productName,
            ProductRootPath = productDir,
            ReleaseFolderName = profile.UseReleaseFolder ? requestedRelease : string.Empty
        };

        var subDirs = SafeEnumerateDirectories(productDir);
        string srcTemplate = string.IsNullOrWhiteSpace(profile.SourceSubfolderTemplate) ? "Source" : profile.SourceSubfolderTemplate.Trim();
        string trgTemplate = string.IsNullOrWhiteSpace(profile.TargetSubfolderTemplate) ? "Target" : profile.TargetSubfolderTemplate.Trim();
        string outTemplate = (profile.ProductOutputSubfolderTemplate ?? string.Empty).Trim();

        // Collecter les dossiers de release disponibles si le mode Release est actif
        if (profile.UseReleaseFolder)
        {
            string srcRootSegment = FirstPathSegment(srcTemplate);
            string outRootSegment = FirstPathSegment(outTemplate);
            var prodReleases = new List<string>();

            foreach (var sub in subDirs)
            {
                string subName = Path.GetFileName(sub);
                if (profile.IsBuiltInTides)
                {
                    if (ReleaseFolderPattern().IsMatch(subName))
                    {
                        discoveredReleasesSet.Add(subName);
                        prodReleases.Add(subName);
                    }
                }
                else if (!string.Equals(subName, srcRootSegment, StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(subName, outRootSegment, StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(subName, "Source", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(subName, "Output", StringComparison.OrdinalIgnoreCase))
                {
                    discoveredReleasesSet.Add(subName);
                    prodReleases.Add(subName);
                }
            }

            prodReleases = SortReleasesList(prodReleases);
            item.AvailableReleasesCsv = prodReleases.Count > 0
                ? string.Join(", ", prodReleases.Take(6))
                : "Aucune release détectée";
        }

        // 1. Résoudre le dossier Source
        string expandedSrcRel = ExpandReleaseToken(srcTemplate, requestedRelease);
        string? resolvedSrcBase = ResolveRelativeSubPath(productDir, expandedSrcRel);
        if (!string.IsNullOrEmpty(resolvedSrcBase) && SafeDirectoryExists(resolvedSrcBase))
        {
            var (resolvedSourceDir, sourceCount) = ResolvePdfDirectoryWithFallback(resolvedSrcBase);
            item.SourceDirectoryPath = resolvedSourceDir;
            item.SourcePdfCount = sourceCount;
            item.HasSourceFolder = true;
        }
        else
        {
            item.SourceDirectoryPath = Path.Combine(productDir, expandedSrcRel);
            item.HasSourceFolder = false;
            item.SourcePdfCount = 0;
        }

        // 2. Résoudre le dossier Cible (avec ou sans Release)
        if (profile.UseReleaseFolder)
        {
            string effectiveTrgTemplate = trgTemplate.Contains("{Release}", StringComparison.OrdinalIgnoreCase)
                ? trgTemplate
                : "{Release}\\" + trgTemplate;

            string? matchedReleaseDir = FindMatchingReleaseDirectory(subDirs, requestedRelease);
            string actualRelease = matchedReleaseDir != null ? Path.GetFileName(matchedReleaseDir) : requestedRelease;
            item.ReleaseFolderName = actualRelease;

            if (!string.IsNullOrWhiteSpace(outTemplate))
            {
                string effectiveOut = outTemplate.Contains("{Release}", StringComparison.OrdinalIgnoreCase)
                    ? ExpandReleaseToken(outTemplate, actualRelease)
                    : Path.Combine(actualRelease, outTemplate);
                item.OutputDirectoryPath = Path.Combine(productDir, effectiveOut);
            }

            string expandedTrgRel = ExpandReleaseToken(effectiveTrgTemplate, actualRelease);
            string? resolvedTrgBase = ResolveRelativeSubPath(productDir, expandedTrgRel);

            if (!string.IsNullOrEmpty(resolvedTrgBase) && SafeDirectoryExists(resolvedTrgBase))
            {
                var (resolvedTargetDir, targetCount) = ResolvePdfDirectoryWithFallback(resolvedTrgBase);
                item.TargetDirectoryPath = resolvedTargetDir;
                item.TargetPdfCount = targetCount;
                item.HasTargetFolder = true;
            }
            else if (matchedReleaseDir != null && SafeDirectoryExists(matchedReleaseDir))
            {
                // Fallback si les PDF sont directement dans le dossier de release
                var (resolvedTargetDir, targetCount) = ResolvePdfDirectoryWithFallback(matchedReleaseDir);
                item.TargetDirectoryPath = resolvedTargetDir;
                item.TargetPdfCount = targetCount;
                item.HasTargetFolder = true;
            }
            else
            {
                item.TargetDirectoryPath = Path.Combine(productDir, expandedTrgRel);
                item.HasTargetFolder = false;
                item.TargetPdfCount = 0;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(outTemplate))
            {
                item.OutputDirectoryPath = Path.Combine(productDir, outTemplate);
            }

            string? resolvedTrgBase = ResolveRelativeSubPath(productDir, trgTemplate);
            if (!string.IsNullOrEmpty(resolvedTrgBase) && SafeDirectoryExists(resolvedTrgBase))
            {
                var (resolvedTargetDir, targetCount) = ResolvePdfDirectoryWithFallback(resolvedTrgBase);
                item.TargetDirectoryPath = resolvedTargetDir;
                item.TargetPdfCount = targetCount;
                item.HasTargetFolder = true;
            }
            else
            {
                item.TargetDirectoryPath = Path.Combine(productDir, trgTemplate);
                item.HasTargetFolder = false;
                item.TargetPdfCount = 0;
            }
        }

        PopulateStatusSummary(item, srcTemplate, profile.UseReleaseFolder ? requestedRelease : trgTemplate);
        return item;
    }

    // =========================================================================
    // MODE 2 : DEUX DOSSIERS SÉPARÉS MIROIRS (RACINE SOURCE VS RACINE CIBLE)
    // =========================================================================
    private TidesDiscoveryResult DiscoverMirrorTrees(
        AutomatedScanProfile profile,
        string? sourceRootOverride,
        string? targetRootOverride,
        CancellationToken ct)
    {
        string srcRoot = (!string.IsNullOrWhiteSpace(sourceRootOverride) ? sourceRootOverride : profile.RootPath)?.Trim().Trim('"') ?? string.Empty;
        string trgRoot = (!string.IsNullOrWhiteSpace(targetRootOverride) ? targetRootOverride : profile.SecondaryTargetRootPath)?.Trim().Trim('"') ?? string.Empty;

        var result = new TidesDiscoveryResult
        {
            ResolvedRootPath = srcRoot,
            ResolvedSecondaryTargetPath = trgRoot,
            IsRootReachable = SafeDirectoryExists(srcRoot) && SafeDirectoryExists(trgRoot)
        };

        if (!SafeDirectoryExists(srcRoot))
        {
            result.StatusMessage = string.IsNullOrWhiteSpace(srcRoot)
                ? "Veuillez indiquer le dossier Source global et le dossier Cible global."
                : $"Le dossier Source '{srcRoot}' est introuvable.";
            return result;
        }

        bool trgRootExists = SafeDirectoryExists(trgRoot);

        void CollectMirrorNodes(string currentSrcDir, string relativePath, int depth)
        {
            ct.ThrowIfCancellationRequested();
            int srcPdfs = CountTopLevelPdfs(currentSrcDir);

            if (srcPdfs > 0)
            {
                string? matchedTrgDir = string.IsNullOrEmpty(relativePath)
                    ? (trgRootExists ? trgRoot : null)
                    : (trgRootExists ? ResolveRelativeSubPath(trgRoot, relativePath) : null);

                int trgPdfs = (!string.IsNullOrEmpty(matchedTrgDir) && SafeDirectoryExists(matchedTrgDir))
                    ? CountTopLevelPdfs(matchedTrgDir)
                    : 0;

                string effectiveTrgPath = matchedTrgDir ?? Path.Combine(trgRoot, relativePath);
                string rawFolderProd = Path.GetFileName(currentSrcDir.TrimEnd('\\', '/'));
                string prodLabel = ProductCodeHelper.ResolveEffectiveProductCode(rawFolderProd, null, currentSrcDir, effectiveTrgPath);
                if (string.IsNullOrWhiteSpace(prodLabel))
                {
                    prodLabel = !string.IsNullOrWhiteSpace(rawFolderProd) ? rawFolderProd : "Produit";
                }
                string channelLabel = ProductCodeHelper.ResolveEffectiveChannel(null, currentSrcDir, effectiveTrgPath, prodLabel);

                var item = new TidesProductItem
                {
                    Channel = channelLabel,
                    SortPriority = string.Equals(channelLabel, "Bank", StringComparison.OrdinalIgnoreCase) ? 1 : 2,
                    ProductName = prodLabel,
                    ProductRootPath = currentSrcDir,
                    SourceDirectoryPath = currentSrcDir,
                    TargetDirectoryPath = effectiveTrgPath,
                    OutputDirectoryPath = string.Empty,
                    HasSourceFolder = true,
                    HasTargetFolder = !string.IsNullOrEmpty(matchedTrgDir) && SafeDirectoryExists(matchedTrgDir),
                    SourcePdfCount = srcPdfs,
                    TargetPdfCount = trgPdfs
                };

                PopulateStatusSummary(item, "Source", "Cible");
                result.Products.Add(item);
            }

            if (depth >= 3) return;

            foreach (var child in SafeEnumerateDirectories(currentSrcDir))
            {
                string childName = Path.GetFileName(child);
                if (string.Equals(childName, "Output", StringComparison.OrdinalIgnoreCase)) continue;
                string nextRel = string.IsNullOrEmpty(relativePath) ? childName : Path.Combine(relativePath, childName);
                CollectMirrorNodes(child, nextRel, depth + 1);
            }
        }

        CollectMirrorNodes(srcRoot, string.Empty, 0);

        result.DiscoveredChannels = result.Products
            .Select(p => p.Channel)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int ready = result.Products.Count(p => p.IsReady);
        result.StatusMessage = !trgRootExists
            ? $"Dossier Source scanné ({result.Products.Count} lot(s)), mais le dossier Cible '{trgRoot}' est introuvable."
            : $"Scan terminé : {result.Products.Count} lot(s) PDF détecté(s) • {ready} prêt(s) à comparer.";
        return result;
    }

    // =========================================================================
    // MODE 3 : DÉTECTION AUTOMATIQUE DE COUPLES SOURCE / TARGET DANS UNE RACINE
    // =========================================================================
    private TidesDiscoveryResult DiscoverAutoPairs(
        AutomatedScanProfile profile,
        string? rootOverride,
        CancellationToken ct)
    {
        string rootPath = (!string.IsNullOrWhiteSpace(rootOverride) ? rootOverride : profile.RootPath)?.Trim().Trim('"') ?? string.Empty;

        var result = new TidesDiscoveryResult
        {
            ResolvedRootPath = rootPath,
            IsRootReachable = SafeDirectoryExists(rootPath)
        };

        if (!result.IsRootReachable)
        {
            result.StatusMessage = string.IsNullOrWhiteSpace(rootPath)
                ? "Choisissez un dossier racine à explorer puis cliquez sur Scanner."
                : $"Le dossier '{rootPath}' n'est pas accessible.";
            return result;
        }

        var sourceAliases = ParseCsvSet(profile.AutoSourceFolderNamesCsv, new[] { "Source", "Attendu", "Ref", "Reference", "Old", "Avant", "Prod" });
        var targetAliases = ParseCsvSet(profile.AutoTargetFolderNamesCsv, new[] { "Target", "Cible", "Obtenu", "Cand", "New", "Apres", "Recette", "UAT" });

        void WalkDirectory(string dir, int depth)
        {
            ct.ThrowIfCancellationRequested();
            var children = SafeEnumerateDirectories(dir);
            if (children.Count == 0) return;

            string? matchedSourceChild = children.FirstOrDefault(c => sourceAliases.Contains(Path.GetFileName(c)));
            string? matchedTargetChild = children.FirstOrDefault(c => targetAliases.Contains(Path.GetFileName(c)));

            if (matchedSourceChild != null || matchedTargetChild != null)
            {
                // Si seul Source existe mais un sous-dossier de release contient un alias Target (ex: <Dir>\2026_R11\Target)
                if (matchedSourceChild != null && matchedTargetChild == null)
                {
                    foreach (var sibling in children.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        if (string.Equals(sibling, matchedSourceChild, StringComparison.OrdinalIgnoreCase)) continue;
                        var grandChildren = SafeEnumerateDirectories(sibling);
                        var nestedTrg = grandChildren.FirstOrDefault(gc => targetAliases.Contains(Path.GetFileName(gc)));
                        if (nestedTrg != null)
                        {
                            matchedTargetChild = nestedTrg;
                            break;
                        }
                    }
                }

                if (matchedSourceChild != null)
                {
                    var (resSrc, srcCount) = ResolvePdfDirectoryWithFallback(matchedSourceChild);
                    string resTrg = matchedTargetChild ?? Path.Combine(dir, targetAliases.First());
                    int trgCount = 0;
                    bool hasTrg = false;
                    if (matchedTargetChild != null && SafeDirectoryExists(matchedTargetChild))
                    {
                        (resTrg, trgCount) = ResolvePdfDirectoryWithFallback(matchedTargetChild);
                        hasTrg = true;
                    }

                    string prodName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                    if (string.IsNullOrWhiteSpace(prodName))
                    {
                        prodName = ProductCodeHelper.ResolveEffectiveProductCode(null, null, resSrc, resTrg);
                    }
                    if (string.IsNullOrWhiteSpace(prodName)) prodName = "Produit";

                    string channelName = ProductCodeHelper.ResolveEffectiveChannel(null, resSrc, resTrg, prodName);

                    var item = new TidesProductItem
                    {
                        Channel = channelName,
                        SortPriority = string.Equals(channelName, "Bank", StringComparison.OrdinalIgnoreCase) ? 1 : 2,
                        ProductName = prodName,
                        ProductRootPath = dir,
                        SourceDirectoryPath = resSrc,
                        TargetDirectoryPath = resTrg,
                        OutputDirectoryPath = Path.Combine(dir, "Output"),
                        HasSourceFolder = true,
                        HasTargetFolder = hasTrg,
                        SourcePdfCount = srcCount,
                        TargetPdfCount = trgCount
                    };

                    PopulateStatusSummary(item, Path.GetFileName(matchedSourceChild), matchedTargetChild != null ? Path.GetFileName(matchedTargetChild) : "Cible");
                    result.Products.Add(item);
                    return;
                }
            }

            if (depth >= 4) return;

            foreach (var child in children)
            {
                string cName = Path.GetFileName(child);
                if (string.Equals(cName, "Output", StringComparison.OrdinalIgnoreCase)) continue;
                WalkDirectory(child, depth + 1);
            }
        }

        WalkDirectory(rootPath, 0);

        // Fallback pratique : si la racine contient directement des PDF ou exactement 2 sous-dossiers contenant des PDF
        if (result.Products.Count == 0)
        {
            var topDirsWithPdfs = SafeEnumerateDirectories(rootPath)
                .Select(d => (Dir: d, Count: CountTopLevelPdfs(d)))
                .Where(x => x.Count > 0)
                .ToList();

            if (topDirsWithPdfs.Count == 2)
            {
                string rootProdName = Path.GetFileName(rootPath.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(rootProdName)) rootProdName = "Produit";
                string autoChannel = ProductCodeHelper.ResolveEffectiveChannel(null, topDirsWithPdfs[0].Dir, topDirsWithPdfs[1].Dir, rootProdName);

                var item = new TidesProductItem
                {
                    Channel = autoChannel,
                    SortPriority = string.Equals(autoChannel, "Bank", StringComparison.OrdinalIgnoreCase) ? 1 : 2,
                    ProductName = rootProdName,
                    ProductRootPath = rootPath,
                    SourceDirectoryPath = topDirsWithPdfs[0].Dir,
                    TargetDirectoryPath = topDirsWithPdfs[1].Dir,
                    OutputDirectoryPath = Path.Combine(rootPath, "Output"),
                    HasSourceFolder = true,
                    HasTargetFolder = true,
                    SourcePdfCount = topDirsWithPdfs[0].Count,
                    TargetPdfCount = topDirsWithPdfs[1].Count
                };
                PopulateStatusSummary(item, Path.GetFileName(topDirsWithPdfs[0].Dir), Path.GetFileName(topDirsWithPdfs[1].Dir));
                result.Products.Add(item);
            }
        }

        result.DiscoveredChannels = result.Products
            .Select(p => p.Channel)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int readyCount = result.Products.Count(p => p.IsReady);
        result.StatusMessage = result.Products.Count > 0
            ? $"Scan automatique terminé : {result.Products.Count} couple(s) Source/Cible trouvé(s) • {readyCount} prêt(s)."
            : "Aucun couple de sous-dossiers Source / Cible trouvé dans ce dossier.";
        return result;
    }

    private static void PopulateStatusSummary(TidesProductItem item, string srcLabel, string trgLabel)
    {
        item.IsReady = item.HasSourceFolder && item.HasTargetFolder && item.SourcePdfCount > 0 && item.TargetPdfCount > 0;
        item.IsSelected = item.IsReady;

        if (item.IsReady)
        {
            item.StatusText = $"Prêt ({item.SourcePdfCount} Source / {item.TargetPdfCount} Cible)";
        }
        else if (!item.HasSourceFolder)
        {
            item.StatusText = $"Dossier '{srcLabel}' introuvable";
        }
        else if (item.SourcePdfCount == 0)
        {
            item.StatusText = $"Dossier '{srcLabel}' vide (0 PDF)";
        }
        else if (!item.HasTargetFolder)
        {
            item.StatusText = $"Dossier '{trgLabel}' absent";
        }
        else
        {
            item.StatusText = $"Dossier '{trgLabel}' vide (0 PDF)";
        }
    }

    private static HashSet<string> ParseCsvSet(string? csv, string[] defaults)
    {
        var items = (csv ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in items) set.Add(x);
        if (set.Count == 0)
        {
            foreach (var d in defaults) set.Add(d);
        }
        return set;
    }

    private static string ExpandReleaseToken(string template, string release)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;
        return Regex.Replace(template, @"\{Release\}", release ?? string.Empty, RegexOptions.IgnoreCase)
            .Trim('\\', '/');
    }

    private static string FirstPathSegment(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;
        return relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    }

    private static string? ResolveRelativeSubPath(string baseDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return baseDir;

        string current = baseDir;
        var segments = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            string? next = FindChildDirectoryIgnoreCase(current, seg);
            if (next == null) return null;
            current = next;
        }
        return current;
    }

    private static string? FindChildDirectoryIgnoreCase(string parentDir, string childName)
    {
        try
        {
            string direct = Path.Combine(parentDir, childName);
            if (Directory.Exists(direct)) return direct;

            return Directory.EnumerateDirectories(parentDir)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), childName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeDirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static List<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static int CountTopLevelPdfs(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly).Count();
        }
        catch
        {
            return 0;
        }
    }

    private static string? FindMatchingReleaseDirectory(List<string> subDirs, string requestedRelease)
    {
        var exact = subDirs.FirstOrDefault(d =>
            string.Equals(Path.GetFileName(d), requestedRelease, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var matchReq = YearMonthReleaseRegex().Match(requestedRelease);
        if (matchReq.Success &&
            int.TryParse(matchReq.Groups["year"].Value, out int reqYear) &&
            int.TryParse(matchReq.Groups["month"].Value, out int reqMonth))
        {
            foreach (var dir in subDirs)
            {
                var m = YearMonthReleaseRegex().Match(Path.GetFileName(dir));
                if (m.Success &&
                    int.TryParse(m.Groups["year"].Value, out int y) &&
                    int.TryParse(m.Groups["month"].Value, out int month) &&
                    y == reqYear && month == reqMonth)
                {
                    return dir;
                }
            }
        }

        return null;
    }

    private static (string ResolvedPath, int PdfCount) ResolvePdfDirectoryWithFallback(string baseDir)
    {
        try
        {
            int topLevelCount = Directory.EnumerateFiles(baseDir, "*.pdf", SearchOption.TopDirectoryOnly).Count();
            if (topLevelCount > 0)
            {
                return (baseDir, topLevelCount);
            }

            foreach (var sub in Directory.EnumerateDirectories(baseDir))
            {
                string subName = Path.GetFileName(sub);
                if (string.Equals(subName, "Output", StringComparison.OrdinalIgnoreCase)) continue;

                int subCount = Directory.EnumerateFiles(sub, "*.pdf", SearchOption.TopDirectoryOnly).Count();
                if (subCount > 0)
                {
                    return (sub, subCount);
                }
            }
        }
        catch
        {
            // Ignorer erreurs d'accès
        }

        return (baseDir, 0);
    }

    private static List<string> SortReleasesList(IEnumerable<string> releases)
    {
        return releases
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(r =>
            {
                var m = YearMonthReleaseRegex().Match(r);
                if (m.Success &&
                    int.TryParse(m.Groups["year"].Value, out int y) &&
                    int.TryParse(m.Groups["month"].Value, out int month))
                {
                    return y * 100 + month;
                }
                return 0;
            })
            .ThenByDescending(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<DocumentPair> BuildOrderedCampaignPairs(
        IEnumerable<TidesProductItem> selectedProducts,
        PdfFileService fileService,
        PdfPairingOptions? pairingOptions = null)
    {
        var allPairs = new List<DocumentPair>();

        var orderedProducts = selectedProducts
            .Where(p => p.IsSelected && p.HasSourceFolder && p.HasTargetFolder)
            .OrderBy(p => p.ChannelPriority)
            .ThenBy(p => p.ProductName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var product in orderedProducts)
        {
            var productPairs = fileService.MatchFiles(
                product.SourceDirectoryPath,
                product.TargetDirectoryPath,
                product.Channel,
                product.ProductName,
                product.ReleaseFolderName,
                product.OutputDirectoryPath,
                pairingOptions);

            allPairs.AddRange(productPairs);
        }

        return allPairs;
    }

    public void ExportPerProductSummaries(IEnumerable<DocumentPair> allPairs)
    {
        var groups = allPairs
            .Where(p => !string.IsNullOrWhiteSpace(p.ProductOutputDirectory))
            .GroupBy(p => p.ProductOutputDirectory, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            string productOutputDir = group.Key;
            try
            {
                Directory.CreateDirectory(productOutputDir);
                string csvPath = Path.Combine(productOutputDir, "Summary_PDFComparatorPro.csv");

                var groupPairs = group.ToList();
                var summaries = ProductDifferenceGroupingService.BuildSummariesFromPairs(groupPairs);
                var groupedProducts = ProductDifferenceGroupingService.GroupSummariesByProduct(summaries);

                var sb = new StringBuilder();
                sb.AppendLine("Canal;Produit;Release;Nb PDF Regroupes;Documents Concernes;Statut;Pages;Texte Source (Attendu);Texte Target (Obtenu);Rapport Exemple");

                string sampleRelease = groupPairs.FirstOrDefault()?.ReleaseName ?? string.Empty;

                foreach (var prod in groupedProducts)
                {
                    foreach (var diffItem in prod.UniqueDifferences)
                    {
                        var b = diffItem.Block;
                        string docsList = string.Join(" | ", diffItem.ImpactedDocs.Select(d => d.DisplayFileName)).Replace("\"", "\"\"");
                        string oldClean = (b.OldText ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
                        string newClean = (b.NewText ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
                        string sampleRep = (diffItem.ImpactedDocs.FirstOrDefault()?.ReportFileName ?? "").Replace("\"", "\"\"");

                        sb.AppendLine($"\"{prod.Channel}\";\"{prod.ProductName}\";\"{sampleRelease}\";{diffItem.ImpactedDocs.Count};\"{docsList}\";\"Different\";\"{diffItem.PagesCompactLabel}\";\"{oldClean}\";\"{newClean}\";\"{sampleRep}\"");
                    }
                }

                foreach (var p in groupPairs.Where(x => x.DiffBlocks.Count == 0))
                {
                    sb.AppendLine($"\"{p.EffectiveChannel}\";\"{p.EffectiveProductCode}\";\"{p.ReleaseName}\";1;\"{p.FileNameOnly}\";\"{p.Status}\";\"P.1\";\"\";\"\";\"{p.ReportPath}\"");
                }

                File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            catch
            {
                // Ignorer si le partage réseau est en lecture seule
            }
        }
    }
}
