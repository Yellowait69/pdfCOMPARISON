using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace PDFComparison.Services;

public class PdfReportGenerator
{
    public void GenerateIndividualReport(string sourcePath, string targetPath, string reportPath, VisualHighlights highlights)
    {
        // Sécurisation de la création du dossier parent
        string? directory = Path.GetDirectoryName(reportPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new PdfDocumentBuilder();
        var (font, fontBold) = LoadFonts(builder);

        // Utilisation des Using Declarations (C# 8+) :
        // Supprime les accolades et l'indentation imbriquée. Les documents seront fermés (Dispose)
        // automatiquement à la fin de la méthode.
        using var sourceDoc = PdfDocument.Open(sourcePath);
        using var targetDoc = PdfDocument.Open(targetPath);

        int maxPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);

        for (int pageIndex = 1; pageIndex <= maxPages; pageIndex++)
        {
            if (pageIndex <= sourceDoc.NumberOfPages)
            {
                var sPage = builder.AddPage(sourceDoc, pageIndex);
                DrawDiffMarkup(sPage, highlights.SourceRed.Where(w => w.PageNumber == pageIndex), 220, 20, 20, MarkupStyle.Strikethrough);
                DrawDiffMarkup(sPage, highlights.SourceYellow.Where(w => w.PageNumber == pageIndex), 255, 140, 0, MarkupStyle.Box);
                DrawPageStamp(sPage, $"[ DOCUMENT SOURCE - Page {pageIndex} ]", fontBold);
            }

            if (pageIndex <= targetDoc.NumberOfPages)
            {
                var tPage = builder.AddPage(targetDoc, pageIndex);
                DrawDiffMarkup(tPage, highlights.TargetRed.Where(w => w.PageNumber == pageIndex), 20, 180, 20, MarkupStyle.Underline);
                DrawDiffMarkup(tPage, highlights.TargetYellow.Where(w => w.PageNumber == pageIndex), 255, 140, 0, MarkupStyle.Box);
                DrawPageStamp(tPage, $"[ DOCUMENT CIBLE (Modifié) - Page {pageIndex} ]", fontBold);
            }
        }

        File.WriteAllBytes(reportPath, builder.Build());
    }

    public void GenerateGlobalSynthesisReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        string reportPath = Path.Combine(outputDiffDir, "Global_Synthesis_Report.pdf");
        Directory.CreateDirectory(outputDiffDir);

        var builder = new PdfDocumentBuilder();
        PdfPageBuilder page = builder.AddPage(842, 595);
        var (font, fontBold) = LoadFonts(builder);

        decimal margin = 40m;
        decimal yPosition = 595m - margin;
        decimal leftColumnX = margin;
        decimal rightColumnX = 430m;
        int maxCharsCol = 65;

        // Target-typed new (C# 9+) : On écrit new(margin, yPosition) au lieu de new PdfPoint(margin, yPosition)
        page.SetTextAndFillColor(0, 0, 0);
        page.AddText("SYNTHÈSE GLOBALE DES DIFFÉRENCES", 18m, new(margin, yPosition), fontBold);
        yPosition -= 20m;

        page.SetTextAndFillColor(100, 100, 100);
        page.AddText("Ce document présente une comparaison côte à côte des documents originaux et modifiés.", 10m, new(margin, yPosition), font);
        yPosition -= 40m;

