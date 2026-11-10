using System;
using System.Collections.Generic;
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
    private string NormalizeLigaturesAndBullets(string input)
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
                case 'œ': sb.Append("oe"); break;
                case 'æ': sb.Append("ae"); break;
                case 'Œ': sb.Append("OE"); break;
                case 'Æ': sb.Append("AE"); break;

                case '•': case '·': case '▪': case '●': case '○': case '\uF0A7': case '\u2023': case '\u2043':
                    sb.Append('-'); break;

                case '–': case '—': case '−':
                    sb.Append('-'); break;

                case '’': case '‘': case '´': case '`':
                    sb.Append('\''); break;
                case '“': case '”': case '«': case '»':
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
            if (c == '\u00AD') continue;
            if (c == '\u00A0' || c == '\u202F' || c == '\u2009') sb.Append(' ');
            else sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormKC);
    }

    private string CleanWord(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string normalizedStr = NormalizeLigaturesAndBullets(input);
        var sb = new StringBuilder(normalizedStr.Length);

        foreach (char c in normalizedStr)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) continue;
            if (c == '\u00A0' || c == '\u202F' || c == '\u2009' || c == '\u200B' || c == '\u200C' || c == '\u200D' || c == '\uFEFF' || c == '\u00AD') continue;

            char finalChar = c;
            if (finalChar == ',') finalChar = '.';

            sb.Append(char.ToLowerInvariant(finalChar));
        }

        return sb.ToString().Normalize(NormalizationForm.FormKC);
    }

    public List<List<(string CleanText, List<LetterLoc> Letters)>> GroupIntoLines(IReadOnlyList<PdfWordInfo> words)
    {
        var list = new List<(string CleanText, List<LetterLoc> Letters)>(words.Count);

        foreach (var word in words)
        {
            if (word.Letters == null || word.Letters.Count == 0) continue;

            bool isMaskedToken = word.Text.StartsWith('[') && word.Text.EndsWith(']');

            var locs = new List<LetterLoc>(word.Letters.Count);
            var deduplicatedTextBuilder = new StringBuilder(word.Letters.Count);
            string lastCleanedGlyph = string.Empty;

            foreach (var letter in word.Letters)
            {
                string cleanedGlyph = CleanWord(letter.Value);
                if (string.IsNullOrEmpty(cleanedGlyph)) continue;

                var loc = new LetterLoc(letter.GlyphRectangle, word.PageNumber, (decimal)letter.Location.Y, (decimal)letter.PointSize);

                if (locs.Count > 0)
                {
                    var last = locs[^1];
                    decimal dupToleranceX = Math.Max(1.2m, loc.FontSize * 0.15m);
                    decimal dupToleranceY = Math.Max(1.0m, loc.FontSize * 0.15m);

                    // Filtre les glyphes doublés par effet "Faux-Gras" (double-frappe vectorielle)
                    if (Math.Abs(last.BaselineY - loc.BaselineY) < dupToleranceY &&
                        Math.Abs((decimal)last.BoundingBox.BottomLeft.X - (decimal)loc.BoundingBox.BottomLeft.X) < dupToleranceX &&
                        string.Equals(lastCleanedGlyph, cleanedGlyph, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                locs.Add(loc);
                lastCleanedGlyph = cleanedGlyph;
                if (!isMaskedToken)
                {
                    deduplicatedTextBuilder.Append(cleanedGlyph);
                }
            }

            string cleanText = isMaskedToken ? CleanWord(word.Text) : deduplicatedTextBuilder.ToString();
            if (string.IsNullOrEmpty(cleanText))
            {
                cleanText = CleanWord(word.Text);
            }

            if (locs.Count > 0 && !string.IsNullOrEmpty(cleanText))
            {
                list.Add((cleanText, locs));
            }
        }

        var linesList = new List<List<(string CleanText, List<LetterLoc> Letters)>>();
        var wordsByPage = list.GroupBy(w => w.Letters[0].PageNumber).OrderBy(g => g.Key);

        foreach (var page in wordsByPage)
        {
            var pageWords = page.OrderByDescending(w => w.Letters[0].BaselineY).ToList();

            if (IsTwoColumnProsePage(pageWords, out decimal gutterX))
            {
                var topWords = new List<(string CleanText, List<LetterLoc> Letters)>();
                var leftColWords = new List<(string CleanText, List<LetterLoc> Letters)>();
                var rightColWords = new List<(string CleanText, List<LetterLoc> Letters)>();
                var bottomWords = new List<(string CleanText, List<LetterLoc> Letters)>();

                foreach (var w in pageWords)
                {
                    decimal y = w.Letters[0].BaselineY;
                    decimal minX = (decimal)w.Letters[0].BoundingBox.BottomLeft.X;
                    decimal maxX = (decimal)w.Letters[^1].BoundingBox.TopRight.X;

                    if (y > 730m)
                        topWords.Add(w);
                    else if (y < 90m)
                        bottomWords.Add(w);
                    else if (maxX <= gutterX + 4m)
                        leftColWords.Add(w);
                    else if (minX >= gutterX - 4m)
                        rightColWords.Add(w);
                    else
                        leftColWords.Add(w);
                }

                GroupWordsSubsetIntoLines(topWords, linesList);
                GroupWordsSubsetIntoLines(leftColWords, linesList);
                GroupWordsSubsetIntoLines(rightColWords, linesList);
                GroupWordsSubsetIntoLines(bottomWords, linesList);
            }
            else
            {
                GroupWordsSubsetIntoLines(pageWords, linesList);
            }
        }

        return linesList;
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
            decimal lineTolerance = Math.Max(4.5m, firstLetter.FontSize * 0.45m);

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

            var mergedLineWords = MergeSplitNumbersInLine(sortedWords);
            linesList.Add(mergedLineWords);
        }
    }

    /// <summary>
    /// Détecte si une page est structurée en 2 colonnes de texte indépendantes (ex: contrats, conditions générales),
    /// tout en ne séparant jamais les tableaux de facturation (où la colonne de droite ne contient que des montants isolés).
    /// </summary>
    private static bool IsTwoColumnProsePage(
        List<(string CleanText, List<LetterLoc> Letters)> pageWords,
        out decimal gutterCenter)
    {
        gutterCenter = 297.5m;
        if (pageWords.Count < 80) return false;

        int leftCount = 0;
        int rightCount = 0;
        int crossingCenterCount = 0;
        var rightLineBands = new Dictionary<int, int>();

        foreach (var w in pageWords)
        {
            decimal y = w.Letters[0].BaselineY;
            if (y < 90m || y > 730m) continue;

            decimal minX = (decimal)w.Letters[0].BoundingBox.BottomLeft.X;
            decimal maxX = (decimal)w.Letters[^1].BoundingBox.TopRight.X;

            if (minX < 288m && maxX > 307m)
            {
                crossingCenterCount++;
            }
            else if (maxX <= 295m)
            {
                leftCount++;
            }
            else if (minX >= 300m)
            {
                rightCount++;
                int band = (int)Math.Round(y / 10m);
                rightLineBands[band] = rightLineBands.GetValueOrDefault(band) + 1;
            }
        }

        if (crossingCenterCount > 2 || leftCount < 35 || rightCount < 35 || rightLineBands.Count < 8)
            return false;

        double avgRightWordsPerLine = rightLineBands.Values.Average();
        return avgRightWordsPerLine >= 4.0;
    }

    private static List<(string CleanText, List<LetterLoc> Letters)> MergeSplitNumbersInLine(
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
            decimal maxThousandGap = Math.Max(4.5m, prev.Letters[^1].FontSize * 0.65m);

            if (gap >= -1.0m && gap <= maxThousandGap &&
                IsLeadingNumberPart(prev.CleanText) &&
                IsTrailingThousandGroup(curr.CleanText))
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

    private static bool IsLeadingNumberPart(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 9) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i])) return false;
        }
        return true;
    }

    private static bool IsTrailingThousandGroup(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 3) return false;
        if (!char.IsDigit(text[0]) || !char.IsDigit(text[1]) || !char.IsDigit(text[2])) return false;

        if (text.Length == 3) return true;
        char fourth = text[3];
        return fourth == '.' || fourth == '€' || fourth == '$' || fourth == '£' || fourth == '%';
    }
}
