using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig;

namespace PDFComparison.Services;

public partial class PdfExtractionService
{
    // Utilisation du Source Generator de Regex pour éviter de recompiler l'expression à chaque appel
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public string ExtractTextFast(string pdfPath)
    {
        // On peut éventuellement définir une capacité initiale au StringBuilder si on sait
        // que les PDF sont gros, pour éviter les redimensionnements internes.
        var sb = new StringBuilder();
        var options = new ParsingOptions { ClipPaths = false };

        using var document = PdfDocument.Open(pdfPath, options);
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(page.Text);
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
                if (!string.IsNullOrWhiteSpace(word.Text))
                {
                    words.Add(new PdfWordInfo
                    {
                        Text = word.Text,
                        Letters = word.Letters,
                        PageNumber = page.Number
                    });
                }
            }
        }

        return words;
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

        // OPTIMISATION MAJEURE ICI :
        // L'utilisation combinée de RemoveEmptyEntries et TrimEntries (apparu dans .NET 5)
        // remplace totalement ton LINQ (.Select(l => l.Trim()).Where(l => l.Length > 0)).
        // C'est beaucoup plus rapide et ça évite de créer plusieurs tableaux intermédiaires en mémoire.
        var lines = flatText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Join("\n", lines);
    }
}