        foreach (var doc in summaries.OrderBy(s => s.DocumentName))
        {
            if (yPosition < margin + 60m)
            {
                page = builder.AddPage(842, 595);
                yPosition = 595m - margin;
            }

            page.SetTextAndFillColor(0, 50, 150);
            page.AddText($"► Fichier: {doc.DocumentName}", 14m, new(margin, yPosition), fontBold);
            yPosition -= 25m;

            foreach (var block in doc.Blocks)
            {
                int linesLeft = (block.ContextBefore.Length + block.OldText.Length + block.ContextAfter.Length) / maxCharsCol;
                int linesRight = (block.ContextBefore.Length + block.NewText.Length + block.ContextAfter.Length) / maxCharsCol;
                decimal estimatedHeight = Math.Max(linesLeft, linesRight) * 13m + 60m;

                if (yPosition - estimatedHeight < margin)
                {
                    page = builder.AddPage(842, 595);
                    yPosition = 595m - margin;
                }

                string changeTypeStr = block.Type switch
                {
                    ChangeType.Inserted => "[ AJOUT ]",
                    ChangeType.Deleted => "[ SUPPRESSION ]",
                    ChangeType.Modified => "[ MODIFICATION ]",
                    _ => "[ CHANGEMENT ]"
                };

                page.SetTextAndFillColor(0, 0, 0);
                page.AddText(changeTypeStr, 11m, new(margin, yPosition), fontBold);

                yPosition -= 15m;
                page.SetTextAndFillColor(150, 150, 150);
                page.AddText("Document Original (Source)", 9m, new(leftColumnX, yPosition), fontBold);
                page.AddText("Document Modifié (Cible)", 9m, new(rightColumnX, yPosition), fontBold);
                yPosition -= 15m;

                decimal currentYLeft = yPosition;
                if (!string.IsNullOrWhiteSpace(block.ContextBefore)) currentYLeft = DrawTextLines(page, $"... {block.ContextBefore}", currentYLeft, leftColumnX, maxCharsCol, 100, 100, 100, font);
                if (!string.IsNullOrWhiteSpace(block.OldText)) currentYLeft = DrawTextLines(page, block.OldText, currentYLeft, leftColumnX, maxCharsCol, 200, 0, 0, fontBold);
                else currentYLeft = DrawTextLines(page, "(Aucun texte original)", currentYLeft, leftColumnX, maxCharsCol, 180, 180, 180, font);
                if (!string.IsNullOrWhiteSpace(block.ContextAfter)) currentYLeft = DrawTextLines(page, $"{block.ContextAfter} ...", currentYLeft, leftColumnX, maxCharsCol, 100, 100, 100, font);

                decimal currentYRight = yPosition;
                if (!string.IsNullOrWhiteSpace(block.ContextBefore)) currentYRight = DrawTextLines(page, $"... {block.ContextBefore}", currentYRight, rightColumnX, maxCharsCol, 100, 100, 100, font);
                if (!string.IsNullOrWhiteSpace(block.NewText)) currentYRight = DrawTextLines(page, block.NewText, currentYRight, rightColumnX, maxCharsCol, 0, 150, 0, fontBold);
                else currentYRight = DrawTextLines(page, "(Texte supprimé)", currentYRight, rightColumnX, maxCharsCol, 180, 180, 180, font);
                if (!string.IsNullOrWhiteSpace(block.ContextAfter)) currentYRight = DrawTextLines(page, $"{block.ContextAfter} ...", currentYRight, rightColumnX, maxCharsCol, 100, 100, 100, font);

                yPosition = Math.Min(currentYLeft, currentYRight) - 20m;
                page.SetStrokeColor(220, 220, 220);
                page.DrawLine(new(margin, yPosition + 10m), new(842m - margin, yPosition + 10m), 0.5m);
            }
            yPosition -= 20m;
        }

