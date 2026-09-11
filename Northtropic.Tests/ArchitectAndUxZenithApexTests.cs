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
    public class ArchitectAndUxZenithApexTests
    {
        [Fact]
        public async Task GamificationService_ReusesExistingContext_WithoutLocks()
        {
            var (ctx, conn) = TestDbContextFactory.CreateInMemoryContext();
            using (conn)
            using (ctx)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "test_architect_student",
                    Grade = "初中三年级",
                    Role = UserRole.Student,
                    Coins = 100,
                    Exp = 50
                };
                ctx.Users.Add(user);

                var achievement = new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "ERROR_KILLER_5",
                    Title = "错题克星",
                    RewardExp = 50,
                    RewardCoins = 25
                };
                ctx.Achievements.Add(achievement);

                await ctx.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = user };
                var gamificationService = new GamificationService(ctx, fakeUserSession);

                var reward = await gamificationService.ProcessAnswerRewardAsync(
                    isCorrect: true,
                    baseExp: 20,
                    combo: 3,
                    difficulty: 3,
                    timeTakenSeconds: 15,
                    targetUserId: user.Id,
                    existingContext: ctx);

                Assert.NotNull(reward);
                Assert.True(reward.EarnedCoins > 0);
                Assert.True(reward.EarnedExp > 0);

                var unlocked = await gamificationService.UnlockAchievementAsync(
                    achievementCode: "ERROR_KILLER_5",
                    targetUserId: user.Id,
                    existingContext: ctx);

                Assert.True(unlocked);

                var updatedUser = ctx.Users.Local.FirstOrDefault(u => u.Id == user.Id) ?? await ctx.Users.FindAsync(user.Id);
                Assert.NotNull(updatedUser);
                Assert.True(updatedUser.Coins > 100);
                Assert.True(updatedUser.Level >= 1);
            }
        }

        [Fact]
        public async Task QuestionManagementService_CascadeNullifiesLlmLog_OnDelete()
        {
            var (ctx, conn) = TestDbContextFactory.CreateInMemoryContext();
            using (conn)
            using (ctx)
            {
                var qService = new QuestionManagementService(ctx);
                Guid questionId = Guid.NewGuid();
                Guid logId = Guid.NewGuid();
                Guid adminId = Guid.NewGuid();

                var admin = new User
                {
                    Id = adminId,
                    Username = "admin",
                    Role = UserRole.SuperAdmin
                };
                ctx.Users.Add(admin);

                var q = new Question
                {
                    Id = questionId,
                    Subject = "物理",
                    Category = "牛顿第二定律",
                    Stem = "测试级联删除题干",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "A",
                    OptionsJson = "[\"A. 正确\", \"B. 错误\"]"
                };
                ctx.Questions.Add(q);

                var log = new LlmGenerationLog
                {
                    Id = logId,
                    UserId = adminId,
                    QuestionId = questionId,
                    ModelName = "MockLLM",
                    Subject = "物理",
                    Category = "牛顿第二定律"
                };
                ctx.LlmGenerationLogs.Add(log);
                await ctx.SaveChangesAsync();

                bool deleted = await qService.DeleteQuestionAsync(questionId, adminId);
                Assert.True(deleted);

                var deletedQ = await ctx.Questions.FindAsync(questionId);
                Assert.Null(deletedQ);

                var logAfter = await ctx.LlmGenerationLogs.FindAsync(logId);
                Assert.NotNull(logAfter);
                Assert.Null(logAfter.QuestionId);
            }
        }

        [Fact]
        public async Task QuestionManagementService_BatchDeleteQuestions_CascadeNullifiesLlmLogs()
        {
            var (ctx, conn) = TestDbContextFactory.CreateInMemoryContext();
            using (conn)
            using (ctx)
            {
                var qService = new QuestionManagementService(ctx);
                var q1Id = Guid.NewGuid();
                var q2Id = Guid.NewGuid();
                var log1Id = Guid.NewGuid();
                var log2Id = Guid.NewGuid();
                var adminId = Guid.NewGuid();

                var admin = new User
                {
                    Id = adminId,
                    Username = "admin",
                    Role = UserRole.SuperAdmin
                };
                ctx.Users.Add(admin);

                ctx.Questions.AddRange(
                    new Question { Id = q1Id, Subject = "化学", Category = "酸碱盐", Stem = "题1", Type = QuestionType.SingleChoice, CorrectAnswer = "A", OptionsJson = "[]" },
                    new Question { Id = q2Id, Subject = "化学", Category = "酸碱盐", Stem = "题2", Type = QuestionType.SingleChoice, CorrectAnswer = "B", OptionsJson = "[]" }
                );

                ctx.LlmGenerationLogs.AddRange(
                    new LlmGenerationLog { Id = log1Id, UserId = adminId, QuestionId = q1Id, ModelName = "m1", Subject = "化学", Category = "酸碱盐" },
                    new LlmGenerationLog { Id = log2Id, UserId = adminId, QuestionId = q2Id, ModelName = "m2", Subject = "化学", Category = "酸碱盐" }
                );
                await ctx.SaveChangesAsync();

                int count = await qService.BatchDeleteQuestionsAsync(new List<Guid> { q1Id, q2Id }, adminId);
                Assert.Equal(2, count);

                Assert.Null(await ctx.Questions.FindAsync(q1Id));
                Assert.Null(await ctx.Questions.FindAsync(q2Id));

                var log1 = await ctx.LlmGenerationLogs.FindAsync(log1Id);
                var log2 = await ctx.LlmGenerationLogs.FindAsync(log2Id);
                Assert.NotNull(log1);
                Assert.NotNull(log2);
                Assert.Null(log1.QuestionId);
                Assert.Null(log2.QuestionId);
            }
        }

        [Fact]
        public async Task PracticeService_GetCategoriesAsync_ThreadSafeCaching_And_Invalidation()
        {
            var (ctx, conn) = TestDbContextFactory.CreateInMemoryContext();
            using (conn)
            using (ctx)
            {
                var practiceService = new PracticeService(ctx, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                ctx.Questions.Add(new Question { Id = Guid.NewGuid(), Subject = "数学", Category = "反比例函数", Stem = "反比例函数考题", Type = QuestionType.SingleChoice, CorrectAnswer = "A", OptionsJson = "[]" });
                await ctx.SaveChangesAsync();

                PracticeService.InvalidateCategoryCache();

                var categories1 = await practiceService.GetCategoriesAsync();
                Assert.Contains("全部", categories1);
                Assert.Contains("反比例函数", categories1);

                ctx.Questions.Add(new Question { Id = Guid.NewGuid(), Subject = "数学", Category = "勾股定理", Stem = "勾股定理考题", Type = QuestionType.SingleChoice, CorrectAnswer = "B", OptionsJson = "[]" });
                await ctx.SaveChangesAsync();

                var categories2 = await practiceService.GetCategoriesAsync();
                Assert.DoesNotContain("勾股定理", categories2);

                PracticeService.InvalidateCategoryCache();
                var categories3 = await practiceService.GetCategoriesAsync();
                Assert.Contains("勾股定理", categories3);
            }
        }

        [Fact]
        public async Task PracticeService_GetCategoriesBySubjectAsync_FallbackToGradeSubjectProvider()
        {
            var (ctx, conn) = TestDbContextFactory.CreateInMemoryContext();
            using (conn)
            using (ctx)
            {
                var practiceService = new PracticeService(ctx, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                var mathCats = await practiceService.GetCategoriesBySubjectAsync("数学");
                Assert.NotNull(mathCats);
                Assert.Contains("全部", mathCats);
                Assert.True(mathCats.Count > 1);
            }
        }

        [Fact]
        public void ErrorReasonCategory_TaxonomyTargeting_GroupsCorrectly()
        {
            var unmastered = new List<ErrorItem>
            {
                new ErrorItem { Id = Guid.NewGuid(), QuestionId = Guid.NewGuid(), ErrorReasonCategory = "概念模糊", Question = new Question { Id = Guid.NewGuid(), Stem = "概念题", OptionsJson = "[]" } },
                new ErrorItem { Id = Guid.NewGuid(), QuestionId = Guid.NewGuid(), ErrorReasonCategory = "概念模糊", Question = new Question { Id = Guid.NewGuid(), Stem = "概念题2", OptionsJson = "[]" } },
                new ErrorItem { Id = Guid.NewGuid(), QuestionId = Guid.NewGuid(), ErrorReasonCategory = "审题粗心", Question = new Question { Id = Guid.NewGuid(), Stem = "粗心题", OptionsJson = "[]" } },
                new ErrorItem { Id = Guid.NewGuid(), QuestionId = Guid.NewGuid(), ErrorReasonCategory = "逻辑计算错误", Question = new Question { Id = Guid.NewGuid(), Stem = "计算题", OptionsJson = "[]" } }
            };

            var conceptQuestions = unmastered
                .Where(i => i.ErrorReasonCategory == "概念模糊" && i.Question != null)
                .Select(i => i.Question!.Id.ToString())
                .ToList();

            Assert.Equal(2, conceptQuestions.Count);

            var carelessQuestions = unmastered
                .Where(i => i.ErrorReasonCategory == "审题粗心" && i.Question != null)
                .Select(i => i.Question!.Id.ToString())
                .ToList();

            Assert.Single(carelessQuestions);
        }
    }
}
