using System;
using System.Collections.Generic;
using System.Linq;
using PDFComparison.Models;

namespace PDFComparison.Services;

public static class VisualSegmentHelper
{
    private const decimal AlignmentTolerance = 5.0m;

    public static List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)> GetSegments(IEnumerable<LetterLoc> letters)
    {
        var segments = new List<(decimal minX, decimal maxX, decimal baselineY, decimal fontSize)>();
        if (letters == null || !letters.Any()) return segments;

        var sorted = letters
            .OrderByDescending(l => Math.Round(l.BaselineY / AlignmentTolerance) * AlignmentTolerance)
            .ThenBy(l => l.BoundingBox.BottomLeft.X)
            .ToList();

        if (sorted.Count == 0) return segments;

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

        return segments;
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

            if (currentMinY - boxMaxY < seg.fontSize * 2.0m)
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