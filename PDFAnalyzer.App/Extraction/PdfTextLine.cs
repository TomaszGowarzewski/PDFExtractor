namespace PDFAnalyzer.App.Extraction;

/// <summary>
/// Represents a single word extracted from the PDF with its bounding box.
/// </summary>
public class PdfWord
{
    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double FontSize { get; set; }
    public bool IsBold { get; set; }
}

/// <summary>
/// A horizontal line of words grouped by Y-coordinate proximity.
/// </summary>
public class PdfTextLine
{
    public List<PdfWord> Words { get; set; } = new();
    public double Y => Words.Count > 0 ? Words.Average(w => w.Y) : 0;
    public double MinX => Words.Count > 0 ? Words.Min(w => w.X) : 0;
    public double MaxRight => Words.Count > 0 ? Words.Max(w => w.Right) : 0;
    public double Height => Words.Count > 0 ? Words.Max(w => w.Height) : 0;
    public double AverageFontSize => Words.Count > 0 ? Words.Average(w => w.FontSize) : 0;
    public bool HasBoldWords => Words.Any(w => w.IsBold);

    public string FullText => string.Join(" ", Words.OrderBy(w => w.X).Select(w => w.Text));

    /// <summary>
    /// Get text segments split by large horizontal gaps (potential column boundaries).
    /// </summary>
    public List<TextSegment> GetSegments(double gapThreshold)
    {
        var sorted = Words.OrderBy(w => w.X).ToList();
        var segments = new List<TextSegment>();
        if (sorted.Count == 0) return segments;

        var currentWords = new List<PdfWord> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            double gap = sorted[i].X - sorted[i - 1].Right;
            if (gap > gapThreshold)
            {
                segments.Add(new TextSegment(currentWords));
                currentWords = new List<PdfWord>();
            }
            currentWords.Add(sorted[i]);
        }

        if (currentWords.Count > 0)
            segments.Add(new TextSegment(currentWords));

        return segments;
    }
}

public class TextSegment
{
    public List<PdfWord> Words { get; }
    public double X => Words.Min(w => w.X);
    public double Right => Words.Max(w => w.Right);
    public double Width => Right - X;
    public string Text => string.Join(" ", Words.OrderBy(w => w.X).Select(w => w.Text));

    public TextSegment(List<PdfWord> words)
    {
        Words = words;
    }
}
