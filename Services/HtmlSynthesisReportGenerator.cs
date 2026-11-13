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
        int criticalBlocks = productGroups.Sum(g => g.CriticalUniqueDiffsCount);

        var sb = new StringBuilder(65536);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"fr\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("<title>Synthèse Regroupée par Produit — PDF Comparator Pro</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(@"
            :root {
                --bg: #f1f5f9; --card: #ffffff; --text: #0f172a; --muted: #64748b;
                --border: #e2e8f0; --primary: #2563eb; --danger: #dc2626; --danger-bg: #fee2e2;
                --success: #059669; --success-bg: #d1fae5; --warn: #d97706; --warn-bg: #fef3c7;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; background: var(--bg); color: var(--text); padding: 20px 24px; line-height: 1.4; }
            .container { max-width: 1440px; margin: 0 auto; }
            .topbar { background: #0f172a; color: white; border-radius: 10px; padding: 14px 20px; margin-bottom: 16px; display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 12px; }
            .topbar h1 { font-size: 18px; font-weight: 800; letter-spacing: -0.01em; }
            .topbar-sub { font-size: 12px; color: #94a3b8; margin-top: 2px; }
            .kpi-bar { display: flex; gap: 12px; flex-wrap: wrap; }
            .kpi-pill { background: #1e293b; border: 1px solid #334155; border-radius: 8px; padding: 6px 14px; display: flex; align-items: center; gap: 8px; }
            .kpi-pill .val { font-size: 18px; font-weight: 800; color: #60a5fa; }
            .kpi-pill .val.crit { color: #f87171; }
            .kpi-pill .lbl { font-size: 11.5px; color: #cbd5e1; font-weight: 600; }
            .toolbar { background: var(--card); border: 1px solid var(--border); border-radius: 10px; padding: 10px 14px; margin-bottom: 16px; display: flex; gap: 10px; flex-wrap: wrap; align-items: center; justify-content: space-between; }
            .search-input { flex: 1; min-width: 240px; padding: 7px 12px; border: 1px solid #cbd5e1; border-radius: 6px; font-size: 13px; outline: none; }
            .search-input:focus { border-color: var(--primary); box-shadow: 0 0 0 2px #bfdbfe; }
            .filter-group { display: flex; gap: 6px; flex-wrap: wrap; }
            .filter-btn { padding: 6px 12px; border-radius: 6px; border: 1px solid var(--border); background: #f8fafc; color: #334155; font-size: 12px; font-weight: 600; cursor: pointer; }
            .filter-btn:hover, .filter-btn.active { background: var(--primary); color: white; border-color: var(--primary); }
            .doc-card { background: var(--card); border: 1px solid #cbd5e1; border-radius: 10px; margin-bottom: 16px; overflow: hidden; box-shadow: 0 1px 3px rgba(15,23,42,0.04); }
            .doc-header { background: #f8fafc; padding: 11px 16px; display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid var(--border); gap: 10px; flex-wrap: wrap; }
            .doc-title { font-size: 14.5px; font-weight: 800; display: flex; align-items: center; gap: 8px; color: #0f172a; }
            .badge { padding: 2px 8px; border-radius: 5px; font-size: 11px; font-weight: 700; display: inline-block; white-space: nowrap; }
            .badge-crit { background: var(--danger-bg); color: var(--danger); }
            .badge-maj { background: var(--warn-bg); color: var(--warn); }
            .badge-group { background: #dbeafe; color: #1e40af; border: 1px solid #93c5fd; }
            .badge-page { background: #eff6ff; color: #1d4ed8; border: 1px solid #bfdbfe; text-decoration: none; }
            .badge-page:hover { background: #2563eb; color: white; }
            .badge-cat { background: #f3e8ff; color: #6d28d9; }
            .badge-delta { background: #fef3c7; color: #b45309; border: 1px solid #fde68a; }
            .btn-action { text-decoration: none; color: white; padding: 4px 9px; border-radius: 6px; font-size: 11.5px; font-weight: 700; display: inline-flex; align-items: center; gap: 4px; white-space: nowrap; }
            .btn-side { background: #059669; }
            .btn-side:hover { background: #047857; }
            .btn-pdf { background: #2563eb; }
            .btn-pdf:hover { background: #1d4ed8; }
            table.diff-table { width: 100%; border-collapse: collapse; font-size: 13px; }
            table.diff-table th { background: #f1f5f9; color: #475569; font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.03em; padding: 7px 12px; text-align: left; border-bottom: 1px solid var(--border); }
            table.diff-table td { padding: 8px 12px; border-bottom: 1px solid #f1f5f9; vertical-align: top; }
            table.diff-table tr:last-child td { border-bottom: none; }
            table.diff-table tr.diff-row:hover { background: #f8fafc; }
            .ctx-cell { color: #64748b; font-size: 12px; max-width: 195px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .old-pill { display: inline-block; background: #fef2f2; color: #991b1b; border: 1px solid #fecaca; font-weight: 700; padding: 3px 8px; border-radius: 5px; white-space: pre-line; word-break: break-word; }
            .new-pill { display: inline-block; background: #ecfdf5; color: #065f46; border: 1px solid #a7f3d0; font-weight: 700; padding: 3px 8px; border-radius: 5px; white-space: pre-line; word-break: break-word; }
            .empty-pill { color: #94a3b8; font-style: italic; font-size: 12px; }
            .arrow-cell { color: #94a3b8; font-weight: 800; text-align: center; width: 26px; }
            details.doc-details { margin-top: 5px; font-size: 11.5px; color: #475569; }
            details.doc-details summary { cursor: pointer; font-weight: 600; color: #2563eb; user-select: none; }
            details.doc-details ul { list-style: none; margin-top: 5px; padding: 6px 8px; background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 6px; max-height: 150px; overflow-y: auto; }
            details.doc-details li { display: flex; justify-content: space-between; align-items: center; gap: 8px; padding: 3px 0; border-bottom: 1px dashed #e2e8f0; }
            details.doc-details li:last-child { border-bottom: none; }
            details.doc-details a { color: #059669; font-weight: 700; text-decoration: none; white-space: nowrap; }
            details.doc-details a:hover { text-decoration: underline; }
        ");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"container\">");

        // En-tête + KPIs compacts sur une seule barre
        sb.AppendLine("<div class=\"topbar\">");
        sb.AppendLine("<div>");
        sb.AppendLine("<h1>⚡ Synthèse des Différences Regroupées par Code Produit</h1>");
        sb.AppendLine($"<div class=\"topbar-sub\">Généré le {DateTime.Now:dd/MM/yyyy à HH:mm} • Les différences identiques d'un même Code Produit sont regroupées en une seule ligne</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"kpi-bar\">");
        sb.AppendLine($"<div class=\"kpi-pill\"><span class=\"val\">{totalProducts}</span><span class=\"lbl\">Code(s) Produit</span></div>");
        sb.AppendLine($"<div class=\"kpi-pill\"><span class=\"val\">{totalUniqueBlocks}</span><span class=\"lbl\">Différence(s) unique(s)</span></div>");
        sb.AppendLine($"<div class=\"kpi-pill\"><span class=\"val\">{totalDocs}</span><span class=\"lbl\">PDF impacté(s)</span></div>");
        sb.AppendLine($"<div class=\"kpi-pill\"><span class=\"val crit\">{criticalBlocks}</span><span class=\"lbl\">Critique(s)</span></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        // Barre de recherche & filtres rapides
        sb.AppendLine("<div class=\"toolbar\">");
        sb.AppendLine("<input type=\"text\" id=\"searchInput\" class=\"search-input\" placeholder=\"Filtrer par code produit, nom de PDF, montant, date ou texte...\" oninput=\"applyFilters()\">");
        sb.AppendLine("<div class=\"filter-group\">");
        sb.AppendLine("<button class=\"filter-btn active\" onclick=\"setFilter('ALL', this)\">Tout afficher</button>");
        sb.AppendLine("<button class=\"filter-btn\" onclick=\"setFilter('CRITIQUE', this)\">🔴 Critiques</button>");
        sb.AppendLine("<button class=\"filter-btn\" onclick=\"setFilter('NUM', this)\">💰 Montants &amp; Taux</button>");
        sb.AppendLine("<button class=\"filter-btn\" onclick=\"setFilter('Date', this)\">📅 Dates</button>");
        sb.AppendLine("<button class=\"filter-btn\" onclick=\"setFilter('TXT', this)\">📝 Texte</button>");
        sb.AppendLine("</div></div>");

        foreach (var prod in productGroups)
        {
            string sevBadge = prod.CriticalUniqueDiffsCount > 0
                ? $"<span class=\"badge badge-crit\">{prod.CriticalUniqueDiffsCount} critique(s)</span>"
                : "<span class=\"badge badge-maj\">Modifié</span>";

            sb.AppendLine($"<div class=\"doc-card\" data-docname=\"{WebUtility.HtmlEncode(prod.ProductGroupKey.ToLowerInvariant())}\">");
            sb.AppendLine("<div class=\"doc-header\">");
            sb.AppendLine($"<div class=\"doc-title\"><span>📦 Code Produit : {WebUtility.HtmlEncode(prod.ProductGroupKey)}</span> {sevBadge} <span class=\"badge badge-group\">{prod.UniqueDifferences.Count} diff(s) unique(s)</span> <span class=\"badge\" style=\"background:#e2e8f0; color:#1e293b;\">{prod.ImpactedDocsCount} PDF concerné(s)</span></div>");
            sb.AppendLine("</div>");

            sb.AppendLine("<table class=\"diff-table\">");
            sb.AppendLine("<thead><tr>");
            sb.AppendLine("<th style=\"width:75px;\">Page</th>");
            sb.AppendLine("<th style=\"width:155px;\">Catégorie / Écart</th>");
            sb.AppendLine("<th style=\"width:245px;\">PDF Regroupés</th>");
            sb.AppendLine("<th style=\"width:170px;\">Repère / Contexte</th>");
            sb.AppendLine("<th>🔴 Attendu (Source)</th>");
            sb.AppendLine("<th class=\"arrow-cell\">➔</th>");
            sb.AppendLine("<th>🟢 Obtenu (Target)</th>");
            sb.AppendLine("</tr></thead><tbody>");

            foreach (var diffItem in prod.UniqueDifferences)
            {
                var block = diffItem.Block;
                var firstDoc = diffItem.ImpactedDocs.FirstOrDefault();
                string primarySideBySide = firstDoc?.SideBySideHtmlFileName ?? string.Empty;
                string primaryPdf = firstDoc?.ReportFileName ?? string.Empty;

                string pageHref = !string.IsNullOrEmpty(primarySideBySide)
                    ? $"{Uri.EscapeDataString(primarySideBySide)}#page={block.PageNumber}"
                    : "#";

                string shortCtx = !string.IsNullOrWhiteSpace(block.ContextBefore)
                    ? block.ContextBefore
                    : block.ContextAfter;

                string oldHtml = !string.IsNullOrWhiteSpace(block.OldText)
                    ? $"<span class=\"old-pill\">{WebUtility.HtmlEncode(block.OldText)}</span>"
                    : "<span class=\"empty-pill\">(Absent dans Source)</span>";

                string newHtml = !string.IsNullOrWhiteSpace(block.NewText)
                    ? $"<span class=\"new-pill\">{WebUtility.HtmlEncode(block.NewText)}</span>"
                    : "<span class=\"empty-pill\">(Supprimé dans Target)</span>";

                string deltaBadge = !string.IsNullOrWhiteSpace(block.FinancialDelta)
                    ? $" <span class=\"badge badge-delta\">{WebUtility.HtmlEncode(block.FinancialDelta)}</span>"
                    : string.Empty;

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

                if (!string.IsNullOrEmpty(primarySideBySide))
                {
                    docsHtml.Append($"<a class=\"btn-action btn-side\" href=\"{pageHref}\" target=\"_blank\">📖 Côte à Côte</a>");
                }
                else if (!string.IsNullOrEmpty(primaryPdf))
                {
                    docsHtml.Append($"<a class=\"btn-action btn-pdf\" href=\"{Uri.EscapeDataString(primaryPdf)}\" target=\"_blank\">📄 PDF</a>");
                }
                docsHtml.Append("</div>");

                if (diffItem.ImpactedDocs.Count > 1)
                {
                    docsHtml.Append($"<details class=\"doc-details\"><summary>Voir les {diffItem.ImpactedDocs.Count} PDF ayant cette même différence ▾</summary><ul>");
                    foreach (var impDoc in diffItem.ImpactedDocs)
                    {
                        string docHref = !string.IsNullOrEmpty(impDoc.SideBySideHtmlFileName)
                            ? $"{Uri.EscapeDataString(impDoc.SideBySideHtmlFileName)}#page={impDoc.FirstPageNumber}"
                            : (!string.IsNullOrEmpty(impDoc.ReportFileName) ? Uri.EscapeDataString(impDoc.ReportFileName) : "#");
                        docsHtml.Append($"<li><span style=\"word-break:break-all;\">{WebUtility.HtmlEncode(impDoc.DisplayFileName)}</span>");
                        if (docHref != "#")
                        {
                            docsHtml.Append($"<a href=\"{docHref}\" target=\"_blank\">Ouvrir P.{impDoc.FirstPageNumber} ↗</a>");
                        }
                        docsHtml.Append("</li>");
                    }
                    docsHtml.Append("</ul></details>");
                }

                sb.AppendLine($"<tr class=\"diff-row\" data-severity=\"{block.SeverityLabel}\" data-category=\"{WebUtility.HtmlEncode(block.Category)}\">");
                sb.AppendLine($"<td><a class=\"badge badge-page\" href=\"{pageHref}\" target=\"_blank\" title=\"Ouvrir la page {block.PageNumber} côte à côte\">{WebUtility.HtmlEncode(diffItem.PagesCompactLabel)} ↗</a></td>");
                sb.AppendLine($"<td><span class=\"badge badge-cat\">{WebUtility.HtmlEncode(block.Category)}</span>{deltaBadge}</td>");
                sb.AppendLine($"<td>{docsHtml}</td>");
                sb.AppendLine($"<td class=\"ctx-cell\" title=\"{WebUtility.HtmlEncode(shortCtx)}\">{WebUtility.HtmlEncode(shortCtx)}</td>");
                sb.AppendLine($"<td>{oldHtml}</td>");
                sb.AppendLine("<td class=\"arrow-cell\">➔</td>");
                sb.AppendLine($"<td>{newHtml}</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody></table></div>");
        }

        sb.AppendLine(@"
        <script>
            let activeFilter = 'ALL';

            function setFilter(filterVal, btn) {
                activeFilter = filterVal;
                document.querySelectorAll('.filter-btn').forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
                applyFilters();
            }

            function applyFilters() {
                const q = (document.getElementById('searchInput').value || '').toLowerCase();
                document.querySelectorAll('.doc-card').forEach(doc => {
                    let visibleRows = 0;
                    const docName = doc.getAttribute('data-docname') || '';
                    doc.querySelectorAll('tr.diff-row').forEach(row => {
                        const sev = row.getAttribute('data-severity') || '';
                        const cat = row.getAttribute('data-category') || '';
                        const txt = row.innerText.toLowerCase();

                        let matchFilter = (activeFilter === 'ALL');
                        if (activeFilter === 'CRITIQUE') matchFilter = (sev === 'CRITIQUE');
                        else if (activeFilter === 'NUM') matchFilter = (cat.includes('Montant') || cat.includes('Taux') || cat.includes('Référence'));
                        else if (activeFilter === 'Date') matchFilter = (cat === 'Date');
                        else if (activeFilter === 'TXT') matchFilter = (cat.includes('Texte') || cat.includes('Clause'));

                        const matchSearch = !q || txt.includes(q) || docName.includes(q);

                        if (matchFilter && matchSearch) {
                            row.style.display = '';
                            visibleRows++;
                        } else {
                            row.style.display = 'none';
                        }
                    });
                    doc.style.display = visibleRows > 0 ? '' : 'none';
                });
            }
        </script>
        </div></body></html>");

        File.WriteAllText(htmlPath, sb.ToString(), Encoding.UTF8);
        return htmlPath;
    }
}
