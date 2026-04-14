using System;
using System.Collections.Generic;
using System.Text;
using PDFComparison.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PDFComparison.Services;

public class PdfExtractionService
{
    private readonly IPdfWatermarkFilterService _watermarkFilter;
    private readonly IPdfIntelligentMaskingService _intelligentMasking;
    private readonly IPdfTextNormalizerService _textNormalizer;

    public PdfExtractionService(
        IPdfWatermarkFilterService watermarkFilter,
        IPdfIntelligentMaskingService intelligentMasking,
        IPdfTextNormalizerService textNormalizer)
    {
        _watermarkFilter = watermarkFilter ?? throw new ArgumentNullException(nameof(watermarkFilter));
        _intelligentMasking = intelligentMasking ?? throw new ArgumentNullException(nameof(intelligentMasking));
        _textNormalizer = textNormalizer ?? throw new ArgumentNullException(nameof(textNormalizer));
    }

    public string ExtractTextFast(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) throw new ArgumentException("PDF path cannot be empty.", nameof(pdfPath));

        var sb = new StringBuilder();
        using var document = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });

        foreach (var page in document.GetPages())
        {
            var currentLine = new StringBuilder();
            double lastY = -1;
            double lastX = -1;

            double headerThresholdY = page.Height - 130.0;
            double footerThresholdY = 40.0;
            double leftMarginThresholdX = 50.0;

            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                if (IsHiddenOrWhiteWord(word)) continue;

                if (word.BoundingBox.BottomLeft.Y > headerThresholdY) continue;
                if (word.BoundingBox.BottomLeft.Y < footerThresholdY) continue;
                if (word.BoundingBox.BottomLeft.X < leftMarginThresholdX) continue;
                if (_watermarkFilter.IsWatermark(word)) continue;

                if (lastY != -1 && Math.Abs(word.BoundingBox.BottomLeft.Y - lastY) > 5.0)
                {
                    sb.AppendLine(_watermarkFilter.CleanRawText(currentLine.ToString().TrimEnd()));
                    currentLine.Clear();
                    lastX = -1;
                }

                if (lastX != -1)
                {
                    double distance = word.BoundingBox.BottomLeft.X - lastX;

                    if (distance > 2.0)
                    {
                        currentLine.Append(' ');
                    }
                }

                currentLine.Append(word.Text);

                lastX = word.BoundingBox.TopRight.X;
                lastY = word.BoundingBox.BottomLeft.Y;
            }

            if (currentLine.Length > 0)
            {
                sb.AppendLine(_watermarkFilter.CleanRawText(currentLine.ToString().TrimEnd()));
            }
        }

        return _intelligentMasking.MaskRepeatingTextElements(sb.ToString());
    }

    public List<PdfWordInfo> ExtractWords(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) throw new ArgumentException("PDF path cannot be empty.", nameof(pdfPath));

        var words = new List<PdfWordInfo>();
        var headerDatesToIgnore = new HashSet<string>(StringComparer.Ordinal);

        using var doc = PdfDocument.Open(pdfPath, new ParsingOptions { ClipPaths = false });

        foreach (var page in doc.GetPages())
        {
            double headerThresholdY = page.Height - 130.0;
            double footerThresholdY = 40.0;
            double leftMarginThresholdX = 50.0;

            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                if (IsHiddenOrWhiteWord(word)) continue;

                if (word.BoundingBox.BottomLeft.Y > headerThresholdY)
                {
                    if (_intelligentMasking.IsDate(word.Text))
                    {
                        headerDatesToIgnore.Add(word.Text);
                    }
                    continue;
                }

                if (word.BoundingBox.BottomLeft.Y < footerThresholdY) continue;
                if (word.BoundingBox.BottomLeft.X < leftMarginThresholdX) continue;
                if (_watermarkFilter.IsWatermark(word)) continue;

                words.Add(new PdfWordInfo
                {
                    Text = word.Text,
                    Letters = word.Letters,
                    PageNumber = page.Number
                });
            }
        }

        _intelligentMasking.MaskRepeatingWordElements(words, headerDatesToIgnore);

        return words;
    }

    private bool IsHiddenOrWhiteWord(Word word)
    {
        if (word.Letters.Count == 0) return false;

        string text = word.Text.Trim();
        if (text == "." || text == "," || text == "-" || text == "'") return false;

        var firstLetter = word.Letters[0];

        if (firstLetter.PointSize <= 1.0 || firstLetter.GlyphRectangle.Width <= 0.1)
        {
            return true;
        }


        return false;
    }

    public string NormalizePdfText(string input)
    {
        return _textNormalizer.NormalizePdfText(input);
    }
}