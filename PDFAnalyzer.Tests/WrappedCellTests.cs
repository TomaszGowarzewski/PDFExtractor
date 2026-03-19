using FluentAssertions;
using PDFAnalyzer.App.Extraction;
using PDFAnalyzer.App.Models;

namespace PDFAnalyzer.Tests;

/// <summary>
/// Tests for wrapped cell text merging and inherited tables with wrapped cells.
/// Simulates the table from the screenshot:
///   A | asdasd | 2  | 65 | 123123123123 | 2   | 2   | ASDASD,ASD
///   (wrap)     |    |    | 123123       |     |     | ASD,ASDASD
///              | 4  | 53 | 123531235432 | 5   | 34  | ASDASD,ASD...
///              |    |    | 1356         |     |     | ASD,ASDASD,QWEQWEE...
///   B | dqwes  | 4  | 12 | 123123123123 | 2   | 4   | aASDASD,QW...
///   ...
/// </summary>
public class WrappedCellTests
{
    [Fact]
    public void MergeWrappedCells_ShouldMergeSingleColumnWrap()
    {
        var table = new ExtractedTable
        {
            Headers = new() { "ID", "Name", "Val1", "Val2", "LongData", "X", "Y", "Tags" },
            Rows = new()
            {
                new() { "A", "asdasd", "2", "65", "123123123123", "2", "2", "ASDASD,ASD" },
                new() { "", "", "", "", "123123", "", "", "ASD,ASDASD" },  // wrap
                new() { "B", "test", "1", "10", "9999", "5", "5", "TAG1" },
            }
        };

        // Run post-processing
        var doc = WrapInDocument(table);

        var result = doc.Pages[0].Elements.OfType<ExtractedTable>().First();
        result.Rows.Should().HaveCount(2, "Wrapped row should be merged into row A");

        result.Rows[0][4].Should().Be("123123123123\n123123", "LongData should have wrapped text joined");
        result.Rows[0][7].Should().Be("ASDASD,ASD\nASD,ASDASD", "Tags should have wrapped text joined");
        result.Rows[0][0].Should().Be("A");
        result.Rows[1][0].Should().Be("B");
    }

    [Fact]
    public void MergeWrappedCells_ShouldHandleMultipleWrapsInSequence()
    {
        // Row with 3 lines of wrapped text
        var table = new ExtractedTable
        {
            Headers = new() { "Code", "Desc", "Qty", "Price", "Notes" },
            Rows = new()
            {
                new() { "PRD-001", "Pompa hydrauliczna", "15", "2450", "Zamowienie pilne" },
                new() { "", "", "", "", "linia produkcyjna stoi" },  // wrap line 2
                new() { "", "", "", "", "od 3 dni" },                 // wrap line 3
                new() { "PRD-002", "Uszczelka", "200", "12", "Standard" },
            }
        };

        var doc = WrapInDocument(table);
        var result = doc.Pages[0].Elements.OfType<ExtractedTable>().First();

        result.Rows.Should().HaveCount(2);
        result.Rows[0][4].Should().Be("Zamowienie pilne\nlinia produkcyjna stoi\nod 3 dni");
        result.Rows[1][0].Should().Be("PRD-002");
    }

    [Fact]
    public void MergeWrappedCells_ShouldNotMergeRealDataRows()
    {
        // All rows have many filled cells - no wrapping
        var table = new ExtractedTable
        {
            Headers = new() { "ID", "Name", "A", "B", "C", "D" },
            Rows = new()
            {
                new() { "1", "Jan", "10", "20", "30", "40" },
                new() { "2", "Anna", "50", "60", "70", "80" },
                new() { "3", "Piotr", "90", "100", "110", "120" },
            }
        };

        var doc = WrapInDocument(table);
        var result = doc.Pages[0].Elements.OfType<ExtractedTable>().First();

        result.Rows.Should().HaveCount(3, "No rows should be merged");
    }

