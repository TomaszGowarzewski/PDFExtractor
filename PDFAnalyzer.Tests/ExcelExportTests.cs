using ClosedXML.Excel;
using FluentAssertions;
using PDFAnalyzer.App.Export;
using PDFAnalyzer.App.Extraction;

namespace PDFAnalyzer.Tests;

public class ExcelExportTests : IDisposable
{
    private static readonly string SamplePdfPath =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "przykladowy_dokument.pdf");

    private readonly string _outputPath;

    public ExcelExportTests()
    {
        _outputPath = Path.Combine(Path.GetTempPath(), $"PDFAnalyzer_test_{Guid.NewGuid()}.xlsx");
    }

    public void Dispose()
    {
        if (File.Exists(_outputPath))
            File.Delete(_outputPath);
    }

    private XLWorkbook GenerateWorkbook()
    {
        var extractor = new PdfDataExtractor();
        var document = extractor.Extract(SamplePdfPath);
        var exporter = new ExcelExporter();
        exporter.Export(document, _outputPath);
        return new XLWorkbook(_outputPath);
    }

    [Fact]
    public void Export_ShouldCreateValidExcelFile()
    {
        using var wb = GenerateWorkbook();
        wb.Worksheets.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Export_ShouldHaveSummarySheet()
    {
        using var wb = GenerateWorkbook();
        wb.Worksheets.TryGetWorksheet("Podsumowanie", out var ws).Should().BeTrue();

        ws!.Cell(1, 1).GetString().Should().Be("Plik");
        ws.Cell(1, 2).GetString().Should().Be("przykladowy_dokument.pdf");
        ws.Cell(2, 1).GetString().Should().Be("Liczba stron");
        ws.Cell(2, 2).GetValue<int>().Should().Be(11);
    }

    [Fact]
    public void Export_ShouldHaveKeyValueSheet()
    {
        using var wb = GenerateWorkbook();
        wb.Worksheets.TryGetWorksheet("Klucz-Wartosc", out var ws).Should().BeTrue();

        // Find Zamawiajacy section
        var allText = GetAllCellText(ws!);
        allText.Should().Contain("Polskie Zaklady Technologiczne S.A.");
        allText.Should().Contain("527-10-37-684");
        allText.Should().Contain("SoftDev Solutions Sp. z o.o.");
        allText.Should().Contain("679-31-42-516");
        allText.Should().Contain("ZK/2026/03/0847");
        allText.Should().Contain("PostgreSQL 16");
        allText.Should().Contain("Keycloak 24");
    }

    [Fact]
    public void Export_ShouldHaveTableSheets()
    {
        using var wb = GenerateWorkbook();

        // Should have at least table sheets for: schedule, team, modules, NFR, payments, costs, service, attachments, signatures
        wb.Worksheets.Count.Should().BeGreaterThanOrEqualTo(10, "Should have summary + KV + formgrid + text + multiple table sheets");
    }

    [Fact]
    public void Export_TableSheets_ShouldContainPaymentData()
    {
        using var wb = GenerateWorkbook();

        // Find sheet with payment data
        var paymentSheet = wb.Worksheets.FirstOrDefault(ws =>
            GetAllCellText(ws).Contains("Podpisanie umowy"));
        paymentSheet.Should().NotBeNull("Should have a sheet with payment schedule");

        var text = GetAllCellText(paymentSheet!);
        text.Should().Contain("Odbiory etapow 1-2");
        text.Should().Contain("Odbiory etapow 3-4");
    }

    [Fact]
    public void Export_TableSheets_ShouldContainAdditionalCosts()
    {
        using var wb = GenerateWorkbook();

        var costSheet = wb.Worksheets.FirstOrDefault(ws =>
            GetAllCellText(ws).Contains("Dodatkowe szkolenie"));
        costSheet.Should().NotBeNull();

        var text = GetAllCellText(costSheet!);
        text.Should().Contain("Konsultacja on-site");
        text.Should().Contain("Konsultacja zdalna");
        text.Should().Contain("Raport audytowy");
    }

    [Fact]
    public void Export_TableSheets_ShouldContainTeamData()
    {
        using var wb = GenerateWorkbook();

        var teamSheet = wb.Worksheets.FirstOrDefault(ws =>
            GetAllCellText(ws).Contains("Tomasz Zielinski"));
        teamSheet.Should().NotBeNull("Should have a sheet with team data");

        var text = GetAllCellText(teamSheet!);
        text.Should().Contain("Agnieszka Pawlak");
        text.Should().Contain("Piotr Adamski");
        text.Should().Contain("Michal Borkowski");
    }

    [Fact]
    public void Export_ShouldHaveFormGridSheet()
    {
        using var wb = GenerateWorkbook();
        wb.Worksheets.TryGetWorksheet("Formularze", out var ws).Should().BeTrue();

        var text = GetAllCellText(ws!);
        text.Should().Contain("Kowalczyk");
        text.Should().Contain("Magdalena");
        text.Should().Contain("15-07-1988");
    }

    [Fact]
    public void Export_ShouldHaveTextSheet()
    {
        using var wb = GenerateWorkbook();
        wb.Worksheets.TryGetWorksheet("Tekst", out var ws).Should().BeTrue();

        var text = GetAllCellText(ws!);
        text.Should().Contain("UMOWA KOMPLEKSOWA");
    }

    [Fact]
    public void Export_NumericValues_ShouldBeParsedAsNumbers()
    {
        using var wb = GenerateWorkbook();

        // Find a sheet with PLN amounts - check they are stored as numbers
        var paymentSheet = wb.Worksheets.FirstOrDefault(ws =>
            GetAllCellText(ws).Contains("Podpisanie umowy"));
        paymentSheet.Should().NotBeNull();

        // Find a cell with "127 125" value (should be numeric)
        bool foundNumeric = false;
        foreach (var row in paymentSheet!.RowsUsed())
        {
            foreach (var cell in row.CellsUsed())
            {
                if (cell.DataType == XLDataType.Number && cell.GetDouble() == 127125.00)
                {
                    foundNumeric = true;
                    break;
                }
            }
            if (foundNumeric) break;
        }

        foundNumeric.Should().BeTrue("PLN amounts should be stored as numeric values in Excel");
    }

    [Fact]
    public void Export_TableSheets_ShouldContainAttachments()
    {
        using var wb = GenerateWorkbook();

        var sheet = wb.Worksheets.FirstOrDefault(ws =>
            GetAllCellText(ws).Contains("specyfikacja wymagan"));
        sheet.Should().NotBeNull();

        var text = GetAllCellText(sheet!);
        text.Should().Contain("Harmonogram szkolen");
        text.Should().Contain("Polisa ubezpieczeniowa");
    }

    private static string GetAllCellText(IXLWorksheet ws)
    {
        var parts = new List<string>();
        foreach (var row in ws.RowsUsed())
        {
            foreach (var cell in row.CellsUsed())
            {
                parts.Add(cell.GetFormattedString());
            }
        }
        return string.Join(" ", parts);
    }
}
