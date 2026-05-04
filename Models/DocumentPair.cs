using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Text.Json.Serialization;

namespace PDFComparison.Models;

public partial class DocumentPair : ObservableObject
{
    [ObservableProperty]
    private string _matchKey = string.Empty;

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private string? _targetPath;

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
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private string _reportPath = string.Empty;

    [ObservableProperty]
    private DateTime? _completedTime;

    [JsonIgnore]
    public bool HasReport => !string.IsNullOrEmpty(ReportPath);

    public DocumentPair()
    {
    }

    public DocumentPair(string matchKey, string sourcePath, string? targetPath)
    {
        MatchKey = matchKey;
        SourcePath = sourcePath;
        TargetPath = targetPath;

        if (string.IsNullOrEmpty(TargetPath))
        {
            Status = CompareStatus.MissingInTarget;
            ErrorMessage = "Missing target file";
            DiffCount = -1;
        }
    }
}