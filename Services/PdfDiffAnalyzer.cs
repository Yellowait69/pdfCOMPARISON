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
        // On extrait la langue de la clé (ex: "NL_44980_36" -> "NL")
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

        // CORRECTION : On nettoie le texte global pour le résumé textuel ligne par ligne
        var diffLines = diffBuilder.BuildDiffModel(CleanLineForDiff(cleanSource), CleanLineForDiff(cleanTarget));

        // 1. Analyse Ligne par Ligne (Pour le résumé global)
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

        // 2. Analyse Mot par Mot (Pour le surlignage visuel)
        // CORRECTION : Nettoyage extrême appliqué à chaque mot extrait pour éviter les faux positifs
        var diffWords = diffBuilder.BuildDiffModel(
            string.Join('\n', sourceWords.Select(w => CleanWordForDiff(w.Text))),
            string.Join('\n', targetWords.Select(w => CleanWordForDiff(w.Text)))
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
                // Encadrement complet des mots modifiés
                result.Highlights.SourceYellow.AddRange(oldWordInfo.Letters.Select(l =>
                    new LetterLoc(l.GlyphRectangle, oldWordInfo.PageNumber, (decimal)l.Location.Y, (decimal)l.PointSize)));

                result.Highlights.TargetYellow.AddRange(newWordInfo.Letters.Select(l =>
                    new LetterLoc(l.GlyphRectangle, newWordInfo.PageNumber, (decimal)l.Location.Y, (decimal)l.PointSize)));
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

        // Applique les mêmes règles de normalisation typographique mais conserve les espaces normaux
        return input
            .Replace("\u00A0", " ")  // Transforme les espaces insécables en espaces normaux
            .Replace("\u00AD", "")   // Supprime les soft hyphens
            .Replace("–", "-").Replace("—", "-").Replace("−", "-") // Normalisation des tirets
            .Replace("’", "'").Replace("‘", "'").Replace("´", "'").Replace("`", "'") // Apostrophes
            .Replace("“", "\"").Replace("”", "\"").Replace("«", "\"").Replace("»", "\"") // Guillemets
            .Normalize(System.Text.NormalizationForm.FormKC);
    }

    private string CleanWordForDiff(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var cleaned = input
            // 1. Élimination totale des espaces invisibles et caractères de contrôle PDF
            .Replace("\u00A0", "")   // Espace insécable (NBSP)
            .Replace("\u200B", "")   // Zero-width space
            .Replace("\u200C", "")   // Zero-width non-joiner
            .Replace("\u200D", "")   // Zero-width joiner
            .Replace("\uFEFF", "")   // Byte Order Mark
            .Replace("\u00AD", "")   // Soft hyphen (Tiret conditionnel utilisé dans les blocs justifiés)
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace("\t", "")
            .Replace(" ", "")        // Élimine les espaces piégés dans un seul mot extrait

            // 2. Normalisation des tirets
            .Replace("–", "-")
            .Replace("—", "-")
            .Replace("−", "-")

            // 3. Normalisation des guillemets et apostrophes
            .Replace("’", "'")
            .Replace("‘", "'")
            .Replace("´", "'")
            .Replace("`", "'")
            .Replace("“", "\"")
            .Replace("”", "\"")
            .Replace("«", "\"")
            .Replace("»", "\"")

            // 4. Normalisation Unicode (Sépare les ligatures comme "ﬁ" en "fi")
            .Normalize(System.Text.NormalizationForm.FormKC)

            // 5. Casse (Ignore les différences Majuscule / Minuscule)
            .ToLowerInvariant()
            .Trim();

        // 6. Sécurité finale : Retire tout autre caractère de contrôle ASCII caché
        return new string(cleaned.Where(c => !char.IsControl(c)).ToArray());
    }
}