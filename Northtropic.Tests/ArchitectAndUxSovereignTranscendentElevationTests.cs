using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    /// <summary>
    /// 系统架构师与用户体验 (UX) 超验主权升华验证测试套件
    /// 涵盖：
    /// 1. UX: 高中/大学生物遗传学孟德尔两对及多对基因自由组合规律基因型置换无序等价 (AaBb <=> BbAa <=> aAbB, AaBbCc <=> CcBbAa)
    /// 2. UX: 伴性遗传基因型无序等价 (X^B X^b <=> X^b X^B, X^B Y <=> Y X^B) 与亲本杂交式置换
    /// 3. UX: 物理与化学核心物理量——密度跨尺度千进制科学单位智能对齐 (1.0 g/cm^3 <=> 1000 kg/m^3 <=> 1.0*10^3 kg/m^3 <=> 1.0 kg/L)
    /// 4. UX: 生物大分子与关键生化辅酶同义词智能映射 (ATP <=> 三磷酸腺苷, DNA <=> 脱氧核糖核酸, NADH <=> 还原型辅酶I 等)
    /// 5. 系统架构师：用户游戏化与连击极值不变量自愈 (MaxCombo < CurrentStreak 校准, TotalCorrect > TotalAnswered 纠偏, 跨日刷题量自动清零, 空白年级清洗)
    /// 6. 系统架构师：错题副本掌握度与防 Null 健壮性自愈 (RevisionCount >= 3 自动对齐 IsMastered, UserWrongAnswer/AiCustomAdvice 防空兜底)
    /// 7. 系统架构师：数据拓扑审计与全维全量自愈编排流水线闭环 (AuditDataIntegrityAsync & HealAllInvariantsAsync)
    /// </summary>
    public class ArchitectAndUxSovereignTranscendentElevationTests
    {
        #region 1. UX: 生物遗传学孟德尔基因型与伴性遗传等价验证

        [Theory]
        // 孟德尔双基因座常染色体二倍体 (独立遗传自由组合)
        [InlineData("AaBb", "BbAa")]
        [InlineData("AaBb", "aAbB")]
        [InlineData("AaBb", "bBaA")]
        [InlineData("AABB", "BBAA")]
        [InlineData("aabb", "bbaa")]
        [InlineData("AAbb", "bbAA")]
        [InlineData("aaBB", "BBaa")]
        [InlineData("AaBB", "BBAa")]
        [InlineData("AABb", "BbAA")]
        // 三基因座二倍体
        [InlineData("AaBbCc", "CcBbAa")]
        [InlineData("AaBbCc", "cCaAbB")]
        [InlineData("AABBCC", "CCBBAA")]
        // 配子单倍体
        [InlineData("AB", "BA")]
        [InlineData("Ab", "bA")]
        [InlineData("aB", "Ba")]
        [InlineData("ab", "ba")]
        [InlineData("ABC", "CBA")]
        public void PracticeService_CheckGenotypeEquivalence_MendelianAutosomalGenotypes(string standard, string user)
        {
            Assert.True(PracticeService.CheckGenotypeEquivalence(user, standard));
            Assert.True(PracticeService.CheckFillInBlankMatch(standard, user));
        }

        [Theory]
        // 伴性遗传基因型
        [InlineData("X^B X^b", "X^b X^B")]
        [InlineData("X^BX^b", "X^bX^B")]
        [InlineData("X^{B}X^{b}", "X^b X^B")]
        [InlineData("X^B Y", "Y X^B")]
        [InlineData("X^b Y", "Y X^b")]
        [InlineData("X^BY", "YX^B")]
        // 亲本杂交与测交组合无序对等
        [InlineData("AaBb × aabb", "aabb × AaBb")]
        [InlineData("AaBb * aabb", "aabb * AaBb")]
        [InlineData("AaBb x aabb", "aabb x AaBb")]
        public void PracticeService_CheckGenotypeEquivalence_SexLinkedAndParentCrosses(string standard, string user)
        {
            Assert.True(PracticeService.CheckGenotypeEquivalence(user, standard));
            Assert.True(PracticeService.CheckFillInBlankMatch(standard, user));
        }

        [Fact]
        public void PracticeService_GenerateEquivalentMatchReason_GeneratesGenotypeReason()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "AaBb" };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, "BbAa");
            Assert.Contains("生物遗传学基因型等价", reason);
            Assert.Contains("AaBb", reason);
        }

        #endregion

        #region 2. UX: 物理密度跨尺度换算等价验证 (g/cm³ <=> kg/m³ <=> kg/L)

        [Theory]
        [InlineData("1.0 g/cm^3", "1000 kg/m^3")]
        [InlineData("1000 kg/m^3", "1.0 g/cm^3")]
        [InlineData("1.0 g/cm^3", "1.0*10^3 kg/m^3")]
        [InlineData("1.84 g/cm^3", "1840 kg/m^3")]
        [InlineData("1840 kg/m^3", "1.84 g/cm^3")]
        [InlineData("0.8 g/cm^3", "800 kg/m^3")]
        [InlineData("1.0 g/cm^3", "1.0 kg/L")]
        [InlineData("1.0 g/cm^3", "1.0 g/mL")]
        [InlineData("13.6 g/cm^3", "13600 kg/m^3")]
        [InlineData("1.0 克/立方厘米", "1000 千克/立方米")]
        public void PracticeService_CheckScientificUnitMultiplierEquivalence_DensityTolerances(string expected, string user)
        {
            Assert.True(PracticeService.CheckScientificUnitMultiplierEquivalence(user, expected));
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
        }

        [Fact]
        public void PracticeService_GenerateEquivalentMatchReason_GeneratesDensityReason()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "1000 kg/m^3" };
            string reason = PracticeService.GenerateEquivalentMatchReason(q, "1.0 g/cm^3");
            Assert.Contains("密度", reason);
        }

        #endregion

        #region 3. UX: 生物大分子与关键生化辅酶同义词验证

        [Theory]
        [InlineData("ATP", "三磷酸腺苷")]
        [InlineData("三磷酸腺苷", "ATP")]
        [InlineData("ADP", "二磷酸腺苷")]
        [InlineData("DNA", "脱氧核糖核酸")]
        [InlineData("脱氧核糖核酸", "DNA")]
        [InlineData("RNA", "核糖核酸")]
        [InlineData("mRNA", "信使核糖核酸")]
        [InlineData("tRNA", "转运核糖核酸")]
        [InlineData("NADH", "还原型辅酶I")]
        [InlineData("NADPH", "还原型辅酶II")]
        [InlineData("甘油", "丙三醇")]
        [InlineData("胆矾", "CuSO4*5H2O")]
        [InlineData("蓝矾", "CuSO4*5H2O")]
        public void PracticeService_CheckFillInBlankMatch_BiochemicalSynonyms(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
        }

        #endregion

        #region 4. 系统架构师：用户游戏化与连击极值不变量自愈验证

        [Fact]
        public async Task SystemHealthService_HealGamificationInvariantsAsync_ReconcilesComboStreakAndStaleCount()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var abnormalUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "test_combo_anomalies",
                    TotalAnswered = 10,
                    TotalCorrect = 12, // 倒挂异常：Correct > Answered
                    CurrentStreak = 8,
                    MaxCombo = 3, // 异常：历史最高连击 3 < 当前活跃连击 8
                    TodayAnsweredCount = 15,
                    TodayCountDate = DateTime.Today.AddDays(-3), // 过期 3 天的作答计数
                    Grade = "   ", // 空白年级
                    Level = 1,
                    Exp = 0,
                    Coins = 100,
                    ActiveTitle = "青铜学童",
                    AccountStatus = UserAccountStatus.Approved,
                    ApprovedAt = DateTime.Now.AddDays(-10)
                };

                context.Users.Add(abnormalUser);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 1. 审计阶段：应精准检测到不变量异常
                var auditBefore = await healthService.AuditDataIntegrityAsync();
                Assert.True(auditBefore.TotalUsersWithInvalidBalances >= 1);

                // 2. 自愈执行阶段
                int healedCount = await healthService.HealGamificationInvariantsAsync();
                Assert.True(healedCount >= 1);

                // 3. 校验纠偏后的实体属性
                var healedUser = await context.Users.FindAsync(abnormalUser.Id);
                Assert.NotNull(healedUser);
                Assert.Equal(10, healedUser.TotalCorrect); // 纠偏为 TotalAnswered (10)
                Assert.Equal(8, healedUser.CurrentStreak); // 依然在 <= TotalCorrect 范围内
                Assert.Equal(8, healedUser.MaxCombo); // 自动提升至 CurrentStreak (8)
                Assert.Equal(0, healedUser.TodayAnsweredCount); // 跨日历史计数清零
                Assert.Equal(DateTime.Today, healedUser.TodayCountDate.Date); // 时间戳滚入今日
                Assert.Equal("初中二年级", healedUser.Grade); // 默认兜底年级补齐

                // 4. 二次审计：不变量异常完全收敛归零
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.TotalUsersWithInvalidBalances);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        #endregion

        #region 5. 系统架构师：错题副本掌握度与防 Null 健壮性自愈验证

        [Fact]
        public async Task SystemHealthService_HealErrorBookInvariantsAsync_AutoMasteryAndNullGuards()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "error_user",
                    Grade = "初中三年级",
                    ActiveTitle = "黄金大师",
                    AccountStatus = UserAccountStatus.Approved,
                    ApprovedAt = DateTime.Now
                };
                context.Users.Add(user);

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试试题题干",
                    Subject = "初中生物",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "AaBb",
                    StandardAnalysis = "解析",
                    Difficulty = 3
                };
                context.Questions.Add(question);

                var abnormalItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = question.Id,
                    RevisionCount = 4, // 复习达 4 次，但未标记掌握
                    IsMastered = false,
                    LastRevisedAt = null,
                    ErrorReasonCategory = "", // 缺失分类，应自愈为 未分类
                    UserWrongAnswer = "错误选项",
                    AiCustomAdvice = "复习提示",
                    CreatedAt = DateTime.Now.AddDays(-5)
                };
                context.ErrorItems.Add(abnormalItem);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 执行错题本自愈
                int healedCount = await healthService.HealErrorBookInvariantsAsync();
                Assert.True(healedCount >= 1);

                // 验证自愈效果
                var healedItem = await context.ErrorItems.FindAsync(abnormalItem.Id);
                Assert.NotNull(healedItem);
                Assert.True(healedItem.IsMastered); // 自动对齐已掌握
                Assert.NotNull(healedItem.LastRevisedAt); // 补全时间戳
                Assert.Equal("未分类", healedItem.ErrorReasonCategory);
                Assert.Equal("错误选项", healedItem.UserWrongAnswer);
                Assert.Equal("复习提示", healedItem.AiCustomAdvice);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        #endregion

        #region 6. 系统架构师：全量领域不变量编排自愈流水线闭环验证

        [Fact]
        public async Task SystemHealthService_HealAllInvariantsAsync_OrchestratesFullPipelineCleanly()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "pipeline_user",
                    TotalAnswered = 5,
                    TotalCorrect = 7, // 倒挂
                    CurrentStreak = 5,
                    MaxCombo = 2, // 异常 MaxCombo < Streak
                    TodayAnsweredCount = 10,
                    TodayCountDate = DateTime.Today.AddDays(-2),
                    Grade = "",
                    Level = 1,
                    Exp = 0,
                    Coins = 50,
                    ActiveTitle = "青铜学童",
                    AccountStatus = UserAccountStatus.Approved,
                    ApprovedAt = DateTime.Now
                };
                context.Users.Add(user);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 启动全量自愈编排流水线
                var result = await healthService.HealAllInvariantsAsync();
                Assert.NotNull(result);
                Assert.True(result.Success);
                Assert.True(result.TotalHealedCount >= 1);
                Assert.True(result.OperationsExecuted.Count >= 1);

                // 验证用户状态已被全量自愈修正
                var refreshedUser = await context.Users.FindAsync(user.Id);
                Assert.NotNull(refreshedUser);
                Assert.True(refreshedUser.TotalCorrect <= refreshedUser.TotalAnswered);
                Assert.True(refreshedUser.MaxCombo >= refreshedUser.CurrentStreak);
                Assert.Equal(0, refreshedUser.TodayAnsweredCount);
                Assert.Equal("初中二年级", refreshedUser.Grade);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        #endregion
    }
}
