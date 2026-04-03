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

    // Injection de dépendances sécurisée
    public PdfComparisonOrchestrator(
        PdfExtractionService extractionService,
        PdfDiffAnalyzer diffAnalyzer,
        PdfReportGenerator reportGenerator)
    {
        _extractionService = extractionService ?? throw new ArgumentNullException(nameof(extractionService));
        _diffAnalyzer = diffAnalyzer ?? throw new ArgumentNullException(nameof(diffAnalyzer));
        _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
    }

    public async Task ProcessPairsAsync(IEnumerable<DocumentPair> validPairs, string outputDiffDir, IProgress<int> progress, CancellationToken cancellationToken = default)
    {
        if (validPairs == null) throw new ArgumentNullException(nameof(validPairs));

        int completed = 0;
        var allSummaries = new ConcurrentBag<DocumentDiffSummary>();

        // Limitation du parallélisme au nombre de cœurs logiques pour éviter de surcharger le disque (I/O Bound)
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(validPairs, parallelOptions, (pair, ct) =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var sourceText = _extractionService.ExtractTextFast(pair.SourcePath);
                var targetText = _extractionService.ExtractTextFast(pair.TargetPath!);

                // Vérification rapide avant de lancer l'artillerie lourde (DiffPlex + Surbriallance)
                if (string.Equals(sourceText, targetText, StringComparison.Ordinal))
                {
                    pair.Status = CompareStatus.Identical;
                    pair.ErrorMessage = "Identical (No differences)";
                    pair.DiffCount = 0;
                }
                else
                {
                    ProcessSinglePair(pair, sourceText, targetText, outputDiffDir, allSummaries, ct);
                }

                pair.CompletedTime = DateTime.Now;
            }
            catch (OperationCanceledException)
            {
                pair.Status = CompareStatus.Pending;
                pair.ErrorMessage = "Cancelled by user";
            }
            catch (IOException ex) // Capture spécifiquement les erreurs de fichiers (ex: fichier ouvert)
            {
                pair.Status = CompareStatus.Error;
                pair.ErrorMessage = $"File access error (is it open?): {ex.Message}";
                pair.DiffCount = -1;
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

        // Génération du rapport de synthèse global (Synchrone mais exécuté en tâche de fond pour ne pas figer l'UI)
        if (!allSummaries.IsEmpty)
        {
            await Task.Run(() =>
            {
                try
                {
                    _reportGenerator.GenerateGlobalSynthesisReport(allSummaries.ToList(), outputDiffDir);
                }
                catch (Exception ex)
                {
                    // Ne doit pas faire crasher l'application si seul le rapport global échoue
                    System.Diagnostics.Debug.WriteLine($"Global Report Error: {ex.Message}");
                }
            }, cancellationToken);
        }
    }

    private void ProcessSinglePair(
        DocumentPair pair,
        string sourceText,
        string targetText,
        string outputDiffDir,
        ConcurrentBag<DocumentDiffSummary> summariesBag,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string cleanSource = _extractionService.NormalizePdfText(sourceText);
        string cleanTarget = _extractionService.NormalizePdfText(targetText);

        var sourceWords = _extractionService.ExtractWords(pair.SourcePath);
        var targetWords = _extractionService.ExtractWords(pair.TargetPath!);

        ct.ThrowIfCancellationRequested();

        // 1. Appel au nouveau PdfDiffAnalyzer (refactorisé et allégé)
        var diffResult = _diffAnalyzer.AnalyzeDifferences(pair, cleanSource, cleanTarget, sourceWords, targetWords);

        pair.DiffCount = diffResult.DifferencesCount;

        if (diffResult.DifferencesCount > 0)
        {
            string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

            ct.ThrowIfCancellationRequested();

            // 2. Génération du rendu avec gestion des verrous système (File Lock)
            try
            {
                _reportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, reportPath, diffResult.Highlights);

                pair.ReportPath = reportPath;
                pair.Status = CompareStatus.Different;
                pair.ErrorMessage = $"{diffResult.DifferencesCount} difference(s) detected";
            }
            catch (IOException)
            {
                // Si le fichier est verrouillé (ex: ouvert par l'utilisateur), on tente de créer une version horodatée
                string fallbackPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}_{DateTime.Now:HHmmss}.pdf");
                _reportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, fallbackPath, diffResult.Highlights);

                pair.ReportPath = fallbackPath;
                pair.Status = CompareStatus.Different;
                pair.ErrorMessage = $"{diffResult.DifferencesCount} difference(s) (Saved as new version)";
            }

            summariesBag.Add(diffResult.Summary);
        }
        else
        {
            // Élimination des "Faux Positifs" détectés par le moteur sémantique
            pair.Status = CompareStatus.Identical;
            pair.ErrorMessage = "False positives ignored";
        }
    }
}