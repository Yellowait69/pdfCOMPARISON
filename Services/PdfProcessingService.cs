using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace PDFComparison.Services;

public class PdfProcessingService
{
    // Precompiled Regex for performance: captures the digits before ".pdf"
    private static readonly Regex KeyRegex = new(@"(\d+)\.pdf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<DocumentPair> MatchFiles(string sourceDir, string targetDir)
    {
        var sourceFiles = Directory.GetFiles(sourceDir, "*.pdf");
        var targetFiles = Directory.GetFiles(targetDir, "*.pdf");

        // Dictionary for O(1) access to target files
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

        // Optimized asynchronous parallel processing (I/O bound and CPU bound)
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

                // 1. Fast O(N) comparison: binary check of string hashes
                if (string.Equals(sourceText, targetText, StringComparison.Ordinal))
                {
                    pair.Status = CompareStatus.Identical;
                }
                else
                {
                    pair.Status = CompareStatus.Different;
                    // 2. Generation of the colored difference report
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
        // Parsing Options optimized to ignore images and paths (major speed gain)
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

            // NORMALISATION : On nettoie les textes pour éviter que des sauts de ligne
            // invisibles ne fassent apparaître tout le document comme une erreur.
            string cleanSource = NormalizePdfText(sourceText);
            string cleanTarget = NormalizePdfText(targetText);

            // Using DiffPlex to generate line-by-line diff sur le texte nettoyé
            var diffBuilder = new InlineDiffBuilder(new DiffPlex.Differ());
            var diff = diffBuilder.BuildDiffModel(cleanSource, cleanTarget);

            var builder = new PdfDocumentBuilder();
            PdfPageBuilder page = builder.AddPage(PageSize.A4);

            // ==========================================
            // FIX: Using system Arial fonts to support all Unicode characters
            // ==========================================
            string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string arialPath = Path.Combine(fontsFolder, "arial.ttf");
            string arialBoldPath = Path.Combine(fontsFolder, "arialbd.ttf");

            // Verify if fonts exist
            if (!File.Exists(arialPath) || !File.Exists(arialBoldPath))
            {
                throw new FileNotFoundException("Required Arial fonts were not found on this system.");
            }

            var font = builder.AddTrueTypeFont(File.ReadAllBytes(arialPath));
            var fontBold = builder.AddTrueTypeFont(File.ReadAllBytes(arialBoldPath));
            // ==========================================

            double margin = 40;
            double yPosition = page.PageSize.Top - margin;
            int maxCharsPerLine = 95; // Limit before automatic line break

            // --- EN-TÊTE INTELLIGENT ---
            string sourceFileName = Path.GetFileName(pair.SourcePath);
            string targetFileName = Path.GetFileName(pair.TargetPath!);

            DrawText(ref page, builder, $"RAPPORT DE DIFFÉRENCES - Document Key: {pair.MatchKey}", fontBold, 14, ref yPosition, margin, 0, 0, 0);
            yPosition -= 15;
            DrawText(ref page, builder, $"Fichier Source (Rouge) : {sourceFileName}", font, 11, ref yPosition, margin, 200, 0, 0);
            DrawText(ref page, builder, $"Fichier Cible (Vert)   : {targetFileName}", font, 11, ref yPosition, margin, 0, 128, 0);
            yPosition -= 20;

            bool hasRealDifferences = false;

            // --- CORPS DU RAPPORT ---
            foreach (var line in diff.Lines)
            {
                // Ignore unchanged lines to only show differences
                if (line.Type == ChangeType.Unchanged) continue;

                hasRealDifferences = true;
                string prefix = "";
                byte r = 0, g = 0, b = 0;

                // Apply colors and labels according to modification type
                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        prefix = "[CIBLE] + : ";
                        r = 0; g = 128; b = 0; // Dark Green
                        break;
                    case ChangeType.Deleted:
                        prefix = "[SOURCE] - : ";
                        r = 200; g = 0; b = 0; // Red
                        break;
                    case ChangeType.Modified:
                        prefix = "[MODIFIÉ] * : ";
                        r = 0; g = 0; b = 200; // Blue
                        break;
                }

                // Line cleanup
                string cleanText = (prefix + line.Text).Replace("\r", "").Replace("\n", "").Replace("\t", "    ");

                // Word Wrap Algorithm (Split into multiple lines instead of truncating)
                var wrappedLines = WrapText(cleanText, maxCharsPerLine);

                foreach (var wrappedLine in wrappedLines)
                {
                    DrawText(ref page, builder, wrappedLine, font, 10, ref yPosition, margin, r, g, b);
                }

                // Small extra spacing between different modified blocks
                yPosition -= 5;
            }

            // Si la normalisation a éliminé les faux positifs et qu'il n'y a pas de vraie différence
            if (!hasRealDifferences)
            {
                DrawText(ref page, builder, "Aucune différence textuelle majeure trouvée (possibles variations d'espaces invisibles).", font, 11, ref yPosition, margin, 100, 100, 100);
            }

            File.WriteAllBytes(reportPath, builder.Build());
        });
    }

    /// <summary>
    /// Fonction pour normaliser le texte du PDF de façon INTELLIGENTE.
    /// Évite que l'ajout d'un seul mot décale tout le document.
    /// Reconstruit le texte phrase par phrase pour un diff granulaire.
    /// </summary>
    private string NormalizePdfText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. On "aplatit" tout le texte : on remplace tous les sauts de lignes
        // et espaces multiples par un seul et unique espace.
        string flatText = Regex.Replace(input, @"\s+", " ");

        // 2. Découpage intelligent : on recrée des lignes logiques basées sur la ponctuation.
        // Cela permet au comparateur de comparer phrase par phrase !
        flatText = flatText.Replace(". ", ".\n");
        flatText = flatText.Replace("? ", "?\n");
        flatText = flatText.Replace("! ", "!\n");
        flatText = flatText.Replace(": ", ":\n");

        // Gestion intelligente des listes à puces pour les isoler
        flatText = flatText.Replace("•", "\n• ");
        flatText = flatText.Replace(" o ", "\n o ");

        // 3. Nettoyage : on sépare par nos nouveaux sauts de ligne et on enlève le vide
        var lines = flatText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0);

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Utilitaire pour dessiner le texte et gérer automatiquement le changement de page PDF.
    /// </summary>
    private void DrawText(ref PdfPageBuilder page, PdfDocumentBuilder builder, string text, PdfDocumentBuilder.AddedFont font, double fontSize, ref double yPosition, double margin, byte r, byte g, byte b)
    {
        // Page change management
        if (yPosition < margin)
        {
            page = builder.AddPage(PageSize.A4);
            yPosition = page.PageSize.Top - margin;
        }

        page.SetTextAndFillColor(r, g, b);

        // CORRECTION DE L'ERREUR CS1503 : (decimal)fontSize
        page.AddText(text, (decimal)fontSize, new PdfPoint(margin, yPosition), font);

        yPosition -= (fontSize + 4); // Hauteur dynamique pour la prochaine ligne
    }

    /// <summary>
    /// Utility function to split long text into multiple lines (Word Wrap)
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