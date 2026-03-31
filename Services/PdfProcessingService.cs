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
                    pair.ErrorMessage = "Identique (Aucune différence)";
                    pair.DiffCount = 0;
                }
                else
                {
                    // Définition du chemin du rapport
                    string reportPath = Path.Combine(outputDiffDir, $"DiffReport_Doc_{pair.MatchKey}.pdf");

                    // 2. Generation of the colored difference report (CÔTE À CÔTE)
                    int diffCount = await GenerateColoredDiffReportAsync(pair, sourceText, targetText, reportPath);

                    // Mise à jour des propriétés du modèle
                    pair.DiffCount = diffCount;
                    pair.ReportPath = reportPath; // Active automatiquement le bouton "Ouvrir PDF"

                    if (diffCount > 0)
                    {
                        pair.Status = CompareStatus.Different;
                        pair.ErrorMessage = $"{diffCount} différence(s) détectée(s)";
                    }
                    else
                    {
                        // Cas où les fichiers sont différents bitairement mais identiques textuellement
                        pair.Status = CompareStatus.Identical;
                        pair.ErrorMessage = "Faux positifs ignorés (espaces/sauts de ligne)";
                    }
                }
            }
            catch (Exception ex)
            {
                pair.Status = CompareStatus.Error;
                pair.ErrorMessage = $"Erreur: {ex.Message}";
                pair.DiffCount = -1; // -1 pour les mettre en bas lors du tri
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

    // Génération du rapport PDF Côte à Côte (Side-by-Side)
    private async Task<int> GenerateColoredDiffReportAsync(DocumentPair pair, string sourceText, string targetText, string reportPath)
    {
        return await Task.Run(() =>
        {
            // S'assurer que le dossier existe
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

            // NORMALISATION : On nettoie les textes pour éviter les faux positifs
            string cleanSource = NormalizePdfText(sourceText);
            string cleanTarget = NormalizePdfText(targetText);

            // CHANGEMENT MAJEUR : Utilisation de SideBySideDiffBuilder pour avoir 2 colonnes synchronisées
            var diffBuilder = new SideBySideDiffBuilder(new DiffPlex.Differ());
            var diff = diffBuilder.BuildDiffModel(cleanSource, cleanTarget);

            var builder = new PdfDocumentBuilder();

            // CORRECTION ICI : Format Paysage (Landscape) : Largeur 842, Hauteur 595
            PdfPageBuilder page = builder.AddPage(842, 595);

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

            double margin = 30;
            double yPosition = 595 - margin;
            int maxCharsPerLine = 70; // Environ 70 caractères max par moitié de page

            // Noms de fichiers
            string sourceFileName = Path.GetFileName(pair.SourcePath);
            string targetFileName = Path.GetFileName(pair.TargetPath!);

            // --- EN-TÊTE DU RAPPORT ---
            page.SetTextAndFillColor(0, 0, 0); // Noir
            page.AddText($"RAPPORT DE DIFFÉRENCES (CÔTE À CÔTE) - Clé: {pair.MatchKey}", 14m, new PdfPoint(margin, yPosition), fontBold);
            yPosition -= 25;

            // Titre Colonne Gauche (Source)
            page.SetTextAndFillColor(200, 0, 0); // Rouge
            page.AddText($"SOURCE (Rouge) : {sourceFileName}", 12m, new PdfPoint(20, yPosition), fontBold);

            // Titre Colonne Droite (Cible)
            page.SetTextAndFillColor(0, 128, 0); // Vert
            page.AddText($"CIBLE (Vert) : {targetFileName}", 12m, new PdfPoint(425, yPosition), fontBold);

            yPosition -= 25; // Espace avant de commencer les textes

            int differencesCount = 0; // Compteur de différences réelles

            // --- CORPS DU RAPPORT (Lecture simultanée gauche/droite) ---
            for (int i = 0; i < diff.OldText.Lines.Count; i++)
            {
                var leftLine = diff.OldText.Lines[i];
                var rightLine = diff.NewText.Lines[i];

                // Vérifier si cette ligne contient une différence
                bool isDiff = (leftLine.Type != ChangeType.Unchanged && leftLine.Type != ChangeType.Imaginary) ||
                              (rightLine.Type != ChangeType.Unchanged && rightLine.Type != ChangeType.Imaginary);

                if (isDiff)
                {
                    differencesCount++;
                }

                // Découper le texte pour qu'il ne dépasse pas sa moitié de page
                var leftWrapped = WrapText(leftLine.Text ?? "", maxCharsPerLine);
                var rightWrapped = WrapText(rightLine.Text ?? "", maxCharsPerLine);

                // On prend le plus grand nombre de lignes entre la gauche et la droite pour rester parfaitement aligné
                int maxLines = Math.Max(1, Math.Max(leftWrapped.Count, rightWrapped.Count));

                for (int j = 0; j < maxLines; j++)
                {
                    // Gestion du changement de page synchronisé pour les deux colonnes
                    if (yPosition < margin)
                    {
                        // CORRECTION ICI AUSSI : 842 de largeur, 595 de hauteur
                        page = builder.AddPage(842, 595); // Nouvelle page Paysage
                        yPosition = 595 - margin;
                    }

                    string lText = j < leftWrapped.Count ? leftWrapped[j] : "";
                    string rText = j < rightWrapped.Count ? rightWrapped[j] : "";

                    // DESSIN COLONNE GAUCHE (Source)
                    if (!string.IsNullOrEmpty(lText))
                    {
                        SetColorForType(page, leftLine.Type);
                        page.AddText(lText, 10m, new PdfPoint(20, yPosition), font);
                    }

                    // DESSIN COLONNE DROITE (Cible)
                    if (!string.IsNullOrEmpty(rText))
                    {
                        SetColorForType(page, rightLine.Type);
                        page.AddText(rText, 10m, new PdfPoint(425, yPosition), font);
                    }

                    yPosition -= 13; // Interligne
                }

                yPosition -= 4; // Petit espace entre chaque ligne/bloc de texte
            }

            File.WriteAllBytes(reportPath, builder.Build());

            return differencesCount; // Retourne le compteur final
        });
    }

    /// <summary>
    /// Applique la couleur correcte en fonction du type de modification
    /// </summary>
    private void SetColorForType(PdfPageBuilder page, ChangeType type)
    {
        switch (type)
        {
            case ChangeType.Inserted:
                page.SetTextAndFillColor(0, 128, 0); // Vert
                break;
            case ChangeType.Deleted:
                page.SetTextAndFillColor(200, 0, 0); // Rouge
                break;
            case ChangeType.Modified:
                page.SetTextAndFillColor(0, 0, 200); // Bleu
                break;
            case ChangeType.Unchanged:
                page.SetTextAndFillColor(90, 90, 90); // Gris foncé pour le texte inchangé
                break;
            default:
                page.SetTextAndFillColor(255, 255, 255); // Imaginary (invisible)
                break;
        }
    }

    /// <summary>
    /// Fonction pour normaliser le texte du PDF de façon INTELLIGENTE.
    /// Évite que l'ajout d'un seul mot décale tout le document.
    /// Reconstruit le texte phrase par phrase pour un diff granulaire.
    /// </summary>
    private string NormalizePdfText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. On "aplatit" tout le texte
        string flatText = Regex.Replace(input, @"\s+", " ");

        // 2. Découpage intelligent basé sur la ponctuation
        flatText = flatText.Replace(". ", ".\n");
        flatText = flatText.Replace("? ", "?\n");
        flatText = flatText.Replace("! ", "!\n");
        flatText = flatText.Replace(": ", ":\n");

        // Gestion intelligente des listes à puces
        flatText = flatText.Replace("•", "\n• ");
        flatText = flatText.Replace(" o ", "\n o ");

        // 3. Nettoyage
        var lines = flatText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0);

        return string.Join("\n", lines);
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