using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer; // Standard14Fonts n'est plus nécessaire ici

namespace PDFComparison.Services;

public class PdfProcessingService
{
    // Precompiled Regex for performance: captures the digits before ".pdf"
    private static readonly Regex KeyRegex = new(@"(\d+)\.pdf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<DocumentPair> MatchFiles(string sourceDir, string targetDir)
    {
        var sourceFiles = Directory.GetFiles(sourceDir, "*.pdf");
        var targetFiles = Directory.GetFiles(targetDir, "*.pdf");

        // Dictionary for O(1) access to target files
        var targetDict = targetFiles
            .Select(f => new { Path = f, Match = KeyRegex.Match(f) })
            .Where(x => x.Match.Success)
            .ToDictionary(x => x.Match.Groups[1].Value, x => x.Path);

        var pairs = new List<DocumentPair>();

        foreach (var sourceFile in sourceFiles)
        {
            var match = KeyRegex.Match(sourceFile);
            if (match.Success)
            {
                string key = match.Groups[1].Value;
                targetDict.TryGetValue(key, out string? targetPath);
                pairs.Add(new DocumentPair(key, sourceFile, targetPath));
            }
        }

        return pairs;
    }

    public async Task ProcessPairsAsync(IEnumerable<DocumentPair> validPairs, string outputDiffDir, IProgress<int> progress)
    {
        int completed = 0;

        // Optimized asynchronous parallel processing (I/O bound and CPU bound)
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        await Parallel.ForEachAsync(validPairs, parallelOptions, async (pair, ct) =>
        {
            try
            {
                var sourceText = ExtractTextFast(pair.SourcePath);
                var targetText = ExtractTextFast(pair.TargetPath!);

                // 1. Fast O(N) comparison: binary check of string hashes
                if (string.Equals(sourceText, targetText, StringComparison.Ordinal))
                {
                    pair.Status = CompareStatus.Identical;
                }
                else
                {
                    pair.Status = CompareStatus.Different;
                    // 2. Generation of the colored difference report
                    await GenerateColoredDiffReportAsync(pair, sourceText, targetText, outputDiffDir);
                }
            }
            catch (Exception ex)
            {
                pair.Status = CompareStatus.Error;
                pair.ErrorMessage = ex.Message;
            }
            finally
            {
                int currentCount = Interlocked.Increment(ref completed);
                progress.Report(currentCount);
            }
        });
    }

    private string ExtractTextFast(string pdfPath)
    {
        var sb = new StringBuilder();
        // Parsing Options optimized to ignore images and paths (major speed gain)
        var options = new ParsingOptions { ClipPaths = false };

        using (var document = PdfDocument.Open(pdfPath, options))
        {
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
            }
        }
        return sb.ToString();
    }

    private async Task GenerateColoredDiffReportAsync(DocumentPair pair, string sourceText, string targetText, string outputDir)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(outputDir);
            string reportPath = Path.Combine(outputDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

            // Using DiffPlex to generate line-by-line diff
            var diffBuilder = new InlineDiffBuilder(new DiffPlex.Differ());
            var diff = diffBuilder.BuildDiffModel(sourceText, targetText);

            var builder = new PdfDocumentBuilder();
            PdfPageBuilder page = builder.AddPage(PageSize.A4);

            // ==========================================
            // FIX: Using system Arial fonts to support all Unicode characters (accents, symbols)
            // ==========================================
            string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string arialPath = Path.Combine(fontsFolder, "arial.ttf");
            string arialBoldPath = Path.Combine(fontsFolder, "arialbd.ttf");

            // Verify if fonts exist, otherwise it might throw on missing files
            if (!File.Exists(arialPath) || !File.Exists(arialBoldPath))
            {
                throw new FileNotFoundException("Required Arial fonts were not found on this system.");
            }

            var font = builder.AddTrueTypeFont(File.ReadAllBytes(arialPath));
            var fontBold = builder.AddTrueTypeFont(File.ReadAllBytes(arialBoldPath));
            // ==========================================

            double margin = 40;
            double yPosition = page.PageSize.Top - margin;
            double xPosition = margin;
            double lineHeight = 12;
            int maxCharsPerLine = 95; // Limit before automatic line break

            // PDF Header
            page.SetTextAndFillColor(0, 0, 0); // Black
            page.AddText($"DIFFERENCE REPORT - Document Key: {pair.MatchKey}", 14, new PdfPoint(xPosition, yPosition), fontBold);
            yPosition -= 30;

            foreach (var line in diff.Lines)
            {
                // Ignore unchanged lines to only show differences
                if (line.Type == ChangeType.Unchanged) continue;

                string prefix = line.Type switch
                {
                    ChangeType.Inserted => "[+] ",
                    ChangeType.Deleted => "[-] ",
                    ChangeType.Modified => "[*] ",
                    _ => ""
                };

                // Apply colors according to modification type
                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        page.SetTextAndFillColor(0, 128, 0); // Dark Green
                        break;
                    case ChangeType.Deleted:
                        page.SetTextAndFillColor(200, 0, 0); // Red
                        break;
                    case ChangeType.Modified:
                        page.SetTextAndFillColor(0, 0, 200); // Blue
                        break;
                }

                // Line cleanup
                string cleanText = (prefix + line.Text).Replace("\r", "").Replace("\n", "").Replace("\t", "    ");

                // Word Wrap Algorithm (Split into multiple lines instead of truncating)
                var wrappedLines = WrapText(cleanText, maxCharsPerLine);

                foreach (var wrappedLine in wrappedLines)
                {
                    // Page change management
                    if (yPosition < margin)
                    {
                        page = builder.AddPage(PageSize.A4);
                        yPosition = page.PageSize.Top - margin;
                    }

                    page.AddText(wrappedLine, 10, new PdfPoint(xPosition, yPosition), font);
                    yPosition -= lineHeight;
                }

                // Small extra spacing between different modified blocks
                yPosition -= 4;
            }

            File.WriteAllBytes(reportPath, builder.Build());
        });
    }

    /// <summary>
    /// Utility function to split long text into multiple lines (Word Wrap)
    /// </summary>
    private List<string> WrapText(string text, int maxLength)
    {
        var lines = new List<string>();
        for (int i = 0; i < text.Length; i += maxLength)
        {
            lines.Add(text.Substring(i, Math.Min(maxLength, text.Length - i)));
        }
        return lines;
    }
}