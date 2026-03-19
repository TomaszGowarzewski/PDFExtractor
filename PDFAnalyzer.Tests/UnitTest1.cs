using FluentAssertions;
using PDFAnalyzer.App.Extraction;
using PDFAnalyzer.App.Models;

namespace PDFAnalyzer.Tests;

/// <summary>
/// Integration tests that verify the PDF extractor against the sample document.
/// The sample PDF (przykladowy_dokument.pdf) contains:
///   - Pages 1-2: Contract parties (KV pairs), contract details
///   - Page 3: Implementation schedule (table), project team (table)
///   - Page 4: Technical spec (KV), module list (table), non-functional requirements (table)
///   - Page 5: Payment schedule (table), additional costs (table), penalties (text)
///   - Page 6: Warranty conditions (KV), post-warranty service (table)
///   - Page 8: Attachments list (table), signatures (table)
///   - Pages 10-11: PIT-37 tax form (form grid + tables)
/// </summary>
public class PdfExtractionTests
{
    private static readonly string SamplePdfPath =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "przykladowy_dokument.pdf");

    private static ExtractedDocument? _cachedResult;
    private static readonly object _lock = new();

    private static ExtractedDocument GetDocument()
    {
        lock (_lock)
        {
            if (_cachedResult == null)
            {
                var extractor = new PdfDataExtractor();
                _cachedResult = extractor.Extract(SamplePdfPath);
            }
            return _cachedResult;
        }
    }

    [Fact]
    public void Extract_ShouldReturnCorrectPageCount()
    {
        var doc = GetDocument();
        doc.TotalPages.Should().Be(11);
        doc.Pages.Should().HaveCount(11);
    }

    [Fact]
    public void Extract_ShouldReturnCorrectFileName()
    {
        var doc = GetDocument();
        doc.FileName.Should().Be("przykladowy_dokument.pdf");
    }

    #region Page 1: Contract Parties (Key-Value Groups)

    [Fact]
    public void Page1_ShouldContainZamawiajacyKeyValueGroup()
    {
        var doc = GetDocument();
        var kvGroups = doc.Pages[0].Elements.OfType<ExtractedKeyValueGroup>().ToList();

        var zamawiajacy = kvGroups.FirstOrDefault(g => g.SectionName?.Contains("Zamawiajacy") == true);
        zamawiajacy.Should().NotBeNull("Page 1 should have Zamawiajacy section");

        var items = zamawiajacy!.Items;
        items.Should().Contain(i => i.Key == "Nazwa firmy" && i.Value == "Polskie Zaklady Technologiczne S.A.");
        items.Should().Contain(i => i.Key == "Adres siedziby" && i.Value.Contains("Marszalkowska 42/8"));
        items.Should().Contain(i => i.Key == "NIP" && i.Value == "527-10-37-684");
        items.Should().Contain(i => i.Key == "REGON" && i.Value == "012345678");
        items.Should().Contain(i => i.Key == "KRS" && i.Value == "0000284567");
        items.Should().Contain(i => i.Key == "Reprezentant" && i.Value.Contains("Jan Kowalski"));
        items.Should().Contain(i => i.Key == "E-mail" && i.Value == "anna.nowicka@pzt.pl");
        items.Should().Contain(i => i.Key == "Nr rachunku" && i.Value.Contains("PL 61 1090"));
    }

    [Fact]
    public void Page1_ShouldContainWykonawcaKeyValueGroup()
    {
        var doc = GetDocument();
        var kvGroups = doc.Pages[0].Elements.OfType<ExtractedKeyValueGroup>().ToList();

        var wykonawca = kvGroups.FirstOrDefault(g => g.SectionName?.Contains("Wykonawca") == true);
        wykonawca.Should().NotBeNull("Page 1 should have Wykonawca section");

        var items = wykonawca!.Items;
        items.Should().Contain(i => i.Key == "Nazwa firmy" && i.Value == "SoftDev Solutions Sp. z o.o.");
        items.Should().Contain(i => i.Key == "NIP" && i.Value == "679-31-42-516");
        items.Should().Contain(i => i.Key == "REGON" && i.Value == "362481957");
        items.Should().Contain(i => i.Key == "KRS" && i.Value == "0000519283");
        items.Should().Contain(i => i.Key == "E-mail" && i.Value == "k.maj@softdev.pl");
    }

    [Fact]
    public void Page1_ShouldContainDaneUmowyKeyValueGroup()
    {
        var doc = GetDocument();
        var kvGroups = doc.Pages[0].Elements.OfType<ExtractedKeyValueGroup>().ToList();

        var daneUmowy = kvGroups.FirstOrDefault(g => g.SectionName?.Contains("Dane umowy") == true);
        daneUmowy.Should().NotBeNull("Page 1 should have Dane umowy section");

        var items = daneUmowy!.Items;
        items.Should().Contain(i => i.Key == "Nr umowy" && i.Value == "ZK/2026/03/0847");
        items.Should().Contain(i => i.Key == "Data zawarcia" && i.Value.Contains("11 marca 2026"));
        items.Should().Contain(i => i.Key == "Wartosc netto" && i.Value.Contains("847 500,00 PLN"));
    }

    [Fact]
    public void Page1and2_DaneUmowy_ShouldContainVATAndBrutto()
    {
        var doc = GetDocument();
        // Dane umowy spans pages 1-2 and should be merged
        var allKvGroups = doc.Pages.SelectMany(p => p.Elements.OfType<ExtractedKeyValueGroup>());
        var daneUmowy = allKvGroups.FirstOrDefault(g => g.SectionName?.Contains("Dane umowy") == true);
        daneUmowy.Should().NotBeNull();

        daneUmowy!.Items.Should().Contain(i => i.Key == "Stawka VAT" && i.Value == "23%");
        daneUmowy.Items.Should().Contain(i => i.Key == "Wartosc brutto" && i.Value.Contains("1 042 425,00 PLN"));
        daneUmowy.Items.Should().Contain(i => i.Key == "Tryb zawarcia" && i.Value.Contains("Przetarg"));
    }

    #endregion

    #region Page 3: Implementation Schedule Table

    [Fact]
    public void Page3_ShouldContainHarmonogramTable()
    {
        var doc = GetDocument();
        var tables = doc.Pages[2].Elements.OfType<ExtractedTable>().ToList();

        // Find the schedule table - should have rows with dates
        var schedule = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("15.03.2026"))));
        schedule.Should().NotBeNull("Page 3 should have the implementation schedule table");

        // Verify all 7 phases are present
        var allRows = schedule!.Rows;
        allRows.Should().Contain(r => r.Any(c => c.Contains("Analiza wymagan")));
        allRows.Should().Contain(r => r.Any(c => c.Contains("Budowa modulow HRM")));
        allRows.Should().Contain(r => r.Any(c => c.Contains("Budowa modulow WMS")));
        allRows.Should().Contain(r => r.Any(c => c.Contains("Modul BI")));
        allRows.Should().Contain(r => r.Any(c => c.Contains("Testy i migracja")));
        allRows.Should().Contain(r => r.Any(c => c.Contains("Szkolenia")));
        allRows.Should().Contain(r => r.Any(c => c.Contains("Wdrozenie produkcyjne")));
    }

    [Fact]
    public void Page3_ScheduleTable_ShouldHaveCorrectAmounts()
    {
        var doc = GetDocument();
        var tables = doc.Pages[2].Elements.OfType<ExtractedTable>().ToList();
        var schedule = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("127 125,00 PLN"))));
        schedule.Should().NotBeNull();

        var allText = string.Join(" ", schedule!.Rows.SelectMany(r => r));
        allText.Should().Contain("127 125,00 PLN");
        allText.Should().Contain("212 500,00 PLN");
        allText.Should().Contain("195 000,00 PLN");
        allText.Should().Contain("85 000,00 PLN");
        allText.Should().Contain("42 500,00 PLN");
        allText.Should().Contain("58 250,00 PLN");
    }

    #endregion

    #region Page 3: Project Team Table

    [Fact]
    public void Page3_ShouldContainTeamTable()
    {
        var doc = GetDocument();
        var tables = doc.Pages[2].Elements.OfType<ExtractedTable>().ToList();

        var team = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("Tomasz Zielinski"))));
        team.Should().NotBeNull("Page 3 should have the project team table");

        // Verify team members
        var allText = string.Join(" ", team!.Rows.SelectMany(r => r));
        allText.Should().Contain("Marek Wisniewski");
        allText.Should().Contain("Tomasz Zielinski");
        allText.Should().Contain("Agnieszka Pawlak");
        allText.Should().Contain("Piotr Adamski");
        allText.Should().Contain("Malgorzata Krol");
        allText.Should().Contain("Jakub Nowak");
        allText.Should().Contain("Ewa Sikorska");
        allText.Should().Contain("Robert Duda");
        allText.Should().Contain("Karolina Lis");
        allText.Should().Contain("Michal Borkowski");
    }

    [Fact]
    public void Page3_TeamTable_ShouldContainRates()
    {
        var doc = GetDocument();
        var tables = doc.Pages[2].Elements.OfType<ExtractedTable>().ToList();
        var team = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("Tomasz Zielinski"))));
        team.Should().NotBeNull();

        var allText = string.Join(" ", team!.Rows.SelectMany(r => r));
        allText.Should().Contain("450,00 PLN");
        allText.Should().Contain("380,00 PLN");
        allText.Should().Contain("320,00 PLN");
        allText.Should().Contain("300,00 PLN");
        allText.Should().Contain("270,00 PLN");
    }

    #endregion

    #region Page 4: Technical Specification

    [Fact]
    public void Page4_ShouldContainArchitectureKeyValues()
    {
        var doc = GetDocument();
        var kvGroups = doc.Pages[3].Elements.OfType<ExtractedKeyValueGroup>().ToList();

        var arch = kvGroups.FirstOrDefault(g => g.SectionName?.Contains("Architektura") == true);
        arch.Should().NotBeNull("Page 4 should have Architecture section");

        var items = arch!.Items;
        items.Should().Contain(i => i.Key == "Architektura" && i.Value.Contains("Mikroserwisy"));
        items.Should().Contain(i => i.Key == "Backend" && i.Value.Contains(".NET 8"));
        items.Should().Contain(i => i.Key == "Frontend" && i.Value.Contains("React 18"));
        items.Should().Contain(i => i.Key == "Baza danych" && i.Value.Contains("PostgreSQL 16"));
        items.Should().Contain(i => i.Key == "Message broker" && i.Value == "RabbitMQ 3.13");
        items.Should().Contain(i => i.Key == "Hosting" && i.Value.Contains("Azure Poland Central"));
        items.Should().Contain(i => i.Key == "CI/CD" && i.Value.Contains("Azure DevOps"));
        items.Should().Contain(i => i.Key == "Monitoring" && i.Value.Contains("Prometheus"));
        items.Should().Contain(i => i.Key == "Autentykacja" && i.Value.Contains("Keycloak"));
        items.Should().Contain(i => i.Key == "API Gateway" && i.Value.Contains("Kong"));
    }

    [Fact]
    public void Page4_ShouldContainModulesTable()
    {
        var doc = GetDocument();
        var tables = doc.Pages[3].Elements.OfType<ExtractedTable>().ToList();

        var modules = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("HRM"))));
        modules.Should().NotBeNull("Page 4 should have modules table");

        var allText = string.Join(" ", modules!.Rows.SelectMany(r => r));
        allText.Should().Contain("HRM");
        allText.Should().Contain("FK");
        allText.Should().Contain("WMS");
        allText.Should().Contain("CRM");
        allText.Should().Contain("BI");
        allText.Should().Contain("ADM");
        allText.Should().Contain("Krytyczny");
        allText.Should().Contain("Wysoki");
        allText.Should().Contain("Sredni");
    }

    [Fact]
    public void Page4_ShouldContainNonFunctionalRequirementsTable()
    {
        var doc = GetDocument();
        var tables = doc.Pages[3].Elements.OfType<ExtractedTable>().ToList();

        var nfr = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("OWASP"))));
        nfr.Should().NotBeNull("Page 4 should have non-functional requirements table");

        var allText = string.Join(" ", nfr!.Rows.SelectMany(r => r));
        allText.Should().Contain("99,5%");
        allText.Should().Contain("99,9%");
        allText.Should().Contain("500 ms");
        allText.Should().Contain("200 ms");
        allText.Should().Contain("OWASP Top 10");
        allText.Should().Contain("ISO 27001");
        allText.Should().Contain("RODO");
    }

    #endregion

    #region Page 5: Payment Schedule & Additional Costs

    [Fact]
    public void Page5_ShouldContainPaymentScheduleTable()
    {
        var doc = GetDocument();
        var tables = doc.Pages[4].Elements.OfType<ExtractedTable>().ToList();

        var payments = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("Podpisanie umowy"))));
        payments.Should().NotBeNull();

        payments!.Rows.Should().HaveCount(5);
        var allText = string.Join(" ", payments.Rows.SelectMany(r => r));
        allText.Should().Contain("127 125,00 PLN");
        allText.Should().Contain("211 875,00 PLN");
        allText.Should().Contain("169 500,00 PLN");
    }

    [Fact]
    public void Page5_ShouldContainAdditionalCostsTable()
    {
        var doc = GetDocument();
        var tables = doc.Pages[4].Elements.OfType<ExtractedTable>().ToList();

        var costs = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("Dodatkowe szkolenie"))));
        costs.Should().NotBeNull();

        costs!.Rows.Should().HaveCount(6);
        var allText = string.Join(" ", costs.Rows.SelectMany(r => r));
        allText.Should().Contain("4 500,00 PLN");
        allText.Should().Contain("3 800,00 PLN");
        allText.Should().Contain("380,00 PLN");
        allText.Should().Contain("2 800,00 PLN");
        allText.Should().Contain("15 000,00 PLN");
        allText.Should().Contain("8 500,00 PLN");
    }

    #endregion

    #region Page 6: Warranty Conditions

    [Fact]
    public void Page6_ShouldContainWarrantyKeyValues()
    {
        var doc = GetDocument();
        var kvGroups = doc.Pages[5].Elements.OfType<ExtractedKeyValueGroup>().ToList();

        var warranty = kvGroups.FirstOrDefault(g => g.SectionName?.Contains("Warunki gwarancji") == true);
        warranty.Should().NotBeNull();

        var items = warranty!.Items;
        items.Should().Contain(i => i.Key == "Okres gwarancji" && i.Value.Contains("24 miesiace"));
        items.Should().Contain(i => i.Key.Contains("Czas reakcji (krytyczne)") && i.Value.Contains("2 godziny"));
        items.Should().Contain(i => i.Key.Contains("Czas naprawy (krytyczne)") && i.Value.Contains("8 godzin"));
        items.Should().Contain(i => i.Key.Contains("Kanal zgloszeniowy") && i.Value.Contains("Jira"));
    }

    [Fact]
    public void Page6_ShouldContainServicePricingTable()
    {
        var doc = GetDocument();
        var tables = doc.Pages[5].Elements.OfType<ExtractedTable>().ToList();

        var pricing = tables.FirstOrDefault(t =>
            t.Headers.Any(h => h.Contains("Pakiet")));
        pricing.Should().NotBeNull();

        var allText = string.Join(" ", pricing!.Rows.SelectMany(r => r));
        allText.Should().Contain("Basic");
        allText.Should().Contain("8 500,00 PLN");
        allText.Should().Contain("14 200,00 PLN");
        allText.Should().Contain("22 800,00 PLN");
        allText.Should().Contain("38 500,00 PLN");
    }

    #endregion

    #region Page 8: Attachments

    [Fact]
    public void Page8_ShouldContainAttachmentsTable()
    {
        var doc = GetDocument();
        var tables = doc.Pages[7].Elements.OfType<ExtractedTable>().ToList();

        var attachments = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("specyfikacja wymagan"))));
        attachments.Should().NotBeNull();

        attachments!.Rows.Should().HaveCount(8);
        var allText = string.Join(" ", attachments.Rows.SelectMany(r => r));
        allText.Should().Contain("Szczegolowa specyfikacja wymagan (SRS)");
        allText.Should().Contain("87");
        allText.Should().Contain("Projekt techniczny architektury");
        allText.Should().Contain("124");
        allText.Should().Contain("Harmonogram szkolen");
        allText.Should().Contain("Polisa ubezpieczeniowa");
    }

    [Fact]
    public void Page8_ShouldContainSignaturesTable()
    {
        var doc = GetDocument();
        var tables = doc.Pages[7].Elements.OfType<ExtractedTable>().ToList();

        var signatures = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("Jan Kowalski"))));
        signatures.Should().NotBeNull();

        var allText = string.Join(" ", signatures!.Rows.SelectMany(r => r));
        allText.Should().Contain("Jan Kowalski");
        allText.Should().Contain("Marek Wisniewski");
        allText.Should().Contain("Prezes Zarzadu");
        allText.Should().Contain("Dyrektor Zarzadzajacy");
    }

    #endregion

    #region Pages 10-11: PIT-37 Tax Form

    [Fact]
    public void Page10_ShouldContainNIPAndDocumentNumber()
    {
        var doc = GetDocument();
        var tables = doc.Pages[9].Elements.OfType<ExtractedTable>().ToList();

        var header = tables.FirstOrDefault(t =>
            t.Rows.Any(r => r.Any(c => c.Contains("83041578923"))));
        header.Should().NotBeNull();

        var allText = string.Join(" ", header!.Rows.SelectMany(r => r));
        allText.Should().Contain("83041578923");
        allText.Should().Contain("PIT-37/2025/004721");
    }

    [Fact]
    public void Page10_ShouldContainTaxpayerFormGrid()
    {
        var doc = GetDocument();
        var formGrids = doc.Pages[9].Elements.OfType<ExtractedFormGrid>().ToList();
        formGrids.Should().NotBeEmpty("Page 10 should have form grids");

        var allCells = formGrids.SelectMany(g => g.Cells).ToList();
        var allValues = string.Join(" ", allCells.Select(c => c.Label + " " + c.Value));

        // Taxpayer data should be present
        allValues.Should().Contain("Kowalczyk");
        allValues.Should().Contain("Magdalena");
        allValues.Should().Contain("15-07-1988");
        allValues.Should().Contain("Polska");
        allValues.Should().Contain("lodzkie");
        allValues.Should().Contain("Kosciuszki");
        allValues.Should().Contain("90-418");
        allValues.Should().Contain("m.kowalczyk@email.pl");
    }

    [Fact]
    public void Page10_ShouldContainSpouseData()
    {
        var doc = GetDocument();
        var formGrids = doc.Pages[9].Elements.OfType<ExtractedFormGrid>().ToList();
        var allValues = string.Join(" ", formGrids.SelectMany(g => g.Cells).Select(c => c.Label + " " + c.Value));

        allValues.Should().Contain("Tomasz");
        allValues.Should().Contain("03-11-1985");
        allValues.Should().Contain("85110312457");
    }

    [Fact]
    public void Page10_ShouldContainIncomeData()
    {
        var doc = GetDocument();
        // Income data is in tables/form grids on page 10
        var allElements = doc.Pages[9].Elements;
        var allText = string.Join(" ", allElements.SelectMany(e =>
        {
            if (e is ExtractedTable t)
                return t.Rows.SelectMany(r => r).Concat(t.Headers);
            if (e is ExtractedFormGrid fg)
                return fg.Cells.Select(c => c.Label + " " + c.Value);
            if (e is ExtractedTextBlock tb)
                return new[] { tb.Text };
            return Enumerable.Empty<string>();
        }));

        allText.Should().Contain("87 450,00");   // Employment income
        allText.Should().Contain("3 000,00");     // Employment costs
        allText.Should().Contain("84 450,00");    // Employment net income
        allText.Should().Contain("7 812,00");     // Tax advance
        allText.Should().Contain("24 000,00");    // Civil law contracts income
        allText.Should().Contain("12 500,00");    // Copyright income
    }

    [Fact]
    public void Page11_ShouldContainDeductionsData()
    {
        var doc = GetDocument();
        var allElements = doc.Pages[10].Elements;
        var allText = string.Join(" ", allElements.SelectMany(e =>
        {
            if (e is ExtractedTable t) return t.Rows.SelectMany(r => r).Concat(t.Headers);
            if (e is ExtractedFormGrid fg) return fg.Cells.Select(c => c.Label + " " + c.Value);
            if (e is ExtractedTextBlock tb) return new[] { tb.Text };
            return Enumerable.Empty<string>();
        }));

        allText.Should().Contain("12 348,72");    // Social security contributions
        allText.Should().Contain("9 388,80");     // IKZE
        allText.Should().Contain("760,00");       // Internet deduction
        allText.Should().Contain("2 500,00");     // Donations
        allText.Should().Contain("18 400,00");    // Thermomodernization
        allText.Should().Contain("53 273,76");    // Total deductions
    }

    [Fact]
    public void Page11_ShouldContainTaxCalculation()
    {
        var doc = GetDocument();
        var allElements = doc.Pages[10].Elements;
        var allText = string.Join(" ", allElements.SelectMany(e =>
        {
            if (e is ExtractedTable t) return t.Rows.SelectMany(r => r).Concat(t.Headers);
            if (e is ExtractedFormGrid fg) return fg.Cells.Select(c => c.Label + " " + c.Value);
            if (e is ExtractedTextBlock tb) return new[] { tb.Text };
            return Enumerable.Empty<string>();
        }));

        allText.Should().Contain("59 826");       // Tax base
        allText.Should().Contain("7 179,12");     // Calculated tax
        allText.Should().Contain("6 948,48");     // Health insurance contribution
        allText.Should().Contain("1 112,04");     // Child deduction
        allText.Should().Contain("12 042,00");    // Total advances paid
        allText.Should().Contain("11 811,36");    // Overpayment
    }

    [Fact]
    public void Page11_ShouldContainTotalIncomeAndCosts()
    {
        var doc = GetDocument();
        var allElements = doc.Pages[10].Elements;
        var allText = string.Join(" ", allElements.SelectMany(e =>
        {
            if (e is ExtractedTable t) return t.Rows.SelectMany(r => r).Concat(t.Headers);
            if (e is ExtractedFormGrid fg) return fg.Cells.Select(c => c.Label + " " + c.Value);
            if (e is ExtractedTextBlock tb) return new[] { tb.Text };
            return Enumerable.Empty<string>();
        }));

        allText.Should().Contain("127 150,00");   // Total income
        allText.Should().Contain("14 050,00");    // Total costs
        allText.Should().Contain("113 100,00");   // Total net income
    }

    #endregion

    #region General Structure Tests

    [Fact]
    public void AllPages_ShouldNotHaveHeaderFooterText()
    {
        var doc = GetDocument();
        foreach (var page in doc.Pages)
        {
            foreach (var element in page.Elements)
            {
                string text = element switch
                {
                    ExtractedTextBlock tb => tb.Text,
                    ExtractedKeyValueGroup kv => string.Join(" ", kv.Items.Select(i => i.Key + " " + i.Value)),
                    _ => string.Empty
                };

                text.Should().NotContain("POUFNE Polskie Zaklady",
                    "Header/footer text should be filtered out");
            }
        }
    }

    [Fact]
    public void AllKeyValueGroups_ShouldHaveNonEmptyKeysAndValues()
    {
        var doc = GetDocument();
        foreach (var page in doc.Pages)
        {
            foreach (var kv in page.Elements.OfType<ExtractedKeyValueGroup>())
            {
                foreach (var item in kv.Items)
                {
                    item.Key.Should().NotBeNullOrWhiteSpace("KV key should not be empty");
                    item.Value.Should().NotBeNull("KV value should not be null");
                }
            }
        }
    }

    [Fact]
    public void AllTables_ShouldHaveHeaders()
    {
        var doc = GetDocument();
        foreach (var page in doc.Pages)
        {
            foreach (var table in page.Elements.OfType<ExtractedTable>())
            {
                table.Headers.Should().NotBeEmpty("Every table should have headers");
                table.Headers.Should().Contain(h => !string.IsNullOrWhiteSpace(h),
                    "Table should have at least one non-empty header");
            }
        }
    }

    [Fact]
    public void AllTables_RowsShouldMatchHeaderCount()
    {
        var doc = GetDocument();
        foreach (var page in doc.Pages)
        {
            foreach (var table in page.Elements.OfType<ExtractedTable>())
            {
                foreach (var row in table.Rows)
                {
                    row.Count.Should().Be(table.Headers.Count,
                        $"Row column count should match header count in table on page {page.PageNumber}");
                }
            }
        }
    }

    [Fact]
    public void Extract_ShouldContainAllMajorDataPoints()
    {
        var doc = GetDocument();
        var allText = GetAllText(doc);

        // Key data points that must be extracted somewhere
        var requiredData = new[]
        {
            "Polskie Zaklady Technologiczne S.A.",
            "SoftDev Solutions Sp. z o.o.",
            "ZK/2026/03/0847",
            "527-10-37-684",
            "679-31-42-516",
            "847 500,00 PLN",
            "1 042 425,00 PLN",
            "PostgreSQL 16",
            "RabbitMQ 3.13",
            "Keycloak 24",
            "Jan Kowalski",
            "Marek Wisniewski",
            "Kowalczyk",
            "Magdalena",
            "83041578923",
            "PIT-37/2025/004721",
            "11 811,36"
        };

        foreach (var data in requiredData)
        {
            allText.Should().Contain(data, $"Critical data point '{data}' must be extracted");
        }
    }

    #endregion

    #region Helpers

    private static string GetAllText(ExtractedDocument doc)
    {
        var parts = new List<string>();
        foreach (var page in doc.Pages)
        {
            foreach (var element in page.Elements)
            {
                switch (element)
                {
                    case ExtractedTextBlock tb:
                        parts.Add(tb.Text);
                        break;
                    case ExtractedKeyValueGroup kv:
                        parts.AddRange(kv.Items.Select(i => i.Key + " " + i.Value));
                        break;
                    case ExtractedTable t:
                        parts.AddRange(t.Headers);
                        parts.AddRange(t.Rows.SelectMany(r => r));
                        break;
                    case ExtractedFormGrid fg:
                        parts.AddRange(fg.Cells.Select(c => c.Label + " " + c.Value));
                        break;
                }
            }
        }
        return string.Join(" ", parts);
    }

    #endregion
}
