using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxOmniSummitEvolutionTests
    {
        public class SqliteDbContextFactory : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;
            public SqliteDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
            public AppDbContext CreateDbContext() => new AppDbContext(_options);
        }

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

        #region 1. 系统架构师：线程池并发容量与垃圾回收代际监控遥测

        [Fact]
        public async Task SystemHealthService_ThreadPoolAndGcTelemetry_CollectedAccurately()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                using var dummyContext = factory.CreateDbContext();

                var healthService = new SystemHealthService(dummyContext, factory);
                var health = await healthService.GetSystemHealthAsync();

                Assert.NotNull(health);
                // 验证线程池可用工作线程数与完成端口线程数遥测正常采集中
                Assert.True(health.ThreadPoolAvailableWorkerThreads > 0, "ThreadPoolAvailableWorkerThreads 应大于 0");
                Assert.True(health.ThreadPoolMaxWorkerThreads > 0, "ThreadPoolMaxWorkerThreads 应大于 0");
                Assert.True(health.ThreadPoolAvailableIocpThreads > 0, "ThreadPoolAvailableIocpThreads 应大于 0");

                // 验证 GC 代际垃圾回收计数遥测采集已连通
                Assert.True(health.GcGen0Collections >= 0, "GcGen0Collections 应为非负整数");
                Assert.True(health.GcGen1Collections >= 0, "GcGen1Collections 应为非负整数");
                Assert.True(health.GcGen2Collections >= 0, "GcGen2Collections 应为非负整数");
            }
        }

        #endregion

        #region 2. 用户体验专家：解析几何圆的标准方程与一般方程代数展开/移项等价

        [Theory]
        [InlineData("(x - 1)^2 + (y + 2)^2 = 9", 1.0, -2.0, 9.0)]
        [InlineData("(y + 2)^2 + (x - 1)^2 = 3^2", 1.0, -2.0, 9.0)]
        [InlineData("(1 - x)^2 + (y + 2)^2 = 9", 1.0, -2.0, 9.0)]
        [InlineData("x^2 + y^2 - 2x + 4y - 4 = 0", 1.0, -2.0, 9.0)]
        [InlineData("2x^2 + 2y^2 - 4x + 8y - 8 = 0", 1.0, -2.0, 9.0)]
        [InlineData("(x - 1)^2 + (y + 2)^2 - 9 = 0", 1.0, -2.0, 9.0)]
        [InlineData("x^2 + y^2 = 16", 0.0, 0.0, 16.0)]
        public void CircleEquation_TryNormalizeCircleEquation_ExtractsCanonicalParameters(
            string eq, double expectedCenterX, double expectedCenterY, double expectedRSquared)
        {
            bool success = PracticeService.TryNormalizeCircleEquation(eq, out double cx, out double cy, out double rSq);

            Assert.True(success, $"圆方程应能成功解析: {eq}");
            Assert.Equal(expectedCenterX, cx, 3);
            Assert.Equal(expectedCenterY, cy, 3);
            Assert.Equal(expectedRSquared, rSq, 3);
        }

        [Theory]
        [InlineData("(x - 1)^2 + (y + 2)^2 = 9", "x^2 + y^2 - 2x + 4y - 4 = 0")]
        [InlineData("x^2 + y^2 - 2x + 4y - 4 = 0", "(x - 1)^2 + (y + 2)^2 = 9")]
        [InlineData("(y + 2)^2 + (x - 1)^2 = 9", "(x - 1)^2 + (y + 2)^2 = 9")]
        [InlineData("(1 - x)^2 + (y + 2)^2 = 9", "(x - 1)^2 + (y + 2)^2 = 9")]
        [InlineData("2x^2 + 2y^2 - 4x + 8y - 8 = 0", "(x - 1)^2 + (y + 2)^2 = 9")]
        [InlineData("(x - 2)^2 + (y - 3)^2 = 25", "(y - 3)^2 + (x - 2)^2 = 25")]
        [InlineData("(2 - x)^2 + (3 - y)^2 = 25", "(x - 2)^2 + (y - 3)^2 = 25")]
        [InlineData("x^2 + y^2 = 4", "x^2 + y^2 - 4 = 0")]
        public void CheckFillInBlankMatch_CircleEquations_StandardVsGeneralForm_Equivalent(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"圆方程应当识别为等价: '{userAns}' 与 '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.Contains("圆的方程等价", reason);
        }

        #endregion

        #region 3. 用户体验专家：平面向量数量积乘法交换律与模长记号等价

        [Theory]
        [InlineData("\\vec{a}\\cdot\\vec{b} = -3", "\\vec{b}\\cdot\\vec{a} = -3")]
        [InlineData("a \\cdot b = -3", "b \\cdot a = -3")]
        [InlineData("\\vec{a}\\cdot\\vec{b} = -3", "-3")]
        [InlineData("b * a = 6", "a * b = 6")]
        [InlineData("|\\vec{a}| = 2", "|a| = 2")]
        [InlineData("|\\vec{a}| = 2", "2")]
        [InlineData("|a| = 5", "5")]
        public void CheckFillInBlankMatch_PlaneVector_CommutativityAndModulus_Equivalent(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"平面向量表达式应当等价匹配: '{userAns}' 与 '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.True(reason.Contains("平面向量") || reason.Contains("等价"), $"反馈应指出平面向量或智能等价性: {reason}");
        }

        #endregion

        #region 4. 用户体验专家：热化学方程式焓变与物理电功能量单位换算等价

        [Theory]
        [InlineData("\\Delta H = -57.3 kJ/mol", "\\Delta H = -57.3 kJ*mol^-1")]
        [InlineData("\\Delta H = -57.3 kJ/mol", "-57.3 kJ/mol")]
        [InlineData("-57.3 kJ/mol", "-57.3")]
        [InlineData("-57.3千焦/摩尔", "-57.3 kJ/mol")]
        [InlineData("1 kWh", "3.6*10^6 J")]
        [InlineData("1度", "3.6e6 J")]
        [InlineData("2 kWh", "7.2*10^6 J")]
        public void CheckFillInBlankMatch_ThermochemicalEnthalpyAndEnergyConversion_Equivalent(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"热化学焓变/电功能量应当等价匹配: '{userAns}' 与 '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.True(reason.Contains("热化学") || reason.Contains("单位") || reason.Contains("等价"), $"反馈应指出单位或科学等价性: {reason}");
        }

        #endregion

        #region 5. 用户体验专家：三角函数特殊角角度制与弧度制等价

        [Theory]
        [InlineData("30°", "\\pi/6")]
        [InlineData("45°", "\\pi/4")]
        [InlineData("60°", "\\pi/3")]
        [InlineData("90°", "\\pi/2")]
        [InlineData("180°", "\\pi")]
        [InlineData("\\pi/4", "45°")]
        [InlineData("\\pi/6", "30")]
        public void CheckFillInBlankMatch_TrigonometricAngleAndRadian_Equivalent(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"角度制与弧度制应当等价匹配: '{userAns}' 与 '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.Contains("三角角度与弧度制等价", reason);
        }

        #endregion
    }
}
