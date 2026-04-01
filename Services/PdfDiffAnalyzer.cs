using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using UglyToad.PdfPig.Content;

namespace PDFComparison.Services;

public class PdfDiffAnalyzer
{
    public DiffAnalysisResult AnalyzeDifferences(DocumentPair pair, string cleanSource, string cleanTarget, IReadOnlyList<PdfWordInfo> sourceWords, IReadOnlyList<PdfWordInfo> targetWords)
    {
        string lang = pair.MatchKey.Contains('_') ? pair.MatchKey.Split('_')[0].ToUpper() : "ND";

        var result = new DiffAnalysisResult
        {
            Summary = new()
            {
                DocumentName = Path.GetFileName(pair.TargetPath!),
                Language = lang
            }
        };

        var diffBuilder = new SideBySideDiffBuilder(new Differ());

        // 1. Analyse Ligne par Ligne (Pour le résumé global textuel)
        var diffLines = diffBuilder.BuildDiffModel(CleanLineForDiff(cleanSource), CleanLineForDiff(cleanTarget));

        for (int i = 0; i < diffLines.NewText.Lines.Count; i++)
        {
            var newLine = diffLines.NewText.Lines[i];
            var oldLine = diffLines.OldText.Lines[i];

            if (newLine.Type is ChangeType.Inserted or ChangeType.Modified || oldLine.Type is ChangeType.Deleted)
            {
                result.DifferencesCount++;

                string oldTextToSet = string.Empty;
                string newTextToSet = string.Empty;

                if (newLine.Type is ChangeType.Modified)
                {
                    oldTextToSet = oldLine.Text;
                    newTextToSet = newLine.Text;
                }
                else if (newLine.Type is ChangeType.Inserted)
                {
                    newTextToSet = newLine.Text;
                }
                else if (oldLine.Type is ChangeType.Deleted)
                {
                    oldTextToSet = oldLine.Text;
                }

                var block = new DiffSummaryBlock
                {
                    Type = newLine.Type is not ChangeType.Unchanged ? newLine.Type : oldLine.Type,
                    ContextBefore = GetValidContextLine(diffLines.NewText.Lines, i, -1),
                    ContextAfter = GetValidContextLine(diffLines.NewText.Lines, i, 1),
                    OldText = oldTextToSet,
                    NewText = newTextToSet
                };

                result.Summary.Blocks.Add(block);
            }
        }

        // ====================================================================
        // 2. CORRECTION: Analyse MOT par MOT (Pour le surlignage visuel PDF)
        // ====================================================================
        var sourceItems = GroupAndCleanWords(sourceWords);
        var targetItems = GroupAndCleanWords(targetWords);

        // On donne à DiffPlex une chaîne où chaque MOT est sur une ligne
        var diffWords = diffBuilder.BuildDiffModel(
            string.Join('\n', sourceItems.Select(x => x.CleanText)),
            string.Join('\n', targetItems.Select(x => x.CleanText))
        );

        int sPointer = 0, tPointer = 0;

        for (int i = 0; i < diffWords.NewText.Lines.Count; i++)
        {
            var oldDiff = diffWords.OldText.Lines[i];
            var newDiff = diffWords.NewText.Lines[i];

            bool hasS = oldDiff.Type != ChangeType.Imaginary && sPointer < sourceItems.Count;
            bool hasT = newDiff.Type != ChangeType.Imaginary && tPointer < targetItems.Count;

            var sVal = hasS ? sourceItems[sPointer++] : default;
            var tVal = hasT ? targetItems[tPointer++] : default;

            if (oldDiff.Type == ChangeType.Deleted && hasS)
            {
                result.Highlights.SourceRed.AddRange(sVal.Letters);
            }
            else if (newDiff.Type == ChangeType.Inserted && hasT)
            {
                result.Highlights.TargetRed.AddRange(tVal.Letters);
            }
            else if ((oldDiff.Type == ChangeType.Modified || newDiff.Type == ChangeType.Modified))
            {
                if (hasS) result.Highlights.SourceYellow.AddRange(sVal.Letters);
                if (hasT) result.Highlights.TargetYellow.AddRange(tVal.Letters);
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

    // ==============================================================
    // NOUVEAU PIPELINE DE NETTOYAGE ET GROUPEMENT PAR MOTS
    // ==============================================================

    private List<(string CleanText, List<LetterLoc> Letters)> GroupAndCleanWords(IReadOnlyList<PdfWordInfo> words)
    {
        var list = new List<(string CleanText, List<LetterLoc> Letters)>();

        foreach (var word in words)
        {
            string cleanText = CleanWord(word.Text);
            if (string.IsNullOrEmpty(cleanText)) continue;

            var locs = new List<LetterLoc>();
            foreach (var letter in word.Letters)
            {
                string cleanedGlyph = CleanWord(letter.Value);
                if (string.IsNullOrEmpty(cleanedGlyph)) continue;

                var loc = new LetterLoc(letter.GlyphRectangle, word.PageNumber, (decimal)letter.Location.Y, (decimal)letter.PointSize);

                // Filtre anti "fake bold" (ombre portée invisible)
                if (locs.Count > 0)
                {
                    var last = locs.Last();
                    if (Math.Abs(last.BaselineY - loc.BaselineY) < 1.0m &&
                        Math.Abs((decimal)last.BoundingBox.BottomLeft.X - (decimal)loc.BoundingBox.BottomLeft.X) < 1.0m)
                    {
                        continue;
                    }
                }
                locs.Add(loc);
            }

            if (locs.Count > 0)
            {
                list.Add((cleanText, locs));
            }
        }

        // Tri visuel strict des mots de Haut en Bas, et de Gauche à Droite
        return list
            .OrderBy(x => x.Letters.First().PageNumber)
            .ThenByDescending(x => Math.Round(x.Letters.First().BaselineY / 5.0m) * 5.0m)
            .ThenBy(x => x.Letters.First().BoundingBox.BottomLeft.X)
            .ToList();
    }

    private string CleanLineForDiff(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        return input
            .Replace("\u00A0", " ")
            .Replace("\u00AD", "")
            .Replace("–", "-").Replace("—", "-").Replace("−", "-")
            .Replace("’", "'").Replace("‘", "'").Replace("´", "'").Replace("`", "'")
            .Replace("“", "\"").Replace("”", "\"").Replace("«", "\"").Replace("»", "\"")
            .Normalize(System.Text.NormalizationForm.FormKC);
    }

    private string CleanWord(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var cleaned = input
            .Replace("\u00A0", "")
            .Replace("\u200B", "")
            .Replace("\u200C", "")
            .Replace("\u200D", "")
            .Replace("\uFEFF", "")
            .Replace("\u00AD", "")
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace("\t", "")
            .Replace(" ", "")
            .Replace("–", "-").Replace("—", "-").Replace("−", "-")
            .Replace("’", "'").Replace("‘", "'").Replace("´", "'").Replace("`", "'")
            .Replace("“", "\"").Replace("”", "\"").Replace("«", "\"").Replace("»", "\"")
            .Normalize(System.Text.NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Trim();

        return new string(cleaned.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
    }
}