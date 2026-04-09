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
                        globalDeletes.Add((item.CleanText, item.Letters, i)); // 'i' représente la phrase
                }
            }

            if (newLineDiff.Type is ChangeType.Inserted or ChangeType.Modified)
            {
                if (hasT && tLine != null)
                {
                    foreach (var item in tLine)
                        globalInserts.Add((item.CleanText, item.Letters, i));
                }
            }
        }

        int deletesCount = globalDeletes.Count;
        int insertsCount = globalInserts.Count;

        bool[] matchedOld = new bool[deletesCount];
        bool[] matchedNew = new bool[insertsCount];

        // --- ÉTAPE A : Matcher de Phrases Entières (Déplacements de blocs) ---
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

            // Mesurer la longueur exacte de la phrase supprimée
            while (idxDel + delLen < deletesCount && globalDeletes[idxDel + delLen].LineIndex == currentLineIndex)
            {
                delLen++;
            }

            // On ne cherche à déplacer que des phrases d'au moins 2 mots
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

                    // Mesurer la longueur de la phrase insérée
                    while (idxIns + insLen < insertsCount && globalInserts[idxIns + insLen].LineIndex == targetLineIndex)
                    {
                        insLen++;
                    }

                    // Si les deux phrases ont exactement le même nombre de mots, on les compare
                    if (delLen == insLen)
                    {
                        bool isMatch = true;
                        for (int k = 0; k < delLen; k++)
                        {
                            // MISE À JOUR : Utilisation du moteur sémantique pour tolérer les erreurs d'OCR ou petits slashs
                            if (!_semanticService.AreConceptuallySimilar(globalDeletes[delStart + k].CleanText, globalInserts[insStart + k].CleanText))
                            {
                                isMatch = false;
                                break;
                            }
                        }

                        // Match ! La phrase entière a été déplacée.
                        if (isMatch)
                        {
                            for (int k = 0; k < delLen; k++)
                            {
                                matchedOld[delStart + k] = true;
                                matchedNew[insStart + k] = true;
                            }
                            break; // Phrase trouvée, on arrête de chercher dans les insertions
                        }
                    }

                    // Sauter à la prochaine phrase insérée
                    idxIns += insLen;
                }
            }

            // Sauter à la prochaine phrase supprimée
            idxDel += delLen;
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
                    // MISE À JOUR : Tolérance de décalage (Reflow). On autorise jusqu'à 5 lignes d'écart
                    if (Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > 5)
                        continue;

                    // Maintien de la sécurité spatiale pour les anomalies de PDF superposés
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
                    // MISE À JOUR : Tolérance de décalage (Reflow). On autorise jusqu'à 2 lignes d'écart
                    if (Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > 2)
                        continue;

                    // Vérification spatiale
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
        // Tous les mots isolés qui ont "voyagé" trop loin atterrissent ici
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
        // VÉRIFICATION COMPLÈTE : Même page, Axe Y < 100 pts de différence, Axe X < 50 pts de différence.
        return oldLocList.Count > 0 && newLocList.Count > 0 &&
               oldLocList[0].PageNumber == newLocList[0].PageNumber &&
               Math.Abs(oldLocList[0].BaselineY - newLocList[0].BaselineY) < 100.0m &&
               Math.Abs((decimal)oldLocList[0].BoundingBox.BottomLeft.X - (decimal)newLocList[0].BoundingBox.BottomLeft.X) < 50.0m;
    }
}