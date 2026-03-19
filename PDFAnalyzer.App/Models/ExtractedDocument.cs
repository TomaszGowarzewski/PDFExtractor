namespace PDFAnalyzer.App.Models;

/// <summary>
/// Root result of extracting data from a PDF document.
/// </summary>
public class ExtractedDocument
{
    public string FileName { get; set; } = string.Empty;
    public int TotalPages { get; set; }
    public List<ExtractedPage> Pages { get; set; } = new();
}

public class ExtractedPage
{
    public int PageNumber { get; set; }
    public List<ExtractedElement> Elements { get; set; } = new();
}

/// <summary>
/// Base class for all extracted elements.
/// </summary>
public abstract class ExtractedElement
{
    public string Type { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public double Y { get; set; } // vertical position for ordering
}

/// <summary>
/// A multi-column table (e.g. schedule, team list, pricing).
/// </summary>
public class ExtractedTable : ExtractedElement
{
    public List<string> Headers { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();

    /// <summary>
    /// Header group levels (nested/hierarchical headers), from top to bottom.
    /// Each level is a list of groups that span columns at that level.
    /// Level 0 = topmost parent groups, Level 1 = sub-groups, etc.
    /// The bottom-most level is always Headers itself.
    /// Null if headers are flat (single level).
    ///
    /// Example with 3 levels:
    ///   Level 0: ["Finanse" spanning cols 1-4]
    ///   Level 1: ["Koszty" spanning 1-2, "Dochody" spanning 3-4]
    ///   Headers: ["Zrodla", "Koszty uzys. przych. zl", "Dochod (b-c) zl", "Strata (c-b) zl", "Zaliczka"]
    /// </summary>
    public List<List<HeaderGroup>>? HeaderLevels { get; set; }

    public ExtractedTable() { Type = "Table"; }
}

/// <summary>
/// A header that spans multiple sub-columns at one level of the hierarchy.
/// </summary>
public class HeaderGroup
{
    public string Name { get; set; } = string.Empty;
    public int StartColumn { get; set; }
    public int EndColumn { get; set; } // inclusive
}

/// <summary>
/// A set of key-value pairs (e.g. company details, technical specs).
/// </summary>
public class ExtractedKeyValueGroup : ExtractedElement
{
    public string? SectionName { get; set; }
    public List<KeyValueItem> Items { get; set; } = new();

    public ExtractedKeyValueGroup() { Type = "KeyValueGroup"; }
}

public class KeyValueItem
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// A form-style grid of labeled cells (e.g. PIT-37 fields).
/// </summary>
public class ExtractedFormGrid : ExtractedElement
{
    public List<FormCell> Cells { get; set; } = new();

    public ExtractedFormGrid() { Type = "FormGrid"; }
}

public class FormCell
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? FieldNumber { get; set; }
}

/// <summary>
/// A table where empty cells in left columns inherit values from the row above.
/// Grouped by a primary key column (first non-empty column defines a "master row").
/// Each group has one master row + zero or more detail rows.
/// </summary>
public class ExtractedInheritedTable : ExtractedElement
{
    public List<string> Headers { get; set; } = new();
    public List<List<HeaderGroup>>? HeaderLevels { get; set; }

    /// <summary>
    /// Column indices that carry inherited (parent) values.
    /// E.g., [0, 1, 2, 3] for ID, Name, Age, Dept.
    /// </summary>
    public List<int> InheritedColumns { get; set; } = new();

    /// <summary>
    /// Column indices that have unique values per sub-row.
    /// E.g., [4, 5, 6] for Contract No, Score, Tags.
    /// </summary>
    public List<int> DetailColumns { get; set; } = new();

    /// <summary>
    /// Grouped rows: each group has shared parent values and multiple detail rows.
    /// </summary>
    public List<InheritedRowGroup> Groups { get; set; } = new();

    /// <summary>
    /// Flat rows with inheritance already resolved (all cells filled).
    /// </summary>
    public List<List<string>> ResolvedRows { get; set; } = new();

    public ExtractedInheritedTable() { Type = "InheritedTable"; }
}

public class InheritedRowGroup
{
    /// <summary>
    /// Values for the inherited (parent) columns.
    /// </summary>
    public List<string> ParentValues { get; set; } = new();

    /// <summary>
    /// Detail rows - only the detail column values.
    /// </summary>
    public List<List<string>> DetailRows { get; set; } = new();
}

/// <summary>
/// A plain text paragraph or section.
/// </summary>
public class ExtractedTextBlock : ExtractedElement
{
    public string Text { get; set; } = string.Empty;

    public ExtractedTextBlock() { Type = "TextBlock"; }
}
