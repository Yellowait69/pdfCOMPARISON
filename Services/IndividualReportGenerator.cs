using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDFComparison.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace PDFComparison.Services;

public interface IIndividualReportGenerator
{
    void GenerateIndividualReport(string sourcePath, string targetPath, string reportPath, VisualHighlights highlights);
}

public class IndividualReportGenerator : IIndividualReportGenerator
{
    private readonly IPdfDrawingService _drawingService;


    private static readonly (byte R, byte G, byte B) ColorRedSource = (255, 99, 71);
    private static readonly (byte R, byte G, byte B) ColorGreenTarget = (50, 205, 50);

    public IndividualReportGenerator(IPdfDrawingService drawingService)
    {
        _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
    }

    public void GenerateIndividualReport(string sourcePath, string targetPath, string reportPath, VisualHighlights highlights)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Le chemin source est invalide.", nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Le chemin cible est invalide.", nameof(targetPath));
        if (string.IsNullOrWhiteSpace(reportPath)) throw new ArgumentException("Le chemin du rapport est invalide.", nameof(reportPath));
        if (highlights == null) throw new ArgumentNullException(nameof(highlights));

        string? directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new PdfDocumentBuilder();
        var (font, fontBold) = _drawingService.LoadFonts(builder);

        var sourceRedDict = GroupHighlightsByPage(highlights.SourceRed);
        var targetRedDict = GroupHighlightsByPage(highlights.TargetRed);

        using var sourceDoc = PdfDocument.Open(sourcePath, new ParsingOptions { ClipPaths = false });
        using var targetDoc = PdfDocument.Open(targetPath, new ParsingOptions { ClipPaths = false });

        int maxPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);

        for (int pageIndex = 1; pageIndex <= maxPages; pageIndex++)
        {
            if (pageIndex <= sourceDoc.NumberOfPages)
            {
                var sPage = builder.AddPage(sourceDoc, pageIndex);

                if (sourceRedDict.TryGetValue(pageIndex, out var sRed))
                    _drawingService.DrawDiffMarkup(sPage, sRed, ColorRedSource.R, ColorRedSource.G, ColorRedSource.B, MarkupStyle.Highlight);

                _drawingService.DrawPageStamp(sPage, "SOURCE", fontBold);
            }

            if (pageIndex <= targetDoc.NumberOfPages)
            {
                var tPage = builder.AddPage(targetDoc, pageIndex);

                if (targetRedDict.TryGetValue(pageIndex, out var tRed))
                    _drawingService.DrawDiffMarkup(tPage, tRed, ColorGreenTarget.R, ColorGreenTarget.G, ColorGreenTarget.B, MarkupStyle.Highlight);

                _drawingService.DrawPageStamp(tPage, "TARGET", fontBold);
            }
        }

        File.WriteAllBytes(reportPath, builder.Build());
    }

    /// <summary>
    /// Utilitaire qui convertit une liste plate de LetterLoc en un dictionnaire indexé par page.
    /// Accès en temps constant O(1) lors de la construction du rapport.
    /// </summary>
    private Dictionary<int, List<LetterLoc>> GroupHighlightsByPage(IEnumerable<LetterLoc> letters)
    {
        return letters
            .GroupBy(l => l.PageNumber)
            .ToDictionary(g => g.Key, g => g.ToList());
    }
}