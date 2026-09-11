using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class AiQuestionGeneratorEngineTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        private readonly FakeGamificationService _fakeGamificationService;
        private readonly AiQuestionGeneratorService _generatorService;

        public AiQuestionGeneratorEngineTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
            _fakeGamificationService = new FakeGamificationService();
            _generatorService = new AiQuestionGeneratorService(_fakeGamificationService, _context, new SimpleHttpClientFactory());
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Theory]
        [InlineData("数学")]
        [InlineData("物理")]
        [InlineData("化学")]
        [InlineData("英语")]
        [InlineData("语文")]
        [InlineData("生物")]
        [InlineData("历史")]
        [InlineData("地理")]
        public async Task GenerateBatchQuestions_AllSubjects_ProduceValidHeuristicQuestions(string subject)
        {
            // Act: In offline / test mode without API key, generator falls back to smart heuristic questions
            var questions = await _generatorService.GenerateBatchQuestionsAsync("初中二年级", subject, null, 3);

            // Assert
            Assert.NotNull(questions);
            Assert.NotEmpty(questions);

            foreach (var q in questions)
            {
                Assert.Equal(subject, q.Subject);
                Assert.False(string.IsNullOrWhiteSpace(q.Stem), $"Question stem should not be empty for {subject}");
                Assert.False(string.IsNullOrWhiteSpace(q.CorrectAnswer), $"Correct answer should not be empty for {subject}");
                Assert.False(string.IsNullOrWhiteSpace(q.StandardAnalysis), $"Standard analysis should not be empty for {subject}");
                Assert.InRange(q.Difficulty, 1, 5);

                if (q.Type == QuestionType.SingleChoice || q.Type == QuestionType.MultipleChoice)
                {
                    Assert.False(string.IsNullOrWhiteSpace(q.OptionsJson), $"OptionsJson should not be empty for choice question in {subject}");
                    var options = JsonSerializer.Deserialize<List<string>>(q.OptionsJson);
                    Assert.NotNull(options);
                    Assert.True(options.Count >= 2, $"Choice question in {subject} should have at least 2 options");
                }
            }
        }

        [Fact]
        public async Task GenerateQuestionByGrade_ReturnsSingleValidQuestion()
        {
            // Act
            var q = await _generatorService.GenerateQuestionByGradeAsync("初中三年级", "数学", "二次函数");

            // Assert
            Assert.NotNull(q);
            Assert.Equal("数学", q.Subject);
            Assert.False(string.IsNullOrWhiteSpace(q.Stem));
            Assert.False(string.IsNullOrWhiteSpace(q.CorrectAnswer));
        }
    }
}
