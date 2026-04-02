using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using ScottPlot;
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

        int totalOldWords = 0;
        int totalNewWords = 0;
        int criticalAlerts = 0;

        var numRegex = new Regex(@"^\s*[\d.,%€$£]+\s*$");
        // CORRECTION : Prise en compte des points dans le format des dates pour que le Dashboard soit précis
        var dateRegex = new Regex(@"\b\d{1,2}[-/\.]\d{1,2}[-/\.]\d{2,4}\b");

        // Mots sensibles pour l'audit juridique/financier
        var criticalRegex = new Regex(@"(?i)\b(prix|pénalité|pénalités|résiliation|ttc|ht|garantie|article|euro|taxe|montant|facture)\b");

        var languageFileCounts = new Dictionary<string, int>();
        var languageDiffCounts = new Dictionary<string, int>();

        foreach (var doc in summaries)
        {
            string lang = string.IsNullOrWhiteSpace(doc.Language) ? "ND" : doc.Language;

            if (!languageFileCounts.ContainsKey(lang))
            {
                languageFileCounts[lang] = 0;
                languageDiffCounts[lang] = 0;
            }

            languageFileCounts[lang]++;
            languageDiffCounts[lang] += doc.Blocks.Count;

            foreach (var block in doc.Blocks)
            {
                if (block.Type == ChangeType.Inserted) totalInserts++;
                else if (block.Type == ChangeType.Deleted) totalDeletes++;
                else if (block.Type == ChangeType.Modified) totalModifies++;

                string oldTxt = block.OldText ?? "";
                string newTxt = block.NewText ?? "";

                totalOldWords += oldTxt.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                totalNewWords += newTxt.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;

                string txtToAnalyze = block.Type == ChangeType.Deleted ? oldTxt : newTxt;

                if (dateRegex.IsMatch(txtToAnalyze)) typeDates++;
                else if (numRegex.IsMatch(txtToAnalyze)) typeNumbers++;
                else typeWords++;

                criticalAlerts += criticalRegex.Matches(oldTxt).Count;
                criticalAlerts += criticalRegex.Matches(newTxt).Count;
            }
        }

        int totalChanges = totalInserts + totalDeletes + totalModifies;
        int wordBalance = totalNewWords - totalOldWords;

        var topModifiedFiles = summaries
            .OrderByDescending(s => s.Blocks.Count)
            .Take(3)
            .ToList();

        // ==========================================
        // 2. CRÉATION DU TABLEAU DE BORD
        // ==========================================
        PdfPageBuilder page = builder.AddPage(842, 595); // Paysage (A4)
        decimal margin = 40m;
        decimal yPosition = 595m - margin;

        DrawDashboardWithScottPlot(page, margin, yPosition,
            totalChanges, totalInserts, totalDeletes, totalModifies,
            typeDates, typeNumbers, typeWords, summaries.Count,
            wordBalance, criticalAlerts, topModifiedFiles,
            languageFileCounts, languageDiffCounts,
            font, fontBold);

        // ==========================================
        // 3. GÉNÉRATION DES PAGES DE DÉTAILS
        // ==========================================
        decimal leftColumnX = margin;
        decimal rightColumnX = 430m;
        int maxCharsCol = 65;

        foreach (var doc in summaries.OrderBy(s => s.DocumentName))
        {
            page = builder.AddPage(842, 595);
            yPosition = 595m - margin;

            page.SetTextAndFillColor(0, 50, 150);
            page.AddText($"► Fichier: {doc.DocumentName}", 14m, new PdfPoint((double)margin, (double)yPosition), fontBold);
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
                page.AddText(changeTypeStr, 11m, new PdfPoint((double)margin, (double)yPosition), fontBold);

                yPosition -= 15m;
                page.SetTextAndFillColor(150, 150, 150);
                page.AddText("Document Original (Source)", 9m, new PdfPoint((double)leftColumnX, (double)yPosition), fontBold);
                page.AddText("Document Modifié (Cible)", 9m, new PdfPoint((double)rightColumnX, (double)yPosition), fontBold);
                yPosition -= 15m;

                var (leftChunks, rightChunks) = GetInlineDiffChunks(block.OldText, block.NewText);

                decimal currentYLeft = yPosition;
                if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                    currentYLeft = DrawTextLines(page, $"... {block.ContextBefore}", currentYLeft, leftColumnX, maxCharsCol, 100, 100, 100, font);

                if (leftChunks.Count > 0)
                    currentYLeft = DrawMixedTextLines(page, leftChunks, currentYLeft, leftColumnX, 370m, font, fontBold);
                else
                    currentYLeft = DrawTextLines(page, "(Aucun texte original)", currentYLeft, leftColumnX, maxCharsCol, 180, 180, 180, font);

                if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                    currentYLeft = DrawTextLines(page, $"{block.ContextAfter} ...", currentYLeft, leftColumnX, maxCharsCol, 100, 100, 100, font);

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
                page.DrawLine(new PdfPoint((double)margin, (double)(yPosition + 10m)), new PdfPoint((double)(842m - margin), (double)(yPosition + 10m)), 0.5m);
            }
            yPosition -= 20m;
        }

        // Ajout d'un bloc Try-Catch de sécurité
        try
        {
            File.WriteAllBytes(reportPath, builder.Build());
        }
        catch (Exception ex)
        {
            throw new Exception($"Erreur critique lors de la construction du fichier {Path.GetFileName(reportPath)}. {ex.Message}");
        }
    }

    // ==========================================
    // MOTEUR DE DESSIN DU TABLEAU DE BORD
    // ==========================================

    private void DrawDashboardWithScottPlot(PdfPageBuilder page, decimal startX, decimal startY,
        int totalChanges, int inserts, int deletes, int modifies,
        int dates, int numbers, int words, int totalFiles,
        int wordBalance, int criticalAlerts, List<DocumentDiffSummary> topFiles,
        Dictionary<string, int> languageFileCounts, Dictionary<string, int> languageDiffCounts,
        PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal currentY = startY;

        page.SetTextAndFillColor(0, 50, 150);
        page.AddText("RAPPORT DE SYNTHÈSE GLOBALE", 18m, new PdfPoint((double)startX, (double)currentY), fontBold);

        page.SetTextAndFillColor(100, 100, 100);
        page.AddText($"Généré le {DateTime.Now:dd/MM/yyyy à HH:mm} • Comparaison automatisée de documents.", 10m, new PdfPoint((double)startX, (double)(currentY - 18m)), font);

        currentY -= 50m;

        string balanceText = wordBalance > 0 ? $"+ {wordBalance} mots" : $"{wordBalance} mots";
        DrawStatBox(page, startX, currentY, "Fichiers impactés", totalFiles.ToString(), font, fontBold, 0, 50, 150);
        DrawStatBox(page, startX + 155m, currentY, "Total changements", totalChanges.ToString(), font, fontBold, 0, 50, 150);
        DrawStatBox(page, startX + 310m, currentY, "Bilan net (Volume)", balanceText, font, fontBold, wordBalance < 0 ? (byte)220 : (byte)16, wordBalance < 0 ? (byte)20 : (byte)185, wordBalance < 0 ? (byte)20 : (byte)129);
        DrawStatBox(page, startX + 465m, currentY, "Mots Sensibles", criticalAlerts.ToString(), font, fontBold, criticalAlerts > 0 ? (byte)255 : (byte)0, criticalAlerts > 0 ? (byte)140 : (byte)50, criticalAlerts > 0 ? (byte)0 : (byte)150);

        currentY -= 60m;

        if (totalChanges == 0)
        {
            page.SetTextAndFillColor(100, 100, 100);
            page.AddText("Aucune différence détectée lors de cette session.", 12m, new PdfPoint((double)startX, (double)currentY), font);
            return;
        }

        page.SetTextAndFillColor(0, 0, 0);
        page.AddText("Top 3 des fichiers les plus modifiés :", 12m, new PdfPoint((double)startX, (double)currentY), fontBold);

        page.AddText("Volume de différences par Langue :", 12m, new PdfPoint((double)(startX + 400m), (double)currentY), fontBold);

        decimal listY = currentY - 20m;
        foreach (var file in topFiles)
        {
            page.SetTextAndFillColor(80, 80, 80);
            page.AddText($"• {file.DocumentName}", 10m, new PdfPoint((double)startX, (double)listY), font);
            page.SetTextAndFillColor(0, 50, 150);
            page.AddText($"{file.Blocks.Count} diffs", 10m, new PdfPoint((double)(startX + 280m), (double)listY), fontBold);
            listY -= 15m;
        }

        decimal langY = currentY - 20m;
        foreach (var kvp in languageDiffCounts.OrderByDescending(x => x.Value).Take(4))
        {
            page.SetTextAndFillColor(80, 80, 80);
            page.AddText($"• Documents [{kvp.Key}]", 10m, new PdfPoint((double)(startX + 400m), (double)langY), font);
            page.SetTextAndFillColor(0, 50, 150);
            page.AddText($"{kvp.Value} erreurs", 10m, new PdfPoint((double)(startX + 520m), (double)langY), fontBold);
            langY -= 15m;
        }

        currentY -= 75m;

        page.SetTextAndFillColor(0, 0, 0);
        page.AddText("Répartition par type d'action :", 11m, new PdfPoint((double)startX, (double)currentY), fontBold);
        page.AddText("Nature des données impactées :", 11m, new PdfPoint((double)(startX + 260m), (double)currentY), fontBold);
        page.AddText("Documents modifiés par Langue :", 11m, new PdfPoint((double)(startX + 520m), (double)currentY), fontBold);
        currentY -= 10m;

        // GRAPHIQUE 1 : ACTIONS
        var plt1 = new Plot();
        plt1.HideGrid();
        plt1.HideAxesAndGrid();
        var slices1 = new List<PieSlice>();
        if (inserts > 0) slices1.Add(new PieSlice { Value = inserts, Label = $"{inserts} Ajouts", FillColor = ScottPlot.Color.FromHex("#10B981") });
        if (deletes > 0) slices1.Add(new PieSlice { Value = deletes, Label = $"{deletes} Supp.", FillColor = ScottPlot.Color.FromHex("#EF4444") });
        if (modifies > 0) slices1.Add(new PieSlice { Value = modifies, Label = $"{modifies} Modif.", FillColor = ScottPlot.Color.FromHex("#F59E0B") });

        if (slices1.Count > 0)
        {
            var pie1 = plt1.Add.Pie(slices1);
            pie1.ExplodeFraction = 0.05;
            byte[] imgBytes1 = plt1.GetImageBytes(240, 200, ImageFormat.Png);
            page.AddPng(imgBytes1, new PdfRectangle((short)startX, (short)(currentY - 200m), (short)(startX + 240m), (short)currentY));
        }
        else
        {
            page.SetTextAndFillColor(150, 150, 150);
            page.AddText("(Aucune donnée)", 10m, new PdfPoint((double)(startX + 60m), (double)(currentY - 100m)), font);
        }

        // GRAPHIQUE 2 : NATURE DES DONNÉES
        var plt2 = new Plot();
        plt2.HideGrid();
        plt2.HideAxesAndGrid();
        var slices2 = new List<PieSlice>();
        if (words > 0) slices2.Add(new PieSlice { Value = words, Label = $"{words} Textes", FillColor = ScottPlot.Color.FromHex("#3B82F6") });
        if (numbers > 0) slices2.Add(new PieSlice { Value = numbers, Label = $"{numbers} Nombres", FillColor = ScottPlot.Color.FromHex("#8B5CF6") });
        if (dates > 0) slices2.Add(new PieSlice { Value = dates, Label = $"{dates} Dates", FillColor = ScottPlot.Color.FromHex("#14B8A6") });

        if (slices2.Count > 0)
        {
            var pie2 = plt2.Add.Pie(slices2);
            pie2.ExplodeFraction = 0.05;
            byte[] imgBytes2 = plt2.GetImageBytes(240, 200, ImageFormat.Png);
            page.AddPng(imgBytes2, new PdfRectangle((short)(startX + 260m), (short)(currentY - 200m), (short)(startX + 260m + 240m), (short)currentY));
        }
        else
        {
            page.SetTextAndFillColor(150, 150, 150);
            page.AddText("(Aucune donnée)", 10m, new PdfPoint((double)(startX + 320m), (double)(currentY - 100m)), font);
        }

        // GRAPHIQUE 3 : LANGUES
        var plt3 = new Plot();
        plt3.HideGrid();
        plt3.HideAxesAndGrid();
        var slices3 = new List<PieSlice>();
        string[] colors = { "#3B82F6", "#F59E0B", "#10B981", "#8B5CF6", "#EF4444", "#14B8A6" };
        int cIdx = 0;
        foreach (var kvp in languageFileCounts.Where(x => x.Value > 0))
        {
            slices3.Add(new PieSlice { Value = kvp.Value, Label = $"{kvp.Key} ({kvp.Value})", FillColor = ScottPlot.Color.FromHex(colors[cIdx % colors.Length]) });
            cIdx++;
        }

        if (slices3.Count > 0)
        {
            var pie3 = plt3.Add.Pie(slices3);
            pie3.ExplodeFraction = 0.05;
            byte[] imgBytes3 = plt3.GetImageBytes(240, 200, ImageFormat.Png);
            page.AddPng(imgBytes3, new PdfRectangle((short)(startX + 520m), (short)(currentY - 200m), (short)(startX + 520m + 240m), (short)currentY));
        }
        else
        {
            page.SetTextAndFillColor(150, 150, 150);
            page.AddText("(Aucune donnée)", 10m, new PdfPoint((double)(startX + 580m), (double)(currentY - 100m)), font);
        }
    }

    private void DrawStatBox(PdfPageBuilder page, decimal x, decimal y, string label, string value, PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold, byte rValue, byte gValue, byte bValue)
    {
        page.SetTextAndFillColor(245, 247, 250);
        page.DrawRectangle(new PdfPoint((double)x, (double)(y - 25m)), 140m, 45m, 0m, true);
        page.SetTextAndFillColor(100, 100, 100);
        page.AddText(label, 9m, new PdfPoint((double)(x + 10m), (double)(y + 2m)), font);
        page.SetTextAndFillColor(rValue, gValue, bValue);
        page.AddText(value, 18m, new PdfPoint((double)(x + 10m), (double)(y - 18m)), fontBold);
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
                    leftChunks.Add((oLine.Text.Replace("\n", ""), 200, 0, 0, true));
                else
                    leftChunks.Add((oLine.Text.Replace("\n", ""), 100, 100, 100, false));
            }

            if (nLine.Type != ChangeType.Imaginary)
            {
                if (nLine.Type == ChangeType.Inserted || nLine.Type == ChangeType.Modified)
                    rightChunks.Add((nLine.Text.Replace("\n", ""), 0, 150, 0, true));
                else
                    rightChunks.Add((nLine.Text.Replace("\n", ""), 100, 100, 100, false));
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
                page.AddText(word.Replace("\n", ""), fontSize, new PdfPoint((double)currentX, (double)currentY), font);

                currentX += wordWidth;
            }
        }

        if (currentX > startX) currentY -= 13m;
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
    // MÉTHODE CORRIGÉE (MISE À JOUR)
    // ==========================================

    private void DrawDiffMarkup(PdfPageBuilder pageBuilder, IEnumerable<LetterLoc> letters, byte r, byte g, byte b, MarkupStyle style)
    {
        // 1. Appliquer la même tolérance d'alignement (5 points) que dans l'analyseur pour garder les lignes unies
        var sorted = letters
            .OrderByDescending(l => Math.Round(l.BaselineY / 5.0m) * 5.0m)
            .ThenBy(l => l.BoundingBox.BottomLeft.X)
            .ToList();

        if (sorted.Count == 0) return;

        var segments = new List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)>();
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

            // 2. Vérifier si on est sur la même ligne visuelle (tolérance de 5 points)
            bool isSameLine = Math.Abs(Math.Round(y / 5.0m) * 5.0m - Math.Round(cBaseline / 5.0m) * 5.0m) < 1m;

            // 3. Remplacer la valeur fixe (15m) par une valeur dynamique basée sur la taille de la police
            // Cela empêche de scinder les encadrés en deux pour les textes écrits en grand
            decimal maxGap = Math.Max(15m, cFontSize * 1.5m);

            // Permettre un léger chevauchement arrière (x >= cMinX - 5m) pour les polices bizarres
            if (isSameLine && (x - cMaxX) < maxGap && x >= cMinX - 5m)
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

        pageBuilder.SetStrokeColor(r, g, b);

        foreach (var seg in segments)
        {
            decimal strokeWidth = Math.Max(seg.fontSize * 0.08m, 0.75m);
            decimal width = seg.maxX - seg.minX;

            switch (style)
            {
                case MarkupStyle.Strikethrough:
                    pageBuilder.DrawLine(new PdfPoint((double)seg.minX, (double)(seg.baselineY + (seg.fontSize * 0.3m))), new PdfPoint((double)seg.maxX, (double)(seg.baselineY + (seg.fontSize * 0.3m))), strokeWidth);
                    break;
                case MarkupStyle.Underline:
                    pageBuilder.DrawLine(new PdfPoint((double)seg.minX, (double)(seg.baselineY - (seg.fontSize * 0.12m))), new PdfPoint((double)seg.maxX, (double)(seg.baselineY - (seg.fontSize * 0.12m))), strokeWidth);
                    break;
                case MarkupStyle.Box:
                    // Ajout d'un padding pour un rendu visuel beaucoup plus propre autour du texte
                    decimal paddingX = seg.fontSize * 0.1m;
                    pageBuilder.DrawRectangle(new PdfPoint((double)(seg.minX - paddingX), (double)(seg.baselineY - (seg.fontSize * 0.15m))), width + (paddingX * 2), seg.fontSize * 0.9m, strokeWidth, false);
                    break;
            }
        }
    }

    private void DrawPageStamp(PdfPageBuilder pageBuilder, string text, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal yPosition = Math.Max((decimal)pageBuilder.PageSize.Height - 30m, 10m);
        pageBuilder.SetTextAndFillColor(255, 255, 255);
        pageBuilder.DrawRectangle(new PdfPoint(10.0, (double)yPosition), 300m, 20m, 0m, true);
        pageBuilder.SetTextAndFillColor(0, 50, 150);
        pageBuilder.AddText(text, 12m, new PdfPoint(15.0, (double)(yPosition + 5m)), fontBold);
    }

    private decimal DrawTextLines(PdfPageBuilder page, string text, decimal startY, decimal startX, int maxChars, byte r, byte g, byte b, PdfDocumentBuilder.AddedFont fontToUse)
    {
        decimal currentY = startY;
        page.SetTextAndFillColor(r, g, b);

        foreach (var line in WrapText(text, maxChars))
        {
            page.AddText(line, 10m, new PdfPoint((double)startX, (double)currentY), fontToUse);
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