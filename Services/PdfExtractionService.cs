using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content; // Ajout nécessaire pour manipuler l'objet 'Word'

namespace PDFComparison.Services;

public partial class PdfExtractionService
{
    // Utilisation du Source Generator de Regex pour éviter de recompiler l'expression à chaque appel
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // Regex pour nettoyer le texte brut des filigranes lors de l'extraction textuelle
    // (?i) rend la regex insensible à la casse. \b assure qu'on match le mot entier.
    [GeneratedRegex(@"(?i)\b(specimen|Q000|D000|P000|A000)\b")]
    private static partial Regex WatermarkTextRegex();

    public string ExtractTextFast(string pdfPath)
    {
        var sb = new StringBuilder();
        var options = new ParsingOptions { ClipPaths = false };

        using var document = PdfDocument.Open(pdfPath, options);
        foreach (var page in document.GetPages())
        {
            // On récupère le texte brut de la page
            string rawText = page.Text;

            // ON DÉTRUIT LE FILIGRANE DU TEXTE BRUT :
            // Cela empêchera les mots "SPECIMEN" d'apparaître dans le rapport de synthèse écrit
            string cleanText = WatermarkTextRegex().Replace(rawText, "");

            sb.AppendLine(cleanText);
        }

        return sb.ToString();
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

                // ==========================================
                // BOUCLIER ANTI-FILIGRANE : On ignore ce mot s'il s'agit d'un filigrane
                // ==========================================
                if (IsWatermark(word))
                    continue;

                words.Add(new PdfWordInfo
                {
                    Text = word.Text,
                    Letters = word.Letters,
                    PageNumber = page.Number
                });
            }
        }

        return words;
    }

    // MÉTHODE D'IDENTIFICATION DES FILIGRANES
    private bool IsWatermark(Word word)
    {
        // 1. FILTRAGE PAR LA TAILLE (Texte "très grand")
        // Les textes normaux dépassent rarement 16-20pt. Les filigranes font souvent > 40pt.
        // CORRECTION : Utilisation de 35.0 (double) au lieu de 35m (decimal) pour éviter l'erreur CS0019
        if (word.Letters.Count > 0 && word.Letters.Max(l => l.PointSize) > 35.0)
        {
            return true;
        }

        // 2. FILTRAGE PAR LE TEXTE EXACT
        string text = word.Text.ToUpperInvariant().Trim();

        if (text.Contains("SPECIMEN") ||
            text == "Q000" ||
            text == "D000" ||
            text == "P000" ||
            text == "A000")
        {
            return true;
        }

        return false;
    }

    public string NormalizePdfText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        // Remplacement ultra-rapide via la Regex générée à la compilation
        string flatText = WhitespaceRegex().Replace(input, " ");

        // Chaînage propre pour plus de lisibilité
        flatText = flatText
            .Replace(". ", ".\n")
            .Replace("? ", "?\n")
            .Replace("! ", "!\n")
            .Replace(": ", ":\n")
            .Replace("•", "\n• ")
            .Replace(" o ", "\n o ");

        // OPTIMISATION MAJEURE :
        // L'utilisation combinée de RemoveEmptyEntries et TrimEntries remplace totalement LINQ.
        // C'est beaucoup plus rapide et ça évite de créer plusieurs tableaux intermédiaires en mémoire.
        var lines = flatText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Join("\n", lines);
    }
}