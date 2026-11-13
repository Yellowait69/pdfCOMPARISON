using System.Collections.Generic;
using System.Windows;
using PDFComparison.Models;
using PDFComparison.ViewModels;

namespace PDFComparison.Views;

public partial class DocumentListWindow : Window
{
    public DocumentListWindow(IEnumerable<DocumentPair> pairs, string initialFilter = "ALL")
    {
        InitializeComponent();
        DataContext = new DocumentListViewModel(pairs, initialFilter);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
