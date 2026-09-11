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
    public class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    public class StudentEvolutionServiceTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        private readonly FakeGamificationService _fakeGamificationService;
        private readonly StudentEvolutionService _evolutionService;

        public StudentEvolutionServiceTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
            _fakeGamificationService = new FakeGamificationService();
            _evolutionService = new StudentEvolutionService(_context, _fakeGamificationService, new SimpleHttpClientFactory());
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Fact]
        public async Task GetMasteryOverviewAsync_WhenNoRecords_ReturnsInitialZeroMastery()
        {
            // Arrange
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "test_student",
                Grade = "初中二年级",
                Role = UserRole.Student
            };
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // Act
            var result = await _evolutionService.GetMasteryOverviewAsync(user.Id, "数学");

            // Assert
            Assert.NotNull(result);
            foreach (var item in result)
            {
                Assert.Equal(0, item.TotalAnswered);
                Assert.Equal(0, item.MasteryScore);
                Assert.Contains("待探索", item.MasteryLevel);
            }
        }

        [Fact]
        public async Task GetMasteryOverviewAsync_WithAccuracyAndMasteredErrors_CalculatesFormulaCorrectly()
        {
            // Arrange
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "math_student",
                Grade = "初中二年级",
                Role = UserRole.Student
            };
            _context.Users.Add(user);

            var q1 = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "数学",
                Category = "一元一次方程",
                Stem = "x + 1 = 2",
                Type = QuestionType.SingleChoice,
                CorrectAnswer = "A",
                StandardAnalysis = "x = 1",
                OptionsJson = "[\"A. 1\", \"B. 2\"]"
            };
            _context.Questions.Add(q1);

            // 2 正确记录, 平均速度 20 秒 (< 60s, 无速度惩罚)
            _context.PracticeRecords.AddRange(
                new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = q1.Id,
                    Question = q1,
                    IsCorrect = true,
                    TimeTakenSeconds = 20,
                    AnsweredAt = DateTime.Now
                },
                new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = q1.Id,
                    Question = q1,
                    IsCorrect = true,
                    TimeTakenSeconds = 20,
                    AnsweredAt = DateTime.Now
                }
            );

            // 1 个错题，且已被净化攻克 (IsMastered = true) => errorBonus = 15
            _context.ErrorItems.Add(new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = q1.Id,
                Question = q1,
                IsMastered = true,
                CreatedAt = DateTime.Now.AddDays(-1)
            });

            await _context.SaveChangesAsync();

            // Act
            var overview = await _evolutionService.GetMasteryOverviewAsync(user.Id, "数学");
            var item = overview.FirstOrDefault(k => k.Category == "一元一次方程");

            // Assert
            Assert.NotNull(item);
            Assert.Equal(2, item.TotalAnswered);
            Assert.Equal(2, item.TotalCorrect);
            Assert.Equal(100.0, item.AccuracyRate);
            // Accuracy 100 * 0.85 = 85 + errorBonus 15 = 100
            Assert.Equal(100, item.MasteryScore);
            Assert.Equal("🔥 融会贯通", item.MasteryLevel);
        }

        [Fact]
        public async Task GetMasteryOverviewAsync_WhenSlowSpeed_AppliesFivePointPenalty()
        {
            // Arrange
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "slow_student",
                Grade = "初中二年级",
                Role = UserRole.Student
            };
            _context.Users.Add(user);

            var q = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "物理",
                Category = "声现象",
                Stem = "声音的传播",
                Type = QuestionType.SingleChoice,
                CorrectAnswer = "A",
                StandardAnalysis = "解析",
                OptionsJson = "[\"A\", \"B\"]"
            };
            _context.Questions.Add(q);

            // 平均耗时 90 秒 (> 60 秒触发扣 5 分)
            _context.PracticeRecords.Add(new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = q.Id,
                Question = q,
                IsCorrect = true,
                TimeTakenSeconds = 90,
                AnsweredAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            // Act
            var overview = await _evolutionService.GetMasteryOverviewAsync(user.Id, "物理");
            var item = overview.FirstOrDefault(k => k.Category == "声现象");

            // Assert: Accuracy 100% * 0.85 = 85 + default errorBonus(10) = 95 - penalty(5) = 90
            Assert.NotNull(item);
            Assert.Equal(90, item.MasteryScore);
        }

        [Fact]
        public async Task GenerateDiagnosisReportAsync_PrioritizesWeakestPracticedCategory()
        {
            // Arrange
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "diag_student",
                Grade = "初中二年级",
                Role = UserRole.Student
            };
            _context.Users.Add(user);

            var qWeak = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "数学",
                Category = "勾股定理",
                Stem = "求斜边",
                Type = QuestionType.SingleChoice,
                CorrectAnswer = "A",
                StandardAnalysis = "a^2+b^2=c^2",
                OptionsJson = "[\"A\", \"B\"]"
            };

            var qStrong = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "数学",
                Category = "整式的乘除",
                Stem = "因式分解",
                Type = QuestionType.SingleChoice,
                CorrectAnswer = "A",
                StandardAnalysis = "解析",
                OptionsJson = "[\"A\", \"B\"]"
            };

            _context.Questions.AddRange(qWeak, qStrong);

            // 勾股定理: 3 题错 2 题 => 正确率 33% (薄弱考点)
            _context.PracticeRecords.AddRange(
                new PracticeRecord { Id = Guid.NewGuid(), UserId = user.Id, QuestionId = qWeak.Id, Question = qWeak, IsCorrect = false, TimeTakenSeconds = 25, AnsweredAt = DateTime.Now },
                new PracticeRecord { Id = Guid.NewGuid(), UserId = user.Id, QuestionId = qWeak.Id, Question = qWeak, IsCorrect = false, TimeTakenSeconds = 25, AnsweredAt = DateTime.Now },
                new PracticeRecord { Id = Guid.NewGuid(), UserId = user.Id, QuestionId = qWeak.Id, Question = qWeak, IsCorrect = true, TimeTakenSeconds = 25, AnsweredAt = DateTime.Now }
            );

            // 整式的乘除: 5 题全对 => 正确率 100%
            for (int i = 0; i < 5; i++)
            {
                _context.PracticeRecords.Add(new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = qStrong.Id,
                    Question = qStrong,
                    IsCorrect = true,
                    TimeTakenSeconds = 15,
                    AnsweredAt = DateTime.Now
                });
            }

            await _context.SaveChangesAsync();

            // Act
            var report = await _evolutionService.GenerateDiagnosisReportAsync(user.Id);

            // Assert
            Assert.NotNull(report);
            Assert.Equal("数学", report.RecommendedSubject);
            Assert.Equal("勾股定理", report.RecommendedCategory);
            Assert.Contains("数学-整式的乘除", report.TopMasteredCategories);
            Assert.Contains("数学-勾股定理", report.TopWeakCategories);
        }
    }
}
