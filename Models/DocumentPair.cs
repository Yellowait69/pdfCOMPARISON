using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfComparer.Models;

public partial class DocumentPair : ObservableObject
{
    public string MatchKey { get; }
    public string SourcePath { get; }
    public string? TargetPath { get; }

    [ObservableProperty]
    private CompareStatus _status = CompareStatus.Pending;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public DocumentPair(string matchKey, string sourcePath, string? targetPath)
    {
        MatchKey = matchKey;
        SourcePath = sourcePath;
        TargetPath = targetPath;

        if (string.IsNullOrEmpty(TargetPath))
        {
            Status = CompareStatus.MissingInTarget;
        }
    }
}