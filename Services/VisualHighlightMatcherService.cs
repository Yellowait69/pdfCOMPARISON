using System;
using System.Collections.Generic;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;

namespace PDFComparison.Services;

public interface IVisualHighlightMatcherService
{
    // CORRECTION : Remplacement de DiffPaneModel par SideBySideDiffModel
    VisualHighlights GenerateHighlights(
        SideBySideDiffModel diffLinesModel,
        List<List<(string CleanText, List<LetterLoc> Letters)>> sourceLinesList,
        List<List<(string CleanText, List<LetterLoc> Letters)>> targetLinesList);
}

public class VisualHighlightMatcherService : IVisualHighlightMatcherService
{
    private readonly ISemanticSimilarityService _semanticService;

    public VisualHighlightMatcherService(ISemanticSimilarityService semanticService)
    {
        _semanticService = semanticService ?? throw new ArgumentNullException(nameof(semanticService));
    }

    // CORRECTION : Remplacement de DiffPaneModel par SideBySideDiffModel
    public VisualHighlights GenerateHighlights(
        SideBySideDiffModel diffLinesModel,
        List<List<(string CleanText, List<LetterLoc> Letters)>> sourceLinesList,
        List<List<(string CleanText, List<LetterLoc> Letters)>> targetLinesList)
    {
        // Sécurité : Validation des entrées
        if (diffLinesModel == null) throw new ArgumentNullException(nameof(diffLinesModel));
        if (sourceLinesList == null) throw new ArgumentNullException(nameof(sourceLinesList));
        if (targetLinesList == null) throw new ArgumentNullException(nameof(targetLinesList));

        var highlights = new VisualHighlights();

        // Initialisation avec une estimation de capacité pour éviter les redimensionnements coûteux
        int estimatedDiffs = diffLinesModel.NewText.Lines.Count / 4;
        var globalDeletes = new List<(string CleanText, List<LetterLoc> Letters)>(estimatedDiffs);
        var globalInserts = new List<(string CleanText, List<LetterLoc> Letters)>(estimatedDiffs);

        int sLineIdx = 0, tLineIdx = 0;
        int diffLinesCount = diffLinesModel.NewText.Lines.Count; // Mise en cache

        // 1. Collecte des mots modifiés
        for (int i = 0; i < diffLinesCount; i++)
        {
            var oldLineDiff = diffLinesModel.OldText.Lines[i];
            var newLineDiff = diffLinesModel.NewText.Lines[i];

            bool hasS = oldLineDiff.Type != ChangeType.Imaginary && sLineIdx < sourceLinesList.Count;
            bool hasT = newLineDiff.Type != ChangeType.Imaginary && tLineIdx < targetLinesList.Count;

            var sLine = hasS ? sourceLinesList[sLineIdx++] : null;
            var tLine = hasT ? targetLinesList[tLineIdx++] : null;

            if (oldLineDiff.Type is ChangeType.Deleted or ChangeType.Modified)
            {
                if (hasS && sLine != null) globalDeletes.AddRange(sLine);
            }

            if (newLineDiff.Type is ChangeType.Inserted or ChangeType.Modified)
            {
                if (hasT && tLine != null) globalInserts.AddRange(tLine);
            }
        }

        int deletesCount = globalDeletes.Count; // Mise en cache
        int insertsCount = globalInserts.Count; // Mise en cache

        bool[] matchedOld = new bool[deletesCount];
        bool[] matchedNew = new bool[insertsCount];

        // --- ÉTAPE A : N-Gram Matcher (Blocs déplacés) ---
        const int minSequenceLength = 2;
        for (int i = 0; i <= deletesCount - minSequenceLength; i++)
        {
            if (matchedOld[i]) continue;

            int bestJ = -1, maxLen = 0;

            for (int j = 0; j <= insertsCount - minSequenceLength; j++)
            {
                if (matchedNew[j]) continue;

                int len = 0;
                // Vérification rapide avec string.Equals pour les performances (Ordinal par défaut)
                while (i + len < deletesCount && j + len < insertsCount &&
                       !matchedOld[i + len] && !matchedNew[j + len] &&
                       string.Equals(globalDeletes[i + len].CleanText, globalInserts[j + len].CleanText))
                {
                    len++;
                }

                if (len > maxLen)
                {
                    bool isMeaningfulSequence = false;
                    for (int k = 0; k < len; k++)
                    {
                        string word = globalDeletes[i + k].CleanText;
                        if (word.Length > 2 && !_semanticService.IsNumber(word))
                        {
                            isMeaningfulSequence = true;
                            break;
                        }
                    }
                    if (isMeaningfulSequence)
                    {
                        maxLen = len;
                        bestJ = j;
                    }
                }
            }

            if (maxLen >= minSequenceLength)
            {
                for (int k = 0; k < maxLen; k++)
                {
                    matchedOld[i + k] = true;
                    matchedNew[bestJ + k] = true;
                }
                i += maxLen - 1; // Sauter les éléments qu'on vient de valider
            }
        }

        // --- ÉTAPE B : Matcher de Mots Isolés Exacts ---
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;
            bool isVolatile = _semanticService.IsVolatile(oldWord);

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                if (string.Equals(oldWord, globalInserts[j].CleanText))
                {
                    if (isVolatile && !IsLocallyClose(globalDeletes[i].Letters, globalInserts[j].Letters))
                        continue;

                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    break;
                }
            }
        }

        // --- ÉTAPE C : Matcher Sémantique ---
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;
            bool isVolatile = _semanticService.IsVolatile(oldWord);

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                if (_semanticService.AreConceptuallySimilar(oldWord, globalInserts[j].CleanText))
                {
                    if (isVolatile && !IsLocallyClose(globalDeletes[i].Letters, globalInserts[j].Letters))
                        continue;

                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    highlights.SourceYellow.AddRange(globalDeletes[i].Letters);
                    highlights.TargetYellow.AddRange(globalInserts[j].Letters);
                    break;
                }
            }
        }

        // --- ÉTAPE D : Reliquat absolu (Rouge) ---
        for (int i = 0; i < deletesCount; i++)
        {
            if (!matchedOld[i])
                highlights.SourceRed.AddRange(globalDeletes[i].Letters);
        }

        for (int j = 0; j < insertsCount; j++)
        {
            if (!matchedNew[j])
                highlights.TargetRed.AddRange(globalInserts[j].Letters);
        }

        return highlights;
    }

    // L'attribut MethodImplOptions.AggressiveInlining force le compilateur à intégrer cette méthode
    // directement dans l'appelant (Étapes B et C), ce qui évite le coût d'un saut de fonction en mémoire.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool IsLocallyClose(List<LetterLoc> oldLocList, List<LetterLoc> newLocList)
    {
        // CORRECTION ICI : Ajout du test sur l'axe X pour éviter les faux-positifs du "gruyère"
        return oldLocList.Count > 0 && newLocList.Count > 0 &&
               oldLocList[0].PageNumber == newLocList[0].PageNumber &&
               Math.Abs(oldLocList[0].BaselineY - newLocList[0].BaselineY) < 100.0m &&
               Math.Abs((decimal)oldLocList[0].BoundingBox.BottomLeft.X - (decimal)newLocList[0].BoundingBox.BottomLeft.X) < 50.0m;
    }
}