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
    /// <summary>
    /// 系统架构师与用户体验专家双重视角深度测试套件
    /// 包含：防越权(IDOR)数据隔离、LaTeX数理与符号等价判题、英语缩略语归一、错题净化里程碑反馈
    /// </summary>
    public class ArchitectAndUxZenithSovereignTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxZenithSovereignTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        // ==========================================
        // 架构师视角 1：防越权 (IDOR) 鉴权与租户数据隔离
        // ==========================================

        [Fact]
        public async Task GetPracticeAnalytics_IdorProtection_RejectsUnauthorizedThirdPartyStudent()
        {
            // 准备两个互不相关的学生
            var studentA = new User { Id = Guid.NewGuid(), Username = "StudentA", Role = UserRole.Student };
            var studentB = new User { Id = Guid.NewGuid(), Username = "StudentB", Role = UserRole.Student };
            _context.Users.AddRange(studentA, studentB);

            // 学生A做题记录
            var question = new Question { Id = Guid.NewGuid(), Stem = "勾股定理测试题", Subject = "数学", CorrectAnswer = "A", OptionsJson = "[\"A\",\"B\"]" };
            _context.Questions.Add(question);
            _context.PracticeRecords.Add(new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = studentA.Id,
                QuestionId = question.Id,
                IsCorrect = true,
                AnsweredAt = DateTime.Now,
                EarnedExp = 100,
                EarnedCoins = 50
            });
            await _context.SaveChangesAsync();

            var fakeSession = new FakeUserSessionService { ActiveUser = studentB };
            var service = new PracticeService(_context, new FakeGamificationService(), fakeSession, new FakeAiTutorService());

            // 学生B尝试越权查学生A的做题分析
            var analytics = await service.GetPracticeAnalyticsAsync(studentA.Id, requestorUserId: studentB.Id);

            // 架构师断言：未经授权的越权访问必须返回空数据，防止敏感学习轨迹泄露
            Assert.Equal(0, analytics.TotalAnswered);
            Assert.Equal(0, analytics.TotalEarnedExp);
            Assert.Empty(analytics.RecentRecords);
        }

        [Fact]
        public async Task GetPracticeAnalytics_AuthorizedParent_AllowsAccessToBoundChild()
        {
            var student = new User { Id = Guid.NewGuid(), Username = "ChildStudent", Role = UserRole.Student };
            var parent = new User { Id = Guid.NewGuid(), Username = "LegitParent", Role = UserRole.Parent };
            _context.Users.AddRange(student, parent);
            _context.StudentParentBindings.Add(new StudentParentBinding
            {
                Id = Guid.NewGuid(),
                StudentUserId = student.Id,
                ParentUserId = parent.Id,
                CreatedAt = DateTime.Now
            });

            var question = new Question { Id = Guid.NewGuid(), Stem = "二次函数性质", Subject = "数学", CorrectAnswer = "B", OptionsJson = "[\"A\",\"B\"]" };
            _context.Questions.Add(question);
            _context.PracticeRecords.Add(new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = student.Id,
                QuestionId = question.Id,
                IsCorrect = true,
                AnsweredAt = DateTime.Now,
                EarnedExp = 80,
                EarnedCoins = 20
            });
            await _context.SaveChangesAsync();

            var fakeSession = new FakeUserSessionService { ActiveUser = parent };
            var service = new PracticeService(_context, new FakeGamificationService(), fakeSession, new FakeAiTutorService());

            // 绑定的家长查询自己孩子的做题分析
            var analytics = await service.GetPracticeAnalyticsAsync(student.Id, requestorUserId: parent.Id);

            // 架构师断言：合法绑定的家长拥有查阅权限
            Assert.Equal(1, analytics.TotalAnswered);
            Assert.Equal(80, analytics.TotalEarnedExp);
            Assert.Single(analytics.RecentRecords);
        }

        [Fact]
        public async Task GetHomeworkAssignmentsByStudent_IdorProtection_FiltersUnauthorizedAttackers()
        {
            var studentTarget = new User { Id = Guid.NewGuid(), Username = "StudentTarget", Role = UserRole.Student };
            var studentAttacker = new User { Id = Guid.NewGuid(), Username = "StudentAttacker", Role = UserRole.Student };
            var teacher = new User { Id = Guid.NewGuid(), Username = "MathTeacher", Role = UserRole.Teacher };
            _context.Users.AddRange(studentTarget, studentAttacker, teacher);

            _context.HomeworkAssignments.Add(new HomeworkAssignment
            {
                Id = Guid.NewGuid(),
                Title = "期中冲刺作业",
                StudentUserId = studentTarget.Id,
                CreatorUserId = teacher.Id,
                Subject = "数学",
                IsCompleted = false,
                CreatedAt = DateTime.Now
            });
            await _context.SaveChangesAsync();

            var fakeSession = new FakeUserSessionService { ActiveUser = studentAttacker };
            var service = new PracticeService(_context, new FakeGamificationService(), fakeSession, new FakeAiTutorService());

            // 攻击者尝试获取目标学生的作业清单
            var result = await service.GetHomeworkAssignmentsByStudentAsync(studentTarget.Id, requestorUserId: studentAttacker.Id);

            // 架构师断言：越权访问返回空列表
            Assert.Empty(result);

            // 目标学生本人查询
            fakeSession.ActiveUser = studentTarget;
            var ownResult = await service.GetHomeworkAssignmentsByStudentAsync(studentTarget.Id, requestorUserId: studentTarget.Id);
            Assert.Single(ownResult);
            Assert.Equal("期中冲刺作业", ownResult[0].Title);
        }

        // ==========================================
        // 架构师视角 2：LaTeX数理与符号等价判题引擎扩展
        // ==========================================

        [Theory]
        [InlineData("\\frac{1}{2}", 0.5)]
        [InlineData("\\dfrac{3}{4}", 0.75)]
        [InlineData("-\\frac{5}{2}", -2.5)]
        [InlineData("+\\tfrac{1}{4}", 0.25)]
        [InlineData("25%", 0.25)]
        [InlineData("12.5%", 0.125)]
        [InlineData("50％", 0.5)] // 全角百分号
        [InlineData("−0.75", -0.75)] // Unicode 减号
        [InlineData("－1.5", -1.5)] // 全角减号
        public void TryParseFractionOrDouble_SupportsDiverseMathExpressions(string input, double expectedVal)
        {
            bool success = PracticeService.TryParseFractionOrDouble(input, out double actualVal);
            Assert.True(success, $"Failed to parse: {input}");
            Assert.Equal(expectedVal, actualVal, 3);
        }

        [Theory]
        [InlineData("don't give up", "do not give up", true)]
        [InlineData("can't stop", "cannot stop", true)]
        [InlineData("it's cool", "it is cool", true)]
        [InlineData("they aren't here", "they are not here", true)]
        [InlineData("hello\u200Bworld", "helloworld", true)] // 零宽字符消除
        [InlineData("25%", "0.25", true)] // 百分比与小数
        [InlineData("−3", "-3", true)] // Unicode 负号
        public void CheckFillInBlankMatch_UnderstandsEnglishContractionsAndZeroWidth(string userAns, string correctAns, bool expectedMatch)
        {
            bool match = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.Equal(expectedMatch, match);
        }

        // ==========================================
        // 用户体验专家视角 3：错题净化里程碑与积极正向激励
        // ==========================================

        [Fact]
        public async Task SubmitAnswer_ErrorPurificationMilestone_CelebratesThirdSuccess()
        {
            var student = new User { Id = Guid.NewGuid(), Username = "JoyfulStudent", Role = UserRole.Student, ResolvedErrorsCount = 0 };
            _context.Users.Add(student);

            var question = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "化学沉淀反应判断题",
                Subject = "化学",
                Category = "盐和化肥",
                CorrectAnswer = "A",
                OptionsJson = "[\"A\",\"B\"]",
                Type = QuestionType.SingleChoice
            };
            _context.Questions.Add(question);

            // 学生错题本中已有该题，且已复练2次 (当前答对将达到第3次，触发净化 Mastered)
            var errorItem = new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = student.Id,
                QuestionId = question.Id,
                IsMastered = false,
                RevisionCount = 2,
                CreatedAt = DateTime.Now.AddDays(-2),
                LastRevisedAt = DateTime.Now.AddDays(-1)
            };
            _context.ErrorItems.Add(errorItem);
            await _context.SaveChangesAsync();

            var fakeSession = new FakeUserSessionService { ActiveUser = student };
            var service = new PracticeService(_context, new FakeGamificationService(), fakeSession, new FakeAiTutorService());

            // 提交正确作答
            var result = await service.SubmitAnswerAsync(question, "A", timeTakenSeconds: 8, currentCombo: 1);

            Assert.True(result.IsCorrect);
            Assert.NotNull(result.StudyPlanProgressFeedback);
            // 验证用户体验专家要求的【已净化】里程碑庆祝提示
            Assert.Contains("已净化", result.StudyPlanProgressFeedback);
            Assert.Contains("3 次复练", result.StudyPlanProgressFeedback);

            // 验证实体状态
            var reloadedError = await _context.ErrorItems.FindAsync(errorItem.Id);
            Assert.NotNull(reloadedError);
            Assert.True(reloadedError.IsMastered);
            Assert.Equal(3, reloadedError.RevisionCount);

            var reloadedUser = await _context.Users.FindAsync(student.Id);
            Assert.NotNull(reloadedUser);
            Assert.Equal(1, reloadedUser.ResolvedErrorsCount);
        }
    }
}
