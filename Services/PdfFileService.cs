using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig;

namespace PDFComparison.Services;

public partial class PdfFileService
{
    private readonly IConfigurationService? _configService;

    [GeneratedRegex(@"([A-Z]{2}_\d+_\d+)\.pdf$", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultKeyRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"(?:[-_.\s]*\b20\d{2}[-_.]?(?:0[1-9]|1[0-2])[-_.]?(?:0[1-9]|[12]\d|3[01])(?:[-_.\s]?\d{4,6})?\b|[-_.\s]*\b(?:0[1-9]|[12]\d|3[01])[-_.](?:0[1-9]|1[0-2])[-_.]20\d{2}\b|[-_.\s]*\b20\d{2}_R\d{1,2}\b)", RegexOptions.IgnoreCase)]
    private static partial Regex VariableTimestampOrReleaseRegex();

    [GeneratedRegex(@"(?:^|[-_.\s]+)(?:source|target|cible|attendu|obtenu|ref|reference|cand|candidat|old|new|avant|apres|apr[eè]s|prod|uat|recette|v\d+)(?=$|[-_.\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RoleAffixRegex();

    [GeneratedRegex(@"[A-Z0-9]{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex AlphaNumTokenRegex();

    public PdfFileService(IConfigurationService? configService = null)
    {
        _configService = configService;
    }

    public List<DocumentPair> MatchFiles(
        string sourcePathOrDir,
        string targetPathOrDir,
        string channel = "",
        string productName = "",
        string releaseName = "",
        string productOutputDir = "",
        PdfPairingOptions? pairingOptions = null)
    {
        var pairs = new List<DocumentPair>();

        sourcePathOrDir = (sourcePathOrDir ?? string.Empty).Trim().Trim('"');
        targetPathOrDir = (targetPathOrDir ?? string.Empty).Trim().Trim('"');

        bool isSourceFile = File.Exists(sourcePathOrDir) && sourcePathOrDir.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        bool isTargetFile = File.Exists(targetPathOrDir) && targetPathOrDir.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        // Mode comparaison directe 1-vs-1 de deux fichiers PDF unitaires (même avec des noms totalement différents)
        if (isSourceFile && isTargetFile)
        {
            string key = BuildPairDisplayKey(sourcePathOrDir, targetPathOrDir);
            pairs.Add(CreatePair(key, sourcePathOrDir, targetPathOrDir, channel, productName, releaseName, productOutputDir));
            return pairs;
        }

        var sourceFiles = isSourceFile
            ? new List<string> { sourcePathOrDir }
            : EnumeratePdfFilesSafe(sourcePathOrDir);

        var targetFiles = isTargetFile
            ? new List<string> { targetPathOrDir }
            : EnumeratePdfFilesSafe(targetPathOrDir);

        PdfPairingStrategy strategy = pairingOptions?.Strategy
            ?? _configService?.UserSettings.ManualComparePairingStrategy
            ?? PdfPairingStrategy.SmartAuto;

        // Si chaque dossier ne contient qu'un seul PDF (hors mode strict TIDES quand les identifiants diffèrent)
        if (sourceFiles.Count == 1 && targetFiles.Count == 1 && strategy != PdfPairingStrategy.ExactOrStemOnly)
        {
            string key = BuildPairDisplayKey(sourceFiles[0], targetFiles[0]);
            pairs.Add(CreatePair(key, sourceFiles[0], targetFiles[0], channel, productName, releaseName, productOutputDir));
            return pairs;
        }

        // Tri déterministe
        sourceFiles.Sort(StringComparer.OrdinalIgnoreCase);
        targetFiles.Sort(StringComparer.OrdinalIgnoreCase);

        // --- MODE ORDRE DU DOSSIER (1er avec 1er, 2e avec 2e...) ---
        if (strategy == PdfPairingStrategy.FolderOrder)
        {
            for (int i = 0; i < sourceFiles.Count; i++)
            {
                string sf = sourceFiles[i];
                if (i < targetFiles.Count)
                {
                    string tf = targetFiles[i];
                    string key = BuildPairDisplayKey(sf, tf);
                    pairs.Add(CreatePair(key, sf, tf, channel, productName, releaseName, productOutputDir));
                }
                else
                {
                    pairs.Add(CreatePair(NormalizeExactFileName(sf), sf, null, channel, productName, releaseName, productOutputDir));
                }
            }
            return pairs;
        }

        var unmatchedSources = new List<string>();
        var availableTargets = new HashSet<string>(targetFiles, StringComparer.OrdinalIgnoreCase);

        // --- ÉTAPE 1 : Appairage exact par nom de fichier normalisé ---
        var exactTargetQueues = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tf in targetFiles)
        {
            string exactKey = NormalizeExactFileName(tf);
            if (!exactTargetQueues.TryGetValue(exactKey, out var q))
            {
                q = new Queue<string>();
                exactTargetQueues[exactKey] = q;
            }
            q.Enqueue(tf);
        }

        foreach (var sf in sourceFiles)
        {
            string exactKey = NormalizeExactFileName(sf);
            if (exactTargetQueues.TryGetValue(exactKey, out var q) && q.Count > 0)
            {
                string matchedTarget = q.Dequeue();
                availableTargets.Remove(matchedTarget);
                pairs.Add(CreatePair(exactKey, sf, matchedTarget, channel, productName, releaseName, productOutputDir));
            }
            else
            {
                unmatchedSources.Add(sf);
            }
        }

        if (unmatchedSources.Count == 0)
        {
            return pairs;
        }

        // --- ÉTAPE 2 : Appairage par radical canonique (sans dates, releases ni mentions source/target/v1/v2) ---
        if (strategy != PdfPairingStrategy.CustomRegexOnly)
        {
            var stemTargetQueues = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tf in targetFiles)
            {
                if (!availableTargets.Contains(tf)) continue;
                string stemKey = ExtractCanonicalStem(tf, stripRoleWords: strategy == PdfPairingStrategy.SmartAuto);
                if (string.IsNullOrEmpty(stemKey)) continue;

                if (!stemTargetQueues.TryGetValue(stemKey, out var q))
                {
                    q = new Queue<string>();
                    stemTargetQueues[stemKey] = q;
                }
                q.Enqueue(tf);
            }

            var nextUnmatched = new List<string>();
            foreach (var sf in unmatchedSources)
            {
                string stemKey = ExtractCanonicalStem(sf, stripRoleWords: strategy == PdfPairingStrategy.SmartAuto);
                if (!string.IsNullOrEmpty(stemKey) &&
                    stemTargetQueues.TryGetValue(stemKey, out var q) &&
                    q.Count > 0)
                {
                    string matchedTarget = q.Dequeue();
                    availableTargets.Remove(matchedTarget);
                    pairs.Add(CreatePair(BuildPairDisplayKey(sf, matchedTarget), sf, matchedTarget, channel, productName, releaseName, productOutputDir));
                }
                else
                {
                    nextUnmatched.Add(sf);
                }
            }

            unmatchedSources = nextUnmatched;
            if (unmatchedSources.Count == 0)
            {
                return pairs;
            }
        }

        // --- ÉTAPE 3 : Appairage par Regex personnalisée ---
        Regex activeKeyRegex = GetActiveKeyRegex(pairingOptions?.CustomRegexPattern);
        var regexTargetQueues = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var tf in targetFiles)
        {
            if (!availableTargets.Contains(tf)) continue;
            string? rxKey = TryExtractRegexMatchKey(tf, activeKeyRegex);
            if (string.IsNullOrEmpty(rxKey)) continue;

            if (!regexTargetQueues.TryGetValue(rxKey, out var q))
            {
                q = new Queue<string>();
                regexTargetQueues[rxKey] = q;
            }
            q.Enqueue(tf);
        }

        var remainingForSmart = new List<string>();
        foreach (var sf in unmatchedSources)
        {
            string? rxKey = TryExtractRegexMatchKey(sf, activeKeyRegex);
            if (!string.IsNullOrEmpty(rxKey) &&
                regexTargetQueues.TryGetValue(rxKey, out var q) &&
                q.Count > 0)
            {
                string matchedTarget = q.Dequeue();
                availableTargets.Remove(matchedTarget);
                pairs.Add(CreatePair(BuildPairDisplayKey(sf, matchedTarget), sf, matchedTarget, channel, productName, releaseName, productOutputDir));
            }
            else
            {
                remainingForSmart.Add(sf);
            }
        }

        if (remainingForSmart.Count == 0)
        {
            return pairs;
        }

        // Si le mode n'est pas SmartAuto, les sources restantes sont marquées comme Manquant
        if (strategy != PdfPairingStrategy.SmartAuto || availableTargets.Count == 0)
        {
            // En cas particulier 1-contre-1 dans le même dossier (hors ExactOrStemOnly déjà traité)
            if (sourceFiles.Count == 1 && targetFiles.Count == 1 && remainingForSmart.Count == 1 && availableTargets.Count == 1 &&
                !HasConflictingNumericIds(remainingForSmart[0], availableTargets.First()))
            {
                string tf = availableTargets.First();
                pairs.Add(CreatePair(BuildPairDisplayKey(remainingForSmart[0], tf), remainingForSmart[0], tf, channel, productName, releaseName, productOutputDir));
                return pairs;
            }

            foreach (var sf in remainingForSmart)
            {
                pairs.Add(CreatePair(NormalizeExactFileName(sf), sf, null, channel, productName, releaseName, productOutputDir));
            }
            return pairs;
        }

        // --- ÉTAPE 4 (SmartAuto) : Identifiant commun dans le nom OU Similarité du contenu texte PDF ---
        // Fonctionne même si les noms des fichiers PDF sont totalement différents !
        MatchRemainingByIdentifierAndContent(
            remainingForSmart,
            availableTargets,
            pairs,
            channel,
            productName,
            releaseName,
            productOutputDir);

        return pairs;
    }

    private static void MatchRemainingByIdentifierAndContent(
        List<string> unmatchedSources,
        HashSet<string> availableTargets,
        List<DocumentPair> pairs,
        string channel,
        string productName,
        string releaseName,
        string productOutputDir)
    {
        // 4a. Chercher d'abord un identifiant alphanumérique contenant au moins 3 chiffres commun aux deux noms
        var targetIdMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tf in availableTargets)
        {
            foreach (var token in ExtractDistinctiveFilenameIds(tf))
            {
                if (!targetIdMap.TryGetValue(token, out var list))
                {
                    list = new List<string>();
                    targetIdMap[token] = list;
                }
                list.Add(tf);
            }
        }

        var stillUnmatched = new List<string>();
        foreach (var sf in unmatchedSources)
        {
            string? bestIdTarget = null;
            foreach (var token in ExtractDistinctiveFilenameIds(sf))
            {
                if (targetIdMap.TryGetValue(token, out var candidates))
                {
                    bestIdTarget = candidates.FirstOrDefault(availableTargets.Contains);
                    if (bestIdTarget != null) break;
                }
            }

            if (bestIdTarget != null)
            {
                availableTargets.Remove(bestIdTarget);
                pairs.Add(CreatePair(BuildPairDisplayKey(sf, bestIdTarget), sf, bestIdTarget, channel, productName, releaseName, productOutputDir));
            }
            else
            {
                stillUnmatched.Add(sf);
            }
        }

        if (stillUnmatched.Count == 0 || availableTargets.Count == 0)
        {
            foreach (var sf in stillUnmatched)
            {
                pairs.Add(CreatePair(NormalizeExactFileName(sf), sf, null, channel, productName, releaseName, productOutputDir));
            }
            return;
        }

        // 4b. Appairage par similarité du contenu texte PDF (pondérée en faveur des numéros et mots distinctifs)
        var targetPool = availableTargets.ToList();
        var sourceFingerprints = stillUnmatched.ToDictionary(sf => sf, ExtractPdfFingerprint, StringComparer.OrdinalIgnoreCase);
        var targetFingerprints = targetPool.ToDictionary(tf => tf, ExtractPdfFingerprint, StringComparer.OrdinalIgnoreCase);

        // Fréquence documentaire sur l'ensemble des candidats pour sur-pondérer les jetons discriminants
        var docFreq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fp in sourceFingerprints.Values.Concat(targetFingerprints.Values))
        {
            foreach (var tok in fp)
            {
                docFreq[tok] = docFreq.TryGetValue(tok, out int c) ? c + 1 : 1;
            }
        }

