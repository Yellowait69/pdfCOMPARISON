using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;

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
        // La manipulation de PDF et la génération d'images sont très gourmandes en RAM.
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4)),
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
                // Évite de marquer deux documents scannés comme "Identiques" car ils retournent tous les deux une chaîne vide.
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

        var diffResult = _diffAnalyzer.AnalyzeDifferences(pair, cleanSource, cleanTarget, sourceWords, targetWords);

        // ==============================================================
        // SYNCHRONISATION PARFAITE UI <=> PDF (Comptage des rectangles)
        // ==============================================================
        int inserted = CountVisualBoxes(diffResult.Highlights.TargetRed);
        int deleted = CountVisualBoxes(diffResult.Highlights.SourceRed);
        int modified = CountVisualBoxes(diffResult.Highlights.TargetYellow); // ou SourceYellow (ils vont de paire)

        int totalVisualDiffs = inserted + deleted + modified;

        // On écrase le compteur abstrait de DiffPlex avec la vraie réalité géométrique !
        diffResult.DifferencesCount = totalVisualDiffs;

        diffResult.Summary.VisualInsertedCount = inserted;
        diffResult.Summary.VisualDeletedCount = deleted;
        diffResult.Summary.VisualModifiedCount = modified;

        if (Application.Current != null && Application.Current.Dispatcher != null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                pair.InsertedCount = inserted;
                pair.DeletedCount = deleted;
                pair.ModifiedCount = modified;
            });
        }
        else
        {
            pair.InsertedCount = inserted;
            pair.DeletedCount = deleted;
            pair.ModifiedCount = modified;
        }
        // ==============================================================

        if (diffResult.DifferencesCount > 0)
        {
            string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

            ct.ThrowIfCancellationRequested();

            try
            {
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, reportPath, diffResult.Highlights);

                SetReportPath(pair, reportPath);
                UpdatePairStatus(pair, CompareStatus.Different, $"{diffResult.DifferencesCount} difference(s) detected", diffResult.DifferencesCount);
            }
            catch (IOException)
            {
                // Si le fichier est verrouillé (ex: ouvert par l'utilisateur), on tente de créer une version horodatée
                string fallbackPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}_{DateTime.Now:HHmmss}.pdf");
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, fallbackPath, diffResult.Highlights);

                SetReportPath(pair, fallbackPath);
                UpdatePairStatus(pair, CompareStatus.Different, $"{diffResult.DifferencesCount} difference(s) (Saved as new version)", diffResult.DifferencesCount);
            }

            summariesBag.Add(diffResult.Summary);
        }
        else
        {
            // Élimination des "Faux Positifs" détectés par le moteur sémantique
            UpdatePairStatus(pair, CompareStatus.Identical, "False positives ignored", 0);
        }
    }

    // ==============================================================
    // ALGORITHME GÉOMÉTRIQUE : Simule le dessin des cadres PDF
    // ==============================================================
    private int CountVisualBoxes(List<LetterLoc> letters)
    {
        if (letters == null || letters.Count == 0) return 0;

        int totalBoxes = 0;
        var pages = letters.GroupBy(l => l.PageNumber);

        foreach (var page in pages)
        {
            decimal AlignmentTolerance = 5.0m;
            var sorted = page
                .OrderByDescending(l => Math.Round(l.BaselineY / AlignmentTolerance) * AlignmentTolerance)
                .ThenBy(l => l.BoundingBox.BottomLeft.X)
                .ToList();

            if (sorted.Count == 0) continue;

            int boxCount = 0;
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

                // Tolérance de l'espace (pour lier les mots en un seul grand rectangle)
                decimal maxGap = Math.Max(15m, cFontSize * 1.5m);

                if (isSameLine && (x - cMaxX) < maxGap && x >= cMinX - 5m)
                {
                    // Extension du rectangle en cours
                    cMaxX = Math.Max(cMaxX, (decimal)loc.BoundingBox.TopRight.X);
                    cFontSize = Math.Max(cFontSize, loc.FontSize);
                }
                else
                {
                    // Clôture du rectangle précédent et démarrage d'un nouveau
                    boxCount++;
                    cMinX = x;
                    cMaxX = (decimal)loc.BoundingBox.TopRight.X;
                    cBaseline = y;
                    cFontSize = loc.FontSize;
                }
            }
            boxCount++; // Pour fermer la dernière boîte de la ligne/page
            totalBoxes += boxCount;
        }

        return totalBoxes;
    }

    // ==========================================
    // MÉTHODES UTILITAIRES (Thread-Safety WPF)
    // ==========================================

    private void UpdatePairStatus(DocumentPair pair, CompareStatus status, string errorMessage, int diffCount)
    {
        if (Application.Current != null && Application.Current.Dispatcher != null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                pair.Status = status;
                pair.ErrorMessage = errorMessage;
                if (diffCount != pair.DiffCount) pair.DiffCount = diffCount;
            });
        }
        else
        {
            // Fallback (ex: pendant les tests unitaires où Application.Current est null)
            pair.Status = status;
            pair.ErrorMessage = errorMessage;
            pair.DiffCount = diffCount;
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