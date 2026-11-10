using System;
using System.Text;
using System.Text.RegularExpressions;

namespace PDFComparison.Services;

public interface IPdfTextNormalizerService
{
    string NormalizePdfText(string input);
}

public partial class PdfTextNormalizerService : IPdfTextNormalizerService
{
    // Découpe intelligente des phrases après un point, un point d'interrogation ou d'exclamation,
    // tout en ignorant les abréviations courantes (M., Mme., Art., cf., etc., N°, réf., p., fig., etc.)
    // et en conservant "Libellé : Valeur" sur la même ligne logique pour préserver le contexte métier.
    [GeneratedRegex(@"(?<!\b(?:M|Mr|Mme|Mlle|Dr|Pr|Me|St|Ste|Art|cf|etc|ex|env|réf|Ref|No|N°|p|fig|vol|ch|inc|ltd|co|corp|v|al|av|bd|imp|sq|pl|all|hab|tel|tél|fax|max|min|approx))([.?!])\s+(?=[A-ZÀ-ÖØ-Þ•\-])")]
    private static partial Regex SmartSentenceSplitRegex();

    public string NormalizePdfText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var sb = new StringBuilder(input.Length);
        bool lastWasWhitespace = false;

        foreach (char c in input)
        {
            if (c == '\r' || c == '\n')
            {
                if (sb.Length > 0 && sb[^1] != '\n')
                {
                    sb.Append('\n');
                }
                lastWasWhitespace = true;
            }
            else if (char.IsWhiteSpace(c) || c == '\u00A0' || c == '\u202F' || c == '\u2009' || c == '\u2007')
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

        sb.Replace(" • ", "\n• ");
        sb.Replace(" - ", "\n- ");

        string normalized = SmartSentenceSplitRegex().Replace(sb.ToString(), "$1\n");

        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n", lines);
    }
}
