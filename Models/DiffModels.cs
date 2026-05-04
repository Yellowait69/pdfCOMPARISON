using System;
using System.Collections.Generic;
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

public class DiffSummaryBlock
{
    public string ContextBefore { get; set; } = string.Empty;
    public string OldText { get; set; } = string.Empty;
    public string NewText { get; set; } = string.Empty;
    public string ContextAfter { get; set; } = string.Empty;
    public ChangeType Type { get; set; }
    public byte[]? SourceImage { get; set; }
    public byte[]? TargetImage { get; set; }
}

public class DocumentDiffSummary
{
    public string DocumentName { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string ReportFileName { get; set; } = string.Empty;

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

public class DiffAnalysisResult
{
    public int DifferencesCount { get; set; }

    public DocumentDiffSummary Summary { get; init; } = new();

    public VisualHighlights Highlights { get; set; } = new();
}