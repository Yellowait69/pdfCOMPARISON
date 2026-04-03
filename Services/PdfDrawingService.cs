using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    // Constantes de mise en page pour éviter les "Magic Numbers"
    private const decimal DefaultFontSize = 10m;
    private const decimal LineHeight = 13m;
    private const decimal AlignmentTolerance = 5.0m;

    public (PdfDocumentBuilder.AddedFont Font, PdfDocumentBuilder.AddedFont FontBold) LoadFonts(PdfDocumentBuilder builder)
    {
        try
        {
            // Tentative de chargement des polices système (Windows)
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
            // Ignorer silencieusement les erreurs d'accès aux dossiers système
        }

        // FALLBACK SÉCURISÉ : Utilisation des polices natives PDF (Standard 14)
        // Garantit que l'application ne crashera pas sur Linux/Mac ou si Arial est absent.
        var fallbackFont = builder.AddStandard14Font(Standard14Font.Helvetica);
        var fallbackFontBold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        return (fallbackFont, fallbackFontBold);
    }

    public void DrawDiffMarkup(PdfPageBuilder pageBuilder, IEnumerable<LetterLoc> letters, byte r, byte g, byte b, MarkupStyle style)
    {
        var sorted = letters
            .OrderByDescending(l => Math.Round(l.BaselineY / AlignmentTolerance) * AlignmentTolerance)
            .ThenBy(l => l.BoundingBox.BottomLeft.X)
            .ToList();

        if (sorted.Count == 0) return;

        var segments = new List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)>();
        var first = sorted[0];

        decimal cMinX = (decimal)first.BoundingBox.BottomLeft.X;
        decimal cMaxX = (decimal)first.BoundingBox.TopRight.X;
        decimal cBaseline = first.BaselineY;
        decimal cFontSize = first.FontSize;

        for (int i = 1; i < sorted.Count; i++)
        {
            var loc = sorted[i];
            decimal x = (decimal)loc.BoundingBox.BottomLeft.X;
            decimal y = loc.BaselineY;

            bool isSameLine = Math.Abs(Math.Round(y / AlignmentTolerance) * AlignmentTolerance - Math.Round(cBaseline / AlignmentTolerance) * AlignmentTolerance) < 1m;
            decimal maxGap = Math.Max(15m, cFontSize * 1.5m);

            if (isSameLine && (x - cMaxX) < maxGap && x >= cMinX - 5m)
            {
                cMaxX = Math.Max(cMaxX, (decimal)loc.BoundingBox.TopRight.X);
                cFontSize = Math.Max(cFontSize, loc.FontSize);
            }
            else
            {
                segments.Add((cMinX, cMaxX, cBaseline, cFontSize));
                cMinX = x;
                cMaxX = (decimal)loc.BoundingBox.TopRight.X;
                cBaseline = y;
                cFontSize = loc.FontSize;
            }
        }
        segments.Add((cMinX, cMaxX, cBaseline, cFontSize));

        pageBuilder.SetStrokeColor(r, g, b);

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
                    // STYLE "ÉDITEUR DE CODE MODERNE" (ex: GitHub, VS Code)

                    // Calcul des dimensions de la boîte de contour
                    decimal padding = seg.fontSize * 0.15m;
                    decimal boxX = seg.minX - padding;
                    decimal boxY = seg.baselineY - (seg.fontSize * 0.2m);
                    decimal boxWidth = (seg.maxX - seg.minX) + (padding * 2);
                    decimal boxHeight = seg.fontSize * 1.1m;

                    // 1. Encadrement net autour du mot
                    pageBuilder.DrawRectangle(
                        new PdfPoint((double)boxX, (double)boxY),
                        boxWidth,
                        boxHeight,
                        3.0m,     // Épaisseur du trait fin et élégant
                        false);   // false = pas de remplissage, le texte en dessous reste 100% visible !

                    // 2. Barre verticale épaisse dans la marge gauche pour attirer l'œil
                    decimal marginX = 10m; // Positionnée tout à gauche de la page
                    pageBuilder.DrawLine(
                        new PdfPoint((double)marginX, (double)boxY),
                        new PdfPoint((double)marginX, (double)(boxY + boxHeight)),
                        15.0m      // Trait très épais et visible
                    );
                    break;
            }
        }
    }

    public void DrawPageStamp(PdfPageBuilder pageBuilder, string text, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal yPosition = Math.Max((decimal)pageBuilder.PageSize.Height - 30m, 10m);

        // CORRECTION : On réinitialise explicitement la couleur de contour (Stroke) en Blanc
        // pour empêcher que la couleur d'une différence (Rouge, Vert, Orange) ne "fuite" sur ce cadre.
        pageBuilder.SetStrokeColor(255, 255, 255);
        pageBuilder.SetTextAndFillColor(255, 255, 255);

        // On dessine le fond blanc pour masquer le texte du PDF en dessous, sans aucune bordure visible
        pageBuilder.DrawRectangle(new PdfPoint(10.0, (double)yPosition), 300m, 20m, 0m, true);

        // On écrit le texte en Bleu standard
        pageBuilder.SetTextAndFillColor(0, 50, 150);
        pageBuilder.AddText(text, 12m, new PdfPoint(15.0, (double)(yPosition + 5m)), fontBold);
    }

    public decimal DrawTextLines(PdfPageBuilder page, string text, decimal startY, decimal startX, int maxChars, byte r, byte g, byte b, PdfDocumentBuilder.AddedFont fontToUse)
    {
        decimal currentY = startY;
        page.SetTextAndFillColor(r, g, b);

        // Utilisation du nouvel algorithme de WrapText
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

                // Retour à la ligne si on dépasse la largeur max
                if (currentX + wordWidth > startX + maxWidth && currentX > startX)
                {
                    currentY -= LineHeight;
                    currentX = startX;

                    if (string.IsNullOrWhiteSpace(word)) continue; // Ignore les espaces en début de ligne
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

    // ==========================================
    // MÉTHODES UTILITAIRES PRIVÉES
    // ==========================================

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

    /// <summary>
    /// Découpe un texte proprement en respectant les mots entiers (Word-Wrap).
    /// </summary>
    private IEnumerable<string> WrapText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var currentLine = new StringBuilder();

        foreach (var word in words)
        {
            // Vérifie si l'ajout du mot dépasse la limite
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