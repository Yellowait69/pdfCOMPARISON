using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PDFComparison.Models;
using PDFComparison.Services;
using UglyToad.PdfPig;

namespace PDFComparison.ViewModels;

public partial class PreviewWordItem : ObservableObject
{
    public string Text { get; init; } = string.Empty;
    public double PdfLeft { get; init; }
    public double PdfBottom { get; init; }
    public double PdfRight { get; init; }
    public double PdfTop { get; init; }

    public double CanvasLeft { get; init; }
    public double CanvasTop { get; init; }
    public double CanvasWidth { get; init; }
    public double CanvasHeight { get; init; }

    [ObservableProperty]
    private bool _isExcluded;
}

public partial class PreviewZoneItem : ObservableObject
{
    public ExclusionZoneRule Rule { get; init; } = null!;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private double _canvasLeft;
    [ObservableProperty] private double _canvasTop;
    [ObservableProperty] private double _canvasWidth;
    [ObservableProperty] private double _canvasHeight;
}

public partial class ProfileSettingsViewModel : ObservableObject
{
    private readonly IConfigurationService _configService;
    private readonly IPdfIntelligentMaskingService _maskingService;

    public const double PreviewCanvasWidth = 420.0;
    public const double PreviewCanvasHeight = 594.0; // Ratio A4 (595 x 842)

    public ComparisonProfile Profile => _configService.ActiveProfile;

    public Array PageScopes => Enum.GetValues(typeof(PageScope));
    public Array CoordinateUnits => Enum.GetValues(typeof(CoordinateUnit));
    public Array ZoneOrigins => Enum.GetValues(typeof(ZoneOrigin));

    [ObservableProperty] private ExclusionZoneRule? _selectedZone;
    [ObservableProperty] private RegexMaskingRule? _selectedRegexRule;

    // --- État de l'aperçu PDF interactif ---
    [ObservableProperty] private string _samplePdfName = "Aucun PDF témoin chargé (Aperçu gabarit A4 595x842 pt)";
    [ObservableProperty] private string? _samplePdfPath;
    [ObservableProperty] private int _currentPreviewPage = 1;
    [ObservableProperty] private int _totalPreviewPages = 1;
    [ObservableProperty] private double _currentPageWidthPt = 595.0;
    [ObservableProperty] private double _currentPageHeightPt = 842.0;
    [ObservableProperty] private int _excludedWordsCount;
    [ObservableProperty] private int _keptWordsCount;

    public ObservableCollection<PreviewWordItem> PreviewWords { get; } = new();
    public ObservableCollection<PreviewZoneItem> PreviewZones { get; } = new();

    // --- Testeur de Regex en direct ---
    [ObservableProperty]
    private string _sampleTestInput =
        "Souscripteur : DUPONT JEAN,\nDate d'émission : 14/05/2026 - Référence : FACT-20260089\nDate de rappel : 14/05/2026\nAncre signature : #SIGN_CLIENT_1#";

    [ObservableProperty]
    private string _sampleTestOutput = string.Empty;

    [ObservableProperty]
    private string _statusBannerText = "Modifiez les zones (ou tracez un rectangle à la souris sur l'aperçu) puis cliquez sur Enregistrer.";

    public ProfileSettingsViewModel() : this(new ConfigurationService())
    {
    }

    public ProfileSettingsViewModel(IConfigurationService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _maskingService = new PdfIntelligentMaskingService(_configService);

        AttachZoneListeners();
        RefreshVisualPreview();
        RunRegexPreview();
    }

    private void AttachZoneListeners()
    {
        foreach (var zone in Profile.ExclusionZones)
        {
            zone.PropertyChanged -= OnZonePropertyChanged;
            zone.PropertyChanged += OnZonePropertyChanged;
        }
    }

