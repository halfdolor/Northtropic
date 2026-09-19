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
    public class ArchitectAndUxGrandmasterApexTests
    {
        #region 1. 系统架构师与数学等价引擎：多项式因式分解因子乘积交换律无序等价匹配

        [Theory]
        [InlineData("(x+1)(x-1)", "(x-1)(x+1)")]
        [InlineData("(x-2)(x+3)", "(x+3)(x-2)")]
        [InlineData("2(x+3)(x-4)", "2(x-4)(x+3)")]
        [InlineData("-(x-1)(x-2)", "-(x-2)(x-1)")]
        [InlineData("(1+x)(x-2)", "(x-2)(x+1)")]
        [InlineData("(x+1)^2(x-3)", "(x-3)(x+1)^2")]
        [InlineData("x(x-2)", "(x-2)x")]
        [InlineData("-2x(x+1)(x-3)", "-2x(x-3)(x+1)")]
        [InlineData("(x+1)(x+2)(x+3)", "(x+3)(x+1)(x+2)")]
        [InlineData("(a+b)(a-b)", "(a-b)(a+b)")]
        public void CheckFillInBlankMatch_PolynomialFactorProduct_CommutativeMatches(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"因式分解因式乘积交换律应该等价匹配: 用户作答 '{userAns}' 与标准答案 '{correctAns}'");
        }

        [Theory]
        [InlineData("2(x-1)(x+2)", "3(x-1)(x+2)")] // 系数不同
        [InlineData("(x-1)(x+2)", "(x-1)(x+3)")]   // 因子不同
        [InlineData("(x-1)(x+2)", "(x-1)")]        // 因子个数不同
        [InlineData("x^2 - 1", "x^2 + 1")]         // 非因式乘积多项式不误判
        public void CheckFillInBlankMatch_PolynomialFactorProduct_MismatchedFails(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.False(isMatch, $"不匹配的因式分解应该返回 false: '{userAns}' vs '{correctAns}'");
        }

        #endregion

        #region 2. 系统架构师与数学等价引擎：对数真数与底数分式/根式深层计算评估

        [Theory]
        [InlineData("\\log_2 \\frac{1}{4}", "-2")]
        [InlineData("log(2, 1/4)", "-2")]
        [InlineData("\\log_2(1/2)", "-1")]
        [InlineData("\\log_3 \\frac{1}{9}", "-2")]
        [InlineData("log(1/2, 4)", "-2")]
        [InlineData("\\log_2 \\sqrt{2}", "0.5")]
        [InlineData("log(2, sqrt(2))", "1/2")]
        public void CheckFillInBlankMatch_LogarithmFractionAndRadical_EvaluatesAccurately(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"对数真数与底数分式/根式应该等价求值: 用户作答 '{userAns}' 与标准答案 '{correctAns}'");
        }

        #endregion

        #region 3. 系统架构师与化学等价引擎：化学可逆反应箭头与复杂反应条件识别

        [Theory]
        [InlineData("N2 + 3H2 ⇌ 2NH3", "3H2 + N2 ⇌ 2NH3")]
        [InlineData("N_2 + 3H_2 \\rightleftharpoons 2NH_3", "3H_2 + N_2 \\rightleftharpoons 2NH_3")]
        [InlineData("H2O <=> H+ + OH-", "H2O ⇌ H+ + OH-")]
        [InlineData("2SO2 + O2 \\stackrel{催化剂}{=} 2SO3", "2SO2 + O2 = 2SO3")]
        [InlineData("2SO2 + O2 \\xlongequal{加热} 2SO3", "O2 + 2SO2 = 2SO3")]
        public void CheckFillInBlankMatch_ChemicalReversibleAndCondition_MatchesEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"化学可逆反应与条件应该等价匹配: 用户作答 '{userAns}' 与标准答案 '{correctAns}'");
        }

        #endregion

        #region 4. 系统架构师与物理数学引擎：向量范数与模长 LaTeX 双竖线容错

        [Theory]
        [InlineData("\\|\\vec{a}\\| = 2", "|a| = 2")]
        [InlineData("\\Vert \\vec{a} \\Vert = 3", "|a| = 3")]
        [InlineData("\\|\\vec{a}\\| = 2", "2")]
        [InlineData("\\|a\\| = \\sqrt{2}", "sqrt(2)")]
        public void CheckFillInBlankMatch_VectorNormModulus_ToleratesDoubleBars(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"向量范数与模长双竖线记号应该等价: 用户作答 '{userAns}' 与标准答案 '{correctAns}'");
        }

        #endregion

        #region 5. 用户体验专家维度：智能等价原因生成与透明解释

        [Fact]
        public void GenerateEquivalentMatchReason_PolynomialFactorization_OutputsClearExplanation()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "(x-1)(x+1)" };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, "(x+1)(x-1)");
            Assert.Contains("因式分解因子交换律等价", reason);
            Assert.Contains("(x-1)(x+1)", reason);
        }

        [Fact]
        public void GenerateEquivalentMatchReason_ChemicalReversible_OutputsClearExplanation()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "3H2 + N2 ⇌ 2NH3" };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, "N2 + 3H2 ⇌ 2NH3");
            Assert.Contains("化学反应方程式/可逆平衡等价", reason);
        }

        [Fact]
        public void GenerateEquivalentMatchReason_VectorNorm_OutputsClearExplanation()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "|a| = 2" };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, "\\|\\vec{a}\\| = 2");
            Assert.Contains("向量范数与模长记号等价", reason);
        }

        #endregion

        #region 6. 系统架构师维度：批量整卷提交 ChangeTracker 内存优化与学习计划自适应推进

        [Fact]
        public async Task SubmitBatchPaperAsync_TracksEntitiesInLocalAndCompletesStudyPlan()
        {
            var (ctx, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (ctx)
            {
                var userId = Guid.NewGuid();
                var user = new User
                {
                    Id = userId,
                    Username = "ApexArchitectStudent",
                    Role = UserRole.Student,
                    ResolvedErrorsCount = 0
                };
                ctx.Users.Add(user);

                // 创建包含 2 道任务指标的学习计划
                var planId = Guid.NewGuid();
                var studyPlan = new StudyPlan
                {
                    Id = planId,
                    UserId = userId,
                    Status = StudyPlanStatus.Active,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Tasks = new List<StudyPlanTask>
                    {
                        new StudyPlanTask
                        {
                            Id = Guid.NewGuid(),
                            StudyPlanId = planId,
                            Title = "数学代数专项因式分解",
                            Subject = "数学",
                            Category = "代数综合",
                            TargetCount = 2,
                            CompletedCount = 0,
                            IsCompleted = false
                        }
                    }
                };
                ctx.StudyPlans.Add(studyPlan);

                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "因式分解：x^2 - 1",
                    CorrectAnswer = "(x-1)(x+1)",
                    Type = QuestionType.FillInBlank,
                    Subject = "数学",
                    Category = "代数综合",
                    Difficulty = 2,
                    BaseExpReward = 20,
                    CreatedByUserId = userId
                };
                var q2 = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "因式分解：x^2 - 4",
                    CorrectAnswer = "(x-2)(x+2)",
                    Type = QuestionType.FillInBlank,
                    Subject = "数学",
                    Category = "代数综合",
                    Difficulty = 2,
                    BaseExpReward = 20,
                    CreatedByUserId = userId
                };
                ctx.Questions.AddRange(q1, q2);
                await ctx.SaveChangesAsync();

                var practiceService = new PracticeService(
                    ctx,
                    new GamificationService(ctx, new FakeUserSessionService { ActiveUser = user }),
                    new FakeUserSessionService { ActiveUser = user },
                    new FakeAiTutorService());

                // 提交整卷作答：两道题均采用因子交换律书写
                var submissions = new List<(Question Question, string UserAnswer, int TimeTakenSeconds)>
                {
                    (q1, "(x+1)(x-1)", 15),
                    (q2, "(x+2)(x-2)", 12)
                };

                var batchResult = await practiceService.SubmitBatchPaperAsync(submissions, initialCombo: 0, targetUserId: userId);

                Assert.NotNull(batchResult);
                Assert.Equal(2, batchResult.TotalQuestions);
                Assert.Equal(2, batchResult.CorrectCount);
                Assert.Equal(2, batchResult.MaxComboAchieved);
                Assert.True(batchResult.ItemResults[1].IsEquivalentMatch);
                Assert.True(batchResult.ItemResults[2].IsEquivalentMatch);

                // 验证认知科学分析指标
                Assert.True(batchResult.AverageTimePerQuestionSeconds > 0);

                // 验证数据库中学习计划状态已自动闭环完成
                var updatedPlan = await ctx.StudyPlans.Include(p => p.Tasks).FirstOrDefaultAsync(p => p.Id == planId);
                Assert.NotNull(updatedPlan);
                Assert.Equal(StudyPlanStatus.Completed, updatedPlan.Status);
                Assert.All(updatedPlan.Tasks, t => Assert.True(t.IsCompleted));
            }
        }

        #endregion
    }
}
