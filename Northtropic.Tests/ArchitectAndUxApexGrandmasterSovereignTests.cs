using System;
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
    /// 系统架构师与用户体验专家双重视角至尊宗师级验证套件
    /// 涵盖：
    /// 1. 架构师维度：ErrorItem、UserAchievement 与 LlmGenerationLog 领域不变量审计
    /// 2. 架构师维度：UserAchievement 与 LlmGenerationLog 自愈引擎及 HealAllInvariants 全量收敛编排
    /// 3. 架构师维度：GamificationService 本地 ChangeTracker (ctx.UserAchievements.Local) 防重解锁
    /// 4. 体验专家维度：STEM 理化全维度物理单位（磁感应强度 T, 电阻率 Ω·m, 电导 S, 导热系数 W/(m·K), 摩尔熵/气体常数 J/(mol·K)）
    /// 5. 体验专家维度：数学几何平行 (∥)、垂直 (⊥)、有向向量 (\overrightarrow{AB}, \vec{AB}, 向量AB)、角记号 (∠ABC) 与无穷区间 ([0, 正无穷), (-\infty, 5])
    /// </summary>
    public class ArchitectAndUxApexGrandmasterSovereignTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxApexGrandmasterSovereignTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        // ==========================================
        // 架构师视角 1：错题本、用户成就、大模型日志领域不变量审计
        // ==========================================

        [Fact]
        public async Task AuditDataIntegrity_DetectsErrorItemAndAchievementAndLlmLogAnomalies()
        {
            var user = new User { Id = Guid.NewGuid(), Username = "ApexArchitectStudent", Role = UserRole.Student };
            var question = new Question { Id = Guid.NewGuid(), Stem = "不变量测试题", CorrectAnswer = "C", Subject = "物理" };
            var achievement = new Achievement { Id = Guid.NewGuid(), Code = "ACH_SOVEREIGN", Title = "至尊成就", RewardExp = 100, RewardCoins = 50 };

            _context.Users.Add(user);
            _context.Questions.Add(question);
            _context.Achievements.Add(achievement);

            // 1. 错题本不变量异常：负复习数、已掌握未填复习时间、重复副本
            _context.ErrorItems.Add(new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = question.Id,
                RevisionCount = -3,
                IsMastered = true,
                LastRevisedAt = null,
                ErrorReasonCategory = "审题不清"
            });
            _context.ErrorItems.Add(new ErrorItem
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = question.Id, // 同一用户同一题目重复副本
                RevisionCount = 1,
                IsMastered = false,
                ErrorReasonCategory = "知识漏洞"
            });

            // 2. 成就重复副本异常：同一用户重复挂载同一成就
            _context.UserAchievements.Add(new UserAchievement
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                AchievementId = achievement.Id,
                UnlockedAt = DateTime.Now.AddDays(-2)
            });
            _context.UserAchievements.Add(new UserAchievement
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                AchievementId = achievement.Id, // 重复
                UnlockedAt = DateTime.Now.AddDays(-1)
            });

            // 3. 大模型日志不变量异常：负 Token、Token 不守恒、空模型名
            _context.LlmGenerationLogs.Add(new LlmGenerationLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PromptTokens = -10,
                CompletionTokens = 50,
                TotalTokens = 40,
                ModelName = "gpt-4o"
            });
            _context.LlmGenerationLogs.Add(new LlmGenerationLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PromptTokens = 100,
                CompletionTokens = 200,
                TotalTokens = 999, // 不守恒
                ModelName = "" // 空模型名
            });

            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);
            var audit = await healthService.AuditDataIntegrityAsync();

            Assert.False(audit.IsHealthy);
            Assert.True(audit.TotalInvalidErrorItems >= 2);
            Assert.Equal(1, audit.TotalDuplicateUserAchievements);
            Assert.Equal(2, audit.TotalInvalidLlmLogs);
            Assert.True(audit.TotalIssuesCount >= 5);
        }

        // ==========================================
        // 架构师视角 2：用户成就与大模型日志独立自愈引擎
        // ==========================================

        [Fact]
        public async Task HealUserAchievementInvariants_PurgesDuplicateAchievementsPreservingEarliest()
        {
            var user = new User { Id = Guid.NewGuid(), Username = "AchieveTestUser", Role = UserRole.Student };
            var achievement = new Achievement { Id = Guid.NewGuid(), Code = "ACH_HEAL_TEST", Title = "自愈测试成就" };
            _context.Users.Add(user);
            _context.Achievements.Add(achievement);

            var earlyDate = DateTime.Now.AddDays(-5);
            var laterDate = DateTime.Now.AddDays(-1);

            var canonical = new UserAchievement
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                AchievementId = achievement.Id,
                UnlockedAt = earlyDate
            };
            var duplicate = new UserAchievement
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                AchievementId = achievement.Id,
                UnlockedAt = laterDate
            };

            _context.UserAchievements.AddRange(canonical, duplicate);
            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);
            var healedCount = await healthService.HealUserAchievementInvariantsAsync();

            Assert.Equal(1, healedCount);

            var remaining = await _context.UserAchievements.Where(a => a.UserId == user.Id).ToListAsync();
            Assert.Single(remaining);
            Assert.Equal(canonical.Id, remaining[0].Id);
            Assert.Equal(earlyDate, remaining[0].UnlockedAt);
        }

        [Fact]
        public async Task HealLlmLogInvariants_RecalibratesTokensAndSuppliesDefaultModel()
        {
            var user = new User { Id = Guid.NewGuid(), Username = "LlmHealUser", Role = UserRole.Student };
            _context.Users.Add(user);

            var log1 = new LlmGenerationLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PromptTokens = -15,
                CompletionTokens = 30,
                TotalTokens = 10,
                ModelName = "deepseek-r1"
            };

            var log2 = new LlmGenerationLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PromptTokens = 50,
                CompletionTokens = 75,
                TotalTokens = 120, // 应当是 125
                ModelName = "   " // 空白
            };

            _context.LlmGenerationLogs.AddRange(log1, log2);
            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);
            var healedCount = await healthService.HealLlmLogInvariantsAsync();

            Assert.Equal(2, healedCount);

            var updatedLog1 = await _context.LlmGenerationLogs.FindAsync(log1.Id);
            Assert.NotNull(updatedLog1);
            Assert.Equal(0, updatedLog1.PromptTokens);
            Assert.Equal(30, updatedLog1.CompletionTokens);
            Assert.Equal(30, updatedLog1.TotalTokens);

            var updatedLog2 = await _context.LlmGenerationLogs.FindAsync(log2.Id);
            Assert.NotNull(updatedLog2);
            Assert.Equal(125, updatedLog2.TotalTokens);
            Assert.Equal("unknown-model", updatedLog2.ModelName);
        }

        // ==========================================
        // 架构师视角 3：HealAllInvariantsAsync 全量收敛编排
        // ==========================================

        [Fact]
        public async Task HealAllInvariants_OrchestratesUserAchievementsAndLlmLogs()
        {
            var user = new User { Id = Guid.NewGuid(), Username = "FullOrchestrationUser", Role = UserRole.Student };
            var achievement = new Achievement { Id = Guid.NewGuid(), Code = "ACH_FULL_ORCH", Title = "编排成就" };
            _context.Users.Add(user);
            _context.Achievements.Add(achievement);

            // 1. 重复成就
            _context.UserAchievements.Add(new UserAchievement { Id = Guid.NewGuid(), UserId = user.Id, AchievementId = achievement.Id, UnlockedAt = DateTime.Now.AddDays(-2) });
            _context.UserAchievements.Add(new UserAchievement { Id = Guid.NewGuid(), UserId = user.Id, AchievementId = achievement.Id, UnlockedAt = DateTime.Now.AddDays(-1) });

            // 2. 破损 LLM 日志
            _context.LlmGenerationLogs.Add(new LlmGenerationLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PromptTokens = 10,
                CompletionTokens = 20,
                TotalTokens = 999,
                ModelName = ""
            });

            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);
            var result = await healthService.HealAllInvariantsAsync();

            Assert.True(result.Success);
            Assert.True(result.TotalHealedCount >= 2);
            Assert.Equal(1, result.HealedUserAchievementsCount);
            Assert.Equal(1, result.HealedLlmLogsCount);
            Assert.Contains(result.OperationsExecuted, op => op.Contains("用户成就重复副本自愈"));
            Assert.Contains(result.OperationsExecuted, op => op.Contains("大模型推理日志自愈"));
        }

        // ==========================================
        // 架构师视角 4：GamificationService 本地 ChangeTracker 防重保护
        // ==========================================

        [Fact]
        public async Task GamificationService_LocalChangeTrackerPreventsDuplicateAchievementUnlock()
        {
            var user = new User { Id = Guid.NewGuid(), Username = "GameUser", Role = UserRole.Student, Exp = 0, Coins = 0 };
            var achievement = new Achievement { Id = Guid.NewGuid(), Code = "FIRST_BLOOD", Title = "初战告捷", RewardExp = 50, RewardCoins = 20 };
            _context.Users.Add(user);
            _context.Achievements.Add(achievement);
            await _context.SaveChangesAsync();

            var gamificationService = new GamificationService(_context, null!);

            // 第一次解锁（传入 existingContext，不落盘保持在 Local 缓存中）
            var unlocked1 = await gamificationService.UnlockAchievementAsync("FIRST_BLOOD", user.Id, _context);
            Assert.True(unlocked1);
            Assert.Single(_context.UserAchievements.Local);

            // 第二次在同一上下文事务中解锁相同的成就，Local 缓存生效拦截
            var unlocked2 = await gamificationService.UnlockAchievementAsync("FIRST_BLOOD", user.Id, _context);
            Assert.False(unlocked2);
            Assert.Single(_context.UserAchievements.Local);

            // 落盘验证数据库最终仅有唯一一条成就记录
            await _context.SaveChangesAsync();
            var count = await _context.UserAchievements.CountAsync(ua => ua.UserId == user.Id && ua.AchievementId == achievement.Id);
            Assert.Equal(1, count);
        }

        // ==========================================
        // 体验专家视角 1：STEM 高级复合物理量纲智能等价 (磁感应强度, 电阻率, 电导, 导热系数, 摩尔常数)
        // ==========================================

        [Theory]
        [InlineData("2 T", "2 特斯拉")]
        [InlineData("2 N/(A·m)", "2 T")]
        [InlineData("2 N/(A*m)", "2 特斯拉")]
        [InlineData("2 Wb/m^2", "2 T")]
        [InlineData("2 韦伯每平方米", "2 N/(A·m)")]
        public void CheckFillInBlankMatch_MagneticFieldUnits_Match(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("1.75*10^-8 Ω·m", "1.75*10^-8 欧·米")]
        [InlineData("1.75*10^-8 \\Omega*m", "1.75*10^-8 欧姆米")]
        [InlineData("1.75*10^-8 欧米", "1.75*10^-8 Ω·m")]
        [InlineData("1.75*10^-8 ohm*m", "1.75*10^-8 欧姆米")]
        public void CheckFillInBlankMatch_ResistivityUnits_Match(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("0.05 S", "0.05 西门子")]
        [InlineData("0.05 1/Ω", "0.05 S")]
        [InlineData("0.05 A/V", "0.05 西门子")]
        [InlineData("0.05 安每伏", "0.05 S")]
        public void CheckFillInBlankMatch_ConductanceUnits_Match(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("0.8 W/(m·K)", "0.8 W/(m*K)")]
        [InlineData("0.8 W/(m·℃)", "0.8 W/(m·K)")]
        [InlineData("0.8 瓦每米开尔文", "0.8 W/(m·K)")]
        [InlineData("0.8 瓦每米摄氏度", "0.8 瓦每米开尔文")]
        public void CheckFillInBlankMatch_ThermalConductivityUnits_Match(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("8.314 J/(mol·K)", "8.314 J/(mol*K)")]
        [InlineData("8.314 焦每摩尔开尔文", "8.314 J/(mol·K)")]
        public void CheckFillInBlankMatch_MolarGasConstantAndEntropy_Match(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        // ==========================================
        // 体验专家视角 2：几何平行垂直、角记号、有向向量与无穷区间智能归一
        // ==========================================

        [Theory]
        [InlineData("AB ∥ CD", "AB // CD")]
        [InlineData("AB \\parallel CD", "AB ∥ CD")]
        [InlineData("AB 平行于 CD", "AB ∥ CD")]
        [InlineData("AB ⊥ CD", "AB \\perp CD")]
        [InlineData("AB \\bot CD", "AB 垂直于 CD")]
        [InlineData("AB 垂直 CD", "AB ⊥ CD")]
        public void CheckFillInBlankMatch_GeometricParallelAndPerpendicular_Match(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("\\overrightarrow{AB}", "\\vec{AB}")]
        [InlineData("向量AB", "\\overrightarrow{AB}")]
        [InlineData("AB^\\rightarrow", "向量AB")]
        [InlineData("\\angle ABC", "∠ABC")]
        [InlineData("角ABC", "\\angle ABC")]
        public void CheckFillInBlankMatch_VectorsAndAngleNotations_Match(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("[0, +\\infty)", "[0, \\infty)")]
        [InlineData("[0, 正无穷)", "[0, +\\infty)")]
        [InlineData("[0, +inf)", "[0, 正无穷)")]
        [InlineData("(-\\infty, 5]", "(-inf, 5]")]
        [InlineData("(负无穷, 5]", "(-\\infty, 5]")]
        public void CheckFillInBlankMatch_InfinityIntervals_Match(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }
    }
}
