using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using UglyToad.PdfPig.Content; // Ajout nécessaire pour manipuler les lettres

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

        // 2. Analyse GLYPHE par GLYPHE (Pour le surlignage visuel PDF)
        // On aplatit, nettoie, dédoublonne et trie toutes les lettres.
        var sourceItems = FlattenAndClean(sourceWords);
        var targetItems = FlattenAndClean(targetWords);

        // On donne à DiffPlex une chaîne où chaque caractère parfaitement propre est sur une ligne
        var diffGlyphs = diffBuilder.BuildDiffModel(
            string.Join('\n', sourceItems.Select(x => x.Char)),
            string.Join('\n', targetItems.Select(x => x.Char))
        );

        int sPointer = 0, tPointer = 0;

        for (int i = 0; i < diffGlyphs.NewText.Lines.Count; i++)
        {
            var oldDiff = diffGlyphs.OldText.Lines[i];
            var newDiff = diffGlyphs.NewText.Lines[i];

            // Pointers sécurisés : on vérifie que DiffPlex ne pointe pas vers des données imaginaires
            bool hasS = oldDiff.Type != ChangeType.Imaginary && sPointer < sourceItems.Count;
            bool hasT = newDiff.Type != ChangeType.Imaginary && tPointer < targetItems.Count;

            var sVal = hasS ? sourceItems[sPointer++] : default;
            var tVal = hasT ? targetItems[tPointer++] : default;

            if (oldDiff.Type == ChangeType.Deleted && hasS)
            {
                result.Highlights.SourceRed.Add(sVal.Loc);
            }
            else if (newDiff.Type == ChangeType.Inserted && hasT)
            {
                result.Highlights.TargetRed.Add(tVal.Loc);
            }
            else if ((oldDiff.Type == ChangeType.Modified || newDiff.Type == ChangeType.Modified))
            {
                if (hasS) result.Highlights.SourceYellow.Add(sVal.Loc);
                if (hasT) result.Highlights.TargetYellow.Add(tVal.Loc);
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
    // PIPELINE DE NETTOYAGE ET SYNCHRONISATION ABSOLUE
    // ==============================================================

    private List<(string Char, LetterLoc Loc)> FlattenAndClean(IReadOnlyList<PdfWordInfo> words)
    {
        var list = new List<(string Char, LetterLoc Loc)>();

        foreach (var word in words)
        {
            foreach (var letter in word.Letters)
            {
                string cleaned = CleanGlyph(letter.Value);
                if (string.IsNullOrEmpty(cleaned)) continue;

                var loc = new LetterLoc(letter.GlyphRectangle, word.PageNumber, (decimal)letter.Location.Y, (decimal)letter.PointSize);

                foreach (char c in cleaned)
                {
                    // 1. FILTRE ANTI "FAKE BOLD" (Ombre portée invisible)
                    // Si le PDF a imprimé la même lettre au même endroit pour faire un effet de gras, on l'ignore.
                    if (list.Count > 0)
                    {
                        var last = list.Last();
                        if (last.Char == c.ToString() &&
                            last.Loc.PageNumber == loc.PageNumber &&
                            Math.Abs(last.Loc.BaselineY - loc.BaselineY) < 1.0m &&
                            Math.Abs((decimal)last.Loc.BoundingBox.BottomLeft.X - (decimal)loc.BoundingBox.BottomLeft.X) < 1.0m)
                        {
                            continue; // On rejette ce doublon fantôme
                        }
                    }

                    list.Add((c.ToString(), loc));
                }
            }
        }

        // 2. TRI VISUEL STRICT (Empêche l'ordre interne du PDF de fausser la comparaison)
        // On force la lecture de Haut en Bas, et de Gauche à Droite, sans tenir compte du code interne du PDF.
        return list
            .OrderBy(x => x.Loc.PageNumber)
            .ThenByDescending(x => Math.Round(x.Loc.BaselineY / 5.0m) * 5.0m) // Tolérance de 5 points pour aligner les textes sur la même ligne
            .ThenBy(x => x.Loc.BoundingBox.BottomLeft.X)
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

    private string CleanGlyph(string input)
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
            .Normalize(System.Text.NormalizationForm.FormKC) // Découpe proprement les ligatures (ex: ﬁ -> f + i)
            .ToLowerInvariant()
            .Trim();

        // On détruit tout ce qui est espace résiduel ou caractère de contrôle invisible
        return new string(cleaned.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
    }
}