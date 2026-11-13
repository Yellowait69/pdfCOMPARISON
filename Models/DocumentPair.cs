using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;

namespace PDFComparison.Models;

public partial class DocumentPair : ObservableObject
{
    [ObservableProperty]
    private string _matchKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileNameOnly))]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private string? _targetPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProductTag))]
    [NotifyPropertyChangedFor(nameof(DisplayProductBadge))]
    private string _channel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProductTag))]
    [NotifyPropertyChangedFor(nameof(DisplayProductBadge))]
    private string _productName = string.Empty;

    [ObservableProperty]
    private string _releaseName = string.Empty;

    [ObservableProperty]
    private string _productOutputDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatusBadge))]
    [NotifyPropertyChangedFor(nameof(DisplayDocumentNote))]
    private CompareStatus _status = CompareStatus.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayDocumentNote))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private int _diffCount;

    [ObservableProperty]
    private int _insertionsCount;

    [ObservableProperty]
    private int _deletionsCount;

    [ObservableProperty]
    private int _criticalCount;

    [ObservableProperty]
    private DiffSeverity _maxSeverity = DiffSeverity.None;

    [ObservableProperty]
    private double _ssimScore = 100.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private string _reportPath = string.Empty;

    [ObservableProperty]
    private DateTime? _completedTime;

    public ObservableCollection<DiffSummaryBlock> DiffBlocks { get; set; } = new();

    [JsonIgnore]
    public ObservableCollection<DocumentPair> GroupedSourcePairs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRepresentativePair))]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [property: JsonIgnore]
    private DocumentPair? _selectedGroupedMember;

    [JsonIgnore]
    public bool IsGroupedRow => GroupedSourcePairs.Count > 1;

    [JsonIgnore]
    public int GroupedPdfCount => GroupedSourcePairs.Count > 0 ? GroupedSourcePairs.Count : 1;

    [JsonIgnore]
    public DocumentPair ActiveRepresentativePair => SelectedGroupedMember ?? (GroupedSourcePairs.Count > 0 ? GroupedSourcePairs[0] : this);

    [JsonIgnore]
    public string DisplayStatusBadge => Status == CompareStatus.Different && GroupedPdfCount > 1
        ? $"Different ({GroupedPdfCount} PDF)"
        : Status.ToString();

    [JsonIgnore]
    public string ProductGroupKey => HasProductTag ? DisplayProductBadge : "Lot Principal";

    [JsonIgnore]
    public bool HasReport => !string.IsNullOrEmpty(ActiveRepresentativePair.ReportPath);

    [JsonIgnore]
    public bool HasProductTag => !string.IsNullOrWhiteSpace(ProductName);

    [JsonIgnore]
    public string DisplayProductBadge => HasProductTag
        ? (string.IsNullOrWhiteSpace(Channel) ? ProductName : $"{Channel} • {ProductName}")
        : "Lot Principal";

    [JsonIgnore]
    public string FileNameOnly => string.IsNullOrWhiteSpace(SourcePath) ? MatchKey : Path.GetFileName(SourcePath);

    [JsonIgnore]
    public string DisplayDocumentNote
    {
        get
        {
            if (Status == CompareStatus.Different && DiffBlocks.Count > 0)
            {
                var b = DiffBlocks[0];
                string oldS = string.IsNullOrWhiteSpace(b.OldText) ? "(vide)" : b.OldText.Replace("\r\n", " ").Replace("\n", " ").Trim();
                if (oldS.Length > 26) oldS = oldS.Substring(0, 26) + "…";
                string newS = string.IsNullOrWhiteSpace(b.NewText) ? "(vide)" : b.NewText.Replace("\r\n", " ").Replace("\n", " ").Trim();
                if (newS.Length > 26) newS = newS.Substring(0, 26) + "…";
                string moreDiffs = DiffBlocks.Count > 1 ? $" (+{DiffBlocks.Count - 1} diff)" : string.Empty;

                if (IsGroupedRow)
                {
                    string filesPreview = string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(GroupedSourcePairs, 2), p => p.FileNameOnly));
                    string extra = GroupedSourcePairs.Count > 2 ? $" +{GroupedSourcePairs.Count - 2} autres" : string.Empty;
                    return $"P.{b.PageNumber} : {oldS} ➔ {newS}{moreDiffs}  •  ({filesPreview}{extra})";
                }

                return $"P.{b.PageNumber} : {oldS} ➔ {newS}{moreDiffs}  •  {FileNameOnly}";
            }

            return SourcePath;
        }
    }

    public DocumentPair()
    {
    }

    public DocumentPair(string matchKey, string sourcePath, string? targetPath, string channel = "", string productName = "", string releaseName = "", string productOutputDirectory = "")
    {
        MatchKey = matchKey;
        SourcePath = sourcePath;
        TargetPath = targetPath;
        Channel = channel;
        ProductName = productName;
        ReleaseName = releaseName;
        ProductOutputDirectory = productOutputDirectory;

        if (string.IsNullOrEmpty(TargetPath))
        {
            Status = CompareStatus.MissingInTarget;
            ErrorMessage = "Missing target file";
            DiffCount = -1;
            SsimScore = 0.0;
        }
    }
}
