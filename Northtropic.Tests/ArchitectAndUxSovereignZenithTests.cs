using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    public class ArchitectAndUxSovereignZenithTests
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

        #region 1. 系统架构师：SQLite 二进制魔数头与灾备校验测试

        [Fact]
        public async Task VerifyBackupSnapshot_WithValidSqliteDatabase_Validates16ByteHeaderAndPageGeometry()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                using var ctx = new AppDbContext(options);
                var factory = new SqliteDbContextFactory(options);
                var healthService = new SystemHealthService(ctx, factory);

                // 在临时目录创建一个标准的物理 SQLite 数据库文件作为测试快照
                var backupsDir = Path.Combine(AppContext.BaseDirectory, "backups");
                Directory.CreateDirectory(backupsDir);
                var testBackupFileName = $"test_snapshot_{Guid.NewGuid():N}.db";
                var fullFilePath = Path.Combine(backupsDir, testBackupFileName);

                try
                {
                    using (var fsConn = new SqliteConnection($"Data Source={fullFilePath}"))
                    {
                        fsConn.Open();
                        using var cmd = fsConn.CreateCommand();
                        cmd.CommandText = "CREATE TABLE Users (Id TEXT PRIMARY KEY); CREATE TABLE Questions (Id TEXT PRIMARY KEY); CREATE TABLE PracticeRecords (Id TEXT PRIMARY KEY); CREATE TABLE ErrorItems (Id TEXT PRIMARY KEY); INSERT INTO Users VALUES ('u1'); INSERT INTO Questions VALUES ('q1');";
                        cmd.ExecuteNonQuery();
                    }
                    SqliteConnection.ClearAllPools();

                    // 校验该快照
                    var verifyResult = await healthService.VerifyBackupSnapshotAsync(testBackupFileName);

                    Assert.NotNull(verifyResult);
                    Assert.True(verifyResult.IsHealthy, $"快照自检应合格: {verifyResult.ErrorMessage}");
                    Assert.True(verifyResult.HeaderValidated, "SQLite 16字节魔数头 (SQLite format 3\\0) 应验证通过");
                    Assert.True(verifyResult.PageSize >= 512, $"页面尺寸必须是合法值(实际: {verifyResult.PageSize})");
                    Assert.True(verifyResult.IsForeignKeyHealthy, "默认无外键冲突应健康");
                    Assert.True(verifyResult.TableCount >= 1, "应检测出测试数据表");
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    if (File.Exists(fullFilePath))
                    {
                        try { File.Delete(fullFilePath); } catch { }
                    }
                }
            }
        }

        [Fact]
        public async Task VerifyBackupSnapshot_WithCorruptedHeader_FailsGracefully()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                using var ctx = new AppDbContext(options);
                var factory = new SqliteDbContextFactory(options);
                var healthService = new SystemHealthService(ctx, factory);

                var backupsDir = Path.Combine(AppContext.BaseDirectory, "backups");
                Directory.CreateDirectory(backupsDir);
                var testCorruptedFileName = $"corrupt_{Guid.NewGuid():N}.db";
                var fullFilePath = Path.Combine(backupsDir, testCorruptedFileName);

                try
                {
                    // 写入错误的头字节
                    var corruptedBytes = Encoding.ASCII.GetBytes("NOT_A_SQLITE_HEADER_CORRUPTED_FILE_DATA_HERE");
                    await File.WriteAllBytesAsync(fullFilePath, corruptedBytes);

                    var verifyResult = await healthService.VerifyBackupSnapshotAsync(testCorruptedFileName);

                    Assert.NotNull(verifyResult);
                    Assert.False(verifyResult.IsHealthy, "魔数损坏的文件不能通过校验");
                    Assert.False(verifyResult.HeaderValidated, "魔数头应标记为未通过");
                    Assert.Contains("魔数", verifyResult.ErrorMessage);
                }
                finally
                {
                    if (File.Exists(fullFilePath))
                    {
                        try { File.Delete(fullFilePath); } catch { }
                    }
                }
            }
        }

        #endregion

        #region 2. 系统架构师：全量架构诊断档案导出 (JSON + Markdown Dossier)

        [Fact]
        public async Task ExportArchitectureDiagnosticReport_JsonAndMarkdown_ContainsComprehensiveDossier()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                using var ctx = new AppDbContext(options);
                var factory = new SqliteDbContextFactory(options);
                var healthService = new SystemHealthService(ctx, factory);

                // 产生一条架构事件注入环形缓冲
                healthService.RecordArchitectureEvent("SystemAudit", "Success", "Sovereign zenith audit initiated");

                // 1. JSON 快照导出
                var jsonReport = await healthService.ExportArchitectureDiagnosticReportJsonAsync();
                Assert.NotNull(jsonReport);
                Assert.True(jsonReport.Length > 50, "JSON 报告不应为空");

                using var doc = JsonDocument.Parse(jsonReport);
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("ReportMetadata", out _));
                Assert.True(root.TryGetProperty("ArchitectureHealth", out var healthElem));
                Assert.True(healthElem.TryGetProperty("IsDatabaseHealthy", out var dbHealthyElem) && dbHealthyElem.GetBoolean());
                Assert.True(root.TryGetProperty("TableStorageMetrics", out _));
                Assert.True(root.TryGetProperty("BackupSnapshots", out _));
                Assert.True(root.TryGetProperty("TelemetryEventStream", out _));

                // 2. Markdown 诊断档案导出
                var markdownDossier = await healthService.ExportArchitectureDiagnosticReportMarkdownAsync();
                Assert.NotNull(markdownDossier);
                Assert.Contains("Northtropic 系统架构全景诊断与灾备健康档案", markdownDossier);
                Assert.Contains("一、系统架构综合健康与评分", markdownDossier);
                Assert.Contains("二、存储引擎与物理文件指标", markdownDossier);
                Assert.Contains("三、托管运行时与内存压力指标", markdownDossier);
                Assert.Contains("四、核心业务数据表分布与对齐", markdownDossier);
                Assert.Contains("五、灾备快照归档状态", markdownDossier);
                Assert.Contains("六、最新架构自愈与 APM 遥测事件流", markdownDossier);
                Assert.Contains("SystemAudit", markdownDossier);
            }
        }

        #endregion

        #region 3. 系统架构师：AI 故障降级与 APM 遥测闭环集成

        [Fact]
        public async Task AiTutor_And_AiGenerator_LogWarningEventsToHealthTelemetryRingBuffer_OnFailure()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                using var ctx = new AppDbContext(options);
                var factory = new SqliteDbContextFactory(options);
                var healthService = new SystemHealthService(ctx, factory);

                var fakeGamification = new FakeGamificationService();
                fakeGamification.CurrentUser.LlmApiKey = "invalid_probe_key";
                fakeGamification.CurrentUser.LlmBaseUrl = "http://127.0.0.1:54321/v1";

                var httpClientFactory = new SimpleHttpClientFactory();
                var aiTutor = new AiTutorService(fakeGamification, httpClientFactory, ctx, factory, healthService);
                var aiGenerator = new AiQuestionGeneratorService(fakeGamification, ctx, httpClientFactory, factory, healthService);

                var testQuestion = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "若 x + 2 = 5，则 x = ?",
                    CorrectAnswer = "3",
                    Type = QuestionType.FillInBlank,
                    Subject = "数学",
                    Category = "一元一次方程",
                    GradeTarget = "初中一年级"
                };

                // 1. 触发 AI 辅导解析，因不可达端点应降级并向 APM 报告事件
                var explanation = await aiTutor.GetExplanationAsync(testQuestion, "2");
                Assert.NotNull(explanation);
                Assert.True(!string.IsNullOrEmpty(explanation.Summary) || !string.IsNullOrEmpty(explanation.StepByStepReasoning));

                // 2. 检查遥测环形缓冲中是否记录了 AiTutor 降级告警
                var telemetryEvents = healthService.GetRecentArchitectureEvents();
                var tutorEvent = telemetryEvents.FirstOrDefault(e => e.Category == "AiTutor");
                Assert.NotNull(tutorEvent);
                Assert.Equal("Warning", tutorEvent.Level);
                Assert.Contains("降级", tutorEvent.Message);

                // 3. 触发 AI 批量出题，不可达端点应降级并记录 AiGenerator 事件
                var genResult = await aiGenerator.GenerateBatchQuestionsAsync("高一", "物理", "力学", 1);
                Assert.NotNull(genResult);
                Assert.True(genResult.Count > 0, "降级启发式仍应生成离线题目保证教学不中断");

                var updatedTelemetry = healthService.GetRecentArchitectureEvents();
                var generatorEvent = updatedTelemetry.FirstOrDefault(e => e.Category == "AiGenerator");
                Assert.NotNull(generatorEvent);
                Assert.Equal("Warning", generatorEvent.Level);
            }
        }

        #endregion

        #region 4. 用户体验专家：三角特殊角角度制与弧度制双向等价测试

        [Theory]
        [InlineData("30°", "\\pi/6")]
        [InlineData("30°", "\\frac{\\pi}{6}")]
        [InlineData("30度", "pi/6")]
        [InlineData("30deg", "\\frac{\\pi}{6}")]
        [InlineData("45°", "\\frac{\\pi}{4}")]
        [InlineData("45度", "pi/4")]
        [InlineData("60°", "\\frac{\\pi}{3}")]
        [InlineData("90°", "\\frac{\\pi}{2}")]
        [InlineData("90度", "pi/2")]
        [InlineData("180°", "\\pi")]
        [InlineData("180度", "pi")]
        [InlineData("360°", "2*pi")]
        [InlineData("360°", "2pi")]
        public void CheckAngleAndRadianEquivalence_ShouldRecognizeBidirectionalEquivalence(string degrees, string radians)
        {
            // 正向：用户输入角度，标准答案为弧度
            bool matchForward = PracticeService.CheckAngleAndRadianEquivalence(degrees, radians);
            Assert.True(matchForward, $"应识别等价: 用户输入 [{degrees}] 与 标准答案 [{radians}]");

            // 反向：用户输入弧度，标准答案为角度
            bool matchBackward = PracticeService.CheckAngleAndRadianEquivalence(radians, degrees);
            Assert.True(matchBackward, $"应识别等价: 用户输入 [{radians}] 与 标准答案 [{degrees}]");
        }

        #endregion

        #region 5. 用户体验专家：国际单位制科学词头换算等价测试

        [Theory]
        // 频率类
        [InlineData("1 kHz", "1000 Hz")]
        [InlineData("1000 Hz", "1 kHz")]
        [InlineData("2.5 MHz", "2500000 Hz")]
        [InlineData("2.5*10^6 Hz", "2.5 MHz")]
        [InlineData("100 kHz", "100000 Hz")]
        // 能量类
        [InlineData("1 kJ", "1000 J")]
        [InlineData("3.6 kJ", "3600 J")]
        [InlineData("1.5 MJ", "1500 kJ")]
        // 电压类
        [InlineData("10 kV", "10000 V")]
        [InlineData("220 V", "0.22 kV")]
        [InlineData("500 mV", "0.5 V")]
        // 阻抗类
        [InlineData("1 kΩ", "1000 Ω")]
        [InlineData("2.2 k\\Omega", "2200 \\Omega")]
        [InlineData("10 komega", "10000 ohm")]
        // 压强类
        [InlineData("100 kPa", "100000 Pa")]
        [InlineData("2 MPa", "2000000 Pa")]
        // 功率类
        [InlineData("1 kW", "1000 W")]
        [InlineData("2.5 MW", "2500000 W")]
        // 长度与质量
        [InlineData("1 km", "1000 m")]
        [InlineData("50 cm", "0.5 m")]
        [InlineData("200 mm", "0.2 m")]
        [InlineData("1 kg", "1000 g")]
        [InlineData("2 t", "2000 kg")]
        public void CheckScientificUnitMultiplierEquivalence_ShouldRecognizeEqualQuantities(string userQty, string correctQty)
        {
            bool match = PracticeService.CheckScientificUnitMultiplierEquivalence(userQty, correctQty);
            Assert.True(match, $"应识别国际单位制词头等价: [{userQty}] 与 [{correctQty}]");

            bool reverseMatch = PracticeService.CheckScientificUnitMultiplierEquivalence(correctQty, userQty);
            Assert.True(reverseMatch, $"反向亦应等价: [{correctQty}] 与 [{userQty}]");
        }

        #endregion

        #region 6. 用户体验专家：智能等价提示与艾宾浩斯记忆留存健康度

        [Fact]
        public void GenerateEquivalentMatchReason_ProvidesInsightfulFeedbackForAngleAndUnits()
        {
            var qAngle = new Question
            {
                Stem = "求 30 度对应的弧度值",
                CorrectAnswer = "\\pi/6",
                Type = QuestionType.FillInBlank
            };
            var angleReason = PracticeService.GenerateEquivalentMatchReason(qAngle, "30°");
            Assert.NotNull(angleReason);
            Assert.Contains("三角角度与弧度制等价", angleReason);

            var qUnit = new Question
            {
                Stem = "交流电频率计算",
                CorrectAnswer = "1000 Hz",
                Type = QuestionType.FillInBlank
            };
            var unitReason = PracticeService.GenerateEquivalentMatchReason(qUnit, "1 kHz");
            Assert.NotNull(unitReason);
            Assert.Contains("国际单位制科学词头换算等价", unitReason);
        }

        [Fact]
        public void ErrorBookService_RetentionHealthScore_FollowsEbbinghausCurve()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                using var ctx = new AppDbContext(options);
                var fakeGamification = new FakeGamificationService();
                var fakeSession = new FakeUserSessionService();
                var errorBookService = new ErrorBookService(ctx, fakeGamification, fakeSession);

                var now = DateTime.Now;

                // 刚刚复习完毕，留存健康度应为 100%
                var itemFresh = new ErrorItem
                {
                    RevisionCount = 0,
                    CreatedAt = now,
                    LastRevisedAt = now
                };
                double scoreFresh = errorBookService.CalculateRetentionHealthScore(itemFresh, now);
                Assert.True(scoreFresh >= 98.0, $"刚复习完的健康度应接近100% (实际: {scoreFresh})");

                // 经过一个推荐周期后，记忆留存度大约衰减为 ~50%
                var itemElapsed = new ErrorItem
                {
                    RevisionCount = 0, // 推荐间隔 12 小时
                    LastRevisedAt = now.AddHours(-12)
                };
                double scoreElapsed = errorBookService.CalculateRetentionHealthScore(itemElapsed, now);
                Assert.True(scoreElapsed >= 40.0 && scoreElapsed <= 60.0, $"1个半衰期后的留存度应在40%-60%之间 (实际: {scoreElapsed})");

                // 经过多次巩固后，相同时间衰减速度应显著变慢
                var itemMastered = new ErrorItem
                {
                    RevisionCount = 3, // 推荐间隔 168 小时 (7天)
                    LastRevisedAt = now.AddHours(-12)
                };
                double scoreMastered = errorBookService.CalculateRetentionHealthScore(itemMastered, now);
                Assert.True(scoreMastered > scoreElapsed, $"高巩固次数的记忆存留率应显著高于初次复习 (实际: {scoreMastered}% vs {scoreElapsed}%)");
            }
        }

        #endregion
    }
}
