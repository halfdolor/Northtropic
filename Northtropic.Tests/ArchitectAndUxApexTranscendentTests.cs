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
    /// 系统架构师与用户体验专家双重视角顶级超越测试套件
    /// 覆盖：
    /// 1. 架构安全维度：GetUserPracticeRecordsAsync / GetFavoriteQuestionsAsync / GetSprintQuestionsFromErrorsAsync / CompleteHomeworkAssignmentAsync 的 IDOR 越权阻断与合法鉴权授权
    /// 2. 架构数据完整性维度：HealGamificationInvariantsAsync 领域模型不变式自愈（纠偏 TotalCorrect > TotalAnswered、统计资产负数等异常）
    /// 3. 用户体验与数理引擎维度：LaTeX 黑板粗体数集、加粗向量、集合符号、英语多维缩略语、物理导出单位与化学同位素等价判题
    /// </summary>
    public class ArchitectAndUxApexTranscendentTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxApexTranscendentTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        // ==========================================
        // 架构师视角 1：防越权 (IDOR) 鉴权边界与多租户数据隔离
        // ==========================================

        [Fact]
        public async Task GetUserPracticeRecords_IdorProtection_RejectsUnauthorizedStudent_AllowsAuthorizedAccess()
        {
            // 准备两个互不相关的学生与超级管理员
            var studentA = new User { Id = Guid.NewGuid(), Username = "StudentA", Role = UserRole.Student };
            var studentB = new User { Id = Guid.NewGuid(), Username = "StudentB", Role = UserRole.Student };
            var admin = new User { Id = Guid.NewGuid(), Username = "Admin", Role = UserRole.SuperAdmin };
            var parent = new User { Id = Guid.NewGuid(), Username = "ParentA", Role = UserRole.Parent };

            _context.Users.AddRange(studentA, studentB, admin, parent);
            _context.StudentParentBindings.Add(new StudentParentBinding
            {
                Id = Guid.NewGuid(),
                ParentUserId = parent.Id,
                StudentUserId = studentA.Id,
                CreatedAt = DateTime.Now
            });

            var question = new Question { Id = Guid.NewGuid(), Stem = "数学题", Subject = "数学", CorrectAnswer = "A" };
            _context.Questions.Add(question);
            _context.PracticeRecords.Add(new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = studentA.Id,
                QuestionId = question.Id,
                IsCorrect = true,
                AnsweredAt = DateTime.Now
            });
            await _context.SaveChangesAsync();

            var fakeSession = new FakeUserSessionService { ActiveUser = studentB };
            var service = new PracticeService(_context, new FakeGamificationService(), fakeSession, new FakeAiTutorService());

            // 1. 无关学生 B 尝试读取学生 A 的流水 -> 越权拦截返回空列表
            var recordsB = await service.GetUserPracticeRecordsAsync(studentA.Id, requestorUserId: studentB.Id);
            Assert.Empty(recordsB);

            // 2. 超级管理员访问学生 A 的流水 -> 放行
            var recordsAdmin = await service.GetUserPracticeRecordsAsync(studentA.Id, requestorUserId: admin.Id);
            Assert.Single(recordsAdmin);

            // 3. 绑定的家长访问学生 A 的流水 -> 放行
            var recordsParent = await service.GetUserPracticeRecordsAsync(studentA.Id, requestorUserId: parent.Id);
            Assert.Single(recordsParent);

            // 4. 学生 A 自行读取流水 -> 放行
            var recordsSelf = await service.GetUserPracticeRecordsAsync(studentA.Id, requestorUserId: studentA.Id);
            Assert.Single(recordsSelf);
        }

        [Fact]
        public async Task GetFavoriteQuestions_IdorProtection_RejectsUnauthorizedStudent()
        {
            var studentA = new User { Id = Guid.NewGuid(), Username = "FavStudentA", Role = UserRole.Student };
            var studentB = new User { Id = Guid.NewGuid(), Username = "FavStudentB", Role = UserRole.Student };
            _context.Users.AddRange(studentA, studentB);

            var question = new Question { Id = Guid.NewGuid(), Stem = "化学题", Subject = "化学", CorrectAnswer = "C" };
            _context.Questions.Add(question);
            _context.UserFavorites.Add(new UserFavorite
            {
                Id = Guid.NewGuid(),
                UserId = studentA.Id,
                QuestionId = question.Id,
                CreatedAt = DateTime.Now
            });
            await _context.SaveChangesAsync();

            var fakeSession = new FakeUserSessionService { ActiveUser = studentB };
            var service = new PracticeService(_context, new FakeGamificationService(), fakeSession, new FakeAiTutorService());

            // 学生 B 尝试查看学生 A 的收藏题目 -> 越权阻断返回空列表
            var favsUnauthorized = await service.GetFavoriteQuestionsAsync(studentA.Id, requestorUserId: studentB.Id);
            Assert.Empty(favsUnauthorized);

            // 学生 A 自身访问 -> 允许返回
            var favsSelf = await service.GetFavoriteQuestionsAsync(studentA.Id, requestorUserId: studentA.Id);
            Assert.Single(favsSelf);
        }

        [Fact]
        public async Task GetSprintQuestionsFromErrors_IdorProtection_RejectsUnauthorizedStudent()
        {
            var studentA = new User { Id = Guid.NewGuid(), Username = "ErrStudentA", Role = UserRole.Student };
            var studentB = new User { Id = Guid.NewGuid(), Username = "ErrStudentB", Role = UserRole.Student };
            _context.Users.AddRange(studentA, studentB);

            var question = new Question { Id = Guid.NewGuid(), Stem = "物理错题", Subject = "物理", CorrectAnswer = "B" };
            _context.Questions.Add(question);
            _context.ErrorItems.Add(new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = studentA.Id,
                QuestionId = question.Id,
                IsMastered = false,
                RevisionCount = 2,
                CreatedAt = DateTime.Now
            });
            await _context.SaveChangesAsync();

            var fakeSession = new FakeUserSessionService { ActiveUser = studentB };
            var service = new PracticeService(_context, new FakeGamificationService(), fakeSession, new FakeAiTutorService());

            // 学生 B 尝试从学生 A 的错题本生成特训题目 -> 越权拦截
            var sprintQuestions = await service.GetSprintQuestionsFromErrorsAsync(studentA.Id, "物理", 5, requestorUserId: studentB.Id);
            Assert.Empty(sprintQuestions);

            // 学生 A 自身提取特训 -> 放行
            var sprintQuestionsSelf = await service.GetSprintQuestionsFromErrorsAsync(studentA.Id, "物理", 5, requestorUserId: studentA.Id);
            Assert.Single(sprintQuestionsSelf);
        }

        [Fact]
        public async Task CompleteHomeworkAssignment_IdorProtection_PreventsForgingByOtherStudent()
        {
            var studentA = new User { Id = Guid.NewGuid(), Username = "HwStudentA", Role = UserRole.Student };
            var studentB = new User { Id = Guid.NewGuid(), Username = "HwStudentB", Role = UserRole.Student };
            var teacher = new User { Id = Guid.NewGuid(), Username = "HwTeacher", Role = UserRole.Teacher };
            _context.Users.AddRange(studentA, studentB, teacher);

            var assignment = new HomeworkAssignment
            {
                Id = Guid.NewGuid(),
                CreatorUserId = teacher.Id,
                StudentUserId = studentA.Id,
                Title = "力学专项作业",
                Subject = "物理",
                QuestionCount = 5,
                IsCompleted = false
            };
            _context.HomeworkAssignments.Add(assignment);
            await _context.SaveChangesAsync();

            var fakeSession = new FakeUserSessionService { ActiveUser = studentB };
            var service = new PracticeService(_context, new FakeGamificationService(), fakeSession, new FakeAiTutorService());

            // 学生 B 尝试提交完成学生 A 的专属作业 -> 拦截返回 false
            var result = await service.CompleteHomeworkAssignmentAsync(assignment.Id, 5, 5, 100, studentUserId: studentB.Id);
            Assert.False(result);

            // 学生 A 本人提交完成作业 -> 成功放行
            var resultSelf = await service.CompleteHomeworkAssignmentAsync(assignment.Id, 5, 5, 100, studentUserId: studentA.Id);
            Assert.True(resultSelf);
            var updated = await _context.HomeworkAssignments.FindAsync(assignment.Id);
            Assert.True(updated!.IsCompleted);
        }

        // ==========================================
        // 架构师视角 2：领域不变式自愈守护 (Gamification Invariants Protector)
        // ==========================================

        [Fact]
        public async Task HealGamificationInvariants_HealsTotalCorrectExceedingTotalAnsweredAndNegativeStats()
        {
            var corruptedUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "CorruptedStatsUser",
                Role = UserRole.Student,
                TotalAnswered = 10,
                TotalCorrect = 25, // 异常：正确数居然大于作答总数
                ComboShieldCount = -3, // 异常：护盾数为负
                ResolvedErrorsCount = -2, // 异常：已攻克错题数为负
                CurrentStreak = -1, // 异常：打卡天数为负
                MaxCombo = -4, // 异常：连击为负
                TodayAnsweredCount = -5, // 异常：今日作答为负
                Exp = -50, // 异常：经验值为负
                Coins = -20 // 异常：金币为负
            };
            _context.Users.Add(corruptedUser);
            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);
            int healedCount = await healthService.HealGamificationInvariantsAsync();

            Assert.True(healedCount > 0);

            var healedUser = await _context.Users.FindAsync(corruptedUser.Id);
            Assert.NotNull(healedUser);
            Assert.Equal(10, healedUser.TotalAnswered);
            Assert.Equal(10, healedUser.TotalCorrect); // 已纠正至不超过 TotalAnswered
            Assert.Equal(0, healedUser.ComboShieldCount);
            Assert.Equal(0, healedUser.ResolvedErrorsCount);
            Assert.Equal(0, healedUser.CurrentStreak);
            Assert.Equal(0, healedUser.MaxCombo);
            Assert.Equal(0, healedUser.TodayAnsweredCount);
            Assert.Equal(0, healedUser.Exp);
            Assert.Equal(0, healedUser.Coins);
        }

        // ==========================================
        // 体验专家视角 3：LaTeX 黑板粗体数集、向量粗体与集合符号等价判卷
        // ==========================================

        [Theory]
        [InlineData("\\mathbb{R}", "R")]
        [InlineData("\\mathbf{R}", "R")]
        [InlineData("全体实数", "R")]
        [InlineData("实数集", "R")]
        [InlineData("\\mathbb{Z}", "Z")]
        [InlineData("\\mathbf{Z}", "Z")]
        [InlineData("全体整数", "Z")]
        [InlineData("\\mathbb{N}", "N")]
        [InlineData("自然数集", "N")]
        [InlineData("\\mathbb{Q}", "Q")]
        [InlineData("有理数集", "Q")]
        [InlineData("\\mathbb{C}", "C")]
        [InlineData("复数集", "C")]
        [InlineData("\\mathbf{a}", "a")]
        [InlineData("\\boldsymbol{v}", "v")]
        [InlineData("\\bm{F}", "F")]
        [InlineData("x \\in \\mathbb{R}", "x in R")]
        [InlineData("x 属于 全体实数", "x in R")]
        [InlineData("x \\notin \\mathbb{Z}", "x !in Z")]
        public void CheckFillInBlankMatch_BlackboardBoldAndSets_MatchesEquivalently(string user, string correct)
        {
            bool matched = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(matched, $"User: '{user}' and Correct: '{correct}' should match as equivalent.");
        }

        // ==========================================
        // 体验专家视角 4：英语人称代词、情态/助动词否定缩略等价
        // ==========================================

        [Theory]
        [InlineData("couldn't", "could not")]
        [InlineData("shouldn't", "should not")]
        [InlineData("wouldn't", "would not")]
        [InlineData("mustn't", "must not")]
        [InlineData("haven't", "have not")]
        [InlineData("hasn't", "has not")]
        [InlineData("hadn't", "had not")]
        [InlineData("I'm", "I am")]
        [InlineData("you're", "you are")]
        [InlineData("we're", "we are")]
        [InlineData("they're", "they are")]
        [InlineData("I'll", "I will")]
        [InlineData("we'll", "we will")]
        [InlineData("they'll", "they will")]
        [InlineData("I've", "I have")]
        [InlineData("we've", "we have")]
        [InlineData("that's", "that is")]
        public void CheckFillInBlankMatch_EnglishContractions_MatchesEquivalently(string user, string correct)
        {
            bool matched = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(matched, $"User: '{user}' and Correct: '{correct}' should match as equivalent.");
        }

        // ==========================================
        // 体验专家视角 5：物理国际与导出单位、化学同位素等价匹配
        // ==========================================

        [Theory]
        [InlineData("50赫兹", "50Hz")]
        [InlineData("50赫", "50Hz")]
        [InlineData("10千赫", "10kHz")]
        [InlineData("100兆赫", "100MHz")]
        [InlineData("2特斯拉", "2T")]
        [InlineData("500毫特", "500mT")]
        [InlineData("1.5韦伯", "1.5Wb")]
        [InlineData("10微法", "10uF")]
        [InlineData("1标准大气压", "1atm")]
        [InlineData("760毫米汞柱", "760mmHg")]
        [InlineData("3.5千瓦时", "3.5kWh")]
        [InlineData("3.5度电", "3.5kWh")]
        [InlineData("9.8 m/s²", "9.8 m/s^2")]
        [InlineData("9.8 m·s^-2", "9.8 m/s^2")]
        [InlineData("^{14}C", "C-14")]
        [InlineData("碳-14", "C-14")]
        [InlineData("碳14", "C-14")]
        [InlineData("^{235}U", "U-235")]
        [InlineData("铀-235", "U-235")]
        [InlineData("铀235", "U-235")]
        public void CheckFillInBlankMatch_PhysicsUnitsAndIsotopes_MatchesEquivalently(string user, string correct)
        {
            bool matched = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(matched, $"User: '{user}' and Correct: '{correct}' should match as equivalent.");
        }
    }
}
