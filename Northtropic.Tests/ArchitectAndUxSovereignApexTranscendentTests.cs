using System;
using System.Collections.Generic;
using System.IO;
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
    public class ArchitectAndUxSovereignApexTranscendentTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;
        private readonly SimpleTestDbContextFactory _factory;
        private readonly SystemHealthService _healthService;

        public ArchitectAndUxSovereignApexTranscendentTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var builder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection);

            _context = new AppDbContext(builder.Options);
            _context.Database.EnsureCreated();

            _factory = new SimpleTestDbContextFactory(builder.Options);
            _healthService = new SystemHealthService(_context, _factory);
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Fact]
        public async Task QuestionDeduplication_ShouldMergeRecordsAndPurgeDuplicates()
        {
            // Arrange
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "test_student",
                Exp = 100,
                Coins = 50,
                Level = 2
            };
            _context.Users.Add(user);

            var q1 = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "已知函数 $f(x) = x^2$，求其导函数 $f'(x)$。",
                Type = QuestionType.SingleChoice,
                Subject = "高等数学",
                Category = "导数计算",
                CorrectAnswer = "2x",
                StandardAnalysis = "利用幂函数求导公式可得导数为 2x",
                IsPublic = false,
                CreatedAt = DateTime.UtcNow.AddDays(-10)
            };

            var q2 = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "已知函数 $f(x) = x^2$，求其导函数 $f'(x)$。",
                Type = QuestionType.SingleChoice,
                Subject = "高等数学",
                Category = "导数计算",
                CorrectAnswer = "2x",
                StandardAnalysis = "利用幂函数求导公式可得导数为 2x",
                IsPublic = true, // Public gets priority for master
                CreatedAt = DateTime.UtcNow.AddDays(-2)
            };

            _context.Questions.AddRange(q1, q2);

            var pr1 = new PracticeRecord
            {
                Id = Guid.NewGuid(),
                QuestionId = q1.Id,
                UserId = user.Id,
                IsCorrect = true,
                AnsweredAt = DateTime.UtcNow.AddDays(-5)
            };
            _context.PracticeRecords.Add(pr1);

            var err1 = new ErrorItem
            {
                Id = Guid.NewGuid(),
                QuestionId = q1.Id,
                UserId = user.Id,
                RevisionCount = 1,
                IsMastered = false,
                CreatedAt = DateTime.UtcNow.AddDays(-4)
            };
            var err2 = new ErrorItem
            {
                Id = Guid.NewGuid(),
                QuestionId = q2.Id,
                UserId = user.Id,
                RevisionCount = 0,
                IsMastered = false,
                CreatedAt = DateTime.UtcNow.AddDays(-1)
            };
            _context.ErrorItems.AddRange(err1, err2);

            var fav1 = new UserFavorite
            {
                Id = Guid.NewGuid(),
                QuestionId = q1.Id,
                UserId = user.Id,
                CreatedAt = DateTime.UtcNow.AddDays(-3)
            };
            _context.UserFavorites.Add(fav1);

            var log1 = new LlmGenerationLog
            {
                Id = Guid.NewGuid(),
                QuestionId = q1.Id,
                ModelName = "qwen2.5-72b",
                Subject = "高等数学",
                Category = "导数计算",
                GeneratedAt = DateTime.UtcNow.AddDays(-2)
            };
            _context.LlmGenerationLogs.Add(log1);

            await _context.SaveChangesAsync();

            // Act 1: Audit
            var audit = await _healthService.AuditDataIntegrityAsync();
            Assert.True(audit.DuplicateQuestionsCount >= 1, "Audit should detect at least 1 duplicate question.");

            // Act 2: Deduplicate
            var dedupResult = await _healthService.DeduplicateQuestionsAsync();

            // Assert
            Assert.True(dedupResult.Success);
            Assert.Equal(1, dedupResult.DuplicateGroupsDetected);
            Assert.Equal(1, dedupResult.DuplicateQuestionsPurged);
            Assert.Equal(1, dedupResult.ReassignedPracticeRecordsCount);
            Assert.Equal(1, dedupResult.ReassignedErrorItemsCount);
            Assert.Equal(1, dedupResult.ReassignedFavoritesCount);

            // Re-query database to verify state
            using var verifyDb = _factory.CreateDbContext();
            var remainingQuestions = await verifyDb.Questions.ToListAsync();
            Assert.Single(remainingQuestions);
            Assert.Equal(q2.Id, remainingQuestions[0].Id); // q2 is public master
            Assert.True(remainingQuestions[0].IsPublic);

            var reassignedRecord = await verifyDb.PracticeRecords.FirstOrDefaultAsync();
            Assert.NotNull(reassignedRecord);
            Assert.Equal(q2.Id, reassignedRecord.QuestionId);

            var remainingErrors = await verifyDb.ErrorItems.ToListAsync();
            Assert.Single(remainingErrors);
            Assert.Equal(q2.Id, remainingErrors[0].QuestionId);
            Assert.Equal(err2.RevisionCount + err1.RevisionCount + 1, remainingErrors[0].RevisionCount);

            var remainingFav = await verifyDb.UserFavorites.FirstOrDefaultAsync();
            Assert.NotNull(remainingFav);
            Assert.Equal(q2.Id, remainingFav.QuestionId);

            var remainingLog = await verifyDb.LlmGenerationLogs.FirstOrDefaultAsync();
            Assert.NotNull(remainingLog);
            Assert.Equal(q2.Id, remainingLog.QuestionId);
        }

        [Fact]
        public async Task GamificationInvariants_ShouldAuditAndSelfHealBalancesAndLevels()
        {
            // Arrange
            var invalidUser1 = new User
            {
                Id = Guid.NewGuid(),
                Username = "negative_user",
                Exp = -50,
                Coins = -20,
                Level = 0
            };
            var desyncedUser2 = new User
            {
                Id = Guid.NewGuid(),
                Username = "high_exp_low_level",
                Exp = 8000,
                Coins = 100,
                Level = 1
            };
            _context.Users.AddRange(invalidUser1, desyncedUser2);
            await _context.SaveChangesAsync();

            // Act 1: Audit
            var audit = await _healthService.AuditDataIntegrityAsync();
            Assert.True(audit.UsersWithInvalidBalancesCount >= 2);

            // Act 2: Heal
            var healedCount = await _healthService.HealGamificationInvariantsAsync();
            Assert.True(healedCount >= 2);

            // Assert
            using var verifyDb = _factory.CreateDbContext();
            var u1 = await verifyDb.Users.FindAsync(invalidUser1.Id);
            Assert.NotNull(u1);
            Assert.Equal(0, u1.Exp);
            Assert.Equal(0, u1.Coins);
            Assert.Equal(1, u1.Level);

            var u2 = await verifyDb.Users.FindAsync(desyncedUser2.Id);
            Assert.NotNull(u2);
            Assert.True(u2.Level > 1, "Level should be recalibrated to reflect 8000 Exp.");
        }

        [Theory]
        [InlineData("1 m^3", "1000 dm^3")]
        [InlineData("1 m3", "1000 L")]
        [InlineData("1000000 cm^3", "1 m^3")]
        [InlineData("500 mL", "0.5 L")]
        [InlineData("500 cm3", "0.5 L")]
        public void VolumeAndCapacityUnits_ShouldMatchEquivalently(string userAns, string correctAns)
        {
            bool match = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(match, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.Contains("单位", reason);
        }

        [Theory]
        [InlineData("1 mol/L", "1 mol/dm^3")]
        [InlineData("1 mol/L", "1000 mmol/L")]
        [InlineData("1000 mol/m^3", "1 mol/L")]
        [InlineData("2.5 mmol/L", "0.0025 mol/L")]
        public void MolarConcentrationUnits_ShouldMatchEquivalently(string userAns, string correctAns)
        {
            bool match = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(match, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.Contains("单位", reason);
        }

        [Theory]
        [InlineData("1 Wb", "1000 mWb")]
        [InlineData("10 mH", "0.01 H")]
        [InlineData("100 uS", "0.1 mS")]
        public void ElectromagnetismUnits_ShouldMatchEquivalently(string userAns, string correctAns)
        {
            bool match = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(match, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.Contains("单位", reason);
        }

        [Theory]
        [InlineData("Fe^{3+}", "Fe3+")]
        [InlineData("Fe3+", "Fe^{+3}")]
        [InlineData("SO_4^{2-}", "SO42-")]
        [InlineData("SO_4^{2-}", "SO_4^{-2}")]
        [InlineData("Cl^-", "Cl-")]
        [InlineData("Al^{3+}", "Al3+")]
        [InlineData("Ba^{2+}", "Ba2+")]
        [InlineData("CO_3^{2-}", "CO32-")]
        public void ChemicalIonEquivalence_ShouldSupportSuperscriptsSubscriptsAndCharges(string userAns, string correctAns)
        {
            bool match = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(match, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.Contains("化学离子", reason);
        }

        [Theory]
        [InlineData("x^2 + C", "C + x^2")]
        [InlineData("x^2 + c", "x^2 + C")]
        [InlineData("C + x^2", "x^2 + c")]
        [InlineData(@"\frac{1}{2}x^2 + C", @"C + \frac{1}{2}x^2")]
        [InlineData(@"\sin(x) + C", @"C + \sin(x)")]
        [InlineData(@"e^x + C", @"e^x + c")]
        public void CalculusIndefiniteIntegralConstantC_ShouldBeCommutativeAndCaseInsensitive(string userAns, string correctAns)
        {
            bool match = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(match, $"Expected '{userAns}' to match '{correctAns}'");

            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = correctAns };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, userAns);
            Assert.Contains("不定积分", reason);
        }
    }
}
