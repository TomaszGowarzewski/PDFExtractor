using FluentAssertions;
using PDFAnalyzer.App.Extraction;
using PDFAnalyzer.App.Models;

namespace PDFAnalyzer.Tests;

public class InheritedTableDetectorTests
{
    #region Pattern 1: ID-based (A,B,C... or PRD-001,PRD-002...)

    [Fact]
    public void TryConvert_RegistryTable_ShouldDetect()
    {
        var table = CreateRegistryTable();
        var result = InheritedTableDetector.TryConvert(table);

        result.Should().NotBeNull();
        result!.InheritedColumns.Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });
        result.DetailColumns.Should().BeEquivalentTo(new[] { 4, 5, 6 });
        result.Groups.Should().HaveCount(4);
    }

    [Fact]
    public void TryConvert_Registry_ParentValuesShouldBeCorrect()
    {
        var result = InheritedTableDetector.TryConvert(CreateRegistryTable())!;

        result.Groups[0].ParentValues.Should().BeEquivalentTo(new[] { "A", "Kowalski Jan", "34", "IT" });
        result.Groups[1].ParentValues.Should().BeEquivalentTo(new[] { "B", "Nowak Katarzyna", "28", "HR" });
        result.Groups[2].ParentValues.Should().BeEquivalentTo(new[] { "C", "Wisniewska Anna", "45", "Finance" });
        result.Groups[3].ParentValues.Should().BeEquivalentTo(new[] { "D", "Zielinski Tomasz", "52", "Operations" });
    }

    [Fact]
    public void TryConvert_Registry_DetailRowCountsShouldBeCorrect()
    {
        var result = InheritedTableDetector.TryConvert(CreateRegistryTable())!;

        result.Groups[0].DetailRows.Should().HaveCount(3); // A: 3 contracts
        result.Groups[1].DetailRows.Should().HaveCount(2); // B: 2 contracts
        result.Groups[2].DetailRows.Should().HaveCount(1); // C: 1 contract
        result.Groups[3].DetailRows.Should().HaveCount(4); // D: 4 contracts
    }

    [Fact]
    public void TryConvert_Registry_ResolvedRowsShouldInheritCorrectly()
    {
        var result = InheritedTableDetector.TryConvert(CreateRegistryTable())!;

        result.ResolvedRows.Should().HaveCount(10);

        // 2nd row: "2024/002-KJ" should inherit from A
        var row2 = result.ResolvedRows[1];
        row2[0].Should().Be("A");
        row2[1].Should().Be("Kowalski Jan");
        row2[2].Should().Be("34");
        row2[3].Should().Be("IT");
        row2[4].Should().Be("2024/002-KJ");
        row2[5].Should().Be("92");

        // 8th row: "2023/201-ZT" should inherit from D
        var rowZT = result.ResolvedRows.First(r => r[4] == "2023/201-ZT");
        rowZT[0].Should().Be("D");
        rowZT[1].Should().Be("Zielinski Tomasz");
        rowZT[3].Should().Be("Operations");
    }

    [Fact]
    public void TryConvert_ProductTable_ShouldDetect()
    {
        var table = CreateProductTable();
        var result = InheritedTableDetector.TryConvert(table);

        result.Should().NotBeNull();
        result!.Groups.Should().HaveCount(3);
        result.Groups[0].ParentValues.Should().Contain("PRD-001");
        result.Groups[0].DetailRows.Should().HaveCount(3); // 3 PO numbers

        var refRow = result.ResolvedRows.First(r => r[4] == "PO-2024/1002");
        refRow[0].Should().Be("PRD-001");
        refRow[1].Should().Be("Pompa hydrauliczna XR-4500");
    }

    #endregion

    #region Pattern 2: Cascading / hierarchical (Region > City > Office)

    [Fact]
    public void TryConvert_CascadingRegionTable_ShouldDetect()
    {
        var table = CreateRegionTable();
        var result = InheritedTableDetector.TryConvert(table);

        result.Should().NotBeNull();
        // Region and City are inherited, Office onward are detail
        result!.InheritedColumns.Should().Contain(0); // Region
        result.InheritedColumns.Should().Contain(1);  // City
    }

    [Fact]
    public void TryConvert_CascadingRegion_ShouldResolveIndependently()
    {
        var table = CreateRegionTable();
        var result = InheritedTableDetector.TryConvert(table)!;

        // Row: South Branch (Region=Mazowieckie inherited, City=Warszawa inherited)
        var southBranch = result.ResolvedRows.First(r => r.Any(c => c.Contains("South Branch")));
        southBranch[0].Should().Be("Mazowieckie");
        southBranch[1].Should().Be("Warszawa");

        // Row: Service Point (Region=Mazowieckie inherited, City=Radom is NEW)
        var servicePoint = result.ResolvedRows.First(r => r.Any(c => c.Contains("Service Point")));
        servicePoint[0].Should().Be("Mazowieckie");
        servicePoint[1].Should().Be("Radom");

        // Row: Main Office (Region=Malopolskie is NEW, City=Krakow is NEW)
        var mainOffice = result.ResolvedRows.First(r => r.Any(c => c.Contains("Main Office")));
        mainOffice[0].Should().Be("Malopolskie");
        mainOffice[1].Should().Be("Krakow");

        // Row: Branch (Region=Malopolskie inherited, City=Tarnow is NEW)
        var branch = result.ResolvedRows.First(r =>
            r.Any(c => c.Contains("Branch")) && r.Any(c => c.Contains("Tarnow")));
        branch[0].Should().Be("Malopolskie");
        branch[1].Should().Be("Tarnow");
    }

    [Fact]
    public void TryConvert_CascadingRegion_GroupsShouldBeByFirstColumn()
    {
        var table = CreateRegionTable();
        var result = InheritedTableDetector.TryConvert(table)!;

        // Groups split by Region (column 0)
        result.Groups.Should().HaveCount(3);
        result.Groups[0].ParentValues[0].Should().Be("Mazowieckie");
        result.Groups[1].ParentValues[0].Should().Be("Malopolskie");
        result.Groups[2].ParentValues[0].Should().Be("Slaskie");
    }

    #endregion

    #region Pattern 3: Category > Subcategory > Item

    [Fact]
    public void TryConvert_CategoryHierarchy_ShouldDetect()
    {
        var table = new ExtractedTable
        {
            Headers = new() { "Category", "Subcategory", "Item", "Price", "Stock" },
            Rows = new()
            {
                new() { "Electronics", "Phones", "iPhone 15", "4999", "120" },
                new() { "", "", "Samsung S24", "3999", "85" },
                new() { "", "", "Pixel 8", "3499", "40" },
                new() { "", "Laptops", "MacBook Pro", "8999", "30" },
                new() { "", "", "ThinkPad X1", "6999", "55" },
                new() { "", "Tablets", "iPad Air", "2999", "90" },
                new() { "Furniture", "Chairs", "Ergonomic Pro", "1299", "200" },
                new() { "", "", "Basic Office", "499", "500" },
                new() { "", "Desks", "Standing Desk", "1899", "75" },
                new() { "", "", "Classic Desk", "799", "150" },
            }
        };

        var result = InheritedTableDetector.TryConvert(table);

        result.Should().NotBeNull();
        result!.InheritedColumns.Should().BeEquivalentTo(new[] { 0, 1 });
        result.DetailColumns.Should().BeEquivalentTo(new[] { 2, 3, 4 });

        // Samsung should inherit Electronics > Phones
        var samsung = result.ResolvedRows.First(r => r[2] == "Samsung S24");
        samsung[0].Should().Be("Electronics");
        samsung[1].Should().Be("Phones");

        // MacBook should inherit Electronics, but Laptops is new
        var macbook = result.ResolvedRows.First(r => r[2] == "MacBook Pro");
        macbook[0].Should().Be("Electronics");
        macbook[1].Should().Be("Laptops");

        // ThinkPad should inherit Electronics > Laptops
        var thinkpad = result.ResolvedRows.First(r => r[2] == "ThinkPad X1");
        thinkpad[0].Should().Be("Electronics");
        thinkpad[1].Should().Be("Laptops");

        // Standing Desk should inherit Furniture > Desks
        var standingDesk = result.ResolvedRows.First(r => r[2] == "Standing Desk");
        standingDesk[0].Should().Be("Furniture");
        standingDesk[1].Should().Be("Desks");
    }

    #endregion

    #region Negative cases: should NOT detect

    [Fact]
    public void TryConvert_RegularTable_ShouldReturnNull()
    {
        var table = new ExtractedTable
        {
            Headers = new() { "Name", "Age", "City" },
            Rows = new()
            {
                new() { "Jan", "30", "Warszawa" },
                new() { "Anna", "25", "Krakow" },
                new() { "Piotr", "40", "Gdansk" },
            }
        };

        InheritedTableDetector.TryConvert(table).Should().BeNull();
    }

    [Fact]
    public void TryConvert_TooSmallTable_ShouldReturnNull()
    {
        var table = new ExtractedTable
        {
            Headers = new() { "A", "B" },
            Rows = new()
            {
                new() { "1", "x" },
                new() { "", "y" },
            }
        };

        InheritedTableDetector.TryConvert(table).Should().BeNull();
    }

    [Fact]
    public void TryConvert_PaymentSchedule_ShouldReturnNull()
    {
        var table = new ExtractedTable
        {
            Headers = new() { "Lp.", "Kamien milowy", "% wartosci", "Kwota netto", "Termin" },
            Rows = new()
            {
                new() { "1", "Podpisanie umowy", "15%", "127 125,00 PLN", "14 dni od podpisania" },
                new() { "2", "Odbiory etapow 1-2", "25%", "211 875,00 PLN", "14 dni od protokolu" },
                new() { "3", "Odbiory etapow 3-4", "25%", "211 875,00 PLN", "14 dni od protokolu" },
                new() { "4", "Odbiory etapow 5-6", "15%", "127 125,00 PLN", "14 dni od protokolu" },
                new() { "5", "Odbiory etap 7", "20%", "169 500,00 PLN", "30 dni od protokolu" },
            }
        };

        InheritedTableDetector.TryConvert(table).Should().BeNull();
    }

    [Fact]
    public void TryConvert_TeamTable_ShouldReturnNull()
    {
        // Team table has no inheritance - all rows have all values
        var table = new ExtractedTable
        {
            Headers = new() { "Name", "Position", "Role", "Rate" },
            Rows = new()
            {
                new() { "Marek Wisniewski", "Director", "Sponsor", "450 PLN" },
                new() { "Tomasz Zielinski", "Architect", "System Architect", "380 PLN" },
                new() { "Agnieszka Pawlak", "PM", "Project Manager", "320 PLN" },
                new() { "Piotr Adamski", "Developer", "Lead Backend", "300 PLN" },
            }
        };

        InheritedTableDetector.TryConvert(table).Should().BeNull();
    }

    [Fact]
    public void TryConvert_TableWithRandomEmptyCells_ShouldReturnNull()
    {
        // Random empty cells don't form an inheritance pattern
        var table = new ExtractedTable
        {
            Headers = new() { "A", "B", "C", "D" },
            Rows = new()
            {
                new() { "x", "", "z", "w" },
                new() { "", "y", "", "w" },
                new() { "x", "y", "z", "" },
                new() { "x", "", "", "w" },
                new() { "", "y", "z", "w" },
            }
        };

        InheritedTableDetector.TryConvert(table).Should().BeNull();
    }

    #endregion

    #region Edge cases

    [Fact]
    public void TryConvert_SingleInheritedColumn_ShouldDetect()
    {
        // Only 1 inherited column (department), rest are detail
        var table = new ExtractedTable
        {
            Headers = new() { "Department", "Employee", "Task", "Hours" },
            Rows = new()
            {
                new() { "IT", "Jan", "Development", "40" },
                new() { "", "Anna", "Testing", "35" },
                new() { "", "Piotr", "DevOps", "42" },
                new() { "HR", "Ewa", "Recruitment", "38" },
                new() { "", "Kasia", "Training", "36" },
                new() { "Finance", "Marek", "Reporting", "40" },
                new() { "", "Tomek", "Audit", "38" },
            }
        };

        var result = InheritedTableDetector.TryConvert(table);

        result.Should().NotBeNull();
        result!.InheritedColumns.Should().BeEquivalentTo(new[] { 0 });
        result.Groups.Should().HaveCount(3);

        var anna = result.ResolvedRows.First(r => r[1] == "Anna");
        anna[0].Should().Be("IT");
    }

    [Fact]
    public void TryConvert_MasterRowWithSingleDetailRow_ShouldStillWork()
    {
        // Some masters have only 1 detail row (no children)
        var table = new ExtractedTable
        {
            Headers = new() { "Group", "Name", "Value", "Status" },
            Rows = new()
            {
                new() { "X", "Alpha", "100", "OK" },
                new() { "", "Beta", "200", "OK" },
                new() { "", "Gamma", "300", "OK" },
                new() { "Y", "Delta", "400", "OK" }, // only 1 detail row
                new() { "Z", "Epsilon", "500", "OK" },
                new() { "", "Zeta", "600", "OK" },
            }
        };

        var result = InheritedTableDetector.TryConvert(table);

        result.Should().NotBeNull();
        result!.Groups.Should().HaveCount(3);
        result.Groups[0].DetailRows.Should().HaveCount(3); // X: Alpha,Beta,Gamma
        result.Groups[1].DetailRows.Should().HaveCount(1); // Y: Delta only
        result.Groups[2].DetailRows.Should().HaveCount(2); // Z: Epsilon,Zeta
    }

    #endregion

    #region Test data builders

    private static ExtractedTable CreateRegistryTable()
    {
        return new ExtractedTable
        {
            Headers = new() { "ID", "Name", "Age", "Dept", "Contract No", "Score", "Tags" },
            Rows = new()
            {
                new() { "A", "Kowalski Jan", "34", "IT", "2024/001-KJ", "87", "python,docker,aws" },
                new() { "", "", "", "", "2024/002-KJ", "92", "react,typescript" },
                new() { "", "", "", "", "2024/003-KJ", "78", "sql,postgres,redis" },
                new() { "B", "Nowak Katarzyna", "28", "HR", "2024/010-NB", "65", "recruitment,onboarding" },
                new() { "", "", "", "", "2024/011-NB", "71", "training,compliance" },
                new() { "C", "Wisniewska Anna", "45", "Finance", "2023/105-WA", "91", "excel,sap,reporting" },
                new() { "D", "Zielinski Tomasz", "52", "Operations", "2023/200-ZT", "88", "logistics,planning" },
                new() { "", "", "", "", "2023/201-ZT", "76", "inventory,warehouse" },
                new() { "", "", "", "", "2023/202-ZT", "94", "scheduling,fleet" },
                new() { "", "", "", "", "2023/203-ZT", "82", "safety,compliance,audit" },
            }
        };
    }

    private static ExtractedTable CreateProductTable()
    {
        return new ExtractedTable
        {
            Headers = new() { "Code", "Description", "Qty", "Unit Price", "Ref Numbers", "Priority", "Notes" },
            Rows = new()
            {
                new() { "PRD-001", "Pompa hydrauliczna XR-4500", "15", "2450.00", "PO-2024/1001", "HIGH", "Zamowienie pilne" },
                new() { "", "", "", "", "PO-2024/1002", "", "" },
                new() { "", "", "", "", "PO-2024/1003", "", "Alternatywny dostawca" },
                new() { "PRD-002", "Uszczelka DN50 PN16", "200", "12.50", "PO-2024/1010", "LOW", "Standard" },
                new() { "PRD-003", "Silnik elektryczny 7.5kW", "3", "8900.00", "PO-2024/1020", "MED", "Wymagany certyfikat ATEX" },
                new() { "", "", "", "", "PO-2024/1021", "", "" },
            }
        };
    }

    private static ExtractedTable CreateRegionTable()
    {
        return new ExtractedTable
        {
            Headers = new() { "Region", "City", "Office", "Employees", "Revenue", "Status" },
            Rows = new()
            {
                new() { "Mazowieckie", "Warszawa", "Central HQ", "245", "12500", "Active" },
                new() { "", "", "South Branch", "89", "4200", "Active" },
                new() { "", "", "Mokotow Lab", "34", "1800", "Pilot" },
                new() { "", "Radom", "Service Point", "12", "650", "Active" },
                new() { "Malopolskie", "Krakow", "Main Office", "156", "8900", "Active" },
                new() { "", "", "Nowa Huta Depot", "28", "1100", "Closing" },
                new() { "", "Tarnow", "Branch", "15", "720", "Active" },
                new() { "Slaskie", "Katowice", "Regional HQ", "112", "6300", "Active" },
            }
        };
    }

    #endregion
}