        File.WriteAllBytes(reportPath, builder.Build());
    }

    private void DrawDiffMarkup(PdfPageBuilder pageBuilder, IEnumerable<LetterLoc> letters, byte r, byte g, byte b, MarkupStyle style)
    {
        var sorted = letters.OrderByDescending(l => l.BaselineY).ThenBy(l => l.BoundingBox.BottomLeft.X).ToList();
        if (sorted.Count is 0) return;

        var segments = new List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)>();
        var first = sorted[0];
        decimal cMinX = (decimal)first.BoundingBox.BottomLeft.X, cMaxX = (decimal)first.BoundingBox.TopRight.X, cBaseline = first.BaselineY, cFontSize = first.FontSize;

        for (int i = 1; i < sorted.Count; i++)
        {
            var loc = sorted[i];
            decimal x = (decimal)loc.BoundingBox.BottomLeft.X, y = loc.BaselineY;

            if (Math.Abs(y - cBaseline) < 3m && (x - cMaxX) < 15m)
            {
                cMaxX = Math.Max(cMaxX, (decimal)loc.BoundingBox.TopRight.X);
                cFontSize = Math.Max(cFontSize, loc.FontSize);
            }
            else
            {
                segments.Add((cMinX, cMaxX, cBaseline, cFontSize));
                cMinX = x; cMaxX = (decimal)loc.BoundingBox.TopRight.X; cBaseline = y; cFontSize = loc.FontSize;
            }
        }
        segments.Add((cMinX, cMaxX, cBaseline, cFontSize));

        pageBuilder.SetStrokeColor(r, g, b);

        foreach (var seg in segments)
        {
            decimal strokeWidth = Math.Max(seg.fontSize * 0.08m, 0.75m);
            decimal width = seg.maxX - seg.minX;

            // Remplacement des suites de "if/else if" par un beau switch statement C#
            switch (style)
            {
                case MarkupStyle.Strikethrough:
                    pageBuilder.DrawLine(new(seg.minX, seg.baselineY + (seg.fontSize * 0.3m)), new(seg.maxX, seg.baselineY + (seg.fontSize * 0.3m)), strokeWidth);
                    break;
                case MarkupStyle.Underline:
                    pageBuilder.DrawLine(new(seg.minX, seg.baselineY - (seg.fontSize * 0.12m)), new(seg.maxX, seg.baselineY - (seg.fontSize * 0.12m)), strokeWidth);
                    break;
                case MarkupStyle.Box:
                    pageBuilder.DrawRectangle(new(seg.minX - 1m, seg.baselineY - (seg.fontSize * 0.15m)), width + 2m, seg.fontSize * 0.9m, strokeWidth, false);
                    break;
            }
        }
    }

    private void DrawPageStamp(PdfPageBuilder pageBuilder, string text, PdfDocumentBuilder.AddedFont fontBold)
    {
        decimal yPosition = Math.Max((decimal)pageBuilder.PageSize.Height - 30m, 10m);
        pageBuilder.SetTextAndFillColor(255, 255, 255);
        pageBuilder.DrawRectangle(new(10m, yPosition), 300m, 20m, 0m, true);
        pageBuilder.SetTextAndFillColor(0, 50, 150);
        pageBuilder.AddText(text, 12m, new(15m, yPosition + 5m), fontBold);
    }

    private decimal DrawTextLines(PdfPageBuilder page, string text, decimal startY, decimal startX, int maxChars, byte r, byte g, byte b, PdfDocumentBuilder.AddedFont fontToUse)
    {
        decimal currentY = startY;
        page.SetTextAndFillColor(r, g, b);

        foreach (var line in WrapText(text, maxChars))
        {
            page.AddText(line, 10m, new(startX, currentY), fontToUse);
            currentY -= 13m;
        }
        return currentY;
    }

    private (PdfDocumentBuilder.AddedFont Font, PdfDocumentBuilder.AddedFont FontBold) LoadFonts(PdfDocumentBuilder builder)
    {
        string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string arialPath = Path.Combine(fontsFolder, "arial.ttf");
        string arialBoldPath = Path.Combine(fontsFolder, "arialbd.ttf");

        if (!File.Exists(arialPath) || !File.Exists(arialBoldPath))
        {
            throw new FileNotFoundException("Required Arial fonts were not found.");
        }

        return (builder.AddTrueTypeFont(File.ReadAllBytes(arialPath)), builder.AddTrueTypeFont(File.ReadAllBytes(arialBoldPath)));
    }

    // OPTIMISATION MAJEURE : On utilise IEnumerable et "yield return".
    // Au lieu de créer et d'allouer une nouvelle List<string> en mémoire pour chaque bloc de texte,
    // l'itérateur renvoie les bouts de chaîne à la volée.
    // Sur des milliers de lignes de rapports, cela soulage grandement le Garbage Collector.
    private IEnumerable<string> WrapText(string text, int maxLength)
    {
        for (int i = 0; i < text.Length; i += maxLength)
        {
            yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
        }
    }
}