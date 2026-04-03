using System;
using System.Collections.Generic;
using System.Text;
using PDFComparison.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content; // Requis pour accéder à la classe Word (Nécessaire pour analyser la couleur)

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
            // Reconstruction du texte ligne par ligne pour exclure les mots invisibles
            // tout en préservant les retours à la ligne
            var currentLine = new StringBuilder();
            double lastY = -1;

            foreach (var word in page.GetWords())
            {
                // FILTRE CRITIQUE : Ignorer les mots blancs ou invisibles
                if (IsHiddenOrWhiteWord(word)) continue;

                // Si la différence de hauteur Y est > 5 points, c'est une nouvelle ligne
                if (lastY != -1 && Math.Abs(word.BoundingBox.BottomLeft.Y - lastY) > 5.0)
                {
                    sb.AppendLine(_watermarkFilter.CleanRawText(currentLine.ToString().TrimEnd()));
                    currentLine.Clear();
                }

                currentLine.Append(word.Text).Append(' ');
                lastY = word.BoundingBox.BottomLeft.Y;
            }

            // Ajouter la dernière ligne de la page
            if (currentLine.Length > 0)
            {
                sb.AppendLine(_watermarkFilter.CleanRawText(currentLine.ToString().TrimEnd()));
            }
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
        // =========================================================================
        foreach (var page in doc.GetPages())
        {
            double headerThresholdY = page.Height - 50.0; // En-tête dynamique
            double footerThresholdY = 40.0;
            double leftMarginThresholdX = 50.0;

            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                // FILTRE CRITIQUE : Ignorer totalement les mots blancs ou invisibles
                if (IsHiddenOrWhiteWord(word)) continue;

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

    // =========================================================================
    // NOUVELLE MÉTHODE : Détecte les artefacts OCR (texte invisible) ou blanc
    // =========================================================================
    private bool IsHiddenOrWhiteWord(Word word)
    {
        if (word.Letters.Count == 0) return false;

        // Optimisation : vérifier la première lettre est suffisant pour déduire l'état du mot entier
        var firstLetter = word.Letters[0];

        // 1. Mode de rendu invisible (Fréquent pour les calques de texte OCR cachés)
        // L'enum TextRenderingMode.NeitherFillNorStroke correspond généralement à la valeur 3.
        if ((int)firstLetter.TextRenderingMode == 3)
        {
            return true;
        }

        // 2. Texte écrit en blanc pur (Police blanche sur fond blanc)
        // On vérifie la représentation textuelle de la couleur pour être résilient
        // face aux différents espaces colorimétriques de la librairie (RGB, CMYK, Gray).
        var colorStr = firstLetter.FillColor?.ToString() ?? string.Empty;

        if (colorStr.Contains("(1, 1, 1)") ||       // Blanc en espace RGB
            colorStr.Contains("Gray: 1") ||         // Blanc en espace Niveaux de gris
            colorStr.Contains("(0, 0, 0, 0)"))      // Blanc en espace CMYK
        {
            return true;
        }

        return false;
    }

    // Proxy vers le service de normalisation pour la rétrocompatibilité
    // avec l'Orchestrateur Principal de l'application
    public string NormalizePdfText(string input)
    {
        return _textNormalizer.NormalizePdfText(input);
    }
}