using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using DiffPlex.DiffBuilder.Model;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace PDFComparison.Models;

public enum MarkupStyle
{
    Strikethrough,
    Underline,
    Box,
    Highlight
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiffSeverity
{
    None = 0,
    Minor = 1,
    Major = 2,
    Critical = 3
}

public class DiffSummaryBlock
{
    public int PageNumber { get; set; } = 1;
    public DiffSeverity Severity { get; set; } = DiffSeverity.Major;
    public string Category { get; set; } = "Texte";
    public string FinancialDelta { get; set; } = string.Empty;
    public string ContextBefore { get; set; } = string.Empty;
    public string OldText { get; set; } = string.Empty;
    public string NewText { get; set; } = string.Empty;
    public string ContextAfter { get; set; } = string.Empty;
    public ChangeType Type { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public string PagesLabel { get; set; } = string.Empty;
    public List<string> ImpactedDocuments { get; set; } = new();

    [JsonIgnore]
    public byte[]? SourceImage { get; set; }

    [JsonIgnore]
    public byte[]? TargetImage { get; set; }

    [JsonIgnore]
    public bool HasFinancialDelta => !string.IsNullOrWhiteSpace(FinancialDelta);

    [JsonIgnore]
    public bool IsMultiPdfGrouped => OccurrenceCount > 1;

    [JsonIgnore]
    public string DisplayPageLabel => !string.IsNullOrWhiteSpace(PagesLabel) ? PagesLabel : $"Page {PageNumber}";

    [JsonIgnore]
    public string OccurrenceBadgeText => OccurrenceCount > 1 ? $"{OccurrenceCount} PDF regroupés" : string.Empty;

    [JsonIgnore]
    public string ImpactedDocumentsTooltip => ImpactedDocuments.Count > 0
        ? $"Présent dans {ImpactedDocuments.Count} PDF :\n• " + string.Join("\n• ", ImpactedDocuments)
        : string.Empty;

    [JsonIgnore]
    public string ChangeTypeLabel => Type switch
    {
        ChangeType.Inserted => "AJOUT",
        ChangeType.Deleted => "SUPPRESSION",
        ChangeType.Modified => "MODIFICATION",
        _ => "CHANGEMENT"
    };

    [JsonIgnore]
    public string SeverityLabel => Severity switch
    {
        DiffSeverity.Critical => "CRITIQUE",
        DiffSeverity.Major => "MAJEUR",
        DiffSeverity.Minor => "MINEUR",
        _ => "INFO"
    };
}

public class DocumentDiffSummary
{
    public string DocumentName { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string Channel { get; init; } = string.Empty;

    public string ProductName { get; init; } = string.Empty;

    public string RawFileName { get; init; } = string.Empty;

    public string ReportFileName { get; set; } = string.Empty;

    [JsonIgnore]
    public string ProductGroupKey => !string.IsNullOrWhiteSpace(ProductName)
        ? (string.IsNullOrWhiteSpace(Channel) ? ProductName : $"{Channel} • {ProductName}")
        : "Lot Principal";

    public double OverallSsimScore { get; set; } = 1.0;

    public DiffSeverity MaxSeverity { get; set; } = DiffSeverity.None;

    public int CriticalAlertsCount { get; set; }

    public int SourcePageCount { get; set; }

    public int TargetPageCount { get; set; }

    public List<DiffSummaryBlock> Blocks { get; } = new();
}

public class PdfWordInfo
{
    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<Letter> Letters { get; init; } = Array.Empty<Letter>();
    public int PageNumber { get; init; }
}

public readonly record struct LetterLoc(
    PdfRectangle BoundingBox,
    int PageNumber,
    decimal BaselineY,
    decimal FontSize
);

public class VisualHighlights
{
    public List<LetterLoc> SourceRed { get; } = new();
    public List<LetterLoc> TargetRed { get; } = new();
}

public record VisualDiffRegion(
    int PageNumber,
    PdfRectangle PdfBounds,
    double LocalSsimScore,
    double DiffPixelPercentage,
    byte[]? SourceCropPng,
    byte[]? TargetCropPng,
    string Description
)
{
    [JsonIgnore] public double PdfX => PdfBounds.BottomLeft.X;
    [JsonIgnore] public double PdfY => PdfBounds.BottomLeft.Y;
    [JsonIgnore] public double PdfWidth => PdfBounds.Width;
    [JsonIgnore] public double PdfHeight => PdfBounds.Height;
    [JsonIgnore] public double LocalSsim => LocalSsimScore;
}

public class DiffAnalysisResult
{
    public int DifferencesCount { get; set; }

    public double OverallSsimScore { get; set; } = 1.0;

    public DocumentDiffSummary Summary { get; set; } = new();

    public VisualHighlights Highlights { get; set; } = new();

    public List<VisualDiffRegion> VisualRegions { get; set; } = new();
}
