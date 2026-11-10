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
    void GenerateIndividualReport(
        string sourcePath,
        string targetPath,
        string reportPath,
        VisualHighlights highlights,
        IReadOnlyList<VisualDiffRegion>? visualRegions = null);
}

public class IndividualReportGenerator : IIndividualReportGenerator
{
    private readonly IPdfDrawingService _drawingService;

    private static readonly (byte R, byte G, byte B) ColorRedSource = (239, 68, 68);
    private static readonly (byte R, byte G, byte B) ColorGreenTarget = (16, 185, 129);
    private static readonly (byte R, byte G, byte B) ColorPurpleVisual = (168, 85, 247);

    public IndividualReportGenerator(IPdfDrawingService drawingService)
    {
        _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
    }

    public void GenerateIndividualReport(
        string sourcePath,
        string targetPath,
        string reportPath,
        VisualHighlights highlights,
        IReadOnlyList<VisualDiffRegion>? visualRegions = null)
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
        var visualRegionsDict = (visualRegions ?? Array.Empty<VisualDiffRegion>())
            .GroupBy(r => r.PageNumber)
            .ToDictionary(g => g.Key, g => g.ToList());

        using var sourceDoc = PdfDocument.Open(sourcePath, new ParsingOptions { ClipPaths = false });
        using var targetDoc = PdfDocument.Open(targetPath, new ParsingOptions { ClipPaths = false });

        int maxPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);

        for (int pageIndex = 1; pageIndex <= maxPages; pageIndex++)
        {
            visualRegionsDict.TryGetValue(pageIndex, out var pageVisuals);
            int visualCount = pageVisuals?.Count ?? 0;

            if (pageIndex <= sourceDoc.NumberOfPages)
            {
                var sPage = builder.AddPage(sourceDoc, pageIndex);
                int sTextCount = 0;

                if (sourceRedDict.TryGetValue(pageIndex, out var sRed) && sRed.Count > 0)
                {
                    sTextCount = VisualSegmentHelper.CountBlocks(VisualSegmentHelper.GetSegments(sRed));
                    _drawingService.DrawDiffMarkup(sPage, sRed, ColorRedSource.R, ColorRedSource.G, ColorRedSource.B, MarkupStyle.Highlight);
                }

                if (pageVisuals != null && pageVisuals.Count > 0)
                {
                    _drawingService.DrawVisualDiffRegions(sPage, pageVisuals, ColorPurpleVisual.R, ColorPurpleVisual.G, ColorPurpleVisual.B, fontBold);
                }

                _drawingService.DrawPageStatusHeader(sPage, "ATTENDU (SOURCE)", pageIndex, sourceDoc.NumberOfPages, sTextCount, visualCount, font, fontBold);
            }

            if (pageIndex <= targetDoc.NumberOfPages)
            {
                var tPage = builder.AddPage(targetDoc, pageIndex);
                int tTextCount = 0;

                if (targetRedDict.TryGetValue(pageIndex, out var tRed) && tRed.Count > 0)
                {
                    tTextCount = VisualSegmentHelper.CountBlocks(VisualSegmentHelper.GetSegments(tRed));
                    _drawingService.DrawDiffMarkup(tPage, tRed, ColorGreenTarget.R, ColorGreenTarget.G, ColorGreenTarget.B, MarkupStyle.Highlight);
                }

                if (pageVisuals != null && pageVisuals.Count > 0)
                {
                    _drawingService.DrawVisualDiffRegions(tPage, pageVisuals, ColorPurpleVisual.R, ColorPurpleVisual.G, ColorPurpleVisual.B, fontBold);
                }

                _drawingService.DrawPageStatusHeader(tPage, "OBTENU (CIBLE)", pageIndex, targetDoc.NumberOfPages, tTextCount, visualCount, font, fontBold);
            }
        }

        File.WriteAllBytes(reportPath, builder.Build());
    }

    private Dictionary<int, List<LetterLoc>> GroupHighlightsByPage(IEnumerable<LetterLoc> letters)
    {
        return letters
            .GroupBy(l => l.PageNumber)
            .ToDictionary(g => g.Key, g => g.ToList());
    }
}
