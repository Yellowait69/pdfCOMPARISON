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

public enum MarkupStyle
{
    Strikethrough, // Pour le texte supprimé (barré)
    Underline,     // Pour le texte ajouté (souligné)
    Box            // Pour le texte modifié (encadré)
}

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

// Stocke la liste complète des lettres pour autoriser un Diff au caractère près
public class PdfWordInfo
{
    public string Text { get; set; } = string.Empty;
    public IReadOnlyList<Letter> Letters { get; set; } = new List<Letter>();
    public int PageNumber { get; set; }
}

// Mémorise la géométrie typographique parfaite d'une lettre à surligner
public class LetterLoc
{
    public PdfRectangle BoundingBox { get; set; }
    public int PageNumber { get; set; }
    public decimal BaselineY { get; set; }
    public decimal FontSize { get; set; }

    public LetterLoc(PdfRectangle bbox, int page, decimal baselineY, decimal fontSize)
    {
        BoundingBox = bbox;
        PageNumber = page;
        BaselineY = baselineY;
        FontSize = fontSize;
    }
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

    // Extraction des mots et de leurs LETTRES pour la précision géométrique au caractère près
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
                        words.Add(new PdfWordInfo {
                            Text = word.Text,
                            Letters = word.Letters, // Conserve les coordonnées typographiques exactes !
                            PageNumber = page.Number
                        });
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

            // 2. Traitement visuel MOT et CARACTÈRE sur les PDFs originaux
            var sourceWords = ExtractWords(pair.SourcePath);
            var targetWords = ExtractWords(pair.TargetPath!);

            var diffBuilderWords = new SideBySideDiffBuilder(new DiffPlex.Differ());
            var diffWords = diffBuilderWords.BuildDiffModel(
                string.Join("\n", sourceWords.Select(w => w.Text)),
                string.Join("\n", targetWords.Select(w => w.Text))
            );

            List<LetterLoc> sourceHighlightsRed = new();
            List<LetterLoc> sourceHighlightsYellow = new();
            List<LetterLoc> targetHighlightsRed = new();
            List<LetterLoc> targetHighlightsYellow = new();

            int sPointer = 0;
            int tPointer = 0;

            for (int i = 0; i < diffWords.NewText.Lines.Count; i++)
            {
                var oldWordDiff = diffWords.OldText.Lines[i];
                var newWordDiff = diffWords.NewText.Lines[i];

                PdfWordInfo oldWordInfo = null;
                PdfWordInfo newWordInfo = null;

                if (oldWordDiff.Type != ChangeType.Imaginary && sPointer < sourceWords.Count)
                    oldWordInfo = sourceWords[sPointer++];

                if (newWordDiff.Type != ChangeType.Imaginary && tPointer < targetWords.Count)
                    newWordInfo = targetWords[tPointer++];

                // Suppression pure (mot complet en rouge)
                if (oldWordDiff.Type == ChangeType.Deleted && oldWordInfo != null)
                {
                    sourceHighlightsRed.AddRange(oldWordInfo.Letters.Select(l => new LetterLoc(l.GlyphRectangle, oldWordInfo.PageNumber, (decimal)l.Location.Y, (decimal)l.PointSize)));
                }
                // Ajout pur (mot complet en vert)
                else if (newWordDiff.Type == ChangeType.Inserted && newWordInfo != null)
                {
                    targetHighlightsRed.AddRange(newWordInfo.Letters.Select(l => new LetterLoc(l.GlyphRectangle, newWordInfo.PageNumber, (decimal)l.Location.Y, (decimal)l.PointSize)));
                }
                // Modification de mot : DIFF AU CARACTÈRE pour isoler les ajouts/modifs partielles
                else if ((oldWordDiff.Type == ChangeType.Modified || newWordDiff.Type == ChangeType.Modified) && oldWordInfo != null && newWordInfo != null)
                {
                    var charDiff = diffBuilderWords.BuildDiffModel(
                        string.Join("\n", oldWordInfo.Text.ToCharArray()),
                        string.Join("\n", newWordInfo.Text.ToCharArray())
                    );

                    int oC = 0, nC = 0;
                    for (int j = 0; j < charDiff.NewText.Lines.Count; j++)
                    {
                        var oChar = charDiff.OldText.Lines[j];
                        var nChar = charDiff.NewText.Lines[j];

                        if (oChar.Type != ChangeType.Imaginary && oC < oldWordInfo.Letters.Count)
                        {
                            var let = oldWordInfo.Letters[oC];
                            if (oChar.Type == ChangeType.Deleted)
                                sourceHighlightsRed.Add(new LetterLoc(let.GlyphRectangle, oldWordInfo.PageNumber, (decimal)let.Location.Y, (decimal)let.PointSize));
                            else if (oChar.Type == ChangeType.Modified)
                                sourceHighlightsYellow.Add(new LetterLoc(let.GlyphRectangle, oldWordInfo.PageNumber, (decimal)let.Location.Y, (decimal)let.PointSize));
                            oC++;
                        }

                        if (nChar.Type != ChangeType.Imaginary && nC < newWordInfo.Letters.Count)
                        {
                            var let = newWordInfo.Letters[nC];
                            if (nChar.Type == ChangeType.Inserted)
                                targetHighlightsRed.Add(new LetterLoc(let.GlyphRectangle, newWordInfo.PageNumber, (decimal)let.Location.Y, (decimal)let.PointSize));
                            else if (nChar.Type == ChangeType.Modified)
                                targetHighlightsYellow.Add(new LetterLoc(let.GlyphRectangle, newWordInfo.PageNumber, (decimal)let.Location.Y, (decimal)let.PointSize));
                            nC++;
                        }
                    }
                }
            }

