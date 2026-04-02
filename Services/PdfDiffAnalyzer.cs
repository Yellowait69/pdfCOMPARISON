using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using UglyToad.PdfPig.Content;

namespace PDFComparison.Services;

public partial class PdfDiffAnalyzer
{
    // Expressions régulières ultra-rapides pour l'analyse sémantique (Étape C)
    [GeneratedRegex(@"\b\d{1,2}[./-]\d{1,2}[./-]\d{2,4}\b")]
    private static partial Regex DateRegex();

    // Amélioration de la Regex pour gérer "0,00", "1.000,00", et les pourcentages "0,00%"
    [GeneratedRegex(@"^\d{1,3}([.,]\d{3})*([.,]\d+)?%?$")]
    private static partial Regex NumRegex();

    [GeneratedRegex(@"^[A-Z0-9-]{5,}$")]
    private static partial Regex CodeRegex();

    // Dictionnaire des mots de liaison à ne jamais relier isolément (Bouclier Anti-Gruyère)
    private static readonly string[] StopWords = {
       "le", "la", "les", "un", "une", "des", "de", "du", "et", "ou", "a", "à", "au", "aux", "en", "par", "sur", "pour", "avec", "dans", "se", "ce", "ces", "son", "sa", "ses", // FR
       "der", "die", "das", "den", "dem", "ein", "eine", "einer", "einem", "einen", "und", "oder", "von", "zu", "in", "im", "am", "auf", "für", "mit", // DE
       "het", "een", "of", "te", "van", "naar", "voor", "door", "op", "aan", "deze", "eur" // NL + Ajout de EUR pour le bloquer en tant que mot isolé
   };

