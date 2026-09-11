using System;
using System.Collections.Generic;
using System.IO;
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
    public class ArchitectAndUxOmniSupremeTranscendenceTests
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

        #region 1. 系统架构师：系统健康多维加权评分模型与运维诊断建议测试

        [Fact]
        public async Task SystemHealthService_GetSystemHealthAsync_CalculatesScoreAndRecommendations()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                using var dummyContext = factory.CreateDbContext();

                var healthService = new SystemHealthService(dummyContext, factory);
                var health = await healthService.GetSystemHealthAsync();

                Assert.NotNull(health);
                // 内存 SQLite 数据库完整性正常，健康分预期在合理高位
                Assert.InRange(health.HealthScore, 80, 100);
                Assert.False(string.IsNullOrWhiteSpace(health.HealthRating));
                Assert.NotNull(health.HealthRecommendations);
                Assert.NotEmpty(health.HealthRecommendations);
                Assert.All(health.HealthRecommendations, r => Assert.False(string.IsNullOrWhiteSpace(r)));
            }
        }

        #endregion

        #region 2. 用户体验专家：解析几何直线方程智能等价判定与教学反馈测试

        [Theory]
        [InlineData("2x + y - 3 = 0", "y = -2x + 3", true)]
        [InlineData("y = -2x + 3", "2x + y - 3 = 0", true)]
        [InlineData("2x + y = 3", "2x + y - 3 = 0", true)]
        [InlineData("4x + 2y - 6 = 0", "2x + y - 3 = 0", true)]
        [InlineData("-2x - y + 3 = 0", "2x + y - 3 = 0", true)]
        [InlineData("x/1.5 + y/3 = 1", "2x + y - 3 = 0", true)]
        [InlineData("y - 1 = -2(x - 1)", "2x + y - 3 = 0", true)]
        [InlineData("x = 3", "2x - 6 = 0", true)]
        [InlineData("y = -2", "y + 2 = 0", true)]
        [InlineData("2x + y - 3 = 0", "2x + y - 4 = 0", false)]
        [InlineData("2x + y - 3 = 0", "x + 2y - 3 = 0", false)]
        public void CheckFillInBlankMatch_LinearEquations_ShouldMatch(string user, string correct, bool expected)
        {
            var match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, match);
        }

        [Fact]
        public void GenerateEquivalentMatchReason_LinearEquationFeedback_ProvidesClearGuidance()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "2x + y - 3 = 0" };
            var reason = PracticeService.GenerateEquivalentMatchReason(q, "y = -2x + 3");
            Assert.Contains("解析几何直线方程等价", reason);
        }

        #endregion

        #region 3. 用户体验专家：复数代数形式加法交换律与纯虚数等价测试

        [Theory]
        [InlineData("3 + 4i", "4i + 3", true)]
        [InlineData("4i + 3", "3 + 4i", true)]
        [InlineData("1 - 2i", "-2i + 1", true)]
        [InlineData("-2i + 1", "1 - 2i", true)]
        [InlineData("2i", "0 + 2i", true)]
        [InlineData("0 + 2i", "2i", true)]
        [InlineData("i", "1i", true)]
        [InlineData("-i", "-1i", true)]
        [InlineData("3 + 4i", "3 - 4i", false)]
        [InlineData("3 + 4i", "4 + 3i", false)]
        public void CheckFillInBlankMatch_ComplexNumbers_ShouldMatch(string user, string correct, bool expected)
        {
            var match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, match);
        }

        [Fact]
        public void GenerateEquivalentMatchReason_ComplexFeedback_ProvidesClearGuidance()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "3 + 4i" };
            var reason = PracticeService.GenerateEquivalentMatchReason(q, "4i + 3");
            Assert.Contains("复数代数形式等价", reason);
        }

        #endregion

        #region 4. 用户体验专家：三维空间直角坐标与空间向量智能等价测试

        [Theory]
        [InlineData("(1, 2, 3)", "x=1,y=2,z=3", true)]
        [InlineData("x=1,y=2,z=3", "(1, 2, 3)", true)]
        [InlineData("(1, 2, 3)", "x=1,z=3,y=2", true)]
        [InlineData("\\vec{a} = (1, 2, 3)", "(1, 2, 3)", true)]
        [InlineData("\\vec{v} = (1, 2, 3)", "x=1,y=2,z=3", true)]
        [InlineData("(x, y) = (2, 3)", "(2, 3)", true)]
        [InlineData("P(1, 2, 3)", "(1, 2, 3)", true)]
        [InlineData("(1, 2, 3)", "(1, 2, 4)", false)]
        public void CheckFillInBlankMatch_3DCoordinatesAndVectors_ShouldMatch(string user, string correct, bool expected)
        {
            var match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, match);
        }

        [Fact]
        public void GenerateEquivalentMatchReason_3DCoordinateFeedback_ProvidesClearGuidance()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "x=1,y=2,z=3" };
            var reason = PracticeService.GenerateEquivalentMatchReason(q, "(1, 2, 3)");
            Assert.Contains("空间直角坐标/向量等价", reason);
        }

        #endregion

        #region 5. 系统架构师与用户体验：题库导入智能表头列自适应映射测试

        [Fact]
        public async Task QuestionImportService_CsvImport_WithShuffledColumns_ShouldMapCorrectly()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                using var dummyContext = factory.CreateDbContext();

                var importService = new QuestionImportService(dummyContext, null, null, factory);

                // 故意打乱列顺序：题干,正确答案,学科,分类,年级,题型,解析,难度
                var csvBuilder = new StringBuilder();
                csvBuilder.AppendLine("题干,正确答案,学科,分类,年级,题型,解析,难度");
                csvBuilder.AppendLine("求解二元一次方程直线斜率,k=2,高中数学,解析几何,高二,填空题,根据斜截式方程 y=2x+1 斜率为 2,4");

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvBuilder.ToString()));
                var result = await importService.ParseAndImportQuestionsByFileFormatAsync(stream, "import.csv", Guid.NewGuid());

                Assert.True(result.IsSuccess, string.Join("; ", result.ErrorMessages));
                Assert.Equal(1, result.SuccessCount);

                using var verifyCtx = factory.CreateDbContext();
                var importedQ = await verifyCtx.Questions.FirstOrDefaultAsync(q => q.Stem.Contains("求解二元一次方程直线斜率"));
                Assert.NotNull(importedQ);
                Assert.Equal("求解二元一次方程直线斜率", importedQ.Stem);
                Assert.Equal("k=2", importedQ.CorrectAnswer);
                Assert.Equal("高中数学", importedQ.Subject);
                Assert.Equal("解析几何", importedQ.Category);
                Assert.Equal("高二", importedQ.GradeTarget);
                Assert.Equal(QuestionType.FillInBlank, importedQ.Type);
                Assert.Equal(4, importedQ.Difficulty);
            }
        }

        [Fact]
        public async Task QuestionImportService_CsvImport_WithEnglishHeaders_ShouldMapCorrectly()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                using var dummyContext = factory.CreateDbContext();

                var importService = new QuestionImportService(dummyContext, null, null, factory);

                // 英文同义词表头：Stem,Answer,Subject,Category,Grade,Type,Explanation,Difficulty
                var csvBuilder = new StringBuilder();
                csvBuilder.AppendLine("Stem,Answer,Subject,Category,Grade,Type,Explanation,Difficulty");
                csvBuilder.AppendLine("What is the speed of light in vacuum?,3*10^8 m/s,Physics,Modern Physics,Grade 11,填空题,Speed of light is approx 3e8 m/s,5");

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvBuilder.ToString()));
                var result = await importService.ParseAndImportQuestionsByFileFormatAsync(stream, "import.csv", Guid.NewGuid());

                Assert.True(result.IsSuccess, string.Join("; ", result.ErrorMessages));
                Assert.Equal(1, result.SuccessCount);

                using var verifyCtx = factory.CreateDbContext();
                var importedQ = await verifyCtx.Questions.FirstOrDefaultAsync(q => q.Stem.Contains("What is the speed of light"));
                Assert.NotNull(importedQ);
                Assert.Equal("3*10^8 m/s", importedQ.CorrectAnswer);
                Assert.Equal("Physics", importedQ.Subject);
                Assert.Equal(5, importedQ.Difficulty);
            }
        }

        #endregion
    }
}
