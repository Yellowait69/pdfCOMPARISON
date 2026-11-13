using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using PDFComparison.Models;

namespace PDFComparison.Services;

public interface IConfigurationService
{
    ComparisonProfile ActiveProfile { get; }
    IReadOnlyList<string> AvailablePresets { get; }
    void SaveActiveProfile();
    void ResetToDefault();
    void ApplyPreset(string presetName);
    void ApplyTidesTestingProfile(bool ignoreRepeatingDates, bool ignoreHeaderFooterZones);
    void AddQuickIgnoreRegexRule(string rawTextToIgnore, string? customRuleName = null);
    void AddQuickIgnoreDiffBlockRule(DiffSummaryBlock block);
    void ExportProfile(string filePath);
    bool ImportProfile(string filePath, out string errorMessage);
    Regex? TryGetCompiledRegex(string pattern, bool ignoreCase = true);
}

public class ConfigurationService : IConfigurationService
{
    public const string PresetTidesTesting = "Testing TIDES — Texte Visible à l'Œil Nu";
    public const string PresetStandard = "Standard (Factures & Contrats)";
    public const string PresetFinancialStrict = "Audit Financier Strict (Montants & Tableaux)";
    public const string PresetLegalReflow = "Juridique & CGV (Tolérant au Reflow)";
    public const string PresetVisualPixelPerfect = "Visuel Strict (Maquettes & Print)";

    private static readonly string[] BuiltInPresets =
    {
        PresetTidesTesting,
        PresetStandard,
        PresetFinancialStrict,
        PresetLegalReflow,
        PresetVisualPixelPerfect
    };

    private readonly string _profileFilePath;
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ComparisonProfile ActiveProfile { get; private set; }
    public IReadOnlyList<string> AvailablePresets => BuiltInPresets;

