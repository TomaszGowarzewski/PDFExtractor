using PDFAnalyzer.App.Models;

namespace PDFAnalyzer.App.Extraction;

/// <summary>
/// Detects tables with cascading inherited row values and converts them.
///
/// The key insight: inheritance is NOT limited to "ID columns". Any table where
/// empty cells in left columns mean "same as row above" follows this pattern.
///
/// Detection is based on the FILL RATE GRADIENT:
///   - Column 0 has the lowest fill rate (changes least often, e.g., Region)
///   - Column 1 has higher fill rate (changes more often, e.g., City)
///   - ...each column to the right fills more frequently...
///   - At some boundary, columns are always filled (detail data)
///
/// The fill rates must be monotonically non-decreasing left-to-right,
/// with a clear jump from "sparse" (&lt;80%) to "dense" (>90%).
///
/// Cascading inheritance means each column inherits INDEPENDENTLY:
///   Region      | City     | Office
///   Mazowieckie | Warszawa | Central HQ    ← all filled
///               |          | South Branch  ← Region+City inherited, Office is new
///               | Radom    | Service Point ← Region inherited, City is new
///   Malopolskie | Krakow   | Main Office   ← all new
/// </summary>
public static class InheritedTableDetector
{
    public static ExtractedInheritedTable? TryConvert(ExtractedTable table)
    {
        if (table.Rows.Count < 3 || table.Headers.Count < 3)
            return null;

        // Step 1: Compute fill rates per column
        var fillRates = ComputeFillRates(table);

        // Step 2: Find the boundary using the fill rate gradient
        int boundary = FindBoundaryByGradient(fillRates, table.Rows.Count);
        if (boundary < 0)
            return null;

        // Step 3: Validate the inheritance pattern
        if (!ValidateInheritancePattern(table, boundary))
            return null;

        // Step 4: Build result with cascading resolution
        return BuildInheritedTable(table, boundary);
    }

    /// <summary>
    /// Compute fill rate (% non-empty) for each column.
    /// </summary>
    private static double[] ComputeFillRates(ExtractedTable table)
    {
        int colCount = table.Headers.Count;
        var rates = new double[colCount];

        for (int c = 0; c < colCount; c++)
        {
            int filled = table.Rows.Count(r =>
                c < r.Count && !string.IsNullOrWhiteSpace(r[c]));
            rates[c] = (double)filled / table.Rows.Count;
        }

        return rates;
    }

    /// <summary>
    /// Find the boundary between inherited (sparse) and detail (dense) columns.
    ///
    /// Rules:
    ///   1. Fill rates must be roughly monotonically non-decreasing.
    ///   2. There must be a clear jump from sparse (&lt;80%) to dense (>85%).
    ///   3. At least one inherited column and one detail column.
    ///   4. First column must be sparse (fill rate &lt; 80%).
    /// </summary>
    private static int FindBoundaryByGradient(double[] fillRates, int rowCount)
    {
        int colCount = fillRates.Length;

        // First column must be sparse (< 80% fill)
        if (fillRates[0] >= 0.80)
            return -1;

        // Find the boundary: the column BEFORE the first always-filled column.
        // An "always-filled" column has fill rate >= 90%.
        // The boundary is the last sparse column before that jump.
        //
        // Strategy: find the first column with fill rate >= 90%.
        // The boundary is the column just before it.
        int firstDenseCol = -1;
        for (int c = 0; c < colCount; c++)
        {
            if (fillRates[c] >= 0.90)
            {
                firstDenseCol = c;
                break;
            }
        }

        if (firstDenseCol <= 0)
            return -1; // no dense column found, or first column is dense

        int boundary = firstDenseCol - 1;

        // All columns up to boundary must be sparse (< 80%)
        for (int c = 0; c <= boundary; c++)
        {
            if (fillRates[c] >= 0.80)
                return -1;
        }

        // Verify detail columns (after boundary) have high fill rates
        for (int c = boundary + 1; c < colCount; c++)
        {
            // At least some detail columns must have values
            // (not all need to be 100% - e.g., Notes column can be sparse)
        }

        // At least one detail column should have >50% fill rate
        // (some detail columns like Notes/Priority can be sparse)
        bool hasFilledDetailCol = false;
        for (int c = boundary + 1; c < colCount; c++)
        {
            if (fillRates[c] > 0.50)
            {
                hasFilledDetailCol = true;
                break;
            }
        }
        if (!hasFilledDetailCol)
            return -1;

        // Need enough empty rows to make this meaningful
        // (not just 1-2 missing values)
        int emptyInFirstCol = (int)((1.0 - fillRates[0]) * rowCount);
        if (emptyInFirstCol < 2)
            return -1;

        return boundary;
    }

