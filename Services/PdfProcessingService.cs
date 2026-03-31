using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using PdfComparer.Models;

namespace PdfComparer.Services;

public class PdfProcessingService
{
    // Regex précompilée pour la performance : capture les chiffres avant ".pdf"
    private static readonly Regex KeyRegex = new(@"(\d+)\.pdf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<DocumentPair> MatchFiles(string sourceDir, string targetDir)
    {
        var sourceFiles = Directory.GetFiles(sourceDir, "*.pdf");
        var targetFiles = Directory.GetFiles(targetDir, "*.pdf");

        // Dictionnaire pour accès O(1) aux fichiers cibles
        var targetDict = targetFiles
            .Select(f => new { Path = f, Match = KeyRegex.Match(f) })
            .Where(x => x.Match.Success)
            .ToDictionary(x => x.Match.Groups[1].Value, x => x.Path);

        var pairs = new List<DocumentPair>();

        foreach (var sourceFile in sourceFiles)
        {
            var match = KeyRegex.Match(sourceFile);
            if (match.Success)
            {
                string key = match.Groups[1].Value;
                targetDict.TryGetValue(key, out string? targetPath);
                pairs.Add(new DocumentPair(key, sourceFile, targetPath));
            }
        }

        return pairs;
    }

    public async Task ProcessPairsAsync(IEnumerable<DocumentPair> pairs, string outputDiffDir, IProgress<int> progress)
    {
        int completed = 0;
        var validPairs = pairs.Where(p => p.Status != CompareStatus.MissingInTarget).ToList();

        // Traitement parallèle asynchrone optimisé (I/O bound et CPU bound)
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        await Parallel.ForEachAsync(validPairs, parallelOptions, async (pair, ct) =>
        {
            try
            {
                var sourceText = ExtractTextFast(pair.SourcePath);
                var targetText = ExtractTextFast(pair.TargetPath!);

                // 1. Comparaison rapide en O(N)
                if (string.Equals(sourceText, targetText, StringComparison.Ordinal))
                {
                    pair.Status = CompareStatus.Identical;
                }
                else
                {
                    pair.Status = CompareStatus.Different;
                    // 2. Génération du rapport de différences si différent
                    await GenerateDiffReportAsync(pair, sourceText, targetText, outputDiffDir);
                }
            }
            catch (Exception ex)
            {
                pair.Status = CompareStatus.Error;
                pair.ErrorMessage = ex.Message;
            }
            finally
            {
                int currentCount = Interlocked.Increment(ref completed);
                progress.Report(currentCount);
            }
        });
    }

    private string ExtractTextFast(string pdfPath)
    {
        var sb = new StringBuilder();
        // Parsing Options optimisées pour ignorer les images et chemins (gain de vitesse)
        var options = new ParsingOptions { ClipPaths = false };

        using (var document = PdfDocument.Open(pdfPath, options))
        {
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
            }
        }
        return sb.ToString();
    }

    private async Task GenerateDiffReportAsync(DocumentPair pair, string sourceText, string targetText, string outputDir)
    {
        return await Task.Run(() =>
        {
            Directory.CreateDirectory(outputDir);
            string reportPath = Path.Combine(outputDir, $"DiffReport_{pair.MatchKey}.pdf");

            // Utilisation de DiffPlex pour générer le diff ligne par ligne
            var diffBuilder = new InlineDiffBuilder(new DiffPlex.Differ());
            var diff = diffBuilder.BuildDiffModel(sourceText, targetText);

            var builder = new PdfDocumentBuilder();
            PdfPageBuilder page = builder.AddPage(PageSize.A4);
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);

            double yPosition = page.PageSize.Top - 50;
            double xPosition = 50;
            double lineHeight = 12;

            page.AddText($"Rapport de différence pour la clé: {pair.MatchKey}", 16, new PdfPoint(xPosition, yPosition), font);
            yPosition -= 30;

            foreach (var line in diff.Lines)
            {
                if (yPosition < 50) // Nouvelle page si on arrive en bas
                {
                    page = builder.AddPage(PageSize.A4);
                    yPosition = page.PageSize.Top - 50;
                }

                string prefix = line.Type switch
                {
                    ChangeType.Inserted => "[NOUVEAU] + ",
                    ChangeType.Deleted => "[SUPPRIME] - ",
                    ChangeType.Modified => "[MODIFIE] * ",
                    _ => "  "
                };

                // Si le texte est très long, PdfPig nécessite une gestion manuelle du retour à la ligne.
                // Pour cet exemple robuste, on tronque à 90 caractères par ligne.
                string safeText = (prefix + line.Text).Replace("\r", "").Replace("\n", "");
                if (safeText.Length > 90) safeText = safeText.Substring(0, 90) + "...";

                // Le surlignage visuel brut (dessin de rectangles de couleur) est possible mais verbeux.
                // L'approche "préfixe" est claire et infaillible pour l'extraction de texte pur.
                if (line.Type != ChangeType.Unchanged)
                {
                    page.AddText(safeText, 10, new PdfPoint(xPosition, yPosition), font);
                    yPosition -= lineHeight;
                }
            }

            File.WriteAllBytes(reportPath, builder.Generate());
        });
    }
}