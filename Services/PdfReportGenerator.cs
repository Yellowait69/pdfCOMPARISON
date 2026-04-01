using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using ScottPlot; // Intégration de ScottPlot pour les graphiques
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace PDFComparison.Services;

public class PdfReportGenerator
{
    public void GenerateIndividualReport(string sourcePath, string targetPath, string reportPath, VisualHighlights highlights)
    {
        string? directory = Path.GetDirectoryName(reportPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new PdfDocumentBuilder();
        var (font, fontBold) = LoadFonts(builder);

        using var sourceDoc = PdfDocument.Open(sourcePath);
        using var targetDoc = PdfDocument.Open(targetPath);

        int maxPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);

        for (int pageIndex = 1; pageIndex <= maxPages; pageIndex++)
        {
            if (pageIndex <= sourceDoc.NumberOfPages)
            {
                var sPage = builder.AddPage(sourceDoc, pageIndex);
                DrawDiffMarkup(sPage, highlights.SourceRed.Where(w => w.PageNumber == pageIndex), 220, 20, 20, MarkupStyle.Strikethrough);
                DrawDiffMarkup(sPage, highlights.SourceYellow.Where(w => w.PageNumber == pageIndex), 255, 140, 0, MarkupStyle.Box);
                DrawPageStamp(sPage, $"[ DOCUMENT SOURCE - Page {pageIndex} ]", fontBold);
            }

            if (pageIndex <= targetDoc.NumberOfPages)
            {
                var tPage = builder.AddPage(targetDoc, pageIndex);
                DrawDiffMarkup(tPage, highlights.TargetRed.Where(w => w.PageNumber == pageIndex), 20, 180, 20, MarkupStyle.Underline);
                DrawDiffMarkup(tPage, highlights.TargetYellow.Where(w => w.PageNumber == pageIndex), 255, 140, 0, MarkupStyle.Box);
                DrawPageStamp(tPage, $"[ DOCUMENT CIBLE (Modifié) - Page {pageIndex} ]", fontBold);
            }
        }

        File.WriteAllBytes(reportPath, builder.Build());
    }

    public void GenerateGlobalSynthesisReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        string reportPath = Path.Combine(outputDiffDir, "Global_Synthesis_Report.pdf");
        Directory.CreateDirectory(outputDiffDir);

        var builder = new PdfDocumentBuilder();
        var (font, fontBold) = LoadFonts(builder);

        // ==========================================
        // 1. CALCUL DES STATISTIQUES GLOBALES
        // ==========================================
        int totalInserts = 0, totalDeletes = 0, totalModifies = 0;
        int typeDates = 0, typeNumbers = 0, typeWords = 0;

        var numRegex = new Regex(@"^\s*[\d.,%€$£]+\s*$");
        var dateRegex = new Regex(@"\b\d{1,4}[-/]\d{1,2}[-/]\d{1,4}\b");

        foreach (var doc in summaries)
        {
            foreach (var block in doc.Blocks)
            {
                if (block.Type == ChangeType.Inserted) totalInserts++;
                else if (block.Type == ChangeType.Deleted) totalDeletes++;
                else if (block.Type == ChangeType.Modified) totalModifies++;

                string txtToAnalyze = block.Type == ChangeType.Deleted ? block.OldText : block.NewText;

                if (dateRegex.IsMatch(txtToAnalyze)) typeDates++;
                else if (numRegex.IsMatch(txtToAnalyze)) typeNumbers++;
                else typeWords++;
            }
        }
        int totalChanges = totalInserts + totalDeletes + totalModifies;

        // ==========================================
        // 2. CRÉATION DU TABLEAU DE BORD
        // ==========================================
        PdfPageBuilder page = builder.AddPage(842, 595); // Format Paysage (A4)
        decimal margin = 40m;
        decimal yPosition = 595m - margin;

        page.SetTextAndFillColor(0, 0, 0);
        page.AddText("SYNTHÈSE GLOBALE DES DIFFÉRENCES", 18m, new(margin, yPosition), fontBold);
        yPosition -= 20m;
        page.SetTextAndFillColor(100, 100, 100);
        page.AddText("Analyse comparative automatisée des documents originaux et modifiés.", 10m, new(margin, yPosition), font);
        yPosition -= 40m;

