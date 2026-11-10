using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PDFComparison.Services;

public interface IPdfDrawingService
{
    (PdfDocumentBuilder.AddedFont Font, PdfDocumentBuilder.AddedFont FontBold) LoadFonts(PdfDocumentBuilder builder);
    void DrawDiffMarkup(PdfPageBuilder pageBuilder, IEnumerable<LetterLoc> letters, byte r, byte g, byte b, MarkupStyle style);
    void DrawVisualDiffRegions(PdfPageBuilder pageBuilder, IEnumerable<VisualDiffRegion> regions, byte r, byte g, byte b, PdfDocumentBuilder.AddedFont fontBold);
    void DrawPageStamp(PdfPageBuilder pageBuilder, string text, PdfDocumentBuilder.AddedFont fontBold);
    void DrawPageStatusHeader(PdfPageBuilder pageBuilder, string roleLabel, int pageNumber, int totalPages, int diffSegmentsCount, int visualRegionsCount, PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold);
    decimal DrawTextLines(PdfPageBuilder page, string text, decimal startY, decimal startX, int maxChars, byte r, byte g, byte b, PdfDocumentBuilder.AddedFont fontToUse);
    decimal DrawMixedTextLines(PdfPageBuilder page, List<(string Text, byte r, byte g, byte b, bool isBold)> chunks, decimal startY, decimal startX, decimal maxWidth, PdfDocumentBuilder.AddedFont fontNormal, PdfDocumentBuilder.AddedFont fontBold);
    void DrawStatBox(PdfPageBuilder page, decimal x, decimal y, string label, string value, PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold, byte rValue, byte gValue, byte bValue);
}

public class PdfDrawingService : IPdfDrawingService
{
    private const decimal DefaultFontSize = 10m;
    private const decimal LineHeight = 13m;
    private bool _usingFallbackStandardFont;

    public (PdfDocumentBuilder.AddedFont Font, PdfDocumentBuilder.AddedFont FontBold) LoadFonts(PdfDocumentBuilder builder)
    {
        try
        {
            string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string arialPath = Path.Combine(fontsFolder, "arial.ttf");
            string arialBoldPath = Path.Combine(fontsFolder, "arialbd.ttf");

            if (File.Exists(arialPath) && File.Exists(arialBoldPath))
            {
                _usingFallbackStandardFont = false;
                return (builder.AddTrueTypeFont(File.ReadAllBytes(arialPath)),
                        builder.AddTrueTypeFont(File.ReadAllBytes(arialBoldPath)));
            }
        }
        catch
        {
            // Fallback silencieux vers les polices standard
        }

        _usingFallbackStandardFont = true;
        var fallbackFont = builder.AddStandard14Font(Standard14Font.Helvetica);
        var fallbackFontBold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        return (fallbackFont, fallbackFontBold);
    }

    private string SafePdfString(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length + 4);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r':
                case '\n':
                case '\t':
                case '\u00AD':
                case '\u200B':
                case '\u200C':
                case '\u200D':
                case '\uFEFF':
                    continue;
                case '\u00A0':
                case '\u202F':
                case '\u2009':
                    sb.Append(' ');
                    break;
                case 'ﬁ': sb.Append("fi"); break;
                case 'ﬂ': sb.Append("fl"); break;
                case 'ﬀ': sb.Append("ff"); break;
                case 'ﬃ': sb.Append("ffi"); break;
                case 'ﬄ': sb.Append("ffl"); break;
                case '▪':
                case '●':
                case '○':
                case '▸':
                case '►':
                    sb.Append(_usingFallbackStandardFont ? "-" : "•");
                    break;
                case '•':
                    sb.Append(_usingFallbackStandardFont ? "-" : "•");
                    break;
                case '€':
                    sb.Append(_usingFallbackStandardFont ? "EUR" : "€");
                    break;
                case 'œ':
                    sb.Append(_usingFallbackStandardFont ? "oe" : "œ");
                    break;
                case 'Œ':
                    sb.Append(_usingFallbackStandardFont ? "OE" : "Œ");
                    break;
                case '’':
                case '‘':
                    sb.Append('\'');
                    break;
                case '“':
                case '”':
                    sb.Append('"');
                    break;
                case '–':
                case '—':
                    sb.Append('-');
                    break;
                default:
                    if (_usingFallbackStandardFont && c > 255)
                        sb.Append('?');
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private void SafeAddText(PdfPageBuilder page, string text, decimal fontSize, PdfPoint pt, PdfDocumentBuilder.AddedFont font)
    {
        string sanitized = SafePdfString(text);
        if (string.IsNullOrEmpty(sanitized)) return;

        try
        {
            page.AddText(sanitized, fontSize, pt, font);
        }
        catch
        {
            // Fallback ASCII pur si un caractère exotique n'est pas présent dans la table glyphe de la police système
            var ascii = new StringBuilder(sanitized.Length);
            foreach (char c in sanitized)
            {
                ascii.Append(c <= 126 ? c : '?');
            }
            try
            {
                page.AddText(ascii.ToString(), fontSize, pt, font);
            }
            catch
            {
                // Ignorer silencieusement en dernier recours
            }
        }
    }

