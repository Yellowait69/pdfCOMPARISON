using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PDFComparison.Services;

public partial class PdfExtractionService
{
    // Utilisation du Source Generator de Regex pour éviter de recompiler l'expression à chaque appel
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // Regex pour nettoyer le texte brut des filigranes lors de l'extraction textuelle
    [GeneratedRegex(@"(?i)(specimen|cimen|speci|men|test|totein|Q000|D000|P000|A000)")]
    private static partial Regex WatermarkTextRegex();

    // NOUVEAU : Détection des dates
    [GeneratedRegex(@"\b\d{1,2}[./-]\d{1,2}[./-]\d{2,4}\b")]
    private static partial Regex DateRegex();

    // NOUVEAU : Cherche des mots d'au moins 2 lettres tout en majuscules (incluant les accents, tirets, apostrophes)
    [GeneratedRegex(@"^[A-ZÀ-Ÿ\-']{2,}$")]
    private static partial Regex UppercaseWordRegex();

    public string ExtractTextFast(string pdfPath)
    {
        var sb = new StringBuilder();
        var options = new ParsingOptions { ClipPaths = false };

        using var document = PdfDocument.Open(pdfPath, options);
        foreach (var page in document.GetPages())
        {
            string rawText = page.Text;
            string cleanText = WatermarkTextRegex().Replace(rawText, "");
            sb.AppendLine(cleanText);
        }

        // Appliquer le masquage des données dynamiques sur le texte brut (pour le résumé textuel)
        return MaskRepeatingTextElements(sb.ToString());
    }

