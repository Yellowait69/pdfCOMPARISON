using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

    public async Task ProcessPairsAsync(IEnumerable<DocumentPair> validPairs, string outputDiffDir, IProgress<int> progress)
    {
        int completed = 0;

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

                // 1. Comparaison rapide en O(N) : vérification binaire du hachage des chaînes
                if (string.Equals(sourceText, targetText, StringComparison.Ordinal))
                {
                    pair.Status = CompareStatus.Identical;
                }
                else
                {
                    pair.Status = CompareStatus.Different;
                    // 2. Génération du rapport de différences en couleurs
                    await GenerateColoredDiffReportAsync(pair, sourceText, targetText, outputDiffDir);
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
        // Parsing Options optimisées pour ignorer les images et chemins (gain de vitesse majeur)
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

    private async Task GenerateColoredDiffReportAsync(DocumentPair pair, string sourceText, string targetText, string outputDir)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(outputDir);
            string reportPath = Path.Combine(outputDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

            // Utilisation de DiffPlex pour générer le diff ligne par ligne
            var diffBuilder = new InlineDiffBuilder(new DiffPlex.Differ());
            var diff = diffBuilder.BuildDiffModel(sourceText, targetText);

            var builder = new PdfDocumentBuilder();
            PdfPageBuilder page = builder.AddPage(PageSize.A4);

            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            var fontBold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

            double margin = 40;
            double yPosition = page.PageSize.Top - margin;
            double xPosition = margin;
            double lineHeight = 12;
            int maxCharsPerLine = 95; // Limite avant le retour à la ligne automatique

            // En-tête du PDF
            page.SetTextAndFillColor(0, 0, 0); // Noir
            page.AddText($"RAPPORT DE DIFFERENCES - Document Clé: {pair.MatchKey}", 14, new PdfPoint(xPosition, yPosition), fontBold);
            yPosition -= 30;

            foreach (var line in diff.Lines)
            {
                // On ignore les lignes inchangées pour ne montrer que les différences
                if (line.Type == ChangeType.Unchanged) continue;

                string prefix = line.Type switch
                {
                    ChangeType.Inserted => "[+] ",
                    ChangeType.Deleted => "[-] ",
                    ChangeType.Modified => "[*] ",
                    _ => ""
                };

                // Application des couleurs selon le type de modification
                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        page.SetTextAndFillColor(0, 128, 0); // Vert foncé
                        break;
                    case ChangeType.Deleted:
                        page.SetTextAndFillColor(200, 0, 0); // Rouge
                        break;
                    case ChangeType.Modified:
                        page.SetTextAndFillColor(0, 0, 200); // Bleu
                        break;
                }

                // Nettoyage de la ligne
                string cleanText = (prefix + line.Text).Replace("\r", "").Replace("\n", "").Replace("\t", "    ");

                // Algorithme de Word Wrap (Découpage en plusieurs lignes au lieu de tronquer)
                var wrappedLines = WrapText(cleanText, maxCharsPerLine);

                foreach (var wrappedLine in wrappedLines)
                {
                    // Gestion du changement de page
                    if (yPosition < margin)
                    {
                        page = builder.AddPage(PageSize.A4);
                        yPosition = page.PageSize.Top - margin;
                    }

                    page.AddText(wrappedLine, 10, new PdfPoint(xPosition, yPosition), font);
                    yPosition -= lineHeight;
                }

                // Petit espacement supplémentaire entre les différents blocs modifiés
                yPosition -= 4;
            }

            File.WriteAllBytes(reportPath, builder.Generate());
        });
    }

    /// <summary>
    /// Fonction utilitaire pour découper un texte trop long en plusieurs lignes (Word Wrap)
    /// </summary>
    private List<string> WrapText(string text, int maxLength)
    {
        var lines = new List<string>();
        for (int i = 0; i < text.Length; i += maxLength)
        {
            lines.Add(text.Substring(i, Math.Min(maxLength, text.Length - i)));
        }
        return lines;
    }
}