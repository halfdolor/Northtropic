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
    public class ArchitectAndUxMasteryTests
    {
        #region 1. 架构解耦与成就聚合服务测试 (Decoupled Architecture & Achievements Overview)

        [Fact]
        public async Task GamificationService_GetAchievementsOverviewAsync_ShouldAggregateCorrectly()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "架构大师",
                    TotalAnswered = 100,
                    TotalCorrect = 90,
                    Exp = 500,
                    Coins = 200,
                    ActiveTitle = "连击大师"
                };
                context.Users.Add(user);

                var ach1 = new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "FIRST_BLOOD",
                    Title = "初出茅庐",
                    Description = "完成第 1 题",
                    Icon = "EmojiEvents",
                    RewardExp = 20,
                    RewardCoins = 10
                };
                var ach2 = new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "CENTURY_KILL",
                    Title = "百题斩",
                    Description = "累计答对 100 题",
                    Icon = "EmojiEvents",
                    RewardExp = 200,
                    RewardCoins = 100
                };
                context.Achievements.AddRange(ach1, ach2);

                context.UserAchievements.Add(new UserAchievement
                {
                    UserId = user.Id,
                    AchievementId = ach1.Id,
                    UnlockedAt = DateTime.Now
                });

                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "物理",
                    Category = "力学",
                    Stem = "物理测试题",
                    CorrectAnswer = "10 N",
                    StandardAnalysis = "解析"
                };
                context.Questions.Add(q1);

                context.UserFavorites.Add(new UserFavorite
                {
                    UserId = user.Id,
                    QuestionId = q1.Id,
                    CreatedAt = DateTime.Now
                });

                context.LlmGenerationLogs.Add(new LlmGenerationLog
                {
                    UserId = user.Id,
                    ModelName = "gemini-1.5-flash",
                    Subject = "物理",
                    Category = "力学",
                    GeneratedAt = DateTime.Now
                });

                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = user };
                var gamificationService = new GamificationService(context, sessionMock);
                var overview = await gamificationService.GetAchievementsOverviewAsync(user.Id);

                Assert.NotNull(overview);
                Assert.Equal(2, overview.AllAchievements.Count);
                Assert.Single(overview.UnlockedAchievementIds);
                Assert.Contains(ach1.Id, overview.UnlockedAchievementIds);
                Assert.DoesNotContain(ach2.Id, overview.UnlockedAchievementIds);
                Assert.Equal(1, overview.FavoritesCount);
                Assert.Equal(1, overview.AiExploredCount);
                Assert.Contains("连击大师", overview.AvailableTitles);
            }
        }

        #endregion

        #region 2. 艾宾浩斯 3 阶段复习与彻底攻克状态机测试 (Ebbinghaus 3-Stage Mastery State Machine)

        [Fact]
        public async Task ErrorBookService_ReviseErrorAsync_ThreeStageMasteryLifecycle()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "艾宾浩斯学者",
                    Exp = 100,
                    Coins = 50,
                    ResolvedErrorsCount = 0
                };
                context.Users.Add(user);

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "数学",
                    Category = "一元二次方程",
                    Stem = "数学代数方程题",
                    CorrectAnswer = "3",
                    StandardAnalysis = "解方程"
                };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = question.Id,
                    UserWrongAnswer = "2",
                    RevisionCount = 0,
                    IsMastered = false,
                    CreatedAt = DateTime.Now.AddDays(-3)
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = user };
                var gamificationService = new GamificationService(context, sessionMock);
                var errorBookService = new ErrorBookService(context, gamificationService, sessionMock);

                // 第 1 阶段复习正确：RevisionCount 变为 1，IsMastered 保持 false，奖励阶段性复习经验
                var r1 = await errorBookService.ReviseErrorAsync(errorItem.Id, isCorrect: true, user.Id);
                Assert.Equal(15, r1.EarnedExp);
                Assert.Equal(5, r1.EarnedCoins);
                var dbItem1 = await context.ErrorItems.FindAsync(errorItem.Id);
                Assert.NotNull(dbItem1);
                Assert.Equal(1, dbItem1.RevisionCount);
                Assert.False(dbItem1.IsMastered);
                Assert.Equal(0, user.ResolvedErrorsCount);

                // 第 2 阶段复习正确：RevisionCount 变为 2，IsMastered 保持 false
                var r2 = await errorBookService.ReviseErrorAsync(errorItem.Id, isCorrect: true, user.Id);
                Assert.Equal(15, r2.EarnedExp);
                Assert.Equal(5, r2.EarnedCoins);
                var dbItem2 = await context.ErrorItems.FindAsync(errorItem.Id);
                Assert.NotNull(dbItem2);
                Assert.Equal(2, dbItem2.RevisionCount);
                Assert.False(dbItem2.IsMastered);

                // 第 3 阶段复习正确：达到 3 次，彻底攻克！
                var r3 = await errorBookService.ReviseErrorAsync(errorItem.Id, isCorrect: true, user.Id);
                Assert.True(r3.EarnedExp >= 35);
                Assert.True(r3.EarnedCoins >= 25);
                var dbItem3 = await context.ErrorItems.FindAsync(errorItem.Id);
                Assert.NotNull(dbItem3);
                Assert.Equal(3, dbItem3.RevisionCount);
                Assert.True(dbItem3.IsMastered);
                Assert.Equal(1, user.ResolvedErrorsCount);

                // 答错回退机制验证：另一个错题，当前复习次数为 2，答错后回退为 1，且未掌握
                var anotherError = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = question.Id,
                    UserWrongAnswer = "0",
                    RevisionCount = 2,
                    IsMastered = false,
                    CreatedAt = DateTime.Now.AddDays(-1)
                };
                context.ErrorItems.Add(anotherError);
                await context.SaveChangesAsync();

                var rFail = await errorBookService.ReviseErrorAsync(anotherError.Id, isCorrect: false, user.Id);
                Assert.Equal(5, rFail.EarnedExp);
                Assert.Equal(2, rFail.EarnedCoins);
                var dbFailItem = await context.ErrorItems.FindAsync(anotherError.Id);
                Assert.NotNull(dbFailItem);
                Assert.Equal(1, dbFailItem.RevisionCount);
                Assert.False(dbFailItem.IsMastered);
            }
        }

        #endregion

        #region 3. 填空题高容错匹配算法测试 (LaTeX 符号与物理单位等价性)

        [Theory]
        [InlineData("2 \\times 3", "2 * 3", true)]
        [InlineData("6 \\div 2", "6 / 2", true)]
        [InlineData("x \\leq 5", "x <= 5", true)]
        [InlineData("x \\geq 5", "x >= 5", true)]
        [InlineData("x \\neq 0", "x != 0", true)]
        [InlineData("10 N", "10", true)]
        [InlineData("10", "10 N", true)]
        [InlineData("10 m/s", "10米/秒", true)]
        [InlineData("50 J", "50焦", true)]
        [InlineData("5 kg", "5", true)]
        [InlineData("100 W", "100瓦", true)]
        [InlineData("x = 5", "5", true)]
        [InlineData("5", "x = 5", true)]
        [InlineData("1/2", "0.5", true)]
        [InlineData("50%", "0.5", true)]
        [InlineData("完全不相干", "100", false)]
        public void CheckFillInBlankMatch_ShouldTolerateLatexAndUnits(string user, string correct, bool expectedMatch)
        {
            bool match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expectedMatch, match);
        }

        #endregion

        #region 4. 解耦服务能力扩展测试 (Decoupled Service Queries)

        [Fact]
        public async Task ServiceDecoupling_Queries_ShouldWorkWithoutDirectRazorDbContext()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "解耦测试员"
                };
                context.Users.Add(user);

                var q1 = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "物理",
                    Category = "力学与牛顿定律",
                    Stem = "物理题1",
                    CorrectAnswer = "A"
                };
                var q2 = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "物理",
                    Category = "电磁学",
                    Stem = "物理题2",
                    CorrectAnswer = "B"
                };
                context.Questions.AddRange(q1, q2);

                var rec1 = new PracticeRecord
                {
                    UserId = user.Id,
                    QuestionId = q1.Id,
                    UserAnswer = "A",
                    IsCorrect = true,
                    TimeTakenSeconds = 12,
                    AnsweredAt = DateTime.Now.AddMinutes(-10)
                };
                var rec2 = new PracticeRecord
                {
                    UserId = user.Id,
                    QuestionId = q2.Id,
                    UserAnswer = "C",
                    IsCorrect = false,
                    TimeTakenSeconds = 18,
                    AnsweredAt = DateTime.Now.AddMinutes(-5)
                };
                context.PracticeRecords.AddRange(rec1, rec2);

                var err = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = q2.Id,
                    UserWrongAnswer = "C",
                    CreatedAt = DateTime.Now
                };
                context.ErrorItems.Add(err);
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = user };
                var gamificationService = new GamificationService(context, sessionMock);
                var practiceService = new PracticeService(context, gamificationService, sessionMock, new FakeAiTutorService());
                var errorBookService = new ErrorBookService(context, gamificationService, sessionMock);

                // 1. 获取错题项目
                var fetchedErr = await errorBookService.GetErrorItemByQuestionAsync(user.Id, q2.Id);
                Assert.NotNull(fetchedErr);
                Assert.Equal("C", fetchedErr.UserWrongAnswer);

                // 2. 获取用户做题记录列表
                var userRecords = await practiceService.GetUserPracticeRecordsAsync(user.Id, 10);
                Assert.Equal(2, userRecords.Count);
                Assert.Equal(q2.Id, userRecords[0].QuestionId); // 倒序排在首位

                // 3. 按科目查询题目分类
                PracticeService.InvalidateCategoryCache();
                var categories = await practiceService.GetCategoriesBySubjectAsync("物理");
                Assert.Contains("力学与牛顿定律", categories);
                Assert.Contains("电磁学", categories);
            }
        }

        #endregion

        #region 5. 会话安全与配置更新隔离测试 (User Session Settings & Lockout Cache)

        [Fact]
        public async Task UserSessionService_UpdateUserSettingsAsync_ShouldPersistSafeProperties()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "系统管理员",
                    AliyunSmsSignName = "旧签名",
                    SessionTimeoutMinutes = 30
                };
                context.Users.Add(user);
                await context.SaveChangesAsync();

                var sessionService = new UserSessionService(context, new HttpClient());

                user.AliyunSmsSignName = "极客学堂";
                user.SessionTimeoutMinutes = 60;
                user.LlmModelName = "gpt-4o";

                bool updated = await sessionService.UpdateUserSettingsAsync(user);
                Assert.True(updated);

                var reloaded = await context.Users.FindAsync(user.Id);
                Assert.NotNull(reloaded);
                Assert.Equal("极客学堂", reloaded.AliyunSmsSignName);
                Assert.Equal(60, reloaded.SessionTimeoutMinutes);
                Assert.Equal("gpt-4o", reloaded.LlmModelName);
            }
        }

        #endregion
    }
}
