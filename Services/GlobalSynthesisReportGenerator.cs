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

    [GeneratedRegex(@"^\s*[\d.,%€$£]+\s*$")]
    private static partial Regex NumRegex();

    [GeneratedRegex(@"\b\d{1,2}[-/\.]\d{1,2}[-/\.]\d{2,4}\b")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"(?i)\b(prix|pénalité|pénalités|résiliation|ttc|ht|garantie|article|euro|taxe|montant|facture|price|penalty|termination|vat|warranty|tax|amount|invoice)\b")]
    private static partial Regex CriticalRegex();

    [GeneratedRegex(@"(\d+)\.pdf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileSuffixNumberRegex();

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

        int totalInserts = 0, totalDeletes = 0;
        int typeDates = 0, typeNumbers = 0, typeWords = 0;
        int totalOldWords = 0, totalNewWords = 0, criticalAlerts = 0;

        var languageFileCounts = new Dictionary<string, int>();
        var languageDiffCounts = new Dictionary<string, int>();

        foreach (var doc in summaries)
        {
            string lang = string.IsNullOrWhiteSpace(doc.Language) ? "NA" : doc.Language;

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

        int totalChanges = summaries.Sum(s => s.Blocks.Count);
        int wordBalance = totalNewWords - totalOldWords;
        var topModifiedFiles = summaries.OrderByDescending(s => s.Blocks.Count).Take(3).ToList();

        PdfPageBuilder page = builder.AddPage(842, 595);
        decimal margin = 40m;
        decimal yPosition = 595m - margin;

        DrawDashboardPage(page, margin, yPosition, totalChanges, summaries.Count, wordBalance, criticalAlerts, totalInserts, totalDeletes, typeDates, typeNumbers, typeWords, languageFileCounts, languageDiffCounts, topModifiedFiles, font, fontBold);

        if (summaries.Any(s => s.Blocks.Count > 0))
        {
            page = builder.AddPage(842, 595);
            yPosition = 595m - margin;
        }

        decimal leftColumnX = margin;
        decimal rightColumnX = 430m;
        int maxCharsCol = 55;
        decimal drawWidth = 370m;

        var sortedSummaries = summaries.OrderBy(s =>
        {
            var match = FileSuffixNumberRegex().Match(s.DocumentName ?? string.Empty);
            return match.Success && int.TryParse(match.Groups[1].Value, out int num) ? num : int.MaxValue;
        }).ThenBy(s => s.DocumentName).ToList();

        foreach (var doc in sortedSummaries)
        {
            if (doc.Blocks.Count == 0) continue;

            bool isFirstBlockOfDoc = true;

            foreach (var block in doc.Blocks)
            {
                bool hasImages = block.SourceImage != null || block.TargetImage != null;
                decimal sourceImgHeight = 0m;
                decimal targetImgHeight = 0m;

                if (block.SourceImage != null)
                {
                    using var ms = new MemoryStream(block.SourceImage);
                    using var img = System.Drawing.Image.FromStream(ms);
                    decimal aspect = (decimal)img.Width / (decimal)img.Height;
                    sourceImgHeight = drawWidth / aspect;
                }

                if (block.TargetImage != null)
                {
                    using var ms = new MemoryStream(block.TargetImage);
                    using var img = System.Drawing.Image.FromStream(ms);
                    decimal aspect = (decimal)img.Width / (decimal)img.Height;
                    targetImgHeight = drawWidth / aspect;
                }

                decimal maxImgHeight = Math.Max(sourceImgHeight, targetImgHeight);
                decimal estimatedHeight = hasImages ? (maxImgHeight + 90m) :
                    (Math.Max(block.OldText.Length, block.NewText.Length) / maxCharsCol + 3) * 15m + 80m;

                decimal requiredHeight = estimatedHeight + (isFirstBlockOfDoc ? 35m : 0m);

                if (page == null || yPosition - requiredHeight < margin)
                {
                    page = builder.AddPage(842, 595);
                    yPosition = 595m - margin;
                    isFirstBlockOfDoc = true;
                }

                if (isFirstBlockOfDoc)
                {
                    page.SetTextAndFillColor(240, 245, 250);
                    page.DrawRectangle(new PdfPoint((double)(margin - 5m), (double)(yPosition - 4m)), 772m, 22m, 0m, true);

                    page.SetTextAndFillColor(15, 23, 42);
                    page.AddText($"Document: {doc.DocumentName}", 12m, new PdfPoint((double)margin, (double)yPosition), fontBold);

                    if (!string.IsNullOrEmpty(doc.ReportFileName))
                    {
                        page.SetTextAndFillColor(37, 99, 235);
                        page.AddText($"► See Details in: {doc.ReportFileName}", 10m, new PdfPoint((double)(margin + 400m), (double)yPosition), fontBold);
                    }

                    yPosition -= 30m;
                    isFirstBlockOfDoc = false;
                }

                string changeTypeStr = block.Type switch
                {
                    ChangeType.Inserted => "INSERTION",
                    ChangeType.Deleted => "DELETION",
                    _ => "CHANGE"
                };

                byte r = 0, g = 50, b = 150;
                if (block.Type == ChangeType.Inserted) { r = 16; g = 185; b = 129; }
                else if (block.Type == ChangeType.Deleted) { r = 239; g = 68; b = 68; }

                page.SetTextAndFillColor(r, g, b);
                page.AddText($"Type: {changeTypeStr}", 10m, new PdfPoint((double)margin, (double)yPosition), fontBold);
                yPosition -= 15m;

                page.SetTextAndFillColor(150, 150, 150);
                page.AddText("Original Document (Source)", 9m, new PdfPoint((double)leftColumnX, (double)yPosition), fontBold);
                page.AddText("Modified Document (Target)", 9m, new PdfPoint((double)rightColumnX, (double)yPosition), fontBold);
                yPosition -= 15m;

                if (hasImages)
                {
                    if (block.SourceImage != null)
                    {
                        page.AddPng(block.SourceImage, new PdfRectangle((double)leftColumnX, (double)(yPosition - sourceImgHeight), (double)(leftColumnX + drawWidth), (double)yPosition));
                    }
                    else
                    {
                        page.SetTextAndFillColor(180, 180, 180);
                        page.AddText("(No original content)", 10m, new PdfPoint((double)leftColumnX, (double)(yPosition - 20m)), font);
                    }

                    if (block.TargetImage != null)
                    {
                        page.AddPng(block.TargetImage, new PdfRectangle((double)rightColumnX, (double)(yPosition - targetImgHeight), (double)(rightColumnX + drawWidth), (double)yPosition));
                    }
                    else
                    {
                        page.SetTextAndFillColor(180, 180, 180);
                        page.AddText("(Content deleted)", 10m, new PdfPoint((double)rightColumnX, (double)(yPosition - 20m)), font);
                    }

                    yPosition -= (maxImgHeight + 10m);
                }
                else
                {
                    var (leftChunks, rightChunks) = _inlineDiffService.GetInlineDiffChunks(block.OldText, block.NewText);

                    decimal currentYLeft = yPosition;
                    if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                        currentYLeft = _drawingService.DrawTextLines(page, $"... {block.ContextBefore}", currentYLeft, leftColumnX, maxCharsCol, 150, 150, 150, font);

                    if (leftChunks.Count > 0)
                        currentYLeft = _drawingService.DrawMixedTextLines(page, leftChunks, currentYLeft, leftColumnX, drawWidth, font, fontBold);
                    else
                        currentYLeft = _drawingService.DrawTextLines(page, "(No original text)", currentYLeft, leftColumnX, maxCharsCol, 180, 180, 180, font);

                    if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                        currentYLeft = _drawingService.DrawTextLines(page, $"{block.ContextAfter} ...", currentYLeft, leftColumnX, maxCharsCol, 150, 150, 150, font);

                    decimal currentYRight = yPosition;
                    if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                        currentYRight = _drawingService.DrawTextLines(page, $"... {block.ContextBefore}", currentYRight, rightColumnX, maxCharsCol, 150, 150, 150, font);

                    if (rightChunks.Count > 0)
                        currentYRight = _drawingService.DrawMixedTextLines(page, rightChunks, currentYRight, rightColumnX, drawWidth, font, fontBold);
                    else
                        currentYRight = _drawingService.DrawTextLines(page, "(Text deleted)", currentYRight, rightColumnX, maxCharsCol, 180, 180, 180, font);

                    if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                        currentYRight = _drawingService.DrawTextLines(page, $"{block.ContextAfter} ...", currentYRight, rightColumnX, maxCharsCol, 150, 150, 150, font);

                    yPosition = Math.Min(currentYLeft, currentYRight) - 10m;
                }

                page.SetStrokeColor(220, 220, 220);
                page.DrawLine(new PdfPoint((double)margin, (double)(yPosition + 5m)), new PdfPoint((double)(842m - margin), (double)(yPosition + 5m)), 1.0m);
                yPosition -= 15m;
            }
        }

        try
        {
            File.WriteAllBytes(reportPath, builder.Build());
        }
        catch (Exception ex)
        {
            throw new Exception($"Critical error while building the file {Path.GetFileName(reportPath)}. {ex.Message}");
        }
    }

    /// <summary>
    /// Nouvelle méthode gérant le dessin complet et séquentiel de la page 1 (Dashboard).
    /// Chaque élément descend proprement le curseur (currentY) pour empêcher tout chevauchement.
    /// </summary>
    private void DrawDashboardPage(
        PdfPageBuilder page, decimal startX, decimal startY, int totalChanges, int totalFiles,
        int wordBalance, int criticalAlerts, int totalInserts, int totalDeletes, int typeDates,
        int typeNumbers, int typeWords, Dictionary<string, int> languageFileCounts,
        Dictionary<string, int> languageDiffCounts, List<DocumentDiffSummary> topFiles,
        PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal currentY = startY;

        page.SetTextAndFillColor(0, 50, 150);
        page.AddText("GLOBAL SYNTHESIS REPORT", 18m, new PdfPoint((double)startX, (double)currentY), fontBold);

        page.SetTextAndFillColor(100, 100, 100);
        page.AddText($"Generated on {DateTime.Now:dd/MM/yyyy at HH:mm} • Automated document comparison.", 10m, new PdfPoint((double)startX, (double)(currentY - 18m)), font);

        currentY -= 65m;

        string balanceText = wordBalance > 0 ? $"+ {wordBalance} words" : $"{wordBalance} words";

        _drawingService.DrawStatBox(page, startX, currentY, "Impacted Files", totalFiles.ToString(), font, fontBold, 0, 50, 150);
        _drawingService.DrawStatBox(page, startX + 155m, currentY, "Total Differences", totalChanges.ToString(), font, fontBold, 0, 50, 150);
        _drawingService.DrawStatBox(page, startX + 310m, currentY, "Net Balance (Volume)", balanceText, font, fontBold, wordBalance < 0 ? (byte)220 : (byte)16, wordBalance < 0 ? (byte)20 : (byte)185, wordBalance < 0 ? (byte)20 : (byte)129);
        _drawingService.DrawStatBox(page, startX + 465m, currentY, "Sensitive Words", criticalAlerts.ToString(), font, fontBold, criticalAlerts > 0 ? (byte)255 : (byte)0, criticalAlerts > 0 ? (byte)140 : (byte)50, criticalAlerts > 0 ? (byte)0 : (byte)150);

        currentY -= 60m;

        if (totalChanges == 0)
        {
            page.SetTextAndFillColor(100, 100, 100);
            page.AddText("No differences detected during this session.", 12m, new PdfPoint((double)startX, (double)currentY), font);
            return;
        }

        page.SetTextAndFillColor(0, 0, 0);
        page.AddText("Distribution by Action Type:", 11m, new PdfPoint((double)startX, (double)currentY), fontBold);
        page.AddText("Nature of Impacted Data:", 11m, new PdfPoint((double)(startX + 260m), (double)currentY), fontBold);
        page.AddText("Modified Documents by Language:", 11m, new PdfPoint((double)(startX + 520m), (double)currentY), fontBold);

        currentY -= 15m;

        _chartService.DrawDashboardCharts(page, startX, currentY, totalInserts, totalDeletes, typeDates, typeNumbers, typeWords, languageFileCounts, font);

        currentY -= 210m;

        page.SetTextAndFillColor(0, 0, 0);
        page.AddText("Top 3 Most Modified Files:", 12m, new PdfPoint((double)startX, (double)currentY), fontBold);
        page.AddText("Differences Volume by Language:", 12m, new PdfPoint((double)(startX + 400m), (double)currentY), fontBold);

        decimal listY = currentY - 20m;
        foreach (var file in topFiles)
        {
            page.SetTextAndFillColor(80, 80, 80);

            string displayName = file.DocumentName;
            if (displayName.Length > 70)
            {
                displayName = displayName.Substring(0, 57) + "...";
            }

            page.AddText($"• {displayName}", 10m, new PdfPoint((double)startX, (double)listY), font);

            page.SetTextAndFillColor(0, 50, 150);
            page.AddText($"{file.Blocks.Count} diffs", 10m, new PdfPoint((double)(startX + 350m), (double)listY), fontBold);
            listY -= 15m;
        }

        decimal langY = currentY - 20m;
        foreach (var kvp in languageDiffCounts.OrderByDescending(x => x.Value).Take(4))
        {
            page.SetTextAndFillColor(80, 80, 80);
            page.AddText($"• Documents [{kvp.Key}]", 10m, new PdfPoint((double)(startX + 400m), (double)langY), font);
            page.SetTextAndFillColor(0, 50, 150);
            page.AddText($"{kvp.Value} errors", 10m, new PdfPoint((double)(startX + 520m), (double)langY), fontBold);
            langY -= 15m;
        }
    }
}