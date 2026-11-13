namespace PDFComparison.Models;

public class DocItem
{
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "\uE8A5";
    public string FilePath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}
