using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PDFComparison.Services;

public partial class PdfExtractionService
{
    private readonly IPdfWatermarkFilterService _watermarkFilter;
    private readonly IPdfIntelligentMaskingService _intelligentMasking;
    private readonly IPdfTextNormalizerService _textNormalizer;
    private readonly IConfigurationService? _configService;

    private static readonly IReadOnlyList<ExclusionZoneRule> FallbackZones = ComparisonProfile.CreateDefault().ExclusionZones;

    [GeneratedRegex(@"^(?:\(?\d{1,2}[./\-]\d{1,2}(?:[./\-]\d{2,4})?[.,;:)]?|0\d{3}\.\d{3}\.\d{3}[.,;:)]?)$")]
    private static partial Regex SplitDateOrVatRegex();

    private sealed class PageWordCandidate
    {
        public required string Text { get; set; }
        public required List<Letter> Letters { get; set; }
        public double Left { get; set; }
        public double Bottom { get; set; }
        public double Right { get; set; }
        public double Top { get; set; }
    }

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
    /// les micro-polices techniques et les filigranes), réconcilie les mots/dates coupés par PdfPig,
    /// et applique les masquages intelligents de manière 100% synchronisée entre texte et mots.
    /// </summary>
    public (string MaskedText, List<PdfWordInfo> Words, int ImagesCount, int ImagesHash) ExtractDocumentFullData(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) throw new ArgumentException("PDF path cannot be empty.", nameof(pdfPath));

        var words = new List<PdfWordInfo>();
        var headerDatesToIgnore = new HashSet<string>(StringComparer.Ordinal);

        var configuredZones = (IReadOnlyList<ExclusionZoneRule>?)Profile?.ExclusionZones ?? FallbackZones;

