using System.Text.RegularExpressions;

namespace PDFAnalyzer.App.Extraction;

/// <summary>
/// Generically detects header and footer lines across a multi-page PDF.
///
/// Strategy:
///   1. From each page, take the top N and bottom N lines by Y position.
///   2. Normalize them (remove page numbers, whitespace).
///   3. Text that appears on 50%+ of pages in the same vertical zone = header/footer.
///   4. Also detect page-number patterns ("Strona X", "Page X", "X / Y", "- X -").
/// </summary>
public class HeaderFooterDetector
{
    // How many lines from top/bottom to consider as candidates
    private const int CandidateLineCount = 3;

    // Minimum percentage of pages a line must appear on to be classified as header/footer
    private const double MinPageRatio = 0.40;

    private readonly HashSet<string> _fingerprints = new();

    /// <summary>
    /// Analyze all pages to build the set of header/footer fingerprints.
    /// Call this once before processing individual pages.
    /// </summary>
    public void Analyze(List<List<PdfTextLine>> allPageLines)
    {
        if (allPageLines.Count < 2)
            return; // Can't detect repeating patterns with < 2 pages

        int pageCount = allPageLines.Count;

        // Collect candidate fingerprints from top and bottom of each page
        var topCandidates = new Dictionary<string, int>();  // fingerprint → page count
        var bottomCandidates = new Dictionary<string, int>();

        foreach (var pageLines in allPageLines)
        {
            if (pageLines.Count == 0) continue;

            // Top lines (highest Y in PDF coordinates)
            var topLines = pageLines
                .OrderByDescending(l => l.Y)
                .Take(CandidateLineCount);

            foreach (var line in topLines)
            {
                string fp = Fingerprint(line.FullText);
                if (!string.IsNullOrWhiteSpace(fp))
                    topCandidates[fp] = topCandidates.GetValueOrDefault(fp) + 1;
            }

            // Bottom lines (lowest Y)
            var bottomLines = pageLines
                .OrderBy(l => l.Y)
                .Take(CandidateLineCount);

            foreach (var line in bottomLines)
            {
                string fp = Fingerprint(line.FullText);
                if (!string.IsNullOrWhiteSpace(fp))
                    bottomCandidates[fp] = bottomCandidates.GetValueOrDefault(fp) + 1;
            }
        }

        // Lines appearing on 40%+ of pages are header/footer
        double threshold = pageCount * MinPageRatio;

        foreach (var (fp, count) in topCandidates)
        {
            if (count >= threshold)
                _fingerprints.Add(fp);
        }

        foreach (var (fp, count) in bottomCandidates)
        {
            if (count >= threshold)
                _fingerprints.Add(fp);
        }
    }

    /// <summary>
    /// Check if a specific line is a header or footer.
    /// </summary>
    public bool IsHeaderFooter(PdfTextLine line)
    {
        string text = line.FullText.Trim();

        // Check against learned fingerprints
        string fp = Fingerprint(text);
        if (!string.IsNullOrWhiteSpace(fp) && _fingerprints.Contains(fp))
            return true;

        // Also catch standalone page number lines that might not repeat exactly
        if (IsPageNumberLine(text))
            return true;

        return false;
    }

    /// <summary>
    /// Create a normalized fingerprint of a line for cross-page comparison.
    /// Removes page numbers and normalizes whitespace so "Page 1" and "Page 2" match.
    /// </summary>
    private static string Fingerprint(string text)
    {
        string normalized = text.Trim();

        // Replace page numbers with a placeholder
        // Patterns: "Strona 1", "Page 12", "str. 3", "- 5 -", "1 / 10", "1/10"
        normalized = Regex.Replace(normalized, @"\b[Ss]tron[ay]?\s*\d+\b", "##PAGE##");
        normalized = Regex.Replace(normalized, @"\b[Pp]age\s*\d+\b", "##PAGE##");
        normalized = Regex.Replace(normalized, @"\b[Ss]tr\.\s*\d+\b", "##PAGE##");
        normalized = Regex.Replace(normalized, @"\b\d+\s*/\s*\d+\b", "##PAGE##");
        normalized = Regex.Replace(normalized, @"-\s*\d+\s*-", "##PAGE##");

        // Replace any remaining standalone numbers (potential page numbers)
        // but only if the line is short (likely a footer)
        if (normalized.Length < 20)
            normalized = Regex.Replace(normalized, @"\b\d+\b", "##NUM##");

        // Normalize whitespace
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        return normalized;
    }

    /// <summary>
    /// Detect standalone page number lines.
    /// </summary>
    private static bool IsPageNumberLine(string text)
    {
        // "Strona 5", "Page 12", "- 3 -", "5 / 10", just "7"
        if (Regex.IsMatch(text, @"^[Ss]tron[ay]?\s+\d+$")) return true;
        if (Regex.IsMatch(text, @"^[Pp]age\s+\d+$")) return true;
        if (Regex.IsMatch(text, @"^-\s*\d+\s*-$")) return true;
        if (Regex.IsMatch(text, @"^\d+\s*/\s*\d+$")) return true;
        if (Regex.IsMatch(text, @"^\d{1,4}$") && text.Length <= 4) return true;
        return false;
    }
}
