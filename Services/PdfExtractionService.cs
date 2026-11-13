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

    private sealed class RawPageExtraction
    {
        public int PageNumber { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public required List<PageWordCandidate> RawCandidates { get; init; }
    }

    private static readonly string[] BottomCopyMentionPrefixes =
    {
        "exemplaires",
        "exemplaire",
        "exemplaren",
        "exemplaar",
        "exemplare",
        "exemplar",
        "ausfertigungen",
        "ausfertigung",
        "esemplari",
        "esemplare",
        "ejemplares",
        "ejemplar"
    };

    private static readonly HashSet<string> PrecedingProseArticlesOrCounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "1", "2", "3", "4", "5",
        "un", "une", "d'un", "d’un", "deux", "trois", "quatre", "en",
        "een", "twee", "drie", "vier", "in",
        "ein", "eine", "einen", "einem", "zwei", "drei",
        "one", "two", "three"
    };

    private static readonly HashSet<string> ContractualProseNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "conditions", "voorwaarden", "bedingungen", "terms",
        "contrat", "contrats", "overeenkomst", "vertrag",
        "règlement", "reglement", "clause", "clauses"
    };

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
    public (string MaskedText, List<PdfWordInfo> Words) ExtractDocumentData(string pdfPath, string? productName = null, string? channel = null)
    {
        var data = ExtractDocumentFullData(pdfPath, productName, channel);
        return (data.MaskedText, data.Words);
    }

    /// <summary>
    /// Extrait uniquement le texte visible à l'œil nu (ignore le texte hors-page, le texte blanc/invisible,
    /// les micro-polices techniques et les filigranes), réconcilie les mots/dates coupés par PdfPig,
    /// et applique les masquages intelligents de manière 100% synchronisée entre texte et mots.
    /// </summary>
    public (string MaskedText, List<PdfWordInfo> Words, int ImagesCount, int ImagesHash) ExtractDocumentFullData(
        string pdfPath,
        string? productName = null,
        string? channel = null)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) throw new ArgumentException("PDF path cannot be empty.", nameof(pdfPath));

        var words = new List<PdfWordInfo>();
        var headerDatesToIgnore = new HashSet<string>(StringComparer.Ordinal);

        var configuredZones = (IReadOnlyList<ExclusionZoneRule>?)Profile?.ExclusionZones ?? FallbackZones;

        int totalPages;
        List<RawPageExtraction> extractedPages;

        // Sérialiser uniquement la lecture bas niveau PdfPig (dont les caches internes de polices ne sont pas thread-safe)
        lock (PdfPigSync.Gate)
        {
            using var document = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });
            totalPages = document.NumberOfPages;
            extractedPages = new List<RawPageExtraction>(totalPages);

            foreach (var page in document.GetPages())
            {
                var rawCandidates = new List<PageWordCandidate>();

                foreach (var word in page.GetWords())
                {
                    if (string.IsNullOrWhiteSpace(word.Text)) continue;
                    if (IsHiddenOrOffPageWord(word, page.Width, page.Height)) continue;
                    if (_watermarkFilter.IsWatermark(word)) continue;

                    string cleanedWordText = _watermarkFilter.CleanRawText(word.Text);
                    if (string.IsNullOrWhiteSpace(cleanedWordText)) continue;

                    var box = word.BoundingBox;
                    var wordLetters = word.Letters.ToList();
                    for (int li = 0; li < wordLetters.Count; li++)
                    {
                        var let = wordLetters[li];
                        _ = let.GlyphRectangle;
                        _ = let.Location;
                        _ = let.PointSize;
                        _ = let.Value;
                        _ = let.Color;
                    }

                    rawCandidates.Add(new PageWordCandidate
                    {
                        Text = cleanedWordText,
                        Letters = wordLetters,
                        Left = box.Left,
                        Bottom = box.Bottom,
                        Right = box.Right,
                        Top = box.Top
                    });
                }

                extractedPages.Add(new RawPageExtraction
                {
                    PageNumber = page.Number,
                    Width = page.Width,
                    Height = page.Height,
                    RawCandidates = rawCandidates
                });
            }
        }

        bool stripBottomCopyMentions = Profile == null ||
            !Profile.RegexRules.Any(r => !r.IsUserRule && r.Pattern != null && r.Pattern.Contains("exemplaire", StringComparison.OrdinalIgnoreCase)) ||
            Profile.RegexRules.Any(r => r.IsEnabled && r.Pattern != null && r.Pattern.Contains("exemplaire", StringComparison.OrdinalIgnoreCase));

        // Traitement géométrique, filtrage des mentions "exemplaire(s)/exemplaar..." de bas de page et masquage en parallèle
        foreach (var pageData in extractedPages)
        {
            var activeZonesForPage = configuredZones
                .Where(z => z.AppliesToPage(pageData.PageNumber, totalPages) &&
                            z.AppliesToProduct(productName, channel, pdfPath, pdfPath))
                .ToList();

            // Ordonner visuellement (haut -> bas, gauche -> droite) et recoller les dates/tokens coupés par PdfPig
            var orderedPageWords = SortAndMergeSplitTokensOnPage(pageData.RawCandidates);

            // Retirer les mentions techniques multilingues de copie en bas de page (exemplaire..., exemplaires..., exemplaar..., etc.)
            // afin qu'une page en plus ou en moins dans Target ne génère jamais de fausse différence sur ces mentions
            if (stripBottomCopyMentions)
            {
                orderedPageWords = StripBottomCopyMentionsOnPage(orderedPageWords, pageData.Height);
            }

            foreach (var candidate in orderedPageWords)
            {
                bool isExcludedByZone = false;

                for (int i = 0; i < activeZonesForPage.Count; i++)
                {
                    var zone = activeZonesForPage[i];
                    if (zone.IntersectsPdfBox(
                            candidate.Left, candidate.Bottom,
                            candidate.Right, candidate.Top,
                            pageData.Width, pageData.Height))
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
                    PageNumber = pageData.PageNumber
                });
            }
        }

        _intelligentMasking.MaskRepeatingWordElements(words, headerDatesToIgnore, productName, channel, pdfPath);
        string maskedText = BuildMaskedTextFromWords(words);

        return (maskedText, words, 0, 0);
    }

    private static List<PageWordCandidate> StripBottomCopyMentionsOnPage(List<PageWordCandidate> orderedWords, double pageHeight)
    {
        if (orderedWords.Count == 0) return orderedWords;

        double bottomZoneMaxY = Math.Max(140.0, pageHeight * 0.17);

        // Regrouper les mots ordonnés par lignes visuelles
        var lines = new List<List<PageWordCandidate>>();
        foreach (var w in orderedWords)
        {
            if (lines.Count > 0 && Math.Abs(w.Bottom - lines[^1][0].Bottom) <= 3.5)
            {
                lines[^1].Add(w);
            }
            else
            {
                lines.Add(new List<PageWordCandidate> { w });
            }
        }

        bool anyRemoved = false;

        for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
        {
            var line = lines[lineIdx];
            if (line.Count == 0 || line[0].Bottom > bottomZoneMaxY) continue;

            for (int k = 0; k < line.Count; k++)
            {
                int remainingTokens = line.Count - k;
                if (remainingTokens > 9) continue;

                bool isStampStart = k == 0 ||
                                    (line[k].Left - line[k - 1].Right >= 16.0) ||
                                    line[k - 1].Text.EndsWith('.');
                if (!isStampStart) continue;

                if (!IsCopyMentionHeadToken(line[k].Text, remainingTokens)) continue;

                // Garde-fou : ne jamais tronquer une vraie phrase contractuelle coupée en fin de ligne ("Fait en 2\nexemplaires...", "un\nexemplaire des conditions...")
                string? prevTokenNorm = null;
                if (k > 0)
                {
                    prevTokenNorm = NormalizeAlphaNumericToken(line[k - 1].Text);
                }
                else if (lineIdx > 0 && lines[lineIdx - 1].Count > 0 &&
                         Math.Abs(lines[lineIdx - 1][0].Bottom - line[0].Bottom) <= 24.0)
                {
                    prevTokenNorm = NormalizeAlphaNumericToken(lines[lineIdx - 1][^1].Text);
                }

                if (!string.IsNullOrEmpty(prevTokenNorm) && PrecedingProseArticlesOrCounts.Contains(prevTokenNorm))
                {
                    continue;
                }

                bool hasContractualProseNoun = false;
                for (int m = k + 1; m < line.Count; m++)
                {
                    string tokNorm = NormalizeAlphaNumericToken(line[m].Text);
                    if (ContractualProseNouns.Contains(tokNorm))
                    {
                        hasContractualProseNoun = true;
                        break;
                    }
                }

                if (hasContractualProseNoun) continue;

                line.RemoveRange(k, line.Count - k);
                anyRemoved = true;
                break;
            }
        }

        if (!anyRemoved) return orderedWords;

        var filtered = new List<PageWordCandidate>(orderedWords.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Count > 0)
            {
                filtered.AddRange(lines[i]);
            }
        }
        return filtered;
    }

    private static bool IsCopyMentionHeadToken(string rawToken, int remainingTokens)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return false;

        string trimmed = rawToken.Trim().Trim('(', ')', '[', ']', '-', '–', '•', ':', ';', ',', '.');
        if (trimmed.Length < 4) return false;

        foreach (string prefix in BottomCopyMentionPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (trimmed.Length == prefix.Length) return true;
                char next = trimmed[prefix.Length];
                if (!char.IsLetter(next) || trimmed.Contains('/'))
                {
                    return true;
                }
            }
        }

        if ((trimmed.Equals("copie", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Equals("kopie", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Equals("copy", StringComparison.OrdinalIgnoreCase)) &&
            remainingTokens >= 2)
        {
            return true;
        }

        return false;
    }

    private static string NormalizeAlphaNumericToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Trim().Trim('.', ',', ';', ':', '(', ')', '[', ']', '"', '«', '»');
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
        double minX = box.Left;
        double maxX = box.Right;
        double minY = box.Bottom;
        double maxY = box.Top;

        // Ignorer le texte situé en dehors de la page visible (MediaBox)
        if (maxX < 0 || minX > pageWidth || maxY < 0 || minY > pageHeight)
        {
            return true;
        }

        // Ignorer les pistes techniques verticales (DataMatrix / AFP / codes d'éditique) tournées à 90°/270° dans les marges latérales
        if (word.TextOrientation != TextOrientation.Horizontal &&
            (maxX <= 28.0 || minX >= pageWidth - 24.0))
        {
            return true;
        }

        string text = word.Text.Trim();
        if (string.IsNullOrEmpty(text)) return true;

        var firstLetter = word.Letters[0];
        double minFontSize = Math.Max(1.8, Profile?.MinAllowedFontSizePt ?? 1.8);
        double glyphSpan = Math.Max(Math.Abs(firstLetter.GlyphRectangle.Width), Math.Abs(firstLetter.GlyphRectangle.Height));

        if (firstLetter.PointSize < minFontSize || glyphSpan <= 0.1)
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