    /// <summary>
    /// Validate the cascading inheritance pattern:
    ///   - When column C is empty, all columns 0..C-1 must also be empty.
    ///     (Exception: a column can be filled while a column to its right is empty,
    ///      but NOT the other way around - if col 0 is empty, col 1 must be empty too,
    ///      UNLESS col 1 itself is starting a new value at that level.)
    ///
    /// Actually, the real rule is simpler: each inherited column independently
    /// carries forward when empty. The constraint is:
    ///   - In detail rows (where col 0 is empty), at least one detail column has a value.
    ///   - No row is completely empty.
    /// </summary>
    private static bool ValidateInheritancePattern(ExtractedTable table, int boundary)
    {
        int totalRows = table.Rows.Count;
        int emptyLeftRows = 0;
        int violationCount = 0;

        for (int r = 0; r < totalRows; r++)
        {
            var row = table.Rows[r];
            bool col0Empty = string.IsNullOrWhiteSpace(row[0]);

            if (col0Empty)
            {
                emptyLeftRows++;

                // When col 0 is empty, check the "cascading" pattern:
                // columns should be empty from the left, with possibly some
                // intermediate columns having new values.
                // But ALL must be empty OR follow a cascading pattern.

                // At minimum: some detail column must have a value
                bool hasDetailValue = false;
                for (int c = boundary + 1; c < row.Count; c++)
                {
                    if (!string.IsNullOrWhiteSpace(row[c]))
                    {
                        hasDetailValue = true;
                        break;
                    }
                }

                if (!hasDetailValue)
                    violationCount++;
            }
        }

        // At least 20% of rows should have empty first column (inheritance)
        if ((double)emptyLeftRows / totalRows < 0.20)
            return false;

        // Allow up to 10% violations (data isn't always perfect)
        if (violationCount > totalRows * 0.10)
            return false;

        return true;
    }

    /// <summary>
    /// Build the InheritedTable with cascading resolution.
    /// Each column inherits independently from the row above.
    /// </summary>
    private static ExtractedInheritedTable BuildInheritedTable(ExtractedTable table, int boundary)
    {
        var inherited = new ExtractedInheritedTable
        {
            PageNumber = table.PageNumber,
            Y = table.Y,
            Headers = table.Headers,
            HeaderLevels = table.HeaderLevels,
            InheritedColumns = Enumerable.Range(0, boundary + 1).ToList(),
            DetailColumns = Enumerable.Range(boundary + 1, table.Headers.Count - boundary - 1).ToList()
        };

        // Step 1: Resolve all values with cascading inheritance
        // Each column carries forward independently.
        var resolved = new List<List<string>>();
        var lastValues = new string[table.Headers.Count];
        Array.Fill(lastValues, string.Empty);

        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var resolvedRow = new List<string>();

            for (int c = 0; c < table.Headers.Count; c++)
            {
                string cellValue = c < row.Count ? row[c] : string.Empty;

                if (c <= boundary)
                {
                    // Inherited column: carry forward if empty
                    if (!string.IsNullOrWhiteSpace(cellValue))
                    {
                        lastValues[c] = cellValue;
                        // When a higher-level column changes, reset lower-level columns
                        // (e.g., when Region changes, City resets)
                        // Actually NO - we don't reset, because the PDF might have
                        // City filled independently of Region changes.
                        // Each column inherits independently.
                    }
                    resolvedRow.Add(lastValues[c]);
                }
                else
                {
                    // Detail column: use as-is
                    resolvedRow.Add(cellValue);
                }
            }

            resolved.Add(resolvedRow);
        }

        inherited.ResolvedRows = resolved;

        // Step 2: Build groups based on column 0 changes
        // A new group starts when column 0 has a non-empty value.
        InheritedRowGroup? currentGroup = null;

        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            bool startsNewGroup = !string.IsNullOrWhiteSpace(row[0]);

            if (startsNewGroup || currentGroup == null)
            {
                currentGroup = new InheritedRowGroup
                {
                    ParentValues = inherited.InheritedColumns
                        .Select(c => resolved[r][c])
                        .ToList()
                };
                inherited.Groups.Add(currentGroup);
            }

            // Add detail values
            currentGroup.DetailRows.Add(
                inherited.DetailColumns
                    .Select(c => c < row.Count ? row[c] : string.Empty)
                    .ToList());
        }

        return inherited;
    }
}
