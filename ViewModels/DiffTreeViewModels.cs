using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex.DiffBuilder.Model;
using PDFComparison.Models;

namespace PDFComparison.ViewModels;

public sealed class ImpactedPdfItemViewModel
{
    public DocumentPair Pair { get; init; } = null!;
    public string DisplayFileName { get; init; } = string.Empty;
    public int FirstPageNumber { get; init; } = 1;
    public string PageLabel => $"Page {FirstPageNumber}";
    public bool HasReport => Pair.HasReport;
    public bool HasSourceFile => Pair.HasSourceFile;
    public bool HasTargetFile => Pair.HasTargetFile;
}

public partial class GroupedDiffRowViewModel : ObservableObject
{
    public DiffSummaryBlock Block { get; init; } = new();
    public string Channel { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string PagesCompactLabel { get; init; } = "Page 1";
    public List<ImpactedPdfItemViewModel> ImpactedPdfs { get; init; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PdfDropdownBadgeText))]
    [NotifyPropertyChangedFor(nameof(ExpandChevron))]
    private bool _isDetailsExpanded;

    public string ExpandChevron => IsDetailsExpanded ? "▾" : "▸";

    public ImpactedPdfItemViewModel? PrimaryPdf => ImpactedPdfs.Count > 0 ? ImpactedPdfs[0] : null;
    public bool HasPrimaryReport => PrimaryPdf?.HasReport == true;
    public bool IsMultiPdfGrouped => ImpactedPdfs.Count > 1;

    public bool IsOldEmpty => string.IsNullOrWhiteSpace(Block.OldText);
    public bool IsNewEmpty => string.IsNullOrWhiteSpace(Block.NewText);

    public string DisplayOldText => IsOldEmpty
        ? "(Absent dans Source)"
        : Block.OldText.Replace("\r\n", " ").Replace("\n", " ").Trim();

    public string DisplayNewText => IsNewEmpty
        ? "(Supprimé dans Cible)"
        : Block.NewText.Replace("\r\n", " ").Replace("\n", " ").Trim();

    public string PdfDropdownBadgeText
    {
        get
        {
            string arrow = IsDetailsExpanded ? "▴" : "▾";
            if (ImpactedPdfs.Count > 1)
                return $"{ImpactedPdfs.Count} PDF regroupés {arrow}";
            return $"1 PDF {arrow}";
        }
    }

    [RelayCommand]
    private void ToggleDetails()
    {
        IsDetailsExpanded = !IsDetailsExpanded;
    }
}

