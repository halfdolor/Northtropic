using System;
using System.Collections.Generic;
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
    /// <summary>
    /// 系统架构师与用户体验专家双重视角赋能验证套件
    /// 包含：
    /// 1. 架构师维度：CurriculumSubjectConfig 考点配置与 EvolutionClosedLoopInsight 学情闭环洞察领域不变量审计
    /// 2. 架构师维度：CurriculumSubjectConfig 与 EvolutionClosedLoopInsight 领域自愈及 HealAllInvariantsAsync 全量编排
    /// 3. 体验专家维度：集合论交集 (∩, \cap, 交集)、包含与真包含 (⊆, ⫋, ⊂)、补集 (∁, \complement, C_U) 与几何弧记号 (\widehat{AB}, \overset{\frown}{AB}, 弧AB)
    /// 4. 体验专家维度：角速度 (rad/s) 与转速 (r/min, rpm, r/s) 物理量自然容错互认
    /// 5. 体验专家维度：动量与冲量 (N·s, kg·m/s, 牛·秒, 千克米每秒) 跨量纲单位等价
    /// 6. 体验专家维度：表面张力系数 (N/m, J/m^2, 牛每米) 与光度学单位 (lx, lm/m^2, lm) 物理等价
    /// </summary>
    public class ArchitectAndUxOmniMasterEmpowermentTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxOmniMasterEmpowermentTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        // ==========================================
        // 架构师视角 1：CurriculumSubjectConfig 领域不变量审计与自愈收敛
        // ==========================================

        [Fact]
        public async Task CurriculumSubjectConfig_InvariantsAuditAndHealing_MergesDuplicatesAndRepairsJson()
        {
            // 准备破损与重复的课程学科配置数据
            // 1. 空白学科名记录
            var emptyConfig = new CurriculumSubjectConfig
            {
                Id = Guid.NewGuid(),
                Grade = "高中",
                Subject = "   ",
                TopicsJson = "[\"全部\",\"力学\"]",
                UpdatedAt = DateTime.UtcNow
            };

            // 2. 损坏的 JSON
            var corruptedJsonConfig = new CurriculumSubjectConfig
            {
                Id = Guid.NewGuid(),
                Grade = "初中",
                Subject = "物理",
                TopicsJson = "THIS_IS_NOT_VALID_JSON{{{{",
                UpdatedAt = DateTime.UtcNow
            };

            // 3. 相同 (Grade, Subject) 的重复记录（需合并考点）
            var dupConfig1 = new CurriculumSubjectConfig
            {
                Id = Guid.NewGuid(),
                Grade = "高中",
                Subject = "化学",
                TopicsJson = JsonSerializer.Serialize(new List<string> { "全部", "物质的量", "氧化还原反应" }),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
            };
            var dupConfig2 = new CurriculumSubjectConfig
            {
                Id = Guid.NewGuid(),
                Grade = "高中",
                Subject = "化学",
                TopicsJson = JsonSerializer.Serialize(new List<string> { "元素周期律", "化学平衡" }),
                UpdatedAt = DateTime.UtcNow
            };

            _context.CurriculumSubjectConfigs.AddRange(emptyConfig, corruptedJsonConfig, dupConfig1, dupConfig2);
            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);

            // 审计验证：检测出破损与重复记录
            var auditBefore = await healthService.AuditDataIntegrityAsync();
            Assert.True(auditBefore.TotalInvalidCurriculumConfigs >= 3);

            // 执行自愈收敛
            var healedCount = await healthService.HealCurriculumSubjectConfigInvariantsAsync();
            Assert.True(healedCount >= 3);

            // 验证自愈后的状态
            var auditAfter = await healthService.AuditDataIntegrityAsync();
            Assert.Equal(0, auditAfter.TotalInvalidCurriculumConfigs);

            // 验证重复记录已合并为一条，且包含了双方的考点，"全部"排在第一位
            var chemConfigs = await _context.CurriculumSubjectConfigs
                .Where(c => c.Grade == "高中" && c.Subject == "化学")
                .ToListAsync();
            Assert.Single(chemConfigs);
            var mergedTopics = JsonSerializer.Deserialize<List<string>>(chemConfigs[0].TopicsJson);
            Assert.NotNull(mergedTopics);
            Assert.Equal("全部", mergedTopics[0]);
            Assert.Contains("物质的量", mergedTopics);
            Assert.Contains("氧化还原反应", mergedTopics);
            Assert.Contains("元素周期律", mergedTopics);
            Assert.Contains("化学平衡", mergedTopics);

            // 验证损坏的初中物理已恢复为有效默认 JSON
            var physicsConfig = await _context.CurriculumSubjectConfigs.FirstOrDefaultAsync(c => c.Id == corruptedJsonConfig.Id);
            Assert.NotNull(physicsConfig);
            var physicsTopics = JsonSerializer.Deserialize<List<string>>(physicsConfig.TopicsJson);
            Assert.NotNull(physicsTopics);
            Assert.Contains("全部", physicsTopics);

            // 验证空白学科名记录已被清理或赋予默认值
            var cleanedEmptyConfig = await _context.CurriculumSubjectConfigs.FirstOrDefaultAsync(c => c.Id == emptyConfig.Id);
            Assert.True(cleanedEmptyConfig == null || !string.IsNullOrWhiteSpace(cleanedEmptyConfig.Subject));
        }

        // ==========================================
        // 架构师视角 2：EvolutionClosedLoopInsight 领域不变量审计与自愈
        // ==========================================

        [Fact]
        public async Task EvolutionClosedLoopInsight_InvariantsAuditAndHealing_ClampsValuesAndDeduplicates()
        {
            var student = new User { Id = Guid.NewGuid(), Username = "InsightStudent", Role = UserRole.Student };
            _context.Users.Add(student);

            var now = DateTime.UtcNow;

            // 1. 评分超限与负数计数器
            var abnormalInsight1 = new EvolutionClosedLoopInsight
            {
                Id = Guid.NewGuid(),
                UserId = student.Id,
                AnalyzedAt = now.AddDays(-2),
                EvaluationStatus = ProgressEvaluationStatus.Progressing,
                PotentialScore = 150, // 超出 [0, 100]
                PurifiedErrorsCount = -5, // 负数
                WeaknessOvercomeCount = -1, // 负数
                RootCauseDiagnosis = "异常指标洞察"
            };

            var abnormalInsight2 = new EvolutionClosedLoopInsight
            {
                Id = Guid.NewGuid(),
                UserId = student.Id,
                AnalyzedAt = now.AddDays(-1),
                EvaluationStatus = ProgressEvaluationStatus.Regressing,
                PotentialScore = -20, // 低于 0
                PurifiedErrorsCount = 10,
                WeaknessOvercomeCount = 3,
                RootCauseDiagnosis = "负评分洞察"
            };

            // 2. 同一秒重复生成的洞察记录
            var duplicateTime = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
            var dupInsightA = new EvolutionClosedLoopInsight
            {
                Id = Guid.NewGuid(),
                UserId = student.Id,
                AnalyzedAt = duplicateTime,
                EvaluationStatus = ProgressEvaluationStatus.SteadilyImproving,
                PotentialScore = 88,
                PurifiedErrorsCount = 4,
                WeaknessOvercomeCount = 2,
                RootCauseDiagnosis = "重复洞察记录A"
            };
            var dupInsightB = new EvolutionClosedLoopInsight
            {
                Id = Guid.NewGuid(),
                UserId = student.Id,
                AnalyzedAt = duplicateTime,
                EvaluationStatus = ProgressEvaluationStatus.SteadilyImproving,
                PotentialScore = 88,
                PurifiedErrorsCount = 4,
                WeaknessOvercomeCount = 2,
                RootCauseDiagnosis = "重复洞察记录B"
            };

            _context.EvolutionClosedLoopInsights.AddRange(abnormalInsight1, abnormalInsight2, dupInsightA, dupInsightB);
            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);

            // 审计检测
            var auditBefore = await healthService.AuditDataIntegrityAsync();
            Assert.True(auditBefore.TotalInvalidInsights >= 3);

            // 执行自愈
            var healedCount = await healthService.HealClosedLoopInsightInvariantsAsync();
            Assert.True(healedCount >= 3);

            // 再次审计应归零
            var auditAfter = await healthService.AuditDataIntegrityAsync();
            Assert.Equal(0, auditAfter.TotalInvalidInsights);

            // 验证评分和计数器修复
            var healed1 = await _context.EvolutionClosedLoopInsights.FindAsync(abnormalInsight1.Id);
            Assert.NotNull(healed1);
            Assert.Equal(100, healed1.PotentialScore);
            Assert.Equal(0, healed1.PurifiedErrorsCount);
            Assert.Equal(0, healed1.WeaknessOvercomeCount);

            var healed2 = await _context.EvolutionClosedLoopInsights.FindAsync(abnormalInsight2.Id);
            Assert.NotNull(healed2);
            Assert.Equal(0, healed2.PotentialScore);

            // 验证重复记录已收敛为 1 条
            var dupCount = await _context.EvolutionClosedLoopInsights
                .Where(i => i.UserId == student.Id && i.AnalyzedAt == duplicateTime)
                .CountAsync();
            Assert.Equal(1, dupCount);
        }

        // ==========================================
        // 架构师视角 3：HealAllInvariantsAsync 全量编排包含两项新自愈
        // ==========================================

        [Fact]
        public async Task HealAllInvariants_OrchestratesCurriculumConfigsAndClosedLoopInsights()
        {
            var user = new User { Id = Guid.NewGuid(), Username = "OrchAllUser", Role = UserRole.Student };
            _context.Users.Add(user);

            // 破损课程配置
            _context.CurriculumSubjectConfigs.Add(new CurriculumSubjectConfig
            {
                Id = Guid.NewGuid(),
                Grade = "高中",
                Subject = "生物",
                TopicsJson = "CORRUPTED_JSON",
                UpdatedAt = DateTime.UtcNow
            });

            // 破损闭环洞察
            _context.EvolutionClosedLoopInsights.Add(new EvolutionClosedLoopInsight
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                AnalyzedAt = DateTime.UtcNow,
                EvaluationStatus = ProgressEvaluationStatus.Progressing,
                PotentialScore = 120,
                PurifiedErrorsCount = -3,
                WeaknessOvercomeCount = 0
            });

            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);
            var result = await healthService.HealAllInvariantsAsync();

            Assert.True(result.Success);
            Assert.True(result.HealedCurriculumConfigsCount >= 1);
            Assert.True(result.HealedInsightsCount >= 1);
            Assert.True(result.TotalHealedCount >= 2);
            Assert.Contains(result.OperationsExecuted, op => op.Contains("课程考点配置自愈"));
            Assert.Contains(result.OperationsExecuted, op => op.Contains("学情闭环洞察自愈"));
        }

        // ==========================================
        // 用户体验专家视角 1：集合运算（交集、包含、真包含、补集）与几何弧
        // ==========================================

        [Fact]
        public void CheckFillInBlankMatch_SetOperationsAndGeometricArcs_MatchesCorrectly()
        {
            // 1. 集合交集符号等价: \cap <=> ∩ <=> 交集 <=> 交
            Assert.True(PracticeService.CheckFillInBlankMatch(@"A \cap B", "A ∩ B"));
            Assert.True(PracticeService.CheckFillInBlankMatch("A ∩ B", @"A \cap B"));
            Assert.True(PracticeService.CheckFillInBlankMatch("A 交集 B", "A ∩ B"));
            Assert.True(PracticeService.CheckFillInBlankMatch("A 交 B", "A ∩ B"));
            Assert.True(PracticeService.CheckFillInBlankMatch("[1, 3] 交 [2, 4]", "[1, 3] ∩ [2, 4]"));

            // 2. 集合包含与真包含等价: \subseteq <=> ⊆ <=> 包含于 <=> 包含; \subsetneq <=> ⫋ <=> 真包含于 <=> 真包含; \subset <=> ⊂
            Assert.True(PracticeService.CheckFillInBlankMatch(@"A \subseteq B", "A ⊆ B"));
            Assert.True(PracticeService.CheckFillInBlankMatch("A 包含于 B", "A ⊆ B"));
            Assert.True(PracticeService.CheckFillInBlankMatch("A 包含 B", "A ⊆ B"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"A \subsetneq B", "A ⫋ B"));
            Assert.True(PracticeService.CheckFillInBlankMatch("A 真包含于 B", "A ⫋ B"));
            Assert.True(PracticeService.CheckFillInBlankMatch("A 真包含 B", "A ⫋ B"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"A \subset B", "A ⊂ B"));

            // 3. 集合补集符号等价: \complement_U A <=> \complement A <=> ∁_U A <=> ∁ A <=> C_U A <=> 补集 A
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\complement_U A", "∁ A"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\complement A", "∁ A"));
            Assert.True(PracticeService.CheckFillInBlankMatch("∁_U A", "∁ A"));
            Assert.True(PracticeService.CheckFillInBlankMatch("C_U A", "∁ A"));
            Assert.True(PracticeService.CheckFillInBlankMatch("补集 A", "∁ A"));

            // 4. 几何弧记号等价: \overset{\frown}{AB} <=> \widehat{AB} <=> \overgroup{AB} <=> 弧AB
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\overset{\frown}{AB}", "弧AB"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\widehat{AB}", "弧AB"));
            Assert.True(PracticeService.CheckFillInBlankMatch(@"\overgroup{AB}", "弧AB"));
            Assert.True(PracticeService.CheckFillInBlankMatch("弧AB", @"\widehat{AB}"));
        }

        // ==========================================
        // 用户体验专家视角 2：角速度与转速物理量
        // ==========================================

        [Fact]
        public void CheckFillInBlankMatch_AngularVelocityAndRotationalSpeed_MatchesCorrectly()
        {
            // 1. 角速度单位等价: rad/s <=> rad·s^-1 <=> rad*s^-1 <=> 弧度/秒 <=> 弧度每秒
            Assert.True(PracticeService.CheckFillInBlankMatch("10 rad/s", "10 rad*s^-1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10 rad/s", "10 rad·s^-1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10 弧度/秒", "10 rad/s"));
            Assert.True(PracticeService.CheckFillInBlankMatch("10 弧度每秒", "10 rad/s"));

            // 2. 转速与转频单位等价: r/min <=> rpm <=> 转/分 <=> 转每分; r/s <=> 转/秒 <=> 转每秒
            Assert.True(PracticeService.CheckFillInBlankMatch("3000 r/min", "3000 rpm"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3000 转/分", "3000 r/min"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3000 转每分", "3000 rpm"));
            Assert.True(PracticeService.CheckFillInBlankMatch("50 r/s", "50 转/秒"));
            Assert.True(PracticeService.CheckFillInBlankMatch("50 转每秒", "50 r/s"));
        }

        // ==========================================
        // 用户体验专家视角 3：动量与冲量、表面张力、光度学物理量
        // ==========================================

        [Fact]
        public void CheckFillInBlankMatch_MomentumSurfaceTensionAndPhotometry_MatchesCorrectly()
        {
            // 1. 动量与冲量单位跨量纲等价: N·s <=> N*s <=> kg·m/s <=> kg*m/s <=> 牛·秒 <=> 牛秒 <=> 千克米每秒 <=> 牛乘秒
            Assert.True(PracticeService.CheckFillInBlankMatch("20 N·s", "20 kg·m/s"));
            Assert.True(PracticeService.CheckFillInBlankMatch("20 N*s", "20 kg*m/s"));
            Assert.True(PracticeService.CheckFillInBlankMatch("20 牛·秒", "20 N·s"));
            Assert.True(PracticeService.CheckFillInBlankMatch("20 牛秒", "20 kg·m/s"));
            Assert.True(PracticeService.CheckFillInBlankMatch("20 千克米每秒", "20 N·s"));
            Assert.True(PracticeService.CheckFillInBlankMatch("20 牛乘秒", "20 N·s"));

            // 2. 表面张力系数单位等价: N/m <=> N·m^-1 <=> 牛每米 <=> 牛/米 <=> J/m^2 <=> 焦每平方米
            Assert.True(PracticeService.CheckFillInBlankMatch("0.072 N/m", "0.072 N·m^-1"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0.072 牛每米", "0.072 N/m"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0.072 牛/米", "0.072 N/m"));
            Assert.True(PracticeService.CheckFillInBlankMatch("0.072 J/m^2", "0.072 N/m"));

            // 3. 照度与光通量单位等价: lx <=> lux <=> 勒克斯 <=> lm/m^2 <=> 流明每平方米; lm <=> lumen <=> 流明
            Assert.True(PracticeService.CheckFillInBlankMatch("500 lx", "500 lux"));
            Assert.True(PracticeService.CheckFillInBlankMatch("500 勒克斯", "500 lx"));
            Assert.True(PracticeService.CheckFillInBlankMatch("500 lm/m^2", "500 lx"));
            Assert.True(PracticeService.CheckFillInBlankMatch("500 流明每平方米", "500 lx"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1200 lm", "1200 lumen"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1200 流明", "1200 lm"));
        }
    }
}
