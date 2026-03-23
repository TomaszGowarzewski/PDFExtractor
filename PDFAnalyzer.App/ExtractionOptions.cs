namespace PDFAnalyzer.App;

/// <summary>
/// User-configurable extraction parameters.
/// Instead of one-size-fits-all, the user picks the strategy matching their PDF.
/// </summary>
public class ExtractionOptions
{
    /// <summary>
    /// How to detect table row boundaries.
    ///
    /// MultiSegment: Each multi-segment text line = new row.
    ///   Best for: bordered tables, simple layouts, tables with short cell text.
    ///   Wrapped text (single-segment lines) is appended to the previous row's cell.
    ///
    /// GapAnalysis: Uses Y-coordinate gap analysis to find natural row breaks.
    ///   Best for: borderless tables, cells with multi-line wrapped text.
    ///   Finds the threshold between intra-cell gaps and inter-row gaps automatically.
    ///
    /// Default: MultiSegment (safest, works for most PDFs).
    /// </summary>
    public RowDetectionMode RowDetection { get; set; } = RowDetectionMode.MultiSegment;

    /// <summary>
    /// Sensitivity for gap-based row detection (only used when RowDetection = GapAnalysis).
    /// Lower = more rows detected (stricter separation).
    /// Higher = fewer rows (more text merged into cells).
    ///
    /// Range: 0.05 - 0.50. Default: 0.15.
    /// Technically: minimum relative jump in sorted gap values to trigger a row break.
    /// </summary>
    public double GapSensitivity { get; set; } = 0.15;

    /// <summary>
    /// Minimum columns a line must fill to be considered a new row (vs wrap).
    /// Only used with GapAnalysis mode.
    /// 0 = auto-detect (colCount / 3).
    /// </summary>
    public int MinColumnsForNewRow { get; set; } = 0;

    /// <summary>
    /// Whether to detect and convert inherited-row tables.
    /// </summary>
    public bool DetectInheritedTables { get; set; } = true;

    /// <summary>
    /// Whether to detect and merge tables spanning multiple pages.
    /// </summary>
    public bool MergeCrossPageTables { get; set; } = true;

    /// <summary>
    /// Whether to detect hierarchical (multi-level) table headers.
    /// </summary>
    public bool DetectHierarchicalHeaders { get; set; } = true;

    /// <summary>
    /// Predefined presets for common document types.
    /// </summary>
    public static ExtractionOptions Default => new();

    public static ExtractionOptions Bordered => new()
    {
        RowDetection = RowDetectionMode.MultiSegment
    };

    public static ExtractionOptions Borderless => new()
    {
        RowDetection = RowDetectionMode.GapAnalysis,
        GapSensitivity = 0.15
    };

    public static ExtractionOptions BorderlessAggressive => new()
    {
        RowDetection = RowDetectionMode.GapAnalysis,
        GapSensitivity = 0.10
    };
}

public enum RowDetectionMode
{
    /// <summary>
    /// Each multi-segment text line = one table row.
    /// Single-segment lines are wrapped cell text.
    /// Safe default for most PDFs.
    /// </summary>
    MultiSegment,

    /// <summary>
    /// Y-gap analysis to detect row boundaries.
    /// Handles multi-line wrapped cells in borderless tables.
    /// </summary>
    GapAnalysis
}
