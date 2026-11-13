using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PDFComparison.Services;
using PDFComparison.ViewModels;

namespace PDFComparison.Views;

public partial class ProfileSettingsWindow : Window
{
    private Point _lassoStartPoint;
    private bool _isDrawingLasso;
    private bool _hasSavedOnClose;

    public ProfileSettingsWindow() : this(new ConfigurationService())
    {
    }

    public ProfileSettingsWindow(
        IConfigurationService configService,
        IEnumerable<string>? knownProducts = null,
        string? initialProductCode = null,
        string? initialSamplePdfPath = null)
    {
        InitializeComponent();
        DataContext = new ProfileSettingsViewModel(configService, knownProducts, initialProductCode, initialSamplePdfPath);
        Closing += OnWindowClosing;
    }

    private void CommitPendingEditsAndSave()
    {
        if (_hasSavedOnClose) return;

        try
        {
            if (Keyboard.FocusedElement is TextBox focusedTb)
            {
                focusedTb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }
            else if (Keyboard.FocusedElement is ComboBox focusedCb)
            {
                focusedCb.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            }

            RegexRulesGrid?.CommitEdit(DataGridEditingUnit.Cell, true);
            RegexRulesGrid?.CommitEdit(DataGridEditingUnit.Row, true);
            ZonesGrid?.CommitEdit(DataGridEditingUnit.Cell, true);
            ZonesGrid?.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch
        {
            // Ignorer si aucune cellule n'était en cours d'édition
        }

        if (DataContext is ProfileSettingsViewModel vm)
        {
            vm.SaveProfileCommand.Execute(null);
        }

        _hasSavedOnClose = true;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        CommitPendingEditsAndSave();
    }

    private void OnQuickInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (sender is TextBox tb)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        if (DataContext is ProfileSettingsViewModel vm)
        {
            vm.CreateQuickRuleCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnLassoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;

        _lassoStartPoint = e.GetPosition(canvas);
        _isDrawingLasso = true;

        Canvas.SetLeft(LassoSelectionBox, _lassoStartPoint.X);
        Canvas.SetTop(LassoSelectionBox, _lassoStartPoint.Y);
        LassoSelectionBox.Width = 0;
        LassoSelectionBox.Height = 0;
        LassoSelectionBox.Visibility = Visibility.Visible;

        canvas.CaptureMouse();
    }

    private void OnLassoMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDrawingLasso || sender is not Canvas canvas) return;

        Point currentPos = e.GetPosition(canvas);
        double clampedX = Math.Clamp(currentPos.X, 0, canvas.ActualWidth);
        double clampedY = Math.Clamp(currentPos.Y, 0, canvas.ActualHeight);

        double left = Math.Min(_lassoStartPoint.X, clampedX);
        double top = Math.Min(_lassoStartPoint.Y, clampedY);
        double width = Math.Abs(clampedX - _lassoStartPoint.X);
        double height = Math.Abs(clampedY - _lassoStartPoint.Y);

        Canvas.SetLeft(LassoSelectionBox, left);
        Canvas.SetTop(LassoSelectionBox, top);
        LassoSelectionBox.Width = width;
        LassoSelectionBox.Height = height;
    }

    private void OnLassoMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawingLasso || sender is not Canvas canvas) return;

        _isDrawingLasso = false;
        canvas.ReleaseMouseCapture();
        LassoSelectionBox.Visibility = Visibility.Collapsed;

        double left = Canvas.GetLeft(LassoSelectionBox);
        double top = Canvas.GetTop(LassoSelectionBox);
        double width = LassoSelectionBox.Width;
        double height = LassoSelectionBox.Height;

        if (width >= 6.0 && height >= 6.0 && DataContext is ProfileSettingsViewModel vm)
        {
            vm.AddZoneFromCanvasLasso(left, top, width, height);
        }
    }

    private void OnSaveAndCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is IInputElement el)
        {
            FocusManager.SetFocusedElement(this, el);
        }

        CommitPendingEditsAndSave();

        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            // Au cas où la fenêtre n'aurait pas été ouverte via ShowDialog()
        }

        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CommitPendingEditsAndSave();
        Close();
    }
}
