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
    private CompareStatus _status = CompareStatus.Pending;

    [ObservableProperty]
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
    public bool HasReport => !string.IsNullOrEmpty(ReportPath);

    [JsonIgnore]
    public bool HasProductTag => !string.IsNullOrWhiteSpace(ProductName);

    [JsonIgnore]
    public string DisplayProductBadge => HasProductTag
        ? (string.IsNullOrWhiteSpace(Channel) ? ProductName : $"{Channel} • {ProductName}")
        : "Standard";

    [JsonIgnore]
    public string FileNameOnly => string.IsNullOrWhiteSpace(SourcePath) ? MatchKey : Path.GetFileName(SourcePath);

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
