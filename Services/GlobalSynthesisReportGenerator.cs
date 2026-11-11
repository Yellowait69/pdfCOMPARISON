using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

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

        // 1. Génération du Rapport Web HTML5 Interactif ultra-compact
        try
        {
            _htmlReportGenerator.GenerateInteractiveHtmlReport(summaries, outputDiffDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HTML Report generation warning: {ex.Message}");
        }

        // 2. Génération de l'Export Audit CSV/Excel et du résumé JSON
        try
        {
            ExportCsvAuditReport(summaries, outputDiffDir);
            ExportCiJsonSummary(summaries, outputDiffDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CSV/JSON Export warning: {ex.Message}");
        }

        // 3. Génération du Rapport PDF Global de Synthèse (Format Court, Direct & Tabulaire dès la Page 1)
        string reportPath = Path.Combine(outputDiffDir, "Global_Synthesis_Report.pdf");

        var builder = new PdfDocumentBuilder();
        var (font, fontBold) = _drawingService.LoadFonts(builder);

        var impactedDocs = summaries
            .Where(s => s.Blocks.Count > 0)
            .OrderByDescending(s => s.CriticalAlertsCount)
            .ThenBy(s =>
            {
                var match = FileSuffixNumberRegex().Match(s.DocumentName ?? string.Empty);
                return match.Success && int.TryParse(match.Groups[1].Value, out int num) ? num : int.MaxValue;
            })
            .ThenBy(s => s.DocumentName)
            .ToList();

        int totalDocs = impactedDocs.Count;
        int totalChanges = impactedDocs.Sum(s => s.Blocks.Count);
        int criticalAlerts = impactedDocs.Sum(s => s.Blocks.Count(b => b.Severity == DiffSeverity.Critical));

        decimal pageWidth = 842m;
        decimal pageHeight = 595m;
        decimal margin = 32m;

        PdfPageBuilder page = builder.AddPage((double)pageWidth, (double)pageHeight);
        decimal yPos = pageHeight - margin;

        // Bandeau d'en-tête compact en haut de la Page 1
        page.SetTextAndFillColor(15, 23, 42);
        page.DrawRectangle(new PdfPoint((double)margin, (double)(yPos - 34m)), pageWidth - (margin * 2m), 38m, 0m, true);

        _drawingService.DrawTextLines(
            page,
            "SYNTHESE DIRECTE DES DIFFERENCES DE TEXTE",
            yPos - 10m,
            margin + 12m,
            90,
            255, 255, 255,
            fontBold);

        string statsSummary = $"Genere le {DateTime.Now:dd/MM/yyyy a HH:mm}   |   Documents avec ecarts : {totalDocs}   |   Differences : {totalChanges}   |   Critiques : {criticalAlerts}";
        _drawingService.DrawTextLines(
            page,
            statsSummary,
            yPos - 25m,
            margin + 12m,
            120,
            148, 163, 184,
            font);

        yPos -= 50m;

        decimal colMetaX = margin + 4m;
        decimal colSourceX = 210m;
        decimal colTargetX = 515m;
        int maxCharsMeta = 28;
        int maxCharsTextCol = 46;

        foreach (var doc in impactedDocs)
        {
            if (yPos < margin + 75m)
            {
                page = builder.AddPage((double)pageWidth, (double)pageHeight);
                yPos = pageHeight - margin;
            }

            // En-tête du document + entêtes de colonnes
            page.SetTextAndFillColor(226, 232, 240);
            page.DrawRectangle(new PdfPoint((double)margin, (double)(yPos - 16m)), pageWidth - (margin * 2m), 20m, 0m, true);

            string docHeader = $"DOCUMENT : {doc.DocumentName}   ({doc.Blocks.Count} difference(s))";
            _drawingService.DrawTextLines(page, docHeader, yPos - 3m, colMetaX, 110, 15, 23, 42, fontBold);
            yPos -= 24m;

            _drawingService.DrawTextLines(page, "PAGE / CATEGORIE", yPos, colMetaX, 30, 100, 116, 139, fontBold);
            _drawingService.DrawTextLines(page, "ATTENDU (SOURCE)", yPos, colSourceX, 40, 185, 28, 28, fontBold);
            _drawingService.DrawTextLines(page, "OBTENU (TARGET)", yPos, colTargetX, 40, 5, 150, 105, fontBold);
            yPos -= 14m;

            foreach (var block in doc.Blocks)
            {
                string oldTxt = string.IsNullOrWhiteSpace(block.OldText) ? "(Absent)" : block.OldText.Replace("\r\n", " | ").Replace("\n", " | ");
                string newTxt = string.IsNullOrWhiteSpace(block.NewText) ? "(Supprime)" : block.NewText.Replace("\r\n", " | ").Replace("\n", " | ");

                int estLines = Math.Max(1, Math.Max(oldTxt.Length, newTxt.Length) / maxCharsTextCol + 1);
                decimal rowHeight = Math.Max(22m, estLines * 12m + 8m);

                if (yPos - rowHeight < margin)
                {
                    page = builder.AddPage((double)pageWidth, (double)pageHeight);
                    yPos = pageHeight - margin;

                    _drawingService.DrawTextLines(page, $"DOCUMENT (suite) : {doc.DocumentName}", yPos, colMetaX, 110, 15, 23, 42, fontBold);
                    yPos -= 18m;
                }

                string deltaInfo = !string.IsNullOrWhiteSpace(block.FinancialDelta) ? $" ({block.FinancialDelta})" : string.Empty;
                string metaLine = $"P.{block.PageNumber} - {block.Category}{deltaInfo}";

                decimal yMeta = _drawingService.DrawTextLines(page, metaLine, yPos, colMetaX, maxCharsMeta, 30, 58, 138, fontBold);
                if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                {
                    yMeta = _drawingService.DrawTextLines(page, block.ContextBefore, yMeta, colMetaX, maxCharsMeta, 120, 130, 145, font);
                }

                decimal yLeft = _drawingService.DrawTextLines(page, oldTxt, yPos, colSourceX, maxCharsTextCol, 185, 28, 28, fontBold);
                decimal yRight = _drawingService.DrawTextLines(page, newTxt, yPos, colTargetX, maxCharsTextCol, 5, 150, 105, fontBold);

                yPos = Math.Min(yMeta, Math.Min(yLeft, yRight)) - 4m;

                page.SetStrokeColor(226, 232, 240);
                page.DrawLine(new PdfPoint((double)margin, (double)(yPos + 2m)), new PdfPoint((double)(pageWidth - margin), (double)(yPos + 2m)), 0.6m);
                yPos -= 6m;
            }

            yPos -= 8m;
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
        sb.AppendLine("Document;Canal / Produit;Page;Severite;Categorie;Ecart;Repere / Contexte;Attendu (Source);Obtenu (Target);Rapport Individuel");

        foreach (var doc in summaries.Where(s => s.Blocks.Count > 0).OrderByDescending(s => s.CriticalAlertsCount).ThenBy(s => s.DocumentName))
        {
            foreach (var b in doc.Blocks)
            {
                string shortCtx = !string.IsNullOrWhiteSpace(b.ContextBefore) ? b.ContextBefore : b.ContextAfter;
                sb.Append(EscapeCsvField(doc.DocumentName)).Append(';')
                  .Append(EscapeCsvField(doc.Language)).Append(';')
                  .Append(b.PageNumber).Append(';')
                  .Append(EscapeCsvField(b.SeverityLabel)).Append(';')
                  .Append(EscapeCsvField(b.Category)).Append(';')
                  .Append(EscapeCsvField(b.FinancialDelta)).Append(';')
                  .Append(EscapeCsvField(shortCtx)).Append(';')
                  .Append(EscapeCsvField(b.OldText)).Append(';')
                  .Append(EscapeCsvField(b.NewText)).Append(';')
                  .AppendLine(EscapeCsvField(doc.ReportFileName));
            }
        }

        File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return csvPath;
    }

    private void ExportCiJsonSummary(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        string jsonPath = Path.Combine(outputDiffDir, "Global_CI_Summary.json");
        var impacted = summaries.Where(s => s.Blocks.Count > 0).ToList();
        var payload = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            ProfileUsed = _configService?.ActiveProfile.ProfileName ?? "Default",
            ImpactedDocumentsCount = impacted.Count,
            TotalDifferencesCount = impacted.Sum(s => s.Blocks.Count),
            TotalCriticalCount = impacted.Sum(s => s.CriticalAlertsCount),
            Documents = impacted.Select(s => new
            {
                s.DocumentName,
                s.Language,
                s.CriticalAlertsCount,
                MaxSeverity = s.MaxSeverity.ToString(),
                s.ReportFileName,
                Differences = s.Blocks.Select(b => new
                {
                    b.PageNumber,
                    Type = b.ChangeTypeLabel,
                    Severity = b.SeverityLabel,
                    b.Category,
                    b.FinancialDelta,
                    Context = !string.IsNullOrWhiteSpace(b.ContextBefore) ? b.ContextBefore : b.ContextAfter,
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
}
