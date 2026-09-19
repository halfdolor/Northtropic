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
    /// <summary>
    /// 系统架构师与用户体验专家双重视角测试套件 (Zenith Evolution Suite)
    /// 覆盖：
    /// 1. 架构师：成就实体领域不变量审计、重复Code合并与UserAchievement级联重定向自愈
    /// 2. 架构师：学生BindingCode碰撞重算自愈、演进洞察极值收敛与全库一键编排自愈
    /// 3. 用户体验：高阶STEM多学科单位等价评测 (化学浓度、物理真空压强、静电荷、电磁场参数、温标、复数工程虚数单位)
    /// </summary>
    public class ArchitectAndUxZenithEvolutionSuiteTests : IDisposable
    {
        private readonly AppDbContext _inMemoryContext;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxZenithEvolutionSuiteTests()
        {
            (_inMemoryContext, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _inMemoryContext.Dispose();
            _connection.Dispose();
        }

        #region 1. 系统架构师：成就定义与用户成就级联自愈测试

        [Fact]
        public async Task AuditDataIntegrity_DetectsAchievementDomainInvariants()
        {
            // Arrange
            var ach1 = new Achievement { Code = "FIRST_BLOOD", Title = "初露锋芒", RewardCoins = 10, RewardExp = 50 };
            var ach2 = new Achievement { Code = "FIRST_BLOOD", Title = "初露锋芒Duplicate", RewardCoins = -5, RewardExp = -10 }; // 重复Code且负数奖励
            var ach3 = new Achievement { Code = "EMPTY_NAME", Title = "   ", RewardCoins = 20, RewardExp = 100 }; // 空名称
            _inMemoryContext.Achievements.AddRange(ach1, ach2, ach3);
            await _inMemoryContext.SaveChangesAsync();

            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var audit = await healthService.AuditDataIntegrityAsync();

            // Assert
            Assert.NotNull(audit);
            Assert.False(audit.IsHealthy);
            Assert.True(audit.TotalInvalidAchievements > 0);
            Assert.True(audit.InvalidAchievementsCount > 0);
        }

        [Fact]
        public async Task HealAchievementInvariants_MergesDuplicates_And_RedirectsUserAchievements()
        {
            // Arrange: 两个相同 Code 的成就定义
            var achA = new Achievement { Code = "PERFECT_SCORE", Title = "满分达人", RewardCoins = 50, RewardExp = 200 };
            var achB = new Achievement { Code = "perfect_score", Title = "  ", RewardCoins = -20, RewardExp = -100 }; // 重复且非法属性
            _inMemoryContext.Achievements.AddRange(achA, achB);

            var user1 = new User { Username = "student_ach_1", Role = UserRole.Student, BindingCode = "STU001" };
            var user2 = new User { Username = "student_ach_2", Role = UserRole.Student, BindingCode = "STU002" };
            _inMemoryContext.Users.AddRange(user1, user2);
            await _inMemoryContext.SaveChangesAsync();

            // user1 关联到 achB (待合并重定向到 achA)
            _inMemoryContext.UserAchievements.Add(new UserAchievement
            {
                UserId = user1.Id,
                AchievementId = achB.Id,
                UnlockedAt = DateTime.UtcNow
            });

            // user2 同时拥有 achA 和 achB (重定向时需清理重复项)
            _inMemoryContext.UserAchievements.Add(new UserAchievement
            {
                UserId = user2.Id,
                AchievementId = achA.Id,
                UnlockedAt = DateTime.UtcNow.AddHours(-1)
            });
            _inMemoryContext.UserAchievements.Add(new UserAchievement
            {
                UserId = user2.Id,
                AchievementId = achB.Id,
                UnlockedAt = DateTime.UtcNow
            });
            await _inMemoryContext.SaveChangesAsync();

            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var healedCount = await healthService.HealAchievementInvariantsAsync();

            // Assert
            Assert.True(healedCount > 0);

            // 验证数据库中只剩下一个规范化成就实体
            var remainingAchs = await _inMemoryContext.Achievements.Where(a => a.Code.ToUpper() == "PERFECT_SCORE").ToListAsync();
            Assert.Single(remainingAchs);
            var canonicalAch = remainingAchs.First();
            Assert.True(canonicalAch.RewardCoins >= 0);
            Assert.True(canonicalAch.RewardExp >= 0);
            Assert.False(string.IsNullOrWhiteSpace(canonicalAch.Title));

            // 验证 user1 的成就已被重定向到 canonicalAch
            var u1Achs = await _inMemoryContext.UserAchievements.Where(ua => ua.UserId == user1.Id).ToListAsync();
            Assert.Single(u1Achs);
            Assert.Equal(canonicalAch.Id, u1Achs.First().AchievementId);

            // 验证 user2 没有重复的成就记录
            var u2Achs = await _inMemoryContext.UserAchievements.Where(ua => ua.UserId == user2.Id).ToListAsync();
            Assert.Single(u2Achs);
            Assert.Equal(canonicalAch.Id, u2Achs.First().AchievementId);
        }

        #endregion

        #region 2. 系统架构师：BindingCode碰撞重算与学情洞察收敛自愈

        [Fact]
        public async Task HealGamificationInvariants_ResolvesBindingCodeCollisions_And_ClampsDailyTarget()
        {
            // Arrange: 两个学生具有相同的 BindingCode，且每日目标异常
            var s1 = new User { Username = "stu_collide_1", Role = UserRole.Student, BindingCode = "SHARED123", DailyTargetQuestions = -10 };
            var s2 = new User { Username = "stu_collide_2", Role = UserRole.Student, BindingCode = "shared123", DailyTargetQuestions = 99999 };
            var s3 = new User { Username = "stu_missing_code", Role = UserRole.Student, BindingCode = "TEMP_NONE", DailyTargetQuestions = 15 };
            s3.BindingCode = ""; // 测试空绑定码
            _inMemoryContext.Users.AddRange(s1, s2, s3);
            await _inMemoryContext.SaveChangesAsync();

            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var healed = await healthService.HealGamificationInvariantsAsync();

            // Assert
            Assert.True(healed >= 3);
            await _inMemoryContext.Entry(s1).ReloadAsync();
            await _inMemoryContext.Entry(s2).ReloadAsync();
            await _inMemoryContext.Entry(s3).ReloadAsync();

            // 两个学生的 BindingCode 必须不同且非空
            Assert.False(string.IsNullOrWhiteSpace(s1.BindingCode));
            Assert.False(string.IsNullOrWhiteSpace(s2.BindingCode));
            Assert.False(string.IsNullOrWhiteSpace(s3.BindingCode));
            Assert.NotEqual(s1.BindingCode, s2.BindingCode, StringComparer.OrdinalIgnoreCase);

            // 每日目标已收敛到合法区间 [1, 200]
            Assert.InRange(s1.DailyTargetQuestions, 1, 200);
            Assert.InRange(s2.DailyTargetQuestions, 1, 200);
            Assert.Equal(15, s3.DailyTargetQuestions);
        }

        [Fact]
        public async Task HealClosedLoopInsightInvariants_ConvergesExtremes_And_SanitizesNulls()
        {
            // Arrange
            var user = new User { Username = "insight_user", Role = UserRole.Student, BindingCode = "INS001" };
            _inMemoryContext.Users.Add(user);
            await _inMemoryContext.SaveChangesAsync();

            var insight = new EvolutionClosedLoopInsight
            {
                UserId = user.Id,
                AccuracyDelta = 1500.0, // 越界 (> 100)
                SpeedDeltaSeconds = 999999.0, // 极端越界 (> 86400)
                WeaknessOvercomeCount = -5, // 异常负数
                PurifiedErrorsCount = -10, // 异常负数
                RootCauseDiagnosis = "初始诊断",
                CorrectivePrescription = "初始处方",
                NextEvolutionStrategy = "初始建议"
            };
            _inMemoryContext.EvolutionClosedLoopInsights.Add(insight);
            await _inMemoryContext.SaveChangesAsync();

            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var healedCount = await healthService.HealClosedLoopInsightInvariantsAsync();

            // Assert
            Assert.True(healedCount > 0);
            await _inMemoryContext.Entry(insight).ReloadAsync();
            Assert.InRange(insight.AccuracyDelta, -100.0, 100.0);
            Assert.Equal(100.0, insight.AccuracyDelta);
            Assert.InRange(insight.SpeedDeltaSeconds, -86400.0, 86400.0);
            Assert.Equal(86400.0, insight.SpeedDeltaSeconds);
            Assert.Equal(0, insight.WeaknessOvercomeCount);
            Assert.Equal(0, insight.PurifiedErrorsCount);
            Assert.False(double.IsNaN(insight.SpeedDeltaSeconds));
            Assert.False(double.IsInfinity(insight.SpeedDeltaSeconds));
            Assert.NotNull(insight.RootCauseDiagnosis);
            Assert.NotNull(insight.CorrectivePrescription);
            Assert.NotNull(insight.NextEvolutionStrategy);
        }

        [Fact]
        public async Task HealAllInvariantsAsync_ExecutesAll15InvariantHeals_Successfully()
        {
            // Arrange: 注入部分破损数据
            var badAch = new Achievement { Code = "ORCH_ACH", Title = "", RewardCoins = -1 };
            var badUser = new User { Username = "orch_user", Role = UserRole.Student, BindingCode = "" };
            _inMemoryContext.Achievements.Add(badAch);
            _inMemoryContext.Users.Add(badUser);
            await _inMemoryContext.SaveChangesAsync();

            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var result = await healthService.HealAllInvariantsAsync();

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.True(result.TotalHealedCount > 0);
            Assert.True(result.HealedAchievementsCount > 0);
            Assert.Contains(result.OperationsExecuted, op => op.Contains("成就目录定义自愈"));
        }

        #endregion

        #region 3. 用户体验专家：高阶多学科答案等价性与符号容错评测

        [Theory]
        [InlineData("1 mol/L", "1 mol·L^-1")]
        [InlineData("0.5 mol/L", "0.5 mol/dm^3")]
        [InlineData("2 摩尔每升", "2 mol/l")]
        [InlineData("5 摩/升", "5 mol/L")]
        [InlineData("10 mmol/L", "10 毫摩尔每升")]
        [InlineData("100 umol/L", "100 μmol/L")]
        [InlineData("50 微摩/升", "50 umol/l")]
        [InlineData("20 mol/m^3", "20 摩尔每立方米")]
        public void PracticeService_CheckFillInBlankMatch_MolarConcentration_Equivalence(string studentAns, string standardAns)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(studentAns, standardAns),
                $"物质的量浓度等价评测应一致: [{studentAns}] vs [{standardAns}]");
        }

        [Theory]
        [InlineData("5 g/L", "5 克每升")]
        [InlineData("2.5 g/L", "2.5 克/升")]
        [InlineData("100 mg/L", "100 毫克每升")]
        [InlineData("50 ug/L", "50 微克每升")]
        [InlineData("50 μg/L", "50 ug/l")]
        public void PracticeService_CheckFillInBlankMatch_MassConcentration_Equivalence(string studentAns, string standardAns)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(studentAns, standardAns),
                $"质量浓度等价评测应一致: [{studentAns}] vs [{standardAns}]");
        }

        [Theory]
        [InlineData("1.013 bar", "1.013 巴")]
        [InlineData("500 mbar", "500 毫巴")]
        [InlineData("760 torr", "760 托")]
        [InlineData("760 mmHg", "760 torr")]
        [InlineData("760 毫米汞柱", "760 托")]
        [InlineData("2.5 GPa", "2.5 吉帕")]
        public void PracticeService_CheckFillInBlankMatch_PressureAndVacuum_Equivalence(string studentAns, string standardAns)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(studentAns, standardAns),
                $"压强真空度等价评测应一致: [{studentAns}] vs [{standardAns}]");
        }

        [Theory]
        [InlineData("5 C", "5 库仑")]
        [InlineData("5 库", "5 C")]
        [InlineData("10 mC", "10 毫库")]
        [InlineData("2.5 uC", "2.5 微库")]
        [InlineData("2.5 μC", "2.5 uC")]
        [InlineData("100 nC", "100 纳库")]
        [InlineData("50 pC", "50 皮库")]
        public void PracticeService_CheckFillInBlankMatch_ElectricCharge_Equivalence(string studentAns, string standardAns)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(studentAns, standardAns),
                $"静电荷量等价评测应一致: [{studentAns}] vs [{standardAns}]");
        }

        [Theory]
        [InlineData("100 A/m", "100 安每米")]
        [InlineData("50 安/米", "50 a/m")]
        [InlineData("8.85e-12 F/m", "8.85e-12 法每米")]
        [InlineData("1.26e-6 H/m", "1.26e-6 亨每米")]
        [InlineData("500 Gs", "500 高斯")]
        [InlineData("1000 gauss", "1000 Gs")]
        public void PracticeService_CheckFillInBlankMatch_Electromagnetism_Equivalence(string studentAns, string standardAns)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(studentAns, standardAns),
                $"电磁场参数等价评测应一致: [{studentAns}] vs [{standardAns}]");
        }

        [Theory]
        [InlineData("300 K", "300 开尔文")]
        [InlineData("273.15 开氏度", "273.15 K")]
        [InlineData("25 ℃", "25 °C")]
        [InlineData("100 摄氏度", "100 ℃")]
        public void PracticeService_CheckFillInBlankMatch_TemperatureScale_Equivalence(string studentAns, string standardAns)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(studentAns, standardAns),
                $"温标等价评测应一致: [{studentAns}] vs [{standardAns}]");
        }

        [Theory]
        [InlineData("3+4j", "3+4i")]
        [InlineData("5j", "5i")]
        [InlineData("-2j", "-2i")]
        [InlineData("1-j", "1-i")]
        [InlineData("j", "i")]
        [InlineData("j3", "i3")]
        [InlineData("2.5 + 1.2j", "2.5+1.2i")]
        public void PracticeService_CheckFillInBlankMatch_ComplexEngineeringNotation_Equivalence(string studentAns, string standardAns)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(studentAns, standardAns),
                $"工程复数虚数符号等价评测应一致: [{studentAns}] vs [{standardAns}]");
        }

        #endregion
    }
}
