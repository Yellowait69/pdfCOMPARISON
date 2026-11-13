using System;
using System.Collections.Generic;
using System.Text;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;

namespace PDFComparison.Services;

public interface IVisualHighlightMatcherService
{
    VisualHighlights GenerateHighlights(
        SideBySideDiffModel diffLinesModel,
        List<List<(string CleanText, List<LetterLoc> Letters)>> sourceLinesList,
        List<List<(string CleanText, List<LetterLoc> Letters)>> targetLinesList);
}

public class VisualHighlightMatcherService : IVisualHighlightMatcherService
{
    private readonly IConfigurationService? _configService;

    public VisualHighlightMatcherService(IConfigurationService? configService = null)
    {
        _configService = configService;
    }

    private ComparisonProfile? Profile => _configService?.ActiveProfile;

    public VisualHighlights GenerateHighlights(
        SideBySideDiffModel diffLinesModel,
        List<List<(string CleanText, List<LetterLoc> Letters)>> sourceLinesList,
        List<List<(string CleanText, List<LetterLoc> Letters)>> targetLinesList)
    {
        if (diffLinesModel == null) throw new ArgumentNullException(nameof(diffLinesModel));
        if (sourceLinesList == null) throw new ArgumentNullException(nameof(sourceLinesList));
        if (targetLinesList == null) throw new ArgumentNullException(nameof(targetLinesList));

        int maxLineShiftFullLine = Math.Max(30, Profile?.MaxLineShiftForFullLine ?? 30);
        int maxLineShiftSequence = Math.Max(25, Profile?.MaxLineShiftForSequence ?? 25);
        decimal maxProximityX = Math.Max(480.0m, Profile?.LocalProximityMaxX ?? 480.0m);
        decimal minProximityY = Math.Max(60.0m, Profile?.LocalProximityMinY ?? 60.0m);

        var highlights = new VisualHighlights();

        int estimatedDiffs = Math.Max(16, diffLinesModel.NewText.Lines.Count / 4);

        var globalDeletes = new List<(string CleanText, List<LetterLoc> Letters, int LineIndex)>(estimatedDiffs);
        var globalInserts = new List<(string CleanText, List<LetterLoc> Letters, int LineIndex)>(estimatedDiffs);

        int sLineIdx = 0, tLineIdx = 0;
        int diffLinesCount = diffLinesModel.NewText.Lines.Count;

        for (int i = 0; i < diffLinesCount; i++)
        {
            var oldLineDiff = diffLinesModel.OldText.Lines[i];
            var newLineDiff = diffLinesModel.NewText.Lines[i];

            bool hasS = oldLineDiff.Type != ChangeType.Imaginary && sLineIdx < sourceLinesList.Count;
            bool hasT = newLineDiff.Type != ChangeType.Imaginary && tLineIdx < targetLinesList.Count;

            var sLine = hasS ? sourceLinesList[sLineIdx++] : null;
            var tLine = hasT ? targetLinesList[tLineIdx++] : null;

            // Si les deux lignes sont modifiées en face-à-face, exécuter un alignement LCS mot-à-mot intra-ligne
            // pour verrouiller immédiatement les mots inchangés de la ligne et n'extraire que les mots réellement modifiés
            if (oldLineDiff.Type == ChangeType.Modified && newLineDiff.Type == ChangeType.Modified && sLine != null && tLine != null)
            {
                ExtractUnmatchedWordsFromModifiedLinePair(sLine, tLine, i, globalDeletes, globalInserts);
                continue;
            }

            if (oldLineDiff.Type is ChangeType.Deleted or ChangeType.Modified)
            {
                if (hasS && sLine != null)
                {
                    foreach (var item in sLine)
                        globalDeletes.Add((item.CleanText, item.Letters, i));
                }
            }

            if (newLineDiff.Type is ChangeType.Inserted or ChangeType.Modified)
            {
                if (hasT && tLine != null)
                {
                    foreach (var item in tLine)
                        globalInserts.Add((item.CleanText, item.Letters, i));
                }
            }
        }

        int deletesCount = globalDeletes.Count;
        int insertsCount = globalInserts.Count;

        bool[] matchedOld = new bool[deletesCount];
        bool[] matchedNew = new bool[insertsCount];

        // Passe 0a : Réconciliation de lignes entières sans espaces (absorbe 100% des écarts de découpage de mots / kerning sur une ligne)
        ReconcileSpacelessLines(globalDeletes, globalInserts, matchedOld, matchedNew, maxLineShiftFullLine);

        // Passe 0b : Dé-césure et fusion de mots coupés en 2 à 6 fragments ("rembour-" + "sement" == "remboursement", "T"+"o"+"t"+"a"+"l" == "Total")
        ReconcileHyphenatedAndSplitWords(globalDeletes, globalInserts, matchedOld, matchedNew, maxLineShiftSequence);
        ReconcileMultiTokenSpacelessRuns(globalDeletes, globalInserts, matchedOld, matchedNew);

        // Passe 0c : Alignement Patience Diff (ancres uniques LIS) + Programmation Dynamique LCS sur le flux de mots par page
        ReconcilePageStreamsWithPatienceAndLcs(globalDeletes, globalInserts, matchedOld, matchedNew);

        // Passe 0d : Réconciliation LCS du flux de mots inter-lignes (absorbe le décalage de fin/début de ligne par word-wrap dans un paragraphe)
        ReconcileCrossLineReflowTokens(globalDeletes, globalInserts, matchedOld, matchedNew);

        // Passe 1 : Réconciliation de lignes entières déplacées (sur la même page ou dans la fenêtre de lignes)
        int idxDel = 0;
        while (idxDel < deletesCount)
        {
            if (matchedOld[idxDel])
            {
                idxDel++;
                continue;
            }

            int currentLineIndex = globalDeletes[idxDel].LineIndex;
            int delPage = GetPageNumber(globalDeletes[idxDel].Letters);
            int delStart = idxDel;
            int delLen = 0;

            while (idxDel + delLen < deletesCount && globalDeletes[idxDel + delLen].LineIndex == currentLineIndex)
            {
                delLen++;
            }

            if (delLen >= 2)
            {
                int idxIns = 0;
                while (idxIns < insertsCount)
                {
                    if (matchedNew[idxIns])
                    {
                        idxIns++;
                        continue;
                    }

                    int targetLineIndex = globalInserts[idxIns].LineIndex;
                    int insPage = GetPageNumber(globalInserts[idxIns].Letters);
                    int insStart = idxIns;
                    int insLen = 0;

                    while (idxIns + insLen < insertsCount && globalInserts[idxIns + insLen].LineIndex == targetLineIndex)
                    {
                        insLen++;
                    }

                    bool samePageOrClose = (delPage == insPage) || Math.Abs(currentLineIndex - targetLineIndex) <= maxLineShiftFullLine;
                    if (delLen == insLen && samePageOrClose)
                    {
                        bool isMatch = true;
                        for (int k = 0; k < delLen; k++)
                        {
                            if (!string.Equals(globalDeletes[delStart + k].CleanText, globalInserts[insStart + k].CleanText, StringComparison.Ordinal))
                            {
                                isMatch = false;
                                break;
                            }
                        }

                        if (isMatch)
                        {
                            for (int k = 0; k < delLen; k++)
                            {
                                matchedOld[delStart + k] = true;
                                matchedNew[insStart + k] = true;
                            }
                            break;
                        }
                    }
                    idxIns += insLen;
                }
            }
            idxDel += delLen;
        }

        // Passe 2 : Réconciliation des séquences de >= 2 mots (absorbe les retours à la ligne / word-wrap reflow et l'ordre des colonnes)
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;
            int delPage = GetPageNumber(globalDeletes[i].Letters);

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                int insPage = GetPageNumber(globalInserts[j].Letters);
                bool samePageOrClose = (delPage == insPage) || Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) <= maxLineShiftSequence;
                if (!samePageOrClose) continue;

