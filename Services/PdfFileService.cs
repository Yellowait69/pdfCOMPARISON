using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PDFComparison.Models;

namespace PDFComparison.Services;

public partial class PdfFileService
{
    // Utilisation du Source Generator de Regex (C# 11) pour des performances optimales.
    // L'expression régulière est compilée à la création de l'application (Build time) plutôt qu'à l'exécution.
    // (Nécessite que la classe soit 'partial')

    // NOUVELLE REGEX : Capture la langue (2 lettres) et les deux blocs de chiffres finaux
    // Exemple : "_NL_44980_36.PDF" donnera la clé "NL_44980_36"
    [GeneratedRegex(@"([A-Z]{2}_\d+_\d+)\.pdf$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyRegex();

    public List<DocumentPair> MatchFiles(string sourceDir, string targetDir)
    {
        // EnumerateFiles est préférable à GetFiles : il n'alloue pas de gros tableaux en mémoire
        // et commence à lire les fichiers immédiatement.
        var targetDict = Directory.EnumerateFiles(targetDir, "*.pdf")
            // Utilisation de ValueTuples (Path: f, Match: ...) au lieu d'objets anonymes (new { ... })
            // C'est plus léger en mémoire et plus rapide.
            .Select(f => (Path: f, Match: KeyRegex().Match(f)))
            .Where(x => x.Match.Success)
            .ToDictionary(x => x.Match.Groups[1].Value.ToUpper(), x => x.Path); // .ToUpper() pour normaliser la clé (ex: "nl" devient "NL")

        var pairs = new List<DocumentPair>();

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*.pdf"))
        {
            var match = KeyRegex().Match(sourceFile);
            if (match.Success)
            {
                // On normalise également la clé source en majuscules pour s'assurer qu'elle match bien le dictionnaire
                string key = match.Groups[1].Value.ToUpper();

                // Pattern matching pour ignorer la déclaration explicite de la variable en amont (out var)
                targetDict.TryGetValue(key, out var targetPath);

                pairs.Add(new DocumentPair(key, sourceFile, targetPath));
            }
        }

        return pairs;
    }
}