using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;

namespace PDFComparison.Services;

public interface ITextDiffSummaryService
{
    (int DifferencesCount, List<DiffSummaryBlock> Blocks, SideBySideDiffModel DiffLinesModel) BuildTextSummary(string cleanSource, string cleanTarget);

    List<DiffSummaryBlock> BuildVerifiedSummaryBlocks(
        string cleanSource,
        string cleanTarget,
        List<List<(string CleanText, List<LetterLoc> Letters)>> sourceLinesList,
        List<List<(string CleanText, List<LetterLoc> Letters)>> targetLinesList,
        VisualHighlights confirmedHighlights);
}

public partial class TextDiffSummaryService : ITextDiffSummaryService
{
    private readonly IConfigurationService? _configService;

    [GeneratedRegex(@"\d+(?:[.,]\d+)?|\b(?:€|\$|£|%|eur|usd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmountOrNumberRegex();

    [GeneratedRegex(@"-?\d{1,3}(?:[\s\u00A0\u202F.]\d{3})*(?:[.,]\d+)?|-?\d+(?:[.,]\d+)?")]
    private static partial Regex NumericValueExtractorRegex();

    [GeneratedRegex(@"\b\d{1,2}[-/\.]\d{1,2}[-/\.]\d{2,4}\b")]
    private static partial Regex DatePatternRegex();

    [GeneratedRegex(@"(?i)\b(prix|pénalité|pénalités|résiliation|ttc|ht|garantie|article|euro|taxe|montant|facture|price|penalty|termination|vat|warranty|tax|amount|invoice)\b")]
    private static partial Regex DefaultCriticalKeywordRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordTokenRegex();

    public TextDiffSummaryService(IConfigurationService? configService = null)
    {
        _configService = configService;
    }

    public (int DifferencesCount, List<DiffSummaryBlock> Blocks, SideBySideDiffModel DiffLinesModel) BuildTextSummary(string cleanSource, string cleanTarget)
    {
        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        var diffLines = diffBuilder.BuildDiffModel(cleanSource ?? string.Empty, cleanTarget ?? string.Empty);
        var blocks = new List<DiffSummaryBlock>();
        int linesCount = diffLines.NewText.Lines.Count;

        var sumDel = new Dictionary<string, int>(StringComparer.Ordinal);
        var sumIns = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < linesCount; i++)
        {
            var oldLine = diffLines.OldText.Lines[i];
            var newLine = diffLines.NewText.Lines[i];

            if (oldLine.Type is ChangeType.Deleted or ChangeType.Modified && oldLine.Text.Trim() is { Length: > 0 } oldTxt)
            {
                sumDel[oldTxt] = sumDel.GetValueOrDefault(oldTxt) + 1;
            }

            if (newLine.Type is ChangeType.Inserted or ChangeType.Modified && newLine.Text.Trim() is { Length: > 0 } newTxt)
            {
                sumIns[newTxt] = sumIns.GetValueOrDefault(newTxt) + 1;
            }
        }

        var skipDel = new Dictionary<string, int>(StringComparer.Ordinal);
        var skipIns = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var kvp in sumDel)
        {
            if (sumIns.TryGetValue(kvp.Key, out int insCount))
            {
                int moves = Math.Min(kvp.Value, insCount);
                skipDel[kvp.Key] = moves;
                skipIns[kvp.Key] = moves;
            }
        }

        List<string> currentDel = new();
        List<string> currentIns = new();
        string ctxBefore = string.Empty;
        int lastDiffIndex = -1;

        for (int i = 0; i < linesCount; i++)
        {
            var newLine = diffLines.NewText.Lines[i];
            var oldLine = diffLines.OldText.Lines[i];

            bool isDel = oldLine.Type is ChangeType.Deleted or ChangeType.Modified;
            bool isIns = newLine.Type is ChangeType.Inserted or ChangeType.Modified;

            if (isDel && oldLine.Text.Trim() is { Length: > 0 } txtDel && skipDel.GetValueOrDefault(txtDel) > 0)
            {
                skipDel[txtDel]--;
                isDel = false;
            }

            if (isIns && newLine.Text.Trim() is { Length: > 0 } txtIns && skipIns.GetValueOrDefault(txtIns) > 0)
            {
                skipIns[txtIns]--;
                isIns = false;
            }

            if (isDel || isIns)
            {
                if (currentDel.Count == 0 && currentIns.Count == 0)
                {
                    ctxBefore = GetValidContextLine(diffLines.NewText.Lines, i, -1);
                }

                if (isDel && !string.IsNullOrEmpty(oldLine.Text)) currentDel.Add(oldLine.Text);
                if (isIns && !string.IsNullOrEmpty(newLine.Text)) currentIns.Add(newLine.Text);

                lastDiffIndex = i;
            }
            else if (currentDel.Count > 0 || currentIns.Count > 0)
            {
                FlushBlocks(blocks, currentDel, currentIns, ctxBefore, GetValidContextLine(diffLines.NewText.Lines, lastDiffIndex, 1), 1);
                currentDel.Clear();
                currentIns.Clear();
            }
        }

        if (currentDel.Count > 0 || currentIns.Count > 0)
        {
            FlushBlocks(blocks, currentDel, currentIns, ctxBefore, string.Empty, 1);
        }

        return (blocks.Count, blocks, diffLines);
    }

