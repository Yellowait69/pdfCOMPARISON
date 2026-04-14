using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PDFComparison.Models;

namespace PDFComparison.Services;

public partial class PdfFileService
{

    [GeneratedRegex(@"([A-Z]{2}_\d+_\d+)\.pdf$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyRegex();

    public List<DocumentPair> MatchFiles(string sourceDir, string targetDir)
    {

        var targetDict = Directory.EnumerateFiles(targetDir, "*.pdf")

            .Select(f => (Path: f, Match: KeyRegex().Match(f)))
            .Where(x => x.Match.Success)
            .ToDictionary(x => x.Match.Groups[1].Value.ToUpper(), x => x.Path);

        var pairs = new List<DocumentPair>();

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*.pdf"))
        {
            var match = KeyRegex().Match(sourceFile);
            if (match.Success)
            {
                string key = match.Groups[1].Value.ToUpper();


                targetDict.TryGetValue(key, out var targetPath);

                pairs.Add(new DocumentPair(key, sourceFile, targetPath));
            }
        }

        return pairs;
    }
}