    public ConfigurationService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appData, "PDFComparisonPro");
        Directory.CreateDirectory(appFolder);
        _profileFilePath = Path.Combine(appFolder, "active_profile.json");

        ActiveProfile = LoadOrCreateDefault();
    }

    private ComparisonProfile LoadOrCreateDefault()
    {
        if (File.Exists(_profileFilePath))
        {
            try
            {
                string json = File.ReadAllText(_profileFilePath);
                var loaded = JsonSerializer.Deserialize<ComparisonProfile>(json, JsonOptions);
                if (loaded != null)
                {
                    if (UpgradeLegacySavedProfile(loaded))
                    {
                        SaveProfileInternal(loaded, _profileFilePath);
                    }
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading profile: {ex.Message}");
            }
        }

        var defaultProfile = ComparisonProfile.CreateDefault();
        SaveProfileInternal(defaultProfile, _profileFilePath);
        return defaultProfile;
    }

    private static bool UpgradeLegacySavedProfile(ComparisonProfile profile)
    {
        if (string.Equals(profile.ProfileName, PresetVisualPixelPerfect, StringComparison.Ordinal))
        {
            return false;
        }

        bool changed = false;

        foreach (var existingRule in profile.RegexRules)
        {
            if (!existingRule.IsUserRule && IsCustomUserRule(existingRule))
            {
                existingRule.IsUserRule = true;
                changed = true;
            }
        }

        if (!profile.RegexRules.Any(r => r.Pattern != null && r.Pattern.Contains("exemplaire", StringComparison.OrdinalIgnoreCase)))
        {
            profile.RegexRules.Add(new RegexMaskingRule
            {
                Name = "Mentions d'exemplaire technique (exemplaire.../CODE/CODE)",
                IsEnabled = true,
                Pattern = ComparisonProfile.ExemplaireRoutingPattern,
                ReplacementMask = "",
                IgnoreCase = true,
                MinOccurrences = 1
            });
            changed = true;
        }

        bool isTidesOrStandard =
            string.Equals(profile.ProfileName, PresetTidesTesting, StringComparison.Ordinal) ||
            string.Equals(profile.ProfileName, PresetStandard, StringComparison.Ordinal) ||
            profile.ProfileName.StartsWith("Profil Standard", StringComparison.OrdinalIgnoreCase);

        if (isTidesOrStandard)
        {
            if (!profile.EnableDynamicClientNameMasking)
            {
                profile.EnableDynamicClientNameMasking = true;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(profile.ClientKeywordsPattern) ||
                !profile.ClientKeywordsPattern.Contains("Preneur", StringComparison.OrdinalIgnoreCase))
            {
                profile.ClientKeywordsPattern = ComparisonProfile.DefaultClientKeywordsPattern;
                changed = true;
            }

            if (!profile.ExclusionZones.Any(z => z.Origin == ZoneOrigin.TopLeft && z.Width >= 500 && z.Height >= 100))
            {
                profile.ExclusionZones.Insert(0, new ExclusionZoneRule
                {
                    Name = "En-tête de page (Header 130pt)",
                    IsEnabled = true,
                    Scope = PageScope.AllPages,
                    Unit = CoordinateUnit.PdfPoints,
                    Origin = ZoneOrigin.TopLeft,
                    X = 0,
                    Y = 0,
                    Width = 2000,
                    Height = 130,
                    CollectDatesInZone = true
                });
                changed = true;
            }

            if (!profile.ExclusionZones.Any(z => z.Origin == ZoneOrigin.BottomLeft))
            {
                profile.ExclusionZones.Add(new ExclusionZoneRule
                {
                    Name = "Pied de page (Footer 40pt)",
                    IsEnabled = true,
                    Scope = PageScope.AllPages,
                    Unit = CoordinateUnit.PdfPoints,
                    Origin = ZoneOrigin.BottomLeft,
                    X = 0,
                    Y = 0,
                    Width = 2000,
                    Height = 40,
                    CollectDatesInZone = false
                });
                changed = true;
            }

            if (!profile.RegexRules.Any(r => string.Equals(r.ReplacementMask, "[DATE_IGNORE]", StringComparison.Ordinal)))
            {
                profile.RegexRules.Insert(0, new RegexMaskingRule
                {
                    Name = "Dates variables répétées",
                    IsEnabled = true,
                    Pattern = ComparisonProfile.DefaultDateRegexPattern,
                    ReplacementMask = "[DATE_IGNORE]",
                    IgnoreCase = true,
                    MinOccurrences = 2
                });
                changed = true;
            }
        }

        return changed;
    }

    public void SaveActiveProfile()
    {
        _regexCache.Clear();
        SaveProfileInternal(ActiveProfile, _profileFilePath);
    }

    public void ResetToDefault()
    {
        _regexCache.Clear();
        var fresh = ComparisonProfile.CreateDefault();
        CopyProfileValues(fresh, ActiveProfile);
        SaveActiveProfile();
    }

    public void ApplyPreset(string presetName)
    {
        var preservedUserZones = ActiveProfile.ExclusionZones
            .Where(z => z.IsUserZone)
            .ToList();

        var preservedUserRules = ActiveProfile.RegexRules
            .Where(IsCustomUserRule)
            .ToList();

        var profile = ComparisonProfile.CreateDefault();

        switch (presetName)
        {
            case PresetTidesTesting:
                profile.ProfileName = PresetTidesTesting;
                profile.ExclusionZones.Clear();
                profile.ExclusionZones.Add(new ExclusionZoneRule
                {
                    Name = "En-tête de page (Header 130pt)",
                    IsEnabled = true,
                    Scope = PageScope.AllPages,
                    Unit = CoordinateUnit.PdfPoints,
                    Origin = ZoneOrigin.TopLeft,
                    X = 0,
                    Y = 0,
                    Width = 2000,
                    Height = 130,
                    CollectDatesInZone = true
                });
                profile.ExclusionZones.Add(new ExclusionZoneRule
                {
                    Name = "Pied de page (Footer 40pt)",
                    IsEnabled = true,
                    Scope = PageScope.AllPages,
                    Unit = CoordinateUnit.PdfPoints,
                    Origin = ZoneOrigin.BottomLeft,
                    X = 0,
                    Y = 0,
                    Width = 2000,
                    Height = 40,
                    CollectDatesInZone = false
                });
                profile.ExclusionZones.Add(new ExclusionZoneRule
                {
                    Name = "Marge technique gauche (50pt)",
                    IsEnabled = true,
                    Scope = PageScope.AllPages,
                    Unit = CoordinateUnit.PdfPoints,
                    Origin = ZoneOrigin.TopLeft,
                    X = 0,
                    Y = 0,
                    Width = 50,
                    Height = 2000,
                    CollectDatesInZone = false
                });
                profile.RegexRules.Clear();
                profile.RegexRules.Add(new RegexMaskingRule
                {
                    Name = "Dates variables répétées",
                    IsEnabled = true,
                    Pattern = ComparisonProfile.DefaultDateRegexPattern,
                    ReplacementMask = "[DATE_IGNORE]",
                    IgnoreCase = true,
                    MinOccurrences = 2
                });
                profile.RegexRules.Add(new RegexMaskingRule
                {
                    Name = "Mentions d'exemplaire technique (exemplaire.../CODE/CODE)",
                    IsEnabled = true,
                    Pattern = ComparisonProfile.ExemplaireRoutingPattern,
                    ReplacementMask = "",
                    IgnoreCase = true,
                    MinOccurrences = 1
                });
                profile.RegexRules.Add(new RegexMaskingRule
                {
                    Name = "Ancres techniques de signature (#...#)",
                    IsEnabled = true,
                    Pattern = @"#[A-Z0-9_]+#",
                    ReplacementMask = "",
                    IgnoreCase = true,
                    MinOccurrences = 1
                });
                profile.EnableDynamicClientNameMasking = true;
                profile.ClientKeywordsPattern = ComparisonProfile.DefaultClientKeywordsPattern;
                profile.IgnoreWhiteOnWhiteText = true;
                profile.MinAllowedFontSizePt = 1.5;
                profile.MaxLineShiftForFullLine = 10;
                profile.MaxLineShiftForSequence = 8;
                profile.MaxLineShiftForSingleWord = 2;
                profile.SsimSimilarityThreshold = 0.960;
                profile.PerceptualYiqThreshold = 0.080;
                profile.IgnoreAntiAliasingPixels = true;
                profile.MinVisualClusterAreaPx = 14;
                break;

            case PresetFinancialStrict:
                profile.ProfileName = PresetFinancialStrict;
                profile.EnableDynamicClientNameMasking = false;
                profile.MaxLineShiftForFullLine = 4;
                profile.MaxLineShiftForSequence = 3;
                profile.MaxLineShiftForSingleWord = 1;
                profile.SsimSimilarityThreshold = 0.990;
                profile.PerceptualYiqThreshold = 0.035;
                profile.MinVisualClusterAreaPx = 12;
                break;

            case PresetLegalReflow:
                profile.ProfileName = PresetLegalReflow;
                profile.EnableDynamicClientNameMasking = true;
                profile.MaxLineShiftForFullLine = 24;
                profile.MaxLineShiftForSequence = 18;
                profile.MaxLineShiftForSingleWord = 5;
                profile.LocalProximityMaxX = 520.0m;
                profile.LocalProximityMinY = 65.0m;
                profile.SsimSimilarityThreshold = 0.965;
                profile.PerceptualYiqThreshold = 0.080;
                break;

            case PresetVisualPixelPerfect:
                profile.ProfileName = PresetVisualPixelPerfect;
                profile.ExclusionZones.Clear();
                profile.RegexRules.Clear();
                profile.EnableDynamicClientNameMasking = false;
                profile.MaxLineShiftForFullLine = 1;
                profile.MaxLineShiftForSequence = 1;
                profile.MaxLineShiftForSingleWord = 0;
                profile.SsimSimilarityThreshold = 0.995;
                profile.PerceptualYiqThreshold = 0.020;
                profile.IgnoreAntiAliasingPixels = true;
                profile.MinVisualClusterAreaPx = 8;
                break;

            default:
                profile.ProfileName = PresetStandard;
                break;
        }

        // Conserver systématiquement toutes les zones et règles Regex créées par l'utilisateur
        foreach (var uz in preservedUserZones)
        {
            uz.IsUserZone = true;
            if (!profile.ExclusionZones.Any(z => z.Id == uz.Id))
            {
                profile.ExclusionZones.Add(uz);
            }
        }

        foreach (var ur in preservedUserRules)
        {
            ur.IsUserRule = true;
            bool alreadyExists = profile.RegexRules.Any(r =>
                r.Id == ur.Id ||
                (!string.IsNullOrWhiteSpace(ur.Pattern) && string.Equals(r.Pattern, ur.Pattern, StringComparison.OrdinalIgnoreCase)));
            if (!alreadyExists)
            {
                profile.RegexRules.Add(ur);
            }
        }

        CopyProfileValues(profile, ActiveProfile);
        SaveActiveProfile();
    }

    public void ApplyTidesTestingProfile(bool ignoreRepeatingDates, bool ignoreHeaderFooterZones)
    {
        ApplyPreset(PresetTidesTesting);

        foreach (var zone in ActiveProfile.ExclusionZones)
        {
            if (zone.Origin == ZoneOrigin.TopLeft && zone.Width >= 500)
            {
                zone.IsEnabled = ignoreHeaderFooterZones;
                zone.CollectDatesInZone = ignoreRepeatingDates;
            }
            else if (zone.Origin == ZoneOrigin.BottomLeft)
            {
                zone.IsEnabled = ignoreHeaderFooterZones;
            }
        }

        foreach (var rule in ActiveProfile.RegexRules)
        {
            if (string.Equals(rule.ReplacementMask, "[DATE_IGNORE]", StringComparison.Ordinal))
            {
                rule.IsEnabled = ignoreRepeatingDates;
            }
        }

        SaveActiveProfile();
    }

    public void AddQuickIgnoreRegexRule(string rawTextToIgnore, string? customRuleName = null)
    {
        if (string.IsNullOrWhiteSpace(rawTextToIgnore)) return;

        string pattern = BuildFlexiblePatternFromDiffSide(rawTextToIgnore);
        if (string.IsNullOrWhiteSpace(pattern)) return;

        var existing = ActiveProfile.RegexRules.FirstOrDefault(r =>
            string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsEnabled = true;
            existing.IsUserRule = true;
            SaveActiveProfile();
            return;
        }

        string trimmed = rawTextToIgnore.Trim();
        string previewLabel = trimmed.Length > 28 ? trimmed.Substring(0, 28) + "..." : trimmed;
        ActiveProfile.RegexRules.Add(new RegexMaskingRule
        {
            IsEnabled = true,
            IsUserRule = true,
            Name = customRuleName ?? $"Ignorer : \"{previewLabel}\"",
            Pattern = pattern,
            ReplacementMask = string.Empty,
            IgnoreCase = true,
            MinOccurrences = 1
        });

        SaveActiveProfile();
    }

    public void AddQuickIgnoreDiffBlockRule(DiffSummaryBlock block)
    {
        if (block == null) return;

        string oldRaw = (block.OldText ?? string.Empty).Trim();
        string newRaw = (block.NewText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(oldRaw) && string.IsNullOrWhiteSpace(newRaw)) return;

        string signature = ProductDifferenceGroupingService.GetBlockSignature(block);

        string oldPattern = BuildFlexiblePatternFromDiffSide(oldRaw);
        string newPattern = BuildFlexiblePatternFromDiffSide(newRaw);

        string finalPattern;
        string replacementMask;
        string ruleLabel;

        if (!string.IsNullOrWhiteSpace(oldPattern) && !string.IsNullOrWhiteSpace(newPattern))
        {
            if (string.Equals(oldPattern, newPattern, StringComparison.OrdinalIgnoreCase))
            {
                finalPattern = oldPattern;
            }
            else
            {
                // Placer l'expression la plus longue en premier pour éviter tout match partiel
                finalPattern = oldRaw.Length >= newRaw.Length
                    ? $"(?:{oldPattern}|{newPattern})"
                    : $"(?:{newPattern}|{oldPattern})";
            }

            replacementMask = "[IGNORED_USER]";
            string pOld = oldRaw.Length > 18 ? oldRaw.Substring(0, 18) + "…" : oldRaw;
            string pNew = newRaw.Length > 18 ? newRaw.Substring(0, 18) + "…" : newRaw;
            ruleLabel = $"Ignorer : \"{pOld}\" ➔ \"{pNew}\"";
        }
        else
        {
            finalPattern = !string.IsNullOrWhiteSpace(newPattern) ? newPattern : oldPattern;
            replacementMask = string.Empty;
            string singleTxt = !string.IsNullOrWhiteSpace(newRaw) ? newRaw : oldRaw;
            string preview = singleTxt.Length > 32 ? singleTxt.Substring(0, 32) + "…" : singleTxt;
            ruleLabel = $"Ignorer : \"{preview}\"";
        }

        var existing = ActiveProfile.RegexRules.FirstOrDefault(r =>
            (!string.IsNullOrWhiteSpace(signature) && string.Equals(r.IgnoredDiffSignature, signature, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(finalPattern) && string.Equals(r.Pattern, finalPattern, StringComparison.OrdinalIgnoreCase)));

        if (existing != null)
        {
            existing.IsEnabled = true;
            existing.IsUserRule = true;
            existing.Pattern = finalPattern;
            existing.ReplacementMask = replacementMask;
            existing.IgnoredDiffSignature = signature;
            SaveActiveProfile();
            return;
        }

        ActiveProfile.RegexRules.Add(new RegexMaskingRule
        {
            IsEnabled = true,
            IsUserRule = true,
            Name = ruleLabel,
            Pattern = finalPattern,
            ReplacementMask = replacementMask,
            IgnoreCase = true,
            MinOccurrences = 1,
            IgnoredDiffSignature = signature
        });

        SaveActiveProfile();
    }

    private static string BuildFlexiblePatternFromDiffSide(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;

        string trimmed = rawText.Trim();
        if (trimmed.Length > 220)
        {
            trimmed = trimmed.Substring(0, 220).Trim();
        }

        var segments = trimmed
            .Split('…', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(seg =>
            {
                var tokens = Regex.Split(seg, @"\s+")
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(Regex.Escape);
                return string.Join(@"\s+", tokens);
            })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (segments.Count == 0) return string.Empty;

        string core = string.Join(@"(?:\s+\S+){1,25}\s+", segments);

        bool startsAlphaNum = char.IsLetterOrDigit(trimmed[0]);
        bool endsAlphaNum = char.IsLetterOrDigit(trimmed[^1]);

        string prefixBoundary = startsAlphaNum ? @"(?<![\p{L}\p{N}])" : string.Empty;
        string suffixBoundary = endsAlphaNum ? @"(?![\p{L}\p{N}])" : string.Empty;

        return $"{prefixBoundary}{core}{suffixBoundary}";
    }

    private static bool IsCustomUserRule(RegexMaskingRule? r)
    {
        if (r == null) return false;
        return r.IsUserRule ||
               !string.IsNullOrWhiteSpace(r.IgnoredDiffSignature) ||
               string.Equals(r.ReplacementMask, "[IGNORED_USER]", StringComparison.Ordinal) ||
               (r.Name != null && (
                   r.Name.StartsWith("Exclusion rapide", StringComparison.OrdinalIgnoreCase) ||
                   r.Name.StartsWith("Ignorer :", StringComparison.OrdinalIgnoreCase) ||
                   r.Name.StartsWith("Règle #", StringComparison.OrdinalIgnoreCase)));
    }

    public void ExportProfile(string filePath)
    {
        SaveProfileInternal(ActiveProfile, filePath);
    }

    public bool ImportProfile(string filePath, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            string json = File.ReadAllText(filePath);
            var imported = JsonSerializer.Deserialize<ComparisonProfile>(json, JsonOptions);
            if (imported == null)
            {
                errorMessage = "Le fichier JSON ne contient pas un profil valide.";
                return false;
            }

            CopyProfileValues(imported, ActiveProfile);
            SaveActiveProfile();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public Regex? TryGetCompiledRegex(string pattern, bool ignoreCase = true)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;

        string key = $"{(ignoreCase ? "I:" : "S:")}{pattern}";
        return _regexCache.GetOrAdd(key, _ =>
        {
            try
            {
                var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                if (ignoreCase) options |= RegexOptions.IgnoreCase;
                return new Regex(pattern, options, TimeSpan.FromSeconds(2));
            }
            catch
            {
                return null;
            }
        });
    }

    private static void SaveProfileInternal(ComparisonProfile profile, string targetPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(profile, JsonOptions);
            string tempPath = targetPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving profile to {targetPath}: {ex.Message}");
        }
    }

    private static void CopyProfileValues(ComparisonProfile source, ComparisonProfile destination)
    {
        destination.ProfileName = source.ProfileName;

        destination.ExclusionZones.Clear();
        foreach (var zone in source.ExclusionZones)
        {
            destination.ExclusionZones.Add(zone);
        }

        destination.RegexRules.Clear();
        foreach (var rule in source.RegexRules)
        {
            destination.RegexRules.Add(rule);
        }

        destination.EnableDynamicClientNameMasking = source.EnableDynamicClientNameMasking;
        destination.ClientKeywordsPattern = source.ClientKeywordsPattern;
        destination.ClientNameReplacementMask = source.ClientNameReplacementMask;

        destination.ExactWatermarkWordsCsv = source.ExactWatermarkWordsCsv;
        destination.LargeWatermarkFragmentsCsv = source.LargeWatermarkFragmentsCsv;
        destination.WatermarkFragmentMinFontSizePt = source.WatermarkFragmentMinFontSizePt;
        destination.MaxAllowedFontSizePt = source.MaxAllowedFontSizePt;
        destination.MinAllowedFontSizePt = source.MinAllowedFontSizePt;
        destination.IgnoreWhiteOnWhiteText = source.IgnoreWhiteOnWhiteText;

        destination.MaxLineShiftForFullLine = source.MaxLineShiftForFullLine;
        destination.MaxLineShiftForSequence = source.MaxLineShiftForSequence;
        destination.MaxLineShiftForSingleWord = source.MaxLineShiftForSingleWord;
        destination.LocalProximityMaxX = source.LocalProximityMaxX;
        destination.LocalProximityMinY = source.LocalProximityMinY;

        destination.EnableVisualSsimEngine = source.EnableVisualSsimEngine;
        destination.VisualRenderDpi = source.VisualRenderDpi;
        destination.PerceptualYiqThreshold = source.PerceptualYiqThreshold;
        destination.SsimSimilarityThreshold = source.SsimSimilarityThreshold;
        destination.IgnoreAntiAliasingPixels = source.IgnoreAntiAliasingPixels;
        destination.EnableSubPixelAutoAlignment = source.EnableSubPixelAutoAlignment;
        destination.MaxAutoAlignmentShiftPx = source.MaxAutoAlignmentShiftPx;
        destination.MinVisualClusterAreaPx = source.MinVisualClusterAreaPx;
        destination.GenerateVisualSnapshotsInGlobalReport = source.GenerateVisualSnapshotsInGlobalReport;

        destination.FileMatchingRegex = source.FileMatchingRegex;
        destination.CriticalKeywordsRegex = source.CriticalKeywordsRegex;
    }
}
