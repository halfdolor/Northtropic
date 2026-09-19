using System;
using System.Collections.Generic;
using System.IO;
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
    public class ArchitectAndUxTranscendentZenithEvolutionTests
    {
        private (AppDbContext context, SqliteConnection connection) CreateInMemoryDbContext()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AppDbContext(options);
            context.Database.EnsureCreated();
            return (context, connection);
        }

        #region 1. 系统架构师维度：数据拓扑完整性巡检与孤儿自愈引擎

        [Fact]
        public async Task DataIntegrity_AuditAndPurge_CorrectlyIdentifiesAndHealsOrphans()
        {
            var (context, connection) = CreateInMemoryDbContext();
            try
            {
                var healthService = new SystemHealthService(context, null);

                // 1. 基础状态校验：全空库应为健康
                var cleanAudit = await healthService.AuditDataIntegrityAsync();
                Assert.True(cleanAudit.IsHealthy);
                Assert.Equal(0, cleanAudit.TotalIssuesCount);

                // 2. 注入合法根数据 (1个合法用户，1道合法题目)
                var validUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "legit_user"
                };
                var validQuestion = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "合法测试题",
                    Subject = "数学",
                    GradeTarget = "高一",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "42"
                };
                context.Users.Add(validUser);
                context.Questions.Add(validQuestion);
                await context.SaveChangesAsync();

                // 3. 临时关闭外键约束模拟历史数据或直接修改产生的孤儿脏数据
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                    cmd.ExecuteNonQuery();
                }

                var nonExistentUserId = Guid.NewGuid();
                var nonExistentQuestionId = Guid.NewGuid();

                // A. 孤儿错题 (Orphan ErrorItem)
                context.ErrorItems.Add(new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = nonExistentUserId,
                    QuestionId = validQuestion.Id,
                    UserWrongAnswer = "错误答案1"
                });
                context.ErrorItems.Add(new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = validUser.Id,
                    QuestionId = nonExistentQuestionId,
                    UserWrongAnswer = "错误答案2"
                });

                // B. 孤儿练习流水 (Orphan PracticeRecord)
                context.PracticeRecords.Add(new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = nonExistentUserId,
                    QuestionId = validQuestion.Id,
                    IsCorrect = true
                });

                // C. 孤儿收藏 (Orphan UserFavorite)
                context.UserFavorites.Add(new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = validUser.Id,
                    QuestionId = nonExistentQuestionId
                });

                // D. 孤儿作业指派 (Orphan HomeworkAssignment)
                context.HomeworkAssignments.Add(new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    StudentUserId = nonExistentUserId,
                    CreatorUserId = validUser.Id,
                    Title = "孤儿作业"
                });

                // E. 孤儿家校绑定 (Orphan StudentParentBinding)
                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = nonExistentUserId,
                    StudentUserId = validUser.Id
                });

                // F. 孤儿学习计划 (Orphan StudyPlan)
                context.StudyPlans.Add(new StudyPlan
                {
                    Id = Guid.NewGuid(),
                    UserId = nonExistentUserId,
                    DailyTargetQuestions = 10
                });

                // G. 选项 JSON 损坏的选择题 (Corrupted Question)
                context.Questions.Add(new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "损坏选择题",
                    Subject = "数学",
                    GradeTarget = "高一",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "A",
                    OptionsJson = "INVALID_NOT_JSON"
                });

                await context.SaveChangesAsync();

                // 4. 执行数据拓扑审计
                var dirtyAudit = await healthService.AuditDataIntegrityAsync();
                Assert.False(dirtyAudit.IsHealthy);
                Assert.Equal(2, dirtyAudit.TotalOrphanErrorItems);
                Assert.Equal(1, dirtyAudit.TotalOrphanPracticeRecords);
                Assert.Equal(1, dirtyAudit.TotalOrphanUserFavorites);
                Assert.Equal(1, dirtyAudit.TotalOrphanHomeworkAssignments);
                Assert.Equal(1, dirtyAudit.TotalOrphanBindings);
                Assert.Equal(1, dirtyAudit.TotalOrphanStudyPlans);
                Assert.Equal(1, dirtyAudit.TotalCorruptedQuestions);
                Assert.Equal(8, dirtyAudit.TotalIssuesCount);
                Assert.NotEmpty(dirtyAudit.AuditDetails);

                // 5. 验证自适应自愈规划引擎自动捕获孤儿问题
                var plan = await healthService.EvaluateAdaptiveMaintenancePlanAsync();
                Assert.True(plan.RequiresOrphanCleanup);
                Assert.Contains(plan.ActionReasons, r => r.Contains("孤儿"));

                // 6. 执行原子孤儿清理自愈
                var purgeResult = await healthService.PurgeOrphanedRecordsAsync();
                Assert.True(purgeResult.Success);
                Assert.Equal(7, purgeResult.TotalPurgedCount); // 7 条孤儿记录被清除
                Assert.Equal(2, purgeResult.PurgedErrorItemsCount);
                Assert.Equal(1, purgeResult.PurgedPracticeRecordsCount);
                Assert.Equal(1, purgeResult.PurgedUserFavoritesCount);
                Assert.Equal(1, purgeResult.PurgedHomeworkAssignmentsCount);
                Assert.Equal(1, purgeResult.PurgedBindingsCount);
                Assert.Equal(1, purgeResult.PurgedStudyPlansCount);

                // 7. 再次审计：所有孤儿关联已被自愈清理 (仅剩需题库老师重修的损坏选择题)
                var healedAudit = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, healedAudit.TotalOrphanErrorItems);
                Assert.Equal(0, healedAudit.TotalOrphanPracticeRecords);
                Assert.Equal(0, healedAudit.TotalOrphanUserFavorites);
                Assert.Equal(0, healedAudit.TotalOrphanHomeworkAssignments);
                Assert.Equal(0, healedAudit.TotalOrphanBindings);
                Assert.Equal(0, healedAudit.TotalOrphanStudyPlans);
            }
            finally
            {
                connection.Close();
            }
        }

        [Fact]
        public async Task ArchitecturalSelfDiagnostic_IntegratesDataIntegrityCheckpoint()
        {
            var (context, connection) = CreateInMemoryDbContext();
            try
            {
                var healthService = new SystemHealthService(context, null);
                var diag = await healthService.RunArchitecturalSelfDiagnosticAsync();

                Assert.True(diag.IsPassed);
                Assert.Equal("ok", diag.DataIntegrityStatus);
                Assert.Equal(0, diag.DataIntegrityIssuesCount);
                Assert.Contains(diag.DiagnosticCheckpoints, cp => cp.Contains("业务数据拓扑完整性巡检"));
            }
            finally
            {
                connection.Close();
            }
        }

        #endregion

        #region 2. 用户体验专家维度：数理化智能容错判卷与学术记法等价

        [Theory]
        [InlineData("tg(x)", "tan(x)")]
        [InlineData("tg x", "tan x")]
        [InlineData(@"\tg x", @"\tan x")]
        [InlineData("ctg(x)", "cot(x)")]
        [InlineData("ctg x", "cot x")]
        [InlineData(@"\ctg(x)", @"\cot(x)")]
        public void CheckFillInBlankMatch_ClassicalTrigonometricEquivalence_MatchesCorrectly(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
            Assert.True(PracticeService.CheckFillInBlankMatch(correct, user));
        }

        [Theory]
        [InlineData("1/tan(x)", "cot(x)")]
        [InlineData("1/tan x", "cot x")]
        [InlineData("1/cot(x)", "tan(x)")]
        [InlineData("1/cos(x)", "sec(x)")]
        [InlineData("1/cos x", "sec x")]
        [InlineData("1/sin(x)", "csc(x)")]
        [InlineData("1/sin x", "csc x")]
        public void CheckFillInBlankMatch_ReciprocalTrigonometricIdentities_MatchesCorrectly(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
            Assert.True(PracticeService.CheckFillInBlankMatch(correct, user));
        }

        [Theory]
        [InlineData("x^-1", "1/x")]
        [InlineData("x^-2", "1/x^2")]
        [InlineData("x^(1/2)", @"\sqrt{x}")]
        [InlineData("x^(1/3)", @"\cbrt{x}")]
        [InlineData("e^x", "exp(x)")]
        [InlineData("exp(2x)", "e^(2x)")]
        public void CheckFillInBlankMatch_PowersAndExponentialEquivalence_MatchesCorrectly(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
            Assert.True(PracticeService.CheckFillInBlankMatch(correct, user));
        }

        [Theory]
        [InlineData("2到5", "[2, 5]")]
        [InlineData("2至5", "[2, 5]")]
        [InlineData("2~5", "[2, 5]")]
        [InlineData("-3到7", "[-3, 7]")]
        [InlineData("x大于等于2且小于等于5", "[2, 5]")]
        [InlineData("x大于等于3", "[3, +inf)")]
        [InlineData("x小于5", "(-inf, 5)")]
        [InlineData("全体实数", "R")]
        [InlineData("实数集", "(-inf, +inf)")]
        [InlineData("x属于R", "R")]
        [InlineData("空集", "∅")]
        [InlineData("无解", "∅")]
        [InlineData("无实数解", "{}")]
        public void CheckFillInBlankMatch_ChineseIntervalsAndSolutionSets_MatchesCorrectly(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
            Assert.True(PracticeService.CheckFillInBlankMatch(correct, user));
        }

        [Theory]
        // 频率: kHz <-> Hz, MHz <-> Hz
        [InlineData("1 kHz", "1000 Hz")]
        [InlineData("2.5 MHz", "2500000 Hz")]
        [InlineData("1000赫兹", "1千赫")]
        // 电容: uF, nF, pF <-> F
        [InlineData("1 uF", "0.000001 F")]
        [InlineData("1000 nF", "1 uF")]
        [InlineData("100 pF", "1e-10 F")]
        // 速度: km/h <-> m/s (36 km/h = 10 m/s, 72 km/h = 20 m/s)
        [InlineData("36 km/h", "10 m/s")]
        [InlineData("72 km/h", "20 m/s")]
        [InlineData("10米/秒", "36公里/小时")]
        // 功率: kW <-> W
        [InlineData("1 kW", "1000 W")]
        [InlineData("2.5千瓦", "2500瓦")]
        // 压强: kPa <-> Pa
        [InlineData("100 kPa", "100000 Pa")]
        [InlineData("100千帕", "100000帕")]
        public void CheckFillInBlankMatch_ScientificPhysicalQuantities_MatchesCorrectly(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
            Assert.True(PracticeService.CheckFillInBlankMatch(correct, user));
        }

        [Fact]
        public void GenerateEquivalentMatchReason_ProvidesEncouragingAndAccurateExplanations()
        {
            var trigQuestion = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "tan(x)"
            };
            var reasonTrig = PracticeService.GenerateEquivalentMatchReason(trigQuestion, "tg(x)");
            Assert.Contains("三角函数", reasonTrig);

            var powerQuestion = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "1/x"
            };
            var reasonPower = PracticeService.GenerateEquivalentMatchReason(powerQuestion, "x^-1");
            Assert.Contains("指数幂", reasonPower);

            var unitQuestion = new Question
            {
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "1000 Hz"
            };
            var reasonUnit = PracticeService.GenerateEquivalentMatchReason(unitQuestion, "1 kHz");
            Assert.Contains("物理与工程量纲", reasonUnit);
        }

        #endregion
    }
}
