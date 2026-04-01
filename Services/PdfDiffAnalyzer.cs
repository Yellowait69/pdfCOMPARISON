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
        // 2. CORRECTION DÉFINITIVE: Algorithme Hybride (Ligne globale -> Mot local)
        // Bloque la fragmentation des grands paragraphes et les décalages de page
        // ====================================================================
        var sourceLinesList = GroupIntoLines(sourceWords);
        var targetLinesList = GroupIntoLines(targetWords);

        string sourceDiffText = string.Join('\n', sourceLinesList.Select(l => string.Join(' ', l.Select(w => w.CleanText))));
        string targetDiffText = string.Join('\n', targetLinesList.Select(l => string.Join(' ', l.Select(w => w.CleanText))));

        var diffLinesModel = diffBuilder.BuildDiffModel(sourceDiffText, targetDiffText);

        var currentDeletes = new List<List<(string CleanText, List<LetterLoc> Letters)>>();
        var currentInserts = new List<List<(string CleanText, List<LetterLoc> Letters)>>();

        int sLineIdx = 0;
        int tLineIdx = 0;

        // Fonction locale pour traiter un bloc accumulé
        void FlushBlocks()
        {
            if (currentDeletes.Count > 0 && currentInserts.Count == 0)
            {
                // Suppression pure de lignes complètes (Aucun mot ne sera épargné)
                foreach (var l in currentDeletes)
                    foreach (var w in l) result.Highlights.SourceRed.AddRange(w.Letters);
            }
            else if (currentInserts.Count > 0 && currentDeletes.Count == 0)
            {
                // Insertion pure d'un nouveau paragraphe (Tout sera vert, même les mots communs comme "EUR")
                foreach (var l in currentInserts)
                    foreach (var w in l) result.Highlights.TargetRed.AddRange(w.Letters);
            }
            else if (currentDeletes.Count > 0 && currentInserts.Count > 0)
            {
                // Modification ciblée d'un bloc -> On zoome et on utilise DiffPlex Mot par Mot pour la précision
                var sWords = currentDeletes.SelectMany(l => l).ToList();
                var tWords = currentInserts.SelectMany(l => l).ToList();

                string sText = string.Join('\n', sWords.Select(w => w.CleanText));
                string tText = string.Join('\n', tWords.Select(w => w.CleanText));

                var wordDiff = diffBuilder.BuildDiffModel(sText, tText);

                int swIdx = 0, twIdx = 0;

                for (int i = 0; i < wordDiff.NewText.Lines.Count; i++)
                {
                    var oldWord = wordDiff.OldText.Lines[i];
                    var newWord = wordDiff.NewText.Lines[i];

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

        // Lecture de haut en bas
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
                    // La ligne est intacte, on déclenche l'analyse du bloc précédent
                    FlushBlocks();
                }
                else
                {
                    // La ligne contient des différences, on l'ajoute au bloc courant pour l'analyser au microscope
                    currentDeletes.Add(sLine!);
                    currentInserts.Add(tLine!);
                }
            }
        }
        FlushBlocks(); // S'assure que les modifications de fin de document sont bien traitées

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