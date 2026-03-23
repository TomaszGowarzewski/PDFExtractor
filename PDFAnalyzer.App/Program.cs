using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using PDFAnalyzer.App;
using PDFAnalyzer.App.Export;
using PDFAnalyzer.App.Extraction;
using PDFAnalyzer.App.Models;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Usage: PDFAnalyzer.App <pdf-file> [output-path] [options]");
    Console.WriteLine();
    Console.WriteLine("  Output format by extension: .json (default) or .xlsx");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --mode <bordered|borderless>   Table extraction mode (default: bordered)");
    Console.WriteLine("     bordered    = standard tables, each text line = one row");
    Console.WriteLine("     borderless  = Y-gap analysis for tables with wrapped cell text");
    Console.WriteLine();
    Console.WriteLine("  --sensitivity <0.05-0.50>      Gap sensitivity for borderless mode (default: 0.15)");
    Console.WriteLine("     Lower = more rows detected (stricter row separation)");
    Console.WriteLine("     Higher = fewer rows (more text merged into cells)");
    Console.WriteLine();
    Console.WriteLine("  --min-cols <N>                 Min columns for new row in borderless mode (default: auto)");
    Console.WriteLine("  --no-inherited                 Disable inherited-table detection");
    Console.WriteLine("  --no-cross-page                Disable cross-page table merging");
    Console.WriteLine("  --no-hierarchy                 Disable hierarchical header detection");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  PDFAnalyzer.App document.pdf");
    Console.WriteLine("  PDFAnalyzer.App document.pdf output.xlsx --mode borderless");
    Console.WriteLine("  PDFAnalyzer.App document.pdf out.json --mode borderless --sensitivity 0.10");
    return 1;
}

// Parse arguments
string inputPath = args[0];
string outputPath = args.Length > 1 && !args[1].StartsWith("--")
    ? args[1]
    : Path.ChangeExtension(inputPath, ".json");

var options = ExtractionOptions.Default;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLower())
    {
        case "--mode" when i + 1 < args.Length:
            options = args[i + 1].ToLower() switch
            {
                "borderless" => ExtractionOptions.Borderless,
                "bordered" => ExtractionOptions.Bordered,
                _ => options
            };
            i++;
            break;

        case "--sensitivity" when i + 1 < args.Length:
            if (double.TryParse(args[i + 1], System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double sens))
                options.GapSensitivity = sens;
            i++;
            break;

        case "--min-cols" when i + 1 < args.Length:
            if (int.TryParse(args[i + 1], out int mc))
                options.MinColumnsForNewRow = mc;
            i++;
            break;

        case "--no-inherited":
            options.DetectInheritedTables = false;
            break;

        case "--no-cross-page":
            options.MergeCrossPageTables = false;
            break;

        case "--no-hierarchy":
            options.DetectHierarchicalHeaders = false;
            break;
    }
}

try
{
    Console.WriteLine($"Extracting: {inputPath}");
    Console.WriteLine($"Mode: {options.RowDetection}" +
        (options.RowDetection == RowDetectionMode.GapAnalysis
            ? $" (sensitivity={options.GapSensitivity})"
            : ""));

    var extractor = new PdfDataExtractor(options);
    var result = extractor.Extract(inputPath);

    int tables = 0, inheritedTables = 0, kvGroups = 0, formGrids = 0, textBlocks = 0;
    foreach (var page in result.Pages)
    {
        foreach (var element in page.Elements)
        {
            switch (element)
            {
                case ExtractedInheritedTable: inheritedTables++; break;
                case ExtractedTable: tables++; break;
                case ExtractedKeyValueGroup: kvGroups++; break;
                case ExtractedFormGrid: formGrids++; break;
                case ExtractedTextBlock: textBlocks++; break;
            }
        }
    }

    Console.WriteLine($"\nDone! Pages: {result.TotalPages} | Tables: {tables} | Inherited: {inheritedTables} | KV: {kvGroups} | FormGrids: {formGrids} | Text: {textBlocks}");

    // Export
    string extension = Path.GetExtension(outputPath).ToLowerInvariant();
    var jsonSettings = new JsonSerializerSettings
    {
        Formatting = Formatting.Indented,
        Converters = { new StringEnumConverter() },
        NullValueHandling = NullValueHandling.Ignore
    };

    if (extension == ".xlsx")
    {
        new ExcelExporter().Export(result, outputPath);
        Console.WriteLine($"Excel: {outputPath}");

        string jsonPath = Path.ChangeExtension(outputPath, ".json");
        File.WriteAllText(jsonPath, JsonConvert.SerializeObject(result, jsonSettings));
        Console.WriteLine($"JSON:  {jsonPath}");
    }
    else
    {
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(result, jsonSettings));
        Console.WriteLine($"JSON:  {outputPath}");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
