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

        int maxLineShiftFullLine = Profile?.MaxLineShiftForFullLine ?? 12;
        int maxLineShiftSequence = Profile?.MaxLineShiftForSequence ?? 10;
        int maxLineShiftSingleWord = Profile?.MaxLineShiftForSingleWord ?? 3;
        decimal maxProximityX = Profile?.LocalProximityMaxX ?? 480.0m;
        decimal minProximityY = Profile?.LocalProximityMinY ?? 36.0m;

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

        // Passe 0 : Dé-césure automatique (ex: "rembour-" + "sement" en fin de ligne vs "remboursement")
        ReconcileHyphenatedAndSplitWords(globalDeletes, globalInserts, matchedOld, matchedNew, maxLineShiftSequence);

        // Passe 1 : Réconciliation de lignes entières déplacées
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

                    if (delLen == insLen && Math.Abs(currentLineIndex - targetLineIndex) <= maxLineShiftFullLine)
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

        // Passe 2 : Réconciliation des séquences de >= 2 mots (absorbe les retours à la ligne / word-wrap reflow)
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                if (Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > maxLineShiftSequence)
                    continue;

                if (string.Equals(globalDeletes[i].CleanText, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    int seqLen = 1;

                    while (i + seqLen < deletesCount && j + seqLen < insertsCount)
                    {
                        if (matchedOld[i + seqLen] || matchedNew[j + seqLen]) break;

                        if (Math.Abs(globalDeletes[i + seqLen].LineIndex - globalInserts[j + seqLen].LineIndex) > maxLineShiftSequence) break;

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

        // Passe 3 : Réconciliation des mots isolés proches géométriquement
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                if (string.Equals(oldWord, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    if (Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > maxLineShiftSingleWord)
                        continue;

                    if (!IsLocallyClose(globalDeletes[i].Letters, globalInserts[j].Letters, minProximityY, maxProximityX))
                        continue;

                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    break;
                }
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

    /// <summary>
    /// Réconcilie les mots coupés par une césure en fin de ligne ("rembour-" + "sement" == "remboursement")
    /// ou scindés en deux par un écart de kerning entre Source et Target.
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

        // Cas A : 2 fragments dans Source == 1 mot complet dans Target
        for (int i = 0; i < delCount - 1; i++)
        {
            if (matchedOld[i] || matchedOld[i + 1]) continue;

            string part1 = globalDeletes[i].CleanText.TrimEnd('-');
            string part2 = globalDeletes[i + 1].CleanText;
            if (part1.Length == 0 || part2.Length == 0) continue;

            string combinedDel = part1 + part2;

            for (int j = 0; j < insCount; j++)
            {
                if (matchedNew[j]) continue;
                if (Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > maxLineShift) continue;

                if (string.Equals(combinedDel, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedOld[i + 1] = true;
                    matchedNew[j] = true;
                    break;
                }
            }
        }

        // Cas B : 1 mot complet dans Source == 2 fragments dans Target
        for (int j = 0; j < insCount - 1; j++)
        {
            if (matchedNew[j] || matchedNew[j + 1]) continue;

            string part1 = globalInserts[j].CleanText.TrimEnd('-');
            string part2 = globalInserts[j + 1].CleanText;
            if (part1.Length == 0 || part2.Length == 0) continue;

            string combinedIns = part1 + part2;

            for (int i = 0; i < delCount; i++)
            {
                if (matchedOld[i]) continue;
                if (Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > maxLineShift) continue;

                if (string.Equals(globalDeletes[i].CleanText, combinedIns, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
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

        decimal verticalTolerance = Math.Max(minVerticalTolerance, oldLocList[0].FontSize * 3.0m);
        return Math.Abs(oldLocList[0].BaselineY - newLocList[0].BaselineY) < verticalTolerance &&
               Math.Abs((decimal)oldLocList[0].BoundingBox.BottomLeft.X - (decimal)newLocList[0].BoundingBox.BottomLeft.X) < maxHorizontalTolerance;
    }
}
