using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxApexGrandmasterTests
    {
        private static (SqliteConnection Connection, DbContextOptions<AppDbContext> Options) CreateSharedInMemoryDb()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            using (var ctx = new AppDbContext(options))
            {
                ctx.Database.EnsureCreated();
            }

            return (connection, options);
        }

        #region 1. 系统架构师维度：系统自检探针与深度遥测

        [Fact]
        public async Task SystemHealthService_RunArchitecturalSelfDiagnosticAsync_PassesAndCapturesCheckpoints()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            using (var context = new AppDbContext(options))
            {
                // 插入测试题目以验证题库表统计探针
                context.Questions.Add(new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试题目1",
                    CorrectAnswer = "A",
                    Type = QuestionType.SingleChoice,
                    Subject = "数学",
                    GradeTarget = "高一",
                    CreatedAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context);

                var diagResult = await healthService.RunArchitecturalSelfDiagnosticAsync();

                Assert.NotNull(diagResult);
                Assert.True(diagResult.IsPassed);
                Assert.Equal("Pass", diagResult.OverallStatus);
                Assert.Equal("ok", diagResult.SqliteIntegrity);
                Assert.Equal("ok", diagResult.ForeignKeyIntegrity);
                Assert.True(diagResult.LatencyMs >= 0);
                Assert.False(string.IsNullOrEmpty(diagResult.LatencyRating));
                Assert.True(diagResult.GcMemoryBytes > 0);
                Assert.True(diagResult.TotalQuestionsScanned >= 1);
                Assert.NotEmpty(diagResult.DiagnosticCheckpoints);
                Assert.Contains(diagResult.DiagnosticCheckpoints, cp => cp.Contains("SQLite PRAGMA"));
                Assert.Contains(diagResult.DiagnosticCheckpoints, cp => cp.Contains("核心数据表巡检"));
            }
        }

        [Fact]
        public async Task SystemHealthService_GetSystemHealthAsync_IncludesLatencyRatingAndPoolSaturation()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            using (var context = new AppDbContext(options))
            {
                var healthService = new SystemHealthService(context);
                var healthDto = await healthService.GetSystemHealthAsync();

                Assert.NotNull(healthDto);
                Assert.False(string.IsNullOrEmpty(healthDto.LatencyRating));
                Assert.True(healthDto.ThreadPoolSaturationRatio >= 0 && healthDto.ThreadPoolSaturationRatio <= 1.0);
            }
        }

        #endregion

        #region 2. 核心代数与符号引擎：空间三维向量与坐标系双向互通

        [Fact]
        public void PracticeService_SpatialBasisVectors3D_MatchesCoordinatesAndFractions()
        {
            // 1. 三维基向量 i, j, k 与坐标三元组互认
            Assert.True(PracticeService.CheckFillInBlankMatch("(1, 2, 3)", @"\mathbf{i} + 2\mathbf{j} + 3\mathbf{k}"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\mathbf{i} + 2\mathbf{j} + 3\mathbf{k}", "(1, 2, 3)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(1, 2, 3)", @"\vec{i} + 2\vec{j} + 3\vec{k}"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"(1, 2, 3)", "i + 2j + 3k"));

            // 2. 基向量乱序项与负项
            Assert.True(PracticeService.CheckFillInBlankMatch("(1, 2, 3)", "3k + 2j + i"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-2, 0, 5)", "-2i + 5k"));
            Assert.True(PracticeService.CheckFillInBlankMatch("-2i + 5k", "(-2, 0, 5)"));

            // 3. 包含分数的坐标与三维基向量
            Assert.True(PracticeService.CheckFillInBlankMatch("(1/2, 3/4, -1)", "0.5i + 0.75j - k"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0.5i + 0.75j - k", "(1/2, 3/4, -1)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(1/2, 3/4)", "(0.5, 0.75)"));

            // 4. 不等价三维向量应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("(1, 2, 3)", "i + 2j + 4k"));
            Assert.False(PracticeService.CheckFillInBlankMatch("(1, 2, 3)", "i - 2j + 3k"));
        }

        #endregion

        #region 3. 核心代数与符号引擎：三角函数精确特殊角求值 (度数与弧度)

        [Fact]
        public void PracticeService_TrigonometricSpecialAngles_MatchesExactValues()
        {
            // 1. 度数特殊角求值
            Assert.True(PracticeService.CheckFillInBlankMatch("sin(30°)", "0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("sin(30)", "1/2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("cos(60°)", "0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("cos(60)", "1/2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("tan(45°)", "1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("tan(45)", "1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("sin(90°)", "1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("cos(180°)", "-1"));

            // 2. 弧度制特殊角求值
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\sin(pi/6)", "0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"sin(pi/6)", "1/2"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\cos(pi/3)", "0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"tan(pi/4)", "1"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"sin(pi/2)", "1"));

            // 3. 不符数值应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("sin(30°)", "0.8"));
            Assert.False(PracticeService.CheckFillInBlankMatch("cos(60°)", "-0.5"));
        }

        #endregion

        #region 4. 核心代数与符号引擎：对数任意底数与复合指数解构

        [Fact]
        public void PracticeService_LogarithmsArbitraryBaseAndLnExp_MatchesExactValues()
        {
            // 1. 任意底对数 LaTeX 与普通格式
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\log_{2}{8}", "3"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\log_2 8", "3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("log(2, 8)", "3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("log(2, 16)", "4"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\log_{3}{27}", "3"));

            // 2. 常用对数 lg 与自然对数 ln(e^n)
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\lg{100}", "2"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\lg 100", "2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("lg(1000)", "3"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\ln e", "1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("ln(e)", "1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("ln(e^2)", "2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("ln(e^3)", "3"));

            // 3. 不符对数值应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch(@"\log_2 8", "4"));
            Assert.False(PracticeService.CheckFillInBlankMatch("ln(e^2)", "3"));
        }

        #endregion

        #region 5. 复数代数运算：共轭复数与虚数单位高次幂

        [Fact]
        public void PracticeService_ComplexConjugatesAndUnitPowers_MatchesCorrectly()
        {
            // 1. 复数共轭解构
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\overline{1+2i}", "1-2i"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\overline{3-4i}", "3+4i"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\bar{2+5i}", "2-5i"));

            // 2. 虚数单位幂次归一
            Assert.True(PracticeService.CheckFillInBlankMatch("i^2", "-1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("i^3", "-i"));
            Assert.True(PracticeService.CheckFillInBlankMatch("i^4", "1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("i^0", "1"));
        }

        #endregion

        #region 6. 化学命名与反应式：多原子离子团与扩展反应条件

        [Fact]
        public void PracticeService_ChemicalPolyatomicIonsAndConditions_MatchesCorrectly()
        {
            // 1. 多原子离子同义词
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("碳酸根", "CO3^2-"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("碳酸根离子", "CO32-"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("铵根", "NH4+"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("高锰酸根", "MnO4-"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("硫酸根", "SO4^2-"));
            Assert.True(PracticeService.IsChemicalNomenclatureEquivalent("氢氧根", "OH-"));

            // 2. 反应条件识别 (通电、电解、高压)
            Assert.True(PracticeService.CheckFillInBlankMatch("2H2O [通电] 2H2 + O2", "2H2O = 2H2 + O2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2NaCl + 2H2O [电解] 2NaOH + H2 + Cl2", "2NaCl + 2H2O = 2NaOH + H2 + Cl2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("N2 + 3H2 [高压] 2NH3", "N2 + 3H2 = 2NH3"));
        }

        #endregion
    }
}