            // 3. Construction du PDF alterné avec le style "Track Changes"
            var builder = new PdfDocumentBuilder();
            var (font, fontBold) = LoadFonts(builder);

            using (var sourceDoc = PdfDocument.Open(pair.SourcePath))
            using (var targetDoc = PdfDocument.Open(pair.TargetPath!))
            {
                int maxPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);

                for (int pageIndex = 1; pageIndex <= maxPages; pageIndex++)
                {
                    if (pageIndex <= sourceDoc.NumberOfPages)
                    {
                        var sPage = builder.AddPage(sourceDoc, pageIndex);

                        // Source : Suppressions pures (Rouge barré) et Modifications (Orange encadré)
                        DrawDiffMarkup(sPage, sourceHighlightsRed.Where(w => w.PageNumber == pageIndex), 220, 20, 20, MarkupStyle.Strikethrough);
                        DrawDiffMarkup(sPage, sourceHighlightsYellow.Where(w => w.PageNumber == pageIndex), 255, 140, 0, MarkupStyle.Box);

                        DrawPageStamp(sPage, $"[ DOCUMENT SOURCE - Page {pageIndex} ]", fontBold);
                    }

                    if (pageIndex <= targetDoc.NumberOfPages)
                    {
                        var tPage = builder.AddPage(targetDoc, pageIndex);

                        // Cible : Ajouts purs (Vert souligné) et Modifications (Orange encadré)
                        DrawDiffMarkup(tPage, targetHighlightsRed.Where(w => w.PageNumber == pageIndex), 20, 180, 20, MarkupStyle.Underline);
                        DrawDiffMarkup(tPage, targetHighlightsYellow.Where(w => w.PageNumber == pageIndex), 255, 140, 0, MarkupStyle.Box);

                        DrawPageStamp(tPage, $"[ DOCUMENT CIBLE (Modifié) - Page {pageIndex} ]", fontBold);
                    }
                }
            }

            File.WriteAllBytes(reportPath, builder.Build());
            return (differencesCount, summary);
        });
    }

    // Le nouvel effet visuel typographique (Suivi des modifications)
    private void DrawDiffMarkup(PdfPageBuilder pageBuilder, IEnumerable<LetterLoc> letters, byte r, byte g, byte b, MarkupStyle style)
    {
        var sorted = letters.OrderByDescending(l => l.BaselineY)
                            .ThenBy(l => l.BoundingBox.BottomLeft.X)
                            .ToList();

        if (sorted.Count == 0) return;

        var segments = new List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)>();

        // Logique de regroupement des lettres en segments continus
        var first = sorted[0];
        decimal cMinX = (decimal)first.BoundingBox.BottomLeft.X;
        decimal cMaxX = (decimal)first.BoundingBox.TopRight.X;
        decimal cBaseline = first.BaselineY;
        decimal cFontSize = first.FontSize;

        for (int i = 1; i < sorted.Count; i++)
        {
            var loc = sorted[i];
            decimal x = (decimal)loc.BoundingBox.BottomLeft.X;
            decimal y = loc.BaselineY;

            // Si c'est sur la même ligne et assez proche (espacement < 15pts)
            if (Math.Abs(y - cBaseline) < 3m && (x - cMaxX) < 15m)
            {
                cMaxX = Math.Max(cMaxX, (decimal)loc.BoundingBox.TopRight.X);
                cFontSize = Math.Max(cFontSize, loc.FontSize);
            }
            else
            {
                segments.Add((cMinX, cMaxX, cBaseline, cFontSize));
                cMinX = x;
                cMaxX = (decimal)loc.BoundingBox.TopRight.X;
                cBaseline = y;
                cFontSize = loc.FontSize;
            }
        }
        segments.Add((cMinX, cMaxX, cBaseline, cFontSize));

        // Application des styles visuels
        pageBuilder.SetStrokeColor(r, g, b); // Couleur du trait (Stroke)

        foreach (var seg in segments)
        {
            // Épaisseur dynamique du trait basée sur la taille de la police
            decimal strokeWidth = seg.fontSize * 0.08m;
            if (strokeWidth < 0.75m) strokeWidth = 0.75m; // Minimum visible

            pageBuilder.SetLineWidth(strokeWidth);
            decimal width = seg.maxX - seg.minX;

            if (style == MarkupStyle.Strikethrough)
            {
                // Barre horizontale au milieu de la hauteur de la lettre
                decimal y = seg.baselineY + (seg.fontSize * 0.3m);
                pageBuilder.DrawLine(new PdfPoint(seg.minX, y), new PdfPoint(seg.maxX, y));
            }
            else if (style == MarkupStyle.Underline)
            {
                // Ligne juste en dessous de la ligne de base
                decimal y = seg.baselineY - (seg.fontSize * 0.12m);
                pageBuilder.DrawLine(new PdfPoint(seg.minX, y), new PdfPoint(seg.maxX, y));
            }
            else if (style == MarkupStyle.Box)
            {
                // Cadre léger autour du mot (fill = false pour ne dessiner que les bords)
                decimal y = seg.baselineY - (seg.fontSize * 0.15m);
                decimal height = seg.fontSize * 0.9m;
                // On élargit très légèrement la boîte pour ne pas coller aux lettres
                pageBuilder.DrawRectangle(new PdfPoint(seg.minX - 1m, y), width + 2m, height, strokeWidth, false);
            }
        }
    }

    // Ajoute un tampon visuel en haut à gauche pour repérer Source/Cible
    private void DrawPageStamp(PdfPageBuilder pageBuilder, string text, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal rectHeight = 20m;
        decimal rectWidth = 300m;
        decimal yPosition = (decimal)pageBuilder.PageSize.Height - 30m;

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