    public void DrawDiffMarkup(PdfPageBuilder pageBuilder, IEnumerable<LetterLoc> letters, byte r, byte g, byte b, MarkupStyle style)
    {
        var segments = VisualSegmentHelper.GetSegments(letters);
        if (segments.Count == 0) return;

        pageBuilder.SetStrokeColor(r, g, b);

        if (style == MarkupStyle.Highlight)
        {
            var marginBlocks = new List<(decimal minY, decimal maxY)>();
            decimal currentMaxY = segments[0].baselineY + (segments[0].fontSize * 0.9m);
            decimal currentMinY = segments[0].baselineY - (segments[0].fontSize * 0.2m);

            for (int i = 1; i < segments.Count; i++)
            {
                var seg = segments[i];
                decimal boxMinY = seg.baselineY - (seg.fontSize * 0.2m);
                decimal boxMaxY = seg.baselineY + (seg.fontSize * 0.9m);

                if (currentMinY - boxMaxY < seg.fontSize * 2.0m)
                {
                    currentMinY = Math.Min(currentMinY, boxMinY);
                    currentMaxY = Math.Max(currentMaxY, boxMaxY);
                }
                else
                {
                    marginBlocks.Add((currentMinY, currentMaxY));
                    currentMaxY = boxMaxY;
                    currentMinY = boxMinY;
                }
            }
            marginBlocks.Add((currentMinY, currentMaxY));

            foreach (var mb in marginBlocks)
            {
                decimal marginX = 6m;
                pageBuilder.DrawLine(
                    new PdfPoint((double)marginX, (double)mb.minY),
                    new PdfPoint((double)marginX, (double)mb.maxY),
                    8.0m
                );
            }
        }

        foreach (var seg in segments)
        {
            decimal strokeWidth = Math.Max(seg.fontSize * 0.08m, 0.75m);
            decimal width = seg.maxX - seg.minX;

            switch (style)
            {
                case MarkupStyle.Strikethrough:
                    pageBuilder.DrawLine(new PdfPoint((double)seg.minX, (double)(seg.baselineY + (seg.fontSize * 0.3m))), new PdfPoint((double)seg.maxX, (double)(seg.baselineY + (seg.fontSize * 0.3m))), strokeWidth);
                    break;
                case MarkupStyle.Underline:
                    pageBuilder.DrawLine(new PdfPoint((double)seg.minX, (double)(seg.baselineY - (seg.fontSize * 0.12m))), new PdfPoint((double)seg.maxX, (double)(seg.baselineY - (seg.fontSize * 0.12m))), strokeWidth);
                    break;
                case MarkupStyle.Box:
                    decimal paddingX = seg.fontSize * 0.1m;
                    pageBuilder.DrawRectangle(new PdfPoint((double)(seg.minX - paddingX), (double)(seg.baselineY - (seg.fontSize * 0.15m))), width + (paddingX * 2), seg.fontSize * 0.9m, strokeWidth, false);
                    break;
                case MarkupStyle.Highlight:
                    decimal padding = Math.Clamp(seg.fontSize * 0.14m, 1.0m, 2.5m);
                    decimal boxX = seg.minX - padding;
                    decimal boxY = seg.baselineY - (seg.fontSize * 0.22m);
                    decimal boxWidth = (seg.maxX - seg.minX) + (padding * 2);
                    decimal boxHeight = seg.fontSize * 1.12m;
                    decimal borderStroke = Math.Clamp(seg.fontSize * 0.16m, 1.2m, 2.2m);

                    pageBuilder.DrawRectangle(
                        new PdfPoint((double)boxX, (double)boxY),
                        boxWidth,
                        boxHeight,
                        borderStroke,
                        false);
                    break;
            }
        }
    }

