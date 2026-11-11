using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly IConfigurationService? _configService;

    private static readonly IReadOnlyList<ExclusionZoneRule> FallbackZones = ComparisonProfile.CreateDefault().ExclusionZones;

    public PdfExtractionService(
        IPdfWatermarkFilterService watermarkFilter,
        IPdfIntelligentMaskingService intelligentMasking,
        IPdfTextNormalizerService textNormalizer,
        IConfigurationService? configService = null)
    {
        _watermarkFilter = watermarkFilter ?? throw new ArgumentNullException(nameof(watermarkFilter));
        _intelligentMasking = intelligentMasking ?? throw new ArgumentNullException(nameof(intelligentMasking));
        _textNormalizer = textNormalizer ?? throw new ArgumentNullException(nameof(textNormalizer));
        _configService = configService;
    }

    private ComparisonProfile? Profile => _configService?.ActiveProfile;

    /// <summary>
    /// Extrait en une seule passe I/O le texte masqué et la liste des mots visibles avec leurs coordonnées.
    /// </summary>
    public (string MaskedText, List<PdfWordInfo> Words) ExtractDocumentData(string pdfPath)
    {
        var data = ExtractDocumentFullData(pdfPath);
        return (data.MaskedText, data.Words);
    }

    /// <summary>
    /// Extrait uniquement le texte visible à l'œil nu (ignore le texte hors-page, le texte blanc/invisible,
    /// les micro-polices techniques et les filigranes).
    /// </summary>
    public (string MaskedText, List<PdfWordInfo> Words, int ImagesCount, int ImagesHash) ExtractDocumentFullData(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) throw new ArgumentException("PDF path cannot be empty.", nameof(pdfPath));

        var sb = new StringBuilder();
        var words = new List<PdfWordInfo>();
        var headerDatesToIgnore = new HashSet<string>(StringComparer.Ordinal);

        var configuredZones = (IReadOnlyList<ExclusionZoneRule>?)Profile?.ExclusionZones ?? FallbackZones;

        using var document = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });
        int totalPages = document.NumberOfPages;

        foreach (var page in document.GetPages())
        {
            var currentLine = new StringBuilder();
            double lastY = -1;
            double lastX = -1;

            var activeZonesForPage = configuredZones
                .Where(z => z.AppliesToPage(page.Number, totalPages))
                .ToList();

            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                if (IsHiddenOrOffPageWord(word, page.Width, page.Height)) continue;

                bool isExcludedByZone = false;
                var box = word.BoundingBox;

                for (int i = 0; i < activeZonesForPage.Count; i++)
                {
                    var zone = activeZonesForPage[i];
                    if (zone.IntersectsPdfBox(
                            box.BottomLeft.X, box.BottomLeft.Y,
                            box.TopRight.X, box.TopRight.Y,
                            page.Width, page.Height))
                    {
                        if (zone.CollectDatesInZone && _intelligentMasking.IsDate(word.Text))
                        {
                            headerDatesToIgnore.Add(word.Text);
                        }
                        isExcludedByZone = true;
                        break;
                    }
                }

                if (isExcludedByZone) continue;
                if (_watermarkFilter.IsWatermark(word)) continue;

                string cleanedWordText = _watermarkFilter.CleanRawText(word.Text);
                if (string.IsNullOrWhiteSpace(cleanedWordText)) continue;

                words.Add(new PdfWordInfo
                {
                    Text = cleanedWordText,
                    Letters = word.Letters,
                    PageNumber = page.Number
                });

                if (lastY != -1 && Math.Abs(box.BottomLeft.Y - lastY) > 5.0)
                {
                    string cleanedLine = _watermarkFilter.CleanRawText(currentLine.ToString().TrimEnd());
                    if (!string.IsNullOrWhiteSpace(cleanedLine))
                    {
                        sb.AppendLine(cleanedLine);
                    }
                    currentLine.Clear();
                    lastX = -1;
                }

                if (lastX != -1)
                {
                    double distance = box.BottomLeft.X - lastX;
                    if (distance > 2.0)
                    {
                        currentLine.Append(' ');
                    }
                }

                currentLine.Append(cleanedWordText);

                lastX = box.TopRight.X;
                lastY = box.BottomLeft.Y;
            }

            if (currentLine.Length > 0)
            {
                string cleanedLine = _watermarkFilter.CleanRawText(currentLine.ToString().TrimEnd());
                if (!string.IsNullOrWhiteSpace(cleanedLine))
                {
                    sb.AppendLine(cleanedLine);
                }
            }
        }

        string maskedText = _intelligentMasking.MaskRepeatingTextElements(sb.ToString(), headerDatesToIgnore);
        _intelligentMasking.MaskRepeatingWordElements(words, headerDatesToIgnore);

        return (maskedText, words, 0, 0);
    }

    public string ExtractTextFast(string pdfPath)
    {
        return ExtractDocumentData(pdfPath).MaskedText;
    }

    public List<PdfWordInfo> ExtractWords(string pdfPath)
    {
        return ExtractDocumentData(pdfPath).Words;
    }

    private bool IsHiddenOrOffPageWord(Word word, double pageWidth, double pageHeight)
    {
        if (word.Letters.Count == 0) return true;

        var box = word.BoundingBox;
        // Ignorer le texte situé en dehors de la page visible (MediaBox)
        if (box.TopRight.X < 0 || box.BottomLeft.X > pageWidth ||
            box.TopRight.Y < 0 || box.BottomLeft.Y > pageHeight)
        {
            return true;
        }

        string text = word.Text.Trim();
        if (string.IsNullOrEmpty(text)) return true;

        var firstLetter = word.Letters[0];
        double minFontSize = Math.Max(1.8, Profile?.MinAllowedFontSizePt ?? 1.8);

        if (firstLetter.PointSize < minFontSize || firstLetter.GlyphRectangle.Width <= 0.1)
        {
            return true;
        }

        if ((Profile?.IgnoreWhiteOnWhiteText ?? true) && firstLetter.Color != null)
        {
            var (r, g, b) = firstLetter.Color.ToRGBValues();
            // Ignorer le texte blanc ou quasi-blanc invisible à l'œil nu sur fond blanc
            if (r >= 0.94 && g >= 0.94 && b >= 0.94)
            {
                return true;
            }
        }

        return false;
    }

    public string NormalizePdfText(string input)
    {
        return _textNormalizer.NormalizePdfText(input);
    }
}