    public List<DiffSummaryBlock> BuildVerifiedSummaryBlocks(
        string cleanSource,
        string cleanTarget,
        List<List<(string CleanText, List<LetterLoc> Letters)>> sourceLinesList,
        List<List<(string CleanText, List<LetterLoc> Letters)>> targetLinesList,
        VisualHighlights confirmedHighlights)
    {
        var (_, candidateBlocks, _) = BuildTextSummary(cleanSource, cleanTarget);

        var confirmedSrcSet = new HashSet<LetterLoc>(confirmedHighlights.SourceRed);
        var confirmedTrgSet = new HashSet<LetterLoc>(confirmedHighlights.TargetRed);

        var confirmedTokensToPage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in sourceLinesList)
        {
            foreach (var word in line)
            {
                if (word.Letters.Any(l => confirmedSrcSet.Contains(l)))
                {
                    foreach (Match m in WordTokenRegex().Matches(word.CleanText))
                    {
                        confirmedTokensToPage.TryAdd(m.Value, word.Letters[0].PageNumber);
                    }
                }
            }
        }

        foreach (var line in targetLinesList)
        {
            foreach (var word in line)
            {
                if (word.Letters.Any(l => confirmedTrgSet.Contains(l)))
                {
                    foreach (Match m in WordTokenRegex().Matches(word.CleanText))
                    {
                        confirmedTokensToPage.TryAdd(m.Value, word.Letters[0].PageNumber);
                    }
                }
            }
        }

        if (confirmedTokensToPage.Count == 0)
        {
            return new List<DiffSummaryBlock>();
        }

        var verifiedBlocks = new List<DiffSummaryBlock>();
        int defaultPage = confirmedHighlights.TargetRed.FirstOrDefault().PageNumber;
        if (defaultPage <= 0) defaultPage = confirmedHighlights.SourceRed.FirstOrDefault().PageNumber;
        if (defaultPage <= 0) defaultPage = 1;

        foreach (var block in candidateBlocks)
        {
            var diffWords = ExtractActuallyChangedTokens(block.OldText, block.NewText);
            if (diffWords.Count == 0) continue;

            int matchedPage = 0;
            foreach (var token in diffWords)
            {
                if (confirmedTokensToPage.TryGetValue(token, out int page))
                {
                    matchedPage = page;
                    break;
                }
            }

            if (matchedPage > 0)
            {
                block.PageNumber = matchedPage;
                ClassifyBlockSeverityAndCategory(block, diffWords);
                verifiedBlocks.Add(block);
            }
        }

