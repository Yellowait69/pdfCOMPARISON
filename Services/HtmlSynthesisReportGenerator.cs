using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using DiffPlex.DiffBuilder.Model;
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

        int totalDocs = summaries.Count;
        int totalBlocks = summaries.Sum(s => s.Blocks.Count);
        int criticalBlocks = summaries.Sum(s => s.Blocks.Count(b => b.Severity == DiffSeverity.Critical));
        double avgSsim = summaries.Count > 0 ? summaries.Average(s => s.OverallSsimScore) * 100.0 : 100.0;

        var sb = new StringBuilder(65536);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"fr\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("<title>Rapport de Synthèse Interactif - PDF Comparator Pro</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(@"
            :root {
                --bg: #f8fafc; --card: #ffffff; --text: #0f172a; --muted: #64748b;
                --border: #e2e8f0; --primary: #2563eb; --danger: #dc2626; --danger-bg: #fee2e2;
                --success: #059669; --success-bg: #d1fae5; --warn: #d97706; --warn-bg: #fef3c7;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; background: var(--bg); color: var(--text); padding: 28px; line-height: 1.5; }
            .container { max-width: 1340px; margin: 0 auto; }
            .header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 24px; }
            .header h1 { font-size: 26px; font-weight: 800; color: #0f172a; }
            .header p { color: var(--muted); font-size: 14px; margin-top: 4px; }
            .kpi-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; margin-bottom: 24px; }
            .kpi-card { background: var(--card); border: 1px solid var(--border); border-radius: 12px; padding: 18px 20px; box-shadow: 0 2px 6px rgba(15,23,42,0.03); }
            .kpi-label { font-size: 12px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; color: var(--muted); }
            .kpi-val { font-size: 28px; font-weight: 800; margin-top: 6px; color: var(--primary); }
            .kpi-val.critical { color: var(--danger); }
            .kpi-val.success { color: var(--success); }
            .toolbar { background: var(--card); border: 1px solid var(--border); border-radius: 12px; padding: 14px 18px; margin-bottom: 22px; display: flex; gap: 12px; flex-wrap: wrap; align-items: center; justify-content: space-between; }
            .search-input { flex: 1; min-width: 260px; padding: 10px 14px; border: 1px solid #cbd5e1; border-radius: 8px; font-size: 14px; outline: none; }
            .search-input:focus { border-color: var(--primary); box-shadow: 0 0 0 2px #bfdbfe; }
            .filter-group { display: flex; gap: 8px; flex-wrap: wrap; }
            .filter-btn { padding: 8px 14px; border-radius: 20px; border: 1px solid var(--border); background: #f1f5f9; color: #334155; font-size: 13px; font-weight: 600; cursor: pointer; transition: 0.15s; }
            .filter-btn:hover, .filter-btn.active { background: var(--primary); color: white; border-color: var(--primary); }
            .doc-card { background: var(--card); border: 1px solid var(--border); border-radius: 12px; margin-bottom: 20px; overflow: hidden; box-shadow: 0 2px 8px rgba(15,23,42,0.04); }
            .doc-header { background: #f1f5f9; padding: 14px 20px; display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid var(--border); cursor: pointer; }
            .doc-title { font-size: 16px; font-weight: 700; display: flex; align-items: center; gap: 10px; }
            .badge { padding: 3px 10px; border-radius: 999px; font-size: 11px; font-weight: 700; display: inline-block; }
            .badge-crit { background: var(--danger-bg); color: var(--danger); }
            .badge-maj { background: var(--warn-bg); color: var(--warn); }
            .badge-min { background: #e0f2fe; color: #0369a1; }
            .badge-page { background: #e2e8f0; color: #1e293b; }
            .badge-cat { background: #ede9fe; color: #6d28d9; }
            .badge-ssim { background: #dbeafe; color: #1d4ed8; }
            .pdf-link { text-decoration: none; background: var(--primary); color: white; padding: 6px 12px; border-radius: 6px; font-size: 12px; font-weight: 600; }
            .pdf-link:hover { background: #1d4ed8; }
            .block-item { padding: 18px 20px; border-bottom: 1px solid var(--border); }
            .block-item:last-child { border-bottom: none; }
            .block-meta { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; }
            .diff-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
            .diff-col { background: #f8fafc; border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; font-size: 13.5px; }
            .diff-col-title { font-size: 11px; font-weight: 700; text-transform: uppercase; color: var(--muted); margin-bottom: 6px; }
            .ctx-line { color: #94a3b8; font-size: 12px; font-style: italic; }
            .del-tok { background: #fecaca; color: #991b1b; font-weight: 700; padding: 1px 4px; border-radius: 3px; text-decoration: line-through; }
            .ins-tok { background: #a7f3d0; color: #065f46; font-weight: 700; padding: 1px 4px; border-radius: 3px; }
            .visual-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin-top: 12px; }
            .visual-box { border: 1px solid #cbd5e1; border-radius: 8px; overflow: hidden; background: white; text-align: center; padding: 6px; }
            .visual-box img { max-width: 100%; height: auto; border-radius: 4px; }
            .blink-btn { margin-left: auto; padding: 4px 10px; font-size: 11px; font-weight: 600; border: 1px solid #cbd5e1; border-radius: 6px; background: white; cursor: pointer; }
            .blink-btn:hover { background: #f1f5f9; }
        ");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"container\">");

        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine("<div><h1>Rapport de Synthèse Interactif — PDF Comparator Pro</h1>");
        sb.AppendLine($"<p>Généré le {DateTime.Now:dd/MM/yyyy à HH:mm:ss} • Filtrage Anti-Faux-Positifs (Reflow, Dé-césure &amp; SSIM) actif</p></div>");
        sb.AppendLine("</div>");

        // Cartes KPI
        sb.AppendLine("<div class=\"kpi-grid\">");
        sb.AppendLine($"<div class=\"kpi-card\"><div class=\"kpi-label\">Documents avec différences</div><div class=\"kpi-val\">{totalDocs}</div></div>");
        sb.AppendLine($"<div class=\"kpi-card\"><div class=\"kpi-label\">Total des Vraies Différences</div><div class=\"kpi-val\">{totalBlocks}</div></div>");
        sb.AppendLine($"<div class=\"kpi-card\"><div class=\"kpi-label\">Modifications Critiques (Montants/Dates)</div><div class=\"kpi-val critical\">{criticalBlocks}</div></div>");
        sb.AppendLine($"<div class=\"kpi-card\"><div class=\"kpi-label\">Similarité Visuelle SSIM Moyenne</div><div class=\"kpi-val success\">{avgSsim:F1}%</div></div>");
        sb.AppendLine("</div>");

        // Barre de recherche & filtres
        sb.AppendLine("<div class=\"toolbar\">");
        sb.AppendLine("<input type=\"text\" id=\"searchInput\" class=\"search-input\" placeholder=\"Rechercher un document, un montant, une date ou un mot-clé...\" oninput=\"applyFilters()\">");
        sb.AppendLine("<div class=\"filter-group\">");
        sb.AppendLine("<button class=\"filter-btn active\" onclick=\"setFilter('ALL', this)\">Toutes les différences</button>");
        sb.AppendLine("<button class=\"filter-btn\" onclick=\"setFilter('CRITIQUE', this)\">🔴 Critiques uniquement</button>");
        sb.AppendLine("<button class=\"filter-btn\" onclick=\"setFilter('Montant / Chiffre', this)\">💰 Montants &amp; Chiffres</button>");
        sb.AppendLine("<button class=\"filter-btn\" onclick=\"setFilter('Date', this)\">📅 Dates</button>");
        sb.AppendLine("<button class=\"filter-btn\" onclick=\"setFilter('Clause Critique', this)\">⚠️ Clauses Critiques</button>");
        sb.AppendLine("<button class=\"filter-btn\" onclick=\"setFilter('Texte', this)\">📝 Texte</button>");
        sb.AppendLine("</div></div>");

        int blockId = 0;
        foreach (var doc in summaries.OrderByDescending(d => d.CriticalAlertsCount).ThenByDescending(d => d.Blocks.Count))
        {
            if (doc.Blocks.Count == 0) continue;

            string sevBadge = doc.MaxSeverity == DiffSeverity.Critical
                ? $"<span class=\"badge badge-crit\">{doc.CriticalAlertsCount} CRITIQUE(S)</span>"
                : "<span class=\"badge badge-maj\">MAJEUR</span>";

            sb.AppendLine($"<div class=\"doc-card\" data-docname=\"{WebUtility.HtmlEncode(doc.DocumentName.ToLowerInvariant())}\">");
            sb.AppendLine("<div class=\"doc-header\">");
            sb.AppendLine($"<div class=\"doc-title\"><span>📄 {WebUtility.HtmlEncode(doc.DocumentName)}</span> <span class=\"badge badge-ssim\">SSIM : {doc.OverallSsimScore * 100:F1}%</span> {sevBadge} <span class=\"badge badge-page\">{doc.Blocks.Count} diff(s)</span></div>");

            if (!string.IsNullOrEmpty(doc.ReportFileName))
            {
                string stem = Path.GetFileNameWithoutExtension(doc.ReportFileName);
                string sideBySideFile = $"{stem}_SideBySide.html";
                sb.AppendLine("<div style=\"display:flex; gap:8px;\">");
                sb.AppendLine($"<a class=\"pdf-link\" style=\"background:#059669;\" href=\"{Uri.EscapeDataString(sideBySideFile)}\" target=\"_blank\">📖 Voir Côte à Côte (Source | Target) ↗</a>");
                sb.AppendLine($"<a class=\"pdf-link\" href=\"{Uri.EscapeDataString(doc.ReportFileName)}\" target=\"_blank\">📄 PDF Annoté ↗</a>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");

            sb.AppendLine("<div class=\"doc-body\">");
            foreach (var block in doc.Blocks)
            {
                blockId++;
                string sevClass = block.Severity switch
                {
                    DiffSeverity.Critical => "badge-crit",
                    DiffSeverity.Major => "badge-maj",
                    _ => "badge-min"
                };

                bool hasImages = block.SourceImage != null || block.TargetImage != null;

                sb.AppendLine($"<div class=\"block-item\" data-severity=\"{block.SeverityLabel}\" data-category=\"{WebUtility.HtmlEncode(block.Category)}\">");
                sb.AppendLine("<div class=\"block-meta\">");
                sb.AppendLine($"<span class=\"badge badge-page\">Page {block.PageNumber}</span>");
                sb.AppendLine($"<span class=\"badge {sevClass}\">{block.SeverityLabel}</span>");
                sb.AppendLine($"<span class=\"badge badge-cat\">{WebUtility.HtmlEncode(block.Category)}</span>");
                if (!string.IsNullOrWhiteSpace(block.FinancialDelta))
                {
                    sb.AppendLine($"<span class=\"badge\" style=\"background:#FEF3C7; color:#B45309; font-weight:800;\">{WebUtility.HtmlEncode(block.FinancialDelta)}</span>");
                }
                sb.AppendLine($"<span style=\"font-size:12px; font-weight:700; color:#475569;\">{block.ChangeTypeLabel}</span>");

                if (block.SourceImage != null && block.TargetImage != null)
                {
                    sb.AppendLine($"<button class=\"blink-btn\" onclick=\"toggleBlink({blockId})\">👁️ Comparer par clignotement A/B</button>");
                }
                sb.AppendLine("</div>");

                var (leftChunks, rightChunks) = _inlineDiffService.GetInlineDiffChunks(block.OldText ?? string.Empty, block.NewText ?? string.Empty);

                sb.AppendLine("<div class=\"diff-grid\">");

                // Colonne Source
                sb.AppendLine("<div class=\"diff-col\">");
                sb.AppendLine("<div class=\"diff-col-title\">Document Source (Original)</div>");
                if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                    sb.AppendLine($"<div class=\"ctx-line\">... {WebUtility.HtmlEncode(block.ContextBefore)}</div>");

                if (leftChunks.Count > 0)
                {
                    sb.Append("<div>");
                    foreach (var chunk in leftChunks)
                    {
                        string encoded = WebUtility.HtmlEncode(chunk.Text);
                        if (chunk.isBold) sb.Append($"<span class=\"del-tok\">{encoded}</span>");
                        else sb.Append(encoded);
                    }
                    sb.AppendLine("</div>");
                }
                else
                {
                    sb.AppendLine("<div class=\"ctx-line\">(Aucun texte original)</div>");
                }

                if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                    sb.AppendLine($"<div class=\"ctx-line\">{WebUtility.HtmlEncode(block.ContextAfter)} ...</div>");
                sb.AppendLine("</div>");

                // Colonne Cible
                sb.AppendLine("<div class=\"diff-col\">");
                sb.AppendLine("<div class=\"diff-col-title\">Document Target (Modifié)</div>");
                if (!string.IsNullOrWhiteSpace(block.ContextBefore))
                    sb.AppendLine($"<div class=\"ctx-line\">... {WebUtility.HtmlEncode(block.ContextBefore)}</div>");

                if (rightChunks.Count > 0)
                {
                    sb.Append("<div>");
                    foreach (var chunk in rightChunks)
                    {
                        string encoded = WebUtility.HtmlEncode(chunk.Text);
                        if (chunk.isBold) sb.Append($"<span class=\"ins-tok\">{encoded}</span>");
                        else sb.Append(encoded);
                    }
                    sb.AppendLine("</div>");
                }
                else
                {
                    sb.AppendLine("<div class=\"ctx-line\">(Texte supprimé)</div>");
                }

                if (!string.IsNullOrWhiteSpace(block.ContextAfter))
                    sb.AppendLine($"<div class=\"ctx-line\">{WebUtility.HtmlEncode(block.ContextAfter)} ...</div>");
                sb.AppendLine("</div>");

                sb.AppendLine("</div>"); // fin diff-grid

                if (hasImages)
                {
                    string srcB64 = block.SourceImage != null ? $"data:image/png;base64,{Convert.ToBase64String(block.SourceImage)}" : "";
                    string trgB64 = block.TargetImage != null ? $"data:image/png;base64,{Convert.ToBase64String(block.TargetImage)}" : "";

                    sb.AppendLine($"<div class=\"visual-grid\" id=\"vis-{blockId}\" data-src=\"{srcB64}\" data-trg=\"{trgB64}\">");
                    sb.AppendLine("<div class=\"visual-box\">");
                    if (!string.IsNullOrEmpty(srcB64))
                        sb.AppendLine($"<img id=\"img-left-{blockId}\" src=\"{srcB64}\" alt=\"Capture Source\"/>");
                    sb.AppendLine("</div>");
                    sb.AppendLine("<div class=\"visual-box\">");
                    if (!string.IsNullOrEmpty(trgB64))
                        sb.AppendLine($"<img id=\"img-right-{blockId}\" src=\"{trgB64}\" alt=\"Capture Target\"/>");
                    sb.AppendLine("</div>");
                    sb.AppendLine("</div>");
                }

                sb.AppendLine("</div>"); // fin block-item
            }
            sb.AppendLine("</div></div>"); // fin doc-body & doc-card
        }

        sb.AppendLine(@"
        <script>
            let activeFilter = 'ALL';
            const blinkTimers = {};

            function setFilter(filterVal, btn) {
                activeFilter = filterVal;
                document.querySelectorAll('.filter-btn').forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
                applyFilters();
            }

            function applyFilters() {
                const q = (document.getElementById('searchInput').value || '').toLowerCase();
                document.querySelectorAll('.doc-card').forEach(doc => {
                    let visibleBlocks = 0;
                    doc.querySelectorAll('.block-item').forEach(blk => {
                        const sev = blk.getAttribute('data-severity');
                        const cat = blk.getAttribute('data-category');
                        const txt = blk.innerText.toLowerCase();
                        const docName = doc.getAttribute('data-docname');

                        const matchFilter = (activeFilter === 'ALL') || (sev === activeFilter) || (cat === activeFilter);
                        const matchSearch = !q || txt.includes(q) || docName.includes(q);

                        if (matchFilter && matchSearch) {
                            blk.style.display = '';
                            visibleBlocks++;
                        } else {
                            blk.style.display = 'none';
                        }
                    });
                    doc.style.display = visibleBlocks > 0 ? '' : 'none';
                });
            }

            function toggleBlink(id) {
                const container = document.getElementById('vis-' + id);
                const imgLeft = document.getElementById('img-left-' + id);
                if (!container || !imgLeft) return;

                if (blinkTimers[id]) {
                    clearInterval(blinkTimers[id]);
                    delete blinkTimers[id];
                    imgLeft.src = container.getAttribute('data-src');
                    return;
                }

                let showTarget = false;
                blinkTimers[id] = setInterval(() => {
                    showTarget = !showTarget;
                    imgLeft.src = showTarget ? container.getAttribute('data-trg') : container.getAttribute('data-src');
                }, 550);
            }
        </script>
        </div></body></html>");

        File.WriteAllText(htmlPath, sb.ToString(), Encoding.UTF8);
        return htmlPath;
    }
}
