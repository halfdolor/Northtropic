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
    public class ArchitectAndUxExcellenceTests
    {
        #region 1. 错题复练阶段性奖励持久化与状态一致性测试

        [Fact]
        public async Task ErrorBook_ReviseError_IntermediatePhase_ShouldCreditUserExpAndCoinsAndCheckLevelUp()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "StudentHero",
                    Role = UserRole.Student,
                    Exp = 0,
                    Coins = 0,
                    Level = 1,
                    TotalAnswered = 0,
                    TotalCorrect = 0
                };
                context.Users.Add(user);

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "数学",
                    Stem = "若 2x + 1 = 5，则 x 等于多少？",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "A",
                    Difficulty = 2
                };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = question.Id,
                    UserWrongAnswer = "B",
                    RevisionCount = 0,
                    IsMastered = false,
                    CreatedAt = DateTime.Now.AddDays(-2),
                    LastRevisedAt = DateTime.Now.AddDays(-2)
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                using var httpClient = new HttpClient();
                var sessionService = new UserSessionService(context, httpClient);
                await sessionService.SwitchUserAsync(user.Id);

                var gamificationService = new GamificationService(context, sessionService);
                var errorBookService = new ErrorBookService(context, gamificationService, sessionService);

                // 第一次阶段性复练答对 (RevisionCount 0 -> 1)
                var result1 = await errorBookService.ReviseErrorAsync(errorItem.Id, isCorrect: true, userId: user.Id);
                Assert.Equal(15, result1.EarnedExp);
                Assert.Equal(5, result1.EarnedCoins);

                var userAfter1 = await context.Users.FindAsync(user.Id);
                Assert.NotNull(userAfter1);
                Assert.Equal(15, userAfter1.Exp);
                Assert.Equal(5, userAfter1.Coins);
                Assert.Equal(1, userAfter1.TotalAnswered);
                Assert.Equal(1, userAfter1.TotalCorrect);

                var itemAfter1 = await context.ErrorItems.FindAsync(errorItem.Id);
                Assert.NotNull(itemAfter1);
                Assert.Equal(1, itemAfter1.RevisionCount);
                Assert.False(itemAfter1.IsMastered);

                // 第二次阶段性复练答对 (RevisionCount 1 -> 2)
                var result2 = await errorBookService.ReviseErrorAsync(errorItem.Id, isCorrect: true, userId: user.Id);
                Assert.Equal(15, result2.EarnedExp);
                Assert.Equal(5, result2.EarnedCoins);

                var userAfter2 = await context.Users.FindAsync(user.Id);
                Assert.NotNull(userAfter2);
                Assert.Equal(30, userAfter2.Exp);
                Assert.Equal(10, userAfter2.Coins);
                Assert.Equal(2, userAfter2.TotalAnswered);
                Assert.Equal(2, userAfter2.TotalCorrect);

                var itemAfter2 = await context.ErrorItems.FindAsync(errorItem.Id);
                Assert.NotNull(itemAfter2);
                Assert.Equal(2, itemAfter2.RevisionCount);
                Assert.False(itemAfter2.IsMastered);

                // 第三次复练答对 (RevisionCount >= 2，触发彻底消灭净化终极奖励 +35 EXP, +25 Coins)
                var result3 = await errorBookService.ReviseErrorAsync(errorItem.Id, isCorrect: true, userId: user.Id);
                Assert.Equal(35, result3.EarnedExp);
                Assert.Equal(25, result3.EarnedCoins);

                var userAfter3 = await context.Users.FindAsync(user.Id);
                Assert.NotNull(userAfter3);
                Assert.Equal(65, userAfter3.Exp);
                Assert.Equal(35, userAfter3.Coins);
                Assert.Equal(1, userAfter3.ResolvedErrorsCount);

                var itemAfter3 = await context.ErrorItems.FindAsync(errorItem.Id);
                Assert.NotNull(itemAfter3);
                Assert.True(itemAfter3.IsMastered);
                Assert.Equal(3, itemAfter3.RevisionCount);
            }
        }

        [Fact]
        public async Task ErrorBook_ReviseError_WithExpAndGoldBuffs_AppliesMultiplier()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "BuffedStudent",
                    Role = UserRole.Student,
                    Exp = 0,
                    Coins = 0,
                    Level = 1,
                    ExpBoostUntil = DateTime.Now.AddHours(2),
                    GoldBoostUntil = DateTime.Now.AddHours(2)
                };
                context.Users.Add(user);

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "物理",
                    Stem = "测定小灯泡电功率的实验原理是？",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "P=UI",
                    Difficulty = 3
                };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = question.Id,
                    UserWrongAnswer = "P=W/t",
                    RevisionCount = 0,
                    IsMastered = false,
                    CreatedAt = DateTime.Now.AddDays(-1)
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                using var httpClient = new HttpClient();
                var sessionService = new UserSessionService(context, httpClient);
                await sessionService.SwitchUserAsync(user.Id);

                var gamificationService = new GamificationService(context, sessionService);
                var errorBookService = new ErrorBookService(context, gamificationService, sessionService);

                // 阶段性复练答对：享受 1.5x 增益 (15*1.5=22.5，Banker's 银行家舍入为 22; 5*1.5=7.5 舍入为 8)
                var result = await errorBookService.ReviseErrorAsync(errorItem.Id, isCorrect: true, userId: user.Id);
                Assert.True(result.ExpBoostActive);
                Assert.True(result.GoldBoostActive);
                Assert.Equal(22, result.EarnedExp);
                Assert.Equal(8, result.EarnedCoins);

                var userInDb = await context.Users.FindAsync(user.Id);
                Assert.NotNull(userInDb);
                Assert.Equal(22, userInDb.Exp);
                Assert.Equal(8, userInDb.Coins);
            }
        }

        #endregion

        #region 2. 成就派发用户归属测试

        [Fact]
        public async Task Gamification_ProcessErrorRevisionRewardAsync_TargetUserId_CreditsSpecifiedAccount()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var parent = new User { Id = Guid.NewGuid(), Username = "ParentUser", Role = UserRole.Parent, Exp = 0, Coins = 0 };
                var child = new User { Id = Guid.NewGuid(), Username = "ChildUser", Role = UserRole.Student, Exp = 10, Coins = 5 };
                context.Users.AddRange(parent, child);

                var question = new Question { Id = Guid.NewGuid(), Subject = "化学", Stem = "水分子式为？", Type = QuestionType.SingleChoice, CorrectAnswer = "H2O" };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = child.Id,
                    QuestionId = question.Id,
                    UserWrongAnswer = "HO",
                    IsMastered = false
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                using var httpClient = new HttpClient();
                var sessionService = new UserSessionService(context, httpClient);
                // 当前 Session 为 Parent
                await sessionService.SwitchUserAsync(parent.Id);

                var gamificationService = new GamificationService(context, sessionService);

                // 家长或后台任务传入 targetUserId: child.Id
                var result = await gamificationService.ProcessErrorRevisionRewardAsync(errorItem.Id, targetUserId: child.Id);
                Assert.Equal(35, result.EarnedExp);
                Assert.Equal(25, result.EarnedCoins);

                // 验证奖励确实进入 Child 账户，Parent 账户保持不变
                var childInDb = await context.Users.FindAsync(child.Id);
                Assert.NotNull(childInDb);
                Assert.Equal(45, childInDb.Exp);
                Assert.Equal(30, childInDb.Coins);

                var parentInDb = await context.Users.FindAsync(parent.Id);
                Assert.NotNull(parentInDb);
                Assert.Equal(0, parentInDb.Exp);
                Assert.Equal(0, parentInDb.Coins);
            }
        }

        #endregion

        #region 3. 主练习模式攻克错题触发错题克星成就

        [Fact]
        public async Task Practice_SubmitAnswer_MasteringError_UnlocksErrorKillerAchievement()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "AceStudent",
                    Role = UserRole.Student,
                    ResolvedErrorsCount = 4, // 已经消灭 4 道
                    Exp = 100,
                    Coins = 50
                };
                context.Users.Add(user);

                var achievement = new Achievement
                {
                    Id = Guid.NewGuid(),
                    Code = "ERROR_KILLER_5",
                    Title = "错题克星",
                    Description = "累计消灭 5 道错题",
                    RewardExp = 100,
                    RewardCoins = 50
                };
                context.Achievements.Add(achievement);

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Subject = "数学",
                    Stem = "计算：(-2)^3",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "-8",
                    Difficulty = 2,
                    BaseExpReward = 10
                };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = question.Id,
                    UserWrongAnswer = "8",
                    RevisionCount = 2, // 已复练 2 次，再次答对达到 3 次完成消灭
                    IsMastered = false
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                using var httpClient = new HttpClient();
                var sessionService = new UserSessionService(context, httpClient);
                await sessionService.SwitchUserAsync(user.Id);

                var gamificationService = new GamificationService(context, sessionService);
                var fakeAiTutor = new FakeAiTutorService();
                var practiceService = new PracticeService(context, gamificationService, sessionService, fakeAiTutor);

                // 在刷题中作为历史错题作答正确
                var result = await practiceService.SubmitAnswerAsync(question, "-8", timeTakenSeconds: 15, currentCombo: 1);
                Assert.True(result.IsCorrect);

                // 验证错题已被消灭
                var itemInDb = await context.ErrorItems.FindAsync(errorItem.Id);
                Assert.NotNull(itemInDb);
                Assert.True(itemInDb.IsMastered);
                Assert.Equal(3, itemInDb.RevisionCount);

                // 验证消灭计数达到 5
                var userInDb = await context.Users.FindAsync(user.Id);
                Assert.NotNull(userInDb);
                Assert.Equal(5, userInDb.ResolvedErrorsCount);

                // 验证成功解锁 ERROR_KILLER_5 成就
                bool hasAchievement = await context.UserAchievements.AnyAsync(ua => ua.UserId == user.Id && ua.Achievement != null && ua.Achievement.Code == "ERROR_KILLER_5");
                Assert.True(hasAchievement);
            }
        }

        #endregion

        #region 4. 作业提交幂等性与完成里程碑奖励

        [Fact]
        public async Task Practice_CompleteHomeworkAssignment_Idempotent_AndAwardsCompletionBonus()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "HomeworkStudent",
                    Role = UserRole.Student,
                    Exp = 0,
                    Coins = 50,
                    Level = 1
                };
                var teacher = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "TeacherA",
                    Role = UserRole.Teacher
                };
                context.Users.AddRange(student, teacher);

                var assignment = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    CreatorUserId = teacher.Id,
                    StudentUserId = student.Id,
                    Title = "周末综合复习作业",
                    Subject = "初中数学",
                    QuestionCount = 5,
                    IsCompleted = false,
                    CreatedAt = DateTime.Now
                };
                context.HomeworkAssignments.Add(assignment);
                await context.SaveChangesAsync();

                using var httpClient = new HttpClient();
                var sessionService = new UserSessionService(context, httpClient);
                await sessionService.SwitchUserAsync(student.Id);

                var gamificationService = new GamificationService(context, sessionService);
                var fakeAiTutor = new FakeAiTutorService();
                var practiceService = new PracticeService(context, gamificationService, sessionService, fakeAiTutor);

                // 第一次完成作业：得分 100 (正确率 80%+，获得基础 +50 EXP, +30 Coins 以及优异奖 +25 EXP, +15 Coins => 总计 +75 EXP, +45 Coins)
                bool firstComplete = await practiceService.CompleteHomeworkAssignmentAsync(assignment.Id, correctCount: 5, totalAnswered: 5, score: 100, studentUserId: student.Id);
                Assert.True(firstComplete);

                var assignmentInDb = await context.HomeworkAssignments.FindAsync(assignment.Id);
                Assert.NotNull(assignmentInDb);
                Assert.True(assignmentInDb.IsCompleted);
                Assert.Equal(100, assignmentInDb.Score);

                var studentInDb = await context.Users.FindAsync(student.Id);
                Assert.NotNull(studentInDb);
                Assert.Equal(75, studentInDb.Exp); // 0 + 75
                Assert.Equal(95, studentInDb.Coins); // 50 + 45

                // 幂等防重测试：再次调用 CompleteHomeworkAssignmentAsync 不应重复发放奖励
                bool secondComplete = await practiceService.CompleteHomeworkAssignmentAsync(assignment.Id, correctCount: 5, totalAnswered: 5, score: 100, studentUserId: student.Id);
                Assert.True(secondComplete);

                var studentAfterSecond = await context.Users.FindAsync(student.Id);
                Assert.NotNull(studentAfterSecond);
                Assert.Equal(75, studentAfterSecond.Exp); // 仍然是 75，未被重复添加
                Assert.Equal(95, studentAfterSecond.Coins); // 仍然是 95
            }
        }

        #endregion

        #region 5. 理科 LaTeX 与数学表达式容错比对

        [Theory]
        [InlineData("\\sqrt{16}", "sqrt(16)", true)]
        [InlineData("\\sqrt{x+1}", "sqrt(x+1)", true)]
        [InlineData("\\sqrt[3]{27}", "root(3,27)", true)]
        [InlineData("x^{2}", "x^2", true)]
        [InlineData("10^{-3}", "10^-3", true)]
        [InlineData("30^\\circ", "30", true)]
        [InlineData("30°", "30度", true)]
        [InlineData("45°", "45", true)]
        public void Practice_CheckFillInBlankMatch_EnrichedLatexMath_EvaluatesCorrectly(string user, string correct, bool expected)
        {
            bool match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, match);
        }

        #endregion

        #region 6. 用户切换账号审批状态安全拦截

        [Fact]
        public async Task UserSession_SwitchUser_PendingOrRejectedUser_ShouldBeBlockedForNonSuperAdmin()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var normalTeacher = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "NormalTeacher",
                    Role = UserRole.Teacher,
                    AccountStatus = UserAccountStatus.Approved
                };
                var pendingStudent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "PendingStudent",
                    Role = UserRole.Student,
                    AccountStatus = UserAccountStatus.PendingApproval
                };
                var rejectedStudent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "RejectedStudent",
                    Role = UserRole.Student,
                    AccountStatus = UserAccountStatus.Rejected,
                    RejectReason = "学籍信息不属实"
                };
                context.Users.AddRange(normalTeacher, pendingStudent, rejectedStudent);
                await context.SaveChangesAsync();

                using var httpClient = new HttpClient();
                var sessionService = new UserSessionService(context, httpClient);
                // 当前 Session 为普通教师
                await sessionService.SwitchUserAsync(normalTeacher.Id);

                // 尝试切换至待审核账号 -> 应被拦截
                var switchPendingResult = await sessionService.SwitchUserAsync(pendingStudent.Id);
                Assert.False(switchPendingResult.Success);
                Assert.Contains("等待超级管理员审批中", switchPendingResult.Message);

                // 尝试切换至已被拒绝账号 -> 应被拦截
                var switchRejectedResult = await sessionService.SwitchUserAsync(rejectedStudent.Id);
                Assert.False(switchRejectedResult.Success);
                Assert.Contains("已被管理员拒绝", switchRejectedResult.Message);
                Assert.Contains("学籍信息不属实", switchRejectedResult.Message);
            }
        }

        #endregion
    }
}
