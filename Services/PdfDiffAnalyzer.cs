using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using PDFComparison.Models;

namespace PDFComparison.Services;

public class PdfDiffAnalyzer
{
    private readonly IPdfLayoutSanitizerService _layoutSanitizer;
    private readonly ITextDiffSummaryService _textSummaryService;
    private readonly IVisualHighlightMatcherService _visualMatcherService;
    private readonly IPdfVisualRegressionService? _visualRegressionService;

    public PdfDiffAnalyzer(
        IPdfLayoutSanitizerService layoutSanitizer,
        ITextDiffSummaryService textSummaryService,
        IVisualHighlightMatcherService visualMatcherService,
        IPdfVisualRegressionService? visualRegressionService = null)
    {
        _layoutSanitizer = layoutSanitizer ?? throw new ArgumentNullException(nameof(layoutSanitizer));
        _textSummaryService = textSummaryService ?? throw new ArgumentNullException(nameof(textSummaryService));
        _visualMatcherService = visualMatcherService ?? throw new ArgumentNullException(nameof(visualMatcherService));
        _visualRegressionService = visualRegressionService;
    }

    public DiffAnalysisResult AnalyzeDifferences(
        DocumentPair pair,
        string cleanSource,
        string cleanTarget,
        IReadOnlyList<PdfWordInfo> sourceWords,
        IReadOnlyList<PdfWordInfo> targetWords)
    {
        if (pair == null) throw new ArgumentNullException(nameof(pair));

        string effectiveProduct = pair.EffectiveProductCode;
        string effectiveChannel = pair.EffectiveChannel;
        string lang = "ND";
        if (!string.IsNullOrWhiteSpace(effectiveProduct))
        {
            lang = string.IsNullOrWhiteSpace(effectiveChannel)
                ? effectiveProduct
                : $"{effectiveChannel}/{effectiveProduct}";
        }
        else if (!string.IsNullOrEmpty(pair.MatchKey) && pair.MatchKey.Contains('_'))
        {
            lang = pair.MatchKey.Split('_')[0].ToUpperInvariant();
        }

        string rawDocName = Path.GetFileName(pair.TargetPath ?? pair.SourcePath ?? "UnknownTarget.pdf");
        string fullDocTitle = !string.IsNullOrWhiteSpace(effectiveProduct)
            ? $"[{pair.DisplayProductBadge}] {rawDocName}"
            : rawDocName;

        var result = new DiffAnalysisResult
        {
            OverallSsimScore = 1.0,
            Summary = new DocumentDiffSummary
            {
                DocumentName = fullDocTitle,
                Language = lang,
                Channel = effectiveChannel,
                ProductName = effectiveProduct,
                RawFileName = rawDocName,
                SourcePath = pair.SourcePath ?? string.Empty,
                TargetPath = pair.TargetPath ?? string.Empty,
                OverallSsimScore = 1.0
            }
        };

        var sourceLinesList = _layoutSanitizer.GroupIntoLines(sourceWords);
        var targetLinesList = _layoutSanitizer.GroupIntoLines(targetWords);

        string sourceDiffText = BuildDiffText(sourceLinesList);
        string targetDiffText = BuildDiffText(targetLinesList);

        // Si le texte visuellement ordonné et normalisé est strictement identique -> 0 différence
        if (string.Equals(sourceDiffText, targetDiffText, StringComparison.Ordinal))
        {
            result.DifferencesCount = 0;
            return result;
        }

        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        var diffLinesModelForVisuals = diffBuilder.BuildDiffModel(sourceDiffText, targetDiffText);

        result.Highlights = _visualMatcherService.GenerateHighlights(diffLinesModelForVisuals, sourceLinesList, targetLinesList);

        if (result.Highlights.SourceRed.Count == 0 && result.Highlights.TargetRed.Count == 0)
        {
            result.DifferencesCount = 0;
            return result;
        }

        string formattedSource = _layoutSanitizer.CleanLineForDiff(cleanSource);
        string formattedTarget = _layoutSanitizer.CleanLineForDiff(cleanTarget);

        // Construction des blocs de synthèse vérifiés uniquement sur les vraies différences de texte
        var verifiedBlocks = _textSummaryService.BuildVerifiedSummaryBlocks(
            formattedSource,
            formattedTarget,
            sourceLinesList,
            targetLinesList,
            result.Highlights,
            sourceWords,
            targetWords,
            effectiveProduct,
            effectiveChannel,
            pair.SourcePath,
            pair.TargetPath);

        result.Summary.Blocks.AddRange(verifiedBlocks);

        // Si aucun bloc de texte n'a de vraie différence alphanumérique visible -> document 100% conforme
        if (result.Summary.Blocks.Count == 0)
        {
            result.Highlights.SourceRed.Clear();
            result.Highlights.TargetRed.Clear();
            result.VisualRegions.Clear();
            result.DifferencesCount = 0;
            result.Summary.MaxSeverity = DiffSeverity.None;
            result.Summary.CriticalAlertsCount = 0;
            result.OverallSsimScore = 1.0;
            result.Summary.OverallSsimScore = 1.0;
            return result;
        }

        int totalWords = Math.Max(1, sourceWords.Count + targetWords.Count);
        int changedLetters = result.Highlights.SourceRed.Count + result.Highlights.TargetRed.Count;
        double textConcordance = Math.Clamp(1.0 - ((double)changedLetters / Math.Max(20, totalWords * 5)), 0.0, 0.999);

        result.OverallSsimScore = textConcordance;
        result.Summary.OverallSsimScore = textConcordance;
        result.DifferencesCount = result.Summary.Blocks.Count;
        result.Summary.CriticalAlertsCount = result.Summary.Blocks.Count(b => b.Severity == DiffSeverity.Critical);
        result.Summary.MaxSeverity = result.Summary.Blocks.Max(b => b.Severity);

        return result;
    }

    private static string BuildDiffText(List<List<(string CleanText, List<LetterLoc> Letters)>> linesList)
    {
        if (linesList == null || linesList.Count == 0) return string.Empty;

        int estimatedCapacity = linesList.Count * 50;
        var sb = new StringBuilder(estimatedCapacity);

        for (int i = 0; i < linesList.Count; i++)
        {
            var line = linesList[i];
            for (int j = 0; j < line.Count; j++)
            {
                sb.Append(line[j].CleanText);

                if (j < line.Count - 1)
                {
                    sb.Append(' ');
                }
            }

            if (i < linesList.Count - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }
}
