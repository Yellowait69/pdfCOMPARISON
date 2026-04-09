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

    // Tolère les espaces accidentels et différents séparateurs générés par l'extraction PDF
    [GeneratedRegex(@"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^([Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur):?$")]
    private static partial Regex ClientKeywordRegex();

    [GeneratedRegex(@"(?:[Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur)\s*[:\s]+([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)|\b([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)\s*,")]
    private static partial Regex DynamicTextNameRegex();

    [GeneratedRegex(@"^([\p{Lu}-]{2,})(,?)$")]
    private static partial Regex UpperWordRegex();

    public bool IsDate(string text) => DateRegex().IsMatch(text);

    // NOUVEAU : Normalise une date pour un comptage fiable malgré les artefacts OCR
    // (ex: le "." coupé par un filigrane et lu comme "/")
    private string NormalizeDateString(string date)
    {
        return Regex.Replace(date, @"[/\-\s]+", ".");
    }

    public string MaskRepeatingTextElements(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1. Comptage basé sur les dates normalisées
        foreach (Match m in DateRegex().Matches(text))
        {
            string norm = NormalizeDateString(m.Value);
            dateCounts.TryGetValue(norm, out int count);
            dateCounts[norm] = count + 1;
        }

        // 2. Remplacement dynamique basé sur le dictionnaire normalisé
        string textWithMaskedDates = DateRegex().Replace(text, match =>
        {
            string norm = NormalizeDateString(match.Value);
            if (dateCounts.TryGetValue(norm, out int count) && count >= 2)
            {
                return DateIgnoreMask;
            }
            return match.Value; // On garde le texte original s'il n'est pas masqué
        });

        var sb = new StringBuilder(textWithMaskedDates);

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

        // Normaliser le dictionnaire des dates d'en-tête pour la comparaison
        var normalizedHeaderDates = new HashSet<string>(headerDates.Select(NormalizeDateString), StringComparer.Ordinal);

        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1. Comptage des mots normalisés
        foreach (var w in words)
        {
            var match = DateRegex().Match(w.Text);
            if (match.Success)
            {
                string norm = NormalizeDateString(match.Value);
                dateCounts.TryGetValue(norm, out int count);
                dateCounts[norm] = count + 1;
            }
        }

        // 2. Application du masque
        for (int i = 0; i < words.Count; i++)
        {
            var match = DateRegex().Match(words[i].Text);
            if (match.Success)
            {
                string norm = NormalizeDateString(match.Value);

                // Si la date normalisée est dans l'en-tête OU apparaît >= 2 fois, on la masque
                if (normalizedHeaderDates.Contains(norm) || (dateCounts.TryGetValue(norm, out int count) && count >= 2))
                {
                    words[i] = new PdfWordInfo { Text = DateIgnoreMask, Letters = words[i].Letters, PageNumber = words[i].PageNumber };
                }
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