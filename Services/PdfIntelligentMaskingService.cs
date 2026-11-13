using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig.Content;

namespace PDFComparison.Services;

public interface IPdfIntelligentMaskingService
{
    string MaskRepeatingTextElements(string text, IReadOnlySet<string>? headerDates = null);
    void MaskRepeatingWordElements(List<PdfWordInfo> words, HashSet<string> headerDates);
    bool IsDate(string text);
}

public partial class PdfIntelligentMaskingService : IPdfIntelligentMaskingService
{
    private readonly IConfigurationService? _configService;

    private const string DefaultDateIgnoreMask = "[DATE_IGNORE]";
    private const string DefaultNameIgnoreMask = "[NOM_IGNORE]";

    [GeneratedRegex(@"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b")]
    private static partial Regex DefaultDateRegex();

    [GeneratedRegex(@"\b(?:Opgemaakt\b[^\r\n]{0,75}?\bop|Gedaan\b[^\r\n]{0,65}?\bop|Ausgefertigt\b[^\r\n]{0,65}?\bam|Erstellt\b[^\r\n]{0,55}?\bam|Fait\b[^\r\n]{0,75}?\ble)\s+(?<date>\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex IssuanceDatePhraseRegex();

    [GeneratedRegex(ComparisonProfile.DefaultClientKeywordsPattern)]
    private static partial Regex DefaultClientKeywordRegex();

    [GeneratedRegex(@"(?:[Aa]uftraggeber(?:in)?|[Oo]pdrachtgever|[Ss]ouscripteur|[Pp]reneur\s+d['’]assurance(?:\s+et\s+assur[ée]e?)?|[Vv]erzekering(?:s)?nemer(?:\s+en\s+verzekerde)?|[Vv]ersicherungsnehmer(?:in)?(?:\s+und\s+[Vv]ersicherter?)?|[Cc]lient|[Aa]ssur[ée]e?|[Tt]itulaire|[Dd]estinataire)\s*[:\s]+([\p{Lu}][\p{Ll}\p{Lu}'’-]{1,}(?:\s+[\p{Lu}][\p{Ll}\p{Lu}'’-]{1,}){1,3})|\b([\p{Lu}'’-]{2,}(?:\s+[\p{Lu}'’-]{2,}){1,3})\s*,\s*(?:n[ée]e?|geboren)\b")]
    private static partial Regex DefaultDynamicTextNameRegex();

    [GeneratedRegex(@"^[\p{L}][\p{L}'’\-]*\.?$")]
    private static partial Regex ValidNameCharsRegex();

    private static readonly HashSet<string> CivilityTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "M", "M.", "Mr", "Mr.", "Mme", "Mme.", "Mlle", "Mlle.",
        "Monsieur", "Madame", "Mademoiselle", "Cher", "Chère", "Chere",
        "Dhr", "Dhr.", "Mevr", "Mevr.", "Mw", "Mw.", "Heer", "Mevrouw", "Geachte",
        "Herr", "Herrn", "Frau", "Dr", "Dr.", "Prof", "Prof."
    };

    private static readonly HashSet<string> SurnameParticles = new(StringComparer.OrdinalIgnoreCase)
    {
        "van", "de", "der", "den", "het", "'t", "ten", "ter", "te",
        "du", "des", "le", "la", "d'", "d’", "von", "zu", "zum", "zur"
    };

    private static readonly HashSet<string> LabelContinuationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "et", "en", "und", "ou", "of", "oder",
        "d'assurance", "d’assurance", "d'ordre", "d’ordre",
        "assuré", "assure", "assurée", "assuree",
        "verzekerde", "versicherter", "versicherte", ":"
    };

    private static readonly HashSet<string> NameStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Mots de liaison / naissance / adresse qui suivent immédiatement un nom
        "né", "ne", "née", "nee", "nés", "nes", "nées", "geboren", "born",
        "et", "en", "und", "ou", "of", "oder", "à", "a", "te", "in", "le", "op", "am",
        "domicilié", "domiciliée", "wonende", "wohnhaft",

