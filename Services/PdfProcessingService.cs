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
using UglyToad.PdfPig.Writer;

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
                    pair.ErrorMessage = "Identical (No differences)";
                    pair.DiffCount = 0;
                }
                else
                {
                    // Definition of the report path
                    string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

                    // 2. Generation of the colored difference report (SIDE-BY-SIDE)
                    int diffCount = await GenerateColoredDiffReportAsync(pair, sourceText, targetText, reportPath);

                    // Update model properties
                    pair.DiffCount = diffCount;
                    pair.ReportPath = reportPath; // Automatically enables the "Open PDF" button

                    if (diffCount > 0)
                    {
                        pair.Status = CompareStatus.Different;
                        pair.ErrorMessage = $"{diffCount} difference(s) detected";
                    }
                    else
                    {
                        // Case where files are binary different but textually identical
                        pair.Status = CompareStatus.Identical;
                        pair.ErrorMessage = "False positives ignored (spaces/line breaks)";
                    }
                }

                // NEW: Save completion time at the end of processing the pair
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

    // Generation of the Side-by-Side PDF report
    private async Task<int> GenerateColoredDiffReportAsync(DocumentPair pair, string sourceText, string targetText, string reportPath)
    {
        return await Task.Run(() =>
        {
            // Ensure the folder exists
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

            // NORMALIZATION: Clean texts to avoid false positives
            string cleanSource = NormalizePdfText(sourceText);
            string cleanTarget = NormalizePdfText(targetText);

            // MAJOR CHANGE: Using SideBySideDiffBuilder to have 2 synchronized columns
            var diffBuilder = new SideBySideDiffBuilder(new DiffPlex.Differ());
            var diff = diffBuilder.BuildDiffModel(cleanSource, cleanTarget);

            var builder = new PdfDocumentBuilder();

            // Landscape format: Width 842, Height 595
            PdfPageBuilder page = builder.AddPage(842, 595);

            // ==========================================
            // FIX: Using system Arial fonts to support all Unicode characters
            // ==========================================
            string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string arialPath = Path.Combine(fontsFolder, "arial.ttf");
            string arialBoldPath = Path.Combine(fontsFolder, "arialbd.ttf");

            // Verify if fonts exist
            if (!File.Exists(arialPath) || !File.Exists(arialBoldPath))
            {
                throw new FileNotFoundException("Required Arial fonts were not found on this system.");
            }

            var font = builder.AddTrueTypeFont(File.ReadAllBytes(arialPath));
            var fontBold = builder.AddTrueTypeFont(File.ReadAllBytes(arialBoldPath));
            // ==========================================

            double margin = 30;
            double yPosition = 595 - margin;
            int maxCharsPerLine = 70; // About 70 chars max per half-page

            // File names
            string sourceFileName = Path.GetFileName(pair.SourcePath);
            string targetFileName = Path.GetFileName(pair.TargetPath!);

            // --- REPORT HEADER ---
            page.SetTextAndFillColor(0, 0, 0); // Black
            page.AddText($"DIFFERENCE REPORT (SIDE-BY-SIDE) - Key: {pair.MatchKey}", 14m, new PdfPoint(margin, yPosition), fontBold);
            yPosition -= 25;

            // Left Column Title (Source)
            page.SetTextAndFillColor(200, 0, 0); // Red
            page.AddText($"SOURCE (Red): {sourceFileName}", 12m, new PdfPoint(20, yPosition), fontBold);

            // Right Column Title (Target)
            page.SetTextAndFillColor(0, 128, 0); // Green
            page.AddText($"TARGET (Green): {targetFileName}", 12m, new PdfPoint(425, yPosition), fontBold);

            yPosition -= 25; // Space before starting the text

            int differencesCount = 0; // Real differences counter
            var linesToPrint = new HashSet<int>();
            int contextLines = 1; // 1 sentence of context before and 1 after

            // --- 1st PASS: Identify differences and target context lines ---
            for (int i = 0; i < diff.OldText.Lines.Count; i++)
            {
                var leftLine = diff.OldText.Lines[i];
                var rightLine = diff.NewText.Lines[i];

                // Check if this line contains a difference
                bool isDiff = (leftLine.Type != ChangeType.Unchanged && leftLine.Type != ChangeType.Imaginary) ||
                              (rightLine.Type != ChangeType.Unchanged && rightLine.Type != ChangeType.Imaginary);

                if (isDiff)
                {
                    differencesCount++;

                    // Add the current line and context lines (before/after)
                    for (int c = -contextLines; c <= contextLines; c++)
                    {
                        int indexToAdd = i + c;
                        // Ensure we don't go out of array bounds
                        if (indexToAdd >= 0 && indexToAdd < diff.OldText.Lines.Count)
                        {
                            linesToPrint.Add(indexToAdd);
                        }
                    }
                }
            }

            int lastPrintedIndex = -2; // Variable to track skipped lines

            // --- 2nd PASS: DRAW REPORT BODY (Only relevant text) ---
            for (int i = 0; i < diff.OldText.Lines.Count; i++)
            {
                // Ignore lines that are neither differences nor context
                if (!linesToPrint.Contains(i)) continue;

                // If lines were skipped, display a visual separator "[...]"
                if (lastPrintedIndex != -2 && i > lastPrintedIndex + 1)
                {
                    if (yPosition < margin + 15)
                    {
                        page = builder.AddPage(842, 595); // New Landscape page
                        yPosition = 595 - margin;
                    }

                    page.SetTextAndFillColor(150, 150, 150); // Light Gray
                    page.AddText(" [...]", 10m, new PdfPoint(20, yPosition), font);
                    page.AddText(" [...]", 10m, new PdfPoint(425, yPosition), font);
                    yPosition -= 17; // Separator line spacing
                }

                lastPrintedIndex = i;

                var leftLine = diff.OldText.Lines[i];
                var rightLine = diff.NewText.Lines[i];

                // Wrap text so it doesn't exceed its half-page
                var leftWrapped = WrapText(leftLine.Text ?? "", maxCharsPerLine);
                var rightWrapped = WrapText(rightLine.Text ?? "", maxCharsPerLine);

                // Take the maximum number of lines between left and right to stay perfectly aligned
                int maxLines = Math.Max(1, Math.Max(leftWrapped.Count, rightWrapped.Count));

                for (int j = 0; j < maxLines; j++)
                {
                    // Synchronized page change handling for both columns
                    if (yPosition < margin)
                    {
                        page = builder.AddPage(842, 595); // New Landscape page
                        yPosition = 595 - margin;
                    }

                    string lText = j < leftWrapped.Count ? leftWrapped[j] : "";
                    string rText = j < rightWrapped.Count ? rightWrapped[j] : "";

                    // DRAW LEFT COLUMN (Source)
                    if (!string.IsNullOrEmpty(lText))
                    {
                        SetColorForType(page, leftLine.Type);
                        page.AddText(lText, 10m, new PdfPoint(20, yPosition), font);
                    }

                    // DRAW RIGHT COLUMN (Target)
                    if (!string.IsNullOrEmpty(rText))
                    {
                        SetColorForType(page, rightLine.Type);
                        page.AddText(rText, 10m, new PdfPoint(425, yPosition), font);
                    }

                    yPosition -= 13; // Line spacing
                }

                yPosition -= 4; // Small space between each line/block of text
            }

            File.WriteAllBytes(reportPath, builder.Build());

            return differencesCount; // Return the final counter
        });
    }

    /// <summary>
    /// Applies the correct color based on the type of modification
    /// </summary>
    private void SetColorForType(PdfPageBuilder page, ChangeType type)
    {
        switch (type)
        {
            case ChangeType.Inserted:
                page.SetTextAndFillColor(0, 128, 0); // Green
                break;
            case ChangeType.Deleted:
                page.SetTextAndFillColor(200, 0, 0); // Red
                break;
            case ChangeType.Modified:
                page.SetTextAndFillColor(0, 0, 200); // Blue
                break;
            case ChangeType.Unchanged:
                page.SetTextAndFillColor(90, 90, 90); // Dark gray for unchanged text
                break;
            default:
                page.SetTextAndFillColor(255, 255, 255); // Imaginary (invisible)
                break;
        }
    }

    /// <summary>
    /// Function to SMARTLY normalize the PDF text.
    /// Prevents adding a single word from shifting the entire document.
    /// Rebuilds text sentence by sentence for a granular diff.
    /// </summary>
    private string NormalizePdfText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. Flatten all text
        string flatText = Regex.Replace(input, @"\s+", " ");

        // 2. Smart splitting based on punctuation
        flatText = flatText.Replace(". ", ".\n");
        flatText = flatText.Replace("? ", "?\n");
        flatText = flatText.Replace("! ", "!\n");
        flatText = flatText.Replace(": ", ":\n");

        // Smart handling of bullet points
        flatText = flatText.Replace("•", "\n• ");
        flatText = flatText.Replace(" o ", "\n o ");

        // 3. Cleanup
        var lines = flatText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0);

        return string.Join("\n", lines);
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