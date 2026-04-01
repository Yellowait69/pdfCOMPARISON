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
        // 1. Analyse Ligne par Ligne (Pour le résumé global textuel du Dashboard)
        // ====================================================================
        var diffLines = diffBuilder.BuildDiffModel(CleanLineForDiff(cleanSource), CleanLineForDiff(cleanTarget));

        // -- DÉTECTION GLOBALE DE DÉPLACEMENT POUR LE RÉSUMÉ TEXTUEL --
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

            // Si la ligne a simplement été déplacée (ex: changement de page), on l'ignore.
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
        // 2. CORRECTION DÉFINITIVE: Algorithme Hybride + Détection de Déplacement
        // Pour les surlignages visuels exacts sur le PDF
        // ====================================================================
        var sourceLinesList = GroupIntoLines(sourceWords);
        var targetLinesList = GroupIntoLines(targetWords);

        string sourceDiffText = string.Join('\n', sourceLinesList.Select(l => string.Join(' ', l.Select(w => w.CleanText))));
        string targetDiffText = string.Join('\n', targetLinesList.Select(l => string.Join(' ', l.Select(w => w.CleanText))));

        var diffLinesModel = diffBuilder.BuildDiffModel(sourceDiffText, targetDiffText);

        // -- DÉTECTION GLOBALE DE DÉPLACEMENT POUR LES COULEURS VISUELLES --
        var visDel = new Dictionary<string, int>();
        var visIns = new Dictionary<string, int>();

        for (int i = 0; i < diffLinesModel.NewText.Lines.Count; i++)
        {
            if (diffLinesModel.OldText.Lines[i].Type == ChangeType.Deleted)
            {
                string t = diffLinesModel.OldText.Lines[i].Text.Trim();
                if (t.Length > 0) visDel[t] = visDel.GetValueOrDefault(t) + 1;
            }
            if (diffLinesModel.NewText.Lines[i].Type == ChangeType.Inserted)
            {
                string t = diffLinesModel.NewText.Lines[i].Text.Trim();
                if (t.Length > 0) visIns[t] = visIns.GetValueOrDefault(t) + 1;
            }
        }

        var visSkipDel = new Dictionary<string, int>();
        var visSkipIns = new Dictionary<string, int>();
        foreach(var kvp in visDel)
        {
            if (visIns.TryGetValue(kvp.Key, out int insC))
            {
                int moves = Math.Min(kvp.Value, insC);
                visSkipDel[kvp.Key] = moves;
                visSkipIns[kvp.Key] = moves;
            }
        }

        var currentDeletes = new List<List<(string CleanText, List<LetterLoc> Letters)>>();
        var currentInserts = new List<List<(string CleanText, List<LetterLoc> Letters)>>();

        int sLineIdx = 0;
        int tLineIdx = 0;

        void FlushBlocks()
        {
            var actualDeletes = new List<List<(string CleanText, List<LetterLoc> Letters)>>();
            var actualInserts = new List<List<(string CleanText, List<LetterLoc> Letters)>>();

            // Filtrage des lignes qui ont juste été déplacées (elles ne seront ni rouges, ni vertes)
            foreach (var l in currentDeletes)
            {
                string text = string.Join(" ", l.Select(w => w.CleanText)).Trim();
                if (visSkipDel.TryGetValue(text, out int moves) && moves > 0)
                {
                    visSkipDel[text] = moves - 1;
                }
                else
                {
                    actualDeletes.Add(l);
                }
            }

            foreach (var l in currentInserts)
            {
                string text = string.Join(" ", l.Select(w => w.CleanText)).Trim();
                if (visSkipIns.TryGetValue(text, out int moves) && moves > 0)
                {
                    visSkipIns[text] = moves - 1;
                }
                else
                {
                    actualInserts.Add(l);
                }
            }

            // Dessin des vraies différences
            if (actualDeletes.Count > 0 && actualInserts.Count == 0)
            {
                foreach (var l in actualDeletes)
                    foreach (var w in l) result.Highlights.SourceRed.AddRange(w.Letters);
            }
            else if (actualInserts.Count > 0 && actualDeletes.Count == 0)
            {
                foreach (var l in actualInserts)
                    foreach (var w in l) result.Highlights.TargetRed.AddRange(w.Letters);
            }
            else if (actualDeletes.Count > 0 || actualInserts.Count > 0)
            {
                // Différence subtile : on zoome mot par mot pour colorier le mot modifié
                var sWords = actualDeletes.SelectMany(l => l).ToList();
                var tWords = actualInserts.SelectMany(l => l).ToList();

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
                        result.Highlights.SourceRed.AddRange(swVal.Letters);
                    }
                    else if (newWord.Type == ChangeType.Inserted && hasTW)
                    {
                        result.Highlights.TargetRed.AddRange(twVal.Letters);
                    }
                    else if ((oldWord.Type == ChangeType.Modified || newWord.Type == ChangeType.Modified))
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
    // PIPELINE DE GROUPEMENT PAR LIGNE SPATIALE
    // ==============================================================

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

                // Filtre anti "fake bold"
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

            // Tri final cohérent : De haut en bas, puis de gauche à droite
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
            // Remplacement direct de la virgule par un point pour pallier les erreurs de l'extraction interne du PDF
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