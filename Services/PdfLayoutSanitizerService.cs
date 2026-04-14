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
    public string CleanLineForDiff(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var sb = new StringBuilder(input.Length);

        foreach (char c in input)
        {
            if (c == '\u00AD') continue;

            char normalized = c;


            if (c == '\u00A0') normalized = ' ';
            else if (c == '–' || c == '—' || c == '−') normalized = '-';
            else if (c == '’' || c == '‘' || c == '´' || c == '`') normalized = '\'';
            else if (c == '“' || c == '”' || c == '«' || c == '»') normalized = '"';

            sb.Append(normalized);
        }

        return sb.ToString().Normalize(NormalizationForm.FormKC);
    }

    private string CleanWord(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var sb = new StringBuilder(input.Length);

        foreach (char c in input)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) continue;
            if (c == '\u00A0' || c == '\u200B' || c == '\u200C' || c == '\u200D' || c == '\uFEFF' || c == '\u00AD') continue;

            char normalized = c;


            if (c == '–' || c == '—' || c == '−') normalized = '-';
            else if (c == '’' || c == '‘' || c == '´' || c == '`') normalized = '\'';
            else if (c == '“' || c == '”' || c == '«' || c == '»') normalized = '"';
            else if (c == ',') normalized = '.';

            sb.Append(char.ToLowerInvariant(normalized));
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