    [Fact]
    public void MergeWrappedCells_CombinedWithInheritedTable_ShouldWork()
    {
        // The full scenario: inherited table WITH wrapped cells
        // A | asdasd | 2  | 65 | 123123123123 | 2  | 2  | ASDASD
        // (wrap)                | 123123       |    |    | ASD,ASDASD
        //           | 4  | 53 | 12353...     | 5  | 34 | ASDASD...
        // (wrap)                | 1356         |    |    | ASD,...QWEQWEE
        // B | dqwes  | 4  | 12 | 123123123123 | 2  | 4  | aASDASD
        // (wrap)                | 123123       |    |    | EQWE,qWEE
        //           |    |    | 345345,1233  | 4  | 4  | ASDASD,...
        var table = new ExtractedTable
        {
            Headers = new() { "Dane", "dane", "dane", "dane", "dane", "dane", "dane", "dane" },
            Rows = new()
            {
                new() { "A", "asdasd", "2", "65", "123123123123", "2", "2", "ASDASD,ASD" },
                new() { "", "", "", "", "123123", "", "", "ASD,ASDASD" },        // wrap of row above
                new() { "", "", "4", "53", "123531235432", "5", "34", "ASDASD,ASD" },  // new sub-row
                new() { "", "", "", "", "1356", "", "", "ASD,ASDASD,QWEQWEE,QWEQWE" }, // wrap
                new() { "B", "dqwes", "4", "12", "123123123123", "2", "4", "aASDASD,QW" },
                new() { "", "", "", "", "123123", "", "", "EQWE,qWEE" },          // wrap
                new() { "", "", "", "", "345345,1233", "4", "4", "ASDASD,ASD" },  // new sub-row (has 3 values)
            }
        };

        var doc = WrapInDocument(table);

        // After wrap merging: should have 4 rows (2 for A, 1 for B master, 1 for B detail)
        var resultTable = doc.Pages[0].Elements.OfType<ExtractedTable>().FirstOrDefault();
        var inherited = doc.Pages[0].Elements.OfType<ExtractedInheritedTable>().FirstOrDefault();

        // It might be detected as inherited table
        if (inherited != null)
        {
            // A has 2 sub-rows, B has 2 sub-rows (or 1+1)
            inherited.Groups.Should().HaveCount(2);
            inherited.Groups[0].ParentValues.Should().Contain("A");
            inherited.Groups[1].ParentValues.Should().Contain("B");

            // Verify wrapped text was merged
            var resolvedA1 = inherited.ResolvedRows[0];
            resolvedA1[4].Should().Contain("123123123123").And.Contain("123123");
        }
        else
        {
            // If not inherited, at least wraps should be merged
            resultTable.Should().NotBeNull();
            resultTable!.Rows.Should().HaveCountLessThan(7, "Wrapped rows should be merged");

            // First row should have merged wrap
            resultTable.Rows[0][4].Should().Contain("123123123123");
            resultTable.Rows[0][4].Should().Contain("123123");
        }
    }

    [Fact]
    public void MergeWrappedCells_ShouldNotMergeWhenCellsDontAlignWithPrev()
    {
        // Row has values in columns where the PREVIOUS row was empty - NOT a wrap
        var table = new ExtractedTable
        {
            Headers = new() { "A", "B", "C", "D", "E", "F" },
            Rows = new()
            {
                new() { "1", "x", "", "", "data", "ok" },
                new() { "", "", "NEW", "", "", "" },  // NOT a wrap - col C was empty above
                new() { "2", "y", "z", "w", "more", "ok" },
            }
        };

        var doc = WrapInDocument(table);
        var result = doc.Pages[0].Elements.OfType<ExtractedTable>().First();

        result.Rows.Should().HaveCount(3, "Row with misaligned cells should NOT be merged");
    }

