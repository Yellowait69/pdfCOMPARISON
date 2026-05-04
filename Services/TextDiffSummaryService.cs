using System;
using System.Collections.Generic;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;

namespace PDFComparison.Services;

public interface ITextDiffSummaryService
{
    (int DifferencesCount, List<DiffSummaryBlock> Blocks, SideBySideDiffModel DiffLinesModel) BuildTextSummary(string cleanSource, string cleanTarget);
}

public class TextDiffSummaryService : ITextDiffSummaryService
{
    public (int DifferencesCount, List<DiffSummaryBlock> Blocks, SideBySideDiffModel DiffLinesModel) BuildTextSummary(string cleanSource, string cleanTarget)
    {
        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        var diffLines = diffBuilder.BuildDiffModel(cleanSource ?? string.Empty, cleanTarget ?? string.Empty);
        var blocks = new List<DiffSummaryBlock>();
        int linesCount = diffLines.NewText.Lines.Count;

        var sumDel = new Dictionary<string, int>(StringComparer.Ordinal);
        var sumIns = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < linesCount; i++)
        {
            var oldLine = diffLines.OldText.Lines[i];
            var newLine = diffLines.NewText.Lines[i];

            if (oldLine.Type is ChangeType.Deleted or ChangeType.Modified && oldLine.Text.Trim() is { Length: > 0 } oldTxt)
            {
                sumDel[oldTxt] = sumDel.GetValueOrDefault(oldTxt) + 1;
            }

            if (newLine.Type is ChangeType.Inserted or ChangeType.Modified && newLine.Text.Trim() is { Length: > 0 } newTxt)
            {
                sumIns[newTxt] = sumIns.GetValueOrDefault(newTxt) + 1;
            }
        }

        var skipDel = new Dictionary<string, int>(StringComparer.Ordinal);
        var skipIns = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var kvp in sumDel)
        {
            if (sumIns.TryGetValue(kvp.Key, out int insCount))
            {
                int moves = Math.Min(kvp.Value, insCount);
                skipDel[kvp.Key] = moves;
                skipIns[kvp.Key] = moves;
            }
        }

        List<string> currentDel = new();
        List<string> currentIns = new();
        string ctxBefore = string.Empty;
        int lastDiffIndex = -1;

        for (int i = 0; i < linesCount; i++)
        {
            var newLine = diffLines.NewText.Lines[i];
            var oldLine = diffLines.OldText.Lines[i];

            bool isDel = oldLine.Type is ChangeType.Deleted or ChangeType.Modified;
            bool isIns = newLine.Type is ChangeType.Inserted or ChangeType.Modified;

            if (isDel && oldLine.Text.Trim() is { Length: > 0 } txtDel && skipDel.GetValueOrDefault(txtDel) > 0)
            {
                skipDel[txtDel]--;
                isDel = false;
            }

            if (isIns && newLine.Text.Trim() is { Length: > 0 } txtIns && skipIns.GetValueOrDefault(txtIns) > 0)
            {
                skipIns[txtIns]--;
                isIns = false;
            }

            if (isDel || isIns)
            {
                if (currentDel.Count == 0 && currentIns.Count == 0)
                {
                    ctxBefore = GetValidContextLine(diffLines.NewText.Lines, i, -1);
                }

                if (isDel && !string.IsNullOrEmpty(oldLine.Text)) currentDel.Add(oldLine.Text);
                if (isIns && !string.IsNullOrEmpty(newLine.Text)) currentIns.Add(newLine.Text);

                lastDiffIndex = i;
            }
            else if (currentDel.Count > 0 || currentIns.Count > 0)
            {
                FlushBlocks(blocks, currentDel, currentIns, ctxBefore, GetValidContextLine(diffLines.NewText.Lines, lastDiffIndex, 1));
                currentDel.Clear();
                currentIns.Clear();
            }
        }

        if (currentDel.Count > 0 || currentIns.Count > 0)
        {
            FlushBlocks(blocks, currentDel, currentIns, ctxBefore, string.Empty);
        }

        return (blocks.Count, blocks, diffLines);
    }

    private void FlushBlocks(List<DiffSummaryBlock> blocks, List<string> dels, List<string> ins, string ctxBefore, string ctxAfter)
    {
        if (dels.Count > 0)
        {
            blocks.Add(new DiffSummaryBlock
            {
                Type = ChangeType.Deleted,
                ContextBefore = ctxBefore,
                ContextAfter = ctxAfter,
                OldText = string.Join("\n", dels),
                NewText = string.Empty
            });
        }

        if (ins.Count > 0)
        {
            blocks.Add(new DiffSummaryBlock
            {
                Type = ChangeType.Inserted,
                ContextBefore = ctxBefore,
                ContextAfter = ctxAfter,
                OldText = string.Empty,
                NewText = string.Join("\n", ins)
            });
        }
    }

    private string GetValidContextLine(List<DiffPiece> lines, int currentIndex, int direction)
    {
        int i = currentIndex + direction;
        int count = lines.Count;

        while (i >= 0 && i < count)
        {
            var line = lines[i];

            if (line.Type is not ChangeType.Imaginary && !string.IsNullOrWhiteSpace(line.Text))
            {
                return line.Text;
            }
            i += direction;
        }
        return string.Empty;
    }
}