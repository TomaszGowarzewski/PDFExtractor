using System.Text.RegularExpressions;
using PDFAnalyzer.App.Models;

namespace PDFAnalyzer.App.Extraction;

/// <summary>
/// Detects and classifies document structures (tables, key-value pairs, form grids).
///
/// Strategy:
///   1. Iterate through lines top-to-bottom.
///   2. Section headers break the flow.
///   3. Consecutive multi-segment lines are grouped as table/KV blocks.
///   4. Single-segment text lines are grouped as text blocks.
///   5. Each structured block is classified after collection.
/// </summary>
public class StructureDetector
{
    private readonly double _pageWidth;
    private readonly double _columnGapThreshold;
    private readonly HeaderFooterDetector? _headerFooterDetector;
    private readonly ExtractionOptions _options;

    public StructureDetector(double pageWidth, HeaderFooterDetector? headerFooterDetector = null,
        ExtractionOptions? options = null)
    {
        _pageWidth = pageWidth;
        _columnGapThreshold = pageWidth * 0.03;
        _headerFooterDetector = headerFooterDetector;
        _options = options ?? ExtractionOptions.Default;
    }

    public List<ExtractedElement> DetectStructures(List<PdfTextLine> lines, int pageNumber)
    {
        var filtered = lines.Where(l => !IsHeaderFooter(l)).ToList();
        if (filtered.Count == 0) return new List<ExtractedElement>();

        var annotated = filtered.Select(l => new AnnotatedLine
        {
            Line = l,
            Segments = l.GetSegments(_columnGapThreshold)
        }).ToList();

        return ProcessLines(annotated, pageNumber);
    }

    private List<ExtractedElement> ProcessLines(List<AnnotatedLine> lines, int pageNumber)
    {
        var elements = new List<ExtractedElement>();
        int i = 0;

        while (i < lines.Count)
        {
            // Detect section header
            if (IsSectionHeader(lines[i]))
            {
                string headerText = lines[i].Line.FullText.Trim();

                // Look ahead: if followed by multi-segment lines, attach header and collect block
                if (i + 1 < lines.Count && lines[i + 1].Segments.Count >= 2)
                {
                    i++;
                    var block = CollectStructuredBlock(lines, ref i);
                    var element = ClassifyBlock(block, headerText, pageNumber,
                        lines[Math.Max(0, i - block.Count)].Line.Y);
                    if (element != null) elements.Add(element);
                }
                else
                {
                    // Standalone header text
                    elements.Add(new ExtractedTextBlock
                    {
                        PageNumber = pageNumber,
                        Y = lines[i].Line.Y,
                        Text = headerText
                    });
                    i++;
                }
                continue;
            }

            // Multi-segment line: start of a structured block
            if (lines[i].Segments.Count >= 2)
            {
                double startY = lines[i].Line.Y;
                var block = CollectStructuredBlock(lines, ref i);
                var element = ClassifyBlock(block, null, pageNumber, startY);
                if (element != null) elements.Add(element);
                continue;
            }

            // Single-segment line: text block
            {
                var textLines = new List<string>();
                double startY = lines[i].Line.Y;

                while (i < lines.Count && lines[i].Segments.Count <= 1 && !IsSectionHeader(lines[i]))
                {
                    string text = lines[i].Line.FullText.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                        textLines.Add(text);
                    i++;
                }

                if (textLines.Count > 0)
                {
                    elements.Add(new ExtractedTextBlock
                    {
                        PageNumber = pageNumber,
                        Y = startY,
                        Text = string.Join("\n", textLines)
                    });
                }
            }
        }

        return elements;
    }

