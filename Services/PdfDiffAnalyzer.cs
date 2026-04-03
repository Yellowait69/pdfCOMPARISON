using System;
using System.Collections.Generic;
using System.IO;
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

    // Injection de dépendances sécurisée
    public PdfDiffAnalyzer(
        IPdfLayoutSanitizerService layoutSanitizer,
        ITextDiffSummaryService textSummaryService,
        IVisualHighlightMatcherService visualMatcherService)
    {
        _layoutSanitizer = layoutSanitizer ?? throw new ArgumentNullException(nameof(layoutSanitizer));
        _textSummaryService = textSummaryService ?? throw new ArgumentNullException(nameof(textSummaryService));
        _visualMatcherService = visualMatcherService ?? throw new ArgumentNullException(nameof(visualMatcherService));
    }

    public DiffAnalysisResult AnalyzeDifferences(DocumentPair pair, string cleanSource, string cleanTarget, IReadOnlyList<PdfWordInfo> sourceWords, IReadOnlyList<PdfWordInfo> targetWords)
    {
        if (pair == null) throw new ArgumentNullException(nameof(pair));

        // Extraction sécurisée de la langue
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

        // 1. Formatage du texte pour DiffPlex
        string formattedSource = _layoutSanitizer.CleanLineForDiff(cleanSource);
        string formattedTarget = _layoutSanitizer.CleanLineForDiff(cleanTarget);

        // 2. Génération du résumé textuel (Dashboard)
        var summaryData = _textSummaryService.BuildTextSummary(formattedSource, formattedTarget);
        result.DifferencesCount = summaryData.DifferencesCount;
        result.Summary.Blocks.AddRange(summaryData.Blocks);

        // 3. Préparation spatiale du texte (Transformer les mots en Lignes PDF)
        var sourceLinesList = _layoutSanitizer.GroupIntoLines(sourceWords);
        var targetLinesList = _layoutSanitizer.GroupIntoLines(targetWords);

        // 4. Synchronisation : Création du texte exact pour le diff visuel
        // OPTIMISATION : Utilisation de StringBuilder au lieu de requêtes LINQ lourdes
        string sourceDiffText = BuildDiffText(sourceLinesList);
        string targetDiffText = BuildDiffText(targetLinesList);

        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        var diffLinesModelForVisuals = diffBuilder.BuildDiffModel(sourceDiffText, targetDiffText);

        // 5. Moteur de surbrillance visuelle
        result.Highlights = _visualMatcherService.GenerateHighlights(diffLinesModelForVisuals, sourceLinesList, targetLinesList);

        return result;
    }

    // OPTIMISATION : Reconstruction du texte sans inonder la mémoire de chaînes temporaires.
    private string BuildDiffText(List<List<(string CleanText, List<LetterLoc> Letters)>> linesList)
    {
        if (linesList == null || linesList.Count == 0) return string.Empty;

        // Estimer la taille pour éviter que le StringBuilder ne se redimensionne en cours de route
        // (ex: ~50 caractères par ligne en moyenne)
        int estimatedCapacity = linesList.Count * 50;
        var sb = new StringBuilder(estimatedCapacity);

        for (int i = 0; i < linesList.Count; i++)
        {
            var line = linesList[i];
            for (int j = 0; j < line.Count; j++)
            {
                sb.Append(line[j].CleanText);

                // Ajouter un espace entre chaque mot de la ligne
                if (j < line.Count - 1)
                {
                    sb.Append(' ');
                }
            }

            // Ajouter un saut de ligne entre chaque ligne, sauf la dernière
            if (i < linesList.Count - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }
}