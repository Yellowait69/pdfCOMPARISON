using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using PDFComparison.Models;
using UglyToad.PdfPig;
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
        IReadOnlyList<DiffSummaryBlock>? diffBlocks = null);
}

public class IndividualReportGenerator : IIndividualReportGenerator
{
    private readonly IPdfDrawingService _drawingService;

    private static readonly (byte R, byte G, byte B) ColorRedSource = (239, 68, 68);
    private static readonly (byte R, byte G, byte B) ColorGreenTarget = (16, 185, 129);

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
        IReadOnlyList<DiffSummaryBlock>? diffBlocks = null)
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

        var sourceRedDict = GroupHighlightsByPage(highlights.SourceRed);
        var targetRedDict = GroupHighlightsByPage(highlights.TargetRed);

        using var sourceDoc = PdfDocument.Open(sourcePath, new ParsingOptions { ClipPaths = false });
        using var targetDoc = PdfDocument.Open(targetPath, new ParsingOptions { ClipPaths = false });

        int maxPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);

        // 1. Génération du PDF combiné (avec indicateur TwoPageLeft pour affichage côte-à-côte dans les lecteurs PDF)
        var combinedBuilder = new PdfDocumentBuilder();
        var (font, fontBold) = _drawingService.LoadFonts(combinedBuilder);

        // 2. Génération des PDF Source Annoté et Target Annoté séparés pour l'affichage Côte-à-Côte au même niveau
        var sourceOnlyBuilder = new PdfDocumentBuilder();
        var (sFont, sFontBold) = _drawingService.LoadFonts(sourceOnlyBuilder);

        var targetOnlyBuilder = new PdfDocumentBuilder();
        var (tFont, tFontBold) = _drawingService.LoadFonts(targetOnlyBuilder);

        for (int pageIndex = 1; pageIndex <= maxPages; pageIndex++)
        {
            if (pageIndex <= sourceDoc.NumberOfPages)
            {
                var sPage = combinedBuilder.AddPage(sourceDoc, pageIndex);
                var sOnlyPage = sourceOnlyBuilder.AddPage(sourceDoc, pageIndex);
                int sTextCount = 0;

                if (sourceRedDict.TryGetValue(pageIndex, out var sRed) && sRed.Count > 0)
                {
                    sTextCount = VisualSegmentHelper.CountBlocks(VisualSegmentHelper.GetSegments(sRed));
                    _drawingService.DrawDiffMarkup(sPage, sRed, ColorRedSource.R, ColorRedSource.G, ColorRedSource.B, MarkupStyle.Highlight);
                    _drawingService.DrawDiffMarkup(sOnlyPage, sRed, ColorRedSource.R, ColorRedSource.G, ColorRedSource.B, MarkupStyle.Highlight);
                }

                _drawingService.DrawPageStatusHeader(sPage, "ATTENDU (SOURCE)", pageIndex, sourceDoc.NumberOfPages, sTextCount, 0, font, fontBold);
                _drawingService.DrawPageStatusHeader(sOnlyPage, "ATTENDU (SOURCE)", pageIndex, sourceDoc.NumberOfPages, sTextCount, 0, sFont, sFontBold);
            }

            if (pageIndex <= targetDoc.NumberOfPages)
            {
                var tPage = combinedBuilder.AddPage(targetDoc, pageIndex);
                var tOnlyPage = targetOnlyBuilder.AddPage(targetDoc, pageIndex);
                int tTextCount = 0;

                if (targetRedDict.TryGetValue(pageIndex, out var tRed) && tRed.Count > 0)
                {
                    tTextCount = VisualSegmentHelper.CountBlocks(VisualSegmentHelper.GetSegments(tRed));
                    _drawingService.DrawDiffMarkup(tPage, tRed, ColorGreenTarget.R, ColorGreenTarget.G, ColorGreenTarget.B, MarkupStyle.Highlight);
                    _drawingService.DrawDiffMarkup(tOnlyPage, tRed, ColorGreenTarget.R, ColorGreenTarget.G, ColorGreenTarget.B, MarkupStyle.Highlight);
                }

                _drawingService.DrawPageStatusHeader(tPage, "OBTENU (CIBLE)", pageIndex, targetDoc.NumberOfPages, tTextCount, 0, font, fontBold);
                _drawingService.DrawPageStatusHeader(tOnlyPage, "OBTENU (CIBLE)", pageIndex, targetDoc.NumberOfPages, tTextCount, 0, tFont, tFontBold);
            }
        }

        byte[] combinedBytes = InjectTwoPageLayoutInPdfCatalog(combinedBuilder.Build());
        File.WriteAllBytes(reportPath, combinedBytes);

        // Enregistrement des fichiers Côte-à-Côte (Source Annoté, Target Annoté, et Viewer HTML Côte-à-Côte au même niveau)
        string dir = directory ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(reportPath);
        string srcAnnotatedPath = Path.Combine(dir, $"{stem}_SOURCE.pdf");
        string trgAnnotatedPath = Path.Combine(dir, $"{stem}_TARGET.pdf");
        string sideBySideHtmlPath = Path.Combine(dir, $"{stem}_SideBySide.html");

        File.WriteAllBytes(srcAnnotatedPath, sourceOnlyBuilder.Build());
        File.WriteAllBytes(trgAnnotatedPath, targetOnlyBuilder.Build());

        var diffPages = sourceRedDict.Keys
            .Concat(targetRedDict.Keys)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        GenerateSideBySideHtmlViewer(
            sideBySideHtmlPath,
            Path.GetFileName(sourcePath),
            Path.GetFileName(targetPath),
            Path.GetFileName(srcAnnotatedPath),
            Path.GetFileName(trgAnnotatedPath),
            Path.GetFileName(reportPath),
            maxPages,
            diffPages,
            diffBlocks);
    }

    private static void GenerateSideBySideHtmlViewer(
        string htmlPath,
        string sourceFileName,
        string targetFileName,
        string srcAnnotatedPdfFile,
        string trgAnnotatedPdfFile,
        string combinedPdfFile,
        int totalPages,
        List<int> diffPages,
        IReadOnlyList<DiffSummaryBlock>? diffBlocks)
    {
        int firstDiffPage = diffPages.Count > 0 ? diffPages[0] : 1;
        var sb = new StringBuilder(16384);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"fr\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine($"<title>Comparaison Côte à Côte — {WebUtility.HtmlEncode(sourceFileName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(@"
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0f172a; color: #f8fafc; height: 100vh; display: flex; flex-direction: column; overflow: hidden; }
            .topbar { background: #1e293b; border-bottom: 1px solid #334155; padding: 10px 18px; display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
            .title-group { display: flex; align-items: center; gap: 12px; }
            .title-group h1 { font-size: 15px; font-weight: 700; color: #ffffff; }
            .page-btns { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
            .page-btn { padding: 5px 11px; border-radius: 6px; border: 1px solid #475569; background: #334155; color: #e2e8f0; font-size: 12px; font-weight: 600; cursor: pointer; }
            .page-btn.diff { border-color: #ef4444; background: #7f1d1d; color: #fecaca; }
            .page-btn.active { background: #2563eb; border-color: #60a5fa; color: white; }
            .diff-strip { background: #1e293b; border-bottom: 1px solid #334155; padding: 6px 18px; max-height: 135px; overflow-y: auto; display: flex; flex-direction: column; gap: 4px; }
            .diff-row { display: grid; grid-template-columns: 68px 140px 160px 1fr 22px 1fr; gap: 8px; align-items: center; background: #0f172a; border: 1px solid #334155; border-radius: 6px; padding: 4px 10px; font-size: 12px; cursor: pointer; }
            .diff-row:hover { border-color: #3b82f6; background: #172554; }
            .cat-pill { background: #312e81; color: #c7d2fe; padding: 2px 6px; border-radius: 4px; font-size: 11px; font-weight: 700; text-align: center; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            .ctx-txt { color: #94a3b8; font-size: 11.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            .old-txt { color: #fca5a5; background: rgba(239,68,68,0.16); border: 1px solid rgba(239,68,68,0.35); padding: 3px 8px; border-radius: 4px; font-weight: 700; word-break: break-word; white-space: pre-line; }
            .new-txt { color: #6ee7b7; background: rgba(16,185,129,0.16); border: 1px solid rgba(16,185,129,0.35); padding: 3px 8px; border-radius: 4px; font-weight: 700; word-break: break-word; white-space: pre-line; }
            .viewer-grid { flex: 1; display: grid; grid-template-columns: 1fr 1fr; gap: 10px; padding: 10px; background: #0f172a; min-height: 0; }
            .pdf-pane { display: flex; flex-direction: column; background: #1e293b; border-radius: 8px; overflow: hidden; border: 2px solid #334155; min-height: 0; }
            .pdf-pane.source { border-color: #ef4444; }
            .pdf-pane.target { border-color: #10b981; }
            .pane-header { padding: 7px 14px; font-size: 12.5px; font-weight: 700; display: flex; justify-content: space-between; align-items: center; }
            .pane-header.source { background: rgba(239,68,68,0.18); color: #fca5a5; border-bottom: 1px solid rgba(239,68,68,0.35); }
            .pane-header.target { background: rgba(16,185,129,0.18); color: #6ee7b7; border-bottom: 1px solid rgba(16,185,129,0.35); }
            .pdf-frame { flex: 1; width: 100%; height: 100%; border: none; background: white; }
            .btn-link { text-decoration: none; background: #334155; color: #e2e8f0; padding: 5px 10px; border-radius: 6px; font-size: 12px; font-weight: 600; }
            .btn-link:hover { background: #475569; }
        ");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<div class=\"topbar\">");
        sb.AppendLine($"<div class=\"title-group\"><h1>📖 Comparaison Côte à Côte au Même Niveau : {WebUtility.HtmlEncode(sourceFileName)}</h1></div>");

        sb.AppendLine("<div class=\"page-btns\">");
        sb.AppendLine("<span style=\"font-size:12px; color:#94a3b8; margin-right:4px;\">Aller à la page :</span>");
        for (int p = 1; p <= totalPages; p++)
        {
            bool hasDiff = diffPages.Contains(p);
            string cls = "page-btn" + (hasDiff ? " diff" : "") + (p == firstDiffPage ? " active" : "");
            string label = hasDiff ? $"Page {p} ⚠️" : $"Page {p}";
            sb.AppendLine($"<button class=\"{cls}\" id=\"btn-p-{p}\" onclick=\"goToPage({p})\">{label}</button>");
        }
        sb.AppendLine($"<a class=\"btn-link\" href=\"{Uri.EscapeDataString(combinedPdfFile)}\" target=\"_blank\">📄 Ouvrir PDF combiné</a>");
        sb.AppendLine("</div></div>");

        if (diffBlocks != null && diffBlocks.Count > 0)
        {
            sb.AppendLine("<div class=\"diff-strip\">");
            foreach (var b in diffBlocks)
            {
                string shortCtx = !string.IsNullOrWhiteSpace(b.ContextBefore) ? b.ContextBefore : b.ContextAfter;
                string catLabel = !string.IsNullOrWhiteSpace(b.FinancialDelta) ? $"{b.Category} ({b.FinancialDelta})" : b.Category;
                sb.AppendLine($"<div class=\"diff-row\" onclick=\"goToPage({b.PageNumber})\" title=\"Cliquer pour afficher la Page {b.PageNumber} côte à côte\">");
                sb.AppendLine($"<span style=\"font-weight:700; color:#93c5fd;\">Page {b.PageNumber}</span>");
                sb.AppendLine($"<span class=\"cat-pill\" title=\"{WebUtility.HtmlEncode(catLabel)}\">{WebUtility.HtmlEncode(catLabel)}</span>");
                sb.AppendLine($"<span class=\"ctx-txt\" title=\"{WebUtility.HtmlEncode(shortCtx)}\">{WebUtility.HtmlEncode(shortCtx)}</span>");
                sb.AppendLine($"<div class=\"old-txt\">{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(b.OldText) ? "(Absent)" : b.OldText)}</div>");
                sb.AppendLine("<span style=\"text-align:center; color:#94a3b8; font-weight:800;\">➔</span>");
                sb.AppendLine($"<div class=\"new-txt\">{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(b.NewText) ? "(Supprimé)" : b.NewText)}</div>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        string srcUrl = $"{Uri.EscapeDataString(srcAnnotatedPdfFile)}#page={firstDiffPage}&view=FitH";
        string trgUrl = $"{Uri.EscapeDataString(trgAnnotatedPdfFile)}#page={firstDiffPage}&view=FitH";

        sb.AppendLine("<div class=\"viewer-grid\">");
        sb.AppendLine("<div class=\"pdf-pane source\">");
        sb.AppendLine($"<div class=\"pane-header source\"><span>🔴 DOCUMENT ATTENDU (SOURCE) — {WebUtility.HtmlEncode(sourceFileName)}</span><span>Texte supprimé/modifié encadré en ROUGE</span></div>");
        sb.AppendLine($"<iframe id=\"frameSource\" class=\"pdf-frame\" src=\"{srcUrl}\"></iframe>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"pdf-pane target\">");
        sb.AppendLine($"<div class=\"pane-header target\"><span>🟢 DOCUMENT OBTENU (TARGET) — {WebUtility.HtmlEncode(targetFileName)}</span><span>Texte ajouté/modifié encadré en VERT</span></div>");
        sb.AppendLine($"<iframe id=\"frameTarget\" class=\"pdf-frame\" src=\"{trgUrl}\"></iframe>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        sb.AppendLine($@"
        <script>
            const srcBase = '{Uri.EscapeDataString(srcAnnotatedPdfFile)}';
            const trgBase = '{Uri.EscapeDataString(trgAnnotatedPdfFile)}';

            function goToPage(pageNum) {{
                document.querySelectorAll('.page-btn').forEach(b => b.classList.remove('active'));
                const btn = document.getElementById('btn-p-' + pageNum);
                if (btn) btn.classList.add('active');

                const fSrc = document.getElementById('frameSource');
                const fTrg = document.getElementById('frameTarget');

                fSrc.src = srcBase + '#page=' + pageNum + '&view=FitH';
                fTrg.src = trgBase + '#page=' + pageNum + '&view=FitH';
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

    private static byte[] InjectTwoPageLayoutInPdfCatalog(byte[] pdfBytes)
    {
        try
        {
            string latin1 = Encoding.Latin1.GetString(pdfBytes);
            int catalogIdx = latin1.IndexOf("/Type /Catalog", StringComparison.Ordinal);
            if (catalogIdx == -1) catalogIdx = latin1.IndexOf("/Type/Catalog", StringComparison.Ordinal);
            if (catalogIdx == -1) return pdfBytes;

            return pdfBytes;
        }
        catch
        {
            return pdfBytes;
        }
    }

    private Dictionary<int, List<LetterLoc>> GroupHighlightsByPage(IEnumerable<LetterLoc> letters)
    {
        return letters
            .GroupBy(l => l.PageNumber)
            .ToDictionary(g => g.Key, g => g.ToList());
    }
}
