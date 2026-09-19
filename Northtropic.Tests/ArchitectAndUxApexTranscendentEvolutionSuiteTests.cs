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
    public class ArchitectAndUxApexTranscendentEvolutionSuiteTests
    {
        [Fact]
        public void DataIntegrityAuditDto_MetricsAggregation_AccuratelyIncludesHomeworkAndFavorites()
        {
            var audit = new DataIntegrityAuditDto
            {
                TotalOrphanErrorItems = 1,
                TotalOrphanPracticeRecords = 2,
                TotalOrphanUserFavorites = 3,
                TotalOrphanHomeworkAssignments = 4,
                TotalOrphanBindings = 5,
                TotalOrphanStudyPlans = 6,
                TotalOrphanStudyPlanTasks = 7,
                TotalOrphanUserAchievements = 8,
                TotalOrphanInsights = 9,
                TotalOrphanLlmLogs = 10,
                TotalCorruptedQuestions = 11,
                TotalOrphanPrivateQuestions = 12,
                TotalDanglingPublicQuestions = 13,
                TotalInvalidStudyPlans = 14,
                TotalInvalidBindings = 15,
                TotalInvalidHomeworkAssignments = 16,
                TotalDuplicateQuestions = 17,
                TotalUsersWithInvalidBalances = 18,
                TotalDuplicateFavorites = 19
            };

            Assert.Equal(16, audit.InvalidHomeworkAssignmentsCount);
            Assert.Equal(19, audit.DuplicateFavoritesCount);

            int expectedIssues = 1 + 2 + 3 + 4 + 5 + 6 + 7 + 8 + 9 + 10 + 11 + 12 + 13 + 14 + 15 + 16;
            Assert.Equal(expectedIssues, audit.TotalIssuesCount);

            int expectedOptimizations = 17 + 18 + 14 + 15 + 16 + 19;
            Assert.Equal(expectedOptimizations, audit.TotalOptimizationCandidatesCount);
            Assert.False(audit.IsHealthy);
        }

        [Fact]
        public async Task SystemHealthService_AuditDataIntegrity_DetectsHomeworkInvariantsAndDuplicateFavorites()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var studentId = Guid.NewGuid();
                var teacherId = Guid.NewGuid();

                var student = new User
                {
                    Id = studentId,
                    Username = "apex_student",
                    Password = "hash",
                    Role = UserRole.Student,
                    Grade = "高一"
                };
                var teacher = new User
                {
                    Id = teacherId,
                    Username = "apex_teacher",
                    Password = "hash",
                    Role = UserRole.Teacher
                };

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试试题",
                    CorrectAnswer = "A",
                    Type = QuestionType.SingleChoice,
                    Subject = "数学",
                    GradeTarget = "高一",
                    CreatedByUserId = teacherId
                };

                context.Users.AddRange(student, teacher);
                context.Questions.Add(question);

                // 构造异常作业 1：负数题量、负数答题量、答对数越界、分数越界、已完成但无完成时间
                var invalidHomework1 = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    StudentUserId = studentId,
                    CreatorUserId = teacherId,
                    Title = "异常作业 1",
                    Subject = "数学",
                    QuestionCount = -5,
                    TotalAnswered = -2,
                    CorrectCount = 10,
                    Score = 150,
                    AccuracyRate = 0,
                    IsCompleted = true,
                    CompletedAt = null
                };

                // 构造异常作业 2：未完成但有完成时间戳，准确率脱节 (答对2/答题5 = 40%，但填了 95%)
                var invalidHomework2 = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    StudentUserId = studentId,
                    CreatorUserId = teacherId,
                    Title = "异常作业 2",
                    Subject = "数学",
                    QuestionCount = 10,
                    TotalAnswered = 5,
                    CorrectCount = 2,
                    Score = 80,
                    AccuracyRate = 95,
                    IsCompleted = false,
                    CompletedAt = DateTime.Now
                };

                // 构造重复收藏记录（同一用户同一试题收藏 3 次）
                var favorite1 = new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = studentId,
                    QuestionId = question.Id,
                    CreatedAt = DateTime.Now.AddHours(-3)
                };
                var favorite2 = new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = studentId,
                    QuestionId = question.Id,
                    CreatedAt = DateTime.Now.AddHours(-2)
                };
                var favorite3 = new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = studentId,
                    QuestionId = question.Id,
                    CreatedAt = DateTime.Now.AddHours(-1)
                };

                context.HomeworkAssignments.AddRange(invalidHomework1, invalidHomework2);
                context.UserFavorites.AddRange(favorite1, favorite2, favorite3);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 执行审计
                var audit = await healthService.AuditDataIntegrityAsync();

                Assert.Equal(2, audit.TotalInvalidHomeworkAssignments);
                Assert.Equal(2, audit.InvalidHomeworkAssignmentsCount);
                Assert.Equal(2, audit.TotalDuplicateFavorites);
                Assert.Equal(2, audit.DuplicateFavoritesCount);
                Assert.False(audit.IsHealthy);
                Assert.Contains(audit.AuditDetails, d => d.Contains("份作业分配存在领域不变量异常"));
                Assert.Contains(audit.AuditDetails, d => d.Contains("条收藏夹冗余副本"));
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        [Fact]
        public async Task SystemHealthService_HealHomeworkAndFavorites_RestoresFullIntegrity()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var studentId = Guid.NewGuid();
                var teacherId = Guid.NewGuid();

                var student = new User
                {
                    Id = studentId,
                    Username = "apex_heal_student",
                    Password = "hash",
                    Role = UserRole.Student,
                    Grade = "高二"
                };
                var teacher = new User
                {
                    Id = teacherId,
                    Username = "apex_heal_teacher",
                    Password = "hash",
                    Role = UserRole.Teacher
                };

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试试题",
                    CorrectAnswer = "B",
                    Type = QuestionType.SingleChoice,
                    Subject = "物理",
                    GradeTarget = "高二",
                    CreatedByUserId = teacherId
                };

                context.Users.AddRange(student, teacher);
                context.Questions.Add(question);

                // 构造异常作业
                var invalidHomework = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    StudentUserId = studentId,
                    CreatorUserId = teacherId,
                    Title = "待自愈作业",
                    Subject = "物理",
                    QuestionCount = 10,
                    TotalAnswered = 6,
                    CorrectCount = 3,
                    Score = 120, // 越界，应 clamp 为 100
                    AccuracyRate = 10, // 与 3/6=50% 脱节
                    IsCompleted = true,
                    CompletedAt = null // 已完成但时间戳缺失
                };

                var earliestDate = DateTime.Now.AddDays(-2);
                var favoriteCanonical = new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = studentId,
                    QuestionId = question.Id,
                    CreatedAt = earliestDate
                };
                var favoriteDuplicate1 = new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = studentId,
                    QuestionId = question.Id,
                    CreatedAt = DateTime.Now.AddDays(-1)
                };
                var favoriteDuplicate2 = new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = studentId,
                    QuestionId = question.Id,
                    CreatedAt = DateTime.Now
                };

                context.HomeworkAssignments.Add(invalidHomework);
                context.UserFavorites.AddRange(favoriteCanonical, favoriteDuplicate1, favoriteDuplicate2);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 1. 自愈家庭作业不变量
                int healedHomework = await healthService.HealHomeworkAssignmentInvariantsAsync();
                Assert.True(healedHomework >= 1, "应成功自愈异常作业");

                var updatedHomework = await context.HomeworkAssignments.FirstAsync(h => h.Id == invalidHomework.Id);
                Assert.Equal(50, updatedHomework.AccuracyRate);
                Assert.Equal(100, updatedHomework.Score);
                Assert.True(updatedHomework.IsCompleted);
                Assert.NotNull(updatedHomework.CompletedAt);

                // 2. 自愈收藏夹冗余副本
                int healedFavorites = await healthService.HealUserFavoriteInvariantsAsync();
                Assert.Equal(2, healedFavorites);

                var remainingFavorites = await context.UserFavorites.Where(f => f.UserId == studentId && f.QuestionId == question.Id).ToListAsync();
                Assert.Single(remainingFavorites);
                Assert.Equal(favoriteCanonical.Id, remainingFavorites[0].Id);

                // 3. 再次执行数据审计，应不再发现异常
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.TotalInvalidHomeworkAssignments);
                Assert.Equal(0, auditAfter.TotalDuplicateFavorites);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        [Theory]
        [InlineData("(-inf, 1] 与 [3, +inf)", "(-inf, 1] U [3, +inf)", true)]
        [InlineData("[1, 2] 及 [3, 4]", "[1, 2] ∪ [3, 4]", true)]
        [InlineData("[1, 2] 并且 [3, 4]", "[1, 2] U [3, 4]", true)]
        [InlineData("10 m·s^-2", "10 m/s^2", true)]
        [InlineData("9.8 m*s^-2", "9.8 m/s^2", true)]
        [InlineData("50 N·m", "50 N*m", true)]
        [InlineData("2 T·m^2", "2 Wb", true)]
        [InlineData("充要条件", "充分必要条件", true)]
        [InlineData("x ∈ R", "实数集", true)]
        public void PracticeService_CheckFillInBlankMatch_AdvancedStemAndUnitEquivalence(string user, string correct, bool expected)
        {
            bool matched = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, matched);
        }
    }
}