        if (verifiedBlocks.Count == 0 && (confirmedHighlights.SourceRed.Count > 0 || confirmedHighlights.TargetRed.Count > 0))
        {
            string oldFallback = string.Join(" ", sourceLinesList
                .SelectMany(l => l)
                .Where(w => w.Letters.Any(loc => confirmedSrcSet.Contains(loc)))
                .Select(w => w.CleanText));

            string newFallback = string.Join(" ", targetLinesList
                .SelectMany(l => l)
                .Where(w => w.Letters.Any(loc => confirmedTrgSet.Contains(loc)))
                .Select(w => w.CleanText));

            var fallbackBlock = new DiffSummaryBlock
            {
                PageNumber = defaultPage,
                Type = (!string.IsNullOrEmpty(oldFallback) && !string.IsNullOrEmpty(newFallback))
                    ? ChangeType.Modified
                    : (!string.IsNullOrEmpty(newFallback) ? ChangeType.Inserted : ChangeType.Deleted),
                OldText = oldFallback,
                NewText = newFallback
            };
            ClassifyBlockSeverityAndCategory(fallbackBlock, ExtractActuallyChangedTokens(oldFallback, newFallback));
            verifiedBlocks.Add(fallbackBlock);
        }

        return verifiedBlocks;
    }

    private static List<string> ExtractActuallyChangedTokens(string oldText, string newText)
    {
        var oldTokens = WordTokenRegex().Matches(oldText ?? string.Empty).Select(m => m.Value.ToLowerInvariant()).ToList();
        var newTokens = WordTokenRegex().Matches(newText ?? string.Empty).Select(m => m.Value.ToLowerInvariant()).ToList();

        var oldCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in oldTokens) oldCounts[t] = oldCounts.GetValueOrDefault(t) + 1;

        var newCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in newTokens) newCounts[t] = newCounts.GetValueOrDefault(t) + 1;

        var changed = new List<string>();
        foreach (var kvp in oldCounts)
        {
            if (newCounts.GetValueOrDefault(kvp.Key) != kvp.Value)
                changed.Add(kvp.Key);
        }
        foreach (var kvp in newCounts)
        {
            if (oldCounts.GetValueOrDefault(kvp.Key) != kvp.Value && !changed.Contains(kvp.Key))
                changed.Add(kvp.Key);
        }

        return changed;
    }

    private void FlushBlocks(List<DiffSummaryBlock> blocks, List<string> dels, List<string> ins, string ctxBefore, string ctxAfter, int pageNumber)
    {
        if (dels.Count > 0 && ins.Count > 0)
        {
            string oldStr = string.Join("\n", dels);
            string newStr = string.Join("\n", ins);

            var modBlock = new DiffSummaryBlock
            {
                PageNumber = pageNumber,
                Type = ChangeType.Modified,
                ContextBefore = ctxBefore,
                ContextAfter = ctxAfter,
                OldText = oldStr,
                NewText = newStr
            };
            ClassifyBlockSeverityAndCategory(modBlock, ExtractActuallyChangedTokens(oldStr, newStr));
            blocks.Add(modBlock);
            return;
        }

        if (dels.Count > 0)
        {
            string oldStr = string.Join("\n", dels);
            var delBlock = new DiffSummaryBlock
            {
                PageNumber = pageNumber,
                Type = ChangeType.Deleted,
                ContextBefore = ctxBefore,
                ContextAfter = ctxAfter,
                OldText = oldStr,
                NewText = string.Empty
            };
            ClassifyBlockSeverityAndCategory(delBlock, ExtractActuallyChangedTokens(oldStr, string.Empty));
            blocks.Add(delBlock);
        }

        if (ins.Count > 0)
        {
            string newStr = string.Join("\n", ins);
            var insBlock = new DiffSummaryBlock
            {
                PageNumber = pageNumber,
                Type = ChangeType.Inserted,
                ContextBefore = ctxBefore,
                ContextAfter = ctxAfter,
                OldText = string.Empty,
                NewText = newStr
            };
            ClassifyBlockSeverityAndCategory(insBlock, ExtractActuallyChangedTokens(string.Empty, newStr));
            blocks.Add(insBlock);
        }
    }

    private void ClassifyBlockSeverityAndCategory(DiffSummaryBlock block, List<string> changedTokens)
    {
        string combinedChanged = string.Join(" ", changedTokens);
        string fullText = $"{block.OldText} {block.NewText}";

        Regex criticalRegex = (_configService != null && !string.IsNullOrWhiteSpace(_configService.ActiveProfile.CriticalKeywordsRegex))
            ? (_configService.TryGetCompiledRegex(_configService.ActiveProfile.CriticalKeywordsRegex, ignoreCase: true) ?? DefaultCriticalKeywordRegex())
            : DefaultCriticalKeywordRegex();

        if (DatePatternRegex().IsMatch(combinedChanged) || DatePatternRegex().IsMatch(fullText))
        {
            block.Category = "Date";
            block.Severity = DiffSeverity.Critical;
        }
        else if (AmountOrNumberRegex().IsMatch(combinedChanged))
        {
            if (TryComputeFinancialDelta(block.OldText, block.NewText, out decimal deltaVal, out string deltaFormatted, out bool isSameNumericValue))
            {
                block.FinancialDelta = deltaFormatted;
                if (isSameNumericValue)
                {
                    block.Category = "Formatage Numérique";
                    block.Severity = DiffSeverity.Minor;
                    return;
                }
            }

            block.Category = "Montant / Chiffre";
            block.Severity = DiffSeverity.Critical;
        }
        else if (criticalRegex.IsMatch(combinedChanged) || criticalRegex.IsMatch(fullText))
        {
            block.Category = "Clause Critique";
            block.Severity = DiffSeverity.Critical;
        }
        else if (changedTokens.Count > 0)
        {
            block.Category = "Texte";
            block.Severity = DiffSeverity.Major;
        }
        else
        {
            block.Category = "Formatage";
            block.Severity = DiffSeverity.Minor;
        }
    }

    private static bool TryComputeFinancialDelta(
        string? oldText,
        string? newText,
        out decimal delta,
        out string formattedDelta,
        out bool isSameNumericValue)
    {
        delta = 0m;
        formattedDelta = string.Empty;
        isSameNumericValue = false;

        if (string.IsNullOrWhiteSpace(oldText) || string.IsNullOrWhiteSpace(newText))
            return false;

        var oldNums = ExtractDecimals(oldText);
        var newNums = ExtractDecimals(newText);

        if (oldNums.Count == 0 || newNums.Count == 0)
            return false;

        // Trouve la première paire de nombres à la même position qui diffère (ou la dernière paire si tous sont égaux)
        int count = Math.Min(oldNums.Count, newNums.Count);
        decimal oldVal = oldNums[^1];
        decimal newVal = newNums[^1];

        for (int i = 0; i < count; i++)
        {
            if (Math.Abs(oldNums[i] - newNums[i]) >= 0.0001m)
            {
                oldVal = oldNums[i];
                newVal = newNums[i];
                break;
            }
        }

        delta = newVal - oldVal;
        if (Math.Abs(delta) < 0.0001m)
        {
            isSameNumericValue = true;
            formattedDelta = "Δ 0.00 (Valeur égale)";
            return true;
        }

        string sign = delta > 0 ? "+" : "";
        if (Math.Abs(oldVal) >= 0.0001m)
        {
            decimal pct = (delta / Math.Abs(oldVal)) * 100m;
            formattedDelta = string.Create(CultureInfo.InvariantCulture, $"Δ {sign}{delta:0.##} ({sign}{pct:0.1}%)");
        }
        else
        {
            formattedDelta = string.Create(CultureInfo.InvariantCulture, $"Δ {sign}{delta:0.##}");
        }

        return true;
    }

    private static List<decimal> ExtractDecimals(string text)
    {
        var list = new List<decimal>();
        foreach (Match m in NumericValueExtractorRegex().Matches(text))
        {
            string raw = m.Value.Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "");
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // Gestion normalisée des séparateurs décimaux FR (,) et EN (.)
            int lastComma = raw.LastIndexOf(',');
            int lastDot = raw.LastIndexOf('.');

            if (lastComma > lastDot)
            {
                raw = raw.Replace(".", "").Replace(',', '.');
            }
            else if (lastDot > lastComma && lastComma != -1)
            {
                raw = raw.Replace(",", "");
            }

            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal val))
            {
                list.Add(val);
            }
        }
        return list;
    }

    private string GetValidContextLine(List<DiffPiece> lines, int currentIndex, int direction)
    {
        int i = currentIndex + direction;
        int count = lines.Count;

        while (i >= 0 && i < count)
        {
            var line = lines[i];

            if (line.Type is not ChangeType.Imaginary && !string.IsNullOrWhiteSpace(line.Text))
            {
                return line.Text;
            }
            i += direction;
        }
        return string.Empty;
    }
}
