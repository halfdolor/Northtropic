using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxApexResilienceTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;
        private readonly HttpClient _httpClient;

        public ArchitectAndUxApexResilienceTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
            _httpClient = new HttpClient();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
            _httpClient.Dispose();
        }

        #region UX Expert Tolerant Grading Tests

        [Theory]
        [InlineData(@"x = 1 +- \sqrt{3}", @"x_1 = 1+\sqrt{3}, x_2 = 1-\sqrt{3}")]
        [InlineData(@"1 +- \sqrt{3}", @"{1+\sqrt{3}, 1-\sqrt{3}}")]
        [InlineData(@"+-2", @"2或-2")]
        [InlineData(@"x = +-3", @"x_1=-3, x_2=3")]
        [InlineData(@"x = 2 +- 1", @"x=3 或 x=1")]
        public void PlusMinusEquationRoots_ShouldMatchEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.True(reason.Contains("方程") || reason.Contains("等价"));
        }

        [Theory]
        [InlineData(@"\sin^2(x)", @"(\sin x)^2")]
        [InlineData(@"\sin^2 x", @"(sin(x))^2")]
        [InlineData(@"\cos^2 x", @"(cos x)^2")]
        [InlineData(@"\tan^2(x)", @"(\tan(x))^2")]
        public void TrigonometricPower_ShouldMatchEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("三角函数幂次", reason);
        }

        [Theory]
        [InlineData("充要条件", "充分必要条件")]
        [InlineData("充分且必要条件", "充要条件")]
        [InlineData("充分不必要条件", "充分非必要条件")]
        [InlineData("必要不充分条件", "必要非充分条件")]
        [InlineData("既不充分也不必要条件", "既非充分又非必要条件")]
        public void PropositionLogic_ShouldMatchEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("命题充分必要条件", reason);
        }

        [Theory]
        [InlineData("单调增加", "单调递增")]
        [InlineData("递增", "单调递增")]
        [InlineData("单调减少", "单调递减")]
        [InlineData("递减", "单调递减")]
        [InlineData("是偶函数", "偶函数")]
        [InlineData("为奇函数", "奇函数")]
        public void MonotonicityAndParity_ShouldMatchEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("函数单调性/奇偶性", reason);
        }

        [Theory]
        [InlineData("72千米/小时", "72km/h")]
        [InlineData("72千米每小时", "72km/h")]
        [InlineData("100千赫", "100kHz")]
        [InlineData("10kΩ", @"10\text{k}\Omega")]
        [InlineData("Fe³⁺", "Fe^{3+}")]
        [InlineData("SO₄²⁻", "SO4^{2-}")]
        [InlineData("OH⁻", "OH^-")]
        [InlineData("N2 + 3H2 ⇌ 2NH3", "N2 + 3H2 <=> 2NH3")]
        public void CompositeUnitsAndChemicalIons_ShouldMatchEquivalently(string userAns, string correctAns)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}'");
        }

        #endregion

        #region System Architect IDOR & Security Tests

        [Fact]
        public async Task ErrorBookService_IDOR_ShouldPreventUnauthorizedAccess()
        {
            var studentA = new User { Id = Guid.NewGuid(), Username = "studentA_sec", Role = UserRole.Student };
            var studentB = new User { Id = Guid.NewGuid(), Username = "studentB_sec", Role = UserRole.Student };
            var superAdmin = new User { Id = Guid.NewGuid(), Username = "adminUser_sec", Role = UserRole.SuperAdmin };
            var parent = new User { Id = Guid.NewGuid(), Username = "parentUser_sec", Role = UserRole.Parent };

            var question = new Question { Id = Guid.NewGuid(), Stem = "Stem1", Subject = "数学", Category = "代数" };
            var errorItemB = new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = studentB.Id,
                QuestionId = question.Id,
                IsMastered = true,
                RevisionCount = 3,
                CreatedAt = DateTime.Now.AddDays(-5)
            };

            _context.Users.AddRange(studentA, studentB, superAdmin, parent);
            _context.Questions.Add(question);
            _context.ErrorItems.Add(errorItemB);
            await _context.SaveChangesAsync();

            var sessionService = new UserSessionService(_context, _httpClient, null);
            await sessionService.SwitchUserAsync(studentA.Id);

            var gamificationService = new GamificationService(_context, sessionService);
            var errorBookService = new ErrorBookService(_context, gamificationService, sessionService, null);

            // 1. Student A tries to clear Student B's mastered errors -> blocked (0 cleared)
            int clearedByUnauthorized = await errorBookService.ClearMasteredErrorsAsync(studentB.Id);
            Assert.Equal(0, clearedByUnauthorized);
            Assert.True(await _context.ErrorItems.AnyAsync(e => e.Id == errorItemB.Id));

            // 2. Student A tries to query Student B's review queue -> blocked (empty list)
            var queue = await errorBookService.GetEbbinghausReviewQueueAsync(studentB.Id);
            Assert.Empty(queue);

            // 3. Student A tries to get Student B's error item by question -> blocked (null)
            var item = await errorBookService.GetErrorItemByQuestionAsync(studentB.Id, question.Id);
            Assert.Null(item);

            // 4. SuperAdmin can access Student B's error item
            await sessionService.SwitchUserAsync(superAdmin.Id);
            var adminItem = await errorBookService.GetErrorItemByQuestionAsync(studentB.Id, question.Id);
            Assert.NotNull(adminItem);

            // 5. Bound parent can access Student B's error item
            _context.StudentParentBindings.Add(new StudentParentBinding
            {
                Id = Guid.NewGuid(),
                ParentUserId = parent.Id,
                StudentUserId = studentB.Id,
                CreatedAt = DateTime.Now
            });
            await _context.SaveChangesAsync();

            await sessionService.SwitchUserAsync(parent.Id);
            var parentItem = await errorBookService.GetErrorItemByQuestionAsync(studentB.Id, question.Id);
            Assert.NotNull(parentItem);
        }

        [Fact]
        public async Task PracticeService_Favorites_IDOR_ShouldPreventUnauthorizedManipulation()
        {
            var studentA = new User { Id = Guid.NewGuid(), Username = "studentA_fav_sec", Role = UserRole.Student };
            var studentB = new User { Id = Guid.NewGuid(), Username = "studentB_fav_sec", Role = UserRole.Student };
            var question = new Question { Id = Guid.NewGuid(), Stem = "Stem Fav", Subject = "物理", Category = "力学" };

            _context.Users.AddRange(studentA, studentB);
            _context.Questions.Add(question);
            await _context.SaveChangesAsync();

            var sessionService = new UserSessionService(_context, _httpClient, null);
            await sessionService.SwitchUserAsync(studentA.Id);

            var gamificationService = new GamificationService(_context, sessionService);
            var practiceService = new PracticeService(_context, gamificationService, sessionService, null!, null);

            // 1. Student A tries to toggle Student B's favorite -> blocked (returns false, no favorite created)
            bool toggled = await practiceService.ToggleFavoriteAsync(studentB.Id, question.Id, "Hacked favorite");
            Assert.False(toggled);
            Assert.False(await _context.UserFavorites.AnyAsync(f => f.UserId == studentB.Id));

            // 2. Student B favorites their own question
            await sessionService.SwitchUserAsync(studentB.Id);
            bool studentBToggled = await practiceService.ToggleFavoriteAsync(studentB.Id, question.Id, "Student B Note");
            Assert.True(studentBToggled);
            Assert.True(await _context.UserFavorites.AnyAsync(f => f.UserId == studentB.Id));

            // 3. Student A tries to read Student B's favorites -> blocked (returns empty list)
            await sessionService.SwitchUserAsync(studentA.Id);
            var studentBFavsForA = await practiceService.GetFavoriteQuestionsAsync(studentB.Id);
            Assert.Empty(studentBFavsForA);

            bool isFavForA = await practiceService.IsFavoriteAsync(studentB.Id, question.Id);
            Assert.False(isFavForA);
        }

        [Fact]
        public async Task SystemHealthService_MaintenancePlanEvaluationAndExecution_ShouldSucceed()
        {
            var healthService = new SystemHealthService(_context);

            // 1. Evaluate maintenance plan
            var plan = await healthService.EvaluateAdaptiveMaintenancePlanAsync();
            Assert.NotNull(plan);
            Assert.False(string.IsNullOrWhiteSpace(plan.UrgencyLevel));
            Assert.NotNull(plan.ActionReasons);

            // 2. Execute maintenance plan
            var result = await healthService.ExecuteAdaptiveMaintenancePlanAsync();
            Assert.NotNull(result);
            Assert.True(result.IntegrityVerified);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));

            // 3. Telemetry summary check
            var telemetry = healthService.GetArchitectureTelemetrySummary();
            Assert.NotNull(telemetry);
            Assert.True(telemetry.ReliabilityScore >= 0 && telemetry.ReliabilityScore <= 100);
        }

        #endregion
    }
}
