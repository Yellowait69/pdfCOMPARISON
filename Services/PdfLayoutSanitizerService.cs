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
            if (c == '\u00A0') sb.Append(' ');
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
            if (c == '\u00A0' || c == '\u200B' || c == '\u200C' || c == '\u200D' || c == '\uFEFF' || c == '\u00AD') continue;

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
            string cleanText = CleanWord(word.Text);
            if (string.IsNullOrEmpty(cleanText)) continue;

            var locs = new List<LetterLoc>(word.Letters.Count);
            foreach (var letter in word.Letters)
            {
                string cleanedGlyph = CleanWord(letter.Value);
                if (string.IsNullOrEmpty(cleanedGlyph)) continue;

                var loc = new LetterLoc(letter.GlyphRectangle, word.PageNumber, (decimal)letter.Location.Y, (decimal)letter.PointSize);

                if (locs.Count > 0)
                {
                    var last = locs.Last();
                    if (Math.Abs(last.BaselineY - loc.BaselineY) < 1.0m &&
                        Math.Abs((decimal)last.BoundingBox.BottomLeft.X - (decimal)loc.BoundingBox.BottomLeft.X) < 1.0m)
                    {
                        continue;
                    }
                }
                locs.Add(loc);
            }

            if (locs.Count > 0)
            {
                list.Add((cleanText, locs));
            }
        }

        var linesList = new List<List<(string CleanText, List<LetterLoc> Letters)>>();
        var wordsByPage = list.GroupBy(w => w.Letters.First().PageNumber).OrderBy(g => g.Key);

        foreach (var page in wordsByPage)
        {
            var pageWords = page.OrderByDescending(w => w.Letters.First().BaselineY).ToList();
            var lines = new List<List<(string CleanText, List<LetterLoc> Letters)>>();

            foreach (var word in pageWords)
            {
                decimal wordY = word.Letters.First().BaselineY;

                var currentLine = lines.FirstOrDefault(l => Math.Abs(l.First().Letters.First().BaselineY - wordY) < 5.0m);

                if (currentLine == null)
                {
                    currentLine = new List<(string CleanText, List<LetterLoc> Letters)>();
                    lines.Add(currentLine);
                }
                currentLine.Add(word);
            }

            foreach (var line in lines.OrderByDescending(l => l.First().Letters.First().BaselineY))
            {
                linesList.Add(line.OrderBy(w => w.Letters.First().BoundingBox.BottomLeft.X).ToList());
            }
        }

        return linesList;
    }
}