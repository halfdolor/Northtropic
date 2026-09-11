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
    public class ArchitectAndUxTranscendenceApexTests
    {
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

        #region 1. 系统架构师维度：SQLite 外键完整性校验与直方图自愈优化测试

        [Fact]
        public async Task SystemHealthService_RunDatabaseIntegrityAndForeignKeyCheck_ShouldPassOnHealthyDb()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            using (var context = new AppDbContext(options))
            {
                var healthService = new SystemHealthService(context);

                // 1. 物理 B-Tree 完整性校验
                var integrityStatus = await healthService.RunDatabaseIntegrityCheckAsync();
                Assert.Equal("ok", integrityStatus);

                // 2. 关系外键完整性校验 (PRAGMA foreign_key_check)
                var foreignKeyStatus = await healthService.RunDatabaseForeignKeyCheckAsync();
                Assert.Equal("ok", foreignKeyStatus);

                // 3. 全局系统健康诊断
                var healthDto = await healthService.GetSystemHealthAsync();
                Assert.True(healthDto.IsDatabaseHealthy);
                Assert.True(healthDto.IsForeignKeyHealthy);
                Assert.Equal("ok", healthDto.ForeignKeyIntegrityStatus);
                Assert.Equal(0, healthDto.ForeignKeyViolationsCount);
                Assert.True(healthDto.HealthScore >= 80);
            }
        }

        [Fact]
        public async Task SystemHealthService_OptimizeDatabase_ShouldExecuteAnalyzeAndReportStatus()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            using (var context = new AppDbContext(options))
            {
                var healthService = new SystemHealthService(context);

                var optimizeResult = await healthService.OptimizeDatabaseAsync();

                Assert.True(optimizeResult.Success);
                Assert.Equal("ok", optimizeResult.AnalyzeStatus);
                Assert.NotNull(optimizeResult.OptimizeStatus);
                Assert.NotNull(optimizeResult.CheckpointStatus);
                Assert.True(optimizeResult.DatabaseLatencyMs >= 0);
            }
        }

        #endregion

        #region 2. 用户体验与数据可靠性维度：非破坏性试飞预校验 (Dry-Run Import) 测试

        [Fact]
        public async Task QuestionImportService_CsvDryRun_ShouldParseAndValidateWithoutPersistingToDatabase()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            using (var context = new AppDbContext(options))
            {
                var importService = new QuestionImportService(context);

                string csvContent = "Subject,Category,Type,Difficulty,Stem,Options,CorrectAnswer,Analysis\n" +
                                   "数学,代数,SingleChoice,Easy,1+1等于几?,\"A. 1|B. 2|C. 3|D. 4\",B,基础加法原理\n" +
                                   "物理,力学,SingleChoice,Medium,重力加速度一般近似为?,\"A. 9.8 m/s²|B. 3.14 m/s²|C. 100 m/s²|D. 0 m/s²\",A,地球表面重力加速度常数";

                using var dryRunStream = new MemoryStream(Encoding.UTF8.GetBytes(csvContent));

                // 1. 执行试飞预校验 (dryRun: true)
                var dryRunResult = await importService.ParseAndImportQuestionsByFileFormatAsync(dryRunStream, "questions.csv", dryRun: true);

                Assert.True(dryRunResult.IsDryRun);
                Assert.Equal(2, dryRunResult.SuccessCount);
                Assert.Equal(0, dryRunResult.FailureCount);
                Assert.Equal(2, dryRunResult.ImportedQuestions.Count);

                // 验证数据库严格未发生任何写入
                var dbQuestionCount = await context.Questions.CountAsync();
                Assert.Equal(0, dbQuestionCount);

                // 2. 执行正式导入 (dryRun: false)
                using var realImportStream = new MemoryStream(Encoding.UTF8.GetBytes(csvContent));
                var realImportResult = await importService.ParseAndImportQuestionsByFileFormatAsync(realImportStream, "questions.csv", dryRun: false);

                Assert.False(realImportResult.IsDryRun);
                Assert.Equal(2, realImportResult.SuccessCount);

                // 验证数据库已被正确持久化
                dbQuestionCount = await context.Questions.CountAsync();
                Assert.Equal(2, dbQuestionCount);
            }
        }

        [Fact]
        public async Task QuestionImportService_ValidateQuestionsFileAsync_ShouldReportFormatErrorsSafely()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            using (var context = new AppDbContext(options))
            {
                var importService = new QuestionImportService(context);

                // 构造包含格式缺陷的数据（题干缺失，选项不足）
                string invalidCsv = "Subject,Category,Type,Difficulty,Stem,Options,CorrectAnswer,Analysis\n" +
                                   "数学,代数,SingleChoice,Easy,,\"A. 1|B. 2\",B,缺少题干\n" +
                                   "物理,力学,SingleChoice,Medium,有效题干单选但选项不足1个,\"A. 仅选项A\",A,选项数量不足";

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalidCsv));

                var validationResult = await importService.ValidateQuestionsFileAsync(stream, "invalid_test.csv");

                Assert.True(validationResult.IsDryRun);
                Assert.Equal(0, validationResult.SuccessCount);
                Assert.Equal(2, validationResult.FailureCount);
                Assert.True(validationResult.ErrorMessages.Count >= 2);

                // 确保对数据库 0 污染
                var dbCount = await context.Questions.CountAsync();
                Assert.Equal(0, dbCount);
            }
        }

        #endregion

        #region 3. 认知流动力学：四象限速度与准确度心流评估模型测试

        [Fact]
        public async Task PracticeService_CheckAnswerInternal_SingleQuestionCognitiveBadgeClassification()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            using (var context = new AppDbContext(options))
            {
                var user = new User { Id = Guid.NewGuid(), Username = "SingleCognitiveUser" };
                context.Users.Add(user);

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试单选",
                    OptionsJson = JsonSerializer.Serialize(new List<string> { "A. 选项1", "B. 选项2", "C. 选项3", "D. 选项4" }),
                    CorrectAnswer = "A",
                    Type = QuestionType.SingleChoice,
                    Subject = "综合",
                    Category = "测试"
                };
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService { ActiveUser = user }, new FakeAiTutorService());

                // 1. 敏捷秒杀 (AgileMastery): 正确且用时极速 (8s <= 15s)
                var agileResult = await practiceService.SubmitAnswerAsync(question, "A", timeTakenSeconds: 8, currentCombo: 0, targetUserId: user.Id);
                Assert.True(agileResult.IsCorrect);
                Assert.Equal("AgileMastery", agileResult.CognitiveClassification);
                Assert.Contains("敏捷秒杀", agileResult.CognitiveBadgeText);

                // 2. 稳健深思 (SteadyMastery): 正确但用时深思 (25s > 15s)
                var steadyResult = await practiceService.SubmitAnswerAsync(question, "A", timeTakenSeconds: 25, currentCombo: 0, targetUserId: user.Id);
                Assert.True(steadyResult.IsCorrect);
                Assert.Equal("SteadyMastery", steadyResult.CognitiveClassification);
                Assert.Contains("稳健深思", steadyResult.CognitiveBadgeText);

                // 3. 急躁粗心 (Careless): 错误且用时仓促 (6s <= 15s)
                var carelessResult = await practiceService.SubmitAnswerAsync(question, "B", timeTakenSeconds: 6, currentCombo: 0, targetUserId: user.Id);
                Assert.False(carelessResult.IsCorrect);
                Assert.Equal("Careless", carelessResult.CognitiveClassification);
                Assert.Contains("急躁粗心", carelessResult.CognitiveBadgeText);

                // 4. 攻坚盲区 (Struggling): 错误且耗时艰难 (30s > 15s)
                var strugglingResult = await practiceService.SubmitAnswerAsync(question, "B", timeTakenSeconds: 30, currentCombo: 0, targetUserId: user.Id);
                Assert.False(strugglingResult.IsCorrect);
                Assert.Equal("Struggling", strugglingResult.CognitiveClassification);
                Assert.Contains("攻坚盲区", strugglingResult.CognitiveBadgeText);
            }
        }

        [Fact]
        public async Task PracticeService_SubmitBatchPaper_ShouldAccuratelyProfileFourQuadrantsAndPacingAdvice()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            using (var context = new AppDbContext(options))
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "CognitivePacingStudent",
                    Role = UserRole.Student
                };
                context.Users.Add(user);

                var q1 = new Question { Id = Guid.NewGuid(), Stem = "题1", OptionsJson = JsonSerializer.Serialize(new[] { "A. 1", "B. 2" }), CorrectAnswer = "A", Type = QuestionType.SingleChoice, Subject = "数学", Category = "代数" };
                var q2 = new Question { Id = Guid.NewGuid(), Stem = "题2", OptionsJson = JsonSerializer.Serialize(new[] { "A. 1", "B. 2" }), CorrectAnswer = "A", Type = QuestionType.SingleChoice, Subject = "数学", Category = "代数" };
                var q3 = new Question { Id = Guid.NewGuid(), Stem = "题3", OptionsJson = JsonSerializer.Serialize(new[] { "A. 1", "B. 2" }), CorrectAnswer = "A", Type = QuestionType.SingleChoice, Subject = "数学", Category = "代数" };
                var q4 = new Question { Id = Guid.NewGuid(), Stem = "题4", OptionsJson = JsonSerializer.Serialize(new[] { "A. 1", "B. 2" }), CorrectAnswer = "A", Type = QuestionType.SingleChoice, Subject = "数学", Category = "代数" };
                context.Questions.AddRange(q1, q2, q3, q4);
                await context.SaveChangesAsync();

                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService { ActiveUser = user }, new FakeAiTutorService());

                var batchSubmissions = new List<(Question Question, string UserAnswer, int TimeTakenSeconds)>
                {
                    // 快速正确 (8s) -> 敏捷秒杀
                    (q1, "A", 8),
                    // 慢速正确 (35s) -> 稳健深思
                    (q2, "A", 35),
                    // 快速错误 (9s) -> 急躁粗心
                    (q3, "B", 9),
                    // 慢速错误 (40s) -> 攻坚盲区
                    (q4, "B", 40)
                };

                var paperResult = await practiceService.SubmitBatchPaperAsync(batchSubmissions, initialCombo: 0, targetUserId: user.Id);

                // 验证四象限聚合计算
                Assert.Equal(1, paperResult.AgileMasteryCount);
                Assert.Equal(1, paperResult.SteadyMasteryCount);
                Assert.Equal(1, paperResult.CarelessCount);
                Assert.Equal(1, paperResult.StrugglingCount);
                Assert.True(paperResult.AverageTimePerQuestionSeconds > 0);
                Assert.Contains("节奏", paperResult.CognitivePaceAdvice);
            }
        }

        [Fact]
        public async Task PracticeService_GetPracticeAnalytics_ShouldAggregateHistoricalCognitiveDistribution()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            using (var context = new AppDbContext(options))
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "AnalyticsCognitiveStudent",
                    Role = UserRole.Student
                };
                context.Users.Add(user);

                var q1 = new Question { Id = Guid.NewGuid(), Stem = "题1", OptionsJson = JsonSerializer.Serialize(new[] { "A", "B" }), CorrectAnswer = "A", Type = QuestionType.SingleChoice, Subject = "化学", Category = "有机" };
                var q2 = new Question { Id = Guid.NewGuid(), Stem = "题2", OptionsJson = JsonSerializer.Serialize(new[] { "A", "B" }), CorrectAnswer = "A", Type = QuestionType.SingleChoice, Subject = "化学", Category = "有机" };
                context.Questions.AddRange(q1, q2);

                // 添加做题历史记录
                context.PracticeRecords.Add(new PracticeRecord
                {
                    UserId = user.Id,
                    QuestionId = q1.Id,
                    UserAnswer = "A",
                    IsCorrect = true,
                    TimeTakenSeconds = 6, // 快速正确
                    AnsweredAt = DateTime.UtcNow
                });
                context.PracticeRecords.Add(new PracticeRecord
                {
                    UserId = user.Id,
                    QuestionId = q2.Id,
                    UserAnswer = "B",
                    IsCorrect = false,
                    TimeTakenSeconds = 5, // 快速粗心错误
                    AnsweredAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync();

                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService { ActiveUser = user }, new FakeAiTutorService());

                var analytics = await practiceService.GetPracticeAnalyticsAsync(user.Id);

                Assert.Equal(1, analytics.AgileMasteryCount);
                Assert.Equal(1, analytics.CarelessCount);
                Assert.False(string.IsNullOrWhiteSpace(analytics.CognitivePaceAdvice));
            }
        }

        #endregion
    }
}
