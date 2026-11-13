using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
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
    [ObservableProperty] private string _displayLabel = string.Empty;
    [ObservableProperty] private bool _isProductSpecific;
    [ObservableProperty] private double _canvasLeft;
    [ObservableProperty] private double _canvasTop;
    [ObservableProperty] private double _canvasWidth;
    [ObservableProperty] private double _canvasHeight;
}

public partial class ProfileSettingsViewModel : ObservableObject
{
    private readonly IConfigurationService _configService;
    private readonly IPdfIntelligentMaskingService _maskingService;
    private readonly List<string> _extraKnownProducts;

    public const double PreviewCanvasWidth = 420.0;
    public const double PreviewCanvasHeight = 594.0; // Ratio A4 (595 x 842)

    public ComparisonProfile Profile => _configService.ActiveProfile;

    public ListCollectionView FilteredZonesView { get; private set; }
    public ListCollectionView FilteredRegexRulesView { get; private set; }

    public ObservableCollection<string> KnownProductCodes { get; } = new();

    public Array PageScopes => Enum.GetValues(typeof(PageScope));
    public Array CoordinateUnits => Enum.GetValues(typeof(CoordinateUnit));
    public Array ZoneOrigins => Enum.GetValues(typeof(ZoneOrigin));

    [ObservableProperty] private ExclusionZoneRule? _selectedZone;
    [ObservableProperty] private RegexMaskingRule? _selectedRegexRule;

    // --- Filtrage & Création Globale vs Par Produit ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterGlobal))]
    [NotifyPropertyChangedFor(nameof(IsFilterProduct))]
    private string _ruleScopeFilter = "ALL";

    public bool IsFilterAll => string.Equals(RuleScopeFilter, "ALL", StringComparison.OrdinalIgnoreCase);
    public bool IsFilterGlobal => string.Equals(RuleScopeFilter, "GLOBAL", StringComparison.OrdinalIgnoreCase);
    public bool IsFilterProduct => string.Equals(RuleScopeFilter, "PRODUCT", StringComparison.OrdinalIgnoreCase);

    public int GlobalZonesCount => Profile.ExclusionZones.Count(z => z.IsGlobalRule);
    public int ProductZonesCount => Profile.ExclusionZones.Count(z => z.IsProductSpecificRule);
    public int GlobalRegexCount => Profile.RegexRules.Count(r => r.IsGlobalRule);
    public int ProductRegexCount => Profile.RegexRules.Count(r => r.IsProductSpecificRule);

    public string HeaderRulesSummaryText =>
        $"{Profile.RegexRules.Count(r => r.IsEnabled)} règle(s) métier / Regex active(s)  •  {Profile.ExclusionZones.Count(z => z.IsEnabled)} zone(s) active(s)  •  Sauvegarde permanente automatique";

    // --- Création rapide 1-clic (sans connaître le Regex ou avec Regex assisté) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuickModeIgnore))]
    [NotifyPropertyChangedFor(nameof(IsQuickModeEquiv))]
    [NotifyPropertyChangedFor(nameof(IsQuickModeRegex))]
    private string _quickRuleMode = "IGNORE";

