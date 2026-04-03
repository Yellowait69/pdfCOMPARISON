using System;
using System.Collections.Generic;
using System.Text;
using PDFComparison.Models;
using UglyToad.PdfPig;

namespace PDFComparison.Services;

public class PdfExtractionService
{
    private readonly IPdfWatermarkFilterService _watermarkFilter;
    private readonly IPdfIntelligentMaskingService _intelligentMasking;
    private readonly IPdfTextNormalizerService _textNormalizer;

    // OPTIMISATION : Sécurisation de l'injection de dépendances
    public PdfExtractionService(
        IPdfWatermarkFilterService watermarkFilter,
        IPdfIntelligentMaskingService intelligentMasking,
        IPdfTextNormalizerService textNormalizer)
    {
        _watermarkFilter = watermarkFilter ?? throw new ArgumentNullException(nameof(watermarkFilter));
        _intelligentMasking = intelligentMasking ?? throw new ArgumentNullException(nameof(intelligentMasking));
        _textNormalizer = textNormalizer ?? throw new ArgumentNullException(nameof(textNormalizer));
    }

    public string ExtractTextFast(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) throw new ArgumentException("PDF path cannot be empty.", nameof(pdfPath));

        var sb = new StringBuilder();
        using var document = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });

        foreach (var page in document.GetPages())
        {
            string cleanText = _watermarkFilter.CleanRawText(page.Text);
            sb.AppendLine(cleanText);
        }

        return _intelligentMasking.MaskRepeatingTextElements(sb.ToString());
    }

    public List<PdfWordInfo> ExtractWords(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) throw new ArgumentException("PDF path cannot be empty.", nameof(pdfPath));

        var words = new List<PdfWordInfo>();

        // OPTIMISATION : Comparaison ordinale beaucoup plus rapide
        var headerDatesToIgnore = new HashSet<string>(StringComparer.Ordinal);

        using var doc = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });

        // =========================================================================
        // OPTIMISATION CRITIQUE : "Single Pass" (Une seule boucle pour tout faire)
        // Réduit par 2 le temps de parcours du document par rapport à l'ancien code.
        // =========================================================================
        foreach (var page in doc.GetPages())
        {
            double headerThresholdY = page.Height - 50.0; // En-tête dynamique
            double footerThresholdY = 40.0;
            double leftMarginThresholdX = 50.0;

            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                // 1. GESTION DE L'EN-TÊTE
                if (word.BoundingBox.BottomLeft.Y > headerThresholdY)
                {
                    if (_intelligentMasking.IsDate(word.Text))
                    {
                        headerDatesToIgnore.Add(word.Text);
                    }
                    // Le mot est dans l'en-tête, on l'ignore pour la comparaison métier
                    continue;
                }

                // 2. GESTION DU PIED DE PAGE ET DE LA MARGE GAUCHE
                if (word.BoundingBox.BottomLeft.Y < footerThresholdY) continue;
                if (word.BoundingBox.BottomLeft.X < leftMarginThresholdX) continue;

                // 3. FILTRAGE ANTI-FILIGRANE (Effectué après le tri spatial pour gagner du temps)
                if (_watermarkFilter.IsWatermark(word)) continue;

                // 4. SAUVEGARDE DU MOT VALIDE
                words.Add(new PdfWordInfo
                {
                    Text = word.Text,
                    Letters = word.Letters,
                    PageNumber = page.Number
                });
            }
        }

        // ==========================================
        // MASQUAGE INTELLIGENT
        // ==========================================
        _intelligentMasking.MaskRepeatingWordElements(words, headerDatesToIgnore);

        return words;
    }

    // Proxy vers le service de normalisation pour la rétrocompatibilité
    // avec l'Orchestrateur Principal de l'application
    public string NormalizePdfText(string input)
    {
        return _textNormalizer.NormalizePdfText(input);
    }
}