        using var document = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });
        int totalPages = document.NumberOfPages;

        foreach (var page in document.GetPages())
        {
            var activeZonesForPage = configuredZones
                .Where(z => z.AppliesToPage(page.Number, totalPages))
                .ToList();

            var rawCandidates = new List<PageWordCandidate>();

            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                if (IsHiddenOrOffPageWord(word, page.Width, page.Height)) continue;
                if (_watermarkFilter.IsWatermark(word)) continue;

                string cleanedWordText = _watermarkFilter.CleanRawText(word.Text);
                if (string.IsNullOrWhiteSpace(cleanedWordText)) continue;

                var box = word.BoundingBox;
                rawCandidates.Add(new PageWordCandidate
                {
                    Text = cleanedWordText,
                    Letters = word.Letters.ToList(),
                    Left = box.BottomLeft.X,
                    Bottom = box.BottomLeft.Y,
                    Right = box.TopRight.X,
                    Top = box.TopRight.Y
                });
            }

            // Ordonner visuellement (haut -> bas, gauche -> droite) et recoller les dates/tokens coupés par PdfPig
            var orderedPageWords = SortAndMergeSplitTokensOnPage(rawCandidates);

            foreach (var candidate in orderedPageWords)
            {
                bool isExcludedByZone = false;

                for (int i = 0; i < activeZonesForPage.Count; i++)
                {
                    var zone = activeZonesForPage[i];
                    if (zone.IntersectsPdfBox(
                            candidate.Left, candidate.Bottom,
                            candidate.Right, candidate.Top,
                            page.Width, page.Height))
                    {
                        if (zone.CollectDatesInZone && _intelligentMasking.IsDate(candidate.Text))
                        {
                            headerDatesToIgnore.Add(candidate.Text);
                        }
                        isExcludedByZone = true;
                        break;
                    }
                }

                if (isExcludedByZone) continue;

                words.Add(new PdfWordInfo
                {
                    Text = candidate.Text,
                    Letters = candidate.Letters,
                    PageNumber = page.Number
                });
            }
        }

        _intelligentMasking.MaskRepeatingWordElements(words, headerDatesToIgnore);
        string maskedText = BuildMaskedTextFromWords(words);

        return (maskedText, words, 0, 0);
    }

    private static List<PageWordCandidate> SortAndMergeSplitTokensOnPage(List<PageWordCandidate> rawCandidates)
    {
        if (rawCandidates.Count <= 1) return rawCandidates;

        var byYDesc = rawCandidates
            .OrderByDescending(w => w.Bottom)
            .ThenBy(w => w.Left)
            .ToList();

        var visualLines = new List<List<PageWordCandidate>>();
        foreach (var item in byYDesc)
        {
            List<PageWordCandidate>? targetLine = null;
            double bestDy = double.MaxValue;

            for (int i = visualLines.Count - 1; i >= Math.Max(0, visualLines.Count - 4); i--)
            {
                double lineRefY = visualLines[i][0].Bottom;
                double dy = Math.Abs(item.Bottom - lineRefY);
                if (dy <= 3.0 && dy < bestDy)
                {
                    bestDy = dy;
                    targetLine = visualLines[i];
                }
            }

            if (targetLine != null)
            {
                targetLine.Add(item);
            }
            else
            {
                visualLines.Add(new List<PageWordCandidate> { item });
            }
        }

        var result = new List<PageWordCandidate>(rawCandidates.Count);

        foreach (var line in visualLines.OrderByDescending(l => l[0].Bottom))
        {
            line.Sort((a, b) => a.Left.CompareTo(b.Left));

            PageWordCandidate? prev = null;
            for (int i = 0; i < line.Count; i++)
            {
                var curr = line[i];
                if (prev == null)
                {
                    prev = curr;
                    continue;
                }

                double dy = Math.Abs(curr.Bottom - prev.Bottom);

                // Ignorer un doublon exact superposé (faux-gras au niveau mot)
                if (dy <= 1.5 && Math.Abs(curr.Left - prev.Left) <= 0.8 &&
                    string.Equals(curr.Text, prev.Text, StringComparison.Ordinal))
                {
                    continue;
                }

                double gap = curr.Left - prev.Right;
                bool isTouchingFragment = dy <= 2.4 && gap >= -1.2 && gap <= 0.75;
                bool isSplitDateOrNumber = dy <= 2.4 && gap >= -1.2 && gap <= 2.6 &&
                                           IsSplitDateOrNumberPair(prev.Text, curr.Text);

                if (isTouchingFragment || isSplitDateOrNumber)
                {
                    prev.Text += curr.Text;
                    prev.Letters.AddRange(curr.Letters);
                    prev.Right = Math.Max(prev.Right, curr.Right);
                    prev.Top = Math.Max(prev.Top, curr.Top);
                    prev.Bottom = Math.Min(prev.Bottom, curr.Bottom);
                }
                else
                {
                    result.Add(prev);
                    prev = curr;
                }
            }

            if (prev != null)
            {
                result.Add(prev);
            }
        }

        return result;
    }

    private static bool IsSplitDateOrNumberPair(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

        bool hasDateSepBoundary =
            left.EndsWith('.') || left.EndsWith('/') ||
            right.StartsWith('.') || right.StartsWith('/');

        if (!hasDateSepBoundary) return false;
        if (!left.Any(char.IsDigit) || !right.Any(char.IsDigit)) return false;

        return SplitDateOrVatRegex().IsMatch(left + right);
    }

    private static string BuildMaskedTextFromWords(List<PdfWordInfo> words)
    {
        if (words.Count == 0) return string.Empty;

        var sb = new StringBuilder(words.Count * 7);
        var currentLine = new StringBuilder();
        int lastPage = -1;
        double lastY = double.NaN;

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (string.IsNullOrWhiteSpace(w.Text)) continue;

            double wordY = w.Letters != null && w.Letters.Count > 0
                ? w.Letters[0].Location.Y
                : lastY;

            if (lastPage != -1 && (w.PageNumber != lastPage || (!double.IsNaN(lastY) && !double.IsNaN(wordY) && Math.Abs(wordY - lastY) > 5.0)))
            {
                if (currentLine.Length > 0)
                {
                    sb.AppendLine(currentLine.ToString());
                    currentLine.Clear();
                }
            }

            if (currentLine.Length > 0)
            {
                currentLine.Append(' ');
            }

            currentLine.Append(w.Text);
            lastPage = w.PageNumber;
            if (!double.IsNaN(wordY))
            {
                lastY = wordY;
            }
        }

        if (currentLine.Length > 0)
        {
            sb.AppendLine(currentLine.ToString());
        }

        return sb.ToString();
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
