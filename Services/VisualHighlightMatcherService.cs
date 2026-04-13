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
    // Le constructeur ne prend plus de ISemanticSimilarityService
    public VisualHighlightMatcherService()
    {
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

        // On stocke l'index de la ligne (la phrase) pour chaque mot
        var globalDeletes = new List<(string CleanText, List<LetterLoc> Letters, int LineIndex)>(estimatedDiffs);
        var globalInserts = new List<(string CleanText, List<LetterLoc> Letters, int LineIndex)>(estimatedDiffs);

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
                if (hasS && sLine != null)
                {
                    foreach (var item in sLine)
                        globalDeletes.Add((item.CleanText, item.Letters, i)); // 'i' représente la ligne source
                }
            }

            if (newLineDiff.Type is ChangeType.Inserted or ChangeType.Modified)
            {
                if (hasT && tLine != null)
                {
                    foreach (var item in tLine)
                        globalInserts.Add((item.CleanText, item.Letters, i)); // 'i' représente la ligne cible
                }
            }
        }

        int deletesCount = globalDeletes.Count;
        int insertsCount = globalInserts.Count;

        bool[] matchedOld = new bool[deletesCount];
        bool[] matchedNew = new bool[insertsCount];

        // --- ÉTAPE A : Matcher de Lignes Entières (Déplacements complets) ---
        int idxDel = 0;
        while (idxDel < deletesCount)
        {
            if (matchedOld[idxDel])
            {
                idxDel++;
                continue;
            }

            int currentLineIndex = globalDeletes[idxDel].LineIndex;
            int delStart = idxDel;
            int delLen = 0;

            while (idxDel + delLen < deletesCount && globalDeletes[idxDel + delLen].LineIndex == currentLineIndex)
            {
                delLen++;
            }

            if (delLen >= 2)
            {
                int idxIns = 0;
                while (idxIns < insertsCount)
                {
                    if (matchedNew[idxIns])
                    {
                        idxIns++;
                        continue;
                    }

                    int targetLineIndex = globalInserts[idxIns].LineIndex;
                    int insStart = idxIns;
                    int insLen = 0;

                    while (idxIns + insLen < insertsCount && globalInserts[idxIns + insLen].LineIndex == targetLineIndex)
                    {
                        insLen++;
                    }

                    if (delLen == insLen)
                    {
                        // Tolérance d'une ligne d'écart maximum
                        if (Math.Abs(currentLineIndex - targetLineIndex) <= 1)
                        {
                            bool isMatch = true;
                            for (int k = 0; k < delLen; k++)
                            {
                                if (!string.Equals(globalDeletes[delStart + k].CleanText, globalInserts[insStart + k].CleanText))
                                {
                                    isMatch = false;
                                    break;
                                }
                            }

                            if (isMatch)
                            {
                                for (int k = 0; k < delLen; k++)
                                {
                                    matchedOld[delStart + k] = true;
                                    matchedNew[insStart + k] = true;
                                }
                                break;
                            }
                        }
                    }
                    idxIns += insLen;
                }
            }
            idxDel += delLen;
        }

        // --- ÉTAPE A2 : Matcher de Sous-Séquences (Phrases décalées par un ajout en début de ligne) ---
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                // Règle de tolérance : la suite de mots peut être sur la même ligne ou la ligne immédiatement suivante
                if (Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > 1)
                    continue;

                if (string.Equals(globalDeletes[i].CleanText, globalInserts[j].CleanText))
                {
                    int seqLen = 1;

                    // On teste jusqu'où la phrase est identique
                    while (i + seqLen < deletesCount && j + seqLen < insertsCount)
                    {
                        if (matchedOld[i + seqLen] || matchedNew[j + seqLen]) break;

                        // Si le bout de phrase franchit un trop grand écart de ligne, on s'arrête
                        if (Math.Abs(globalDeletes[i + seqLen].LineIndex - globalInserts[j + seqLen].LineIndex) > 1) break;

                        if (!string.Equals(globalDeletes[i + seqLen].CleanText, globalInserts[j + seqLen].CleanText)) break;

                        seqLen++;
                    }

                    // On a trouvé une suite d'au moins 2 mots identiques (ce n'est donc pas un mot isolé)
                    if (seqLen >= 2)
                    {
                        for (int k = 0; k < seqLen; k++)
                        {
                            matchedOld[i + k] = true;
                            matchedNew[j + k] = true;
                        }
                        // On avance l'index principal i puisqu'on a validé un groupe de mots
                        i += seqLen - 1;
                        break;
                    }
                }
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
                    // RÈGLE STRICTE : Un mot isolé DOIT obligatoirement appartenir à la même ligne
                    if (globalDeletes[i].LineIndex != globalInserts[j].LineIndex)
                        continue;

                    if (!IsLocallyClose(globalDeletes[i].Letters, globalInserts[j].Letters))
                        continue;

                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    break;
                }
            }
        }

        // --- ÉTAPE FINALE : Reliquat absolu (Tout ce qui reste est classé en Ajout ou Suppression pure) ---
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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool IsLocallyClose(List<LetterLoc> oldLocList, List<LetterLoc> newLocList)
    {
        // VÉRIFICATION COMPLÈTE : Même page, Axe Y < 15 pts (même ligne physique garantie).
        // L'Axe X est à 300 pts pour tolérer qu'un mot isolé soit poussé vers la droite sur la MÊME ligne.
        return oldLocList.Count > 0 && newLocList.Count > 0 &&
               oldLocList[0].PageNumber == newLocList[0].PageNumber &&
               Math.Abs(oldLocList[0].BaselineY - newLocList[0].BaselineY) < 15.0m &&
               Math.Abs((decimal)oldLocList[0].BoundingBox.BottomLeft.X - (decimal)newLocList[0].BoundingBox.BottomLeft.X) < 300.0m;
    }
}