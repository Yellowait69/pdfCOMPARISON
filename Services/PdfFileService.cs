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

    public PdfFileService(IConfigurationService? configService = null)
    {
        _configService = configService;
    }

    public List<DocumentPair> MatchFiles(string sourcePathOrDir, string targetPathOrDir)
    {
        var pairs = new List<DocumentPair>();

        bool isSourceFile = File.Exists(sourcePathOrDir) && sourcePathOrDir.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        bool isTargetFile = File.Exists(targetPathOrDir) && targetPathOrDir.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        // Mode comparaison directe 1-vs-1 de deux fichiers PDF unitaires (même avec des noms différents)
        if (isSourceFile && isTargetFile)
        {
            string sName = Path.GetFileNameWithoutExtension(sourcePathOrDir);
            string tName = Path.GetFileNameWithoutExtension(targetPathOrDir);
            string key = string.Equals(sName, tName, StringComparison.OrdinalIgnoreCase)
                ? sName.ToUpperInvariant()
                : $"{sName}_VS_{tName}".ToUpperInvariant();

            pairs.Add(new DocumentPair(key, sourcePathOrDir, targetPathOrDir));
            return pairs;
        }

        var sourceFiles = isSourceFile
            ? new List<string> { sourcePathOrDir }
            : (Directory.Exists(sourcePathOrDir) ? Directory.EnumerateFiles(sourcePathOrDir, "*.pdf").ToList() : new List<string>());

        var targetFiles = isTargetFile
            ? new List<string> { targetPathOrDir }
            : (Directory.Exists(targetPathOrDir) ? Directory.EnumerateFiles(targetPathOrDir, "*.pdf").ToList() : new List<string>());

        // Si chaque dossier ne contient qu'un seul PDF mais avec des noms différents, appairage direct 1-vs-1
        if (sourceFiles.Count == 1 && targetFiles.Count == 1)
        {
            string sName = Path.GetFileNameWithoutExtension(sourceFiles[0]);
            string tName = Path.GetFileNameWithoutExtension(targetFiles[0]);
            string key = string.Equals(sName, tName, StringComparison.OrdinalIgnoreCase)
                ? sName.ToUpperInvariant()
                : $"{sName}_VS_{tName}".ToUpperInvariant();

            pairs.Add(new DocumentPair(key, sourceFiles[0], targetFiles[0]));
            return pairs;
        }

        var targetDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Regex activeKeyRegex = GetActiveKeyRegex();

        foreach (var targetFile in targetFiles)
        {
            string key = ExtractMatchKey(targetFile, activeKeyRegex);
            if (!string.IsNullOrEmpty(key))
            {
                targetDict.TryAdd(key, targetFile);
            }
        }

        foreach (var sourceFile in sourceFiles)
        {
            string key = ExtractMatchKey(sourceFile, activeKeyRegex);
            if (!string.IsNullOrEmpty(key))
            {
                targetDict.TryGetValue(key, out var targetPath);
                pairs.Add(new DocumentPair(key, sourceFile, targetPath));
            }
        }

        return pairs;
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

    private static string ExtractMatchKey(string filePath, Regex keyRegex)
    {
        var match = keyRegex.Match(filePath);
        if (match.Success)
        {
            return (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value).ToUpperInvariant();
        }

        return Path.GetFileNameWithoutExtension(filePath).Trim().ToUpperInvariant();
    }
}
