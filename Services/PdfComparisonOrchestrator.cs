using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PDFComparison.Models;
using DiffPlex.DiffBuilder.Model;

namespace PDFComparison.Services;

public class PdfComparisonOrchestrator
{
    private readonly PdfExtractionService _extractionService;
    private readonly PdfDiffAnalyzer _diffAnalyzer;
    private readonly IIndividualReportGenerator _individualReportGenerator;
    private readonly IGlobalSynthesisReportGenerator _globalReportGenerator;

    // Injection de dépendances sécurisée et alignée avec la nouvelle architecture
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

        // AMÉLIORATION : Limitation stricte du parallélisme pour éviter le OutOfMemoryException (OOM)
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

                var sourceText = _extractionService.ExtractTextFast(pair.SourcePath);
                var targetText = _extractionService.ExtractTextFast(pair.TargetPath!);

                // AMÉLIORATION : Détection des PDF scannés (sans texte / OCR requis)
                if (string.IsNullOrWhiteSpace(sourceText) && string.IsNullOrWhiteSpace(targetText))
                {
                    UpdatePairStatus(pair, CompareStatus.Error, "Unreadable files (Scanned/OCR required)", -1);
                }
                else if (string.Equals(sourceText, targetText, StringComparison.Ordinal))
                {
                    UpdatePairStatus(pair, CompareStatus.Identical, "Identical (No differences)", 0);
                }
                else
                {
                    ProcessSinglePair(pair, sourceText, targetText, outputDiffDir, allSummaries, ct);
                }