        int totalDocsInPool = Math.Max(2, stillUnmatched.Count + targetPool.Count);

        var scoredCandidates = new List<(string Source, string Target, double Score)>();
        foreach (var sf in stillUnmatched)
        {
            var sSet = sourceFingerprints[sf];
            foreach (var tf in targetPool)
            {
                var tSet = targetFingerprints[tf];
                double sim = ComputeWeightedJaccard(sSet, tSet, docFreq, totalDocsInPool);
                if (sim >= 0.18)
                {
                    scoredCandidates.Add((sf, tf, sim));
                }
            }
        }

        scoredCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var pairedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cand in scoredCandidates)
        {
            if (pairedSources.Contains(cand.Source) || !availableTargets.Contains(cand.Target))
                continue;

            pairedSources.Add(cand.Source);
            availableTargets.Remove(cand.Target);
            pairs.Add(CreatePair(
                BuildPairDisplayKey(cand.Source, cand.Target),
                cand.Source,
                cand.Target,
                channel,
                productName,
                releaseName,
                productOutputDir));
        }

        // Tout PDF Source restant sans correspondance suffisante est signalé comme Manquant
        foreach (var sf in stillUnmatched)
        {
            if (!pairedSources.Contains(sf))
            {
                pairs.Add(CreatePair(NormalizeExactFileName(sf), sf, null, channel, productName, releaseName, productOutputDir));
            }
        }
    }

    private static HashSet<string> ExtractPdfFingerprint(string pdfPath)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            lock (PdfPigSync.Gate)
            {
                using var doc = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });
                int maxPages = Math.Min(3, doc.NumberOfPages);
                for (int p = 1; p <= maxPages; p++)
                {
                    var page = doc.GetPage(p);
                    int wordCap = 0;
                    foreach (var w in page.GetWords())
                    {
                        if (string.IsNullOrWhiteSpace(w.Text) || w.Text.Length < 2) continue;
                        tokens.Add(w.Text.Trim().ToUpperInvariant());
                        if (++wordCap >= 900) break;
                    }
                }
            }
        }
        catch
        {
            // En cas de PDF illisible, empreinte vide -> sera traité normalement par l'orchestrateur ou marqué Manquant
        }
        return tokens;
    }

    private static double ComputeWeightedJaccard(
        HashSet<string> a,
        HashSet<string> b,
        Dictionary<string, int> docFreq,
        int totalPoolDocs)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        if (a.Count == 0 || b.Count == 0) return 0.0;

        double intersectionWeight = 0.0;
        double unionWeight = 0.0;

        static double GetTokenWeight(string tok, Dictionary<string, int> dfMap, int total)
        {
            bool hasDigit = false;
            for (int i = 0; i < tok.Length; i++)
            {
                if (char.IsDigit(tok[i])) { hasDigit = true; break; }
            }

            int df = dfMap.TryGetValue(tok, out int count) ? count : 1;
            double rarityBoost = df <= 2 ? 6.0 : (df < total / 2 ? 2.5 : 1.0);
            return hasDigit ? rarityBoost * 2.0 : rarityBoost;
        }

        foreach (var tok in a)
        {
            double w = GetTokenWeight(tok, docFreq, totalPoolDocs);
            unionWeight += w;
            if (b.Contains(tok))
            {
                intersectionWeight += w;
            }
        }

        foreach (var tok in b)
        {
            if (!a.Contains(tok))
            {
                unionWeight += GetTokenWeight(tok, docFreq, totalPoolDocs);
            }
        }

        return unionWeight > 0 ? intersectionWeight / unionWeight : 0.0;
    }

    private static List<string> ExtractDistinctiveFilenameIds(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        string withoutDates = VariableTimestampOrReleaseRegex().Replace(name, " ");
        var ids = new List<string>();

        foreach (Match m in AlphaNumTokenRegex().Matches(withoutDates))
        {
            string val = m.Value.ToUpperInvariant();
            int digitCount = val.Count(char.IsDigit);
            if (digitCount >= 4 && val.Length >= 5)
            {
                ids.Add(val);
            }
        }

        return ids;
    }

    private static bool HasConflictingNumericIds(string sourcePath, string targetPath)
    {
        var sIds = ExtractDistinctiveFilenameIds(sourcePath);
        var tIds = ExtractDistinctiveFilenameIds(targetPath);
        if (sIds.Count == 0 || tIds.Count == 0) return false;
        return !sIds.Intersect(tIds, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static string BuildPairDisplayKey(string sourcePath, string targetPath)
    {
        string sName = NormalizeDisplayKey(Path.GetFileNameWithoutExtension(sourcePath));
        string tName = NormalizeDisplayKey(Path.GetFileNameWithoutExtension(targetPath));

        if (string.Equals(sName, tName, StringComparison.OrdinalIgnoreCase))
        {
            return sName;
        }

        string Shorten(string s) => s.Length > 42 ? s.Substring(0, 39) + "…" : s;
        return $"{Shorten(sName)} ➔ {Shorten(tName)}";
    }

    private static List<string> EnumeratePdfFilesSafe(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return new List<string>();
        }

        try
        {
            return Directory.EnumerateFiles(directoryPath, "*.pdf", SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static DocumentPair CreatePair(
        string rawKey,
        string sourcePath,
        string? targetPath,
        string channel,
        string productName,
        string releaseName,
        string productOutputDir)
    {
        string resolvedProd = ProductCodeHelper.ResolveEffectiveProductCode(productName, channel, sourcePath, targetPath);
        string resolvedChannel = ProductCodeHelper.ResolveEffectiveChannel(channel, sourcePath, targetPath, resolvedProd);

        string cleanKey = NormalizeDisplayKey(rawKey);
        string finalKey = string.IsNullOrWhiteSpace(resolvedProd)
            ? cleanKey
            : $"[{resolvedProd}] {cleanKey}";

        return new DocumentPair(finalKey, sourcePath, targetPath, resolvedChannel, resolvedProd, releaseName, productOutputDir);
    }

    private static string NormalizeExactFileName(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        return MultiSpaceRegex().Replace(name, " ").Trim();
    }

    private static string ExtractCanonicalStem(string filePath, bool stripRoleWords = false)
    {
        string exact = NormalizeExactFileName(filePath);
        string stripped = VariableTimestampOrReleaseRegex().Replace(exact, "");
        if (stripRoleWords)
        {
            stripped = RoleAffixRegex().Replace(stripped, " ");
        }
        return MultiSpaceRegex().Replace(stripped, " ").Trim(' ', '_', '-', '.');
    }

    private static string NormalizeDisplayKey(string key)
    {
        return MultiSpaceRegex().Replace(key ?? string.Empty, " ").Trim();
    }

    private Regex GetActiveKeyRegex(string? overridePattern = null)
    {
        string? pattern = !string.IsNullOrWhiteSpace(overridePattern)
            ? overridePattern
            : _configService?.ActiveProfile.FileMatchingRegex;

        if (_configService != null && !string.IsNullOrWhiteSpace(pattern))
        {
            return _configService.TryGetCompiledRegex(pattern, ignoreCase: true) ?? DefaultKeyRegex();
        }

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            try
            {
                return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch
            {
                return DefaultKeyRegex();
            }
        }

        return DefaultKeyRegex();
    }

    private static string? TryExtractRegexMatchKey(string filePath, Regex keyRegex)
    {
        string fileName = Path.GetFileName(filePath);
        var match = keyRegex.Match(fileName);
        if (!match.Success)
        {
            match = keyRegex.Match(filePath);
        }

        if (match.Success)
        {
            return (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value).ToUpperInvariant();
        }
        return null;
    }
}
