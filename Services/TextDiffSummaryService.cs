using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
        VisualHighlights confirmedHighlights,
        IReadOnlyList<PdfWordInfo>? rawSourceWords = null,
        IReadOnlyList<PdfWordInfo>? rawTargetWords = null);
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

    [GeneratedRegex(@"(?i)\b(prix|pénalité|pénalités|résiliation|ttc|ht|garantie|article|euro|eur|taxe|montant|facture|prime|capital|rachat|versement|taux|frais|rente|police|bénéficiaire|iban|bic|price|penalty|termination|vat|warranty|tax|amount|invoice|bedrag|premie|totaal|kapitaal|kosten|afkoop|storting|overdracht|betaling|rekening|polis|begunstigde|waarborg|belasting|taks|intrest|rendement)\b")]
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
        return (blocks.Count, blocks, diffLines);
    }

    public List<DiffSummaryBlock> BuildVerifiedSummaryBlocks(
        string cleanSource,
        string cleanTarget,
        List<List<(string CleanText, List<LetterLoc> Letters)>> sourceLinesList,
        List<List<(string CleanText, List<LetterLoc> Letters)>> targetLinesList,
        VisualHighlights confirmedHighlights,
        IReadOnlyList<PdfWordInfo>? rawSourceWords = null,
        IReadOnlyList<PdfWordInfo>? rawTargetWords = null)
    {
        if (confirmedHighlights.SourceRed.Count == 0 && confirmedHighlights.TargetRed.Count == 0)
        {
            return new List<DiffSummaryBlock>();
        }

        var confirmedSrcRects = new HashSet<(int Page, decimal X, decimal Y)>(
            confirmedHighlights.SourceRed.Select(l => (l.PageNumber, Math.Round((decimal)l.BoundingBox.BottomLeft.X, 1), Math.Round(l.BaselineY, 1))));
        var confirmedTrgRects = new HashSet<(int Page, decimal X, decimal Y)>(
            confirmedHighlights.TargetRed.Select(l => (l.PageNumber, Math.Round((decimal)l.BoundingBox.BottomLeft.X, 1), Math.Round(l.BaselineY, 1))));

        var srcVisualLines = BuildReadableVisualLines(rawSourceWords, sourceLinesList, confirmedSrcRects);
        var trgVisualLines = BuildReadableVisualLines(rawTargetWords, targetLinesList, confirmedTrgRects);

        var affectedPages = srcVisualLines.Where(l => l.HasDiff).Select(l => l.PageNumber)
            .Concat(trgVisualLines.Where(l => l.HasDiff).Select(l => l.PageNumber))
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        var verifiedBlocks = new List<DiffSummaryBlock>();

        foreach (int pageNum in affectedPages)
        {
            var pageSrcLines = srcVisualLines.Where(l => l.PageNumber == pageNum).OrderBy(l => l.LineIndex).ToList();
            var pageTrgLines = trgVisualLines.Where(l => l.PageNumber == pageNum).OrderBy(l => l.LineIndex).ToList();

            // Regrouper les lignes modifiées consécutives en un seul bloc unifié
            var srcClusters = BuildConsecutiveDiffClusters(pageSrcLines);
            var trgClusters = BuildConsecutiveDiffClusters(pageTrgLines);

            bool[] usedTrg = new bool[trgClusters.Count];

            // Appairage des blocs consécutifs situés au même niveau vertical (Y) sur la page
            for (int i = 0; i < srcClusters.Count; i++)
            {
                var sCluster = srcClusters[i];
                var matchedTrgIndices = new List<int>();

                for (int j = 0; j < trgClusters.Count; j++)
                {
                    if (usedTrg[j]) continue;
                    var tCluster = trgClusters[j];

                    bool overlapsY = (sCluster.MinY - 48.0m <= tCluster.MaxY) && (tCluster.MinY - 48.0m <= sCluster.MaxY);
                    decimal centerDy = Math.Abs(sCluster.ReferenceY - tCluster.ReferenceY);

                    if (overlapsY || centerDy <= 55.0m)
                    {
                        matchedTrgIndices.Add(j);
                    }
                }

                if (matchedTrgIndices.Count > 0)
                {
                    foreach (int idx in matchedTrgIndices)
                    {
                        usedTrg[idx] = true;
                    }

                    string combinedTrgDiffText = string.Join(Environment.NewLine, matchedTrgIndices.Select(idx => trgClusters[idx].DiffOnlyText));
                    TryAddVerifiedBlock(
                        verifiedBlocks,
                        pageNum,
                        sCluster.DiffOnlyText,
                        combinedTrgDiffText,
                        sCluster.ContextBefore,
                        sCluster.ContextAfter);
                }
                else
                {
                    TryAddVerifiedBlock(
                        verifiedBlocks,
                        pageNum,
                        sCluster.DiffOnlyText,
                        string.Empty,
                        sCluster.ContextBefore,
                        sCluster.ContextAfter);
                }
            }

            // Blocs insérés dans Target qui n'avaient pas d'équivalent au même niveau dans Source
            for (int j = 0; j < trgClusters.Count; j++)
            {
                if (usedTrg[j]) continue;
                var tCluster = trgClusters[j];
                TryAddVerifiedBlock(
                    verifiedBlocks,
                    pageNum,
                    string.Empty,
                    tCluster.DiffOnlyText,
                    tCluster.ContextBefore,
                    tCluster.ContextAfter);
            }
        }

        // Fusionner les blocs consécutifs sur la même page si l'un est une suppression pure et le suivant un ajout pur
        verifiedBlocks = MergeAdjacentDeleteInsertBlocksOnSamePage(verifiedBlocks);

        return verifiedBlocks;
    }

    private void TryAddVerifiedBlock(
        List<DiffSummaryBlock> verifiedBlocks,
        int pageNumber,
        string oldText,
        string newText,
        string ctxBefore,
        string ctxAfter)
    {
        // Élaguer tout préfixe ou suffixe de mots identiques pour n'afficher QUE ce qui est différent à l'œil nu
        var (diffOldOnly, diffNewOnly, updatedBefore, updatedAfter) = TrimCommonPrefixAndSuffixWords(
            oldText,
            newText,
            ctxBefore,
            ctxAfter);

        string cleanOld = PdfLayoutSanitizerService.CleanWord(diffOldOnly);
        string cleanNew = PdfLayoutSanitizerService.CleanWord(diffNewOnly);

        // Si le texte alphanumérique sans espaces/ponctuations est identique, ce n'est PAS une vraie différence de texte
        if (string.Equals(cleanOld, cleanNew, StringComparison.Ordinal))
        {
            return;
        }

        var changedTokens = ExtractActuallyChangedTokens(diffOldOnly, diffNewOnly);
        if (changedTokens.Count == 0)
        {
            return;
        }

        var block = new DiffSummaryBlock
        {
            PageNumber = pageNumber,
            Type = (!string.IsNullOrWhiteSpace(diffOldOnly) && !string.IsNullOrWhiteSpace(diffNewOnly))
                ? ChangeType.Modified
                : (!string.IsNullOrWhiteSpace(diffNewOnly) ? ChangeType.Inserted : ChangeType.Deleted),
            OldText = diffOldOnly.Trim(),
            NewText = diffNewOnly.Trim(),
            ContextBefore = updatedBefore,
            ContextAfter = updatedAfter
        };

        ClassifyBlockSeverityAndCategory(block, changedTokens);

        // Si la différence n'est qu'un formatage numérique de valeur strictement égale (ex: "1 000,00" vs "1.000,00") ou formatage mineur sans changement de mot
        if (block.Category == "Formatage Numérique" || block.Category == "Formatage")
        {
            return;
        }

        verifiedBlocks.Add(block);
    }

    private static (string DiffOld, string DiffNew, string CtxBefore, string CtxAfter) TrimCommonPrefixAndSuffixWords(
        string oldText,
        string newText,
        string ctxBefore,
        string ctxAfter)
    {
        if (string.IsNullOrWhiteSpace(oldText) || string.IsNullOrWhiteSpace(newText))
        {
            return (oldText ?? string.Empty, newText ?? string.Empty, ctxBefore ?? string.Empty, ctxAfter ?? string.Empty);
        }

        var oldWords = oldText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var newWords = newText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        var removedPrefix = new List<string>();
        while (oldWords.Count > 0 && newWords.Count > 0)
        {
            string cOld = PdfLayoutSanitizerService.CleanWord(oldWords[0]);
            string cNew = PdfLayoutSanitizerService.CleanWord(newWords[0]);
            if (!string.IsNullOrEmpty(cOld) && string.Equals(cOld, cNew, StringComparison.Ordinal))
            {
                removedPrefix.Add(oldWords[0]);
                oldWords.RemoveAt(0);
                newWords.RemoveAt(0);
            }
            else
            {
                break;
            }
        }

        var removedSuffix = new List<string>();
        while (oldWords.Count > 0 && newWords.Count > 0)
        {
            string cOld = PdfLayoutSanitizerService.CleanWord(oldWords[^1]);
            string cNew = PdfLayoutSanitizerService.CleanWord(newWords[^1]);
            if (!string.IsNullOrEmpty(cOld) && string.Equals(cOld, cNew, StringComparison.Ordinal))
            {
                removedSuffix.Insert(0, oldWords[^1]);
                oldWords.RemoveAt(oldWords.Count - 1);
                newWords.RemoveAt(newWords.Count - 1);
            }
            else
            {
                break;
            }
        }

        string finalBefore = ctxBefore ?? string.Empty;
        if (removedPrefix.Count > 0)
        {
            string prefixStr = string.Join(" ", removedPrefix);
            finalBefore = string.IsNullOrWhiteSpace(finalBefore) ? prefixStr : $"{finalBefore} {prefixStr}";
        }

        string finalAfter = ctxAfter ?? string.Empty;
        if (removedSuffix.Count > 0)
        {
            string suffixStr = string.Join(" ", removedSuffix);
            finalAfter = string.IsNullOrWhiteSpace(finalAfter) ? suffixStr : $"{suffixStr} {finalAfter}";
        }

        return (
            string.Join(" ", oldWords),
            string.Join(" ", newWords),
            finalBefore.Trim(),
            finalAfter.Trim());
    }

    private List<DiffSummaryBlock> MergeAdjacentDeleteInsertBlocksOnSamePage(List<DiffSummaryBlock> blocks)
    {
        if (blocks.Count <= 1) return blocks;

        var merged = new List<DiffSummaryBlock>(blocks.Count);
        int i = 0;
        while (i < blocks.Count)
        {
            var curr = blocks[i];
            if (i + 1 < blocks.Count &&
                curr.PageNumber == blocks[i + 1].PageNumber &&
                curr.Type == ChangeType.Deleted &&
                blocks[i + 1].Type == ChangeType.Inserted)
            {
                var next = blocks[i + 1];
                string cleanOld = PdfLayoutSanitizerService.CleanWord(curr.OldText);
                string cleanNew = PdfLayoutSanitizerService.CleanWord(next.NewText);

                if (!string.Equals(cleanOld, cleanNew, StringComparison.Ordinal))
                {
                    var changed = ExtractActuallyChangedTokens(curr.OldText, next.NewText);
                    if (changed.Count > 0)
                    {
                        var modBlock = new DiffSummaryBlock
                        {
                            PageNumber = curr.PageNumber,
                            Type = ChangeType.Modified,
                            OldText = curr.OldText,
                            NewText = next.NewText,
                            ContextBefore = !string.IsNullOrWhiteSpace(curr.ContextBefore) ? curr.ContextBefore : next.ContextBefore,
                            ContextAfter = !string.IsNullOrWhiteSpace(next.ContextAfter) ? next.ContextAfter : curr.ContextAfter
                        };
                        ClassifyBlockSeverityAndCategory(modBlock, changed);
                        if (modBlock.Category != "Formatage Numérique" && modBlock.Category != "Formatage")
                        {
                            merged.Add(modBlock);
                        }
                    }
                }
                i += 2;
                continue;
            }

            merged.Add(curr);
            i++;
        }

        return merged;
    }

    private sealed class ReadableVisualLine
    {
        public int PageNumber { get; init; }
        public int LineIndex { get; init; }
        public decimal ReferenceY { get; init; }
        public string ReadableText { get; set; } = string.Empty;
        public string DiffOnlyText { get; set; } = string.Empty;
        public string SameLinePrefixContext { get; set; } = string.Empty;
        public string SameLineSuffixContext { get; set; } = string.Empty;
        public bool HasDiff { get; set; }
        public string ContextBefore { get; set; } = string.Empty;
        public string ContextAfter { get; set; } = string.Empty;
    }

    private sealed class VisualDiffCluster
    {
        public int PageNumber { get; init; }
        public decimal MinY { get; set; }
        public decimal MaxY { get; set; }
        public decimal ReferenceY => (MinY + MaxY) / 2.0m;
        public string DiffOnlyText { get; set; } = string.Empty;
        public string ContextBefore { get; set; } = string.Empty;
        public string ContextAfter { get; set; } = string.Empty;
    }

    private static List<VisualDiffCluster> BuildConsecutiveDiffClusters(List<ReadableVisualLine> pageLines)
    {
        var clusters = new List<VisualDiffCluster>();
        var diffLines = pageLines.Where(l => l.HasDiff && !string.IsNullOrWhiteSpace(l.DiffOnlyText)).OrderBy(l => l.LineIndex).ToList();
        if (diffLines.Count == 0) return clusters;

        var currentGroup = new List<ReadableVisualLine> { diffLines[0] };

        for (int i = 1; i < diffLines.Count; i++)
        {
            var prev = currentGroup[^1];
            var curr = diffLines[i];

            // Regrouper si les lignes modifiées se suivent directement (indices consécutifs ou écart vertical <= 34pt)
            bool isConsecutiveLine = (curr.LineIndex <= prev.LineIndex + 1) || (Math.Abs(prev.ReferenceY - curr.ReferenceY) <= 34.0m);

            if (isConsecutiveLine)
            {
                currentGroup.Add(curr);
            }
            else
            {
                clusters.Add(CreateClusterFromGroup(currentGroup));
                currentGroup = new List<ReadableVisualLine> { curr };
            }
        }

        if (currentGroup.Count > 0)
        {
            clusters.Add(CreateClusterFromGroup(currentGroup));
        }

        return clusters;
    }

    private static VisualDiffCluster CreateClusterFromGroup(List<ReadableVisualLine> group)
    {
        var first = group[0];
        var last = group[^1];

        return new VisualDiffCluster
        {
            PageNumber = first.PageNumber,
            MinY = group.Min(l => l.ReferenceY),
            MaxY = group.Max(l => l.ReferenceY),
            DiffOnlyText = string.Join(Environment.NewLine, group.Select(l => l.DiffOnlyText.Trim()).Where(t => !string.IsNullOrEmpty(t))),
            ContextBefore = first.ContextBefore,
            ContextAfter = last.ContextAfter
        };
    }

    private static List<ReadableVisualLine> BuildReadableVisualLines(
        IReadOnlyList<PdfWordInfo>? rawWords,
        List<List<(string CleanText, List<LetterLoc> Letters)>> sanitizedLines,
        HashSet<(int Page, decimal X, decimal Y)> confirmedHighlightSet)
    {
        var result = new List<ReadableVisualLine>();

        if (rawWords != null && rawWords.Count > 0)
        {
            foreach (var pageGroup in rawWords.Where(w => w.Letters != null && w.Letters.Count > 0).GroupBy(w => w.PageNumber).OrderBy(g => g.Key))
            {
                int pageNum = pageGroup.Key;
                var pageWords = pageGroup.OrderByDescending(w => (decimal)w.Letters[0].Location.Y).ToList();
                var lines = new List<(decimal RefY, List<PdfWordInfo> Words)>();

                foreach (var w in pageWords)
                {
                    decimal wy = (decimal)w.Letters[0].Location.Y;
                    decimal tol = Math.Max(4.5m, (decimal)w.Letters[0].PointSize * 0.48m);
                    int bestIdx = -1;
                    decimal bestDy = decimal.MaxValue;

                    for (int i = 0; i < lines.Count; i++)
                    {
                        decimal dy = Math.Abs(lines[i].RefY - wy);
                        if (dy < tol && dy < bestDy)
                        {
                            bestDy = dy;
                            bestIdx = i;
                        }
                    }

                    if (bestIdx == -1)
                    {
                        lines.Add((wy, new List<PdfWordInfo> { w }));
                    }
                    else
                    {
                        var entry = lines[bestIdx];
                        decimal newRef = ((entry.RefY * entry.Words.Count) + wy) / (entry.Words.Count + 1);
                        entry.Words.Add(w);
                        lines[bestIdx] = (newRef, entry.Words);
                    }
                }

                var orderedPageLines = new List<ReadableVisualLine>();
                int lineIdx = 0;

                foreach (var l in lines.OrderByDescending(x => x.RefY))
                {
                    var sortedW = l.Words.OrderBy(w => w.Letters[0].GlyphRectangle.BottomLeft.X).ToList();
                    var fullSb = new StringBuilder();
                    double lastRightX = -1000;

                    var wordEntries = new List<(string Text, bool IsHighlighted)>(sortedW.Count);

                    foreach (var w in sortedW)
                    {
                        double leftX = w.Letters[0].GlyphRectangle.BottomLeft.X;
                        if (fullSb.Length > 0 && leftX - lastRightX > 1.5)
                        {
                            fullSb.Append(' ');
                        }
                        fullSb.Append(w.Text);
                        lastRightX = w.Letters[^1].GlyphRectangle.TopRight.X;

                        bool wordHasHighlight = false;
                        foreach (var let in w.Letters)
                        {
                            var key = (pageNum, Math.Round((decimal)let.GlyphRectangle.BottomLeft.X, 1), Math.Round((decimal)let.Location.Y, 1));
                            if (confirmedHighlightSet.Contains(key))
                            {
                                wordHasHighlight = true;
                                break;
                            }
                        }

                        wordEntries.Add((w.Text, wordHasHighlight));
                    }

                    int firstDiffIdx = wordEntries.FindIndex(x => x.IsHighlighted);
                    int lastDiffIdx = wordEntries.FindLastIndex(x => x.IsHighlighted);
                    bool hasDiff = firstDiffIdx != -1;

                    string diffOnlyText = string.Empty;
                    string sameLinePrefix = string.Empty;
                    string sameLineSuffix = string.Empty;

                    if (hasDiff)
                    {
                        // N'extraire QUE les mots modifiés sur la ligne (entre le 1er et le dernier mot surligné de la ligne)
                        var diffWords = wordEntries
                            .Where(x => x.IsHighlighted)
                            .Select(x => x.Text);
                        diffOnlyText = string.Join(" ", diffWords).Trim();

                        if (firstDiffIdx > 0)
                        {
                            sameLinePrefix = string.Join(" ", wordEntries.Take(firstDiffIdx).Select(x => x.Text)).Trim();
                        }
                        if (lastDiffIdx + 1 < wordEntries.Count)
                        {
                            sameLineSuffix = string.Join(" ", wordEntries.Skip(lastDiffIdx + 1).Select(x => x.Text)).Trim();
                        }
                    }

                    orderedPageLines.Add(new ReadableVisualLine
                    {
                        PageNumber = pageNum,
                        LineIndex = lineIdx++,
                        ReferenceY = l.RefY,
                        ReadableText = fullSb.ToString().Trim(),
                        DiffOnlyText = diffOnlyText,
                        SameLinePrefixContext = sameLinePrefix,
                        SameLineSuffixContext = sameLineSuffix,
                        HasDiff = hasDiff && !string.IsNullOrWhiteSpace(diffOnlyText)
                    });
                }

                for (int i = 0; i < orderedPageLines.Count; i++)
                {
                    string prevLineText = i > 0 ? orderedPageLines[i - 1].ReadableText : string.Empty;
                    string nextLineText = i + 1 < orderedPageLines.Count ? orderedPageLines[i + 1].ReadableText : string.Empty;

                    orderedPageLines[i].ContextBefore = string.Join(" ", new[] { prevLineText, orderedPageLines[i].SameLinePrefixContext }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    orderedPageLines[i].ContextAfter = string.Join(" ", new[] { orderedPageLines[i].SameLineSuffixContext, nextLineText }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    result.Add(orderedPageLines[i]);
                }
            }

            return result;
        }

        // Fallback sur sanitizedLines si rawWords n'est pas fourni
        int fallbackIdx = 0;
        foreach (var line in sanitizedLines)
        {
            if (line.Count == 0 || line[0].Letters.Count == 0) continue;
            int pageNum = line[0].Letters[0].PageNumber;
            decimal refY = line[0].Letters[0].BaselineY;

            var highlightedWords = line
                .Where(w => w.Letters.Any(l =>
                    confirmedHighlightSet.Contains((l.PageNumber, Math.Round((decimal)l.BoundingBox.BottomLeft.X, 1), Math.Round(l.BaselineY, 1)))))
                .Select(w => w.CleanText)
                .ToList();

            bool hasDiff = highlightedWords.Count > 0;
            string fullText = string.Join(" ", line.Select(w => w.CleanText));
            string diffOnly = string.Join(" ", highlightedWords);

            result.Add(new ReadableVisualLine
            {
                PageNumber = pageNum,
                LineIndex = fallbackIdx++,
                ReferenceY = refY,
                ReadableText = fullText,
                DiffOnlyText = diffOnly,
                HasDiff = hasDiff
            });
        }

        return result;
    }

    public static List<string> ExtractActuallyChangedTokens(string? oldText, string? newText)
    {
        var oldTokens = WordTokenRegex().Matches(oldText ?? string.Empty)
            .Select(m => PdfLayoutSanitizerService.CleanWord(m.Value))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        var newTokens = WordTokenRegex().Matches(newText ?? string.Empty)
            .Select(m => PdfLayoutSanitizerService.CleanWord(m.Value))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

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
            if (TryComputeFinancialDelta(block.OldText, block.NewText, out _, out string deltaFormatted, out bool isSameNumericValue))
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
        if (Math.Abs(delta) < 0.0001m && oldNums.Count == newNums.Count)
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
}
