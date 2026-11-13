namespace PDFComparison.Services;

/// <summary>
/// UglyToad.PdfPig partage des structures internes de polices et de métriques de glyphes non thread-safe
/// lorsque plusieurs threads appellent PdfDocument.Open / GetWords() simultanément.
/// Ce verrou sérialise uniquement les accès bas niveau PdfPig tandis que le masquage, DiffPlex,
/// l'alignement de mise en page et la génération HTML s'exécutent pleinement en parallèle.
/// </summary>
internal static class PdfPigSync
{
    public static readonly object Gate = new();
}
