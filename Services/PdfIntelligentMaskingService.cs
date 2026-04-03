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

    // REGEX TEXTE : Cherche 1 ou 2 mots entièrement en majuscules (autorise les tirets), suivis d'une virgule.
    [GeneratedRegex(@"\b([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)\s*,")]
    private static partial Regex DynamicTextNameRegex();

    // REGEX MOTS (PdfPig) : Identifie un mot unique en majuscules, avec une virgule collée optionnelle.
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

        // 1. Masquage des dates
        foreach (var kvp in dateCounts)
        {
            if (kvp.Value >= 2) sb.Replace(kvp.Key, DateIgnoreMask);
        }

        // --- DÉTECTION ET MASQUAGE DYNAMIQUE DU PREMIER NOM ---
        Match nameMatch = DynamicTextNameRegex().Match(sb.ToString());
        if (nameMatch.Success)
        {
            // nameMatch.Groups[1] contient le nom sans la virgule (ex: "QARROLI LOCCAROLINE" ou "SOUSFORMAT")
            string targetName = nameMatch.Groups[1].Value;

            // On remplace toutes ses occurrences exactes dans le texte
            string pattern = $@"\b{Regex.Escape(targetName)}\b";
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

        // --- 1. REMPLACEMENT DES DATES ---
        for (int i = 0; i < words.Count; i++)
        {
            var match = DateRegex().Match(words[i].Text);
            if (match.Success && (headerDates.Contains(match.Value) || (dateCounts.TryGetValue(match.Value, out int count) && count >= 2)))
            {
                words[i] = new PdfWordInfo { Text = DateIgnoreMask, Letters = words[i].Letters, PageNumber = words[i].PageNumber };
            }
        }

        // --- 2. DÉTECTION DU PREMIER NOM (La cible unique) ---
        string[] targetNameParts = Array.Empty<string>();

        for (int i = 0; i < words.Count; i++)
        {
            var match1 = UpperWordRegex().Match(words[i].Text);
            if (match1.Success)
            {
                string word1 = match1.Groups[1].Value;
                bool hasComma1 = match1.Groups[2].Value == ",";

                // Cas 1 : Le 1er mot contient la virgule attachée (ex: "QARROLI,")
                if (hasComma1)
                {
                    targetNameParts = new[] { word1 };
                    break;
                }

                if (i + 1 < words.Count)
                {
                    // Cas 2 : Le 1er mot est suivi d'une virgule détachée (ex: "QARROLI" ",")
                    if (words[i + 1].Text == ",")
                    {
                        targetNameParts = new[] { word1 };
                        break;
                    }

                    // Regarder le 2eme mot
                    var match2 = UpperWordRegex().Match(words[i + 1].Text);
                    if (match2.Success)
                    {
                        string word2 = match2.Groups[1].Value;
                        bool hasComma2 = match2.Groups[2].Value == ",";

                        // Cas 3 : Les 2 mots, le 2eme a la virgule attachée (ex: "QARROLI" "LOCCAROLINE,")
                        if (hasComma2)
                        {
                            targetNameParts = new[] { word1, word2 };
                            break;
                        }

                        // Cas 4 : Les 2 mots, suivis d'une virgule détachée (ex: "QARROLI" "LOCCAROLINE" ",")
                        if (i + 2 < words.Count && words[i + 2].Text == ",")
                        {
                            targetNameParts = new[] { word1, word2 };
                            break;
                        }
                    }
                }
            }
        }

        // --- 3. MASQUAGE DU NOM CIBLE DANS TOUT LE DOCUMENT ---
        // Une fois trouvé, on parcourt tout le document et on masque CHAQUE apparition de ce nom (avec ou sans virgule)
        if (targetNameParts.Length > 0)
        {
            for (int i = 0; i < words.Count; i++)
            {
                // TrimEnd(',') permet de neutraliser la virgule pour que "QARROLI," et "QARROLI" matchent.
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
                            words[i] = new PdfWordInfo { Text = NameIgnoreMask, Letters = words[i].Letters, PageNumber = words[i].PageNumber };
                            // On vide le deuxième mot
                            words[i + 1] = new PdfWordInfo { Text = string.Empty, Letters = words[i + 1].Letters, PageNumber = words[i + 1].PageNumber };
                            i++; // On avance l'index pour ne pas revérifier le 2eme mot
                        }
                    }
                }
            }
        }

        // Nettoyage final pour purger les mots que nous avons vidés (string.Empty)
        words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
    }
}