using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using PdfiumViewer;

namespace PDFComparison.Services;

public interface IPdfImageService
{
    // NOUVEAU : On passe la couleur exacte du surlignage (Vert, Rouge, Orange)
    byte[]? CaptureZone(string pdfPath, int pageNumber, RectangleF exactTextZone, Color highlightColor);
}

public class PdfImageService : IPdfImageService
{
    public byte[]? CaptureZone(string pdfPath, int pageNumber, RectangleF exactTextZone, Color highlightColor)
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

                        // Facteurs d'échelle (ex: 300 DPI vs 72 DPI natif)
                        float scaleX = bitmap.Width / pdfPageSize.Width;
                        float scaleY = bitmap.Height / pdfPageSize.Height;

                        // =================================================================
                        // 1. DESSIN DU SURLIGNEUR DIRECTEMENT SUR L'IMAGE
                        // =================================================================
                        using (Graphics g = Graphics.FromImage(bitmap))
                        {
                            // Inversion de l'Axe Y : PdfPig compte de bas en haut, System.Drawing de haut en bas
                            float topYPdf = pdfPageSize.Height - (exactTextZone.Y + exactTextZone.Height);

                            // Calcul du rectangle exact en pixels (avec une marge de 2 unités PDF pour encadrer joliment le texte)
                            RectangleF pixelHighlight = new RectangleF(
                                (exactTextZone.X - 2f) * scaleX,
                                (topYPdf - 2f) * scaleY,
                                (exactTextZone.Width + 4f) * scaleX,
                                (exactTextZone.Height + 4f) * scaleY
                            );

                            // Remplissage semi-transparent (Alpha à 70 sur 255)
                            using (Brush brush = new SolidBrush(Color.FromArgb(70, highlightColor)))
                            {
                                g.FillRectangle(brush, pixelHighlight);
                            }

                            // Bordure un peu plus prononcée pour délimiter le cadre
                            using (Pen pen = new Pen(Color.FromArgb(200, highlightColor), 3f))
                            {
                                g.DrawRectangle(pen, pixelHighlight.X, pixelHighlight.Y, pixelHighlight.Width, pixelHighlight.Height);
                            }
                        }

                        // =================================================================
                        // 2. ROGNAGE LARGE (CROP) POUR GARDER LE CONTEXTE DE LA PAGE
                        // =================================================================
                        // On prend toute la largeur de la page, et on ajoute 40 points de marge en haut et en bas
                        float cropTopYPdf = pdfPageSize.Height - (exactTextZone.Y + exactTextZone.Height) - 40f;

                        Rectangle cropRect = new Rectangle(
                            0, // On prend toute la largeur (X = 0)
                            (int)(cropTopYPdf * scaleY), // Y calculé avec la marge
                            bitmap.Width, // Largeur totale de l'image
                            (int)((exactTextZone.Height + 80f) * scaleY) // Hauteur ciblée (zone + marges)
                        );

                        // Sécurité (Clamping) pour ne pas sortir des limites de l'image
                        cropRect.X = Math.Max(0, cropRect.X);
                        cropRect.Y = Math.Max(0, cropRect.Y);
                        cropRect.Width = Math.Min(cropRect.Width, bitmap.Width - cropRect.X);
                        cropRect.Height = Math.Min(cropRect.Height, bitmap.Height - cropRect.Y);

                        // Si la zone de rognage est invalide, on abandonne
                        if (cropRect.Width <= 0 || cropRect.Height <= 0)
                            return null;

                        // Création de l'image finale rognée
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
}