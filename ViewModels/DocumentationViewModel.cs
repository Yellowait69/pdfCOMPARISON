using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    private string _markdownContent = "Sélectionnez un document dans le menu de gauche pour lire sa description.";

    public ICollectionView GroupedDocuments { get; private set; }

    public DocumentationViewModel()
    {
        LoadDocuments();
        GroupedDocuments = CollectionViewSource.GetDefaultView(Documents);
        GroupedDocuments.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DocItem.Category)));
    }

    private void LoadDocuments()
    {
        string docsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Docs");

        if (Directory.Exists(docsPath))
        {
            var files = Directory.GetFiles(docsPath, "*.md");

            foreach (var file in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                Documents.Add(new DocItem
                {
                    Title = fileName,
                    FilePath = file,
                    Category = DetermineCategory(fileName)
                });
            }

            if (Documents.Count == 0)
            {
                MarkdownContent = "Le dossier 'Docs' a été trouvé, mais il ne contient aucun fichier Markdown (.md).";
            }
        }
        else
        {
            MarkdownContent = $"**Erreur :** Le dossier de documentation est introuvable.\n\nVeuillez vérifier qu'un dossier `Docs` existe à l'emplacement suivant et que vos fichiers `.md` sont configurés pour être copiés dans le répertoire de sortie : \n\n`{docsPath}`";
        }
    }

    private string DetermineCategory(string fileName)
    {
        if (fileName.EndsWith("Service") || fileName.Contains("Orchestrator") || fileName.Contains("Generator") || fileName.Contains("Helper") || fileName.Contains("Analyzer") || fileName.Contains("Sanitizer") || fileName.Contains("Filter"))
            return "Services (Backend)";

        if (fileName.EndsWith("ViewModel"))
            return "ViewModels (Logique)";

        if (fileName.EndsWith("Window") || fileName.EndsWith("View") || fileName.EndsWith("MainWindow"))
            return "Vues (UI)";

        if (fileName.EndsWith("Converter"))
            return "Convertisseurs (WPF)";

        if (fileName == "DocItem" || fileName == "DocumentPair" || fileName == "CompareStatus" || fileName == "DiffModels" || fileName == "Models")
            return "Modèles (Données)";

        return "Général";
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
                MarkdownContent = $"**Erreur lors de la lecture du fichier :**\n\n{ex.Message}";
            }
        }
    }
}