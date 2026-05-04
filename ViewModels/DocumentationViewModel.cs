using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.IO;
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

    public DocumentationViewModel()
    {
        LoadDocuments();
    }

    private void LoadDocuments()
    {
        string docsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Docs");

        if (Directory.Exists(docsPath))
        {
            var files = Directory.GetFiles(docsPath, "*.md");

            foreach (var file in files)
            {
                Documents.Add(new DocItem
                {
                    Title = Path.GetFileNameWithoutExtension(file),
                    FilePath = file
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