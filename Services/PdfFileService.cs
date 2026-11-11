using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PDFComparison.Models;

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
        string productOutputDir = "")
    {
        var pairs = new List<DocumentPair>();

        bool isSourceFile = File.Exists(sourcePathOrDir) && sourcePathOrDir.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        bool isTargetFile = File.Exists(targetPathOrDir) && targetPathOrDir.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        // Mode comparaison directe 1-vs-1 de deux fichiers PDF unitaires (même avec des noms différents)
        if (isSourceFile && isTargetFile)
        {
            string sName = NormalizeDisplayKey(Path.GetFileNameWithoutExtension(sourcePathOrDir));
            string tName = NormalizeDisplayKey(Path.GetFileNameWithoutExtension(targetPathOrDir));
            string key = string.Equals(sName, tName, StringComparison.OrdinalIgnoreCase)
                ? sName
                : $"{sName}_VS_{tName}";

            pairs.Add(CreatePair(key, sourcePathOrDir, targetPathOrDir, channel, productName, releaseName, productOutputDir));
            return pairs;
        }

        var sourceFiles = isSourceFile
            ? new List<string> { sourcePathOrDir }
            : EnumeratePdfFilesSafe(sourcePathOrDir);

        var targetFiles = isTargetFile
            ? new List<string> { targetPathOrDir }
            : EnumeratePdfFilesSafe(targetPathOrDir);

        // Si chaque dossier ne contient qu'un seul PDF mais avec des noms différents, appairage direct 1-vs-1
        if (sourceFiles.Count == 1 && targetFiles.Count == 1)
        {
            string sName = NormalizeDisplayKey(Path.GetFileNameWithoutExtension(sourceFiles[0]));
            string tName = NormalizeDisplayKey(Path.GetFileNameWithoutExtension(targetFiles[0]));
            string key = string.Equals(sName, tName, StringComparison.OrdinalIgnoreCase)
                ? sName
                : $"{sName}_VS_{tName}";

            pairs.Add(CreatePair(key, sourceFiles[0], targetFiles[0], channel, productName, releaseName, productOutputDir));
            return pairs;
        }

        // Tri déterministe (ordre naturel par nom de fichier)
        sourceFiles.Sort(StringComparer.OrdinalIgnoreCase);
        targetFiles.Sort(StringComparer.OrdinalIgnoreCase);

        var unmatchedSources = new List<string>();
        var availableTargets = new HashSet<string>(targetFiles, StringComparer.OrdinalIgnoreCase);

        // --- ÉTAPE 1 : Appairage exact par nom de fichier normalisé (insensible aux espaces multiples et à la casse) ---
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
                pairs.Add(CreatePair( exactKey, sf, matchedTarget, channel, productName, releaseName, productOutputDir));
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

        // --- ÉTAPE 2 : Appairage TIDES par radical canonique (sans horodatages / dates de génération variables) ---
        var stemTargetQueues = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tf in targetFiles)
        {
            if (!availableTargets.Contains(tf)) continue;
            string stemKey = ExtractCanonicalStem(tf);
            if (string.IsNullOrEmpty(stemKey)) continue;

            if (!stemTargetQueues.TryGetValue(stemKey, out var q))
            {
                q = new Queue<string>();
                stemTargetQueues[stemKey] = q;
            }
            q.Enqueue(tf);
        }

        var stillUnmatchedSources = new List<string>();
        foreach (var sf in unmatchedSources)
        {
            string stemKey = ExtractCanonicalStem(sf);
            if (!string.IsNullOrEmpty(stemKey) &&
                stemTargetQueues.TryGetValue(stemKey, out var q) &&
                q.Count > 0)
            {
                string matchedTarget = q.Dequeue();
                availableTargets.Remove(matchedTarget);
                string displayKey = NormalizeExactFileName(sf);
                pairs.Add(CreatePair(displayKey, sf, matchedTarget, channel, productName, releaseName, productOutputDir));
            }
            else
            {
                stillUnmatchedSources.Add(sf);
            }
        }

        if (stillUnmatchedSources.Count == 0)
        {
            return pairs;
        }

        // --- ÉTAPE 3 : Appairage par Regex personnalisée (sans collision 1-pour-1 via Queue) ---
        Regex activeKeyRegex = GetActiveKeyRegex();
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

        foreach (var sf in stillUnmatchedSources)
        {
            string? rxKey = TryExtractRegexMatchKey(sf, activeKeyRegex);
            string displayKey = !string.IsNullOrEmpty(rxKey) ? rxKey : NormalizeExactFileName(sf);

            if (!string.IsNullOrEmpty(rxKey) &&
                regexTargetQueues.TryGetValue(rxKey, out var q) &&
                q.Count > 0)
            {
                string matchedTarget = q.Dequeue();
                availableTargets.Remove(matchedTarget);
                pairs.Add(CreatePair(displayKey, sf, matchedTarget, channel, productName, releaseName, productOutputDir));
            }
            else
            {
                pairs.Add(CreatePair(displayKey, sf, null, channel, productName, releaseName, productOutputDir));
            }
        }

        return pairs;
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
        string cleanKey = NormalizeDisplayKey(rawKey);
        string finalKey = string.IsNullOrWhiteSpace(productName)
            ? cleanKey
            : $"[{productName}] {cleanKey}";

        return new DocumentPair(finalKey, sourcePath, targetPath, channel, productName, releaseName, productOutputDir);
    }

    private static string NormalizeExactFileName(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        return MultiSpaceRegex().Replace(name, " ").Trim();
    }

    private static string ExtractCanonicalStem(string filePath)
    {
        string exact = NormalizeExactFileName(filePath);
        string stripped = VariableTimestampOrReleaseRegex().Replace(exact, "");
        return MultiSpaceRegex().Replace(stripped, " ").Trim();
    }

    private static string NormalizeDisplayKey(string key)
    {
        return MultiSpaceRegex().Replace(key ?? string.Empty, " ").Trim();
    }

    private Regex GetActiveKeyRegex()
    {
        string? pattern = _configService?.ActiveProfile.FileMatchingRegex;
        if (_configService != null && !string.IsNullOrWhiteSpace(pattern))
        {
            return _configService.TryGetCompiledRegex(pattern, ignoreCase: true) ?? DefaultKeyRegex();
        }
        return DefaultKeyRegex();
    }

    private static string? TryExtractRegexMatchKey(string filePath, Regex keyRegex)
    {
        var match = keyRegex.Match(filePath);
        if (match.Success)
        {
            return (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value).ToUpperInvariant();
        }
        return null;
    }
}
