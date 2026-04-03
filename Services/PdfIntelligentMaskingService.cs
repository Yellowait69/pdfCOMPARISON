using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;

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

    [GeneratedRegex(@"\b\d{1,2}[./-]\d{1,2}[./-]\d{2,4}\b")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\b[A-ZÀ-Ÿ]{2,}(?:[\s\-']+[A-ZÀ-Ÿ]{2,})+\b")]
    private static partial Regex UppercaseSeqRegex();

    public bool IsDate(string text) => DateRegex().IsMatch(text);

    public string MaskRepeatingTextElements(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // OPTIMISATION : Comptage manuel beaucoup plus léger que .Cast<Match>().GroupBy().ToDictionary()
        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in DateRegex().Matches(text))
        {
            dateCounts.TryGetValue(m.Value, out int count);
            dateCounts[m.Value] = count + 1;
        }

        var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in UppercaseSeqRegex().Matches(text))
        {
            nameCounts.TryGetValue(m.Value, out int count);
            nameCounts[m.Value] = count + 1;
        }

        // OPTIMISATION : Remplacements In-Place avec un StringBuilder
        // au lieu d'assigner une nouvelle string à chaque itération (text = text.Replace...)
        var sb = new StringBuilder(text);

        foreach (var kvp in dateCounts)
        {
            if (kvp.Value >= 2) sb.Replace(kvp.Key, DateIgnoreMask);
        }

        foreach (var kvp in nameCounts)
        {
            if (kvp.Value >= 2) sb.Replace(kvp.Key, NameIgnoreMask);
        }

        return sb.ToString();
    }

    private bool IsUppercaseWord(string text)
    {
        if (string.Equals(text, DateIgnoreMask, StringComparison.Ordinal) ||
            string.Equals(text, NameIgnoreMask, StringComparison.Ordinal))
            return false;

        int letterCount = 0;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                if (char.IsLower(c)) return false;
                letterCount++;
            }
        }
        return letterCount >= 2;
    }

    // OPTIMISATION : Évite la création d'un array via LINQ `.Where(char.IsLetter).ToArray()`
    private string GetOnlyLetters(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetter(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    public void MaskRepeatingWordElements(List<PdfWordInfo> words, HashSet<string> headerDates)
    {
        if (words == null || words.Count == 0) return;

        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in words)
        {
            var match = DateRegex().Match(w.Text);
            if (match.Success)
            {
                dateCounts.TryGetValue(match.Value, out int count);
                dateCounts[match.Value] = count + 1;
            }
        }

        var uppercaseSequences = new Dictionary<string, int>(StringComparer.Ordinal);
        var currentSequenceIndices = new List<int>(10);

        // OPTIMISATION : Un seul StringBuilder recyclé pour créer les clés de dictionnaire
        var seqKeyBuilder = new StringBuilder();

        for (int i = 0; i <= words.Count; i++)
        {
            bool isUpper = i < words.Count && IsUppercaseWord(words[i].Text);
            if (isUpper)
            {
                currentSequenceIndices.Add(i);
            }
            else
            {
                if (currentSequenceIndices.Count >= 2)
                {
                    seqKeyBuilder.Clear();
                    for (int j = 0; j < currentSequenceIndices.Count; j++)
                    {
                        if (j > 0) seqKeyBuilder.Append(' ');
                        seqKeyBuilder.Append(GetOnlyLetters(words[currentSequenceIndices[j]].Text));
                    }

                    string seqKey = seqKeyBuilder.ToString();
                    uppercaseSequences.TryGetValue(seqKey, out int count);
                    uppercaseSequences[seqKey] = count + 1;
                }
                currentSequenceIndices.Clear();
            }
        }

        // Remplacement des dates
        for (int i = 0; i < words.Count; i++)
        {
            var match = DateRegex().Match(words[i].Text);
            if (match.Success && (headerDates.Contains(match.Value) || (dateCounts.TryGetValue(match.Value, out int count) && count >= 2)))
            {
                words[i] = new PdfWordInfo { Text = DateIgnoreMask, Letters = words[i].Letters, PageNumber = words[i].PageNumber };
            }
        }

        // Remplacement des séquences de noms
        currentSequenceIndices.Clear();
        for (int i = 0; i <= words.Count; i++)
        {
            bool isUpper = i < words.Count && IsUppercaseWord(words[i].Text);
            if (isUpper)
            {
                currentSequenceIndices.Add(i);
            }
            else
            {
                if (currentSequenceIndices.Count >= 2)
                {
                    seqKeyBuilder.Clear();
                    for (int j = 0; j < currentSequenceIndices.Count; j++)
                    {
                        if (j > 0) seqKeyBuilder.Append(' ');
                        seqKeyBuilder.Append(GetOnlyLetters(words[currentSequenceIndices[j]].Text));
                    }

                    string seqKey = seqKeyBuilder.ToString();
                    if (uppercaseSequences.TryGetValue(seqKey, out int count) && count >= 2)
                    {
                        for (int j = 0; j < currentSequenceIndices.Count; j++)
                        {
                            int wordIdx = currentSequenceIndices[j];
                            words[wordIdx] = new PdfWordInfo
                            {
                                Text = j == 0 ? NameIgnoreMask : string.Empty,
                                Letters = words[wordIdx].Letters,
                                PageNumber = words[wordIdx].PageNumber
                            };
                        }
                    }
                }
                currentSequenceIndices.Clear();
            }
        }

        // List.RemoveAll est hautement optimisé en C#, c'est la bonne méthode à conserver
        words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
    }
}