                if (Application.Current != null)
                {
                    Application.Current.Dispatcher.Invoke(() => pair.CompletedTime = DateTime.Now);
                }
            }
            catch (OperationCanceledException)
            {
                UpdatePairStatus(pair, CompareStatus.Pending, "Cancelled by user", pair.DiffCount);
            }
            catch (IOException ex) // Capture spécifiquement les erreurs de fichiers (ex: fichier ouvert)
            {
                UpdatePairStatus(pair, CompareStatus.Error, $"File access error (is it open?): {ex.Message}", -1);
            }
            catch (Exception ex)
            {
                UpdatePairStatus(pair, CompareStatus.Error, $"Error: {ex.Message}", -1);
            }
            finally
            {
                int currentCount = Interlocked.Increment(ref completed);
                progress.Report(currentCount);
            }

            return ValueTask.CompletedTask;
        });

        // Génération du rapport de synthèse global en tâche de fond pour ne pas figer l'UI
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

        var diffResult = _diffAnalyzer.AnalyzeDifferences(pair, cleanSource, cleanTarget, sourceWords, targetWords);

        // NOUVEAU : Calcul exact basé sur le rendu visuel page par page
        int visualInsertions = CountVisualSegments(diffResult.Highlights.TargetRed);
        int visualDeletions = CountVisualSegments(diffResult.Highlights.SourceRed);
        int visualModifications = CountVisualSegments(diffResult.Highlights.TargetYellow);

        int totalVisualDiffs = visualInsertions + visualDeletions + visualModifications;

        // On écrase le score textuel par le vrai score visuel
        diffResult.DifferencesCount = totalVisualDiffs;

        if (totalVisualDiffs > 0)
        {
            string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

            ct.ThrowIfCancellationRequested();

            try
            {
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, reportPath, diffResult.Highlights);
                SetReportPath(pair, reportPath);

                UpdatePairStatus(pair, CompareStatus.Different, $"{totalVisualDiffs} difference(s) detected", totalVisualDiffs, visualInsertions, visualDeletions);
            }
            catch (IOException)
            {
                string fallbackPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}_{DateTime.Now:HHmmss}.pdf");
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, fallbackPath, diffResult.Highlights);
                SetReportPath(pair, fallbackPath);

                UpdatePairStatus(pair, CompareStatus.Different, $"{totalVisualDiffs} difference(s) (Saved as new version)", totalVisualDiffs, visualInsertions, visualDeletions);
            }

            summariesBag.Add(diffResult.Summary);
        }
        else
        {
            UpdatePairStatus(pair, CompareStatus.Identical, "False positives ignored", 0);
        }
    }

    /// <summary>
    /// Reproduit très exactement la logique de regroupement géométrique et de fusion du PdfDrawingService.
    /// Garantit que le compteur de l'UI correspond à 100% au nombre de blocs visuels générés dans le PDF.
    /// </summary>
    private int CountVisualSegments(IEnumerable<LetterLoc> letters)
    {
        if (letters == null || !letters.Any()) return 0;

        int totalBlocksCount = 0;

        // ÉTAPE 1 : Séparation stricte par numéro de page
        var lettersByPage = letters.GroupBy(l => l.PageNumber);

        foreach (var pageGroup in lettersByPage)
        {
            const decimal AlignmentTolerance = 5.0m;
            var sorted = pageGroup
                .OrderByDescending(l => Math.Round(l.BaselineY / AlignmentTolerance) * AlignmentTolerance)
                .ThenBy(l => l.BoundingBox.BottomLeft.X)
                .ToList();

            if (sorted.Count == 0) continue;

            // ÉTAPE 2 : Création des segments horizontaux (lignes)
            var segments = new List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)>();
            var first = sorted[0];

            decimal cMinX = (decimal)first.BoundingBox.BottomLeft.X;
            decimal cMaxX = (decimal)first.BoundingBox.TopRight.X;
            decimal cBaseline = first.BaselineY;
            decimal cFontSize = first.FontSize;

            for (int i = 1; i < sorted.Count; i++)
            {
                var loc = sorted[i];
                decimal x = (decimal)loc.BoundingBox.BottomLeft.X;
                decimal y = loc.BaselineY;

                bool isSameLine = Math.Abs(Math.Round(y / AlignmentTolerance) * AlignmentTolerance - Math.Round(cBaseline / AlignmentTolerance) * AlignmentTolerance) < 1m;
                decimal maxGap = Math.Max(15m, cFontSize * 1.5m);

                if (isSameLine && (x - cMaxX) < maxGap && x >= cMinX - 5m)
                {
                    cMaxX = Math.Max(cMaxX, (decimal)loc.BoundingBox.TopRight.X);
                    cFontSize = Math.Max(cFontSize, loc.FontSize);
                }
                else
                {
                    segments.Add((cMinX, cMaxX, cBaseline, cFontSize));
                    cMinX = x;
                    cMaxX = (decimal)loc.BoundingBox.TopRight.X;
                    cBaseline = y;
                    cFontSize = loc.FontSize;
                }
            }
            segments.Add((cMinX, cMaxX, cBaseline, cFontSize));

            // ÉTAPE 3 : Fusion verticale des blocs proches (comme pour la barre de marge dans PdfDrawingService)
            if (segments.Count > 0)
            {
                int pageBlocksCount = 0;
                decimal currentMaxY = segments[0].baselineY + (segments[0].fontSize * 0.9m);
                decimal currentMinY = segments[0].baselineY - (segments[0].fontSize * 0.2m);

                for (int i = 1; i < segments.Count; i++)
                {
                    var seg = segments[i];
                    decimal boxMinY = seg.baselineY - (seg.fontSize * 0.2m);
                    decimal boxMaxY = seg.baselineY + (seg.fontSize * 0.9m);

                    // Si l'espace est inférieur à 2x la taille de la police, on fusionne !
                    if (currentMinY - boxMaxY < seg.fontSize * 2.0m)
                    {
                        currentMinY = Math.Min(currentMinY, boxMinY);
                        currentMaxY = Math.Max(currentMaxY, boxMaxY);
                    }
                    else
                    {
                        // L'écart est grand, on valide le bloc précédent
                        pageBlocksCount++;
                        currentMaxY = boxMaxY;
                        currentMinY = boxMinY;
                    }
                }

                // Ne pas oublier le tout dernier bloc de la page
                pageBlocksCount++;

                totalBlocksCount += pageBlocksCount;
            }
        }

        return totalBlocksCount;
    }

    // ==========================================
    // MÉTHODES UTILITAIRES (Thread-Safety WPF)
    // ==========================================

    private void UpdatePairStatus(DocumentPair pair, CompareStatus status, string errorMessage, int diffCount, int insertions = 0, int deletions = 0)
    {
        if (Application.Current != null && Application.Current.Dispatcher != null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                pair.Status = status;
                pair.ErrorMessage = errorMessage;
                if (diffCount != pair.DiffCount) pair.DiffCount = diffCount;

                pair.InsertionsCount = insertions;
                pair.DeletionsCount = deletions;
            });
        }
        else
        {
            pair.Status = status;
            pair.ErrorMessage = errorMessage;
            pair.DiffCount = diffCount;
            pair.InsertionsCount = insertions;
            pair.DeletionsCount = deletions;
        }
    }

    private void SetReportPath(DocumentPair pair, string reportPath)
    {
        if (Application.Current != null && Application.Current.Dispatcher != null)
        {
            Application.Current.Dispatcher.Invoke(() => pair.ReportPath = reportPath);
        }
        else
        {
            pair.ReportPath = reportPath;
        }
    }
}