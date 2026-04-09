using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace PDFComparison.Services;

public interface IInlineDiffService
{
    (List<(string Text, byte r, byte g, byte b, bool isBold)> Left,
     List<(string Text, byte r, byte g, byte b, bool isBold)> Right) GetInlineDiffChunks(string oldText, string newText);
}

public partial class InlineDiffService : IInlineDiffService
{
    // OPTIMISATION : Utilisation du générateur de Regex (Compile-time) pour de meilleures performances
    [GeneratedRegex(@"(?<=\s+)")]
    private static partial Regex SplitWordsRegex();

    // NOUVELLES COULEURS : Plus modernes et mieux séparées
    private static readonly (byte r, byte g, byte b) ColorDeleted = (220, 38, 38);       // Rouge vif (Suppressions)
    private static readonly (byte r, byte g, byte b) ColorInserted = (22, 163, 74);      // Vert vif (Ajouts)
    private static readonly (byte r, byte g, byte b) ColorModified = (37, 99, 235);      // Bleu vif (Modifications)
    private static readonly (byte r, byte g, byte b) ColorUnchanged = (100, 100, 100);   // Gris clair (Inchangé)

    public (List<(string Text, byte r, byte g, byte b, bool isBold)> Left,
            List<(string Text, byte r, byte g, byte b, bool isBold)> Right) GetInlineDiffChunks(string oldText, string newText)
    {
        var leftChunks = new List<(string Text, byte r, byte g, byte b, bool isBold)>();
        var rightChunks = new List<(string Text, byte r, byte g, byte b, bool isBold)>();

        if (string.IsNullOrWhiteSpace(oldText) && string.IsNullOrWhiteSpace(newText))
            return (leftChunks, rightChunks);

        // Découpage propre avec la Regex pré-compilée
        var oldWords = SplitWordsRegex().Split(oldText ?? string.Empty).Where(x => x.Length > 0).ToList();
        var newWords = SplitWordsRegex().Split(newText ?? string.Empty).Where(x => x.Length > 0).ToList();

        var diff = new SideBySideDiffBuilder(new Differ()).BuildDiffModel(
            string.Join("\n", oldWords),
            string.Join("\n", newWords)
        );

        for (int i = 0; i < diff.OldText.Lines.Count; i++)
        {
            var oLine = diff.OldText.Lines[i];
            var nLine = diff.NewText.Lines[i];

            // Traitement de la colonne de GAUCHE (Document Source)
            if (oLine.Type != ChangeType.Imaginary)
            {
                bool isChanged = oLine.Type is ChangeType.Deleted or ChangeType.Modified;

                // CORRECTION : Attribution de la bonne couleur selon le type exact
                var color = oLine.Type == ChangeType.Deleted ? ColorDeleted :
                           (oLine.Type == ChangeType.Modified ? ColorModified : ColorUnchanged);

                string cleanText = oLine.Text.Replace("\n", "");
                leftChunks.Add((cleanText, color.r, color.g, color.b, isChanged));
            }

            // Traitement de la colonne de DROITE (Document Cible)
            if (nLine.Type != ChangeType.Imaginary)
            {
                bool isChanged = nLine.Type is ChangeType.Inserted or ChangeType.Modified;

                // CORRECTION : Attribution de la bonne couleur selon le type exact
                var color = nLine.Type == ChangeType.Inserted ? ColorInserted :
                           (nLine.Type == ChangeType.Modified ? ColorModified : ColorUnchanged);

                string cleanText = nLine.Text.Replace("\n", "");
                rightChunks.Add((cleanText, color.r, color.g, color.b, isChanged));
            }
        }

        return (leftChunks, rightChunks);
    }
}