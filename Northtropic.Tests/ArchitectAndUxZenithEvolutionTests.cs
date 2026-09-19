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
    public class ArchitectAndUxZenithEvolutionTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public class FakeStudentEvolutionService : IStudentEvolutionService
        {
            public Task<List<KnowledgePointMasteryDto>> GetMasteryOverviewAsync(Guid userId, string? subject = null)
                => Task.FromResult(new List<KnowledgePointMasteryDto>());

            public Task<EvolutionDiagnosisReportDto> GenerateDiagnosisReportAsync(Guid userId)
                => Task.FromResult(new EvolutionDiagnosisReportDto { UserId = userId, RecommendedSubject = "数学", RecommendedCategory = "一次函数" });

            public Task<List<KnowledgePointMasteryDto>> GetPendingSpacedReviewNodesAsync(Guid userId)
                => Task.FromResult(new List<KnowledgePointMasteryDto>());

            public Task<List<PrerequisiteTraceWarningDto>> TraceWeakPrerequisitesAsync(Guid userId, string subject, string category)
                => Task.FromResult(new List<PrerequisiteTraceWarningDto>());
        }

        public ArchitectAndUxZenithEvolutionTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        #region 1. 系统架构师：IDOR 横向越权防御深度测试 (ErrorBook & StudyPlan)

        [Fact]
        public async Task ErrorBookService_UpdateErrorReason_UnauthorizedNonOwner_IsBlocked()
        {
            // Arrange
            var student1 = new User { Id = Guid.NewGuid(), Username = "student_owner", Role = UserRole.Student };
            var student2 = new User { Id = Guid.NewGuid(), Username = "student_attacker", Role = UserRole.Student };
            _context.Users.AddRange(student1, student2);

            var question = new Question { Id = Guid.NewGuid(), Stem = "测试试题", Type = QuestionType.SingleChoice, CorrectAnswer = "A" };
            _context.Questions.Add(question);

            var errorItem = new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = student1.Id,
                QuestionId = question.Id,
                ErrorReasonCategory = "概念模糊"
            };
            _context.ErrorItems.Add(errorItem);
            await _context.SaveChangesAsync();

            var gamification = new FakeGamificationService();
            var session = new FakeUserSessionService();
            var service = new ErrorBookService(_context, gamification, session);

            // Act 1: 攻击者 student2 尝试篡改 student1 的错题原因分类
            await service.UpdateErrorReasonAsync(errorItem.Id, "审题不清", requestorUserId: student2.Id);

            // Assert 1: 应被严格拦截，错因仍保持原值
            var freshItem1 = await _context.ErrorItems.FindAsync(errorItem.Id);
            Assert.Equal("概念模糊", freshItem1?.ErrorReasonCategory);

            // Act 2: 正当拥有者 student1 更新错题原因分类
            await service.UpdateErrorReasonAsync(errorItem.Id, "公式记错", requestorUserId: student1.Id);

            // Assert 2: 应当成功放行并持久化
            var freshItem2 = await _context.ErrorItems.FindAsync(errorItem.Id);
            Assert.Equal("公式记错", freshItem2?.ErrorReasonCategory);
        }

        [Fact]
        public async Task ErrorBookService_DeleteErrorItem_UnauthorizedNonOwner_IsBlocked_AdminAllowed()
        {
            // Arrange
            var student = new User { Id = Guid.NewGuid(), Username = "student_alice", Role = UserRole.Student };
            var peer = new User { Id = Guid.NewGuid(), Username = "student_bob", Role = UserRole.Student };
            var admin = new User { Id = Guid.NewGuid(), Username = "admin_master", Role = UserRole.SuperAdmin };
            _context.Users.AddRange(student, peer, admin);

            var question = new Question { Id = Guid.NewGuid(), Stem = "测试试题2", Type = QuestionType.SingleChoice, CorrectAnswer = "B" };
            _context.Questions.Add(question);

            var errorItem = new ErrorItem { Id = Guid.NewGuid(), UserId = student.Id, QuestionId = question.Id };
            _context.ErrorItems.Add(errorItem);
            await _context.SaveChangesAsync();

            var service = new ErrorBookService(_context, new FakeGamificationService(), new FakeUserSessionService());

            // Act 1: 同行学员 peer 尝试删除该错题
            var peerDelete = await service.DeleteErrorItemAsync(errorItem.Id, requestorUserId: peer.Id);
            Assert.False(peerDelete);
            Assert.NotNull(await _context.ErrorItems.FindAsync(errorItem.Id));

            // Act 2: 超级管理员执行统一清理
            var adminDelete = await service.DeleteErrorItemAsync(errorItem.Id, requestorUserId: admin.Id);
            Assert.True(adminDelete);
            Assert.Null(await _context.ErrorItems.FindAsync(errorItem.Id));
        }

        [Fact]
        public async Task ErrorBookService_ParentBinding_AllowsBoundParent_BlocksUnboundParent()
        {
            // Arrange
            var student = new User { Id = Guid.NewGuid(), Username = "student_carol", Role = UserRole.Student };
            var boundParent = new User { Id = Guid.NewGuid(), Username = "parent_dad", Role = UserRole.Parent };
            var strangerParent = new User { Id = Guid.NewGuid(), Username = "parent_stranger", Role = UserRole.Parent };
            _context.Users.AddRange(student, boundParent, strangerParent);

            var binding = new StudentParentBinding
            {
                ParentUserId = boundParent.Id,
                StudentUserId = student.Id,
                RelationType = "父亲"
            };
            _context.StudentParentBindings.Add(binding);

            var question = new Question { Id = Guid.NewGuid(), Stem = "测试试题3", Type = QuestionType.FillInBlank, CorrectAnswer = "42" };
            _context.Questions.Add(question);

            var errorItem = new ErrorItem { Id = Guid.NewGuid(), UserId = student.Id, QuestionId = question.Id, IsMastered = false };
            _context.ErrorItems.Add(errorItem);
            await _context.SaveChangesAsync();

            var gamification = new GamificationService(_context, new FakeUserSessionService());
            var service = new ErrorBookService(_context, gamification, new FakeUserSessionService());

            // Act 1: 陌生家长尝试标记掌握该学员错题
            var strangerReward = await service.MarkErrorAsMasteredAsync(errorItem.Id, requestorUserId: strangerParent.Id);
            Assert.Equal(0, strangerReward.EarnedExp);
            Assert.False((await _context.ErrorItems.FindAsync(errorItem.Id))!.IsMastered);

            // Act 2: 已合法绑定的家长为子女辅导后标记掌握
            var boundReward = await service.MarkErrorAsMasteredAsync(errorItem.Id, requestorUserId: boundParent.Id);
            Assert.True(boundReward.EarnedExp > 0);
            var refreshed = await _context.ErrorItems.FindAsync(errorItem.Id);
            Assert.True(refreshed?.IsMastered);
        }

        [Fact]
        public async Task StudyPlanService_IDOR_BlocksUnauthorizedPeerAndAllowsAuthorizedUsers()
        {
            // Arrange
            var student = new User { Id = Guid.NewGuid(), Username = "student_dan", Role = UserRole.Student };
            var peer = new User { Id = Guid.NewGuid(), Username = "student_eve", Role = UserRole.Student };
            var teacher = new User { Id = Guid.NewGuid(), Username = "teacher_smith", Role = UserRole.Teacher };
            _context.Users.AddRange(student, peer, teacher);

            var plan = new StudyPlan
            {
                Id = Guid.NewGuid(),
                UserId = student.Id,
                Title = "专属提分计划",
                Status = StudyPlanStatus.Active,
                StartDate = DateTime.Now.Date,
                TargetEndDate = DateTime.Now.Date.AddDays(7)
            };
            _context.StudyPlans.Add(plan);
            await _context.SaveChangesAsync();

            var studyPlanService = new StudyPlanService(_context, new FakeStudentEvolutionService());

            // Act & Assert 1: 同级学员 peer 尝试越权查看 student 的学习计划 -> 返回 null
            var peerView = await studyPlanService.GetActivePlanAsync(student.Id, requestorUserId: peer.Id);
            Assert.Null(peerView);

            // Act & Assert 2: 执教老师查看学员计划 -> 正常放行
            var teacherView = await studyPlanService.GetActivePlanAsync(student.Id, requestorUserId: teacher.Id);
            Assert.NotNull(teacherView);
            Assert.Equal(plan.Id, teacherView?.Id);

            // Act & Assert 3: 同级学员尝试越权督促诊断 -> 返回无权访问
            var (nudgeMsg, severity, _) = await studyPlanService.SuperviseAndNudgeAsync(student.Id, requestorUserId: peer.Id);
            Assert.Contains("无权访问", nudgeMsg);
            Assert.Equal("Error", severity);

            // Act & Assert 4: 同级学员尝试越权触发闭环评估 -> 抛出 UnauthorizedAccessException
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                studyPlanService.EvaluateClosedLoopProgressAsync(student.Id, requestorUserId: peer.Id));
        }

        #endregion

        #region 2. 系统架构师：领域不变量自愈引擎测试 (Gamification & HomeworkAssignment)

        [Fact]
        public async Task SystemHealthService_HealGamificationInvariants_ClampsExtremeBounds()
        {
            // Arrange: 构造包含不合理资产上限溢出的用户 (如连续连击数大于总答对数、今日答题数大于总答题数)
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "anomalous_user",
                TotalAnswered = 20,
                TotalCorrect = 10,
                MaxCombo = 999, // 异常：连击数远大于答对总数
                TodayAnsweredCount = 50, // 异常：今日答题数大于历史总答题数
                ResolvedErrorsCount = 30, // 异常：解决错题数大于答对总数
                Level = 1,
                Exp = 100
            };
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);

            // Act
            int healed = await healthService.HealGamificationInvariantsAsync();

            // Assert
            Assert.True(healed > 0);
            var refreshed = await _context.Users.FindAsync(user.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(10, refreshed.MaxCombo);
            Assert.Equal(20, refreshed.TodayAnsweredCount);
            Assert.Equal(10, refreshed.ResolvedErrorsCount);
        }

        [Fact]
        public async Task SystemHealthService_HealHomeworkAssignmentInvariants_NormalizesDomainBounds()
        {
            // Arrange: 构造指标脱节与状态未对齐的家庭作业
            var creator = new User { Id = Guid.NewGuid(), Username = "parent_assigner", Role = UserRole.Parent };
            var student = new User { Id = Guid.NewGuid(), Username = "student_doer", Role = UserRole.Student };
            _context.Users.AddRange(creator, student);

            var assignment = new HomeworkAssignment
            {
                Id = Guid.NewGuid(),
                CreatorUserId = creator.Id,
                StudentUserId = student.Id,
                Title = "错题专项作业",
                QuestionCount = 10,
                TotalAnswered = 15, // 异常：答题数大于总题数
                CorrectCount = 18, // 异常：对题数大于答题数
                AccuracyRate = 0, // 异常：正确率未同步
                Score = 150, // 异常：分数超过 100
                IsCompleted = true,
                CompletedAt = null // 异常：完成但无完成时间戳
            };
            _context.HomeworkAssignments.Add(assignment);
            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);

            // Act
            int healed = await healthService.HealHomeworkAssignmentInvariantsAsync();

            // Assert
            Assert.True(healed > 0);
            var refreshed = await _context.HomeworkAssignments.FindAsync(assignment.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(10, refreshed.TotalAnswered);
            Assert.Equal(10, refreshed.CorrectCount);
            Assert.Equal(100, refreshed.AccuracyRate);
            Assert.Equal(100, refreshed.Score);
            Assert.NotNull(refreshed.CompletedAt);
        }

        #endregion

        #region 3. 用户体验专家：数理化多量纲等价与根号有理化判分体验测试

        [Theory]
        [InlineData("1/sqrt(2)", "sqrt(2)/2", true)]
        [InlineData("sqrt(2)/2", "1/sqrt(2)", true)]
        [InlineData("\\frac{1}{\\sqrt{2}}", "\\frac{\\sqrt{2}}{2}", true)]
        [InlineData("1/\\sqrt{2}", "\\sqrt{2}/2", true)]
        [InlineData("2/\\sqrt{2}", "\\sqrt{2}", true)]
        [InlineData("1/sqrt(3)", "sqrt(3)/3", true)]
        [InlineData("3/sqrt(3)", "sqrt(3)", true)]
        [InlineData("-1/sqrt(2)", "-sqrt(2)/2", true)]
        public void PracticeService_CheckFillInBlankMatch_RadicalRationalization_MatchesEquivalently(string user, string correct, bool expected)
        {
            bool match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, match);
        }

        [Fact]
        public void PracticeService_CheckFillInBlankMatch_PhysicalUnitsMultiDimensional_MatchesEquivalently()
        {
            // 压强 Pa 与 N/m^2
            Assert.True(PracticeService.CheckFillInBlankMatch("100 Pa", "100 N/m^2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("100帕", "100牛/米²"));
            Assert.True(PracticeService.CheckFillInBlankMatch("100000 Pa", "100 kPa"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10^5 Pa", "100 kPa"));

            // 重力加速度 / 引力场强: N/kg 与 m/s^2
            Assert.True(PracticeService.CheckFillInBlankMatch("9.8 N/kg", "9.8 m/s^2"));
            Assert.True(PracticeService.CheckFillInBlankMatch("9.8牛/千克", "9.8米/秒²"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10 N/kg", "10 m/s^2"));

            // 密度换算: g/cm^3 与 kg/m^3
            Assert.True(PracticeService.CheckFillInBlankMatch("1.0 g/cm^3", "1000 kg/m^3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1.0×10^3 kg/m^3", "1.0 g/cm^3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1克/立方厘米", "1000千克/立方米"));
        }

        [Fact]
        public void PracticeService_CheckFillInBlankMatch_ChemicalSynonymsAndContractions_MatchesEquivalently()
        {
            // 化学俗名与矿石名称
            Assert.True(PracticeService.CheckFillInBlankMatch("苛性钾", "KOH"));
            Assert.True(PracticeService.CheckFillInBlankMatch("KOH", "苛性钾"));
            Assert.True(PracticeService.CheckFillInBlankMatch("赤铁矿", "Fe2O3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("磁铁矿", "Fe3O4"));
            Assert.True(PracticeService.CheckFillInBlankMatch("水玻璃", "Na2SiO3"));
            Assert.True(PracticeService.CheckFillInBlankMatch("硅酸钠", "水玻璃"));

            // 英语缩写词智能展开
            Assert.True(PracticeService.CheckFillInBlankMatch("let's go", "let us go"));
            Assert.True(PracticeService.CheckFillInBlankMatch("who's that", "who is that"));
            Assert.True(PracticeService.CheckFillInBlankMatch("we shan't fail", "we shall not fail"));
            Assert.True(PracticeService.CheckFillInBlankMatch("where's my book", "where is my book"));
        }

        #endregion
    }
}
