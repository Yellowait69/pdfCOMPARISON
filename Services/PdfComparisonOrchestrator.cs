using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
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
    private readonly IPdfImageService _imageService;

    // Injection de dépendances mise à jour
    public PdfComparisonOrchestrator(
        PdfExtractionService extractionService,
        PdfDiffAnalyzer diffAnalyzer,
        IIndividualReportGenerator individualReportGenerator,
        IGlobalSynthesisReportGenerator globalReportGenerator,
        IPdfImageService imageService)
    {
        _extractionService = extractionService ?? throw new ArgumentNullException(nameof(extractionService));
        _diffAnalyzer = diffAnalyzer ?? throw new ArgumentNullException(nameof(diffAnalyzer));
        _individualReportGenerator = individualReportGenerator ?? throw new ArgumentNullException(nameof(individualReportGenerator));
        _globalReportGenerator = globalReportGenerator ?? throw new ArgumentNullException(nameof(globalReportGenerator));
        _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
    }

    public async Task ProcessPairsAsync(IEnumerable<DocumentPair> validPairs, string outputDiffDir, IProgress<int> progress, CancellationToken cancellationToken = default)
    {
        if (validPairs == null) throw new ArgumentNullException(nameof(validPairs));

        int completed = 0;
        var allSummaries = new ConcurrentBag<DocumentDiffSummary>();

        var parallelOptions = new ParallelOptions
        {
            // Limitation stricte pour éviter la saturation de RAM avec PdfiumViewer
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
            catch (IOException ex)
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

        // =====================================================================
        // NOUVEAU COMPTAGE : On utilise directement les blocs logiques fusionnés
        // du résumé au lieu de compter les fragments visuels un par un.
        // =====================================================================
        int insertions = diffResult.Summary.Blocks.Count(b => b.Type == ChangeType.Inserted);
        int deletions = diffResult.Summary.Blocks.Count(b => b.Type == ChangeType.Deleted);

        int totalDiffs = diffResult.Summary.Blocks.Count;
        diffResult.DifferencesCount = totalDiffs;

        if (totalDiffs > 0)
        {
            string reportFileName = $"DiffReport_Doc_{pair.MatchKey}.pdf";
            string reportPath = Path.Combine(outputDiffDir, reportFileName);

            ct.ThrowIfCancellationRequested();

            // =====================================================================
            // CAPTURES D'IMAGES AVEC COULEURS DYNAMIQUES
            // =====================================================================
            foreach (var block in diffResult.Summary.Blocks)
            {
                if (block.Type == ChangeType.Deleted || block.Type == ChangeType.Modified)
                {
                    var rect = FindExactBoundingBoxForText(block.OldText, sourceWords, out int pageNum);
                    if (pageNum > 0)
                    {
                        var color = block.Type == ChangeType.Modified ? Color.FromArgb(245, 158, 11) : Color.FromArgb(239, 68, 68); // Orange ou Rouge
                        block.SourceImage = _imageService.CaptureZone(pair.SourcePath, pageNum, rect, color);
                    }
                }

                if (block.Type == ChangeType.Inserted || block.Type == ChangeType.Modified)
                {
                    var rect = FindExactBoundingBoxForText(block.NewText, targetWords, out int pageNum);
                    if (pageNum > 0)
                    {
                        var color = block.Type == ChangeType.Modified ? Color.FromArgb(245, 158, 11) : Color.FromArgb(16, 185, 129); // Orange ou Vert
                        block.TargetImage = _imageService.CaptureZone(pair.TargetPath!, pageNum, rect, color);
                    }
                }
            }
            // =====================================================================

            try
            {
                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, reportPath, diffResult.Highlights);
                SetReportPath(pair, reportPath);

                // Mémorisation du nom du fichier pour le bouton du Dashboard
                diffResult.Summary.ReportFileName = reportFileName;

                UpdatePairStatus(pair, CompareStatus.Different, $"{totalDiffs} difference(s) detected", totalDiffs, insertions, deletions);
            }
            catch (IOException)
            {
                reportFileName = $"DiffReport_Doc_{pair.MatchKey}_{DateTime.Now:HHmmss}.pdf";
                string fallbackPath = Path.Combine(outputDiffDir, reportFileName);

                _individualReportGenerator.GenerateIndividualReport(pair.SourcePath, pair.TargetPath!, fallbackPath, diffResult.Highlights);
                SetReportPath(pair, fallbackPath);

                // Mémorisation du nom du fichier de secours pour le bouton du Dashboard
                diffResult.Summary.ReportFileName = reportFileName;

                UpdatePairStatus(pair, CompareStatus.Different, $"{totalDiffs} difference(s) (Saved as new version)", totalDiffs, insertions, deletions);
            }

            summariesBag.Add(diffResult.Summary);
        }
        else
        {
            UpdatePairStatus(pair, CompareStatus.Identical, "False positives ignored", 0);
        }
    }

    /// <summary>
    /// Trouve la boîte EXACTE englobant le texte pour que le service d'image puisse la surligner avec précision.
    /// </summary>
    private RectangleF FindExactBoundingBoxForText(string text, IReadOnlyList<PdfWordInfo> words, out int pageNumber)
    {
        pageNumber = -1;
        if (string.IsNullOrWhiteSpace(text) || words == null || words.Count == 0) return RectangleF.Empty;

        var searchWords = text.Split(new[] { ' ', '\n', '\r', '.', ',', ';', ':' }, StringSplitOptions.RemoveEmptyEntries);
        if (searchWords.Length == 0) return RectangleF.Empty;

        int matchTarget = Math.Min(searchWords.Length, 3);

        for (int i = 0; i <= words.Count - matchTarget; i++)
        {
            bool match = true;
            for (int j = 0; j < matchTarget; j++)
            {
                string cleanWord = new string(words[i + j].Text.Where(char.IsLetterOrDigit).ToArray());
                string cleanSearch = new string(searchWords[j].Where(char.IsLetterOrDigit).ToArray());

                if (!string.Equals(cleanWord, cleanSearch, StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                pageNumber = words[i].PageNumber;
                double minX = double.MaxValue;
                double minY = double.MaxValue;
                double maxX = double.MinValue;
                double maxY = double.MinValue;

                int j = 0;
                while (i + j < words.Count && words[i+j].PageNumber == pageNumber && j < searchWords.Length * 1.5)
                {
                    foreach (var letter in words[i + j].Letters)
                    {
                        // On récupère bien les dimensions exactes en X et Y pour le surlignage
                        minX = Math.Min(minX, letter.GlyphRectangle.BottomLeft.X);
                        minY = Math.Min(minY, letter.GlyphRectangle.BottomLeft.Y);
                        maxX = Math.Max(maxX, letter.GlyphRectangle.TopRight.X);
                        maxY = Math.Max(maxY, letter.GlyphRectangle.TopRight.Y);
                    }
                    j++;
                }

                if (minX != double.MaxValue)
                {
                    return new RectangleF((float)minX, (float)minY, (float)(maxX - minX), (float)(maxY - minY));
                }
            }
        }
        return RectangleF.Empty;
    }

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