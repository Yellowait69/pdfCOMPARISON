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
    string MaskRepeatingTextElements(string text, IReadOnlySet<string>? headerDates = null);
    void MaskRepeatingWordElements(List<PdfWordInfo> words, HashSet<string> headerDates);
    bool IsDate(string text);
}

public partial class PdfIntelligentMaskingService : IPdfIntelligentMaskingService
{
    private readonly IConfigurationService? _configService;

    private const string DefaultDateIgnoreMask = "[DATE_IGNORE]";
    private const string DefaultNameIgnoreMask = "[NOM_IGNORE]";

    [GeneratedRegex(@"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b")]
    private static partial Regex DefaultDateRegex();

    [GeneratedRegex(@"^([Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur|[Cc]lient|[Aa]ssur[ée]|[Tt]itulaire|[Dd]estinataire):?$")]
    private static partial Regex DefaultClientKeywordRegex();

    [GeneratedRegex(@"(?:[Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur|[Cc]lient|[Aa]ssur[ée]|[Tt]itulaire|[Dd]estinataire)\s*[:\s]+([\p{Lu}][\p{Ll}\p{Lu}-]{1,}(?:\s+[\p{Lu}][\p{Ll}\p{Lu}-]{1,})?)|\b([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)\s*,")]
    private static partial Regex DefaultDynamicTextNameRegex();

    [GeneratedRegex(@"^([\p{Lu}-]{2,})(,?)$")]
    private static partial Regex UpperWordRegex();

    [GeneratedRegex(@"^([\p{Lu}][\p{Ll}\p{Lu}-]{1,})(,?)$")]
    private static partial Regex NameCapitalizedWordRegex();

    public PdfIntelligentMaskingService(IConfigurationService? configService = null)
    {
        _configService = configService;
    }

    private ComparisonProfile? Profile => _configService?.ActiveProfile;

    public bool IsDate(string text) => DefaultDateRegex().IsMatch(text);

    private static string NormalizeTokenForComparison(string token)
    {
        if (DefaultDateRegex().IsMatch(token))
        {
            return Regex.Replace(token, @"[^\d]+", ".");
        }
        return token.Trim().ToUpperInvariant();
    }

    public string MaskRepeatingTextElements(string text, IReadOnlySet<string>? headerDates = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var normalizedHeaderDates = headerDates != null && headerDates.Count > 0
            ? new HashSet<string>(headerDates.Select(NormalizeTokenForComparison), StringComparer.Ordinal)
            : null;

        string currentText = text;
        var rules = GetActiveRegexRules();

        foreach (var rule in rules)
        {
            var regex = GetRegexForRule(rule);
            if (regex == null) continue;

            if (rule.MinOccurrences <= 1 && normalizedHeaderDates == null)
            {
                currentText = regex.Replace(currentText, rule.ReplacementMask ?? string.Empty);
                continue;
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in regex.Matches(currentText))
            {
                string norm = NormalizeTokenForComparison(m.Value);
                counts[norm] = counts.GetValueOrDefault(norm) + 1;
            }

            int minOcc = Math.Max(1, rule.MinOccurrences);
            string mask = rule.ReplacementMask ?? string.Empty;

            currentText = regex.Replace(currentText, match =>
            {
                string norm = NormalizeTokenForComparison(match.Value);
                bool inHeader = normalizedHeaderDates != null && normalizedHeaderDates.Contains(norm);
                if (inHeader || (counts.TryGetValue(norm, out int count) && count >= minOcc))
                {
                    return mask;
                }
                return match.Value;
            });
        }

        if (Profile?.EnableDynamicClientNameMasking ?? true)
        {
            string nameMask = Profile?.ClientNameReplacementMask ?? DefaultNameIgnoreMask;
            var matches = DefaultDynamicTextNameRegex().Matches(currentText);
            var detectedPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match nameMatch in matches)
            {
                if (!nameMatch.Success) continue;
                string targetName = nameMatch.Groups[1].Success ? nameMatch.Groups[1].Value : nameMatch.Groups[2].Value;
                string[] parts = targetName.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                string pattern = $@"\b{string.Join(@"\s+", Array.ConvertAll(parts, Regex.Escape))}\b";
                detectedPatterns.Add(pattern);
            }

            foreach (var pattern in detectedPatterns)
            {
                currentText = Regex.Replace(currentText, pattern, nameMask, RegexOptions.IgnoreCase);
            }
        }

