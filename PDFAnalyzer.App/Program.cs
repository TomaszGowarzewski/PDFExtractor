using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using PDFAnalyzer.App.Export;
using PDFAnalyzer.App.Extraction;
using PDFAnalyzer.App.Models;

if (args.Length == 0)
{
    Console.WriteLine("Usage: PDFAnalyzer.App <pdf-file-path> [output-path]");
    Console.WriteLine();
    Console.WriteLine("  Extracts all structured data from a PDF document.");
    Console.WriteLine("  Output format is determined by extension:");
    Console.WriteLine("    .json  - JSON (default)");
    Console.WriteLine("    .xlsx  - Excel workbook");
    Console.WriteLine();
    Console.WriteLine("  Examples:");
    Console.WriteLine("    PDFAnalyzer.App document.pdf                  -> document.json");
    Console.WriteLine("    PDFAnalyzer.App document.pdf output.xlsx      -> output.xlsx");
    Console.WriteLine("    PDFAnalyzer.App document.pdf output.json      -> output.json");
    return 1;
}

string inputPath = args[0];
string outputPath = args.Length > 1 ? args[1] : Path.ChangeExtension(inputPath, ".json");

try
{
    Console.WriteLine($"Extracting data from: {inputPath}");

    var extractor = new PdfDataExtractor();
    var result = extractor.Extract(inputPath);

    // Print summary
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

    Console.WriteLine($"\nExtraction complete!");
    Console.WriteLine($"  Pages: {result.TotalPages}");
    Console.WriteLine($"  Tables: {tables}");
    Console.WriteLine($"  Inherited Tables: {inheritedTables}");
    Console.WriteLine($"  Key-Value Groups: {kvGroups}");
    Console.WriteLine($"  Form Grids: {formGrids}");
    Console.WriteLine($"  Text Blocks: {textBlocks}");

    // Export based on extension
    string extension = Path.GetExtension(outputPath).ToLowerInvariant();

    if (extension == ".xlsx")
    {
        var excelExporter = new ExcelExporter();
        excelExporter.Export(result, outputPath);
        Console.WriteLine($"\nExcel saved to: {outputPath}");
    }
    else
    {
        // JSON output (default)
        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore
        };
        string json = JsonConvert.SerializeObject(result, settings);
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"\nJSON saved to: {outputPath}");
    }

    // If only one output specified and it's xlsx, also generate json (or vice versa)
    // Generate both if user wants
    if (extension == ".xlsx")
    {
        string jsonPath = Path.ChangeExtension(outputPath, ".json");
        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore
        };
        string json = JsonConvert.SerializeObject(result, settings);
        File.WriteAllText(jsonPath, json);
        Console.WriteLine($"JSON saved to: {jsonPath}");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
