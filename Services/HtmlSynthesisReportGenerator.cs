using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using PDFComparison.Models;

namespace PDFComparison.Services;

public interface IHtmlSynthesisReportGenerator
{
    string GenerateInteractiveHtmlReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir);
}

public class HtmlSynthesisReportGenerator : IHtmlSynthesisReportGenerator
{
    private readonly IInlineDiffService _inlineDiffService;

    public HtmlSynthesisReportGenerator(IInlineDiffService inlineDiffService)
    {
        _inlineDiffService = inlineDiffService ?? throw new ArgumentNullException(nameof(inlineDiffService));
    }

    public string GenerateInteractiveHtmlReport(IReadOnlyCollection<DocumentDiffSummary> summaries, string outputDiffDir)
    {
        Directory.CreateDirectory(outputDiffDir);
        string htmlPath = Path.Combine(outputDiffDir, "Global_Synthesis_Report.html");

        var productGroups = ProductDifferenceGroupingService.GroupSummariesByProduct(summaries);
        int totalProducts = productGroups.Count;
        int totalDocs = productGroups.Sum(g => g.ImpactedDocsCount);
        int totalUniqueBlocks = productGroups.Sum(g => g.UniqueDifferences.Count);

        var sb = new StringBuilder(49152);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"fr\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("<title>Synthèse par Produit — PDF Comparator</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(@"
            :root {
                --bg: #f8fafc; --card: #ffffff; --text: #0f172a; --muted: #64748b;
                --border: #e2e8f0; --primary: #2563eb;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; background: var(--bg); color: var(--text); padding: 16px 20px; line-height: 1.4; }
            .container { max-width: 1400px; margin: 0 auto; }
            .topbar { background: #0f172a; color: white; border-radius: 8px; padding: 12px 18px; margin-bottom: 12px; display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 12px; }
            .topbar h1 { font-size: 16px; font-weight: 700; }
            .kpi-bar { display: flex; gap: 10px; flex-wrap: wrap; }
            .kpi-pill { background: #1e293b; border: 1px solid #334155; border-radius: 6px; padding: 4px 12px; display: flex; align-items: center; gap: 6px; }
            .kpi-pill .val { font-size: 16px; font-weight: 700; color: #60a5fa; }
            .kpi-pill .lbl { font-size: 11.5px; color: #cbd5e1; font-weight: 600; }
            .toolbar { margin-bottom: 12px; }
            .search-input { width: 100%; padding: 8px 12px; border: 1px solid #cbd5e1; border-radius: 6px; font-size: 13px; outline: none; background: white; }
            .search-input:focus { border-color: var(--primary); }
            .doc-card { background: var(--card); border: 1px solid #cbd5e1; border-radius: 8px; margin-bottom: 14px; overflow: hidden; }
            .doc-header { background: #f1f5f9; padding: 9px 14px; display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid var(--border); gap: 8px; flex-wrap: wrap; }
            .doc-title { font-size: 14px; font-weight: 700; display: flex; align-items: center; gap: 8px; color: #0f172a; }
            .badge { padding: 2px 8px; border-radius: 5px; font-size: 11px; font-weight: 600; display: inline-block; white-space: nowrap; }
            .badge-group { background: #dbeafe; color: #1e40af; }
            .badge-page { background: #eff6ff; color: #1d4ed8; border: 1px solid #bfdbfe; text-decoration: none; }
            .badge-page:hover { background: #2563eb; color: white; }
            .btn-action { text-decoration: none; color: white; background: #2563eb; padding: 3px 9px; border-radius: 5px; font-size: 11.5px; font-weight: 600; display: inline-flex; align-items: center; white-space: nowrap; }
            .btn-action:hover { background: #1d4ed8; }
            table.diff-table { width: 100%; border-collapse: collapse; font-size: 13px; }
            table.diff-table th { background: #f8fafc; color: #475569; font-size: 11px; font-weight: 700; text-transform: uppercase; padding: 6px 12px; text-align: left; border-bottom: 1px solid var(--border); }
            table.diff-table td { padding: 7px 12px; border-bottom: 1px solid #f1f5f9; vertical-align: top; }
            table.diff-table tr:last-child td { border-bottom: none; }
            table.diff-table tr.diff-row:hover { background: #f8fafc; }
            .old-pill { display: inline-block; background: #fef2f2; color: #991b1b; border: 1px solid #fecaca; font-weight: 600; padding: 2px 8px; border-radius: 4px; white-space: pre-line; word-break: break-word; }
            .new-pill { display: inline-block; background: #ecfdf5; color: #065f46; border: 1px solid #a7f3d0; font-weight: 600; padding: 2px 8px; border-radius: 4px; white-space: pre-line; word-break: break-word; }
            .empty-pill { color: #94a3b8; font-style: italic; font-size: 12px; }
            .arrow-cell { color: #94a3b8; font-weight: 700; text-align: center; width: 26px; }
            details.doc-details { margin-top: 4px; font-size: 11.5px; color: #475569; }
            details.doc-details summary { cursor: pointer; font-weight: 600; color: #2563eb; user-select: none; }
            details.doc-details ul { list-style: none; margin-top: 4px; padding: 5px 8px; background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 5px; max-height: 140px; overflow-y: auto; }
            details.doc-details li { display: flex; justify-content: space-between; align-items: center; gap: 8px; padding: 2px 0; border-bottom: 1px dashed #e2e8f0; }
            details.doc-details li:last-child { border-bottom: none; }
            details.doc-details a { color: #2563eb; font-weight: 600; text-decoration: none; white-space: nowrap; }
        ");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"container\">");

        sb.AppendLine("<div class=\"topbar\">");
        sb.AppendLine($"<h1>Synthèse par Produit ({DateTime.Now:dd/MM/yyyy HH:mm})</h1>");
        sb.AppendLine("<div class=\"kpi-bar\">");
        sb.AppendLine($"<div class=\"kpi-pill\"><span class=\"val\">{totalProducts}</span><span class=\"lbl\">Produit(s)</span></div>");
        sb.AppendLine($"<div class=\"kpi-pill\"><span class=\"val\">{totalUniqueBlocks}</span><span class=\"lbl\">Différence(s) unique(s)</span></div>");
        sb.AppendLine($"<div class=\"kpi-pill\"><span class=\"val\">{totalDocs}</span><span class=\"lbl\">PDF concerné(s)</span></div>");
        sb.AppendLine("</div></div>");

        sb.AppendLine("<div class=\"toolbar\">");
        sb.AppendLine("<input type=\"text\" id=\"searchInput\" class=\"search-input\" placeholder=\"Rechercher un produit, un PDF ou un texte...\" oninput=\"applyFilters()\">");
        sb.AppendLine("</div>");

        foreach (var prod in productGroups)
        {
            sb.AppendLine($"<div class=\"doc-card\" data-docname=\"{WebUtility.HtmlEncode(prod.ProductGroupKey.ToLowerInvariant())}\">");
            sb.AppendLine("<div class=\"doc-header\">");
            sb.AppendLine($"<div class=\"doc-title\"><span>{WebUtility.HtmlEncode(prod.ProductGroupKey)}</span> <span class=\"badge badge-group\">{prod.UniqueDifferences.Count} diff(s) unique(s)</span> <span class=\"badge\" style=\"background:#e2e8f0; color:#334155;\">{prod.ImpactedDocsCount} PDF</span></div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<table class=\"diff-table\">");
            sb.AppendLine("<thead><tr>");
            sb.AppendLine("<th style=\"width:75px;\">Page</th>");
            sb.AppendLine("<th style=\"width:260px;\">Document(s)</th>");
            sb.AppendLine("<th>Attendu (Source)</th>");
            sb.AppendLine("<th class=\"arrow-cell\">➔</th>");
            sb.AppendLine("<th>Obtenu (Cible)</th>");
            sb.AppendLine("</tr></thead><tbody>");

            foreach (var diffItem in prod.UniqueDifferences)
            {
                var block = diffItem.Block;
                var firstDoc = diffItem.ImpactedDocs.FirstOrDefault();
                string primarySideBySide = firstDoc?.SideBySideHtmlFileName ?? string.Empty;
                string primaryPdf = firstDoc?.ReportFileName ?? string.Empty;

                string pageHref = !string.IsNullOrEmpty(primarySideBySide)
                    ? $"{Uri.EscapeDataString(primarySideBySide)}#page={block.PageNumber}"
                    : (!string.IsNullOrEmpty(primaryPdf) ? Uri.EscapeDataString(primaryPdf) : "#");

                string oldHtml = !string.IsNullOrWhiteSpace(block.OldText)
                    ? $"<span class=\"old-pill\">{WebUtility.HtmlEncode(block.OldText)}</span>"
                    : "<span class=\"empty-pill\">(Absent)</span>";

                string newHtml = !string.IsNullOrWhiteSpace(block.NewText)
                    ? $"<span class=\"new-pill\">{WebUtility.HtmlEncode(block.NewText)}</span>"
                    : "<span class=\"empty-pill\">(Supprimé)</span>";

                var docsHtml = new StringBuilder();
                docsHtml.Append("<div style=\"display:flex; align-items:center; gap:6px; flex-wrap:wrap;\">");
                if (diffItem.ImpactedDocs.Count > 1)
                {
                    docsHtml.Append($"<span class=\"badge badge-group\">{diffItem.ImpactedDocs.Count} PDF regroupés</span>");
                }
                else if (firstDoc != null)
                {
                    docsHtml.Append($"<span style=\"font-size:11.5px; font-weight:600; color:#334155; word-break:break-all;\">{WebUtility.HtmlEncode(firstDoc.DisplayFileName)}</span>");
                }

                if (pageHref != "#")
                {
                    docsHtml.Append($"<a class=\"btn-action\" href=\"{pageHref}\" target=\"_blank\">Côte à Côte</a>");
                }
                docsHtml.Append("</div>");

                if (diffItem.ImpactedDocs.Count > 1)
                {
                    docsHtml.Append($"<details class=\"doc-details\"><summary>Voir les {diffItem.ImpactedDocs.Count} PDF ▾</summary><ul>");
                    foreach (var impDoc in diffItem.ImpactedDocs)
                    {
                        string docHref = !string.IsNullOrEmpty(impDoc.SideBySideHtmlFileName)
                            ? $"{Uri.EscapeDataString(impDoc.SideBySideHtmlFileName)}#page={impDoc.FirstPageNumber}"
                            : (!string.IsNullOrEmpty(impDoc.ReportFileName) ? Uri.EscapeDataString(impDoc.ReportFileName) : "#");
                        docsHtml.Append($"<li><span style=\"word-break:break-all;\">{WebUtility.HtmlEncode(impDoc.DisplayFileName)}</span>");
                        if (docHref != "#")
                        {
                            docsHtml.Append($"<a href=\"{docHref}\" target=\"_blank\">P.{impDoc.FirstPageNumber} ↗</a>");
                        }
                        docsHtml.Append("</li>");
                    }
                    docsHtml.Append("</ul></details>");
                }

                sb.AppendLine("<tr class=\"diff-row\">");
                sb.AppendLine($"<td><a class=\"badge badge-page\" href=\"{pageHref}\" target=\"_blank\">{WebUtility.HtmlEncode(diffItem.PagesCompactLabel)} ↗</a></td>");
                sb.AppendLine($"<td>{docsHtml}</td>");
                sb.AppendLine($"<td>{oldHtml}</td>");
                sb.AppendLine("<td class=\"arrow-cell\">➔</td>");
                sb.AppendLine($"<td>{newHtml}</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody></table></div>");
        }

        sb.AppendLine(@"
        <script>
            function applyFilters() {
                const q = (document.getElementById('searchInput').value || '').toLowerCase();
                document.querySelectorAll('.doc-card').forEach(doc => {
                    let visibleRows = 0;
                    const docName = doc.getAttribute('data-docname') || '';
                    doc.querySelectorAll('tr.diff-row').forEach(row => {
                        const txt = row.innerText.toLowerCase();
                        const matchQuery = !q || txt.includes(q) || docName.includes(q);
                        row.style.display = matchQuery ? '' : 'none';
                        if (matchQuery) visibleRows++;
                    });
                    doc.style.display = (visibleRows > 0) ? '' : 'none';
                });
            }
        </script>
        </div></body></html>");

        File.WriteAllText(htmlPath, sb.ToString(), Encoding.UTF8);
        return htmlPath;
    }
}
