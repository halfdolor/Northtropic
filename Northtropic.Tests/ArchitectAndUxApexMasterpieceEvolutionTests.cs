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
    /// 系统架构师与用户体验专家双重视角顶级演进测试套件
    /// 涵盖：
    /// 1. 架构师维度：PracticeRecords 领域不变量审计、自愈收敛与 HealAllInvariants 全量编排
    /// 2. 体验专家维度：STEM 理科全维度物理单位（压强、电容、电感、磁通量、比热容、物质的量浓度）
    /// 3. 体验专家维度：数学几何全等、相似、三角形标记与多重向量模长/范数智能等价识别
    /// </summary>
    public class ArchitectAndUxApexMasterpieceEvolutionTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;

        public ArchitectAndUxApexMasterpieceEvolutionTests()
        {
            (_context, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        // ==========================================
        // 架构师视角 1：学生答题流水表 (PracticeRecords) 领域不变量审计与自愈
        // ==========================================

        [Fact]
        public async Task AuditDataIntegrity_DetectsCorruptedPracticeRecordInvariants()
        {
            var user = new User { Id = Guid.NewGuid(), Username = "AuditTestStudent", Role = UserRole.Student };
            var question = new Question { Id = Guid.NewGuid(), Stem = "测试试题", CorrectAnswer = "A", Subject = "数学" };
            _context.Users.Add(user);
            _context.Questions.Add(question);

            // 1. 正常作答流水
            _context.PracticeRecords.Add(new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = question.Id,
                TimeTakenSeconds = 45,
                ComboAtAnswer = 3,
                EarnedExp = 10,
                EarnedCoins = 5,
                IsCorrect = true
            });

            // 2. 异常流水：负用时与负连击
            _context.PracticeRecords.Add(new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = question.Id,
                TimeTakenSeconds = -12,
                ComboAtAnswer = -2,
                EarnedExp = 10,
                EarnedCoins = 5,
                IsCorrect = true
            });

            // 3. 异常流水：负经验与负金币
            _context.PracticeRecords.Add(new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = question.Id,
                TimeTakenSeconds = 30,
                ComboAtAnswer = 1,
                EarnedExp = -50,
                EarnedCoins = -20,
                IsCorrect = false
            });

            // 4. 异常流水：异常越界用时 (> 86400 秒)
            _context.PracticeRecords.Add(new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = question.Id,
                TimeTakenSeconds = 120000,
                ComboAtAnswer = 0,
                EarnedExp = 5,
                EarnedCoins = 2,
                IsCorrect = true
            });

            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);
            var audit = await healthService.AuditDataIntegrityAsync();

            // 3 条异常记录应被准确识别
            Assert.Equal(3, audit.TotalInvalidPracticeRecords);
            Assert.False(audit.IsHealthy);
            Assert.Contains(audit.AuditDetails, detail => detail.Contains("学生答题流水存在领域不变量异常"));
        }

        [Fact]
        public async Task HealPracticeRecordInvariants_CalibratesNegativeAndOverflowMetrics()
        {
            var user = new User { Id = Guid.NewGuid(), Username = "HealStudent", Role = UserRole.Student };
            var question = new Question { Id = Guid.NewGuid(), Stem = "自愈测试题", CorrectAnswer = "B", Subject = "物理" };
            _context.Users.Add(user);
            _context.Questions.Add(question);

            var negativeRecord = new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = question.Id,
                TimeTakenSeconds = -30,
                ComboAtAnswer = -5,
                EarnedExp = -100,
                EarnedCoins = -50,
                IsCorrect = true
            };

            var overflowRecord = new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = question.Id,
                TimeTakenSeconds = 99999,
                ComboAtAnswer = 2,
                EarnedExp = 15,
                EarnedCoins = 10,
                IsCorrect = true
            };

            _context.PracticeRecords.AddRange(negativeRecord, overflowRecord);
            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);
            int healedCount = await healthService.HealPracticeRecordInvariantsAsync();

            Assert.Equal(2, healedCount);

            var r1 = await _context.PracticeRecords.FindAsync(negativeRecord.Id);
            Assert.NotNull(r1);
            Assert.Equal(0, r1.TimeTakenSeconds);
            Assert.Equal(0, r1.ComboAtAnswer);
            Assert.Equal(0, r1.EarnedExp);
            Assert.Equal(0, r1.EarnedCoins);

            var r2 = await _context.PracticeRecords.FindAsync(overflowRecord.Id);
            Assert.NotNull(r2);
            Assert.Equal(86400, r2.TimeTakenSeconds);

            // 再次审计应为 0 异常
            var auditAfter = await healthService.AuditDataIntegrityAsync();
            Assert.Equal(0, auditAfter.TotalInvalidPracticeRecords);
        }

        [Fact]
        public async Task HealAllInvariants_OrchestratesPracticeRecordSelfHealing()
        {
            var user = new User { Id = Guid.NewGuid(), Username = "OrchestrateUser", Role = UserRole.Student };
            var question = new Question { Id = Guid.NewGuid(), Stem = "全量编排测试题", CorrectAnswer = "C", Subject = "化学" };
            _context.Users.Add(user);
            _context.Questions.Add(question);

            // 注入异常流水记录
            _context.PracticeRecords.Add(new PracticeRecord
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                QuestionId = question.Id,
                TimeTakenSeconds = -15,
                ComboAtAnswer = -1,
                EarnedExp = -10,
                EarnedCoins = -5,
                IsCorrect = true
            });
            await _context.SaveChangesAsync();

            var healthService = new SystemHealthService(_context);
            var result = await healthService.HealAllInvariantsAsync();

            Assert.True(result.Success);
            Assert.True(result.HealedPracticeRecordsCount >= 1);
            Assert.True(result.TotalHealedCount >= 1);
            Assert.Contains(result.OperationsExecuted, op => op.Contains("答题流水领域不变量自愈"));
        }

        // ==========================================
        // 体验专家视角 2：STEM 理科全维度物理复合单位等价判卷
        // ==========================================

        [Theory]
        [InlineData("100 Pa", "100 Pa")]
        [InlineData("100 N/m^2", "100 Pa")]
        [InlineData("100 N/m²", "100 Pa")]
        [InlineData("100 N*m^-2", "100 Pa")]
        [InlineData("100 帕", "100 Pa")]
        [InlineData("100 帕斯卡", "100 Pa")]
        [InlineData("100 牛每平方米", "100 Pa")]
        [InlineData("100 N/m^2", "100 帕")]
        public void CheckFillInBlank_PressureUnitsEquivalence_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"压强单位判分不符: 用户输入 '{user}' 应等价于正确答案 '{correct}'");
        }

        [Theory]
        [InlineData("10 F", "10 F")]
        [InlineData("10 C/V", "10 F")]
        [InlineData("10 库/伏", "10 F")]
        [InlineData("10 库仑每伏特", "10 F")]
        [InlineData("10 法拉", "10 F")]
        [InlineData("10 法", "10 F")]
        public void CheckFillInBlank_CapacitanceUnitsEquivalence_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"电容单位判分不符: 用户输入 '{user}' 应等价于正确答案 '{correct}'");
        }

        [Theory]
        [InlineData("2 H", "2 H")]
        [InlineData("2 Wb/A", "2 H")]
        [InlineData("2 韦伯每安培", "2 H")]
        [InlineData("2 亨利", "2 H")]
        [InlineData("2 亨", "2 H")]
        public void CheckFillInBlank_InductanceUnitsEquivalence_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"电感单位判分不符: 用户输入 '{user}' 应等价于正确答案 '{correct}'");
        }

        [Theory]
        [InlineData("5 Wb", "5 Wb")]
        [InlineData("5 V*s", "5 Wb")]
        [InlineData("5 V·s", "5 Wb")]
        [InlineData("5 伏秒", "5 Wb")]
        [InlineData("5 T*m^2", "5 Wb")]
        [InlineData("5 特斯拉平方米", "5 Wb")]
        [InlineData("5 韦伯", "5 Wb")]
        public void CheckFillInBlank_MagneticFluxUnitsEquivalence_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"磁通量单位判分不符: 用户输入 '{user}' 应等价于正确答案 '{correct}'");
        }

        [Theory]
        [InlineData("4.2*10^3 J/(kg*℃)", "4.2*10^3 J/(kg*K)")]
        [InlineData("4200 J/(kg*℃)", "4200 J/(kg*K)")]
        [InlineData("4200 焦每千克摄氏度", "4200 J/(kg*K)")]
        [InlineData("4200 J/(kg*K)", "4200 J/(kg*K)")]
        public void CheckFillInBlank_SpecificHeatCapacityUnitsEquivalence_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"比热容单位判分不符: 用户输入 '{user}' 应等价于正确答案 '{correct}'");
        }

        [Theory]
        [InlineData("0.5 mol/L", "0.5 mol/L")]
        [InlineData("0.5 mol/dm^3", "0.5 mol/L")]
        [InlineData("0.5 摩尔每升", "0.5 mol/L")]
        [InlineData("0.5 摩/升", "0.5 mol/L")]
        public void CheckFillInBlank_ConcentrationUnitsEquivalence_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"物质的量浓度单位判分不符: 用户输入 '{user}' 应等价于正确答案 '{correct}'");
        }

        // ==========================================
        // 体验专家视角 3：数学几何全等、相似、三角形标记与向量模长/范数
        // ==========================================

        [Theory]
        [InlineData("\\triangle ABC \\cong \\triangle DEF", "△ABC ≌ △DEF")]
        [InlineData("三角形ABC 全等于 三角形DEF", "△ABC ≌ △DEF")]
        [InlineData("△ABC ≌ △DEF", "△ABC ≌ △DEF")]
        [InlineData("\\triangle ABC \\sim \\triangle DEF", "△ABC ∽ △DEF")]
        [InlineData("三角形ABC 相似于 三角形DEF", "△ABC ∽ △DEF")]
        public void CheckFillInBlank_GeometricCongruenceAndSimilarity_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"几何关系符号判分不符: 用户输入 '{user}' 应等价于正确答案 '{correct}'");
        }

        [Theory]
        [InlineData("\\|\\vec{a}\\|", "|a|")]
        [InlineData("||\\vec{a}||", "|a|")]
        [InlineData("|\\vec{a}|", "|a|")]
        [InlineData("||a||", "|a|")]
        [InlineData("|a|", "|a|")]
        public void CheckFillInBlank_VectorNormAndMagnitude_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"向量模长/范数符号判分不符: 用户输入 '{user}' 应等价于正确答案 '{correct}'");
        }
    }
}
