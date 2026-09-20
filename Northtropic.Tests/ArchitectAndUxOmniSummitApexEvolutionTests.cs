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
    public class ArchitectAndUxOmniSummitApexEvolutionTests
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

        #region 1. 系统架构师：学习任务 (StudyPlanTask) 与用户成就 (UserAchievement) 领域不变量自愈

        [Fact]
        public async Task SystemHealthService_StudyPlanTaskInvariants_AuditedAndHealed()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                var userId = Guid.NewGuid();
                var planId = Guid.NewGuid();
                var t1Id = Guid.NewGuid();
                var t2Id = Guid.NewGuid();

                using (var setupContext = factory.CreateDbContext())
                {
                    var user = new User
                    {
                        Id = userId,
                        Username = "architect_task_student",
                        RegisteredAt = DateTime.UtcNow
                    };
                    setupContext.Users.Add(user);

                    var plan = new StudyPlan
                    {
                        Id = planId,
                        UserId = userId,
                        Title = "高考数学与物理冲刺计划",
                        Status = StudyPlanStatus.Active,
                        CreatedAt = DateTime.UtcNow.AddDays(-5),
                        StartDate = DateTime.UtcNow.AddDays(-5),
                        TargetEndDate = DateTime.UtcNow.AddDays(10)
                    };
                    setupContext.StudyPlans.Add(plan);

                    // 异常任务 1：目标为 0，完成量为负数，时间戳未来漂移
                    var task1 = new StudyPlanTask
                    {
                        Id = t1Id,
                        StudyPlanId = planId,
                        Title = "解析几何精练",
                        Subject = "数学",
                        Category = "解析几何",
                        TargetCount = 0,
                        CompletedCount = -3,
                        IsCompleted = false,
                        CompletedAt = null,
                        CreatedAt = DateTime.Now.AddDays(5) // 未来时间戳
                    };

                    // 异常任务 2：完成量超过目标，未标记完成且 CompletedAt 为空
                    var task2 = new StudyPlanTask
                    {
                        Id = t2Id,
                        StudyPlanId = planId,
                        Title = "天体物理与电磁感应",
                        Subject = "物理",
                        Category = "天体物理",
                        TargetCount = 10,
                        CompletedCount = 15,
                        IsCompleted = false,
                        CompletedAt = null,
                        CreatedAt = DateTime.Now.AddDays(-2)
                    };

                    setupContext.StudyPlanTasks.AddRange(task1, task2);
                    await setupContext.SaveChangesAsync();
                }

                using var dummyContext = factory.CreateDbContext();
                var healthService = new SystemHealthService(dummyContext, factory);

                // 1. 验证审计能精准检出异常任务
                var audit = await healthService.AuditDataIntegrityAsync();
                Assert.True(audit.TotalInvalidStudyPlanTasks >= 2, "审计应精准检出异常 StudyPlanTask 数量");

                // 2. 执行自愈
                int healedCount = await healthService.HealStudyPlanTaskInvariantsAsync();
                Assert.True(healedCount >= 2, "自愈应修复至少 2 个异常任务");

                // 3. 验证自愈后的不变量规范
                using (var verifyContext = factory.CreateDbContext())
                {
                    var healedTask1 = await verifyContext.StudyPlanTasks.FindAsync(t1Id);
                    Assert.NotNull(healedTask1);
                    Assert.True(healedTask1.TargetCount >= 1, "TargetCount 必须规范为 >= 1");
                    Assert.True(healedTask1.CompletedCount >= 0, "CompletedCount 不能为负数");
                    Assert.True(healedTask1.CreatedAt <= DateTime.Now.AddMinutes(1), "未来创建时间戳已被校准");

                    var healedTask2 = await verifyContext.StudyPlanTasks.FindAsync(t2Id);
                    Assert.NotNull(healedTask2);
                    Assert.Equal(10, healedTask2.CompletedCount);
                    Assert.True(healedTask2.IsCompleted, "超额完成的任务必须标记为 IsCompleted");
                    Assert.NotNull(healedTask2.CompletedAt);
                }

                // 4. 再次审计，确认异常数收敛为 0
                var postAudit = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, postAudit.TotalInvalidStudyPlanTasks);
            }
        }

        [Fact]
        public async Task SystemHealthService_StudyPlan_ParentStatusConverged_WhenAllTasksCompleted()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                var userId = Guid.NewGuid();
                var planId = Guid.NewGuid();

                using (var setupContext = factory.CreateDbContext())
                {
                    var user = new User
                    {
                        Id = userId,
                        Username = "architect_convergence_user",
                        RegisteredAt = DateTime.UtcNow
                    };
                    setupContext.Users.Add(user);

                    var plan = new StudyPlan
                    {
                        Id = planId,
                        UserId = userId,
                        Title = "化学有机与热力学专项",
                        Status = StudyPlanStatus.Active,
                        CreatedAt = DateTime.UtcNow.AddDays(-3),
                        StartDate = DateTime.UtcNow.AddDays(-3),
                        TargetEndDate = DateTime.UtcNow.AddDays(5)
                    };
                    setupContext.StudyPlans.Add(plan);

                    var t1 = new StudyPlanTask
                    {
                        Id = Guid.NewGuid(),
                        StudyPlanId = planId,
                        Title = "有机结构简式与分子式",
                        Subject = "化学",
                        Category = "有机化学",
                        TargetCount = 5,
                        CompletedCount = 5,
                        IsCompleted = false // 需自愈闭环为 true
                    };
                    var t2 = new StudyPlanTask
                    {
                        Id = Guid.NewGuid(),
                        StudyPlanId = planId,
                        Title = "热化学方程式与焓变",
                        Subject = "化学",
                        Category = "化学反应热",
                        TargetCount = 5,
                        CompletedCount = 5,
                        IsCompleted = false // 需自愈闭环为 true
                    };
                    setupContext.StudyPlanTasks.AddRange(t1, t2);
                    await setupContext.SaveChangesAsync();
                }

                using var dummyContext = factory.CreateDbContext();
                var healthService = new SystemHealthService(dummyContext, factory);

                await healthService.HealStudyPlanTaskInvariantsAsync();

                using (var verifyContext = factory.CreateDbContext())
                {
                    var plan = await verifyContext.StudyPlans.FindAsync(planId);
                    Assert.NotNull(plan);
                    Assert.Equal(StudyPlanStatus.Completed, plan.Status);
                }
            }
        }

        [Fact]
        public async Task SystemHealthService_UserAchievementInvariants_AuditedAndHealed()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                var userId = Guid.NewGuid();
                var a1Id = Guid.NewGuid();
                var a2Id = Guid.NewGuid();
                var achDefId = Guid.NewGuid();

                using (var setupContext = factory.CreateDbContext())
                {
                    var user = new User
                    {
                        Id = userId,
                        Username = "architect_achieve_user",
                        RegisteredAt = DateTime.Now
                    };
                    setupContext.Users.Add(user);

                    var achDef = new Achievement
                    {
                        Id = achDefId,
                        Code = "TEST_ACH_01",
                        Title = "测试成就"
                    };
                    setupContext.Achievements.Add(achDef);

                    var ach1 = new UserAchievement
                    {
                        Id = a1Id,
                        UserId = userId,
                        AchievementId = achDefId,
                        UnlockedAt = DateTime.Now.AddDays(10) // 未来漂移
                    };
                    var ach2 = new UserAchievement
                    {
                        Id = a2Id,
                        UserId = userId,
                        AchievementId = achDefId,
                        UnlockedAt = new DateTime(1998, 1, 1, 0, 0, 0, DateTimeKind.Local) // 非法史前时间戳
                    };
                    setupContext.UserAchievements.AddRange(ach1, ach2);
                    await setupContext.SaveChangesAsync();
                }

                using var dummyContext = factory.CreateDbContext();
                var healthService = new SystemHealthService(dummyContext, factory);

                var audit = await healthService.AuditDataIntegrityAsync();
                Assert.True(audit.TotalInvalidUserAchievements >= 2, "应检出至少 2 项非法 UserAchievement 时间戳");

                int healed = await healthService.HealUserAchievementStateInvariantsAsync();
                Assert.True(healed >= 2, "应成功自愈至少 2 项 UserAchievement");

                using (var verifyContext = factory.CreateDbContext())
                {
                    var a1 = await verifyContext.UserAchievements.FindAsync(a1Id);
                    var a2 = await verifyContext.UserAchievements.FindAsync(a2Id);
                    Assert.NotNull(a1);
                    Assert.NotNull(a2);
                    Assert.True(a1.UnlockedAt <= DateTime.Now.AddMinutes(1), "未来解锁时间已收敛至合法范围");
                    Assert.True(a2.UnlockedAt >= new DateTime(2020, 1, 1), "史前非法纪元时间已校准");
                }

                var postAudit = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, postAudit.TotalInvalidUserAchievements);
            }
        }

        [Fact]
        public async Task SystemHealthService_HealAllInvariants_ExecutesTasksAndAchievementsOrchestration()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                var userId = Guid.NewGuid();
                var planId = Guid.NewGuid();
                var achDefId = Guid.NewGuid();

                using (var setupContext = factory.CreateDbContext())
                {
                    var user = new User { Id = userId, Username = "orchestration_user", RegisteredAt = DateTime.Now };
                    setupContext.Users.Add(user);

                    var plan = new StudyPlan { Id = planId, UserId = userId, Title = "编排测试计划", Status = StudyPlanStatus.Active };
                    setupContext.StudyPlans.Add(plan);

                    var task = new StudyPlanTask
                    {
                        Id = Guid.NewGuid(),
                        StudyPlanId = planId,
                        Title = "编排任务",
                        Subject = "综合",
                        Category = "测试",
                        TargetCount = 10,
                        CompletedCount = 5,
                        CreatedAt = DateTime.Now.AddDays(5) // 未来时间戳漂移：由 Step 16 进行领域时序不变量自愈
                    };
                    setupContext.StudyPlanTasks.Add(task);

                    var achDef = new Achievement { Id = achDefId, Code = "ORCH_ACH", Title = "编排测试成就" };
                    setupContext.Achievements.Add(achDef);

                    var userAch = new UserAchievement
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        AchievementId = achDefId,
                        UnlockedAt = DateTime.Now.AddDays(10) // 待自愈
                    };
                    setupContext.UserAchievements.Add(userAch);
                    await setupContext.SaveChangesAsync();
                }

                using var dummyContext = factory.CreateDbContext();
                var healthService = new SystemHealthService(dummyContext, factory);

                var healAllResult = await healthService.HealAllInvariantsAsync();
                Assert.NotNull(healAllResult);
                Assert.True(healAllResult.HealedStudyPlanTasksCount >= 1, "全量编排应成功自愈学习任务");
                Assert.True(healAllResult.HealedUserAchievementStatesCount >= 1, "全量编排应成功自愈成就状态");
                Assert.Contains(healAllResult.OperationsExecuted, op => op.Contains("学习计划任务自愈"));
                Assert.Contains(healAllResult.OperationsExecuted, op => op.Contains("用户成就状态时序自愈"));
            }
        }

        #endregion

        #region 2. 用户体验专家：复数代数形式交互容错与虚数单位扩展

        [Theory]
        [InlineData("2 + 3i", "3i + 2", true)]
        [InlineData("z = 2 + 3i", "2 + 3i", true)]
        [InlineData("z = 3i + 2", "2 + 3i", true)]
        [InlineData("Z = -4 + 5i", "5i - 4", true)]
        [InlineData("3 + 4*i", "4i + 3", true)]
        [InlineData("3 + 4j", "3 + 4i", true)]
        [InlineData("5j", "5i", true)]
        [InlineData("1 + \\mathrm{i}", "i + 1", true)]
        [InlineData("2 - 3i", "3i - 2", false)] // 符号相反不应匹配
        public void ComplexNumber_AlgebraicAndPrefixEquivalence(string userAns, string correctAns, bool expectedMatch)
        {
            bool match = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.Equal(expectedMatch, match);
        }

        [Fact]
        public void ComplexNumber_TryParseComplex_ExtractsRealAndImaginary()
        {
            Assert.True(PracticeService.TryParseComplex("z = 3 - 4i", out double r1, out double i1));
            Assert.Equal(3.0, r1);
            Assert.Equal(-4.0, i1);

            Assert.True(PracticeService.TryParseComplex("5i + 2", out double r2, out double i2));
            Assert.Equal(2.0, r2);
            Assert.Equal(5.0, i2);

            Assert.True(PracticeService.TryParseComplex("4j", out double r3, out double i3));
            Assert.Equal(0.0, r3);
            Assert.Equal(4.0, i3);
        }

        #endregion

        #region 3. 用户体验专家：有机化学结构简式、示性式与分子式等价

        [Theory]
        [InlineData("CH3-CH2-OH", "C2H5OH", true)]
        [InlineData("CH3-CH2-OH", "C2H6O", true)]
        [InlineData("CH3CH2OH", "C2H6O", true)]
        [InlineData("CH3-COOH", "CH3COOH", true)]
        [InlineData("CH3-COOH", "C2H4O2", true)]
        [InlineData("CH3CO2H", "CH3COOH", true)]
        [InlineData("(CH3)2CO", "CH3COCH3", true)]
        [InlineData("CH3COCH3", "C3H6O", true)]
        [InlineData("(CH3)2CO", "C3H6O", true)]
        [InlineData("(C2H5)2O", "CH3CH2OCH2CH3", true)]
        [InlineData("C2H5OC2H5", "C4H10O", true)]
        [InlineData("CH2=CH2", "C2H4", true)]
        [InlineData("H2C=CH2", "CH2=CH2", true)]
        [InlineData("CH≡CH", "C2H2", true)]
        [InlineData("CH3COOCH2CH3", "CH3COOC2H5", true)]
        [InlineData("CH3COOC2H5", "C4H8O2", true)]
        [InlineData("C6H6", "c6h6", true)]
        public void OrganicChemistry_CondensedAndMolecularEquivalence(string a, string b, bool expectedMatch)
        {
            bool eq = PracticeService.IsOrganicStructureEquivalent(a, b);
            Assert.Equal(expectedMatch, eq);

            bool fillMatch = PracticeService.CheckFillInBlankMatch(a, b);
            Assert.Equal(expectedMatch, fillMatch);
        }

        #endregion

        #region 4. 用户体验专家：天文学与光学工程量纲科学换算与智能容差

        [Theory]
        [InlineData("1 ly", "9.46e15 m", true)]
        [InlineData("1 光年", "9.4607e15 米", true)]
        [InlineData("1 au", "1.496e11 m", true)]
        [InlineData("1 天文单位", "1.5e11 米", true)] // 允许天文约算 2.5% 容差
        [InlineData("1 pc", "3.26 ly", true)]
        [InlineData("1 秒差距", "3.086e16 米", true)]
        [InlineData("1 kpc", "1000 pc", true)]
        [InlineData("1 mpc", "1000 kpc", true)]
        [InlineData("1 Å", "0.1 nm", true)]
        [InlineData("1 埃米", "1e-10 米", true)]
        [InlineData("500 nm", "5000 Å", true)]
        [InlineData("5000 埃", "500 纳米", true)]
        [InlineData("100 lx", "100 勒克斯", true)]
        public void ScientificUnits_AstronomyAndOpticsEquivalence(string userAns, string correctAns, bool expectedMatch)
        {
            bool match = PracticeService.CheckFillInBlankMatch(userAns, correctAns);
            Assert.Equal(expectedMatch, match);
        }

        #endregion

        #region 5. 用户体验专家：几何零向量记号与空间直角坐标等价

        [Theory]
        [InlineData("\\vec{0}", "(0,0)", true)]
        [InlineData("\\mathbf{0}", "(0, 0)", true)]
        [InlineData("零向量", "(0, 0, 0)", true)]
        [InlineData("(0,0)", "\\vec{0}", true)]
        [InlineData("(1, 2)", "(1,2)", true)]
        [InlineData("(1, 2, 3)", "(1,2,3)", true)]
        public void CoordinateAndZeroVector_Equivalence(string userAns, string correctAns, bool expectedMatch)
        {
            bool match = PracticeService.CheckCoordinateEquationMatch(userAns, correctAns);
            Assert.Equal(expectedMatch, match);
        }

        #endregion

        #region 6. 用户体验专家：等价命中教学反馈文案精准度

        [Fact]
        public void PracticeService_GenerateEquivalentMatchReason_ReturnsTailoredFeedback()
        {
            var qComplex = new Question { CorrectAnswer = "2 + 3i", Type = QuestionType.FillInBlank };
            string reasonComplex = PracticeService.GenerateEquivalentMatchReason(qComplex, "3i + 2");
            Assert.Contains("复数代数形式等价", reasonComplex);

            var qOrganic = new Question { CorrectAnswer = "CH3COCH3", Type = QuestionType.FillInBlank };
            string reasonOrganic = PracticeService.GenerateEquivalentMatchReason(qOrganic, "C3H6O");
            Assert.Contains("有机化学结构简式与分子式等价", reasonOrganic);

            var qAstro = new Question { CorrectAnswer = "1 ly", Type = QuestionType.FillInBlank };
            string reasonAstro = PracticeService.GenerateEquivalentMatchReason(qAstro, "9.46e15 m");
            Assert.Contains("天文与光学工程量纲等价", reasonAstro);
        }

        #endregion
    }
}
