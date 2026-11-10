using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig.Content;

namespace PDFComparison.Services;

public interface IPdfWatermarkFilterService
{
    string CleanRawText(string rawText);
    bool IsWatermark(Word word);
}

public partial class PdfWatermarkFilterService : IPdfWatermarkFilterService
{
    private readonly IConfigurationService? _configService;

    [GeneratedRegex(@"\b(?i)(specimen|specimens|ecimien|totein|Q000|D000|P000|A000)\b")]
    private static partial Regex DefaultWatermarkTextRegex();

    [GeneratedRegex(@"\[\s*DOCUMENT\s+(SOURCE|CIBLE).*?\]", RegexOptions.IgnoreCase)]
    private static partial Regex StampRegex();

    [GeneratedRegex(@"(?i)^[\d.,/\-\s€$£]+(?:EUR)?$")]
    private static partial Regex ProtectedDataRegex();

    [GeneratedRegex(@"(?i)^(Q|D|P|A)0{1,3}$")]
    private static partial Regex WatermarkCodeRegex();

    [GeneratedRegex(@"#[A-Z0-9_]+#", RegexOptions.IgnoreCase)]
    private static partial Regex SignatureAnchorRegex();

    private static readonly string[] DefaultExactWatermarkWords =
    {
        "SPECIMEN", "SPECIMENS", "SPECIME", "SPECIM", "PECIMEN", "ECIMEN", "TOTEIN", "ECIMIEN"
    };

    private static readonly string[] DefaultLargeWatermarkFragments =
    {
        "SPECIMEN", "SPECIME", "SPECIM", "PECIMEN", "ECIMEN", "CIMEN", "SPECI", "IMEN", "TOTEIN", "TEST", "ECIMIEN"
    };

    public PdfWatermarkFilterService(IConfigurationService? configService = null)
    {
        _configService = configService;
    }

    private ComparisonProfile? Profile => _configService?.ActiveProfile;

    public string CleanRawText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;

        string textWithoutAnchors = SignatureAnchorRegex().Replace(rawText, "");

        var exactWords = GetExactWatermarkWords();
        string textWithoutWatermark;

        if (exactWords.Count > 0 && _configService != null)
        {
            string pattern = $@"\b(?:{string.Join("|", exactWords.Select(Regex.Escape))}|Q000|D000|P000|A000)\b";
            var dynamicRegex = _configService.TryGetCompiledRegex(pattern, ignoreCase: true) ?? DefaultWatermarkTextRegex();
            textWithoutWatermark = dynamicRegex.Replace(textWithoutAnchors, "");
        }
        else
        {
            textWithoutWatermark = DefaultWatermarkTextRegex().Replace(textWithoutAnchors, "");
        }

        return StampRegex().Replace(textWithoutWatermark, "");
    }

    public bool IsWatermark(Word word)
    {
        if (word == null || word.Letters.Count == 0) return false;

        string text = word.Text.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        if (SignatureAnchorRegex().IsMatch(text)) return true;

        if (ProtectedDataRegex().IsMatch(text)) return false;

        if (WatermarkCodeRegex().IsMatch(text)) return true;

        var exactWords = GetExactWatermarkWords();
        if (exactWords.Contains(text)) return true;

        double maxPointSize = 0;
        foreach (var letter in word.Letters)
        {
            if (letter.PointSize > maxPointSize)
            {
                maxPointSize = letter.PointSize;
            }
        }

        double minFragmentFontSize = Profile?.WatermarkFragmentMinFontSizePt ?? 16.0;
        double maxAllowedFontSize = Profile?.MaxAllowedFontSizePt ?? 42.0;

        if (maxPointSize >= minFragmentFontSize)
        {
            foreach (var fragment in GetLargeWatermarkFragments())
            {
                if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (maxPointSize > maxAllowedFontSize) return true;

        return false;
    }

    private HashSet<string> GetExactWatermarkWords()
    {
        string? csv = Profile?.ExactWatermarkWordsCsv;
        if (string.IsNullOrWhiteSpace(csv))
        {
            return new HashSet<string>(DefaultExactWatermarkWords, StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(
            csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    private string[] GetLargeWatermarkFragments()
    {
        string? csv = Profile?.LargeWatermarkFragmentsCsv;
        if (string.IsNullOrWhiteSpace(csv))
        {
            return DefaultLargeWatermarkFragments;
        }

        return csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
