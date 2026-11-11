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
    void AddQuickIgnoreRegexRule(string rawTextToIgnore, string? customRuleName = null);
    void ExportProfile(string filePath);
    bool ImportProfile(string filePath, out string errorMessage);
    Regex? TryGetCompiledRegex(string pattern, bool ignoreCase = true);
}

public class ConfigurationService : IConfigurationService
{
    public const string PresetStandard = "Standard (Factures & Contrats)";
    public const string PresetFinancialStrict = "Audit Financier Strict (Montants & Tableaux)";
    public const string PresetLegalReflow = "Juridique & CGV (Tolérant au Reflow)";
    public const string PresetVisualPixelPerfect = "Visuel Strict (Maquettes & Print)";

    private static readonly string[] BuiltInPresets =
    {
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
        var profile = ComparisonProfile.CreateDefault();

        switch (presetName)
        {
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

        CopyProfileValues(profile, ActiveProfile);
        SaveActiveProfile();
    }

    public void AddQuickIgnoreRegexRule(string rawTextToIgnore, string? customRuleName = null)
    {
        if (string.IsNullOrWhiteSpace(rawTextToIgnore)) return;

        string trimmed = rawTextToIgnore.Trim();
        if (trimmed.Length > 140)
        {
            trimmed = trimmed.Substring(0, 140).Trim();
        }

        string escaped = Regex.Escape(trimmed);
        string flexibleWhitespacePattern = Regex.Replace(escaped, @"(\\\s|\s)+", @"\s+");

        if (ActiveProfile.RegexRules.Any(r => string.Equals(r.Pattern, flexibleWhitespacePattern, StringComparison.OrdinalIgnoreCase)))
            return;

        string previewLabel = trimmed.Length > 28 ? trimmed.Substring(0, 28) + "..." : trimmed;
        ActiveProfile.RegexRules.Add(new RegexMaskingRule
        {
            IsEnabled = true,
            Name = customRuleName ?? $"Exclusion rapide ({previewLabel})",
            Pattern = flexibleWhitespacePattern,
            ReplacementMask = "[IGNORED_USER]",
            IgnoreCase = true
        });

        SaveActiveProfile();
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
