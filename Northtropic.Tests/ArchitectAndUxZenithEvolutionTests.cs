using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxZenithEvolutionTests
    {
        [Fact]
        public void CheckFillInBlankMatch_ChemicalReactionCommutative_MatchesCorrectly()
        {
            // 1. 经典复分解反应项交换次序匹配
            Assert.True(PracticeService.CheckFillInBlankMatch("2NaOH + CuSO4 = Cu(OH)2 + Na2SO4", "CuSO4 + 2NaOH = Na2SO4 + Cu(OH)2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("CuSO4 + 2NaOH = Na2SO4 + Cu(OH)2", "2NaOH + CuSO4 = Cu(OH)2 + Na2SO4"));

            // 2. 箭头连接符与简单化合反应
            Assert.True(PracticeService.CheckFillInBlankMatch("2H2 + O2 -> 2H2O", "O2 + 2H2 -> 2H2O"));
            Assert.True(PracticeService.CheckFillInBlankMatch("C + O2 = CO2", "O2 + C = CO2"));

            // 3. 反应物或生成物化学式错误应被拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("NaOH + CuSO4 = Cu(OH)2 + Na2SO4", "2NaOH + CuSO4 = Cu(OH)2 + Na2SO4"));
            Assert.False(PracticeService.CheckFillInBlankMatch("2NaOH + FeCl3 = Fe(OH)3 + 3NaCl", "2NaOH + CuSO4 = Cu(OH)2 + Na2SO4"));
        }

        [Fact]
        public void CheckFillInBlankMatch_LaTeXSystemsOfEquationsAndMultiVariable_MatchesCorrectly()
        {
            // 1. 多元方程组顺序无关等价
            Assert.True(PracticeService.CheckFillInBlankMatch("x=2, y=3", "y=3, x=2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x = 1, y = -2, z = 5", "z=5, x=1, y=-2"));

            // 2. LaTeX cases 格式剥离与平铺等价
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\begin{cases} x=2 \\ y=3 \end{cases}", "x=2, y=3"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\begin{cases} y=3 \\ x=2 \end{cases}", "x=2, y=3"));

            // 3. 坐标点格式与方程组格式互认
            Assert.True(PracticeService.CheckFillInBlankMatch("(2, 3)", "x=2, y=3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(x, y) = (2, 3)", "x=2, y=3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x=2, y=3", "(2, 3)"));

            // 4. 数值不匹配应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("(2, 4)", "x=2, y=3"));
            Assert.False(PracticeService.CheckFillInBlankMatch("x=3, y=2", "x=2, y=3"));
        }

        [Fact]
        public void CheckFillInBlankMatch_InequalityToIntervalBidirectional_MatchesCorrectly()
        {
            // 1. 单边不等式与无穷区间等价
            Assert.True(PracticeService.CheckFillInBlankMatch("x >= 3", "[3, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x > 2", "(2, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x <= 5", "(-inf, 5]"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x < 4", "(-inf, 4)"));

            // 2. 双边复合不等式与闭/开区间等价
            Assert.True(PracticeService.CheckFillInBlankMatch("-1 < x < 3", "(-1, 3)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2 <= x <= 7", "[2, 7]"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0 < x <= 5", "(0, 5]"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3 <= x < 8", "[3, 8)"));

            // 3. LaTeX 集合属于符号 \\in 剥离
            Assert.True(PracticeService.CheckFillInBlankMatch(@"x \in [1, 5]", "[1, 5]"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"x \in (2, +inf)", "(2, +inf)"));

            // 4. 端点开闭错误或数值不匹配应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("x > 3", "[3, +inf)"));
            Assert.False(PracticeService.CheckFillInBlankMatch("x >= 3", "(3, +inf)"));
            Assert.False(PracticeService.CheckFillInBlankMatch("-1 <= x < 3", "(-1, 3)"));
        }

        [Fact]
        public void CheckFillInBlankMatch_CompoundPhysicalUnits_MatchesCorrectly()
        {
            // 1. 复合物理单位中文与国际单位符号互认
            Assert.True(PracticeService.CheckFillInBlankMatch("15 m/s", "15 米每秒"));
            Assert.True(PracticeService.CheckFillInBlankMatch("50 N*m", "50 牛·米"));
            Assert.True(PracticeService.CheckFillInBlankMatch("100 kW*h", "100 千瓦时"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1.2 g/cm^3", "1.2 g/cm³"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1000 kg/m3", "1000 kg/m³"));

            // 2. StripCommonUnits 剥离能力验证
            Assert.Equal("15", PracticeService.StripCommonUnits("15 米每秒"));
            Assert.Equal("50", PracticeService.StripCommonUnits("50 牛·米"));
            Assert.Equal("100", PracticeService.StripCommonUnits("100 千瓦时"));
        }

        [Fact]
        public void GenerateEquivalentMatchReason_PedagogicalExplanations_ReturnsClearReasons()
        {
            // 1. 化学方程式等价原因
            var qChem = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "CuSO4 + 2NaOH = Na2SO4 + Cu(OH)2"
            };
            var reasonChem = PracticeService.GenerateEquivalentMatchReason(qChem, "2NaOH + CuSO4 = Cu(OH)2 + Na2SO4");
            Assert.Contains("化学方程式反应项等价", reasonChem);

            // 2. 方程组与多元解集等价原因
            var qEq = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "x=2, y=3"
            };
            var reasonEq = PracticeService.GenerateEquivalentMatchReason(qEq, "y=3, x=2");
            Assert.Contains("方程组与多元解集等价", reasonEq);

            // 3. 不等式与区间等价原因
            var qIneq = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "[3, +inf)"
            };
            var reasonIneq = PracticeService.GenerateEquivalentMatchReason(qIneq, "x >= 3");
            Assert.Contains("不等式与实数区间解集等价", reasonIneq);

            // 4. 复合单位等价原因
            var qUnit = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "15 m/s"
            };
            var reasonUnit = PracticeService.GenerateEquivalentMatchReason(qUnit, "15 米每秒");
            Assert.Contains("物理/科学单位智能对齐等价", reasonUnit);
        }

        [Fact]
        public void OpenXmlSpreadsheetHelper_CreateAndRead_PreservesDataIntegrity()
        {
            var headers = new List<string> { "姓名", "学科", "考点", "得分" };
            var rows = new List<List<string>>
            {
                new List<string> { "张三", "高中数学", "导数极值", "98.5" },
                new List<string> { "李四", "初中物理", "欧姆定律", "100" },
                new List<string> { "王五", "信息奥赛", "动态规划", "85" }
            };

            // 1. 导出二进制数据
            byte[] xlsxBytes = OpenXmlSpreadsheetHelper.CreateSpreadsheet("测试成绩表", headers, rows);
            Assert.NotNull(xlsxBytes);
            Assert.True(xlsxBytes.Length > 0);

            // 2. 读取解析校验
            using var ms = new MemoryStream(xlsxBytes);
            var readRows = OpenXmlSpreadsheetHelper.ReadSpreadsheet(ms);
            Assert.Equal(4, readRows.Count); // 表头 + 3 行数据

            // 校验表头
            Assert.Equal("姓名", readRows[0].Cells[0]);
            Assert.Equal("学科", readRows[0].Cells[1]);
            Assert.Equal("考点", readRows[0].Cells[2]);
            Assert.Equal("得分", readRows[0].Cells[3]);

            // 校验行数据
            Assert.Equal("张三", readRows[1].Cells[0]);
            Assert.Equal("高中数学", readRows[1].Cells[1]);
            Assert.Equal("李四", readRows[2].Cells[0]);
            Assert.Equal("初中物理", readRows[2].Cells[1]);
            Assert.Equal("王五", readRows[3].Cells[0]);
            Assert.Equal("动态规划", readRows[3].Cells[2]);
        }

        [Fact]
        public async Task QuestionImportService_XlsxTemplateAndImportFlow_Success()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var importService = new QuestionImportService(context);

                // 1. 生成内置 XLSX 模板
                byte[] templateBytes = importService.GenerateXlsxTemplateBytes();
                Assert.NotNull(templateBytes);
                Assert.True(templateBytes.Length > 0);

                // 2. 模拟用户上传该模板进行导入
                using var templateStream = new MemoryStream(templateBytes);
                var testUserId = Guid.NewGuid();
                var result = await importService.ParseXlsxImportAsync(templateStream, testUserId);

                // 3. 验证导入结果
                Assert.True(result.IsSuccess);
                Assert.Equal(3, result.SuccessCount);
                Assert.Equal(0, result.FailureCount);
                Assert.Equal(0, result.DuplicateCount);

                // 4. 校验数据库中的持久化数据
                var imported = await context.Questions.ToListAsync();
                Assert.Equal(3, imported.Count);

                var physicsQ = imported.FirstOrDefault(q => q.Subject == "初中物理");
                Assert.NotNull(physicsQ);
                Assert.Equal("压强与浮力", physicsQ.Category);
                Assert.Equal(QuestionType.SingleChoice, physicsQ.Type);
                Assert.Equal("A", physicsQ.CorrectAnswer);

                var csharpQ = imported.FirstOrDefault(q => q.Subject.Contains("C#"));
                Assert.NotNull(csharpQ);
                Assert.Equal(QuestionType.MultipleChoice, csharpQ.Type);
                Assert.Equal("A,B,C", csharpQ.CorrectAnswer);

                var mathQ = imported.FirstOrDefault(q => q.Subject == "高中数学");
                Assert.NotNull(mathQ);
                Assert.Equal(QuestionType.FillInBlank, mathQ.Type);
                Assert.Equal("1", mathQ.CorrectAnswer);
            }
        }

        [Fact]
        public async Task QuestionManagementService_XlsxExport_ProducesValidSpreadsheet()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var mgmtService = new QuestionManagementService(context);
                var userId = Guid.NewGuid();

                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "生物学",
                    Category = "遗传与进化",
                    GradeTarget = "高中二年级",
                    Type = QuestionType.SingleChoice,
                    Stem = "DNA 的双螺旋结构是由哪两位科学家提出的？",
                    OptionsJson = "[\"A. 沃森和克里克\",\"B. 达尔文和孟德尔\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "1953年沃森与克里克提出 DNA 双螺旋模型。",
                    CreatedByUserId = userId,
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    Difficulty = 2
                };
                context.Questions.Add(q1);
                await context.SaveChangesAsync();

                // 导出为 XLSX
                byte[] exportedBytes = await mgmtService.ExportQuestionsXlsxAsync(userId);
                Assert.NotNull(exportedBytes);
                Assert.True(exportedBytes.Length > 0);

                // 通过 OpenXmlSpreadsheetHelper 解析验证
                using var ms = new MemoryStream(exportedBytes);
                var parsedRows = OpenXmlSpreadsheetHelper.ReadSpreadsheet(ms);

                Assert.True(parsedRows.Count >= 2); // 表头 + 至少 1 条数据
                Assert.Contains("学科", parsedRows[0].Cells);
                Assert.Contains("题干", parsedRows[0].Cells);

                var dataRow = parsedRows[1];
                Assert.Equal("生物学", dataRow.Cells[1]);
                Assert.Equal("遗传与进化", dataRow.Cells[2]);
                Assert.Equal("单选题", dataRow.Cells[4]);
                Assert.Equal("DNA 的双螺旋结构是由哪两位科学家提出的？", dataRow.Cells[6]);
                Assert.Equal("A", dataRow.Cells[8]);
            }
        }
    }
}
