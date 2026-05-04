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

        sb.Replace(". ", ".\n");
        sb.Replace("? ", "?\n");
        sb.Replace("! ", "!\n");
        sb.Replace(": ", ":\n");
        sb.Replace(" • ", "\n• ");
        sb.Replace(" - ", "\n- ");
        sb.Replace(" o ", "\n o ");

        var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Join("\n", lines);
    }
}