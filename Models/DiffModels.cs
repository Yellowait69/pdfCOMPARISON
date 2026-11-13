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

    // --- Contexte Produit & Coordonnées Géométriques PDF (Points 72 DPI, Origine Bottom-Left) ---
    public string ProductCode { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public bool HasPdfBounds { get; set; }
    public double PdfLeft { get; set; }
    public double PdfBottom { get; set; }
    public double PdfRight { get; set; }
    public double PdfTop { get; set; }
    public double FieldZoneLeft { get; set; }
    public double FieldZoneBottom { get; set; }
    public double FieldZoneRight { get; set; }
    public double FieldZoneTop { get; set; }
    public double PdfPageWidth { get; set; } = 595.0;
    public double PdfPageHeight { get; set; } = 842.0;

    // --- Regroupement Géométrique des Données Scramblées (même endroit pour un même produit) ---
    public bool IsScrambledZoneGroup { get; set; }
    public int VariantCount { get; set; } = 1;
    public List<string> ScrambledVariants { get; set; } = new();

    [JsonIgnore]
    public byte[]? SourceImage { get; set; }

    [JsonIgnore]
    public byte[]? TargetImage { get; set; }

    [JsonIgnore]
    public bool HasFinancialDelta => !string.IsNullOrWhiteSpace(FinancialDelta);

    [JsonIgnore]
    public bool IsMultiPdfGrouped => OccurrenceCount > 1;

    [JsonIgnore]
    public bool HasZoneRuleSupport => HasPdfBounds && (FieldZoneRight > FieldZoneLeft || PdfRight > PdfLeft);

    [JsonIgnore]
    public bool HasScrambledBadge => IsScrambledZoneGroup && VariantCount > 1;

    [JsonIgnore]
    public string ScrambledBadgeText => HasScrambledBadge
        ? $"Même zone ({VariantCount} variantes)"
        : string.Empty;

    [JsonIgnore]
    public string ZoneRuleButtonText => !string.IsNullOrWhiteSpace(ProductCode)
        ? $"Ignorer Zone ({(ProductCode.Length > 14 ? ProductCode[..14] + "…" : ProductCode)})"
        : "Ignorer Zone";

    [JsonIgnore]
    public string ZoneRuleButtonTooltip => !string.IsNullOrWhiteSpace(ProductCode)
        ? $"Créer une zone d'exclusion géométrique sur la Page {PageNumber} uniquement pour le produit « {ProductCode} » (idéal pour adresses / noms scramblés au même endroit)"
        : $"Créer une zone d'exclusion géométrique à cet emplacement sur la Page {PageNumber}";

    [JsonIgnore]
    public string DisplayPageLabel => !string.IsNullOrWhiteSpace(PagesLabel) ? PagesLabel : $"Page {PageNumber}";

    [JsonIgnore]
    public string OccurrenceBadgeText => OccurrenceCount > 1 ? $"{OccurrenceCount} PDF regroupés" : string.Empty;

    [JsonIgnore]
    public string ImpactedDocumentsTooltip
    {
        get
        {
            var lines = new List<string>();
            if (HasScrambledBadge && ScrambledVariants.Count > 0)
            {
                lines.Add($"Données variables / scramblées détectées au même endroit ({VariantCount} variantes) :");
                foreach (var v in ScrambledVariants)
                {
                    lines.Add($"• {v}");
                }
            }

            if (ImpactedDocuments.Count > 0)
            {
                if (lines.Count > 0) lines.Add(string.Empty);
                lines.Add($"Présent dans {ImpactedDocuments.Count} PDF :");
                foreach (var d in ImpactedDocuments)
                {
                    lines.Add($"• {d}");
                }
            }

            return string.Join("\n", lines);
        }
    }

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

    public string SourcePath { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;

    public string ReportFileName { get; set; } = string.Empty;

    [JsonIgnore]
    public string ProductGroupKey
    {
        get
        {
            string src = !string.IsNullOrWhiteSpace(SourcePath) ? SourcePath : RawFileName;
            string trg = !string.IsNullOrWhiteSpace(TargetPath) ? TargetPath : DocumentName;
            string ch = ProductCodeHelper.ResolveEffectiveChannel(Channel, src, trg, ProductName);
            string eff = ProductCodeHelper.ResolveEffectiveProductCode(ProductName, ch, src, trg);
            return !string.IsNullOrWhiteSpace(eff) ? $"{ch} • {eff}" : ch;
        }
    }

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
    public bool IsMasked { get; init; }
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
