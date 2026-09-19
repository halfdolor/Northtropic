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
    public class ArchitectAndUxZenithTranscendentTests
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

        #region 1. 物理科学量纲与等价换算扩展 (速度、动量冲量、磁感应强度)

        [Theory]
        // 速度换算 (km/h <=> m/s)
        [InlineData("72 km/h", "20 m/s", true)]
        [InlineData("36 km/h", "10 m/s", true)]
        [InlineData("18 千米/小时", "5 米/秒", true)]
        [InlineData("108 km/hr", "30 m/s", true)]
        [InlineData("20 m/s", "72 公里/小时", true)]
        // 动量冲量换算 (N·s <=> kg·m/s)
        [InlineData("10 N*s", "10 kg*m/s", true)]
        [InlineData("5 N·s", "5 kg·m/s", true)]
        [InlineData("12 牛·秒", "12 千克·米/秒", true)]
        [InlineData("25 N s", "25 kg*m/s", true)]
        // 磁感应强度 / 磁通密度 (T <=> Wb/m^2)
        [InlineData("0.5 特斯拉", "0.5 Wb/m^2", true)]
        [InlineData("2 特", "2 韦伯/平方米", true)]
        [InlineData("500 mT", "0.5 Wb/m^2", true)]
        [InlineData("1 毫特", "0.001 Wb/m²", true)]
        // 防跨量纲碰撞
        [InlineData("20 m/s", "20 N*s", false)]
        [InlineData("10 kg*m/s", "10 Wb/m^2", false)]
        public void CheckScientificUnitMultiplierEquivalence_SpeedMomentumMag_EvaluatesCorrectly(string u, string c, bool expected)
        {
            bool actual = PracticeService.CheckScientificUnitMultiplierEquivalence(u, c);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void CheckAnswerCorrectness_PhysicsSpeedAndMomentum_EvaluatesFillInBlank()
        {
            var qSpeed = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "物理",
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "20 m/s",
                Stem = "汽车在平直公路上匀速行驶，速度为____"
            };

            Assert.True(PracticeService.CheckAnswerCorrectness(qSpeed, "72 km/h"));
            Assert.True(PracticeService.CheckAnswerCorrectness(qSpeed, "72 千米/小时"));

            var qMomentum = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "物理",
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "10 kg*m/s",
                Stem = "物体的动量为____"
            };

            Assert.True(PracticeService.CheckAnswerCorrectness(qMomentum, "10 N*s"));
            Assert.True(PracticeService.CheckAnswerCorrectness(qMomentum, "10 牛·秒"));
        }

        #endregion

        #region 2. 数学不等式与区间智能等价 (绝对值不等式、逻辑连词复合不等式)

        [Theory]
        // 绝对值不等式
        [InlineData("|x| <= 3", "[-3, 3]", true)]
        [InlineData("|x| <= 3", "-3 <= x <= 3", true)]
        [InlineData("|x| < 5", "(-5, 5)", true)]
        [InlineData("|x| < 5", "-5 < x < 5", true)]
        [InlineData("|x| >= 2", "(-∞, -2] ∪ [2, +∞)", true)]
        [InlineData("|x| > 4", "(-∞, -4) ∪ (4, +∞)", true)]
        // 逻辑与连词复合不等式
        [InlineData("x > 1 且 x < 5", "(1, 5)", true)]
        [InlineData("x > 1 且 x < 5", "1 < x < 5", true)]
        [InlineData("x >= 2 and x <= 6", "[2, 6]", true)]
        [InlineData("1 < x 且 x < 5", "(1, 5)", true)]
        [InlineData("x >= -1 且 x <= 3", "[-1, 3]", true)]
        public void CheckMathIntervalOrInequalityEquivalence_EvaluatesCorrectly(string user, string correct, bool expected)
        {
            var q = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "数学",
                Type = QuestionType.FillInBlank,
                CorrectAnswer = correct,
                Stem = "解下列不等式或求定义域"
            };
            bool actual = PracticeService.CheckAnswerCorrectness(q, user);
            Assert.Equal(expected, actual);
        }

        #endregion

        #region 3. 化学同义词中英文映射扩展 (重铬酸钾、草酸、葡萄糖、有机物等)

        [Theory]
        [InlineData("重铬酸钾", "K2Cr2O7")]
        [InlineData("草酸", "H2C2O4")]
        [InlineData("葡萄糖", "C6H12O6")]
        [InlineData("蔗糖", "C12H22O11")]
        [InlineData("苯", "C6H6")]
        [InlineData("甲苯", "C7H8")]
        [InlineData("乙酸乙酯", "CH3COOC2H5")]
        [InlineData("碘化钾", "KI")]
        [InlineData("氯化钡", "BaCl2")]
        public void CheckChemicalSynonyms_EvaluatesFillInBlank(string cnName, string formula)
        {
            var q = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "化学",
                Type = QuestionType.FillInBlank,
                CorrectAnswer = formula,
                Stem = "请写出该物质的化学式"
            };

            Assert.True(PracticeService.CheckAnswerCorrectness(q, cnName));
        }

        #endregion

        #region 4. 系统架构师：全库 12 类业务表拓扑无死角自愈清洗测试

        [Fact]
        public async Task SystemHealthService_FullTopologyOrphans_AuditAndPurgeWorksAccurately()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                using (var ctx = factory.CreateDbContext())
                {
                    // 创建合法基准用户和题目
                    var validUser = new User { Id = Guid.NewGuid(), Username = "NormalStudent", Role = UserRole.Student };
                    var validQuestion = new Question { Id = Guid.NewGuid(), Stem = "测试试题", Subject = "数学" };
                    var validStudyPlan = new StudyPlan { Id = Guid.NewGuid(), UserId = validUser.Id, Title = "中考冲刺计划" };
                    var validAchievement = new Achievement { Id = Guid.NewGuid(), Title = "初试锋芒", Code = "first_step", Description = "完成首次练习" };

                    ctx.Users.Add(validUser);
                    ctx.Questions.Add(validQuestion);
                    ctx.StudyPlans.Add(validStudyPlan);
                    ctx.Achievements.Add(validAchievement);

                    // 1. 合法关联数据
                    ctx.StudyPlanTasks.Add(new StudyPlanTask { Id = Guid.NewGuid(), StudyPlanId = validStudyPlan.Id, Title = "函数专题" });
                    ctx.UserAchievements.Add(new UserAchievement { Id = Guid.NewGuid(), UserId = validUser.Id, AchievementId = validAchievement.Id });
                    ctx.EvolutionClosedLoopInsights.Add(new EvolutionClosedLoopInsight { Id = Guid.NewGuid(), UserId = validUser.Id, EvaluationStatus = ProgressEvaluationStatus.SteadilyImproving });
                    ctx.LlmGenerationLogs.Add(new LlmGenerationLog { Id = Guid.NewGuid(), UserId = validUser.Id, QuestionId = validQuestion.Id, ModelName = "LocalQwen" });

                    await ctx.SaveChangesAsync();

                    // 临时关闭外键约束模拟级联丢失产生的孤儿历史数据
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA foreign_keys = OFF;";
                        cmd.ExecuteNonQuery();
                    }

                    // 2. 注入 4 种新型孤儿数据
                    var nonExistentStudyPlanId = Guid.NewGuid();
                    var nonExistentUserId = Guid.NewGuid();

                    // 孤儿子任务 (指向不存在的计划)
                    ctx.StudyPlanTasks.Add(new StudyPlanTask { Id = Guid.NewGuid(), StudyPlanId = nonExistentStudyPlanId, Title = "孤儿子任务" });
                    // 孤儿成就 (指向不存在的用户)
                    ctx.UserAchievements.Add(new UserAchievement { Id = Guid.NewGuid(), UserId = nonExistentUserId, AchievementId = validAchievement.Id });
                    // 孤儿演化洞察 (指向不存在的用户)
                    ctx.EvolutionClosedLoopInsights.Add(new EvolutionClosedLoopInsight { Id = Guid.NewGuid(), UserId = nonExistentUserId, EvaluationStatus = ProgressEvaluationStatus.Regressing });
                    // 孤儿大模型调用日志 (指向不存在的用户)
                    ctx.LlmGenerationLogs.Add(new LlmGenerationLog { Id = Guid.NewGuid(), UserId = nonExistentUserId, QuestionId = validQuestion.Id, ModelName = "LocalQwen" });

                    await ctx.SaveChangesAsync();
                }

                // 实例化 SystemHealthService 进行拓扑审计
                using var healthCtx = factory.CreateDbContext();
                var healthService = new SystemHealthService(healthCtx, factory);

                var auditResult = await healthService.AuditDataIntegrityAsync();
                Assert.True(auditResult.TotalIssuesCount >= 4);
                Assert.Equal(1, auditResult.TotalOrphanStudyPlanTasks);
                Assert.Equal(1, auditResult.TotalOrphanUserAchievements);
                Assert.Equal(1, auditResult.TotalOrphanInsights);
                Assert.Equal(1, auditResult.TotalOrphanLlmLogs);

                // 执行一键自愈净化
                var purgeResult = await healthService.PurgeOrphanedRecordsAsync();
                Assert.True(purgeResult.Success);
                Assert.True(purgeResult.TotalPurgedCount >= 4);
                Assert.Equal(1, purgeResult.PurgedStudyPlanTasksCount);
                Assert.Equal(1, purgeResult.PurgedUserAchievementsCount);
                Assert.Equal(1, purgeResult.PurgedInsightsCount);
                Assert.Equal(1, purgeResult.PurgedLlmLogsCount);

                // 复查审计：孤儿数全清为 0
                var postAudit = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, postAudit.TotalIssuesCount);
                Assert.Equal(0, postAudit.TotalOrphanStudyPlanTasks);
                Assert.Equal(0, postAudit.TotalOrphanUserAchievements);
                Assert.Equal(0, postAudit.TotalOrphanInsights);
                Assert.Equal(0, postAudit.TotalOrphanLlmLogs);

                // 验证合法业务数据完好无损
                using (var verifyCtx = factory.CreateDbContext())
                {
                    Assert.Equal(1, await verifyCtx.StudyPlanTasks.CountAsync());
                    Assert.Equal(1, await verifyCtx.UserAchievements.CountAsync());
                    Assert.Equal(1, await verifyCtx.EvolutionClosedLoopInsights.CountAsync());
                    Assert.Equal(1, await verifyCtx.LlmGenerationLogs.CountAsync());
                }
            }
        }

        #endregion

        #region 5. 用户体验专家：错题本艾宾浩斯复习紧迫度层级筛选

        [Fact]
        public void ErrorBook_RetentionHealthScore_TierFilteringWorksAccurately()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                using var ctx = factory.CreateDbContext();
                var errorBookService = new ErrorBookService(ctx, null!, null!, factory);

                var now = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

                // 1. 紧急抢救错题: revisionCount=0 (推荐间隔 12h)，过去 36 小时未复习 -> 留存率 ~12.5% (< 40)
                var urgentItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = now.AddHours(-36),
                    RevisionCount = 0,
                    IsMastered = false
                };

                // 2. 临界巩固错题: revisionCount=0 (推荐间隔 12h)，过去 12 小时未复习 -> 留存率 ~50% (40-70)
                var consolidateItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = now.AddHours(-12),
                    RevisionCount = 0,
                    IsMastered = false
                };

                // 3. 稳固记忆错题: revisionCount=0 (推荐间隔 12h)，过去 2 小时刚建立 -> 留存率 ~89% (> 70)
                var solidItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = now.AddHours(-2),
                    RevisionCount = 0,
                    IsMastered = false
                };

                var items = new List<ErrorItem> { urgentItem, consolidateItem, solidItem };

                double urgentScore = errorBookService.CalculateRetentionHealthScore(urgentItem, now);
                double consolidateScore = errorBookService.CalculateRetentionHealthScore(consolidateItem, now);
                double solidScore = errorBookService.CalculateRetentionHealthScore(solidItem, now);

                Assert.True(urgentScore < 40, $"urgentScore should be < 40, actual: {urgentScore}");
                Assert.True(consolidateScore >= 40 && consolidateScore <= 70, $"consolidateScore should be 40-70, actual: {consolidateScore}");
                Assert.True(solidScore > 70, $"solidScore should be > 70, actual: {solidScore}");

                // 模拟 ErrorBook FilteredUnmasteredItems 过滤行为
                var filteredUrgent = items.Where(i => errorBookService.CalculateRetentionHealthScore(i, now) < 40).ToList();
                Assert.Single(filteredUrgent);
                Assert.Equal(urgentItem.Id, filteredUrgent[0].Id);

                var filteredConsolidate = items.Where(i =>
                {
                    var s = errorBookService.CalculateRetentionHealthScore(i, now);
                    return s >= 40 && s <= 70;
                }).ToList();
                Assert.Single(filteredConsolidate);
                Assert.Equal(consolidateItem.Id, filteredConsolidate[0].Id);

                var filteredSolid = items.Where(i => errorBookService.CalculateRetentionHealthScore(i, now) > 70).ToList();
                Assert.Single(filteredSolid);
                Assert.Equal(solidItem.Id, filteredSolid[0].Id);
            }
        }

        #endregion
    }
}
