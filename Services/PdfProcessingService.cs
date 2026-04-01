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
    private static readonly Regex KeyRegex = new(@"(\d+)\.pdf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<DocumentPair> MatchFiles(string sourceDir, string targetDir)
    {
        var sourceFiles = Directory.GetFiles(sourceDir, "*.pdf");
        var targetFiles = Directory.GetFiles(targetDir, "*.pdf");

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
        var allSummaries = new ConcurrentBag<DocumentDiffSummary>();

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        await Parallel.ForEachAsync(validPairs, parallelOptions, async (pair, ct) =>
        {
            try
            {
                var sourceText = ExtractTextFast(pair.SourcePath);
                var targetText = ExtractTextFast(pair.TargetPath!);

                if (string.Equals(sourceText, targetText, StringComparison.Ordinal))
                {
                    pair.Status = CompareStatus.Identical;
                    pair.ErrorMessage = "Identical (No differences)";
                    pair.DiffCount = 0;
                }
                else
                {
                    string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

                    var result = await GenerateIndividualFullReportAsync(pair, sourceText, targetText, reportPath);

                    pair.DiffCount = result.DiffCount;
                    pair.ReportPath = reportPath;

                    if (result.DiffCount > 0)
                    {
                        pair.Status = CompareStatus.Different;
                        pair.ErrorMessage = $"{result.DiffCount} difference(s) detected";
                        allSummaries.Add(result.Summary);
                    }
                    else
                    {
                        pair.Status = CompareStatus.Identical;
                        pair.ErrorMessage = "False positives ignored";
                    }
                }
                pair.CompletedTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                pair.Status = CompareStatus.Error;
                pair.ErrorMessage = $"Error: {ex.Message}";
                pair.DiffCount = -1;
            }
            finally
            {
                int currentCount = Interlocked.Increment(ref completed);
                progress.Report(currentCount);
            }
        });

        if (!allSummaries.IsEmpty)
        {
            await GenerateGlobalSynthesisReportAsync(allSummaries.ToList(), outputDiffDir);
        }
    }

    private string ExtractTextFast(string pdfPath)
    {
        var sb = new StringBuilder();
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

    // NOUVEAU: Génère le rapport en PAYSAGE (Source à gauche, Target à droite) avec les types Decimal (m)
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
            // Format A4 Paysage (Landscape): 842 x 595
            PdfPageBuilder page = builder.AddPage(842, 595);
            var (font, fontBold) = LoadFonts(builder);

            // CORRECTION: Utilisation de decimal ("m") à la place de double
            decimal margin = 30m;
            decimal colWidth = 370m;
            decimal leftColX = margin;
            decimal rightColX = 842m / 2m + 10m;
            decimal yPosition = 595m - margin;

            string targetFileName = Path.GetFileName(pair.TargetPath!);

            // En-tête
            page.SetTextAndFillColor(0, 0, 0);
            page.AddText($"RAPPORT DÉTAILLÉ - Document: {targetFileName} (Format Paysage)", 14m, new PdfPoint(margin, yPosition), fontBold);
            yPosition -= 15m;
            page.SetTextAndFillColor(100, 100, 100);
            page.AddText("Légende : Fond Rouge = Ajout/Suppression | Fond Jaune = Modification exacte", 10m, new PdfPoint(margin, yPosition), font);
            yPosition -= 25m;

            // Titres des colonnes
            page.SetTextAndFillColor(0, 50, 150);
            page.AddText("DOCUMENT SOURCE (Original)", 12m, new PdfPoint(leftColX, yPosition), fontBold);
            page.AddText("DOCUMENT CIBLE (Modifié)", 12m, new PdfPoint(rightColX, yPosition), fontBold);
            yPosition -= 20m;

            int differencesCount = 0;
            var summary = new DocumentDiffSummary { DocumentName = targetFileName };

            for (int i = 0; i < diff.NewText.Lines.Count; i++)
            {
                var newLine = diff.NewText.Lines[i];
                var oldLine = diff.OldText.Lines[i];

                if (newLine.Type == ChangeType.Inserted || newLine.Type == ChangeType.Modified || oldLine.Type == ChangeType.Deleted)
                {
                    differencesCount++;
                    var block = new DiffSummaryBlock
                    {
                        Type = newLine.Type != ChangeType.Unchanged ? newLine.Type : oldLine.Type,
                        ContextBefore = GetValidContextLine(diff.NewText.Lines, i, -1),
                        ContextAfter = GetValidContextLine(diff.NewText.Lines, i, 1),
                    };

                    if (newLine.Type == ChangeType.Modified)
                        block.DiffContent = $"Le texte initial \"{oldLine.Text}\" a été remplacé par \"{newLine.Text}\".";
                    else if (newLine.Type == ChangeType.Inserted)
                        block.DiffContent = $"Ajout : \"{newLine.Text}\".";
                    else if (oldLine.Type == ChangeType.Deleted)
                        block.DiffContent = $"Suppression : \"{oldLine.Text}\".";

                    summary.Blocks.Add(block);
                }

                if (newLine.Type == ChangeType.Imaginary && oldLine.Type == ChangeType.Imaginary) continue;

                // Pagination
                if (yPosition < margin)
                {
                    page = builder.AddPage(842, 595);
                    yPosition = 595m - margin;
                    page.SetTextAndFillColor(0, 50, 150);
                    page.AddText("DOCUMENT SOURCE (Suite)", 10m, new PdfPoint(leftColX, yPosition), fontBold);
                    page.AddText("DOCUMENT CIBLE (Suite)", 10m, new PdfPoint(rightColX, yPosition), fontBold);
                    yPosition -= 20m;
                }

                // Affichage côte à côte
                if (newLine.Type == ChangeType.Modified || oldLine.Type == ChangeType.Modified)
                {
                    // MODIFICATION : On compare mot par mot pour surligner en jaune
                    DrawLineWithWordDiff(page, oldLine.Text, newLine.Text, leftColX, rightColX, yPosition, font, colWidth);
                }
                else
                {
                    // LIGNE INCHANGÉE, AJOUTÉE OU SUPPRIMÉE (Rouge ou Noir)
                    if (oldLine.Type != ChangeType.Imaginary)
                    {
                        bool isDeleted = oldLine.Type == ChangeType.Deleted;
                        DrawSimpleText(page, oldLine.Text ?? "", leftColX, yPosition, font, colWidth, isDeleted ? "Red" : "Black");
                    }
                    if (newLine.Type != ChangeType.Imaginary)
                    {
                        bool isInserted = newLine.Type == ChangeType.Inserted;
                        DrawSimpleText(page, newLine.Text ?? "", rightColX, yPosition, font, colWidth, isInserted ? "Red" : "Black");
                    }
                }

                yPosition -= 14m; // Espacement de ligne
            }

            File.WriteAllBytes(reportPath, builder.Build());
            return (differencesCount, summary);
        });
    }

    // CORRECTION: types decimal
    private void DrawLineWithWordDiff(PdfPageBuilder page, string oldText, string newText, decimal leftX, decimal rightX, decimal y, PdfDocumentBuilder.AddedFont font, decimal maxWidth)
    {
        oldText ??= "";
        newText ??= "";

        // Comparaison mot par mot
        var wordDiffBuilder = new SideBySideDiffBuilder(new DiffPlex.Differ());
        var wordDiff = wordDiffBuilder.BuildDiffModel(oldText.Replace(" ", "\n"), newText.Replace(" ", "\n"));

        decimal currentLeftX = leftX;
        decimal currentRightX = rightX;

        for (int i = 0; i < wordDiff.OldText.Lines.Count; i++)
        {
            var oldWord = wordDiff.OldText.Lines[i];
            var newWord = wordDiff.NewText.Lines[i];

            // Rendu à Gauche (Source)
            if (oldWord.Type != ChangeType.Imaginary && oldWord.Text != null)
            {
                if (currentLeftX < leftX + maxWidth - 20m)
                {
                    string word = oldWord.Text + " ";
                    decimal wWidth = word.Length * 5m; // Approximation de la largeur

                    if (oldWord.Type == ChangeType.Deleted) DrawHighlightBox(page, currentLeftX, y, wWidth, 12m, 255, 200, 200); // Rouge clair
                    if (oldWord.Type == ChangeType.Modified) DrawHighlightBox(page, currentLeftX, y, wWidth, 12m, 255, 255, 150); // Jaune clair

                    page.SetTextAndFillColor(0, 0, 0);
                    page.AddText(word, 10m, new PdfPoint(currentLeftX, y), font);
                    currentLeftX += wWidth;
                }
            }

            // Rendu à Droite (Cible)
            if (newWord.Type != ChangeType.Imaginary && newWord.Text != null)
            {
                if (currentRightX < rightX + maxWidth - 20m)
                {
                    string word = newWord.Text + " ";
                    decimal wWidth = word.Length * 5m;

                    if (newWord.Type == ChangeType.Inserted) DrawHighlightBox(page, currentRightX, y, wWidth, 12m, 255, 200, 200); // Rouge clair
                    if (newWord.Type == ChangeType.Modified) DrawHighlightBox(page, currentRightX, y, wWidth, 12m, 255, 255, 150); // Jaune clair

                    page.SetTextAndFillColor(0, 0, 0);
                    page.AddText(word, 10m, new PdfPoint(currentRightX, y), font);
                    currentRightX += wWidth;
                }
            }
        }
    }

    // CORRECTION: types decimal
    private void DrawSimpleText(PdfPageBuilder page, string text, decimal x, decimal y, PdfDocumentBuilder.AddedFont font, decimal maxWidth, string colorCode)
    {
        string display = text.Length > 70 ? text.Substring(0, 67) + "..." : text;

        if (colorCode == "Red")
        {
            DrawHighlightBox(page, x, y, display.Length * 5m, 12m, 255, 200, 200);
        }

        page.SetTextAndFillColor(0, 0, 0);
        page.AddText(display, 10m, new PdfPoint(x, y), font);
    }

    // CORRECTION: Utilisation de "fill: true" de PdfPig et suppression de FillPath()
    private void DrawHighlightBox(PdfPageBuilder page, decimal x, decimal y, decimal width, decimal height, byte r, byte g, byte b)
    {
        page.SetTextAndFillColor(r, g, b);
        // Signature PdfPig: DrawRectangle(PdfPoint bottomL, decimal width, decimal height, decimal lineWidth, bool fill)
        page.DrawRectangle(new PdfPoint(x, y - 2m), width, height, 0m, true);
    }

    private async Task GenerateGlobalSynthesisReportAsync(List<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        await Task.Run(() =>
        {
            string reportPath = Path.Combine(outputDiffDir, "Global_Synthesis_Report.pdf");
            Directory.CreateDirectory(outputDiffDir);

            var builder = new PdfDocumentBuilder();
            PdfPageBuilder page = builder.AddPage(595, 842); // Portrait classique pour la synthèse
            var (font, fontBold) = LoadFonts(builder);

            decimal margin = 40m;
            decimal yPosition = 842m - margin;
            int maxChars = 85;

            page.SetTextAndFillColor(0, 0, 0);
            page.AddText("SYNTHÈSE GLOBALE DES DIFFÉRENCES", 16m, new PdfPoint(margin, yPosition), fontBold);
            yPosition -= 20m;
            page.SetTextAndFillColor(100, 100, 100);
            page.AddText("Ce document présente un résumé narratif de toutes les modifications détectées.", 10m, new PdfPoint(margin, yPosition), font);
            yPosition -= 35m;

            foreach (var doc in summaries.OrderBy(s => s.DocumentName))
            {
                if (yPosition < margin + 50m) { page = builder.AddPage(595, 842); yPosition = 842m - margin; }

                page.SetTextAndFillColor(0, 50, 150);
                page.AddText($"► Fichier: {doc.DocumentName}", 13m, new PdfPoint(margin, yPosition), fontBold);
                yPosition -= 20m;

                foreach (var block in doc.Blocks)
                {
                    if (yPosition < margin + 40m) { page = builder.AddPage(595, 842); yPosition = 842m - margin; }

                    page.SetTextAndFillColor(0, 0, 0);
                    if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                    {
                        foreach (var l in WrapText($"... {block.ContextBefore}", maxChars))
                        {
                            page.AddText(l, 10m, new PdfPoint(margin + 15m, yPosition), font);
                            yPosition -= 12m;
                        }
                    }

                    page.SetTextAndFillColor(200, 0, 0);
                    foreach (var l in WrapText($"-> {block.DiffContent}", maxChars))
                    {
                        page.AddText(l, 11m, new PdfPoint(margin + 15m, yPosition), fontBold);
                        yPosition -= 13m;
                    }

                    page.SetTextAndFillColor(0, 0, 0);
                    if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                    {
                        foreach (var l in WrapText($"{block.ContextAfter} ...", maxChars))
                        {
                            page.AddText(l, 10m, new PdfPoint(margin + 15m, yPosition), font);
                            yPosition -= 12m;
                        }
                    }
                    yPosition -= 15m;
                }
                yPosition -= 20m;
            }

            File.WriteAllBytes(reportPath, builder.Build());
        });
    }

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

    private (PdfDocumentBuilder.AddedFont Font, PdfDocumentBuilder.AddedFont FontBold) LoadFonts(PdfDocumentBuilder builder)
    {
        string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string arialPath = Path.Combine(fontsFolder, "arial.ttf");
        string arialBoldPath = Path.Combine(fontsFolder, "arialbd.ttf");

        if (!File.Exists(arialPath) || !File.Exists(arialBoldPath))
            throw new FileNotFoundException("Required Arial fonts were not found on this system.");

        return (builder.AddTrueTypeFont(File.ReadAllBytes(arialPath)),
                builder.AddTrueTypeFont(File.ReadAllBytes(arialBoldPath)));
    }

    private string NormalizePdfText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string flatText = Regex.Replace(input, @"\s+", " ");
        flatText = flatText.Replace(". ", ".\n").Replace("? ", "?\n").Replace("! ", "!\n").Replace(": ", ":\n");
        flatText = flatText.Replace("•", "\n• ").Replace(" o ", "\n o ");
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