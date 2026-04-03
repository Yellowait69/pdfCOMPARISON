using System;
using System.Collections.Generic;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;

namespace PDFComparison.Services;

public interface IVisualHighlightMatcherService
{
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
        int diffLinesCount = diffLinesModel.NewText.Lines.Count;

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

        int deletesCount = globalDeletes.Count;
        int insertsCount = globalInserts.Count;

        bool[] matchedOld = new bool[deletesCount];
        bool[] matchedNew = new bool[insertsCount];

        // --- ÉTAPE A : N-Gram Matcher (Blocs déplacés) ---
        // Les séquences de mots (phrases de 2 mots ou plus) ont le droit d'être déplacées librement dans le document.
        const int minSequenceLength = 2;
        for (int i = 0; i <= deletesCount - minSequenceLength; i++)
        {
            if (matchedOld[i]) continue;

            int bestJ = -1, maxLen = 0;

            for (int j = 0; j <= insertsCount - minSequenceLength; j++)
            {
                if (matchedNew[j]) continue;

                int len = 0;
                // Vérification rapide avec string.Equals pour les performances
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
                i += maxLen - 1;
            }
        }

        // --- ÉTAPE B : Matcher de Mots Isolés Exacts ---
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                if (string.Equals(oldWord, globalInserts[j].CleanText))
                {
                    // RÈGLE STRICTE : Si c'est un mot isolé, il DOIT être au même endroit (X, Y et Page).
                    // Sinon, c'est considéré comme une suppression et un ajout indépendant.
                    if (!IsLocallyClose(globalDeletes[i].Letters, globalInserts[j].Letters))
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

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                if (_semanticService.AreConceptuallySimilar(oldWord, globalInserts[j].CleanText))
                {
                    // RÈGLE STRICTE : Une modification sémantique d'un mot isolé
                    // n'est valable que si on est au même endroit sur la page.
                    if (!IsLocallyClose(globalDeletes[i].Letters, globalInserts[j].Letters))
                        continue;

                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    highlights.SourceYellow.AddRange(globalDeletes[i].Letters);
                    highlights.TargetYellow.AddRange(globalInserts[j].Letters);
                    break;
                }
            }
        }

        // --- ÉTAPE D : Reliquat absolu (Rouge et Vert) ---
        // Tous les mots isolés qui ont "voyagé" trop loin atterrissent ici et sont correctement mis en Diff absolu.
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
    // directement dans l'appelant, ce qui évite le coût d'un saut de fonction en mémoire.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool IsLocallyClose(List<LetterLoc> oldLocList, List<LetterLoc> newLocList)
    {
        // VÉRIFICATION COMPLÈTE : Même page, Axe Y < 100 pts de différence, Axe X < 50 pts de différence.
        return oldLocList.Count > 0 && newLocList.Count > 0 &&
               oldLocList[0].PageNumber == newLocList[0].PageNumber &&
               Math.Abs(oldLocList[0].BaselineY - newLocList[0].BaselineY) < 100.0m &&
               Math.Abs((decimal)oldLocList[0].BoundingBox.BottomLeft.X - (decimal)newLocList[0].BoundingBox.BottomLeft.X) < 50.0m;
    }
}