        return currentText;
    }

    public void MaskRepeatingWordElements(List<PdfWordInfo> words, HashSet<string> headerDates)
    {
        if (words == null || words.Count == 0) return;

        var normalizedHeaderDates = new HashSet<string>(headerDates.Select(NormalizeTokenForComparison), StringComparer.Ordinal);
        var rules = GetActiveRegexRules();

        foreach (var rule in rules)
        {
            if (words.Count == 0) break;

            var regex = GetRegexForRule(rule);
            if (regex == null) continue;

            var sb = new StringBuilder(words.Count * 8);
            var charToWord = new List<int>(words.Count * 8);

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
            var matches = regex.Matches(fullText);
            if (matches.Count == 0) continue;

            var tokenCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match match in matches)
            {
                string norm = NormalizeTokenForComparison(match.Value);
                tokenCounts[norm] = tokenCounts.GetValueOrDefault(norm) + 1;
            }

            int minOcc = Math.Max(1, rule.MinOccurrences);
            string mask = rule.ReplacementMask ?? string.Empty;

            foreach (Match match in matches)
            {
                string norm = NormalizeTokenForComparison(match.Value);

                if (normalizedHeaderDates.Contains(norm) ||
                    (tokenCounts.TryGetValue(norm, out int count) && count >= minOcc))
                {
                    int startWordIdx = -1;
                    int endWordIdx = -1;

                    for (int c = match.Index; c < match.Index + match.Length && c < charToWord.Count; c++)
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
                        if (!string.IsNullOrEmpty(mask) && words[startWordIdx].Text == mask) continue;

                        var combinedLetters = new List<Letter>();
                        for (int k = startWordIdx; k <= endWordIdx; k++)
                        {
                            if (words[k].Letters != null)
                            {
                                combinedLetters.AddRange(words[k].Letters);
                            }

                            if (k > startWordIdx)
                            {
                                words[k] = new PdfWordInfo { Text = string.Empty, Letters = Array.Empty<Letter>(), PageNumber = words[k].PageNumber };
                            }
                        }

                        words[startWordIdx] = new PdfWordInfo
                        {
                            Text = mask,
                            Letters = string.IsNullOrEmpty(mask) ? Array.Empty<Letter>() : combinedLetters,
                            PageNumber = words[startWordIdx].PageNumber
                        };
                    }
                }
            }

            words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
        }

        if (Profile?.EnableDynamicClientNameMasking ?? true)
        {
            ApplyClientNameMaskingOnWords(words);
        }
    }

    private void ApplyClientNameMaskingOnWords(List<PdfWordInfo> words)
    {
        var detectedClientNames = new List<string[]>();
        Regex clientKeywordRegex = (_configService != null && !string.IsNullOrWhiteSpace(Profile?.ClientKeywordsPattern))
            ? (_configService.TryGetCompiledRegex(Profile.ClientKeywordsPattern, ignoreCase: true) ?? DefaultClientKeywordRegex())
            : DefaultClientKeywordRegex();

        string nameMask = Profile?.ClientNameReplacementMask ?? DefaultNameIgnoreMask;

        for (int i = 0; i < words.Count; i++)
        {
            string token = words[i].Text.TrimEnd(':');
            if (clientKeywordRegex.IsMatch(words[i].Text) || clientKeywordRegex.IsMatch(token))
            {
                int nextIdx = i + 1;
                if (nextIdx < words.Count && words[nextIdx].Text == ":") nextIdx++;

                if (nextIdx < words.Count)
                {
                    var matchNext1 = NameCapitalizedWordRegex().Match(words[nextIdx].Text);
                    if (matchNext1.Success)
                    {
                        string word1 = matchNext1.Groups[1].Value;

                        if (nextIdx + 1 < words.Count)
                        {
                            var matchNext2 = NameCapitalizedWordRegex().Match(words[nextIdx + 1].Text);
                            if (matchNext2.Success)
                            {
                                detectedClientNames.Add(new[] { word1, matchNext2.Groups[1].Value });
                                continue;
                            }
                        }

                        detectedClientNames.Add(new[] { word1 });
                        continue;
                    }
                }
            }

            var match1 = UpperWordRegex().Match(words[i].Text);
            if (match1.Success && detectedClientNames.Count == 0)
            {
                string word1 = match1.Groups[1].Value;
                bool hasComma1 = match1.Groups[2].Value == ",";

                if (hasComma1)
                {
                    detectedClientNames.Add(new[] { word1 });
                    continue;
                }

                if (i + 1 < words.Count)
                {
                    if (words[i + 1].Text == ",")
                    {
                        detectedClientNames.Add(new[] { word1 });
                        continue;
                    }

                    var match2 = UpperWordRegex().Match(words[i + 1].Text);
                    if (match2.Success)
                    {
                        string word2 = match2.Groups[1].Value;
                        bool hasComma2 = match2.Groups[2].Value == ",";

                        if (hasComma2 || (i + 2 < words.Count && words[i + 2].Text == ","))
                        {
                            detectedClientNames.Add(new[] { word1, word2 });
                        }
                    }
                }
            }
        }

        foreach (var targetNameParts in detectedClientNames)
        {
            for (int i = 0; i < words.Count; i++)
            {
                if (string.IsNullOrEmpty(words[i].Text)) continue;
                string currentClean = words[i].Text.TrimEnd(',');

                if (targetNameParts.Length == 1)
                {
                    if (currentClean.Equals(targetNameParts[0], StringComparison.OrdinalIgnoreCase))
                    {
                        words[i] = new PdfWordInfo { Text = nameMask, Letters = words[i].Letters, PageNumber = words[i].PageNumber };
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

                            words[i] = new PdfWordInfo { Text = nameMask, Letters = combinedLetters, PageNumber = words[i].PageNumber };
                            words[i + 1] = new PdfWordInfo { Text = string.Empty, Letters = Array.Empty<Letter>(), PageNumber = words[i + 1].PageNumber };
                            i++;
                        }
                    }
                }
            }
        }

        words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
    }

    private IReadOnlyList<RegexMaskingRule> GetActiveRegexRules()
    {
        if (Profile != null && Profile.RegexRules.Count > 0)
        {
            return Profile.RegexRules.Where(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.Pattern)).ToList();
        }

        return new[]
        {
            new RegexMaskingRule
            {
                Name = "Default Date Mask",
                IsEnabled = true,
                Pattern = @"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b",
                ReplacementMask = DefaultDateIgnoreMask,
                IgnoreCase = true,
                MinOccurrences = 2
            }
        };
    }

    private Regex? GetRegexForRule(RegexMaskingRule rule)
    {
        if (_configService != null)
        {
            return _configService.TryGetCompiledRegex(rule.Pattern, rule.IgnoreCase);
        }

        try
        {
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (rule.IgnoreCase) options |= RegexOptions.IgnoreCase;
            return new Regex(rule.Pattern, options, TimeSpan.FromSeconds(2));
        }
        catch
        {
            return null;
        }
    }
}
