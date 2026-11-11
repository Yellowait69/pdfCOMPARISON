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

    public async Task ProcessPairsAsync(
        IEnumerable<DocumentPair> validPairs,
        string outputDiffDir,
        IProgress<int> progress,
        CancellationToken cancellationToken = default,
        bool copyToProductOutput = false)
    {
        if (validPairs == null) throw new ArgumentNullException(nameof(validPairs));

        int completed = 0;
        var allSummaries = new ConcurrentBag<DocumentDiffSummary>();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 16)),
            CancellationToken = cancellationToken
        };

        var pairsList = validPairs.ToList();
        bool hasBankChannel = pairsList.Any(p => string.Equals(p.Channel, "Bank", StringComparison.OrdinalIgnoreCase));

        var batches = hasBankChannel
            ? new List<List<DocumentPair>>
            {
                pairsList.Where(p => string.Equals(p.Channel, "Bank", StringComparison.OrdinalIgnoreCase)).ToList(),
                pairsList.Where(p => !string.Equals(p.Channel, "Bank", StringComparison.OrdinalIgnoreCase)).ToList()
            }
            : new List<List<DocumentPair>> { pairsList };

        foreach (var batch in batches.Where(b => b.Count > 0))
        {
            await Parallel.ForEachAsync(batch, parallelOptions, (pair, ct) =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var (sourceText, sourceWords, _, _) = _extractionService.ExtractDocumentFullData(pair.SourcePath);
                    var (targetText, targetWords, _, _) = _extractionService.ExtractDocumentFullData(pair.TargetPath!);

                    if (string.IsNullOrWhiteSpace(sourceText) && string.IsNullOrWhiteSpace(targetText) &&
                        sourceWords.Count == 0 && targetWords.Count == 0)
                    {
                        UpdatePairStatus(pair, CompareStatus.Identical, "Aucun texte visible détecté", 0, 0, 0, 100.0, DiffSeverity.None, 0, null);
                    }
                    else if (string.Equals(sourceText, targetText, StringComparison.Ordinal))
                    {
                        UpdatePairStatus(pair, CompareStatus.Identical, "Conforme (Texte 100% identique)", 0, 0, 0, 100.0, DiffSeverity.None, 0, null);
                    }
                    else
                    {
                        ProcessSinglePair(pair, sourceText, targetText, sourceWords, targetWords, false, outputDiffDir, allSummaries, copyToProductOutput, ct);
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
        }

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
        bool copyToProductOutput,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string cleanSource = _extractionService.NormalizePdfText(sourceText);
        string cleanTarget = _extractionService.NormalizePdfText(targetText);

        ct.ThrowIfCancellationRequested();

        var diffResult = _diffAnalyzer.AnalyzeDifferences(pair, cleanSource, cleanTarget, sourceWords, targetWords);

        int visualInsertions = CountVisualSegments(diffResult.Highlights.TargetRed);
        int visualDeletions = CountVisualSegments(diffResult.Highlights.SourceRed);

        // Uniquement les vraies différences de texte vérifiées
        int totalVisualDiffs = diffResult.Summary.Blocks.Count;

        diffResult.DifferencesCount = totalVisualDiffs;
        double ssimPercent = totalVisualDiffs == 0 ? 100.0 : Math.Round(diffResult.OverallSsimScore * 100.0, 2);

        if (totalVisualDiffs > 0)
        {
            string safeKey = BuildSafeReportKey(pair);
            string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{safeKey}.pdf");

            ct.ThrowIfCancellationRequested();

            var firstBlock = diffResult.Summary.Blocks[0];
            string shortOld = TruncateInline(firstBlock.OldText, 22);
            string shortNew = TruncateInline(firstBlock.NewText, 22);
            string statusDetail = totalVisualDiffs == 1
                ? $"1 diff (P.{firstBlock.PageNumber}) : {shortOld} ➔ {shortNew}"
                : $"{totalVisualDiffs} diffs • P.{firstBlock.PageNumber}: {shortOld} ➔ {shortNew} (+{totalVisualDiffs - 1})";

            try
            {
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, reportPath, diffResult.Highlights, diffResult.VisualRegions, diffResult.Summary.Blocks);
                diffResult.Summary.ReportFileName = Path.GetFileName(reportPath);
                SetReportPath(pair, reportPath);
                TryCopyReportToProductOutput(pair, reportPath, copyToProductOutput);

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
                string fallbackPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{safeKey}_{DateTime.Now:HHmmss}.pdf");
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, fallbackPath, diffResult.Highlights, diffResult.VisualRegions, diffResult.Summary.Blocks);
                diffResult.Summary.ReportFileName = Path.GetFileName(fallbackPath);
                SetReportPath(pair, fallbackPath);
                TryCopyReportToProductOutput(pair, fallbackPath, copyToProductOutput);

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
            UpdatePairStatus(pair, CompareStatus.Identical, "Conforme (Texte 100% identique)", 0, 0, 0, 100.0, DiffSeverity.None, 0, null);
        }
    }

    private static string TruncateInline(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(vide)";
        string singleLine = value.Replace("\r\n", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= maxLen ? singleLine : singleLine.Substring(0, maxLen) + "…";
    }

    private static string BuildSafeReportKey(DocumentPair pair)
    {
        string raw = pair.MatchKey ?? "Document";
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var chars = raw.Select(c => invalidChars.Contains(c) || c == '[' || c == ']' || c == '•' ? '_' : c).ToArray();
        string cleaned = new string(chars).Trim('_');
        while (cleaned.Contains("__"))
        {
            cleaned = cleaned.Replace("__", "_");
        }
        cleaned = cleaned.Replace(' ', '_');

        if (cleaned.Length > 85)
        {
            uint hash = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(raw);
            cleaned = $"{cleaned.Substring(0, 74)}_{hash:X8}";
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "Doc" : cleaned;
    }

    private static void TryCopyReportToProductOutput(DocumentPair pair, string generatedReportPath, bool copyToProductOutput)
    {
        if (!copyToProductOutput || string.IsNullOrWhiteSpace(pair.ProductOutputDirectory) || !File.Exists(generatedReportPath))
            return;

        try
        {
            Directory.CreateDirectory(pair.ProductOutputDirectory);
            string srcDir = Path.GetDirectoryName(generatedReportPath) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(generatedReportPath);

            string[] relatedFiles =
            {
                generatedReportPath,
                Path.Combine(srcDir, $"{stem}_SOURCE.pdf"),
                Path.Combine(srcDir, $"{stem}_TARGET.pdf"),
                Path.Combine(srcDir, $"{stem}_SideBySide.html")
            };

            foreach (var file in relatedFiles)
            {
                if (!File.Exists(file)) continue;
                string destFile = Path.Combine(pair.ProductOutputDirectory, Path.GetFileName(file));
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(destFile), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file, destFile, overwrite: true);
                }
            }
        }
        catch
        {
            // Ignorer si le dossier Output réseau est verrouillé ou en lecture seule
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
