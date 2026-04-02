using System;

using System.Collections.Generic;

using System.Linq;

using System.Text;

using System.Text.RegularExpressions;

using PDFComparison.Models;

using UglyToad.PdfPig;

using UglyToad.PdfPig.Content; // Ajout nécessaire pour manipuler l'objet 'Word'



namespace PDFComparison.Services;



public partial class PdfExtractionService

{

    // Utilisation du Source Generator de Regex pour éviter de recompiler l'expression à chaque appel

    [GeneratedRegex(@"\s+")]

    private static partial Regex WhitespaceRegex();



    // NOUVEAU : Regex pour nettoyer le texte brut des filigranes lors de l'extraction textuelle

    // On enlève les \b pour attraper les morceaux de filigrane cachés dans le texte (ex: CIMEN, MEN, TEST, TOTEIN)

    [GeneratedRegex(@"(?i)(specimen|cimen|speci|men|test|totein|Q000|D000|P000|A000)")]

    private static partial Regex WatermarkTextRegex();



    public string ExtractTextFast(string pdfPath)

    {

        var sb = new StringBuilder();

        var options = new ParsingOptions { ClipPaths = false };



        using var document = PdfDocument.Open(pdfPath, options);

        foreach (var page in document.GetPages())

        {

            // On récupère le texte brut de la page

            string rawText = page.Text;



            // ON DÉTRUIT LE FILIGRANE DU TEXTE BRUT :

            // Cela empêchera les mots "SPECIMEN" (et ses fragments) d'apparaître dans le rapport de synthèse écrit

            string cleanText = WatermarkTextRegex().Replace(rawText, "");



            sb.AppendLine(cleanText);

        }



        return sb.ToString();

    }



    public List<PdfWordInfo> ExtractWords(string pdfPath)

    {

        var words = new List<PdfWordInfo>();

        using var doc = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });



        foreach (var page in doc.GetPages())

        {

            foreach (var word in page.GetWords())

            {

                if (string.IsNullOrWhiteSpace(word.Text))

                    continue;



                // ==========================================

                // BOUCLIER ANTI-FILIGRANE : On ignore ce mot s'il s'agit d'un filigrane

                // ==========================================

                if (IsWatermark(word))

                    continue;



                words.Add(new PdfWordInfo

                {

                    Text = word.Text,

                    Letters = word.Letters,

                    PageNumber = page.Number

                });

            }

        }



        return words;

    }



    // À PLACER EN HAUT DE LA CLASSE (Optimisé pour les performances)

    // Ce Regex détecte tout ce qui ressemble à une date, un montant ou un numéro de compte/police

    [GeneratedRegex(@"^[\d.,/\-\s€$£]+(?:EUR)?$")]

    private static partial Regex ProtectedDataRegex();

    // Regex ciblant spécifiquement les codes filigranes isolés (Q0, Q000, A0, etc.)

    [GeneratedRegex(@"^(Q|D|P|A)0{1,3}$")]

    private static partial Regex WatermarkCodeRegex();

    // MÉTHODE D'IDENTIFICATION DES FILIGRANES (BLINDÉE)

    private bool IsWatermark(Word word)

    {

        string text = word.Text.ToUpperInvariant().Trim();

        // ==============================================================

        // 1. BOUCLIER ABSOLU (Dates, Montants, Numéros de police)

        // ==============================================================

        // Si le mot est un chiffre, une date (13.01.2025), un montant (0.00, 0,00 EUR)

        // ou un numéro de compte/police avec des tirets (006-5506356-25), ON LE GARDE TOUJOURS.

        if (ProtectedDataRegex().IsMatch(text))

        {

            return false;

        }

        // Protection des mots légitimes de la langue ("EN" en français, "MEN" en néerlandais).

        // On ne les considère comme des morceaux de filigrane QUE s'ils sont gigantesques (> 12.0)

        if ((text == "EN" || text == "S" || text == "P" || text == "E" || text == "C" || text == "I" || text == "M" || text == "E" || text == "N" || text == "Q" || text == "D" || text == "MEN" || text == "SP" || text == "SPE" || text == "SPEC") &&

             word.Letters.Count > 0 && word.Letters.Max(l => l.PointSize) <= 15.0)

        {

            return false;

        }

        // ==============================================================

        // 2. FILTRAGE STRICT PAR LA TAILLE

        // ==============================================================

        // Toute police supérieure à 18.0 points est un filigrane (sauf si le bouclier l'a sauvée juste au-dessus)

        if (word.Letters.Count > 0 && word.Letters.Max(l => l.PointSize) > 18.0)

        {

            return true;

        }

        // ==============================================================

        // 3. FILTRAGE PAR LE TEXTE EXACT (Mots entiers et fragments)

        // ==============================================================

        // Morceaux clairs de SPECIMEN impossibles à confondre avec de vrais mots

        if (text.Contains("SPECIMEN") ||

            text.Contains("SPECIME") ||

            text.Contains("SPECIM") ||

            text.Contains("PECIMEN") ||

            text.Contains("ECIMEN") ||

            text.Contains("CIMEN") ||

            text == "SPECI" ||

            text == "IMEN" ||

            text == "TOTEIN" ||

            text == "TEST")

        {

            return true;

        }

        // Codes du type Q0, Q00, Q000, D000, P000, A000...

        // Remplace l'ancien "Contains" qui était trop dangereux.

        if (WatermarkCodeRegex().IsMatch(text))

        {

            return true;

        }

        return false;

    }



    public string NormalizePdfText(string input)

    {

        if (string.IsNullOrWhiteSpace(input))

            return string.Empty;



        // Remplacement ultra-rapide via la Regex générée à la compilation

        string flatText = WhitespaceRegex().Replace(input, " ");



        // Chaînage propre pour plus de lisibilité

        flatText = flatText

            .Replace(". ", ".\n")

            .Replace("? ", "?\n")

            .Replace("! ", "!\n")

            .Replace(": ", ":\n")

            .Replace("•", "\n• ")

            .Replace(" o ", "\n o ");



        // OPTIMISATION MAJEURE :

        // L'utilisation combinée de RemoveEmptyEntries et TrimEntries remplace totalement LINQ.

        // C'est beaucoup plus rapide et ça évite de créer plusieurs tableaux intermédiaires en mémoire.

        var lines = flatText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);



        return string.Join("\n", lines);

    }

}

 