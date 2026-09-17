using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxApexExcellenceTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxApexExcellenceTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Theory]
        [InlineData(@"\begin{pmatrix} 2 \\ -3 \end{pmatrix}", "(2, -3)")]
        [InlineData(@"\begin{bmatrix} -1 \\ 5 \end{bmatrix}", "(-1, 5)")]
        [InlineData(@"\begin{pmatrix} 1 \\ 0 \\ -2 \end{pmatrix}", "(1, 0, -2)")]
        [InlineData("(2, -3)", @"\begin{pmatrix} 2 \\ -3 \end{pmatrix}")]
        public void MatrixColumnVector_ShouldMatchCoordinateTuple_Equivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("向量", reason);
        }

        [Theory]
        [InlineData("[-1; 2]", "[-1, 2]")]
        [InlineData("[-1；2]", "[-1, 2]")]
        [InlineData("(-3; +inf)", "(-3, +inf)")]
        [InlineData("(4; 5)", "(4, 5)")]
        [InlineData("[-2; 3)", "[-2, 3)")]
        public void SemicolonIntervalAndCoordinates_ShouldMatchCommaEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("数理区间/点坐标", reason);
        }

        [Theory]
        [InlineData("3.0·10^8", "3.0*10^8")]
        [InlineData("3.0•10^8", "3e8")]
        [InlineData("1.6·10^-19", "1.6*10^-19")]
        [InlineData("2·3", "6")]
        [InlineData("3×10^5", "300000")]
        public void MiddleDotAndMultiplicationSymbols_ShouldMatchEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}'");
        }

        [Theory]
        [InlineData("干冰", "CO2")]
        [InlineData("干冰", "二氧化碳")]
        [InlineData("水银", "Hg")]
        [InlineData("汞", "Hg")]
        [InlineData("水银", "汞")]
        public void ChemicalSynonyms_DryIceAndMercury_ShouldMatchEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}'");
        }

        [Fact]
        public void OpenXmlSpreadsheetHelper_BooleanAndFormulaStrCells_ShouldParseCorrectly()
        {
            // Synthesize an in-memory .xlsx containing boolean (b) and formula string (str) cells
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                // [Content_Types].xml
                var ctEntry = archive.CreateEntry("[Content_Types].xml");
                using (var writer = new StreamWriter(ctEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
    <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
    <Default Extension=""xml"" ContentType=""application/xml""/>
    <Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
    <Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
</Types>");
                }

                // _rels/.rels
                var relsEntry = archive.CreateEntry("_rels/.rels");
                using (var writer = new StreamWriter(relsEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
    <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>");
                }

                // xl/workbook.xml
                var wbEntry = archive.CreateEntry("xl/workbook.xml");
                using (var writer = new StreamWriter(wbEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
    <sheets>
        <sheet name=""Sheet1"" sheetId=""1"" r:id=""rId1""/>
    </sheets>
</workbook>");
                }

                // xl/_rels/workbook.xml.rels
                var wbRelsEntry = archive.CreateEntry("xl/_rels/workbook.xml.rels");
                using (var writer = new StreamWriter(wbRelsEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
    <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
</Relationships>");
                }

                // xl/worksheets/sheet1.xml
                var sheetEntry = archive.CreateEntry("xl/worksheets/sheet1.xml");
                using (var writer = new StreamWriter(sheetEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<worksheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
    <sheetData>
        <row r=""1"">
            <c r=""A1"" t=""b""><v>1</v></c>
            <c r=""B1"" t=""b""><v>0</v></c>
            <c r=""C1"" t=""str""><v>CalculatedVal</v></c>
        </row>
    </sheetData>
</worksheet>");
                }
            }

            ms.Position = 0;
            var rows = OpenXmlSpreadsheetHelper.ReadSpreadsheet(ms);
            Assert.NotEmpty(rows);
            var firstRow = rows[0];
            Assert.True(firstRow.Cells.Count >= 3);
            Assert.Equal("True", firstRow.Cells[0]);
            Assert.Equal("False", firstRow.Cells[1]);
            Assert.Equal("CalculatedVal", firstRow.Cells[2]);
        }

        [Fact]
        public async Task SystemHealthDto_MemoryTelemetry_ShouldExposeValidMetrics()
        {
            var service = new SystemHealthService(_context);
            var health = await service.GetSystemHealthAsync();

            Assert.NotNull(health);
            Assert.True(health.GcMemoryBytes > 0);
            Assert.False(string.IsNullOrWhiteSpace(health.GcMemoryFormatted));

            // Host / Container memory telemetry metrics
            Assert.True(health.TotalAvailableMemoryBytes >= 0);
            Assert.False(string.IsNullOrWhiteSpace(health.TotalAvailableMemoryFormatted));
            Assert.True(health.MemoryPressurePercentage >= 0 && health.MemoryPressurePercentage <= 100);
            Assert.True(health.GcPauseRatio >= 0);
        }
    }
}
