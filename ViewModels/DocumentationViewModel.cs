using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Data;
using PDFComparison.Models;

namespace PDFComparison.ViewModels;

public partial class DocumentationViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<DocItem> _documents = new();

    [ObservableProperty]
    private DocItem? _selectedDocument;

    [ObservableProperty]
    private string _markdownContent = string.Empty;

    public ICollectionView GroupedDocuments { get; }

    public DocumentationViewModel()
    {
        LoadDocuments();
        GroupedDocuments = CollectionViewSource.GetDefaultView(Documents);
        GroupedDocuments.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DocItem.Category)));
        SelectedDocument = Documents.FirstOrDefault();
    }

    private void LoadDocuments()
    {
        string docsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Docs");

        if (!Directory.Exists(docsPath))
        {
            MarkdownContent = "# Guide introuvable\n\nLe dossier `Docs` est absent du répertoire de l'application.";
            return;
        }

        var files = Directory.GetFiles(docsPath, "*.md")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            var (title, subtitle, icon, category) = DescribeGuide(stem, file);
            Documents.Add(new DocItem
            {
                Title = title,
                Subtitle = subtitle,
                IconGlyph = icon,
                FilePath = file,
                Category = category
            });
        }

        if (Documents.Count == 0)
        {
            MarkdownContent = "# Aucun guide disponible\n\nLe dossier `Docs` ne contient aucun fichier `.md`.";
        }
    }

    private static (string Title, string Subtitle, string Icon, string Category) DescribeGuide(string stem, string filePath)
    {
        if (stem.StartsWith("01_", StringComparison.OrdinalIgnoreCase))
            return ("1. Démarrage Rapide", "Dossiers, PDF unitaires & filtres", "\uE768", "PRISE EN MAIN");

        if (stem.StartsWith("02_", StringComparison.OrdinalIgnoreCase))
            return ("2. Équipe Testing & Scans Auto", "Boutons personnalisés & partage", "\uE945", "PRISE EN MAIN");

        if (stem.StartsWith("03_", StringComparison.OrdinalIgnoreCase))
            return ("3. Synthèse & Côte à Côte", "Vérification rapide & PDF fusionnés", "\uE9D2", "ANALYSE DES ÉCARTS");

        if (stem.StartsWith("04_", StringComparison.OrdinalIgnoreCase))
            return ("4. Paramètres, Mail & Règles", "Dossier rapports, mail & exclusions", "\uE713", "PERSONNALISATION");

        // Fallback : extraire le premier titre Markdown '# ...' si présent
        try
        {
            string? firstHeading = File.ReadLines(filePath)
                .FirstOrDefault(l => l.TrimStart().StartsWith("# "));
            if (!string.IsNullOrWhiteSpace(firstHeading))
            {
                return (firstHeading.TrimStart('#', ' ').Trim(), "Guide utilisateur", "\uE8A5", "GUIDES");
            }
        }
        catch
        {
            // Ignorer
        }

        return (stem.Replace('_', ' '), "Guide utilisateur", "\uE8A5", "GUIDES");
    }

    partial void OnSelectedDocumentChanged(DocItem? value)
    {
        if (value != null && File.Exists(value.FilePath))
        {
            try
            {
                MarkdownContent = File.ReadAllText(value.FilePath);
            }
            catch (Exception ex)
            {
                MarkdownContent = $"**Erreur lors de la lecture du guide :**\n\n{ex.Message}";
            }
        }
    }
}
