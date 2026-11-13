using System;
using System.Collections.Concurrent;
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
    ComparisonProfile GeneralProfile { get; }
    AppUserSettings UserSettings { get; }
    void SaveActiveProfile();
    void SaveUserSettings();
    void ResetToDefault();
    void UseGeneralProfile();
    void ActivateScanProfile(AutomatedScanProfile scanProfile, bool ignoreRepeatingDates, bool ignoreHeaderFooterZones);
    void ApplyTidesTestingProfile(bool ignoreRepeatingDates, bool ignoreHeaderFooterZones);
    void AddQuickIgnoreRegexRule(string rawTextToIgnore, string? customRuleName = null, string? productFilter = null);
    RegexMaskingRule? AddQuickEquivalenceRule(string textA, string? textB, string? customRuleName = null, string? productFilter = null);
    void AddQuickIgnoreDiffBlockRule(DiffSummaryBlock block, string? productFilter = null);
    ExclusionZoneRule? AddQuickIgnoreZoneRule(DiffSummaryBlock block, string? productFilter = null);
    void SyncActiveToGeneralIfNeeded();
    void ExportProfile(string filePath);
    bool ImportProfile(string filePath, out string errorMessage);
    void ExportScanProfile(AutomatedScanProfile scanProfile, string filePath);
    bool ImportScanProfile(string filePath, out AutomatedScanProfile? imported, out string errorMessage);
    IConfigurationService CreateScopedServiceForProfile(ComparisonProfile targetProfile);
    Regex? TryGetCompiledRegex(string pattern, bool ignoreCase = true);
}

public class ConfigurationService : IConfigurationService
{
    public const string DefaultVisibleTextProfileName = "Texte Visible à l'Œil Nu";

    private readonly string _profileFilePath;
    private readonly string _settingsFilePath;
    private readonly ComparisonProfile _generalProfile;
    private ComparisonProfile? _activeOverrideProfile;
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ComparisonProfile GeneralProfile => _generalProfile;
    public ComparisonProfile ActiveProfile => _activeOverrideProfile ?? _generalProfile;
    public AppUserSettings UserSettings { get; private set; }

