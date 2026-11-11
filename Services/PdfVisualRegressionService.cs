using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace PDFComparison.Services;

public interface IPdfVisualRegressionService
{
    void AnalyzeVisualAndPopulateSnapshots(
        string sourcePath,
        string targetPath,
        IReadOnlyList<PdfWordInfo> sourceWords,
        IReadOnlyList<PdfWordInfo> targetWords,
        DiffAnalysisResult analysisResult);

    (byte[] SourcePng, byte[] TargetPng, byte[] HeatmapPng, double PageSsim, int TotalPages) RenderInteractiveInspectionPage(
        string sourcePath,
        string targetPath,
        int pageNumber);
}

public class PdfVisualRegressionService : IPdfVisualRegressionService
{
    private readonly IConfigurationService? _configService;

    public PdfVisualRegressionService(IConfigurationService? configService = null)
    {
        _configService = configService;
    }

    private ComparisonProfile Profile => _configService?.ActiveProfile ?? ComparisonProfile.CreateDefault();

    public void AnalyzeVisualAndPopulateSnapshots(
        string sourcePath,
        string targetPath,
        IReadOnlyList<PdfWordInfo> sourceWords,
        IReadOnlyList<PdfWordInfo> targetWords,
        DiffAnalysisResult analysisResult)
    {
        if (analysisResult == null) throw new ArgumentNullException(nameof(analysisResult));
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath)) return;

        var profile = Profile;
        if (!profile.EnableVisualSsimEngine && !profile.GenerateVisualSnapshotsInGlobalReport)
        {
            analysisResult.OverallSsimScore = 1.0;
            return;
        }

        int dpi = Math.Clamp(profile.VisualRenderDpi, 72, 200);
        double scale = dpi / 72.0;

        using var sourceDoc = PdfDocument.Open(sourcePath, new ParsingOptions { ClipPaths = false });
        using var targetDoc = PdfDocument.Open(targetPath, new ParsingOptions { ClipPaths = false });

        analysisResult.Summary.SourcePageCount = sourceDoc.NumberOfPages;
        analysisResult.Summary.TargetPageCount = targetDoc.NumberOfPages;

        int maxPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);
        var sourceWordsByPage = sourceWords.GroupBy(w => w.PageNumber).ToDictionary(g => g.Key, g => g.ToList());
        var targetWordsByPage = targetWords.GroupBy(w => w.PageNumber).ToDictionary(g => g.Key, g => g.ToList());

        var sourceHighByPage = analysisResult.Highlights.SourceRed.GroupBy(l => l.PageNumber).ToDictionary(g => g.Key, g => g.ToList());
        var targetHighByPage = analysisResult.Highlights.TargetRed.GroupBy(l => l.PageNumber).ToDictionary(g => g.Key, g => g.ToList());

        double minSsimAcrossPages = 1.0;
        var confirmedSourceRed = new List<LetterLoc>();
        var confirmedTargetRed = new List<LetterLoc>();

        // Alignement intelligent des pages (évite que l'insertion d'une page ne mette en erreur toutes les pages suivantes)
        var alignedPairs = AlignDocumentPages(sourceDoc.NumberOfPages, sourceWordsByPage, targetDoc.NumberOfPages, targetWordsByPage);

        foreach (var (sPageNum, tPageNum) in alignedPairs)
        {
            if (!sPageNum.HasValue || !tPageNum.HasValue)
            {
                minSsimAcrossPages = Math.Min(minSsimAcrossPages, 0.5);
                int orphanPage = tPageNum ?? sPageNum ?? 1;
                if (sPageNum.HasValue && sourceHighByPage.TryGetValue(sPageNum.Value, out var sRem)) confirmedSourceRed.AddRange(sRem);
                if (tPageNum.HasValue && targetHighByPage.TryGetValue(tPageNum.Value, out var tRem)) confirmedTargetRed.AddRange(tRem);

                analysisResult.Summary.Blocks.Insert(0, new DiffSummaryBlock
                {
                    PageNumber = orphanPage,
                    Category = "Structure PDF",
                    Severity = DiffSeverity.Critical,
                    FinancialDelta = $"{targetDoc.NumberOfPages - sourceDoc.NumberOfPages:+#;-#;0} page(s)",
                    Type = tPageNum.HasValue ? ChangeType.Inserted : ChangeType.Deleted,
                    OldText = sPageNum.HasValue ? $"[Page {sPageNum.Value} présente dans Attendu ({sourceDoc.NumberOfPages} p.)]" : $"[Aucune page correspondante ({sourceDoc.NumberOfPages} p.)]",
                    NewText = tPageNum.HasValue ? $"[Page {tPageNum.Value} ajoutée dans Obtenu ({targetDoc.NumberOfPages} p.)]" : $"[Page supprimée dans Obtenu ({targetDoc.NumberOfPages} p.)]"
                });
                continue;
            }

            int pageNum = tPageNum.Value;
            int srcPageIndex = sPageNum.Value;

            var sPage = sourceDoc.GetPage(srcPageIndex);
            var tPage = targetDoc.GetPage(pageNum);

            bool hasPageHighlights = sourceHighByPage.ContainsKey(srcPageIndex) || targetHighByPage.ContainsKey(pageNum);
            var sImages = SafeGetImages(sPage);
            var tImages = SafeGetImages(tPage);
            bool hasImagesOnPage = sImages.Count > 0 || tImages.Count > 0;

            if (!hasPageHighlights && !hasImagesOnPage)
            {
                continue;
            }

            sourceWordsByPage.TryGetValue(srcPageIndex, out var sPageWords);
            targetWordsByPage.TryGetValue(pageNum, out var tPageWords);

            using var srcBmp = RenderNormalizedPageBitmap(sPage, sPageWords ?? new List<PdfWordInfo>(), sImages, maxPages, scale, profile);
            using var trgBmp = RenderNormalizedPageBitmap(tPage, tPageWords ?? new List<PdfWordInfo>(), tImages, maxPages, scale, profile);

            var (pageSsim, diffRectsPx, shiftX, shiftY) = CompareBitmapsPerceptually(srcBmp, trgBmp, profile);
            minSsimAcrossPages = Math.Min(minSsimAcrossPages, pageSsim);

            // Conversion des régions de différences visuelles (Pixels Top-Left -> Points PDF Bottom-Left)
            var pageVisualRegions = new List<VisualDiffRegion>();
            foreach (var rectPx in diffRectsPx)
            {
                double pdfLeft = rectPx.Left / scale;
                double pdfRight = rectPx.Right / scale;
                double pdfTop = sPage.Height - (rectPx.Top / scale);
                double pdfBottom = sPage.Height - (rectPx.Bottom / scale);

                var pdfRect = new PdfRectangle(
                    Math.Min(pdfLeft, pdfRight),
                    Math.Min(pdfBottom, pdfTop),
                    Math.Max(pdfLeft, pdfRight),
                    Math.Max(pdfBottom, pdfTop));

                pageVisualRegions.Add(new VisualDiffRegion(
                    pageNum,
                    pdfRect,
                    pageSsim,
                    (double)(rectPx.Width * rectPx.Height) / (srcBmp.Width * srcBmp.Height) * 100.0,
                    null,
                    null,
                    $"Page {pageNum} (SSIM: {pageSsim * 100:F1}%)"));
            }

            analysisResult.VisualRegions.AddRange(pageVisualRegions);

            // Filtrage anti-faux-positifs des highlights par validation perceptuelle SSIM/YIQ
            if (profile.EnableVisualSsimEngine)
            {
                if (sourceHighByPage.TryGetValue(pageNum, out var sLetters))
                {
                    foreach (var letter in sLetters)
                    {
                        if (IntersectsAnyVisualRegion(letter.BoundingBox, pageVisualRegions, tolerancePt: 12.0))
                        {
                            confirmedSourceRed.Add(letter);
                        }
                    }
                }

                if (targetHighByPage.TryGetValue(pageNum, out var tLetters))
                {
                    foreach (var letter in tLetters)
                    {
                        if (IntersectsAnyVisualRegion(letter.BoundingBox, pageVisualRegions, tolerancePt: 12.0))
                        {
                            confirmedTargetRed.Add(letter);
                        }
                    }
                }
            }
            else
            {
                if (sourceHighByPage.TryGetValue(pageNum, out var sLetters)) confirmedSourceRed.AddRange(sLetters);
                if (targetHighByPage.TryGetValue(pageNum, out var tLetters)) confirmedTargetRed.AddRange(tLetters);
            }

            // Détection de différences purement graphiques (ex: image, graphique, scan ou signature modifiée sans changement de texte)
            if (hasImagesOnPage && pageVisualRegions.Count > 0 && !hasPageHighlights)
            {
                foreach (var visReg in pageVisualRegions.Take(3))
                {
                    var (srcCrop, trgCrop) = CreateAnnotatedSideBySideCrops(
                        srcBmp, trgBmp, visReg.PdfBounds, sPage.Height, scale, shiftX, shiftY);

                    analysisResult.Summary.Blocks.Add(new DiffSummaryBlock
                    {
                        PageNumber = pageNum,
                        Category = "Visuel / Image",
                        Severity = DiffSeverity.Major,
                        Type = ChangeType.Modified,
                        OldText = $"[Élément graphique original - Page {pageNum}]",
                        NewText = $"[Élément graphique modifié - Page {pageNum} (SSIM : {pageSsim * 100:F1}%)]",
                        SourceImage = srcCrop,
                        TargetImage = trgCrop
                    });
                }
            }

            // Génération des captures visuelles annotées pour les blocs du rapport global
            if (profile.GenerateVisualSnapshotsInGlobalReport &&
                (confirmedSourceRed.Any(l => l.PageNumber == pageNum) || confirmedTargetRed.Any(l => l.PageNumber == pageNum)))
            {
                AttachVisualCropsToSummaryBlocks(
                    analysisResult,
                    srcBmp,
                    trgBmp,
                    pageNum,
                    sPage.Height,
                    scale,
                    shiftX,
                    shiftY,
                    confirmedSourceRed.Where(l => l.PageNumber == pageNum).ToList(),
                    confirmedTargetRed.Where(l => l.PageNumber == pageNum).ToList());
            }
        }

        if (profile.EnableVisualSsimEngine)
        {
            analysisResult.Highlights.SourceRed.Clear();
            analysisResult.Highlights.SourceRed.AddRange(confirmedSourceRed);

            analysisResult.Highlights.TargetRed.Clear();
            analysisResult.Highlights.TargetRed.AddRange(confirmedTargetRed);
        }

        analysisResult.OverallSsimScore = minSsimAcrossPages;
        analysisResult.Summary.OverallSsimScore = minSsimAcrossPages;
    }

    public (byte[] SourcePng, byte[] TargetPng, byte[] HeatmapPng, double PageSsim, int TotalPages) RenderInteractiveInspectionPage(
        string sourcePath,
        string targetPath,
        int pageNumber)
    {
        var profile = Profile;
        double scale = Math.Clamp(profile.VisualRenderDpi, 96, 150) / 72.0;

        using var sourceDoc = PdfDocument.Open(sourcePath, new ParsingOptions { ClipPaths = false });
        using var targetDoc = PdfDocument.Open(targetPath, new ParsingOptions { ClipPaths = false });

        int totalPages = Math.Max(sourceDoc.NumberOfPages, targetDoc.NumberOfPages);
        int pNum = Math.Clamp(pageNumber, 1, Math.Max(1, totalPages));

        var sPage = pNum <= sourceDoc.NumberOfPages ? sourceDoc.GetPage(pNum) : sourceDoc.GetPage(1);
        var tPage = pNum <= targetDoc.NumberOfPages ? targetDoc.GetPage(pNum) : targetDoc.GetPage(1);

        var sWords = sPage.GetWords().Select(w => new PdfWordInfo { Text = w.Text, Letters = w.Letters, PageNumber = pNum }).ToList();
        var tWords = tPage.GetWords().Select(w => new PdfWordInfo { Text = w.Text, Letters = w.Letters, PageNumber = pNum }).ToList();

        using var srcBmp = RenderNormalizedPageBitmap(sPage, sWords, SafeGetImages(sPage), totalPages, scale, profile);
        using var trgBmp = RenderNormalizedPageBitmap(tPage, tWords, SafeGetImages(tPage), totalPages, scale, profile);

        // Calculer et surligner uniquement les vraies différences de texte sur les deux pages côte à côte au même niveau
        var sanitizer = new PdfLayoutSanitizerService();
        var matcher = new VisualHighlightMatcherService(_configService);
        var sLines = sanitizer.GroupIntoLines(sWords);
        var tLines = sanitizer.GroupIntoLines(tWords);

        string sDiffTxt = string.Join("\n", sLines.Select(l => string.Join(" ", l.Select(w => w.CleanText))));
        string tDiffTxt = string.Join("\n", tLines.Select(l => string.Join(" ", l.Select(w => w.CleanText))));

        var diffModel = new DiffPlex.DiffBuilder.SideBySideDiffBuilder(new DiffPlex.Differ()).BuildDiffModel(sDiffTxt, tDiffTxt);
        var highlights = matcher.GenerateHighlights(diffModel, sLines, tLines);

        DrawTextDiffHighlightsOnBitmap(srcBmp, highlights.SourceRed, sPage.Height, scale, isSource: true);
        DrawTextDiffHighlightsOnBitmap(trgBmp, highlights.TargetRed, tPage.Height, scale, isSource: false);

        int totalWords = Math.Max(1, sWords.Count + tWords.Count);
        int changedLetters = highlights.SourceRed.Count + highlights.TargetRed.Count;
        double textConcordance = changedLetters == 0 ? 1.0 : Math.Clamp(1.0 - ((double)changedLetters / Math.Max(20, totalWords * 5)), 0.0, 0.999);

        using var heatmapBmp = BuildHeatmapBitmap(srcBmp, trgBmp, new List<Rectangle>());

        return (BitmapToPngBytes(srcBmp), BitmapToPngBytes(trgBmp), BitmapToPngBytes(heatmapBmp), textConcordance, totalPages);
    }

    private static void DrawTextDiffHighlightsOnBitmap(
        Bitmap bmp,
        List<LetterLoc> letters,
        double pageHeightPt,
        double scale,
        bool isSource)
    {
        if (letters == null || letters.Count == 0) return;

        var unifiedBlocks = VisualSegmentHelper.GetUnifiedBlocks(letters);
        if (unifiedBlocks.Count == 0) return;

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var fillColor = isSource
            ? System.Drawing.Color.FromArgb(65, 239, 68, 68)
            : System.Drawing.Color.FromArgb(65, 16, 185, 129);
        var strokeColor = isSource
            ? System.Drawing.Color.FromArgb(230, 220, 38, 38)
            : System.Drawing.Color.FromArgb(230, 5, 150, 105);
        var guideColor = isSource
            ? System.Drawing.Color.FromArgb(95, 239, 68, 68)
            : System.Drawing.Color.FromArgb(95, 16, 185, 129);

        using var fillBrush = new SolidBrush(fillColor);
        using var borderPen = new Pen(strokeColor, 2.2f);
        using var marginBrush = new SolidBrush(strokeColor);
        using var guidePen = new Pen(guideColor, 1.0f) { DashStyle = DashStyle.Dash };

        foreach (var blk in unifiedBlocks)
        {
            float x = (float)((double)blk.minX * scale) - 3f;
            float w = (float)((double)(blk.maxX - blk.minX) * scale) + 6f;
            float topPt = (float)((double)pageHeightPt - (double)blk.maxY);
            float h = (float)((double)(blk.maxY - blk.minY) * scale) + 4f;
            float y = (float)(topPt * scale) - 2f;

            // Ligne guide horizontale sur toute la largeur pour repérer immédiatement le niveau vertical côte à côte
            float midY = y + (h / 2f);
            g.DrawLine(guidePen, 0, midY, bmp.Width, midY);

            // Repère épais dans la marge gauche
            g.FillRectangle(marginBrush, 2f, y, 7f, Math.Max(8f, h));

            // Surlignage + Encadrement unifié du bloc de différences consécutives
            g.FillRectangle(fillBrush, x, y, Math.Max(8f, w), Math.Max(8f, h));
            g.DrawRectangle(borderPen, x, y, Math.Max(8f, w), Math.Max(8f, h));
        }
    }

    private static Bitmap BuildHeatmapBitmap(Bitmap srcBmp, Bitmap trgBmp, List<Rectangle> diffRectsPx)
    {
        int w = Math.Min(srcBmp.Width, trgBmp.Width);
        int h = Math.Min(srcBmp.Height, trgBmp.Height);
        var heat = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        byte[] sBgra = ExtractBgraBytes(srcBmp, w, h, out byte[] sGray);
        byte[] tBgra = ExtractBgraBytes(trgBmp, w, h, out byte[] tGray);
        byte[] outBgra = new byte[w * h * 4];

        for (int i = 0; i < w * h; i++)
        {
            int idx = i * 4;
            int diff = Math.Abs(sGray[i] - tGray[i]);
            if (diff > 18)
            {
                // Pixel en écart : coloration rouge/magenta proportionnelle à l'intensité de la différence
                outBgra[idx] = 90;
                outBgra[idx + 1] = 20;
                outBgra[idx + 2] = 235;
                outBgra[idx + 3] = 255;
            }
            else
            {
                // Fond atténué en niveaux de gris clair pour faire ressortir les écarts
                byte faded = (byte)(255 - ((255 - sGray[i]) / 3));
                outBgra[idx] = faded;
                outBgra[idx + 1] = faded;
                outBgra[idx + 2] = faded;
                outBgra[idx + 3] = 255;
            }
        }

        var data = heat.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(outBgra, 0, data.Scan0, outBgra.Length);
        }
        finally
        {
            heat.UnlockBits(data);
        }

        using var g = Graphics.FromImage(heat);
        using var pen = new Pen(System.Drawing.Color.FromArgb(220, 220, 38, 38), 2f);
        foreach (var r in diffRectsPx)
        {
            g.DrawRectangle(pen, r);
        }

        return heat;
    }

    private static byte[] BitmapToPngBytes(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// Aligne dynamiquement les pages de deux documents (Programmation Dynamique type Needleman-Wunsch sur similarité Jaccard)
    /// pour éviter qu'une page insérée ou supprimée au milieu d'un PDF ne décale toutes les pages suivantes.
    /// </summary>
    private static List<(int? SourcePage, int? TargetPage)> AlignDocumentPages(
        int sourceCount,
        Dictionary<int, List<PdfWordInfo>> sourceWordsByPage,
        int targetCount,
        Dictionary<int, List<PdfWordInfo>> targetWordsByPage)
    {
        var result = new List<(int? SourcePage, int? TargetPage)>();

        if (sourceCount == targetCount)
        {
            for (int p = 1; p <= sourceCount; p++)
            {
                result.Add((p, p));
            }
            return result;
        }

        var sSets = new HashSet<string>[sourceCount + 1];
        for (int s = 1; s <= sourceCount; s++)
        {
            sSets[s] = sourceWordsByPage.TryGetValue(s, out var wList)
                ? new HashSet<string>(wList.Select(w => w.Text.ToUpperInvariant()), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }

        var tSets = new HashSet<string>[targetCount + 1];
        for (int t = 1; t <= targetCount; t++)
        {
            tSets[t] = targetWordsByPage.TryGetValue(t, out var wList)
                ? new HashSet<string>(wList.Select(w => w.Text.ToUpperInvariant()), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }

        double[,] dp = new double[sourceCount + 1, targetCount + 1];
        const double GapPenalty = 0.25;

        for (int i = 1; i <= sourceCount; i++) dp[i, 0] = i * -GapPenalty;
        for (int j = 1; j <= targetCount; j++) dp[0, j] = j * -GapPenalty;

        for (int i = 1; i <= sourceCount; i++)
        {
            for (int j = 1; j <= targetCount; j++)
            {
                double sim = ComputeJaccard(sSets[i], tSets[j]);
                double matchScore = dp[i - 1, j - 1] + sim;
                double deleteScore = dp[i - 1, j] - GapPenalty;
                double insertScore = dp[i, j - 1] - GapPenalty;

                dp[i, j] = Math.Max(matchScore, Math.Max(deleteScore, insertScore));
            }
        }

        int si = sourceCount, tj = targetCount;
        while (si > 0 || tj > 0)
        {
            if (si > 0 && tj > 0)
            {
                double sim = ComputeJaccard(sSets[si], tSets[tj]);
                if (Math.Abs(dp[si, tj] - (dp[si - 1, tj - 1] + sim)) < 1e-6)
                {
                    result.Add((si, tj));
                    si--;
                    tj--;
                    continue;
                }
            }

            if (si > 0 && (tj == 0 || Math.Abs(dp[si, tj] - (dp[si - 1, tj] - GapPenalty)) < 1e-6))
            {
                result.Add((si, null));
                si--;
            }
            else
            {
                result.Add((null, tj));
                tj--;
            }
        }

        result.Reverse();
        return result;
    }

    private static double ComputeJaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        if (a.Count == 0 || b.Count == 0) return 0.0;

        int inter = 0;
        foreach (var item in a)
        {
            if (b.Contains(item)) inter++;
        }

        int union = a.Count + b.Count - inter;
        return union > 0 ? (double)inter / union : 0.0;
    }

    /// <summary>
    /// Rastérise une page PDF de manière déterministe en dessinant les images embarquées et les mots filtrés/masqués,
    /// tout en occultant les zones d'exclusion géométriques du profil actif.
    /// </summary>
    private static Bitmap RenderNormalizedPageBitmap(
        Page page,
        IReadOnlyList<PdfWordInfo> filteredWords,
        IReadOnlyList<IPdfImage> pageImages,
        int totalPages,
        double scale,
        ComparisonProfile profile)
    {
        int widthPx = Math.Max(64, (int)Math.Round(page.Width * scale));
        int heightPx = Math.Max(64, (int)Math.Round(page.Height * scale));

        var bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var activeZones = profile.ExclusionZones
            .Where(z => z.AppliesToPage(page.Number, totalPages))
            .ToList();

        // 1. Dessin des images embarquées (logos, graphiques, tampons, scans) hors zones d'exclusion
        foreach (var pdfImg in pageImages)
        {
            var b = pdfImg.Bounds;
            bool isExcluded = activeZones.Any(z =>
                z.IntersectsPdfBox(b.BottomLeft.X, b.BottomLeft.Y, b.TopRight.X, b.TopRight.Y, page.Width, page.Height));

            if (isExcluded) continue;

            if (TryDecodePdfImage(pdfImg, out var decodedBmp) && decodedBmp != null)
            {
                using (decodedBmp)
                {
                    float x = (float)(b.BottomLeft.X * scale);
                    float y = (float)((page.Height - b.TopRight.Y) * scale);
                    float w = Math.Max(1f, (float)((b.TopRight.X - b.BottomLeft.X) * scale));
                    float h = Math.Max(1f, (float)((b.TopRight.Y - b.BottomLeft.Y) * scale));
                    g.DrawImage(decodedBmp, x, y, w, h);
                }
            }
        }

        // 1b. Dessin des tracés vectoriels PDF (bordures de tableaux, filets, signatures vectorielles)
        try
        {
            var paths = page.ExperimentalAccess?.Paths;
            if (paths != null)
            {
                using var vectorPen = new Pen(System.Drawing.Color.FromArgb(140, 60, 70, 85), Math.Max(1f, (float)(0.75 * scale)));
                foreach (var pdfPath in paths)
                {
                    if (!pdfPath.IsStroked && !pdfPath.IsFilled) continue;
                    foreach (var subPath in pdfPath)
                    {
                        var bbox = subPath.GetBoundingRectangle();
                        if (!bbox.HasValue) continue;
                        var b = bbox.Value;

                        // Ignore les grands cadres pleins de fond de page (> 90% de la page)
                        if (b.Width > page.Width * 0.9 && b.Height > page.Height * 0.9) continue;

                        bool isExcluded = activeZones.Any(z =>
                            z.IntersectsPdfBox(b.BottomLeft.X, b.BottomLeft.Y, b.TopRight.X, b.TopRight.Y, page.Width, page.Height));
                        if (isExcluded) continue;

                        float rx = (float)(b.BottomLeft.X * scale);
                        float ry = (float)((page.Height - b.TopRight.Y) * scale);
                        float rw = Math.Max(1f, (float)(b.Width * scale));
                        float rh = Math.Max(1f, (float)(b.Height * scale));
                        g.DrawRectangle(vectorPen, rx, ry, rw, rh);
                    }
                }
            }
        }
        catch
        {
            // Ignorer silencieusement si un tracé vectoriel PDF est malformé
        }

        // 2. Dessin des mots filtrés/masqués (les jetons [DATE_IGNORE] / [NOM_IGNORE] sont rendus de manière identique)
        using var textBrush = new SolidBrush(System.Drawing.Color.FromArgb(20, 24, 35));
        using var stringFormat = StringFormat.GenericTypographic;

        foreach (var word in filteredWords)
        {
            if (word.Letters == null || word.Letters.Count == 0 || string.IsNullOrWhiteSpace(word.Text))
                continue;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            double maxPt = 9.0;

            foreach (var l in word.Letters)
            {
                var r = l.GlyphRectangle;
                if (r.BottomLeft.X < minX) minX = r.BottomLeft.X;
                if (r.BottomLeft.Y < minY) minY = r.BottomLeft.Y;
                if (r.TopRight.X > maxX) maxX = r.TopRight.X;
                if (r.TopRight.Y > maxY) maxY = r.TopRight.Y;
                if (l.PointSize > maxPt) maxPt = l.PointSize;
            }

            float fontSizePx = Math.Clamp((float)(maxPt * scale * 0.85), 6f, 72f);
            float drawX = (float)(minX * scale);
            float drawY = (float)((page.Height - maxY) * scale);

            using var font = new Font("Arial", fontSizePx, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString(word.Text, font, textBrush, drawX, drawY, stringFormat);
        }

        // 3. Masquage blanc des zones d'exclusion géométriques
        using var maskBrush = new SolidBrush(System.Drawing.Color.White);
        foreach (var zone in activeZones)
        {
            var (zLeft, zBottom, zRight, zTop) = zone.ToPdfBounds(page.Width, page.Height);
            float x = (float)(Math.Max(0, zLeft) * scale);
            float y = (float)(Math.Max(0, page.Height - zTop) * scale);
            float w = (float)(Math.Max(0, zRight - zLeft) * scale);
            float h = (float)(Math.Max(0, zTop - zBottom) * scale);
            g.FillRectangle(maskBrush, x, y, w, h);
        }

        return bmp;
    }

    /// <summary>
    /// Compare deux Bitmaps avec :
    /// 1. Alignement par translation (dx, dy) pour absorber les micro-décalages
    /// 2. Calcul SSIM local O(1) par images intégrales
    /// 3. Distance perceptuelle YIQ + Filtre d'anti-aliasing de Vysniauskas
    /// 4. Ouverture morphologique + Regroupement en composantes connexes
    /// </summary>
    private static (double GlobalSsim, List<Rectangle> DiffRegionsPx, int ShiftX, int ShiftY) CompareBitmapsPerceptually(
        Bitmap srcBmp,
        Bitmap trgBmp,
        ComparisonProfile profile)
    {
        int width = Math.Min(srcBmp.Width, trgBmp.Width);
        int height = Math.Min(srcBmp.Height, trgBmp.Height);
        if (width < 16 || height < 16)
        {
            return (1.0, new List<Rectangle>(), 0, 0);
        }

        byte[] srcBgra = ExtractBgraBytes(srcBmp, width, height, out byte[] srcGray);
        byte[] trgBgra = ExtractBgraBytes(trgBmp, width, height, out byte[] trgGray);

        // 1. Recalage sub-pixel / translation globale (dx, dy)
        int bestDx = 0, bestDy = 0;
        if (profile.EnableSubPixelAutoAlignment && profile.MaxAutoAlignmentShiftPx > 0)
        {
            (bestDx, bestDy) = FindOptimalTranslationShift(srcGray, trgGray, width, height, profile.MaxAutoAlignmentShiftPx);
        }

        // Application du décalage (bestDx, bestDy) sur les buffers cibles si nécessaire
        if (bestDx != 0 || bestDy != 0)
        {
            ApplyTranslationShift(trgBgra, trgGray, width, height, bestDx, bestDy, out trgBgra, out trgGray);
        }

        // 2. Calcul SSIM par Images Intégrales (fenêtre 9x9, rayon R = 4) en temps O(1) par pixel
        int n = width * height;
        long[] int1 = new long[n];
        long[] int2 = new long[n];
        long[] int1Sq = new long[n];
        long[] int2Sq = new long[n];
        long[] int12 = new long[n];

        BuildIntegralImages(srcGray, trgGray, width, height, int1, int2, int1Sq, int2Sq, int12);

        const double C1 = 6.5025;
        const double C2 = 58.5225;
        const int radius = 4;

        double ssimSum = 0.0;
        int ssimSamples = 0;

        byte[] diffMask = new byte[n];
        double maxYiqDeltaSq = 35215.0 * profile.PerceptualYiqThreshold * profile.PerceptualYiqThreshold;
        double ssimThreshold = profile.SsimSimilarityThreshold;
        bool ignoreAA = profile.IgnoreAntiAliasingPixels;

        for (int y = radius; y < height - radius; y++)
        {
            int y0 = y - radius;
            int y1 = y + radius;
            int rowOffset = y * width;

            for (int x = radius; x < width - radius; x++)
            {
                int idx = rowOffset + x;
                int x0 = x - radius;
                int x1 = x + radius;
                double area = (x1 - x0 + 1) * (y1 - y0 + 1);

                double s1 = GetIntegralSum(int1, width, x0, y0, x1, y1);
                double s2 = GetIntegralSum(int2, width, x0, y0, x1, y1);
                double s1Sq = GetIntegralSum(int1Sq, width, x0, y0, x1, y1);
                double s2Sq = GetIntegralSum(int2Sq, width, x0, y0, x1, y1);
                double s12 = GetIntegralSum(int12, width, x0, y0, x1, y1);

                double mu1 = s1 / area;
                double mu2 = s2 / area;
                double mu1Sq = mu1 * mu1;
                double mu2Sq = mu2 * mu2;
                double mu12 = mu1 * mu2;

                double sigma1Sq = Math.Max(0.0, (s1Sq / area) - mu1Sq);
                double sigma2Sq = Math.Max(0.0, (s2Sq / area) - mu2Sq);
                double sigma12 = (s12 / area) - mu12;

                double numerator = (2.0 * mu12 + C1) * (2.0 * sigma12 + C2);
                double denominator = (mu1Sq + mu2Sq + C1) * (sigma1Sq + sigma2Sq + C2);
                double localSsim = denominator > 0 ? numerator / denominator : 1.0;

                ssimSum += localSsim;
                ssimSamples++;

                if (localSsim >= ssimThreshold) continue;

                // 3. Vérification de la distance colorimétrique perceptuelle YIQ
                int bgraIdx = idx * 4;
                byte b1 = srcBgra[bgraIdx], g1 = srcBgra[bgraIdx + 1], r1 = srcBgra[bgraIdx + 2];
                byte b2 = trgBgra[bgraIdx], g2 = trgBgra[bgraIdx + 1], r2 = trgBgra[bgraIdx + 2];

                if (b1 == b2 && g1 == g2 && r1 == r2) continue;

                double dy = (r1 * 0.29889531 + g1 * 0.58662247 + b1 * 0.11448223) -
                            (r2 * 0.29889531 + g2 * 0.58662247 + b2 * 0.11448223);
                double di = (r1 * 0.59597799 - g1 * 0.27417610 - b1 * 0.32180189) -
                            (r2 * 0.59597799 - g2 * 0.27417610 - b2 * 0.32180189);
                double dq = (r1 * 0.21147017 - g1 * 0.52261711 + b1 * 0.31114694) -
                            (r2 * 0.21147017 - g2 * 0.52261711 + b2 * 0.31114694);

                double deltaSq = 0.5053 * dy * dy + 0.299 * di * di + 0.1957 * dq * dq;
                if (deltaSq <= maxYiqDeltaSq) continue;

                // 4. Détecteur d'anti-aliasing de Vysniauskas (Pixelmatch)
                if (ignoreAA &&
                    (IsAntiAliasedPixel(srcGray, x, y, width) || IsAntiAliasedPixel(trgGray, x, y, width)))
                {
                    continue;
                }

                diffMask[idx] = 1;
            }
        }

        double meanSsim = ssimSamples > 0 ? Math.Clamp(ssimSum / ssimSamples, 0.0, 1.0) : 1.0;

        // 5. Filtrage morphologique (suppression des pixels isolés) et extraction des boîtes englobantes
        byte[] cleanedMask = FilterIsolatedPixels(diffMask, width, height);
        List<Rectangle> regions = ExtractConnectedDiffRegions(cleanedMask, width, height, profile.MinVisualClusterAreaPx);

        if (regions.Count == 0)
        {
            meanSsim = Math.Max(meanSsim, 0.999);
        }

        return (meanSsim, regions, bestDx, bestDy);
    }

    private static (int BestDx, int BestDy) FindOptimalTranslationShift(
        byte[] srcGray, byte[] trgGray, int width, int height, int maxShift)
    {
        int step = 4; // Sous-échantillonnage rapide 1/4 pour le recalage
        double bestSad = double.MaxValue;
        int bestDx = 0, bestDy = 0;

        int margin = maxShift + 8;
        if (width <= margin * 2 || height <= margin * 2) return (0, 0);

        for (int dy = -maxShift; dy <= maxShift; dy++)
        {
            for (int dx = -maxShift; dx <= maxShift; dx++)
            {
                long sad = 0;
                int count = 0;

                for (int y = margin; y < height - margin; y += step)
                {
                    int srcRow = y * width;
                    int trgRow = (y + dy) * width;

                    for (int x = margin; x < width - margin; x += step)
                    {
                        sad += Math.Abs(srcGray[srcRow + x] - trgGray[trgRow + x + dx]);
                        count++;
                    }
                }

                // Légère pénalité (0.05) pour préférer (0,0) si le score est identique sur fond blanc
                double avgSad = (count > 0 ? (double)sad / count : double.MaxValue) + (Math.Abs(dx) + Math.Abs(dy)) * 0.02;
                if (avgSad < bestSad)
                {
                    bestSad = avgSad;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }

        return (bestDx, bestDy);
    }

    private static void ApplyTranslationShift(
        byte[] trgBgra, byte[] trgGray, int width, int height, int dx, int dy,
        out byte[] shiftedBgra, out byte[] shiftedGray)
    {
        shiftedBgra = new byte[trgBgra.Length];
        shiftedGray = new byte[trgGray.Length];
        Array.Fill(shiftedBgra, (byte)255);
        Array.Fill(shiftedGray, (byte)255);

        for (int y = 0; y < height; y++)
        {
            int srcY = y + dy;
            if (srcY < 0 || srcY >= height) continue;

            for (int x = 0; x < width; x++)
            {
                int srcX = x + dx;
                if (srcX < 0 || srcX >= width) continue;

                int dstIdx = y * width + x;
                int srcIdx = srcY * width + srcX;

                shiftedGray[dstIdx] = trgGray[srcIdx];

                int dstB = dstIdx * 4;
                int srcB = srcIdx * 4;
                shiftedBgra[dstB] = trgBgra[srcB];
                shiftedBgra[dstB + 1] = trgBgra[srcB + 1];
                shiftedBgra[dstB + 2] = trgBgra[srcB + 2];
                shiftedBgra[dstB + 3] = trgBgra[srcB + 3];
            }
        }
    }

    private static void BuildIntegralImages(
        byte[] g1, byte[] g2, int width, int height,
        long[] int1, long[] int2, long[] int1Sq, long[] int2Sq, long[] int12)
    {
        for (int y = 0; y < height; y++)
        {
            long row1 = 0, row2 = 0, row1Sq = 0, row2Sq = 0, row12 = 0;
            int rowOffset = y * width;
            int prevRowOffset = (y - 1) * width;

            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                long v1 = g1[idx];
                long v2 = g2[idx];

                row1 += v1;
                row2 += v2;
                row1Sq += v1 * v1;
                row2Sq += v2 * v2;
                row12 += v1 * v2;

                if (y > 0)
                {
                    int prevIdx = prevRowOffset + x;
                    int1[idx] = int1[prevIdx] + row1;
                    int2[idx] = int2[prevIdx] + row2;
                    int1Sq[idx] = int1Sq[prevIdx] + row1Sq;
                    int2Sq[idx] = int2Sq[prevIdx] + row2Sq;
                    int12[idx] = int12[prevIdx] + row12;
                }
                else
                {
                    int1[idx] = row1;
                    int2[idx] = row2;
                    int1Sq[idx] = row1Sq;
                    int2Sq[idx] = row2Sq;
                    int12[idx] = row12;
                }
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static long GetIntegralSum(long[] integral, int width, int x0, int y0, int x1, int y1)
    {
        long d = integral[y1 * width + x1];
        long b = y0 > 0 ? integral[(y0 - 1) * width + x1] : 0;
        long c = x0 > 0 ? integral[y1 * width + (x0 - 1)] : 0;
        long a = (x0 > 0 && y0 > 0) ? integral[(y0 - 1) * width + (x0 - 1)] : 0;
        return d - b - c + a;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsAntiAliasedPixel(byte[] gray, int x, int y, int width)
    {
        int center = gray[y * width + x];
        int darker = 0, brighter = 0, equal = 0;

        for (int ny = y - 1; ny <= y + 1; ny++)
        {
            int row = ny * width;
            for (int nx = x - 1; nx <= x + 1; nx++)
            {
                if (nx == x && ny == y) continue;
                int diff = gray[row + nx] - center;
                if (Math.Abs(diff) <= 4) equal++;
                else if (diff < -18) darker++;
                else if (diff > 18) brighter++;
            }
        }

        return darker >= 1 && brighter >= 1 && equal >= 1 && equal <= 5;
    }

    private static byte[] FilterIsolatedPixels(byte[] mask, int width, int height)
    {
        byte[] cleaned = new byte[mask.Length];

        for (int y = 1; y < height - 1; y++)
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                if (mask[row + x] == 0) continue;

                int neighbors =
                    mask[(y - 1) * width + (x - 1)] + mask[(y - 1) * width + x] + mask[(y - 1) * width + (x + 1)] +
                    mask[row + (x - 1)] + mask[row + (x + 1)] +
                    mask[(y + 1) * width + (x - 1)] + mask[(y + 1) * width + x] + mask[(y + 1) * width + (x + 1)];

                if (neighbors >= 2)
                {
                    cleaned[row + x] = 1;
                }
            }
        }

        return cleaned;
    }

    private static List<Rectangle> ExtractConnectedDiffRegions(byte[] mask, int width, int height, int minClusterArea)
    {
        var regions = new List<Rectangle>();
        bool[] visited = new bool[mask.Length];
        var queue = new Queue<(int X, int Y)>();

        const int mergeRadiusX = 8;
        const int mergeRadiusY = 4;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask[idx] == 0 || visited[idx]) continue;

                visited[idx] = true;
                queue.Enqueue((x, y));

                int minX = x, maxX = x;
                int minY = y, maxY = y;
                int activeCount = 0;

                while (queue.Count > 0)
                {
                    var (cx, cy) = queue.Dequeue();
                    activeCount++;

                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;

                    int yStart = Math.Max(0, cy - mergeRadiusY);
                    int yEnd = Math.Min(height - 1, cy + mergeRadiusY);
                    int xStart = Math.Max(0, cx - mergeRadiusX);
                    int xEnd = Math.Min(width - 1, cx + mergeRadiusX);

                    for (int ny = yStart; ny <= yEnd; ny++)
                    {
                        int nRow = ny * width;
                        for (int nx = xStart; nx <= xEnd; nx++)
                        {
                            int nIdx = nRow + nx;
                            if (mask[nIdx] != 0 && !visited[nIdx])
                            {
                                visited[nIdx] = true;
                                queue.Enqueue((nx, ny));
                            }
                        }
                    }
                }

                if (activeCount >= minClusterArea)
                {
                    regions.Add(new Rectangle(minX, minY, Math.Max(4, maxX - minX + 1), Math.Max(4, maxY - minY + 1)));
                }
            }
        }

        return regions;
    }

    private static bool IntersectsAnyVisualRegion(
        PdfRectangle letterBox,
        IReadOnlyList<VisualDiffRegion> pageRegions,
        double tolerancePt)
    {
        if (pageRegions.Count == 0) return false;

        double lLeft = letterBox.BottomLeft.X - tolerancePt;
        double lRight = letterBox.TopRight.X + tolerancePt;
        double lBottom = letterBox.BottomLeft.Y - tolerancePt;
        double lTop = letterBox.TopRight.Y + tolerancePt;

        for (int i = 0; i < pageRegions.Count; i++)
        {
            var r = pageRegions[i].PdfBounds;
            if (lLeft <= r.TopRight.X &&
                lRight >= r.BottomLeft.X &&
                lBottom <= r.TopRight.Y &&
                lTop >= r.BottomLeft.Y)
            {
                return true;
            }
        }

        return false;
    }

    private static void AttachVisualCropsToSummaryBlocks(
        DiffAnalysisResult analysisResult,
        Bitmap srcBmp,
        Bitmap trgBmp,
        int pageNum,
        double pageHeightPt,
        double scale,
        int shiftX,
        int shiftY,
        List<LetterLoc> pageSourceRed,
        List<LetterLoc> pageTargetRed)
    {
        var allPageLetters = new List<LetterLoc>(pageSourceRed.Count + pageTargetRed.Count);
        allPageLetters.AddRange(pageSourceRed);
        allPageLetters.AddRange(pageTargetRed);
        if (allPageLetters.Count == 0) return;

        var segments = VisualSegmentHelper.GetSegments(allPageLetters);
        if (segments.Count == 0) return;

        // Regroupement vertical des segments proches en bandeaux visuels
        var bands = new List<(double MinX, double MinY, double MaxX, double MaxY)>();
        double curMinX = (double)segments[0].minX;
        double curMaxX = (double)segments[0].maxX;
        double curMinY = (double)(segments[0].baselineY - segments[0].fontSize * 0.4m);
        double curMaxY = (double)(segments[0].baselineY + segments[0].fontSize * 1.1m);

        for (int i = 1; i < segments.Count; i++)
        {
            var seg = segments[i];
            double sMinY = (double)(seg.baselineY - seg.fontSize * 0.4m);
            double sMaxY = (double)(seg.baselineY + seg.fontSize * 1.1m);

            if (curMinY - sMaxY < 28.0)
            {
                curMinX = Math.Min(curMinX, (double)seg.minX);
                curMaxX = Math.Max(curMaxX, (double)seg.maxX);
                curMinY = Math.Min(curMinY, sMinY);
                curMaxY = Math.Max(curMaxY, sMaxY);
            }
            else
            {
                bands.Add((curMinX, curMinY, curMaxX, curMaxY));
                curMinX = (double)seg.minX;
                curMaxX = (double)seg.maxX;
                curMinY = sMinY;
                curMaxY = sMaxY;
            }
        }
        bands.Add((curMinX, curMinY, curMaxX, curMaxY));

        // Associe les bandeaux visuels aux blocs du résumé n'ayant pas encore de capture d'image
        int bandIdx = 0;
        foreach (var block in analysisResult.Summary.Blocks.Where(b => b.SourceImage == null && b.TargetImage == null))
        {
            if (bandIdx >= bands.Count || bandIdx >= 15) break;

            var band = bands[bandIdx++];
            var pdfRect = new PdfRectangle(
                Math.Max(0, band.MinX - 20.0),
                Math.Max(0, band.MinY - 12.0),
                band.MaxX + 20.0,
                band.MaxY + 12.0);

            var (srcCrop, trgCrop) = CreateAnnotatedSideBySideCrops(
                srcBmp, trgBmp, pdfRect, pageHeightPt, scale, shiftX, shiftY, pageSourceRed, pageTargetRed);

            block.SourceImage = srcCrop;
            block.TargetImage = trgCrop;
        }
    }

    private static (byte[]? SourcePng, byte[]? TargetPng) CreateAnnotatedSideBySideCrops(
        Bitmap srcBmp,
        Bitmap trgBmp,
        PdfRectangle pdfBounds,
        double pageHeightPt,
        double scale,
        int shiftX,
        int shiftY,
        List<LetterLoc>? sourceRedLetters = null,
        List<LetterLoc>? targetRedLetters = null)
    {
        int paddingPx = (int)Math.Round(18 * scale);

        int x = Math.Max(0, (int)Math.Round(pdfBounds.BottomLeft.X * scale) - paddingPx);
        int y = Math.Max(0, (int)Math.Round((pageHeightPt - pdfBounds.TopRight.Y) * scale) - paddingPx);
        int right = Math.Min(srcBmp.Width, (int)Math.Round(pdfBounds.TopRight.X * scale) + paddingPx);
        int bottom = Math.Min(srcBmp.Height, (int)Math.Round((pageHeightPt - pdfBounds.BottomLeft.Y) * scale) + paddingPx);

        // Élargit le bandeau horizontalement pour donner le contexte de la ligne sur la page
        x = Math.Max(0, Math.Min(x, (int)(srcBmp.Width * 0.06)));
        right = Math.Min(srcBmp.Width, Math.Max(right, (int)(srcBmp.Width * 0.94)));

        int w = Math.Max(32, right - x);
        int h = Math.Max(24, bottom - y);

        if (x + w > srcBmp.Width) w = srcBmp.Width - x;
        if (y + h > srcBmp.Height) h = srcBmp.Height - y;
        if (w <= 8 || h <= 8) return (null, null);

        var srcCropRect = new Rectangle(x, y, w, h);
        var trgCropRect = new Rectangle(
            Math.Clamp(x + shiftX, 0, Math.Max(0, trgBmp.Width - w)),
            Math.Clamp(y + shiftY, 0, Math.Max(0, trgBmp.Height - h)),
            Math.Min(w, trgBmp.Width),
            Math.Min(h, trgBmp.Height));

        byte[] srcPng = RenderAnnotatedCropToPng(
            srcBmp, srcCropRect, pageHeightPt, scale, sourceRedLetters, System.Drawing.Color.FromArgb(239, 68, 68));

        byte[] trgPng = RenderAnnotatedCropToPng(
            trgBmp, trgCropRect, pageHeightPt, scale, targetRedLetters, System.Drawing.Color.FromArgb(16, 185, 129));

        return (srcPng, trgPng);
    }

    private static byte[] RenderAnnotatedCropToPng(
        Bitmap pageBmp,
        Rectangle cropRect,
        double pageHeightPt,
        double scale,
        List<LetterLoc>? highlightLetters,
        System.Drawing.Color highlightColor)
    {
        using var cropBmp = new Bitmap(cropRect.Width, cropRect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(cropBmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(pageBmp, new Rectangle(0, 0, cropRect.Width, cropRect.Height), cropRect, GraphicsUnit.Pixel);

        if (highlightLetters != null && highlightLetters.Count > 0)
        {
            using var fillBrush = new SolidBrush(System.Drawing.Color.FromArgb(55, highlightColor));
            using var borderPen = new Pen(System.Drawing.Color.FromArgb(210, highlightColor), 1.5f);

            var segments = VisualSegmentHelper.GetSegments(highlightLetters);
            foreach (var seg in segments)
            {
                float sx = (float)((double)seg.minX * scale) - cropRect.X - 2f;
                float sy = (float)((pageHeightPt - (double)(seg.baselineY + seg.fontSize * 0.9m)) * scale) - cropRect.Y - 1f;
                float sw = Math.Max(4f, (float)((double)(seg.maxX - seg.minX) * scale) + 4f);
                float sh = Math.Max(6f, (float)((double)(seg.fontSize * 1.15m) * scale) + 2f);

                if (sx + sw >= 0 && sx <= cropRect.Width && sy + sh >= 0 && sy <= cropRect.Height)
                {
                    g.FillRectangle(fillBrush, sx, sy, sw, sh);
                    g.DrawRectangle(borderPen, sx, sy, sw, sh);
                }
            }
        }

        using var borderFramePen = new Pen(System.Drawing.Color.FromArgb(203, 213, 225), 1f);
        g.DrawRectangle(borderFramePen, 0, 0, cropRect.Width - 1, cropRect.Height - 1);

        using var ms = new MemoryStream();
        cropBmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] ExtractBgraBytes(Bitmap bmp, int width, int height, out byte[] gray)
    {
        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] raw = new byte[stride * height];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);

            byte[] bgra = new byte[width * height * 4];
            gray = new byte[width * height];

            for (int y = 0; y < height; y++)
            {
                int srcRow = y * stride;
                int dstRowBgra = y * width * 4;
                int dstRowGray = y * width;

                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 4;
                    int d = dstRowBgra + x * 4;

                    byte b = raw[s];
                    byte g = raw[s + 1];
                    byte r = raw[s + 2];
                    byte a = raw[s + 3];

                    bgra[d] = b;
                    bgra[d + 1] = g;
                    bgra[d + 2] = r;
                    bgra[d + 3] = a;

                    gray[dstRowGray + x] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                }
            }

            return bgra;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static IReadOnlyList<IPdfImage> SafeGetImages(Page page)
    {
        try
        {
            return page.GetImages().ToList();
        }
        catch
        {
            return Array.Empty<IPdfImage>();
        }
    }

    private static bool TryDecodePdfImage(IPdfImage pdfImg, out Bitmap? bitmap)
    {
        bitmap = null;
        try
        {
            if (pdfImg.TryGetPng(out var pngBytes) && pngBytes != null && pngBytes.Length > 0)
            {
                using var ms = new MemoryStream(pngBytes);
                using var temp = Image.FromStream(ms);
                bitmap = new Bitmap(temp);
                return true;
            }

            var rawBytes = pdfImg.RawBytes.ToArray();
            if (rawBytes.Length > 0)
            {
                using var ms = new MemoryStream(rawBytes);
                using var temp = Image.FromStream(ms);
                bitmap = new Bitmap(temp);
                return true;
            }
        }
        catch
        {
            // Ignore les flux d'images PDF propriétaires non décodables directement
        }

        return false;
    }
}
