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
    /// 系统架构师与用户体验（UX）主权巅峰演进验证套件
    /// 覆盖：
    /// 1. UX: 理工科流体动力粘度、热力学、密度、压强、声学、光学、核辐射、微观尺度、向量与反三角函数等全量容差
    /// 2. UX: 全角符号与括号规范化（【】、［］、｛｝）
    /// 3. 系统架构师：作业不变量自愈（难度范围、学科标题缺省自愈与准确率/状态联动）
    /// 4. 系统架构师：用户游戏化与账户生命周期自愈（过期 Buff 剪枝、连击越界自愈、称号与审核状态补齐）
    /// 5. 系统架构师：试题领域不变量自愈（难度/经验奖励区间、元数据缺省对齐、选项破损修复）
    /// 6. 系统架构师：自适应维护引擎（自愈规划编排与 Action E 全量领域不变量自愈）
    /// </summary>
    public class ArchitectAndUxSovereignZenithElevationTests
    {
        #region 1. UX: 理工科单位容差与符号规范化验证 (PracticeService.CheckFillInBlankMatch)

        [Theory]
        [InlineData("1.2 Pa·s", "1.2 N·s/m^2")]
        [InlineData("1.2 Pa·s", "1.2 帕·秒")]
        [InlineData("1.2 Pa·s", "1.2 帕秒")]
        [InlineData("1.2 Pa·s", "1.2 kg/(m·s)")]
        [InlineData("0.05 m^2/s", "0.05 平方米每秒")]
        public void PracticeService_CheckFillInBlankMatch_FluidKinematicViscosityTolerances(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
        }

        [Theory]
        [InlineData("-393.5 kJ/mol", "-393.5 千焦每摩尔")]
        [InlineData("4.2 kJ/kg", "4.2 千焦每千克")]
        [InlineData("4200 J/kg", "4200 焦每千克")]
        [InlineData("18 g/mol", "18 克每摩尔")]
        public void PracticeService_CheckFillInBlankMatch_EnthalpyAndHeatTolerances(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
        }

        [Theory]
        [InlineData("1.84 g/cm^3", "1.84 克每立方厘米")]
        [InlineData("1.84 g/cm^3", "1.84 g/mL")]
        [InlineData("1840 kg/m^3", "1840 千克每立方米")]
        public void PracticeService_CheckFillInBlankMatch_DensityTolerances(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
        }

        [Theory]
        [InlineData("101.3 kPa", "101.3 千帕")]
        [InlineData("0.1 MPa", "0.1 兆帕")]
        [InlineData("1013 hPa", "1013 百帕")]
        [InlineData("65 dB", "65 分贝")]
        [InlineData("100 cd", "100 坎德拉")]
        [InlineData("500 Bq", "500 贝克勒尔")]
        [InlineData("2 Gy", "2 戈瑞")]
        [InlineData("1.5 Sv", "1.5 希沃特")]
        [InlineData("500 nm", "500 纳米")]
        [InlineData("10 μm", "10 微米")]
        [InlineData("100 pm", "100 皮米")]
        public void PracticeService_CheckFillInBlankMatch_PressureAcousticLuminousNuclearMicroTolerances(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
        }

        [Theory]
        [InlineData(@"\vec{F}", @"\mathbf{F}")]
        [InlineData(@"\vec{F}", @"\boldsymbol{F}")]
        [InlineData(@"\vec{F}", @"\overrightarrow{F}")]
        [InlineData(@"\vec{F}", "向量F")]
        [InlineData(@"\arcsin(x)", @"\sin^{-1}(x)")]
        [InlineData(@"\arcsin(x)", "asin(x)")]
        [InlineData(@"\arccos(x)", @"\cos^{-1}(x)")]
        [InlineData(@"\arccos(x)", "acos(x)")]
        [InlineData(@"\arctan(x)", @"\tan^{-1}(x)")]
        [InlineData(@"\arctan(x)", "atan(x)")]
        [InlineData("【1, 5】", "[1, 5]")]
        [InlineData("｛x | x > 0｝", "{x | x > 0}")]
        public void PracticeService_CheckFillInBlankMatch_VectorInverseTrigAndFullWidthBracketsTolerances(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
        }

        #endregion

        #region 2. 系统架构师：作业不变量自愈测试 (HealHomeworkAssignmentInvariantsAsync)

        [Fact]
        public async Task SystemHealthService_HealHomeworkAssignmentInvariantsAsync_ClampsDifficultyAndRepairsMetadata()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var studentId = Guid.NewGuid();
                var teacherId = Guid.NewGuid();

                var student = new User { Id = studentId, Username = "student_hw", Role = UserRole.Student };
                var teacher = new User { Id = teacherId, Username = "teacher_hw", Role = UserRole.Teacher };
                context.Users.AddRange(student, teacher);

                // 构造难度越界、空标题、空学科的作业
                var abnormalHw1 = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    StudentUserId = studentId,
                    CreatorUserId = teacherId,
                    Title = "",
                    Subject = "   ",
                    TargetDifficulty = 9, // 越界，应 clamp 为 5
                    QuestionCount = 5,
                    TotalAnswered = 5,
                    CorrectCount = 4,
                    AccuracyRate = 10, // 脱节，4/5 = 80%
                    Score = 120, // 越界，应 clamp 为 100
                    IsCompleted = true,
                    CompletedAt = null // 缺失时间戳
                };

                var abnormalHw2 = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    StudentUserId = studentId,
                    CreatorUserId = teacherId,
                    Title = "微积分强化",
                    Subject = "数学",
                    TargetDifficulty = 0, // 越界，应 clamp 为 1
                    QuestionCount = 0, // 题量非法，应矫正为 1
                    TotalAnswered = -1,
                    CorrectCount = -2,
                    AccuracyRate = 0,
                    Score = -10,
                    IsCompleted = false,
                    CompletedAt = DateTime.Now // 未完成却带有时间戳，应置空
                };

                context.HomeworkAssignments.AddRange(abnormalHw1, abnormalHw2);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 执行审计
                var audit = await healthService.AuditDataIntegrityAsync();
                Assert.Equal(2, audit.TotalInvalidHomeworkAssignments);

                // 执行自愈
                int healedCount = await healthService.HealHomeworkAssignmentInvariantsAsync();
                Assert.Equal(2, healedCount);

                // 检验 hw1
                var h1 = await context.HomeworkAssignments.FindAsync(abnormalHw1.Id);
                Assert.NotNull(h1);
                Assert.Equal("专属专项强化作业", h1.Title);
                Assert.Equal("初中物理", h1.Subject);
                Assert.Equal(5, h1.TargetDifficulty);
                Assert.Equal(80, h1.AccuracyRate);
                Assert.Equal(100, h1.Score);
                Assert.NotNull(h1.CompletedAt);

                // 检验 hw2
                var h2 = await context.HomeworkAssignments.FindAsync(abnormalHw2.Id);
                Assert.NotNull(h2);
                Assert.Equal(1, h2.TargetDifficulty);
                Assert.Equal(1, h2.QuestionCount);
                Assert.Equal(0, h2.TotalAnswered);
                Assert.Equal(0, h2.CorrectCount);
                Assert.Equal(0, h2.Score);
                Assert.Null(h2.CompletedAt);

                // 再次审计，不变量异常应归零
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

        #region 3. 系统架构师：用户游戏化与账户生命周期自愈测试 (HealGamificationInvariantsAsync)

        [Fact]
        public async Task SystemHealthService_HealGamificationInvariantsAsync_PrunesExpiredBuffsAndClampsStreak()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var user1 = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "user_buffs",
                    TotalAnswered = 10,
                    TotalCorrect = 8,
                    CurrentStreak = 50, // 越界，答对总数仅 8，连击不能超 8
                    ExpBoostUntil = DateTime.Now.AddDays(-40), // 过期 40 天
                    GoldBoostUntil = DateTime.Now.AddDays(-35), // 过期 35 天
                    ActiveTitle = "   ", // 空白称号，应重置为 "青铜学童"
                    AccountStatus = UserAccountStatus.Approved,
                    ApprovedAt = null, // 审核通过但无审核时间戳
                    RegisteredAt = DateTime.Now.AddDays(-100)
                };

                var user2 = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "user_rejected",
                    AccountStatus = UserAccountStatus.Rejected,
                    RejectReason = "", // 已驳回但拒绝原因为空
                    TotalAnswered = 5,
                    TotalCorrect = 5,
                    Exp = 500, // 经验值超额，需计算升级
                    Level = 1
                };

                context.Users.AddRange(user1, user2);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 执行审计
                var audit = await healthService.AuditDataIntegrityAsync();
                Assert.True(audit.TotalUsersWithInvalidBalances >= 2);

                // 执行自愈
                int healedCount = await healthService.HealGamificationInvariantsAsync();
                Assert.True(healedCount >= 2);

                // 检验 user1
                var u1 = await context.Users.FindAsync(user1.Id);
                Assert.NotNull(u1);
                Assert.Equal(8, u1.CurrentStreak);
                Assert.Null(u1.ExpBoostUntil);
                Assert.Null(u1.GoldBoostUntil);
                Assert.Equal("青铜学童", u1.ActiveTitle);
                Assert.Equal(u1.RegisteredAt, u1.ApprovedAt);

                // 检验 user2
                var u2 = await context.Users.FindAsync(user2.Id);
                Assert.NotNull(u2);
                Assert.Equal("未说明具体原因", u2.RejectReason);
                Assert.True(u2.Level > 1); // 自动结算升级
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        #endregion

        #region 4. 系统架构师：试题领域不变量自愈测试 (HealCorruptedQuestionsAsync)

        [Fact]
        public async Task SystemHealthService_HealCorruptedQuestionsAsync_RepairsDifficultyRewardAndMetadata()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var teacherId = Guid.NewGuid();
                context.Users.Add(new User { Id = teacherId, Username = "stem_teacher", Role = UserRole.Teacher });

                var corruptedQuestion = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherId,
                    Stem = "测试试题：下列哪个是物理单位？",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "INVALID_JSON_STRING", // 选项损坏
                    CorrectAnswer = "",
                    Difficulty = 10, // 越界，应 clamp 为 5
                    BaseExpReward = 200, // 越界，应 clamp 为 100
                    Subject = "", // 空学科
                    Category = "", // 空分类
                    GradeTarget = "" // 空学段
                };

                context.Questions.Add(corruptedQuestion);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 执行审计
                var audit = await healthService.AuditDataIntegrityAsync();
                Assert.True(audit.TotalCorruptedQuestions >= 1);

                // 执行自愈
                int healed = await healthService.HealCorruptedQuestionsAsync();
                Assert.True(healed >= 1);

                // 检验自愈后结果
                var q = await context.Questions.FindAsync(corruptedQuestion.Id);
                Assert.NotNull(q);
                Assert.Equal(5, q.Difficulty);
                Assert.Equal(100, q.BaseExpReward);
                Assert.Equal("通用知识", q.Subject);
                Assert.Equal("基础概念", q.Category);
                Assert.Equal("通用", q.GradeTarget);
                Assert.Equal("A", q.CorrectAnswer);
                Assert.StartsWith("[", q.OptionsJson.Trim());
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        #endregion

        #region 5. 系统架构师：自适应维护引擎全量领域不变量自愈编排测试 (ExecuteAdaptiveMaintenancePlanAsync Action E)

        [Fact]
        public async Task SystemHealthService_AdaptiveMaintenancePlan_IntegratesInvariantsSelfHealing()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var studentId = Guid.NewGuid();
                var teacherId = Guid.NewGuid();
                var student = new User { Id = studentId, Username = "adaptive_student", Role = UserRole.Student };
                var teacher = new User { Id = teacherId, Username = "adaptive_teacher", Role = UserRole.Teacher };
                context.Users.AddRange(student, teacher);

                // 构造异常作业（属于领域优化候选）
                var hw = new HomeworkAssignment
                {
                    Id = Guid.NewGuid(),
                    StudentUserId = studentId,
                    CreatorUserId = teacherId,
                    Title = "自适应维护测试作业",
                    Subject = "数学",
                    TargetDifficulty = 8, // 越界
                    QuestionCount = 5,
                    TotalAnswered = 5,
                    CorrectCount = 2,
                    AccuracyRate = 10,
                    Score = 150
                };
                context.HomeworkAssignments.Add(hw);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 1. 评估自适应维护计划
                var plan = await healthService.EvaluateAdaptiveMaintenancePlanAsync();
                Assert.True(plan.RequiresOptimization, "存在领域优化候选时，计划应激活 RequiresOptimization");
                Assert.Contains(plan.ActionReasons, r => r.Contains("领域业务模型优化"));

                // 2. 执行自适应维护计划
                var result = await healthService.ExecuteAdaptiveMaintenancePlanAsync(plan);
                Assert.NotNull(result);
                Assert.True(result.Success);
                Assert.Contains("全量领域模型不变量自愈", string.Join(" | ", result.ExecutedActions));

                // 3. 验证作业已被 Action E 成功自愈
                var healedHw = await context.HomeworkAssignments.FindAsync(hw.Id);
                Assert.NotNull(healedHw);
                Assert.Equal(5, healedHw.TargetDifficulty);
                Assert.Equal(100, healedHw.Score);
                Assert.Equal(40, healedHw.AccuracyRate);
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
