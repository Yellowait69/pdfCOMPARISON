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

    // NOUVELLES COULEURS : Des teintes vives mais adaptées au surlignage
    private static readonly (byte R, byte G, byte B) ColorRedSource = (255, 99, 71);    // Rouge Tomate (Suppressions)
    private static readonly (byte R, byte G, byte B) ColorOrange = (255, 165, 0);       // Orange (Modifications)
    private static readonly (byte R, byte G, byte B) ColorGreenTarget = (50, 205, 50);  // Vert Lime (Ajouts)

    public IndividualReportGenerator(IPdfDrawingService drawingService)
    {
        _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
    }

    public void GenerateIndividualReport(string sourcePath, string targetPath, string reportPath, VisualHighlights highlights)
    {
        // 1. Validation stricte des entrées
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Le chemin source est invalide.", nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Le chemin cible est invalide.", nameof(targetPath));
        if (string.IsNullOrWhiteSpace(reportPath)) throw new ArgumentException("Le chemin du rapport est invalide.", nameof(reportPath));
        if (highlights == null) throw new ArgumentNullException(nameof(highlights));

        // 2. Préparation du dossier de destination
        string? directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new PdfDocumentBuilder();
        var (font, fontBold) = _drawingService.LoadFonts(builder);

        // =========================================================================
        // 3. OPTIMISATION MAJEURE (Gain de perf x10 sur les gros documents)
        // Grouper les surbrillances par numéro de page en amont avec des dictionnaires (O(N)).
        // Cela évite de rescanner toute la liste de mots pour chaque page du document.
        // =========================================================================
        var sourceRedDict = GroupHighlightsByPage(highlights.SourceRed);
        var sourceYellowDict = GroupHighlightsByPage(highlights.SourceYellow);
        var targetRedDict = GroupHighlightsByPage(highlights.TargetRed);
        var targetYellowDict = GroupHighlightsByPage(highlights.TargetYellow);

        // 4. Ouverture des documents avec ClipPaths désactivé (Plus de stabilité pour PdfPig)
        using var sourceDoc = PdfDocument.Open(sourcePath, new ParsingOptions { ClipPaths = false });
        using var targetDoc = PdfDocument.Open(targetPath, new ParsingOptions { ClipPaths = false });

        int maxPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);

        for (int pageIndex = 1; pageIndex <= maxPages; pageIndex++)
        {
            // === TRAITEMENT DE LA PAGE DU DOCUMENT SOURCE ===
            if (pageIndex <= sourceDoc.NumberOfPages)
            {
                var sPage = builder.AddPage(sourceDoc, pageIndex);

                if (sourceRedDict.TryGetValue(pageIndex, out var sRed))
                    _drawingService.DrawDiffMarkup(sPage, sRed, ColorRedSource.R, ColorRedSource.G, ColorRedSource.B, MarkupStyle.Highlight);

                if (sourceYellowDict.TryGetValue(pageIndex, out var sYellow))
                    _drawingService.DrawDiffMarkup(sPage, sYellow, ColorOrange.R, ColorOrange.G, ColorOrange.B, MarkupStyle.Highlight);

                _drawingService.DrawPageStamp(sPage, $"[ DOCUMENT SOURCE - Page {pageIndex} ]", fontBold);
            }

            // === TRAITEMENT DE LA PAGE DU DOCUMENT CIBLE ===
            if (pageIndex <= targetDoc.NumberOfPages)
            {
                var tPage = builder.AddPage(targetDoc, pageIndex);

                if (targetRedDict.TryGetValue(pageIndex, out var tRed))
                    _drawingService.DrawDiffMarkup(tPage, tRed, ColorGreenTarget.R, ColorGreenTarget.G, ColorGreenTarget.B, MarkupStyle.Highlight);

                if (targetYellowDict.TryGetValue(pageIndex, out var tYellow))
                    _drawingService.DrawDiffMarkup(tPage, tYellow, ColorOrange.R, ColorOrange.G, ColorOrange.B, MarkupStyle.Highlight);

                _drawingService.DrawPageStamp(tPage, $"[ DOCUMENT CIBLE (Modifié) - Page {pageIndex} ]", fontBold);
            }
        }

        // 5. Sauvegarde finale
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