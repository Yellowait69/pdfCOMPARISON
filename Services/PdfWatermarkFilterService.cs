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
    [GeneratedRegex(@"(?i)(specimen|cimen|speci|men|test|totein|ecimien|Q000|D000|P000|A000)")]
    private static partial Regex WatermarkTextRegex();

    [GeneratedRegex(@"\[\s*DOCUMENT\s+(SOURCE|CIBLE).*?\]", RegexOptions.IgnoreCase)]
    private static partial Regex StampRegex();

    [GeneratedRegex(@"(?i)^[\d.,/\-\s€$£]+(?:EUR)?$")]
    private static partial Regex ProtectedDataRegex();

    [GeneratedRegex(@"(?i)^(Q|D|P|A)0{1,3}$")]
    private static partial Regex WatermarkCodeRegex();

    [GeneratedRegex(@"#[A-Z0-9_]+#", RegexOptions.IgnoreCase)]
    private static partial Regex SignatureAnchorRegex();

    private static readonly HashSet<string> SafeShortWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "EN", "S", "P", "E", "C", "I", "M", "N", "Q", "D", "MEN", "SP", "SPE", "SPEC"
    };

    private static readonly string[] WatermarkFragments =
    {
        "SPECIMEN", "SPECIME", "SPECIM", "PECIMEN", "ECIMEN", "CIMEN", "SPECI", "IMEN", "TOTEIN", "TEST", "ECIMIEN"
    };

    public string CleanRawText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;

        string textWithoutAnchors = SignatureAnchorRegex().Replace(rawText, "");
        string textWithoutWatermark = WatermarkTextRegex().Replace(textWithoutAnchors, "");
        return StampRegex().Replace(textWithoutWatermark, "");
    }

    public bool IsWatermark(Word word)
    {
        if (word == null || word.Letters.Count == 0) return false;

        string text = word.Text.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        if (SignatureAnchorRegex().IsMatch(text)) return true;

        if (ProtectedDataRegex().IsMatch(text)) return false;

        double maxPointSize = 0;
        foreach (var letter in word.Letters)
        {
            if (letter.PointSize > maxPointSize)
            {
                maxPointSize = letter.PointSize;
            }
        }

        if (maxPointSize <= 15.0 && SafeShortWords.Contains(text))
        {
            return false;
        }

        if (maxPointSize > 18.0) return true;

        foreach (var fragment in WatermarkFragments)
        {
            if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (WatermarkCodeRegex().IsMatch(text)) return true;

        return false;
    }
}