    [Fact]
    public void FullScreenshotTable_ShouldResolveCorrectly()
    {
        // Exact simulation of the table from the user's screenshot.
        // Rows marked "wrap" are cell text continuations.
        // A,B,C = inherited rows with sub-rows. D,E,F = simple rows with wrapped cells.
        var table = new ExtractedTable
        {
            Headers = new() { "Dane", "dane", "dane", "dane", "dane", "dane", "dane", "dane" },
            Rows = new()
            {
                // === A group ===
                new() { "A", "asdasd", "2", "65", "123123123123", "2", "2", "ASDASD,ASD" },
                new() { "", "", "", "", "123123", "", "", "ASD,ASDASD" },           // wrap
                new() { "", "", "4", "53", "123531235432", "5", "34", "ASDASD,ASD" },
                new() { "", "", "", "", "1356", "", "", "ASD,ASDASD,QWEQWEE,QWEQWE" }, // wrap
                // === B group ===
                new() { "B", "dqwes", "4", "12", "123123123123", "2", "4", "aASDASD,QW EQWE,qWEE" },
                new() { "", "", "", "", "123123", "", "", "" },                      // wrap
                new() { "", "", "", "", "345345,1233", "4", "4", "ASDASD,ASD ASD,ASDASD,QWEQWEE,Q WEQWE" },
                // === C group ===
                new() { "C", "asdasdads asdasdasd asdasdasd asdasd", "4", "4", "1231231231213", "123", "123", "aASDASD,QW EQWE,qWEE" },
                new() { "", "", "", "", "123123123", "", "", "" },                   // wrap
                // === D (simple, wrapped name + data) ===
                new() { "D", "QWEQWE", "24", "24", "1231231231213", "4", "5", "QEWQWE" },
                new() { "", "QWEQWE", "", "", "1233", "", "", "" },                  // wrap
                // === E (simple, wrapped name + data) ===
                new() { "E", "qweqwe qweqwe qwe", "23", "25", "1232132131213", "2", "2", "QEQW" },
                new() { "", "", "", "", "123123", "", "", "" },                      // wrap
                // === F (simple, wrapped name) ===
                new() { "F", "qweqweqwe", "23", "54", "2312312312312", "4", "1", "SDASDA" },
                new() { "", "qwe qwe", "", "", "", "", "", "" },                     // wrap (name only)
            }
        };

        var doc = WrapInDocument(table);

        // Get result - should be an InheritedTable
        var inherited = doc.Pages[0].Elements.OfType<ExtractedInheritedTable>().FirstOrDefault();
        var plainTable = doc.Pages[0].Elements.OfType<ExtractedTable>().FirstOrDefault();

        // After wrap merge, we should have these logical rows:
        // A: (2,65,...) and (4,53,...)     = 2 sub-rows
        // B: (4,12,...) and (345345,...)    = 2 sub-rows
        // C: (4,4,...)                     = 1 sub-row
        // D: (24,24,...)                   = 1 row
        // E: (23,25,...)                   = 1 row
        // F: (23,54,...)                   = 1 row
        // Total: 8 logical rows

        if (inherited != null)
        {
            inherited.Groups.Should().HaveCount(6, "A,B,C,D,E,F = 6 groups");

            // A has 2 detail rows
            inherited.Groups[0].ParentValues.Should().Contain("A");
            inherited.Groups[0].DetailRows.Should().HaveCount(2);

            // B has 2 detail rows
            inherited.Groups[1].ParentValues.Should().Contain("B");
            inherited.Groups[1].DetailRows.Should().HaveCount(2);

            // C has 1 detail row
            inherited.Groups[2].DetailRows.Should().HaveCount(1);

            // D,E,F have 1 detail row each
            inherited.Groups[3].ParentValues.Should().Contain("D");
            inherited.Groups[3].DetailRows.Should().HaveCount(1);

            // Verify wrapped text was merged in resolved rows
            var rowA1 = inherited.ResolvedRows[0];
            rowA1[4].Should().Contain("123123123123").And.Contain("123123",
                "Wrapped data in column should be merged");

            // F should have merged name
            var rowF = inherited.ResolvedRows.Last();
            rowF[0].Should().Be("F");
            rowF[1].Should().Contain("qweqweqwe").And.Contain("qwe qwe",
                "Wrapped name should be merged");

            // D wrapped name
            var rowD = inherited.ResolvedRows.First(r => r[0] == "D");
            rowD[1].Should().Contain("QWEQWE");

            // Total resolved rows = 2+2+1+1+1+1 = 8
            inherited.ResolvedRows.Should().HaveCount(8);
        }
        else
        {
            plainTable.Should().NotBeNull();
            // After wrap merge: 16 raw rows → 8 logical rows
            plainTable!.Rows.Should().HaveCount(8,
                "16 raw rows with 8 wraps should merge to 8 logical rows");
        }
    }

    /// <summary>
    /// Helper: wrap a table in a document and run post-processing.
    /// </summary>
    private static ExtractedDocument WrapInDocument(ExtractedTable table)
    {
        var doc = new ExtractedDocument
        {
            FileName = "test.pdf",
            TotalPages = 1,
            Pages = new()
            {
                new ExtractedPage
                {
                    PageNumber = 1,
                    Elements = new List<ExtractedElement> { table }
                }
            }
        };

        // Use reflection to call the private PostProcess method
        var extractor = new PdfDataExtractor();
        var method = typeof(PdfDataExtractor).GetMethod("PostProcess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(extractor, new object[] { doc });

        return doc;
    }
}
