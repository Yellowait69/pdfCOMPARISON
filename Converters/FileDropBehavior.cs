using System.Windows;
using System.Windows.Controls;

namespace PDFComparison.Converters;

/// <summary>
/// Comportement attaché (Attached Behavior) permettant de glisser-déposer (Drag & Drop)
/// directement un dossier ou un fichier PDF depuis l'Explorateur Windows sur un TextBox WPF.
/// </summary>
public static class FileDropBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(FileDropBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox) return;

        if ((bool)e.NewValue)
        {
            textBox.AllowDrop = true;
            textBox.PreviewDragOver += OnPreviewDragOver;
            textBox.PreviewDrop += OnPreviewDrop;
        }
        else
        {
            textBox.PreviewDragOver -= OnPreviewDragOver;
            textBox.PreviewDrop -= OnPreviewDrop;
        }
    }

    private static void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private static void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] droppedPaths &&
            droppedPaths.Length > 0)
        {
            textBox.Text = droppedPaths[0];
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}
