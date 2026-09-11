using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class CurriculumConfigServiceTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        private readonly CurriculumConfigService _curriculumService;

        public CurriculumConfigServiceTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
            _curriculumService = new CurriculumConfigService(_context);
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Fact]
        public async Task EnsureInitializedAsync_WhenEmpty_SeedsAllGradesAndSubjects()
        {
            // Act
            await _curriculumService.EnsureInitializedAsync();

            // Assert
            var count = await _context.CurriculumSubjectConfigs.CountAsync();
            Assert.True(count > 0, "CurriculumSubjectConfigs should have been seeded");

            var grades = _curriculumService.GetAvailableGrades();
            Assert.NotEmpty(grades);

            foreach (var grade in grades)
            {
                var subjects = await _curriculumService.GetSubjectsByGradeAsync(grade);
                Assert.NotEmpty(subjects);
            }

            var junior2Subjects = await _curriculumService.GetSubjectsByGradeAsync("初中二年级");
            Assert.Contains("数学", junior2Subjects);
        }

        [Fact]
        public async Task GetCategoriesBySubjectAsync_AlwaysPrependsAllCategoriesOption()
        {
            // Arrange
            await _curriculumService.EnsureInitializedAsync();

            // Act
            var categories = await _curriculumService.GetCategoriesBySubjectAsync("数学");

            // Assert
            Assert.NotEmpty(categories);
            Assert.Equal("全部", categories[0]);
            Assert.Contains("数与式（实数/代数式/整式乘除）", categories);
        }

        [Fact]
        public async Task SaveSubjectTopicsAsync_UpdatesTopicsAndFiresEvent()
        {
            // Arrange
            await _curriculumService.EnsureInitializedAsync();
            bool eventFired = false;
            _curriculumService.OnCurriculumChanged += () => { eventFired = true; };

            var configs = await _curriculumService.GetAllConfigsAsync();
            var mathConfig = configs.First(c => c.Subject == "数学");

            var newTopics = new List<string> { "全部", "微积分初探", "极限理论" };

            // Act
            var (success, msg) = await _curriculumService.SaveSubjectTopicsAsync(mathConfig.Id, newTopics);

            // Assert
            Assert.True(success);
            Assert.True(eventFired);

            var updatedConfig = await _context.CurriculumSubjectConfigs.FindAsync(mathConfig.Id);
            Assert.NotNull(updatedConfig);
            var topicsInDb = JsonSerializer.Deserialize<List<string>>(updatedConfig.TopicsJson);
            Assert.NotNull(topicsInDb);
            Assert.Contains("微积分初探", topicsInDb);
        }

        [Fact]
        public async Task AddSubjectConfigAsync_AddsNewSubjectAndPersists()
        {
            // Arrange
            await _curriculumService.EnsureInitializedAsync();

            string testGrade = "初中三年级";
            string testSubject = "信息学奥赛";
            var testTopics = new List<string> { "全部", "算法基础", "动态规划" };

            // Act
            var (success, msg) = await _curriculumService.AddSubjectConfigAsync(testGrade, testSubject, testTopics);

            // Assert
            Assert.True(success);

            var subjects = await _curriculumService.GetSubjectsByGradeAsync(testGrade);
            Assert.Contains(testSubject, subjects);

            var categories = await _curriculumService.GetCategoriesBySubjectAsync(testSubject);
            Assert.Contains("动态规划", categories);
        }
    }
}
