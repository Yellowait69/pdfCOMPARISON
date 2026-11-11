using System;
using System.Collections.Generic;
using System.Text;
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
    private readonly IConfigurationService? _configService;

    public VisualHighlightMatcherService(IConfigurationService? configService = null)
    {
        _configService = configService;
    }

    private ComparisonProfile? Profile => _configService?.ActiveProfile;

    public VisualHighlights GenerateHighlights(
        SideBySideDiffModel diffLinesModel,
        List<List<(string CleanText, List<LetterLoc> Letters)>> sourceLinesList,
        List<List<(string CleanText, List<LetterLoc> Letters)>> targetLinesList)
    {
        if (diffLinesModel == null) throw new ArgumentNullException(nameof(diffLinesModel));
        if (sourceLinesList == null) throw new ArgumentNullException(nameof(sourceLinesList));
        if (targetLinesList == null) throw new ArgumentNullException(nameof(targetLinesList));

        int maxLineShiftFullLine = Math.Max(30, Profile?.MaxLineShiftForFullLine ?? 30);
        int maxLineShiftSequence = Math.Max(25, Profile?.MaxLineShiftForSequence ?? 25);
        decimal maxProximityX = Math.Max(480.0m, Profile?.LocalProximityMaxX ?? 480.0m);
        decimal minProximityY = Math.Max(60.0m, Profile?.LocalProximityMinY ?? 60.0m);

        var highlights = new VisualHighlights();

        int estimatedDiffs = Math.Max(16, diffLinesModel.NewText.Lines.Count / 4);

        var globalDeletes = new List<(string CleanText, List<LetterLoc> Letters, int LineIndex)>(estimatedDiffs);
        var globalInserts = new List<(string CleanText, List<LetterLoc> Letters, int LineIndex)>(estimatedDiffs);

        int sLineIdx = 0, tLineIdx = 0;
        int diffLinesCount = diffLinesModel.NewText.Lines.Count;

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
                        globalDeletes.Add((item.CleanText, item.Letters, i));
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

        // Passe 0a : Réconciliation de lignes entières sans espaces (absorbe 100% des écarts de découpage de mots / kerning sur une ligne)
        ReconcileSpacelessLines(globalDeletes, globalInserts, matchedOld, matchedNew, maxLineShiftFullLine);

        // Passe 0b : Dé-césure et fusion de mots coupés en 2 ou 3 fragments ("rembour-" + "sement" == "remboursement")
        ReconcileHyphenatedAndSplitWords(globalDeletes, globalInserts, matchedOld, matchedNew, maxLineShiftSequence);

        // Passe 1 : Réconciliation de lignes entières déplacées (sur la même page ou dans la fenêtre de lignes)
        int idxDel = 0;
        while (idxDel < deletesCount)
        {
            if (matchedOld[idxDel])
            {
                idxDel++;
                continue;
            }

            int currentLineIndex = globalDeletes[idxDel].LineIndex;
            int delPage = GetPageNumber(globalDeletes[idxDel].Letters);
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
                    int insPage = GetPageNumber(globalInserts[idxIns].Letters);
                    int insStart = idxIns;
                    int insLen = 0;

                    while (idxIns + insLen < insertsCount && globalInserts[idxIns + insLen].LineIndex == targetLineIndex)
                    {
                        insLen++;
                    }

                    bool samePageOrClose = (delPage == insPage) || Math.Abs(currentLineIndex - targetLineIndex) <= maxLineShiftFullLine;
                    if (delLen == insLen && samePageOrClose)
                    {
                        bool isMatch = true;
                        for (int k = 0; k < delLen; k++)
                        {
                            if (!string.Equals(globalDeletes[delStart + k].CleanText, globalInserts[insStart + k].CleanText, StringComparison.Ordinal))
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
                    idxIns += insLen;
                }
            }
            idxDel += delLen;
        }

        // Passe 2 : Réconciliation des séquences de >= 2 mots (absorbe les retours à la ligne / word-wrap reflow et l'ordre des colonnes)
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;
            int delPage = GetPageNumber(globalDeletes[i].Letters);

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                int insPage = GetPageNumber(globalInserts[j].Letters);
                bool samePageOrClose = (delPage == insPage) || Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) <= maxLineShiftSequence;
                if (!samePageOrClose) continue;

                if (string.Equals(globalDeletes[i].CleanText, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    int seqLen = 1;

                    while (i + seqLen < deletesCount && j + seqLen < insertsCount)
                    {
                        if (matchedOld[i + seqLen] || matchedNew[j + seqLen]) break;

                        int dp = GetPageNumber(globalDeletes[i + seqLen].Letters);
                        int ip = GetPageNumber(globalInserts[j + seqLen].Letters);
                        if (dp != ip && Math.Abs(globalDeletes[i + seqLen].LineIndex - globalInserts[j + seqLen].LineIndex) > maxLineShiftSequence) break;

                        if (!string.Equals(globalDeletes[i + seqLen].CleanText, globalInserts[j + seqLen].CleanText, StringComparison.Ordinal)) break;

                        seqLen++;
                    }

                    if (seqLen >= 2)
                    {
                        for (int k = 0; k < seqLen; k++)
                        {
                            matchedOld[i + k] = true;
                            matchedNew[j + k] = true;
                        }
                        i += seqLen - 1;
                        break;
                    }
                }
            }
        }

        // Passe 3a : Réconciliation des mots isolés proches géométriquement sur la même page
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                if (string.Equals(oldWord, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    if (!IsLocallyClose(globalDeletes[i].Letters, globalInserts[j].Letters, minProximityY, maxProximityX))
                        continue;

                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    break;
                }
            }
        }

        // Passe 3b : Annulation 1-pour-1 des mots strictement identiques restants sur la MÊME page (évite qu'un mot déplacé dans un tableau soit signalé comme différent de lui-même)
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;
            int delPage = GetPageNumber(globalDeletes[i].Letters);
            decimal delY = globalDeletes[i].Letters.Count > 0 ? globalDeletes[i].Letters[0].BaselineY : 0m;

            int bestJ = -1;
            decimal bestDeltaY = decimal.MaxValue;

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;
                if (GetPageNumber(globalInserts[j].Letters) != delPage) continue;

                if (string.Equals(oldWord, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    decimal insY = globalInserts[j].Letters.Count > 0 ? globalInserts[j].Letters[0].BaselineY : 0m;
                    decimal dy = Math.Abs(delY - insY);

                    if ((oldWord.Length >= 2 || dy <= 160m) && dy < bestDeltaY)
                    {
                        bestDeltaY = dy;
                        bestJ = j;
                    }
                }
            }

            if (bestJ != -1)
            {
                matchedOld[i] = true;
                matchedNew[bestJ] = true;
            }
        }

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

    private static int GetPageNumber(List<LetterLoc> letters)
    {
        return letters != null && letters.Count > 0 ? letters[0].PageNumber : 1;
    }

    /// <summary>
    /// Réconcilie les lignes qui ont exactement la même suite de caractères alphanumériques lorsqu'on ignore les espaces.
    /// </summary>
    private static void ReconcileSpacelessLines(
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts,
        bool[] matchedOld,
        bool[] matchedNew,
        int maxLineShift)
    {
        int delCount = globalDeletes.Count;
        int insCount = globalInserts.Count;

        int idxDel = 0;
        while (idxDel < delCount)
        {
            int delLine = globalDeletes[idxDel].LineIndex;
            int delPage = GetPageNumber(globalDeletes[idxDel].Letters);
            int delStart = idxDel;
            int delLen = 0;
            var sbDel = new StringBuilder();

            while (idxDel + delLen < delCount && globalDeletes[idxDel + delLen].LineIndex == delLine)
            {
                sbDel.Append(globalDeletes[idxDel + delLen].CleanText);
                delLen++;
            }

            string spacelessDel = sbDel.ToString();
            if (spacelessDel.Length > 0)
            {
                int idxIns = 0;
                while (idxIns < insCount)
                {
                    int insLine = globalInserts[idxIns].LineIndex;
                    int insPage = GetPageNumber(globalInserts[idxIns].Letters);
                    int insStart = idxIns;
                    int insLen = 0;
                    var sbIns = new StringBuilder();

                    while (idxIns + insLen < insCount && globalInserts[idxIns + insLen].LineIndex == insLine)
                    {
                        if (!matchedNew[idxIns + insLen])
                        {
                            sbIns.Append(globalInserts[idxIns + insLen].CleanText);
                        }
                        insLen++;
                    }

                    bool samePageOrClose = (delPage == insPage) || Math.Abs(delLine - insLine) <= maxLineShift;
                    if (samePageOrClose && string.Equals(spacelessDel, sbIns.ToString(), StringComparison.Ordinal))
                    {
                        for (int k = 0; k < delLen; k++) matchedOld[delStart + k] = true;
                        for (int k = 0; k < insLen; k++) matchedNew[insStart + k] = true;
                        break;
                    }

                    idxIns += insLen;
                }
            }

            idxDel += delLen;
        }
    }

    /// <summary>
    /// Réconcilie les mots coupés par une césure en fin de ligne ("rembour-" + "sement" == "remboursement")
    /// ou scindés en 2 ou 3 fragments par un écart de kerning entre Source et Target.
    /// </summary>
    private static void ReconcileHyphenatedAndSplitWords(
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts,
        bool[] matchedOld,
        bool[] matchedNew,
        int maxLineShift)
    {
        int delCount = globalDeletes.Count;
        int insCount = globalInserts.Count;

        // Cas A : 2 ou 3 fragments dans Source == 1 mot complet dans Target
        for (int i = 0; i < delCount - 1; i++)
        {
            if (matchedOld[i] || matchedOld[i + 1]) continue;

            string p1 = globalDeletes[i].CleanText;
            string p2 = globalDeletes[i + 1].CleanText;
            if (p1.Length == 0 || p2.Length == 0) continue;

            string combined2 = p1 + p2;
            string? combined3 = (i + 2 < delCount && !matchedOld[i + 2]) ? combined2 + globalDeletes[i + 2].CleanText : null;

            for (int j = 0; j < insCount; j++)
            {
                if (matchedNew[j]) continue;
                if (GetPageNumber(globalDeletes[i].Letters) != GetPageNumber(globalInserts[j].Letters) &&
                    Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > maxLineShift)
                    continue;

                if (string.Equals(combined2, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedOld[i + 1] = true;
                    matchedNew[j] = true;
                    break;
                }

                if (combined3 != null && string.Equals(combined3, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedOld[i + 1] = true;
                    matchedOld[i + 2] = true;
                    matchedNew[j] = true;
                    break;
                }
            }
        }

        // Cas B : 1 mot complet dans Source == 2 ou 3 fragments dans Target
        for (int j = 0; j < insCount - 1; j++)
        {
            if (matchedNew[j] || matchedNew[j + 1]) continue;

            string p1 = globalInserts[j].CleanText;
            string p2 = globalInserts[j + 1].CleanText;
            if (p1.Length == 0 || p2.Length == 0) continue;

            string combined2 = p1 + p2;
            string? combined3 = (j + 2 < insCount && !matchedNew[j + 2]) ? combined2 + globalInserts[j + 2].CleanText : null;

            for (int i = 0; i < delCount; i++)
            {
                if (matchedOld[i]) continue;
                if (GetPageNumber(globalDeletes[i].Letters) != GetPageNumber(globalInserts[j].Letters) &&
                    Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > maxLineShift)
                    continue;

                if (string.Equals(globalDeletes[i].CleanText, combined2, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    matchedNew[j + 1] = true;
                    break;
                }

                if (combined3 != null && string.Equals(globalDeletes[i].CleanText, combined3, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    matchedNew[j + 1] = true;
                    matchedNew[j + 2] = true;
                    break;
                }
            }
        }

        // Cas C : 2 fragments dans Source == 2 fragments dans Target coupés à un endroit différent ("rembour-"+"sement" vs "rembourse-"+"ment")
        for (int i = 0; i < delCount - 1; i++)
        {
            if (matchedOld[i] || matchedOld[i + 1]) continue;
            string combinedDel = globalDeletes[i].CleanText + globalDeletes[i + 1].CleanText;
            if (combinedDel.Length == 0) continue;

            for (int j = 0; j < insCount - 1; j++)
            {
                if (matchedNew[j] || matchedNew[j + 1]) continue;
                if (GetPageNumber(globalDeletes[i].Letters) != GetPageNumber(globalInserts[j].Letters) &&
                    Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > maxLineShift)
                    continue;

                string combinedIns = globalInserts[j].CleanText + globalInserts[j + 1].CleanText;
                if (string.Equals(combinedDel, combinedIns, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedOld[i + 1] = true;
                    matchedNew[j] = true;
                    matchedNew[j + 1] = true;
                    break;
                }
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool IsLocallyClose(List<LetterLoc> oldLocList, List<LetterLoc> newLocList, decimal minVerticalTolerance, decimal maxHorizontalTolerance)
    {
        if (oldLocList.Count == 0 || newLocList.Count == 0) return false;
        if (oldLocList[0].PageNumber != newLocList[0].PageNumber) return false;

        decimal verticalTolerance = Math.Max(minVerticalTolerance, oldLocList[0].FontSize * 4.5m);
        return Math.Abs(oldLocList[0].BaselineY - newLocList[0].BaselineY) < verticalTolerance &&
               Math.Abs((decimal)oldLocList[0].BoundingBox.BottomLeft.X - (decimal)newLocList[0].BoundingBox.BottomLeft.X) < maxHorizontalTolerance;
    }
}
