using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PDFComparison.Services;

public interface ISemanticSimilarityService
{
    bool IsDate(string text);
    bool IsNumber(string text);
    bool IsVolatile(string text);
    bool AreConceptuallySimilar(string a, string b);
}

public partial class SemanticSimilarityService : ISemanticSimilarityService
{
    // MISE À JOUR ICI : Ajout de \s et + pour tolérer les espaces accidentels causés par l'OCR
    [GeneratedRegex(@"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^\d{1,3}([.,]\d{3})*([.,]\d+)?%?$")]
    private static partial Regex NumRegex();

    [GeneratedRegex(@"^[A-Z0-9-]{5,}$")]
    private static partial Regex CodeRegex();

    // OPTIMISATION : Utilisation d'un HashSet (O(1)) au lieu d'un Array (O(N)).
    // OrdinalIgnoreCase permet de gérer "Le", "LE" et "le" automatiquement.
    // Ajout de EN et ES pour combler la faille du "Bouclier Anti-Gruyère".
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
       // FR
       "le", "la", "les", "un", "une", "des", "de", "du", "et", "ou", "a", "à", "au", "aux", "en", "par", "sur", "pour", "avec", "dans", "se", "ce", "ces", "son", "sa", "ses",
       // DE
       "der", "die", "das", "den", "dem", "ein", "eine", "einer", "einem", "einen", "und", "oder", "von", "zu", "in", "im", "am", "auf", "für", "mit",
       // NL
       "het", "een", "of", "te", "van", "naar", "voor", "door", "op", "aan", "deze", "eur",
       // EN
       "the", "a", "an", "and", "or", "to", "of", "in", "on", "for", "with", "as", "by", "at", "it", "is", "that", "this",
       // ES
       "el", "la", "los", "las", "un", "una", "unos", "unas", "y", "o", "de", "en", "por", "para", "con", "su", "sus", "al", "del"
    };

    public bool IsDate(string text) => DateRegex().IsMatch(text);

    public bool IsNumber(string text) => NumRegex().IsMatch(text);

    public bool IsVolatile(string text)
    {
        // CORRECTION ICI : On retire IsDate(text).
        // Une date ne doit pas être volatile car elle est très spécifique.
        return StopWords.Contains(text) || text.Length <= 4 || IsNumber(text);
    }

    public bool AreConceptuallySimilar(string a, string b)
    {
        if (IsDate(a) && IsDate(b)) return true;
        if (IsNumber(a) && IsNumber(b)) return true;
        if (CodeRegex().IsMatch(a) && CodeRegex().IsMatch(b)) return true;

        if (a.Length >= 4 && b.Length >= 4)
        {
            // CORRECTION ICI : Remplacement de .Contains() par une Regex stricte avec \b
            // Cela empêche un petit mot comme "elle" de s'associer à l'intérieur de "réelle".
            if (Regex.IsMatch(b, $@"\b{Regex.Escape(a)}\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(a, $@"\b{Regex.Escape(b)}\b", RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        if (a.Length >= 4 && b.Length >= 4 && Math.Abs(a.Length - b.Length) <= 2)
        {
            int distance = CalculateLevenshtein(a, b);
            int allowed = Math.Max(1, Math.Min(a.Length, b.Length) / 4);
            if (distance <= allowed) return true;
        }

        return false;
    }

    // OPTIMISATION MÉMOIRE : Algorithme de Levenshtein avec 2 tableaux 1D.
    // Évite l'allocation d'une matrice [,] qui surchargerait le Garbage Collector.
    private int CalculateLevenshtein(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
        if (string.IsNullOrEmpty(t)) return s.Length;

        int[] v0 = new int[t.Length + 1];
        int[] v1 = new int[t.Length + 1];

        for (int i = 0; i < v0.Length; i++)
            v0[i] = i;

        for (int i = 0; i < s.Length; i++)
        {
            v1[0] = i + 1;

            for (int j = 0; j < t.Length; j++)
            {
                int cost = (s[i] == t[j]) ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }

            for (int j = 0; j < v0.Length; j++)
                v0[j] = v1[j];
        }

        return v1[t.Length];
    }
}