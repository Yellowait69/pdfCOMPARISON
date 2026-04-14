using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig.Content;

namespace PDFComparison.Services;

public interface IPdfIntelligentMaskingService
{
    string MaskRepeatingTextElements(string text);
    void MaskRepeatingWordElements(List<PdfWordInfo> words, HashSet<string> headerDates);
    bool IsDate(string text);
}

public partial class PdfIntelligentMaskingService : IPdfIntelligentMaskingService
{
    private const string DateIgnoreMask = "[DATE_IGNORE]";
    private const string NameIgnoreMask = "[NOM_IGNORE]";


    [GeneratedRegex(@"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^([Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur):?$")]
    private static partial Regex ClientKeywordRegex();

    [GeneratedRegex(@"(?:[Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur)\s*[:\s]+([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)|\b([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)\s*,")]
    private static partial Regex DynamicTextNameRegex();

    [GeneratedRegex(@"^([\p{Lu}-]{2,})(,?)$")]
    private static partial Regex UpperWordRegex();

    public bool IsDate(string text) => DateRegex().IsMatch(text);

    private string NormalizeDateString(string date)
    {
        return Regex.Replace(date, @"[^\d]+", ".");
    }

    public string MaskRepeatingTextElements(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match m in DateRegex().Matches(text))
        {
            string norm = NormalizeDateString(m.Value);
            dateCounts.TryGetValue(norm, out int count);
            dateCounts[norm] = count + 1;
        }

        string textWithMaskedDates = DateRegex().Replace(text, match =>
        {
            string norm = NormalizeDateString(match.Value);
            if (dateCounts.TryGetValue(norm, out int count) && count >= 2)
            {
                return DateIgnoreMask;
            }
            return match.Value;
        });

        var sb = new StringBuilder(textWithMaskedDates);

        Match nameMatch = DynamicTextNameRegex().Match(sb.ToString());
        if (nameMatch.Success)
        {
            string targetName = nameMatch.Groups[1].Success ? nameMatch.Groups[1].Value : nameMatch.Groups[2].Value;
            string[] parts = targetName.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string pattern = $@"\b{string.Join(@"\s+", Array.ConvertAll(parts, Regex.Escape))}\b";

            string replacedText = Regex.Replace(sb.ToString(), pattern, NameIgnoreMask);
            sb.Clear();
            sb.Append(replacedText);
        }

