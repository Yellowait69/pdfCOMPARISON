using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace PDFComparison.Services;

public interface IPdfDrawingService
{
    (PdfDocumentBuilder.AddedFont Font, PdfDocumentBuilder.AddedFont FontBold) LoadFonts(PdfDocumentBuilder builder);
    void DrawDiffMarkup(PdfPageBuilder pageBuilder, IEnumerable<LetterLoc> letters, byte r, byte g, byte b, MarkupStyle style);
    void DrawPageStamp(PdfPageBuilder pageBuilder, string text, PdfDocumentBuilder.AddedFont fontBold);
    decimal DrawTextLines(PdfPageBuilder page, string text, decimal startY, decimal startX, int maxChars, byte r, byte g, byte b, PdfDocumentBuilder.AddedFont fontToUse);
    decimal DrawMixedTextLines(PdfPageBuilder page, List<(string Text, byte r, byte g, byte b, bool isBold)> chunks, decimal startY, decimal startX, decimal maxWidth, PdfDocumentBuilder.AddedFont fontNormal, PdfDocumentBuilder.AddedFont fontBold);
    void DrawStatBox(PdfPageBuilder page, decimal x, decimal y, string label, string value, PdfDocumentBuilder.AddedFont font, PdfDocumentBuilder.AddedFont fontBold, byte rValue, byte gValue, byte bValue);
}

public class PdfDrawingService : IPdfDrawingService
{
    private const decimal DefaultFontSize = 10m;
    private const decimal LineHeight = 13m;

    public (PdfDocumentBuilder.AddedFont Font, PdfDocumentBuilder.AddedFont FontBold) LoadFonts(PdfDocumentBuilder builder)
    {
        try
        {
            string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string arialPath = Path.Combine(fontsFolder, "arial.ttf");
            string arialBoldPath = Path.Combine(fontsFolder, "arialbd.ttf");

            if (File.Exists(arialPath) && File.Exists(arialBoldPath))
            {
                return (builder.AddTrueTypeFont(File.ReadAllBytes(arialPath)),
                        builder.AddTrueTypeFont(File.ReadAllBytes(arialBoldPath)));
            }
        }
        catch
        {
            // Fallback silencieux vers les polices standard
        }

        var fallbackFont = builder.AddStandard14Font(Standard14Font.Helvetica);
        var fallbackFontBold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        return (fallbackFont, fallbackFontBold);
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
                decimal marginX = 10m;
                pageBuilder.DrawLine(
                    new PdfPoint((double)marginX, (double)mb.minY),
                    new PdfPoint((double)marginX, (double)mb.maxY),
                    15.0m
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
                    decimal padding = seg.fontSize * 0.15m;
                    decimal boxX = seg.minX - padding;
                    decimal boxY = seg.baselineY - (seg.fontSize * 0.2m);
                    decimal boxWidth = (seg.maxX - seg.minX) + (padding * 2);
                    decimal boxHeight = seg.fontSize * 1.1m;

                    pageBuilder.DrawRectangle(
                        new PdfPoint((double)boxX, (double)boxY),
                        boxWidth,
                        boxHeight,
                        3.0m,
                        false);
                    break;
            }
        }
    }

    public void DrawPageStamp(PdfPageBuilder pageBuilder, string text, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal xPosition = 10m;
        decimal yPosition = 10m;

        pageBuilder.SetTextAndFillColor(0, 50, 150);
        pageBuilder.AddText(text, 14m, new PdfPoint((double)xPosition, (double)yPosition), fontBold);
    }

    public decimal DrawTextLines(PdfPageBuilder page, string text, decimal startY, decimal startX, int maxChars, byte r, byte g, byte b, PdfDocumentBuilder.AddedFont fontToUse)
    {
        decimal currentY = startY;
        page.SetTextAndFillColor(r, g, b);

        foreach (var line in WrapText(text, maxChars))
        {
            page.AddText(line, DefaultFontSize, new PdfPoint((double)startX, (double)currentY), fontToUse);
            currentY -= LineHeight;
        }

        return currentY;
    }

    public decimal DrawMixedTextLines(PdfPageBuilder page, List<(string Text, byte r, byte g, byte b, bool isBold)> chunks, decimal startY, decimal startX, decimal maxWidth, PdfDocumentBuilder.AddedFont fontNormal, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal currentY = startY;
        decimal currentX = startX;

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
                    currentY -= LineHeight;
                    currentX = startX;

                    if (string.IsNullOrWhiteSpace(word)) continue;
                }

                page.SetTextAndFillColor(chunk.r, chunk.g, chunk.b);
                page.AddText(word.Replace("\n", ""), DefaultFontSize, new PdfPoint((double)currentX, (double)currentY), fontToUse);

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
        page.AddText(label, 9m, new PdfPoint((double)(x + 10m), (double)(y + 2m)), font);
        page.SetTextAndFillColor(rValue, gValue, bValue);
        page.AddText(value, 18m, new PdfPoint((double)(x + 10m), (double)(y - 18m)), fontBold);
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