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
    public class ArchitectAndUxPinnacleMasteryTests
    {
        [Fact]
        public void CheckFillInBlankMatch_LeadingDecimalPointOmission_MatchesCorrectly()
        {
            // 1. 省略前导零的正负小数与标准浮点数
            Assert.True(PracticeService.CheckFillInBlankMatch(".5", "0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0.5", ".5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("-.75", "-0.75"));
            Assert.True(PracticeService.CheckFillInBlankMatch("-0.75", "-.75"));
            Assert.True(PracticeService.CheckFillInBlankMatch("+.25", "0.25"));

            // 2. 省略前导零的分数形式与等式
            Assert.True(PracticeService.CheckFillInBlankMatch(".5", @"\frac{1}{2}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("-.75", @"-\frac{3}{4}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("x = .5", "x = 0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("y = -.2", "y = -0.2"));

            // 3. 根号内的前导零省略
            Assert.True(PracticeService.CheckFillInBlankMatch("sqrt(.25)", "0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("sqrt(0.25)", ".5"));

            // 4. 纯数值不符应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch(".5", "0.6"));
            Assert.False(PracticeService.CheckFillInBlankMatch("-.5", "0.5"));
        }

        [Fact]
        public void CheckFillInBlankMatch_IntervalUnionsWithDiverseConnectors_MatchesCorrectly()
        {
            // 1. 标准 LaTeX \cup 与大写 U、小写 u 互认
            Assert.True(PracticeService.CheckFillInBlankMatch(@"(-\infty, 1] \cup [3, +\infty)", "(-inf, 1] U [3, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-inf, 1] U [3, +inf)", @"(-\infty, 1] \cup [3, +\infty)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-inf, 1] u [3, +inf)", "(-inf, 1] U [3, +inf)"));

            // 2. 中文“并”、“并集”、“或”连词等价识别
            Assert.True(PracticeService.CheckFillInBlankMatch("(-inf, 1]并[3, +inf)", "(-inf, 1] U [3, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-inf, 1]并集[3, +inf)", "(-inf, 1] U [3, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-inf, 1]或[3, +inf)", "(-inf, 1] U [3, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(-inf, 1] 或者 [3, +inf)", "(-inf, 1] U [3, +inf)"));

            // 3. 区间并集交换律 (集合并集元素次序互换等价)
            Assert.True(PracticeService.CheckFillInBlankMatch("[3, +inf) U (-inf, 1]", "(-inf, 1] U [3, +inf)"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"[3, +\infty) \cup (-\infty, 1]", "(-inf, 1] U [3, +inf)"));

            // 4. 区间端点或开闭符号不匹配应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("(-inf, 1) U [3, +inf)", "(-inf, 1] U [3, +inf)"));
            Assert.False(PracticeService.CheckFillInBlankMatch("(-inf, 2] U [3, +inf)", "(-inf, 1] U [3, +inf)"));
        }

        [Fact]
        public void CheckFillInBlankMatch_FiniteSetPermutations_MatchesCorrectly()
        {
            // 1. 元素次序颠倒的有限集合等价识别
            Assert.True(PracticeService.CheckFillInBlankMatch("{1, 2}", "{2, 1}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("{2, 1}", "{1, 2}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("{-3, 0, 5}", "{5, -3, 0}"));
            Assert.True(PracticeService.CheckFillInBlankMatch("{a, b, c}", "{c, b, a}"));

            // 2. 集合内部元素存在代数/符号等价 (如 0.5 vs 1/2)
            Assert.True(PracticeService.CheckFillInBlankMatch("{0.5, 1}", @"{\frac{1}{2}, 1}"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"{\frac{1}{2}, 1}", "{1, 0.5}"));

            // 3. 元素不相同应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("{1, 2}", "{1, 3}"));
            Assert.False(PracticeService.CheckFillInBlankMatch("{1, 2, 3}", "{1, 2}"));
        }

        [Fact]
        public void CheckFillInBlankMatch_PowerOfTenAndScientificNotation_MatchesCorrectly()
        {
            // 1. 纯 10 次幂与 1*10^8、1.0*10^8、1e8
            Assert.True(PracticeService.CheckFillInBlankMatch("10^8", "1*10^8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10^8", "1.0*10^8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10^8", "1e8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1*10^8", "10^8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10^8", "100000000"));

            // 2. 中文幂次表达 (如 10的8次方、10的-3次方)
            Assert.True(PracticeService.CheckFillInBlankMatch("10的8次方", "10^8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10的8次方", "1*10^8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10的-3次方", "0.001"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3*10的8次方", "3*10^8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3*10的8次方", "3e8"));

            // 3. 省略前导零的科学记数法
            Assert.True(PracticeService.CheckFillInBlankMatch(".5*10^3", "500"));
            Assert.True(PracticeService.CheckFillInBlankMatch("500", ".5*10^3"));

            // 4. 次方不符应拒绝
            Assert.False(PracticeService.CheckFillInBlankMatch("10^8", "10^7"));
            Assert.False(PracticeService.CheckFillInBlankMatch("10的8次方", "10^9"));
        }

        [Fact]
        public void CheckAnswerCorrectness_ChoiceNumericAndFormattedKeys_MatchesAndGeneratesReason()
        {
            // 1. 单选数字键映射 1 -> A, 2 -> B
            var qSingle = new Question
            {
                Type = QuestionType.SingleChoice,
                CorrectAnswer = "A",
                OptionsJson = "[\"A. 牛顿第一定律\", \"B. 欧姆定律\", \"C. 焦耳定律\", \"D. 浮力定律\"]"
            };

            Assert.True(PracticeService.CheckAnswerCorrectness(qSingle, "1"));
            Assert.True(PracticeService.CheckAnswerCorrectness(qSingle, "(A)"));
            Assert.True(PracticeService.CheckAnswerCorrectness(qSingle, "A. 牛顿第一定律"));

            var singleReason = PracticeService.GenerateEquivalentMatchReason(qSingle, "1");
            Assert.Contains("选项映射等价", singleReason);
            Assert.Contains("A", singleReason);

            // 2. 多选连写与符号组合 124 -> ABD, A、B、D -> ABD
            var qMulti = new Question
            {
                Type = QuestionType.MultipleChoice,
                CorrectAnswer = "ABD"
            };

            Assert.True(PracticeService.CheckAnswerCorrectness(qMulti, "124"));
            Assert.True(PracticeService.CheckAnswerCorrectness(qMulti, "A、B、D"));
            Assert.True(PracticeService.CheckAnswerCorrectness(qMulti, "BDA"));

            var multiReason = PracticeService.GenerateEquivalentMatchReason(qMulti, "124");
            Assert.Contains("多选格式等价", multiReason);
            Assert.Contains("ABD", multiReason);
        }

        [Fact]
        public void GenerateEquivalentMatchReason_GeneratesDetailedReasonForDiverseTypes()
        {
            var qFill = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "1/2"
            };
            var reason1 = PracticeService.GenerateEquivalentMatchReason(qFill, "0.5");
            Assert.Contains("数值运算等价", reason1);

            var qSci = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "3.0*10^8"
            };
            var reason2 = PracticeService.GenerateEquivalentMatchReason(qSci, "3e8");
            Assert.Contains("科学记数法等价", reason2);

            var qSet = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "{1, 2}"
            };
            var reason3 = PracticeService.GenerateEquivalentMatchReason(qSet, "{2, 1}");
            Assert.Contains("有限集合元素等价", reason3);

            var qInterval = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "(-inf, 1] U [3, +inf)"
            };
            var reason4 = PracticeService.GenerateEquivalentMatchReason(qInterval, "(-inf, 1]并[3, +inf)");
            Assert.Contains("区间/集合并集等价", reason4);
        }

        [Fact]
        public async Task SubmitBatchPaperAsync_DefensiveAgainstNullItems_ExecutesSafely()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Type = QuestionType.SingleChoice,
                    Stem = "测试题1",
                    CorrectAnswer = "A",
                    BaseExpReward = 10,
                    Difficulty = 1
                };
                context.Questions.Add(q1);
                await context.SaveChangesAsync();

                // 构造包含 null Question 的边缘数据
                var submissions = new List<(Question Question, string UserAnswer, int TimeTakenSeconds)>
                {
                    (q1, "A", 15),
                    (null!, "B", 10) // 恶意或边缘脏数据项
                };

                var batchResult = await practiceService.SubmitBatchPaperAsync(submissions, 1);
                Assert.NotNull(batchResult);
                Assert.Equal(2, batchResult.TotalQuestions);
                Assert.Equal(1, batchResult.CorrectCount);
                Assert.True(batchResult.ItemResults.ContainsKey(1));
                Assert.True(batchResult.ItemResults[1].IsCorrect);
            }
        }

        [Fact]
        public void DbInitializer_LlmGenerationLogsDdl_HasNullableQuestionId()
        {
            // 架构级数据完整性约束：确保 DDL 中的 QuestionId 允许为 NULL，契合 Guid? 实体模型
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../Data/DbInitializer.cs");
            if (System.IO.File.Exists(path))
            {
                var code = System.IO.File.ReadAllText(path);
                Assert.True(code.Contains("\"\"QuestionId\"\" TEXT NULL") || code.Contains("\"QuestionId\" TEXT NULL"));
            }
        }
    }
}
