using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PDFComparison.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Writer;

namespace PDFComparison.Services;

public interface IIndividualReportGenerator
{
    void GenerateIndividualReport(
        string sourcePath,
        string targetPath,
        string reportPath,
        VisualHighlights highlights,
        IReadOnlyList<VisualDiffRegion>? visualRegions = null,
        IReadOnlyList<DiffSummaryBlock>? diffBlocks = null,
        string? channel = null,
        string? productName = null);
}

public partial class IndividualReportGenerator : IIndividualReportGenerator
{
    private readonly IPdfDrawingService _drawingService;

    private static readonly (byte R, byte G, byte B) ColorRedSource = (239, 68, 68);
    private static readonly (byte R, byte G, byte B) ColorGreenTarget = (16, 185, 129);

    [GeneratedRegex(@"[\\/](?<channel>Bank|Broker)[\\/](?<product>[^\\/]+)[\\/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TidesPathChannelProductRegex();

    public IndividualReportGenerator(IPdfDrawingService drawingService)
    {
        _drawingService = drawingService ?? throw new ArgumentNullException(nameof(drawingService));
    }

    public void GenerateIndividualReport(
        string sourcePath,
        string targetPath,
        string reportPath,
        VisualHighlights highlights,
        IReadOnlyList<VisualDiffRegion>? visualRegions = null,
        IReadOnlyList<DiffSummaryBlock>? diffBlocks = null,
        string? channel = null,
        string? productName = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Le chemin source est invalide.", nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Le chemin cible est invalide.", nameof(targetPath));
        if (string.IsNullOrWhiteSpace(reportPath)) throw new ArgumentException("Le chemin du rapport est invalide.", nameof(reportPath));
        if (highlights == null) throw new ArgumentNullException(nameof(highlights));

        string? directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var (resolvedChannel, resolvedProduct) = ResolveChannelAndProduct(sourcePath, targetPath, channel, productName);

        var sourceRedDict = GroupHighlightsByPage(highlights.SourceRed);
        var targetRedDict = GroupHighlightsByPage(highlights.TargetRed);

        using var sourceDoc = PdfDocument.Open(sourcePath, new ParsingOptions { ClipPaths = false });
        using var targetDoc = PdfDocument.Open(targetPath, new ParsingOptions { ClipPaths = false });

        int maxPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);
        const double dividerGap = 14.0;

        // Génération d'un PDF unique fusionné Côte-à-Côte (Source à gauche | Target à droite sur la même page large)
        // afin que les deux documents défilent exactement à la même vitesse
        var fusedBuilder = new PdfDocumentBuilder();
        var (font, fontBold) = _drawingService.LoadFonts(fusedBuilder);

        for (int pageIndex = 1; pageIndex <= maxPages; pageIndex++)
        {
            var sPage = pageIndex <= sourceDoc.NumberOfPages ? sourceDoc.GetPage(pageIndex) : null;
            var tPage = pageIndex <= targetDoc.NumberOfPages ? targetDoc.GetPage(pageIndex) : null;

            double sW = sPage?.Width > 0 ? sPage.Width : (tPage?.Width > 0 ? tPage.Width : 595.0);
            double tW = tPage?.Width > 0 ? tPage.Width : sW;
            double sH = sPage?.Height > 0 ? sPage.Height : 0.0;
            double tH = tPage?.Height > 0 ? tPage.Height : 0.0;
            double rawMaxH = Math.Max(sH, tH);
            double totalH = rawMaxH > 0 ? rawMaxH : 842.0;

            double offsetX = sW + dividerGap;
            double totalW = sW + dividerGap + tW;

            var widePage = fusedBuilder.AddPage(totalW, totalH);

            // 1. Moitié gauche : Document Attendu (Source)
            if (sPage != null)
            {
                widePage.CurrentStream.Operations.Add(Push.Value);
                widePage.CopyFrom(sPage);
                widePage.CurrentStream.Operations.Add(Pop.Value);
            }
            else
            {
                widePage.SetTextAndFillColor(248, 250, 252);
                widePage.DrawRectangle(new PdfPoint(0, 0), (decimal)sW, (decimal)totalH, 0m, true);
                _drawingService.DrawTextLines(widePage, $"Page {pageIndex} absente dans Source", (decimal)(totalH * 0.5), 40m, 50, 148, 163, 184, fontBold);
            }

            // 2. Moitié droite : Document Obtenu (Cible), translatée de offsetX
            widePage.NewContentStreamAfter();
            if (tPage != null)
            {
                widePage.CurrentStream.Operations.Add(Push.Value);
                widePage.CurrentStream.Operations.Add(new ModifyCurrentTransformationMatrix(new decimal[] { 1m, 0m, 0m, 1m, (decimal)offsetX, 0m }));
                widePage.CopyFrom(tPage);
                widePage.CurrentStream.Operations.Add(Pop.Value);
            }
            else
            {
                widePage.SetTextAndFillColor(248, 250, 252);
                widePage.DrawRectangle(new PdfPoint(offsetX, 0), (decimal)tW, (decimal)totalH, 0m, true);
                _drawingService.DrawTextLines(widePage, $"Page {pageIndex} absente dans Cible", (decimal)(totalH * 0.5), (decimal)offsetX + 40m, 50, 148, 163, 184, fontBold);
            }

            // 3. Séparateur central + encadrés des différences (Rouge à gauche, Vert à droite)
            widePage.NewContentStreamAfter();

            // Séparateur vertical net entre Source et Cible
            widePage.SetTextAndFillColor(15, 23, 42);
            widePage.DrawRectangle(new PdfPoint(sW, 0), (decimal)dividerGap, (decimal)totalH, 0m, true);

            // Fine barre couleur en haut de chaque page (Rouge = Attendu, Vert = Obtenu)
            widePage.SetTextAndFillColor(ColorRedSource.R, ColorRedSource.G, ColorRedSource.B);
            widePage.DrawRectangle(new PdfPoint(0, totalH - 3.0), (decimal)sW, 3.0m, 0m, true);

            widePage.SetTextAndFillColor(ColorGreenTarget.R, ColorGreenTarget.G, ColorGreenTarget.B);
            widePage.DrawRectangle(new PdfPoint(offsetX, totalH - 3.0), (decimal)tW, 3.0m, 0m, true);

            int sTextCount = 0;
            if (sourceRedDict.TryGetValue(pageIndex, out var sRed) && sRed.Count > 0)
            {
                sTextCount = VisualSegmentHelper.CountBlocks(VisualSegmentHelper.GetSegments(sRed));
                _drawingService.DrawDiffMarkup(widePage, sRed, ColorRedSource.R, ColorRedSource.G, ColorRedSource.B, MarkupStyle.Highlight, offsetX: 0m);
            }

            int tTextCount = 0;
            if (targetRedDict.TryGetValue(pageIndex, out var tRed) && tRed.Count > 0)
            {
                tTextCount = VisualSegmentHelper.CountBlocks(VisualSegmentHelper.GetSegments(tRed));
                _drawingService.DrawDiffMarkup(widePage, tRed, ColorGreenTarget.R, ColorGreenTarget.G, ColorGreenTarget.B, MarkupStyle.Highlight, offsetX: (decimal)offsetX);
            }

            if (sPage != null)
            {
                _drawingService.DrawPageStatusHeader(widePage, "ATTENDU (SOURCE)", pageIndex, sourceDoc.NumberOfPages, sTextCount, 0, font, fontBold, offsetX: 0m);
            }

            if (tPage != null)
            {
                _drawingService.DrawPageStatusHeader(widePage, "OBTENU (CIBLE)", pageIndex, targetDoc.NumberOfPages, tTextCount, 0, font, fontBold, offsetX: (decimal)offsetX);
            }
        }

        File.WriteAllBytes(reportPath, fusedBuilder.Build());

        string dir = directory ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(reportPath);

        // Nettoyer d'éventuels anciens fichiers séparés obsolètes
        TryDeleteFileQuietly(Path.Combine(dir, $"{stem}_SOURCE.pdf"));
        TryDeleteFileQuietly(Path.Combine(dir, $"{stem}_TARGET.pdf"));

        string sideBySideHtmlPath = Path.Combine(dir, $"{stem}_SideBySide.html");

        var diffPages = sourceRedDict.Keys
            .Concat(targetRedDict.Keys)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        GenerateSideBySideHtmlViewer(
            sideBySideHtmlPath,
            Path.GetFileName(sourcePath),
            Path.GetFileName(reportPath),
            diffPages,
            diffBlocks,
            resolvedChannel,
            resolvedProduct);
    }

