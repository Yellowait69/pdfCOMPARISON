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

        // 3. Génération du Rapport PDF Global de Synthèse (Regroupé par Code Produit, sans doublons)
        string reportPath = Path.Combine(outputDiffDir, "Global_Synthesis_Report.pdf");

        var productGroups = ProductDifferenceGroupingService.GroupSummariesByProduct(summaries);
        if (productGroups.Count == 0) return;

        var builder = new PdfDocumentBuilder();
        var (font, fontBold) = _drawingService.LoadFonts(builder);

        int totalProducts = productGroups.Count;
        int totalDocs = productGroups.Sum(g => g.ImpactedDocsCount);
        int totalUniqueChanges = productGroups.Sum(g => g.UniqueDifferences.Count);
        int criticalAlerts = productGroups.Sum(g => g.CriticalUniqueDiffsCount);

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
            "SYNTHESE DES DIFFERENCES REGROUPEES PAR CODE PRODUIT",
            yPos - 10m,
            margin + 12m,
            95,
            255, 255, 255,
            fontBold);

        string statsSummary = $"Genere le {DateTime.Now:dd/MM/yyyy a HH:mm}  |  Produits : {totalProducts}  |  Differences uniques : {totalUniqueChanges}  |  PDF impactes : {totalDocs}  |  Critiques : {criticalAlerts}";
        _drawingService.DrawTextLines(
            page,
            statsSummary,
            yPos - 25m,
            margin + 12m,
            135,
            148, 163, 184,
            font);

        yPos -= 50m;

        decimal colMetaX = margin + 4m;
        decimal colSourceX = 220m;
        decimal colTargetX = 520m;
        int maxCharsMeta = 30;
        int maxCharsTextCol = 45;

        foreach (var prodGroup in productGroups)
        {
            if (yPos < margin + 85m)
            {
                page = builder.AddPage((double)pageWidth, (double)pageHeight);
                yPos = pageHeight - margin;
            }

            // En-tête du Code Produit + entêtes de colonnes
            page.SetTextAndFillColor(226, 232, 240);
            page.DrawRectangle(new PdfPoint((double)margin, (double)(yPos - 16m)), pageWidth - (margin * 2m), 20m, 0m, true);

            string prodHeader = $"CODE PRODUIT : {prodGroup.ProductGroupKey}   ({prodGroup.UniqueDifferences.Count} difference(s) unique(s) sur {prodGroup.ImpactedDocsCount} PDF)";
            _drawingService.DrawTextLines(page, prodHeader, yPos - 3m, colMetaX, 115, 15, 23, 42, fontBold);
            yPos -= 24m;

            _drawingService.DrawTextLines(page, "PAGE / REGROUPEMENT", yPos, colMetaX, 32, 100, 116, 139, fontBold);
            _drawingService.DrawTextLines(page, "ATTENDU (SOURCE)", yPos, colSourceX, 40, 185, 28, 28, fontBold);
            _drawingService.DrawTextLines(page, "OBTENU (TARGET)", yPos, colTargetX, 40, 5, 150, 105, fontBold);
            yPos -= 14m;

            foreach (var diffItem in prodGroup.UniqueDifferences)
            {
                var block = diffItem.Block;
                string oldTxt = string.IsNullOrWhiteSpace(block.OldText) ? "(Absent)" : block.OldText.Replace("\r\n", " | ").Replace("\n", " | ");
                string newTxt = string.IsNullOrWhiteSpace(block.NewText) ? "(Supprime)" : block.NewText.Replace("\r\n", " | ").Replace("\n", " | ");

                int estLines = Math.Max(1, Math.Max(oldTxt.Length, newTxt.Length) / maxCharsTextCol + 1);
                decimal rowHeight = Math.Max(36m, estLines * 13m + 24m);

                if (yPos - rowHeight < margin)
                {
                    page = builder.AddPage((double)pageWidth, (double)pageHeight);
                    yPos = pageHeight - margin;

                    _drawingService.DrawTextLines(page, $"CODE PRODUIT (suite) : {prodGroup.ProductGroupKey}", yPos, colMetaX, 115, 15, 23, 42, fontBold);
                    yPos -= 18m;
                }

                string deltaInfo = !string.IsNullOrWhiteSpace(block.FinancialDelta) ? $" ({block.FinancialDelta})" : string.Empty;
                string metaLine = $"{diffItem.PagesCompactLabel} - {block.Category}{deltaInfo}";

                decimal yMeta = _drawingService.DrawTextLines(page, metaLine, yPos, colMetaX, maxCharsMeta, 30, 58, 138, fontBold);

                string occLabel = diffItem.ImpactedDocs.Count > 1
                    ? $"Regroupe : {diffItem.ImpactedDocs.Count} PDF identiques"
                    : "1 PDF concerne";
                yMeta = _drawingService.DrawTextLines(page, occLabel, yMeta, colMetaX, maxCharsMeta, 37, 99, 235, fontBold);

                if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                {
                    yMeta = _drawingService.DrawTextLines(page, block.ContextBefore, yMeta, colMetaX, maxCharsMeta, 120, 130, 145, font);
                }

                decimal yLeft = _drawingService.DrawTextLines(page, oldTxt, yPos, colSourceX, maxCharsTextCol, 185, 28, 28, fontBold);
                decimal yRight = _drawingService.DrawTextLines(page, newTxt, yPos, colTargetX, maxCharsTextCol, 5, 150, 105, fontBold);

                yPos = Math.Min(yMeta, Math.Min(yLeft, yRight)) - 2m;

                // Liste compacte des PDF regroupés sous la différence unique
                int docCount = diffItem.ImpactedDocs.Count;
                string docsLine = docCount > 1
                    ? $"PDF regroupes ({docCount}) : " + string.Join(", ", diffItem.ImpactedDocs.Take(4).Select(d => d.DisplayFileName)) +
                      (docCount > 4 ? $" (+{docCount - 4} autre(s))" : string.Empty)
                    : $"Document : {diffItem.ImpactedDocs.FirstOrDefault()?.DisplayFileName ?? string.Empty}";

                yPos = _drawingService.DrawTextLines(page, docsLine, yPos, colMetaX, 122, 100, 116, 139, font);
                yPos -= 2m;

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

        var productGroups = ProductDifferenceGroupingService.GroupSummariesByProduct(summaries);

        var sb = new StringBuilder();
        sb.AppendLine("Canal / Produit;Nb PDF Regroupes;Pages;Severite;Categorie;Ecart;Repere / Contexte;Attendu (Source);Obtenu (Target);Documents Regroupes;Rapport Exemple");

        foreach (var prod in productGroups)
        {
            foreach (var item in prod.UniqueDifferences)
            {
                var b = item.Block;
                string shortCtx = !string.IsNullOrWhiteSpace(b.ContextBefore) ? b.ContextBefore : b.ContextAfter;
                string docsJoined = string.Join(" | ", item.ImpactedDocs.Select(d => d.DisplayFileName));
                string sampleReport = item.ImpactedDocs.FirstOrDefault()?.ReportFileName ?? string.Empty;

                sb.Append(EscapeCsvField(prod.ProductGroupKey)).Append(';')
                  .Append(item.ImpactedDocs.Count).Append(';')
                  .Append(EscapeCsvField(item.PagesCompactLabel)).Append(';')
                  .Append(EscapeCsvField(b.SeverityLabel)).Append(';')
                  .Append(EscapeCsvField(b.Category)).Append(';')
                  .Append(EscapeCsvField(b.FinancialDelta)).Append(';')
                  .Append(EscapeCsvField(shortCtx)).Append(';')
                  .Append(EscapeCsvField(b.OldText)).Append(';')
                  .Append(EscapeCsvField(b.NewText)).Append(';')
                  .Append(EscapeCsvField(docsJoined)).Append(';')
                  .AppendLine(EscapeCsvField(sampleReport));
            }
        }

        File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return csvPath;
    }

    private void ExportCiJsonSummary(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        string jsonPath = Path.Combine(outputDiffDir, "Global_CI_Summary.json");
        var productGroups = ProductDifferenceGroupingService.GroupSummariesByProduct(summaries);
        var payload = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            ProfileUsed = _configService?.ActiveProfile.ProfileName ?? "Default",
            ImpactedProductsCount = productGroups.Count,
            ImpactedDocumentsCount = productGroups.Sum(g => g.ImpactedDocsCount),
            TotalUniqueDifferencesCount = productGroups.Sum(g => g.UniqueDifferences.Count),
            TotalCriticalUniqueCount = productGroups.Sum(g => g.CriticalUniqueDiffsCount),
            Products = productGroups.Select(p => new
            {
                p.ProductGroupKey,
                p.Channel,
                p.ProductName,
                p.ImpactedDocsCount,
                p.CriticalUniqueDiffsCount,
                UniqueDifferences = p.UniqueDifferences.Select(u => new
                {
                    Pages = u.PagesCompactLabel,
                    ImpactedPdfCount = u.ImpactedDocs.Count,
                    Type = u.Block.ChangeTypeLabel,
                    Severity = u.Block.SeverityLabel,
                    u.Block.Category,
                    u.Block.FinancialDelta,
                    Context = !string.IsNullOrWhiteSpace(u.Block.ContextBefore) ? u.Block.ContextBefore : u.Block.ContextAfter,
                    u.Block.OldText,
                    u.Block.NewText,
                    ImpactedDocuments = u.ImpactedDocs.Select(d => d.DisplayFileName)
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
