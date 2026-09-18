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

        [Fact]
        public async Task EvaluateAndExecuteAdaptiveMaintenancePlanAsync_ShouldSucceedAndRecordEvents()
        {
            // Arrange
            var healthService = new SystemHealthService(_inMemoryContext);

            // Act 1: Evaluate
            var plan = await healthService.EvaluateAdaptiveMaintenancePlanAsync();

            // Assert 1
            Assert.NotNull(plan);
            Assert.False(string.IsNullOrWhiteSpace(plan.UrgencyLevel));
            Assert.NotEmpty(plan.ActionReasons);

            // Act 2: Execute
            var result = await healthService.ExecuteAdaptiveMaintenancePlanAsync(plan);

            // Assert 2
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.True(result.ElapsedMilliseconds >= 0);
            Assert.NotEmpty(result.ExecutedActions);
            Assert.Contains("自适应自愈维护执行成功", result.Message);
        }

        [Fact]
        public async Task GetTableStorageMetricsAsync_ReturnsCoreTablesAndCounts()
        {
            // Arrange
            _inMemoryContext.Users.Add(new User { Username = "table_metric_user", Role = UserRole.Student });
            _inMemoryContext.Questions.Add(new Question { Stem = "表指标测试题", Subject = "语文", GradeTarget = "初中一年级", Category = "现代文阅读", Type = QuestionType.SingleChoice, CorrectAnswer = "C" });
            await _inMemoryContext.SaveChangesAsync();

            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var metrics = await healthService.GetTableStorageMetricsAsync();

            // Assert
            Assert.NotNull(metrics);
            Assert.True(metrics.Count >= 6);

            var userTable = metrics.Find(m => m.TableName == "Users");
            Assert.NotNull(userTable);
            Assert.True(userTable.RowCount >= 1);

            var questionTable = metrics.Find(m => m.TableName == "Questions");
            Assert.NotNull(questionTable);
            Assert.True(questionTable.RowCount >= 1);
        }

        [Fact]
        public void ArchitectureTelemetryRingBuffer_RecordsAndFiltersEvents()
        {
            // Arrange
            var healthService = new SystemHealthService(_inMemoryContext);
            var uniqueMessage = $"SelfHealing-Test-{Guid.NewGuid():N}";

            // Act
            healthService.RecordArchitectureEvent("SelfHealing", "Info", uniqueMessage, 12.3);
            var events = healthService.GetRecentArchitectureEvents("SelfHealing", "Info", 100);

            // Assert
            Assert.NotNull(events);
            var matched = events.FirstOrDefault(e => e.Message == uniqueMessage);
            Assert.NotNull(matched);
            Assert.Equal("Info", matched.Level);
            Assert.Equal("SelfHealing", matched.Category);
            Assert.Equal(12.3, matched.DurationMs);
        }

        [Fact]
        public async Task ExportArchitectureDiagnosticReportMarkdownAsync_GeneratesStructuredMarkdown()
        {
            // Arrange
            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var markdown = await healthService.ExportArchitectureDiagnosticReportMarkdownAsync();

            // Assert
            Assert.NotNull(markdown);
            Assert.Contains("Northtropic 系统架构全景诊断", markdown);
            Assert.Contains("核心业务数据表分布与对齐", markdown);
        }
    }
}