        // Sociétés, institutions, termes juridiques et financiers
        "AG", "INSURANCE", "ASSURANCE", "ASSURANCES", "VERZEKERING", "VERZEKERINGEN",
        "VERZEKERINGSPRODUCT", "VERZEKERINGSONDERNEMING", "VERSICHERUNG", "VERSICHERUNGSPRODUKT",
        "VERSICHERUNGSGESELLSCHAFT", "BNP", "PARIBAS", "FORTIS", "AGEAS",
        "SA", "NV", "SA/NV", "BV", "SRL", "SPRL", "BVBA", "GMBH", "AG/SA",
        "BANK", "BANQUE", "NATIONALE", "NATIONALBANK", "BELGISCHE", "BELGE",
        "BELGIQUE", "BELGIE", "BELGIË", "BELGIEN", "BRUXELLES", "BRUSSEL", "BRÜSSEL",
        "FSMA", "IBAN", "BIC", "TVA", "BTW", "MWST", "RPM", "RPR", "RJP", "EUR", "EURO",
        "LIFE", "FUND", "PLAN", "EASY", "CASH", "SMART", "FUTURE", "HUMAN", "EQUITIES", "NEUTRAL",

        // Titres, fonctions et signataires institutionnels fixes
        "CHIEF", "EXECUTIVE", "OFFICER", "PRESIDENT", "PRÉSIDENT", "PRÉSIDENTE",
        "VORSITZENDE", "DIRECTEUR", "COMITÉ", "COMITE", "DIRECTION", "DIREKTIONSKOMITEES",
        "HEIDI", "DELOBELLE",