        // Génère les graphiques via ScottPlot
        DrawDashboardWithScottPlot(page, margin, yPosition, totalChanges, totalInserts, totalDeletes, totalModifies, typeDates, typeNumbers, typeWords, summaries.Count, font, fontBold);

        // ==========================================
        // 3. GÉNÉRATION DES PAGES DE DÉTAILS
        // ==========================================
        decimal leftColumnX = margin;
        decimal rightColumnX = 430m;
        int maxCharsCol = 65;

        foreach (var doc in summaries.OrderBy(s => s.DocumentName))
        {
            // Nouvelle page pour chaque document après le tableau de bord
            page = builder.AddPage(842, 595);
            yPosition = 595m - margin;

            page.SetTextAndFillColor(0, 50, 150);
            page.AddText($"► Fichier: {doc.DocumentName}", 14m, new(margin, yPosition), fontBold);
            yPosition -= 25m;

            foreach (var block in doc.Blocks)
            {
                int linesLeft = (block.ContextBefore.Length + block.OldText.Length + block.ContextAfter.Length) / maxCharsCol;
                int linesRight = (block.ContextBefore.Length + block.NewText.Length + block.ContextAfter.Length) / maxCharsCol;
                decimal estimatedHeight = Math.Max(linesLeft, linesRight) * 13m + 60m;

                if (yPosition - estimatedHeight < margin)
                {
                    page = builder.AddPage(842, 595);
                    yPosition = 595m - margin;
                }

                string changeTypeStr = block.Type switch
                {
                    ChangeType.Inserted => "[ AJOUT ]",
                    ChangeType.Deleted => "[ SUPPRESSION ]",
                    ChangeType.Modified => "[ MODIFICATION ]",
                    _ => "[ CHANGEMENT ]"
                };

                page.SetTextAndFillColor(0, 0, 0);
                page.AddText(changeTypeStr, 11m, new(margin, yPosition), fontBold);

                yPosition -= 15m;
                page.SetTextAndFillColor(150, 150, 150);
                page.AddText("Document Original (Source)", 9m, new(leftColumnX, yPosition), fontBold);
                page.AddText("Document Modifié (Cible)", 9m, new(rightColumnX, yPosition), fontBold);
                yPosition -= 15m;

                var (leftChunks, rightChunks) = GetInlineDiffChunks(block.OldText, block.NewText);

                // --- COLONNE GAUCHE ---
                decimal currentYLeft = yPosition;
                if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                    currentYLeft = DrawTextLines(page, $"... {block.ContextBefore}", currentYLeft, leftColumnX, maxCharsCol, 100, 100, 100, font);

                if (leftChunks.Count > 0)
                    currentYLeft = DrawMixedTextLines(page, leftChunks, currentYLeft, leftColumnX, 370m, font, fontBold);
                else
                    currentYLeft = DrawTextLines(page, "(Aucun texte original)", currentYLeft, leftColumnX, maxCharsCol, 180, 180, 180, font);

                if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                    currentYLeft = DrawTextLines(page, $"{block.ContextAfter} ...", currentYLeft, leftColumnX, maxCharsCol, 100, 100, 100, font);

                // --- COLONNE DROITE ---
                decimal currentYRight = yPosition;
                if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                    currentYRight = DrawTextLines(page, $"... {block.ContextBefore}", currentYRight, rightColumnX, maxCharsCol, 100, 100, 100, font);

                if (rightChunks.Count > 0)
                    currentYRight = DrawMixedTextLines(page, rightChunks, currentYRight, rightColumnX, 370m, font, fontBold);
                else
                    currentYRight = DrawTextLines(page, "(Texte supprimé)", currentYRight, rightColumnX, maxCharsCol, 180, 180, 180, font);

                if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                    currentYRight = DrawTextLines(page, $"{block.ContextAfter} ...", currentYRight, rightColumnX, maxCharsCol, 100, 100, 100, font);

                yPosition = Math.Min(currentYLeft, currentYRight) - 20m;
                page.SetStrokeColor(220, 220, 220);
                page.DrawLine(new(margin, yPosition + 10m), new(842m - margin, yPosition + 10m), 0.5m);
            }
            yPosition -= 20m;
        }

