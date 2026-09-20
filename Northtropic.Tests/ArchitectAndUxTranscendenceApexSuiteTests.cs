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
    /// 系统架构师与用户体验（UX）超越巅峰验证套件
    /// 包含：
    /// 1. UX 体验专家：
    ///    - 生物遗传学 ABO 复等位基因书写等价 (I^A I^B <=> I^B I^A <=> IAIB <=> I(A)I(B), I^A i <=> i I^A)
    ///    - 常染色体与伴性遗传混合基因型与配子置换 (Aa X^B Y <=> X^B Y Aa <=> aA Y X^B, AX^B <=> X^B A)
    ///    - 热学比热容与比潜热复合单位换算 (kJ/(kg·K) <=> J/(kg·℃), kJ/kg <=> J/kg)
    ///    - 电磁场强度与磁感应强度 (kV/m <=> V/m <=> N/C, T <=> Gauss <=> mT)
    ///    - 数学排列组合多种主流记号等价 (C_n^m <=> \binom{n}{m} <=> C(n,m) <=> 数值, A_n^m <=> P_n^m <=> A(n,m) <=> 数值)
    ///    - 专属教学匹配反馈理由 (GenerateEquivalentMatchReason)
    /// 2. 系统架构师：
    ///    - 用户鉴权与安全态不变量（超级管理员特权豁免、空密码兜底、会话超时范围收敛、每日目标量范围收敛）
    ///    - 错题本艾宾浩斯记忆周期不变量（复习达标自动掌握、已掌握复习次数补偿、未来时间戳漂移修复）
    ///    - 家庭作业完成态拓扑与截止时限时序自愈（已完成自动闭环、截止日期早于创建日期修正）
    ///    - 学生答题流水时间戳漂移与用时自愈（未来/远古脏时间戳纠正、负数与超时用时收敛）
    /// </summary>
    public class ArchitectAndUxTranscendenceApexSuiteTests
    {
        #region 1. UX: 生物遗传学等价匹配测试 (ABO血型、伴性混合、配子)

        [Theory]
        [InlineData("I^A I^B", "I^B I^A")]
        [InlineData("I^A I^B", "I(A)I(B)")]
        [InlineData("I^A I^B", "IAIB")]
        [InlineData("I^A i", "i I^A")]
        [InlineData("I^A i", "I(A)i")]
        [InlineData("I^A i", "IAi")]
        [InlineData("I^B i", "i I^B")]
        [InlineData("I^B i", "IBi")]
        [InlineData("ii", "ii")]
        [InlineData("I^A I^A", "I(A)I(A)")]
        [InlineData("I^B I^B", "IBIB")]
        public void PracticeService_CheckFillInBlankMatch_AboBloodGroupGenotypes(string correct, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("Aa X^B Y", "X^B Y Aa")]
        [InlineData("Aa X^B Y", "aA Y X^B")]
        [InlineData("Aa X^B X^b", "X^B X^b Aa")]
        [InlineData("Aa X^B X^b", "X^b X^B aA")]
        [InlineData("aa X^b X^b", "X^b X^b aa")]
        [InlineData("AX^B", "X^B A")]
        [InlineData("aY", "Y a")]
        [InlineData("aX^b", "X^b a")]
        public void PracticeService_CheckFillInBlankMatch_SexLinkedAndAutosomalHybridCrosses(string correct, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        #endregion

        #region 2. UX: 热学、电场与磁场单位与量纲等价测试

        [Theory]
        [InlineData("4.2 kJ/(kg·K)", "4200 J/(kg·K)")]
        [InlineData("4.2 kJ/(kg·℃)", "4200 J/(kg·℃)")]
        [InlineData("4.2 kJ/(kg·K)", "4200 焦耳每千克开")]
        [InlineData("4.2 千焦每千克摄氏度", "4200 焦每千克摄氏度")]
        public void PracticeService_CheckFillInBlankMatch_SpecificHeatCapacityTolerances(string correct, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("2260 kJ/kg", "2260000 J/kg")]
        [InlineData("334 kJ/kg", "334000 J/kg")]
        [InlineData("334 千焦每千克", "334000 焦每千克")]
        public void PracticeService_CheckFillInBlankMatch_LatentHeatTolerances(string correct, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("1 kV/m", "1000 V/m")]
        [InlineData("1000 V/m", "1000 N/C")]
        [InlineData("1 千伏/米", "1000 伏/米")]
        [InlineData("1000 伏/米", "1000 牛顿每库仑")]
        public void PracticeService_CheckFillInBlankMatch_ElectricFieldStrengthTolerances(string correct, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("1 T", "10000 Gauss")]
        [InlineData("1 T", "10000 gs")]
        [InlineData("1 特斯拉", "10000 高斯")]
        [InlineData("1 T", "1000 mT")]
        [InlineData("1 特斯拉", "1000 毫特")]
        public void PracticeService_CheckFillInBlankMatch_MagneticFieldTolerances(string correct, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        #endregion

        #region 3. UX: 数学排列组合等价测试

        [Theory]
        [InlineData("C_5^2", "\\binom{5}{2}")]
        [InlineData("C_5^2", "C(5, 2)")]
        [InlineData("C_5^2", "10")]
        [InlineData("C_5^2", "C_5^3")]
        [InlineData("\\binom{6}{2}", "15")]
        [InlineData("C_4^0", "1")]
        [InlineData("C_4^4", "1")]
        public void PracticeService_CheckFillInBlankMatch_CombinatoricsEquivalence(string correct, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Theory]
        [InlineData("A_5^2", "P_5^2")]
        [InlineData("A_5^2", "A(5, 2)")]
        [InlineData("A_5^2", "P(5, 2)")]
        [InlineData("A_5^2", "20")]
        [InlineData("A_4^4", "24")]
        public void PracticeService_CheckFillInBlankMatch_PermutationsEquivalence(string correct, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct));
        }

        [Fact]
        public void PracticeService_GenerateEquivalentMatchReason_ReturnsSpecializedPedagogicalBadges()
        {
            // 1. 遗传学反馈
            var qGenetics = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "I^A I^B" };
            var reasonGenetics = PracticeService.GenerateEquivalentMatchReason(qGenetics, "I^B I^A");
            Assert.Contains("遗传学基因型等价", reasonGenetics);

            // 2. 排列组合反馈
            var qComb = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "C_5^2" };
            var reasonComb = PracticeService.GenerateEquivalentMatchReason(qComb, "C(5, 2)");
            Assert.Contains("数学排列组合记号等价", reasonComb);

            // 3. 热学比热容单位换算反馈
            var qUnit = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "4.2 kJ/(kg·K)" };
            var reasonUnit = PracticeService.GenerateEquivalentMatchReason(qUnit, "4200 J/(kg·K)");
            Assert.Contains("理化科学量纲与单位换算等价", reasonUnit);
        }

        #endregion

        #region 4. 系统架构师：用户安全态与生命周期自愈测试

        [Fact]
        public async Task SystemHealthService_HealGamificationInvariantsAsync_HealsSecurityAndSessionInvariants()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var superAdmin = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "admin_test",
                    Role = UserRole.SuperAdmin,
                    AccountStatus = UserAccountStatus.Rejected, // 超级管理员被误操作拒绝
                    Password = "", // 密码为空
                    SessionTimeoutMinutes = 2, // 会话超时过小 (<5)
                    DailyTargetQuestions = 300 // 目标刷题量越界 (>200)
                };

                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "student_test",
                    Role = UserRole.Student,
                    Password = "   ", // 空白密码
                    SessionTimeoutMinutes = 3000, // 超时过大 (>1440)
                    DailyTargetQuestions = 2 // 目标题量过小 (<5)
                };

                context.Users.AddRange(superAdmin, student);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 审计应检测出 2 名用户安全态不变量异常
                var audit = await healthService.AuditDataIntegrityAsync();
                Assert.True(audit.TotalUsersWithInvalidBalances >= 2);

                // 执行自愈
                int healed = await healthService.HealGamificationInvariantsAsync();
                Assert.True(healed >= 2);

                // 验证超级管理员特权豁免与安全自愈
                var sa = await context.Users.FindAsync(superAdmin.Id);
                Assert.NotNull(sa);
                Assert.Equal(UserAccountStatus.Approved, sa.AccountStatus);
                Assert.Equal("123456", sa.Password);
                Assert.True(sa.MustChangePassword);
                Assert.Equal(5, sa.SessionTimeoutMinutes);
                Assert.Equal(200, sa.DailyTargetQuestions);

                // 验证学生参数自愈
                var st = await context.Users.FindAsync(student.Id);
                Assert.NotNull(st);
                Assert.Equal("123456", st.Password);
                Assert.True(st.MustChangePassword);
                Assert.Equal(1440, st.SessionTimeoutMinutes);
                Assert.Equal(20, st.DailyTargetQuestions);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        #endregion

        #region 5. 系统架构师：错题本艾宾浩斯记忆与掌握态不变量自愈

        [Fact]
        public async Task SystemHealthService_HealErrorBookInvariantsAsync_HealsMasteryAndTimestamps()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var user = new User { Id = Guid.NewGuid(), Username = "ErrTestStudent", Role = UserRole.Student };
                var q1 = new Question { Id = Guid.NewGuid(), Stem = "错题1", CorrectAnswer = "A", Subject = "生物" };
                var q2 = new Question { Id = Guid.NewGuid(), Stem = "错题2", CorrectAnswer = "B", Subject = "生物" };
                var q3 = new Question { Id = Guid.NewGuid(), Stem = "错题3", CorrectAnswer = "C", Subject = "生物" };
                context.Users.Add(user);
                context.Questions.AddRange(q1, q2, q3);
                await context.SaveChangesAsync();

                var e1 = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = q1.Id,
                    RevisionCount = 4,
                    IsMastered = false, // 复习已满 4 次却未标记掌握
                    ErrorReasonCategory = "概念模糊"
                };

                var e2 = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = q2.Id,
                    RevisionCount = 0,
                    IsMastered = true, // 标记掌握但复习次数为 0
                    LastRevisedAt = DateTime.Now,
                    ErrorReasonCategory = "审题不清"
                };

                var e3 = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    QuestionId = q3.Id,
                    RevisionCount = 1,
                    IsMastered = false,
                    CreatedAt = DateTime.Now.AddDays(30), // 未来时间戳漂移
                    ErrorReasonCategory = "计算失误"
                };

                context.ErrorItems.AddRange(e1, e2, e3);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 审计应检测出 3 条错题不变量异常
                var audit = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(3, audit.TotalInvalidErrorItems);

                // 执行自愈
                int healed = await healthService.HealErrorBookInvariantsAsync();
                Assert.Equal(3, healed);

                // 检验 e1: 自动掌握
                var healedE1 = await context.ErrorItems.FindAsync(e1.Id);
                Assert.NotNull(healedE1);
                Assert.True(healedE1.IsMastered);
                Assert.NotNull(healedE1.LastRevisedAt);

                // 检验 e2: 复习次数提升为至少 1 次
                var healedE2 = await context.ErrorItems.FindAsync(e2.Id);
                Assert.NotNull(healedE2);
                Assert.Equal(1, healedE2.RevisionCount);

                // 检验 e3: 未来时间戳重置回当前时间
                var healedE3 = await context.ErrorItems.FindAsync(e3.Id);
                Assert.NotNull(healedE3);
                Assert.True(healedE3.CreatedAt <= DateTime.Now.AddSeconds(5));

                // 再次审计应为 0 异常
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.TotalInvalidErrorItems);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        #endregion

        #region 6. 系统架构师：家庭作业拓扑闭环与截止时间自愈

        [Fact]
        public async Task SystemHealthService_HealHomeworkAssignmentInvariantsAsync_AutoCompletesAndHealsDeadlines()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var student = new User { Id = Guid.NewGuid(), Username = "HwTestStudent", Role = UserRole.Student };
                var teacher = new User { Id = Guid.NewGuid(), Username = "HwTestTeacher", Role = UserRole.Teacher };
                context.Users.AddRange(student, teacher);
                await context.SaveChangesAsync();

                var h1 = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    CreatorUserId = teacher.Id,
                    StudentUserId = student.Id,
                    Title = "物理力学培优",
                    Subject = "初中物理",
                    QuestionCount = 5,
                    TotalAnswered = 5, // 全部作答完毕
                    CorrectCount = 4,
                    IsCompleted = false, // 但未标记完成
                    CompletedAt = null,
                    CreatedAt = DateTime.Now.AddDays(-2),
                    Deadline = DateTime.Now.AddDays(-10) // 截止时间在创建时间之前
                };

                context.HomeworkAssignments.Add(h1);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 审计应报作业分配异常
                var audit = await healthService.AuditDataIntegrityAsync();
                Assert.True(audit.TotalInvalidHomeworkAssignments > 0);

                // 执行自愈
                int healed = await healthService.HealHomeworkAssignmentInvariantsAsync();
                Assert.True(healed > 0);

                // 校验自愈效果
                var healedH1 = await context.HomeworkAssignments.FindAsync(h1.Id);
                Assert.NotNull(healedH1);
                Assert.True(healedH1.IsCompleted);
                Assert.NotNull(healedH1.CompletedAt);
                Assert.True(healedH1.Deadline > healedH1.CreatedAt);

                // 再次审计异常清零
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.TotalInvalidHomeworkAssignments);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        #endregion

        #region 7. 系统架构师：学生答题流水时间戳与时长自愈

        [Fact]
        public async Task SystemHealthService_HealPracticeRecordInvariantsAsync_HealsTimestampsAndDuration()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var testUser = new User { Id = Guid.NewGuid(), Username = "TestStudent", Role = UserRole.Student };
                var testQuestion = new Question { Id = Guid.NewGuid(), Stem = "测试题目", CorrectAnswer = "A", Subject = "数学" };
                context.Users.Add(testUser);
                context.Questions.Add(testQuestion);
                await context.SaveChangesAsync();

                var r1 = new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = testUser.Id,
                    QuestionId = testQuestion.Id,
                    AnsweredAt = DateTime.Now.AddDays(10), // 未来异常漂移
                    TimeTakenSeconds = -5, // 负数用时
                    ComboAtAnswer = -2,
                    EarnedExp = -10,
                    EarnedCoins = -5
                };

                var r2 = new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = testUser.Id,
                    QuestionId = testQuestion.Id,
                    AnsweredAt = new DateTime(2015, 5, 20), // 远古时间戳 (<2020)
                    TimeTakenSeconds = 120000 // 超出 24 小时 (>86400)
                };

                context.PracticeRecords.AddRange(r1, r2);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 审计应报答题流水异常
                var audit = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(2, audit.TotalInvalidPracticeRecords);

                // 执行自愈
                int healed = await healthService.HealPracticeRecordInvariantsAsync();
                Assert.Equal(2, healed);

                // 检验 r1
                var healedR1 = await context.PracticeRecords.FindAsync(r1.Id);
                Assert.NotNull(healedR1);
                Assert.True(healedR1.AnsweredAt <= DateTime.Now.AddSeconds(5));
                Assert.Equal(0, healedR1.TimeTakenSeconds);
                Assert.Equal(0, healedR1.ComboAtAnswer);
                Assert.Equal(0, healedR1.EarnedExp);
                Assert.Equal(0, healedR1.EarnedCoins);

                // 检验 r2
                var healedR2 = await context.PracticeRecords.FindAsync(r2.Id);
                Assert.NotNull(healedR2);
                Assert.True(healedR2.AnsweredAt >= new DateTime(2020, 1, 1));
                Assert.Equal(86400, healedR2.TimeTakenSeconds);

                // 再次审计异常清零
                var auditAfter = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(0, auditAfter.TotalInvalidPracticeRecords);
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
