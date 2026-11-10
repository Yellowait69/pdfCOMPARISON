using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PDFComparison.Models;

namespace PDFComparison.Services;

public class PdfComparisonOrchestrator
{
    private readonly PdfExtractionService _extractionService;
    private readonly PdfDiffAnalyzer _diffAnalyzer;
    private readonly IIndividualReportGenerator _individualReportGenerator;
    private readonly IGlobalSynthesisReportGenerator _globalReportGenerator;

    public PdfComparisonOrchestrator(
        PdfExtractionService extractionService,
        PdfDiffAnalyzer diffAnalyzer,
        IIndividualReportGenerator individualReportGenerator,
        IGlobalSynthesisReportGenerator globalReportGenerator)
    {
        _extractionService = extractionService ?? throw new ArgumentNullException(nameof(extractionService));
        _diffAnalyzer = diffAnalyzer ?? throw new ArgumentNullException(nameof(diffAnalyzer));
        _individualReportGenerator = individualReportGenerator ?? throw new ArgumentNullException(nameof(individualReportGenerator));
        _globalReportGenerator = globalReportGenerator ?? throw new ArgumentNullException(nameof(globalReportGenerator));
    }

    public async Task ProcessPairsAsync(IEnumerable<DocumentPair> validPairs, string outputDiffDir, IProgress<int> progress, CancellationToken cancellationToken = default)
    {
        if (validPairs == null) throw new ArgumentNullException(nameof(validPairs));

        int completed = 0;
        var allSummaries = new ConcurrentBag<DocumentDiffSummary>();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 16)),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(validPairs, parallelOptions, (pair, ct) =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var (sourceText, sourceWords, sourceImagesCount, sourceImagesHash) = _extractionService.ExtractDocumentFullData(pair.SourcePath);
                var (targetText, targetWords, targetImagesCount, targetImagesHash) = _extractionService.ExtractDocumentFullData(pair.TargetPath!);

                bool isScannedPdf = sourceWords.Count == 0 && targetWords.Count == 0 && (sourceImagesCount > 0 || targetImagesCount > 0);

                if (string.IsNullOrWhiteSpace(sourceText) && string.IsNullOrWhiteSpace(targetText) &&
                    sourceWords.Count == 0 && targetWords.Count == 0 &&
                    sourceImagesCount == 0 && targetImagesCount == 0)
                {
                    UpdatePairStatus(pair, CompareStatus.Error, "Unreadable / Empty PDF files", -1, 0, 0, 0.0, DiffSeverity.None, 0, null);
                }
                else if (isScannedPdf && sourceImagesHash == targetImagesHash)
                {
                    UpdatePairStatus(pair, CompareStatus.Identical, "PDF Scanné • Conforme (SSIM 100%)", 0, 0, 0, 100.0, DiffSeverity.None, 0, null);
                }
                else if (!isScannedPdf &&
                         string.Equals(sourceText, targetText, StringComparison.Ordinal) &&
                         sourceImagesHash == targetImagesHash)
                {
                    UpdatePairStatus(pair, CompareStatus.Identical, "Identical (No differences)", 0, 0, 0, 100.0, DiffSeverity.None, 0, null);
                }
                else
                {
                    ProcessSinglePair(pair, sourceText, targetText, sourceWords, targetWords, isScannedPdf, outputDiffDir, allSummaries, ct);
                }

                RunOnUiThread(() => pair.CompletedTime = DateTime.Now);
            }
            catch (OperationCanceledException)
            {
                UpdatePairStatus(pair, CompareStatus.Pending, "Cancelled by user", pair.DiffCount, pair.InsertionsCount, pair.DeletionsCount, pair.SsimScore, pair.MaxSeverity, pair.CriticalCount, null);
            }
            catch (IOException ex)
            {
                UpdatePairStatus(pair, CompareStatus.Error, $"File access error (is it open?): {ex.Message}", -1, 0, 0, 0.0, DiffSeverity.None, 0, null);
            }
            catch (Exception ex)
            {
                UpdatePairStatus(pair, CompareStatus.Error, $"Error: {ex.Message}", -1, 0, 0, 0.0, DiffSeverity.None, 0, null);
            }
            finally
            {
                int currentCount = Interlocked.Increment(ref completed);
                progress.Report(currentCount);
            }

            return ValueTask.CompletedTask;
        });

        if (!allSummaries.IsEmpty)
        {
            await Task.Run(() =>
            {
                try
                {
                    _globalReportGenerator.GenerateGlobalSynthesisReport(allSummaries.ToList(), outputDiffDir);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Global Report Error: {ex.Message}");
                }
            }, cancellationToken);
        }
    }

    private void ProcessSinglePair(
        DocumentPair pair,
        string sourceText,
        string targetText,
        IReadOnlyList<PdfWordInfo> sourceWords,
        IReadOnlyList<PdfWordInfo> targetWords,
        bool isScannedPdf,
        string outputDiffDir,
        ConcurrentBag<DocumentDiffSummary> summariesBag,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string cleanSource = _extractionService.NormalizePdfText(sourceText);
        string cleanTarget = _extractionService.NormalizePdfText(targetText);

        ct.ThrowIfCancellationRequested();

        var diffResult = _diffAnalyzer.AnalyzeDifferences(pair, cleanSource, cleanTarget, sourceWords, targetWords);

        int visualInsertions = CountVisualSegments(diffResult.Highlights.TargetRed);
        int visualDeletions = CountVisualSegments(diffResult.Highlights.SourceRed);

        int totalVisualDiffs = diffResult.Summary.Blocks.Count > 0
            ? diffResult.Summary.Blocks.Count
            : (visualInsertions + visualDeletions + diffResult.VisualRegions.Count);

        diffResult.DifferencesCount = totalVisualDiffs;
        double ssimPercent = Math.Round(diffResult.OverallSsimScore * 100.0, 2);

        if (totalVisualDiffs > 0)
        {
            string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

            ct.ThrowIfCancellationRequested();

            string statusDetail = isScannedPdf
                ? $"PDF Scanné • {totalVisualDiffs} écart(s) visuel(s)"
                : $"{totalVisualDiffs} diff(s) • {diffResult.Summary.CriticalAlertsCount} critique(s)";

            try
            {
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, reportPath, diffResult.Highlights, diffResult.VisualRegions);
                diffResult.Summary.ReportFileName = Path.GetFileName(reportPath);
                SetReportPath(pair, reportPath);

                UpdatePairStatus(
                    pair,
                    CompareStatus.Different,
                    statusDetail,
                    totalVisualDiffs,
                    visualInsertions,
                    visualDeletions,
                    ssimPercent,
                    diffResult.Summary.MaxSeverity,
                    diffResult.Summary.CriticalAlertsCount,
                    diffResult.Summary.Blocks);
            }
            catch (IOException)
            {
                string fallbackPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}_{DateTime.Now:HHmmss}.pdf");
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, fallbackPath, diffResult.Highlights, diffResult.VisualRegions);
                diffResult.Summary.ReportFileName = Path.GetFileName(fallbackPath);
                SetReportPath(pair, fallbackPath);

                UpdatePairStatus(
                    pair,
                    CompareStatus.Different,
                    $"{statusDetail} (New file)",
                    totalVisualDiffs,
                    visualInsertions,
                    visualDeletions,
                    ssimPercent,
                    diffResult.Summary.MaxSeverity,
                    diffResult.Summary.CriticalAlertsCount,
                    diffResult.Summary.Blocks);
            }

            summariesBag.Add(diffResult.Summary);
        }
        else
        {
            string okMessage = isScannedPdf
                ? $"PDF Scanné • Conforme (SSIM {ssimPercent:0.0}%)"
                : "False positives ignored (SSIM & Reflow verified)";
            UpdatePairStatus(pair, CompareStatus.Identical, okMessage, 0, 0, 0, ssimPercent, DiffSeverity.None, 0, null);
        }
    }

    private int CountVisualSegments(IEnumerable<LetterLoc> letters)
    {
        if (letters == null || !letters.Any()) return 0;

        int totalBlocksCount = 0;
        var lettersByPage = letters.GroupBy(l => l.PageNumber);

        foreach (var pageGroup in lettersByPage)
        {
            var segments = VisualSegmentHelper.GetSegments(pageGroup);
            totalBlocksCount += VisualSegmentHelper.CountBlocks(segments);
        }

        return totalBlocksCount;
    }

    private void UpdatePairStatus(
        DocumentPair pair,
        CompareStatus status,
        string errorMessage,
        int diffCount,
        int insertions,
        int deletions,
        double ssimScore,
        DiffSeverity maxSeverity,
        int criticalCount,
        IReadOnlyList<DiffSummaryBlock>? blocks)
    {
        RunOnUiThread(() =>
        {
            pair.Status = status;
            pair.ErrorMessage = errorMessage;
            if (diffCount != pair.DiffCount) pair.DiffCount = diffCount;

            pair.InsertionsCount = insertions;
            pair.DeletionsCount = deletions;
            pair.SsimScore = ssimScore;
            pair.MaxSeverity = maxSeverity;
            pair.CriticalCount = criticalCount;

            pair.DiffBlocks.Clear();
            if (blocks != null)
            {
                foreach (var b in blocks)
                {
                    pair.DiffBlocks.Add(b);
                }
            }
        });
    }

    private void SetReportPath(DocumentPair pair, string reportPath)
    {
        RunOnUiThread(() => pair.ReportPath = reportPath);
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }
}
