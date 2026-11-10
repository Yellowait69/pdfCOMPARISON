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
    /// Extrait en une seule passe I/O le texte masqué et la liste des mots avec leurs coordonnées.
    /// </summary>
    public (string MaskedText, List<PdfWordInfo> Words) ExtractDocumentData(string pdfPath)
    {
        var data = ExtractDocumentFullData(pdfPath);
        return (data.MaskedText, data.Words);
    }

    /// <summary>
    /// Extrait en une seule passe I/O le texte masqué, les mots avec coordonnées, ainsi que le nombre d'images
    /// et leur empreinte rapide (pour détecter les PDF scannés et les changements de logos/signatures même à texte identique).
    /// </summary>
    public (string MaskedText, List<PdfWordInfo> Words, int ImagesCount, int ImagesHash) ExtractDocumentFullData(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) throw new ArgumentException("PDF path cannot be empty.", nameof(pdfPath));

        var sb = new StringBuilder();
        var words = new List<PdfWordInfo>();
        var headerDatesToIgnore = new HashSet<string>(StringComparer.Ordinal);
        int imagesCount = 0;
        int imagesHash = 17;

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

            try
            {
                foreach (var img in page.GetImages())
                {
                    var b = img.Bounds;
                    bool imgExcluded = false;
                    for (int i = 0; i < activeZonesForPage.Count; i++)
                    {
                        if (activeZonesForPage[i].IntersectsPdfBox(
                                b.BottomLeft.X, b.BottomLeft.Y,
                                b.TopRight.X, b.TopRight.Y,
                                page.Width, page.Height))
                        {
                            imgExcluded = true;
                            break;
                        }
                    }

                    if (imgExcluded) continue;

                    imagesCount++;
                    int rawLen = img.RawBytes.Count;
                    imagesHash = HashCode.Combine(imagesHash, page.Number, (int)Math.Round(b.Width), (int)Math.Round(b.Height), rawLen);

                    if (rawLen > 0)
                    {
                        var span = img.RawBytes;
                        int step = Math.Max(1, span.Count / 16);
                        for (int k = 0; k < span.Count; k += step)
                        {
                            imagesHash = HashCode.Combine(imagesHash, span[k]);
                        }
                    }
                }
            }
            catch
            {
                // Ignorer les flux d'images non standard
            }

            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                if (IsHiddenOrWhiteWord(word)) continue;

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

                words.Add(new PdfWordInfo
                {
                    Text = word.Text,
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

                currentLine.Append(word.Text);

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

        return (maskedText, words, imagesCount, imagesHash);
    }

    public string ExtractTextFast(string pdfPath)
    {
        return ExtractDocumentData(pdfPath).MaskedText;
    }

    public List<PdfWordInfo> ExtractWords(string pdfPath)
    {
        return ExtractDocumentData(pdfPath).Words;
    }

    private bool IsHiddenOrWhiteWord(Word word)
    {
        if (word.Letters.Count == 0) return false;

        string text = word.Text.Trim();
        if (text == "." || text == "," || text == "-" || text == "'") return false;

        var firstLetter = word.Letters[0];
        double minFontSize = Profile?.MinAllowedFontSizePt ?? 1.0;

        if (firstLetter.PointSize <= minFontSize || firstLetter.GlyphRectangle.Width <= 0.1)
        {
            return true;
        }

        if ((Profile?.IgnoreWhiteOnWhiteText ?? true) && firstLetter.Color != null)
        {
            var (r, g, b) = firstLetter.Color.ToRGBValues();
            if (r >= 0.98 && g >= 0.98 && b >= 0.98)
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
