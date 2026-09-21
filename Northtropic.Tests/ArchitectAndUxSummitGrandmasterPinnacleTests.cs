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
    public class ArchitectAndUxSummitGrandmasterPinnacleTests
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

        [Fact]
        public void ExtractOptionsFromStem_WithDots_ExtractsAllOptionsAndCleansStem()
        {
            string stem = "下列关于重力的说法中，正确的是：A. 重力的方向总是垂直向下 B. 物体的重心一定在物体上 C. 质量越大的物体所受重力越大 D. 重力的大小可以用天平直接测量";
            
            var options = SystemHealthService.ExtractOptionsFromStem(stem, out var cleanedStem);

            Assert.Equal(4, options.Count);
            Assert.Equal("A. 重力的方向总是垂直向下", options[0]);
            Assert.Equal("B. 物体的重心一定在物体上", options[1]);
            Assert.Equal("C. 质量越大的物体所受重力越大", options[2]);
            Assert.Equal("D. 重力的大小可以用天平直接测量", options[3]);
            Assert.Equal("下列关于重力的说法中，正确的是：", cleanedStem);
        }

        [Fact]
        public void ExtractOptionsFromStem_WithParentheses_ExtractsCorrectly()
        {
            string stem = "选择正确的一项：(A) 氧化铜 (B) 氯化钠 (C) 硫酸铜 (D) 氢氧化钠";
            
            var options = SystemHealthService.ExtractOptionsFromStem(stem, out var cleanedStem);

            Assert.Equal(4, options.Count);
            Assert.Equal("A. 氧化铜", options[0]);
            Assert.Equal("B. 氯化钠", options[1]);
            Assert.Equal("C. 硫酸铜", options[2]);
            Assert.Equal("D. 氢氧化钠", options[3]);
            Assert.Equal("选择正确的一项：", cleanedStem);
        }

        [Fact]
        public void ExtractOptionsFromStem_WithChineseParentheses_ExtractsCorrectly()
        {
            string stem = "下列物质属于混合物的是：（A）空气（B）氧气（C）干冰（D）蒸馏水";
            
            var options = SystemHealthService.ExtractOptionsFromStem(stem, out var cleanedStem);

            Assert.Equal(4, options.Count);
            Assert.Equal("A. 空气", options[0]);
            Assert.Equal("B. 氧气", options[1]);
            Assert.Equal("C. 干冰", options[2]);
            Assert.Equal("D. 蒸馏水", options[3]);
            Assert.Equal("下列物质属于混合物的是：", cleanedStem);
        }

        [Fact]
        public void ExtractOptionsFromStem_NoOptions_ReturnsEmptyAndPreservesStem()
        {
            string stem = "计算抛体运动的最大射程与初速度的关系。";
            
            var options = SystemHealthService.ExtractOptionsFromStem(stem, out var cleanedStem);

            Assert.Empty(options);
            Assert.Equal(stem, cleanedStem);
        }

        [Theory]
        [InlineData("故选：A", "A")]
        [InlineData("故答案为：B", "B")]
        [InlineData("【答案解析】C", "C")]
        [InlineData("**A**", "A")]
        [InlineData("<b>D</b>", "D")]
        [InlineData("<i>C</i>", "C")]
        [InlineData("正确选项：A", "A")]
        [InlineData("本题选：B", "B")]
        [InlineData("【题目详解】D", "D")]
        [InlineData("【考点】选：A", "A")]
        public void CleanAnswerPrefixes_StripsPedagogicalPrefixesAndMarkup(string input, string expected)
        {
            var cleaned = SystemHealthService.CleanAnswerPrefixes(input);
            Assert.Equal(expected, cleaned);
        }

        [Theory]
        [InlineData("【A】", "A")]
        [InlineData("（B）", "B")]
        [InlineData("(C)", "C")]
        [InlineData("Ａ", "A")]
        [InlineData("Ｂ", "B")]
        [InlineData("选 A", "A")]
        [InlineData("1", "A")]
        [InlineData("2", "B")]
        [InlineData("3", "C")]
        [InlineData("4", "D")]
        public void ExtractChoiceLetter_ExtractsNormalizedChoiceLetter(string input, string expected)
        {
            var letter = SystemHealthService.ExtractChoiceLetter(input);
            Assert.Equal(expected, letter);
        }

        [Theory]
        [InlineData("A与C", "AC")]
        [InlineData("A、B、D", "ABD")]
        [InlineData("【A】【B】", "AB")]
        [InlineData("C, A, B", "ABC")]
        [InlineData("**A** 和 **D**", "AD")]
        [InlineData("选 A、C 项", "AC")]
        [InlineData("b, a", "AB")]
        public void ExtractMultipleChoiceLetters_CleansAndSortsLetters(string input, string expected)
        {
            var letters = SystemHealthService.ExtractMultipleChoiceLetters(input);
            Assert.Equal(expected, letters);
        }

        [Fact]
        public async Task HealCorruptedQuestionsAsync_RecoversOptionsFromStemDirectly()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                using var dummyContext = factory.CreateDbContext();
                var service = new SystemHealthService(dummyContext, factory);

                var q = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "下列属于初中物理电学元件的是：A. 电压表 B. 显微镜 C. 托盘天平 D. 量筒",
                    OptionsJson = "", // Corrupted
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "故选：A",
                    Subject = "物理",
                    CreatedAt = DateTime.UtcNow
                };

                using (var ctx = factory.CreateDbContext())
                {
                    ctx.Questions.Add(q);
                    await ctx.SaveChangesAsync();
                }

                int healed = await service.HealCorruptedQuestionsAsync();
                Assert.True(healed > 0);

                using (var ctx = factory.CreateDbContext())
                {
                    var healedQ = await ctx.Questions.FindAsync(q.Id);
                    Assert.NotNull(healedQ);
                    Assert.Contains("A. 电压表", healedQ.OptionsJson);
                    Assert.Contains("B. 显微镜", healedQ.OptionsJson);
                    Assert.Equal("下列属于初中物理电学元件的是：", healedQ.Stem);
                }
            }
        }

        [Theory]
        // 26. 动能定理
        [InlineData("W = \\Delta E_k", "W = E_{k2} - E_{k1}")]
        [InlineData("W_{合} = \\Delta E_k", "W = \\Delta E_k")]
        [InlineData("W = 0.5mv2^2 - 0.5mv1^2", "W = \\Delta E_k")]
        // 27. 机械能守恒
        [InlineData("Ep1 + Ek1 = Ep2 + Ek2", "\\Delta Ep + \\Delta Ek = 0")]
        [InlineData("Ek1 + Ep1 = Ek2 + Ep2", "Ep1 + Ek1 = Ep2 + Ek2")]
        // 28. 洛伦兹力
        [InlineData("F = qvB", "F = Bqv")]
        [InlineData("qvB = F", "F = qvB")]
        // 29. 安培力
        [InlineData("F = BIL", "F = ILB")]
        [InlineData("BIL = F", "F = BIL")]
        // 30. 法拉第电磁感应定律
        [InlineData("E = BLv", "E = n \\frac{\\Delta \\Phi}{\\Delta t}")]
        [InlineData("BLv = E", "E = BLv")]
        // 31. 电容定义式
        [InlineData("C = Q/U", "Q = CU")]
        [InlineData("U = Q/C", "Q = CU")]
        // 32. 焦耳定律
        [InlineData("Q = I^2Rt", "Q = \\frac{U^2t}{R}")]
        [InlineData("Q = I^2Rt", "Q = UIt")]
        // 33. 固体压强
        [InlineData("p = F/S", "F = pS")]
        [InlineData("S = F/p", "p = F/S")]
        // 34. 杠杆平衡
        [InlineData("F_1 L_1 = F_2 L_2", "F_2 L_2 = F_1 L_1")]
        [InlineData("F1/F2 = L2/L1", "F1L1 = F2L2")]
        // 35. 机械效率
        [InlineData("\\eta = \\frac{W_{有}}{W_{总}}", "\\eta = W_{有用} / W_{总}")]
        [InlineData("W_{有} / W_{总} = \\eta", "\\eta = \\frac{W_{有}}{W_{总}}")]
        public void CheckPhysicsFormulaEquivalence_NewGroups_RecognizesEquivalence(string userFormula, string correctFormula)
        {
            bool isEquiv = PracticeService.CheckPhysicsFormulaEquivalence(userFormula, correctFormula);
            Assert.True(isEquiv, $"Expected '{userFormula}' to be equivalent to '{correctFormula}'");
        }

        [Fact]
        public void CheckFillInBlankMatch_WithSmartApostrophesAndQuotes_MatchesCorrectly()
        {
            // 智能弯单引号与缩略语
            Assert.True(PracticeService.CheckFillInBlankMatch("can’t", "cannot"));
            Assert.True(PracticeService.CheckFillInBlankMatch("it’s", "it is"));
            Assert.True(PracticeService.CheckFillInBlankMatch("let’s", "let us"));
            Assert.True(PracticeService.CheckFillInBlankMatch("haven’t", "have not"));
            Assert.True(PracticeService.CheckFillInBlankMatch("needn’t", "need not"));
            Assert.True(PracticeService.CheckFillInBlankMatch("can not", "cannot"));

            // 中文双引号与破折号、省略号
            Assert.True(PracticeService.CheckFillInBlankMatch("“师说”", "\"师说\""));
            Assert.True(PracticeService.CheckFillInBlankMatch("春蚕到死丝方尽……", "春蚕到死丝方尽..."));
            Assert.True(PracticeService.CheckFillInBlankMatch("这是一条线索——希望", "这是一条线索--希望"));
        }
    }
}
