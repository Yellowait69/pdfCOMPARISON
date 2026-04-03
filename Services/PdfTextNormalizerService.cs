using System;
using System.Text;

namespace PDFComparison.Services;

public interface IPdfTextNormalizerService
{
    string NormalizePdfText(string input);
}

public class PdfTextNormalizerService : IPdfTextNormalizerService
{
    public string NormalizePdfText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // OPTIMISATION 1 : Remplacement de la Regex par un parcours manuel ultra-rapide O(N)
        // On alloue un StringBuilder de la taille du texte initial pour éviter les redimensionnements
        var sb = new StringBuilder(input.Length);
        bool lastWasWhitespace = false;

        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasWhitespace)
                {
                    sb.Append(' ');
                    lastWasWhitespace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasWhitespace = false;
            }
        }

        // OPTIMISATION 2 : Remplacements In-Place (Zéro allocation mémoire)
        sb.Replace(". ", ".\n");
        sb.Replace("? ", "?\n");
        sb.Replace("! ", "!\n");
        sb.Replace(": ", ":\n");
        sb.Replace("•", "\n• ");
        sb.Replace(" o ", "\n o ");

        // OPTIMISATION 3 : Nettoyage final
        // C'est le seul moment où l'on crée de nouvelles chaînes pour nettoyer les espaces en début/fin de ligne
        var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Join("\n", lines);
    }
}