    public List<PdfWordInfo> ExtractWords(string pdfPath)
    {
        var words = new List<PdfWordInfo>();
        using var doc = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });

        foreach (var page in doc.GetPages())
        {
            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text))
                    continue;

                // Bouclier Anti-Filigrane
                if (IsWatermark(word))
                    continue;

                // ==========================================
                // 1. FILTRAGE SPATIAL : Exclure les codes-barres
                // ==========================================
                // Si le mot est situé dans les 50 premiers points de la marge gauche (codes-barres verticaux)
                if (word.BoundingBox.BottomLeft.X < 50)
                    continue;

                words.Add(new PdfWordInfo
                {
                    Text = word.Text,
                    Letters = word.Letters,
                    PageNumber = page.Number
                });
            }
        }

        // ==========================================
        // 2. MASQUAGE INTELLIGENT (Noms et Dates répétés >= 2 fois)
        // ==========================================
        MaskRepeatingWordElements(words);

        return words;
    }

    // ==============================================================
    // LOGIQUE DE MASQUAGE DES DONNÉES DYNAMIQUES
    // ==============================================================

    private string MaskRepeatingTextElements(string text)
    {
        // 1. Masquage des dates répétées
        var dateMatches = DateRegex().Matches(text);
        var dateCounts = dateMatches.Cast<Match>().GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.Count());

        foreach (var kvp in dateCounts.Where(k => k.Value >= 2))
        {
            text = text.Replace(kvp.Key, "[DATE_IGNORE]");
        }

        // 2. Masquage des Noms/Prénoms répétés (suite d'au moins 2 mots en majuscules séparés par des espaces)
        var uppercaseSeqRegex = new Regex(@"\b[A-ZÀ-Ÿ\-']{2,}(?:\s+[A-ZÀ-Ÿ\-']{2,})+\b");
        var nameMatches = uppercaseSeqRegex.Matches(text);
        var nameCounts = nameMatches.Cast<Match>().GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.Count());

        foreach (var kvp in nameCounts.Where(k => k.Value >= 2))
        {
            text = text.Replace(kvp.Key, "[NOM_IGNORE]");
        }

        return text;
    }

    private void MaskRepeatingWordElements(List<PdfWordInfo> words)
    {
        // A. Compter la fréquence des dates
        var dateCounts = new Dictionary<string, int>();
        foreach (var w in words)
        {
            if (DateRegex().IsMatch(w.Text))
            {
                if (!dateCounts.ContainsKey(w.Text)) dateCounts[w.Text] = 0;
                dateCounts[w.Text]++;
            }
        }

        // B. Trouver et compter les séquences de majuscules (Noms/Prénoms)
        var uppercaseSequences = new Dictionary<string, int>();
        var currentSequence = new List<string>();

        for (int i = 0; i <= words.Count; i++)
        {
            bool isUpper = i < words.Count && UppercaseWordRegex().IsMatch(words[i].Text);

            if (isUpper)
            {
                currentSequence.Add(words[i].Text);
            }
            else
            {
                // Si on a une suite d'au moins 2 mots majuscules (ex: "MAXENCE" + "DESSILLY")
                if (currentSequence.Count >= 2)
                {
                    string seqStr = string.Join(" ", currentSequence);
                    if (!uppercaseSequences.ContainsKey(seqStr)) uppercaseSequences[seqStr] = 0;
                    uppercaseSequences[seqStr]++;
                }
                currentSequence.Clear();
            }
        }

        // C. Remplacer les mots dans la liste par nos balises d'ignorance
        for (int i = 0; i < words.Count; i++)
        {
            // Remplacement des dates répétées
            if (DateRegex().IsMatch(words[i].Text) && dateCounts[words[i].Text] >= 2)
            {
                words[i] = new PdfWordInfo { Text = "[DATE_IGNORE]", Letters = words[i].Letters, PageNumber = words[i].PageNumber };
                continue;
            }
        }

        for (int i = 0; i < words.Count; i++)
        {
            // Remplacement des séquences de noms répétés
            foreach (var seq in uppercaseSequences.Where(kvp => kvp.Value >= 2))
            {
                var seqWords = seq.Key.Split(' ');
                if (i + seqWords.Length <= words.Count)
                {
                    bool match = true;
                    for (int j = 0; j < seqWords.Length; j++)
                    {
                        if (words[i + j].Text != seqWords[j])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        for (int j = 0; j < seqWords.Length; j++)
                        {
                            // On place la balise sur le premier mot, et on vide les autres
                            string newText = j == 0 ? "[NOM_IGNORE]" : "";
                            words[i + j] = new PdfWordInfo { Text = newText, Letters = words[i + j].Letters, PageNumber = words[i + j].PageNumber };
                        }
                        i += seqWords.Length - 1; // On avance l'index pour sauter la séquence traitée
                        break;
                    }
                }
            }
        }

        // D. Retirer les mots que nous avons vidés (les "restes" des noms propres)
        words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
    }

    // ==============================================================
    // LE RESTE DE LA CLASSE (IDENTIQUE)
    // ==============================================================

    [GeneratedRegex(@"^[\d.,/\-\s€$£]+(?:EUR)?$")]
    private static partial Regex ProtectedDataRegex();

    [GeneratedRegex(@"^(Q|D|P|A)0{1,3}$")]
    private static partial Regex WatermarkCodeRegex();

    private bool IsWatermark(Word word)
    {
        string text = word.Text.ToUpperInvariant().Trim();

        // 1. Bouclier Absolu (Dates, Montants, Numéros)
        if (ProtectedDataRegex().IsMatch(text)) return false;

        // Protection des petits mots
        if ((text == "EN" || text == "S" || text == "P" || text == "E" || text == "C" || text == "I" || text == "M" || text == "E" || text == "N" || text == "Q" || text == "D" || text == "MEN" || text == "SP" || text == "SPE" || text == "SPEC") &&
             word.Letters.Count > 0 && word.Letters.Max(l => l.PointSize) <= 15.0)
        {
            return false;
        }

        // 2. Filtrage par taille
        if (word.Letters.Count > 0 && word.Letters.Max(l => l.PointSize) > 18.0) return true;

        // 3. Filtrage par texte
        if (text.Contains("SPECIMEN") || text.Contains("SPECIME") || text.Contains("SPECIM") || text.Contains("PECIMEN") || text.Contains("ECIMEN") || text.Contains("CIMEN") || text == "SPECI" || text == "IMEN" || text == "TOTEIN" || text == "TEST")
        {
            return true;
        }

        if (WatermarkCodeRegex().IsMatch(text)) return true;

        return false;
    }

    public string NormalizePdfText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string flatText = WhitespaceRegex().Replace(input, " ");
        flatText = flatText
            .Replace(". ", ".\n")
            .Replace("? ", "?\n")
            .Replace("! ", "!\n")
            .Replace(": ", ":\n")
            .Replace("•", "\n• ")
            .Replace(" o ", "\n o ");

        var lines = flatText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n", lines);
    }
}