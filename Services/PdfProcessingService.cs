using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using System;
using System.Collections.Concurrent;
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
using UglyToad.PdfPig.Writer;

namespace PDFComparison.Services;

// Local models to store data for the global synthesis report
public class DiffSummaryBlock
{
    public string ContextBefore { get; set; } = string.Empty;
    public string DiffContent { get; set; } = string.Empty;
    public string ContextAfter { get; set; } = string.Empty;
    public ChangeType Type { get; set; }
}

public class DocumentDiffSummary
{
    public string DocumentName { get; set; } = string.Empty;
    public List<DiffSummaryBlock> Blocks { get; set; } = new();
}

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

        // Concurrent bag to safely store all summaries from parallel threads
        var allSummaries = new ConcurrentBag<DocumentDiffSummary>();

        // Optimized asynchronous parallel processing
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
                    pair.ErrorMessage = "Identical (No differences)";
                    pair.DiffCount = 0;
                }
                else
                {
                    string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

                    // 2. Generate the individual full report AND extract data for the global synthesis
                    var result = await GenerateIndividualFullReportAsync(pair, sourceText, targetText, reportPath);

                    // Update model properties
                    pair.DiffCount = result.DiffCount;
                    pair.ReportPath = reportPath;

                    if (result.DiffCount > 0)
                    {
                        pair.Status = CompareStatus.Different;
                        pair.ErrorMessage = $"{result.DiffCount} difference(s) detected";

                        // Add to the global synthesis
                        allSummaries.Add(result.Summary);
                    }
                    else
                    {
                        pair.Status = CompareStatus.Identical;
                        pair.ErrorMessage = "False positives ignored";
                    }
                }

                // Save completion time at the end of processing the pair
                pair.CompletedTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                pair.Status = CompareStatus.Error;
                pair.ErrorMessage = $"Error: {ex.Message}";
                pair.DiffCount = -1; // -1 to put them at the bottom during sorting
            }
            finally
            {
                int currentCount = Interlocked.Increment(ref completed);
                progress.Report(currentCount);
            }
        });

        // NEW: Once all files are processed, generate the grand synthesis report
        if (!allSummaries.IsEmpty)
        {
            await GenerateGlobalSynthesisReportAsync(allSummaries.ToList(), outputDiffDir);
        }
    }

    private string ExtractTextFast(string pdfPath)
    {
        var sb = new StringBuilder();
        // Parsing Options optimized to ignore images and paths
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

    // NEW: Generates the full PDF content with highlights
    private async Task<(int DiffCount, DocumentDiffSummary Summary)> GenerateIndividualFullReportAsync(DocumentPair pair, string sourceText, string targetText, string reportPath)
    {
        return await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

            string cleanSource = NormalizePdfText(sourceText);
            string cleanTarget = NormalizePdfText(targetText);

            var diffBuilder = new SideBySideDiffBuilder(new DiffPlex.Differ());
            var diff = diffBuilder.BuildDiffModel(cleanSource, cleanTarget);

            var builder = new PdfDocumentBuilder();
            // Standard Portrait format
            PdfPageBuilder page = builder.AddPage(595, 842);

            var (font, fontBold) = LoadFonts(builder);

            double margin = 40;
            double yPosition = 842 - margin;
            int maxCharsPerLine = 90; // Width for Portrait

            string targetFileName = Path.GetFileName(pair.TargetPath!);

            // Individual document header
            page.SetTextAndFillColor(0, 0, 0);
            page.AddText($"DETAILED REPORT - Document: {targetFileName}", 14m, new PdfPoint(margin, yPosition), fontBold);
            yPosition -= 15;
            page.SetTextAndFillColor(100, 100, 100);
            page.AddText("Legend: Red = Added text | Yellow = Modified text", 10m, new PdfPoint(margin, yPosition), font);
            yPosition -= 30;

            int differencesCount = 0;
            var summary = new DocumentDiffSummary { DocumentName = targetFileName };

            // Traverse the target document to display it in full
            for (int i = 0; i < diff.NewText.Lines.Count; i++)
            {
                var newLine = diff.NewText.Lines[i];
                var oldLine = diff.OldText.Lines[i]; // Used to extract old text in the synthesis

                // Record data for the Global Synthesis
                if (newLine.Type == ChangeType.Inserted || newLine.Type == ChangeType.Modified || oldLine.Type == ChangeType.Deleted)
                {
                    differencesCount++;
                    var block = new DiffSummaryBlock
                    {
                        Type = newLine.Type != ChangeType.Unchanged ? newLine.Type : oldLine.Type,
                        // Find a non-imaginary line before and after for context
                        ContextBefore = GetValidContextLine(diff.NewText.Lines, i, -1),
                        ContextAfter = GetValidContextLine(diff.NewText.Lines, i, 1),
                    };

                    if (newLine.Type == ChangeType.Modified)
                        block.DiffContent = $"The initial text \"{oldLine.Text}\" was replaced by \"{newLine.Text}\".";
                    else if (newLine.Type == ChangeType.Inserted)
                        block.DiffContent = $"The following text was added: \"{newLine.Text}\".";
                    else if (oldLine.Type == ChangeType.Deleted)
                        block.DiffContent = $"The following text was deleted: \"{oldLine.Text}\".";

                    summary.Blocks.Add(block);
                }

                // Skip drawing imaginary lines in the new document view
                if (newLine.Type == ChangeType.Imaginary) continue;

                // Display in the individual PDF
                var wrappedText = WrapText(newLine.Text ?? "", maxCharsPerLine);
                foreach (var lineContent in wrappedText)
                {
                    if (yPosition < margin)
                    {
                        page = builder.AddPage(595, 842);
                        yPosition = 842 - margin;
                    }

                    // Apply the requested color code
                    if (newLine.Type == ChangeType.Inserted)
                        page.SetTextAndFillColor(220, 20, 20); // Red for addition
                    else if (newLine.Type == ChangeType.Modified)
                        page.SetTextAndFillColor(200, 150, 0); // Dark Yellow/Mustard for readability
                    else
                        page.SetTextAndFillColor(0, 0, 0); // Normal Black

                    page.AddText(lineContent, 11m, new PdfPoint(margin, yPosition), font);
                    yPosition -= 14;
                }
            }

            File.WriteAllBytes(reportPath, builder.Build());
            return (differencesCount, summary);
        });
    }

    // NEW: Generates the global narrative merged document
    private async Task GenerateGlobalSynthesisReportAsync(List<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        await Task.Run(() =>
        {
            string reportPath = Path.Combine(outputDiffDir, "Global_Synthesis_Report.pdf");

            // Ensure folder exists
            Directory.CreateDirectory(outputDiffDir);

            var builder = new PdfDocumentBuilder();
            PdfPageBuilder page = builder.AddPage(595, 842);
            var (font, fontBold) = LoadFonts(builder);

            double margin = 40;
            double yPosition = 842 - margin;
            int maxChars = 85;

            page.SetTextAndFillColor(0, 0, 0);
            page.AddText("GLOBAL DIFFERENCES SYNTHESIS", 16m, new PdfPoint(margin, yPosition), fontBold);
            yPosition -= 20;
            page.SetTextAndFillColor(100, 100, 100);
            page.AddText("This document presents a narrative summary of all detected modifications.", 10m, new PdfPoint(margin, yPosition), font);
            yPosition -= 35;

            // Group by document name alphabetically
            foreach (var doc in summaries.OrderBy(s => s.DocumentName))
            {
                if (yPosition < margin + 50) { page = builder.AddPage(595, 842); yPosition = 842 - margin; }

                page.SetTextAndFillColor(0, 50, 150); // Document title in Dark Blue
                page.AddText($"► File: {doc.DocumentName}", 13m, new PdfPoint(margin, yPosition), fontBold);
                yPosition -= 20;

                foreach (var block in doc.Blocks)
                {
                    if (yPosition < margin + 40) { page = builder.AddPage(595, 842); yPosition = 842 - margin; }

                    page.SetTextAndFillColor(0, 0, 0);

                    // Context Before
                    if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                    {
                        foreach (var l in WrapText($"... {block.ContextBefore}", maxChars))
                        {
                            page.AddText(l, 10m, new PdfPoint(margin + 15, yPosition), font);
                            yPosition -= 12;
                        }
                    }

                    // The reformulated error (Synthesis)
                    page.SetTextAndFillColor(200, 0, 0); // Red to draw attention to the modification
                    foreach (var l in WrapText($"➔ {block.DiffContent}", maxChars))
                    {
                        page.AddText(l, 11m, new PdfPoint(margin + 15, yPosition), fontBold);
                        yPosition -= 13;
                    }

                    // Context After
                    page.SetTextAndFillColor(0, 0, 0);
                    if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                    {
                        foreach (var l in WrapText($"{block.ContextAfter} ...", maxChars))
                        {
                            page.AddText(l, 10m, new PdfPoint(margin + 15, yPosition), font);
                            yPosition -= 12;
                        }
                    }
                    yPosition -= 15; // Space between two errors
                }
                yPosition -= 20; // Space between two documents
            }

            File.WriteAllBytes(reportPath, builder.Build());
        });
    }

    /// <summary>
    /// Helper method to safely get a valid context line ignoring "Imaginary" lines
    /// </summary>
    private string GetValidContextLine(List<DiffPiece> lines, int currentIndex, int direction)
    {
        int i = currentIndex + direction;
        while (i >= 0 && i < lines.Count)
        {
            if (lines[i].Type != ChangeType.Imaginary && !string.IsNullOrWhiteSpace(lines[i].Text))
            {
                return lines[i].Text;
            }
            i += direction;
        }
        return string.Empty;
    }

    private (AddedFont Font, AddedFont FontBold) LoadFonts(PdfDocumentBuilder builder)
    {
        string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string arialPath = Path.Combine(fontsFolder, "arial.ttf");
        string arialBoldPath = Path.Combine(fontsFolder, "arialbd.ttf");

        if (!File.Exists(arialPath) || !File.Exists(arialBoldPath))
           throw new FileNotFoundException("Required Arial fonts were not found on this system.");

        // Dans PdfPig.Writer, la méthode AddTrueTypeFont retourne un objet de type 'AddedFont'
        AddedFont regular = builder.AddTrueTypeFont(File.ReadAllBytes(arialPath));
        AddedFont bold = builder.AddTrueTypeFont(File.ReadAllBytes(arialBoldPath));

        return (regular, bold);
    }

    private string NormalizePdfText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. Flatten all text
        string flatText = Regex.Replace(input, @"\s+", " ");

        // 2. Smart splitting based on punctuation
        flatText = flatText.Replace(". ", ".\n").Replace("? ", "?\n").Replace("! ", "!\n").Replace(": ", ":\n");
        flatText = flatText.Replace("•", "\n• ").Replace(" o ", "\n o ");

        // 3. Cleanup
        var lines = flatText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim()).Where(l => l.Length > 0);

        return string.Join("\n", lines);
    }

    private List<string> WrapText(string text, int maxLength)
    {
        var lines = new List<string>();
        for (int i = 0; i < text.Length; i += maxLength)
            lines.Add(text.Substring(i, Math.Min(maxLength, text.Length - i)));
        return lines;
    }
}