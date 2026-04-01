using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDFComparison.Models;

namespace PDFComparison.Services;

public class PdfComparisonOrchestrator
{
    private readonly PdfExtractionService _extractionService;
    private readonly PdfDiffAnalyzer _diffAnalyzer;
    private readonly PdfReportGenerator _reportGenerator;

    public PdfComparisonOrchestrator(
        PdfExtractionService extractionService,
        PdfDiffAnalyzer diffAnalyzer,
        PdfReportGenerator reportGenerator)
    {
        _extractionService = extractionService;
        _diffAnalyzer = diffAnalyzer;
        _reportGenerator = reportGenerator;
    }

    public async Task ProcessPairsAsync(IEnumerable<DocumentPair> validPairs, string outputDiffDir, IProgress<int> progress, CancellationToken cancellationToken = default)
    {
        int completed = 0;
        var allSummaries = new ConcurrentBag<DocumentDiffSummary>();

        // Environment.ProcessorCount is good, but sometimes leaving it to the ThreadPool default (-1)
        // allows the runtime to manage I/O bound tasks (like reading PDFs) more efficiently.
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        // Parallel.ForEachAsync natively runs the delegate on ThreadPool threads
        await Parallel.ForEachAsync(validPairs, parallelOptions, (pair, ct) =>
        {
            try
            {
                // Ensure we haven't been cancelled before starting a new heavy task
                ct.ThrowIfCancellationRequested();

                var sourceText = _extractionService.ExtractTextFast(pair.SourcePath);
                var targetText = _extractionService.ExtractTextFast(pair.TargetPath!);

                if (string.Equals(sourceText, targetText, StringComparison.Ordinal))
                {
                    pair.Status = CompareStatus.Identical;
                    pair.ErrorMessage = "Identical (No differences)";
                    pair.DiffCount = 0;
                }
                else
                {
                    // Calling synchronous method directly since Parallel.ForEachAsync already runs this delegate on a background thread.
                    ProcessSinglePair(pair, sourceText, targetText, outputDiffDir, allSummaries, ct);
                }

                pair.CompletedTime = DateTime.Now;
            }
            catch (OperationCanceledException)
            {
                // User cancelled the operation
                pair.Status = CompareStatus.Pending;
                pair.ErrorMessage = "Cancelled";
            }
            catch (Exception ex)
            {
                pair.Status = CompareStatus.Error;
                pair.ErrorMessage = $"Error: {ex.Message}";
                pair.DiffCount = -1;
            }
            finally
            {
                int currentCount = Interlocked.Increment(ref completed);
                progress.Report(currentCount);
            }

            return ValueTask.CompletedTask;
        });

        // Outside of the Parallel loop, we use Task.Run for the final synchronous generation to keep UI responsive
        if (!allSummaries.IsEmpty)
        {
            await Task.Run(() => _reportGenerator.GenerateGlobalSynthesisReport(allSummaries.ToList(), outputDiffDir), cancellationToken);
        }
    }

    // Removed 'async Task' and 'Task.Run' because this method is already being executed
    // inside the background threads managed by Parallel.ForEachAsync.
    private void ProcessSinglePair(DocumentPair pair, string sourceText, string targetText, string outputDiffDir, ConcurrentBag<DocumentDiffSummary> summariesBag, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string cleanSource = _extractionService.NormalizePdfText(sourceText);
        string cleanTarget = _extractionService.NormalizePdfText(targetText);

        var sourceWords = _extractionService.ExtractWords(pair.SourcePath);
        var targetWords = _extractionService.ExtractWords(pair.TargetPath!);

        ct.ThrowIfCancellationRequested();

        // 1. Analyse métier (DiffPlex)
        var diffResult = _diffAnalyzer.AnalyzeDifferences(pair, cleanSource, cleanTarget, sourceWords, targetWords);

        pair.DiffCount = diffResult.DifferencesCount;

        if (diffResult.DifferencesCount > 0)
        {
            string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");
            pair.ReportPath = reportPath;
            pair.Status = CompareStatus.Different;
            pair.ErrorMessage = $"{diffResult.DifferencesCount} difference(s) detected";

            ct.ThrowIfCancellationRequested();

            // 2. Génération du rendu (PdfPig)
            _reportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, reportPath, diffResult.Highlights);

            summariesBag.Add(diffResult.Summary);
        }
        else
        {
            pair.Status = CompareStatus.Identical;
            pair.ErrorMessage = "False positives ignored";
        }
    }
}