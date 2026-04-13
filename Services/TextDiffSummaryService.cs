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

        // Sécurité : DiffPlex n'aime pas les valeurs nulles
        var diffLines = diffBuilder.BuildDiffModel(cleanSource ?? string.Empty, cleanTarget ?? string.Empty);

        var blocks = new List<DiffSummaryBlock>();

        // Mise en cache de la longueur pour de meilleures performances dans les boucles
        int linesCount = diffLines.NewText.Lines.Count;

        // OPTIMISATION : Utilisation de StringComparer.Ordinal pour un hachage et une comparaison beaucoup plus rapides
        var sumDel = new Dictionary<string, int>(StringComparer.Ordinal);
        var sumIns = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1. Première passe : Comptabiliser les insertions et suppressions (y compris celles issues de modifications)
        for (int i = 0; i < linesCount; i++)
        {
            var oldLine = diffLines.OldText.Lines[i];
            var newLine = diffLines.NewText.Lines[i];

            if (oldLine.Type is ChangeType.Deleted or ChangeType.Modified)
            {
                string t = oldLine.Text.Trim();
                if (t.Length > 0)
                {
                    sumDel.TryGetValue(t, out int count);
                    sumDel[t] = count + 1;
                }
            }

            if (newLine.Type is ChangeType.Inserted or ChangeType.Modified)
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

        // 3. Deuxième passe : Générer les blocs de résumé en scindant les modifications en ajouts et suppressions purs
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

            // Ignorer les blocs identiques qui ont juste été déplacés
            if (isDel)
            {
                string txt = oldLine.Text.Trim();
                if (txt.Length > 0 && skipDel.TryGetValue(txt, out int moves) && moves > 0)
                {
                    skipDel[txt] = moves - 1;
                    isDel = false; // On annule la suppression puisqu'elle a été déplacée
                }
            }
            if (isIns)
            {
                string txt = newLine.Text.Trim();
                if (txt.Length > 0 && skipIns.TryGetValue(txt, out int moves) && moves > 0)
                {
                    skipIns[txt] = moves - 1;
                    isIns = false; // On annule l'insertion puisqu'elle a été déplacée
                }
            }

            // Traitement d'une différence validée
            if (isDel || isIns)
            {
                // Si on commence un nouveau bloc, on capture le contexte "Avant"
                if (currentDel.Count == 0 && currentIns.Count == 0)
                {
                    ctxBefore = GetValidContextLine(diffLines.NewText.Lines, i, -1);
                }

                if (isDel && !string.IsNullOrEmpty(oldLine.Text)) currentDel.Add(oldLine.Text);
                if (isIns && !string.IsNullOrEmpty(newLine.Text)) currentIns.Add(newLine.Text);

                lastDiffIndex = i;
            }
            else
            {
                // Ligne inchangée : on flush (vide) les blocs accumulés avec le contexte "Après"
                if (currentDel.Count > 0 || currentIns.Count > 0)
                {
                    FlushBlocks(blocks, currentDel, currentIns, ctxBefore, GetValidContextLine(diffLines.NewText.Lines, lastDiffIndex, 1));
                    currentDel.Clear();
                    currentIns.Clear();
                }
            }
        }

        // Flush final si le document se termine par une différence
        if (currentDel.Count > 0 || currentIns.Count > 0)
        {
            FlushBlocks(blocks, currentDel, currentIns, ctxBefore, string.Empty);
        }

        return (blocks.Count, blocks, diffLines);
    }

    private void FlushBlocks(List<DiffSummaryBlock> blocks, List<string> dels, List<string> ins, string ctxBefore, string ctxAfter)
    {
        // On scinde formellement les différences en Suppressions Pures (Rouge) et Ajouts Purs (Vert)
        // Les modifications n'existent plus en tant que telles.

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