    private void OnZonePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshVisualPreview();
    }

    partial void OnSampleTestInputChanged(string value)
    {
        RunRegexPreview();
    }

    [RelayCommand]
    private void RunRegexPreview()
    {
        try
        {
            SampleTestOutput = _maskingService.MaskRepeatingTextElements(SampleTestInput ?? string.Empty);
        }
        catch (Exception ex)
        {
            SampleTestOutput = $"Erreur d'évaluation : {ex.Message}";
        }
    }

    [RelayCommand]
    private void AddZone()
    {
        var newZone = new ExclusionZoneRule
        {
            Name = $"Zone #{Profile.ExclusionZones.Count + 1}",
            IsEnabled = true,
            Scope = PageScope.AllPages,
            Unit = CoordinateUnit.PdfPoints,
            Origin = ZoneOrigin.TopLeft,
            X = 50,
            Y = 150,
            Width = 250,
            Height = 60
        };

        newZone.PropertyChanged += OnZonePropertyChanged;
        Profile.ExclusionZones.Add(newZone);
        SelectedZone = newZone;
        RefreshVisualPreview();
    }

    [RelayCommand]
    private void RemoveZone()
    {
        if (SelectedZone == null) return;
        SelectedZone.PropertyChanged -= OnZonePropertyChanged;
        Profile.ExclusionZones.Remove(SelectedZone);
        SelectedZone = Profile.ExclusionZones.FirstOrDefault();
        RefreshVisualPreview();
    }

    /// <summary>
    /// Crée directement une zone d'exclusion à partir d'un rectangle tracé à la souris sur le Canvas d'aperçu.
    /// </summary>
    public void AddZoneFromCanvasLasso(double canvasX, double canvasY, double canvasW, double canvasH)
    {
        double scaleX = CurrentPageWidthPt / PreviewCanvasWidth;
        double scaleY = CurrentPageHeightPt / PreviewCanvasHeight;

        var drawnZone = new ExclusionZoneRule
        {
            Name = $"Zone dessinée #{Profile.ExclusionZones.Count + 1}",
            IsEnabled = true,
            Scope = PageScope.AllPages,
            Unit = CoordinateUnit.PdfPoints,
            Origin = ZoneOrigin.TopLeft,
            X = Math.Round(canvasX * scaleX, 1),
            Y = Math.Round(canvasY * scaleY, 1),
            Width = Math.Round(canvasW * scaleX, 1),
            Height = Math.Round(canvasH * scaleY, 1)
        };

        drawnZone.PropertyChanged += OnZonePropertyChanged;
        Profile.ExclusionZones.Add(drawnZone);
        SelectedZone = drawnZone;
        RefreshVisualPreview();
        StatusBannerText = $"Nouvelle zone '{drawnZone.Name}' créée ({drawnZone.Width}x{drawnZone.Height} pt).";
    }

    [RelayCommand]
    private void AddRegexRule()
    {
        var rule = new RegexMaskingRule
        {
            Name = $"Règle #{Profile.RegexRules.Count + 1}",
            IsEnabled = true,
            Pattern = @"\b\d{6,10}\b",
            ReplacementMask = "[ID_IGNORE]",
            IgnoreCase = true,
            MinOccurrences = 1
        };

        Profile.RegexRules.Add(rule);
        SelectedRegexRule = rule;
        RunRegexPreview();
    }

    [RelayCommand]
    private void RemoveRegexRule()
    {
        if (SelectedRegexRule == null) return;
        Profile.RegexRules.Remove(SelectedRegexRule);
        SelectedRegexRule = Profile.RegexRules.FirstOrDefault();
        RunRegexPreview();
    }

    [RelayCommand]
    private void LoadSamplePdf()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Sélectionner un fichier PDF témoin pour calibrer les zones",
            Filter = "Fichiers PDF (*.pdf)|*.pdf"
        };

        if (dialog.ShowDialog() == true)
        {
            SamplePdfPath = dialog.FileName;
            SamplePdfName = Path.GetFileName(dialog.FileName);
            CurrentPreviewPage = 1;
            LoadSamplePdfPageWords();
        }
    }

    [RelayCommand]
    private void PreviousPreviewPage()
    {
        if (CurrentPreviewPage > 1)
        {
            CurrentPreviewPage--;
            LoadSamplePdfPageWords();
        }
    }

    [RelayCommand]
    private void NextPreviewPage()
    {
        if (CurrentPreviewPage < TotalPreviewPages)
        {
            CurrentPreviewPage++;
            LoadSamplePdfPageWords();
        }
    }

    private void LoadSamplePdfPageWords()
    {
        if (string.IsNullOrEmpty(SamplePdfPath) || !File.Exists(SamplePdfPath)) return;

        try
        {
            PreviewWords.Clear();
            using var doc = PdfDocument.Open(SamplePdfPath, new ParsingOptions { ClipPaths = false });
            TotalPreviewPages = Math.Max(1, doc.NumberOfPages);
            int pageNum = Math.Clamp(CurrentPreviewPage, 1, TotalPreviewPages);

            var page = doc.GetPage(pageNum);
            CurrentPageWidthPt = page.Width > 0 ? page.Width : 595.0;
            CurrentPageHeightPt = page.Height > 0 ? page.Height : 842.0;

            double scaleX = PreviewCanvasWidth / CurrentPageWidthPt;
            double scaleY = PreviewCanvasHeight / CurrentPageHeightPt;

            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                var box = word.BoundingBox;

                double cLeft = box.BottomLeft.X * scaleX;
                double cTop = (CurrentPageHeightPt - box.TopRight.Y) * scaleY;
                double cWidth = Math.Max(2.0, (box.TopRight.X - box.BottomLeft.X) * scaleX);
                double cHeight = Math.Max(2.0, (box.TopRight.Y - box.BottomLeft.Y) * scaleY);

                PreviewWords.Add(new PreviewWordItem
                {
                    Text = word.Text,
                    PdfLeft = box.BottomLeft.X,
                    PdfBottom = box.BottomLeft.Y,
                    PdfRight = box.TopRight.X,
                    PdfTop = box.TopRight.Y,
                    CanvasLeft = cLeft,
                    CanvasTop = cTop,
                    CanvasWidth = cWidth,
                    CanvasHeight = cHeight
                });
            }

            RefreshVisualPreview();
        }
        catch (Exception ex)
        {
            StatusBannerText = $"Impossible de lire le PDF témoin : {ex.Message}";
        }
    }

    [RelayCommand]
    public void RefreshVisualPreview()
    {
        PreviewZones.Clear();
        double scaleX = PreviewCanvasWidth / CurrentPageWidthPt;
        double scaleY = PreviewCanvasHeight / CurrentPageHeightPt;

        var activeZones = Profile.ExclusionZones
            .Where(z => z.AppliesToPage(CurrentPreviewPage, TotalPreviewPages))
            .ToList();

        foreach (var zone in activeZones)
        {
            var (pdfLeft, pdfBottom, pdfRight, pdfTop) = zone.ToPdfBounds(CurrentPageWidthPt, CurrentPageHeightPt);

            double clampedLeft = Math.Clamp(pdfLeft, 0, CurrentPageWidthPt);
            double clampedRight = Math.Clamp(pdfRight, 0, CurrentPageWidthPt);
            double clampedBottom = Math.Clamp(pdfBottom, 0, CurrentPageHeightPt);
            double clampedTop = Math.Clamp(pdfTop, 0, CurrentPageHeightPt);

            double cLeft = clampedLeft * scaleX;
            double cTop = (CurrentPageHeightPt - clampedTop) * scaleY;
            double cWidth = Math.Max(0, (clampedRight - clampedLeft) * scaleX);
            double cHeight = Math.Max(0, (clampedTop - clampedBottom) * scaleY);

            PreviewZones.Add(new PreviewZoneItem
            {
                Rule = zone,
                Name = zone.Name,
                CanvasLeft = cLeft,
                CanvasTop = cTop,
                CanvasWidth = cWidth,
                CanvasHeight = cHeight
            });
        }

        int excluded = 0;
        int kept = 0;

        foreach (var w in PreviewWords)
        {
            bool isExcl = activeZones.Any(z =>
                z.IntersectsPdfBox(w.PdfLeft, w.PdfBottom, w.PdfRight, w.PdfTop, CurrentPageWidthPt, CurrentPageHeightPt));

            w.IsExcluded = isExcl;
            if (isExcl) excluded++;
            else kept++;
        }

        ExcludedWordsCount = excluded;
        KeptWordsCount = kept;
    }

    [RelayCommand]
    private void SaveProfile()
    {
        _configService.SaveActiveProfile();
        RunRegexPreview();
        RefreshVisualPreview();
        StatusBannerText = $"Profil '{Profile.ProfileName}' enregistré avec succès ({DateTime.Now:HH:mm:ss}).";
    }

    [RelayCommand]
    private void ResetProfile()
    {
        var result = MessageBox.Show(
            "Voulez-vous vraiment réinitialiser toutes les zones d'exclusion, règles Regex et tolérances aux valeurs par défaut ?",
            "Réinitialiser le profil",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _configService.ResetToDefault();
            AttachZoneListeners();
            RefreshVisualPreview();
            RunRegexPreview();
            StatusBannerText = "Profil réinitialisé aux valeurs par défaut.";
        }
    }

    [RelayCommand]
    private void ExportProfile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exporter le profil de comparaison",
            Filter = "Profil JSON (*.json)|*.json",
            FileName = $"{Profile.ProfileName.Replace(' ', '_')}.json"
        };

        if (dialog.ShowDialog() == true)
        {
            _configService.ExportProfile(dialog.FileName);
            StatusBannerText = $"Profil exporté vers : {Path.GetFileName(dialog.FileName)}";
        }
    }

    [RelayCommand]
    private void ImportProfile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importer un profil de comparaison",
            Filter = "Profil JSON (*.json)|*.json"
        };

        if (dialog.ShowDialog() == true)
        {
            if (_configService.ImportProfile(dialog.FileName, out string error))
            {
                AttachZoneListeners();
                RefreshVisualPreview();
                RunRegexPreview();
                StatusBannerText = $"Profil '{Profile.ProfileName}' importé avec succès.";
            }
            else
            {
                MessageBox.Show($"Erreur lors de l'importation du profil :\n{error}", "Erreur d'importation", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
