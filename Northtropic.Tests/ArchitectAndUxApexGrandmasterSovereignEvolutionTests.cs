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
    public class ArchitectAndUxApexGrandmasterSovereignEvolutionTests
    {
        [Fact]
        public void DataIntegrityAuditDto_IsStrictlyHealthy_And_ComprehensiveAnomalies_Contracts()
        {
            var audit = new DataIntegrityAuditDto
            {
                TotalOrphanErrorItems = 0,
                TotalOrphanPracticeRecords = 0,
                TotalOrphanUserFavorites = 0,
                TotalOrphanHomeworkAssignments = 0,
                TotalOrphanBindings = 0,
                TotalOrphanStudyPlans = 0,
                TotalOrphanStudyPlanTasks = 0,
                TotalOrphanUserAchievements = 0,
                TotalOrphanInsights = 0,
                TotalOrphanLlmLogs = 0,
                TotalCorruptedQuestions = 0,
                TotalOrphanPrivateQuestions = 0,
                TotalDanglingPublicQuestions = 0,
                TotalInvalidStudyPlans = 0,
                TotalInvalidBindings = 0,
                TotalInvalidHomeworkAssignments = 0,
                TotalInvalidPracticeRecords = 0,
                TotalInvalidErrorItems = 0,
                TotalDuplicateUserAchievements = 0,
                TotalInvalidLlmLogs = 0,
                TotalInvalidCurriculumConfigs = 0,
                TotalInvalidInsights = 0,
                TotalInvalidAchievements = 0,
                TotalInvalidStudyPlanTasks = 0,
                TotalInvalidUserAchievements = 0,
                TotalDuplicateQuestions = 0,
                TotalUsersWithInvalidBalances = 2,
                TotalDuplicateFavorites = 3
            };

            Assert.Equal(0, audit.TotalIssuesCount);
            Assert.True(audit.IsHealthy);
            Assert.Equal(5, audit.TotalComprehensiveAnomaliesCount);
            Assert.False(audit.IsStrictlyHealthy);

            audit.TotalUsersWithInvalidBalances = 0;
            audit.TotalDuplicateFavorites = 0;
            Assert.Equal(0, audit.TotalComprehensiveAnomaliesCount);
            Assert.True(audit.IsStrictlyHealthy);
        }

        [Theory]
        [InlineData("v = v0 + at", "v = at + v0", true)]
        [InlineData("v - v0 = at", "at = v - v0", true)]
        [InlineData("vt = v0 + at", "v = v0 + at", true)]
        [InlineData("s = v0t + 1/2at^2", "s = 0.5at^2 + v0t", true)]
        [InlineData("x = v0t + 0.5at^2", "s = v0t + 0.5at²", true)]
        [InlineData("v^2 - v0^2 = 2as", "v² = v0² + 2as", true)]
        [InlineData("2as = v² - v0²", "vt² - v0² = 2as", true)]
        [InlineData("F = ma", "ma = F", true)]
        [InlineData("a = F/m", "m = F/a", true)]
        [InlineData("Ek = 1/2mv^2", "ek = 0.5mv²", true)]
        [InlineData("Ep = mgh", "mgh = ep", true)]
        [InlineData("W = Fs", "fs = w", true)]
        [InlineData("U = IR", "I = U/R", true)]
        [InlineData("P = UI", "P = IU", true)]
        [InlineData("P = I^2R", "i²r = p", true)]
        [InlineData("1/f = 1/u + 1/v", "1/u + 1/v = 1/f", true)]
        [InlineData("F = ma", "F = mg", false)]
        public void CheckPhysicsFormulaEquivalence_EvaluatesAccurately(string a, string b, bool expected)
        {
            var result = PracticeService.CheckPhysicsFormulaEquivalence(a, b);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("C_n H_{2n+2}", "CnH2n+2", true)]
        [InlineData("CnH(2n+2)", "C_n H_{2n+2}", true)]
        [InlineData("C_n H_{2n}", "CnH2n", true)]
        [InlineData("C_n H_{2n-2}", "CnH2n-2", true)]
        [InlineData("C_n H_{2n-6}", "CnH2n-6", true)]
        [InlineData("C_n H_{2n+2}O", "C_n H_{2n+1}OH", true)]
        [InlineData("\\Delta H < 0", "ΔH < 0", true)]
        [InlineData("\\Delta H > 0", "ΔH > 0", true)]
        [InlineData("C_n H_{2n+2}", "C_n H_{2n}", false)]
        public void CheckOrganicGeneralFormulaEquivalence_EvaluatesAccurately(string a, string b, bool expected)
        {
            var result = PracticeService.CheckOrganicGeneralFormulaEquivalence(a, b);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("N(\\mu, \\sigma^2)", "N(u, \\sigma^2)", true)]
        [InlineData("N(μ, σ²)", "N(u, σ^2)", true)]
        [InlineData("N(0, 1)", "n(0, 1)", true)]
        [InlineData("E(X)", "EX", true)]
        [InlineData("E[X]", "E(ξ)", true)]
        [InlineData("D(X)", "Var(X)", true)]
        [InlineData("D[X]", "DX", true)]
        [InlineData("D(ξ)", "Var(ξ)", true)]
        [InlineData("E(X)", "D(X)", false)]
        public void CheckProbabilityStatisticsEquivalence_EvaluatesAccurately(string a, string b, bool expected)
        {
            var result = PracticeService.CheckProbabilityStatisticsEquivalence(a, b);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void PracticeService_CheckFillInBlankMatch_And_Explanations_WorkForStemEquivalences()
        {
            // 物理公式
            Assert.True(PracticeService.CheckFillInBlankMatch("v = at + v0", "v = v0 + at"));
            var qPhys = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "v = v0 + at" };
            var physExp = PracticeService.GenerateEquivalentMatchReason(qPhys, "v = at + v0");
            Assert.Contains("物理核心定律与公式等价", physExp);

            // 化学通式
            Assert.True(PracticeService.CheckFillInBlankMatch("CnH2n+2", "C_n H_{2n+2}"));
            var qChem = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "C_n H_{2n+2}" };
            var chemExp = PracticeService.GenerateEquivalentMatchReason(qChem, "CnH2n+2");
            Assert.Contains("有机化学通式等价", chemExp);

            // 统计记号
            Assert.True(PracticeService.CheckFillInBlankMatch("Var(X)", "D(X)"));
            var qProb = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "D(X)" };
            var probExp = PracticeService.GenerateEquivalentMatchReason(qProb, "Var(X)");
            Assert.Contains("概率统计记号等价", probExp);
        }

        [Fact]
        public async Task SystemHealthService_HealCurriculumSubjectConfigInvariants_DeduplicatesAndCleansTopics()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var cfg = new CurriculumSubjectConfig
                {
                    Id = Guid.NewGuid(),
                    Grade = "高一",
                    Subject = "物理",
                    TopicsJson = "[\"全部\", \"匀变速直线运动\", \"  匀变速直线运动  \", \"牛顿运动定律\", \"\", \"全部\"]",
                    UpdatedAt = DateTime.Now.AddDays(-2)
                };
                context.CurriculumSubjectConfigs.Add(cfg);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);
                var healedCount = await healthService.HealCurriculumSubjectConfigInvariantsAsync();

                Assert.True(healedCount > 0);
                var reloaded = await context.CurriculumSubjectConfigs.FirstAsync(c => c.Id == cfg.Id);
                var topics = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reloaded.TopicsJson);

                Assert.NotNull(topics);
                Assert.Equal(3, topics.Count);
                Assert.Equal("全部", topics[0]);
                Assert.Equal("匀变速直线运动", topics[1]);
                Assert.Equal("牛顿运动定律", topics[2]);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        [Fact]
        public async Task SystemHealthService_HealStudyPlan_And_Homework_TimestampInvariants()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var studentId = Guid.NewGuid();
                var teacherId = Guid.NewGuid();

                var student = new User
                {
                    Id = studentId,
                    Username = "apex_student_sovereign",
                    Password = "hash",
                    Role = UserRole.Student,
                    Grade = "高二"
                };
                var teacher = new User
                {
                    Id = teacherId,
                    Username = "apex_teacher_sovereign",
                    Password = "hash",
                    Role = UserRole.Teacher
                };
                context.Users.AddRange(student, teacher);

                var baseTime = new DateTime(2026, 9, 1, 12, 0, 0);

                // 异常计划：完成时间早于开始时间，更新时间早于创建时间
                var plan = new StudyPlan
                {
                    Id = Guid.NewGuid(),
                    UserId = studentId,
                    Title = "时序倒流计划",
                    StartDate = baseTime,
                    TargetEndDate = baseTime.AddDays(7),
                    Status = StudyPlanStatus.Completed,
                    CompletedDate = baseTime.AddDays(-3), // 早于 StartDate
                    CreatedAt = baseTime,
                    UpdatedAt = baseTime.AddDays(-1)       // 早于 CreatedAt
                };
                context.StudyPlans.Add(plan);

                // 异常作业：完成时间早于创建时间
                var homework = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    CreatorUserId = teacherId,
                    StudentUserId = studentId,
                    Title = "时序倒流作业",
                    Subject = "高二化学",
                    IsCompleted = true,
                    CreatedAt = baseTime,
                    CompletedAt = baseTime.AddDays(-2),   // 早于 CreatedAt
                    Deadline = baseTime.AddDays(3)
                };
                context.HomeworkAssignments.Add(homework);

                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);
                var planHealed = await healthService.HealStudyPlanInvariantsAsync();
                var hwHealed = await healthService.HealHomeworkAssignmentInvariantsAsync();

                Assert.True(planHealed > 0);
                Assert.True(hwHealed > 0);

                var reloadedPlan = await context.StudyPlans.FirstAsync(p => p.Id == plan.Id);
                var reloadedHw = await context.HomeworkAssignments.FirstAsync(h => h.Id == homework.Id);

                Assert.True(reloadedPlan.CompletedDate >= reloadedPlan.StartDate);
                Assert.True(reloadedPlan.UpdatedAt >= reloadedPlan.CreatedAt);
                Assert.True(reloadedHw.CompletedAt >= reloadedHw.CreatedAt);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }
    }
}
