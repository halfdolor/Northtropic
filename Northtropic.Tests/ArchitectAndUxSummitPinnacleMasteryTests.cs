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
    public class ArchitectAndUxSummitPinnacleMasteryTests
    {
        public class SqliteDbContextFactory : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;
            public SqliteDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
            public AppDbContext CreateDbContext() => new AppDbContext(_options);
        }

        private static (SqliteConnection Connection, DbContextOptions<AppDbContext> Options) CreateSharedInMemoryDb()
        {
            var dbName = $"memdb_summit_{Guid.NewGuid():N}";
            var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connectionString);
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;

            using (var ctx = new AppDbContext(options))
            {
                ctx.Database.EnsureCreated();
            }

            return (connection, options);
        }

        #region 1. 系统架构师：SQLite 自适应自愈闭环维护引擎 (ExecuteAdaptiveMaintenancePlanAsync)

        [Fact]
        public async Task SystemHealthService_ExecuteAdaptiveMaintenancePlan_ExecutesFullLifecycleSafely()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "northtropic_heal_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var dbFilePath = Path.Combine(tempDir, "northtropic_heal.db");

            try
            {
                var connStr = $"Data Source={dbFilePath};Mode=ReadWriteCreate;Cache=Shared";
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connStr)
                    .Options;

                var factory = new SqliteDbContextFactory(options);
                using (var setupCtx = factory.CreateDbContext())
                {
                    await setupCtx.Database.EnsureCreatedAsync();
                    // 开启 WAL 模式以支持 Checkpoint
                    await setupCtx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

                    // 写入测试数据
                    for (int i = 0; i < 20; i++)
                    {
                        setupCtx.Questions.Add(new Question
                        {
                            Stem = $"自适应自愈基准测试题目 #{i}",
                            CorrectAnswer = "A",
                            Category = "架构可靠性",
                            Subject = "系统工程",
                            Type = QuestionType.SingleChoice,
                            Difficulty = 3
                        });
                    }
                    await setupCtx.SaveChangesAsync();
                }

                using var dummyCtx = factory.CreateDbContext();
                var healthService = new SystemHealthService(dummyCtx, factory);

                // 评估维护计划
                var plan = await healthService.EvaluateAdaptiveMaintenancePlanAsync();
                Assert.NotNull(plan);

                // 自定义一个需要完整 WAL截断、VACUUM 与 索引优化的维护计划
                var fullMaintenancePlan = new AdaptiveMaintenancePlanDto
                {
                    RequiresWalCheckpoint = true,
                    RequiresVacuum = true,
                    RequiresOptimization = true,
                    UrgencyLevel = "High",
                    ActionReasons = new List<string> { "高吞吐量写入后执行定时存储碎片规整与直方图重建" }
                };

                // 执行维护计划
                var result = await healthService.ExecuteAdaptiveMaintenancePlanAsync(fullMaintenancePlan);

                Assert.NotNull(result);
                Assert.True(result.Success, $"自愈维护执行失败: {result.Message}");
                Assert.True(result.ElapsedMilliseconds > 0);
                Assert.True(result.IntegrityVerified);
                Assert.Contains("WAL 日志截断归档", string.Join(" | ", result.ExecutedActions));
                Assert.Contains("SQLite 物理存储碎片重整 (VACUUM)", string.Join(" | ", result.ExecutedActions));
                Assert.Contains("查询优化器与直方图统计重建", string.Join(" | ", result.ExecutedActions));

                // 验证遥测事件流中产生对应的 "SelfHealing" 成功事件
                var selfHealingEvents = healthService.GetRecentArchitectureEvents(category: "SelfHealing", level: "Success");
                Assert.NotEmpty(selfHealingEvents);
                Assert.Contains(selfHealingEvents, e => e.Message.Contains("自适应自愈维护执行成功"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task SystemHealthService_ExecuteAdaptiveMaintenancePlan_DefaultEvaluationFallback_Succeeds()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);
            using var dummyCtx = factory.CreateDbContext();
            var healthService = new SystemHealthService(dummyCtx, factory);

            // 当传入 null 时，引擎自动触发 EvaluateAdaptiveMaintenancePlanAsync 评估并安全执行
            var result = await healthService.ExecuteAdaptiveMaintenancePlanAsync(null);

            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.True(result.IntegrityVerified);
            Assert.True(result.ElapsedMilliseconds >= 0);
        }

        #endregion

        #region 2. 系统架构师：备份快照全量实体表与行数深度对齐校验 (Backup Parity Verification)

        [Fact]
        public async Task SystemHealthService_VerifyBackupSnapshot_InspectsTableRowCountsAndVerifiesParity()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "northtropic_backup_parity_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var dbFileName = "parity_test_snapshot.db";
            var fullPath = Path.Combine(tempDir, dbFileName);

            try
            {
                var connStr = $"Data Source={fullPath}";
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connStr)
                    .Options;

                // 创建包含具体行数的实体库
                await using (var ctx = new AppDbContext(options))
                {
                    await ctx.Database.EnsureCreatedAsync();
                    ctx.Users.Add(new User { Username = "architect_user", Password = "secret_hash" });
                    ctx.Questions.Add(new Question { Stem = "光速是多少？", CorrectAnswer = "3×10^8 m/s", Category = "物理", Difficulty = 2 });
                    ctx.Questions.Add(new Question { Stem = "绝对零度是多少？", CorrectAnswer = "-273.15℃", Category = "物理", Difficulty = 3 });
                    await ctx.SaveChangesAsync();
                }
                SqliteConnection.ClearAllPools();

                var (memConn, memOptions) = CreateSharedInMemoryDb();
                using (memConn)
                {
                    var factory = new SqliteDbContextFactory(memOptions);
                    using var dummyContext = factory.CreateDbContext();
                    var healthService = new SystemHealthService(dummyContext, factory);

                    var result = await healthService.VerifyBackupSnapshotAsync(dbFileName, tempDir);

                    Assert.NotNull(result);
                    Assert.True(result.IsHealthy);
                    Assert.True(result.ParityVerified);
                    Assert.True(result.CoreTablesPresent);
                    Assert.True(result.TableRowCounts["Users"] >= 1);
                    Assert.Equal(2, result.TableRowCounts["Questions"]);
                    Assert.True(result.TableRowCounts.ContainsKey("PracticeRecords"));
                    Assert.True(result.TableRowCounts.ContainsKey("ErrorItems"));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        #endregion

        #region 3. 用户体验专家：题库管理多维题型与难度分级检索过滤测试 (Question Management Multi-Filtering)

        [Fact]
        public async Task QuestionManagementService_MultiFiltering_ByTypeAndDifficulty_FiltersAccurately()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);

            var superAdminId = Guid.NewGuid();
            var teacherUserId = Guid.NewGuid();

            using (var ctx = factory.CreateDbContext())
            {
                ctx.Users.Add(new User { Id = superAdminId, Username = "admin", Role = UserRole.SuperAdmin });
                ctx.Users.Add(new User { Id = teacherUserId, Username = "teacher", Role = UserRole.Teacher });

                // 题库样本：涵盖不同题型与难度
                ctx.Questions.AddRange(new[]
                {
                    new Question
                    {
                        Id = Guid.NewGuid(),
                        Stem = "单选题入门题：1+1=？",
                        Type = QuestionType.SingleChoice,
                        Difficulty = 1,
                        Subject = "初中数学",
                        Category = "代数",
                        IsPublic = true,
                        CreatedByUserId = teacherUserId
                    },
                    new Question
                    {
                        Id = Guid.NewGuid(),
                        Stem = "多选题巩固题：下列属于质数的是？",
                        Type = QuestionType.MultipleChoice,
                        Difficulty = 2,
                        Subject = "初中数学",
                        Category = "数论",
                        IsPublic = true,
                        CreatedByUserId = teacherUserId
                    },
                    new Question
                    {
                        Id = Guid.NewGuid(),
                        Stem = "填空题能力题：一元二次方程判别式为？",
                        Type = QuestionType.FillInBlank,
                        Difficulty = 3,
                        Subject = "初中数学",
                        Category = "方程",
                        IsPublic = true,
                        CreatedByUserId = teacherUserId
                    },
                    new Question
                    {
                        Id = Guid.NewGuid(),
                        Stem = "简答题进阶题：阐述牛顿第一运动定律的内容",
                        Type = QuestionType.ShortAnswer,
                        Difficulty = 4,
                        Subject = "初中物理",
                        Category = "力学",
                        IsPublic = true,
                        CreatedByUserId = teacherUserId
                    },
                    new Question
                    {
                        Id = Guid.NewGuid(),
                        Stem = "综合解析大题冲刺压轴：解析几何抛物线焦半径综合题",
                        Type = QuestionType.EssayAnalysis,
                        Difficulty = 5,
                        Subject = "高中数学",
                        Category = "解析几何",
                        IsPublic = true,
                        CreatedByUserId = teacherUserId
                    }
                });
                await ctx.SaveChangesAsync();
            }

            var service = new QuestionManagementService(factory.CreateDbContext(), factory);

            // 1. 无额外过滤：查询全部
            var allQuestions = await service.GetQuestionsForManagementAsync(superAdminId);
            Assert.Equal(5, allQuestions.Count);

            // 2. 按题型过滤：单选题
            var singleChoiceQuestions = await service.GetQuestionsForManagementAsync(superAdminId, questionType: QuestionType.SingleChoice);
            Assert.Single(singleChoiceQuestions);
            Assert.Equal(QuestionType.SingleChoice, singleChoiceQuestions[0].Type);

            // 3. 按题型过滤：综合解析大题
            var essayQuestions = await service.GetQuestionsForManagementAsync(superAdminId, questionType: QuestionType.EssayAnalysis);
            Assert.Single(essayQuestions);
            Assert.Equal(QuestionType.EssayAnalysis, essayQuestions[0].Type);

            // 4. 按难度过滤：5星压轴
            var diff5Questions = await service.GetQuestionsForManagementAsync(superAdminId, difficulty: 5);
            Assert.Single(diff5Questions);
            Assert.Equal(5, diff5Questions[0].Difficulty);

            // 5. 组合过滤：初中数学 + 3星难度
            var combinedQuestions = await service.GetQuestionsForManagementAsync(superAdminId, subject: "初中数学", difficulty: 3);
            Assert.Single(combinedQuestions);
            Assert.Equal(QuestionType.FillInBlank, combinedQuestions[0].Type);
            Assert.Equal(3, combinedQuestions[0].Difficulty);

            // 6. 组合过滤无匹配：初中物理 + 5星难度
            var emptyResults = await service.GetQuestionsForManagementAsync(superAdminId, subject: "初中物理", difficulty: 5);
            Assert.Empty(emptyResults);
        }

        #endregion
    }
}
