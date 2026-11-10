using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;

namespace PDFComparison.Services;

public interface IGlobalSynthesisReportGenerator
{
    void GenerateGlobalSynthesisReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir);
    string ExportCsvAuditReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir);
}

public partial class GlobalSynthesisReportGenerator : IGlobalSynthesisReportGenerator
{
    private readonly IPdfDrawingService _drawingService;
    private readonly IPdfChartService _chartService;
    private readonly IInlineDiffService _inlineDiffService;
    private readonly IConfigurationService? _configService;
    private readonly IHtmlSynthesisReportGenerator _htmlReportGenerator;

    [GeneratedRegex(@"^\s*[\d.,%€$£]+\s*$")]
    private static partial Regex NumRegex();

    [GeneratedRegex(@"\b\d{1,2}[-/\.]\d{1,2}[-/\.]\d{2,4}\b")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"(?i)\b(prix|pénalité|pénalités|résiliation|ttc|ht|garantie|article|euro|taxe|montant|facture|price|penalty|termination|vat|warranty|tax|amount|invoice)\b")]
    private static partial Regex DefaultCriticalRegex();

    [GeneratedRegex(@"(\d+)\.pdf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileSuffixNumberRegex();

    public GlobalSynthesisReportGenerator(
        IPdfDrawingService drawingService,
        IPdfChartService chartService,
        IInlineDiffService inlineDiffService,
        IConfigurationService? configService = null,
        IHtmlSynthesisReportGenerator? htmlReportGenerator = null)
    {
        _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
        _chartService = chartService ?? throw new ArgumentNullException(nameof(chartService));
        _inlineDiffService = inlineDiffService ?? throw new ArgumentNullException(nameof(inlineDiffService));
        _configService = configService;
        _htmlReportGenerator = htmlReportGenerator ?? new HtmlSynthesisReportGenerator(inlineDiffService);
    }

    public void GenerateGlobalSynthesisReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        if (summaries == null || summaries.Count == 0) return;

        Directory.CreateDirectory(outputDiffDir);

        // 1. Génération automatique du Rapport Web HTML5 Interactif
        try
        {
            _htmlReportGenerator.GenerateInteractiveHtmlReport(summaries, outputDiffDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HTML Report generation warning: {ex.Message}");
        }

        // 2. Génération automatique de l'Export Audit CSV/Excel et du résumé JSON CI/CD
        try
        {
            ExportCsvAuditReport(summaries, outputDiffDir);
            ExportCiJsonSummary(summaries, outputDiffDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CSV/JSON Export warning: {ex.Message}");
        }

        // 3. Génération du Rapport PDF Global de Synthèse
        string reportPath = Path.Combine(outputDiffDir, "Global_Synthesis_Report.pdf");

        var builder = new PdfDocumentBuilder();
        var (font, fontBold) = _drawingService.LoadFonts(builder);

        int totalInserts = 0, totalDeletes = 0;
        int typeDates = 0, typeNumbers = 0, typeWords = 0;
        int totalOldWords = 0, totalNewWords = 0, criticalAlerts = 0;

        var languageFileCounts = new Dictionary<string, int>();
        var languageDiffCounts = new Dictionary<string, int>();

        Regex activeCriticalRegex = GetActiveCriticalRegex();

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
                else if (block.Type == ChangeType.Modified)
                {
                    totalInserts++;
                    totalDeletes++;
                }

                string oldTxt = block.OldText ?? string.Empty;
                string newTxt = block.NewText ?? string.Empty;

                totalOldWords += oldTxt.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                totalNewWords += newTxt.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;

                string txtToAnalyze = $"{oldTxt} {newTxt}";

                if (block.Category == "Date" || DateRegex().IsMatch(txtToAnalyze)) typeDates++;
                else if (block.Category == "Montant / Chiffre" || NumRegex().IsMatch(txtToAnalyze)) typeNumbers++;
                else typeWords++;

                if (block.Severity == DiffSeverity.Critical)
                {
                    criticalAlerts++;
                }
                else
                {
                    criticalAlerts += activeCriticalRegex.Matches(oldTxt).Count;
                    criticalAlerts += activeCriticalRegex.Matches(newTxt).Count;
                }
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

        var sortedSummaries = summaries.OrderByDescending(s => s.CriticalAlertsCount).ThenBy(s =>
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
                bool hasText = !string.IsNullOrWhiteSpace(block.OldText) || !string.IsNullOrWhiteSpace(block.NewText);
                decimal sourceImgHeight = 0m;
                decimal targetImgHeight = 0m;

                if (block.SourceImage != null)
                {
                    using var ms = new MemoryStream(block.SourceImage);
                    using var img = System.Drawing.Image.FromStream(ms);
                    decimal aspect = (decimal)img.Width / Math.Max(1m, (decimal)img.Height);
                    sourceImgHeight = Math.Min(160m, drawWidth / aspect);
                }

                if (block.TargetImage != null)
                {
                    using var ms = new MemoryStream(block.TargetImage);
                    using var img = System.Drawing.Image.FromStream(ms);
                    decimal aspect = (decimal)img.Width / Math.Max(1m, (decimal)img.Height);
                    targetImgHeight = Math.Min(160m, drawWidth / aspect);
                }

                decimal maxImgHeight = Math.Max(sourceImgHeight, targetImgHeight);
                int cappedTextLen = Math.Min(900, Math.Max(block.OldText?.Length ?? 0, block.NewText?.Length ?? 0));
                decimal textEstimatedHeight = hasText
                    ? ((cappedTextLen / maxCharsCol + 3) * 14m + 55m)
                    : 40m;
                decimal estimatedHeight = textEstimatedHeight + (hasImages ? (maxImgHeight + 25m) : 0m);

                decimal requiredHeight = Math.Min(480m, estimatedHeight + (isFirstBlockOfDoc ? 35m : 0m));

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

                    _drawingService.DrawTextLines(page, $"Document: {doc.DocumentName} (SSIM: {doc.OverallSsimScore * 100:F1}%)", yPosition, margin, 90, 15, 23, 42, fontBold);

                    if (!string.IsNullOrEmpty(doc.ReportFileName))
                    {
                        _drawingService.DrawTextLines(page, $"-> Details: {doc.ReportFileName}", yPosition, margin + 420m, 55, 37, 99, 235, fontBold);
                    }

                    yPosition -= 30m;
                    isFirstBlockOfDoc = false;
                }

                byte r = 37, g = 99, b = 235;
                if (block.Severity == DiffSeverity.Critical) { r = 220; g = 38; b = 38; }
                else if (block.Type == ChangeType.Inserted) { r = 16; g = 185; b = 129; }
                else if (block.Type == ChangeType.Deleted) { r = 239; g = 68; b = 68; }

                string deltaSuffix = !string.IsNullOrWhiteSpace(block.FinancialDelta) ? $" [{block.FinancialDelta}]" : string.Empty;
                _drawingService.DrawTextLines(page, $"[Page {block.PageNumber}] {block.ChangeTypeLabel} - {block.Category} ({block.SeverityLabel}){deltaSuffix}", yPosition, margin, 110, r, g, b, fontBold);
                yPosition -= 15m;

                _drawingService.DrawTextLines(page, "Original Document (Source)", yPosition, leftColumnX, 50, 150, 150, 150, fontBold);
                _drawingService.DrawTextLines(page, "Modified Document (Target)", yPosition, rightColumnX, 50, 150, 150, 150, fontBold);
                yPosition -= 15m;

                if (hasText)
                {
                    var (leftChunks, rightChunks) = _inlineDiffService.GetInlineDiffChunks(block.OldText ?? string.Empty, block.NewText ?? string.Empty);

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

                    yPosition = Math.Min(currentYLeft, currentYRight) - 8m;
                }

                if (hasImages)
                {
                    if (yPosition - maxImgHeight < margin)
                    {
                        page = builder.AddPage(842, 595);
                        yPosition = 595m - margin;
                    }

                    if (block.SourceImage != null)
                    {
                        page.AddPng(block.SourceImage, new PdfRectangle((double)leftColumnX, (double)(yPosition - sourceImgHeight), (double)(leftColumnX + drawWidth), (double)yPosition));
                    }
                    else
                    {
                        _drawingService.DrawTextLines(page, "(No original visual)", yPosition - 20m, leftColumnX, 40, 180, 180, 180, font);
                    }

                    if (block.TargetImage != null)
                    {
                        page.AddPng(block.TargetImage, new PdfRectangle((double)rightColumnX, (double)(yPosition - targetImgHeight), (double)(rightColumnX + drawWidth), (double)yPosition));
                    }
                    else
                    {
                        _drawingService.DrawTextLines(page, "(Visual deleted)", yPosition - 20m, rightColumnX, 40, 180, 180, 180, font);
                    }

                    yPosition -= (maxImgHeight + 10m);
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

    public string ExportCsvAuditReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        Directory.CreateDirectory(outputDiffDir);
        string csvPath = Path.Combine(outputDiffDir, "Global_Audit_Report.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Document;Langue;Score SSIM (%);Page;Severite;Categorie;Action;Texte Attendu (Source);Texte Obtenu (Cible);Rapport Individuel");

        foreach (var doc in summaries.OrderByDescending(s => s.CriticalAlertsCount).ThenBy(s => s.DocumentName))
        {
            foreach (var b in doc.Blocks)
            {
                sb.Append(EscapeCsvField(doc.DocumentName)).Append(';')
                  .Append(EscapeCsvField(doc.Language)).Append(';')
                  .Append($"{doc.OverallSsimScore * 100.0:0.00}").Append(';')
                  .Append(b.PageNumber).Append(';')
                  .Append(EscapeCsvField(b.SeverityLabel)).Append(';')
                  .Append(EscapeCsvField(b.Category)).Append(';')
                  .Append(EscapeCsvField(b.ChangeTypeLabel)).Append(';')
                  .Append(EscapeCsvField(b.OldText)).Append(';')
                  .Append(EscapeCsvField(b.NewText)).Append(';')
                  .AppendLine(EscapeCsvField(doc.ReportFileName));
            }
        }

        // Écriture en UTF-8 avec BOM pour une ouverture directe dans Microsoft Excel avec les caractères accentués et le symbole €
        File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return csvPath;
    }

    private void ExportCiJsonSummary(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        string jsonPath = Path.Combine(outputDiffDir, "Global_CI_Summary.json");
        var payload = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            ProfileUsed = _configService?.ActiveProfile.ProfileName ?? "Default",
            ImpactedDocumentsCount = summaries.Count,
            TotalDifferencesCount = summaries.Sum(s => s.Blocks.Count),
            TotalCriticalCount = summaries.Sum(s => s.CriticalAlertsCount),
            Documents = summaries.Select(s => new
            {
                s.DocumentName,
                s.Language,
                SsimScorePercent = Math.Round(s.OverallSsimScore * 100.0, 2),
                s.CriticalAlertsCount,
                MaxSeverity = s.MaxSeverity.ToString(),
                s.ReportFileName,
                Differences = s.Blocks.Select(b => new
                {
                    b.PageNumber,
                    Type = b.ChangeTypeLabel,
                    Severity = b.SeverityLabel,
                    b.Category,
                    b.OldText,
                    b.NewText
                })
            })
        };

        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json, Encoding.UTF8);
    }

    private static string EscapeCsvField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        string cleaned = value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
        return $"\"{cleaned}\"";
    }

    private Regex GetActiveCriticalRegex()
    {
        string? pattern = _configService?.ActiveProfile.CriticalKeywordsRegex;
        if (_configService != null && !string.IsNullOrWhiteSpace(pattern))
        {
            return _configService.TryGetCompiledRegex(pattern, ignoreCase: true) ?? DefaultCriticalRegex();
        }
        return DefaultCriticalRegex();
    }

    private void DrawDashboardPage(
        PdfPageBuilder page, decimal startX, decimal startY, int totalChanges, int totalFiles,
        int wordBalance, int criticalAlerts, int totalInserts, int totalDeletes, int typeDates,
        int typeNumbers, int typeWords, Dictionary<string, int> languageFileCounts,
        Dictionary<string, int> languageDiffCounts, List<DocumentDiffSummary> topFiles,
        PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal currentY = startY;

        _drawingService.DrawTextLines(page, "GLOBAL SYNTHESIS REPORT", currentY, startX, 80, 0, 50, 150, fontBold);
        _drawingService.DrawTextLines(page, $"Generated on {DateTime.Now:dd/MM/yyyy at HH:mm} - Automated document comparison.", currentY - 18m, startX, 110, 100, 100, 100, font);

        currentY -= 65m;

        string balanceText = wordBalance > 0 ? $"+ {wordBalance} words" : $"{wordBalance} words";

        _drawingService.DrawStatBox(page, startX, currentY, "Impacted Files", totalFiles.ToString(), font, fontBold, 0, 50, 150);
        _drawingService.DrawStatBox(page, startX + 155m, currentY, "Total Differences", totalChanges.ToString(), font, fontBold, 0, 50, 150);
        _drawingService.DrawStatBox(page, startX + 310m, currentY, "Net Balance (Volume)", balanceText, font, fontBold, wordBalance < 0 ? (byte)220 : (byte)16, wordBalance < 0 ? (byte)20 : (byte)185, wordBalance < 0 ? (byte)20 : (byte)129);
        _drawingService.DrawStatBox(page, startX + 465m, currentY, "Sensitive Words", criticalAlerts.ToString(), font, fontBold, criticalAlerts > 0 ? (byte)255 : (byte)0, criticalAlerts > 0 ? (byte)140 : (byte)50, criticalAlerts > 0 ? (byte)0 : (byte)150);

        currentY -= 60m;

        if (totalChanges == 0)
        {
            _drawingService.DrawTextLines(page, "No differences detected during this session.", currentY, startX, 100, 100, 100, 100, font);
            return;
        }

        _drawingService.DrawTextLines(page, "Distribution by Action Type:", currentY, startX, 45, 0, 0, 0, fontBold);
        _drawingService.DrawTextLines(page, "Nature of Impacted Data:", currentY, startX + 260m, 45, 0, 0, 0, fontBold);
        _drawingService.DrawTextLines(page, "Modified Documents by Language:", currentY, startX + 520m, 45, 0, 0, 0, fontBold);

        currentY -= 15m;

        _chartService.DrawDashboardCharts(page, startX, currentY, totalInserts, totalDeletes, typeDates, typeNumbers, typeWords, languageFileCounts, font);

        currentY -= 210m;

        _drawingService.DrawTextLines(page, "Top 3 Most Modified Files:", currentY, startX, 60, 0, 0, 0, fontBold);
        _drawingService.DrawTextLines(page, "Differences Volume by Language:", currentY, startX + 400m, 60, 0, 0, 0, fontBold);

        decimal listY = currentY - 20m;
        foreach (var file in topFiles)
        {
            string displayName = file.DocumentName;
            if (displayName.Length > 70)
            {
                displayName = displayName.Substring(0, 57) + "...";
            }

            _drawingService.DrawTextLines(page, $"- {displayName}", listY, startX, 75, 80, 80, 80, font);
            _drawingService.DrawTextLines(page, $"{file.Blocks.Count} diffs", listY, startX + 350m, 25, 0, 50, 150, fontBold);
            listY -= 15m;
        }

        decimal langY = currentY - 20m;
        foreach (var kvp in languageDiffCounts.OrderByDescending(x => x.Value).Take(4))
        {
            _drawingService.DrawTextLines(page, $"- Documents [{kvp.Key}]", langY, startX + 400m, 40, 80, 80, 80, font);
            _drawingService.DrawTextLines(page, $"{kvp.Value} errors", langY, startX + 520m, 25, 0, 50, 150, fontBold);
            langY -= 15m;
        }
    }
}
