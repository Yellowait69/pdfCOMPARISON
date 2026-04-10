using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PDFComparison.Models;
using DiffPlex.DiffBuilder.Model; // NOUVEAU : Ajouté pour accéder à ChangeType

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

        if (diffResult.DifferencesCount > 0)
        {
            string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

            ct.ThrowIfCancellationRequested();

            // NOUVEAU : Calcul du nombre d'ajouts et de suppressions basés sur les blocs
            int insertions = diffResult.Summary.Blocks.Count(b => b.Type == ChangeType.Inserted);
            int deletions = diffResult.Summary.Blocks.Count(b => b.Type == ChangeType.Deleted);

            try
            {
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, reportPath, diffResult.Highlights);

                SetReportPath(pair, reportPath);

                // NOUVEAU : On passe les compteurs d'ajouts et suppressions
                UpdatePairStatus(pair, CompareStatus.Different, $"{diffResult.DifferencesCount} difference(s) detected", diffResult.DifferencesCount, insertions, deletions);
            }
            catch (IOException)
            {
                // Si le fichier est verrouillé (ex: ouvert par l'utilisateur), on tente de créer une version horodatée
                string fallbackPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}_{DateTime.Now:HHmmss}.pdf");
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, fallbackPath, diffResult.Highlights);

                SetReportPath(pair, fallbackPath);

                // NOUVEAU : On passe les compteurs d'ajouts et suppressions
                UpdatePairStatus(pair, CompareStatus.Different, $"{diffResult.DifferencesCount} difference(s) (Saved as new version)", diffResult.DifferencesCount, insertions, deletions);
            }

            summariesBag.Add(diffResult.Summary);
        }
        else
        {
            // Élimination des "Faux Positifs" détectés par le moteur sémantique
            UpdatePairStatus(pair, CompareStatus.Identical, "False positives ignored", 0);
        }
    }

    // ==========================================
    // MÉTHODES UTILITAIRES (Thread-Safety WPF)
    // ==========================================

    // NOUVEAU : Ajout des paramètres optionnels `insertions` et `deletions`
    private void UpdatePairStatus(DocumentPair pair, CompareStatus status, string errorMessage, int diffCount, int insertions = 0, int deletions = 0)
    {
        if (Application.Current != null && Application.Current.Dispatcher != null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                pair.Status = status;
                pair.ErrorMessage = errorMessage;
                if (diffCount != pair.DiffCount) pair.DiffCount = diffCount;

                // NOUVEAU : Mise à jour des compteurs
                pair.InsertionsCount = insertions;
                pair.DeletionsCount = deletions;
            });
        }
        else
        {
            // Fallback (ex: pendant les tests unitaires où Application.Current est null)
            pair.Status = status;
            pair.ErrorMessage = errorMessage;
            pair.DiffCount = diffCount;

            // NOUVEAU : Mise à jour des compteurs
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