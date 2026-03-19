using ClosedXML.Excel;
using PDFAnalyzer.App.Models;

namespace PDFAnalyzer.App.Export;

/// <summary>
/// Exports extracted PDF data to an Excel workbook.
/// Each structural element gets its own sheet or section within a sheet.
/// Strategy:
///   - "Podsumowanie" sheet: overview of all pages and element counts
///   - One sheet per table (named by headers or page context)
///   - One sheet for all Key-Value groups
///   - One sheet for all Form Grids
///   - One sheet for all Text Blocks
/// </summary>
public class ExcelExporter
{
    public void Export(ExtractedDocument document, string outputPath)
    {
        using var workbook = new XLWorkbook();

        // 1. Summary sheet
        CreateSummarySheet(workbook, document);

        // 2. Tables - each table gets its own sheet
        int tableIndex = 1;
        foreach (var page in document.Pages)
        {
            foreach (var table in page.Elements.OfType<ExtractedTable>())
            {
                string sheetName = GenerateTableSheetName(table, page.PageNumber, tableIndex);
                CreateTableSheet(workbook, sheetName, table, page.PageNumber);
                tableIndex++;
            }

            // Inherited tables get two sheets: grouped view + resolved flat view
            foreach (var iTable in page.Elements.OfType<ExtractedInheritedTable>())
            {
                string baseName = GenerateInheritedTableSheetName(iTable, page.PageNumber, tableIndex);
                CreateInheritedTableSheet(workbook, baseName, iTable, page.PageNumber);
                tableIndex++;

                string resolvedName = $"{tableIndex}. Resolved";
                CreateResolvedTableSheet(workbook, resolvedName, iTable, page.PageNumber);
                tableIndex++;
            }
        }

        // 3. Key-Value groups - all on one sheet, separated by section
        var allKvGroups = document.Pages
            .SelectMany(p => p.Elements.OfType<ExtractedKeyValueGroup>()
                .Select(kv => (Page: p.PageNumber, Kv: kv)))
            .ToList();

        if (allKvGroups.Count > 0)
            CreateKeyValueSheet(workbook, allKvGroups);

        // 4. Form Grids - all on one sheet
        var allFormGrids = document.Pages
            .SelectMany(p => p.Elements.OfType<ExtractedFormGrid>()
                .Select(fg => (Page: p.PageNumber, Grid: fg)))
            .ToList();

        if (allFormGrids.Count > 0)
            CreateFormGridSheet(workbook, allFormGrids);

        // 5. Text blocks - all on one sheet
        var allTextBlocks = document.Pages
            .SelectMany(p => p.Elements.OfType<ExtractedTextBlock>()
                .Select(tb => (Page: p.PageNumber, Block: tb)))
            .ToList();

        if (allTextBlocks.Count > 0)
            CreateTextBlockSheet(workbook, allTextBlocks);

        workbook.SaveAs(outputPath);
    }

    private void CreateSummarySheet(XLWorkbook workbook, ExtractedDocument document)
    {
        var ws = workbook.Worksheets.Add("Podsumowanie");

        ws.Cell(1, 1).Value = "Plik";
        ws.Cell(1, 2).Value = document.FileName;
        ws.Cell(2, 1).Value = "Liczba stron";
        ws.Cell(2, 2).Value = document.TotalPages;

        StyleAsHeader(ws.Range(1, 1, 2, 1));

        int row = 4;
        ws.Cell(row, 1).Value = "Strona";
        ws.Cell(row, 2).Value = "Tabele (zwykle)";
        ws.Cell(row, 3).Value = "Klucz-Wartosc";
        ws.Cell(row, 4).Value = "Formularze";
        ws.Cell(row, 5).Value = "Tekst";
        StyleAsHeader(ws.Range(row, 1, row, 5));

        foreach (var page in document.Pages)
        {
            row++;
            ws.Cell(row, 1).Value = page.PageNumber;
            ws.Cell(row, 2).Value = page.Elements.OfType<ExtractedTable>().Count();
            ws.Cell(row, 3).Value = page.Elements.OfType<ExtractedKeyValueGroup>().Count();
            ws.Cell(row, 4).Value = page.Elements.OfType<ExtractedFormGrid>().Count();
            ws.Cell(row, 5).Value = page.Elements.OfType<ExtractedTextBlock>().Count();
        }

        // Totals row
        row++;
        ws.Cell(row, 1).Value = "RAZEM";
        ws.Cell(row, 1).Style.Font.Bold = true;
        for (int c = 2; c <= 5; c++)
        {
            ws.Cell(row, c).FormulaA1 = $"SUM({ws.Cell(5, c).Address}:{ws.Cell(row - 1, c).Address})";
            ws.Cell(row, c).Style.Font.Bold = true;
        }

        ws.Columns().AdjustToContents();
    }

