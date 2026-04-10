using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using PdfiumViewer;

namespace PDFComparison.Services;

public interface IPdfImageService
{
    byte[]? CaptureZone(string pdfPath, int pageNumber, RectangleF zone);
}

public class PdfImageService : IPdfImageService
{
    public byte[]? CaptureZone(string pdfPath, int pageNumber, RectangleF zone)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            return null;

        try
        {
            using (var document = PdfDocument.Load(pdfPath))
            {
                // PdfiumViewer utilise un index de page commençant à 0
                int pdfiumPageIndex = pageNumber - 1;

                if (pdfiumPageIndex < 0 || pdfiumPageIndex >= document.PageCount)
                    return null;

                // Rendu en 300 DPI pour avoir un texte net et lisible
                using (var image = document.Render(pdfiumPageIndex, 300, 300, true))
                {
                    using (var bitmap = new Bitmap(image))
                    {
                        var pdfPageSize = document.PageSizes[pdfiumPageIndex];

                        // Calcul des coordonnées exactes de découpe
                        Rectangle cropRect = CalculatePixelRect(bitmap, zone, pdfPageSize);

                        // Si la zone est invalide ou vide, on abandonne
                        if (cropRect.Width <= 0 || cropRect.Height <= 0)
                            return null;

                        // Rognage (Crop) de l'image
                        using (Bitmap cropped = bitmap.Clone(cropRect, bitmap.PixelFormat))
                        using (var ms = new MemoryStream())
                        {
                            cropped.Save(ms, ImageFormat.Png);
                            return ms.ToArray();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erreur lors de la capture d'image : {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Convertit les coordonnées vectorielles du PDF (PdfPig) en coordonnées de pixels pour l'image.
    /// </summary>
    private Rectangle CalculatePixelRect(Bitmap bitmap, RectangleF zone, SizeF pdfPageSize)
    {
        // 1. Facteur de mise à l'échelle (ex: image 300 DPI vs PDF standard 72 DPI)
        float scaleX = bitmap.Width / pdfPageSize.Width;
        float scaleY = bitmap.Height / pdfPageSize.Height;

        // 2. Conversion des largeurs et hauteurs en pixels
        int width = (int)(zone.Width * scaleX);
        int height = (int)(zone.Height * scaleY);
        int x = (int)(zone.X * scaleX);

        // 3. INVERSION DE L'AXE Y CRITIQUE
        // UglyToad.PdfPig (origine zone.Y) est en BAS de la page.
        // System.Drawing (origine image) est en HAUT de la page.
        float topYInPdf = pdfPageSize.Height - (zone.Y + zone.Height);
        int y = (int)(topYInPdf * scaleY);

        // 4. Marge de sécurité (Padding) pour inclure un peu de contexte autour du texte modifié
        // Environ 50 pixels de chaque côté
        int paddingX = 50;
        int paddingY = 30;

        x -= paddingX;
        y -= paddingY;
        width += paddingX * 2;
        height += paddingY * 2;

        // 5. CLAMPING (Sécurité pour ne pas sortir des limites de l'image)
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        width = Math.Min(width, bitmap.Width - x);
        height = Math.Min(height, bitmap.Height - y);

        return new Rectangle(x, y, width, height);
    }
}