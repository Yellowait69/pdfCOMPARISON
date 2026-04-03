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

    // REGEX TEXTE : Cherche 1 ou 2 mots en majuscules (autorise les tirets), OBLIGATOIREMENT suivis d'une virgule.
    [GeneratedRegex(@"\b([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)\s*,")]
    private static partial Regex DynamicTextNameRegex();

    // REGEX MOTS (PdfPig) : Identifie un mot en majuscules (au moins 2 lettres), avec une virgule optionnelle collée.
    [GeneratedRegex(@"^([\p{Lu}-]{2,})(,?)$")]
    private static partial Regex UpperWordRegex();

    public bool IsDate(string text) => DateRegex().IsMatch(text);

    public string MaskRepeatingTextElements(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in DateRegex().Matches(text))
        {
            dateCounts.TryGetValue(m.Value, out int count);
            dateCounts[m.Value] = count + 1;
        }

        var sb = new StringBuilder(text);

        foreach (var kvp in dateCounts)
        {
            if (kvp.Value >= 2) sb.Replace(kvp.Key, DateIgnoreMask);
        }

        // --- DÉTECTION STRICTE (Avec Virgule) ---
        Match nameMatch = DynamicTextNameRegex().Match(sb.ToString());
        if (nameMatch.Success)
        {
            // Le groupe 1 contient le nom (ex: "QARROLI LOCCAROLINE" ou "QARROLI") SANS la virgule
            string targetName = nameMatch.Groups[1].Value;

            // --- REMPLACEMENT SOUPLE (Espaces variables, avec ou sans virgule) ---
            // On sépare les mots ("SOUS" et "FORMAT") et on crée une Regex qui accepte \s+ (n'importe quel espacement)
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

        for (int i = 0; i < words.Count; i++)
        {
            var match = DateRegex().Match(words[i].Text);
            if (match.Success && (headerDates.Contains(match.Value) || (dateCounts.TryGetValue(match.Value, out int count) && count >= 2)))
            {
                words[i] = new PdfWordInfo { Text = DateIgnoreMask, Letters = words[i].Letters, PageNumber = words[i].PageNumber };
            }
        }

        // --- 1. DÉTECTION STRICTE DU PREMIER NOM (Doit comporter une virgule) ---
        string[] targetNameParts = Array.Empty<string>();

        for (int i = 0; i < words.Count; i++)
        {
            var match1 = UpperWordRegex().Match(words[i].Text);
            if (match1.Success)
            {
                string word1 = match1.Groups[1].Value;
                bool hasComma1 = match1.Groups[2].Value == ",";

                // Cas 1 : 1 mot avec virgule collée (ex: "QARROLI,")
                if (hasComma1)
                {
                    targetNameParts = new[] { word1 };
                    break;
                }

                if (i + 1 < words.Count)
                {
                    // Cas 2 : 1 mot avec virgule détachée (ex: "QARROLI" ",")
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

                        // Cas 3 : 2 mots, le second a une virgule collée (ex: "QARROLI" "LOCCAROLINE,")
                        if (hasComma2)
                        {
                            targetNameParts = new[] { word1, word2 };
                            break;
                        }

                        // Cas 4 : 2 mots, suivis d'une virgule détachée (ex: "QARROLI" "LOCCAROLINE" ",")
                        if (i + 2 < words.Count && words[i + 2].Text == ",")
                        {
                            targetNameParts = new[] { word1, word2 };
                            break;
                        }
                    }
                }
            }
        }

        // --- 2. MASQUAGE UNIVERSEL DANS TOUT LE DOCUMENT ---
        if (targetNameParts.Length > 0)
        {
            for (int i = 0; i < words.Count; i++)
            {
                // On retire la virgule pour comparer le mot pur
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
                            // Le nom complet a été trouvé, peu importe l'espacement initial !
                            words[i] = new PdfWordInfo { Text = NameIgnoreMask, Letters = words[i].Letters, PageNumber = words[i].PageNumber };
                            words[i + 1] = new PdfWordInfo { Text = string.Empty, Letters = words[i + 1].Letters, PageNumber = words[i + 1].PageNumber };
                            i++;
                        }
                    }
                }
            }
        }

        words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
    }
}