    public bool IsQuickModeIgnore => string.Equals(QuickRuleMode, "IGNORE", StringComparison.OrdinalIgnoreCase);
    public bool IsQuickModeEquiv => string.Equals(QuickRuleMode, "EQUIV", StringComparison.OrdinalIgnoreCase);
    public bool IsQuickModeRegex => string.Equals(QuickRuleMode, "REGEX", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleGlobal))]
    private bool _newRuleForProduct;

    public bool NewRuleGlobal => !NewRuleForProduct;

    [ObservableProperty]
    private string _quickIgnoreText = string.Empty;

    [ObservableProperty]
    private string _quickEquivLeft = string.Empty;

    [ObservableProperty]
    private string _quickEquivRight = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuickRegexInput))]
    [NotifyPropertyChangedFor(nameof(IsQuickRegexValid))]
    [NotifyPropertyChangedFor(nameof(QuickRegexValidationText))]
    private string _quickRegexPattern = string.Empty;

    [ObservableProperty]
    private string _quickRuleCustomName = string.Empty;

    public bool HasQuickRegexInput => !string.IsNullOrWhiteSpace(QuickRegexPattern);

    public bool IsQuickRegexValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(QuickRegexPattern)) return true;
            try
            {
                _ = new System.Text.RegularExpressions.Regex(QuickRegexPattern);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public string QuickRegexValidationText =>
        string.IsNullOrWhiteSpace(QuickRegexPattern)
            ? string.Empty
            : (IsQuickRegexValid ? "✓ Regex valide" : "⚠ Regex invalide");

    [ObservableProperty]
    private string _targetProductForNewRule = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DrawLassoGlobal))]
    private bool _drawLassoForProduct;

    public bool DrawLassoGlobal => !DrawLassoForProduct;

    [ObservableProperty]
    private string _previewSimulatedProduct = string.Empty;

    [ObservableProperty]
    private string _regexTestProduct = string.Empty;

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
    private string _statusBannerText = "Créez des règles Globales (tous produits) ou Par Produit (ex: SP.EFP, SP.017) puis cliquez sur Enregistrer & Appliquer.";

    public ProfileSettingsViewModel() : this(new ConfigurationService())
    {
    }

    public ProfileSettingsViewModel(
        IConfigurationService configService,
        IEnumerable<string>? knownProducts = null,
        string? initialProductCode = null,
        string? initialSamplePdfPath = null)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _maskingService = new PdfIntelligentMaskingService(_configService);
        _extraKnownProducts = knownProducts?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();

        FilteredZonesView = CreateZonesView();
        FilteredRegexRulesView = CreateRegexView();

        ReloadKnownProducts(initialProductCode, initialSamplePdfPath);

        string resolvedInitialProd = ProductCodeHelper.ResolveEffectiveProductCode(initialProductCode, null, initialSamplePdfPath, null);
        if (!string.IsNullOrWhiteSpace(resolvedInitialProd))
        {
            TargetProductForNewRule = resolvedInitialProd;
            PreviewSimulatedProduct = resolvedInitialProd;
            RegexTestProduct = resolvedInitialProd;
            DrawLassoForProduct = true;
            NewRuleForProduct = false;
        }
        else if (KnownProductCodes.Count > 0)
        {
            TargetProductForNewRule = KnownProductCodes[0];
        }

        AttachRuleListeners();

        if (!string.IsNullOrWhiteSpace(initialSamplePdfPath) &&
            !initialSamplePdfPath.StartsWith(@"\\", StringComparison.Ordinal) &&
            File.Exists(initialSamplePdfPath))
        {
            SamplePdfPath = initialSamplePdfPath;
            SamplePdfName = Path.GetFileName(initialSamplePdfPath);
            CurrentPreviewPage = 1;
            LoadSamplePdfPageWords();
        }
        else
        {
            RefreshVisualPreview();
        }

        RunRegexPreview();
        NotifyRuleCounts();
    }

    private ListCollectionView CreateZonesView()
    {
        var view = new ListCollectionView(Profile.ExclusionZones)
        {
            Filter = obj =>
            {
                if (obj is not ExclusionZoneRule z) return true;
                if (IsFilterGlobal) return z.IsGlobalRule;
                if (IsFilterProduct) return z.IsProductSpecificRule;
                return true;
            }
        };
        return view;
    }

    private ListCollectionView CreateRegexView()
    {
        var view = new ListCollectionView(Profile.RegexRules)
        {
            Filter = obj =>
            {
                if (obj is not RegexMaskingRule r) return true;
                if (IsFilterGlobal) return r.IsGlobalRule;
                if (IsFilterProduct) return r.IsProductSpecificRule;
                return true;
            }
        };
        return view;
    }

    private void ReloadKnownProducts(string? initialProductCode = null, string? samplePath = null)
    {
        KnownProductCodes.Clear();

        foreach (var p in _extraKnownProducts)
            RegisterKnownProduct(p);

        RegisterKnownProduct(initialProductCode);

        if (!string.IsNullOrWhiteSpace(samplePath))
        {
            RegisterKnownProduct(ProductCodeHelper.ResolveEffectiveProductCode(null, null, samplePath, null));
        }

        foreach (var z in Profile.ExclusionZones)
            RegisterKnownProduct(z.ProductFilter);
        foreach (var r in Profile.RegexRules)
            RegisterKnownProduct(r.ProductFilter);

        if (!ReferenceEquals(Profile, _configService.GeneralProfile))
        {
            foreach (var z in _configService.GeneralProfile.ExclusionZones)
                RegisterKnownProduct(z.ProductFilter);
            foreach (var r in _configService.GeneralProfile.RegexRules)
                RegisterKnownProduct(r.ProductFilter);
        }

        // Collecter instantanément les codes produits depuis les dossiers locaux (sans jamais interroger un partage réseau UNC)
        foreach (var scan in _configService.UserSettings.ScanProfiles)
        {
            TryCollectLocalDirectoryProducts(scan.RootPath);
        }
    }

    private void TryCollectLocalDirectoryProducts(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return;
        string clean = rootPath.Trim().Trim('"');
        if (clean.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(clean))
            return;

        try
        {
            foreach (string subDir in Directory.EnumerateDirectories(clean).Take(60))
            {
                string name = Path.GetFileName(subDir);
                if (!ProductCodeHelper.IsGenericOrEmptyProduct(name))
                {
                    RegisterKnownProduct(name);
                }
                else if (string.Equals(name, "Bank", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(name, "Broker", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string prodDir in Directory.EnumerateDirectories(subDir).Take(60))
                    {
                        string prodName = Path.GetFileName(prodDir);
                        if (!ProductCodeHelper.IsGenericOrEmptyProduct(prodName))
                        {
                            RegisterKnownProduct(prodName);
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignorer silencieusement tout dossier non accessible
        }
    }

    private void RegisterKnownProduct(string? rawFilter)
    {
        if (ProductCodeHelper.IsGlobalFilter(rawFilter)) return;

        var tokens = rawFilter!
            .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string tok in tokens)
        {
            string clean = tok.Trim().Trim('[', ']');
            if (string.IsNullOrWhiteSpace(clean) || ProductCodeHelper.IsGlobalFilter(clean))
                continue;

            if (!KnownProductCodes.Any(existing => string.Equals(existing, clean, StringComparison.OrdinalIgnoreCase)))
            {
                KnownProductCodes.Add(clean);
            }
        }
    }

    private void AttachRuleListeners()
    {
        Profile.PropertyChanged -= OnProfilePropertyChanged;
        Profile.PropertyChanged += OnProfilePropertyChanged;

        foreach (var zone in Profile.ExclusionZones)
        {
            zone.PropertyChanged -= OnZonePropertyChanged;
            zone.PropertyChanged += OnZonePropertyChanged;
        }

        foreach (var rule in Profile.RegexRules)
        {
            rule.PropertyChanged -= OnRegexRulePropertyChanged;
            rule.PropertyChanged += OnRegexRulePropertyChanged;
        }
    }

    private void PersistChangesQuietly()
    {
        _configService.SyncActiveToGeneralIfNeeded();
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RunRegexPreview();
        PersistChangesQuietly();
    }

    private void OnZonePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ExclusionZoneRule z)
        {
            if (e.PropertyName == nameof(ExclusionZoneRule.ProductFilter))
            {
                if (!z.IsGlobalRule) z.IsUserZone = true;
                RegisterKnownProduct(z.ProductFilter);
            }
            NotifyRuleCounts();
        }
        RefreshVisualPreview();
        PersistChangesQuietly();
    }

    private void OnRegexRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is RegexMaskingRule r)
        {
            if (e.PropertyName == nameof(RegexMaskingRule.ProductFilter))
            {
                RegisterKnownProduct(r.ProductFilter);
            }

            if ((e.PropertyName == nameof(RegexMaskingRule.Pattern) ||
                 e.PropertyName == nameof(RegexMaskingRule.ReplacementMask) ||
                 e.PropertyName == nameof(RegexMaskingRule.Name) ||
                 e.PropertyName == nameof(RegexMaskingRule.ProductFilter)) &&
                !string.Equals(r.ReplacementMask, "[DATE_IGNORE]", StringComparison.Ordinal))
            {
                r.IsUserRule = true;
            }

            NotifyRuleCounts();
        }
        RunRegexPreview();
        PersistChangesQuietly();
    }

    private void NotifyRuleCounts()
    {
        OnPropertyChanged(nameof(GlobalZonesCount));
        OnPropertyChanged(nameof(ProductZonesCount));
        OnPropertyChanged(nameof(GlobalRegexCount));
        OnPropertyChanged(nameof(ProductRegexCount));
        OnPropertyChanged(nameof(HeaderRulesSummaryText));
    }

    partial void OnSampleTestInputChanged(string value) => RunRegexPreview();

    partial void OnRegexTestProductChanged(string value)
    {
        RegisterKnownProduct(value);
        RunRegexPreview();
    }

    partial void OnPreviewSimulatedProductChanged(string value)
    {
        RegisterKnownProduct(value);
        RefreshVisualPreview();
    }

    partial void OnTargetProductForNewRuleChanged(string value)
    {
        RegisterKnownProduct(value);
    }

    [RelayCommand]
    private void SetRuleScopeFilter(string? mode)
    {
        RuleScopeFilter = string.IsNullOrWhiteSpace(mode) ? "ALL" : mode.Trim().ToUpperInvariant();
        FilteredZonesView.Refresh();
        FilteredRegexRulesView.Refresh();
    }

    [RelayCommand]
    private void SetDrawLassoScope(string? scope)
    {
        DrawLassoForProduct = string.Equals(scope, "PRODUCT", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void SetQuickRuleMode(string? mode)
    {
        QuickRuleMode = string.IsNullOrWhiteSpace(mode) ? "IGNORE" : mode.Trim().ToUpperInvariant();
    }

    [RelayCommand]
    private void SetNewRuleScope(string? scope)
    {
        NewRuleForProduct = string.Equals(scope, "PRODUCT", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ApplyRegexPreset(string? presetKey)
    {
        QuickRuleMode = "REGEX";
        switch ((presetKey ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "DIGITS":
                QuickRegexPattern = @"\b\d{6,12}\b";
                QuickRuleCustomName = "Numéro / Référence (6-12 chiffres)";
                break;
            case "REF":
                QuickRegexPattern = @"\b[A-Z]{2,5}[-_/]?\d{4,}\b";
                QuickRuleCustomName = "Code référence (ex: FACT-12345)";
                break;
            case "IBAN":
                QuickRegexPattern = @"\b[A-Z]{2}\d{2}(?:[\s\u00A0]?[A-Z0-9]{4}){3,7}\b";
                QuickRuleCustomName = "Numéro IBAN";
                break;
            case "DATE":
                QuickRegexPattern = @"\b\d{1,2}[./\-\s]+\d{1,2}[./\-\s]+\d{2,4}\b";
                QuickRuleCustomName = "Date numérique (JJ/MM/AAAA)";
                break;
        }
    }

    [RelayCommand]
    private void CreateQuickRule()
    {
        string? scopeProd = null;
        if (NewRuleForProduct)
        {
            scopeProd = ResolveProductForNewRule();
            TargetProductForNewRule = scopeProd;
            RegexTestProduct = scopeProd;
            RegisterKnownProduct(scopeProd);
            if (IsFilterGlobal) SetRuleScopeFilter("ALL");
        }
        else if (IsFilterProduct)
        {
            SetRuleScopeFilter("ALL");
        }

        string? customName = string.IsNullOrWhiteSpace(QuickRuleCustomName) ? null : QuickRuleCustomName.Trim();
        RegexMaskingRule? activeRule = null;

        if (IsQuickModeIgnore)
        {
            string raw = (QuickIgnoreText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                StatusBannerText = "Saisissez d'abord le mot ou la phrase à ignorer.";
                return;
            }

            _configService.AddQuickIgnoreRegexRule(raw, customName, scopeProd);
            string pat = ConfigurationService.BuildFlexiblePatternFromDiffSide(raw);
            string cleanScope = scopeProd ?? string.Empty;
            activeRule = Profile.RegexRules.LastOrDefault(r =>
                string.Equals(r.Pattern, pat, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((r.ProductFilter ?? string.Empty).Trim(), cleanScope, StringComparison.OrdinalIgnoreCase))
                ?? Profile.RegexRules.LastOrDefault();

            QuickIgnoreText = string.Empty;
            QuickRuleCustomName = string.Empty;
        }
        else if (IsQuickModeEquiv)
        {
            string left = (QuickEquivLeft ?? string.Empty).Trim();
            string right = (QuickEquivRight ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                StatusBannerText = "Saisissez les deux textes équivalents (ex: Attendu « R » et Obtenu « Rue du »).";
                return;
            }

            activeRule = _configService.AddQuickEquivalenceRule(left, right, customName, scopeProd);
            QuickEquivLeft = string.Empty;
            QuickEquivRight = string.Empty;
            QuickRuleCustomName = string.Empty;
        }
        else
        {
            string rawPat = (QuickRegexPattern ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rawPat))
            {
                StatusBannerText = "Saisissez ou choisissez d'abord une expression Regex.";
                return;
            }
            if (!IsQuickRegexValid)
            {
                StatusBannerText = "L'expression Regex saisie est invalide (vérifiez les parenthèses ou crochets).";
                return;
            }

            string cleanScope = scopeProd ?? string.Empty;
            string scopePrefix = !string.IsNullOrEmpty(cleanScope) ? $"[{cleanScope}] " : string.Empty;
            var existing = Profile.RegexRules.FirstOrDefault(r =>
                string.Equals(r.Pattern, rawPat, StringComparison.Ordinal) &&
                string.Equals((r.ProductFilter ?? string.Empty).Trim(), cleanScope, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.IsEnabled = true;
                existing.IsUserRule = true;
                if (!string.IsNullOrEmpty(customName)) existing.Name = customName;
                activeRule = existing;
            }
            else
            {
                var created = new RegexMaskingRule
                {
                    IsEnabled = true,
                    IsUserRule = true,
                    ProductFilter = cleanScope,
                    Name = customName ?? $"{scopePrefix}Règle Regex #{Profile.RegexRules.Count + 1}",
                    Pattern = rawPat,
                    ReplacementMask = string.Empty,
                    IgnoreCase = true,
                    MinOccurrences = 1
                };
                Profile.RegexRules.Add(created);
                activeRule = created;
            }

            QuickRegexPattern = string.Empty;
            QuickRuleCustomName = string.Empty;
        }

        AttachRuleListeners();
        PersistChangesQuietly();
        FilteredRegexRulesView.Refresh();
        if (activeRule != null)
        {
            SelectedRegexRule = activeRule;
        }
        NotifyRuleCounts();
        RunRegexPreview();

        if (activeRule != null)
        {
            StatusBannerText = $"Règle « {activeRule.Name} » ajoutée et enregistrée de façon permanente.";
        }
    }

    private string ResolveProductForNewRule()
    {
        if (!ProductCodeHelper.IsGlobalFilter(TargetProductForNewRule))
            return TargetProductForNewRule.Trim();
        if (!ProductCodeHelper.IsGlobalFilter(PreviewSimulatedProduct))
            return PreviewSimulatedProduct.Trim();
        if (KnownProductCodes.Count > 0)
            return KnownProductCodes[0];
        return "SP.EXEMPLE";
    }

    [RelayCommand]
    private void RunRegexPreview()
    {
        try
        {
            string? simProd = ProductCodeHelper.IsGlobalFilter(RegexTestProduct) ? null : RegexTestProduct.Trim();
            SampleTestOutput = _maskingService.MaskRepeatingTextElements(SampleTestInput ?? string.Empty, null, simProd);
        }
        catch (Exception ex)
        {
            SampleTestOutput = $"Erreur d'évaluation : {ex.Message}";
        }
    }

    [RelayCommand]
    private void AddZone() => AddGlobalZone();

    [RelayCommand]
    private void AddGlobalZone()
    {
        if (IsFilterProduct) SetRuleScopeFilter("ALL");

        var newZone = new ExclusionZoneRule
        {
            Name = $"Zone Globale #{Profile.ExclusionZones.Count + 1}",
            IsEnabled = true,
            IsUserZone = true,
            ProductFilter = string.Empty,
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
        PersistChangesQuietly();
        FilteredZonesView.Refresh();
        SelectedZone = newZone;
        NotifyRuleCounts();
        RefreshVisualPreview();
    }

    [RelayCommand]
    private void AddProductZone()
    {
        if (IsFilterGlobal) SetRuleScopeFilter("ALL");

        string prod = ResolveProductForNewRule();
        TargetProductForNewRule = prod;
        RegisterKnownProduct(prod);

        var newZone = new ExclusionZoneRule
        {
            Name = $"[{prod}] Zone P.{CurrentPreviewPage} (Scramble)",
            IsEnabled = true,
            IsUserZone = true,
            ProductFilter = prod,
            Scope = CurrentPreviewPage == 1 ? PageScope.FirstPageOnly : PageScope.CustomRange,
            CustomPageStart = CurrentPreviewPage,
            CustomPageEnd = CurrentPreviewPage,
            Unit = CoordinateUnit.PdfPoints,
            Origin = ZoneOrigin.TopLeft,
            X = 200,
            Y = 180,
            Width = 300,
            Height = 45
        };

        newZone.PropertyChanged += OnZonePropertyChanged;
        Profile.ExclusionZones.Add(newZone);
        PersistChangesQuietly();
        FilteredZonesView.Refresh();
        SelectedZone = newZone;
        NotifyRuleCounts();
        RefreshVisualPreview();
        StatusBannerText = $"Nouvelle zone par produit créée pour « {prod} ».";
    }

    [RelayCommand]
    private void ClearZoneProductFilter(ExclusionZoneRule? zone)
    {
        var target = zone ?? SelectedZone;
        if (target == null) return;
        target.ProductFilter = string.Empty;
        PersistChangesQuietly();
        FilteredZonesView.Refresh();
        NotifyRuleCounts();
        RefreshVisualPreview();
    }

    [RelayCommand]
    private void DeleteZone(ExclusionZoneRule? zone)
    {
        var target = zone ?? SelectedZone;
        if (target == null) return;
        target.PropertyChanged -= OnZonePropertyChanged;
        Profile.ExclusionZones.Remove(target);
        FilteredZonesView.Refresh();
        if (ReferenceEquals(SelectedZone, target))
        {
            SelectedZone = Profile.ExclusionZones.FirstOrDefault();
        }
        PersistChangesQuietly();
        NotifyRuleCounts();
        RefreshVisualPreview();
    }

    [RelayCommand]
    private void RemoveZone() => DeleteZone(SelectedZone);

    /// <summary>
    /// Crée directement une zone d'exclusion (Globale ou Par Produit selon le mode sélectionné)
    /// à partir d'un rectangle tracé à la souris sur le Canvas d'aperçu.
    /// </summary>
    public void AddZoneFromCanvasLasso(double canvasX, double canvasY, double canvasW, double canvasH)
    {
        double scaleX = CurrentPageWidthPt / PreviewCanvasWidth;
        double scaleY = CurrentPageHeightPt / PreviewCanvasHeight;

        string prodFilter = string.Empty;
        string zoneName;
        PageScope pageScope = PageScope.AllPages;

        if (DrawLassoForProduct)
        {
            prodFilter = ResolveProductForNewRule();
            TargetProductForNewRule = prodFilter;
            RegisterKnownProduct(prodFilter);
            zoneName = $"[{prodFilter}] Zone P.{CurrentPreviewPage} #{Profile.ExclusionZones.Count + 1}";
            pageScope = CurrentPreviewPage == 1 && TotalPreviewPages > 1
                ? PageScope.FirstPageOnly
                : (TotalPreviewPages > 1 ? PageScope.CustomRange : PageScope.AllPages);
            if (IsFilterGlobal) SetRuleScopeFilter("ALL");
        }
        else
        {
            zoneName = $"Zone Globale #{Profile.ExclusionZones.Count + 1}";
            if (IsFilterProduct) SetRuleScopeFilter("ALL");
        }

        var drawnZone = new ExclusionZoneRule
        {
            Name = zoneName,
            IsEnabled = true,
            IsUserZone = true,
            ProductFilter = prodFilter,
            Scope = pageScope,
            CustomPageStart = CurrentPreviewPage,
            CustomPageEnd = CurrentPreviewPage,
            Unit = CoordinateUnit.PdfPoints,
            Origin = ZoneOrigin.TopLeft,
            X = Math.Round(canvasX * scaleX, 1),
            Y = Math.Round(canvasY * scaleY, 1),
            Width = Math.Round(canvasW * scaleX, 1),
            Height = Math.Round(canvasH * scaleY, 1)
        };

        drawnZone.PropertyChanged += OnZonePropertyChanged;
        Profile.ExclusionZones.Add(drawnZone);
        PersistChangesQuietly();
        FilteredZonesView.Refresh();
        SelectedZone = drawnZone;
        NotifyRuleCounts();
        RefreshVisualPreview();

        string scopeInfo = drawnZone.IsProductSpecificRule ? $"pour le produit {drawnZone.ProductFilter}" : "globale (tous produits)";
        StatusBannerText = $"Zone '{drawnZone.Name}' créée {scopeInfo} ({drawnZone.Width}x{drawnZone.Height} pt).";
    }

    [RelayCommand]
    private void AddRegexRule() => AddGlobalRegexRule();

    [RelayCommand]
    private void AddGlobalRegexRule()
    {
        if (IsFilterProduct) SetRuleScopeFilter("ALL");

        var rule = new RegexMaskingRule
        {
            Name = $"Règle Globale #{Profile.RegexRules.Count + 1}",
            IsEnabled = true,
            IsUserRule = true,
            ProductFilter = string.Empty,
            Pattern = @"\b\d{6,10}\b",
            ReplacementMask = string.Empty,
            IgnoreCase = true,
            MinOccurrences = 1
        };

        rule.PropertyChanged += OnRegexRulePropertyChanged;
        Profile.RegexRules.Add(rule);
        PersistChangesQuietly();
        FilteredRegexRulesView.Refresh();
        SelectedRegexRule = rule;
        NotifyRuleCounts();
        RunRegexPreview();
    }

    [RelayCommand]
    private void AddProductRegexRule()
    {
        if (IsFilterGlobal) SetRuleScopeFilter("ALL");

        string prod = ResolveProductForNewRule();
        TargetProductForNewRule = prod;
        RegexTestProduct = prod;
        RegisterKnownProduct(prod);

        var rule = new RegexMaskingRule
        {
            Name = $"[{prod}] Règle métier #{Profile.RegexRules.Count + 1}",
            IsEnabled = true,
            IsUserRule = true,
            ProductFilter = prod,
            Pattern = @"\b\d{6,10}\b",
            ReplacementMask = string.Empty,
            IgnoreCase = true,
            MinOccurrences = 1
        };

        rule.PropertyChanged += OnRegexRulePropertyChanged;
        Profile.RegexRules.Add(rule);
        PersistChangesQuietly();
        FilteredRegexRulesView.Refresh();
        SelectedRegexRule = rule;
        NotifyRuleCounts();
        RunRegexPreview();
        StatusBannerText = $"Nouvelle règle créée pour le produit « {prod} ».";
    }

    [RelayCommand]
    private void ClearRegexProductFilter(RegexMaskingRule? rule)
    {
        var target = rule ?? SelectedRegexRule;
        if (target == null) return;
        target.ProductFilter = string.Empty;
        PersistChangesQuietly();
        FilteredRegexRulesView.Refresh();
        NotifyRuleCounts();
        RunRegexPreview();
    }

    [RelayCommand]
    private void DeleteRegexRule(RegexMaskingRule? rule)
    {
        var target = rule ?? SelectedRegexRule;
        if (target == null) return;
        target.PropertyChanged -= OnRegexRulePropertyChanged;
        Profile.RegexRules.Remove(target);
        FilteredRegexRulesView.Refresh();
        if (ReferenceEquals(SelectedRegexRule, target))
        {
            SelectedRegexRule = Profile.RegexRules.FirstOrDefault();
        }
        PersistChangesQuietly();
        NotifyRuleCounts();
        RunRegexPreview();
    }

    [RelayCommand]
    private void RemoveRegexRule() => DeleteRegexRule(SelectedRegexRule);

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

            string detectedProd = ProductCodeHelper.ResolveEffectiveProductCode(null, null, dialog.FileName, null);
            if (!string.IsNullOrWhiteSpace(detectedProd))
            {
                RegisterKnownProduct(detectedProd);
                TargetProductForNewRule = detectedProd;
                PreviewSimulatedProduct = detectedProd;
                RegexTestProduct = detectedProd;
                DrawLassoForProduct = true;
            }

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
            lock (PdfPigSync.Gate)
            {
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

        bool filterBySimulatedProd = !ProductCodeHelper.IsGlobalFilter(PreviewSimulatedProduct);
        string simProd = filterBySimulatedProd ? PreviewSimulatedProduct.Trim() : string.Empty;

        var activeZones = Profile.ExclusionZones
            .Where(z => z.AppliesToPage(CurrentPreviewPage, TotalPreviewPages) &&
                        (!filterBySimulatedProd || z.AppliesToProduct(simProd, null, SamplePdfPath, null)))
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

            string label = zone.IsProductSpecificRule
                ? $"[{zone.ProductFilter}] {zone.Name}"
                : zone.Name;

            PreviewZones.Add(new PreviewZoneItem
            {
                Rule = zone,
                Name = zone.Name,
                DisplayLabel = label,
                IsProductSpecific = zone.IsProductSpecificRule,
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
        PersistChangesQuietly();
        RunRegexPreview();
        RefreshVisualPreview();
        NotifyRuleCounts();
        StatusBannerText = $"Règles enregistrées avec succès ({DateTime.Now:HH:mm:ss}).";
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
            FilteredZonesView = CreateZonesView();
            FilteredRegexRulesView = CreateRegexView();
            OnPropertyChanged(nameof(FilteredZonesView));
            OnPropertyChanged(nameof(FilteredRegexRulesView));
            AttachRuleListeners();
            ReloadKnownProducts();
            NotifyRuleCounts();
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
                FilteredZonesView = CreateZonesView();
                FilteredRegexRulesView = CreateRegexView();
                OnPropertyChanged(nameof(FilteredZonesView));
                OnPropertyChanged(nameof(FilteredRegexRulesView));
                AttachRuleListeners();
                ReloadKnownProducts();
                NotifyRuleCounts();
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