public partial class ProductDiffGroupViewModel : ObservableObject
{
    public string Channel { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string ProductGroupKey { get; init; } = string.Empty;
    public List<DocumentPair> ImpactedPairs { get; init; } = new();
    public ObservableCollection<GroupedDiffRowViewModel> Differences { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    public int UniqueDiffsCount => Differences.Count;
    public int ImpactedPdfsCount => ImpactedPairs.Count;

    public string DiffsCountBadgeText => $"{UniqueDiffsCount} différence{(UniqueDiffsCount > 1 ? "s" : "")} regroupée{(UniqueDiffsCount > 1 ? "s" : "")}";
    public string PdfsCountBadgeText => $"{ImpactedPdfsCount} PDF";
}

public partial class ChannelDiffGroupViewModel : ObservableObject
{
    public string ChannelKey { get; init; } = string.Empty;
    public string ChannelDisplayName { get; init; } = string.Empty;
    public string ChannelKind { get; init; } = "Bank"; // "Bank" ou "Broker" uniquement
    public ObservableCollection<ProductDiffGroupViewModel> Products { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    public int ProductsCount => Products.Count;
    public int UniqueDiffsCount => Products.Sum(p => p.UniqueDiffsCount);
    public int ImpactedPdfsCount => Products.Sum(p => p.ImpactedPdfsCount);

    public string HeaderSummaryText =>
        $"{ProductsCount} produit{(ProductsCount > 1 ? "s" : "")}  •  " +
        $"{UniqueDiffsCount} différence{(UniqueDiffsCount > 1 ? "s" : "")} regroupée{(UniqueDiffsCount > 1 ? "s" : "")}  •  " +
        $"{ImpactedPdfsCount} PDF concerné{(ImpactedPdfsCount > 1 ? "s" : "")}";
}

public partial class DocumentListViewModel : ObservableObject
{
    private readonly List<DocumentPair> _allPairs;

    public ListCollectionView FilteredDocumentsView { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterErrors))]
    [NotifyPropertyChangedFor(nameof(IsFilterCompatible))]
    [NotifyPropertyChangedFor(nameof(IsFilterMissing))]
    [NotifyPropertyChangedFor(nameof(EmptyStateMessage))]
    private string _activeFilter = "ALL"; // "ALL", "ERRORS", "COMPATIBLE", "MISSING"

    public bool IsFilterAll => ActiveFilter == "ALL";
    public bool IsFilterErrors => ActiveFilter == "ERRORS";
    public bool IsFilterCompatible => ActiveFilter == "COMPATIBLE";
    public bool IsFilterMissing => ActiveFilter == "MISSING";

    public int TotalCount { get; }
    public int ErrorsOrDiffsCount { get; }
    public int CompatibleCount { get; }
    public int MissingCount { get; }

    public bool IsCurrentListEmpty => FilteredDocumentsView.IsEmpty;

    public string EmptyStateMessage => ActiveFilter switch
    {
        "ERRORS" => "Aucun PDF avec différence ou erreur.",
        "COMPATIBLE" => "Aucun PDF compatible dans cette session.",
        "MISSING" => "Aucun PDF manquant dans Cible.",
        _ => "Aucun document PDF analysé pour le moment."
    };

    public DocumentListViewModel(IEnumerable<DocumentPair> pairs, string initialFilter = "ALL")
    {
        _allPairs = (pairs ?? Enumerable.Empty<DocumentPair>())
            .OrderBy(GetStatusSortOrder)
            .ThenBy(p => string.Equals(p.EffectiveChannel, "Bank", StringComparison.OrdinalIgnoreCase) ? 1 :
                         string.Equals(p.EffectiveChannel, "Broker", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
            .ThenBy(p => p.EffectiveProductCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.FileNameOnly, StringComparer.OrdinalIgnoreCase)
            .ToList();

        TotalCount = _allPairs.Count;
        ErrorsOrDiffsCount = _allPairs.Count(p => p.Status is CompareStatus.Different or CompareStatus.Error);
        CompatibleCount = _allPairs.Count(p => p.Status == CompareStatus.Identical);
        MissingCount = _allPairs.Count(p => p.Status == CompareStatus.MissingInTarget);

        _activeFilter = string.IsNullOrWhiteSpace(initialFilter) ? "ALL" : initialFilter.Trim().ToUpperInvariant();

        FilteredDocumentsView = new ListCollectionView(_allPairs)
        {
            Filter = FilterPair
        };
    }

    private static int GetStatusSortOrder(DocumentPair p) => p.Status switch
    {
        CompareStatus.Different => 1,
        CompareStatus.Error => 2,
        CompareStatus.MissingInTarget => 3,
        CompareStatus.Identical => 4,
        _ => 5
    };

    private bool FilterPair(object obj)
    {
        if (obj is not DocumentPair p) return false;

        return ActiveFilter switch
        {
            "ERRORS" => p.Status is CompareStatus.Different or CompareStatus.Error,
            "COMPATIBLE" => p.Status == CompareStatus.Identical,
            "MISSING" => p.Status == CompareStatus.MissingInTarget,
            _ => true
        };
    }

    [RelayCommand]
    private void SetFilter(string? filter)
    {
        ActiveFilter = string.IsNullOrWhiteSpace(filter) ? "ALL" : filter.Trim().ToUpperInvariant();
        FilteredDocumentsView.Refresh();
        OnPropertyChanged(nameof(IsCurrentListEmpty));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    [RelayCommand]
    private void OpenSideBySide(DocumentPair? pair)
    {
        if (pair == null || string.IsNullOrEmpty(pair.ReportPath)) return;

        string dir = Path.GetDirectoryName(pair.ReportPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(pair.ReportPath);
        string sideBySideHtmlPath = Path.Combine(dir, $"{stem}_SideBySide.html");
        string pathToOpen = File.Exists(sideBySideHtmlPath) ? sideBySideHtmlPath : pair.ReportPath;

        if (File.Exists(pathToOpen))
        {
            try
            {
                Process.Start(new ProcessStartInfo(pathToOpen) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir le rapport : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void OpenSourcePdf(DocumentPair? pair)
    {
        if (pair != null && !string.IsNullOrWhiteSpace(pair.SourcePath) && File.Exists(pair.SourcePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(pair.SourcePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir le PDF Source : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void OpenTargetPdf(DocumentPair? pair)
    {
        if (pair != null && !string.IsNullOrWhiteSpace(pair.TargetPath) && File.Exists(pair.TargetPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(pair.TargetPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir le PDF Cible : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
