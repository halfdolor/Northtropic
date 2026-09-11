using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxSummitEvolutionTests
    {
        [Fact]
        public void CheckFillInBlankMatch_DfracAndTfrac_NormalizedAccurately()
        {
            // 1. 中高考极度普及的 amsmath 宏包公式 \dfrac 与 \tfrac 归一化
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\dfrac{1}{2}", "1/2"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\dfrac{1}{2}", "0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0.5", @"\dfrac{1}{2}"));

            Assert.True(PracticeService.CheckFillInBlankMatch(@"\tfrac{3}{4}", "3/4"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\tfrac{3}{4}", "0.75"));

            Assert.True(PracticeService.CheckFillInBlankMatch(@"\dfrac{x+1}{2}", "(x+1)/2"));
        }

        [Fact]
        public void CheckFillInBlankMatch_EscapedLaTeXPercentage_MatchesAccurately()
        {
            // 2. 标准 LaTeX 中转义百分号 \% 与常规百分比、小数等价匹配
            Assert.True(PracticeService.CheckFillInBlankMatch(@"25\%", "25%"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"25\%", "0.25"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0.25", @"25\%"));
            Assert.True(PracticeService.CheckFillInBlankMatch("25%", @"25\%"));

            Assert.True(PracticeService.CheckFillInBlankMatch(@"0.5\%", "0.005"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"80 \%", "80%"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"80 \%", "0.8"));
        }

        [Fact]
        public void CheckFillInBlankMatch_DelimitersAndIntervals_MatchesTolerantly()
        {
            // 3. LaTeX 定界符 \left[, \right], \left|, \right|, \left\{, \right\} 解构
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\left[ 1, 5 \right)", "[1, 5)"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\left[ 1, 5 \right]", "[1, 5]"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\left| x \right|", "|x|"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\left\{ 1, 2, 3 \right\}", "{1, 2, 3}"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\left( -\infty, 0 \right]", "(-inf, 0]"));
        }

        [Fact]
        public void CheckFillInBlankMatch_TrigAndLogFunctions_NormalizedAccurately()
        {
            // 4. 三角函数与对数函数前缀反斜杠剥离
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\sin x", "sin x"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\sin(x)", "sin x"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\cos\theta", "cos theta"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\tan\alpha", "tan alpha"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\ln 2", "ln 2"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\lg 10", "lg 10"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\log(x)", "log(x)"));
        }

        [Fact]
        public void CheckFillInBlankMatch_Vectors_NormalizedAccurately()
        {
            // 5. 向量与线段标记解构
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\vec{a}", "a"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\vec{AB}", "AB"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\overrightarrow{AB}", "AB"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\overline{AB}", "AB"));
        }

        [Fact]
        public void CheckFillInBlankMatch_ChemistryStatesAndArrows_MatchesTolerantly()
        {
            // 6. 化学物态标注与扩展反应箭头解构
            Assert.True(PracticeService.CheckFillInBlankMatch("2H2(g) + O2(g) = 2H2O(l)", "2H2 + O2 = 2H2O"));
            Assert.True(PracticeService.CheckFillInBlankMatch("2H2（气） + O2（气） = 2H2O（液）", "2H2 + O2 -> 2H2O"));
            Assert.True(PracticeService.CheckFillInBlankMatch("Ag+ + Cl- -> AgCl(s)", "Ag+ + Cl- = AgCl↓"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"2KClO3 \xrightarrow[\Delta]{MnO2} 2KCl + 3O2", "2KClO3 = 2KCl + 3O2↑"));
        }

        [Fact]
        public void CheckFillInBlankMatch_ExtendedRomanAndBracketBlanks_SplitsCorrectly()
        {
            // 7. 罗马数字与括号小题编号切分
            Assert.True(PracticeService.CheckFillInBlankMatch("(I) 动能 (II) 势能", "动能; 势能"));
            Assert.True(PracticeService.CheckFillInBlankMatch("(i) 3 (ii) 5", "3; 5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("[1] 3 [2] 5", "(1) 3 (2) 5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("【1】 红外线 【2】 紫外线", "红外线; 紫外线"));
            Assert.True(PracticeService.CheckFillInBlankMatch("⑪ 动能 ⑫ 势能", "① 动能 ② 势能"));
        }

        [Fact]
        public void CheckFillInBlankMatch_FullWidthMathOperators_NormalizedAccurately()
        {
            // 8. 中文全角数学运算符与符号
            Assert.True(PracticeService.CheckFillInBlankMatch("１＋２＝３", "1+2=3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("５０％", "0.5"));
            Assert.True(PracticeService.CheckFillInBlankMatch("（1，2）", "(1,2)"));
        }

        [Fact]
        public void AppDbContext_IndexesConfiguredForHighFrequencyTables()
        {
            // 9. 验证数据库高频查询复合索引配置
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var model = context.Model;

                // UserFavorite 索引验证
                var favEntity = model.FindEntityType(typeof(UserFavorite));
                Assert.NotNull(favEntity);
                var favIndexes = favEntity.GetIndexes().ToList();
                Assert.Contains(favIndexes, idx => idx.Properties.Select(p => p.Name).SequenceEqual(new[] { "UserId", "QuestionId" }));
                Assert.Contains(favIndexes, idx => idx.Properties.Select(p => p.Name).SequenceEqual(new[] { "UserId", "CreatedAt" }));

                // LlmGenerationLog 索引验证
                var logEntity = model.FindEntityType(typeof(LlmGenerationLog));
                Assert.NotNull(logEntity);
                var logIndexes = logEntity.GetIndexes().ToList();
                Assert.Contains(logIndexes, idx => idx.Properties.Select(p => p.Name).SequenceEqual(new[] { "UserId", "GeneratedAt" }));

                // UserAchievement 索引验证
                var achEntity = model.FindEntityType(typeof(UserAchievement));
                Assert.NotNull(achEntity);
                var achIndexes = achEntity.GetIndexes().ToList();
                Assert.Contains(achIndexes, idx => idx.Properties.Select(p => p.Name).SequenceEqual(new[] { "UserId", "AchievementId" }));
            }
        }

        [Fact]
        public async Task AiTutorService_FallbackSubjectiveGrading_ProvidesRubricAndAdvice()
        {
            // 10. 验证主观题与填空题智能降级打分，包含多维 Rubric 与激励建议
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var sessionMock = new FakeUserSessionService();
                var gamification = new GamificationService(context, sessionMock);
                using var httpClient = new HttpClient();
                var httpFactory = new MockHttpClientFactory(httpClient);
                var aiTutor = new AiTutorService(gamification, httpFactory, context);

                // A. 填空题命中 (复用 PracticeService 判题容错)
                var fillQ = new Question
                {
                    Id = Guid.NewGuid(),
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = @"\dfrac{1}{2}",
                    Subject = "数学",
                    Category = "分式"
                };
                var fillResult = await aiTutor.GradeSubjectiveAnswerAsync(fillQ, "0.5");
                Assert.True(fillResult.IsPassed);
                Assert.True(fillResult.Score >= 80);
                Assert.NotEmpty(fillResult.RubricBreakdown);
                Assert.False(string.IsNullOrWhiteSpace(fillResult.EncouragementAdvice));

                // B. 主观题充分作答
                var essayQ = new Question
                {
                    Id = Guid.NewGuid(),
                    Type = QuestionType.ShortAnswer,
                    CorrectAnswer = "由能量守恒定律，动能与势能相互转化，机械能保持守恒。",
                    Subject = "物理",
                    Category = "机械能守恒"
                };
                var essayResult = await aiTutor.GradeSubjectiveAnswerAsync(essayQ, "根据机械能守恒定律，在只有重力做功的情形下，物体的动能与势能发生相互转化，总机械能保持不变。");
                Assert.True(essayResult.IsPassed);
                Assert.True(essayResult.Score >= 80);
                Assert.NotEmpty(essayResult.RubricBreakdown);
                Assert.Contains(essayResult.RubricBreakdown, r => r.Contains("核心论点"));
                Assert.False(string.IsNullOrWhiteSpace(essayResult.EncouragementAdvice));

                // C. 主观题过短未达标
                var shortResult = await aiTutor.GradeSubjectiveAnswerAsync(essayQ, "不清楚");
                Assert.False(shortResult.IsPassed);
                Assert.True(shortResult.Score < 60);
                Assert.NotEmpty(shortResult.RubricBreakdown);
                Assert.False(string.IsNullOrWhiteSpace(shortResult.EncouragementAdvice));
            }
        }
    }
}
