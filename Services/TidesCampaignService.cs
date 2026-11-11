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
    public string ActiveReleaseName { get; set; } = string.Empty;
    public List<TidesProductItem> Products { get; set; } = new();
    public List<string> DiscoveredReleases { get; set; } = new();
    public string StatusMessage { get; set; } = string.Empty;
}

public interface ITidesCampaignService
{
    string GetDefaultRootPath();
    string GetCurrentMonthReleaseName(DateTime? referenceDate = null);
    List<string> GetSuggestedReleaseNames(int year);
    Task<TidesDiscoveryResult> DiscoverProductsAsync(string rootPath, string releaseFolderName, CancellationToken ct = default);
    List<DocumentPair> BuildOrderedCampaignPairs(IEnumerable<TidesProductItem> selectedProducts, PdfFileService fileService);
    void ExportPerProductSummaries(IEnumerable<DocumentPair> allPairs);
}

public partial class TidesCampaignService : ITidesCampaignService
{
    public const string DefaultNetworkRootPath = @"\\sppdfc01\TIDES\Life";

    private static readonly string[] ChannelsInPriorityOrder = { "Bank", "Broker" };

    /// <summary>
    /// Liste des produits Bank connus dans \\sppdfc01\TIDES\Life\Bank (affichés à titre indicatif si le lecteur réseau est hors-ligne).
    /// </summary>
    private static readonly string[] KnownBankProducts =
    {
        "SP.DH",
        "SP.EFP",
        "SP.FIB",
        "SP.FIP",
        "SP.FIX",
        "SP.PIP",
        "SP.SDR",
        "SP.SFP",
        "SP.SFP Private",
        "SP.SIP"
    };

