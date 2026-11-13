using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using PDFComparison.ViewModels;

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

public sealed class ChannelGroupedDiffSummary
{
    public string ChannelKey { get; init; } = string.Empty;
    public string ChannelDisplayName { get; init; } = string.Empty;
    public string ChannelKind { get; init; } = "Bank"; // "Bank" ou "Broker" uniquement
    public List<ProductGroupedDiffSummary> Products { get; init; } = new();
    public int ProductsCount => Products.Count;
    public int UniqueDiffsCount => Products.Sum(p => p.UniqueDifferences.Count);
    public int ImpactedDocsCount => Products.Sum(p => p.ImpactedDocsCount);
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
    /// Détermine si deux différences d'un même produit se trouvent exactement au même emplacement géométrique
    /// (même page, même ligne/champ et même colonne de valeur), typique de données scramblées (noms, adresses, IBAN...).
    /// </summary>
    public static bool AreSameProductGeometricSpot(DiffSummaryBlock? a, DiffSummaryBlock? b)
    {
        if (a == null || b == null || !a.HasPdfBounds || !b.HasPdfBounds)
            return false;

        if (a.PageNumber != b.PageNumber)
            return false;

        double aCenterY = (a.PdfBottom + a.PdfTop) * 0.5;
        double bCenterY = (b.PdfBottom + b.PdfTop) * 0.5;
        bool sameVerticalLevel = Math.Abs(aCenterY - bCenterY) <= 15.0 &&
                                 (Math.Min(a.PdfTop, b.PdfTop) + 2.0 >= Math.Max(a.PdfBottom, b.PdfBottom));

        if (!sameVerticalLevel)
            return false;

        double aColLeft = a.FieldZoneRight > a.FieldZoneLeft ? a.FieldZoneLeft : a.PdfLeft;
        double bColLeft = b.FieldZoneRight > b.FieldZoneLeft ? b.FieldZoneLeft : b.PdfLeft;
        bool sameColumnStart = Math.Abs(aColLeft - bColLeft) <= 28.0;
        bool overlapsTightX = Math.Min(a.PdfRight, b.PdfRight) > Math.Max(a.PdfLeft, b.PdfLeft);

        return sameColumnStart || overlapsTightX;
    }

    private static void MergeBlockBoundsAndMeta(DiffSummaryBlock target, DiffSummaryBlock incoming)
    {
        if (string.IsNullOrWhiteSpace(target.ProductCode) && !string.IsNullOrWhiteSpace(incoming.ProductCode))
            target.ProductCode = incoming.ProductCode;
        if (string.IsNullOrWhiteSpace(target.Channel) && !string.IsNullOrWhiteSpace(incoming.Channel))
            target.Channel = incoming.Channel;

        if (!incoming.HasPdfBounds) return;

        if (!target.HasPdfBounds)
        {
            target.HasPdfBounds = true;
            target.PdfLeft = incoming.PdfLeft;
            target.PdfBottom = incoming.PdfBottom;
            target.PdfRight = incoming.PdfRight;
            target.PdfTop = incoming.PdfTop;
            target.FieldZoneLeft = incoming.FieldZoneLeft;
            target.FieldZoneBottom = incoming.FieldZoneBottom;
            target.FieldZoneRight = incoming.FieldZoneRight;
            target.FieldZoneTop = incoming.FieldZoneTop;
            target.PdfPageWidth = incoming.PdfPageWidth;
            target.PdfPageHeight = incoming.PdfPageHeight;
            return;
        }

        if (target.PageNumber == incoming.PageNumber &&
            Math.Abs(((target.PdfBottom + target.PdfTop) * 0.5) - ((incoming.PdfBottom + incoming.PdfTop) * 0.5)) <= 22.0)
        {
            target.PdfLeft = Math.Min(target.PdfLeft, incoming.PdfLeft);
            target.PdfBottom = Math.Min(target.PdfBottom, incoming.PdfBottom);
            target.PdfRight = Math.Max(target.PdfRight, incoming.PdfRight);
            target.PdfTop = Math.Max(target.PdfTop, incoming.PdfTop);

            if (incoming.FieldZoneRight > incoming.FieldZoneLeft)
            {
                target.FieldZoneLeft = target.FieldZoneRight > target.FieldZoneLeft
                    ? Math.Min(target.FieldZoneLeft, incoming.FieldZoneLeft)
                    : incoming.FieldZoneLeft;
                target.FieldZoneBottom = target.FieldZoneTop > target.FieldZoneBottom
                    ? Math.Min(target.FieldZoneBottom, incoming.FieldZoneBottom)
                    : incoming.FieldZoneBottom;
                target.FieldZoneRight = Math.Max(target.FieldZoneRight, incoming.FieldZoneRight);
                target.FieldZoneTop = Math.Max(target.FieldZoneTop, incoming.FieldZoneTop);
            }
        }
    }

