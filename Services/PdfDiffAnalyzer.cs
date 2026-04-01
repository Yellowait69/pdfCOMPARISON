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

        // ====================================================================
        // 1. Analyse Ligne par Ligne (Pour le résumé textuel du Dashboard)
        // ====================================================================
        var diffLines = diffBuilder.BuildDiffModel(CleanLineForDiff(cleanSource), CleanLineForDiff(cleanTarget));

        var sumDel = new Dictionary<string, int>();
        var sumIns = new Dictionary<string, int>();

        for (int i = 0; i < diffLines.NewText.Lines.Count; i++) {
            if (diffLines.OldText.Lines[i].Type == ChangeType.Deleted) {
                string t = diffLines.OldText.Lines[i].Text.Trim();
                if (t.Length > 0) sumDel[t] = sumDel.GetValueOrDefault(t) + 1;
            }
            if (diffLines.NewText.Lines[i].Type == ChangeType.Inserted) {
                string t = diffLines.NewText.Lines[i].Text.Trim();
                if (t.Length > 0) sumIns[t] = sumIns.GetValueOrDefault(t) + 1;
            }
        }

        var skipDel = new Dictionary<string, int>();
        var skipIns = new Dictionary<string, int>();
        foreach(var kvp in sumDel) {
            if (sumIns.TryGetValue(kvp.Key, out int insC)) {
                int moves = Math.Min(kvp.Value, insC);
                skipDel[kvp.Key] = moves;
                skipIns[kvp.Key] = moves;
            }
        }

        for (int i = 0; i < diffLines.NewText.Lines.Count; i++)
        {
            var newLine = diffLines.NewText.Lines[i];
            var oldLine = diffLines.OldText.Lines[i];

            if (oldLine.Type == ChangeType.Deleted)
            {
                string txt = oldLine.Text.Trim();
                if (skipDel.TryGetValue(txt, out int moves) && moves > 0)
                {
                    skipDel[txt] = moves - 1;
                    continue;
                }
            }
            else if (newLine.Type == ChangeType.Inserted)
            {
                string txt = newLine.Text.Trim();
                if (skipIns.TryGetValue(txt, out int moves) && moves > 0)
                {
                    skipIns[txt] = moves - 1;
                    continue;
                }
            }

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
        // 2. CORRECTION DÉFINITIVE: Algorithme Hybride + Panier Global LCS
        // Blindage contre les sauts de pages et décalages d'en-têtes
        // ====================================================================
        var sourceLinesList = GroupIntoLines(sourceWords);
        var targetLinesList = GroupIntoLines(targetWords);

        string sourceDiffText = string.Join('\n', sourceLinesList.Select(l => string.Join(' ', l.Select(w => w.CleanText))));
        string targetDiffText = string.Join('\n', targetLinesList.Select(l => string.Join(' ', l.Select(w => w.CleanText))));

        var diffLinesModel = diffBuilder.BuildDiffModel(sourceDiffText, targetDiffText);

        var currentDeletes = new List<List<(string CleanText, List<LetterLoc> Letters)>>();
        var currentInserts = new List<List<(string CleanText, List<LetterLoc> Letters)>>();

        // Paniers pour collecter tous les mots potentiellement modifiés
        var globalDeletes = new List<(string CleanText, List<LetterLoc> Letters)>();
        var globalInserts = new List<(string CleanText, List<LetterLoc> Letters)>();

        int sLineIdx = 0;
        int tLineIdx = 0;

        void FlushBlocks()
        {
            if (currentDeletes.Count > 0 && currentInserts.Count == 0)
            {
                foreach (var l in currentDeletes) globalDeletes.AddRange(l);
            }
            else if (currentInserts.Count > 0 && currentDeletes.Count == 0)
            {
                foreach (var l in currentInserts) globalInserts.AddRange(l);
            }
            else if (currentDeletes.Count > 0 && currentInserts.Count > 0)
            {
                var sWords = currentDeletes.SelectMany(l => l).ToList();
                var tWords = currentInserts.SelectMany(l => l).ToList();

                string sText = string.Join('\n', sWords.Select(w => w.CleanText));
                string tText = string.Join('\n', tWords.Select(w => w.CleanText));

                var wordDiff = diffBuilder.BuildDiffModel(sText, tText);

                int swIdx = 0, twIdx = 0;

                for (int j = 0; j < wordDiff.NewText.Lines.Count; j++)
                {
                    var oldWord = wordDiff.OldText.Lines[j];
                    var newWord = wordDiff.NewText.Lines[j];

                    bool hasSW = oldWord.Type != ChangeType.Imaginary && swIdx < sWords.Count;
                    bool hasTW = newWord.Type != ChangeType.Imaginary && twIdx < tWords.Count;

                    var swVal = hasSW ? sWords[swIdx++] : default;
                    var twVal = hasTW ? tWords[twIdx++] : default;

                    if (oldWord.Type == ChangeType.Deleted && hasSW)
                    {
                        globalDeletes.Add(swVal);
                    }
                    else if (newWord.Type == ChangeType.Inserted && hasTW)
                    {
                        globalInserts.Add(twVal);
                    }
                    else if (oldWord.Type == ChangeType.Modified || newWord.Type == ChangeType.Modified)
                    {
                        if (hasSW) result.Highlights.SourceYellow.AddRange(swVal.Letters);
                        if (hasTW) result.Highlights.TargetYellow.AddRange(twVal.Letters);
                    }
                }
            }

            currentDeletes.Clear();
            currentInserts.Clear();
        }

        for (int i = 0; i < diffLinesModel.NewText.Lines.Count; i++)
        {
            var oldLineDiff = diffLinesModel.OldText.Lines[i];
            var newLineDiff = diffLinesModel.NewText.Lines[i];

            bool hasS = oldLineDiff.Type != ChangeType.Imaginary && sLineIdx < sourceLinesList.Count;
            bool hasT = newLineDiff.Type != ChangeType.Imaginary && tLineIdx < targetLinesList.Count;

            var sLine = hasS ? sourceLinesList[sLineIdx++] : null;
            var tLine = hasT ? targetLinesList[tLineIdx++] : null;

            if (hasS && !hasT)
            {
                currentDeletes.Add(sLine!);
            }
            else if (!hasS && hasT)
            {
                currentInserts.Add(tLine!);
            }
            else if (hasS && hasT)
            {
                if (oldLineDiff.Type == ChangeType.Unchanged && newLineDiff.Type == ChangeType.Unchanged)
                {
                    FlushBlocks();
                }
                else
                {
                    currentDeletes.Add(sLine!);
                    currentInserts.Add(tLine!);
                }
            }
        }
        FlushBlocks();

        // --- 3. PASSE FINALE : DÉTECTION GLOBALE DES DÉPLACEMENTS ---
        if (globalDeletes.Count > 0 && globalInserts.Count > 0)
        {
            var moveDiff = diffBuilder.BuildDiffModel(
                string.Join('\n', globalDeletes.Select(w => w.CleanText)),
                string.Join('\n', globalInserts.Select(w => w.CleanText))
            );

            int gdIdx = 0, giIdx = 0;
            var currentUnchangedDeletes = new List<(string CleanText, List<LetterLoc> Letters)>();
            var currentUnchangedInserts = new List<(string CleanText, List<LetterLoc> Letters)>();

            void FlushUnchanged()
            {
                // SEUIL DE BLINDAGE : 3 mots consécutifs identiques = Texte déplacé (on ignore)
                if (currentUnchangedDeletes.Count >= 3)
                {
                    // On ne fait rien : le texte est considéré comme inchangé mais déplacé
                }
                else
                {
                    // Trop court : ce sont de vraies différences ou des coïncidences (ex: "le", "de")
                    foreach (var w in currentUnchangedDeletes) result.Highlights.SourceRed.AddRange(w.Letters);
                    foreach (var w in currentUnchangedInserts) result.Highlights.TargetRed.AddRange(w.Letters);
                }
                currentUnchangedDeletes.Clear();
                currentUnchangedInserts.Clear();
            }

            for (int i = 0; i < moveDiff.NewText.Lines.Count; i++)
            {
                var oldMove = moveDiff.OldText.Lines[i];
                var newMove = moveDiff.NewText.Lines[i];

                bool hasOld = oldMove.Type != ChangeType.Imaginary && gdIdx < globalDeletes.Count;
                bool hasNew = newMove.Type != ChangeType.Imaginary && giIdx < globalInserts.Count;

                var oldVal = hasOld ? globalDeletes[gdIdx++] : default;
                var newVal = hasNew ? globalInserts[giIdx++] : default;

                if (oldMove.Type == ChangeType.Unchanged && newMove.Type == ChangeType.Unchanged)
                {
                    if (hasOld) currentUnchangedDeletes.Add(oldVal);
                    if (hasNew) currentUnchangedInserts.Add(newVal);
                }
                else
                {
                    FlushUnchanged();

                    if (oldMove.Type == ChangeType.Deleted && hasOld)
                        result.Highlights.SourceRed.AddRange(oldVal.Letters);
                    else if (newMove.Type == ChangeType.Inserted && hasNew)
                        result.Highlights.TargetRed.AddRange(newVal.Letters);
                    else if (oldMove.Type == ChangeType.Modified || newMove.Type == ChangeType.Modified)
                    {
                        if (hasOld) result.Highlights.SourceRed.AddRange(oldVal.Letters);
                        if (hasNew) result.Highlights.TargetRed.AddRange(newVal.Letters);
                    }
                }
            }
            FlushUnchanged();
        }
        else
        {
            foreach (var w in globalDeletes) result.Highlights.SourceRed.AddRange(w.Letters);
            foreach (var w in globalInserts) result.Highlights.TargetRed.AddRange(w.Letters);
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

    private List<List<(string CleanText, List<LetterLoc> Letters)>> GroupIntoLines(IReadOnlyList<PdfWordInfo> words)
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

        var linesList = new List<List<(string CleanText, List<LetterLoc> Letters)>>();
        var wordsByPage = list.GroupBy(w => w.Letters.First().PageNumber).OrderBy(g => g.Key);

        foreach (var page in wordsByPage)
        {
            var pageWords = page.OrderByDescending(w => w.Letters.First().BaselineY).ToList();
            var lines = new List<List<(string CleanText, List<LetterLoc> Letters)>>();

            foreach (var word in pageWords)
            {
                decimal wordY = word.Letters.First().BaselineY;
                var currentLine = lines.FirstOrDefault(l => Math.Abs(l.First().Letters.First().BaselineY - wordY) < 5.0m);

                if (currentLine == null)
                {
                    currentLine = new List<(string CleanText, List<LetterLoc> Letters)>();
                    lines.Add(currentLine);
                }
                currentLine.Add(word);
            }

            foreach (var line in lines.OrderByDescending(l => l.First().Letters.First().BaselineY))
            {
                linesList.Add(line.OrderBy(w => w.Letters.First().BoundingBox.BottomLeft.X).ToList());
            }
        }

        return linesList;
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
            .Replace(",", ".")
            .Replace("–", "-").Replace("—", "-").Replace("−", "-")
            .Replace("’", "'").Replace("‘", "'").Replace("´", "'").Replace("`", "'")
            .Replace("“", "\"").Replace("”", "\"").Replace("«", "\"").Replace("»", "\"")
            .Normalize(System.Text.NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Trim();

        return new string(cleaned.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
    }
}