using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxSystemHealthEvolutionTests : IDisposable
    {
        private readonly AppDbContext _inMemoryContext;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxSystemHealthEvolutionTests()
        {
            (_inMemoryContext, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _inMemoryContext.Dispose();
            _connection.Dispose();
        }

        [Fact]
        public async Task GetSystemHealthAsync_WithInMemoryContext_ReturnsValidMetrics()
        {
            // Arrange
            _inMemoryContext.Users.Add(new User { Username = "health_admin", Role = UserRole.SuperAdmin });
            _inMemoryContext.Questions.Add(new Question { Stem = "测试健康试题", Subject = "数学", GradeTarget = "初中二年级", Category = "代数", Type = QuestionType.SingleChoice, CorrectAnswer = "A" });
            await _inMemoryContext.SaveChangesAsync();

            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var health = await healthService.GetSystemHealthAsync();

            // Assert
            Assert.NotNull(health);
            Assert.True(health.TotalUsers >= 1);
            Assert.True(health.TotalQuestions >= 1);
            Assert.True(health.DatabaseLatencyMs >= 0);
            Assert.Equal("ok", health.SqliteIntegrityStatus);
            Assert.True(health.GcMemoryBytes > 0);
            Assert.NotEmpty(health.GcMemoryFormatted);
            Assert.True(health.IsDatabaseHealthy);
        }

        [Fact]
        public async Task RunDatabaseIntegrityCheckAsync_ShouldReturnOk()
        {
            // Arrange
            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var integrity = await healthService.RunDatabaseIntegrityCheckAsync();

            // Assert
            Assert.Equal("ok", integrity);
        }

        [Fact]
        public async Task MeasureDatabaseLatencyAsync_ShouldReturnNonNegativeNumber()
        {
            // Arrange
            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var latency = await healthService.MeasureDatabaseLatencyAsync();

            // Assert
            Assert.True(latency >= 0, $"Database latency should be >= 0 ms, got {latency}");
        }

        [Fact]
        public async Task OptimizeDatabaseAsync_ShouldSucceed_WithCheckpointAndOptimize()
        {
            // Arrange
            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var result = await healthService.OptimizeDatabaseAsync();

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.Equal("ok", result.OptimizeStatus);
            Assert.False(string.IsNullOrWhiteSpace(result.CheckpointStatus));
            Assert.True(result.ElapsedMilliseconds >= 0);
            Assert.True(result.DatabaseLatencyMs >= 0);
            Assert.Contains("SQLite 优化自愈执行成功", result.Message);
        }

        [Fact]
        public async Task VacuumDatabaseAsync_ShouldSucceedOnInMemoryDb()
        {
            // Arrange
            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var success = await healthService.VacuumDatabaseAsync();

            // Assert
            Assert.True(success);
        }

        [Fact]
        public async Task GetSystemHealthAsync_WithPhysicalSqliteFile_DetectsFileSizesAndWal()
        {
            // Arrange
            var tempDbPath = Path.Combine(Path.GetTempPath(), $"northtropic_test_{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={tempDbPath};";

            try
            {
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connectionString)
                    .Options;

                using (var fileContext = new AppDbContext(options))
                {
                    await fileContext.Database.EnsureCreatedAsync();
                    await fileContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

                    fileContext.Users.Add(new User { Username = "file_admin", Role = UserRole.SuperAdmin });
                    fileContext.Questions.Add(new Question { Stem = "物理文件健康检查试题", Subject = "物理", GradeTarget = "初中三年级", Category = "力学", Type = QuestionType.SingleChoice, CorrectAnswer = "B" });
                    await fileContext.SaveChangesAsync();

                    var healthService = new SystemHealthService(fileContext);

                    // Act
                    var health = await healthService.GetSystemHealthAsync();
                    var optimizeRes = await healthService.OptimizeDatabaseAsync();

                    // Assert
                    Assert.NotNull(health);
                    Assert.True(health.DatabaseFileExists);
                    Assert.True(health.DatabaseSizeBytes > 0);
                    Assert.NotEmpty(health.DatabaseSizeFormatted);
                    Assert.True(health.IsDatabaseHealthy);

                    Assert.NotNull(optimizeRes);
                    Assert.True(optimizeRes.Success);
                }
            }
            finally
            {
                // Cleanup temp files
                SqliteConnection.ClearAllPools();
                if (File.Exists(tempDbPath))
                {
                    try { File.Delete(tempDbPath); } catch { }
                }
                var walPath = tempDbPath + "-wal";
                if (File.Exists(walPath))
                {
                    try { File.Delete(walPath); } catch { }
                }
                var shmPath = tempDbPath + "-shm";
                if (File.Exists(shmPath))
                {
                    try { File.Delete(shmPath); } catch { }
                }
            }
        }

        [Fact]
        public async Task CurriculumCachePreheating_EnsuresSyncDynamicCurriculum_WorksImmediately()
        {
            // Arrange
            var curriculumService = new CurriculumConfigService(_inMemoryContext);

            // Act: Simulate Program.cs startup initialization
            await curriculumService.EnsureInitializedAsync();

            // Assert: Cached provider works immediately
            var junior2Subjects = GradeSubjectProvider.GetSubjectsByGrade("初中二年级");
            Assert.NotEmpty(junior2Subjects);
            Assert.Contains("数学", junior2Subjects);

            var mathCategories = GradeSubjectProvider.GetCategoriesBySubject("数学");
            Assert.NotEmpty(mathCategories);
            Assert.Contains("全部", mathCategories);
        }
    }
}
