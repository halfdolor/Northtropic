using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxUnifiedZenithTests
    {
        #region 1. 系统架构师：试题标准答案前缀清洗与选项字母提取单元测试
        [Theory]
        [InlineData("【答案】A", "A")]
        [InlineData("【参考答案】B", "B")]
        [InlineData("【标准答案】C", "C")]
        [InlineData("【正确答案】D", "D")]
        [InlineData("答案：42", "42")]
        [InlineData("答案: 100", "100")]
        [InlineData("答案是：甲醇", "甲醇")]
        [InlineData("答：光合作用", "光合作用")]
        [InlineData("解：x = 5", "x = 5")]
        [InlineData("【答案】答案：选：A", "A")]
        public void SystemHealthService_CleanAnswerPrefixes_ShouldStripRedundantPrefixes(string input, string expected)
        {
            var cleaned = SystemHealthService.CleanAnswerPrefixes(input);
            Assert.Equal(expected, cleaned);
        }

        [Theory]
        [InlineData("选A", "A")]
        [InlineData("选 B", "B")]
        [InlineData("C项", "C")]
        [InlineData("D选项", "D")]
        [InlineData("a", "A")]
        public void SystemHealthService_ExtractChoiceLetter_ShouldExtractCorrectLetter(string input, string expected)
        {
            var letter = SystemHealthService.ExtractChoiceLetter(input);
            Assert.Equal(expected, letter);
        }

        [Theory]
        [InlineData("选AB", "AB")]
        [InlineData("选 A, B, C", "ABC")]
        [InlineData("BCD选项", "BCD")]
        [InlineData("B、A", "AB")]
        [InlineData("C, A, B", "ABC")]
        public void SystemHealthService_ExtractMultipleChoiceLetters_ShouldSortAndDeduplicate(string input, string expected)
        {
            var letters = SystemHealthService.ExtractMultipleChoiceLetters(input);
            Assert.Equal(expected, letters);
        }
        #endregion

        #region 2. 系统架构师：题库破损试题自愈恢复答案不变量测试
        [Fact]
        public async Task SystemHealthService_HealCorruptedQuestions_CleansPrefixesAndRestoresInvariants()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var healthService = new SystemHealthService(context, null);

                var q1 = new Question
                {
                    Subject = "物理",
                    Category = "力学",
                    Stem = "关于牛顿第一定律，下列说法正确的是？",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A. 一定成立\",\"B. 惯性定律\",\"C. 加速度为零\",\"D. 受平衡力\"]",
                    CorrectAnswer = "【答案】选B",
                    StandardAnalysis = "【解析】牛顿第一定律又称惯性定律。"
                };

                var q2 = new Question
                {
                    Subject = "化学",
                    Category = "多选测试",
                    Stem = "下列属于电解质的是？",
                    Type = QuestionType.MultipleChoice,
                    OptionsJson = "[\"A. NaCl\",\"B. H2SO4\",\"C. 蔗糖\",\"D. 铜\"]",
                    CorrectAnswer = "答案：选B、A",
                    StandardAnalysis = "氯化钠和硫酸均为电解质。"
                };

                var q3 = new Question
                {
                    Subject = "生物",
                    Category = "光合作用",
                    Stem = "植物光合作用释放的气体是？",
                    Type = QuestionType.FillInBlank,
                    OptionsJson = "[]",
                    CorrectAnswer = "【参考答案】“氧气”",
                    StandardAnalysis = "水光解产生氧气。"
                };

                context.Questions.AddRange(q1, q2, q3);
                await context.SaveChangesAsync();

                var healed = await healthService.HealCorruptedQuestionsAsync();
                Assert.True(healed >= 3, $"应至少成功自愈3道题目，实际自愈数: {healed}");

                var updatedQ1 = await context.Questions.FindAsync(q1.Id);
                var updatedQ2 = await context.Questions.FindAsync(q2.Id);
                var updatedQ3 = await context.Questions.FindAsync(q3.Id);

                Assert.Equal("B", updatedQ1!.CorrectAnswer);
                Assert.Equal("AB", updatedQ2!.CorrectAnswer);
                Assert.Equal("氧气", updatedQ3!.CorrectAnswer);
            }
        }
        #endregion

        #region 3. 架构与领域推理：物理核心定律变式与代数等价测试
        [Theory]
        // 匀速直线运动速度公式 v = s/t <=> s = vt <=> t = s/v
        [InlineData("v = s / t", "s = v * t", true)]
        [InlineData("t = s / v", "v = s / t", true)]
        [InlineData("s = vt", "v = s / t", true)]
        [InlineData("v = x / t", "x = v * t", true)]
        // 密度公式 ρ = m/V <=> m = ρV <=> V = m/ρ
        [InlineData("ρ = m / V", "m = ρ * V", true)]
        [InlineData("V = m / ρ", "ρ = m / V", true)]
        [InlineData("\\rho = \\frac{m}{V}", "m = \\rho V", true)]
        // 固体与液体压强公式
        [InlineData("p = F / S", "F = p * S", true)]
        [InlineData("S = F / p", "p = F / S", true)]
        [InlineData("p = \\rho g h", "p = \\rho * g * h", true)]
        // 机械功率公式
        [InlineData("P = W / t", "W = P * t", true)]
        [InlineData("t = W / P", "P = W / t", true)]
        // 弹簧胡克定律
        [InlineData("F = k * x", "k = F / x", true)]
        [InlineData("x = F / k", "F = kx", true)]
        // 动量公式
        [InlineData("p = m * v", "m * v = p", true)]
        [InlineData("v = p / m", "p = mv", true)]
        // 并联总电阻
        [InlineData("1/R = 1/R1 + 1/R2", "1/R = 1/R2 + 1/R1", true)]
        public void PracticeService_CheckPhysicsFormulaEquivalence_ExpandedLaws_ShouldMatch(string user, string correct, bool expected)
        {
            var match = PracticeService.CheckPhysicsFormulaEquivalence(user, correct);
            Assert.Equal(expected, match);
        }
        #endregion

        #region 4. 架构与领域推理：多空填空无序集合等价匹配测试
        [Theory]
        [InlineData("氧气；氢气", "氢气；氧气", true)]
        [InlineData("氧气、氢气", "氢气、氧气", true)]
        [InlineData("氢气、氧气", "氧气；氢气", true)]
        [InlineData("甲；乙；丙", "丙；甲；乙", true)]
        [InlineData("36 km/h；1000 kg/m³", "10 m/s；1 g/cm³", true)]
        [InlineData("x = 1, y = 2", "y = 2, x = 1", true)]
        [InlineData("单质；化合物", "混合物；化合物", false)]
        [InlineData("氧气", "氢气", false)]
        public void PracticeService_CheckMultiItemSetEquivalence_ShouldHandleOrderIndependence(string user, string correct, bool expected)
        {
            var match = PracticeService.CheckMultiItemSetEquivalence(user, correct);
            Assert.Equal(expected, match);
        }
        #endregion

        #region 5. 答题引擎端到端集成：SubmitAnswerAsync 等价判定与文案反馈
        [Fact]
        public async Task PracticeService_SubmitAnswerAsync_MultiItemAndPhysics_ShouldReturnEquivalentReason()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var multiItemQ = new Question
                {
                    Subject = "化学",
                    Category = "电解水",
                    Stem = "电解水实验在阳极和阴极分别产生的气体是？",
                    Type = QuestionType.FillInBlank,
                    OptionsJson = "[]",
                    CorrectAnswer = "氧气；氢气",
                    StandardAnalysis = "阳极氧气，阴极氢气。"
                };

                var physicsQ = new Question
                {
                    Subject = "物理",
                    Category = "力学",
                    Stem = "请写出匀速直线运动速度计算公式？",
                    Type = QuestionType.FillInBlank,
                    OptionsJson = "[]",
                    CorrectAnswer = "v = s / t",
                    StandardAnalysis = "速度等于路程除以时间。"
                };

                // 测试多空无序组合匹配
                var resMulti = await practiceService.SubmitAnswerAsync(multiItemQ, "氢气、氧气", timeTakenSeconds: 15, currentCombo: 0);
                Assert.True(resMulti.IsCorrect);
                Assert.True(resMulti.IsEquivalentMatch);
                Assert.Contains("多空填空无序组合等价", resMulti.EquivalentMatchReason);

                // 测试物理公式变式匹配
                var resPhysics = await practiceService.SubmitAnswerAsync(physicsQ, "s = v * t", timeTakenSeconds: 12, currentCombo: 0);
                Assert.True(resPhysics.IsCorrect);
                Assert.True(resPhysics.IsEquivalentMatch);
                Assert.Contains("物理核心定律", resPhysics.EquivalentMatchReason);
            }
        }
        #endregion
    }
}
