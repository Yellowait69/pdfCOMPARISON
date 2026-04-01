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

// Classe pour stocker la position géométrique exacte de chaque mot
public class PdfWordInfo
{
    public string Text { get; set; } = string.Empty;
    public UglyToad.PdfPig.Core.PdfRectangle BoundingBox { get; set; }
    public int PageNumber { get; set; }
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

    // Extraction des mots et de leurs coordonnées pour l'overlay
    private List<PdfWordInfo> ExtractWords(string pdfPath)
    {
        var words = new List<PdfWordInfo>();
        using (var doc = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false }))
        {
            foreach (var page in doc.GetPages())
            {
                foreach (var word in page.GetWords())
                {
                    if (!string.IsNullOrWhiteSpace(word.Text))
                    {
                        words.Add(new PdfWordInfo { Text = word.Text, BoundingBox = word.BoundingBox, PageNumber = page.Number });
                    }
                }
            }
        }
        return words;
    }

    private async Task<(int DiffCount, DocumentDiffSummary Summary)> GenerateIndividualFullReportAsync(DocumentPair pair, string sourceText, string targetText, string reportPath)
    {
        return await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

            // 1. Synthèse globale (Comparaison textuelle pour générer les phrases du résumé)
            string cleanSource = NormalizePdfText(sourceText);
            string cleanTarget = NormalizePdfText(targetText);
            var diffBuilderLines = new SideBySideDiffBuilder(new DiffPlex.Differ());
            var diffLines = diffBuilderLines.BuildDiffModel(cleanSource, cleanTarget);

            int differencesCount = 0;
            var summary = new DocumentDiffSummary { DocumentName = Path.GetFileName(pair.TargetPath!) };

            for (int i = 0; i < diffLines.NewText.Lines.Count; i++)
            {
                var newLine = diffLines.NewText.Lines[i];
                var oldLine = diffLines.OldText.Lines[i];

                if (newLine.Type == ChangeType.Inserted || newLine.Type == ChangeType.Modified || oldLine.Type == ChangeType.Deleted)
                {
                    differencesCount++;
                    var block = new DiffSummaryBlock
                    {
                        Type = newLine.Type != ChangeType.Unchanged ? newLine.Type : oldLine.Type,
                        ContextBefore = GetValidContextLine(diffLines.NewText.Lines, i, -1),
                        ContextAfter = GetValidContextLine(diffLines.NewText.Lines, i, 1),
                    };

                    if (newLine.Type == ChangeType.Modified)
                        block.DiffContent = $"Texte modifié : \"{oldLine.Text}\" -> \"{newLine.Text}\"";
                    else if (newLine.Type == ChangeType.Inserted)
                        block.DiffContent = $"Ajout : \"{newLine.Text}\"";
                    else if (oldLine.Type == ChangeType.Deleted)
                        block.DiffContent = $"Suppression : \"{oldLine.Text}\"";

                    summary.Blocks.Add(block);
                }
            }

            // 2. Traitement visuel mot par mot sur les PDFs originaux
            var sourceWords = ExtractWords(pair.SourcePath);
            var targetWords = ExtractWords(pair.TargetPath!);

            // Création d'un modèle de diff mot à mot
            var diffBuilderWords = new SideBySideDiffBuilder(new DiffPlex.Differ());
            var diffWords = diffBuilderWords.BuildDiffModel(
                string.Join("\n", sourceWords.Select(w => w.Text)),
                string.Join("\n", targetWords.Select(w => w.Text))
            );

            List<PdfWordInfo> sourceHighlightsRed = new();
            List<PdfWordInfo> sourceHighlightsYellow = new();
            List<PdfWordInfo> targetHighlightsRed = new();
            List<PdfWordInfo> targetHighlightsYellow = new();

            int sPointer = 0;
            int tPointer = 0;

            for (int i = 0; i < diffWords.NewText.Lines.Count; i++)
            {
                var oldWord = diffWords.OldText.Lines[i];
                var newWord = diffWords.NewText.Lines[i];

                if (oldWord.Type != ChangeType.Imaginary && sPointer < sourceWords.Count)
                {
                    if (oldWord.Type == ChangeType.Deleted) sourceHighlightsRed.Add(sourceWords[sPointer]);
                    else if (oldWord.Type == ChangeType.Modified) sourceHighlightsYellow.Add(sourceWords[sPointer]);
                    sPointer++;
                }

                if (newWord.Type != ChangeType.Imaginary && tPointer < targetWords.Count)
                {
                    if (newWord.Type == ChangeType.Inserted) targetHighlightsRed.Add(targetWords[tPointer]);
                    else if (newWord.Type == ChangeType.Modified) targetHighlightsYellow.Add(targetWords[tPointer]);
                    tPointer++;
                }
            }

            // 3. Construction du PDF alterné (Page 1 Source, Page 1 Target, etc.)
            var builder = new PdfDocumentBuilder();
            var (font, fontBold) = LoadFonts(builder);

            using (var sourceDoc = PdfDocument.Open(pair.SourcePath))
            using (var targetDoc = PdfDocument.Open(pair.TargetPath!))
            {
                int maxPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);

                for (int pageIndex = 1; pageIndex <= maxPages; pageIndex++)
                {
                    // Ajouter la page SOURCE si elle existe
                    if (pageIndex <= sourceDoc.NumberOfPages)
                    {
                        var sPage = builder.AddPage(sourceDoc, pageIndex);
                        DrawBoxHighlights(sPage, sourceHighlightsRed.Where(w => w.PageNumber == pageIndex), 220, 20, 20); // Rouge (Suppressions)
                        DrawBoxHighlights(sPage, sourceHighlightsYellow.Where(w => w.PageNumber == pageIndex), 200, 150, 0); // Jaune (Modifications)

                        // Tampon indicatif en haut de page
                        DrawPageStamp(sPage, $"[ DOCUMENT SOURCE - Page {pageIndex} ]", fontBold);
                    }

                    // Ajouter la page CIBLE si elle existe
                    if (pageIndex <= targetDoc.NumberOfPages)
                    {
                        var tPage = builder.AddPage(targetDoc, pageIndex);
                        DrawBoxHighlights(tPage, targetHighlightsRed.Where(w => w.PageNumber == pageIndex), 220, 20, 20); // Rouge (Ajouts)
                        DrawBoxHighlights(tPage, targetHighlightsYellow.Where(w => w.PageNumber == pageIndex), 200, 150, 0); // Jaune (Modifications)

                        // Tampon indicatif en haut de page
                        DrawPageStamp(tPage, $"[ DOCUMENT CIBLE (Modifié) - Page {pageIndex} ]", fontBold);
                    }
                }
            }

            File.WriteAllBytes(reportPath, builder.Build());
            return (differencesCount, summary);
        });
    }

    // Dessine un encadré de couleur (sans masquer le texte) autour des mots
    private void DrawBoxHighlights(PdfPageBuilder pageBuilder, IEnumerable<PdfWordInfo> words, byte r, byte g, byte b)
    {
        if (!words.Any()) return;

        pageBuilder.SetStrokeColor(r, g, b);

        foreach (var word in words)
        {
            var rect = word.BoundingBox;
            // Rectangle vide (stroke=true, fill=false), avec une petite marge
            pageBuilder.DrawRectangle(new PdfPoint(rect.BottomLeft.X - 1.5m, rect.BottomLeft.Y - 1.5m), rect.Width + 3m, rect.Height + 3m, 1.5m, false);
        }
    }

    // Ajoute un tampon visuel en haut à gauche pour repérer Source/Cible
    private void DrawPageStamp(PdfPageBuilder pageBuilder, string text, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal rectHeight = 20m;
        decimal rectWidth = 300m;
        decimal yPosition = pageBuilder.PageSize.Height - 30m;

        // Si on est trop haut, on ajuste le tampon
        if (yPosition < 0) yPosition = 10m;

        pageBuilder.SetTextAndFillColor(255, 255, 255);
        pageBuilder.DrawRectangle(new PdfPoint(10m, yPosition), rectWidth, rectHeight, 0m, true); // Fond blanc opaque

        pageBuilder.SetTextAndFillColor(0, 50, 150);
        pageBuilder.AddText(text, 12m, new PdfPoint(15m, yPosition + 5m), fontBold);
    }

    private async Task GenerateGlobalSynthesisReportAsync(List<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        await Task.Run(() =>
        {
            string reportPath = Path.Combine(outputDiffDir, "Global_Synthesis_Report.pdf");
            Directory.CreateDirectory(outputDiffDir);

            var builder = new PdfDocumentBuilder();
            PdfPageBuilder page = builder.AddPage(595, 842); // Portrait
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