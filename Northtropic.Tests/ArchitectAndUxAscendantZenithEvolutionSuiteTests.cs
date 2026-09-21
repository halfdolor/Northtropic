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
    public class ArchitectAndUxAscendantZenithEvolutionSuiteTests
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

        #region STEM Equivalence Groups 46 - 55 Tests

        [Theory]
        // 46. 万有引力与天体运动黄金代换公式
        [InlineData("GM = gR^2", "g = GM/R^2")]
        [InlineData("GM = gr^2", "g' = GM/r^2")]
        [InlineData("gR^2 = GM", "GM = gR^2")]
        [InlineData("g = GM/R²", "GM = gR²")]
        // 47. 第一宇宙速度推导公式
        [InlineData("v = \\sqrt{gR}", "v^2 = gR")]
        [InlineData("v = \\sqrt{GM/R}", "v^2 = GM/R")]
        [InlineData("v1 = \\sqrt{gR}", "v^2 = gR")]
        [InlineData("v = sqrt(gr)", "v^2 = gr")]
        // 48. LC振荡电路固有周期与频率
        [InlineData("T = 2\\pi\\sqrt{LC}", "2\\pi\\sqrt{LC} = T")]
        [InlineData("f = 1/(2\\pi\\sqrt{LC})", "1/(2\\pi\\sqrt{LC}) = f")]
        [InlineData("\\omega = 1/\\sqrt{LC}", "1/\\sqrt{LC} = \\omega")]
        // 49. 化学反应速率与平衡常数公式
        [InlineData("v = \\Delta c/\\Delta t", "\\Delta c = v \\cdot \\Delta t")]
        [InlineData("v = \\delta c/\\delta t", "\\delta c/\\delta t = v")]
        [InlineData("v = \\Delta c/t", "\\Delta c/t = v")]
        // 50. 阿伏加德罗常数微观与宏观换算
        [InlineData("N = n \\cdot N_A", "n = N/N_A")]
        [InlineData("M = m_0 \\cdot N_A", "m_0 = M/N_A")]
        [InlineData("N_A = N/n", "N = n \\cdot N_A")]
        // 51. 磁感应强度定义式与安培力扩展
        [InlineData("B = F/(IL)", "B = F/(I \\cdot L)")]
        [InlineData("B = F/(IL)", "F/(IL) = B")]
        [InlineData("B = F/(qv)", "F/(qv) = B")]
        // 52. 高中数学等差数列通项与前n项和
        [InlineData("a_n = a_1 + (n-1)d", "a_n - a_1 = (n-1)d")]
        [InlineData("S_n = n(a_1+a_n)/2", "S_n = na_1 + n(n-1)d/2")]
        [InlineData("a_1 + (n-1)d = a_n", "a_n = a_1 + (n-1)d")]
        // 53. 高中数学等比数列通项与前n项和
        [InlineData("a_n = a_1 q^{n-1}", "a_1 q^{n-1} = a_n")]
        [InlineData("S_n = a_1(1-q^n)/(1-q)", "S_n = (a_1 - a_n q)/(1-q)")]
        // 54. 平面向量数量积与坐标/夹角运算
        [InlineData("\\vec{a} \\cdot \\vec{b} = |a||b|\\cos\\theta", "a \\cdot b = ab\\cos\\theta")]
        [InlineData("\\vec{a} \\cdot \\vec{b} = x_1 x_2 + y_1 y_2", "x_1 x_2 + y_1 y_2 = a \\cdot b")]
        // 55. 电容器电容定义式与平行板决定式
        [InlineData("C = Q/U", "Q = CU")]
        [InlineData("U = Q/C", "C = Q/U")]
        [InlineData("C = \\varepsilon_r S / (4\\pi kd)", "C = \\varepsilon S / (4\\pi kd)")]
        public void PracticeService_CheckSTEMEquivalence_Groups46To55_RecognizesMatches(string a, string b)
        {
            bool equivalent = PracticeService.CheckPhysicsFormulaEquivalence(a, b);
            Assert.True(equivalent, $"Expected '{a}' and '{b}' to be recognized as mathematically/physically equivalent.");
        }

        #endregion

        #region SystemHealthService Cleaning & Heuristics Tests

        [Theory]
        [InlineData("【参考解题结论】A", "A")]
        [InlineData("【标准答案为】 C", "C")]
        [InlineData("【本题答案为】B", "B")]
        [InlineData("【本题正确选项】 D", "D")]
        [InlineData("【解析结论】由此可知：A", "A")]
        [InlineData("综合以上分析，选：B", "B")]
        [InlineData("由分析可知，故选：C", "C")]
        [InlineData("经计算可知：D", "D")]
        [InlineData("根据题意，选：A", "A")]
        [InlineData("由此可推知：B", "B")]
        public void SystemHealthService_CleanAnswerPrefixes_RemovesPedagogicalPollution(string input, string expected)
        {
            var cleaned = SystemHealthService.CleanAnswerPrefixes(input);
            Assert.Equal(expected, cleaned);
        }

        [Theory]
        [InlineData("①", "A")]
        [InlineData("②", "B")]
        [InlineData("③", "C")]
        [InlineData("④", "D")]
        [InlineData("Ⅰ", "A")]
        [InlineData("Ⅱ", "B")]
        [InlineData("Ⅲ", "C")]
        [InlineData("Ⅳ", "D")]
        [InlineData("选 A 项", "A")]
        [InlineData("【B】", "B")]
        [InlineData("(C)", "C")]
        public void SystemHealthService_ExtractChoiceLetter_SupportsCircledAndRomanNumerals(string input, string expected)
        {
            var letter = SystemHealthService.ExtractChoiceLetter(input);
            Assert.Equal(expected, letter);
        }

        [Fact]
        public void SystemHealthService_ExtractOptionsFromStem_SupportsCircledNumbers()
        {
            string stemWithCircled = "质点做简谐运动时，关于回复力的描述正确的是：①与位移大小成正比 ②方向始终指向平衡位置 ③可能为零 ④恒定不变";
            var options = SystemHealthService.ExtractOptionsFromStem(stemWithCircled, out var cleanedStem);

            Assert.Equal("质点做简谐运动时，关于回复力的描述正确的是：", cleanedStem);
            Assert.Equal(4, options.Count);
            Assert.Equal("A. 与位移大小成正比", options[0]);
            Assert.Equal("B. 方向始终指向平衡位置", options[1]);
            Assert.Equal("C. 可能为零", options[2]);
            Assert.Equal("D. 恒定不变", options[3]);
        }

        [Fact]
        public void SystemHealthService_ExtractOptionsFromStem_SupportsHalfBracketsAndRomanNumerals()
        {
            string stemWithHalfBrackets = "下列物理量中属于矢量的是：A）速度 B）速率 C）路程 D）质量";
            var options = SystemHealthService.ExtractOptionsFromStem(stemWithHalfBrackets, out var cleanedStem);

            Assert.Equal("下列物理量中属于矢量的是：", cleanedStem);
            Assert.Equal(4, options.Count);
            Assert.Equal("A. 速度", options[0]);
            Assert.Equal("B. 速率", options[1]);
            Assert.Equal("C. 路程", options[2]);
            Assert.Equal("D. 质量", options[3]);
        }

        #endregion

        #region Markdown Worksheet Export Architecture & UX Tests

        [Fact]
        public async Task QuestionManagementService_ExportQuestionsMarkdownAsync_TeacherSolution_OutputsStructuredTestSheet()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);

            var userId = Guid.NewGuid();
            using (var ctx = factory.CreateDbContext())
            {
                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = userId,
                    Subject = "物理",
                    Category = "力学",
                    Type = QuestionType.SingleChoice,
                    Difficulty = 3,
                    Stem = "关于匀变速直线运动，下列说法正确的是：",
                    OptionsJson = "[\"A. 加速度恒定不变\",\"B. 速度大小恒定\",\"C. 位移与时间成正比\",\"D. 加速度方向随速度改变\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "匀变速直线运动的定义即加速度大小和方向恒定不变的直线运动。",
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedAt = DateTime.UtcNow
                };

                var q2 = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = userId,
                    Subject = "物理",
                    Category = "电学",
                    Type = QuestionType.FillInBlank,
                    Difficulty = 2,
                    Stem = "真空中两个静止的点电荷，它们之间的静电力与它们电荷量的乘积成正比，与它们距离的______成反比。",
                    CorrectAnswer = "二次方",
                    StandardAnalysis = "由库仑定律 F = k q1 q2 / r^2 可知。",
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedAt = DateTime.UtcNow
                };

                ctx.Questions.AddRange(q1, q2);
                await ctx.SaveChangesAsync();

                var mgmtService = new QuestionManagementService(ctx, factory);
                string md = await mgmtService.ExportQuestionsMarkdownAsync(userId, subject: "物理", includeAnswers: true);

                Assert.NotNull(md);
                Assert.Contains("# 物理 标准试卷 · 教研全解版", md);
                Assert.Contains("包含标准答案与权威名师解析", md);
                Assert.Contains("第 1 题", md);
                Assert.Contains("关于匀变速直线运动", md);
                Assert.Contains("- A. 加速度恒定不变", md);
                Assert.Contains("**参考答案**: A", md);
                Assert.Contains("**名师深度解析**: 匀变速直线运动的定义即加速度大小和方向恒定不变的直线运动。", md);
                Assert.Contains("第 2 题", md);
                Assert.Contains("**参考答案**: 二次方", md);
            }
        }

        [Fact]
        public async Task QuestionManagementService_ExportQuestionsMarkdownAsync_StudentBlank_OutputsBlankTestSheet()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);

            var userId = Guid.NewGuid();
            using (var ctx = factory.CreateDbContext())
            {
                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = userId,
                    Subject = "数学",
                    Category = "函数",
                    Type = QuestionType.SingleChoice,
                    Difficulty = 2,
                    Stem = "函数 f(x) = x^2 的对称轴为：",
                    OptionsJson = "[\"A. x = 0\",\"B. y = 0\",\"C. x = 1\",\"D. y = 1\"]",
                    CorrectAnswer = "A",
                    StandardAnalysis = "偶函数对称轴为 y 轴，即 x = 0。",
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedAt = DateTime.UtcNow
                };

                var q2 = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = userId,
                    Subject = "数学",
                    Category = "三角函数",
                    Type = QuestionType.FillInBlank,
                    Difficulty = 1,
                    Stem = "sin(π/6) 的值为______。",
                    CorrectAnswer = "1/2",
                    StandardAnalysis = "基本特殊角三角函数值 sin 30° = 1/2。",
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedAt = DateTime.UtcNow
                };

                ctx.Questions.AddRange(q1, q2);
                await ctx.SaveChangesAsync();

                var mgmtService = new QuestionManagementService(ctx, factory);
                string md = await mgmtService.ExportQuestionsMarkdownAsync(userId, subject: "数学", includeAnswers: false);

                Assert.NotNull(md);
                Assert.Contains("# 数学 标准试卷 · 学生自测空白版", md);
                Assert.Contains("不含参考答案与解析", md);
                Assert.Contains("第 1 题", md);
                Assert.Contains("- A. x = 0", md);
                Assert.Contains("**考生选择**: [　　]", md);
                // 确保不包含答案和解析
                Assert.DoesNotContain("**参考答案**", md);
                Assert.DoesNotContain("**名师深度解析**", md);
                Assert.DoesNotContain("偶函数对称轴为 y 轴", md);
                Assert.Contains("sin(π/6) 的值为", md);
                Assert.Contains("**考生作答**: ____________________", md);
            }
        }

        #endregion
    }
}
