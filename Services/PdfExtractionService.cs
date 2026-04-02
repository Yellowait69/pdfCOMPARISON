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
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?i)(specimen|cimen|speci|men|test|totein|Q000|D000|P000|A000)")]
    private static partial Regex WatermarkTextRegex();

    [GeneratedRegex(@"\b\d{1,2}[./-]\d{1,2}[./-]\d{2,4}\b")]
    private static partial Regex DateRegex();

    public string ExtractTextFast(string pdfPath)
    {
        var sb = new StringBuilder();
        var options = new ParsingOptions { ClipPaths = false };

        using var document = PdfDocument.Open(pdfPath, options);
        foreach (var page in document.GetPages())
        {
            string rawText = page.Text;
            string cleanText = WatermarkTextRegex().Replace(rawText, "");

            // SÉCURITÉ EXTRA : Suppression textuelle des tampons de rapports
            cleanText = Regex.Replace(cleanText, @"\[\s*DOCUMENT\s+(SOURCE|CIBLE).*?\]", "", RegexOptions.IgnoreCase);

            sb.AppendLine(cleanText);
        }

        return MaskRepeatingTextElements(sb.ToString());
    }

    public List<PdfWordInfo> ExtractWords(string pdfPath)
    {
        var words = new List<PdfWordInfo>();
        var headerDatesToIgnore = new HashSet<string>();

        using var doc = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });

        // ==========================================
        // PASSE 1 : Capturer les dates d'en-tête (Y > 800)
        // ==========================================
        foreach (var page in doc.GetPages())
        {
            foreach (var word in page.GetWords())
            {
                if (word.BoundingBox.BottomLeft.Y > 800)
                {
                    var match = DateRegex().Match(word.Text);
                    if (match.Success)
                    {
                        headerDatesToIgnore.Add(match.Value);
                    }
                }
            }
        }

        // ==========================================
        // PASSE 2 : Extraction normale et filtrage spatial
        // ==========================================
        foreach (var page in doc.GetPages())
        {
            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text))
                    continue;

                if (IsWatermark(word))
                    continue;

                // A. Marge gauche (Exclut les codes-barres verticaux)
                if (word.BoundingBox.BottomLeft.X < 50)
                    continue;

                // B. En-tête (Exclut le tampon et la date d'en-tête physiquement)
                if (word.BoundingBox.BottomLeft.Y > 800)
                    continue;

                // C. Pied de page (Exclut les numéros de page originaux, ex: "- 2 -")
                if (word.BoundingBox.BottomLeft.Y < 40)
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
        // 3. MASQUAGE INTELLIGENT (On transmet les dates d'en-tête)
        // ==========================================
        MaskRepeatingWordElements(words, headerDatesToIgnore);

        return words;
    }

    private string MaskRepeatingTextElements(string text)
    {
        var dateMatches = DateRegex().Matches(text);
        var dateCounts = dateMatches.Cast<Match>().GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.Count());

        foreach (var kvp in dateCounts.Where(k => k.Value >= 2))
        {
            text = text.Replace(kvp.Key, "[DATE_IGNORE]");
        }

        var uppercaseSeqRegex = new Regex(@"\b[A-ZÀ-Ÿ]{2,}(?:[\s\-']+[A-ZÀ-Ÿ]{2,})+\b");
        var nameMatches = uppercaseSeqRegex.Matches(text);
        var nameCounts = nameMatches.Cast<Match>().GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.Count());

        foreach (var kvp in nameCounts.Where(k => k.Value >= 2))
        {
            text = text.Replace(kvp.Key, "[NOM_IGNORE]");
        }

        return text;
    }

    // ==============================================================
    // LOGIQUE AVANCÉE DE MASQUAGE (Avec tolérance Ponctuation)
    // ==============================================================

    private bool IsUppercaseWord(string text)
    {
        if (text == "[DATE_IGNORE]" || text == "[NOM_IGNORE]") return false;

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

    private string GetOnlyLetters(string text)
    {
        return new string(text.Where(char.IsLetter).ToArray());
    }

    private void MaskRepeatingWordElements(List<PdfWordInfo> words, HashSet<string> headerDates)
    {
        // A. Comptage des dates
        var dateCounts = new Dictionary<string, int>();
        foreach (var w in words)
        {
            var match = DateRegex().Match(w.Text);
            if (match.Success)
            {
                string dateVal = match.Value;
                if (!dateCounts.ContainsKey(dateVal)) dateCounts[dateVal] = 0;
                dateCounts[dateVal]++;
            }
        }

        // B. Trouver et compter les séquences de majuscules (Noms/Prénoms)
        var uppercaseSequences = new Dictionary<string, int>();
        var currentSequenceIndices = new List<int>();

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
                    string seqKey = string.Join(" ", currentSequenceIndices.Select(idx => GetOnlyLetters(words[idx].Text)));
                    if (!uppercaseSequences.ContainsKey(seqKey)) uppercaseSequences[seqKey] = 0;
                    uppercaseSequences[seqKey]++;
                }
                currentSequenceIndices.Clear();
            }
        }

        // C. Remplacer les dates dans la liste
        for (int i = 0; i < words.Count; i++)
        {
            var match = DateRegex().Match(words[i].Text);
            if (match.Success)
            {
                string dateVal = match.Value;
                // SI la date vient de l'en-tête OU qu'elle se répète 2+ fois, on l'ignore.
                if (headerDates.Contains(dateVal) || (dateCounts.TryGetValue(dateVal, out int count) && count >= 2))
                {
                    words[i] = new PdfWordInfo { Text = "[DATE_IGNORE]", Letters = words[i].Letters, PageNumber = words[i].PageNumber };
                }
            }
        }

        // D. Remplacer les séquences de noms
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
                    string seqKey = string.Join(" ", currentSequenceIndices.Select(idx => GetOnlyLetters(words[idx].Text)));
                    if (uppercaseSequences.TryGetValue(seqKey, out int count) && count >= 2)
                    {
                        for (int j = 0; j < currentSequenceIndices.Count; j++)
                        {
                            int wordIdx = currentSequenceIndices[j];
                            string newText = j == 0 ? "[NOM_IGNORE]" : "";
                            words[wordIdx] = new PdfWordInfo { Text = newText, Letters = words[wordIdx].Letters, PageNumber = words[wordIdx].PageNumber };
                        }
                    }
                }
                currentSequenceIndices.Clear();
            }
        }

        // E. Retirer les mots que nous avons vidés
        words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
    }

    // ==============================================================
    // RESTE DU FILTRAGE ANTI-FILIGRANE
    // ==============================================================

    [GeneratedRegex(@"^[\d.,/\-\s€$£]+(?:EUR)?$")]
    private static partial Regex ProtectedDataRegex();

    [GeneratedRegex(@"^(Q|D|P|A)0{1,3}$")]
    private static partial Regex WatermarkCodeRegex();

    private bool IsWatermark(Word word)
    {
        string text = word.Text.ToUpperInvariant().Trim();

        if (ProtectedDataRegex().IsMatch(text)) return false;

        if ((text == "EN" || text == "S" || text == "P" || text == "E" || text == "C" || text == "I" || text == "M" || text == "E" || text == "N" || text == "Q" || text == "D" || text == "MEN" || text == "SP" || text == "SPE" || text == "SPEC") &&
             word.Letters.Count > 0 && word.Letters.Max(l => l.PointSize) <= 15.0)
        {
            return false;
        }

        if (word.Letters.Count > 0 && word.Letters.Max(l => l.PointSize) > 18.0) return true;

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