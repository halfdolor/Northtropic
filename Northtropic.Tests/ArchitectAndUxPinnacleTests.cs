using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxPinnacleTests
    {
        [Fact]
        public async Task MaxCombo_DynamicUpdateAndPersistence_ShouldTrackHighestComboAccurately()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "连击挑战者",
                    Role = UserRole.Student,
                    MaxCombo = 0
                };
                context.Users.Add(student);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = student };
                var gamificationService = new GamificationService(context, fakeUserSession);

                // 1. 连续答对 3 题，从 0 连击起步
                int currentCombo = 0;
                for (int i = 0; i < 3; i++)
                {
                    var result = await gamificationService.ProcessAnswerRewardAsync(isCorrect: true, baseExp: 10, combo: currentCombo);
                    currentCombo = result.CurrentCombo;
                }

                Assert.Equal(3, currentCombo);
                var studentInDb = await context.Users.FindAsync(student.Id);
                Assert.NotNull(studentInDb);
                Assert.Equal(3, studentInDb.MaxCombo);

                // 2. 答错 1 题，连击中断
                var wrongResult = await gamificationService.ProcessAnswerRewardAsync(isCorrect: false, baseExp: 10, combo: currentCombo);
                currentCombo = wrongResult.CurrentCombo;
                Assert.Equal(0, currentCombo);

                studentInDb = await context.Users.FindAsync(student.Id);
                Assert.NotNull(studentInDb);
                Assert.Equal(3, studentInDb.MaxCombo); // 历史最高纪录仍为 3

                // 3. 再次答对 4 题，突破前高达到 4
                for (int i = 0; i < 4; i++)
                {
                    var result = await gamificationService.ProcessAnswerRewardAsync(isCorrect: true, baseExp: 10, combo: currentCombo);
                    currentCombo = result.CurrentCombo;
                }

                Assert.Equal(4, currentCombo);
                studentInDb = await context.Users.FindAsync(student.Id);
                Assert.NotNull(studentInDb);
                Assert.Equal(4, studentInDb.MaxCombo); // 成功突破并持久化为 4
            }
        }

        [Fact]
        public async Task DailyStreak_ConsecutiveDaysAndReset_ShouldPreserveAndIncrementCorrectly()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var studentId = Guid.NewGuid();
                var student = new User
                {
                    Id = studentId,
                    Username = "坚持打卡学员",
                    Role = UserRole.Student,
                    CurrentStreak = 3,
                    LastStudyDate = DateTime.Today.AddDays(-1) // 昨天打卡
                };
                context.Users.Add(student);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = student };
                var gamificationService = new GamificationService(context, fakeUserSession);

                // 1. 今天打卡：连续天数递增为 4
                await gamificationService.UpdateStreakAsync();
                var studentInDb = await context.Users.FindAsync(studentId);
                Assert.NotNull(studentInDb);
                Assert.Equal(4, studentInDb.CurrentStreak);
                Assert.Equal(DateTime.Today, studentInDb.LastStudyDate.Date);

                // 2. 同一天内再次刷新/打卡：天数保持 4，不重复累加
                await gamificationService.UpdateStreakAsync();
                studentInDb = await context.Users.FindAsync(studentId);
                Assert.NotNull(studentInDb);
                Assert.Equal(4, studentInDb.CurrentStreak);

                // 3. 模拟跨越 2 天未打卡 (断签重置)
                studentInDb.LastStudyDate = DateTime.Today.AddDays(-3);
                await context.SaveChangesAsync();

                await gamificationService.UpdateStreakAsync();
                studentInDb = await context.Users.FindAsync(studentId);
                Assert.NotNull(studentInDb);
                Assert.Equal(1, studentInDb.CurrentStreak); // 自动重置为 1
                Assert.Equal(DateTime.Today, studentInDb.LastStudyDate.Date);
            }
        }

        [Fact]
        public async Task PracticeService_SubmitAnswerAsync_HistoryWrongQuestion_ShouldSyncErrorItemAndMastery()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var studentId = Guid.NewGuid();
                var student = new User
                {
                    Id = studentId,
                    Username = "抗遗忘先锋",
                    Role = UserRole.Student,
                    ResolvedErrorsCount = 0
                };
                context.Users.Add(student);

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "关于机械效率的计算题",
                    OptionsJson = "[\"A. 60%\", \"B. 80%\", \"C. 90%\", \"D. 100%\"]",
                    CorrectAnswer = "B",
                    Subject = "初中物理",
                    Category = "机械效率",
                    Difficulty = 3,
                    BaseExpReward = 20
                };
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = student };
                var gamificationService = new GamificationService(context, fakeUserSession);
                var fakeAiTutor = new FakeAiTutorService();
                var practiceService = new PracticeService(context, gamificationService, fakeUserSession, fakeAiTutor);

                // 1. 初次作答：答错加入错题本
                var wrongRes = await practiceService.SubmitAnswerAsync(question, "A", 15, 0);
                Assert.False(wrongRes.IsCorrect);

                var errorItem = await context.ErrorItems.FirstOrDefaultAsync(e => e.UserId == studentId && e.QuestionId == question.Id);
                Assert.NotNull(errorItem);
                Assert.False(errorItem.IsMastered);
                Assert.Equal(0, errorItem.RevisionCount);

                // 2. 第一次复练答对：RevisionCount -> 1
                var correctRes1 = await practiceService.SubmitAnswerAsync(question, "B", 10, 0);
                Assert.True(correctRes1.IsCorrect);
                errorItem = await context.ErrorItems.FirstOrDefaultAsync(e => e.UserId == studentId && e.QuestionId == question.Id);
                Assert.NotNull(errorItem);
                Assert.Equal(1, errorItem.RevisionCount);
                Assert.False(errorItem.IsMastered);
                Assert.NotNull(errorItem.LastRevisedAt);

                // 3. 第二次复练答对：RevisionCount -> 2
                var correctRes2 = await practiceService.SubmitAnswerAsync(question, "B", 10, 1);
                Assert.True(correctRes2.IsCorrect);
                errorItem = await context.ErrorItems.FirstOrDefaultAsync(e => e.UserId == studentId && e.QuestionId == question.Id);
                Assert.NotNull(errorItem);
                Assert.Equal(2, errorItem.RevisionCount);
                Assert.False(errorItem.IsMastered);

                // 4. 第三次复练答对：RevisionCount -> 3，判定为彻底掌握，ResolvedErrorsCount 递增
                var correctRes3 = await practiceService.SubmitAnswerAsync(question, "B", 8, 2);
                Assert.True(correctRes3.IsCorrect);
                errorItem = await context.ErrorItems.FirstOrDefaultAsync(e => e.UserId == studentId && e.QuestionId == question.Id);
                Assert.NotNull(errorItem);
                Assert.Equal(3, errorItem.RevisionCount);
                Assert.True(errorItem.IsMastered);

                var refreshedStudent = await context.Users.FindAsync(studentId);
                Assert.NotNull(refreshedStudent);
                Assert.Equal(1, refreshedStudent.ResolvedErrorsCount);
            }
        }

        [Fact]
        public async Task HomeworkAssignment_Create_UnauthorizedParent_ShouldThrowSecurityException()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var parentId = Guid.NewGuid();
                var victimStudentId = Guid.NewGuid();

                var parent = new User { Id = parentId, Username = "隔壁家长", Role = UserRole.Parent };
                var victimStudent = new User { Id = victimStudentId, Username = "未绑定学员", Role = UserRole.Student };
                context.Users.AddRange(parent, victimStudent);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = parent };
                var gamificationService = new GamificationService(context, fakeUserSession);
                var practiceService = new PracticeService(context, gamificationService, fakeUserSession, new FakeAiTutorService());

                // 未绑定关系时越权布置作业，应当抛出安全拦截异常
                await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                {
                    await practiceService.CreateHomeworkAssignmentAsync(
                        creatorUserId: parentId,
                        studentUserId: victimStudentId,
                        title: "恶意作业",
                        subject: "初中物理",
                        category: "全部分类",
                        questionCount: 5,
                        difficulty: 3,
                        deadline: DateTime.Now.AddDays(1),
                        note: "必须完成！"
                    );
                });

                // 建立正式监护绑定
                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parentId,
                    StudentUserId = victimStudentId,
                    RelationType = "监护人"
                });
                await context.SaveChangesAsync();

                // 绑定后正常布置成功
                var assignment = await practiceService.CreateHomeworkAssignmentAsync(
                    creatorUserId: parentId,
                    studentUserId: victimStudentId,
                    title: "爱心巩固作业",
                    subject: "初中物理",
                    category: "全部分类",
                    questionCount: 5,
                    difficulty: 3,
                    deadline: DateTime.Now.AddDays(1),
                    note: "加油！"
                );

                Assert.NotNull(assignment);
                Assert.Equal("爱心巩固作业", assignment.Title);
            }
        }

        [Fact]
        public async Task HomeworkAssignment_DeleteAndComplete_ShouldEnforceSecurityAndIntegrity()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var parentId = Guid.NewGuid();
                var studentId = Guid.NewGuid();
                var strangerId = Guid.NewGuid();

                var parent = new User { Id = parentId, Username = "正牌家长", Role = UserRole.Parent };
                var student = new User { Id = studentId, Username = "学员小王", Role = UserRole.Student };
                var stranger = new User { Id = strangerId, Username = "路人甲", Role = UserRole.Student };
                context.Users.AddRange(parent, student, stranger);

                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parentId,
                    StudentUserId = studentId,
                    RelationType = "监护人"
                });
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = parent };
                var gamificationService = new GamificationService(context, fakeUserSession);
                var practiceService = new PracticeService(context, gamificationService, fakeUserSession, new FakeAiTutorService());

                var assignment = await practiceService.CreateHomeworkAssignmentAsync(
                    parentId, studentId, "期末冲刺作业", "初中物理", "全部分类", 10, 3, DateTime.Now.AddDays(2), "好好做！"
                );

                // 1. 路人甲试图删除作业 -> 拦截失败
                var deleteResult = await practiceService.DeleteHomeworkAssignmentAsync(assignment.Id, requestorUserId: strangerId);
                Assert.False(deleteResult);

                // 2. 学员完成作业
                var completeResult = await practiceService.CompleteHomeworkAssignmentAsync(assignment.Id, 9, 10, 90, studentUserId: studentId);
                Assert.True(completeResult);

                var refreshedAssignment = await practiceService.GetHomeworkAssignmentByIdAsync(assignment.Id);
                Assert.NotNull(refreshedAssignment);
                Assert.True(refreshedAssignment.IsCompleted);
                Assert.Equal(90, refreshedAssignment.Score);
                Assert.Equal(9, refreshedAssignment.CorrectCount);

                // 3. 家长合法删除作业
                var parentDeleteResult = await practiceService.DeleteHomeworkAssignmentAsync(assignment.Id, requestorUserId: parentId);
                Assert.True(parentDeleteResult);

                var nullAssignment = await practiceService.GetHomeworkAssignmentByIdAsync(assignment.Id);
                Assert.Null(nullAssignment);
            }
        }

        [Fact]
        public async Task QuestionManagement_ApproveAndPublish_ShouldRewardExpAndTriggerLevelUp()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var adminId = Guid.NewGuid();
                var authorStudentId = Guid.NewGuid();

                var admin = new User { Id = adminId, Username = "教研审核员", Role = UserRole.Teacher };
                var authorStudent = new User
                {
                    Id = authorStudentId,
                    Username = "题目创作者",
                    Role = UserRole.Student,
                    Level = 1,
                    Exp = 80, // Lv.1 升级需要 100 经验，获得 50 后将达到 130，应当触发升至 Lv.2 (剩余 30)
                    Coins = 10
                };
                context.Users.AddRange(admin, authorStudent);

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "自创精品力学题",
                    CreatedByUserId = authorStudentId,
                    IsPublic = false,
                    PublishStatus = PublishStatusEnum.Pending
                };
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var questionService = new QuestionManagementService(context);

                // 审核通过试题
                var approved = await questionService.ApprovePublishRequestAsync(question.Id, adminId);
                Assert.True(approved);

                var refreshedAuthor = await context.Users.FindAsync(authorStudentId);
                Assert.NotNull(refreshedAuthor);
                Assert.Equal(2, refreshedAuthor.Level); // 成功跃升至 Lv.2
                Assert.Equal(30, refreshedAuthor.Exp); // 剩余 30 经验
                Assert.Equal(30, refreshedAuthor.Coins); // +20 金币
            }
        }
    }
}
