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
    public class ArchitectAndUxSummitApexPinnacleEvolutionTests
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

        #region STEM Equivalence Groups 36 - 45 Tests

        [Theory]
        // 36. 动量守恒定律
        [InlineData("m_1 v_1 + m_2 v_2 = m_1 v_1' + m_2 v_2'", "m_1 v_1' + m_2 v_2' = m_1 v_1 + m_2 v_2")]
        [InlineData("m1v1 + m2v2 = (m1 + m2)v", "m_1 v_1 + m_2 v_2 = (m_1 + m_2) v")]
        [InlineData("p_1 + p_2 = p_1' + p_2'", "p_1' + p_2' = p_1 + p_2")]
        [InlineData("\\Delta p = 0", "p = p'")]
        // 37. 闭合电路欧姆定律
        [InlineData("I = \\frac{E}{R + r}", "E = I(R + r)")]
        [InlineData("E = U + Ir", "U = E - Ir")]
        [InlineData("I = E/(R+r)", "E = IR + Ir")]
        [InlineData("U = E - Ir", "E - Ir = U")]
        // 38. 热力学第一定律
        [InlineData("\\Delta U = W + Q", "\\Delta U = Q + W")]
        [InlineData("W + Q = \\Delta U", "\\Delta U = W + Q")]
        [InlineData("W = \\Delta U - Q", "Q = \\Delta U - W")]
        [InlineData("\\Delta U - W = Q", "W = \\Delta U - Q")]
        // 39. 爱因斯坦光电效应方程
        [InlineData("E_k = h\\nu - W_0", "h\\nu = E_k + W_0")]
        [InlineData("h\\nu = W_0 + E_k", "E_k = h\\nu - W_0")]
        [InlineData("E_k = h\\nu - W", "h\\nu = E_k + W")]
        [InlineData("\\Delta E = h\\nu", "h\\nu = E_2 - E_1")]
        // 40. 光的折射定律 / 斯涅尔定律
        [InlineData("n = \\frac{\\sin i}{\\sin r}", "\\sin i / \\sin r = n")]
        [InlineData("n_1 \\sin\\theta_1 = n_2 \\sin\\theta_2", "n_2 \\sin\\theta_2 = n_1 \\sin\\theta_1")]
        [InlineData("n = c/v", "c/v = n")]
        [InlineData("v = c/n", "n = c/v")]
        // 41. 开普勒第三定律
        [InlineData("a^3 / T^2 = k", "r^3 / T^2 = k")]
        [InlineData("r_1^3 / T_1^2 = r_2^3 / T_2^2", "r_2^3 / T_2^2 = r_1^3 / T_1^2")]
        [InlineData("a^3 / T^2 = k", "k = a^3 / T^2")]
        [InlineData("T^2 / r^3 = k", "r^3 / T^2 = k")]
        // 42. 爱因斯坦质能方程
        [InlineData("E = mc^2", "mc^2 = E")]
        [InlineData("E = mc²", "E = mc^2")]
        [InlineData("\\Delta E = \\Delta mc^2", "\\Delta mc^2 = \\Delta E")]
        [InlineData("\\Delta E = \\Delta m \\cdot c^2", "\\Delta E = \\Delta mc^2")]
        // 43. 理想变压器变压变流比
        [InlineData("U_1 / U_2 = n_1 / n_2", "n_1 / n_2 = U_1 / U_2")]
        [InlineData("I_1 / I_2 = n_2 / n_1", "n_2 / n_1 = I_1 / I_2")]
        [InlineData("U_1 I_1 = U_2 I_2", "P_1 = P_2")]
        [InlineData("P_{入} = P_{出}", "P_1 = P_2")]
        // 44. 物质的量与气体摩尔体积
        [InlineData("n = V / V_m", "V = n V_m")]
        [InlineData("n = m / M", "m = nM")]
        [InlineData("c = n / V", "n = cV")]
        // 45. 物质的量浓度与质量分数换算
        [InlineData("c = \\frac{1000\\rho w}{M}", "c = 1000\\rho w / M")]
        [InlineData("w = cM / (1000\\rho)", "c = 1000\\rho w / M")]
        public void CheckPhysicsFormulaEquivalence_Groups36To45_RecognizesEquivalence(string formulaA, string formulaB)
        {
            bool isEquiv = PracticeService.CheckPhysicsFormulaEquivalence(formulaA, formulaB);
            Assert.True(isEquiv, $"Expected '{formulaA}' to be equivalent to '{formulaB}'");
        }

        #endregion

        #region OCR Fullwidth Dot and Multi-option Parsing Tests

        [Fact]
        public void ExtractOptionsFromStem_WithOcrFullWidthPeriods_ExtractsCorrectly()
        {
            // OCR 常见全角点号 (．, \uFF0E)
            string stem = "关于牛顿第一定律，下列叙述正确的是：A．牛顿第一定律是通过实验直接得出的 B．物体只有在不受力时才保持匀速运动 C．惯性是物体的固有属性 D．力是维持物体运动的原因";
            
            var options = SystemHealthService.ExtractOptionsFromStem(stem, out var cleanedStem);

            Assert.Equal(4, options.Count);
            Assert.Equal("A. 牛顿第一定律是通过实验直接得出的", options[0]);
            Assert.Equal("B. 物体只有在不受力时才保持匀速运动", options[1]);
            Assert.Equal("C. 惯性是物体的固有属性", options[2]);
            Assert.Equal("D. 力是维持物体运动的原因", options[3]);
            Assert.Equal("关于牛顿第一定律，下列叙述正确的是：", cleanedStem);
        }

        [Fact]
        public void ExtractOptionsFromStem_WithFiveOptions_ExtractsUpToOptionE()
        {
            string stem = "选择符合条件的化学物质：A. 乙醇 B. 乙酸 C. 乙酸乙酯 D. 苯酚 E. 葡萄糖";
            
            var options = SystemHealthService.ExtractOptionsFromStem(stem, out var cleanedStem);

            Assert.Equal(5, options.Count);
            Assert.Equal("A. 乙醇", options[0]);
            Assert.Equal("B. 乙酸", options[1]);
            Assert.Equal("C. 乙酸乙酯", options[2]);
            Assert.Equal("D. 苯酚", options[3]);
            Assert.Equal("E. 葡萄糖", options[4]);
            Assert.Equal("选择符合条件的化学物质：", cleanedStem);
        }

        [Fact]
        public void ExtractOptionsFromStem_WithSixOptions_ExtractsUpToOptionF()
        {
            string stem = "下列各生物体中属于真核生物的是：A. 酵母菌 B. 大肠杆菌 C. 蓝细菌 D. 草履虫 E. 衣藻 F. 噬菌体";
            
            var options = SystemHealthService.ExtractOptionsFromStem(stem, out var cleanedStem);

            Assert.Equal(6, options.Count);
            Assert.Equal("A. 酵母菌", options[0]);
            Assert.Equal("B. 大肠杆菌", options[1]);
            Assert.Equal("C. 蓝细菌", options[2]);
            Assert.Equal("D. 草履虫", options[3]);
            Assert.Equal("E. 衣藻", options[4]);
            Assert.Equal("F. 噬菌体", options[5]);
            Assert.Equal("下列各生物体中属于真核生物的是：", cleanedStem);
        }

        #endregion

        #region Answer Prefix Deep Cleaning Tests

        [Theory]
        [InlineData("【参考解法】A", "A")]
        [InlineData("【解题思路】B", "B")]
        [InlineData("【参考结论】C", "C")]
        [InlineData("【正确选项】D", "D")]
        [InlineData("【本题解答】A", "A")]
        [InlineData("综上所述，故选：B", "B")]
        [InlineData("综上所述：C", "C")]
        [InlineData("由上可知，选：D", "D")]
        [InlineData("由上可知，选 A", "A")]
        public void CleanAnswerPrefixes_ExtendedPedagogicalPrefixes_CleanedProperly(string input, string expected)
        {
            string cleaned = SystemHealthService.CleanAnswerPrefixes(input);
            Assert.Equal(expected, cleaned);
        }

        [Theory]
        [InlineData("选 E 项", "E")]
        [InlineData("【F】", "F")]
        [InlineData("（G）", "G")]
        [InlineData("选 H", "H")]
        public void ExtractChoiceLetter_SupportsExtendedLetters_E_Through_H(string input, string expected)
        {
            string letter = SystemHealthService.ExtractChoiceLetter(input);
            Assert.Equal(expected, letter);
        }

        #endregion

        #region Invariant Self-Healing End-to-End Test

        [Fact]
        public async Task HealAllInvariantsAsync_ExecutesCleanlyAndReportsSuccess()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                using var dummyContext = factory.CreateDbContext();
                var service = new SystemHealthService(dummyContext, factory);

                // 插入待自愈试题与错题
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "ZenithStudent",
                    Role = UserRole.Student,
                    Grade = "高三"
                };

                var q = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "动量守恒的条件是：A．系统合外力为零 B．系统内力为零 C．两物体质量相等 D．碰撞为弹性碰撞",
                    OptionsJson = "",
                    CorrectAnswer = "【参考解法】A",
                    Subject = "物理",
                    Type = QuestionType.SingleChoice,
                    CreatedAt = DateTime.UtcNow
                };

                var err = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = q.Id,
                    RevisionCount = -1, // 异常不变量
                    ErrorReasonCategory = "", // 空分类
                    CreatedAt = DateTime.UtcNow
                };

                using (var ctx = factory.CreateDbContext())
                {
                    ctx.Users.Add(user);
                    ctx.Questions.Add(q);
                    ctx.ErrorItems.Add(err);
                    await ctx.SaveChangesAsync();
                }

                // 执行全量不变量自愈编排
                var result = await service.HealAllInvariantsAsync();

                Assert.True(result.Success);
                Assert.True(result.TotalHealedCount > 0);

                // 验证试题与错题已被自愈
                using (var ctx = factory.CreateDbContext())
                {
                    var healedQ = await ctx.Questions.FindAsync(q.Id);
                    Assert.NotNull(healedQ);
                    Assert.Contains("A. 系统合外力为零", healedQ.OptionsJson);
                    Assert.Equal("A", healedQ.CorrectAnswer);

                    var healedErr = await ctx.ErrorItems.FindAsync(err.Id);
                    Assert.NotNull(healedErr);
                    Assert.True(healedErr.RevisionCount >= 0);
                    Assert.Equal("未分类", healedErr.ErrorReasonCategory);
                }
            }
        }

        #endregion
    }
}
