using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;

namespace PDFComparison.Services;

public interface IGlobalSynthesisReportGenerator
{
    void GenerateGlobalSynthesisReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir);
}

public partial class GlobalSynthesisReportGenerator : IGlobalSynthesisReportGenerator
{
    private readonly IPdfDrawingService _drawingService;
    private readonly IPdfChartService _chartService;
    private readonly IInlineDiffService _inlineDiffService;

    // Expressions régulières compilées (Haute performance)
    [GeneratedRegex(@"^\s*[\d.,%€$£]+\s*$")]
    private static partial Regex NumRegex();

    [GeneratedRegex(@"\b\d{1,2}[-/\.]\d{1,2}[-/\.]\d{2,4}\b")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"(?i)\b(prix|pénalité|pénalités|résiliation|ttc|ht|garantie|article|euro|taxe|montant|facture)\b")]
    private static partial Regex CriticalRegex();

    public GlobalSynthesisReportGenerator(
        IPdfDrawingService drawingService,
        IPdfChartService chartService,
        IInlineDiffService inlineDiffService)
    {
        _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
        _chartService = chartService ?? throw new ArgumentNullException(nameof(chartService));
        _inlineDiffService = inlineDiffService ?? throw new ArgumentNullException(nameof(inlineDiffService));
    }

    public void GenerateGlobalSynthesisReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        if (summaries == null || summaries.Count == 0) return;

        string reportPath = Path.Combine(outputDiffDir, "Global_Synthesis_Report.pdf");
        Directory.CreateDirectory(outputDiffDir);

        var builder = new PdfDocumentBuilder();
        var (font, fontBold) = _drawingService.LoadFonts(builder);

        // ==========================================
        // 1. CALCUL DES STATISTIQUES GLOBALES
        // ==========================================
        int totalInserts = 0, totalDeletes = 0, totalModifies = 0;
        int typeDates = 0, typeNumbers = 0, typeWords = 0;
        int totalOldWords = 0, totalNewWords = 0, criticalAlerts = 0;

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

            // SYNCHRONISATION : On utilise les compteurs visuels (rectangles)
            int visualTotalForDoc = doc.VisualInsertedCount + doc.VisualDeletedCount + doc.VisualModifiedCount;
            languageDiffCounts[lang] += visualTotalForDoc;

            totalInserts += doc.VisualInsertedCount;
            totalDeletes += doc.VisualDeletedCount;
            totalModifies += doc.VisualModifiedCount;

            foreach (var block in doc.Blocks)
            {
                string oldTxt = block.OldText ?? string.Empty;
                string newTxt = block.NewText ?? string.Empty;

                totalOldWords += oldTxt.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                totalNewWords += newTxt.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;

                string txtToAnalyze = block.Type == ChangeType.Deleted ? oldTxt : newTxt;

                if (DateRegex().IsMatch(txtToAnalyze)) typeDates++;
                else if (NumRegex().IsMatch(txtToAnalyze)) typeNumbers++;
                else typeWords++;

                criticalAlerts += CriticalRegex().Matches(oldTxt).Count;
                criticalAlerts += CriticalRegex().Matches(newTxt).Count;
            }
        }

        int totalChanges = totalInserts + totalDeletes + totalModifies;
        int wordBalance = totalNewWords - totalOldWords;

        // Top 3 basé sur la réalité visuelle
        var topModifiedFiles = summaries
            .OrderByDescending(s => s.VisualInsertedCount + s.VisualDeletedCount + s.VisualModifiedCount)
            .Take(3)
            .ToList();

        // ==========================================
        // 2. CRÉATION DU TABLEAU DE BORD
        // ==========================================
        PdfPageBuilder page = builder.AddPage(842, 595); // Paysage (A4)
        decimal margin = 40m;
        decimal yPosition = 595m - margin;

        DrawDashboardLayout(page, margin, yPosition, totalChanges, summaries.Count, wordBalance, criticalAlerts, topModifiedFiles, languageDiffCounts, font, fontBold);

        // Les graphiques ScottPlot utiliseront désormais les valeurs synchronisées
        if (totalChanges > 0)
        {
            _chartService.DrawDashboardCharts(page, margin, yPosition - 160m, totalInserts, totalDeletes, totalModifies, typeDates, typeNumbers, typeWords, languageFileCounts, font);
        }

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

                var (leftChunks, rightChunks) = _inlineDiffService.GetInlineDiffChunks(block.OldText, block.NewText);

                // --- COLONNE GAUCHE ---
                decimal currentYLeft = yPosition;
                if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                    currentYLeft = _drawingService.DrawTextLines(page, $"... {block.ContextBefore}", currentYLeft, leftColumnX, maxCharsCol, 100, 100, 100, font);

                if (leftChunks.Count > 0)
                    currentYLeft = _drawingService.DrawMixedTextLines(page, leftChunks, currentYLeft, leftColumnX, 370m, font, fontBold);
                else
                    currentYLeft = _drawingService.DrawTextLines(page, "(Aucun texte original)", currentYLeft, leftColumnX, maxCharsCol, 180, 180, 180, font);

                if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                    currentYLeft = _drawingService.DrawTextLines(page, $"{block.ContextAfter} ...", currentYLeft, leftColumnX, maxCharsCol, 100, 100, 100, font);

                // --- COLONNE DROITE ---
                decimal currentYRight = yPosition;
                if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                    currentYRight = _drawingService.DrawTextLines(page, $"... {block.ContextBefore}", currentYRight, rightColumnX, maxCharsCol, 100, 100, 100, font);

                if (rightChunks.Count > 0)
                    currentYRight = _drawingService.DrawMixedTextLines(page, rightChunks, currentYRight, rightColumnX, 370m, font, fontBold);
                else
                    currentYRight = _drawingService.DrawTextLines(page, "(Texte supprimé)", currentYRight, rightColumnX, maxCharsCol, 180, 180, 180, font);

                if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                    currentYRight = _drawingService.DrawTextLines(page, $"{block.ContextAfter} ...", currentYRight, rightColumnX, maxCharsCol, 100, 100, 100, font);

                yPosition = Math.Min(currentYLeft, currentYRight) - 20m;

                page.SetStrokeColor(220, 220, 220);
                page.DrawLine(new PdfPoint((double)margin, (double)(yPosition + 10m)), new PdfPoint((double)(842m - margin), (double)(yPosition + 10m)), 0.5m);
            }
            yPosition -= 20m;
        }

        try
        {
            File.WriteAllBytes(reportPath, builder.Build());
        }
        catch (Exception ex)
        {
            throw new Exception($"Erreur critique lors de la construction du fichier {Path.GetFileName(reportPath)}. {ex.Message}");
        }
    }

    private void DrawDashboardLayout(PdfPageBuilder page, decimal startX, decimal startY, int totalChanges, int totalFiles, int wordBalance, int criticalAlerts, List<DocumentDiffSummary> topFiles, Dictionary<string, int> languageDiffCounts, PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal currentY = startY;

        page.SetTextAndFillColor(0, 50, 150);
        page.AddText("RAPPORT DE SYNTHÈSE GLOBALE", 18m, new PdfPoint((double)startX, (double)currentY), fontBold);

        page.SetTextAndFillColor(100, 100, 100);
        page.AddText($"Généré le {DateTime.Now:dd/MM/yyyy à HH:mm} • Comparaison automatisée de documents.", 10m, new PdfPoint((double)startX, (double)(currentY - 18m)), font);

        currentY -= 50m;

        string balanceText = wordBalance > 0 ? $"+ {wordBalance} mots" : $"{wordBalance} mots";

        _drawingService.DrawStatBox(page, startX, currentY, "Fichiers impactés", totalFiles.ToString(), font, fontBold, 0, 50, 150);
        _drawingService.DrawStatBox(page, startX + 155m, currentY, "Total changements", totalChanges.ToString(), font, fontBold, 0, 50, 150);
        _drawingService.DrawStatBox(page, startX + 310m, currentY, "Bilan net (Volume)", balanceText, font, fontBold, wordBalance < 0 ? (byte)220 : (byte)16, wordBalance < 0 ? (byte)20 : (byte)185, wordBalance < 0 ? (byte)20 : (byte)129);
        _drawingService.DrawStatBox(page, startX + 465m, currentY, "Mots Sensibles", criticalAlerts.ToString(), font, fontBold, criticalAlerts > 0 ? (byte)255 : (byte)0, criticalAlerts > 0 ? (byte)140 : (byte)50, criticalAlerts > 0 ? (byte)0 : (byte)150);

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

            // SYNCHRONISATION : Utilisation des compteurs visuels ici aussi
            int docVisualTotal = file.VisualInsertedCount + file.VisualDeletedCount + file.VisualModifiedCount;
            page.AddText($"{docVisualTotal} diffs", 10m, new PdfPoint((double)(startX + 280m), (double)listY), fontBold);
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
    }
}