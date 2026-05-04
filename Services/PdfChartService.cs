using System.Collections.Generic;
using System.Linq;
using PDFComparison.Models;
using ScottPlot;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;

namespace PDFComparison.Services;

public interface IPdfChartService
{
    void DrawDashboardCharts(PdfPageBuilder page, decimal startX, decimal currentY, int inserts, int deletes, int dates, int numbers, int words, Dictionary<string, int> languageFileCounts, PdfDocumentBuilder.AddedFont font);
}

public class PdfChartService : IPdfChartService
{
    public void DrawDashboardCharts(PdfPageBuilder page, decimal startX, decimal currentY, int inserts, int deletes, int dates, int numbers, int words, Dictionary<string, int> languageFileCounts, PdfDocumentBuilder.AddedFont font)
    {
        var slices1 = new List<PieSlice>();
        if (inserts > 0) slices1.Add(new PieSlice { Value = inserts, Label = $"{inserts} Ajouts", FillColor = ScottPlot.Color.FromHex("#10B981") });
        if (deletes > 0) slices1.Add(new PieSlice { Value = deletes, Label = $"{deletes} Supp.", FillColor = ScottPlot.Color.FromHex("#EF4444") });

        DrawPieChartOrEmptyState(page, slices1, startX, currentY, font);

        var slices2 = new List<PieSlice>();
        if (words > 0) slices2.Add(new PieSlice { Value = words, Label = $"{words} Textes", FillColor = ScottPlot.Color.FromHex("#3B82F6") });
        if (numbers > 0) slices2.Add(new PieSlice { Value = numbers, Label = $"{numbers} Nombres", FillColor = ScottPlot.Color.FromHex("#8B5CF6") });
        if (dates > 0) slices2.Add(new PieSlice { Value = dates, Label = $"{dates} Dates", FillColor = ScottPlot.Color.FromHex("#14B8A6") });

        DrawPieChartOrEmptyState(page, slices2, startX + 260m, currentY, font);

        var slices3 = new List<PieSlice>();
        string[] colors = { "#3B82F6", "#F59E0B", "#10B981", "#8B5CF6", "#EF4444", "#14B8A6" };
        int cIdx = 0;

        if (languageFileCounts != null)
        {
            foreach (var kvp in languageFileCounts.Where(x => x.Value > 0))
            {
                slices3.Add(new PieSlice { Value = kvp.Value, Label = $"{kvp.Key} ({kvp.Value})", FillColor = ScottPlot.Color.FromHex(colors[cIdx % colors.Length]) });
                cIdx++;
            }
        }

        DrawPieChartOrEmptyState(page, slices3, startX + 520m, currentY, font);
    }

    private void DrawPieChartOrEmptyState(PdfPageBuilder page, List<PieSlice> slices, decimal startX, decimal currentY, PdfDocumentBuilder.AddedFont font)
    {
        if (slices.Count > 0)
        {
            var plt = new Plot();
            plt.HideGrid();
            plt.HideAxesAndGrid();

            var pie = plt.Add.Pie(slices);
            pie.ExplodeFraction = 0.05;

            byte[] imgBytes = plt.GetImageBytes(240, 200, ImageFormat.Png);

            page.AddPng(imgBytes, new PdfRectangle((short)startX, (short)(currentY - 200m), (short)(startX + 240m), (short)currentY));
        }
        else
        {
            page.SetTextAndFillColor(150, 150, 150);
            page.AddText("(Aucune donnée)", 10m, new PdfPoint((double)(startX + 60m), (double)(currentY - 100m)), font);
        }
    }
}