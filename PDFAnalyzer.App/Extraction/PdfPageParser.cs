using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PDFAnalyzer.App.Extraction;

/// <summary>
/// Parses PDF pages into structured PdfTextLine objects.
/// </summary>
public static class PdfPageParser
{
    /// <summary>
    /// Extract words from a PdfPig page and group them into text lines.
    /// </summary>
    public static List<PdfTextLine> ExtractLines(Page page)
    {
        var words = page.GetWords().Select(w => new PdfWord
        {
            Text = w.Text,
            X = w.BoundingBox.Left,
            Y = w.BoundingBox.Bottom,
            Width = w.BoundingBox.Width,
            Height = w.BoundingBox.Height,
            FontSize = w.Letters.Any() ? w.Letters.Average(l => l.FontSize) : 0,
            IsBold = w.Letters.Any() && w.Letters.First().Value is not null
                     && (w.Letters.First().PointSize > 0)
                     && (w.Letters.First().Font?.Name?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true
                         || w.Letters.First().Font?.Name?.Contains("Heavy", StringComparison.OrdinalIgnoreCase) == true)
        }).ToList();

        return GroupIntoLines(words);
    }

    /// <summary>
    /// Group words into lines based on Y-coordinate proximity.
    /// Words within yTolerance of each other are considered same line.
    /// </summary>
    public static List<PdfTextLine> GroupIntoLines(List<PdfWord> words, double yTolerance = 3.0)
    {
        if (words.Count == 0) return new List<PdfTextLine>();

        // Sort by Y descending (PDF coords: bottom = 0, top = max), then by X
        var sorted = words.OrderByDescending(w => w.Y).ThenBy(w => w.X).ToList();

        var lines = new List<PdfTextLine>();
        var currentLine = new PdfTextLine();
        currentLine.Words.Add(sorted[0]);

        for (int i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].Y - currentLine.Words[0].Y) <= yTolerance)
            {
                currentLine.Words.Add(sorted[i]);
            }
            else
            {
                lines.Add(currentLine);
                currentLine = new PdfTextLine();
                currentLine.Words.Add(sorted[i]);
            }
        }

        lines.Add(currentLine);

        // Sort lines top-to-bottom (highest Y first in PDF coords)
        return lines.OrderByDescending(l => l.Y).ToList();
    }
}