    private static string FormatVariantLine(DiffSummaryBlock b)
    {
        string o = TruncateOneLine(b.OldText, 36);
        string n = TruncateOneLine(b.NewText, 36);
        return $"{o} ➔ {n}";
    }

    private sealed class ProductDiffBucket<TDocItem>
    {
        public DiffSummaryBlock RepBlock { get; }
        public SortedSet<int> Pages { get; } = new();
        public List<string> Contexts { get; } = new();
        public List<(int OrderIndex, string DocKey, TDocItem Item)> OrderedDocs { get; } = new();
        public HashSet<string> DocKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> VariantSignatures { get; } = new();
        public List<string> VariantSummaries { get; } = new();

        public ProductDiffBucket(DiffSummaryBlock repBlock, string initialSig, string initialVariantSummary)
        {
            RepBlock = repBlock;
            VariantSignatures.Add(initialSig);
            VariantSummaries.Add(initialVariantSummary);
        }
    }

    private static List<(DiffSummaryBlock Block, SortedSet<int> Pages, List<TDocItem> Docs)> BuildCoalescedProductBuckets<TDoc, TDocItem>(
        IReadOnlyList<TDoc> docsInProduct,
        Func<TDoc, string> getDocKey,
        Func<TDoc, IEnumerable<DiffSummaryBlock>> getBlocks,
        Func<TDoc, DiffSummaryBlock, TDocItem> createDocItem,
        Func<TDocItem, string> getDisplayFileName)
    {
        var buckets = new List<ProductDiffBucket<TDocItem>>();
        var bySig = new Dictionary<string, ProductDiffBucket<TDocItem>>(StringComparer.Ordinal);

        // Étape 1 : Regroupement par signature textuelle identique
        for (int docIdx = 0; docIdx < docsInProduct.Count; docIdx++)
        {
            var doc = docsInProduct[docIdx];
            string docKey = getDocKey(doc);

            foreach (var b in getBlocks(doc))
            {
                string sig = GetBlockSignature(b);
                if (!bySig.TryGetValue(sig, out var bucket))
                {
                    bucket = new ProductDiffBucket<TDocItem>(CloneBlock(b), sig, FormatVariantLine(b));
                    bySig[sig] = bucket;
                    buckets.Add(bucket);
                }
                else
                {
                    MergeBlockBoundsAndMeta(bucket.RepBlock, b);
                    if (b.Severity > bucket.RepBlock.Severity)
                    {
                        bucket.RepBlock.Severity = b.Severity;
                        bucket.RepBlock.Category = b.Category;
                    }
                    if (string.IsNullOrWhiteSpace(bucket.RepBlock.FinancialDelta) && !string.IsNullOrWhiteSpace(b.FinancialDelta))
                    {
                        bucket.RepBlock.FinancialDelta = b.FinancialDelta;
                    }
                }

                bucket.Pages.Add(b.PageNumber);

                string ctx = !string.IsNullOrWhiteSpace(b.ContextBefore) ? b.ContextBefore.Trim() : b.ContextAfter.Trim();
                if (!string.IsNullOrWhiteSpace(ctx) && !bucket.Contexts.Contains(ctx, StringComparer.OrdinalIgnoreCase))
                {
                    bucket.Contexts.Add(ctx);
                }

                if (bucket.DocKeys.Add(docKey))
                {
                    bucket.OrderedDocs.Add((docIdx, docKey, createDocItem(doc, b)));
                }
            }
        }

        // Étape 2 : Regroupement géométrique des données scramblées situées au même endroit dans plusieurs PDF du même produit
        if (buckets.Count > 1)
        {
            var mergedAway = new bool[buckets.Count];
            bool changed = true;

            while (changed)
            {
                changed = false;
                for (int i = 0; i < buckets.Count; i++)
                {
                    if (mergedAway[i]) continue;
                    var target = buckets[i];
                    if (!target.RepBlock.HasPdfBounds) continue;

                    for (int j = i + 1; j < buckets.Count; j++)
                    {
                        if (mergedAway[j]) continue;
                        var cand = buckets[j];
                        if (!cand.RepBlock.HasPdfBounds) continue;

                        // Ne jamais fusionner deux blocs provenant uniquement du même document PDF unique
                        if (target.DocKeys.Count == 1 && cand.DocKeys.Count == 1 && target.DocKeys.SetEquals(cand.DocKeys))
                            continue;

                        if (!AreSameProductGeometricSpot(target.RepBlock, cand.RepBlock))
                            continue;

                        MergeBlockBoundsAndMeta(target.RepBlock, cand.RepBlock);
                        if (cand.RepBlock.Severity > target.RepBlock.Severity)
                        {
                            target.RepBlock.Severity = cand.RepBlock.Severity;
                            target.RepBlock.Category = cand.RepBlock.Category;
                        }
                        if (string.IsNullOrWhiteSpace(target.RepBlock.FinancialDelta) && !string.IsNullOrWhiteSpace(cand.RepBlock.FinancialDelta))
                        {
                            target.RepBlock.FinancialDelta = cand.RepBlock.FinancialDelta;
                        }

                        foreach (int p in cand.Pages) target.Pages.Add(p);
                        foreach (string c in cand.Contexts)
                        {
                            if (!target.Contexts.Contains(c, StringComparer.OrdinalIgnoreCase))
                                target.Contexts.Add(c);
                        }

                        foreach (var docEntry in cand.OrderedDocs)
                        {
                            if (target.DocKeys.Add(docEntry.DocKey))
                            {
                                target.OrderedDocs.Add(docEntry);
                            }
                        }

                        for (int k = 0; k < cand.VariantSignatures.Count; k++)
                        {
                            if (!target.VariantSignatures.Contains(cand.VariantSignatures[k], StringComparer.Ordinal))
                            {
                                target.VariantSignatures.Add(cand.VariantSignatures[k]);
                                target.VariantSummaries.Add(cand.VariantSummaries[k]);
                            }
                        }

                        mergedAway[j] = true;
                        changed = true;
                    }
                }
            }

            var survivors = new List<ProductDiffBucket<TDocItem>>(buckets.Count);
            for (int i = 0; i < buckets.Count; i++)
            {
                if (!mergedAway[i]) survivors.Add(buckets[i]);
            }
            buckets = survivors;
        }

        // Étape 3 : Finalisation canonique de chaque différence unique du produit
        var finalized = new List<(DiffSummaryBlock Block, SortedSet<int> Pages, List<TDocItem> Docs)>(buckets.Count);
        foreach (var bucket in buckets)
        {
            bucket.OrderedDocs.Sort((a, b) => a.OrderIndex.CompareTo(b.OrderIndex));
            var docs = bucket.OrderedDocs.Select(x => x.Item).ToList();

            var block = bucket.RepBlock;
            block.OccurrenceCount = docs.Count;
            block.ImpactedDocuments = docs.Select(getDisplayFileName).ToList();
            block.PageNumber = bucket.Pages.Count > 0 ? bucket.Pages.Min : block.PageNumber;
            block.PagesLabel = bucket.Pages.Count > 1
                ? $"Pages {string.Join(", ", bucket.Pages)}"
                : $"Page {block.PageNumber}";

            if (bucket.Contexts.Count > 1)
            {
                block.ContextBefore = string.Join(" / ", bucket.Contexts.Take(2));
            }

            if (bucket.VariantSignatures.Count > 1)
            {
                block.IsScrambledZoneGroup = true;
                block.VariantCount = bucket.VariantSignatures.Count;
                var topVariants = bucket.VariantSummaries.Take(12).ToList();
                if (bucket.VariantSummaries.Count > 12)
                {
                    topVariants.Add($"… (+{bucket.VariantSummaries.Count - 12} autres variantes)");
                }
                block.ScrambledVariants = topVariants;
            }

            finalized.Add((block, bucket.Pages, docs));
        }

        return finalized;
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
                MergeBlockBoundsAndMeta(entry.Block, b);
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
    /// ainsi que les données scramblées situées au même emplacement géométrique dans plusieurs PDF du même produit.
    /// </summary>
    public static List<ProductGroupedDiffSummary> GroupSummariesByProduct(IEnumerable<DocumentDiffSummary> summaries)
    {
        var result = new List<ProductGroupedDiffSummary>();
        if (summaries == null) return result;

        var impacted = summaries.Where(s => s.Blocks != null && s.Blocks.Count > 0).ToList();
        if (impacted.Count == 0) return result;

        var enriched = impacted
            .Select(s =>
            {
                string srcRef = !string.IsNullOrWhiteSpace(s.SourcePath) ? s.SourcePath : s.RawFileName;
                string trgRef = !string.IsNullOrWhiteSpace(s.TargetPath) ? s.TargetPath : s.DocumentName;
                string prod = ProductCodeHelper.ResolveEffectiveProductCode(s.ProductName, s.Channel, srcRef, trgRef);
                string ch = ProductCodeHelper.ResolveEffectiveChannel(s.Channel, srcRef, trgRef, prod);
                string effProd = !string.IsNullOrWhiteSpace(prod) ? prod : "Produit";
                string groupKey = $"{ch} • {effProd}";
                return (Summary: s, EffectiveChannel: ch, EffectiveProduct: effProd, GroupKey: groupKey);
            })
            .ToList();

        var productGroups = enriched
            .GroupBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => string.Equals(g.First().EffectiveChannel, "Bank", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var prodGroup in productGroups)
        {
            var docsInProduct = prodGroup.Select(x => x.Summary).ToList();
            string resolvedChannel = prodGroup.First().EffectiveChannel;
            string effectiveProdName = prodGroup.First().EffectiveProduct;

            var coalesced = BuildCoalescedProductBuckets(
                docsInProduct,
                getDocKey: doc => !string.IsNullOrWhiteSpace(doc.TargetPath) || !string.IsNullOrWhiteSpace(doc.SourcePath)
                    ? $"{doc.SourcePath}|{doc.TargetPath}"
                    : (!string.IsNullOrWhiteSpace(doc.DocumentName) ? doc.DocumentName : ExtractCleanFileName(doc.RawFileName)),
                getBlocks: doc => doc.Blocks,
                createDocItem: (doc, b) =>
                {
                    string displayFile = !string.IsNullOrWhiteSpace(doc.RawFileName)
                        ? doc.RawFileName
                        : ExtractCleanFileName(doc.DocumentName);
                    string stem = !string.IsNullOrEmpty(doc.ReportFileName)
                        ? Path.GetFileNameWithoutExtension(doc.ReportFileName)
                        : string.Empty;
                    string sideBySideHtml = !string.IsNullOrEmpty(stem) ? $"{stem}_SideBySide.html" : string.Empty;

                    return new GroupedImpactedDoc
                    {
                        DisplayFileName = displayFile,
                        FullDocumentName = doc.DocumentName,
                        ReportFileName = doc.ReportFileName,
                        SideBySideHtmlFileName = sideBySideHtml,
                        FirstPageNumber = b.PageNumber
                    };
                },
                getDisplayFileName: d => d.DisplayFileName);

            var uniqueDiffItems = coalesced
                .Select(c => new GroupedDiffItem
                {
                    Block = c.Block,
                    PagesCompactLabel = c.Pages.Count > 1 ? $"P.{string.Join(",", c.Pages)}" : $"P.{c.Block.PageNumber}",
                    ImpactedDocs = c.Docs
                })
                .OrderByDescending(d => d.ImpactedDocs.Count)
                .ThenBy(d => d.Block.PageNumber)
                .ToList();

            result.Add(new ProductGroupedDiffSummary
            {
                ProductGroupKey = prodGroup.Key,
                Channel = resolvedChannel,
                ProductName = effectiveProdName,
                ImpactedDocsCount = docsInProduct.Count,
                CriticalUniqueDiffsCount = uniqueDiffItems.Count(d => d.Block.Severity == DiffSeverity.Critical),
                UniqueDifferences = uniqueDiffItems
            });
        }

        return result;
    }

    /// <summary>
    /// Regroupe les résumés de la Synthèse par Canal (Bank ou Broker uniquement) ➔ Produit ➔ Différences regroupées (avec dropdown des PDF).
    /// </summary>
    public static List<ChannelGroupedDiffSummary> GroupSummariesByChannelAndProduct(IEnumerable<DocumentDiffSummary> summaries)
    {
        var products = GroupSummariesByProduct(summaries);
        if (products.Count == 0)
            return new List<ChannelGroupedDiffSummary>();

        return products
            .GroupBy(p => string.Equals(p.Channel, "Broker", StringComparison.OrdinalIgnoreCase) ? "Broker" : "Bank", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => string.Equals(g.Key, "Bank", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Select(g =>
            {
                string chName = string.Equals(g.Key, "Broker", StringComparison.OrdinalIgnoreCase) ? "Broker" : "Bank";
                return new ChannelGroupedDiffSummary
                {
                    ChannelKey = chName,
                    ChannelDisplayName = chName,
                    ChannelKind = chName,
                    Products = g.ToList()
                };
            })
            .ToList();
    }

    /// <summary>
    /// Construit la liste d'affichage pour le tableau principal : au sein d'un même Code Produit,
    /// les différences identiques ou situées au même emplacement géométrique (données scramblées)
    /// présentes dans plusieurs PDF sont regroupées en une seule ligne.
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

            var coalesced = BuildCoalescedProductBuckets(
                prodPairs,
                getDocKey: p => $"{p.SourcePath}|{p.TargetPath}",
                getBlocks: p => p.DiffBlocks,
                createDocItem: (p, _) => p,
                getDisplayFileName: p => p.FileNameOnly);

            // Regrouper les différences uniques partageant exactement le même sous-ensemble de PDF impactés du produit
            var docSetGroups = new Dictionary<string, (List<DocumentPair> Docs, List<DiffSummaryBlock> Blocks)>(StringComparer.Ordinal);
            var docSetOrder = new List<string>();

            foreach (var (mergedBlock, _, docsForBlock) in coalesced)
            {
                string docSetKey = string.Join("\u001F", docsForBlock.Select(p => $"{p.SourcePath}|{p.TargetPath}"));

                if (!docSetGroups.TryGetValue(docSetKey, out var dsGroup))
                {
                    dsGroup = (docsForBlock, new List<DiffSummaryBlock>());
                    docSetGroups[docSetKey] = dsGroup;
                    docSetOrder.Add(docSetKey);
                }

                dsGroup.Blocks.Add(mergedBlock);
            }

            foreach (string docSetKey in docSetOrder)
            {
                var (docs, blocks) = docSetGroups[docSetKey];
                var firstDoc = docs[0];
                string resolvedProd = firstDoc.EffectiveProductCode;
                string resolvedChannel = firstDoc.EffectiveChannel;

                string displayKey;
                if (docs.Count > 1)
                {
                    bool allScrambledZone = blocks.All(b => b.HasScrambledBadge);
                    string reason = allScrambledZone ? "même zone" : "même diff";
                    displayKey = !string.IsNullOrWhiteSpace(resolvedProd)
                        ? $"[{resolvedProd}] {docs.Count} PDF regroupés ({reason})"
                        : $"{docs.Count} PDF regroupés ({reason})";
                }
                else
                {
                    displayKey = firstDoc.MatchKey;
                }

                string tooltipSummary = docs.Count > 1
                    ? $"{blocks.Count} différence(s) regroupée(s) partagée(s) par {docs.Count} PDF du produit {firstDoc.ProductGroupKey} :\n• " +
                      string.Join("\n• ", docs.Select(d => d.FileNameOnly))
                    : firstDoc.ErrorMessage;

                var groupedRow = new DocumentPair(
                    displayKey,
                    firstDoc.SourcePath,
                    firstDoc.TargetPath,
                    resolvedChannel,
                    resolvedProd,
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

        return output;
    }

    /// <summary>
    /// Construit l'arborescence visuelle à 3 niveaux pour le tableau principal :
    /// Canal (Bank ou Broker uniquement) ➔ Produit (dossier contenant Source et 2026_Rxx) ➔ Différences regroupées (avec dropdown des PDF).
    /// Les documents compatibles et manquants ne sont jamais inclus ici afin de garder la vue épurée.
    /// </summary>
    public static List<ChannelDiffGroupViewModel> BuildChannelProductDiffTree(IReadOnlyList<DocumentPair> allPairs)
    {
        var channels = new List<ChannelDiffGroupViewModel>();
        if (allPairs == null || allPairs.Count == 0)
            return channels;

        var differentPairs = allPairs
            .Where(p => p.Status == CompareStatus.Different && p.DiffBlocks.Count > 0)
            .ToList();

        if (differentPairs.Count == 0)
            return channels;

        var channelGroups = differentPairs
            .GroupBy(p => string.Equals(p.EffectiveChannel, "Broker", StringComparison.OrdinalIgnoreCase) ? "Broker" : "Bank", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => string.Equals(g.Key, "Bank", StringComparison.OrdinalIgnoreCase) ? 1 : 2);

        foreach (var chGroup in channelGroups)
        {
            string channelName = string.Equals(chGroup.Key, "Broker", StringComparison.OrdinalIgnoreCase) ? "Broker" : "Bank";

            var chVm = new ChannelDiffGroupViewModel
            {
                ChannelKey = channelName,
                ChannelDisplayName = channelName,
                ChannelKind = channelName,
                IsExpanded = true
            };

            var productGroups = chGroup
                .GroupBy(p =>
                {
                    string prod = p.EffectiveProductCode;
                    return !string.IsNullOrWhiteSpace(prod) ? prod : "Produit";
                }, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var prodGroup in productGroups)
            {
                var prodPairs = prodGroup.ToList();
                string productName = prodGroup.Key;

                var coalesced = BuildCoalescedProductBuckets(
                    prodPairs,
                    getDocKey: p => $"{p.SourcePath}|{p.TargetPath}",
                    getBlocks: p => p.DiffBlocks,
                    createDocItem: (p, b) => new ImpactedPdfItemViewModel
                    {
                        Pair = p,
                        DisplayFileName = p.FileNameOnly,
                        FirstPageNumber = b.PageNumber
                    },
                    getDisplayFileName: item => item.DisplayFileName);

                var prodVm = new ProductDiffGroupViewModel
                {
                    Channel = channelName,
                    ProductName = productName,
                    ProductGroupKey = $"{channelName} • {productName}",
                    ImpactedPairs = prodPairs,
                    IsExpanded = true
                };

                var diffRows = coalesced
                    .Select(c =>
                    {
                        if (string.IsNullOrWhiteSpace(c.Block.ProductCode) &&
                            !ProductCodeHelper.IsGenericOrEmptyProduct(productName) &&
                            !string.Equals(productName, "Produit", StringComparison.OrdinalIgnoreCase))
                        {
                            c.Block.ProductCode = productName;
                        }

                        return new GroupedDiffRowViewModel
                        {
                            Block = c.Block,
                            Channel = channelName,
                            ProductName = productName,
                            PagesCompactLabel = c.Pages.Count > 1
                                ? $"Pages {string.Join(", ", c.Pages)}"
                                : $"Page {c.Block.PageNumber}",
                            ImpactedPdfs = c.Docs,
                            IsDetailsExpanded = false
                        };
                    })
                    .OrderByDescending(r => r.ImpactedPdfs.Count)
                    .ThenBy(r => r.Block.PageNumber)
                    .ToList();

                foreach (var dr in diffRows)
                {
                    prodVm.Differences.Add(dr);
                }

                chVm.Products.Add(prodVm);
            }

            channels.Add(chVm);
        }

        return channels;
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
            var groupList = g.ToList();
            var coalesced = BuildCoalescedProductBuckets(
                groupList,
                getDocKey: p => $"{p.SourcePath}|{p.TargetPath}",
                getBlocks: p => p.DiffBlocks,
                createDocItem: (p, _) => p,
                getDisplayFileName: p => p.FileNameOnly);

            totalUnique += coalesced.Count;
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
            string effectiveChannel = pair.EffectiveChannel;
            string effectiveProd = pair.EffectiveProductCode;
            bool hasProd = !string.IsNullOrWhiteSpace(effectiveProd);
            string badge = pair.DisplayProductBadge;

            string fullTitle = hasProd
                ? $"[{badge}] {rawDocName}"
                : rawDocName;

            string lang = hasProd ? $"{effectiveChannel}/{effectiveProd}" : effectiveChannel;

            var summary = new DocumentDiffSummary
            {
                DocumentName = fullTitle,
                Language = lang,
                Channel = effectiveChannel,
                ProductName = effectiveProd,
                RawFileName = rawDocName,
                SourcePath = pair.SourcePath ?? string.Empty,
                TargetPath = pair.TargetPath ?? string.Empty,
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
            ImpactedDocuments = b.ImpactedDocuments != null ? new List<string>(b.ImpactedDocuments) : new List<string>(),
            ProductCode = b.ProductCode,
            Channel = b.Channel,
            HasPdfBounds = b.HasPdfBounds,
            PdfLeft = b.PdfLeft,
            PdfBottom = b.PdfBottom,
            PdfRight = b.PdfRight,
            PdfTop = b.PdfTop,
            FieldZoneLeft = b.FieldZoneLeft,
            FieldZoneBottom = b.FieldZoneBottom,
            FieldZoneRight = b.FieldZoneRight,
            FieldZoneTop = b.FieldZoneTop,
            PdfPageWidth = b.PdfPageWidth,
            PdfPageHeight = b.PdfPageHeight,
            IsScrambledZoneGroup = b.IsScrambledZoneGroup,
            VariantCount = b.VariantCount,
            ScrambledVariants = b.ScrambledVariants != null ? new List<string>(b.ScrambledVariants) : new List<string>()
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
