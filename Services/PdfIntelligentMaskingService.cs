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

    // OPTIMISATION : Tolère maintenant les espaces générés par une extraction PDF défectueuse
    [GeneratedRegex(@"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b")]
    private static partial Regex DateRegex();

    // REGEX TEXTE : Cherche via le mot-clé (DE, NL, FR) OU l'ancienne méthode de la virgule
    [GeneratedRegex(@"(?:[Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur)\s*[:\s]+([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)|\b([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)\s*,")]
    private static partial Regex DynamicTextNameRegex();

    public bool IsDate(string text) => DateRegex().IsMatch(text);

    public string MaskRepeatingTextElements(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // 1. MASQUAGE DES DATES (Avec normalisation avant le comptage)
        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in DateRegex().Matches(text))
        {
            // Normalise "13 01.2025" en "13.01.2025" pour avoir un compteur exact
            string normDate = Regex.Replace(m.Value, @"[./\-\s]+", ".");
            dateCounts.TryGetValue(normDate, out int count);
            dateCounts[normDate] = count + 1;
        }

        var sb = new StringBuilder(text);

        foreach (Match m in DateRegex().Matches(text))
        {
            string normDate = Regex.Replace(m.Value, @"[./\-\s]+", ".");
            if (dateCounts.TryGetValue(normDate, out int count) && count >= 2)
            {
                sb.Replace(m.Value, DateIgnoreMask);
            }
        }

        // 2. MASQUAGE DU NOM
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

        // --- 1. MASQUAGE DES DATES (Au niveau des mots PdfPig) ---
        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in words)
        {
            var match = DateRegex().Match(w.Text);
            if (match.Success)
            {
                // Normalisation de la date trouvée dans la liste des mots
                string normDate = Regex.Replace(match.Value, @"[./\-\s]+", ".");
                dateCounts.TryGetValue(normDate, out int count);
                dateCounts[normDate] = count + 1;
            }
        }

        for (int i = 0; i < words.Count; i++)
        {
            var match = DateRegex().Match(words[i].Text);
            if (match.Success)
            {
                string normDate = Regex.Replace(match.Value, @"[./\-\s]+", ".");
                // On vérifie si la date normalisée apparaît au moins 2 fois
                if (headerDates.Contains(match.Value) || (dateCounts.TryGetValue(normDate, out int count) && count >= 2))
                {
                    // Remplace uniquement la partie date, conserve la ponctuation éventuelle à la fin
                    string newText = words[i].Text.Replace(match.Value, DateIgnoreMask);
                    words[i] = new PdfWordInfo { Text = newText, Letters = words[i].Letters, PageNumber = words[i].PageNumber };
                }
            }
        }

        // --- 2. DÉTECTION DU NOM (Reconstruction robuste de la ligne) ---
        // On fusionne tous les mots en une seule chaîne pour faire une détection parfaite,
        // indépendamment de la façon dont PdfPig a découpé les blocs de texte.
        var sb = new StringBuilder();
        for (int i = 0; i < words.Count; i++)
        {
            sb.Append(words[i].Text).Append(' ');
        }

        Match nameMatch = DynamicTextNameRegex().Match(sb.ToString());
        string[] targetNameParts = Array.Empty<string>();

        if (nameMatch.Success)
        {
            string targetName = nameMatch.Groups[1].Success ? nameMatch.Groups[1].Value : nameMatch.Groups[2].Value;
            targetNameParts = targetName.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // --- 3. MASQUAGE UNIVERSEL DU NOM DANS LES MOTS ---
        if (targetNameParts.Length > 0)
        {
            for (int i = 0; i < words.Count; i++)
            {
                bool matchFound = true;

                // Sécurité : s'assurer qu'il reste assez de mots dans la liste pour faire correspondre le nom complet
                if (i + targetNameParts.Length > words.Count) break;

                // Vérifier si la séquence de mots correspond aux parties du nom
                for (int j = 0; j < targetNameParts.Length; j++)
                {
                    string currentClean = words[i + j].Text.TrimEnd(',', '.', ':');
                    if (!currentClean.Equals(targetNameParts[j], StringComparison.OrdinalIgnoreCase))
                    {
                        matchFound = false;
                        break;
                    }
                }

                // Si on a trouvé la séquence complète (ex: "QARROLI" suivi de "LOCCAROLINE")
                if (matchFound)
                {
                    // Fusionne les lettres de tous les mots trouvés pour masquer l'ensemble proprement
                    var combinedLetters = new List<Letter>();
                    for (int j = 0; j < targetNameParts.Length; j++)
                    {
                        if (words[i + j].Letters != null)
                        {
                            combinedLetters.AddRange(words[i + j].Letters);
                        }
                    }

                    // On assigne le masque au premier mot
                    words[i] = new PdfWordInfo { Text = NameIgnoreMask, Letters = combinedLetters, PageNumber = words[i].PageNumber };

                    // On vide les mots suivants qui faisaient partie du nom
                    for (int j = 1; j < targetNameParts.Length; j++)
                    {
                        words[i + j] = new PdfWordInfo { Text = string.Empty, Letters = new List<Letter>(), PageNumber = words[i + j].PageNumber };
                    }

                    // On saute les mots qu'on vient de traiter
                    i += targetNameParts.Length - 1;
                }
            }
        }

        // Nettoyage final des mots vides
        words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
    }
}