    public ConfigurationService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appData, "PDFComparisonPro");
        Directory.CreateDirectory(appFolder);

        _profileFilePath = Path.Combine(appFolder, "active_profile.json");
        _settingsFilePath = Path.Combine(appFolder, "app_settings.json");

        _generalProfile = LoadOrCreateDefaultProfile();
        UserSettings = LoadOrCreateUserSettings();
    }

    private ComparisonProfile LoadOrCreateDefaultProfile()
    {
        if (File.Exists(_profileFilePath))
        {
            try
            {
                string json = File.ReadAllText(_profileFilePath);
                var loaded = JsonSerializer.Deserialize<ComparisonProfile>(json, JsonOptions);
                if (loaded != null)
                {
                    if (UpgradeLegacySavedProfile(loaded, forceDefaultName: true))
                    {
                        SaveJsonAtomic(loaded, _profileFilePath);
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
        SaveJsonAtomic(defaultProfile, _profileFilePath);
        return defaultProfile;
    }

    private AppUserSettings LoadOrCreateUserSettings()
    {
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppUserSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    bool changed = false;

                    if (string.IsNullOrWhiteSpace(loaded.DefaultReportOutputDirectory))
                    {
                        loaded.DefaultReportOutputDirectory = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                            "PDF_DiffReports");
                        changed = true;
                    }

                    if (loaded.ScanProfiles.Count == 0)
                    {
                        loaded.ScanProfiles.Add(AutomatedScanProfile.CreateDefaultEquipeTesting());
                        changed = true;
                    }

                    foreach (var sp in loaded.ScanProfiles)
                    {
                        if (sp.DedicatedProfile == null)
                        {
                            sp.DedicatedProfile = ComparisonProfile.CreateDefault();
                            changed = true;
                        }
                        else if (UpgradeLegacySavedProfile(sp.DedicatedProfile, forceDefaultName: false))
                        {
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        SaveJsonAtomic(loaded, _settingsFilePath);
                    }

                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading app settings: {ex.Message}");
            }
        }

        var defaults = AppUserSettings.CreateDefault();
        SaveJsonAtomic(defaults, _settingsFilePath);
        return defaults;
    }

    internal static bool UpgradeLegacySavedProfile(ComparisonProfile profile, bool forceDefaultName)
    {
        bool changed = false;

        bool isLegacyNamedProfile = forceDefaultName && !string.Equals(profile.ProfileName, DefaultVisibleTextProfileName, StringComparison.Ordinal);
        if (isLegacyNamedProfile)
        {
            profile.ProfileName = DefaultVisibleTextProfileName;
            changed = true;
        }

        foreach (var existingRule in profile.RegexRules)
        {
            if (!existingRule.IsUserRule && IsCustomUserRule(existingRule))
            {
                existingRule.IsUserRule = true;
                changed = true;
            }
        }

        for (int i = profile.RegexRules.Count - 1; i >= 0; i--)
        {
            var r = profile.RegexRules[i];
            if (!r.IsUserRule && !r.IsEnabled && string.Equals(r.ReplacementMask, "[REF_IGNORE]", StringComparison.Ordinal))
            {
                profile.RegexRules.RemoveAt(i);
                changed = true;
            }
        }

        if (isLegacyNamedProfile)
        {
            if (!profile.EnableDynamicClientNameMasking)
            {
                profile.EnableDynamicClientNameMasking = true;
                changed = true;
            }

            if (!profile.IgnoreWhiteOnWhiteText)
            {
                profile.IgnoreWhiteOnWhiteText = true;
                changed = true;
            }
        }

        if (profile.MinAllowedFontSizePt < 1.5)
        {
            profile.MinAllowedFontSizePt = 1.5;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(profile.ClientKeywordsPattern) ||
            !profile.ClientKeywordsPattern.Contains("Preneur", StringComparison.OrdinalIgnoreCase))
        {
            profile.ClientKeywordsPattern = ComparisonProfile.DefaultClientKeywordsPattern;
            changed = true;
        }

        foreach (var existingZone in profile.ExclusionZones)
        {
            if (!existingZone.IsUserZone && (!existingZone.IsGlobalRule || IsCustomUserZone(existingZone)))
            {
                existingZone.IsUserZone = true;
                changed = true;
            }
        }

        if (!profile.ExclusionZones.Any(z => !z.IsUserZone && z.IsGlobalRule && z.Origin == ZoneOrigin.TopLeft && z.Width >= 500 && z.Height >= 100))
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

        if (!profile.ExclusionZones.Any(z => !z.IsUserZone && z.IsGlobalRule && z.Origin == ZoneOrigin.BottomLeft))
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

        var existingLeftMargin = profile.ExclusionZones.FirstOrDefault(z =>
            !z.IsUserZone && z.IsGlobalRule && z.Origin == ZoneOrigin.TopLeft && z.X == 0 && z.Width <= 80 && z.Height >= 500);
        if (existingLeftMargin == null)
        {
            profile.ExclusionZones.Add(new ExclusionZoneRule
            {
                Name = "Marge technique gauche (28pt)",
                IsEnabled = true,
                Scope = PageScope.AllPages,
                Unit = CoordinateUnit.PdfPoints,
                Origin = ZoneOrigin.TopLeft,
                X = 0,
                Y = 0,
                Width = 28,
                Height = 2000,
                CollectDatesInZone = false
            });
            changed = true;
        }
        else if (existingLeftMargin.Width > 30 && existingLeftMargin.Width <= 60)
        {
            existingLeftMargin.Width = 28;
            existingLeftMargin.Name = "Marge technique gauche (28pt)";
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

        var existingExemplaireRule = profile.RegexRules.FirstOrDefault(r =>
            !r.IsUserRule && r.Pattern != null && r.Pattern.Contains("exemplaire", StringComparison.OrdinalIgnoreCase));
        if (existingExemplaireRule == null)
        {
            profile.RegexRules.Add(new RegexMaskingRule
            {
                Name = "Mentions d'exemplaire multilingues (exemplaire(s) / exemplaar...)",
                IsEnabled = true,
                Pattern = ComparisonProfile.ExemplaireRoutingPattern,
                ReplacementMask = "",
                IgnoreCase = true,
                MinOccurrences = 1
            });
            changed = true;
        }
        else if (!string.Equals(existingExemplaireRule.Pattern, ComparisonProfile.ExemplaireRoutingPattern, StringComparison.Ordinal) ||
                 existingExemplaireRule.MinOccurrences != 1)
        {
            existingExemplaireRule.Name = "Mentions d'exemplaire multilingues (exemplaire(s) / exemplaar...)";
            existingExemplaireRule.Pattern = ComparisonProfile.ExemplaireRoutingPattern;
            existingExemplaireRule.ReplacementMask = "";
            existingExemplaireRule.IgnoreCase = true;
            existingExemplaireRule.MinOccurrences = 1;
            changed = true;
        }

        if (!profile.RegexRules.Any(r => string.Equals(r.Pattern, @"#[A-Z0-9_]+#", StringComparison.Ordinal)))
        {
            profile.RegexRules.Add(new RegexMaskingRule
            {
                Name = "Ancres techniques de signature (#...#)",
                IsEnabled = true,
                Pattern = @"#[A-Z0-9_]+#",
                ReplacementMask = "",
                IgnoreCase = true,
                MinOccurrences = 1
            });
            changed = true;
        }

        return changed;
    }

    public void SaveActiveProfile()
    {
        _regexCache.Clear();
        SaveJsonAtomic(_generalProfile, _profileFilePath);
        SaveJsonAtomic(UserSettings, _settingsFilePath);
    }

    public void SaveUserSettings()
    {
        _regexCache.Clear();
        SaveJsonAtomic(UserSettings, _settingsFilePath);
    }

    public void ResetToDefault()
    {
        _regexCache.Clear();
        var fresh = ComparisonProfile.CreateDefault();
        CopyProfileValues(fresh, ActiveProfile);
        SaveActiveProfile();
    }

    public void UseGeneralProfile()
    {
        _activeOverrideProfile = null;
    }

    public void ActivateScanProfile(AutomatedScanProfile scanProfile, bool ignoreRepeatingDates, bool ignoreHeaderFooterZones)
    {
        if (scanProfile != null && scanProfile.UseDedicatedRules)
        {
            scanProfile.DedicatedProfile ??= ComparisonProfile.CreateDefault();
            _activeOverrideProfile = scanProfile.DedicatedProfile;
            ApplyFlagsToProfile(_activeOverrideProfile, ignoreRepeatingDates, ignoreHeaderFooterZones, forceDefaultName: false);
            SaveUserSettings();
        }
        else
        {
            _activeOverrideProfile = null;
            ApplyTidesTestingProfile(ignoreRepeatingDates, ignoreHeaderFooterZones);
        }
    }

    public void ApplyTidesTestingProfile(bool ignoreRepeatingDates, bool ignoreHeaderFooterZones)
    {
        ApplyFlagsToProfile(ActiveProfile, ignoreRepeatingDates, ignoreHeaderFooterZones, forceDefaultName: ActiveProfile == _generalProfile);
        SaveActiveProfile();
    }

    public void SyncActiveToGeneralIfNeeded()
    {
        if (_activeOverrideProfile != null && !ReferenceEquals(_activeOverrideProfile, _generalProfile))
        {
            string savedGeneralName = _generalProfile.ProfileName;
            CopyProfileValues(_activeOverrideProfile, _generalProfile);
            _generalProfile.ProfileName = savedGeneralName;
        }
        SaveActiveProfile();
    }

    private static void ApplyFlagsToProfile(
        ComparisonProfile target,
        bool ignoreRepeatingDates,
        bool ignoreHeaderFooterZones,
        bool forceDefaultName)
    {
        UpgradeLegacySavedProfile(target, forceDefaultName);

        foreach (var zone in target.ExclusionZones)
        {
            if (zone.IsUserZone || !zone.IsGlobalRule) continue;

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

        foreach (var rule in target.RegexRules)
        {
            if (string.Equals(rule.ReplacementMask, "[DATE_IGNORE]", StringComparison.Ordinal) && rule.IsGlobalRule && !rule.IsUserRule)
            {
                rule.IsEnabled = ignoreRepeatingDates;
            }
        }
    }

    public void AddQuickIgnoreRegexRule(string rawTextToIgnore, string? customRuleName = null, string? productFilter = null)
    {
        AddQuickIgnoreRegexRuleOnProfile(ActiveProfile, rawTextToIgnore, customRuleName, productFilter);
        if (!ReferenceEquals(ActiveProfile, _generalProfile))
        {
            AddQuickIgnoreRegexRuleOnProfile(_generalProfile, rawTextToIgnore, customRuleName, productFilter);
        }
        SaveActiveProfile();
    }

    public RegexMaskingRule? AddQuickEquivalenceRule(string textA, string? textB, string? customRuleName = null, string? productFilter = null)
    {
        var rule = AddQuickEquivalenceRuleOnProfile(ActiveProfile, textA, textB, customRuleName, productFilter);
        if (!ReferenceEquals(ActiveProfile, _generalProfile))
        {
            AddQuickEquivalenceRuleOnProfile(_generalProfile, textA, textB, customRuleName, productFilter);
        }
        if (rule != null)
        {
            SaveActiveProfile();
        }
        return rule;
    }

    public void AddQuickIgnoreDiffBlockRule(DiffSummaryBlock block, string? productFilter = null)
    {
        AddQuickIgnoreDiffBlockRuleOnProfile(ActiveProfile, block, productFilter);
        if (!ReferenceEquals(ActiveProfile, _generalProfile))
        {
            AddQuickIgnoreDiffBlockRuleOnProfile(_generalProfile, block, productFilter);
        }
        SaveActiveProfile();
    }

    public ExclusionZoneRule? AddQuickIgnoreZoneRule(DiffSummaryBlock block, string? productFilter = null)
    {
        var rule = AddQuickIgnoreZoneRuleOnProfile(ActiveProfile, block, productFilter);
        if (!ReferenceEquals(ActiveProfile, _generalProfile))
        {
            AddQuickIgnoreZoneRuleOnProfile(_generalProfile, block, productFilter);
        }
        if (rule != null)
        {
            SaveActiveProfile();
        }
        return rule;
    }

    internal static RegexMaskingRule? AddQuickEquivalenceRuleOnProfile(
        ComparisonProfile profile,
        string? textA,
        string? textB,
        string? customRuleName = null,
        string? productFilter = null)
    {
        string rawA = (textA ?? string.Empty).Trim();
        string rawB = (textB ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawA) || string.IsNullOrWhiteSpace(rawB))
            return null;

        string patA = BuildFlexiblePatternFromDiffSide(rawA);
        string patB = BuildFlexiblePatternFromDiffSide(rawB);
        if (string.IsNullOrWhiteSpace(patA) || string.IsNullOrWhiteSpace(patB))
            return null;

        string cleanScope = ProductCodeHelper.IsGlobalFilter(productFilter) ? string.Empty : productFilter!.Trim();
        string scopePrefix = !string.IsNullOrEmpty(cleanScope) ? $"[{cleanScope}] " : string.Empty;

        string finalPattern = string.Equals(patA, patB, StringComparison.OrdinalIgnoreCase)
            ? patA
            : (rawA.Length >= rawB.Length ? $"(?:{patA}|{patB})" : $"(?:{patB}|{patA})");

        const string mask = "[EQUI_IGNORE]";
        string existingMatch = profile.RegexRules
            .FirstOrDefault(r =>
                string.Equals((r.ProductFilter ?? string.Empty).Trim(), cleanScope, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Pattern, finalPattern, StringComparison.OrdinalIgnoreCase))?.Pattern ?? string.Empty;

        if (!string.IsNullOrEmpty(existingMatch))
        {
            var found = profile.RegexRules.First(r =>
                string.Equals((r.ProductFilter ?? string.Empty).Trim(), cleanScope, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Pattern, finalPattern, StringComparison.OrdinalIgnoreCase));
            found.IsEnabled = true;
            found.IsUserRule = true;
            found.ReplacementMask = mask;
            return found;
        }

        string pA = rawA.Length > 20 ? rawA[..20] + "…" : rawA;
        string pB = rawB.Length > 20 ? rawB[..20] + "…" : rawB;

        var created = new RegexMaskingRule
        {
            IsEnabled = true,
            IsUserRule = true,
            ProductFilter = cleanScope,
            Name = customRuleName ?? $"{scopePrefix}Équivalence : \"{pA}\" = \"{pB}\"",
            Pattern = finalPattern,
            ReplacementMask = mask,
            IgnoreCase = true,
            MinOccurrences = 1
        };
        profile.RegexRules.Add(created);
        return created;
    }

    internal static void AddQuickIgnoreRegexRuleOnProfile(ComparisonProfile profile, string rawTextToIgnore, string? customRuleName, string? productFilter = null)
    {
        if (string.IsNullOrWhiteSpace(rawTextToIgnore)) return;

        string pattern = BuildFlexiblePatternFromDiffSide(rawTextToIgnore);
        if (string.IsNullOrWhiteSpace(pattern)) return;

        string cleanScope = ProductCodeHelper.IsGlobalFilter(productFilter) ? string.Empty : productFilter!.Trim();

        var existing = profile.RegexRules.FirstOrDefault(r =>
            string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((r.ProductFilter ?? string.Empty).Trim(), cleanScope, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsEnabled = true;
            existing.IsUserRule = true;
            return;
        }

        string trimmed = rawTextToIgnore.Trim();
        string previewLabel = trimmed.Length > 28 ? trimmed.Substring(0, 28) + "..." : trimmed;
        string scopePrefix = !string.IsNullOrEmpty(cleanScope) ? $"[{cleanScope}] " : string.Empty;
        profile.RegexRules.Add(new RegexMaskingRule
        {
            IsEnabled = true,
            IsUserRule = true,
            ProductFilter = cleanScope,
            Name = customRuleName ?? $"{scopePrefix}Ignorer : \"{previewLabel}\"",
            Pattern = pattern,
            ReplacementMask = string.Empty,
            IgnoreCase = true,
            MinOccurrences = 1
        });
    }

    internal static void AddQuickIgnoreDiffBlockRuleOnProfile(ComparisonProfile profile, DiffSummaryBlock block, string? productFilter = null)
    {
        if (block == null) return;

        string oldRaw = (block.OldText ?? string.Empty).Trim();
        string newRaw = (block.NewText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(oldRaw) && string.IsNullOrWhiteSpace(newRaw)) return;

        string signature = ProductDifferenceGroupingService.GetBlockSignature(block);
        string cleanScope = ProductCodeHelper.IsGlobalFilter(productFilter) ? string.Empty : productFilter!.Trim();
        string scopePrefix = !string.IsNullOrEmpty(cleanScope) ? $"[{cleanScope}] " : string.Empty;

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
                finalPattern = oldRaw.Length >= newRaw.Length
                    ? $"(?:{oldPattern}|{newPattern})"
                    : $"(?:{newPattern}|{oldPattern})";
            }

            replacementMask = "[IGNORED_USER]";
            string pOld = oldRaw.Length > 18 ? oldRaw.Substring(0, 18) + "…" : oldRaw;
            string pNew = newRaw.Length > 18 ? newRaw.Substring(0, 18) + "…" : newRaw;
            ruleLabel = $"{scopePrefix}Ignorer : \"{pOld}\" ➔ \"{pNew}\"";
        }
        else
        {
            finalPattern = !string.IsNullOrWhiteSpace(newPattern) ? newPattern : oldPattern;
            replacementMask = string.Empty;
            string singleTxt = !string.IsNullOrWhiteSpace(newRaw) ? newRaw : oldRaw;
            string preview = singleTxt.Length > 32 ? singleTxt.Substring(0, 32) + "…" : singleTxt;
            ruleLabel = $"{scopePrefix}Ignorer : \"{preview}\"";
        }

        var existing = profile.RegexRules.FirstOrDefault(r =>
            string.Equals((r.ProductFilter ?? string.Empty).Trim(), cleanScope, StringComparison.OrdinalIgnoreCase) &&
            ((!string.IsNullOrWhiteSpace(signature) && string.Equals(r.IgnoredDiffSignature, signature, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(finalPattern) && string.Equals(r.Pattern, finalPattern, StringComparison.OrdinalIgnoreCase))));

        if (existing != null)
        {
            existing.IsEnabled = true;
            existing.IsUserRule = true;
            existing.Pattern = finalPattern;
            existing.ReplacementMask = replacementMask;
            existing.IgnoredDiffSignature = signature;
            return;
        }

        profile.RegexRules.Add(new RegexMaskingRule
        {
            IsEnabled = true,
            IsUserRule = true,
            ProductFilter = cleanScope,
            Name = ruleLabel,
            Pattern = finalPattern,
            ReplacementMask = replacementMask,
            IgnoreCase = true,
            MinOccurrences = 1,
            IgnoredDiffSignature = signature
        });
    }

    internal static ExclusionZoneRule? AddQuickIgnoreZoneRuleOnProfile(ComparisonProfile profile, DiffSummaryBlock block, string? productFilter = null)
    {
        if (profile == null || block == null || !block.HasZoneRuleSupport) return null;

        string effectiveScope = !ProductCodeHelper.IsGlobalFilter(productFilter)
            ? productFilter!.Trim()
            : (!ProductCodeHelper.IsGlobalFilter(block.ProductCode) ? block.ProductCode.Trim() : string.Empty);

        bool useField = block.FieldZoneRight > block.FieldZoneLeft && block.FieldZoneTop > block.FieldZoneBottom;
        double left = useField ? block.FieldZoneLeft : Math.Max(0, block.PdfLeft - 4.0);
        double bottom = useField ? block.FieldZoneBottom : Math.Max(0, block.PdfBottom - 2.5);
        double right = useField ? block.FieldZoneRight : block.PdfRight + 12.0;
        double top = useField ? block.FieldZoneTop : block.PdfTop + 2.5;

        double width = Math.Max(8.0, right - left);
        double height = Math.Max(6.0, top - bottom);
        int pageNum = Math.Max(1, block.PageNumber);
        double pageW = block.PdfPageWidth > 0 ? block.PdfPageWidth : 595.0;
        double pageH = block.PdfPageHeight > 0 ? block.PdfPageHeight : 842.0;

        // Vérifier s'il existe déjà une zone utilisateur pour le même produit sur la même page qui chevauche cette zone
        var overlapping = profile.ExclusionZones.FirstOrDefault(z =>
        {
            if (!z.IsUserZone) return false;
            if (!string.Equals((z.ProductFilter ?? string.Empty).Trim(), effectiveScope, StringComparison.OrdinalIgnoreCase))
                return false;

            bool sameSinglePage =
                (pageNum == 1 && z.Scope == PageScope.FirstPageOnly) ||
                (z.Scope == PageScope.CustomRange && z.CustomPageStart == pageNum && z.CustomPageEnd == pageNum);
            if (!sameSinglePage) return false;

            var (zL, zB, zR, zT) = z.ToPdfBounds(pageW, pageH);
            bool overlapY = Math.Min(top, zT) + 2.0 >= Math.Max(bottom, zB) &&
                            Math.Abs(((bottom + top) * 0.5) - ((zB + zT) * 0.5)) <= 16.0;
            bool overlapX = Math.Min(right, zR) + 10.0 >= Math.Max(left, zL);
            return overlapX && overlapY;
        });

        if (overlapping != null)
        {
            var (zL, zB, zR, zT) = overlapping.ToPdfBounds(pageW, pageH);
            double uLeft = Math.Min(left, zL);
            double uBottom = Math.Min(bottom, zB);
            double uRight = Math.Max(right, zR);
            double uTop = Math.Max(top, zT);

            overlapping.IsEnabled = true;
            overlapping.Unit = CoordinateUnit.PdfPoints;
            overlapping.Origin = ZoneOrigin.BottomLeft;
            overlapping.X = Math.Round(uLeft, 1);
            overlapping.Y = Math.Round(uBottom, 1);
            overlapping.Width = Math.Round(Math.Max(8.0, uRight - uLeft), 1);
            overlapping.Height = Math.Round(Math.Max(6.0, uTop - uBottom), 1);
            return overlapping;
        }

        string hint = !string.IsNullOrWhiteSpace(block.ContextBefore)
            ? block.ContextBefore.Trim()
            : (!string.IsNullOrWhiteSpace(block.NewText) ? block.NewText.Trim() : block.OldText.Trim());
        hint = hint.Replace("\r\n", " ").Replace("\n", " ");
        if (hint.Length > 22) hint = hint[..22].Trim() + "…";
        if (string.IsNullOrWhiteSpace(hint)) hint = $"X={Math.Round(left)} Y={Math.Round(bottom)}";

        string zoneName = !string.IsNullOrEmpty(effectiveScope)
            ? $"[{effectiveScope}] Zone P.{pageNum} ({hint})"
            : $"Zone P.{pageNum} ({hint})";

        var created = new ExclusionZoneRule
        {
            Name = zoneName,
            IsEnabled = true,
            IsUserZone = true,
            ProductFilter = effectiveScope,
            Scope = pageNum == 1 ? PageScope.FirstPageOnly : PageScope.CustomRange,
            CustomPageStart = pageNum,
            CustomPageEnd = pageNum,
            Unit = CoordinateUnit.PdfPoints,
            Origin = ZoneOrigin.BottomLeft,
            X = Math.Round(left, 1),
            Y = Math.Round(bottom, 1),
            Width = Math.Round(width, 1),
            Height = Math.Round(height, 1),
            CollectDatesInZone = false
        };

        profile.ExclusionZones.Add(created);
        return created;
    }

    private static bool IsCustomUserZone(ExclusionZoneRule? z)
    {
        if (z == null) return false;
        if (z.IsUserZone || !z.IsGlobalRule) return true;
        if (z.Name == null) return false;

        bool isDefaultHeader = z.Origin == ZoneOrigin.TopLeft && z.X == 0 && z.Width >= 500 && z.Height >= 100 && z.Scope == PageScope.AllPages;
        bool isDefaultFooter = z.Origin == ZoneOrigin.BottomLeft && z.X == 0 && z.Width >= 500 && z.Height <= 60 && z.Scope == PageScope.AllPages;
        bool isDefaultLeftMargin = z.Origin == ZoneOrigin.TopLeft && z.X == 0 && z.Width <= 60 && z.Height >= 500 && z.Scope == PageScope.AllPages;
        return !(isDefaultHeader || isDefaultFooter || isDefaultLeftMargin);
    }

    public static string BuildFlexiblePatternFromDiffSide(string? rawText)
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
               !r.IsGlobalRule ||
               !string.IsNullOrWhiteSpace(r.IgnoredDiffSignature) ||
               string.Equals(r.ReplacementMask, "[IGNORED_USER]", StringComparison.Ordinal) ||
               string.Equals(r.ReplacementMask, "[EQUI_IGNORE]", StringComparison.Ordinal) ||
               (r.Name != null && (
                   r.Name.StartsWith("Exclusion rapide", StringComparison.OrdinalIgnoreCase) ||
                   r.Name.StartsWith("Ignorer :", StringComparison.OrdinalIgnoreCase) ||
                   r.Name.StartsWith("Équivalence :", StringComparison.OrdinalIgnoreCase) ||
                   r.Name.StartsWith("Règle ", StringComparison.OrdinalIgnoreCase) ||
                   r.Name.StartsWith("[", StringComparison.Ordinal)));
    }

    public void ExportProfile(string filePath)
    {
        SaveJsonAtomic(ActiveProfile, filePath);
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
                errorMessage = "Le fichier JSON ne contient pas un profil de règles valide.";
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

    public void ExportScanProfile(AutomatedScanProfile scanProfile, string filePath)
    {
        SaveJsonAtomic(scanProfile, filePath);
    }

    public bool ImportScanProfile(string filePath, out AutomatedScanProfile? imported, out string errorMessage)
    {
        imported = null;
        errorMessage = string.Empty;
        try
        {
            string json = File.ReadAllText(filePath);
            var parsed = JsonSerializer.Deserialize<AutomatedScanProfile>(json, JsonOptions);
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.ButtonName))
            {
                errorMessage = "Le fichier JSON ne correspond pas à un bouton de scan valide.";
                return false;
            }

            parsed.Id = Guid.NewGuid();
            parsed.IsBuiltInTides = false;
            parsed.DedicatedProfile ??= ComparisonProfile.CreateDefault();
            UpgradeLegacySavedProfile(parsed.DedicatedProfile, forceDefaultName: false);

            imported = parsed;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public IConfigurationService CreateScopedServiceForProfile(ComparisonProfile targetProfile)
    {
        return new ScopedConfigurationService(this, targetProfile);
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

    internal static void SaveJsonAtomic<T>(T data, string targetPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(data, JsonOptions);
            string tempPath = targetPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving JSON to {targetPath}: {ex.Message}");
        }
    }

    internal static void CopyProfileValues(ComparisonProfile source, ComparisonProfile destination)
    {
        destination.ProfileName = source.ProfileName;

        destination.ExclusionZones.Clear();
        foreach (var zone in source.ExclusionZones)
        {
            destination.ExclusionZones.Add(zone.Clone());
        }

        destination.RegexRules.Clear();
        foreach (var rule in source.RegexRules)
        {
            destination.RegexRules.Add(rule.Clone());
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

    private sealed class ScopedConfigurationService : IConfigurationService
    {
        private readonly ConfigurationService _parent;
        private readonly ComparisonProfile _target;

        public ScopedConfigurationService(ConfigurationService parent, ComparisonProfile target)
        {
            _parent = parent;
            _target = target;
        }

        public ComparisonProfile ActiveProfile => _target;
        public ComparisonProfile GeneralProfile => _parent.GeneralProfile;
        public AppUserSettings UserSettings => _parent.UserSettings;

        public void SaveActiveProfile() => _parent.SaveActiveProfile();
        public void SaveUserSettings() => _parent.SaveUserSettings();

        public void ResetToDefault()
        {
            var fresh = ComparisonProfile.CreateDefault();
            fresh.ProfileName = _target.ProfileName;
            CopyProfileValues(fresh, _target);
            _parent.SaveActiveProfile();
        }

        public void UseGeneralProfile() => _parent.UseGeneralProfile();

        public void ActivateScanProfile(AutomatedScanProfile scanProfile, bool ignoreRepeatingDates, bool ignoreHeaderFooterZones)
            => _parent.ActivateScanProfile(scanProfile, ignoreRepeatingDates, ignoreHeaderFooterZones);

        public void ApplyTidesTestingProfile(bool ignoreRepeatingDates, bool ignoreHeaderFooterZones)
        {
            ApplyFlagsToProfile(_target, ignoreRepeatingDates, ignoreHeaderFooterZones, forceDefaultName: false);
            _parent.SaveActiveProfile();
        }

        public void AddQuickIgnoreRegexRule(string rawTextToIgnore, string? customRuleName = null, string? productFilter = null)
        {
            AddQuickIgnoreRegexRuleOnProfile(_target, rawTextToIgnore, customRuleName, productFilter);
            _parent.SaveActiveProfile();
        }

        public RegexMaskingRule? AddQuickEquivalenceRule(string textA, string? textB, string? customRuleName = null, string? productFilter = null)
        {
            var created = AddQuickEquivalenceRuleOnProfile(_target, textA, textB, customRuleName, productFilter);
            if (created != null)
            {
                _parent.SaveActiveProfile();
            }
            return created;
        }

        public void AddQuickIgnoreDiffBlockRule(DiffSummaryBlock block, string? productFilter = null)
        {
            AddQuickIgnoreDiffBlockRuleOnProfile(_target, block, productFilter);
            _parent.SaveActiveProfile();
        }

        public ExclusionZoneRule? AddQuickIgnoreZoneRule(DiffSummaryBlock block, string? productFilter = null)
        {
            var created = AddQuickIgnoreZoneRuleOnProfile(_target, block, productFilter);
            if (created != null)
            {
                _parent.SaveActiveProfile();
            }
            return created;
        }

        public void SyncActiveToGeneralIfNeeded() => _parent.SaveActiveProfile();

        public void ExportProfile(string filePath) => SaveJsonAtomic(_target, filePath);

        public bool ImportProfile(string filePath, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                string json = File.ReadAllText(filePath);
                var imported = JsonSerializer.Deserialize<ComparisonProfile>(json, JsonOptions);
                if (imported == null)
                {
                    errorMessage = "Le fichier JSON ne contient pas un profil de règles valide.";
                    return false;
                }

                CopyProfileValues(imported, _target);
                _parent.SaveActiveProfile();
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public void ExportScanProfile(AutomatedScanProfile scanProfile, string filePath) => _parent.ExportScanProfile(scanProfile, filePath);
        public bool ImportScanProfile(string filePath, out AutomatedScanProfile? imported, out string errorMessage)
            => _parent.ImportScanProfile(filePath, out imported, out errorMessage);

        public IConfigurationService CreateScopedServiceForProfile(ComparisonProfile targetProfile)
            => _parent.CreateScopedServiceForProfile(targetProfile);

        public Regex? TryGetCompiledRegex(string pattern, bool ignoreCase = true)
            => _parent.TryGetCompiledRegex(pattern, ignoreCase);
    }
}
