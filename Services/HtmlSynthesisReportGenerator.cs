using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
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

        var channelGroups = ProductDifferenceGroupingService.GroupSummariesByChannelAndProduct(
            summaries ?? Array.Empty<DocumentDiffSummary>());
        var allProducts = channelGroups.SelectMany(c => c.Products).ToList();

        int totalProducts = allProducts.Count;
        int totalDocs = allProducts.Sum(g => g.ImpactedDocsCount);
        int totalUniqueBlocks = allProducts.Sum(g => g.UniqueDifferences.Count);

        bool hasBank = channelGroups.Any(c => string.Equals(c.ChannelKind, "Bank", StringComparison.OrdinalIgnoreCase));
        bool hasBroker = channelGroups.Any(c => string.Equals(c.ChannelKind, "Broker", StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder(65536);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"fr\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("<title>Synthèse — PDF Comparator</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(@"
            :root {
                --bg: #f8fafc; --card: #ffffff; --text: #0f172a; --muted: #64748b;
                --border: #e2e8f0; --primary: #2563eb; --primary-hover: #1d4ed8;
                --green: #059669; --green-bg: #ecfdf5; --green-border: #a7f3d0;
                --red: #b91c1c; --red-bg: #fef2f2; --red-border: #fecaca;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body {
                font-family: 'Segoe UI Variable', 'Segoe UI', system-ui, -apple-system, sans-serif;
                background: var(--bg); color: var(--text); line-height: 1.45; padding-bottom: 44px;
            }
            summary { list-style: none; }
            summary::-webkit-details-marker { display: none; }

            /* En-tête fixe */
            .sticky-header {
                position: sticky; top: 0; z-index: 50;
                background: rgba(15, 23, 42, 0.97); backdrop-filter: blur(8px);
                color: white; border-bottom: 1px solid #1e293b; padding: 11px 24px;
                box-shadow: 0 4px 12px rgba(15, 23, 42, 0.12);
            }
            .header-inner {
                max-width: 1500px; margin: 0 auto;
                display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 12px;
            }
            .title-block { display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap; }
            .title-block h1 { font-size: 17px; font-weight: 700; letter-spacing: -0.01em; }
            .title-block .date { font-size: 12px; color: #94a3b8; font-weight: 500; }
            .header-right { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
            .kpi-pill {
                background: #1e293b; border: 1px solid #334155; border-radius: 7px;
                padding: 4px 11px; display: inline-flex; align-items: center; gap: 6px;
            }
            .kpi-pill .val { font-size: 14.5px; font-weight: 700; color: #60a5fa; }
            .kpi-pill.ok .val { color: #34d399; }
            .kpi-pill .lbl { font-size: 11.5px; color: #cbd5e1; font-weight: 600; }
            .btn-top {
                background: #334155; color: #f8fafc; border: 1px solid #475569; border-radius: 6px;
                padding: 5px 11px; font-size: 12px; font-weight: 600; cursor: pointer; transition: 0.15s;
            }
            .btn-top:hover { background: #475569; }
            .container { max-width: 1500px; margin: 14px auto 0; padding: 0 24px; }

            /* Barre de filtres en 1 clic */
            .filter-bar {
                background: white; border: 1px solid var(--border); border-radius: 10px;
                padding: 10px 14px; margin-bottom: 14px;
                display: flex; flex-direction: column; gap: 8px;
            }
            .filter-row {
                display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 8px;
            }
            .pill-group { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
            .pill-label { font-size: 11px; font-weight: 700; color: var(--muted); text-transform: uppercase; margin-right: 3px; }
            .filter-pill {
                background: #f8fafc; color: #334155; border: 1px solid #cbd5e1; border-radius: 999px;
                padding: 4px 11px; font-size: 12px; font-weight: 600; cursor: pointer; transition: 0.15s;
            }
            .filter-pill:hover { border-color: var(--primary); color: var(--primary); background: #eff6ff; }
            .filter-pill.active { background: var(--primary); color: white; border-color: var(--primary); }
            .filter-pill.todo-toggle.active { background: #0f766e; border-color: #0f766e; color: white; }

            /* NIVEAU 1 : Accordéon Canal (Bank / Broker) */
            details.channel-card {
                background: white; border: 1px solid #cbd5e1; border-radius: 11px;
                margin-bottom: 16px; overflow: hidden; box-shadow: 0 1px 3px rgba(15, 23, 42, 0.04);
            }
            details.channel-card.ch-bank { border-color: #93c5fd; }
            details.channel-card.ch-broker { border-color: #c4b5fd; }
            summary.channel-header {
                padding: 10px 16px; cursor: pointer; user-select: none;
                display: flex; justify-content: space-between; align-items: center; gap: 10px;
                background: #f1f5f9; border-bottom: 1px solid transparent;
            }
            details.channel-card[open] > summary.channel-header { border-bottom-color: var(--border); }
            details.channel-card.ch-bank > summary.channel-header { background: #eff6ff; }
            details.channel-card.ch-broker > summary.channel-header { background: #f5f3ff; }
            .ch-left { display: flex; align-items: center; gap: 10px; }
            .chevron {
                display: inline-block; font-size: 12px; font-weight: 800; color: #475569;
                transition: transform 0.15s ease; width: 14px; text-align: center;
            }
            details:not([open]) > summary .chevron { transform: rotate(-90deg); }
            .ch-pill {
                padding: 3px 11px; border-radius: 6px; font-size: 13px; font-weight: 700;
                background: #1d4ed8; color: white;
            }
            .ch-bank .ch-pill { background: #1d4ed8; }
            .ch-broker .ch-pill { background: #6d28d9; }
            .ch-summary-meta { font-size: 12.5px; font-weight: 600; color: #475569; }
            .channel-body { padding: 12px 14px 4px; background: #fcfdff; }

            /* NIVEAU 2 : Accordéon Produit */
            details.prod-card {
                background: var(--card); border: 1px solid #dbe3ee; border-radius: 9px;
                margin-bottom: 12px; overflow: hidden;
            }
            summary.prod-header {
                background: #f8fafc; padding: 9px 14px; cursor: pointer; user-select: none;
                display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 10px;
                border-bottom: 1px solid transparent;
            }
            details.prod-card[open] > summary.prod-header { border-bottom-color: var(--border); }
            summary.prod-header:hover { background: #f1f5f9; }
            .prod-title-wrap { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
            .prod-name { font-size: 14.5px; font-weight: 700; color: #0f172a; }
            .badge {
                padding: 2px 9px; border-radius: 6px; font-size: 11.5px; font-weight: 700;
                display: inline-flex; align-items: center; white-space: nowrap;
            }
            .badge-neutral { background: #e2e8f0; color: #334155; }
            .badge-diffcount { background: #eff6ff; color: #1e40af; border: 1px solid #bfdbfe; }
            .badge-scramble { background: #fef3c7; color: #92400e; border: 1px solid #fcd34d; }
            .btn-check-all {
                background: white; color: #334155; border: 1px solid #cbd5e1; border-radius: 6px;
                padding: 4px 10px; font-size: 11.5px; font-weight: 600; cursor: pointer;
            }
            .btn-check-all:hover { background: var(--green-bg); color: var(--green); border-color: var(--green-border); }
            .prod-body { padding: 4px 8px 6px; background: white; }

            /* NIVEAU 3 : Différences regroupées du produit (avec dropdown des PDF concernés) */
            table.diff-table { width: 100%; border-collapse: collapse; font-size: 13px; }
            table.diff-table th {
                text-align: left; font-size: 10.5px; font-weight: 700; color: #64748b;
                text-transform: uppercase; padding: 6px 12px 5px; border-bottom: 1px solid #e2e8f0;
            }
            table.diff-table td { padding: 9px 12px; border-bottom: 1px solid #f1f5f9; vertical-align: middle; }
            table.diff-table tr:last-child td { border-bottom: none; }
            table.diff-table tr.diff-row:hover { background: #f8fafc; }
            table.diff-table tr.diff-row.verified { background: #f0fdf4; }
            table.diff-table tr.diff-row.verified .compare-grid { opacity: 0.62; }

            .check-btn {
                width: 27px; height: 27px; border-radius: 7px; border: 1.5px solid #cbd5e1;
                background: white; color: transparent; font-size: 14px; font-weight: 800;
                cursor: pointer; display: inline-flex; align-items: center; justify-content: center; transition: 0.12s;
            }
            .check-btn:hover { border-color: var(--green); color: #a7f3d0; }
            tr.diff-row.verified .check-btn { background: var(--green); border-color: var(--green); color: white; }

            .page-link {
                display: inline-flex; align-items: center; gap: 4px;
                background: #eff6ff; color: #1d4ed8; border: 1px solid #bfdbfe;
                padding: 3px 9px; border-radius: 6px; font-size: 12px; font-weight: 700; text-decoration: none; white-space: nowrap;
            }
            .page-link:hover { background: var(--primary); color: white; border-color: var(--primary); }

            .compare-grid {
                display: grid; grid-template-columns: minmax(0, 1fr) 24px minmax(0, 1fr);
                align-items: stretch; gap: 8px;
            }
            .side-box {
                padding: 6px 10px; border-radius: 7px; font-size: 13px;
                white-space: pre-line; word-break: break-word; display: flex; align-items: center;
            }
            .side-old { background: #fffafa; border: 1px solid var(--red-border); color: #334155; }
            .side-new { background: #f4fdf8; border: 1px solid var(--green-border); color: #334155; }
            .arrow-mid { display: flex; align-items: center; justify-content: center; color: #94a3b8; font-weight: 700; font-size: 14px; }
            mark.hl-del { background: #fee2e2; color: #991b1b; font-weight: 700; padding: 1px 4px; border-radius: 4px; }
            mark.hl-ins { background: #d1fae5; color: #065f46; font-weight: 700; padding: 1px 4px; border-radius: 4px; }
            .empty-pill { color: #94a3b8; font-style: italic; font-size: 12px; }

            /* Dropdown des PDF concernés & Côte à Côte */
            .doc-action-row { display: flex; align-items: center; justify-content: space-between; gap: 8px; flex-wrap: wrap; }
            .doc-SingleName { font-size: 12px; font-weight: 600; color: #334155; word-break: break-all; max-width: 190px; }
            .btn-sbs {
                text-decoration: none; color: white; background: var(--primary);
                padding: 5px 11px; border-radius: 6px; font-size: 12px; font-weight: 600;
                display: inline-flex; align-items: center; gap: 4px; white-space: nowrap; transition: 0.15s;
            }
            .btn-sbs:hover { background: var(--primary-hover); }
            details.doc-details { margin-top: 5px; font-size: 11.5px; color: #475569; }
            details.doc-details > summary {
                cursor: pointer; font-weight: 600; color: var(--primary); user-select: none;
                display: inline-flex; align-items: center; gap: 4px;
            }
            details.doc-details ul {
                list-style: none; margin-top: 5px; padding: 6px 8px;
                background: #f8fafc; border: 1px solid var(--border); border-radius: 6px;
                max-height: 155px; overflow-y: auto;
            }
            details.doc-details li {
                display: flex; justify-content: space-between; align-items: center; gap: 8px;
                padding: 3px 0; border-bottom: 1px dashed var(--border);
            }
            details.doc-details li:last-child { border-bottom: none; }
            details.doc-details a { color: var(--primary); font-weight: 700; text-decoration: none; white-space: nowrap; }

            .zero-state {
                background: white; border: 1px solid var(--green-border); border-radius: 12px;
                padding: 42px 24px; text-align: center; margin-top: 24px;
            }
            .zero-badge {
                width: 48px; height: 48px; border-radius: 50%; background: #d1fae5; color: var(--green);
                font-size: 24px; font-weight: 800; display: inline-flex; align-items: center; justify-content: center; margin-bottom: 12px;
            }
            .zero-state h2 { font-size: 18px; color: #065f46; margin-bottom: 6px; }
            .zero-state p { font-size: 13.5px; color: var(--muted); }

            @media print {
                .sticky-header { position: static; background: #0f172a !important; -webkit-print-color-adjust: exact; print-color-adjust: exact; }
                .filter-bar, .btn-top, .btn-check-all, .btn-sbs { display: none !important; }
                body { background: white; padding: 0; }
                .container { max-width: 100%; padding: 0; margin-top: 10px; }
                details.prod-card { break-inside: avoid; }
            }
        ");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // 1. EN-TÊTE FIXE
        sb.AppendLine("<div class=\"sticky-header\"><div class=\"header-inner\">");
        sb.AppendLine("<div class=\"title-block\">");
        sb.AppendLine("<h1>Synthèse Regroupée des Différences</h1>");
        sb.AppendLine($"<span class=\"date\">Générée le {DateTime.Now:dd/MM/yyyy à HH:mm}</span>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"header-right\">");
        sb.AppendLine($"<div class=\"kpi-pill\"><span class=\"val\">{totalProducts}</span><span class=\"lbl\">Produit(s)</span></div>");
        sb.AppendLine($"<div class=\"kpi-pill\"><span class=\"val\">{totalUniqueBlocks}</span><span class=\"lbl\">Différence(s) regroupée(s)</span></div>");
        sb.AppendLine($"<div class=\"kpi-pill\"><span class=\"val\">{totalDocs}</span><span class=\"lbl\">PDF concerné(s)</span></div>");
        if (totalUniqueBlocks > 0)
        {
            sb.AppendLine($"<div class=\"kpi-pill ok\"><span class=\"val\" id=\"verifiedCount\">0 / {totalUniqueBlocks}</span><span class=\"lbl\">Vérifié(s)</span></div>");
            sb.AppendLine("<button class=\"btn-top\" onclick=\"expandAll(true)\" title=\"Déplier tous les produits et les listes de PDF concernés\">Tout déplier</button>");
            sb.AppendLine("<button class=\"btn-top\" onclick=\"expandAll(false)\" title=\"Replier les produits pour une vue d'ensemble immédiate\">Tout replier</button>");
        }
        sb.AppendLine("<button class=\"btn-top\" onclick=\"window.print()\">Imprimer / PDF</button>");
        sb.AppendLine("</div></div></div>");

        sb.AppendLine("<div class=\"container\">");

        if (channelGroups.Count == 0)
        {
            sb.AppendLine("<div class=\"zero-state\">");
            sb.AppendLine("<div class=\"zero-badge\">✓</div>");
            sb.AppendLine("<h2>Aucune différence détectée</h2>");
            sb.AppendLine("<p>Tous les documents PDF comparés sont 100 % conformes à l'œil nu.</p>");
            sb.AppendLine("</div>");
        }
        else
        {
            // 2. BARRE DE FILTRES EN 1 CLIC (CANAL BANK / BROKER + PRODUIT + À VÉRIFIER)
            sb.AppendLine("<div class=\"filter-bar\">");
            sb.AppendLine("<div class=\"filter-row\">");

            sb.AppendLine("<div class=\"pill-group\">");
            sb.AppendLine("<span class=\"pill-label\">Canal :</span>");
            sb.AppendLine("<button class=\"filter-pill chan-pill active\" data-channel=\"ALL\" onclick=\"setChannelFilter('ALL', this)\">Tous</button>");
            if (hasBank)
            {
                sb.AppendLine("<button class=\"filter-pill chan-pill\" data-channel=\"bank\" onclick=\"setChannelFilter('bank', this)\">Bank</button>");
            }
            if (hasBroker)
            {
                sb.AppendLine("<button class=\"filter-pill chan-pill\" data-channel=\"broker\" onclick=\"setChannelFilter('broker', this)\">Broker</button>");
            }
            sb.AppendLine("</div>");

            sb.AppendLine("<div class=\"pill-group\">");
            sb.AppendLine("<button id=\"btnTodoOnly\" class=\"filter-pill todo-toggle\" onclick=\"toggleTodoOnly()\">Reste à vérifier uniquement</button>");
            sb.AppendLine("<button class=\"filter-pill\" onclick=\"resetChecks()\" title=\"Décocher toutes les lignes vérifiées\">Réinitialiser ✓</button>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>"); // fin filter-row 1

            if (allProducts.Count > 1)
            {
                sb.AppendLine("<div class=\"filter-row\" id=\"prodFilterRow\">");
                sb.AppendLine("<div class=\"pill-group\">");
                sb.AppendLine("<span class=\"pill-label\">Produit :</span>");
                foreach (var pg in allProducts)
                {
                    string pKeyAttr = WebUtility.HtmlEncode(pg.ProductGroupKey);
                    string pChan = WebUtility.HtmlEncode((pg.Channel ?? string.Empty).Trim().ToLowerInvariant());
                    string shortName = !string.IsNullOrWhiteSpace(pg.ProductName) ? pg.ProductName : pg.ProductGroupKey;
                    string prefix = (hasBank && hasBroker && !string.IsNullOrWhiteSpace(pg.Channel)) ? $"{pg.Channel} • " : string.Empty;
                    sb.AppendLine($"<button class=\"filter-pill prod-pill\" data-prod=\"{pKeyAttr}\" data-prod-chan=\"{pChan}\" onclick=\"toggleProductFilter('{EscapeJsSingleQuoted(pg.ProductGroupKey)}', this)\">{WebUtility.HtmlEncode(prefix + shortName)} ({pg.UniqueDifferences.Count})</button>");
                }
                sb.AppendLine("</div></div>");
            }

            sb.AppendLine("</div>"); // fin filter-bar

            // 3. ARBORESCENCE VISUELLE REGROUPÉE : CANAL (BANK / BROKER) ➔ PRODUIT ➔ DIFFÉRENCES REGROUPÉES
            foreach (var chGroup in channelGroups)
            {
                string chLower = chGroup.ChannelKind.Equals("Broker", StringComparison.OrdinalIgnoreCase)
                    ? "broker"
                    : "bank";
                string chCss = chLower == "broker" ? "ch-broker" : "ch-bank";
                string chSummary = $"{chGroup.ProductsCount} produit{(chGroup.ProductsCount > 1 ? "s" : "")}  •  " +
                                   $"{chGroup.UniqueDiffsCount} différence{(chGroup.UniqueDiffsCount > 1 ? "s" : "")} regroupée{(chGroup.UniqueDiffsCount > 1 ? "s" : "")}  •  " +
                                   $"{chGroup.ImpactedDocsCount} PDF concerné{(chGroup.ImpactedDocsCount > 1 ? "s" : "")}";

                sb.AppendLine($"<details open class=\"channel-card {chCss}\" data-channel-card=\"{chLower}\">");
                sb.AppendLine("<summary class=\"channel-header\">");
                sb.AppendLine("<div class=\"ch-left\">");
                sb.AppendLine("<span class=\"chevron\">▾</span>");
                sb.AppendLine($"<span class=\"ch-pill\">{WebUtility.HtmlEncode(chGroup.ChannelDisplayName)}</span>");
                sb.AppendLine("</div>");
                sb.AppendLine($"<span class=\"ch-summary-meta\">{WebUtility.HtmlEncode(chSummary)}</span>");
                sb.AppendLine("</summary>");

                sb.AppendLine("<div class=\"channel-body\">");

                foreach (var prod in chGroup.Products)
                {
                    string displayProdTitle = !string.IsNullOrWhiteSpace(prod.ProductName)
                        ? prod.ProductName
                        : prod.ProductGroupKey;

                    sb.AppendLine($"<details open class=\"prod-card\" data-channel=\"{chLower}\" data-prod=\"{WebUtility.HtmlEncode(prod.ProductGroupKey)}\">");
                    sb.AppendLine("<summary class=\"prod-header\">");
                    sb.AppendLine("<div class=\"prod-title-wrap\">");
                    sb.AppendLine("<span class=\"chevron\">▾</span>");
                    sb.AppendLine($"<span class=\"prod-name\">{WebUtility.HtmlEncode(displayProdTitle)}</span>");
                    sb.AppendLine($"<span class=\"badge badge-diffcount\">{prod.UniqueDifferences.Count} différence{(prod.UniqueDifferences.Count > 1 ? "s" : "")} regroupée{(prod.UniqueDifferences.Count > 1 ? "s" : "")}</span>");
                    sb.AppendLine($"<span class=\"badge badge-neutral\">{prod.ImpactedDocsCount} PDF</span>");
                    sb.AppendLine("</div>");
                    sb.AppendLine("<button type=\"button\" class=\"btn-check-all\" onclick=\"event.preventDefault(); event.stopPropagation(); markCardVerified(this)\">✓ Tout marquer vérifié</button>");
                    sb.AppendLine("</summary>");

                    sb.AppendLine("<div class=\"prod-body\">");
                    sb.AppendLine("<table class=\"diff-table\">");
                    sb.AppendLine("<thead><tr>");
                    sb.AppendLine("<th style=\"width:48px; text-align:center;\">✓</th>");
                    sb.AppendLine("<th style=\"width:92px;\">Page</th>");
                    sb.AppendLine("<th>Attendu (Source)   ➔   Obtenu (Cible)</th>");
                    sb.AppendLine("<th style=\"width:295px;\">PDF concernés</th>");
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

                        var (oldHtml, newHtml) = BuildHighlightedPairHtml(block.OldText, block.NewText);
                        string rowKey = ComputeRowKey(prod.ProductGroupKey, block);

                        var docsHtml = new StringBuilder();
                        docsHtml.Append("<div class=\"doc-action-row\">");
                        if (diffItem.ImpactedDocs.Count > 1)
                        {
                            docsHtml.Append($"<span class=\"badge badge-diffcount\">{diffItem.ImpactedDocs.Count} PDF regroupés</span>");
                        }
                        else if (firstDoc != null)
                        {
                            docsHtml.Append($"<span class=\"doc-SingleName\" title=\"{WebUtility.HtmlEncode(firstDoc.DisplayFileName)}\">{WebUtility.HtmlEncode(firstDoc.DisplayFileName)}</span>");
                        }

                        if (pageHref != "#")
                        {
                            docsHtml.Append($"<a class=\"btn-sbs\" href=\"{pageHref}\" target=\"_blank\">Côte à Côte ↗</a>");
                        }
                        docsHtml.Append("</div>");

                        if (diffItem.ImpactedDocs.Count > 1)
                        {
                            docsHtml.Append($"<details class=\"doc-details\"><summary>Voir les {diffItem.ImpactedDocs.Count} PDF concernés ▾</summary><ul>");
                            foreach (var impDoc in diffItem.ImpactedDocs)
                            {
                                string docHref = !string.IsNullOrEmpty(impDoc.SideBySideHtmlFileName)
                                    ? $"{Uri.EscapeDataString(impDoc.SideBySideHtmlFileName)}#page={impDoc.FirstPageNumber}"
                                    : (!string.IsNullOrEmpty(impDoc.ReportFileName) ? Uri.EscapeDataString(impDoc.ReportFileName) : "#");
                                docsHtml.Append($"<li><span style=\"word-break:break-all;\">{WebUtility.HtmlEncode(impDoc.DisplayFileName)}</span>");
                                if (docHref != "#")
                                {
                                    docsHtml.Append($"<a href=\"{docHref}\" target=\"_blank\">Côte à Côte (P.{impDoc.FirstPageNumber}) ↗</a>");
                                }
                                docsHtml.Append("</li>");
                            }
                            docsHtml.Append("</ul></details>");
                        }

                        var compareCellHtml = new StringBuilder();
                        if (block.HasScrambledBadge)
                        {
                            compareCellHtml.Append($"<div style=\"margin-bottom:5px;\"><span class=\"badge badge-scramble\">Même zone géométrique ({block.VariantCount} variantes — probable donnée scramblée)</span></div>");
                        }
                        compareCellHtml.Append($"<div class=\"compare-grid\"><div class=\"side-box side-old\">{oldHtml}</div><div class=\"arrow-mid\">➔</div><div class=\"side-box side-new\">{newHtml}</div></div>");
                        if (block.HasScrambledBadge && block.ScrambledVariants.Count > 1)
                        {
                            compareCellHtml.Append($"<details class=\"doc-details\"><summary>Voir les {block.ScrambledVariants.Count} variantes au même emplacement ▾</summary><ul>");
                            foreach (string variant in block.ScrambledVariants)
                            {
                                compareCellHtml.Append($"<li><span>{WebUtility.HtmlEncode(variant)}</span></li>");
                            }
                            compareCellHtml.Append("</ul></details>");
                        }

                        sb.AppendLine($"<tr class=\"diff-row\" data-key=\"{rowKey}\">");
                        sb.AppendLine("<td style=\"width:48px; text-align:center;\"><button type=\"button\" class=\"check-btn\" onclick=\"toggleRowVerified(this)\" title=\"Marquer comme vérifié\">✓</button></td>");
                        sb.AppendLine($"<td style=\"width:92px;\"><a class=\"page-link\" href=\"{pageHref}\" target=\"_blank\">{WebUtility.HtmlEncode(diffItem.PagesCompactLabel)} ↗</a></td>");
                        sb.AppendLine($"<td>{compareCellHtml}</td>");
                        sb.AppendLine($"<td style=\"width:295px;\">{docsHtml}</td>");
                        sb.AppendLine("</tr>");
                    }

                    sb.AppendLine("</tbody></table>");
                    sb.AppendLine("</div></details>"); // fin prod-card
                }

                sb.AppendLine("</div></details>"); // fin channel-card
            }
        }

        sb.AppendLine(@"
        <script>
            const STORAGE_KEY = 'pdfcomp_verified_v2';
            let activeChannel = 'ALL';
            let activeProd = '';
            let todoOnly = false;

            function loadVerifiedSet() {
                try {
                    const raw = localStorage.getItem(STORAGE_KEY);
                    return raw ? new Set(JSON.parse(raw)) : new Set();
                } catch {
                    return new Set();
                }
            }

            function saveVerifiedSet(set) {
                try {
                    localStorage.setItem(STORAGE_KEY, JSON.stringify(Array.from(set)));
                } catch {}
            }

            let verifiedSet = loadVerifiedSet();

            function syncVerifiedUi() {
                const rows = document.querySelectorAll('tr.diff-row');
                let checkedCount = 0;
                rows.forEach(r => {
                    const k = r.getAttribute('data-key');
                    const isChecked = verifiedSet.has(k);
                    r.classList.toggle('verified', isChecked);
                    if (isChecked) checkedCount++;
                });
                const counterEl = document.getElementById('verifiedCount');
                if (counterEl) {
                    counterEl.textContent = checkedCount + ' / ' + rows.length;
                }
                applyVisibility();
            }

            function toggleRowVerified(btn) {
                const row = btn.closest('tr.diff-row');
                if (!row) return;
                const k = row.getAttribute('data-key');
                if (verifiedSet.has(k)) verifiedSet.delete(k);
                else verifiedSet.add(k);
                saveVerifiedSet(verifiedSet);
                syncVerifiedUi();
            }

            function markCardVerified(btn) {
                const card = btn.closest('.prod-card');
                if (!card) return;
                card.querySelectorAll('tr.diff-row').forEach(r => {
                    if (r.style.display === 'none') return;
                    const k = r.getAttribute('data-key');
                    if (k) verifiedSet.add(k);
                });
                saveVerifiedSet(verifiedSet);
                syncVerifiedUi();
            }

            function resetChecks() {
                verifiedSet.clear();
                saveVerifiedSet(verifiedSet);
                syncVerifiedUi();
            }

            function expandAll(openState) {
                document.querySelectorAll('details.channel-card').forEach(d => d.open = true);
                document.querySelectorAll('details.prod-card').forEach(d => d.open = openState);
                document.querySelectorAll('details.doc-details').forEach(d => d.open = openState);
            }

            function setChannelFilter(channel, btn) {
                activeChannel = channel;
                activeProd = '';
                document.querySelectorAll('.chan-pill').forEach(b => b.classList.toggle('active', b === btn));
                document.querySelectorAll('.prod-pill').forEach(b => {
                    b.classList.remove('active');
                    const pChan = b.getAttribute('data-prod-chan') || '';
                    b.style.display = (channel === 'ALL' || pChan === channel) ? '' : 'none';
                });
                applyVisibility();
            }

            function toggleProductFilter(prodKey, btn) {
                if (activeProd === prodKey) {
                    activeProd = '';
                    btn.classList.remove('active');
                } else {
                    activeProd = prodKey;
                    document.querySelectorAll('.prod-pill').forEach(b => b.classList.toggle('active', b === btn));
                }
                applyVisibility();
            }

            function toggleTodoOnly() {
                todoOnly = !todoOnly;
                const btn = document.getElementById('btnTodoOnly');
                if (btn) btn.classList.toggle('active', todoOnly);
                applyVisibility();
            }

            function applyVisibility() {
                document.querySelectorAll('details.channel-card').forEach(chCard => {
                    let visibleProds = 0;

                    chCard.querySelectorAll('details.prod-card').forEach(card => {
                        const cardChannel = card.getAttribute('data-channel') || '';
                        const cardProd = card.getAttribute('data-prod') || '';

                        const matchChannel = (activeChannel === 'ALL') || (cardChannel === activeChannel);
                        const matchProd = !activeProd || (cardProd === activeProd);

                        if (!matchChannel || !matchProd) {
                            card.style.display = 'none';
                            return;
                        }

                        let visibleRows = 0;
                        card.querySelectorAll('tr.diff-row').forEach(row => {
                            const hideRow = todoOnly && row.classList.contains('verified');
                            row.style.display = hideRow ? 'none' : '';
                            if (!hideRow) visibleRows++;
                        });

                        card.style.display = visibleRows > 0 ? '' : 'none';
                        if (visibleRows > 0) visibleProds++;
                    });

                    chCard.style.display = visibleProds > 0 ? '' : 'none';
                });
            }

            syncVerifiedUi();
        </script>
        </div></body></html>");

        File.WriteAllText(htmlPath, sb.ToString(), Encoding.UTF8);
        return htmlPath;
    }

    private (string OldHtml, string NewHtml) BuildHighlightedPairHtml(string? oldText, string? newText)
    {
        bool hasOld = !string.IsNullOrWhiteSpace(oldText);
        bool hasNew = !string.IsNullOrWhiteSpace(newText);

        if (!hasOld && !hasNew)
        {
            return ("<span class=\"empty-pill\">(Vide)</span>", "<span class=\"empty-pill\">(Vide)</span>");
        }

        if (!hasOld)
        {
            return ("<span class=\"empty-pill\">(Absent dans Source)</span>", $"<span><mark class=\"hl-ins\">{WebUtility.HtmlEncode(newText!.Trim())}</mark></span>");
        }

        if (!hasNew)
        {
            return ($"<span><mark class=\"hl-del\">{WebUtility.HtmlEncode(oldText!.Trim())}</mark></span>", "<span class=\"empty-pill\">(Supprimé dans Cible)</span>");
        }

        var (leftChunks, rightChunks) = _inlineDiffService.GetInlineDiffChunks(oldText!.Trim(), newText!.Trim());

        string RenderChunks(List<(string Text, byte r, byte g, byte b, bool isBold)> chunks, string markClass, string fallback)
        {
            if (chunks.Count == 0)
            {
                return $"<span><mark class=\"{markClass}\">{WebUtility.HtmlEncode(fallback)}</mark></span>";
            }

            var b = new StringBuilder();
            b.Append("<span>");
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                string encoded = WebUtility.HtmlEncode(c.Text);
                if (c.isBold)
                {
                    b.Append($"<mark class=\"{markClass}\">{encoded}</mark>");
                }
                else
                {
                    b.Append(encoded);
                }
            }
            b.Append("</span>");
            return b.ToString();
        }

        return (
            RenderChunks(leftChunks, "hl-del", oldText.Trim()),
            RenderChunks(rightChunks, "hl-ins", newText.Trim()));
    }

    private static string ComputeRowKey(string productKey, DiffSummaryBlock block)
    {
        string raw = block.IsScrambledZoneGroup && block.HasPdfBounds
            ? $"{productKey}|ZONE|{block.PageNumber}|{Math.Round((block.FieldZoneRight > block.FieldZoneLeft ? block.FieldZoneLeft : block.PdfLeft) / 15.0)}|{Math.Round(((block.PdfBottom + block.PdfTop) * 0.5) / 12.0)}"
            : $"{productKey}|{ProductDifferenceGroupingService.GetBlockSignature(block)}";
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    private static string EscapeJsSingleQuoted(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");
    }
}
