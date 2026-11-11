using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDFComparison.Models;
using PDFComparison.Services;

namespace PDFComparison.Views;

public partial class VisualDiffViewerWindow : Window
{
    private readonly DocumentPair _pair;
    private readonly IPdfVisualRegressionService _visualService;
    private readonly DispatcherTimer _blinkTimer;

    private int _currentPage = 1;
    private int _totalPages = 1;
    private bool _blinkShowingTarget;

    private static readonly SolidColorBrush ActiveBtnBrush = new(Color.FromRgb(37, 99, 235));
    private static readonly SolidColorBrush InactiveBtnBrush = new(Color.FromRgb(51, 65, 85));

    public VisualDiffViewerWindow(DocumentPair pair, IPdfVisualRegressionService visualService, int initialPage = 1)
    {
        InitializeComponent();

        _pair = pair ?? throw new ArgumentNullException(nameof(pair));
        _visualService = visualService ?? throw new ArgumentNullException(nameof(visualService));
        _currentPage = Math.Max(1, initialPage);

        DocTitleText.Text = $"Document : {_pair.MatchKey}";

        _blinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _blinkTimer.Tick += OnBlinkTimerTick;

        Closed += (_, _) => _blinkTimer.Stop();

        RenderCurrentPage();
    }

    private void RenderCurrentPage()
    {
        if (string.IsNullOrWhiteSpace(_pair.SourcePath) || string.IsNullOrWhiteSpace(_pair.TargetPath))
            return;

        try
        {
            var (srcBytes, trgBytes, heatBytes, pageSsim, totalPages) =
                _visualService.RenderInteractiveInspectionPage(_pair.SourcePath, _pair.TargetPath!, _currentPage);

            _totalPages = Math.Max(1, totalPages);
            _currentPage = Math.Clamp(_currentPage, 1, _totalPages);

            var srcImg = BytesToBitmapImage(srcBytes);
            var trgImg = BytesToBitmapImage(trgBytes);
            var heatImg = BytesToBitmapImage(heatBytes);

            ImgSideSource.Source = srcImg;
            ImgSideTarget.Source = trgImg;
            ImgOverlaySource.Source = srcImg;
            ImgOverlayTarget.Source = trgImg;
            ImgHeatmap.Source = heatImg;

            PageIndicatorText.Text = $"Page {_currentPage} / {_totalPages}";
            SsimScoreBadge.Text = $"SSIM Page : {pageSsim * 100.0:0.0}%";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur lors du rendu visuel de la page {_currentPage} : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static BitmapImage BytesToBitmapImage(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 1)
        {
            _currentPage--;
            RenderCurrentPage();
        }
    }

    private void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        if (_currentPage < _totalPages)
        {
            _currentPage++;
            RenderCurrentPage();
        }
    }

    private void ResetModeButtons()
    {
        _blinkTimer.Stop();
        BtnSideBySide.Background = InactiveBtnBrush;
        BtnOverlay.Background = InactiveBtnBrush;
        BtnBlink.Background = InactiveBtnBrush;
        BtnHeatmap.Background = InactiveBtnBrush;
        OverlayControlsPanel.Visibility = Visibility.Collapsed;
        SideBySideView.Visibility = Visibility.Collapsed;
        OverlayBlinkView.Visibility = Visibility.Collapsed;
        HeatmapView.Visibility = Visibility.Collapsed;
    }

    private void OnModeSideBySideClick(object sender, RoutedEventArgs e)
    {
        ResetModeButtons();
        BtnSideBySide.Background = ActiveBtnBrush;
        SideBySideView.Visibility = Visibility.Visible;
    }

    private void OnModeOverlayClick(object sender, RoutedEventArgs e)
    {
        ResetModeButtons();
        BtnOverlay.Background = ActiveBtnBrush;
        OverlayControlsPanel.Visibility = Visibility.Visible;
        OverlayBlinkView.Visibility = Visibility.Visible;
        ImgOverlayTarget.Opacity = OverlayOpacitySlider.Value;
        OverlayModeLabel.Text = $"SUPERPOSITION CALQUE (OPACITÉ CIBLE : {OverlayOpacitySlider.Value * 100:0}%)";
    }

    private void OnModeBlinkClick(object sender, RoutedEventArgs e)
    {
        ResetModeButtons();
        BtnBlink.Background = ActiveBtnBrush;
        OverlayBlinkView.Visibility = Visibility.Visible;
        _blinkShowingTarget = false;
        ImgOverlayTarget.Opacity = 0.0;
        OverlayModeLabel.Text = "BLINK A/B : ATTENDU (SOURCE)";
        _blinkTimer.Start();
    }

    private void OnModeHeatmapClick(object sender, RoutedEventArgs e)
    {
        ResetModeButtons();
        BtnHeatmap.Background = ActiveBtnBrush;
        HeatmapView.Visibility = Visibility.Visible;
    }

    private void OnBlinkTimerTick(object? sender, EventArgs e)
    {
        _blinkShowingTarget = !_blinkShowingTarget;
        ImgOverlayTarget.Opacity = _blinkShowingTarget ? 1.0 : 0.0;
        OverlayModeLabel.Text = _blinkShowingTarget
            ? "BLINK A/B : OBTENU (CIBLE)"
            : "BLINK A/B : ATTENDU (SOURCE)";
    }

    private void OnOverlayOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImgOverlayTarget != null && !_blinkTimer.IsEnabled)
        {
            ImgOverlayTarget.Opacity = e.NewValue;
            if (OverlayModeLabel != null)
            {
                OverlayModeLabel.Text = $"SUPERPOSITION CALQUE (OPACITÉ CIBLE : {e.NewValue * 100:0}%)";
            }
        }
    }

    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MainScaleTransform != null)
        {
            MainScaleTransform.ScaleX = e.NewValue;
            MainScaleTransform.ScaleY = e.NewValue;
        }
        if (ZoomPercentText != null)
        {
            ZoomPercentText.Text = $"{e.NewValue * 100:0}%";
        }
    }
}
