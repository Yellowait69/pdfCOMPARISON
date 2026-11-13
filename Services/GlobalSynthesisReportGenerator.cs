using System;
using System.Collections.Generic;
using System.IO;
using PDFComparison.Models;

namespace PDFComparison.Services;

public interface IGlobalSynthesisReportGenerator
{
    void GenerateGlobalSynthesisReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir);
}

public class GlobalSynthesisReportGenerator : IGlobalSynthesisReportGenerator
{
    private readonly IHtmlSynthesisReportGenerator _htmlReportGenerator;

    public GlobalSynthesisReportGenerator(
        IPdfDrawingService drawingService,
        IPdfChartService chartService,
        IInlineDiffService inlineDiffService,
        IConfigurationService? configService = null,
        IHtmlSynthesisReportGenerator? htmlReportGenerator = null)
    {
        _ = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
        _ = chartService ?? throw new ArgumentNullException(nameof(chartService));
        ArgumentNullException.ThrowIfNull(inlineDiffService);
        _ = configService;
        _htmlReportGenerator = htmlReportGenerator ?? new HtmlSynthesisReportGenerator(inlineDiffService);
    }

    public void GenerateGlobalSynthesisReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        if (string.IsNullOrWhiteSpace(outputDiffDir)) return;

        Directory.CreateDirectory(outputDiffDir);

        // Supprimer les anciens exports redondants (PDF de synthèse / CSV / JSON) s'ils existaient d'une session précédente
        TryDeleteLegacyFile(Path.Combine(outputDiffDir, "Global_Synthesis_Report.pdf"));
        TryDeleteLegacyFile(Path.Combine(outputDiffDir, "Global_Audit_Report.csv"));
        TryDeleteLegacyFile(Path.Combine(outputDiffDir, "Global_CI_Summary.json"));

        // Générer l'unique Synthèse Web interactive (même si 0 différence pour afficher l'état 100% conforme)
        _htmlReportGenerator.GenerateInteractiveHtmlReport(
            summaries ?? Array.Empty<DocumentDiffSummary>(),
            outputDiffDir);
    }

    private static void TryDeleteLegacyFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Ignorer si un ancien fichier est ouvert dans une autre application
        }
    }
}
