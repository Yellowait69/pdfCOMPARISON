using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PDFComparison.Models;

namespace PDFComparison.Services;

public sealed class GroupedImpactedDoc
{
    public string DisplayFileName { get; init; } = string.Empty;
    public string FullDocumentName { get; init; } = string.Empty;
    public string ReportFileName { get; init; } = string.Empty;
    public string SideBySideHtmlFileName { get; init; } = string.Empty;
    public int FirstPageNumber { get; init; } = 1;
}

public sealed class GroupedDiffItem
{
    public DiffSummaryBlock Block { get; init; } = new();
    public string PagesCompactLabel { get; init; } = "P.1";
    public List<GroupedImpactedDoc> ImpactedDocs { get; init; } = new();
}

public sealed class ProductGroupedDiffSummary
{
    public string ProductGroupKey { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public int ImpactedDocsCount { get; init; }
    public int CriticalUniqueDiffsCount { get; init; }
    public List<GroupedDiffItem> UniqueDifferences { get; init; } = new();
}

public static partial class ProductDifferenceGroupingService
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiWhitespaceRegex();

    public static string NormalizeDiffKeyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return MultiWhitespaceRegex().Replace(text.Trim(), " ");
    }

    public static string GetBlockSignature(DiffSummaryBlock block)
    {
        if (block == null) return string.Empty;
        string normOld = NormalizeDiffKeyText(block.OldText);
        string normNew = NormalizeDiffKeyText(block.NewText);
        return $"{(int)block.Type}|{normOld}|{normNew}";
    }

    public static bool AreSameDifference(DiffSummaryBlock a, DiffSummaryBlock b)
    {
        return string.Equals(GetBlockSignature(a), GetBlockSignature(b), StringComparison.Ordinal);
    }

    /// <summary>
    /// Déduplique les différences strictement identiques à l'intérieur d'un même document PDF.
    /// </summary>
    public static List<DiffSummaryBlock> DeduplicateBlocksWithinDocument(IReadOnlyList<DiffSummaryBlock> blocks)
    {
        if (blocks == null || blocks.Count <= 1)
            return blocks?.ToList() ?? new List<DiffSummaryBlock>();

        var result = new List<DiffSummaryBlock>(blocks.Count);
        var bySig = new Dictionary<string, (DiffSummaryBlock Block, SortedSet<int> Pages)>(StringComparer.Ordinal);

        foreach (var b in blocks)
        {
            string sig = GetBlockSignature(b);
            if (!bySig.TryGetValue(sig, out var entry))
            {
                var clone = CloneBlock(b);
                var pages = new SortedSet<int> { b.PageNumber };
                bySig[sig] = (clone, pages);
                result.Add(clone);
            }
            else
            {
                entry.Pages.Add(b.PageNumber);
                if (b.Severity > entry.Block.Severity)
                {
                    entry.Block.Severity = b.Severity;
                    entry.Block.Category = b.Category;
                }
                if (string.IsNullOrWhiteSpace(entry.Block.FinancialDelta) && !string.IsNullOrWhiteSpace(b.FinancialDelta))
                {
                    entry.Block.FinancialDelta = b.FinancialDelta;
                }
            }
        }

        foreach (var (_, (block, pages)) in bySig)
        {
            if (pages.Count > 1)
            {
                block.PagesLabel = $"Pages {string.Join(", ", pages)}";
            }
        }

        return result;
    }

    /// <summary>
    /// Regroupe tous les résumés de documents par Code Produit, puis fusionne les différences identiques
    /// présentes dans plusieurs PDF d'un même Code Produit pour qu'elles n'apparaissent qu'une seule fois.
    /// </summary>
    public static List<ProductGroupedDiffSummary> GroupSummariesByProduct(IEnumerable<DocumentDiffSummary> summaries)
    {
        var result = new List<ProductGroupedDiffSummary>();
        if (summaries == null) return result;

        var impacted = summaries.Where(s => s.Blocks != null && s.Blocks.Count > 0).ToList();
        if (impacted.Count == 0) return result;

        var productGroups = impacted
            .GroupBy(s => s.ProductGroupKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Any(s => string.Equals(s.Channel, "Bank", StringComparison.OrdinalIgnoreCase)) ? 1 : 2)
            .ThenByDescending(g => g.Sum(s => s.CriticalAlertsCount))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var prodGroup in productGroups)
        {
            var docsInProduct = prodGroup.ToList();
            var diffMap = new Dictionary<string, (
                DiffSummaryBlock Representative,
                SortedSet<int> Pages,
                List<string> Contexts,
                List<GroupedImpactedDoc> Docs,
                HashSet<string> SeenDocNames)>(StringComparer.Ordinal);

            var orderedSigs = new List<string>();

            foreach (var doc in docsInProduct)
            {
                string displayFile = !string.IsNullOrWhiteSpace(doc.RawFileName)
                    ? doc.RawFileName
                    : ExtractCleanFileName(doc.DocumentName);

                string stem = !string.IsNullOrEmpty(doc.ReportFileName)
                    ? Path.GetFileNameWithoutExtension(doc.ReportFileName)
                    : string.Empty;
                string sideBySideHtml = !string.IsNullOrEmpty(stem) ? $"{stem}_SideBySide.html" : string.Empty;

                foreach (var b in doc.Blocks)
                {
                    string sig = GetBlockSignature(b);
                    if (!diffMap.TryGetValue(sig, out var acc))
                    {
                        acc = (
                            CloneBlock(b),
                            new SortedSet<int>(),
                            new List<string>(),
                            new List<GroupedImpactedDoc>(),
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        diffMap[sig] = acc;
                        orderedSigs.Add(sig);
                    }

                    acc.Pages.Add(b.PageNumber);

                    if (b.Severity > acc.Representative.Severity)
                    {
                        acc.Representative.Severity = b.Severity;
                        acc.Representative.Category = b.Category;
                    }
                    if (string.IsNullOrWhiteSpace(acc.Representative.FinancialDelta) && !string.IsNullOrWhiteSpace(b.FinancialDelta))
                    {
                        acc.Representative.FinancialDelta = b.FinancialDelta;
                    }

                    string ctx = !string.IsNullOrWhiteSpace(b.ContextBefore) ? b.ContextBefore.Trim() : b.ContextAfter.Trim();
                    if (!string.IsNullOrWhiteSpace(ctx) && !acc.Contexts.Contains(ctx, StringComparer.OrdinalIgnoreCase))
                    {
                        acc.Contexts.Add(ctx);
                    }

                    string uniqueDocKey = !string.IsNullOrWhiteSpace(doc.DocumentName) ? doc.DocumentName : displayFile;
                    if (acc.SeenDocNames.Add(uniqueDocKey))
                    {
                        acc.Docs.Add(new GroupedImpactedDoc
                        {
                            DisplayFileName = displayFile,
                            FullDocumentName = doc.DocumentName,
                            ReportFileName = doc.ReportFileName,
                            SideBySideHtmlFileName = sideBySideHtml,
                            FirstPageNumber = b.PageNumber
                        });
                    }
                }
            }

            var uniqueDiffItems = new List<GroupedDiffItem>(orderedSigs.Count);
            foreach (string sig in orderedSigs)
            {
                var acc = diffMap[sig];
                var block = acc.Representative;
                block.OccurrenceCount = acc.Docs.Count;
                block.ImpactedDocuments = acc.Docs.Select(d => d.DisplayFileName).ToList();
                block.PageNumber = acc.Pages.Min;
                block.PagesLabel = acc.Pages.Count > 1
                    ? $"Pages {string.Join(", ", acc.Pages)}"
                    : $"Page {block.PageNumber}";

                if (acc.Contexts.Count > 1)
                {
                    block.ContextBefore = string.Join(" / ", acc.Contexts.Take(2));
                }

                string compactPages = acc.Pages.Count > 1
                    ? $"P.{string.Join(",", acc.Pages)}"
                    : $"P.{block.PageNumber}";

                uniqueDiffItems.Add(new GroupedDiffItem
                {
                    Block = block,
                    PagesCompactLabel = compactPages,
                    ImpactedDocs = acc.Docs
                });
            }

            uniqueDiffItems = uniqueDiffItems
                .OrderByDescending(d => d.Block.Severity)
                .ThenByDescending(d => d.ImpactedDocs.Count)
                .ThenBy(d => d.Block.PageNumber)
                .ToList();

            var sampleDoc = docsInProduct[0];
            result.Add(new ProductGroupedDiffSummary
            {
                ProductGroupKey = prodGroup.Key,
                Channel = sampleDoc.Channel,
                ProductName = sampleDoc.ProductName,
                ImpactedDocsCount = docsInProduct.Count,
                CriticalUniqueDiffsCount = uniqueDiffItems.Count(d => d.Block.Severity == DiffSeverity.Critical),
                UniqueDifferences = uniqueDiffItems
            });
        }

        return result;
    }

    /// <summary>
    /// Construit la liste d'affichage pour le tableau principal : au sein d'un même Code Produit,
    /// les différences identiques présentes dans plusieurs PDF sont regroupées en une seule ligne
    /// afin de n'apparaître qu'une seule fois.
    /// </summary>
    public static List<DocumentPair> BuildGroupedDisplayPairs(IReadOnlyList<DocumentPair> allPairs)
    {
        if (allPairs == null || allPairs.Count == 0)
            return new List<DocumentPair>();

        var output = new List<DocumentPair>(allPairs.Count);

        var differentPairs = allPairs
            .Where(p => p.Status == CompareStatus.Different && p.DiffBlocks.Count > 0)
            .ToList();

        var productGroups = differentPairs
            .GroupBy(p => p.ProductGroupKey, StringComparer.OrdinalIgnoreCase);

        foreach (var prodGroup in productGroups)
        {
            var prodPairs = prodGroup.ToList();

            var sigMap = new Dictionary<string, (
                DiffSummaryBlock RepBlock,
                SortedSet<int> Pages,
                List<string> Contexts,
                List<DocumentPair> Pairs,
                HashSet<DocumentPair> PairSet)>(StringComparer.Ordinal);

            var sigOrder = new List<string>();

            foreach (var pair in prodPairs)
            {
                foreach (var b in pair.DiffBlocks)
                {
                    string sig = GetBlockSignature(b);
                    if (!sigMap.TryGetValue(sig, out var entry))
                    {
                        entry = (
                            CloneBlock(b),
                            new SortedSet<int>(),
                            new List<string>(),
                            new List<DocumentPair>(),
                            new HashSet<DocumentPair>());
                        sigMap[sig] = entry;
                        sigOrder.Add(sig);
                    }

                    entry.Pages.Add(b.PageNumber);
                    if (b.Severity > entry.RepBlock.Severity)
                    {
                        entry.RepBlock.Severity = b.Severity;
                        entry.RepBlock.Category = b.Category;
                    }
                    if (string.IsNullOrWhiteSpace(entry.RepBlock.FinancialDelta) && !string.IsNullOrWhiteSpace(b.FinancialDelta))
                    {
                        entry.RepBlock.FinancialDelta = b.FinancialDelta;
                    }

                    string ctx = !string.IsNullOrWhiteSpace(b.ContextBefore) ? b.ContextBefore.Trim() : b.ContextAfter.Trim();
                    if (!string.IsNullOrWhiteSpace(ctx) && !entry.Contexts.Contains(ctx, StringComparer.OrdinalIgnoreCase))
                    {
                        entry.Contexts.Add(ctx);
                    }

                    if (entry.PairSet.Add(pair))
                    {
                        entry.Pairs.Add(pair);
                    }
                }
            }

            // Regrouper les différences uniques partageant exactement le même sous-ensemble de PDF impactés du produit
            var docSetGroups = new Dictionary<string, (List<DocumentPair> Docs, List<DiffSummaryBlock> Blocks)>(StringComparer.Ordinal);
            var docSetOrder = new List<string>();

            foreach (string sig in sigOrder)
            {
                var entry = sigMap[sig];
                string docSetKey = string.Join("\u001F", entry.Pairs.Select(p => $"{p.SourcePath}|{p.TargetPath}"));

                var mergedBlock = entry.RepBlock;
                mergedBlock.OccurrenceCount = entry.Pairs.Count;
                mergedBlock.ImpactedDocuments = entry.Pairs.Select(p => p.FileNameOnly).ToList();
                mergedBlock.PageNumber = entry.Pages.Min;
                mergedBlock.PagesLabel = entry.Pages.Count > 1
                    ? $"Pages {string.Join(", ", entry.Pages)}"
                    : $"Page {mergedBlock.PageNumber}";

                if (entry.Contexts.Count > 1)
                {
                    mergedBlock.ContextBefore = string.Join(" / ", entry.Contexts.Take(2));
                }

                if (!docSetGroups.TryGetValue(docSetKey, out var dsGroup))
                {
                    dsGroup = (entry.Pairs, new List<DiffSummaryBlock>());
                    docSetGroups[docSetKey] = dsGroup;
                    docSetOrder.Add(docSetKey);
                }

                dsGroup.Blocks.Add(mergedBlock);
            }

            foreach (string docSetKey in docSetOrder)
            {
                var (docs, blocks) = docSetGroups[docSetKey];
                var firstDoc = docs[0];

                string displayKey;
                if (docs.Count > 1)
                {
                    displayKey = firstDoc.HasProductTag
                        ? $"[{firstDoc.ProductName}] {docs.Count} PDF regroupés (même diff)"
                        : $"{docs.Count} PDF regroupés (même diff)";
                }
                else
                {
                    displayKey = firstDoc.MatchKey;
                }

                var firstB = blocks[0];
                string shortOld = TruncateOneLine(firstB.OldText, 24);
                string shortNew = TruncateOneLine(firstB.NewText, 24);
                string diffPreview = $"{shortOld} ➔ {shortNew}" + (blocks.Count > 1 ? $" (+{blocks.Count - 1})" : "");

                string tooltipSummary = docs.Count > 1
                    ? $"{blocks.Count} différence(s) unique(s) partagée(s) par {docs.Count} PDF du produit {firstDoc.ProductGroupKey} :\n• " +
                      string.Join("\n• ", docs.Select(d => d.FileNameOnly))
                    : firstDoc.ErrorMessage;

                var groupedRow = new DocumentPair(
                    displayKey,
                    firstDoc.SourcePath,
                    firstDoc.TargetPath,
                    firstDoc.Channel,
                    firstDoc.ProductName,
                    firstDoc.ReleaseName,
                    firstDoc.ProductOutputDirectory)
                {
                    Status = CompareStatus.Different,
                    ErrorMessage = tooltipSummary,
                    DiffCount = blocks.Count,
                    CriticalCount = blocks.Count(b => b.Severity == DiffSeverity.Critical),
                    MaxSeverity = blocks.Max(b => b.Severity),
                    InsertionsCount = blocks.Count(b => b.Type is DiffPlex.DiffBuilder.Model.ChangeType.Inserted or DiffPlex.DiffBuilder.Model.ChangeType.Modified),
                    DeletionsCount = blocks.Count(b => b.Type is DiffPlex.DiffBuilder.Model.ChangeType.Deleted or DiffPlex.DiffBuilder.Model.ChangeType.Modified),
                    SsimScore = Math.Round(docs.Average(d => d.SsimScore), 1),
                    ReportPath = firstDoc.ReportPath,
                    CompletedTime = docs.Max(d => d.CompletedTime) ?? firstDoc.CompletedTime
                };

                foreach (var d in docs)
                {
                    groupedRow.GroupedSourcePairs.Add(d);
                }
                groupedRow.SelectedGroupedMember = firstDoc;

                foreach (var b in blocks)
                {
                    groupedRow.DiffBlocks.Add(b);
                }

                output.Add(groupedRow);
            }
        }

        // Conserver les documents Conformes, Manquants ou en Erreur à la suite pour le filtrage KPI
        foreach (var pair in allPairs)
        {
            if (pair.Status != CompareStatus.Different || pair.DiffBlocks.Count == 0)
            {
                output.Add(pair);
            }
        }

        return output;
    }

    public static int CountTotalUniqueDifferences(IEnumerable<DocumentPair> allPairs)
    {
        if (allPairs == null) return 0;

        int totalUnique = 0;
        var groups = allPairs
            .Where(p => p.Status == CompareStatus.Different && p.DiffBlocks.Count > 0)
            .GroupBy(p => p.ProductGroupKey, StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            var sigs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in g)
            {
                foreach (var b in p.DiffBlocks)
                {
                    sigs.Add(GetBlockSignature(b));
                }
            }
            totalUnique += sigs.Count;
        }

        return totalUnique;
    }

    public static List<DocumentDiffSummary> BuildSummariesFromPairs(IEnumerable<DocumentPair> pairs)
    {
        var list = new List<DocumentDiffSummary>();
        if (pairs == null) return list;

        foreach (var pair in pairs.Where(p => p.Status == CompareStatus.Different && p.DiffBlocks.Count > 0))
        {
            string rawDocName = Path.GetFileName(pair.TargetPath ?? pair.SourcePath ?? "Document.pdf");
            string fullTitle = pair.HasProductTag
                ? $"[{pair.DisplayProductBadge}] {rawDocName}"
                : rawDocName;

            string lang = pair.HasProductTag
                ? (string.IsNullOrWhiteSpace(pair.Channel) ? pair.ProductName : $"{pair.Channel}/{pair.ProductName}")
                : "Standard";

            var summary = new DocumentDiffSummary
            {
                DocumentName = fullTitle,
                Language = lang,
                Channel = pair.Channel,
                ProductName = pair.ProductName,
                RawFileName = rawDocName,
                ReportFileName = !string.IsNullOrWhiteSpace(pair.ReportPath) ? Path.GetFileName(pair.ReportPath) : string.Empty,
                OverallSsimScore = pair.SsimScore / 100.0,
                MaxSeverity = pair.MaxSeverity,
                CriticalAlertsCount = pair.CriticalCount
            };
            summary.Blocks.AddRange(pair.DiffBlocks.Select(CloneBlock));
            list.Add(summary);
        }

        return list;
    }

    public static DiffSummaryBlock CloneBlock(DiffSummaryBlock b)
    {
        return new DiffSummaryBlock
        {
            PageNumber = b.PageNumber,
            Severity = b.Severity,
            Category = b.Category,
            FinancialDelta = b.FinancialDelta,
            ContextBefore = b.ContextBefore,
            OldText = b.OldText,
            NewText = b.NewText,
            ContextAfter = b.ContextAfter,
            Type = b.Type,
            OccurrenceCount = b.OccurrenceCount,
            PagesLabel = b.PagesLabel,
            ImpactedDocuments = b.ImpactedDocuments != null ? new List<string>(b.ImpactedDocuments) : new List<string>()
        };
    }

    private static string ExtractCleanFileName(string? docName)
    {
        if (string.IsNullOrWhiteSpace(docName)) return "Document.pdf";
        int closeBracket = docName.IndexOf(']');
        if (docName.StartsWith('[') && closeBracket > 0 && closeBracket + 1 < docName.Length)
        {
            return docName.Substring(closeBracket + 1).Trim();
        }
        return docName.Trim();
    }

    private static string TruncateOneLine(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(vide)";
        string clean = value.Replace("\r\n", " ").Replace("\n", " ").Trim();
        return clean.Length <= maxLen ? clean : clean.Substring(0, maxLen) + "…";
    }
}
