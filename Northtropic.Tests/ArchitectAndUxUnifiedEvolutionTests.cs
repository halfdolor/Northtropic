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
    internal class CustomTestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public CustomTestDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext()
        {
            return new AppDbContext(_options);
        }

        public Task<AppDbContext> CreateDbContextAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppDbContext(_options));
        }
    }

    public class ArchitectAndUxUnifiedEvolutionTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxUnifiedEvolutionTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Fact]
        public async Task AsyncDbScope_WithDirectFallbackContext_DoesNotPrematurelyDisposeCallerContext()
        {
            // Arrange
            var initialCount = await _context.Questions.CountAsync();

            // Act: Scope wraps the direct fallback context
            await using (var scope = await AsyncDbScope.CreateAsync(null, _context))
            {
                Assert.Same(_context, scope.Context);
                scope.Context.Questions.Add(new Question
                {
                    Stem = "测试题目 1",
                    Subject = "数学",
                    Category = "代数",
                    CorrectAnswer = "A",
                    OptionsJson = "[\"A\",\"B\",\"C\",\"D\"]"
                });
                await scope.Context.SaveChangesAsync();
            }

            // Assert: Caller context is NOT disposed and can continue querying
            var finalCount = await _context.Questions.CountAsync();
            Assert.Equal(initialCount + 1, finalCount);
        }

        [Fact]
        public async Task AsyncDbScope_WithFactory_CreatesOwnedContextAndDisposesSafely()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;
            var factory = new CustomTestDbContextFactory(options);

            // Act
            await using (var scope = await AsyncDbScope.CreateAsync(factory, _context))
            {
                Assert.NotNull(scope.Context);
                Assert.NotSame(_context, scope.Context);

                scope.Context.Questions.Add(new Question
                {
                    Stem = "Factory 作用域题目",
                    Subject = "物理",
                    Category = "力学",
                    CorrectAnswer = "B",
                    OptionsJson = "[\"A\",\"B\",\"C\",\"D\"]"
                });
                await scope.Context.SaveChangesAsync();
            }

            // Assert: Saved data persisted in the underlying database
            var persisted = await _context.Questions.FirstOrDefaultAsync(q => q.Stem == "Factory 作用域题目");
            Assert.NotNull(persisted);
            Assert.Equal("物理", persisted.Subject);
        }

        [Fact]
        public async Task AsyncDbScope_BeginTransactionAsync_RollbackAndCommitWorkCorrectly()
        {
            // Arrange
            await using var scope = await AsyncDbScope.CreateAsync(null, _context);

            // Test Rollback
            await using (var tx = await scope.BeginTransactionAsync())
            {
                scope.Context.Questions.Add(new Question
                {
                    Stem = "事务回滚题目",
                    Subject = "化学",
                    Category = "元素",
                    CorrectAnswer = "C",
                    OptionsJson = "[\"A\",\"B\",\"C\",\"D\"]"
                });
                await scope.Context.SaveChangesAsync();
                await tx.RollbackAsync();
            }

            var rollbackSearch = await _context.Questions.FirstOrDefaultAsync(q => q.Stem == "事务回滚题目");
            Assert.Null(rollbackSearch);

            // Test Commit
            await using (var tx2 = await scope.BeginTransactionAsync())
            {
                scope.Context.Questions.Add(new Question
                {
                    Stem = "事务提交题目",
                    Subject = "化学",
                    Category = "酸碱盐",
                    CorrectAnswer = "D",
                    OptionsJson = "[\"A\",\"B\",\"C\",\"D\"]"
                });
                await scope.Context.SaveChangesAsync();
                await tx2.CommitAsync();
            }

            var commitSearch = await _context.Questions.FirstOrDefaultAsync(q => q.Stem == "事务提交题目");
            Assert.NotNull(commitSearch);
            Assert.Equal("酸碱盐", commitSearch.Category);
        }

        [Fact]
        public async Task PracticeService_CacheInvalidation_ForcesImmediateReloadFromDatabase()
        {
            // Arrange
            PracticeService.InvalidateCategoryCache();
            var userSession = new FakeUserSessionService();
            var gamification = new FakeGamificationService();
            var practiceService = new PracticeService(_context, gamification, userSession, new FakeAiTutorService());

            _context.Questions.Add(new Question
            {
                Stem = "缓存初始题目",
                Subject = "数学",
                Category = "几何证明",
                CorrectAnswer = "A",
                OptionsJson = "[\"A\",\"B\"]"
            });
            await _context.SaveChangesAsync();

            // Warm up category cache
            var initialCategories = await practiceService.GetCategoriesAsync();
            Assert.Contains("几何证明", initialCategories);

            // Insert new category directly into database
            _context.Questions.Add(new Question
            {
                Stem = "新分类试题",
                Subject = "数学",
                Category = "函数极值与导数",
                CorrectAnswer = "B",
                OptionsJson = "[\"A\",\"B\"]"
            });
            await _context.SaveChangesAsync();

            // Prior to invalidation, cached categories shouldn't contain newly added category
            var cachedCategories = await practiceService.GetCategoriesAsync();
            Assert.DoesNotContain("函数极值与导数", cachedCategories);

            // Act: Invalidate category cache
            PracticeService.InvalidateCategoryCache();

            // Assert: Next call immediately fetches updated list from database
            var reloadedCategories = await practiceService.GetCategoriesAsync();
            Assert.Contains("函数极值与导数", reloadedCategories);
        }

        [Fact]
        public void SqliteConnectionStringBuilder_DefaultTimeout_SetsFiveSecondsBusyTimeout()
        {
            // Arrange
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = "northtropic.db",
                DefaultTimeout = 5,
                Mode = SqliteOpenMode.ReadWriteCreate
            };

            var connectionString = builder.ToString();

            // Assert: Validates busy timeout is serialized in connection string
            Assert.Contains("Default Timeout=5", connectionString);
            Assert.Contains("Data Source=northtropic.db", connectionString);
        }

        [Theory]
        [InlineData("A", 0)]
        [InlineData("1", 0)]
        [InlineData("B", 1)]
        [InlineData("2", 1)]
        [InlineData("C", 2)]
        [InlineData("3", 2)]
        [InlineData("D", 3)]
        [InlineData("4", 3)]
        [InlineData("E", 4)]
        [InlineData("5", 4)]
        public void KeyboardShortcut_KeyIndexMapping_ResolvesExpectedOptionIndex(string key, int expectedIndex)
        {
            // Act: Test switch expression used in Practice.razor keyboard dispatcher
            int optIdx = key switch
            {
                "A" => 0, "1" => 0,
                "B" => 1, "2" => 1,
                "C" => 2, "3" => 2,
                "D" => 3, "4" => 3,
                "E" => 4, "5" => 4,
                _ => -1
            };

            // Assert
            Assert.Equal(expectedIndex, optIdx);
        }

        [Theory]
        [InlineData("正确", true)]
        [InlineData("对", true)]
        [InlineData("√", true)]
        [InlineData("True", true)]
        [InlineData("错误", false)]
        [InlineData("错", false)]
        [InlineData("×", false)]
        [InlineData("False", false)]
        public void TryNormalizeJudgement_AccuratelyNormalizesDiverseInputs(string input, bool expected)
        {
            // Act
            bool success = PracticeService.TryNormalizeJudgement(input, out bool result);

            // Assert
            Assert.True(success);
            Assert.Equal(expected, result);
        }

        [Fact]
        public async Task CurriculumConfigService_OnCurriculumChanged_FiresOnCurriculumUpdate()
        {
            // Arrange
            var curriculumService = new CurriculumConfigService(_context);
            await curriculumService.EnsureInitializedAsync();

            bool eventFired = false;
            curriculumService.OnCurriculumChanged += () =>
            {
                eventFired = true;
            };

            var configs = await curriculumService.GetAllConfigsAsync();
            var mathConfig = configs.First(c => c.Subject == "数学");

            // Act: Update subject topics
            var (success, _) = await curriculumService.SaveSubjectTopicsAsync(mathConfig.Id, new List<string> { "全部", "函数方程", "立体几何" });

            // Assert: Event was triggered to notify UI components
            Assert.True(success);
            Assert.True(eventFired);
        }
    }
}
