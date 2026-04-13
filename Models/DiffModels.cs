using System;
using System.Collections.Generic;
using DiffPlex.DiffBuilder.Model;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace PDFComparison.Models;

/// <summary>
/// Définit le style visuel appliqué à un élément dans le rapport PDF.
/// </summary>
public enum MarkupStyle
{
    Strikethrough, // Pour le texte supprimé (barré)
    Underline,     // Pour le texte ajouté (souligné)
    Box,           // Pour le texte modifié (encadré - conservé pour rétrocompatibilité si besoin)
    Highlight      // NOUVEAU : Pour le texte avec le style "Éditeur de code"
}

/// <summary>
/// Représente un bloc de texte comparé pour le résumé global.
/// Les propriétés utilisent 'set' au lieu de 'init' pour permettre la fusion dynamique
/// de lignes contiguës en un seul grand bloc de différence.
/// </summary>
public class DiffSummaryBlock
{
    public string ContextBefore { get; set; } = string.Empty;
    public string OldText { get; set; } = string.Empty;
    public string NewText { get; set; } = string.Empty;
    public string ContextAfter { get; set; } = string.Empty;
    public ChangeType Type { get; set; }

    // NOUVEAU : Stockage des captures d'écran pour le rapport visuel (via PdfiumViewer)
    public byte[]? SourceImage { get; set; }
    public byte[]? TargetImage { get; set; }
}

/// <summary>
/// Contient l'ensemble des blocs de différences pour un document donné.
/// </summary>
public class DocumentDiffSummary
{
    public string DocumentName { get; init; } = string.Empty;

    // NOUVELLE PROPRIÉTÉ : Stocke la langue extraite du fichier (ex: "NL", "FR", "DE")
    public string Language { get; init; } = string.Empty;

    // NOUVEAU : Stocke le nom exact du fichier PDF de rapport détaillé (pour le lien)
    public string ReportFileName { get; set; } = string.Empty;

    // Le setter a été retiré. La liste est instanciée une fois pour toutes.
    public List<DiffSummaryBlock> Blocks { get; } = new();
}

/// <summary>
/// Stocke un mot extrait du PDF ainsi que la liste exacte de ses lettres (glyphes).
/// </summary>
public class PdfWordInfo
{
    public string Text { get; init; } = string.Empty;

    // Array.Empty évite d'allouer une List vide en mémoire si le mot n'a pas de lettres.
    public IReadOnlyList<Letter> Letters { get; init; } = Array.Empty<Letter>();
    public int PageNumber { get; init; }
}

/// <summary>
/// Mémorise la géométrie typographique parfaite d'une lettre à surligner.
/// OPTIMISATION MAJEURE (C# 10+) : 'readonly record struct'
/// N'alloue aucun objet sur le tas (Heap), ce qui allège considérablement le Garbage Collector.
/// Le constructeur et les propriétés sont générés automatiquement.
/// </summary>
public readonly record struct LetterLoc(
    PdfRectangle BoundingBox,
    int PageNumber,
    decimal BaselineY,
    decimal FontSize
);

// ==============================================================
// DTOs POUR L'ARCHITECTURE DE DÉCOUPLAGE
// ==============================================================

/// <summary>
/// Transporte les coordonnées de dessin entre le PdfDiffAnalyzer et le PdfReportGenerator.
/// </summary>
public class VisualHighlights
{
    // Suppression des setters pour protéger l'intégrité des listes.
    // Les listes Yellow (Modifications) ont été retirées. On ne garde que les Ajouts et Suppressions pures.
    public List<LetterLoc> SourceRed { get; } = new();
    public List<LetterLoc> TargetRed { get; } = new();
}

/// <summary>
/// Résultat global renvoyé par le service de Diff (PdfDiffAnalyzer).
/// </summary>
public class DiffAnalysisResult
{
    // Le Count peut être incrémenté, donc on garde get; set;
    public int DifferencesCount { get; set; }

    public DocumentDiffSummary Summary { get; init; } = new();

    // CORRECTION ICI : Remplacement de "init" par "set" pour permettre
    // l'assignation de l'objet Highlights après la création de DiffAnalysisResult
    public VisualHighlights Highlights { get; set; } = new();
}