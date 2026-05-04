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

    public VisualHighlights GenerateHighlights(
        SideBySideDiffModel diffLinesModel,
        List<List<(string CleanText, List<LetterLoc> Letters)>> sourceLinesList,
        List<List<(string CleanText, List<LetterLoc> Letters)>> targetLinesList)
    {
        if (diffLinesModel == null) throw new ArgumentNullException(nameof(diffLinesModel));
        if (sourceLinesList == null) throw new ArgumentNullException(nameof(sourceLinesList));
        if (targetLinesList == null) throw new ArgumentNullException(nameof(targetLinesList));

        var highlights = new VisualHighlights();

        int estimatedDiffs = diffLinesModel.NewText.Lines.Count / 4;

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

        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                if (Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > 1)
                    continue;

                if (string.Equals(globalDeletes[i].CleanText, globalInserts[j].CleanText))
                {
                    int seqLen = 1;

                    while (i + seqLen < deletesCount && j + seqLen < insertsCount)
                    {
                        if (matchedOld[i + seqLen] || matchedNew[j + seqLen]) break;

                        if (Math.Abs(globalDeletes[i + seqLen].LineIndex - globalInserts[j + seqLen].LineIndex) > 1) break;

                        if (!string.Equals(globalDeletes[i + seqLen].CleanText, globalInserts[j + seqLen].CleanText)) break;

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

        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                if (string.Equals(oldWord, globalInserts[j].CleanText))
                {
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
        return oldLocList.Count > 0 && newLocList.Count > 0 &&
               oldLocList[0].PageNumber == newLocList[0].PageNumber &&
               Math.Abs(oldLocList[0].BaselineY - newLocList[0].BaselineY) < 15.0m &&
               Math.Abs((decimal)oldLocList[0].BoundingBox.BottomLeft.X - (decimal)newLocList[0].BoundingBox.BottomLeft.X) < 300.0m;
    }
}