    public DiffAnalysisResult AnalyzeDifferences(DocumentPair pair, string cleanSource, string cleanTarget, IReadOnlyList<PdfWordInfo> sourceWords, IReadOnlyList<PdfWordInfo> targetWords)
    {
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

        // ====================================================================
        // 1. Analyse Ligne par Ligne (Pour le résumé textuel du Dashboard)
        // ====================================================================
        var diffLines = diffBuilder.BuildDiffModel(CleanLineForDiff(cleanSource), CleanLineForDiff(cleanTarget));

        var sumDel = new Dictionary<string, int>();
        var sumIns = new Dictionary<string, int>();

        for (int i = 0; i < diffLines.NewText.Lines.Count; i++)
        {
            if (diffLines.OldText.Lines[i].Type == ChangeType.Deleted)
            {
                string t = diffLines.OldText.Lines[i].Text.Trim();
                if (t.Length > 0) sumDel[t] = sumDel.GetValueOrDefault(t) + 1;
            }

            if (diffLines.NewText.Lines[i].Type == ChangeType.Inserted)
            {
                string t = diffLines.NewText.Lines[i].Text.Trim();
                if (t.Length > 0) sumIns[t] = sumIns.GetValueOrDefault(t) + 1;
            }
        }

        var skipDel = new Dictionary<string, int>();
        var skipIns = new Dictionary<string, int>();

        foreach (var kvp in sumDel)
        {
            if (sumIns.TryGetValue(kvp.Key, out int insC))
            {
                int moves = Math.Min(kvp.Value, insC);
                skipDel[kvp.Key] = moves;
                skipIns[kvp.Key] = moves;
            }
        }

        for (int i = 0; i < diffLines.NewText.Lines.Count; i++)
        {
            var newLine = diffLines.NewText.Lines[i];
            var oldLine = diffLines.OldText.Lines[i];

            if (oldLine.Type == ChangeType.Deleted)
            {
                string txt = oldLine.Text.Trim();
                if (skipDel.TryGetValue(txt, out int moves) && moves > 0)
                {
                    skipDel[txt] = moves - 1;
                    continue;
                }
            }
            else if (newLine.Type == ChangeType.Inserted)
            {
                string txt = newLine.Text.Trim();
                if (skipIns.TryGetValue(txt, out int moves) && moves > 0)
                {
                    skipIns[txt] = moves - 1;
                    continue;
                }
            }

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

        // ====================================================================
        // 2. MOTEUR DE SURBRILLANCE VISUELLE "SMART MATCHER"
        // Remplace totalement DiffPlex pour éliminer les hallucinations de mots.
        // ====================================================================
        var sourceLinesList = GroupIntoLines(sourceWords);
        var targetLinesList = GroupIntoLines(targetWords);

        string sourceDiffText = string.Join('\n', sourceLinesList.Select(l => string.Join(' ', l.Select(w => w.CleanText))));
        string targetDiffText = string.Join('\n', targetLinesList.Select(l => string.Join(' ', l.Select(w => w.CleanText))));

        var diffLinesModel = diffBuilder.BuildDiffModel(sourceDiffText, targetDiffText);

        var globalDeletes = new List<(string CleanText, List<LetterLoc> Letters)>();
        var globalInserts = new List<(string CleanText, List<LetterLoc> Letters)>();

        int sLineIdx = 0;
        int tLineIdx = 0;

        // Collecte de tous les mots contenus dans les lignes modifiées/ajoutées/supprimées
        for (int i = 0; i < diffLinesModel.NewText.Lines.Count; i++)
        {
            var oldLineDiff = diffLinesModel.OldText.Lines[i];
            var newLineDiff = diffLinesModel.NewText.Lines[i];

            bool hasS = oldLineDiff.Type != ChangeType.Imaginary && sLineIdx < sourceLinesList.Count;
            bool hasT = newLineDiff.Type != ChangeType.Imaginary && tLineIdx < targetLinesList.Count;

            var sLine = hasS ? sourceLinesList[sLineIdx++] : null;
            var tLine = hasT ? targetLinesList[tLineIdx++] : null;

            if (oldLineDiff.Type == ChangeType.Deleted || oldLineDiff.Type == ChangeType.Modified)
            {
                if (hasS) globalDeletes.AddRange(sLine!);
            }

            if (newLineDiff.Type == ChangeType.Inserted || newLineDiff.Type == ChangeType.Modified)
            {
                if (hasT) globalInserts.AddRange(tLine!);
            }
        }

        bool[] matchedOld = new bool[globalDeletes.Count];
        bool[] matchedNew = new bool[globalInserts.Count];

        // --- ÉTAPE A : N-Gram Matcher (Verrouille les blocs déplacés, ex: Sauts de page) ---
        int minSequenceLength = 2;

        for (int i = 0; i <= globalDeletes.Count - minSequenceLength; i++)
        {
            if (matchedOld[i]) continue;

            int bestJ = -1;
            int maxLen = 0;

            for (int j = 0; j <= globalInserts.Count - minSequenceLength; j++)
            {
                if (matchedNew[j]) continue;

                int len = 0;
                while (i + len < globalDeletes.Count &&
                       j + len < globalInserts.Count &&
                       !matchedOld[i + len] &&
                       !matchedNew[j + len] &&
                       globalDeletes[i + len].CleanText == globalInserts[j + len].CleanText)
                {
                    len++;
                }

                // Sécurité "Anti-Tableau"
                if (len > maxLen)
                {
                    bool isMeaningfulSequence = false;
                    for (int k = 0; k < len; k++)
                    {
                        string word = globalDeletes[i + k].CleanText;
                        if (word.Length > 2 && !NumRegex().IsMatch(word))
                        {
                            isMeaningfulSequence = true;
                            break;
                        }
                    }

                    if (isMeaningfulSequence)
                    {
                        maxLen = len;
                        bestJ = j;
                    }
                }
            }

            if (maxLen >= minSequenceLength)
            {
                for (int k = 0; k < maxLen; k++)
                {
                    matchedOld[i + k] = true;
                    matchedNew[bestJ + k] = true;
                }
                i += maxLen - 1;
            }
        }

        // --- ÉTAPE B : Matcher de Mots Isolés Exacts (Bouclier Anti-Gruyère renforcé) ---
        for (int i = 0; i < globalDeletes.Count; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;

            // CORRECTION : Tous les nombres, dates et mots courts (<=4) sont désormais "volatils"
            bool isVolatile = StopWords.Contains(oldWord) || oldWord.Length <= 4 || NumRegex().IsMatch(oldWord) || DateRegex().IsMatch(oldWord);

            for (int j = 0; j < globalInserts.Count; j++)
            {
                if (matchedNew[j]) continue;

                if (oldWord == globalInserts[j].CleanText)
                {
                    // Si le mot est volatil (ex: "0,00" ou "EUR"), il DOIT se trouver
                    // dans la même région physique (max 100 points d'écart).
                    if (isVolatile)
                    {
                        var oldLocList = globalDeletes[i].Letters;
                        var newLocList = globalInserts[j].Letters;

                        bool isLocallyClose = oldLocList.Count > 0 && newLocList.Count > 0 &&
                                              oldLocList[0].PageNumber == newLocList[0].PageNumber &&
                                              Math.Abs(oldLocList[0].BaselineY - newLocList[0].BaselineY) < 100.0m;

                        if (!isLocallyClose) continue; // Trop éloigné -> aucune liaison !
                    }

                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    break;
                }
            }
        }

        // --- ÉTAPE C : Matcher Sémantique (Verrouille en JAUNE les dates/chiffres modifiés) ---
        for (int i = 0; i < globalDeletes.Count; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;
            bool isVolatile = oldWord.Length <= 4 || NumRegex().IsMatch(oldWord) || DateRegex().IsMatch(oldWord);

            for (int j = 0; j < globalInserts.Count; j++)
            {
                if (matchedNew[j]) continue;
                string newWord = globalInserts[j].CleanText;

                if (AreConceptuallySimilar(oldWord, newWord))
                {
                    // Appliquer le bouclier géographique ici aussi !
                    if (isVolatile)
                    {
                        var oldLocList = globalDeletes[i].Letters;
                        var newLocList = globalInserts[j].Letters;

                        bool isLocallyClose = oldLocList.Count > 0 && newLocList.Count > 0 &&
                                              oldLocList[0].PageNumber == newLocList[0].PageNumber &&
                                              Math.Abs(oldLocList[0].BaselineY - newLocList[0].BaselineY) < 100.0m;

                        if (!isLocallyClose) continue;
                    }

                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    result.Highlights.SourceYellow.AddRange(globalDeletes[i].Letters);
                    result.Highlights.TargetYellow.AddRange(globalInserts[j].Letters);
                    break;
                }
            }
        }

        // --- ÉTAPE D : Reliquat absolu (Verrouille en ROUGE et VERT les vrais changements uniques) ---
        for (int i = 0; i < globalDeletes.Count; i++)
        {
            if (!matchedOld[i])
            {
                result.Highlights.SourceRed.AddRange(globalDeletes[i].Letters);
            }
        }

        for (int j = 0; j < globalInserts.Count; j++)
        {
            if (!matchedNew[j])
            {
                result.Highlights.TargetRed.AddRange(globalInserts[j].Letters);
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

    private List<List<(string CleanText, List<LetterLoc> Letters)>> GroupIntoLines(IReadOnlyList<PdfWordInfo> words)
    {
        var list = new List<(string CleanText, List<LetterLoc> Letters)>();

        foreach (var word in words)
        {
            string cleanText = CleanWord(word.Text);
            if (string.IsNullOrEmpty(cleanText)) continue;

            var locs = new List<LetterLoc>();

            foreach (var letter in word.Letters)
            {
                string cleanedGlyph = CleanWord(letter.Value);
                if (string.IsNullOrEmpty(cleanedGlyph)) continue;

                var loc = new LetterLoc(letter.GlyphRectangle, word.PageNumber, (decimal)letter.Location.Y, (decimal)letter.PointSize);

                if (locs.Count > 0)
                {
                    var last = locs.Last();
                    if (Math.Abs(last.BaselineY - loc.BaselineY) < 1.0m &&
                        Math.Abs((decimal)last.BoundingBox.BottomLeft.X - (decimal)loc.BoundingBox.BottomLeft.X) < 1.0m)
                    {
                        continue;
                    }
                }

                locs.Add(loc);
            }

            if (locs.Count > 0)
            {
                list.Add((cleanText, locs));
            }
        }

        var linesList = new List<List<(string CleanText, List<LetterLoc> Letters)>>();
        var wordsByPage = list.GroupBy(w => w.Letters.First().PageNumber).OrderBy(g => g.Key);

        foreach (var page in wordsByPage)
        {
            var pageWords = page.OrderByDescending(w => w.Letters.First().BaselineY).ToList();
            var lines = new List<List<(string CleanText, List<LetterLoc> Letters)>>();

            foreach (var word in pageWords)
            {
                decimal wordY = word.Letters.First().BaselineY;
                var currentLine = lines.FirstOrDefault(l => Math.Abs(l.First().Letters.First().BaselineY - wordY) < 5.0m);

                if (currentLine == null)
                {
                    currentLine = new List<(string CleanText, List<LetterLoc> Letters)>();
                    lines.Add(currentLine);
                }

                currentLine.Add(word);
            }

            foreach (var line in lines.OrderByDescending(l => l.First().Letters.First().BaselineY))
            {
                linesList.Add(line.OrderBy(w => w.Letters.First().BoundingBox.BottomLeft.X).ToList());
            }
        }

        return linesList;
    }

    private string CleanLineForDiff(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        return input
            .Replace("\u00A0", " ")
            .Replace("\u00AD", "")
            .Replace("–", "-").Replace("—", "-").Replace("−", "-")
            .Replace("’", "'").Replace("‘", "'").Replace("´", "'").Replace("`", "'")
            .Replace("“", "\"").Replace("”", "\"").Replace("«", "\"").Replace("»", "\"")
            .Normalize(System.Text.NormalizationForm.FormKC);
    }

    private string CleanWord(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var cleaned = input
            .Replace("\u00A0", "")
            .Replace("\u200B", "")
            .Replace("\u200C", "")
            .Replace("\u200D", "")
            .Replace("\uFEFF", "")
            .Replace("\u00AD", "")
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace("\t", "")
            .Replace(" ", "")
            .Replace(",", ".")
            .Replace("–", "-").Replace("—", "-").Replace("−", "-")
            .Replace("’", "'").Replace("‘", "'").Replace("´", "'").Replace("`", "'")
            .Replace("“", "\"").Replace("”", "\"").Replace("«", "\"").Replace("»", "\"")
            .Normalize(System.Text.NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Trim();

        return new string(cleaned.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
    }

    // ==============================================================
    // OUTILS D'ANALYSE SÉMANTIQUE (Étape C)
    // ==============================================================

    private bool AreConceptuallySimilar(string a, string b)
    {
        // Si ce sont deux dates (ou deux nombres) détectées par regex, on valide direct
        if (DateRegex().IsMatch(a) && DateRegex().IsMatch(b)) return true;
        if (NumRegex().IsMatch(a) && NumRegex().IsMatch(b)) return true;
        if (CodeRegex().IsMatch(a) && CodeRegex().IsMatch(b)) return true;

        // NOUVELLE RÈGLE : Si l'un des mots contient l'intégralité de l'autre + des lettres
        // (ex: "malade" et "maladeeee"), on valide la modification (surbriallance Orange/Jaune).
        // On limite aux mots de 4 lettres minimum pour éviter de lier "le" avec "lettre".
        if (a.Length >= 4 && b.Length >= 4)
        {
            if (b.Contains(a) || a.Contains(b))
            {
                return true;
            }
        }

        // Règle existante (Tolérance aux fautes de frappe mineures via l'algorithme de Levenshtein)
        if (a.Length >= 4 && b.Length >= 4 && Math.Abs(a.Length - b.Length) <= 2)
        {
            int distance = CalculateLevenshtein(a, b);
            int allowed = Math.Max(1, Math.Min(a.Length, b.Length) / 4);
            if (distance <= allowed) return true;
        }

        return false;
    }

    private int CalculateLevenshtein(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;
        int[,] d = new int[n + 1, m + 1];

        if (n == 0) return m;
        if (m == 0) return n;

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;

                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }
}