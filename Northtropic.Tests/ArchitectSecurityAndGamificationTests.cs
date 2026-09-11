using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectSecurityAndGamificationTests
    {
        [Fact]
        public async Task UpdateErrorReasonAsync_UnauthorizedUser_ShouldNotUpdateReason()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var victimUserId = Guid.NewGuid();
                var attackerUserId = Guid.NewGuid();

                var question = new Question { Stem = "浮力计算题", OptionsJson = "[]", CorrectAnswer = "A" };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = victimUserId,
                    Question = question,
                    UserWrongAnswer = "B",
                    ErrorReasonCategory = "计算失误",
                    IsMastered = false
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = new User { Id = attackerUserId } };
                var fakeGamification = new FakeGamificationService();
                var service = new ErrorBookService(context, fakeGamification, fakeUserSession);

                // Act: 试图以他人身份篡改错因
                await service.UpdateErrorReasonAsync(errorItem.Id, "粗心大意", attackerUserId);

                // Assert: 原始错因不得被篡改
                var refreshed = await context.ErrorItems.FindAsync(errorItem.Id);
                Assert.NotNull(refreshed);
                Assert.Equal("计算失误", refreshed.ErrorReasonCategory);
            }
        }

        [Fact]
        public async Task UpdateErrorReasonAsync_AuthorizedUser_ShouldUpdateReason()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var ownerUserId = Guid.NewGuid();

                var question = new Question { Stem = "欧姆定律题", OptionsJson = "[]", CorrectAnswer = "A" };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = ownerUserId,
                    Question = question,
                    UserWrongAnswer = "B",
                    ErrorReasonCategory = "概念模糊",
                    IsMastered = false
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = new User { Id = ownerUserId } };
                var fakeGamification = new FakeGamificationService();
                var service = new ErrorBookService(context, fakeGamification, fakeUserSession);

                // Act
                await service.UpdateErrorReasonAsync(errorItem.Id, "公式记错", ownerUserId);

                // Assert
                var refreshed = await context.ErrorItems.FindAsync(errorItem.Id);
                Assert.NotNull(refreshed);
                Assert.Equal("公式记错", refreshed.ErrorReasonCategory);
            }
        }

        [Fact]
        public async Task ProcessErrorRevisionRewardAsync_UnauthorizedUser_ShouldRejectRewardAndNotMaster()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var victimUserId = Guid.NewGuid();
                var attackerUserId = Guid.NewGuid();

                var victim = new User { Id = victimUserId, Username = "受害学生", Exp = 0, Coins = 100, Level = 1 };
                var attacker = new User { Id = attackerUserId, Username = "攻击者", Exp = 0, Coins = 100, Level = 1 };
                context.Users.AddRange(victim, attacker);

                var question = new Question { Stem = "电功率计算", OptionsJson = "[]", CorrectAnswer = "A" };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = victimUserId,
                    Question = question,
                    UserWrongAnswer = "B",
                    IsMastered = false
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = attacker };
                var gamificationService = new GamificationService(context, fakeUserSession);

                // Act: 攻击者试图净化领取受害者的错题
                var result = await gamificationService.ProcessErrorRevisionRewardAsync(errorItem.Id);

                // Assert
                Assert.Equal(0, result.EarnedExp);
                Assert.Equal(0, result.EarnedCoins);

                var errorInDb = await context.ErrorItems.FindAsync(errorItem.Id);
                Assert.NotNull(errorInDb);
                Assert.False(errorInDb.IsMastered); // 错题仍未掌握

                var attackerInDb = await context.Users.FindAsync(attackerUserId);
                Assert.NotNull(attackerInDb);
                Assert.Equal(0, attackerInDb.Exp); // 未获得经验
            }
        }

        [Fact]
        public async Task MultiLevelUp_LargeExpGain_ShouldAdvanceMultipleLevels()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "冲刺学生",
                    Level = 1, // Lv.1 需要 100 经验, Lv.2 需要 200 经验, Lv.3 需要 300 经验
                    Exp = 0,
                    Coins = 100,
                    TodayCountDate = DateTime.Today
                };
                context.Users.Add(student);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = student };
                var gamificationService = new GamificationService(context, fakeUserSession);

                // Act: 注入高额基础经验 (如 350 EXP)
                // 100 升 Lv.2 (剩 250), 200 升 Lv.3 (剩 50)
                var result = await gamificationService.ProcessAnswerRewardAsync(isCorrect: true, baseExp: 350, combo: 0);

                // Assert
                Assert.True(result.LeveledUp);
                Assert.Equal(3, result.NewLevel);

                var studentInDb = await context.Users.FindAsync(student.Id);
                Assert.NotNull(studentInDb);
                Assert.Equal(3, studentInDb.Level);
                Assert.Equal(50, studentInDb.Exp);
            }
        }

        [Fact]
        public async Task GuestSession_AnswerReward_ShouldNotMutateDatabaseUserOrSuperAdmin()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var superAdmin = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "超级管理员",
                    Role = UserRole.SuperAdmin,
                    Level = 1,
                    Exp = 0,
                    Coins = 500,
                    TotalAnswered = 0
                };
                context.Users.Add(superAdmin);
                await context.SaveChangesAsync();

                // 未登录访客会话 (ActiveUser = null)
                var fakeUserSession = new FakeUserSessionService { ActiveUser = null };
                var gamificationService = new GamificationService(context, fakeUserSession);

                // Act: 访客作答
                var result = await gamificationService.ProcessAnswerRewardAsync(isCorrect: true, baseExp: 20, combo: 0);

                // Assert
                Assert.True(result.EarnedExp > 0);

                // 检验数据库中超级管理员未被篡改或污染
                var adminInDb = await context.Users.FindAsync(superAdmin.Id);
                Assert.NotNull(adminInDb);
                Assert.Equal(0, adminInDb.Exp);
                Assert.Equal(0, adminInDb.TotalAnswered);
                Assert.Equal(500, adminInDb.Coins);
            }
        }

        [Fact]
        public async Task DeleteQuestionAsync_UnauthorizedUser_ShouldRejectDeletion()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var authorTeacher = new User { Id = Guid.NewGuid(), Username = "作者老师", Role = UserRole.Teacher };
                var randomStudent = new User { Id = Guid.NewGuid(), Username = "普通学生", Role = UserRole.Student };

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "物理受力分析题",
                    CreatedByUserId = authorTeacher.Id
                };

                context.Users.AddRange(authorTeacher, randomStudent);
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Act: 普通学生尝试删除老师的题
                var result = await service.DeleteQuestionAsync(question.Id, randomStudent.Id);

                // Assert
                Assert.False(result);
                var exists = await context.Questions.AnyAsync(q => q.Id == question.Id);
                Assert.True(exists);
            }
        }

        [Fact]
        public async Task DeleteQuestionAsync_QuestionAuthorOrAdmin_ShouldSucceed()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var authorTeacher = new User { Id = Guid.NewGuid(), Username = "作者老师", Role = UserRole.Teacher };
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "化学实验题",
                    CreatedByUserId = authorTeacher.Id
                };

                context.Users.Add(authorTeacher);
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Act: 作者本人删除
                var result = await service.DeleteQuestionAsync(question.Id, authorTeacher.Id);

                // Assert
                Assert.True(result);
                var exists = await context.Questions.AnyAsync(q => q.Id == question.Id);
                Assert.False(exists);
            }
        }

        [Fact]
        public async Task SendSmsCodeAsync_WithinCooldown_ShouldRejectRateLimit()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                using var httpClient = new System.Net.Http.HttpClient();
                var userSessionService = new UserSessionService(context, httpClient);
                string testPhone = "13912345678";

                // Act 1: 第一次发送 (记录60秒冷却)
                await userSessionService.SendSmsCodeAsync(testPhone);

                // Act 2: 立即高频触发第二次发送
                var secondResult = await userSessionService.SendSmsCodeAsync(testPhone);

                // Assert: 第二次必须被防刷频冷却拦截
                Assert.False(secondResult.Success);
                Assert.Contains("频繁", secondResult.Message);
            }
        }

        [Fact]
        public async Task LoginOrRegisterWithSmsAsync_NonDemoUserWithBypassCode_ShouldReject()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                // 创建一个真实手机号的正式注册用户
                var realUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "真实学生",
                    PhoneNumber = "13987654321",
                    Role = UserRole.Student,
                    AccountStatus = UserAccountStatus.Approved
                };
                context.Users.Add(realUser);
                await context.SaveChangesAsync();

                using var httpClient = new System.Net.Http.HttpClient();
                var userSessionService = new UserSessionService(context, httpClient);

                // Act: 恶意用户试图以 123456 弱口令绕过短信验证码登录
                var result = await userSessionService.LoginOrRegisterWithSmsAsync("13987654321", "123456");

                // Assert: 严禁放行非演示测试账号
                Assert.False(result.Success);
                Assert.Null(result.User);
                Assert.Contains("无效或已过期", result.Message);
            }
        }

        [Fact]
        public async Task SubmitAnswerAsync_GuestUser_ShouldGradeCorrectlyWithoutDbForeignConstraintError()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "下列属于光的折射现象的是？",
                    OptionsJson = "[\"A. 水中倒影\", \"B. 筷子弯折\", \"C. 小孔成像\", \"D. 影子形成\"]",
                    CorrectAnswer = "B",
                    Type = QuestionType.SingleChoice,
                    Difficulty = 2,
                    BaseExpReward = 20
                };
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = null };
                var gamificationService = new GamificationService(context, fakeUserSession);
                var fakeAiTutor = new FakeAiTutorService();
                var practiceService = new PracticeService(context, gamificationService, fakeUserSession, fakeAiTutor);

                // Act: 未登录访客提交作答
                var result = await practiceService.SubmitAnswerAsync(question, "B", timeTakenSeconds: 15, currentCombo: 0);

                // Assert: 判分正常，奖励模拟正常
                Assert.True(result.IsCorrect);
                Assert.NotNull(result.Reward);
                Assert.True(result.Reward.EarnedExp > 0);

                // 检验数据库中绝对没有插入 Guid.Empty 的孤儿答题记录和错题
                var hasGuestRecord = await context.PracticeRecords.AnyAsync(r => r.UserId == Guid.Empty);
                var hasGuestError = await context.ErrorItems.AnyAsync(e => e.UserId == Guid.Empty);
                Assert.False(hasGuestRecord);
                Assert.False(hasGuestError);
            }
        }

        [Fact]
        public async Task AchievementUnlock_ShouldTriggerLevelUpWhenExpThresholdExceeded()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "考霸考生",
                    Level = 1,
                    Exp = 90, // 距离升级差 10 经验
                    Coins = 50,
                    TotalAnswered = 0,
                    TodayCountDate = DateTime.Today
                };
                context.Users.Add(student);

                // 添加首杀成就 (奖励 100 经验)
                var achievement = new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "FIRST_BLOOD",
                    Title = "初露锋芒 (首答奖励)",
                    RewardExp = 100,
                    RewardCoins = 50
                };
                context.Achievements.Add(achievement);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = student };
                var gamificationService = new GamificationService(context, fakeUserSession);

                // Act: 第一次答对题目触发 FIRST_BLOOD 成就解锁
                var result = await gamificationService.ProcessAnswerRewardAsync(isCorrect: true, baseExp: 15, combo: 0);

                // Assert: 答题 15 经验 + 成就 100 经验 = 115 经验
                // 90 + 115 = 205 经验 -> 扣除 100 升级到 Lv.2，剩余 105 经验；
                // 此时 Lv.2 升级需 200 经验，105 < 200，最终处于 Lv.2
                Assert.True(result.LeveledUp);
                Assert.Equal(2, result.NewLevel);
                Assert.Equal("初露锋芒 (首答奖励)", result.UnlockedAchievementTitle);

                var userInDb = await context.Users.FindAsync(student.Id);
                Assert.NotNull(userInDb);
                Assert.Equal(2, userInDb.Level);
            }
        }
    }
}
