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

    // Regex optimisée avec compilation à la génération et timeouts pour éviter les dénis de service (DOS)
    [GeneratedRegex(@"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b", RegexOptions.Compiled)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"(?:[Aa]uftraggeber|[Oo]pdrachtgever|[Ss]ouscripteur)\s*[:\s]+([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)|\b([\p{Lu}-]{2,}(?:\s+[\p{Lu}-]{2,})?)\s*,", RegexOptions.Compiled)]
    private static partial Regex DynamicTextNameRegex();

    // Regex pour normaliser les séparateurs de date rapidement
    [GeneratedRegex(@"[./\-\s]+")]
    private static partial Regex DateSeparatorRegex();

    public bool IsDate(string text) => !string.IsNullOrEmpty(text) && DateRegex().IsMatch(text);

    /// <summary>
    /// Normalise une date pour le comptage (ex: "13 01 2025" -> "13.01.2025")
    /// </summary>
    private string NormalizeDate(string dateValue) => DateSeparatorRegex().Replace(dateValue, ".");

    public string MaskRepeatingTextElements(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // 1. Comptage des dates normalisées
        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var matches = DateRegex().Matches(text);

        foreach (Match m in matches)
        {
            string norm = NormalizeDate(m.Value);
            dateCounts[norm] = dateCounts.GetValueOrDefault(norm) + 1;
        }

        // 2. Remplacement des dates (en partant de la fin pour ne pas corrompre les index si on n'utilisait pas Replace)
        var sb = new StringBuilder(text);
        foreach (Match m in matches)
        {
            if (dateCounts.TryGetValue(NormalizeDate(m.Value), out int count) && count >= 2)
            {
                // Note: Replace sur StringBuilder est plus performant qu'une Regex ici
                sb.Replace(m.Value, DateIgnoreMask);
            }
        }

        // 3. Masquage du Nom
        var currentText = sb.ToString();
        var nameMatch = DynamicTextNameRegex().Match(currentText);
        if (nameMatch.Success)
        {
            string targetName = nameMatch.Groups[1].Success ? nameMatch.Groups[1].Value : nameMatch.Groups[2].Value;

            // Création d'un pattern qui accepte n'importe quel nombre d'espaces ou de sauts de ligne entre les parties du nom
            string[] parts = targetName.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string flexiblePattern = $@"\b{string.Join(@"\s+", parts.Select(Regex.Escape))}\b";

            return Regex.Replace(currentText, flexiblePattern, NameIgnoreMask);
        }

        return currentText;
    }

    public void MaskRepeatingWordElements(List<PdfWordInfo> words, HashSet<string> headerDates)
    {
        if (words == null || words.Count == 0) return;

        // --- 1. TRAITEMENT DES DATES ---
        var dateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in words)
        {
            var m = DateRegex().Match(w.Text);
            if (m.Success)
            {
                string norm = NormalizeDate(m.Value);
                dateCounts[norm] = dateCounts.GetValueOrDefault(norm) + 1;
            }
        }

        for (int i = 0; i < words.Count; i++)
        {
            var m = DateRegex().Match(words[i].Text);
            if (m.Success)
            {
                string norm = NormalizeDate(m.Value);
                if (headerDates.Contains(m.Value) || dateCounts.GetValueOrDefault(norm) >= 2)
                {
                    // On préserve les éventuels caractères collés au masque
                    string newText = words[i].Text.Replace(m.Value, DateIgnoreMask);
                    words[i] = new PdfWordInfo {
                        Text = newText,
                        Letters = words[i].Letters,
                        PageNumber = words[i].PageNumber
                    };
                }
            }
        }

        // --- 2. DÉTECTION DU NOM (Analyse de flux) ---
        var fullContent = string.Join(' ', words.Select(w => w.Text));
        var nameMatch = DynamicTextNameRegex().Match(fullContent);

        if (nameMatch.Success)
        {
            string foundName = nameMatch.Groups[1].Success ? nameMatch.Groups[1].Value : nameMatch.Groups[2].Value;
            var nameParts = foundName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Masquage par fenêtre glissante pour plus de précision
            for (int i = 0; i < words.Count - nameParts.Length + 1; i++)
            {
                bool sequenceMatch = true;
                for (int j = 0; j < nameParts.Length; j++)
                {
                    // Nettoyage des ponctuations pour la comparaison
                    string cleanWord = words[i + j].Text.Trim(',', '.', ':', ';', ' ');
                    if (!cleanWord.Equals(nameParts[j], StringComparison.OrdinalIgnoreCase))
                    {
                        sequenceMatch = false;
                        break;
                    }
                }

                if (sequenceMatch)
                {
                    // Fusion des glyphes (Letters) pour le rapport visuel
                    var combinedLetters = new List<Letter>();
                    for (int j = 0; j < nameParts.Length; j++)
                    {
                        if (words[i + j].Letters != null)
                            combinedLetters.AddRange(words[i + j].Letters);
                    }

                    // On remplace le premier mot par le masque, et on vide les suivants
                    words[i] = new PdfWordInfo { Text = NameIgnoreMask, Letters = combinedLetters, PageNumber = words[i].PageNumber };

                    for (int j = 1; j < nameParts.Length; j++)
                    {
                        words[i + j] = new PdfWordInfo { Text = string.Empty, Letters = new List<Letter>(), PageNumber = words[i + j].PageNumber };
                    }

                    i += nameParts.Length - 1; // Sauter la séquence traitée
                }
            }
        }

        // Suppression efficace
        words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
    }
}