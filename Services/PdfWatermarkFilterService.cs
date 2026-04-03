using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;

namespace PDFComparison.Services;

public interface IPdfWatermarkFilterService
{
    string CleanRawText(string rawText);
    bool IsWatermark(Word word);
}

public partial class PdfWatermarkFilterService : IPdfWatermarkFilterService
{
    // Ajout de "ecimien" à la Regex
    [GeneratedRegex(@"(?i)(specimen|cimen|speci|men|test|totein|ecimien|Q000|D000|P000|A000)")]
    private static partial Regex WatermarkTextRegex();

    [GeneratedRegex(@"\[\s*DOCUMENT\s+(SOURCE|CIBLE).*?\]", RegexOptions.IgnoreCase)]
    private static partial Regex StampRegex();

    // OPTIMISATION : Ajout de (?i) pour rendre EUR insensible à la casse nativement
    [GeneratedRegex(@"(?i)^[\d.,/\-\s€$£]+(?:EUR)?$")]
    private static partial Regex ProtectedDataRegex();

    // OPTIMISATION : Ajout de (?i) pour la casse
    [GeneratedRegex(@"(?i)^(Q|D|P|A)0{1,3}$")]
    private static partial Regex WatermarkCodeRegex();

    // OPTIMISATION : Remplacement de la condition à rallonge par un HashSet O(1) insensible à la casse
    private static readonly HashSet<string> SafeShortWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "EN", "S", "P", "E", "C", "I", "M", "N", "Q", "D", "MEN", "SP", "SPE", "SPEC"
    };

    // CORRECTION ICI : Ajout de "ECIMIEN" à la liste des fragments
    private static readonly string[] WatermarkFragments =
    {
        "SPECIMEN", "SPECIME", "SPECIM", "PECIMEN", "ECIMEN", "CIMEN", "SPECI", "IMEN", "TOTEIN", "TEST", "ECIMIEN"
    };

    public string CleanRawText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;

        string textWithoutWatermark = WatermarkTextRegex().Replace(rawText, "");
        return StampRegex().Replace(textWithoutWatermark, "");
    }

    public bool IsWatermark(Word word)
    {
        if (word == null || word.Letters.Count == 0) return false;

        string text = word.Text.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        // 1. Les données protégées (Nombres, devises) ne sont jamais des filigranes
        if (ProtectedDataRegex().IsMatch(text)) return false;

        // OPTIMISATION : On calcule le PointSize maximum en une seule passe sans allouer de mémoire avec LINQ
        double maxPointSize = 0;
        foreach (var letter in word.Letters)
        {
            if (letter.PointSize > maxPointSize)
            {
                maxPointSize = letter.PointSize;
            }
        }

        // 2. Faux positifs connus : petits bouts de mots issus de filigranes brisés
        // Si la police est standard (<= 15), on les autorise
        if (maxPointSize <= 15.0 && SafeShortWords.Contains(text))
        {
            return false;
        }

        // 3. Détection géométrique : Si c'est écrit en très gros (> 18pt), c'est un filigrane
        if (maxPointSize > 18.0) return true;

        // 4. Détection textuelle : Vérification des fragments de filigranes classiques
        // StringComparison.OrdinalIgnoreCase évite de créer un string ToUpperInvariant() en mémoire
        foreach (var fragment in WatermarkFragments)
        {
            if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 5. Détection par codes spécifiques (Q000, D00, etc.)
        if (WatermarkCodeRegex().IsMatch(text)) return true;

        return false;
    }
}