    public void DrawVisualDiffRegions(PdfPageBuilder pageBuilder, IEnumerable<VisualDiffRegion> regions, byte r, byte g, byte b, PdfDocumentBuilder.AddedFont fontBold)
    {
        if (regions == null) return;

        foreach (var reg in regions)
        {
            decimal x = (decimal)Math.Max(2.0, reg.PdfX - 3.0);
            decimal y = (decimal)Math.Max(2.0, reg.PdfY - 3.0);
            decimal w = (decimal)Math.Max(12.0, reg.PdfWidth + 6.0);
            decimal h = (decimal)Math.Max(12.0, reg.PdfHeight + 6.0);

            pageBuilder.SetStrokeColor(r, g, b);
            pageBuilder.DrawRectangle(new PdfPoint((double)x, (double)y), w, h, 2.2m, false);

            // Indicateur dans la marge gauche
            pageBuilder.DrawLine(new PdfPoint(6.0, (double)y), new PdfPoint(6.0, (double)(y + h)), 8.0m);

            // Badge d'annotation au-dessus du cadre visuel
            decimal badgeY = y + h + 2m;
            pageBuilder.SetTextAndFillColor(r, g, b);
            pageBuilder.DrawRectangle(new PdfPoint((double)x, (double)badgeY), Math.Min(w, 135m), 11m, 0m, true);
            pageBuilder.SetTextAndFillColor(255, 255, 255);
            SafeAddText(pageBuilder, $"VISUEL (SSIM {reg.LocalSsim * 100.0:0.0}%)", 7m, new PdfPoint((double)(x + 3m), (double)(badgeY + 2.5m)), fontBold);
        }
    }

    public void DrawPageStamp(PdfPageBuilder pageBuilder, string text, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal xPosition = 10m;
        decimal yPosition = 10m;

        pageBuilder.SetTextAndFillColor(0, 50, 150);
        SafeAddText(pageBuilder, text, 14m, new PdfPoint((double)xPosition, (double)yPosition), fontBold);
    }

    public void DrawPageStatusHeader(
        PdfPageBuilder pageBuilder,
        string roleLabel,
        int pageNumber,
        int totalPages,
        int diffSegmentsCount,
        int visualRegionsCount,
        PdfDocumentBuilder.AddedFont font,
        PdfDocumentBuilder.AddedFont fontBold)
    {
        bool hasDiffs = diffSegmentsCount > 0 || visualRegionsCount > 0;
        byte bgR = hasDiffs ? (byte)220 : (byte)16;
        byte bgG = hasDiffs ? (byte)38 : (byte)185;
        byte bgB = hasDiffs ? (byte)38 : (byte)129;

        pageBuilder.SetTextAndFillColor(bgR, bgG, bgB);
        pageBuilder.DrawRectangle(new PdfPoint(10, 8), 265m, 16m, 0m, true);

        pageBuilder.SetTextAndFillColor(255, 255, 255);
        string statusText = hasDiffs
            ? $"{roleLabel} • P.{pageNumber}/{totalPages} • {diffSegmentsCount} TEXTE / {visualRegionsCount} VISUEL"
            : $"{roleLabel} • P.{pageNumber}/{totalPages} • PAGE CONFORME";

        SafeAddText(pageBuilder, statusText, 8.5m, new PdfPoint(15, 12.5), fontBold);
    }

