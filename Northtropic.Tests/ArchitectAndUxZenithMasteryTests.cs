using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxZenithMasteryTests
    {
        public class SqliteDbContextFactory : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;
            public SqliteDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
            public AppDbContext CreateDbContext() => new AppDbContext(_options);
        }

        private static (SqliteConnection Connection, DbContextOptions<AppDbContext> Options) CreateSharedInMemoryDb()
        {
            var dbName = $"memdb_{Guid.NewGuid():N}";
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

        #region 1. 系统架构师：APM 遥测事件流多维过滤与环形队列生命周期测试

        [Fact]
        public void SystemHealthService_TelemetryEvents_RecordingAndFiltering_OperatesFlawlessly()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);
            var service = new SystemHealthService(factory.CreateDbContext(), factory);

            // 记录多维事件
            service.RecordArchitectureEvent("Backup", "Success", "快照备份成功", 15.0);
            service.RecordArchitectureEvent("Backup", "Error", "快照备份校验失败", 45.0);
            service.RecordArchitectureEvent("SelfHealing", "Warning", "碎片率偏高需要重整", 8.0);
            service.RecordArchitectureEvent("Security", "Info", "拦截恶意爆破访问", 1.5);
            service.RecordArchitectureEvent("SelfHealing", "Success", "VACUUM 执行成功", 120.0);

            // 1. 无过滤查询全量
            var allEvents = service.GetRecentArchitectureEvents();
            Assert.True(allEvents.Count >= 5);

            // 2. 按分类过滤: Backup
            var backupEvents = service.GetRecentArchitectureEvents(category: "Backup");
            Assert.True(backupEvents.Count >= 2);
            Assert.All(backupEvents, e => Assert.Equal("Backup", e.Category, ignoreCase: true));

            // 3. 按级别过滤: Error
            var errorEvents = service.GetRecentArchitectureEvents(level: "Error");
            Assert.True(errorEvents.Count >= 1);
            Assert.All(errorEvents, e => Assert.Equal("Error", e.Level, ignoreCase: true));

            // 4. 双重过滤: Category = SelfHealing, Level = Success
            var selfHealingSuccess = service.GetRecentArchitectureEvents(category: "SelfHealing", level: "Success");
            Assert.True(selfHealingSuccess.Count >= 1);
            Assert.All(selfHealingSuccess, e =>
            {
                Assert.Equal("SelfHealing", e.Category, ignoreCase: true);
                Assert.Equal("Success", e.Level, ignoreCase: true);
            });

            // 5. 限制返回最大条数
            var limitedEvents = service.GetRecentArchitectureEvents(maxCount: 2);
            Assert.True(limitedEvents.Count <= 2);
        }

        #endregion

        #region 2. 系统架构师：APM 遥测摘要与系统可靠性指数 (Reliability Score) 测试

        [Fact]
        public void SystemHealthService_TelemetrySummary_CalculatesReliabilityScoreAndAverages()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);
            var service = new SystemHealthService(factory.CreateDbContext(), factory);

            // 注入已知事件以验证统计
            service.RecordArchitectureEvent("Performance", "Success", "查询延迟正常", 5.0);
            service.RecordArchitectureEvent("Performance", "Warning", "查询延迟略高", 25.0);

            var summary = service.GetArchitectureTelemetrySummary();
            Assert.NotNull(summary);
            Assert.True(summary.TotalEvents >= 2);
            Assert.True(summary.SuccessCount >= 1);
            Assert.True(summary.WarningCount >= 1);
            Assert.True(summary.ReliabilityScore >= 0.0 && summary.ReliabilityScore <= 100.0);
            Assert.NotNull(summary.AverageDurationMs);
            Assert.True(summary.AverageDurationMs > 0);
        }

        #endregion

        #region 3. 系统架构师：自适应 SQLite 维护评估 (Adaptive Maintenance Plan) 测试

        [Fact]
        public async Task SystemHealthService_EvaluateAdaptiveMaintenancePlanAsync_ProducesValidPlan()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using var conn = connection;
            var factory = new SqliteDbContextFactory(options);
            var service = new SystemHealthService(factory.CreateDbContext(), factory);

            var plan = await service.EvaluateAdaptiveMaintenancePlanAsync();
            Assert.NotNull(plan);
            Assert.Contains(plan.UrgencyLevel, new[] { "Low", "Medium", "High", "Critical" });
            Assert.NotNull(plan.ActionReasons);
            Assert.True(plan.ActionReasons.Count > 0);
        }

        #endregion

        #region 4. 用户体验专家：对数数学表达式变量及等价换底智能容差匹配

        [Theory]
        // 自然对数与常用对数变量表达
        [InlineData("\\ln(x)", "\\log_e(x)")]
        [InlineData("\\ln{x}", "\\log_{e}{x}")]
        [InlineData("\\ln x", "\\log_e x")]
        [InlineData("\\lg(x)", "\\log_{10}(x)")]
        [InlineData("\\lg{x}", "\\log_{10}{x}")]
        [InlineData("\\lg x", "\\log_{10} x")]
        // 通用对数基底与真数
        [InlineData("\\log_2(x)", "\\log_{2}{x}")]
        [InlineData("\\log_2 x", "\\log_{2}(x)")]
        [InlineData("log(2,x)", "\\log_2(x)")]
        [InlineData("log(2, x)", "\\log_{2}{x}")]
        [InlineData("\\log_a(b)", "\\log_{a}{b}")]
        // 数值对数求值与化简等价
        [InlineData("\\log_2(8)", "3")]
        [InlineData("\\log_{2}{8}", "3")]
        [InlineData("\\log_2 8", "3")]
        [InlineData("\\lg(100)", "2")]
        [InlineData("\\lg(10)", "1")]
        [InlineData("\\lg(1)", "0")]
        [InlineData("\\ln(e)", "1")]
        public void PracticeService_LogarithmicExpressions_EquivalenceMatch_Succeeds(string userAns, string correctAns)
        {
            var isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}' as mathematically equivalent logarithm.");
        }

        #endregion

        #region 5. 用户体验专家：物理理科复合量纲与国际单位制符号等价匹配

        [Theory]
        // 加速度复合单位
        [InlineData("9.8 m/s^2", "9.8米每二次方秒")]
        [InlineData("9.8 m/s²", "9.8 m·s^-2")]
        [InlineData("9.80 m*s^{-2}", "9.8 m/s^2")]
        [InlineData("9.8米每秒的平方", "9.8 m/s^2")]
        // 速度复合单位
        [InlineData("10 m/s", "10米/秒")]
        [InlineData("10.0 m·s^-1", "10 m/s")]
        [InlineData("10 m*s^{-1}", "10米每秒")]
        // 密度复合单位
        [InlineData("1000 kg/m^3", "1000千克每立方米")]
        [InlineData("1.0×10^3 kg·m^-3", "1000 kg/m³")]
        [InlineData("1.0 g/cm^3", "1.0克每立方厘米")]
        // 压强复合单位
        [InlineData("100 Pa", "100牛每平方米")]
        [InlineData("100.0帕", "100 Pa")]
        [InlineData("100 n/m^2", "100 Pa")]
        // 功率复合单位
        [InlineData("50 W", "50焦每秒")]
        [InlineData("50.0瓦特", "50 j/s")]
        public void PracticeService_PhysicalCompoundUnits_EquivalenceMatch_Succeeds(string userAns, string correctAns)
        {
            var isMatch = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.True(isMatch, $"Expected '{userAns}' to match '{correctAns}' with equivalent physical compound units.");
        }

        #endregion

        #region 6. 用户体验专家：教育学等价判定理由生成 (GenerateEquivalentMatchReason)

        [Fact]
        public void PracticeService_GenerateEquivalentMatchReason_LogarithmFeedback_ProvidesPedagogicalGuidance()
        {
            var q = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "数学",
                Stem = "已知函数 f(x)，求导数结果",
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "\\ln(x)"
            };

            var reason = PracticeService.GenerateEquivalentMatchReason(q, "\\log_e(x)");
            Assert.NotNull(reason);
            Assert.Contains("对数记号与底数等价", reason);
            Assert.Contains("\\ln(x)", reason);
        }

        [Fact]
        public void PracticeService_GenerateEquivalentMatchReason_PhysicsCompoundUnitsFeedback_ProvidesPedagogicalGuidance()
        {
            var q = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "物理",
                Stem = "求物体的加速度大小",
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "9.8 m/s^2"
            };

            var reason = PracticeService.GenerateEquivalentMatchReason(q, "9.8米每二次方秒");
            Assert.NotNull(reason);
            Assert.Contains("物理/科学单位智能对齐等价", reason);
            Assert.Contains("理科复合单位", reason);
            Assert.Contains("9.8 m/s^2", reason);
        }

        #endregion
    }
}
