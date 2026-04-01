using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Text.Json.Serialization;

namespace PDFComparison.Models;

public partial class DocumentPair : ObservableObject
{
    // Added "set" to allow restoration from the save file (JSON deserialization)
    public string MatchKey { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string? TargetPath { get; set; }

    [ObservableProperty]
    private CompareStatus _status = CompareStatus.Pending;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // Property to store the number of errors/differences
    // Allows sorting documents to display those with the most errors first
    [ObservableProperty]
    private int _diffCount;

    // NEW: Path of the generated PDF report (side-by-side)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private string _reportPath = string.Empty;

    // NEW: End date and time of processing (Completed Time)
    [ObservableProperty]
    private DateTime? _completedTime;

    // NEW: Automatic boolean to enable/disable the "Open PDF" button
    // [JsonIgnore] prevents saving this property in the file because it is calculated dynamically
    [JsonIgnore]
    public bool HasReport => !string.IsNullOrEmpty(ReportPath);

    // NEW: Empty constructor required by the JSON deserializer
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
            DiffCount = -1; // -1 to ensure they are placed at the end during a descending sort
        }
    }
}