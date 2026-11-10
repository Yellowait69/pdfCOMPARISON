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

    public DiffAnalysisResult AnalyzeDifferences(DocumentPair pair, string cleanSource, string cleanTarget, IReadOnlyList<PdfWordInfo> sourceWords, IReadOnlyList<PdfWordInfo> targetWords)
    {
        if (pair == null) throw new ArgumentNullException(nameof(pair));

        string lang = "ND";
        if (!string.IsNullOrEmpty(pair.MatchKey) && pair.MatchKey.Contains('_'))
        {
            lang = pair.MatchKey.Split('_')[0].ToUpperInvariant();
        }

        var result = new DiffAnalysisResult
        {
            Summary = new DocumentDiffSummary
            {
                DocumentName = Path.GetFileName(pair.TargetPath ?? "UnknownTarget.pdf"),
                Language = lang
            }
        };

        var sourceLinesList = _layoutSanitizer.GroupIntoLines(sourceWords);
        var targetLinesList = _layoutSanitizer.GroupIntoLines(targetWords);

        string sourceDiffText = BuildDiffText(sourceLinesList);
        string targetDiffText = BuildDiffText(targetLinesList);

        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        var diffLinesModelForVisuals = diffBuilder.BuildDiffModel(sourceDiffText, targetDiffText);

        result.Highlights = _visualMatcherService.GenerateHighlights(diffLinesModelForVisuals, sourceLinesList, targetLinesList);

        string formattedSource = _layoutSanitizer.CleanLineForDiff(cleanSource);
        string formattedTarget = _layoutSanitizer.CleanLineForDiff(cleanTarget);

        // Construction des blocs de synthèse vérifiés (uniquement pour les différences réelles après filtrage du reflow)
        if (result.Highlights.SourceRed.Count > 0 || result.Highlights.TargetRed.Count > 0)
        {
            var verifiedBlocks = _textSummaryService.BuildVerifiedSummaryBlocks(
                formattedSource,
                formattedTarget,
                sourceLinesList,
                targetLinesList,
                result.Highlights);

            result.Summary.Blocks.AddRange(verifiedBlocks);
        }

        // Validation perceptuelle (SSIM + Distance YIQ + Filtre Anti-Aliasing) et génération des captures visuelles (SourceImage / TargetImage)
        if (_visualRegressionService != null &&
            !string.IsNullOrWhiteSpace(pair.SourcePath) &&
            !string.IsNullOrWhiteSpace(pair.TargetPath))
        {
            _visualRegressionService.AnalyzeVisualAndPopulateSnapshots(
                pair.SourcePath,
                pair.TargetPath!,
                sourceWords,
                targetWords,
                result);
        }

        if (result.Highlights.SourceRed.Count == 0 &&
            result.Highlights.TargetRed.Count == 0 &&
            result.VisualRegions.Count == 0)
        {
            result.Summary.Blocks.Clear();
            result.DifferencesCount = 0;
            result.Summary.MaxSeverity = DiffSeverity.None;
            result.Summary.CriticalAlertsCount = 0;
            return result;
        }

        result.DifferencesCount = result.Summary.Blocks.Count;
        result.Summary.CriticalAlertsCount = result.Summary.Blocks.Count(b => b.Severity == DiffSeverity.Critical);
        result.Summary.MaxSeverity = result.Summary.Blocks.Count > 0
            ? result.Summary.Blocks.Max(b => b.Severity)
            : DiffSeverity.Minor;

        return result;
    }

    private string BuildDiffText(List<List<(string CleanText, List<LetterLoc> Letters)>> linesList)
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
