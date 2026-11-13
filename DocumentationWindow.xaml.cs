using System.Windows;

namespace PDFComparison.Views;

public partial class DocumentationWindow : Window
{
    public DocumentationWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
