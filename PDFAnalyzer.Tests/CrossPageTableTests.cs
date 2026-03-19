using FluentAssertions;
using PDFAnalyzer.App.Extraction;
using PDFAnalyzer.App.Models;

namespace PDFAnalyzer.Tests;

/// <summary>
/// Tests for cross-page table merging scenarios.
/// </summary>
public class CrossPageTableTests
{
    [Fact]
    public void CrossPage_SameHeaders_ShouldMerge()
    {
        var doc = CreateMultiPageDoc(
            page1Table: new ExtractedTable
            {
                Headers = new() { "ID", "Name", "Value" },
                Rows = new()
                {
                    new() { "A", "Alpha", "100" },
                    new() { "B", "Beta", "200" },
                }
            },
            page2Table: new ExtractedTable
            {
                Headers = new() { "ID", "Name", "Value" }, // same headers
                Rows = new()
                {
                    new() { "C", "Gamma", "300" },
                    new() { "D", "Delta", "400" },
                }
            }
        );

        PostProcess(doc);

        var tables = doc.Pages.SelectMany(p => p.Elements.OfType<ExtractedTable>()).ToList();
        tables.Should().HaveCount(1, "Two tables with same headers should merge into one");
        tables[0].Rows.Should().HaveCount(4);
        tables[0].Rows[2][0].Should().Be("C");
        tables[0].Rows[3][0].Should().Be("D");
    }

    [Fact]
    public void CrossPage_HeaderlessContinuation_ShouldMerge()
    {
        // Page 2 has no headers - the "headers" are actually data
        var doc = CreateMultiPageDoc(
            page1Table: new ExtractedTable
            {
                Headers = new() { "ID", "Name", "Value" },
                Rows = new()
                {
                    new() { "A", "Alpha", "100" },
                    new() { "B", "Beta", "200" },
                }
            },
            page2Table: new ExtractedTable
            {
                Headers = new() { "C", "Gamma", "300" }, // looks like data!
                Rows = new()
                {
                    new() { "D", "Delta", "400" },
                }
            }
        );

        PostProcess(doc);

        var tables = doc.Pages.SelectMany(p => p.Elements.OfType<ExtractedTable>()).ToList();
        tables.Should().HaveCount(1, "Headerless continuation should merge");
        tables[0].Headers[0].Should().Be("ID", "Original headers preserved");
        tables[0].Rows.Should().HaveCount(4, "Should have A,B,C,D");
        tables[0].Rows[2][0].Should().Be("C");
    }

    [Fact]
    public void CrossPage_ThreePages_ShouldMergeAll()
    {
        var doc = new ExtractedDocument
        {
            FileName = "test.pdf",
            TotalPages = 3,
            Pages = new()
            {
                new ExtractedPage
                {
                    PageNumber = 1,
                    Elements = new List<ExtractedElement>
                    {
                        new ExtractedTable
                        {
                            Headers = new() { "ID", "Name", "Score" },
                            Rows = new()
                            {
                                new() { "A", "Alpha", "10" },
                                new() { "B", "Beta", "20" },
                            }
                        }
                    }
                },
                new ExtractedPage
                {
                    PageNumber = 2,
                    Elements = new List<ExtractedElement>
                    {
                        new ExtractedTable
                        {
                            Headers = new() { "C", "Gamma", "30" }, // headerless continuation
                            Rows = new()
                            {
                                new() { "D", "Delta", "40" },
                            }
                        }
                    }
                },
                new ExtractedPage
                {
                    PageNumber = 3,
                    Elements = new List<ExtractedElement>
                    {
                        new ExtractedTable
                        {
                            Headers = new() { "E", "Epsilon", "50" }, // headerless continuation
                            Rows = new()
                            {
                                new() { "F", "Zeta", "60" },
                            }
                        }
                    }
                }
            }
        };

        PostProcess(doc);

        var tables = doc.Pages.SelectMany(p => p.Elements.OfType<ExtractedTable>()).ToList();
        tables.Should().HaveCount(1, "3-page table should merge into one");
        tables[0].Rows.Should().HaveCount(6, "A through F");
        tables[0].Headers[0].Should().Be("ID");
    }

    [Fact]
    public void CrossPage_WithWrappedRows_ShouldMergeAndResolveWraps()
    {
        // Page 1 ends with a row, page 2 starts with its wrap continuation
        var doc = CreateMultiPageDoc(
            page1Table: new ExtractedTable
            {
                Headers = new() { "ID", "Name", "Data", "Long", "Tags" },
                Rows = new()
                {
                    new() { "A", "Alpha", "10", "123456789012", "tag1,tag2" },
                }
            },
            page2Table: new ExtractedTable
            {
                // First "header" is actually wrap of last row from page 1
                Headers = new() { "", "", "", "345678", "tag3" },
                Rows = new()
                {
                    new() { "B", "Beta", "20", "999888777", "tag4" },
                }
            }
        );

        PostProcess(doc);

        var tables = doc.Pages.SelectMany(p => p.Elements.OfType<ExtractedTable>()).ToList();
        tables.Should().HaveCount(1);

        // After merge + wrap: should have 2 logical rows (A and B)
        // The wrap row should be merged into A's row
        tables[0].Rows.Should().HaveCount(2);
        tables[0].Rows[0][0].Should().Be("A");
        tables[0].Rows[0][3].Should().Contain("345678", "Wrapped long data should be merged");
        tables[0].Rows[1][0].Should().Be("B");
    }

    [Fact]
    public void CrossPage_DifferentTables_ShouldNotMerge()
    {
        var doc = CreateMultiPageDoc(
            page1Table: new ExtractedTable
            {
                Headers = new() { "ID", "Name", "Value" },
                Rows = new() { new() { "A", "Alpha", "100" } }
            },
            page2Table: new ExtractedTable
            {
                Headers = new() { "Code", "Description", "Price", "Qty" }, // different column count
                Rows = new() { new() { "X", "Widget", "50", "10" } }
            }
        );

        PostProcess(doc);

        var tables = doc.Pages.SelectMany(p => p.Elements.OfType<ExtractedTable>()).ToList();
        tables.Should().HaveCount(2, "Different tables should NOT merge");
    }

    #region Helpers

    private static ExtractedDocument CreateMultiPageDoc(ExtractedTable page1Table, ExtractedTable page2Table)
    {
        return new ExtractedDocument
        {
            FileName = "test.pdf",
            TotalPages = 2,
            Pages = new()
            {
                new ExtractedPage
                {
                    PageNumber = 1,
                    Elements = new List<ExtractedElement> { page1Table }
                },
                new ExtractedPage
                {
                    PageNumber = 2,
                    Elements = new List<ExtractedElement> { page2Table }
                }
            }
        };
    }

    private static void PostProcess(ExtractedDocument doc)
    {
        var extractor = new PdfDataExtractor();
        var method = typeof(PdfDataExtractor).GetMethod("PostProcess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(extractor, new object[] { doc });
    }

    #endregion
}