        // Libellés de colonnes / rôles / adresses
        "PRENEUR", "SOUSCRIPTEUR", "ASSURÉ", "ASSURE", "ASSURÉE", "OPDRACHTGEVER",
        "AUFTRAGGEBER", "AUFTRAGGEBERIN", "VERZEKERINGNEMER", "VERZEKERINGSNEMER", "VERZEKERDE",
        "VERSICHERUNGSNEHMER", "VERSICHERUNGSNEHMERIN", "VERSICHERTER", "VERSICHERTE",
        "BEGUNSTIGDE", "BEGÜNSTIGTER", "BEGÜNSTIGTEN", "BEGÜNSTIGTENKONTO",
        "TITULAIRE", "CLIENT", "KLANT", "KUNDE", "INTERMÉDIAIRE", "TUSSENPERSOON", "VERMITTLER",
        "NAAM", "NAME", "NOM", "ADRES", "ADRESSE", "REKENING", "KONTONUMMER",
        "CONTRAT", "VERTRAG", "OVEREENKOMST", "POLICE", "POLIS", "GESTION", "LIBRE", "VIE", "ENTIÈRE",
        "RUE", "STRAAT", "LAAN", "AVENUE", "BOULEVARD", "CHAUSSÉE", "CHAUSSEE", "PLACE", "PLEIN",
        "DOELSTR", "DOELSTRAAT", "CONDITIONS", "PARTICULIERES", "PARTICULIÈRES", "GENERALES", "GÉNÉRALES",
        "EXEMPLAIRE", "EXEMPLAAR", "EXEMPLAR", "SIGNATURE", "HANDTEKENING", "UNTERSCHRIFT"
    };

    private sealed class WordLineItem
    {
        public int WordIndex { get; init; }
        public required string Text { get; init; }
        public double LeftX { get; init; }
        public double RightX { get; init; }
    }

    private sealed class WordLine
    {
        public int PageNumber { get; init; }
        public double Y { get; init; }
        public List<WordLineItem> Items { get; } = new();
    }

    public PdfIntelligentMaskingService(IConfigurationService? configService = null)
    {
        _configService = configService;
    }

    private ComparisonProfile? Profile => _configService?.ActiveProfile;

    public bool IsDate(string text) => DefaultDateRegex().IsMatch(text);

    private static string NormalizeTokenForComparison(string token)
    {
        if (DefaultDateRegex().IsMatch(token))
        {
            return Regex.Replace(token, @"[^\d]+", ".").Trim('.');
        }
        return token.Trim().ToUpperInvariant();
    }

    public string MaskRepeatingTextElements(string text, IReadOnlySet<string>? headerDates = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var rules = GetActiveRegexRules();
        bool hasDateRule = rules.Any(r => string.Equals(r.ReplacementMask, DefaultDateIgnoreMask, StringComparison.Ordinal));

        var normalizedAutoDates = new HashSet<string>(StringComparer.Ordinal);
        if (headerDates != null)
        {
            foreach (var hd in headerDates)
            {
                normalizedAutoDates.Add(NormalizeTokenForComparison(hd));
            }
        }

        if (hasDateRule)
        {
            CollectIssuanceDatesFromText(text, normalizedAutoDates);
        }

        string currentText = text;

        foreach (var rule in rules)
        {
            var regex = GetRegexForRule(rule);
            if (regex == null) continue;

            bool isDateMaskRule = string.Equals(rule.ReplacementMask, DefaultDateIgnoreMask, StringComparison.Ordinal);

            if (rule.MinOccurrences <= 1 && (!isDateMaskRule || normalizedAutoDates.Count == 0))
            {
                currentText = regex.Replace(currentText, rule.ReplacementMask ?? string.Empty);
                continue;
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in regex.Matches(currentText))
            {
                string norm = NormalizeTokenForComparison(m.Value);
                counts[norm] = counts.GetValueOrDefault(norm) + 1;
            }

            int minOcc = Math.Max(1, rule.MinOccurrences);
            string mask = rule.ReplacementMask ?? string.Empty;

            currentText = regex.Replace(currentText, match =>
            {
                string norm = NormalizeTokenForComparison(match.Value);
                bool inAutoDates = isDateMaskRule && normalizedAutoDates.Contains(norm);
                if (inAutoDates || (counts.TryGetValue(norm, out int count) && count >= minOcc))
                {
                    return mask;
                }
                return match.Value;
            });
        }

        if (Profile?.EnableDynamicClientNameMasking ?? true)
        {
            string nameMask = Profile?.ClientNameReplacementMask ?? DefaultNameIgnoreMask;
            var matches = DefaultDynamicTextNameRegex().Matches(currentText);
            var detectedPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match nameMatch in matches)
            {
                if (!nameMatch.Success) continue;
                string targetName = nameMatch.Groups[1].Success ? nameMatch.Groups[1].Value : nameMatch.Groups[2].Value;
                string[] parts = targetName.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (parts.Any(p => NameStopWords.Contains(p.TrimEnd(',', '.', ':')))) continue;

                string pattern = $@"\b{string.Join(@"\s+", Array.ConvertAll(parts, Regex.Escape))}\b";
                detectedPatterns.Add(pattern);
            }

            foreach (var pattern in detectedPatterns)
            {
                currentText = Regex.Replace(currentText, pattern, nameMask, RegexOptions.IgnoreCase);
            }
        }

        return currentText;
    }

    public void MaskRepeatingWordElements(List<PdfWordInfo> words, HashSet<string> headerDates)
    {
        if (words == null || words.Count == 0) return;

        var rules = GetActiveRegexRules();
        bool hasDateRule = rules.Any(r => string.Equals(r.ReplacementMask, DefaultDateIgnoreMask, StringComparison.Ordinal));

        var normalizedAutoDates = new HashSet<string>(headerDates.Select(NormalizeTokenForComparison), StringComparer.Ordinal);
        if (hasDateRule)
        {
            var (initialDocText, _, _, _) = BuildIndexedDocumentText(words);
            CollectIssuanceDatesFromText(initialDocText, normalizedAutoDates);
        }

        foreach (var rule in rules)
        {
            if (words.Count == 0) break;

            var regex = GetRegexForRule(rule);
            if (regex == null) continue;

            var (fullText, charToWord, wordStartChar, wordEndChar) = BuildIndexedDocumentText(words);
            var matches = regex.Matches(fullText);
            if (matches.Count == 0) continue;

            bool isDateMaskRule = string.Equals(rule.ReplacementMask, DefaultDateIgnoreMask, StringComparison.Ordinal);

            var tokenCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match match in matches)
            {
                string norm = NormalizeTokenForComparison(match.Value);
                tokenCounts[norm] = tokenCounts.GetValueOrDefault(norm) + 1;
            }

            int minOcc = Math.Max(1, rule.MinOccurrences);
            string mask = rule.ReplacementMask ?? string.Empty;

            // Parcourir de la fin vers le début pour préserver les indices de mots et de caractères
            for (int mIdx = matches.Count - 1; mIdx >= 0; mIdx--)
            {
                Match match = matches[mIdx];
                string norm = NormalizeTokenForComparison(match.Value);
                bool inAutoDates = isDateMaskRule && normalizedAutoDates.Contains(norm);

                if (!inAutoDates && (!tokenCounts.TryGetValue(norm, out int count) || count < minOcc))
                {
                    continue;
                }

                int startWordIdx = -1;
                int endWordIdx = -1;
                int matchEnd = Math.Min(match.Index + match.Length, charToWord.Count);

                for (int c = match.Index; c < matchEnd; c++)
                {
                    int wIdx = charToWord[c];
                    if (wIdx != -1)
                    {
                        if (startWordIdx == -1) startWordIdx = wIdx;
                        endWordIdx = wIdx;
                    }
                }

                if (startWordIdx == -1 || endWordIdx == -1) continue;
                if (!string.IsNullOrEmpty(mask) && words[startWordIdx].Text.Contains(mask, StringComparison.Ordinal)) continue;

                string rawPrefix = match.Index > wordStartChar[startWordIdx]
                    ? fullText.Substring(wordStartChar[startWordIdx], match.Index - wordStartChar[startWordIdx])
                    : string.Empty;

                string rawSuffix = matchEnd < wordEndChar[endWordIdx]
                    ? fullText.Substring(matchEnd, wordEndChar[endWordIdx] - matchEnd)
                    : string.Empty;

                string prefix = rawPrefix.Any(char.IsLetterOrDigit) ? rawPrefix : string.Empty;
                string suffix = rawSuffix.Any(char.IsLetterOrDigit) ? rawSuffix : string.Empty;

                string replacedText = (prefix + mask + suffix).Trim();

                var combinedLetters = new List<Letter>();
                for (int k = startWordIdx; k <= endWordIdx; k++)
                {
                    if (!string.IsNullOrEmpty(replacedText) && words[k].Letters != null)
                    {
                        combinedLetters.AddRange(words[k].Letters);
                    }

                    if (k > startWordIdx)
                    {
                        words[k] = new PdfWordInfo
                        {
                            Text = string.Empty,
                            Letters = Array.Empty<Letter>(),
                            PageNumber = words[k].PageNumber
                        };
                    }
                }

                words[startWordIdx] = new PdfWordInfo
                {
                    Text = replacedText,
                    Letters = string.IsNullOrEmpty(replacedText) ? Array.Empty<Letter>() : combinedLetters,
                    PageNumber = words[startWordIdx].PageNumber
                };
            }

            words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
        }

        if (Profile?.EnableDynamicClientNameMasking ?? true)
        {
            ApplyClientNameMaskingOnWords(words);
        }
    }

    private static void CollectIssuanceDatesFromText(string text, HashSet<string> normalizedDatesSet)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (Match m in IssuanceDatePhraseRegex().Matches(text))
        {
            var dateGroup = m.Groups["date"];
            if (dateGroup.Success && !string.IsNullOrWhiteSpace(dateGroup.Value))
            {
                normalizedDatesSet.Add(NormalizeTokenForComparison(dateGroup.Value));
            }
        }
    }

    private static (string FullText, List<int> CharToWord, int[] WordStartChar, int[] WordEndChar) BuildIndexedDocumentText(List<PdfWordInfo> words)
    {
        var sb = new StringBuilder(words.Count * 8);
        var charToWord = new List<int>(words.Count * 8);
        var wordStartChar = new int[words.Count];
        var wordEndChar = new int[words.Count];

        int lastPage = -1;
        double lastY = double.NaN;

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            double wY = w.Letters != null && w.Letters.Count > 0 ? w.Letters[0].Location.Y : lastY;

            if (i > 0)
            {
                bool isNewLine = w.PageNumber != lastPage ||
                                 (!double.IsNaN(lastY) && !double.IsNaN(wY) && Math.Abs(wY - lastY) > 5.0);
                sb.Append(isNewLine ? '\n' : ' ');
                charToWord.Add(-1);
            }

            wordStartChar[i] = sb.Length;
            foreach (char c in w.Text)
            {
                sb.Append(c);
                charToWord.Add(i);
            }
            wordEndChar[i] = sb.Length;

            lastPage = w.PageNumber;
            if (!double.IsNaN(wY))
            {
                lastY = wY;
            }
        }

        return (sb.ToString(), charToWord, wordStartChar, wordEndChar);
    }

    private void ApplyClientNameMaskingOnWords(List<PdfWordInfo> words)
    {
        if (words.Count == 0) return;

        Regex clientKeywordRegex = (_configService != null && !string.IsNullOrWhiteSpace(Profile?.ClientKeywordsPattern))
            ? (_configService.TryGetCompiledRegex(Profile.ClientKeywordsPattern, ignoreCase: true) ?? DefaultClientKeywordRegex())
            : DefaultClientKeywordRegex();

        string nameMask = Profile?.ClientNameReplacementMask ?? DefaultNameIgnoreMask;
        var lines = BuildWordLines(words);
        var detectedClientNames = new List<string[]>();

        void AddDetectedName(string[]? candidate)
        {
            if (candidate == null || candidate.Length < 2) return;
            bool exists = detectedClientNames.Any(existing =>
                existing.Length == candidate.Length &&
                existing.Zip(candidate, (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)).All(x => x));
            if (!exists)
            {
                detectedClientNames.Add(candidate);
            }
        }

        for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
        {
            var line = lines[lineIdx];

            for (int itemIdx = 0; itemIdx < line.Items.Count; itemIdx++)
            {
                // Le libellé rôle client doit débuter une colonne (premier mot de la ligne ou séparé d'au moins 35 pt de la colonne précédente)
                bool isColumnStart = itemIdx == 0 ||
                                     (line.Items[itemIdx].LeftX - line.Items[itemIdx - 1].RightX >= 35.0);
                if (!isColumnStart) continue;

                if (!TryMatchClientRoleLabel(line, itemIdx, clientKeywordRegex, out int labelEndIdx))
                {
                    continue;
                }

                double labelLeftX = line.Items[itemIdx].LeftX;
                double labelEndRightX = line.Items[labelEndIdx].RightX;
                bool foundForThisLabel = false;

                // 1. Même ligne, colonne de valeur immédiatement à droite (ex: "preneur d'assurance et -> ALDARCIE DISENADAS, né le...")
                if (labelEndIdx + 1 < line.Items.Count)
                {
                    var rightCandidateTokens = new List<string>();
                    double prevRight = -1;

                    for (int k = labelEndIdx + 1; k < line.Items.Count; k++)
                    {
                        var item = line.Items[k];
                        if (rightCandidateTokens.Count == 0)
                        {
                            if (item.LeftX < labelEndRightX + 6.0 || item.LeftX > labelLeftX + 230.0)
                                break;
                        }
                        else if (item.LeftX - prevRight >= 35.0)
                        {
                            break;
                        }

                        rightCandidateTokens.Add(item.Text);
                        prevRight = item.RightX;
                    }

                    var sameLineName = TryExtractPersonName(rightCandidateTokens, requireAllUppercase: false);
                    if (sameLineName != null)
                    {
                        AddDetectedName(sameLineName);
                        foundForThisLabel = true;
                    }
                }

                // 2. Ligne juste en dessous dans la même colonne (ex: "Opdrachtgever" / "Auftraggeber" au-dessus de "COLEEN DÉNIVELÉE")
                if (!foundForThisLabel)
                {
                    for (int nextLineIdx = lineIdx + 1; nextLineIdx < lines.Count; nextLineIdx++)
                    {
                        var nextLine = lines[nextLineIdx];
                        if (nextLine.PageNumber != line.PageNumber) break;

                        double dy = line.Y - nextLine.Y;
                        if (dy < 5.5) continue;
                        if (dy > 24.0) break;

                        var colTokens = ExtractColumnTokensAtX(nextLine, labelLeftX, toleranceX: 25.0);
                        var belowName = TryExtractPersonName(colTokens, requireAllUppercase: false);
                        if (belowName != null)
                        {
                            AddDetectedName(belowName);
                            foundForThisLabel = true;
                        }
                        break;
                    }
                }

                // 3. Bloc de signature : nom en MAJUSCULES situé juste au-dessus de la légende ("opdrachtgever", "preneur d'assurance")
                for (int prevLineIdx = lineIdx - 1; prevLineIdx >= 0; prevLineIdx--)
                {
                    var prevLine = lines[prevLineIdx];
                    if (prevLine.PageNumber != line.PageNumber) break;

                    double dy = prevLine.Y - line.Y;
                    if (dy < 6.0) continue;
                    if (dy > 20.0) break;

                    var aboveTokens = ExtractColumnTokensAtX(prevLine, labelLeftX, toleranceX: 25.0);
                    var aboveName = TryExtractPersonName(aboveTokens, requireAllUppercase: true);
                    if (aboveName != null)
                    {
                        AddDetectedName(aboveName);
                    }
                    break;
                }
            }
        }

        if (detectedClientNames.Count == 0) return;

        // Remplacer d'abord les séquences complètes les plus longues (Prénom + Nom) dans tout le document
        foreach (var targetNameParts in detectedClientNames.OrderByDescending(n => n.Length))
        {
            int len = targetNameParts.Length;
            for (int i = 0; i <= words.Count - len; i++)
            {
                bool allMatch = true;
                for (int k = 0; k < len; k++)
                {
                    string candidateClean = CleanNameTokenForMatch(words[i + k].Text);
                    if (!string.Equals(candidateClean, targetNameParts[k], StringComparison.OrdinalIgnoreCase))
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (!allMatch) continue;

                string lastRaw = words[i + len - 1].Text;
                string trailingPunct = ExtractTrailingPunctuation(lastRaw);

                var combinedLetters = new List<Letter>();
                for (int k = 0; k < len; k++)
                {
                    if (words[i + k].Letters != null)
                    {
                        combinedLetters.AddRange(words[i + k].Letters);
                    }
                    if (k > 0)
                    {
                        words[i + k] = new PdfWordInfo
                        {
                            Text = string.Empty,
                            Letters = Array.Empty<Letter>(),
                            PageNumber = words[i + k].PageNumber
                        };
                    }
                }

                words[i] = new PdfWordInfo
                {
                    Text = nameMask + trailingPunct,
                    Letters = combinedLetters,
                    PageNumber = words[i].PageNumber
                };

                i += len - 1;
            }

            words.RemoveAll(w => string.IsNullOrEmpty(w.Text));
        }

        // Masquer également un nom/prénom isolé lorsqu'il suit immédiatement une civilité ("Monsieur DUPONT", "Geachte heer DHESTR")
        var individualNameParts = new HashSet<string>(
            detectedClientNames.SelectMany(n => n).Where(p => p.Length >= 3 && !SurnameParticles.Contains(p)),
            StringComparer.OrdinalIgnoreCase);

        if (individualNameParts.Count > 0)
        {
            for (int i = 1; i < words.Count; i++)
            {
                string prevClean = words[i - 1].Text.TrimEnd(',', ':');
                if (!CivilityTokens.Contains(prevClean)) continue;

                string currClean = CleanNameTokenForMatch(words[i].Text);
                if (individualNameParts.Contains(currClean))
                {
                    string trailingPunct = ExtractTrailingPunctuation(words[i].Text);
                    words[i] = new PdfWordInfo
                    {
                        Text = nameMask + trailingPunct,
                        Letters = words[i].Letters,
                        PageNumber = words[i].PageNumber
                    };
                }
            }
        }
    }

    private static List<WordLine> BuildWordLines(List<PdfWordInfo> words)
    {
        var lines = new List<WordLine>();
        WordLine? current = null;

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (string.IsNullOrWhiteSpace(w.Text) || w.Letters == null || w.Letters.Count == 0)
                continue;

            double y = w.Letters[0].Location.Y;
            double leftX = w.Letters[0].GlyphRectangle.Left;
            double rightX = w.Letters[^1].GlyphRectangle.Right;

            if (current == null || current.PageNumber != w.PageNumber || Math.Abs(y - current.Y) > 4.0)
            {
                current = new WordLine { PageNumber = w.PageNumber, Y = y };
                lines.Add(current);
            }

            current.Items.Add(new WordLineItem
            {
                WordIndex = i,
                Text = w.Text,
                LeftX = leftX,
                RightX = rightX
            });
        }

        return lines;
    }

    private static bool TryMatchClientRoleLabel(WordLine line, int startIdx, Regex singleWordKeywordRegex, out int labelEndIdx)
    {
        labelEndIdx = startIdx;
        string raw0 = line.Items[startIdx].Text;
        string clean0 = raw0.TrimEnd(':', ',', '.');
        if (string.IsNullOrEmpty(clean0)) return false;

        bool matched = false;

        // Libellés composés FR : "preneur d'assurance", "donneur d'ordre"
        if ((string.Equals(clean0, "preneur", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(clean0, "donneur", StringComparison.OrdinalIgnoreCase)) &&
            startIdx + 1 < line.Items.Count)
        {
            string nextClean = line.Items[startIdx + 1].Text.TrimEnd(':', ',', '.');
            if (nextClean.StartsWith("d'assurance", StringComparison.OrdinalIgnoreCase) ||
                nextClean.StartsWith("d’assurance", StringComparison.OrdinalIgnoreCase) ||
                nextClean.StartsWith("d'ordre", StringComparison.OrdinalIgnoreCase) ||
                nextClean.StartsWith("d’ordre", StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
                labelEndIdx = startIdx + 1;
            }
        }

        if (!matched && (singleWordKeywordRegex.IsMatch(raw0) || singleWordKeywordRegex.IsMatch(clean0)))
        {
            matched = true;
            labelEndIdx = startIdx;
        }

        if (!matched) return false;

        // Consommer la suite immédiate du libellé dans la même colonne (ex: "preneur d'assurance et", "Versicherungsnehmer und Versicherter", ":")
        while (labelEndIdx + 1 < line.Items.Count)
        {
            var nextItem = line.Items[labelEndIdx + 1];
            double gap = nextItem.LeftX - line.Items[labelEndIdx].RightX;
            if (gap > 16.0) break;

            string nextTok = nextItem.Text.TrimEnd(':', ',');
            if (LabelContinuationWords.Contains(nextItem.Text) || LabelContinuationWords.Contains(nextTok))
            {
                labelEndIdx++;
            }
            else
            {
                break;
            }
        }

        return true;
    }

    private static List<string> ExtractColumnTokensAtX(WordLine line, double targetLeftX, double toleranceX)
    {
        var result = new List<string>();
        double prevRight = -1;

        for (int i = 0; i < line.Items.Count; i++)
        {
            var item = line.Items[i];
            if (result.Count == 0)
            {
                if (item.LeftX < targetLeftX - toleranceX) continue;
                if (item.LeftX > targetLeftX + toleranceX) break;
            }
            else if (item.LeftX - prevRight >= 35.0)
            {
                break;
            }

            result.Add(item.Text);
            prevRight = item.RightX;
        }

        return result;
    }

    private static string[]? TryExtractPersonName(IReadOnlyList<string> candidateTokens, bool requireAllUppercase)
    {
        if (candidateTokens == null || candidateTokens.Count == 0) return null;

        int idx = 0;
        while (idx < candidateTokens.Count &&
               (candidateTokens[idx] == ":" || CivilityTokens.Contains(candidateTokens[idx].TrimEnd(',', ':'))))
        {
            idx++;
        }

        var parts = new List<string>();
        for (; idx < candidateTokens.Count && parts.Count < 5; idx++)
        {
            string raw = candidateTokens[idx];
            bool endsWithCommaOrSemi = raw.EndsWith(',') || raw.EndsWith(';');
            string clean = CleanNameTokenForMatch(raw);

            if (string.IsNullOrEmpty(clean) || clean.Length < 2) break;
            if (clean.Any(char.IsDigit)) break;
            if (!ValidNameCharsRegex().IsMatch(clean)) break;
            if (NameStopWords.Contains(clean)) break;

            bool isParticle = SurnameParticles.Contains(clean);
            if (!isParticle && !char.IsUpper(clean[0])) break;

            if (requireAllUppercase && !clean.Where(char.IsLetter).All(char.IsUpper))
            {
                break;
            }

            parts.Add(clean);

            if (endsWithCommaOrSemi)
            {
                break;
            }
        }

        // Retirer d'éventuelles particules orphelines en fin de séquence
        while (parts.Count > 0 && SurnameParticles.Contains(parts[^1]))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        int totalLetters = parts.Sum(p => p.Count(char.IsLetter));
        if (parts.Count < 2 || totalLetters < 5)
        {
            return null;
        }

        return parts.ToArray();
    }

    private static string CleanNameTokenForMatch(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return raw.Trim().TrimEnd(',', ';', ':');
    }

    private static string ExtractTrailingPunctuation(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        int i = raw.Length;
        while (i > 0 && (raw[i - 1] == ',' || raw[i - 1] == ';'))
        {
            i--;
        }
        return i < raw.Length ? raw.Substring(i) : string.Empty;
    }

    private IReadOnlyList<RegexMaskingRule> GetActiveRegexRules()
    {
        var list = new List<RegexMaskingRule>();

        if (Profile != null)
        {
            list.AddRange(Profile.RegexRules.Where(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.Pattern)));

            // Garantir que les mentions d'exemplaire technique (ex: exemplaireAG/1JQ3B/FIB16) ne sont jamais marquées comme différence
            if (!string.Equals(Profile.ProfileName, ConfigurationService.PresetVisualPixelPerfect, StringComparison.Ordinal) &&
                !list.Any(r => r.Pattern.Contains("exemplaire", StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new RegexMaskingRule
                {
                    Name = "Mentions d'exemplaire technique (exemplaire.../CODE/CODE)",
                    IsEnabled = true,
                    Pattern = ComparisonProfile.ExemplaireRoutingPattern,
                    ReplacementMask = string.Empty,
                    IgnoreCase = true,
                    MinOccurrences = 1
                });
            }

            return list;
        }

        list.Add(new RegexMaskingRule
        {
            Name = "Default Date Mask",
            IsEnabled = true,
            Pattern = ComparisonProfile.DefaultDateRegexPattern,
            ReplacementMask = DefaultDateIgnoreMask,
            IgnoreCase = true,
            MinOccurrences = 2
        });
        list.Add(new RegexMaskingRule
        {
            Name = "Mentions d'exemplaire technique (exemplaire.../CODE/CODE)",
            IsEnabled = true,
            Pattern = ComparisonProfile.ExemplaireRoutingPattern,
            ReplacementMask = string.Empty,
            IgnoreCase = true,
            MinOccurrences = 1
        });

        return list;
    }

    private Regex? GetRegexForRule(RegexMaskingRule rule)
    {
        if (_configService != null)
        {
            return _configService.TryGetCompiledRegex(rule.Pattern, rule.IgnoreCase);
        }

        try
        {
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (rule.IgnoreCase) options |= RegexOptions.IgnoreCase;
            return new Regex(rule.Pattern, options, TimeSpan.FromSeconds(2));
        }
        catch
        {
            return null;
        }
    }
}