        return sb.ToString();
    }

    public void MaskRepeatingWordElements(List<PdfWordInfo> words, HashSet<string> headerDates)
    {
        if (words == null || words.Count == 0) return;

        var normalizedHeaderDates = new HashSet<string>(headerDates.Select(NormalizeDateString), StringComparer.Ordinal);
        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        var sb = new StringBuilder();
        var charToWord = new List<int>();

        for (int i = 0; i < words.Count; i++)
        {
            foreach (char c in words[i].Text)
            {
                sb.Append(c);
                charToWord.Add(i);
            }
            sb.Append(' ');
            charToWord.Add(-1);
        }

        string fullText = sb.ToString();
        var matches = DateRegex().Matches(fullText);

        foreach (Match match in matches)
        {
            string norm = NormalizeDateString(match.Value);
            dateCounts.TryGetValue(norm, out int count);
            dateCounts[norm] = count + 1;
        }

        foreach (Match match in matches)
        {
            string norm = NormalizeDateString(match.Value);

            if (normalizedHeaderDates.Contains(norm) || (dateCounts.TryGetValue(norm, out int count) && count >= 2))
            {
                int startWordIdx = -1;
                int endWordIdx = -1;

                for (int c = match.Index; c < match.Index + match.Length; c++)
                {
                    int wIdx = charToWord[c];
                    if (wIdx != -1)
                    {
                        if (startWordIdx == -1) startWordIdx = wIdx;
                        endWordIdx = wIdx;
                    }
                }

                if (startWordIdx != -1 && endWordIdx != -1)
                {
                    if (words[startWordIdx].Text == DateIgnoreMask) continue;

                    var combinedLetters = new List<Letter>();
                    for (int k = startWordIdx; k <= endWordIdx; k++)
                    {
                        if (words[k].Letters != null)
                        {
                            combinedLetters.AddRange(words[k].Letters);
                        }

                        if (k > startWordIdx)
                        {
                            words[k] = new PdfWordInfo { Text = string.Empty, Letters = new List<Letter>(), PageNumber = words[k].PageNumber };
                        }
                    }

                    words[startWordIdx] = new PdfWordInfo { Text = DateIgnoreMask, Letters = combinedLetters, PageNumber = words[startWordIdx].PageNumber };
                }
            }
        }

        words.RemoveAll(w => string.IsNullOrEmpty(w.Text));

        string[] targetNameParts = Array.Empty<string>();

        for (int i = 0; i < words.Count; i++)
        {
            if (ClientKeywordRegex().IsMatch(words[i].Text))
            {
                if (i + 1 < words.Count)
                {
                    var matchNext1 = UpperWordRegex().Match(words[i + 1].Text);
                    if (matchNext1.Success)
                    {
                        string word1 = matchNext1.Groups[1].Value;

                        if (i + 2 < words.Count)
                        {
                            var matchNext2 = UpperWordRegex().Match(words[i + 2].Text);
                            if (matchNext2.Success)
                            {
                                targetNameParts = new[] { word1, matchNext2.Groups[1].Value };
                                break;
                            }
                        }

                        targetNameParts = new[] { word1 };
                        break;
                    }
                }
            }

            var match1 = UpperWordRegex().Match(words[i].Text);
            if (match1.Success && targetNameParts.Length == 0)
            {
                string word1 = match1.Groups[1].Value;
                bool hasComma1 = match1.Groups[2].Value == ",";

                if (hasComma1)
                {
                    targetNameParts = new[] { word1 };
                    break;
                }

                if (i + 1 < words.Count)
                {
                    if (words[i + 1].Text == ",")
                    {
                        targetNameParts = new[] { word1 };
                        break;
                    }

                    var match2 = UpperWordRegex().Match(words[i + 1].Text);
                    if (match2.Success)
                    {
                        string word2 = match2.Groups[1].Value;
                        bool hasComma2 = match2.Groups[2].Value == ",";

                        if (hasComma2)
                        {
                            targetNameParts = new[] { word1, word2 };
                            break;
                        }

                        if (i + 2 < words.Count && words[i + 2].Text == ",")
                        {
                            targetNameParts = new[] { word1, word2 };
                            break;
                        }
                    }
                }
            }
        }

        if (targetNameParts.Length > 0)
        {
            for (int i = 0; i < words.Count; i++)
            {
                string currentClean = words[i].Text.TrimEnd(',');

                if (targetNameParts.Length == 1)
                {
                    if (currentClean.Equals(targetNameParts[0], StringComparison.OrdinalIgnoreCase))
                    {
                        words[i] = new PdfWordInfo { Text = NameIgnoreMask, Letters = words[i].Letters, PageNumber = words[i].PageNumber };
                    }
                }
                else if (targetNameParts.Length == 2)
                {
                    if (currentClean.Equals(targetNameParts[0], StringComparison.OrdinalIgnoreCase) && i + 1 < words.Count)
                    {
                        string nextClean = words[i + 1].Text.TrimEnd(',');
                        if (nextClean.Equals(targetNameParts[1], StringComparison.OrdinalIgnoreCase))
                        {
                            var combinedLetters = new List<Letter>(words[i].Letters);
                            combinedLetters.AddRange(words[i + 1].Letters);

                            words[i] = new PdfWordInfo { Text = NameIgnoreMask, Letters = combinedLetters, PageNumber = words[i].PageNumber };
                            words[i + 1] = new PdfWordInfo { Text = string.Empty, Letters = new List<Letter>(), PageNumber = words[i + 1].PageNumber };
                            i++;
                        }
                    }
                }
            }
        }

        words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
    }
}