    private void CreateTableSheet(XLWorkbook workbook, string sheetName, ExtractedTable table, int pageNumber)
    {
        var ws = workbook.Worksheets.Add(sheetName);

        // Info row
        ws.Cell(1, 1).Value = $"Strona {pageNumber}";
        ws.Cell(1, 1).Style.Font.Italic = true;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.Gray;

        int startRow = 3;
        int colCount = Math.Max(table.Headers.Count, 1);

        // Multi-level hierarchical headers
        if (table.HeaderLevels != null && table.HeaderLevels.Count > 0)
        {
            int totalLevels = table.HeaderLevels.Count; // group levels
            int headerEndRow = startRow + totalLevels; // last row = sub-headers

            // Collect all grouped columns across all levels
            var allGroupedCols = new HashSet<int>();
            foreach (var level in table.HeaderLevels)
                foreach (var g in level)
                    for (int c = g.StartColumn; c <= g.EndColumn; c++)
                        allGroupedCols.Add(c);

            // Render each group level
            for (int lvl = 0; lvl < totalLevels; lvl++)
            {
                int currentRow = startRow + lvl;
                var groups = table.HeaderLevels[lvl];

                foreach (var group in groups)
                {
                    int sc = group.StartColumn + 1;
                    int ec = group.EndColumn + 1;

                    ws.Cell(currentRow, sc).Value = group.Name;
                    if (ec > sc)
                        ws.Range(currentRow, sc, currentRow, ec).Merge();
                }

                StyleAsHeader(ws.Range(currentRow, 1, currentRow, colCount));
            }

            // Columns NOT covered by any group: merge vertically from top to bottom header row
            for (int c = 0; c < table.Headers.Count; c++)
            {
                if (!allGroupedCols.Contains(c) && !string.IsNullOrWhiteSpace(table.Headers[c]))
                {
                    ws.Cell(startRow, c + 1).Value = table.Headers[c];
                    if (totalLevels > 0)
                    {
                        ws.Range(startRow, c + 1, headerEndRow, c + 1).Merge();
                        ws.Cell(startRow, c + 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    }
                }
            }

            // Bottom row: sub-headers (column names)
            for (int c = 0; c < table.Headers.Count; c++)
            {
                if (allGroupedCols.Contains(c))
                    ws.Cell(headerEndRow, c + 1).Value = table.Headers[c];
            }
            StyleAsSubHeader(ws.Range(headerEndRow, 1, headerEndRow, colCount));

            startRow = headerEndRow; // data starts after this
        }
        else
        {
            // Flat headers (single row)
            for (int c = 0; c < table.Headers.Count; c++)
            {
                ws.Cell(startRow, c + 1).Value = table.Headers[c];
            }
            StyleAsHeader(ws.Range(startRow, 1, startRow, colCount));
        }

        // Data rows
        for (int r = 0; r < table.Rows.Count; r++)
        {
            for (int c = 0; c < table.Rows[r].Count; c++)
            {
                var cell = ws.Cell(startRow + 1 + r, c + 1);
                string value = table.Rows[r][c];
                SetCellValue(cell, value);
            }

            // Alternate row coloring
            if (r % 2 == 1)
            {
                ws.Range(startRow + 1 + r, 1, startRow + 1 + r, table.Headers.Count)
                    .Style.Fill.BackgroundColor = XLColor.FromArgb(245, 247, 250);
            }
        }

        // Add borders
        var dataRange = ws.Range(startRow, 1, startRow + table.Rows.Count, Math.Max(table.Headers.Count, 1));
        dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        ws.Columns().AdjustToContents();
    }

    private void CreateInheritedTableSheet(XLWorkbook workbook, string sheetName,
        ExtractedInheritedTable table, int pageNumber)
    {
        var ws = workbook.Worksheets.Add(sheetName);

        ws.Cell(1, 1).Value = $"Strona {pageNumber} | Tabela z dziedziczeniem wartosci";
        ws.Cell(1, 1).Style.Font.Italic = true;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.Gray;

        ws.Cell(2, 1).Value = $"Kolumny dziedziczone: {string.Join(", ", table.InheritedColumns.Select(c => table.Headers[c]))}";
        ws.Cell(2, 1).Style.Font.Italic = true;
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromArgb(39, 174, 96);

        int startRow = 4;
        int colCount = table.Headers.Count;

        // Headers
        for (int c = 0; c < colCount; c++)
        {
            ws.Cell(startRow, c + 1).Value = table.Headers[c];
        }

        // Color inherited header columns differently
        foreach (int c in table.InheritedColumns)
        {
            ws.Cell(startRow, c + 1).Style.Fill.BackgroundColor = XLColor.FromArgb(39, 174, 96);
            ws.Cell(startRow, c + 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(startRow, c + 1).Style.Font.Bold = true;
        }
        foreach (int c in table.DetailColumns)
        {
            ws.Cell(startRow, c + 1).Style.Fill.BackgroundColor = XLColor.FromArgb(44, 62, 80);
            ws.Cell(startRow, c + 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(startRow, c + 1).Style.Font.Bold = true;
        }

        var headerRange = ws.Range(startRow, 1, startRow, colCount);
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        // Grouped data with merged cells for inherited columns
        int dataRow = startRow + 1;
        foreach (var group in table.Groups)
        {
            int groupStartRow = dataRow;
            int groupRowCount = group.DetailRows.Count;

            for (int dr = 0; dr < groupRowCount; dr++)
            {
                // Detail columns: always write
                for (int d = 0; d < group.DetailRows[dr].Count && d < table.DetailColumns.Count; d++)
                {
                    int col = table.DetailColumns[d];
                    var cell = ws.Cell(dataRow, col + 1);
                    SetCellValue(cell, group.DetailRows[dr][d]);
                }

                dataRow++;
            }

            // Inherited columns: write in first row, merge if multiple rows
            for (int p = 0; p < group.ParentValues.Count && p < table.InheritedColumns.Count; p++)
            {
                int col = table.InheritedColumns[p];
                ws.Cell(groupStartRow, col + 1).Value = group.ParentValues[p];
                ws.Cell(groupStartRow, col + 1).Style.Font.Bold = true;

                if (groupRowCount > 1)
                {
                    var mergeRange = ws.Range(groupStartRow, col + 1, groupStartRow + groupRowCount - 1, col + 1);
                    mergeRange.Merge();
                    mergeRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
                }
            }

            // Alternate group background
            int groupIndex = table.Groups.IndexOf(group);
            if (groupIndex % 2 == 1)
            {
                ws.Range(groupStartRow, 1, groupStartRow + groupRowCount - 1, colCount)
                    .Style.Fill.BackgroundColor = XLColor.FromArgb(245, 247, 250);
            }

            // Group separator border
            ws.Range(groupStartRow, 1, groupStartRow, colCount)
                .Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        // Final border
        var dataRange = ws.Range(startRow, 1, dataRow - 1, colCount);
        dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Hair;

        ws.Columns().AdjustToContents();
    }

    private void CreateResolvedTableSheet(XLWorkbook workbook, string sheetName,
        ExtractedInheritedTable table, int pageNumber)
    {
        var ws = workbook.Worksheets.Add(sheetName);

        ws.Cell(1, 1).Value = $"Strona {pageNumber} | Widok plaski (wartosci dziedziczone wypelnione)";
        ws.Cell(1, 1).Style.Font.Italic = true;
        ws.Cell(1, 1).Style.Font.FontColor = XLColor.Gray;

        int startRow = 3;
        int colCount = table.Headers.Count;

        // Headers
        for (int c = 0; c < colCount; c++)
        {
            ws.Cell(startRow, c + 1).Value = table.Headers[c];
        }
        StyleAsHeader(ws.Range(startRow, 1, startRow, colCount));

        // Resolved rows (all values filled)
        for (int r = 0; r < table.ResolvedRows.Count; r++)
        {
            for (int c = 0; c < table.ResolvedRows[r].Count && c < colCount; c++)
            {
                var cell = ws.Cell(startRow + 1 + r, c + 1);
                SetCellValue(cell, table.ResolvedRows[r][c]);
            }

            if (r % 2 == 1)
            {
                ws.Range(startRow + 1 + r, 1, startRow + 1 + r, colCount)
                    .Style.Fill.BackgroundColor = XLColor.FromArgb(245, 247, 250);
            }
        }

        var dataRange = ws.Range(startRow, 1, startRow + table.ResolvedRows.Count, colCount);
        dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        ws.Columns().AdjustToContents();
    }

    private void CreateKeyValueSheet(XLWorkbook workbook,
        List<(int Page, ExtractedKeyValueGroup Kv)> groups)
    {
        var ws = workbook.Worksheets.Add("Klucz-Wartosc");

        int row = 1;

        foreach (var (page, kv) in groups)
        {
            // Section header
            if (!string.IsNullOrWhiteSpace(kv.SectionName))
            {
                ws.Cell(row, 1).Value = kv.SectionName;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 12;
                ws.Cell(row, 3).Value = $"(Strona {page})";
                ws.Cell(row, 3).Style.Font.Italic = true;
                ws.Cell(row, 3).Style.Font.FontColor = XLColor.Gray;
                row++;
            }
            else
            {
                ws.Cell(row, 3).Value = $"(Strona {page})";
                ws.Cell(row, 3).Style.Font.Italic = true;
                ws.Cell(row, 3).Style.Font.FontColor = XLColor.Gray;
            }

            // Column headers
            ws.Cell(row, 1).Value = "Klucz";
            ws.Cell(row, 2).Value = "Wartosc";
            StyleAsHeader(ws.Range(row, 1, row, 2));
            row++;

            // Items
            foreach (var item in kv.Items)
            {
                ws.Cell(row, 1).Value = item.Key;
                ws.Cell(row, 1).Style.Font.Bold = true;

                var cell = ws.Cell(row, 2);
                SetCellValue(cell, item.Value);
                row++;
            }

            // Add borders around this group
            if (kv.Items.Count > 0)
            {
                var groupRange = ws.Range(row - kv.Items.Count - 1, 1, row - 1, 2);
                groupRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                groupRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            }

            row += 2; // gap between groups
        }

        ws.Column(1).Width = 30;
        ws.Column(2).Width = 60;
    }

    private void CreateFormGridSheet(XLWorkbook workbook,
        List<(int Page, ExtractedFormGrid Grid)> grids)
    {
        var ws = workbook.Worksheets.Add("Formularze");

        int row = 1;

        // Headers
        ws.Cell(row, 1).Value = "Nr pola";
        ws.Cell(row, 2).Value = "Etykieta";
        ws.Cell(row, 3).Value = "Wartosc";
        ws.Cell(row, 4).Value = "Strona";
        StyleAsHeader(ws.Range(row, 1, row, 4));
        row++;

        foreach (var (page, grid) in grids)
        {
            foreach (var cell in grid.Cells)
            {
                ws.Cell(row, 1).Value = cell.FieldNumber ?? "";
                ws.Cell(row, 2).Value = cell.Label;
                var valueCell = ws.Cell(row, 3);
                SetCellValue(valueCell, cell.Value);
                ws.Cell(row, 4).Value = page;

                // Alternate row coloring
                if (row % 2 == 0)
                {
                    ws.Range(row, 1, row, 4).Style.Fill.BackgroundColor = XLColor.FromArgb(245, 247, 250);
                }

                row++;
            }

            // Visual separator between grids
            ws.Range(row, 1, row, 4).Style.Border.TopBorder = XLBorderStyleValues.Medium;
            ws.Range(row, 1, row, 4).Style.Border.TopBorderColor = XLColor.FromArgb(44, 62, 80);
            row++;
        }

        // Add full border
        var dataRange = ws.Range(1, 1, row - 1, 4);
        dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

        ws.Column(1).Width = 10;
        ws.Column(2).Width = 50;
        ws.Column(3).Width = 50;
        ws.Column(4).Width = 8;
    }

    private void CreateTextBlockSheet(XLWorkbook workbook,
        List<(int Page, ExtractedTextBlock Block)> blocks)
    {
        var ws = workbook.Worksheets.Add("Tekst");

        ws.Cell(1, 1).Value = "Strona";
        ws.Cell(1, 2).Value = "Tekst";
        StyleAsHeader(ws.Range(1, 1, 1, 2));

        int row = 2;
        foreach (var (page, block) in blocks)
        {
            ws.Cell(row, 1).Value = page;
            ws.Cell(row, 2).Value = block.Text;
            ws.Cell(row, 2).Style.Alignment.WrapText = true;

            if (row % 2 == 0)
                ws.Range(row, 1, row, 2).Style.Fill.BackgroundColor = XLColor.FromArgb(245, 247, 250);

            row++;
        }

        ws.Column(1).Width = 8;
        ws.Column(2).Width = 100;
    }

    #region Helpers

    private string GenerateTableSheetName(ExtractedTable table, int pageNumber, int tableIndex)
    {
        // Try to create a meaningful name from headers
        var meaningfulHeaders = table.Headers
            .Where(h => !string.IsNullOrWhiteSpace(h) && h.Length > 2)
            .Take(2)
            .ToList();

        string baseName;
        if (meaningfulHeaders.Count > 0)
        {
            baseName = string.Join(" ", meaningfulHeaders);
            if (baseName.Length > 25)
                baseName = baseName[..25];
        }
        else
        {
            baseName = $"Tabela";
        }

        // Excel sheet names max 31 chars, no special characters
        string name = $"{tableIndex}. {baseName}";
        name = SanitizeSheetName(name);

        if (name.Length > 31)
            name = name[..31];

        return name;
    }

    private string GenerateInheritedTableSheetName(ExtractedInheritedTable table, int pageNumber, int tableIndex)
    {
        var meaningfulHeaders = table.Headers
            .Where(h => !string.IsNullOrWhiteSpace(h) && h.Length > 2)
            .Take(2)
            .ToList();

        string baseName = meaningfulHeaders.Count > 0
            ? string.Join(" ", meaningfulHeaders)
            : "Tabela dziedziczona";

        if (baseName.Length > 20)
            baseName = baseName[..20];

        string name = $"{tableIndex}. {baseName}";
        name = SanitizeSheetName(name);
        return name.Length > 31 ? name[..31] : name;
    }

    private static string SanitizeSheetName(string name)
    {
        var invalid = new[] { '\\', '/', '*', '[', ']', ':', '?' };
        foreach (char c in invalid)
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static void StyleAsHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = XLColor.FromArgb(44, 62, 80);
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
    }

    private static void StyleAsSubHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = XLColor.FromArgb(74, 96, 117);
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
    }

    /// <summary>
    /// Try to parse numeric values and set them as numbers in Excel, otherwise set as string.
    /// </summary>
    private static void SetCellValue(IXLCell cell, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            cell.Value = "";
            return;
        }

        string trimmed = value.Trim();

        // Try to parse Polish-format numbers like "127 125,00" or "23%"
        if (trimmed.EndsWith("%") && double.TryParse(
                trimmed.TrimEnd('%').Replace(" ", "").Replace(",", "."),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double pct))
        {
            cell.Value = pct / 100.0;
            cell.Style.NumberFormat.Format = "0%";
            return;
        }

        // Try to parse PLN amounts: "127 125,00 PLN" or "127 125,00"
        string noPln = trimmed.Replace("PLN", "").Trim();
        string normalized = noPln.Replace(" ", "").Replace(",", ".");
        if (double.TryParse(normalized,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double num)
            && noPln != trimmed.Replace(" ", "")) // has spaces = likely formatted number
        {
            cell.Value = num;
            if (trimmed.Contains("PLN"))
                cell.Style.NumberFormat.Format = "#,##0.00 \"PLN\"";
            else
                cell.Style.NumberFormat.Format = "#,##0.00";
            return;
        }

        cell.Value = trimmed;
    }

    #endregion
}