        File.WriteAllBytes(reportPath, builder.Build());
    }

    // ==========================================
    // MOTEUR DE DESSIN DU TABLEAU DE BORD (SCOTTPLOT)
    // ==========================================

    private void DrawDashboardWithScottPlot(PdfPageBuilder page, decimal startX, decimal startY,
        int totalChanges, int inserts, int deletes, int modifies,
        int dates, int numbers, int words, int totalFiles,
        PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal currentY = startY;

        page.SetTextAndFillColor(0, 50, 150);
        page.AddText("TABLEAU DE BORD DES STATISTIQUES", 14m, new(startX, currentY), fontBold);
        currentY -= 35m;

        DrawStatBox(page, startX, currentY, "Fichiers impactés", totalFiles.ToString(), font, fontBold);
        DrawStatBox(page, startX + 160m, currentY, "Total des changements", totalChanges.ToString(), font, fontBold);

        currentY -= 65m;

        if (totalChanges == 0)
        {
            page.SetTextAndFillColor(100, 100, 100);
            page.AddText("Aucune différence détectée lors de cette session.", 12m, new(startX, currentY), font);
            return;
        }

        page.SetTextAndFillColor(0, 0, 0);
        page.AddText("Répartition par type d'action :", 12m, new(startX, currentY), fontBold);
        page.AddText("Nature des données impactées :", 12m, new(startX + 380m, currentY), fontBold);
        currentY -= 10m;

        // Graphique 1
        var plt1 = new Plot();
        plt1.HideGrid();
        plt1.HideAxesAndGrid();

        var slices1 = new List<PieSlice>();
        if (inserts > 0) slices1.Add(new PieSlice { Value = inserts, Label = $"{inserts} Ajouts", FillColor = ScottPlot.Color.FromHex("#10B981") });
        if (deletes > 0) slices1.Add(new PieSlice { Value = deletes, Label = $"{deletes} Supp.", FillColor = ScottPlot.Color.FromHex("#EF4444") });
        if (modifies > 0) slices1.Add(new PieSlice { Value = modifies, Label = $"{modifies} Modif.", FillColor = ScottPlot.Color.FromHex("#F59E0B") });

        var pie1 = plt1.Add.Pie(slices1);
        pie1.ExplodeFraction = 0.05;

        byte[] imgBytes1 = plt1.GetImageBytes(350, 250, ImageFormat.Png);

        // CORRECTION : Cast explicite en (short) pour le constructeur de PdfRectangle
        page.AddPng(imgBytes1, new PdfRectangle((short)startX, (short)(currentY - 250m), (short)(startX + 350m), (short)currentY));

        // Graphique 2
        var plt2 = new Plot();
        plt2.HideGrid();
        plt2.HideAxesAndGrid();

        var slices2 = new List<PieSlice>();
        if (words > 0) slices2.Add(new PieSlice { Value = words, Label = $"{words} Textes", FillColor = ScottPlot.Color.FromHex("#3B82F6") });
        if (numbers > 0) slices2.Add(new PieSlice { Value = numbers, Label = $"{numbers} Nombres", FillColor = ScottPlot.Color.FromHex("#8B5CF6") });
        if (dates > 0) slices2.Add(new PieSlice { Value = dates, Label = $"{dates} Dates", FillColor = ScottPlot.Color.FromHex("#14B8A6") });

        var pie2 = plt2.Add.Pie(slices2);
        pie2.ExplodeFraction = 0.05;

        byte[] imgBytes2 = plt2.GetImageBytes(350, 250, ImageFormat.Png);

        // CORRECTION : Cast explicite en (short) pour le constructeur de PdfRectangle
        page.AddPng(imgBytes2, new PdfRectangle((short)(startX + 380m), (short)(currentY - 250m), (short)(startX + 380m + 350m), (short)currentY));
    }

    private void DrawStatBox(PdfPageBuilder page, decimal x, decimal y, string label, string value, PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold)
    {
        page.SetTextAndFillColor(245, 247, 250);
        page.DrawRectangle(new(x, y - 25m), 140m, 45m, 0m, true);
        page.SetTextAndFillColor(100, 100, 100);
        page.AddText(label, 9m, new(x + 10m, y + 2m), font);
        page.SetTextAndFillColor(0, 50, 150);
        page.AddText(value, 18m, new(x + 10m, y - 18m), fontBold);
    }

    // ==========================================
    // MÉTHODES DE RENDU "INLINE" DES MOTS
    // ==========================================

    private (List<(string Text, byte r, byte g, byte b, bool isBold)> Left, List<(string Text, byte r, byte g, byte b, bool isBold)> Right) GetInlineDiffChunks(string oldText, string newText)
    {
        var leftChunks = new List<(string Text, byte r, byte g, byte b, bool isBold)>();
        var rightChunks = new List<(string Text, byte r, byte g, byte b, bool isBold)>();

        if (string.IsNullOrWhiteSpace(oldText) && string.IsNullOrWhiteSpace(newText))
            return (leftChunks, rightChunks);

        var oldWords = Regex.Split(oldText ?? string.Empty, @"(?<=\s+)").Where(x => x.Length > 0).ToList();
        var newWords = Regex.Split(newText ?? string.Empty, @"(?<=\s+)").Where(x => x.Length > 0).ToList();

        var diff = new SideBySideDiffBuilder(new Differ()).BuildDiffModel(
            string.Join("\n", oldWords),
            string.Join("\n", newWords)
        );

        for (int i = 0; i < diff.OldText.Lines.Count; i++)
        {
            var oLine = diff.OldText.Lines[i];
            var nLine = diff.NewText.Lines[i];

            if (oLine.Type != ChangeType.Imaginary)
            {
                if (oLine.Type == ChangeType.Deleted || oLine.Type == ChangeType.Modified)
                    leftChunks.Add((oLine.Text.Replace("\n", ""), 200, 0, 0, true)); // Rouge et Gras
                else
                    leftChunks.Add((oLine.Text.Replace("\n", ""), 100, 100, 100, false)); // Gris
            }

            if (nLine.Type != ChangeType.Imaginary)
            {
                if (nLine.Type == ChangeType.Inserted || nLine.Type == ChangeType.Modified)
                    rightChunks.Add((nLine.Text.Replace("\n", ""), 0, 150, 0, true)); // Vert et Gras
                else
                    rightChunks.Add((nLine.Text.Replace("\n", ""), 100, 100, 100, false)); // Gris
            }
        }

        return (leftChunks, rightChunks);
    }

    private decimal DrawMixedTextLines(PdfPageBuilder page, List<(string Text, byte r, byte g, byte b, bool isBold)> chunks, decimal startY, decimal startX, decimal maxWidth, PdfDocumentBuilder.AddedFont fontNormal, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal currentY = startY;
        decimal currentX = startX;
        decimal fontSize = 10m;

        foreach (var chunk in chunks)
        {
            if (string.IsNullOrEmpty(chunk.Text)) continue;

            var words = Regex.Split(chunk.Text, @"(?<=\s+)");

            foreach (var word in words)
            {
                if (string.IsNullOrEmpty(word)) continue;

                decimal wordWidth = MeasureStringWidth(word, fontSize, chunk.isBold);

                if (currentX + wordWidth > startX + maxWidth && currentX > startX)
                {
                    currentY -= 13m;
                    currentX = startX;

                    if (string.IsNullOrWhiteSpace(word)) continue;
                }

                var font = chunk.isBold ? fontBold : fontNormal;
                page.SetTextAndFillColor(chunk.r, chunk.g, chunk.b);
                page.AddText(word.Replace("\n", ""), fontSize, new(currentX, currentY), font);

                currentX += wordWidth;
            }
        }

        if (currentX > startX)
        {
            currentY -= 13m;
        }
        return currentY;
    }

    private decimal MeasureStringWidth(string text, decimal fontSize, bool isBold)
    {
        decimal width = 0m;
        foreach (char c in text)
        {
            if (c == ' ') width += 0.278m;
            else if (c == 'i' || c == 'j' || c == 'l') width += 0.222m;
            else if (c == 'f' || c == 't' || c == 'I') width += 0.278m;
            else if (c == 'm' || c == 'w' || c == 'M' || c == 'W') width += 0.833m;
            else if (char.IsUpper(c)) width += 0.667m;
            else if (char.IsDigit(c)) width += 0.556m;
            else width += 0.556m;
        }
        return width * fontSize * (isBold ? 1.05m : 1.0m);
    }

    // ==========================================
    // MÉTHODES CONSERVÉES
    // ==========================================

    private void DrawDiffMarkup(PdfPageBuilder pageBuilder, IEnumerable<LetterLoc> letters, byte r, byte g, byte b, MarkupStyle style)
    {
        var sorted = letters.OrderByDescending(l => l.BaselineY).ThenBy(l => l.BoundingBox.BottomLeft.X).ToList();
        if (sorted.Count is 0) return;

        var segments = new List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)>();
        var first = sorted[0];
        decimal cMinX = (decimal)first.BoundingBox.BottomLeft.X, cMaxX = (decimal)first.BoundingBox.TopRight.X, cBaseline = first.BaselineY, cFontSize = first.FontSize;

        for (int i = 1; i < sorted.Count; i++)
        {
            var loc = sorted[i];
            decimal x = (decimal)loc.BoundingBox.BottomLeft.X, y = loc.BaselineY;

            if (Math.Abs(y - cBaseline) < 3m && (x - cMaxX) < 15m)
            {
                cMaxX = Math.Max(cMaxX, (decimal)loc.BoundingBox.TopRight.X);
                cFontSize = Math.Max(cFontSize, loc.FontSize);
            }
            else
            {
                segments.Add((cMinX, cMaxX, cBaseline, cFontSize));
                cMinX = x; cMaxX = (decimal)loc.BoundingBox.TopRight.X; cBaseline = y; cFontSize = loc.FontSize;
            }
        }
        segments.Add((cMinX, cMaxX, cBaseline, cFontSize));

        pageBuilder.SetStrokeColor(r, g, b);

        foreach (var seg in segments)
        {
            decimal strokeWidth = Math.Max(seg.fontSize * 0.08m, 0.75m);
            decimal width = seg.maxX - seg.minX;

            switch (style)
            {
                case MarkupStyle.Strikethrough:
                    pageBuilder.DrawLine(new(seg.minX, seg.baselineY + (seg.fontSize * 0.3m)), new(seg.maxX, seg.baselineY + (seg.fontSize * 0.3m)), strokeWidth);
                    break;
                case MarkupStyle.Underline:
                    pageBuilder.DrawLine(new(seg.minX, seg.baselineY - (seg.fontSize * 0.12m)), new(seg.maxX, seg.baselineY - (seg.fontSize * 0.12m)), strokeWidth);
                    break;
                case MarkupStyle.Box:
                    pageBuilder.DrawRectangle(new(seg.minX - 1m, seg.baselineY - (seg.fontSize * 0.15m)), width + 2m, seg.fontSize * 0.9m, strokeWidth, false);
                    break;
            }
        }
    }

    private void DrawPageStamp(PdfPageBuilder pageBuilder, string text, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal yPosition = Math.Max((decimal)pageBuilder.PageSize.Height - 30m, 10m);
        pageBuilder.SetTextAndFillColor(255, 255, 255);
        pageBuilder.DrawRectangle(new(10m, yPosition), 300m, 20m, 0m, true);
        pageBuilder.SetTextAndFillColor(0, 50, 150);
        pageBuilder.AddText(text, 12m, new(15m, yPosition + 5m), fontBold);
    }

    private decimal DrawTextLines(PdfPageBuilder page, string text, decimal startY, decimal startX, int maxChars, byte r, byte g, byte b, PdfDocumentBuilder.AddedFont fontToUse)
    {
        decimal currentY = startY;
        page.SetTextAndFillColor(r, g, b);

        foreach (var line in WrapText(text, maxChars))
        {
            page.AddText(line, 10m, new(startX, currentY), fontToUse);
            currentY -= 13m;
        }
        return currentY;
    }

    private (PdfDocumentBuilder.AddedFont Font, PdfDocumentBuilder.AddedFont FontBold) LoadFonts(PdfDocumentBuilder builder)
    {
        string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string arialPath = Path.Combine(fontsFolder, "arial.ttf");
        string arialBoldPath = Path.Combine(fontsFolder, "arialbd.ttf");

        if (!File.Exists(arialPath) || !File.Exists(arialBoldPath))
        {
            throw new FileNotFoundException("Required Arial fonts were not found.");
        }

        return (builder.AddTrueTypeFont(File.ReadAllBytes(arialPath)), builder.AddTrueTypeFont(File.ReadAllBytes(arialBoldPath)));
    }

    private IEnumerable<string> WrapText(string text, int maxLength)
    {
        for (int i = 0; i < text.Length; i += maxLength)
        {
            yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
        }
    }
}