    public decimal DrawTextLines(PdfPageBuilder page, string text, decimal startY, decimal startX, int maxChars, byte r, byte g, byte b, PdfDocumentBuilder.AddedFont fontToUse)
    {
        decimal currentY = startY;
        page.SetTextAndFillColor(r, g, b);

        foreach (var line in WrapText(text, maxChars))
        {
            SafeAddText(page, line, DefaultFontSize, new PdfPoint((double)startX, (double)currentY), fontToUse);
            currentY -= LineHeight;
        }

        return currentY;
    }

    public decimal DrawMixedTextLines(PdfPageBuilder page, List<(string Text, byte r, byte g, byte b, bool isBold)> chunks, decimal startY, decimal startX, decimal maxWidth, PdfDocumentBuilder.AddedFont fontNormal, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal currentY = startY;
        decimal currentX = startX;
        int linesRendered = 0;
        const int MaxLinesPerBlock = 16;

        foreach (var chunk in chunks)
        {
            if (string.IsNullOrEmpty(chunk.Text)) continue;

            var words = Regex.Split(chunk.Text, @"(?<=\s+)");
            var fontToUse = chunk.isBold ? fontBold : fontNormal;

            foreach (var word in words)
            {
                if (string.IsNullOrEmpty(word)) continue;

                decimal wordWidth = MeasureStringWidth(word, DefaultFontSize, chunk.isBold);

                if (currentX + wordWidth > startX + maxWidth && currentX > startX)
                {
                    linesRendered++;
                    currentY -= LineHeight;
                    currentX = startX;

                    if (linesRendered >= MaxLinesPerBlock || currentY < 45m)
                    {
                        page.SetTextAndFillColor(120, 120, 120);
                        SafeAddText(page, "... [Bloc long tronqué — voir synthèse HTML ou PDF individuel]", 8.5m, new PdfPoint((double)startX, (double)currentY), fontNormal);
                        return currentY - LineHeight;
                    }

                    if (string.IsNullOrWhiteSpace(word)) continue;
                }

                page.SetTextAndFillColor(chunk.r, chunk.g, chunk.b);
                SafeAddText(page, word.Replace("\n", ""), DefaultFontSize, new PdfPoint((double)currentX, (double)currentY), fontToUse);

                currentX += wordWidth;
            }
        }

        if (currentX > startX) currentY -= LineHeight;

        return currentY;
    }

    public void DrawStatBox(PdfPageBuilder page, decimal x, decimal y, string label, string value, PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold, byte rValue, byte gValue, byte bValue)
    {
        page.SetTextAndFillColor(245, 247, 250);
        page.DrawRectangle(new PdfPoint((double)x, (double)(y - 25m)), 140m, 45m, 0m, true);
        page.SetTextAndFillColor(100, 100, 100);
        SafeAddText(page, label, 9m, new PdfPoint((double)(x + 10m), (double)(y + 2m)), font);
        page.SetTextAndFillColor(rValue, gValue, bValue);
        SafeAddText(page, value, 18m, new PdfPoint((double)(x + 10m), (double)(y - 18m)), fontBold);
    }

    private decimal MeasureStringWidth(string text, decimal fontSize, bool isBold)
    {
        decimal width = 0m;
        foreach (char c in text)
        {
            if (c == ' ') width += 0.278m;
            else if (c == 'i' || c == 'j' || c == 'l') width += 0.222m;
            else if (c == 'f' || c == 't' || c == 'I') width += 0.278m;
            else if (c == 'm' || c == 'w' || c == 'M' || c == 'W') width += 0.833m;
            else if (char.IsUpper(c)) width += 0.667m;
            else width += 0.556m;
        }
        return width * fontSize * (isBold ? 1.05m : 1.0m);
    }

    private IEnumerable<string> WrapText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var currentLine = new StringBuilder();

        foreach (var word in words)
        {
            if (currentLine.Length + word.Length + 1 > maxLength && currentLine.Length > 0)
            {
                yield return currentLine.ToString();
                currentLine.Clear();
            }

            if (currentLine.Length > 0) currentLine.Append(' ');
            currentLine.Append(word);
        }

        if (currentLine.Length > 0)
        {
            yield return currentLine.ToString();
        }
    }
}
