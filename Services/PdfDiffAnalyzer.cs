using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;

namespace PDFComparison.Services;

public class PdfDiffAnalyzer
{
    public DiffAnalysisResult AnalyzeDifferences(DocumentPair pair, string cleanSource, string cleanTarget, IReadOnlyList<PdfWordInfo> sourceWords, IReadOnlyList<PdfWordInfo> targetWords)
    {
        // Utilisation du "Target-typed new" (C# 9+) pour alléger l'écriture
        var result = new DiffAnalysisResult
        {
            Summary = new() { DocumentName = Path.GetFileName(pair.TargetPath!) }
        };

        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        var diffLines = diffBuilder.BuildDiffModel(cleanSource, cleanTarget);

        // 1. Analyse Ligne par Ligne (Pour le résumé global)
        for (int i = 0; i < diffLines.NewText.Lines.Count; i++)
        {
            var newLine = diffLines.NewText.Lines[i];
            var oldLine = diffLines.OldText.Lines[i];

            // Utilisation du Pattern Matching logique (C# 9+) : "is" et "or"
            if (newLine.Type is ChangeType.Inserted or ChangeType.Modified || oldLine.Type is ChangeType.Deleted)
            {
                result.DifferencesCount++;

                var block = new DiffSummaryBlock
                {
                    Type = newLine.Type is not ChangeType.Unchanged ? newLine.Type : oldLine.Type,
                    ContextBefore = GetValidContextLine(diffLines.NewText.Lines, i, -1),
                    ContextAfter = GetValidContextLine(diffLines.NewText.Lines, i, 1),
                };

                // Simplification des affectations
                if (newLine.Type is ChangeType.Modified)
                {
                    block.OldText = oldLine.Text;
                    block.NewText = newLine.Text;
                }
                else if (newLine.Type is ChangeType.Inserted)
                {
                    block.OldText = string.Empty;
                    block.NewText = newLine.Text;
                }
                else if (oldLine.Type is ChangeType.Deleted)
                {
                    block.OldText = oldLine.Text;
                    block.NewText = string.Empty;
                }

                result.Summary.Blocks.Add(block);
            }
        }

        // 2. Analyse Mot par Mot (Pour le surlignage visuel)
        var diffWords = diffBuilder.BuildDiffModel(
            string.Join('\n', sourceWords.Select(w => w.Text)),
            string.Join('\n', targetWords.Select(w => w.Text))
        );

        int sPointer = 0, tPointer = 0;

        for (int i = 0; i < diffWords.NewText.Lines.Count; i++)
        {
            var oldWordDiff = diffWords.OldText.Lines[i];
            var newWordDiff = diffWords.NewText.Lines[i];

            PdfWordInfo? oldWordInfo = (oldWordDiff.Type is not ChangeType.Imaginary && sPointer < sourceWords.Count) ? sourceWords[sPointer++] : null;
            PdfWordInfo? newWordInfo = (newWordDiff.Type is not ChangeType.Imaginary && tPointer < targetWords.Count) ? targetWords[tPointer++] : null;

            if (oldWordDiff.Type is ChangeType.Deleted && oldWordInfo is not null)
            {
                result.Highlights.SourceRed.AddRange(oldWordInfo.Letters.Select(l => new LetterLoc(l.GlyphRectangle, oldWordInfo.PageNumber, (decimal)l.Location.Y, (decimal)l.PointSize)));
            }
            else if (newWordDiff.Type is ChangeType.Inserted && newWordInfo is not null)
            {
                result.Highlights.TargetRed.AddRange(newWordInfo.Letters.Select(l => new LetterLoc(l.GlyphRectangle, newWordInfo.PageNumber, (decimal)l.Location.Y, (decimal)l.PointSize)));
            }
            else if ((oldWordDiff.Type is ChangeType.Modified || newWordDiff.Type is ChangeType.Modified) && oldWordInfo is not null && newWordInfo is not null)
            {
                // OPTIMISATION : Les chaînes en C# implémentent IEnumerable<char>.
                // Inutile d'appeler .ToCharArray() qui alloue un nouveau tableau en mémoire pour chaque mot modifié.
                var charDiff = diffBuilder.BuildDiffModel(
                    string.Join('\n', oldWordInfo.Text),
                    string.Join('\n', newWordInfo.Text)
                );

                int oC = 0, nC = 0;
                for (int j = 0; j < charDiff.NewText.Lines.Count; j++)
                {
                    var oChar = charDiff.OldText.Lines[j];
                    var nChar = charDiff.NewText.Lines[j];

                    if (oChar.Type is not ChangeType.Imaginary && oC < oldWordInfo.Letters.Count)
                    {
                        var let = oldWordInfo.Letters[oC];
                        if (oChar.Type is ChangeType.Deleted)
                            result.Highlights.SourceRed.Add(new LetterLoc(let.GlyphRectangle, oldWordInfo.PageNumber, (decimal)let.Location.Y, (decimal)let.PointSize));
                        else if (oChar.Type is ChangeType.Modified)
                            result.Highlights.SourceYellow.Add(new LetterLoc(let.GlyphRectangle, oldWordInfo.PageNumber, (decimal)let.Location.Y, (decimal)let.PointSize));
                        oC++;
                    }

                    if (nChar.Type is not ChangeType.Imaginary && nC < newWordInfo.Letters.Count)
                    {
                        var let = newWordInfo.Letters[nC];
                        if (nChar.Type is ChangeType.Inserted)
                            result.Highlights.TargetRed.Add(new LetterLoc(let.GlyphRectangle, newWordInfo.PageNumber, (decimal)let.Location.Y, (decimal)let.PointSize));
                        else if (nChar.Type is ChangeType.Modified)
                            result.Highlights.TargetYellow.Add(new LetterLoc(let.GlyphRectangle, newWordInfo.PageNumber, (decimal)let.Location.Y, (decimal)let.PointSize));
                        nC++;
                    }
                }
            }
        }

        return result;
    }

    private string GetValidContextLine(List<DiffPiece> lines, int currentIndex, int direction)
    {
        int i = currentIndex + direction;
        while (i >= 0 && i < lines.Count)
        {
            if (lines[i].Type is not ChangeType.Imaginary && !string.IsNullOrWhiteSpace(lines[i].Text))
            {
                return lines[i].Text;
            }
            i += direction;
        }
        return string.Empty;
    }
}