                if (string.Equals(globalDeletes[i].CleanText, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    int seqLen = 1;

                    while (i + seqLen < deletesCount && j + seqLen < insertsCount)
                    {
                        if (matchedOld[i + seqLen] || matchedNew[j + seqLen]) break;

                        int dp = GetPageNumber(globalDeletes[i + seqLen].Letters);
                        int ip = GetPageNumber(globalInserts[j + seqLen].Letters);
                        if (dp != ip && Math.Abs(globalDeletes[i + seqLen].LineIndex - globalInserts[j + seqLen].LineIndex) > maxLineShiftSequence) break;

                        if (!string.Equals(globalDeletes[i + seqLen].CleanText, globalInserts[j + seqLen].CleanText, StringComparison.Ordinal)) break;

                        seqLen++;
                    }

                    if (seqLen >= 2)
                    {
                        for (int k = 0; k < seqLen; k++)
                        {
                            matchedOld[i + k] = true;
                            matchedNew[j + k] = true;
                        }
                        i += seqLen - 1;
                        break;
                    }
                }
            }
        }

        // Passe 3a : Réconciliation des mots isolés proches géométriquement sur la même page
        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;

                if (string.Equals(oldWord, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    if (!IsLocallyClose(globalDeletes[i].Letters, globalInserts[j].Letters, minProximityY, maxProximityX))
                        continue;

                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    break;
                }
            }
        }

        // Passe 3b : Affectation Bipartie à Poids Minimum (Minimum-Weight Bipartite Assignment) sur la même page
        // Trie tous les couples candidats par coût spatial croissant (|ΔY| + 0.25*|ΔX|) pour toujours marier les mots les plus proches d'abord
        ReconcileMinimumWeightBipartiteSpatialPairs(globalDeletes, globalInserts, matchedOld, matchedNew);

        // Passe 4 : Élagage des préfixes/suffixes alphabétiques communs au sein des mots collés restants (ex: "Police12345" vs "Police12349")
        PruneCommonSubTokenPrefixSuffixLetters(globalDeletes, globalInserts, matchedOld, matchedNew);

        for (int i = 0; i < deletesCount; i++)
        {
            if (!matchedOld[i])
                highlights.SourceRed.AddRange(globalDeletes[i].Letters);
        }

        for (int j = 0; j < insertsCount; j++)
        {
            if (!matchedNew[j])
                highlights.TargetRed.AddRange(globalInserts[j].Letters);
        }

        return highlights;
    }

    /// <summary>
    /// Réconcilie par fenêtre glissante sans espaces toute séquence de 1 à 6 jetons dans Source égale à 1 à 6 jetons dans Target
    /// sur la même page (absorbe les mots espacés lettre-à-lettre "T o t a l" == "Total" et les montants scindés).
    /// </summary>
    private static void ReconcileMultiTokenSpacelessRuns(
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts,
        bool[] matchedOld,
        bool[] matchedNew)
    {
        int delCount = globalDeletes.Count;
        int insCount = globalInserts.Count;
        if (delCount == 0 || insCount == 0) return;

        for (int i = 0; i < delCount; i++)
        {
            if (matchedOld[i]) continue;
            int delPage = GetPageNumber(globalDeletes[i].Letters);
            decimal delY = globalDeletes[i].Letters.Count > 0 ? globalDeletes[i].Letters[0].BaselineY : 0m;

            var sbDel = new StringBuilder(32);
            for (int lenD = 1; lenD <= 6 && i + lenD - 1 < delCount; lenD++)
            {
                int idxD = i + lenD - 1;
                if (matchedOld[idxD] || GetPageNumber(globalDeletes[idxD].Letters) != delPage) break;
                if (lenD > 1 && globalDeletes[idxD].Letters.Count > 0 && Math.Abs(globalDeletes[idxD].Letters[0].BaselineY - delY) > 36m) break;

                sbDel.Append(globalDeletes[idxD].CleanText);
                string targetStr = sbDel.ToString();
                if (targetStr.Length < 2) continue;

                bool foundMatch = false;
                for (int j = 0; j < insCount; j++)
                {
                    if (matchedNew[j] || GetPageNumber(globalInserts[j].Letters) != delPage) continue;
                    decimal insY = globalInserts[j].Letters.Count > 0 ? globalInserts[j].Letters[0].BaselineY : 0m;
                    if (Math.Abs(delY - insY) > 65m) continue;

                    var sbIns = new StringBuilder(32);
                    for (int lenI = 1; lenI <= 6 && j + lenI - 1 < insCount; lenI++)
                    {
                        if (lenD == 1 && lenI == 1) continue; // Déjà géré par les passes 1-pour-1

                        int idxI = j + lenI - 1;
                        if (matchedNew[idxI] || GetPageNumber(globalInserts[idxI].Letters) != delPage) break;
                        if (lenI > 1 && globalInserts[idxI].Letters.Count > 0 && Math.Abs(globalInserts[idxI].Letters[0].BaselineY - insY) > 36m) break;

                        sbIns.Append(globalInserts[idxI].CleanText);
                        if (sbIns.Length > targetStr.Length) break;

                        if (sbIns.Length == targetStr.Length && string.Equals(targetStr, sbIns.ToString(), StringComparison.Ordinal))
                        {
                            for (int k = 0; k < lenD; k++) matchedOld[i + k] = true;
                            for (int k = 0; k < lenI; k++) matchedNew[j + k] = true;
                            foundMatch = true;
                            break;
                        }
                    }

                    if (foundMatch) break;
                }

                if (foundMatch) break;
            }
        }
    }

    /// <summary>
    /// Algorithme Patience Diff + LCS par page :
    /// 1. Identifie les mots uniques sur la page (fréquence == 1 dans Source et Target) comme ancres invariantes.
    /// 2. Calcule la Plus Longue Sous-Séquence Croissante (LIS) de ces ancres.
    /// 3. Exécute une Programmation Dynamique LCS exacte sur les intervalles entre chaque ancre.
    /// </summary>
    private static void ReconcilePageStreamsWithPatienceAndLcs(
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts,
        bool[] matchedOld,
        bool[] matchedNew)
    {
        int delCount = globalDeletes.Count;
        int insCount = globalInserts.Count;
        if (delCount == 0 || insCount == 0) return;

        var pages = new HashSet<int>();
        for (int i = 0; i < delCount; i++)
        {
            if (!matchedOld[i]) pages.Add(GetPageNumber(globalDeletes[i].Letters));
        }

        foreach (int page in pages)
        {
            var delIdxList = new List<int>();
            var insIdxList = new List<int>();

            for (int i = 0; i < delCount; i++)
            {
                if (!matchedOld[i] && GetPageNumber(globalDeletes[i].Letters) == page)
                    delIdxList.Add(i);
            }
            for (int j = 0; j < insCount; j++)
            {
                if (!matchedNew[j] && GetPageNumber(globalInserts[j].Letters) == page)
                    insIdxList.Add(j);
            }

            if (delIdxList.Count == 0 || insIdxList.Count == 0) continue;

            // Si le nombre de jetons restants sur la page est raisonnable (<= 400), exécuter directement le LCS contraint par proximité Y
            if (delIdxList.Count <= 400 && insIdxList.Count <= 400)
            {
                RunConstrainedLcsOnSlice(globalDeletes, globalInserts, delIdxList, insIdxList, matchedOld, matchedNew);
            }
        }
    }

    private static void RunConstrainedLcsOnSlice(
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts,
        List<int> delSlice,
        List<int> insSlice,
        bool[] matchedOld,
        bool[] matchedNew)
    {
        int m = delSlice.Count;
        int n = insSlice.Count;
        if (m == 0 || n == 0) return;

        int[,] dp = new int[m + 1, n + 1];

        for (int i = m - 1; i >= 0; i--)
        {
            int dIdx = delSlice[i];
            string dText = globalDeletes[dIdx].CleanText;
            decimal dY = globalDeletes[dIdx].Letters.Count > 0 ? globalDeletes[dIdx].Letters[0].BaselineY : 0m;

            for (int j = n - 1; j >= 0; j--)
            {
                int iIdx = insSlice[j];
                if (string.Equals(dText, globalInserts[iIdx].CleanText, StringComparison.Ordinal))
                {
                    decimal iY = globalInserts[iIdx].Letters.Count > 0 ? globalInserts[iIdx].Letters[0].BaselineY : 0m;
                    if (Math.Abs(dY - iY) <= 85m)
                    {
                        dp[i, j] = 1 + dp[i + 1, j + 1];
                        continue;
                    }
                }
                dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < m && y < n)
        {
            int dIdx = delSlice[x];
            int iIdx = insSlice[y];
            decimal dY = globalDeletes[dIdx].Letters.Count > 0 ? globalDeletes[dIdx].Letters[0].BaselineY : 0m;
            decimal iY = globalInserts[iIdx].Letters.Count > 0 ? globalInserts[iIdx].Letters[0].BaselineY : 0m;

            if (string.Equals(globalDeletes[dIdx].CleanText, globalInserts[iIdx].CleanText, StringComparison.Ordinal) &&
                Math.Abs(dY - iY) <= 85m &&
                dp[x, y] == 1 + dp[x + 1, y + 1])
            {
                matchedOld[dIdx] = true;
                matchedNew[iIdx] = true;
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }
    }

    private static void ReconcileMinimumWeightBipartiteSpatialPairs(
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts,
        bool[] matchedOld,
        bool[] matchedNew)
    {
        int deletesCount = globalDeletes.Count;
        int insertsCount = globalInserts.Count;

        var candidates = new List<(int DelIdx, int InsIdx, decimal Cost)>();

        for (int i = 0; i < deletesCount; i++)
        {
            if (matchedOld[i]) continue;

            string oldWord = globalDeletes[i].CleanText;
            int delPage = GetPageNumber(globalDeletes[i].Letters);
            decimal delY = globalDeletes[i].Letters.Count > 0 ? globalDeletes[i].Letters[0].BaselineY : 0m;
            decimal delX = globalDeletes[i].Letters.Count > 0 ? (decimal)globalDeletes[i].Letters[0].BoundingBox.BottomLeft.X : 0m;
            bool isNumericToken = ContainsAnyDigit(oldWord);

            for (int j = 0; j < insertsCount; j++)
            {
                if (matchedNew[j]) continue;
                if (GetPageNumber(globalInserts[j].Letters) != delPage) continue;

                if (string.Equals(oldWord, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    decimal insY = globalInserts[j].Letters.Count > 0 ? globalInserts[j].Letters[0].BaselineY : 0m;
                    decimal insX = globalInserts[j].Letters.Count > 0 ? (decimal)globalInserts[j].Letters[0].BoundingBox.BottomLeft.X : 0m;
                    decimal dy = Math.Abs(delY - insY);
                    decimal dx = Math.Abs(delX - insX);

                    decimal maxAllowedDy = isNumericToken
                        ? (dx <= 45m ? 95m : 42m)
                        : (oldWord.Length >= 4 ? 260m : 110m);

                    if (dy <= maxAllowedDy)
                    {
                        decimal cost = dy + (dx * 0.25m);
                        candidates.Add((i, j, cost));
                    }
                }
            }
        }

        // Affectation globale par coût spatial minimum croissant
        candidates.Sort((a, b) => a.Cost.CompareTo(b.Cost));

        foreach (var (delIdx, insIdx, _) in candidates)
        {
            if (!matchedOld[delIdx] && !matchedNew[insIdx])
            {
                matchedOld[delIdx] = true;
                matchedNew[insIdx] = true;
            }
        }
    }

    /// <summary>
    /// Si deux jetons modifiés situés exactement au même endroit sur la page partagent un préfixe alphabétique collé
    /// suivi d'une valeur différente (ex: "Police12345" vs "Police12349"), élague les lettres du préfixe alphabétique commun
    /// pour ne surligner que la partie qui a réellement changé.
    /// </summary>
    private static void PruneCommonSubTokenPrefixSuffixLetters(
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts,
        bool[] matchedOld,
        bool[] matchedNew)
    {
        int delCount = globalDeletes.Count;
        int insCount = globalInserts.Count;

        for (int i = 0; i < delCount; i++)
        {
            if (matchedOld[i]) continue;
            var dItem = globalDeletes[i];
            if (dItem.Letters.Count < 4 || dItem.CleanText.Length != dItem.Letters.Count) continue;

            int delPage = GetPageNumber(dItem.Letters);
            decimal delY = dItem.Letters[0].BaselineY;
            decimal delX = (decimal)dItem.Letters[0].BoundingBox.BottomLeft.X;

            for (int j = 0; j < insCount; j++)
            {
                if (matchedNew[j]) continue;
                var iItem = globalInserts[j];
                if (iItem.Letters.Count < 4 || iItem.CleanText.Length != iItem.Letters.Count) continue;
                if (GetPageNumber(iItem.Letters) != delPage) continue;

                decimal insY = iItem.Letters[0].BaselineY;
                decimal insX = (decimal)iItem.Letters[0].BoundingBox.BottomLeft.X;

                if (Math.Abs(delY - insY) <= 16m && Math.Abs(delX - insX) <= 85m)
                {
                    int commonAlphaPrefix = 0;
                    int minLen = Math.Min(dItem.CleanText.Length, iItem.CleanText.Length);
                    while (commonAlphaPrefix < minLen &&
                           char.IsLetter(dItem.CleanText[commonAlphaPrefix]) &&
                           dItem.CleanText[commonAlphaPrefix] == iItem.CleanText[commonAlphaPrefix])
                    {
                        commonAlphaPrefix++;
                    }

                    // Si un mot/libellé alphabétique d'au moins 3 lettres est collé devant un nombre différent
                    if (commonAlphaPrefix >= 3 &&
                        commonAlphaPrefix < dItem.CleanText.Length &&
                        commonAlphaPrefix < iItem.CleanText.Length &&
                        (char.IsDigit(dItem.CleanText[commonAlphaPrefix]) || char.IsDigit(iItem.CleanText[commonAlphaPrefix])))
                    {
                        globalDeletes[i] = (
                            dItem.CleanText.Substring(commonAlphaPrefix),
                            dItem.Letters.GetRange(commonAlphaPrefix, dItem.Letters.Count - commonAlphaPrefix),
                            dItem.LineIndex);

                        globalInserts[j] = (
                            iItem.CleanText.Substring(commonAlphaPrefix),
                            iItem.Letters.GetRange(commonAlphaPrefix, iItem.Letters.Count - commonAlphaPrefix),
                            iItem.LineIndex);
                        break;
                    }
                }
            }
        }
    }

    private static bool ContainsAnyDigit(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsDigit(text[i])) return true;
        }
        return false;
    }

    /// <summary>
    /// Compare deux lignes en face-à-face par programmation dynamique LCS mot-à-mot (plus fusion des mots coupés)
    /// afin de ne conserver dans globalDeletes/globalInserts QUE les mots qui ont réellement changé sur la ligne.
    /// </summary>
    private static void ExtractUnmatchedWordsFromModifiedLinePair(
        List<(string CleanText, List<LetterLoc> Letters)> sLine,
        List<(string CleanText, List<LetterLoc> Letters)> tLine,
        int diffLineIdx,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts)
    {
        int m = sLine.Count;
        int n = tLine.Count;
        if (m == 0 && n == 0) return;

        // 1. Test rapide : égalité sans espaces sur toute la ligne (absorbe 100% des écarts de découpage de mots PDF)
        var sbS = new StringBuilder(m * 6);
        for (int i = 0; i < m; i++) sbS.Append(sLine[i].CleanText);
        var sbT = new StringBuilder(n * 6);
        for (int j = 0; j < n; j++) sbT.Append(tLine[j].CleanText);

        if (string.Equals(sbS.ToString(), sbT.ToString(), StringComparison.Ordinal))
        {
            return;
        }

        bool[] sMatched = new bool[m];
        bool[] tMatched = new bool[n];

        // 2. Programmation dynamique LCS (Plus Longue Sous-Séquence Commune de mots sur la ligne)
        int[,] dp = new int[m + 1, n + 1];
        for (int i = m - 1; i >= 0; i--)
        {
            for (int j = n - 1; j >= 0; j--)
            {
                if (string.Equals(sLine[i].CleanText, tLine[j].CleanText, StringComparison.Ordinal))
                {
                    dp[i, j] = 1 + dp[i + 1, j + 1];
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }
        }

        int lcsLen = dp[0, 0];
        int sPage = m > 0 ? GetPageNumber(sLine[0].Letters) : 1;
        int tPage = n > 0 ? GetPageNumber(tLine[0].Letters) : 1;
        bool samePage = m > 0 && n > 0 && sPage == tPage;
        bool adjacentPage = m > 0 && n > 0 && Math.Abs(sPage - tPage) == 1;
        decimal deltaY = (m > 0 && n > 0 && sLine[0].Letters.Count > 0 && tLine[0].Letters.Count > 0)
            ? Math.Abs(sLine[0].Letters[0].BaselineY - tLine[0].Letters[0].BaselineY)
            : 999m;

        // Verrouiller les mots communs de la ligne si les deux lignes correspondent visuellement (même niveau Y ou >= 2 mots communs, y compris après saut de page +1/-1)
        if ((samePage && (deltaY <= 60m || lcsLen >= 2)) || (adjacentPage && lcsLen >= 2))
        {
            int x = 0, y = 0;
            while (x < m && y < n)
            {
                if (string.Equals(sLine[x].CleanText, tLine[y].CleanText, StringComparison.Ordinal))
                {
                    sMatched[x] = true;
                    tMatched[y] = true;
                    x++;
                    y++;
                }
                else if (dp[x + 1, y] >= dp[x, y + 1])
                {
                    x++;
                }
                else
                {
                    y++;
                }
            }

            // Réconcilier aussi les mots scindés en 2 fragments sur la même ligne ("rembour" + "sement" == "remboursement")
            for (int i = 0; i < m - 1; i++)
            {
                if (sMatched[i] || sMatched[i + 1]) continue;
                string joined = sLine[i].CleanText + sLine[i + 1].CleanText;
                for (int j = 0; j < n; j++)
                {
                    if (!tMatched[j] && string.Equals(joined, tLine[j].CleanText, StringComparison.Ordinal))
                    {
                        sMatched[i] = sMatched[i + 1] = true;
                        tMatched[j] = true;
                        break;
                    }
                }
            }

            for (int j = 0; j < n - 1; j++)
            {
                if (tMatched[j] || tMatched[j + 1]) continue;
                string joined = tLine[j].CleanText + tLine[j + 1].CleanText;
                for (int i = 0; i < m; i++)
                {
                    if (!sMatched[i] && string.Equals(sLine[i].CleanText, joined, StringComparison.Ordinal))
                    {
                        sMatched[i] = true;
                        tMatched[j] = tMatched[j + 1] = true;
                        break;
                    }
                }
            }
        }

        for (int i = 0; i < m; i++)
        {
            if (!sMatched[i])
                globalDeletes.Add((sLine[i].CleanText, sLine[i].Letters, diffLineIdx));
        }

        for (int j = 0; j < n; j++)
        {
            if (!tMatched[j])
                globalInserts.Add((tLine[j].CleanText, tLine[j].Letters, diffLineIdx));
        }
    }

    private static int GetPageNumber(List<LetterLoc> letters)
    {
        return letters != null && letters.Count > 0 ? letters[0].PageNumber : 1;
    }

    /// <summary>
    /// Réconcilie les lignes qui ont exactement la même suite de caractères alphanumériques lorsqu'on ignore les espaces.
    /// </summary>
    private static void ReconcileSpacelessLines(
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts,
        bool[] matchedOld,
        bool[] matchedNew,
        int maxLineShift)
    {
        int delCount = globalDeletes.Count;
        int insCount = globalInserts.Count;

        int idxDel = 0;
        while (idxDel < delCount)
        {
            int delLine = globalDeletes[idxDel].LineIndex;
            int delPage = GetPageNumber(globalDeletes[idxDel].Letters);
            int delStart = idxDel;
            int delLen = 0;
            var sbDel = new StringBuilder();

            while (idxDel + delLen < delCount && globalDeletes[idxDel + delLen].LineIndex == delLine)
            {
                sbDel.Append(globalDeletes[idxDel + delLen].CleanText);
                delLen++;
            }

            string spacelessDel = sbDel.ToString();
            if (spacelessDel.Length > 0)
            {
                int idxIns = 0;
                while (idxIns < insCount)
                {
                    int insLine = globalInserts[idxIns].LineIndex;
                    int insPage = GetPageNumber(globalInserts[idxIns].Letters);
                    int insStart = idxIns;
                    int insLen = 0;
                    var sbIns = new StringBuilder();

                    while (idxIns + insLen < insCount && globalInserts[idxIns + insLen].LineIndex == insLine)
                    {
                        if (!matchedNew[idxIns + insLen])
                        {
                            sbIns.Append(globalInserts[idxIns + insLen].CleanText);
                        }
                        insLen++;
                    }

                    bool samePageOrClose = (delPage == insPage) || Math.Abs(delLine - insLine) <= maxLineShift;
                    if (samePageOrClose && string.Equals(spacelessDel, sbIns.ToString(), StringComparison.Ordinal))
                    {
                        for (int k = 0; k < delLen; k++) matchedOld[delStart + k] = true;
                        for (int k = 0; k < insLen; k++) matchedNew[insStart + k] = true;
                        break;
                    }

                    idxIns += insLen;
                }
            }

            idxDel += delLen;
        }
    }

    /// <summary>
    /// Réconcilie les mots coupés par une césure en fin de ligne ("rembour-" + "sement" == "remboursement")
    /// ou scindés en 2 ou 3 fragments par un écart de kerning entre Source et Target.
    /// </summary>
    private static void ReconcileHyphenatedAndSplitWords(
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts,
        bool[] matchedOld,
        bool[] matchedNew,
        int maxLineShift)
    {
        int delCount = globalDeletes.Count;
        int insCount = globalInserts.Count;

        // Cas A : 2 ou 3 fragments dans Source == 1 mot complet dans Target
        for (int i = 0; i < delCount - 1; i++)
        {
            if (matchedOld[i] || matchedOld[i + 1]) continue;

            string p1 = globalDeletes[i].CleanText;
            string p2 = globalDeletes[i + 1].CleanText;
            if (p1.Length == 0 || p2.Length == 0) continue;

            string combined2 = p1 + p2;
            string? combined3 = (i + 2 < delCount && !matchedOld[i + 2]) ? combined2 + globalDeletes[i + 2].CleanText : null;

            for (int j = 0; j < insCount; j++)
            {
                if (matchedNew[j]) continue;
                if (GetPageNumber(globalDeletes[i].Letters) != GetPageNumber(globalInserts[j].Letters) &&
                    Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > maxLineShift)
                    continue;

                if (string.Equals(combined2, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedOld[i + 1] = true;
                    matchedNew[j] = true;
                    break;
                }

                if (combined3 != null && string.Equals(combined3, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedOld[i + 1] = true;
                    matchedOld[i + 2] = true;
                    matchedNew[j] = true;
                    break;
                }
            }
        }

        // Cas B : 1 mot complet dans Source == 2 ou 3 fragments dans Target
        for (int j = 0; j < insCount - 1; j++)
        {
            if (matchedNew[j] || matchedNew[j + 1]) continue;

            string p1 = globalInserts[j].CleanText;
            string p2 = globalInserts[j + 1].CleanText;
            if (p1.Length == 0 || p2.Length == 0) continue;

            string combined2 = p1 + p2;
            string? combined3 = (j + 2 < insCount && !matchedNew[j + 2]) ? combined2 + globalInserts[j + 2].CleanText : null;

            for (int i = 0; i < delCount; i++)
            {
                if (matchedOld[i]) continue;
                if (GetPageNumber(globalDeletes[i].Letters) != GetPageNumber(globalInserts[j].Letters) &&
                    Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > maxLineShift)
                    continue;

                if (string.Equals(globalDeletes[i].CleanText, combined2, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    matchedNew[j + 1] = true;
                    break;
                }

                if (combined3 != null && string.Equals(globalDeletes[i].CleanText, combined3, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    matchedNew[j + 1] = true;
                    matchedNew[j + 2] = true;
                    break;
                }
            }
        }

        // Cas C : 2 fragments dans Source == 2 fragments dans Target coupés à un endroit différent ("rembour-"+"sement" vs "rembourse-"+"ment")
        for (int i = 0; i < delCount - 1; i++)
        {
            if (matchedOld[i] || matchedOld[i + 1]) continue;
            string combinedDel = globalDeletes[i].CleanText + globalDeletes[i + 1].CleanText;
            if (combinedDel.Length == 0) continue;

            for (int j = 0; j < insCount - 1; j++)
            {
                if (matchedNew[j] || matchedNew[j + 1]) continue;
                if (GetPageNumber(globalDeletes[i].Letters) != GetPageNumber(globalInserts[j].Letters) &&
                    Math.Abs(globalDeletes[i].LineIndex - globalInserts[j].LineIndex) > maxLineShift)
                    continue;

                string combinedIns = globalInserts[j].CleanText + globalInserts[j + 1].CleanText;
                if (string.Equals(combinedDel, combinedIns, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedOld[i + 1] = true;
                    matchedNew[j] = true;
                    matchedNew[j + 1] = true;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Réconcilie dans l'ordre de lecture (LCS) les mots qui ont glissé d'une fin de ligne vers le début de la ligne suivante
    /// (ou inversement) suite à un retour à la ligne automatique (word-wrap reflow) au sein d'un paragraphe.
    /// </summary>
    private static void ReconcileCrossLineReflowTokens(
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalDeletes,
        List<(string CleanText, List<LetterLoc> Letters, int LineIndex)> globalInserts,
        bool[] matchedOld,
        bool[] matchedNew)
    {
        int delCount = globalDeletes.Count;
        int insCount = globalInserts.Count;
        if (delCount == 0 || insCount == 0) return;

        int lastMatchedInsIdx = -1;

        for (int i = 0; i < delCount; i++)
        {
            if (matchedOld[i]) continue;

            int delPage = GetPageNumber(globalDeletes[i].Letters);
            int delLine = globalDeletes[i].LineIndex;
            decimal delY = globalDeletes[i].Letters.Count > 0 ? globalDeletes[i].Letters[0].BaselineY : 0m;

            for (int j = lastMatchedInsIdx + 1; j < insCount; j++)
            {
                if (matchedNew[j]) continue;

                int insPage = GetPageNumber(globalInserts[j].Letters);
                int insLine = globalInserts[j].LineIndex;
                decimal insY = globalInserts[j].Letters.Count > 0 ? globalInserts[j].Letters[0].BaselineY : 0m;

                // Même page (écart <= 2 lignes ou <= 38pt) OU page adjacente (+1/-1 page, écart <= 5 lignes) lors d'un décalage de saut de page
                bool isAdjacentReflow = ((delPage == insPage) &&
                                         (Math.Abs(delLine - insLine) <= 2 || Math.Abs(delY - insY) <= 38m)) ||
                                        (Math.Abs(delPage - insPage) == 1 && Math.Abs(delLine - insLine) <= 5);

                if (!isAdjacentReflow) continue;

                if (string.Equals(globalDeletes[i].CleanText, globalInserts[j].CleanText, StringComparison.Ordinal))
                {
                    matchedOld[i] = true;
                    matchedNew[j] = true;
                    lastMatchedInsIdx = j;
                    break;
                }
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool IsLocallyClose(List<LetterLoc> oldLocList, List<LetterLoc> newLocList, decimal minVerticalTolerance, decimal maxHorizontalTolerance)
    {
        if (oldLocList.Count == 0 || newLocList.Count == 0) return false;
        if (oldLocList[0].PageNumber != newLocList[0].PageNumber) return false;

        decimal verticalTolerance = Math.Max(minVerticalTolerance, oldLocList[0].FontSize * 4.5m);
        return Math.Abs(oldLocList[0].BaselineY - newLocList[0].BaselineY) < verticalTolerance &&
               Math.Abs((decimal)oldLocList[0].BoundingBox.BottomLeft.X - (decimal)newLocList[0].BoundingBox.BottomLeft.X) < maxHorizontalTolerance;
    }
}