    [GeneratedRegex(@"^20\d{2}_R\d{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseFolderPattern();

    [GeneratedRegex(@"^(?<year>20\d{2})_R0?(?<month>\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex YearMonthReleaseRegex();

    public string GetDefaultRootPath() => DefaultNetworkRootPath;

    /// <summary>
    /// Calcule automatiquement le nom du dossier de release du mois actuel : ex. en Novembre 2026 -> "2026_R11", en Septembre 2026 -> "2026_R9".
    /// </summary>
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
        return await Task.Run(() => DiscoverProductsInternal(rootPath, releaseFolderName, ct), ct);
    }

    private TidesDiscoveryResult DiscoverProductsInternal(string rawRootPath, string releaseFolderName, CancellationToken ct)
    {
        string rootPath = string.IsNullOrWhiteSpace(rawRootPath)
            ? DefaultNetworkRootPath
            : rawRootPath.Trim();

        string targetRelease = string.IsNullOrWhiteSpace(releaseFolderName)
            ? GetCurrentMonthReleaseName()
            : releaseFolderName.Trim();

        var result = new TidesDiscoveryResult
        {
            ResolvedRootPath = rootPath,
            ActiveReleaseName = targetRelease
        };

        var discoveredReleasesSet = new HashSet<string>(GetSuggestedReleaseNames(DateTime.Now.Year), StringComparer.OrdinalIgnoreCase);
        discoveredReleasesSet.Add(targetRelease);

        // Si l'utilisateur a sélectionné directement "...\TIDES\Life\Bank" ou "...\TIDES\Life\Broker", remonter au parent "...\TIDES\Life"
        string folderNameOnly = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(folderNameOnly, "Bank", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(folderNameOnly, "Broker", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent))
            {
                rootPath = parent;
                result.ResolvedRootPath = rootPath;
            }
        }

        bool rootExists = false;
        try
        {
            rootExists = Directory.Exists(rootPath);
        }
        catch
        {
            rootExists = false;
        }

        result.IsRootReachable = rootExists;

        if (!rootExists)
        {
            // Pré-affichage des produits connus pour permettre à l'utilisateur de visualiser la structure même hors VPN
            foreach (var prodName in KnownBankProducts)
            {
                string prodPath = Path.Combine(rootPath, "Bank", prodName);
                result.Products.Add(new TidesProductItem
                {
                    IsSelected = false,
                    Channel = "Bank",
                    ProductName = prodName,
                    ProductRootPath = prodPath,
                    ReleaseFolderName = targetRelease,
                    SourceDirectoryPath = Path.Combine(prodPath, "Source"),
                    TargetDirectoryPath = Path.Combine(prodPath, targetRelease, "Target"),
                    OutputDirectoryPath = Path.Combine(prodPath, targetRelease, "Output"),
                    HasSourceFolder = false,
                    HasTargetFolder = false,
                    SourcePdfCount = 0,
                    TargetPdfCount = 0,
                    IsReady = false,
                    StatusText = $"Partage réseau inaccessible ({rootPath})"
                });
            }

            result.DiscoveredReleases = SortReleasesList(discoveredReleasesSet);
            result.StatusMessage = $"Le chemin '{rootPath}' n'est pas accessible actuellement (vérifiez votre connexion réseau / VPN).";
            return result;
        }

        // 1. Traiter d'abord Bank (Priorité 1), puis Broker (Priorité 2)
        foreach (var channel in ChannelsInPriorityOrder)
        {
            ct.ThrowIfCancellationRequested();

            string channelDir = Path.Combine(rootPath, channel);
            if (!Directory.Exists(channelDir)) continue;

            List<string> productDirs;
            try
            {
                productDirs = Directory.EnumerateDirectories(channelDir)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                continue;
            }

            foreach (var prodDir in productDirs)
            {
                ct.ThrowIfCancellationRequested();
                var item = InspectProductDirectory(channel, prodDir, targetRelease, discoveredReleasesSet);
                result.Products.Add(item);
            }
        }

        // Si le dossier choisi contient directement des produits (ex: structure personnalisée sans sous-dossier Bank/Broker)
        if (result.Products.Count == 0)
        {
            try
            {
                var subDirs = Directory.EnumerateDirectories(rootPath)
                    .Where(d => !string.Equals(Path.GetFileName(d), "Bank", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(Path.GetFileName(d), "Broker", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var prodDir in subDirs)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = InspectProductDirectory("Bank", prodDir, targetRelease, discoveredReleasesSet);
                    result.Products.Add(item);
                }
            }
            catch
            {
                // Ignorer
            }
        }

        result.DiscoveredReleases = SortReleasesList(discoveredReleasesSet);

        int bankCount = result.Products.Count(p => string.Equals(p.Channel, "Bank", StringComparison.OrdinalIgnoreCase));
        int brokerCount = result.Products.Count(p => string.Equals(p.Channel, "Broker", StringComparison.OrdinalIgnoreCase));
        int readyCount = result.Products.Count(p => p.IsReady);

        result.StatusMessage = $"Scan terminé : {bankCount} produit(s) Bank et {brokerCount} produit(s) Broker trouvés • {readyCount} prêt(s) pour {targetRelease}.";
        return result;
    }

    private TidesProductItem InspectProductDirectory(
        string channel,
        string productDir,
        string requestedRelease,
        HashSet<string> discoveredReleasesSet)
    {
        string productName = Path.GetFileName(productDir);
        var item = new TidesProductItem
        {
            Channel = channel,
            ProductName = productName,
            ProductRootPath = productDir,
            ReleaseFolderName = requestedRelease
        };

        List<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(productDir).ToList();
        }
        catch
        {
            item.StatusText = "Accès refusé au dossier produit";
            return item;
        }

        // Collecter les dossiers 20XX_R* disponibles dans ce produit
        var productReleases = new List<string>();
        foreach (var sub in subDirs)
        {
            string subName = Path.GetFileName(sub);
            if (ReleaseFolderPattern().IsMatch(subName))
            {
                discoveredReleasesSet.Add(subName);
                productReleases.Add(subName);
            }
        }

        productReleases = SortReleasesList(productReleases);
        item.AvailableReleasesCsv = productReleases.Count > 0
            ? string.Join(", ", productReleases.Take(5))
            : "Aucune release 20XX_R*";

        // 1. Résoudre le dossier Source (ex: \\sppdfc01\TIDES\Life\Bank\SP.DH\Source)
        string? sourceDir = subDirs.FirstOrDefault(d =>
            string.Equals(Path.GetFileName(d), "Source", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(sourceDir) && Directory.Exists(sourceDir))
        {
            var (resolvedSourceDir, sourceCount) = ResolvePdfDirectoryWithFallback(sourceDir);
            item.SourceDirectoryPath = resolvedSourceDir;
            item.SourcePdfCount = sourceCount;
            item.HasSourceFolder = true;
        }
        else
        {
            item.SourceDirectoryPath = Path.Combine(productDir, "Source");
            item.HasSourceFolder = false;
            item.SourcePdfCount = 0;
        }

        // 2. Résoudre le dossier Release (ex: 2026_R11 ou variante 2026_R09 / 2026_R9)
        string? matchedReleaseDir = FindMatchingReleaseDirectory(subDirs, requestedRelease);

        if (!string.IsNullOrEmpty(matchedReleaseDir) && Directory.Exists(matchedReleaseDir))
        {
            string actualReleaseFolderName = Path.GetFileName(matchedReleaseDir);
            item.ReleaseFolderName = actualReleaseFolderName;
            item.OutputDirectoryPath = Path.Combine(matchedReleaseDir, "Output");

            // Comme vu sur la capture d'écran (2026_R9 -> Target & Output), les PDF cibles sont dans le sous-dossier "Target"
            string? targetSubDir = null;
            try
            {
                targetSubDir = Directory.EnumerateDirectories(matchedReleaseDir)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Target", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // Ignorer
            }

            if (!string.IsNullOrEmpty(targetSubDir) && Directory.Exists(targetSubDir))
            {
                var (resolvedTargetDir, targetCount) = ResolvePdfDirectoryWithFallback(targetSubDir);
                item.TargetDirectoryPath = resolvedTargetDir;
                item.TargetPdfCount = targetCount;
                item.HasTargetFolder = true;
            }
            else
            {
                // Fallback si les PDF sont directement dans 2026_R11 sans sous-dossier Target
                var (resolvedTargetDir, targetCount) = ResolvePdfDirectoryWithFallback(matchedReleaseDir);
                item.TargetDirectoryPath = resolvedTargetDir;
                item.TargetPdfCount = targetCount;
                item.HasTargetFolder = true;
            }
        }
        else
        {
            item.TargetDirectoryPath = Path.Combine(productDir, requestedRelease, "Target");
            item.OutputDirectoryPath = Path.Combine(productDir, requestedRelease, "Output");
            item.HasTargetFolder = false;
            item.TargetPdfCount = 0;
        }

        item.IsReady = item.HasSourceFolder && item.HasTargetFolder && item.SourcePdfCount > 0 && item.TargetPdfCount > 0;
        item.IsSelected = item.IsReady;

        if (item.IsReady)
        {
            item.StatusText = $"Prêt ({item.SourcePdfCount} Source / {item.TargetPdfCount} Target)";
        }
        else if (!item.HasSourceFolder)
        {
            item.StatusText = "Dossier 'Source' introuvable";
        }
        else if (item.SourcePdfCount == 0)
        {
            item.StatusText = "Dossier 'Source' vide (0 PDF)";
        }
        else if (!item.HasTargetFolder)
        {
            item.StatusText = $"Dossier '{requestedRelease}' absent";
        }
        else
        {
            item.StatusText = $"Dossier '{item.ReleaseFolderName}\\Target' vide (0 PDF)";
        }

        return item;
    }

    private static string? FindMatchingReleaseDirectory(List<string> subDirs, string requestedRelease)
    {
        // Correspondance exacte d'abord
        var exact = subDirs.FirstOrDefault(d =>
            string.Equals(Path.GetFileName(d), requestedRelease, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // Tolérance zéro initial sur le mois (ex: 2026_R9 <-> 2026_R09)
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

            // Si les PDF sont rangés dans un sous-dossier immédiat
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

    /// <summary>
    /// Construit toutes les paires de documents des produits cochés en respectant strictement l'ordre :
    /// 1. D'abord tous les produits Bank cochés
    /// 2. Ensuite tous les produits Broker cochés
    /// </summary>
    public List<DocumentPair> BuildOrderedCampaignPairs(IEnumerable<TidesProductItem> selectedProducts, PdfFileService fileService)
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
                product.OutputDirectoryPath);

            allPairs.AddRange(productPairs);
        }

        return allPairs;
    }

    /// <summary>
    /// Génère un fichier récapitulatif CSV directement dans le dossier &lt;Release&gt;\Output de chaque produit traité.
    /// </summary>
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

                var sb = new StringBuilder();
                sb.AppendLine("Canal;Produit;Release;Document;Statut;Score SSIM (%);Nb Differences;Alertes Critiques;Page;Severite;Categorie;Texte Source (Attendu);Texte Target (Obtenu);Rapport PDF");

                foreach (var p in group)
                {
                    if (p.DiffBlocks.Count == 0)
                    {
                        sb.AppendLine($"\"{p.Channel}\";\"{p.ProductName}\";\"{p.ReleaseName}\";\"{p.FileNameOnly}\";\"{p.Status}\";\"{p.SsimScore:0.00}\";{p.DiffCount};{p.CriticalCount};1;\"Conforme\";\"Aucune\";\"\";\"\";\"{p.ReportPath}\"");
                    }
                    else
                    {
                        foreach (var b in p.DiffBlocks)
                        {
                            string oldClean = (b.OldText ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
                            string newClean = (b.NewText ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
                            sb.AppendLine($"\"{p.Channel}\";\"{p.ProductName}\";\"{p.ReleaseName}\";\"{p.FileNameOnly}\";\"{p.Status}\";\"{p.SsimScore:0.00}\";{p.DiffCount};{p.CriticalCount};{b.PageNumber};\"{b.SeverityLabel}\";\"{b.Category}\";\"{oldClean}\";\"{newClean}\";\"{p.ReportPath}\"");
                        }
                    }
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
