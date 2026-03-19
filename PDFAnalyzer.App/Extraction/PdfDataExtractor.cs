using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using PDFAnalyzer.App.Models;

namespace PDFAnalyzer.App.Extraction;

/// <summary>
/// Main orchestrator: opens a PDF, extracts all pages, detects structures.
/// </summary>
public class PdfDataExtractor
{
    public ExtractedDocument Extract(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"PDF file not found: {filePath}");

        using var pdf = PdfDocument.Open(filePath);
        return ExtractFromPdf(pdf, Path.GetFileName(filePath));
    }

    public ExtractedDocument Extract(byte[] pdfBytes, string fileName = "document.pdf")
    {
        using var pdf = PdfDocument.Open(pdfBytes);
        return ExtractFromPdf(pdf, fileName);
    }

    private ExtractedDocument ExtractFromPdf(PdfDocument pdf, string fileName)
    {
        var document = new ExtractedDocument { FileName = fileName };
        document.TotalPages = pdf.NumberOfPages;

        // Pass 1: extract raw lines from all pages
        var allPageLines = new List<(List<PdfTextLine> Lines, double PageWidth)>();
        for (int pageNum = 1; pageNum <= pdf.NumberOfPages; pageNum++)
        {
            var page = pdf.GetPage(pageNum);
            allPageLines.Add((PdfPageParser.ExtractLines(page), page.Width));
        }

        // Pass 2: detect header/footer patterns across all pages
        var hfDetector = new HeaderFooterDetector();
        hfDetector.Analyze(allPageLines.Select(p => p.Lines).ToList());

        // Pass 3: detect structures on each page (with header/footer filtering)
        for (int i = 0; i < allPageLines.Count; i++)
        {
            var (lines, pageWidth) = allPageLines[i];
            var detector = new StructureDetector(pageWidth, hfDetector);
            var elements = detector.DetectStructures(lines, i + 1);
            document.Pages.Add(new ExtractedPage { PageNumber = i + 1, Elements = elements });
        }

        PostProcess(document);
        return document;
    }

    private void PostProcess(ExtractedDocument document)
    {
        // Phase 1: Per-page table cleanup
        foreach (var page in document.Pages)
        {
            foreach (var element in page.Elements.OfType<ExtractedTable>().ToList())
            {
                MergeWrappedCellRows(element);
                MergeMultiLineHeaders(element);
                RemoveEmptyColumns(element);
            }

            MergeHeaderOnlyTables(page);
        }

        // Phase 2: Remove embedded footers (needs cross-page comparison)
        RemoveEmbeddedFooterRows(document);

        // Phase 3: Absorb orphan TextBlocks into adjacent tables (wrapped cell overflow)
        AbsorbOrphanTextBlocks(document);

        // Phase 4: Cross-page table merge (BEFORE inherited detection!)
        MergeCrossPageTables(document);

        // Phase 5: Per-page structure detection (now on fully merged tables)
        foreach (var page in document.Pages)
        {
            // Re-clean merged tables (remove empty cols that appeared after cross-page merge)
            foreach (var table in page.Elements.OfType<ExtractedTable>().ToList())
            {
                RemoveEmptyColumns(table);
            }

            MergeConsecutiveKeyValueGroups(page);
            MergeConsecutiveTextBlocks(page);
            DetectInheritedTables(page);

            // Remove empties
            page.Elements.RemoveAll(e =>
                e is ExtractedTextBlock tb && string.IsNullOrWhiteSpace(tb.Text));
            page.Elements.RemoveAll(e =>
                e is ExtractedTable t && t.Rows.Count == 0 && t.Headers.All(string.IsNullOrWhiteSpace));
            page.Elements.RemoveAll(e =>
                e is ExtractedKeyValueGroup kv && kv.Items.Count == 0);
            page.Elements.RemoveAll(e =>
                e is ExtractedFormGrid fg && fg.Cells.Count == 0);
        }

        MergeCrossPageKeyValueGroups(document);
    }

    /// <summary>
    /// Absorb orphan TextBlocks that sit between table fragments.
    /// These are typically wrapped cell text that overflowed from the last row
    /// of the preceding table (e.g., "QWEQWEE,\nQWEQWE" between page breaks).
    /// Also handles text blocks at the START of a page that belong to the
    /// last table on the previous page.
    /// </summary>
    private static void AbsorbOrphanTextBlocks(ExtractedDocument document)
    {
        // Within each page: absorb TextBlocks between two tables
        foreach (var page in document.Pages)
        {
            for (int i = page.Elements.Count - 2; i >= 0; i--)
            {
                if (page.Elements[i] is ExtractedTable table &&
                    i + 1 < page.Elements.Count &&
                    page.Elements[i + 1] is ExtractedTextBlock textBlock)
                {
                    // Check if next element after the text is also a table
                    bool nextIsTable = i + 2 < page.Elements.Count &&
                                       page.Elements[i + 2] is ExtractedTable;

                    // Short text between tables = wrapped cell overflow
                    string text = textBlock.Text.Trim();
                    if (text.Length < 100 && table.Rows.Count > 0)
                    {
                        // Append to the last cell of the last row
                        var lastRow = table.Rows[^1];
                        int lastNonEmptyCol = -1;
                        for (int c = lastRow.Count - 1; c >= 0; c--)
                        {
                            if (!string.IsNullOrWhiteSpace(lastRow[c]))
                            {
                                lastNonEmptyCol = c;
                                break;
                            }
                        }

                        if (lastNonEmptyCol >= 0)
                        {
                            lastRow[lastNonEmptyCol] += "\n" + text;
                            page.Elements.RemoveAt(i + 1);
                        }
                    }
                }
            }
        }

        // Across pages: TextBlock at start of page → append to last table on previous page
        for (int p = 1; p < document.Pages.Count; p++)
        {
            var prevPage = document.Pages[p - 1];
            var curPage = document.Pages[p];

            if (curPage.Elements.Count == 0) continue;
            if (curPage.Elements[0] is not ExtractedTextBlock tb) continue;

            // Find last table on previous page
            var lastTable = prevPage.Elements.OfType<ExtractedTable>().LastOrDefault();
            if (lastTable == null || lastTable.Rows.Count == 0) continue;

            string text = tb.Text.Trim();
            if (text.Length >= 100) continue; // too long, probably real content

            var lastRow = lastTable.Rows[^1];
            int lastCol = -1;
            for (int c = lastRow.Count - 1; c >= 0; c--)
            {
                if (!string.IsNullOrWhiteSpace(lastRow[c]))
                {
                    lastCol = c;
                    break;
                }
            }

            if (lastCol >= 0)
            {
                lastRow[lastCol] += "\n" + text;
                curPage.Elements.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Handle multi-line headers (2, 3, or more header rows).
    ///
    /// Consumes consecutive header-like rows from the top and merges them.
    /// Two merge modes:
    ///   1. HIERARCHICAL: A row has MORE non-empty cells than the one above it
    ///      → the upper row becomes parent header groups (merged cells).
    ///   2. SIMPLE CONTINUATION: A row fills in gaps or appends text to headers above.
    ///
    /// Works iteratively so 3+ header lines are handled:
    ///   Row 0 (original headers): "        | Koszty        | Dochody i straty          |           "
    ///   Row 1 (sub-headers):      "Zrodla  | Koszty uzys.  | Dochod    | Strata        | Zaliczka  "
    ///   Row 2 (units):            "        | przych. zl    | (b-c) zl  | (c-b) zl      | przez pl. "
    ///   Row 3 (first DATA row):   "1. Nal. | 87 450,00     | 84 450,00 | -             | 7 812,00  "
    /// </summary>
    /// <summary>
    /// Merge rows that are actually wrapped cell text from the row above.
    ///
    /// A "wrapped continuation" row has very few non-empty cells compared to a full data row,
    /// and those cells are in columns where the previous row had long text.
    ///
    /// Example:
    ///   Row N:   "A" | "asdasd" | "2" | "65" | "123123123123" | "2" | "2"  | "ASDASD,ASD"
    ///   Row N+1: ""  | ""       | ""  | ""   | "123123"       | ""  | ""   | "ASD,ASDASD"   ← wrap!
    ///   Row N+2: ""  | ""       | "4" | "53" | "123531235432" | "5" | "34" | "ASDASD,ASD"   ← new sub-row
    ///
    /// Row N+1 has only 2 non-empty cells (vs 8 in a full row) → it's a wrap continuation.
    /// Row N+2 has 6 non-empty cells → it's a real data row.
    /// </summary>
    private void MergeWrappedCellRows(ExtractedTable table)
    {
        if (table.Rows.Count < 2) return;

        int colCount = table.Headers.Count;

        // Compute the "full row" fill count using the 75th percentile.
        // This is robust: even if half the rows are wraps, the upper quartile
        // will still represent real data rows.
        var fillCounts = table.Rows
            .Select(r => r.Count(c => !string.IsNullOrWhiteSpace(c)))
            .OrderBy(x => x)
            .ToList();
        int p75Fill = fillCounts[(int)(fillCounts.Count * 0.75)];

        // A row is a wrap if it has less than half the cells of a full row.
        // This cleanly separates wraps (1-3 cells) from data rows (5-8 cells).
        double wrapThreshold = Math.Max(p75Fill * 0.50, 2);
        int maxWrapCols = Math.Max(colCount / 2, 2);

        // Process bottom-up so indices stay valid
        for (int r = table.Rows.Count - 1; r >= 1; r--)
        {
            var row = table.Rows[r];
            int nonEmpty = row.Count(c => !string.IsNullOrWhiteSpace(c));

            if (nonEmpty > 0 && nonEmpty <= wrapThreshold && nonEmpty <= maxWrapCols)
            {
                // This looks like a wrap. Verify: the non-empty cells should be in columns
                // where the previous row also has values (the text was too long and wrapped).
                var prevRow = table.Rows[r - 1];
                bool allCellsAlignWithPrev = true;

                for (int c = 0; c < row.Count; c++)
                {
                    if (!string.IsNullOrWhiteSpace(row[c]) && c < prevRow.Count)
                    {
                        // This column has wrap text - prev row should also have a value here
                        if (string.IsNullOrWhiteSpace(prevRow[c]))
                        {
                            allCellsAlignWithPrev = false;
                            break;
                        }
                    }
                }

                if (allCellsAlignWithPrev)
                {
                    // Merge: append this row's cell text to the previous row
                    for (int c = 0; c < row.Count && c < prevRow.Count; c++)
                    {
                        string text = row[c].Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (string.IsNullOrWhiteSpace(prevRow[c]))
                                prevRow[c] = text;
                            else
                                prevRow[c] = prevRow[c] + "\n" + text;
                        }
                    }

                    table.Rows.RemoveAt(r);
                }
            }
        }
    }

    private void MergeMultiLineHeaders(ExtractedTable table)
    {
        // Collect all consecutive header-like rows from the top
        var headerRows = new List<List<string>> { table.Headers };
        int consumed = 0;

        while (consumed < table.Rows.Count && IsHeaderLikeRow(table.Rows[consumed]))
        {
            // Don't consume if the NEXT row is also header-like and this is the last one
            // (we need at least 1 data row to remain)
            if (consumed + 1 >= table.Rows.Count)
                break;

            headerRows.Add(table.Rows[consumed]);
            consumed++;
        }

        if (consumed == 0)
            return; // No header-like rows to merge

        // Remove consumed rows
        table.Rows.RemoveRange(0, consumed);

        // Now merge headerRows into final headers + optional multi-level groups
        MergeHeaderRowsIntoTable(table, headerRows);
    }

    /// <summary>
    /// Merge N header rows (2, 3, or more) into the table.
    ///
    /// Strategy: scan from bottom to top. The bottom-most row is the column headers.
    /// Each row above it that has FEWER non-empty cells is a group level.
    /// If a row has the SAME or MORE non-empty cells as the one below, it's a continuation
    /// (text merges into the row below it).
    ///
    /// Example with 4 rows:
    ///   Row 0: "Finanse"                            → Level 0 group (1 non-empty)
    ///   Row 1: "Koszty" | "Dochody i straty"        → Level 1 groups (2 non-empty)
    ///   Row 2: "Zrodla" | "Koszty uzys." | "Dochod" | "Strata" | "Zaliczka"  → sub-headers (5)
    ///   Row 3: "" | "przych. zl" | "(b-c) zl" | "(c-b) zl" | "przez pl."    → continuation of Row 2
    /// </summary>
    private void MergeHeaderRowsIntoTable(ExtractedTable table, List<List<string>> headerRows)
    {
        if (headerRows.Count < 2)
            return;

        // Step 1: Identify which rows are group levels and which are continuations.
        // Work bottom-up: merge continuations first, then build group levels.

        // Start by merging consecutive rows with similar fill count (continuations).
        // A row is a continuation of the row below if it has SAME or MORE non-empty cells,
        // or if both rows fill the same columns.
        var mergedRows = new List<List<string>> { headerRows[^1] };

        for (int i = headerRows.Count - 2; i >= 0; i--)
        {
            var upper = headerRows[i];
            var lower = mergedRows[0]; // current bottom row

            int upperNonEmpty = upper.Count(h => !string.IsNullOrWhiteSpace(h));
            int lowerNonEmpty = lower.Count(h => !string.IsNullOrWhiteSpace(h));

            if (upperNonEmpty >= lowerNonEmpty || upperNonEmpty == 0)
            {
                // Continuation: merge upper into lower
                mergedRows[0] = MergeHeaderTexts(upper, lower);
            }
            else
            {
                // Fewer non-empty cells → this is a GROUP level above
                mergedRows.Insert(0, upper);
            }
        }

        // mergedRows is now [level0, level1, ..., finalHeaders] from top to bottom.
        // Last entry = final column headers. Preceding entries = group levels.
        table.Headers = mergedRows[^1];

        if (mergedRows.Count >= 2)
        {
            // Build multi-level header groups
            var levels = new List<List<HeaderGroup>>();

            for (int lvl = 0; lvl < mergedRows.Count - 1; lvl++)
            {
                var groupRow = mergedRows[lvl];
                var childRow = mergedRows[lvl + 1]; // the row directly below

                var groups = BuildHeaderGroups(groupRow, childRow);
                if (groups.Count > 0)
                {
                    levels.Add(groups);

                    // Fill ungrouped cells in the child from the parent
                    for (int c = 0; c < childRow.Count && c < groupRow.Count; c++)
                    {
                        if (string.IsNullOrWhiteSpace(childRow[c]) && !string.IsNullOrWhiteSpace(groupRow[c]))
                        {
                            bool inGroup = groups.Any(g => c >= g.StartColumn && c <= g.EndColumn);
                            if (!inGroup)
                                childRow[c] = groupRow[c];
                        }
                    }
                }
            }

            if (levels.Count > 0)
            {
                table.HeaderLevels = levels;
                // Update final headers (might have been modified by fill-in)
                table.Headers = mergedRows[^1];
            }
        }
    }

    /// <summary>
    /// Merge two header text rows: child fills gaps in parent or appends to it.
    /// </summary>
    private static List<string> MergeHeaderTexts(List<string> upper, List<string> lower)
    {
        int colCount = Math.Max(upper.Count, lower.Count);
        var result = new List<string>();

        for (int c = 0; c < colCount; c++)
        {
            string u = c < upper.Count ? upper[c].Trim() : string.Empty;
            string l = c < lower.Count ? lower[c].Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(u))
                result.Add(l);
            else if (string.IsNullOrWhiteSpace(l))
                result.Add(u);
            else
                result.Add(u + " " + l);
        }

        return result;
    }

    private bool IsHeaderLikeRow(List<string> row)
    {
        // All non-empty cells must be short label-like text (no numeric data)
        int nonEmpty = 0;
        foreach (var cell in row)
        {
            string t = cell.Trim();
            if (string.IsNullOrWhiteSpace(t)) continue;
            nonEmpty++;
            if (t.Length > 40) return false;
            if (Regex.IsMatch(t, @"\d{2}\.\d{2}\.\d{4}")) return false; // date
            if (Regex.IsMatch(t, @"\d+\s+\d+,\d{2}")) return false; // formatted amount
            if (Regex.IsMatch(t, @"\d+,\d{2}")) return false; // decimal number
            if (Regex.IsMatch(t, @"\d+%")) return false; // percentage
            if (Regex.IsMatch(t, @"^\d+$")) return false; // standalone pure number
            if (Regex.IsMatch(t, @"\d{3,}")) return false; // contains 3+ digit number (like 500, 200)
        }
        return nonEmpty > 0;
    }

    private List<HeaderGroup> BuildHeaderGroups(List<string> parentHeaders, List<string> subHeaders)
    {
        var groups = new List<HeaderGroup>();
        int colCount = Math.Min(parentHeaders.Count, subHeaders.Count);

        int c = 0;
        while (c < colCount)
        {
            string parentText = parentHeaders[c].Trim();

            if (!string.IsNullOrWhiteSpace(parentText))
            {
                int endCol = c;
                for (int j = c + 1; j < colCount; j++)
                {
                    if (string.IsNullOrWhiteSpace(parentHeaders[j].Trim()))
                        endCol = j;
                    else
                        break;
                }

                if (endCol > c)
                {
                    bool hasSubHeaders = false;
                    for (int j = c; j <= endCol; j++)
                    {
                        if (!string.IsNullOrWhiteSpace(subHeaders[j].Trim()))
                        { hasSubHeaders = true; break; }
                    }

                    if (hasSubHeaders)
                    {
                        groups.Add(new HeaderGroup
                        {
                            Name = parentText,
                            StartColumn = c,
                            EndColumn = endCol
                        });
                    }
                }

                c = endCol + 1;
            }
            else c++;
        }

        return groups;
    }

    /// <summary>
    /// Merge a header-only table (0 rows) with the next table if they share similar structure.
    /// This handles cases where multi-line headers create separate blocks.
    /// </summary>
    private void MergeHeaderOnlyTables(ExtractedPage page)
    {
        for (int i = page.Elements.Count - 2; i >= 0; i--)
        {
            if (page.Elements[i] is ExtractedTable headerTable &&
                headerTable.Rows.Count == 0)
            {
                // Find next table (skip text blocks between them)
                for (int j = i + 1; j < page.Elements.Count; j++)
                {
                    if (page.Elements[j] is ExtractedTable dataTable && dataTable.Rows.Count > 0)
                    {
                        // Merge: use the header table's headers as the real headers,
                        // and prepend the data table's headers as first row if they have content
                        if (headerTable.Headers.Count == dataTable.Headers.Count)
                        {
                            // The data table's "headers" are actually the first data row
                            dataTable.Rows.Insert(0, dataTable.Headers);
                            dataTable.Headers = headerTable.Headers;
                            page.Elements.RemoveAt(i);
                        }
                        else if (dataTable.Headers.Count > headerTable.Headers.Count)
                        {
                            // Data table has more columns - just prepend headers row
                            dataTable.Rows.Insert(0, dataTable.Headers);
                            // Pad header table headers to match
                            while (headerTable.Headers.Count < dataTable.Headers.Count)
                                headerTable.Headers.Add(string.Empty);
                            dataTable.Headers = headerTable.Headers;
                            page.Elements.RemoveAt(i);
                        }
                        break;
                    }
                    else if (page.Elements[j] is not ExtractedTextBlock)
                    {
                        break; // Different type blocks the merge
                    }
                }
            }
        }
    }

    /// <summary>
    /// Remove columns that are empty in all rows.
    /// </summary>
    private void RemoveEmptyColumns(ExtractedTable table)
    {
        if (table.Headers.Count == 0) return;

        var colsToRemove = new List<int>();
        for (int c = 0; c < table.Headers.Count; c++)
        {
            bool headerEmpty = string.IsNullOrWhiteSpace(table.Headers[c]);
            bool allRowsEmpty = table.Rows.All(r => c >= r.Count || string.IsNullOrWhiteSpace(r[c]));
            if (headerEmpty && allRowsEmpty)
                colsToRemove.Add(c);
        }

        // Remove from back to front to preserve indices
        foreach (int c in colsToRemove.OrderByDescending(x => x))
        {
            table.Headers.RemoveAt(c);
            foreach (var row in table.Rows)
            {
                if (c < row.Count)
                    row.RemoveAt(c);
            }
        }
    }

    private void MergeConsecutiveKeyValueGroups(ExtractedPage page)
    {
        for (int i = page.Elements.Count - 2; i >= 0; i--)
        {
            if (page.Elements[i] is ExtractedKeyValueGroup kv1 &&
                page.Elements[i + 1] is ExtractedKeyValueGroup kv2 &&
                kv1.SectionName == null && kv2.SectionName == null)
            {
                kv1.Items.AddRange(kv2.Items);
                page.Elements.RemoveAt(i + 1);
            }
        }
    }

    private void DetectInheritedTables(ExtractedPage page)
    {
        for (int i = 0; i < page.Elements.Count; i++)
        {
            if (page.Elements[i] is ExtractedTable table)
            {
                var inherited = InheritedTableDetector.TryConvert(table);
                if (inherited != null)
                {
                    page.Elements[i] = inherited;
                }
            }
        }
    }

    private void MergeConsecutiveTextBlocks(ExtractedPage page)
    {
        for (int i = page.Elements.Count - 2; i >= 0; i--)
        {
            if (page.Elements[i] is ExtractedTextBlock tb1 &&
                page.Elements[i + 1] is ExtractedTextBlock tb2)
            {
                // Only merge if both are short paragraph-like text (not section headers)
                bool tb1IsHeader = System.Text.RegularExpressions.Regex.IsMatch(tb1.Text, @"^\d+(\.\d+)?\s+");
                bool tb2IsHeader = System.Text.RegularExpressions.Regex.IsMatch(tb2.Text, @"^\d+(\.\d+)?\s+");

                if (!tb1IsHeader && !tb2IsHeader)
                {
                    tb1.Text = tb1.Text + "\n" + tb2.Text;
                    page.Elements.RemoveAt(i + 1);
                }
            }
        }
    }

    /// <summary>
    /// Merge tables that span across page boundaries.
    ///
    /// Handles 3 scenarios:
    ///   1. Next page table has SAME headers → merge rows, drop duplicate headers.
    ///   2. Next page table has NO real headers (headers look like data) and same column count
    ///      → headerless continuation, merge rows.
    ///   3. Next page starts with a partial row (few cells, aligns with last row of prev table)
    ///      → merge as wrapped cell continuation of the last row.
    ///
    /// Runs iteratively so tables spanning 3+ pages get fully merged.
    /// </summary>

    /// <summary>
    /// Detect and remove footer/header text that got embedded as last/first rows of tables.
    /// Works by finding text patterns that repeat in the same position across multiple pages.
    /// E.g., "Dokument taki sraki i owaki" + page number as the last row of every page's table.
    /// </summary>
    private void RemoveEmbeddedFooterRows(ExtractedDocument document)
    {
        if (document.Pages.Count < 2) return;

        // Collect fingerprints of last rows across all pages
        var lastRowFingerprints = new Dictionary<string, int>();
        var firstRowFingerprints = new Dictionary<string, int>();

        foreach (var page in document.Pages)
        {
            foreach (var table in page.Elements.OfType<ExtractedTable>())
            {
                if (table.Rows.Count > 0)
                {
                    string lastFp = RowFingerprint(table.Rows[^1]);
                    if (!string.IsNullOrWhiteSpace(lastFp))
                        lastRowFingerprints[lastFp] = lastRowFingerprints.GetValueOrDefault(lastFp) + 1;

                    if (table.Rows.Count > 1)
                    {
                        string firstFp = RowFingerprint(table.Rows[0]);
                        if (!string.IsNullOrWhiteSpace(firstFp))
                            firstRowFingerprints[firstFp] = firstRowFingerprints.GetValueOrDefault(firstFp) + 1;
                    }
                }
            }
        }

        // Rows appearing in 40%+ of pages at the same position are likely footers/headers
        double threshold = document.Pages.Count * 0.40;

        var footerPatterns = lastRowFingerprints
            .Where(kv => kv.Value >= threshold)
            .Select(kv => kv.Key)
            .ToHashSet();

        var headerPatterns = firstRowFingerprints
            .Where(kv => kv.Value >= threshold)
            .Select(kv => kv.Key)
            .ToHashSet();

        // Remove matching rows
        foreach (var page in document.Pages)
        {
            foreach (var table in page.Elements.OfType<ExtractedTable>())
            {
                if (table.Rows.Count > 0 && footerPatterns.Contains(RowFingerprint(table.Rows[^1])))
                    table.Rows.RemoveAt(table.Rows.Count - 1);

                if (table.Rows.Count > 0 && headerPatterns.Contains(RowFingerprint(table.Rows[0])))
                    table.Rows.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Create a fingerprint of a row for cross-page comparison.
    /// Replaces page numbers with placeholder, normalizes whitespace.
    /// A footer row typically has mostly empty cells with one text cell + one number.
    /// </summary>
    private static string RowFingerprint(List<string> row)
    {
        // Footer rows: mostly empty cells with 1-2 non-empty (text + page number).
        // Must have significantly fewer filled cells than the row width.
        int nonEmpty = row.Count(c => !string.IsNullOrWhiteSpace(c));
        int totalCols = row.Count;

        // Footer: at most 2 non-empty cells, OR at most 30% fill rate for wider tables
        if (nonEmpty > 2 || (totalCols > 0 && (double)nonEmpty / totalCols > 0.4))
            return string.Empty;

        if (nonEmpty == 0) return string.Empty;

        var parts = row
            .Select(c => c.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Regex.IsMatch(c, @"^\d{1,4}$") ? "##PAGE##" : c)
            .ToList();

        return string.Join("|", parts);
    }

    private void MergeCrossPageTables(ExtractedDocument document)
    {
        // Track tables that received cross-page rows (need wrap re-merge)
        var mergedTables = new HashSet<ExtractedTable>();

        // Repeat until no more merges happen (handles 3+ page spans)
        bool merged = true;
        while (merged)
        {
            merged = false;

            // Find the last page with a table, then check against the next page with elements
            for (int p = 0; p < document.Pages.Count - 1; p++)
            {
                var currentPage = document.Pages[p];
                var lastElement = currentPage.Elements.LastOrDefault(e => e is ExtractedTable);
                if (lastElement is not ExtractedTable lastTable)
                    continue;

                // Find the next page that has any elements
                ExtractedPage? nextPage = null;
                for (int np = p + 1; np < document.Pages.Count; np++)
                {
                    if (document.Pages[np].Elements.Count > 0)
                    {
                        nextPage = document.Pages[np];
                        break;
                    }
                }

                if (nextPage == null)
                    continue;

                var firstElement = nextPage.Elements.FirstOrDefault();

                if (firstElement is ExtractedTable firstTable)
                {
                    // Scenario 1: Same headers (exact or close match)
                    if (lastTable.Headers.Count == firstTable.Headers.Count &&
                        HeadersMatch(lastTable.Headers, firstTable.Headers))
                    {
                        lastTable.Rows.AddRange(firstTable.Rows);
                        nextPage.Elements.Remove(firstTable);
                        mergedTables.Add(lastTable);
                        merged = true;
                        continue;
                    }

                    // Scenario 2: Headerless continuation.
                    if (lastTable.Headers.Count == firstTable.Headers.Count &&
                        !IsHeaderLikeRow(firstTable.Headers))
                    {
                        var dataRow = firstTable.Headers;
                        lastTable.Rows.Add(dataRow);
                        lastTable.Rows.AddRange(firstTable.Rows);
                        nextPage.Elements.Remove(firstTable);
                        mergedTables.Add(lastTable);
                        merged = true;
                        continue;
                    }

                    // Scenario 2b: Column count differs slightly
                    if (Math.Abs(lastTable.Headers.Count - firstTable.Headers.Count) <= 2 &&
                        !IsHeaderLikeRow(firstTable.Headers) &&
                        lastTable.Rows.Count > 0)
                    {
                        var dataRow = NormalizeRowToColumnCount(firstTable.Headers, lastTable.Headers.Count);
                        lastTable.Rows.Add(dataRow);

                        foreach (var row in firstTable.Rows)
                        {
                            lastTable.Rows.Add(NormalizeRowToColumnCount(row, lastTable.Headers.Count));
                        }

                        nextPage.Elements.Remove(firstTable);
                        mergedTables.Add(lastTable);
                        merged = true;
                        continue;
                    }
                }

                // Scenario 3: Next page starts with text/partial data that belongs to the last table.
                // Could be wrapped text from the last row of the previous page.
                if (firstElement is ExtractedTextBlock textBlock && lastTable.Rows.Count > 0)
                {
                    // Very short text might be a wrapped cell value
                    // Skip this for now - it's hard to determine which column it belongs to.
                }
            }
        }

        // Re-run wrap detection only on tables that received cross-page rows
        foreach (var table in mergedTables)
        {
            MergeWrappedCellRows(table);
        }
    }

    private static List<string> NormalizeRowToColumnCount(List<string> row, int targetCount)
    {
        var result = new List<string>(row);
        while (result.Count < targetCount)
            result.Add(string.Empty);
        if (result.Count > targetCount)
            result = result.Take(targetCount).ToList();
        return result;
    }

    private void MergeCrossPageKeyValueGroups(ExtractedDocument document)
    {
        for (int p = 0; p < document.Pages.Count - 1; p++)
        {
            var currentPage = document.Pages[p];
            var nextPage = document.Pages[p + 1];

            if (currentPage.Elements.Count == 0 || nextPage.Elements.Count == 0)
                continue;

            var lastElement = currentPage.Elements.Last();
            var firstElement = nextPage.Elements.First();

            // If last element on page is KV group and first on next page is KV group without section name
            if (lastElement is ExtractedKeyValueGroup lastKv &&
                firstElement is ExtractedKeyValueGroup firstKv &&
                firstKv.SectionName == null)
            {
                lastKv.Items.AddRange(firstKv.Items);
                nextPage.Elements.RemoveAt(0);
            }
        }
    }

    private bool HeadersMatch(List<string> h1, List<string> h2)
    {
        for (int i = 0; i < h1.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(h2[i]) &&
                !h1[i].Equals(h2[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
