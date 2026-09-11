using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxAscendanceElevationTests
    {
        public class SqliteDbContextFactory : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;
            public SqliteDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
            public AppDbContext CreateDbContext() => new AppDbContext(_options);
        }

        private static (SqliteConnection Connection, DbContextOptions<AppDbContext> Options) CreateSharedInMemoryDb()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            using (var ctx = new AppDbContext(options))
            {
                ctx.Database.EnsureCreated();
            }

            return (connection, options);
        }

        #region 1. 系统架构师：SQLite 在线热快照物理深度校验引擎测试

        [Fact]
        public async Task VerifyBackupSnapshotAsync_ValidSnapshot_ReturnsHealthyAndAccurateMetrics()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "northtropic_test_backups_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var dbFile = "test_snapshot.db";
            var fullPath = Path.Combine(tempDir, dbFile);

            try
            {
                // 创建一个包含核心表的实体快照 SQLite 数据库文件
                var connStr = $"Data Source={fullPath}";
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connStr)
                    .Options;

                await using (var ctx = new AppDbContext(options))
                {
                    await ctx.Database.EnsureCreatedAsync();
                    ctx.Users.Add(new User { Username = "architect", Password = "hash" });
                    ctx.Questions.Add(new Question { Stem = "2+2=?", CorrectAnswer = "4", Category = "数学" });
                    await ctx.SaveChangesAsync();
                }
                SqliteConnection.ClearAllPools();

                var (memConn, memOptions) = CreateSharedInMemoryDb();
                using (memConn)
                {
                    var factory = new SqliteDbContextFactory(memOptions);
                    using var dummyContext = factory.CreateDbContext();
                    var healthService = new SystemHealthService(dummyContext, factory);

                    var result = await healthService.VerifyBackupSnapshotAsync(dbFile, tempDir);

                    Assert.NotNull(result);
                    Assert.True(result.IsHealthy, $"Verification failed: {result.ErrorMessage}");
                    Assert.Equal("ok", result.SqliteIntegrityStatus);
                    Assert.True(result.TableCount >= 4);
                    Assert.True(result.CoreTablesPresent);
                    Assert.True(result.FileSizeBytes > 0);
                    Assert.NotNull(result.Sha256Hash);
                    Assert.Equal(64, result.Sha256Hash.Length);
                    Assert.True(result.VerificationDurationMs >= 0);

                    // 验证遥测事件流记录了 BackupVerify 事件
                    var events = healthService.GetRecentArchitectureEvents();
                    Assert.Contains(events, e => e.Category == "BackupVerify" && e.Level == "Success");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task VerifyBackupSnapshotAsync_CorruptOrZeroByte_ReturnsUnhealthyAndErrorMessage()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "northtropic_test_corrupt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var dbFile = "empty.db";
            var fullPath = Path.Combine(tempDir, dbFile);
            File.WriteAllBytes(fullPath, Array.Empty<byte>()); // 0 字节文件

            try
            {
                var (memConn, memOptions) = CreateSharedInMemoryDb();
                using (memConn)
                {
                    var factory = new SqliteDbContextFactory(memOptions);
                    using var dummyContext = factory.CreateDbContext();
                    var healthService = new SystemHealthService(dummyContext, factory);

                    var result = await healthService.VerifyBackupSnapshotAsync(dbFile, tempDir);

                    Assert.NotNull(result);
                    Assert.False(result.IsHealthy);
                    Assert.Contains("0 字节", result.ErrorMessage);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task VerifyBackupSnapshotAsync_SecurityDefense_BlocksPathTraversalAndInvalidExtension()
        {
            var (memConn, memOptions) = CreateSharedInMemoryDb();
            using (memConn)
            {
                var factory = new SqliteDbContextFactory(memOptions);
                using var dummyContext = factory.CreateDbContext();
                var healthService = new SystemHealthService(dummyContext, factory);

                // 路径穿越攻击尝试
                var resTraversal1 = await healthService.VerifyBackupSnapshotAsync("../../etc/passwd.db");
                Assert.False(resTraversal1.IsHealthy);
                Assert.Contains("拦截", resTraversal1.ErrorMessage);

                var resTraversal2 = await healthService.VerifyBackupSnapshotAsync("subdir\\malicious.db");
                Assert.False(resTraversal2.IsHealthy);
                Assert.Contains("拦截", resTraversal2.ErrorMessage);

                // 非法扩展名
                var resExt = await healthService.VerifyBackupSnapshotAsync("exploit.exe");
                Assert.False(resExt.IsHealthy);
                Assert.Contains("扩展名不合法", resExt.ErrorMessage);
            }
        }

        [Fact]
        public async Task ExportArchitectureDiagnosticReportJsonAsync_ProducesStructuredValidJson()
        {
            var (memConn, memOptions) = CreateSharedInMemoryDb();
            using (memConn)
            {
                var factory = new SqliteDbContextFactory(memOptions);
                using var dummyContext = factory.CreateDbContext();
                var healthService = new SystemHealthService(dummyContext, factory);

                var json = await healthService.ExportArchitectureDiagnosticReportJsonAsync();

                Assert.False(string.IsNullOrWhiteSpace(json));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                Assert.True(root.TryGetProperty("ReportMetadata", out var meta));
                Assert.True(meta.TryGetProperty("HostEnvironment", out var host));
                Assert.True(host.TryGetProperty("DotNetVersion", out _));

                Assert.True(root.TryGetProperty("ArchitectureHealth", out var health));
                Assert.True(health.TryGetProperty("HealthScore", out _));

                Assert.True(root.TryGetProperty("TableStorageMetrics", out var tableMetrics));
                Assert.Equal(JsonValueKind.Array, tableMetrics.ValueKind);

                Assert.True(root.TryGetProperty("TelemetryEventStream", out var events));
                Assert.Equal(JsonValueKind.Array, events.ValueKind);
            }
        }

        #endregion

        #region 2. 用户体验专家：解析几何圆锥曲线（双曲线与抛物线）智能归一化与等价匹配测试

        [Theory]
        [InlineData("x^2/9 - y^2/16 = 1", HyperbolaOrientation.Horizontal, 0, 0, 9, 16)]
        [InlineData("16x^2 - 9y^2 = 144", HyperbolaOrientation.Horizontal, 0, 0, 9, 16)]
        [InlineData("-y^2/16 + x^2/9 = 1", HyperbolaOrientation.Horizontal, 0, 0, 9, 16)]
        [InlineData("y^2/16 - x^2/9 = 1", HyperbolaOrientation.Vertical, 0, 0, 16, 9)]
        [InlineData("9y^2 - 16x^2 = 144", HyperbolaOrientation.Vertical, 0, 0, 16, 9)]
        [InlineData("(x-1)^2/4 - (y+2)^2/9 = 1", HyperbolaOrientation.Horizontal, 1, -2, 4, 9)]
        public void TryNormalizeHyperbolaEquation_VariousForms_NormalizesCorrectly(
            string eq, HyperbolaOrientation expOri, double expX, double expY, double expA2, double expB2)
        {
            var success = PracticeService.TryNormalizeHyperbolaEquation(eq, out var ori, out var x, out var y, out var a2, out var b2);
            Assert.True(success, $"解析双曲线方程失败: {eq}");
            Assert.Equal(expOri, ori);
            Assert.Equal(expX, x);
            Assert.Equal(expY, y);
            Assert.Equal(expA2, a2);
            Assert.Equal(expB2, b2);
        }

        [Theory]
        [InlineData("x^2/9 - y^2/16 = 1", "16x^2 - 9y^2 = 144")]
        [InlineData("16x^2 - 9y^2 = 144", "-y^2/16 + x^2/9 = 1")]
        [InlineData("(x-1)^2/4 - (y+2)^2/9 = 1", "9*(x-1)^2 - 4*(y+2)^2 = 36")]
        [InlineData("y^2/25 - x^2/16 = 1", "16y^2 - 25x^2 = 400")]
        public void CheckFillInBlankMatch_HyperbolaEquivalences_Accepted(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct), $"用户作答 [{user}] 与标准答案 [{correct}] 预期判定为等价，但判为不匹配");
        }

        [Fact]
        public void CheckFillInBlankMatch_HyperbolaDifferentOrientation_Rejected()
        {
            // 水平双曲线 vs 垂直双曲线焦点轴不同，必须判定不等价
            Assert.False(PracticeService.CheckFillInBlankMatch("x^2/9 - y^2/16 = 1", "y^2/9 - x^2/16 = 1"));
        }

        [Theory]
        [InlineData("y^2 = 4x", ParabolaOrientation.Horizontal, 0, 0, 4)]
        [InlineData("y^2 - 4x = 0", ParabolaOrientation.Horizontal, 0, 0, 4)]
        [InlineData("x = y^2/4", ParabolaOrientation.Horizontal, 0, 0, 4)]
        [InlineData("x = 0.25y^2", ParabolaOrientation.Horizontal, 0, 0, 4)]
        [InlineData("x^2 = 8y", ParabolaOrientation.Vertical, 0, 0, 8)]
        [InlineData("y = x^2/8", ParabolaOrientation.Vertical, 0, 0, 8)]
        [InlineData("y = 0.125x^2", ParabolaOrientation.Vertical, 0, 0, 8)]
        [InlineData("x^2 - 8y = 0", ParabolaOrientation.Vertical, 0, 0, 8)]
        [InlineData("(y-1)^2 = 4(x+2)", ParabolaOrientation.Horizontal, -2, 1, 4)]
        public void TryNormalizeParabolaEquation_VariousForms_NormalizesCorrectly(
            string eq, ParabolaOrientation expOri, double expX, double expY, double exp2P)
        {
            var success = PracticeService.TryNormalizeParabolaEquation(eq, out var ori, out var x, out var y, out var twoP);
            Assert.True(success, $"解析抛物线方程失败: {eq}");
            Assert.Equal(expOri, ori);
            Assert.Equal(expX, x);
            Assert.Equal(expY, y);
            Assert.Equal(exp2P, twoP);
        }

        [Theory]
        [InlineData("y^2 = 4x", "y^2 - 4x = 0")]
        [InlineData("x = y^2/4", "y^2 = 4x")]
        [InlineData("x = 0.25*y^2", "y^2 = 4x")]
        [InlineData("y = x^2/8", "x^2 = 8y")]
        [InlineData("x^2 - 8y = 0", "y = 0.125x^2")]
        public void CheckFillInBlankMatch_ParabolaEquivalences_Accepted(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct), $"用户作答 [{user}] 与标准答案 [{correct}] 预期判定为等价，但判为不匹配");
        }

        [Fact]
        public void CheckFillInBlankMatch_ParabolaDifferentOrientation_Rejected()
        {
            // 开口向右/向左 vs 开口向上/向下对称轴不同，必须判定不等价
            Assert.False(PracticeService.CheckFillInBlankMatch("y^2 = 4x", "x^2 = 4y"));
        }

        #endregion

        #region 3. 用户体验专家：初等对数函数数值智能求值与教学反馈测试

        [Theory]
        [InlineData("ln(e)", "1")]
        [InlineData("ln(1)", "0")]
        [InlineData("lg(10)", "1")]
        [InlineData("lg(100)", "2")]
        [InlineData("log_2(8)", "3")]
        [InlineData("log2(8)", "3")]
        [InlineData("log(2,8)", "3")]
        [InlineData("log(3,9)", "2")]
        public void CheckFillInBlankMatch_LogarithmEvaluation_MatchesNumber(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct), $"用户对数作答 [{user}] 应智能求值并匹配标准数值 [{correct}]");
        }

        [Fact]
        public void GenerateEquivalentMatchReason_HyperbolaAndParabola_ProvidesPedagogicalFeedback()
        {
            var (conn, options) = CreateSharedInMemoryDb();
            using (conn)
            {
                var factory = new SqliteDbContextFactory(options);
                using var dummyContext = factory.CreateDbContext();
                var practiceService = new PracticeService(dummyContext, null!, null!, null!, factory);

                // 双曲线教学反馈
                var qHyp = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "x^2/9 - y^2/16 = 1" };
                var hypReason = PracticeService.GenerateEquivalentMatchReason(qHyp, "16x^2 - 9y^2 = 144");
                Assert.NotNull(hypReason);
                Assert.Contains("双曲线", hypReason);
                Assert.Contains("焦点在 x 轴", hypReason);
                Assert.Contains("a²=9", hypReason);
                Assert.Contains("b²=16", hypReason);

                // 抛物线教学反馈
                var qPar = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "y^2 = 4x" };
                var parReason = PracticeService.GenerateEquivalentMatchReason(qPar, "x = y^2/4");
                Assert.NotNull(parReason);
                Assert.Contains("抛物线", parReason);
                Assert.Contains("对称轴平行于 x 轴", parReason);
                Assert.Contains("焦准距 2p=4", parReason);
            }
        }

        #endregion
    }
}
