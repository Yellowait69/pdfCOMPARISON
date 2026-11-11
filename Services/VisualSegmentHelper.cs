using System;
using System.Collections.Generic;
using System.Linq;
using PDFComparison.Models;

namespace PDFComparison.Services;

public static class VisualSegmentHelper
{
    private const decimal DefaultAlignmentTolerance = 4.5m;

    public static List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)> GetSegments(IEnumerable<LetterLoc> letters)
    {
        var segments = new List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)>();
        if (letters == null) return segments;

        var rawList = letters as IList<LetterLoc> ?? letters.ToList();
        if (rawList.Count == 0) return segments;

        // Regroupement adaptatif par ligne (moyenne glissante sur BaselineY)
        var byY = rawList.OrderByDescending(l => l.BaselineY).ToList();
        var lineClusters = new List<(decimal ReferenceY, List<LetterLoc> Items)>();

        foreach (var loc in byY)
        {
            decimal tol = Math.Max(DefaultAlignmentTolerance, loc.FontSize * 0.48m);
            int bestIdx = -1;
            decimal bestDelta = decimal.MaxValue;

            for (int i = 0; i < lineClusters.Count; i++)
            {
                decimal delta = Math.Abs(lineClusters[i].ReferenceY - loc.BaselineY);
                if (delta <= tol && delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIdx = i;
                }
            }

            if (bestIdx == -1)
            {
                lineClusters.Add((loc.BaselineY, new List<LetterLoc> { loc }));
            }
            else
            {
                var cluster = lineClusters[bestIdx];
                int count = cluster.Items.Count;
                decimal updatedRefY = ((cluster.ReferenceY * count) + loc.BaselineY) / (count + 1);
                cluster.Items.Add(loc);
                lineClusters[bestIdx] = (updatedRefY, cluster.Items);
            }
        }

        foreach (var cluster in lineClusters.OrderByDescending(c => c.ReferenceY))
        {
            var sortedLine = cluster.Items.OrderBy(l => l.BoundingBox.BottomLeft.X).ToList();
            if (sortedLine.Count == 0) continue;

            var first = sortedLine[0];
            decimal cMinX = (decimal)first.BoundingBox.BottomLeft.X;
            decimal cMaxX = (decimal)first.BoundingBox.TopRight.X;
            decimal cBaseline = cluster.ReferenceY;
            decimal cFontSize = first.FontSize;

            for (int i = 1; i < sortedLine.Count; i++)
            {
                var loc = sortedLine[i];
                decimal x = (decimal)loc.BoundingBox.BottomLeft.X;
                decimal rightX = (decimal)loc.BoundingBox.TopRight.X;
                // Fusionne les mots qui se suivent sur la même ligne en un seul segment continu
                decimal maxGap = Math.Max(24m, cFontSize * 2.5m);

                if ((x - cMaxX) <= maxGap && x >= cMinX - 5m)
                {
                    cMaxX = Math.Max(cMaxX, rightX);
                    cFontSize = Math.Max(cFontSize, loc.FontSize);
                }
                else
                {
                    segments.Add((cMinX, cMaxX, cBaseline, cFontSize));
                    cMinX = x;
                    cMaxX = rightX;
                    cBaseline = loc.BaselineY;
                    cFontSize = loc.FontSize;
                }
            }

            segments.Add((cMinX, cMaxX, cBaseline, cFontSize));
        }

        return segments;
    }

    /// <summary>
    /// Regroupe les segments qui se suivent (sur la même ligne ou sur des lignes consécutives)
    /// en un seul bloc rectangulaire unifié (minX, maxX, minY, maxY, fontSize).
    /// </summary>
    public static List<(decimal minX, decimal maxX, decimal minY, decimal maxY, decimal fontSize)> GetUnifiedBlocks(IEnumerable<LetterLoc> letters)
    {
        var segments = GetSegments(letters);
        var blocks = new List<(decimal minX, decimal maxX, decimal minY, decimal maxY, decimal fontSize)>();
        if (segments.Count == 0) return blocks;

        var first = segments[0];
        decimal bMinX = first.minX;
        decimal bMaxX = first.maxX;
        decimal bMinY = first.baselineY - (first.fontSize * 0.22m);
        decimal bMaxY = first.baselineY + (first.fontSize * 0.92m);
        decimal bFont = first.fontSize;

        for (int i = 1; i < segments.Count; i++)
        {
            var seg = segments[i];
            decimal sMinY = seg.baselineY - (seg.fontSize * 0.22m);
            decimal sMaxY = seg.baselineY + (seg.fontSize * 0.92m);

            decimal verticalGap = bMinY - sMaxY;
            decimal maxAllowedVerticalGap = Math.Max(18m, Math.Max(bFont, seg.fontSize) * 2.2m);

            // Vérifie si les segments se chevauchent ou sont proches horizontalement et se suivent verticalement
            bool horizontalOverlapOrClose = (seg.minX <= bMaxX + 120m) && (seg.maxX >= bMinX - 120m);

            if (verticalGap <= maxAllowedVerticalGap && horizontalOverlapOrClose)
            {
                bMinX = Math.Min(bMinX, seg.minX);
                bMaxX = Math.Max(bMaxX, seg.maxX);
                bMinY = Math.Min(bMinY, sMinY);
                bMaxY = Math.Max(bMaxY, sMaxY);
                bFont = Math.Max(bFont, seg.fontSize);
            }
            else
            {
                blocks.Add((bMinX, bMaxX, bMinY, bMaxY, bFont));
                bMinX = seg.minX;
                bMaxX = seg.maxX;
                bMinY = sMinY;
                bMaxY = sMaxY;
                bFont = seg.fontSize;
            }
        }

        blocks.Add((bMinX, bMaxX, bMinY, bMaxY, bFont));
        return blocks;
    }

    public static int CountBlocks(List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)> segments)
    {
        if (segments == null || segments.Count == 0) return 0;

        int blocksCount = 0;
        decimal currentMaxY = segments[0].baselineY + (segments[0].fontSize * 0.9m);
        decimal currentMinY = segments[0].baselineY - (segments[0].fontSize * 0.2m);

        for (int i = 1; i < segments.Count; i++)
        {
            var seg = segments[i];
            decimal boxMinY = seg.baselineY - (seg.fontSize * 0.2m);
            decimal boxMaxY = seg.baselineY + (seg.fontSize * 0.9m);

            if (currentMinY - boxMaxY < Math.Max(18m, seg.fontSize * 2.2m))
            {
                currentMinY = Math.Min(currentMinY, boxMinY);
                currentMaxY = Math.Max(currentMaxY, boxMaxY);
            }
            else
            {
                blocksCount++;
                currentMaxY = boxMaxY;
                currentMinY = boxMinY;
            }
        }

        blocksCount++;
        return blocksCount;
    }
}
