using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PDFComparison.Models;

namespace PDFComparison.Services;

public interface IPdfLayoutSanitizerService
{
    string CleanLineForDiff(string input);
    List<List<(string CleanText, List<LetterLoc> Letters)>> GroupIntoLines(IReadOnlyList<PdfWordInfo> words);
}

public class PdfLayoutSanitizerService : IPdfLayoutSanitizerService
{
    private static string NormalizeLigaturesAndBullets(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var sb = new StringBuilder(input.Length + 5);
        foreach (char c in input)
        {
            switch (c)
            {
                case 'ﬁ': sb.Append("fi"); break;
                case 'ﬂ': sb.Append("fl"); break;
                case 'ﬀ': sb.Append("ff"); break;
                case 'ﬃ': sb.Append("ffi"); break;
                case 'ﬄ': sb.Append("ffl"); break;
                case 'ﬅ': case 'ﬆ': sb.Append("st"); break;
                case 'œ': sb.Append("oe"); break;
                case 'æ': sb.Append("ae"); break;
                case 'Œ': sb.Append("OE"); break;
                case 'Æ': sb.Append("AE"); break;
                case 'ĳ': sb.Append("ij"); break;
                case 'Ĳ': sb.Append("IJ"); break;

                case '•': case '·': case '▪': case '●': case '○': case '◦': case '\uF0A7': case '\uF0B7': case '\u2023': case '\u2043':
                    sb.Append(' '); break;

                case '–': case '—': case '−': case '\u2010': case '\u2011': case '\u2012':
                    sb.Append('-'); break;

                case '’': case '‘': case '´': case '`': case 'ʼ':
                    sb.Append('\''); break;
                case '“': case '”': case '«': case '»': case '„':
                    sb.Append('"'); break;

                default:
                    sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public string CleanLineForDiff(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string normalizedStr = NormalizeLigaturesAndBullets(input);
        var sb = new StringBuilder(normalizedStr.Length);

        foreach (char c in normalizedStr)
        {
            if (c == '\u00AD' || c == '\u200B' || c == '\u200C' || c == '\u200D' || c == '\uFEFF') continue;
            if (c == '\u00A0' || c == '\u202F' || c == '\u2009') sb.Append(' ');
            else sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormKC);
    }

    /// <summary>
    /// Normalise un mot à son cœur alphanumérique visible (lettres, chiffres, devises/%)
    /// afin d'éliminer 100 % des faux-positifs liés aux ponctuations détachées (":" vs " :"),
    /// séparateurs de milliers ("1.250,00" vs "1 250,00"), tirets de césure ou parenthèses collées.
    /// </summary>
    public static string CleanWord(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string normalizedStr = NormalizeLigaturesAndBullets(input).Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(normalizedStr.Length);

        foreach (char c in normalizedStr)
        {
            if (char.IsLetterOrDigit(c) || c == '€' || c == '$' || c == '£' || c == '%')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    public List<List<(string CleanText, List<LetterLoc> Letters)>> GroupIntoLines(IReadOnlyList<PdfWordInfo> words)
    {
        var list = new List<(string CleanText, List<LetterLoc> Letters)>(words.Count);

        foreach (var word in words)
        {
            if (word.Letters == null || word.Letters.Count == 0) continue;

            bool isMaskedToken = word.Text.StartsWith('[') && word.Text.EndsWith(']');
            if (isMaskedToken)
            {
                var maskedLocs = word.Letters
                    .Select(l => new LetterLoc(l.GlyphRectangle, word.PageNumber, (decimal)l.Location.Y, (decimal)l.PointSize))
                    .ToList();
                string maskedClean = CleanWord(word.Text);
                if (maskedLocs.Count > 0 && !string.IsNullOrEmpty(maskedClean))
                {
                    list.Add((maskedClean, maskedLocs));
                }
                continue;
            }

            // Découper les mots collés par deux-points ou point-virgule (ex: "Police:123456" -> "Police" + "123456")
            // tout en gardant intacts les nombres ("1.250,00"), dates ("10/11/2026") et références ("FBB_006-8459084-73")
            foreach (var subWordLetters in SplitCompoundWordLetters(word.Letters))
            {
                var locs = new List<LetterLoc>(subWordLetters.Count);
                var deduplicatedTextBuilder = new StringBuilder(subWordLetters.Count);
                string lastCleanedGlyph = string.Empty;

                foreach (var letter in subWordLetters)
                {
                    string rawGlyph = letter.Value ?? string.Empty;
                    string cleanedGlyph = CleanWord(rawGlyph);

                    var loc = new LetterLoc(letter.GlyphRectangle, word.PageNumber, (decimal)letter.Location.Y, (decimal)letter.PointSize);

                    if (locs.Count > 0)
                    {
                        var last = locs[^1];
                        decimal dupToleranceX = Math.Max(1.5m, loc.FontSize * 0.18m);
                        decimal dupToleranceY = Math.Max(1.2m, loc.FontSize * 0.18m);

                        // Filtre les glyphes doublés par effet "Faux-Gras" (double-frappe vectorielle au même endroit)
                        if (Math.Abs(last.BaselineY - loc.BaselineY) < dupToleranceY &&
                            Math.Abs((decimal)last.BoundingBox.BottomLeft.X - (decimal)loc.BoundingBox.BottomLeft.X) < dupToleranceX &&
                            string.Equals(lastCleanedGlyph, cleanedGlyph, StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }

                    locs.Add(loc);
                    if (!string.IsNullOrEmpty(cleanedGlyph))
                    {
                        lastCleanedGlyph = cleanedGlyph;
                        deduplicatedTextBuilder.Append(cleanedGlyph);
                    }
                }

                string cleanText = deduplicatedTextBuilder.ToString();

                // On ignore les jetons constitués uniquement de ponctuation isolée (".", ":", "-", "•")
                if (locs.Count > 0 && !string.IsNullOrEmpty(cleanText))
                {
                    list.Add((cleanText, locs));
                }
            }
        }

        var linesList = new List<List<(string CleanText, List<LetterLoc> Letters)>>();
        var wordsByPage = list.GroupBy(w => w.Letters[0].PageNumber).OrderBy(g => g.Key);

        foreach (var page in wordsByPage)
        {
            var pageWords = page.OrderByDescending(w => w.Letters[0].BaselineY).ToList();
            GroupWordsSubsetIntoLines(pageWords, linesList);
        }

        return linesList;
    }

    /// <summary>
    /// Scinde un mot PDF lorsque deux unités lexicales distinctes sont collées par ':', ';', '(' ou ')'
    /// (ex: "Montant:1250" -> "Montant" et "1250"), sans jamais couper les heures ("14:30"), les dates ou les montants.
    /// </summary>
    private static List<List<UglyToad.PdfPig.Content.Letter>> SplitCompoundWordLetters(IReadOnlyList<UglyToad.PdfPig.Content.Letter> letters)
    {
        var result = new List<List<UglyToad.PdfPig.Content.Letter>>();
        if (letters == null || letters.Count == 0) return result;

        var current = new List<UglyToad.PdfPig.Content.Letter>(letters.Count);

        for (int i = 0; i < letters.Count; i++)
        {
            var l = letters[i];
            string val = l.Value ?? string.Empty;

            bool isSplitSeparator = false;
            if (val is ":" or ";" or "(" or ")" or "[" or "]")
            {
                // Ne pas couper une heure comme "14:30" (chiffre avant et chiffre après ':')
                bool prevIsDigit = i > 0 && !string.IsNullOrEmpty(letters[i - 1].Value) && char.IsDigit(letters[i - 1].Value[0]);
                bool nextIsDigit = i + 1 < letters.Count && !string.IsNullOrEmpty(letters[i + 1].Value) && char.IsDigit(letters[i + 1].Value[0]);

                if (!(val == ":" && prevIsDigit && nextIsDigit))
                {
                    isSplitSeparator = true;
                }
            }

            if (isSplitSeparator)
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                    current = new List<UglyToad.PdfPig.Content.Letter>();
                }
            }
            else
            {
                current.Add(l);
            }
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static void GroupWordsSubsetIntoLines(
        List<(string CleanText, List<LetterLoc> Letters)> wordsSubset,
        List<List<(string CleanText, List<LetterLoc> Letters)>> linesList)
    {
        if (wordsSubset.Count == 0) return;

        var lines = new List<(decimal ReferenceY, List<(string CleanText, List<LetterLoc> Letters)> Words)>();

        foreach (var word in wordsSubset)
        {
            var firstLetter = word.Letters[0];
            decimal wordY = firstLetter.BaselineY;
            decimal lineTolerance = Math.Max(4.5m, firstLetter.FontSize * 0.48m);

            int bestLineIdx = -1;
            decimal smallestDeltaY = decimal.MaxValue;

            for (int i = 0; i < lines.Count; i++)
            {
                decimal deltaY = Math.Abs(lines[i].ReferenceY - wordY);
                if (deltaY < lineTolerance && deltaY < smallestDeltaY)
                {
                    smallestDeltaY = deltaY;
                    bestLineIdx = i;
                }
            }

            if (bestLineIdx == -1)
            {
                lines.Add((wordY, new List<(string CleanText, List<LetterLoc> Letters)> { word }));
            }
            else
            {
                var targetLine = lines[bestLineIdx];
                int currentCount = targetLine.Words.Count;
                decimal updatedRefY = ((targetLine.ReferenceY * currentCount) + wordY) / (currentCount + 1);
                targetLine.Words.Add(word);
                lines[bestLineIdx] = (updatedRefY, targetLine.Words);
            }
        }

        foreach (var lineEntry in lines.OrderByDescending(l => l.ReferenceY))
        {
            var sortedWords = lineEntry.Words
                .OrderBy(w => w.Letters[0].BoundingBox.BottomLeft.X)
                .ToList();

            var deduplicatedWords = DeduplicateOverlappingWordsInLine(sortedWords);
            var mergedLineWords = MergeKerningAndSplitNumbersInLine(deduplicatedWords);

            if (mergedLineWords.Count > 0)
            {
                linesList.Add(mergedLineWords);
            }
        }
    }

    /// <summary>
    /// Supprime les mots en doublon superposés exactement aux mêmes coordonnées (effet gras / ombrage PDF).
    /// </summary>
    private static List<(string CleanText, List<LetterLoc> Letters)> DeduplicateOverlappingWordsInLine(
        List<(string CleanText, List<LetterLoc> Letters)> sortedWords)
    {
        if (sortedWords.Count <= 1) return sortedWords;

        var result = new List<(string CleanText, List<LetterLoc> Letters)>(sortedWords.Count) { sortedWords[0] };

        for (int i = 1; i < sortedWords.Count; i++)
        {
            var prev = result[^1];
            var curr = sortedWords[i];

            decimal prevLeft = (decimal)prev.Letters[0].BoundingBox.BottomLeft.X;
            decimal currLeft = (decimal)curr.Letters[0].BoundingBox.BottomLeft.X;
            decimal toleranceX = Math.Max(3.0m, prev.Letters[0].FontSize * 0.35m);

            if (Math.Abs(currLeft - prevLeft) <= toleranceX &&
                string.Equals(prev.CleanText, curr.CleanText, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(curr);
        }

        return result;
    }

    /// <summary>
    /// Fusionne les fragments d'un même mot coupés par le kerning PDF (écart &lt;= 0.22 * taille police)
    /// ainsi que les nombres séparés par un espace de milliers ("1" + "250,00" -> "125000").
    /// </summary>
    private static List<(string CleanText, List<LetterLoc> Letters)> MergeKerningAndSplitNumbersInLine(
        List<(string CleanText, List<LetterLoc> Letters)> lineWords)
    {
        if (lineWords.Count <= 1) return lineWords;

        var result = new List<(string CleanText, List<LetterLoc> Letters)>(lineWords.Count) { lineWords[0] };

        for (int i = 1; i < lineWords.Count; i++)
        {
            var prev = result[^1];
            var curr = lineWords[i];

            decimal prevRightX = (decimal)prev.Letters[^1].BoundingBox.TopRight.X;
            decimal currLeftX = (decimal)curr.Letters[0].BoundingBox.BottomLeft.X;
            decimal gap = currLeftX - prevRightX;
            decimal fontSize = prev.Letters[^1].FontSize;

            decimal kerningMergeGap = Math.Max(1.8m, fontSize * 0.22m);
            decimal thousandNumberGap = Math.Max(5.0m, fontSize * 0.70m);

            bool isKerningSplit = gap >= -1.5m && gap <= kerningMergeGap;
            bool isSplitNumber = gap >= -1.5m && gap <= thousandNumberGap &&
                                 AllDigits(prev.CleanText) && AllDigitsOrCurrency(curr.CleanText);

            if (isKerningSplit || isSplitNumber)
            {
                var combinedLocs = new List<LetterLoc>(prev.Letters.Count + curr.Letters.Count);
                combinedLocs.AddRange(prev.Letters);
                combinedLocs.AddRange(curr.Letters);
                result[^1] = (prev.CleanText + curr.CleanText, combinedLocs);
            }
            else
            {
                result.Add(curr);
            }
        }

        return result;
    }

    private static bool AllDigits(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 12) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i])) return false;
        }
        return true;
    }

    private static bool AllDigitsOrCurrency(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsDigit(c) && c != '€' && c != '$' && c != '£' && c != '%')
                return false;
        }
        return true;
    }
}
