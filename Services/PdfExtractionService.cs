using System;
using System.Collections.Generic;
using System.Text;
using PDFComparison.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PDFComparison.Services;

public class PdfExtractionService
{
    private readonly IPdfWatermarkFilterService _watermarkFilter;
    private readonly IPdfIntelligentMaskingService _intelligentMasking;
    private readonly IPdfTextNormalizerService _textNormalizer;

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
            var currentLine = new StringBuilder();
            double lastY = -1;
            double lastX = -1; // AJOUT : Mémorise la coordonnée X de la fin du mot précédent

            foreach (var word in page.GetWords())
            {
                // FILTRE CRITIQUE : Ignorer les mots invisibles (Artefacts OCR)
                if (IsHiddenOrWhiteWord(word)) continue;

                // Changement de ligne détecté
                if (lastY != -1 && Math.Abs(word.BoundingBox.BottomLeft.Y - lastY) > 5.0)
                {
                    sb.AppendLine(_watermarkFilter.CleanRawText(currentLine.ToString().TrimEnd()));
                    currentLine.Clear();
                    lastX = -1; // Réinitialiser l'axe X pour la nouvelle ligne
                }

                // AJOUT : Calcul de la distance avec le mot précédent pour ajouter ou non un espace
                if (lastX != -1)
                {
                    double distance = word.BoundingBox.BottomLeft.X - lastX;

                    // Si l'écart horizontal est suffisant (> 2.0 points), on insère un espace.
                    // Sinon, on considère que le texte est "collé" (ex: "13", ".", "01")
                    if (distance > 2.0)
                    {
                        currentLine.Append(' ');
                    }
                }

                currentLine.Append(word.Text);

                // Mémoriser la position X de la fin de ce mot et le Y actuel
                lastX = word.BoundingBox.TopRight.X;
                lastY = word.BoundingBox.BottomLeft.Y;
            }

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
        var headerDatesToIgnore = new HashSet<string>(StringComparer.Ordinal);

        using var doc = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });

        foreach (var page in doc.GetPages())
        {
            double headerThresholdY = page.Height - 50.0;
            double footerThresholdY = 40.0;
            double leftMarginThresholdX = 50.0;

            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                // FILTRE CRITIQUE : Ignorer les mots invisibles (Artefacts OCR)
                if (IsHiddenOrWhiteWord(word)) continue;

                if (word.BoundingBox.BottomLeft.Y > headerThresholdY)
                {
                    if (_intelligentMasking.IsDate(word.Text))
                    {
                        headerDatesToIgnore.Add(word.Text);
                    }
                    continue;
                }

                if (word.BoundingBox.BottomLeft.Y < footerThresholdY) continue;
                if (word.BoundingBox.BottomLeft.X < leftMarginThresholdX) continue;
                if (_watermarkFilter.IsWatermark(word)) continue;

                words.Add(new PdfWordInfo
                {
                    Text = word.Text,
                    Letters = word.Letters,
                    PageNumber = page.Number
                });
            }
        }

        _intelligentMasking.MaskRepeatingWordElements(words, headerDatesToIgnore);

        return words;
    }

    private bool IsHiddenOrWhiteWord(Word word)
    {
        if (word.Letters.Count == 0) return false;

        // AJOUT : Sauvegarder la ponctuation, même si sa largeur est minuscule !
        // Cela empêche le filtre anti-artefact de supprimer les points dans les dates "13.01.2025"
        string text = word.Text.Trim();
        if (text == "." || text == "," || text == "-" || text == "'") return false;

        var firstLetter = word.Letters[0];

        // =========================================================================
        // 1. APPROCHE RÉTROCOMPATIBLE (Anciennes versions de PdfPig)
        // Permet de compiler sans erreur CS1061.
        // Les artefacts OCR cachés sont très souvent réduits à une taille microscopique.
        // =========================================================================
        if (firstLetter.PointSize <= 1.0 || firstLetter.GlyphRectangle.Width <= 0.1)
        {
            return true;
        }

        // =========================================================================
        // 2. APPROCHE AVANCÉE (Couleurs et Rendu)
        // [!] À DÉCOMMENTER uniquement si vous mettez à jour votre package NuGet
        //     UglyToad.PdfPig vers la version 0.1.8 ou supérieure.
        // =========================================================================
        /*
        // Mode rendu invisible (TextRenderingMode = 3)
        if ((int)firstLetter.TextRenderingMode == 3)
        {
            return true;
        }

        // Textes écrits en blanc sur fond blanc
        var colorStr = firstLetter.FillColor?.ToString() ?? string.Empty;
        if (colorStr.Contains("(1, 1, 1)") || colorStr.Contains("Gray: 1") || colorStr.Contains("(0, 0, 0, 0)"))
        {
            return true;
        }
        */

        return false;
    }

    public string NormalizePdfText(string input)
    {
        return _textNormalizer.NormalizePdfText(input);
    }
}