    /// <summary>
    /// Collect consecutive multi-segment lines as a structured block.
    /// Also picks up short single-segment lines that are part of the block (cell text wrap).
    /// </summary>
    private List<AnnotatedLine> CollectStructuredBlock(List<AnnotatedLine> lines, ref int i)
    {
        var block = new List<AnnotatedLine>();

        while (i < lines.Count)
        {
            if (lines[i].Segments.Count >= 2)
            {
                block.Add(lines[i]);
                i++;
            }
            else if (lines[i].Segments.Count <= 1)
            {
                if (IsSectionHeader(lines[i]))
                    break;

                // Look ahead: collect consecutive single-segment lines and check if
                // a multi-segment line follows within a reasonable distance.
                // This handles multi-line cell wraps like:
                //   "ASDASD,"   ← single
                //   "ASDASD,"   ← single
                //   "QWEQWEE,"  ← single
                //   "QWEQWE"    ← single
                //   "B" | "dqwes" | "4" | ...  ← multi (table continues!)
                var pendingSingles = new List<AnnotatedLine>();
                int lookahead = i;
                bool foundMultiSeg = false;

                while (lookahead < lines.Count && lines[lookahead].Segments.Count <= 1)
                {
                    if (IsSectionHeader(lines[lookahead]))
                        break;

                    string text = lines[lookahead].Line.FullText.Trim();
                    if (text.Length > 80) // too long for a wrapped cell
                        break;

                    pendingSingles.Add(lines[lookahead]);
                    lookahead++;

                    // Don't look too far ahead (max ~10 lines of wrapping)
                    if (pendingSingles.Count > 10)
                        break;
                }

                if (lookahead < lines.Count && lines[lookahead].Segments.Count >= 2)
                    foundMultiSeg = true;

                if (foundMultiSeg && block.Count > 0)
                {
                    // Include all pending single-segment lines as wrapped text
                    block.AddRange(pendingSingles);
                    i = lookahead;
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        return block;
    }

    /// <summary>
    /// Classify a collected block of mostly multi-segment lines.
    /// </summary>
    private ExtractedElement? ClassifyBlock(List<AnnotatedLine> block, string? sectionName,
        int pageNumber, double y)
    {
        if (block.Count == 0) return null;

        var multiSegLines = block.Where(b => b.Segments.Count >= 2).ToList();

        // Check for form grid
        if (IsFormGridBlock(block))
            return BuildFormGrid(block, sectionName, pageNumber, y);

        // Check for key-value
        if (IsKeyValueBlock(multiSegLines))
            return BuildKeyValueGroup(block, sectionName, pageNumber, y);

        // Default: table
        return BuildTable(block, sectionName, pageNumber, y);
    }

    #region Block Classification

    private bool IsFormGridBlock(List<AnnotatedLine> block)
    {
        int numberedFieldCount = 0;
        int numberedAtStartCount = 0;

        foreach (var line in block)
        {
            // Check each segment: numbered fields at the START of segments indicate form grid.
            // Numbered fields that ARE the entire segment (just "68.") indicate table data.
            foreach (var seg in line.Segments)
            {
                string text = seg.Text.Trim();
                var matches = Regex.Matches(text, @"(?<![0-9.])\b(\d{1,3})\.\s+(?!\d{2}[./])");
                numberedFieldCount += matches.Count;

                // Check if numbered field is at start with a label after it (real form field)
                // vs just a standalone number like "68." (table column data)
                if (matches.Count > 0)
                {
                    var firstMatch = matches[0];
                    bool hasLabelText = text.Length > firstMatch.Length + 2;
                    bool isFirstSegment = seg.X == line.Segments.Min(s => s.X);

                    if (hasLabelText || isFirstSegment)
                        numberedAtStartCount += matches.Count;
                }
            }
        }

        // Need enough numbered fields and most should be real form fields (with labels),
        // not just standalone numbers in table cells
        return numberedFieldCount >= 4 && numberedAtStartCount >= 3;
    }

    private bool IsKeyValueBlock(List<AnnotatedLine> multiSegLines)
    {
        if (multiSegLines.Count == 0) return false;

        int twoSegCount = multiSegLines.Count(b => b.Segments.Count == 2);
        int colonCount = multiSegLines.Count(b =>
            b.Segments[0].Text.TrimEnd().EndsWith(":") ||
            b.Segments[0].Text.TrimEnd().EndsWith(";"));

        double twoSegRatio = (double)twoSegCount / multiSegLines.Count;
        double colonRatio = (double)colonCount / multiSegLines.Count;

        // KV if mostly 2-segment lines with colon pattern
        return twoSegRatio >= 0.5 && colonRatio >= 0.35;
    }

    private bool IsSectionHeader(AnnotatedLine line)
    {
        if (line.Segments.Count != 1) return false;
        string text = line.Line.FullText.Trim();

        // "1. STRONY UMOWY", "5.3 Wymagania", "1.1 Zamawiajacy"
        if (Regex.IsMatch(text, @"^\d+(\.\d+)?\s+\S") && text.Length < 100)
            return true;

        // Bold standalone headers
        if (line.Line.HasBoldWords && text.Length < 80 && !text.Contains('.') == false)
            return true;

        // Letter-section headers: "A. MIEJSCE", "B.1. DANE"
        if (Regex.IsMatch(text, @"^[A-Z](\.\d+)?\.\s+"))
            return true;

        return false;
    }

    #endregion

    #region Building Elements

    private ExtractedKeyValueGroup BuildKeyValueGroup(List<AnnotatedLine> block, string? sectionName,
        int pageNumber, double y)
    {
        var kv = new ExtractedKeyValueGroup
        {
            PageNumber = pageNumber,
            Y = y,
            SectionName = sectionName
        };

        foreach (var line in block)
        {
            if (line.Segments.Count >= 2)
            {
                string key = line.Segments[0].Text.TrimEnd(':', ' ', ';');
                string value = string.Join(" ", line.Segments.Skip(1).Select(s => s.Text)).Trim();
                kv.Items.Add(new KeyValueItem { Key = key, Value = value });
            }
            else if (line.Segments.Count == 1 && kv.Items.Count > 0)
            {
                // Wrapped text: append to last item's value
                var last = kv.Items.Last();
                string text = line.Line.FullText.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    last.Value = (last.Value + " " + text).Trim();
            }
        }

        return kv;
    }

    /// <summary>
    /// Build table using Y-gap analysis for row detection (handles word-wrapped cells).
    ///
    /// Key insight from research: Y-gap distribution in tables with wrapped cells
    /// is BIMODAL - small gaps within cells, large gaps between rows.
    /// Finding the "largest jump" in sorted gaps gives the natural threshold.
    ///
    /// Two modes controlled by ExtractionOptions.RowDetection:
    ///   MultiSegment: Each multi-seg line = row. Simple, safe, works for bordered tables.
    ///   GapAnalysis:  Y-gap analysis for borderless tables with wrapped cells.
    /// </summary>
    private ExtractedTable BuildTable(List<AnnotatedLine> block, string? sectionName,
        int pageNumber, double y)
    {
        if (_options.RowDetection == RowDetectionMode.MultiSegment)
            return BuildTableMultiSeg(block, pageNumber, y);
        else
            return BuildTableGapAnalysis(block, pageNumber, y);
    }

    /// <summary>
    /// MultiSegment mode: each multi-segment line = one row.
    /// Single-segment lines = wrapped cell text, appended to previous row by X-column.
    /// </summary>
    private ExtractedTable BuildTableMultiSeg(List<AnnotatedLine> block, int pageNumber, double y)
    {
        var table = new ExtractedTable { PageNumber = pageNumber, Y = y };

        var multiSegLines = block.Where(b => b.Segments.Count >= 2).ToList();
        if (multiSegLines.Count == 0) return table;

        var columns = DetectColumns(multiSegLines);
        if (columns.Count < 2) return table;

        // Build rows: multi-seg = new row, single-seg = append to column
        var rows = new List<List<string>>();
        List<string>? currentRow = null;

        foreach (var line in block)
        {
            if (line.Segments.Count >= 2)
            {
                currentRow = AssignToColumns(line.Segments, columns);
                rows.Add(currentRow);
            }
            else if (line.Segments.Count == 1 && currentRow != null)
            {
                string text = line.Line.FullText.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                int colIdx = FindNearestColumn(line.Segments[0].X, columns);
                if (colIdx < currentRow.Count)
                {
                    if (string.IsNullOrWhiteSpace(currentRow[colIdx]))
                        currentRow[colIdx] = text;
                    else
                        currentRow[colIdx] += "\n" + text;
                }
            }
        }

        if (rows.Count == 0) return table;
        table.Headers = rows[0];
        for (int i = 1; i < rows.Count; i++)
            table.Rows.Add(rows[i]);

        return table;
    }

    /// <summary>
    /// GapAnalysis mode: Y-gap analysis for row boundaries.
    /// Combined with column-occupancy signal for robust wrap vs new-row detection.
    /// </summary>
    private ExtractedTable BuildTableGapAnalysis(List<AnnotatedLine> block, int pageNumber, double y)
    {
        var table = new ExtractedTable
        {
            PageNumber = pageNumber,
            Y = y
        };

        var multiSegLines = block.Where(b => b.Segments.Count >= 2).ToList();
        if (multiSegLines.Count == 0) return table;

        var columns = DetectColumns(multiSegLines);
        if (columns.Count < 2) return table;

        // Step 1: Build text lines - assign every segment to a column
        var textLines = new List<(double Y, Dictionary<int, string> Cells)>();

        foreach (var line in block)
        {
            var cells = new Dictionary<int, string>();
            foreach (var seg in line.Segments)
            {
                int col = FindNearestColumn(seg.X, columns);
                if (cells.ContainsKey(col))
                    cells[col] += " " + seg.Text;
                else
                    cells[col] = seg.Text;
            }
            if (cells.Count > 0)
                textLines.Add((line.Line.Y, cells));
        }

        if (textLines.Count == 0) return table;

        // Step 2: Compute Y-gaps between consecutive lines
        var gaps = new List<double>();
        for (int i = 1; i < textLines.Count; i++)
        {
            double gap = Math.Abs(textLines[i - 1].Y - textLines[i].Y);
            if (gap > 0.5)
                gaps.Add(gap);
        }

        // Step 3: Find row threshold.
        // The gap distribution is typically: ~11pt (intra-cell wrap) and ~15pt (inter-row).
        // We need to find the boundary between these two modes.
        //
        // Strategy: find the first significant jump in sorted gaps.
        // "Significant" = a jump that's >= 20% of the gap value (relative jump).
        // This catches 11→14.7 (34% jump) but not 14.7→14.8 (0.7% jump).
        double rowThreshold = 0;

        if (gaps.Count >= 3)
        {
            var sortedGaps = gaps.OrderBy(g => g).Distinct().ToList();

            if (sortedGaps.Count >= 2)
            {
                // Find the first significant relative jump in the lower portion of gaps
                for (int i = 0; i < sortedGaps.Count - 1; i++)
                {
                    double relJump = (sortedGaps[i + 1] - sortedGaps[i]) / sortedGaps[i];
                    if (relJump >= _options.GapSensitivity)
                    {
                        rowThreshold = (sortedGaps[i] + sortedGaps[i + 1]) / 2.0;
                        break;
                    }
                }
            }
        }

        // Step 4: Group text lines into logical rows.
        // Combined signals:
        //   - Gap > threshold → candidate for new row
        //   - Column occupancy: a TRUE new row fills MANY columns (3+).
        //     A wrap continuation fills only 1-2 columns.
        //   - If gap > threshold AND line has 3+ columns filled → new row.
        //   - If gap > threshold BUT line has ≤2 columns → wrap, merge.
        //   - If gap <= threshold → always merge (same cell).
        int minColsForNewRow = _options.MinColumnsForNewRow > 0
            ? _options.MinColumnsForNewRow
            : Math.Max(columns.Count / 3, 2);

        var logicalRows = new List<Dictionary<int, string>>();
        var currentRow = new Dictionary<int, string>(textLines[0].Cells);

        for (int i = 1; i < textLines.Count; i++)
        {
            double gap = Math.Abs(textLines[i - 1].Y - textLines[i].Y);
            int filledCols = textLines[i].Cells.Count(c => !string.IsNullOrWhiteSpace(c.Value));

            bool isNewRow;
            if (gap <= rowThreshold)
            {
                // Small gap: always merge (wrap within cell)
                isNewRow = false;
            }
            else if (filledCols >= minColsForNewRow)
            {
                // Large gap AND many columns filled → real new row
                isNewRow = true;
            }
            else
            {
                // Large gap BUT few columns → could be wrap or sub-row.
                // Check if filled cells are in columns where current row has SHORT values
                // (sub-row) vs LONG values (wrap).
                bool hasShortPrevValues = false;
                foreach (var (col, _) in textLines[i].Cells)
                {
                    if (currentRow.TryGetValue(col, out string? prev) &&
                        !string.IsNullOrWhiteSpace(prev))
                    {
                        // Get the LAST line of prev value (in case it was already multi-line)
                        var lastLine = prev.Split('\n').Last().Trim();
                        if (lastLine.Length <= 4 && !lastLine.EndsWith(",") && !lastLine.EndsWith("-"))
                        {
                            hasShortPrevValues = true;
                            break;
                        }
                    }
                }

                isNewRow = hasShortPrevValues;
            }

            if (isNewRow)
            {
                logicalRows.Add(currentRow);
                currentRow = new Dictionary<int, string>(textLines[i].Cells);
            }
            else
            {
                // Merge: wrap within the same cell
                foreach (var (col, text) in textLines[i].Cells)
                {
                    if (currentRow.TryGetValue(col, out string? existing) &&
                        !string.IsNullOrEmpty(existing))
                        currentRow[col] = existing + "\n" + text;
                    else
                        currentRow[col] = text;
                }
            }
        }
        logicalRows.Add(currentRow);

        // Step 5: Convert to table format
        if (logicalRows.Count == 0) return table;

        table.Headers = RowDictToList(logicalRows[0], columns.Count);
        for (int i = 1; i < logicalRows.Count; i++)
            table.Rows.Add(RowDictToList(logicalRows[i], columns.Count));

        return table;
    }

    private static List<string> RowDictToList(Dictionary<int, string> rowDict, int colCount)
    {
        var list = new List<string>();
        for (int c = 0; c < colCount; c++)
            list.Add(rowDict.TryGetValue(c, out string? val) ? val ?? string.Empty : string.Empty);
        return list;
    }

    private ExtractedFormGrid BuildFormGrid(List<AnnotatedLine> block, string? sectionName,
        int pageNumber, double y)
    {
        var grid = new ExtractedFormGrid
        {
            PageNumber = pageNumber,
            Y = y
        };

        // Strategy: PIT-37 style forms have label lines (with "NN. Label") followed by value lines.
        // We pair them spatially by X position.
        //
        // Step 1: Classify each line as "label line" or "value line"
        // Step 2: For each label line, extract field definitions with their X positions
        // Step 3: Match value lines to the label line above them by X proximity

        var pendingFields = new List<FormFieldDef>(); // fields awaiting values

        for (int i = 0; i < block.Count; i++)
        {
            var line = block[i];
            var segments = line.Segments;

            // Extract all numbered fields from this line's segments
            var fieldsOnLine = ExtractFieldDefs(segments);

            if (fieldsOnLine.Count > 0)
            {
                // This is a label line.
                // First, flush any pending fields that won't get values
                FlushPendingFields(pendingFields, grid);

                pendingFields = fieldsOnLine;
            }
            else if (pendingFields.Count > 0)
            {
                // This is a value line - map segments to pending fields by X position
                AssignValuesByPosition(pendingFields, segments);
                FlushPendingFields(pendingFields, grid);
                pendingFields = new List<FormFieldDef>();
            }
            else
            {
                // No pending fields and no new fields - could be standalone text
                string text = string.Join(" ", segments.Select(s => s.Text)).Trim();
                if (!string.IsNullOrWhiteSpace(text) && grid.Cells.Count > 0)
                {
                    var last = grid.Cells.Last();
                    if (string.IsNullOrEmpty(last.Value))
                        last.Value = text;
                    else
                        last.Value += " " + text;
                }
            }
        }

        // Flush any remaining pending fields
        FlushPendingFields(pendingFields, grid);

        return grid;
    }

    /// <summary>
    /// Extract numbered field definitions from segments, tracking their X positions.
    /// A line like "11. Nazwisko 12. Pierwsze imie 13. Data urodzenia"
    /// produces 3 field defs with the X position of each segment.
    /// </summary>
    private List<FormFieldDef> ExtractFieldDefs(List<TextSegment> segments)
    {
        var fields = new List<FormFieldDef>();

        foreach (var seg in segments)
        {
            string text = seg.Text.Trim();

            // Try to find all "NN. Label" patterns within this segment
            var matches = Regex.Matches(text, @"(\d{1,3})\.\s*([^0-9].*?)(?=\s+\d{1,3}\.\s|$)");

            if (matches.Count > 0)
            {
                foreach (Match m in matches)
                {
                    fields.Add(new FormFieldDef
                    {
                        FieldNumber = m.Groups[1].Value,
                        Label = m.Groups[2].Value.Trim(),
                        X = seg.X + (m.Index * (seg.Width / Math.Max(text.Length, 1)))
                    });
                }
            }
            else
            {
                // Single field number at start
                var singleMatch = Regex.Match(text, @"^(\d{1,3})\.\s*(.*)$");
                if (singleMatch.Success)
                {
                    fields.Add(new FormFieldDef
                    {
                        FieldNumber = singleMatch.Groups[1].Value,
                        Label = singleMatch.Groups[2].Value.Trim(),
                        X = seg.X
                    });
                }
            }
        }

        return fields;
    }

    /// <summary>
    /// Assign value segments to field definitions by X position proximity.
    /// </summary>
    private void AssignValuesByPosition(List<FormFieldDef> fields, List<TextSegment> valueSegments)
    {
        if (fields.Count == 0 || valueSegments.Count == 0) return;

        // Sort fields and segments by X
        var sortedFields = fields.OrderBy(f => f.X).ToList();
        var sortedSegs = valueSegments.OrderBy(s => s.X).ToList();

        if (sortedFields.Count == 1)
        {
            // Only one field - all value segments belong to it
            sortedFields[0].Value = string.Join(" ", sortedSegs.Select(s => s.Text.Trim()));
            return;
        }

        // Multiple fields: assign each value segment to the nearest field
        foreach (var seg in sortedSegs)
        {
            double segCenter = seg.X + seg.Width / 2.0;
            FormFieldDef? bestField = null;
            double bestDist = double.MaxValue;

            foreach (var field in sortedFields)
            {
                double dist = Math.Abs(segCenter - field.X);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestField = field;
                }
            }

            if (bestField != null)
            {
                if (string.IsNullOrEmpty(bestField.Value))
                    bestField.Value = seg.Text.Trim();
                else
                    bestField.Value += " " + seg.Text.Trim();
            }
        }
    }

    private void FlushPendingFields(List<FormFieldDef> fields, ExtractedFormGrid grid)
    {
        foreach (var field in fields)
        {
            grid.Cells.Add(new FormCell
            {
                FieldNumber = field.FieldNumber,
                Label = field.Label,
                Value = field.Value ?? string.Empty
            });
        }
    }

    #endregion

    /// <summary>
    /// Check if a line looks like a header (short label-like text, no numeric data).
    /// </summary>
    private bool IsHeaderLikeLine(AnnotatedLine line)
    {
        string fullText = line.Line.FullText;

        // Headers should be mostly alphabetic label text.
        // Count digits vs letters in the full text. Data lines have lots of numbers.
        int digitCount = fullText.Count(char.IsDigit);
        int letterCount = fullText.Count(char.IsLetter);
        if (letterCount > 0 && (double)digitCount / letterCount > 0.3) return false;
        if (digitCount > 5) return false; // too many digits for a header line

        foreach (var seg in line.Segments)
        {
            string text = seg.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (Regex.IsMatch(text, @"\d{2}\.\d{2}\.\d{4}")) return false; // date
            if (Regex.IsMatch(text, @"\d+[.,]\d{2}")) return false; // decimal number
            if (Regex.IsMatch(text, @"\d+%")) return false; // percentage
            if (Regex.IsMatch(text, @"\d{3,}")) return false; // 3+ digit number
            if (text.Length > 45) return false;
        }
        return true;
    }

    #region Column Detection

    private List<ColumnDef> DetectColumns(List<AnnotatedLine> dataLines)
    {
        var allXPositions = dataLines
            .SelectMany(b => b.Segments.Select(s => s.X))
            .OrderBy(x => x)
            .ToList();

        var clusters = new List<List<double>>();
        foreach (var x in allXPositions)
        {
            var existing = clusters.FirstOrDefault(c => Math.Abs(c.Average() - x) < _columnGapThreshold * 1.5);
            if (existing != null)
                existing.Add(x);
            else
                clusters.Add(new List<double> { x });
        }

        return clusters
            .OrderBy(c => c.Average())
            .Select(c => new ColumnDef { LeftX = c.Average() })
            .ToList();
    }

    private List<string> AssignToColumns(List<TextSegment> segments, List<ColumnDef> columns)
    {
        var result = new string[columns.Count];
        for (int i = 0; i < columns.Count; i++)
            result[i] = string.Empty;

        foreach (var seg in segments)
        {
            int bestCol = FindNearestColumn(seg.X, columns);
            if (string.IsNullOrEmpty(result[bestCol]))
                result[bestCol] = seg.Text;
            else
                result[bestCol] += " " + seg.Text;
        }

        return result.ToList();
    }

    private int FindNearestColumn(double x, List<ColumnDef> columns)
    {
        int bestCol = 0;
        double bestDist = double.MaxValue;
        for (int c = 0; c < columns.Count; c++)
        {
            double dist = Math.Abs(x - columns[c].LeftX);
            if (dist < bestDist) { bestDist = dist; bestCol = c; }
        }
        return bestCol;
    }

    #endregion

    #region Helpers

    private bool IsHeaderFooter(PdfTextLine line)
    {
        if (_headerFooterDetector != null)
            return _headerFooterDetector.IsHeaderFooter(line);

        return false;
    }

    #endregion
}

internal class AnnotatedLine
{
    public PdfTextLine Line { get; set; } = null!;
    public List<TextSegment> Segments { get; set; } = new();
}

internal class ColumnDef
{
    public double LeftX { get; set; }
}

internal class FormFieldDef
{
    public string FieldNumber { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Value { get; set; }
    public double X { get; set; }
}