    private static (string Channel, string Product) ResolveChannelAndProduct(
        string sourcePath,
        string targetPath,
        string? explicitChannel,
        string? explicitProduct)
    {
        string ch = explicitChannel?.Trim() ?? string.Empty;
        string prod = explicitProduct?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(ch) || !string.IsNullOrWhiteSpace(prod))
        {
            return (ch, prod);
        }

        foreach (string candidatePath in new[] { sourcePath, targetPath })
        {
            if (string.IsNullOrWhiteSpace(candidatePath)) continue;
            var m = TidesPathChannelProductRegex().Match(candidatePath);
            if (m.Success)
            {
                return (m.Groups["channel"].Value, m.Groups["product"].Value);
            }
        }

        return (string.Empty, string.Empty);
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Ignorer si le fichier est ouvert
        }
    }

    private static void GenerateSideBySideHtmlViewer(
        string htmlPath,
        string sourceFileName,
        string fusedPdfFile,
        List<int> diffPages,
        IReadOnlyList<DiffSummaryBlock>? diffBlocks,
        string channel,
        string productName)
    {
        int firstDiffPage = diffPages.Count > 0 ? diffPages[0] : 1;

        string badgeText = !string.IsNullOrWhiteSpace(channel) && !string.IsNullOrWhiteSpace(productName)
            ? $"Canal : {channel}  •  Produit : {productName}"
            : (!string.IsNullOrWhiteSpace(productName)
                ? $"Produit : {productName}"
                : (!string.IsNullOrWhiteSpace(channel) ? $"Canal : {channel}" : string.Empty));

        string badgeClass = string.Equals(channel, "Broker", StringComparison.OrdinalIgnoreCase)
            ? "prod-badge broker"
            : "prod-badge";

        var sb = new StringBuilder(12288);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"fr\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine($"<title>Côte à Côte — {WebUtility.HtmlEncode(sourceFileName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(@"
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0f172a; color: #f8fafc; height: 100vh; display: flex; flex-direction: column; overflow: hidden; }
            .topbar { background: #1e293b; border-bottom: 1px solid #334155; padding: 8px 16px; display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
            .title-group { display: flex; align-items: center; gap: 10px; min-width: 0; flex: 1; }
            .prod-badge { background: #1d4ed8; color: #ffffff; border: 1px solid #3b82f6; padding: 3px 10px; border-radius: 6px; font-size: 12.5px; font-weight: 700; white-space: nowrap; }
            .prod-badge.broker { background: #6d28d9; border-color: #8b5cf6; }
            .doc-name { font-size: 14px; font-weight: 600; color: #e2e8f0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            .legend { display: flex; align-items: center; gap: 14px; font-size: 12px; font-weight: 600; white-space: nowrap; }
            .leg-src { color: #fca5a5; }
            .leg-trg { color: #6ee7b7; }
            .diff-strip { background: #111827; border-bottom: 1px solid #334155; padding: 5px 16px; max-height: 98px; overflow-y: auto; display: flex; flex-direction: column; gap: 4px; }
            .diff-row { display: grid; grid-template-columns: 64px 1fr 22px 1fr; gap: 8px; align-items: center; background: #1e293b; border: 1px solid #334155; border-radius: 6px; padding: 3px 10px; font-size: 12px; cursor: pointer; }
            .diff-row:hover { border-color: #3b82f6; background: #172554; }
            .old-txt { color: #fca5a5; background: rgba(239,68,68,0.14); border: 1px solid rgba(239,68,68,0.3); padding: 2px 8px; border-radius: 4px; font-weight: 600; word-break: break-word; white-space: pre-line; }
            .new-txt { color: #6ee7b7; background: rgba(16,185,129,0.14); border: 1px solid rgba(16,185,129,0.3); padding: 2px 8px; border-radius: 4px; font-weight: 600; word-break: break-word; white-space: pre-line; }
            .viewer-wrap { flex: 1; display: flex; min-height: 0; background: #0f172a; }
            .pdf-frame { flex: 1; width: 100%; height: 100%; border: none; background: #0f172a; }
        ");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<div class=\"topbar\">");
        sb.AppendLine("<div class=\"title-group\">");
        if (!string.IsNullOrWhiteSpace(badgeText))
        {
            sb.AppendLine($"<span class=\"{badgeClass}\">{WebUtility.HtmlEncode(badgeText)}</span>");
        }
        sb.AppendLine($"<span class=\"doc-name\" title=\"{WebUtility.HtmlEncode(sourceFileName)}\">{WebUtility.HtmlEncode(sourceFileName)}</span>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"legend\">");
        sb.AppendLine("<span class=\"leg-src\">🔴 Gauche : Attendu (Source)</span>");
        sb.AppendLine("<span class=\"leg-trg\">🟢 Droite : Obtenu (Cible)</span>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        if (diffBlocks != null && diffBlocks.Count > 0)
        {
            sb.AppendLine("<div class=\"diff-strip\">");
            foreach (var b in diffBlocks)
            {
                sb.AppendLine($"<div class=\"diff-row\" onclick=\"goToPage({b.PageNumber})\" title=\"Aller à la page {b.PageNumber}\">");
                sb.AppendLine($"<span style=\"font-weight:700; color:#93c5fd;\">Page {b.PageNumber}</span>");
                sb.AppendLine($"<div class=\"old-txt\">{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(b.OldText) ? "(Absent)" : b.OldText)}</div>");
                sb.AppendLine("<span style=\"text-align:center; color:#94a3b8; font-weight:700;\">➔</span>");
                sb.AppendLine($"<div class=\"new-txt\">{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(b.NewText) ? "(Supprimé)" : b.NewText)}</div>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        string fusedUrl = $"{Uri.EscapeDataString(fusedPdfFile)}#page={firstDiffPage}&view=FitH";

        sb.AppendLine("<div class=\"viewer-wrap\">");
        sb.AppendLine($"<iframe id=\"frameFused\" class=\"pdf-frame\" src=\"{fusedUrl}\"></iframe>");
        sb.AppendLine("</div>");

        sb.AppendLine($@"
        <script>
            const pdfBase = '{Uri.EscapeDataString(fusedPdfFile)}';

            function goToPage(pageNum) {{
                const oldFrame = document.getElementById('frameFused');
                if (!oldFrame) return;
                const nextFrame = oldFrame.cloneNode(false);
                nextFrame.src = pdfBase + '#page=' + pageNum + '&view=FitH';
                oldFrame.replaceWith(nextFrame);
            }}

            window.addEventListener('DOMContentLoaded', () => {{
                const m = window.location.hash.match(/page=(\d+)/i);
                if (m && m[1]) {{
                    goToPage(parseInt(m[1], 10));
                }}
            }});
        </script>
        </body></html>");

        File.WriteAllText(htmlPath, sb.ToString(), Encoding.UTF8);
    }

    private Dictionary<int, List<LetterLoc>> GroupHighlightsByPage(IEnumerable<LetterLoc> letters)
    {
        return letters
            .GroupBy(l => l.PageNumber)
            .ToDictionary(g => g.Key, g => g.ToList());
    }
}
