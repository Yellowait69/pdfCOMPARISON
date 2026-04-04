using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig.Content; // AJOUT CRUCIAL ICI pour la classe Letter

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

    // OPTIMISATION : Ajout de l'espace (\s) et du "+" pour tolérer les espaces accidentels
    // générés par l'extraction PDF (ex: "13 01.2025" ou "13. 01 .2025")
    [GeneratedRegex(@"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b")]
    private static partial Regex DateRegex();

    // NOUVEAU : Regex pour identifier les mots-clés qui précèdent un nom de client (DE, NL, FR)
    [GeneratedRegex(@"^([Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur):?$")]
    private static partial Regex ClientKeywordRegex();

    // REGEX TEXTE : Cherche via le mot-clé OU via l'ancienne méthode de la virgule.
    // On n'utilise pas (?i) globalement pour ne pas casser la détection stricte des majuscules \p{Lu}.
    [GeneratedRegex(@"(?:[Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur)\s*[:\s]+([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)|\b([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)\s*,")]
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

        // --- DÉTECTION DU NOM (Par mot-clé ou par virgule) ---
        Match nameMatch = DynamicTextNameRegex().Match(sb.ToString());
        if (nameMatch.Success)
        {
            // Le groupe 1 contient le nom trouvé via le mot-clé (ex: "Auftraggeber QARROLI")
            // Le groupe 2 contient le nom trouvé via la virgule (ex: "QARROLI,")
            string targetName = nameMatch.Groups[1].Success ? nameMatch.Groups[1].Value : nameMatch.Groups[2].Value;

            // --- REMPLACEMENT SOUPLE (Espaces variables, avec ou sans virgule) ---
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

        // --- 1. DÉTECTION STRICTE DU PREMIER NOM ---
        string[] targetNameParts = Array.Empty<string>();

        for (int i = 0; i < words.Count; i++)
        {
            // STRATÉGIE 1 : Détection ancrée sur le mot-clé ("Auftraggeber", "Opdrachtgever"...)
            if (ClientKeywordRegex().IsMatch(words[i].Text))
            {
                if (i + 1 < words.Count)
                {
                    var matchNext1 = UpperWordRegex().Match(words[i + 1].Text);
                    if (matchNext1.Success) // Le mot suivant est en majuscule
                    {
                        string word1 = matchNext1.Groups[1].Value;

                        // Vérifier s'il y a un 2ème nom/prénom en majuscule juste après
                        if (i + 2 < words.Count)
                        {
                            var matchNext2 = UpperWordRegex().Match(words[i + 2].Text);
                            if (matchNext2.Success)
                            {
                                targetNameParts = new[] { word1, matchNext2.Groups[1].Value };
                                break;
                            }
                        }

                        // Sinon on ne garde que le premier mot
                        targetNameParts = new[] { word1 };
                        break;
                    }
                }
            }

            // STRATÉGIE 2 : Rétrocompatibilité (Mots en majuscules suivis d'une virgule)
            var match1 = UpperWordRegex().Match(words[i].Text);
            if (match1.Success && targetNameParts.Length == 0) // Exécuté seulement si la Stratégie 1 n'a rien trouvé
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
                            // Fusion des Letters pour masquer les deux mots proprement
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