using System;
using System.Collections.Generic;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;

namespace PDFComparison.Services;

public interface ITextDiffSummaryService
{
    // CORRECTION : Remplacement de DiffPaneModel par SideBySideDiffModel
    (int DifferencesCount, List<DiffSummaryBlock> Blocks, SideBySideDiffModel DiffLinesModel) BuildTextSummary(string cleanSource, string cleanTarget);
}

public class TextDiffSummaryService : ITextDiffSummaryService
{
    // CORRECTION : Remplacement de DiffPaneModel par SideBySideDiffModel
    public (int DifferencesCount, List<DiffSummaryBlock> Blocks, SideBySideDiffModel DiffLinesModel) BuildTextSummary(string cleanSource, string cleanTarget)
    {
        var diffBuilder = new SideBySideDiffBuilder(new Differ());

        // Sécurité : DiffPlex n'aime pas les valeurs nulles
        var diffLines = diffBuilder.BuildDiffModel(cleanSource ?? string.Empty, cleanTarget ?? string.Empty);

        var blocks = new List<DiffSummaryBlock>();
        int diffCount = 0;

        // Mise en cache de la longueur pour de meilleures performances dans les boucles
        int linesCount = diffLines.NewText.Lines.Count;

        // OPTIMISATION : Utilisation de StringComparer.Ordinal pour un hachage et une comparaison beaucoup plus rapides
        var sumDel = new Dictionary<string, int>(StringComparer.Ordinal);
        var sumIns = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1. Première passe : Comptabiliser les insertions et suppressions
        for (int i = 0; i < linesCount; i++)
        {
            var oldLine = diffLines.OldText.Lines[i];
            var newLine = diffLines.NewText.Lines[i];

            if (oldLine.Type == ChangeType.Deleted)
            {
                string t = oldLine.Text.Trim();
                if (t.Length > 0)
                {
                    sumDel.TryGetValue(t, out int count);
                    sumDel[t] = count + 1;
                }
            }

            if (newLine.Type == ChangeType.Inserted)
            {
                string t = newLine.Text.Trim();
                if (t.Length > 0)
                {
                    sumIns.TryGetValue(t, out int count);
                    sumIns[t] = count + 1;
                }
            }
        }

        var skipDel = new Dictionary<string, int>(StringComparer.Ordinal);
        var skipIns = new Dictionary<string, int>(StringComparer.Ordinal);

        // 2. Calculer les correspondances (les blocs identiques qui ont juste été déplacés)
        foreach (var kvp in sumDel)
        {
            if (sumIns.TryGetValue(kvp.Key, out int insCount))
            {
                int moves = Math.Min(kvp.Value, insCount);
                skipDel[kvp.Key] = moves;
                skipIns[kvp.Key] = moves;
            }
        }

        // 3. Deuxième passe : Générer les blocs de résumé et ignorer les blocs déplacés
        for (int i = 0; i < linesCount; i++)
        {
            var newLine = diffLines.NewText.Lines[i];
            var oldLine = diffLines.OldText.Lines[i];

            if (oldLine.Type == ChangeType.Deleted)
            {
                string txt = oldLine.Text.Trim();
                if (txt.Length > 0 && skipDel.TryGetValue(txt, out int moves) && moves > 0)
                {
                    skipDel[txt] = moves - 1;
                    continue;
                }
            }
            else if (newLine.Type == ChangeType.Inserted)
            {
                string txt = newLine.Text.Trim();
                if (txt.Length > 0 && skipIns.TryGetValue(txt, out int moves) && moves > 0)
                {
                    skipIns[txt] = moves - 1;
                    continue;
                }
            }

            if (newLine.Type is ChangeType.Inserted or ChangeType.Modified || oldLine.Type is ChangeType.Deleted)
            {
                diffCount++;

                blocks.Add(new DiffSummaryBlock
                {
                    Type = newLine.Type is not ChangeType.Unchanged ? newLine.Type : oldLine.Type,
                    ContextBefore = GetValidContextLine(diffLines.NewText.Lines, i, -1),
                    ContextAfter = GetValidContextLine(diffLines.NewText.Lines, i, 1),
                    OldText = (newLine.Type is ChangeType.Modified || oldLine.Type is ChangeType.Deleted) ? oldLine.Text : string.Empty,
                    NewText = (newLine.Type is ChangeType.Inserted || newLine.Type is ChangeType.Modified) ? newLine.Text : string.Empty
                });
            }
        }

        return (diffCount, blocks, diffLines); // retourne bien le SideBySideDiffModel maintenant
    }

    private string GetValidContextLine(List<DiffPiece> lines, int currentIndex, int direction)
    {
        int i = currentIndex + direction;
        int count = lines.Count; // Mise en cache

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