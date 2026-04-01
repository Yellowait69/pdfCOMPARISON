using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using UglyToad.PdfPig.Content; // Ajout obligatoire pour manipuler les lettres (Glyphes)

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
        // C'est l'arme absolue contre les faux positifs liés aux espaces et au découpage des mots

        // On aplatit les listes de mots pour n'obtenir qu'une suite ininterrompue de lettres
        var sourceItems = sourceWords.SelectMany(w => w.Letters.Select(l => (Letter: l, Page: w.PageNumber))).ToList();
        var targetItems = targetWords.SelectMany(w => w.Letters.Select(l => (Letter: l, Page: w.PageNumber))).ToList();

        // On compare lettre par lettre (en les nettoyant avec CleanGlyph)
        var diffGlyphs = diffBuilder.BuildDiffModel(
            string.Join('\n', sourceItems.Select(x => CleanGlyph(x.Letter.Value))),
            string.Join('\n', targetItems.Select(x => CleanGlyph(x.Letter.Value)))
        );

        int sPointer = 0, tPointer = 0;

        for (int i = 0; i < diffGlyphs.NewText.Lines.Count; i++)
        {
            var oldDiff = diffGlyphs.OldText.Lines[i];
            var newDiff = diffGlyphs.NewText.Lines[i];

            bool hasS = oldDiff.Type != ChangeType.Imaginary && sPointer < sourceItems.Count;
            bool hasT = newDiff.Type != ChangeType.Imaginary && tPointer < targetItems.Count;

            var sVal = hasS ? sourceItems[sPointer++] : default;
            var tVal = hasT ? targetItems[tPointer++] : default;

            if (oldDiff.Type == ChangeType.Deleted && hasS)
            {
                result.Highlights.SourceRed.Add(new LetterLoc(sVal.Letter.GlyphRectangle, sVal.Page, (decimal)sVal.Letter.Location.Y, (decimal)sVal.Letter.PointSize));
            }
            else if (newDiff.Type == ChangeType.Inserted && hasT)
            {
                result.Highlights.TargetRed.Add(new LetterLoc(tVal.Letter.GlyphRectangle, tVal.Page, (decimal)tVal.Letter.Location.Y, (decimal)tVal.Letter.PointSize));
            }
            else if (oldDiff.Type == ChangeType.Modified || newDiff.Type == ChangeType.Modified)
            {
                if (hasS) result.Highlights.SourceYellow.Add(new LetterLoc(sVal.Letter.GlyphRectangle, sVal.Page, (decimal)sVal.Letter.Location.Y, (decimal)sVal.Letter.PointSize));
                if (hasT) result.Highlights.TargetYellow.Add(new LetterLoc(tVal.Letter.GlyphRectangle, tVal.Page, (decimal)tVal.Letter.Location.Y, (decimal)tVal.Letter.PointSize));
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
    // FILTRES ANTI FAUX-POSITIFS (PDF QUIRKS)
    // ==============================================================

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

    // Nettoyage au niveau de la lettre individuelle (Glyphe)
    private string CleanGlyph(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var cleaned = input
            .Replace("\u00A0", "")   // Espace insécable (NBSP)
            .Replace("\u200B", "")   // Zero-width space
            .Replace("\u200C", "")   // Zero-width non-joiner
            .Replace("\u200D", "")   // Zero-width joiner
            .Replace("\uFEFF", "")   // Byte Order Mark
            .Replace("\u00AD", "")   // Soft hyphen (Tiret conditionnel invisible)
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace("\t", "")
            .Replace(" ", "")        // Élimine les espaces classiques piégés dans les glyphes
            .Replace("–", "-").Replace("—", "-").Replace("−", "-")
            .Replace("’", "'").Replace("‘", "'").Replace("´", "'").Replace("`", "'")
            .Replace("“", "\"").Replace("”", "\"").Replace("«", "\"").Replace("»", "\"")
            .Normalize(System.Text.NormalizationForm.FormKC) // Sépare les ligatures (ex: ﬁ devient f + i)
            .ToLowerInvariant()
            .Trim();

        return new string(cleaned.Where(c => !char.IsControl(c)).ToArray());
    }
}