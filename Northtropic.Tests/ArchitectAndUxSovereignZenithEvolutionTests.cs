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
    /// 系统架构师与用户体验（UX）主权巅峰演进全面验证测试套件
    /// 包含：
    /// 1. 架构维度：试题领域不变量审计与自愈（审批/驳回状态与公开可见性对齐、选项破损修复、多选题答案规范化、非选择题选项清空）
    /// 2. 自然科学与理科维度：核物理辐射（Bq/Ci, Gy/rad, Sv/rem）、流体真空压强（Torr, psi）、计算机存储单位（Byte, bit, KB, MB, GB, TB）
    /// 3. 数学等价推理：排列组合多位参数解析（\binom{10}{3}, \binom{12}{4}）、对数换底公式（\log_a b <=> \frac{\ln b}{\ln a}）、区间变量属于引导词解构（x \in [1, 2]）
    /// 4. 用户体验（UX）答题与战报反馈维度：AnswerCheckResult.UserAnswer 贯穿传递、对等判定教学解析文案生成
    /// </summary>
    public class ArchitectAndUxSovereignZenithEvolutionTests
    {
        #region 1. 系统架构师维度：试题领域不变量审计与自愈测试

        [Fact]
        public async Task SystemHealthService_AuditAndHeal_RejectedAndPendingPublicExposure_EnforcesPrivate()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var teacherId = Guid.NewGuid();
                context.Users.Add(new User { Id = teacherId, Username = "arch_admin", Role = UserRole.Teacher });

                // 试题1：被驳回但错误保留 IsPublic = true
                var rejectedQuestion = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherId,
                    Stem = "测试试题1：已被驳回但标记为公开",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A\", \"B\", \"C\", \"D\"]",
                    CorrectAnswer = "A",
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Rejected
                };

                // 试题2：待审核但错误提前公开 IsPublic = true
                var pendingQuestion = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherId,
                    Stem = "测试试题2：待审核但标记为公开",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A\", \"B\", \"C\", \"D\"]",
                    CorrectAnswer = "B",
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Pending
                };

                // 试题3：单选题选项破损（空或少于2项）
                var brokenOptionsQuestion = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherId,
                    Stem = "测试试题3：单选题选项缺失",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[]",
                    CorrectAnswer = "A",
                    IsPublic = false,
                    PublishStatus = PublishStatusEnum.Private
                };

                // 试题4：多选题答案无序且带 JSON 括号格式
                var messyMultipleChoice = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherId,
                    Stem = "测试试题4：多选题答案格式需规范化",
                    Type = QuestionType.MultipleChoice,
                    OptionsJson = "[\"A\", \"B\", \"C\", \"D\"]",
                    CorrectAnswer = "[\"C\", \"A\", \"B\"]",
                    IsPublic = false,
                    PublishStatus = PublishStatusEnum.Private
                };

                // 试题5：填空题冗余挂载了非空 OptionsJson
                var nonChoiceWithOptions = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherId,
                    Stem = "测试试题5：填空题不应包含选项列表",
                    Type = QuestionType.FillInBlank,
                    OptionsJson = "[\"A\", \"B\"]",
                    CorrectAnswer = "42",
                    IsPublic = false,
                    PublishStatus = PublishStatusEnum.Private
                };

                context.Questions.AddRange(rejectedQuestion, pendingQuestion, brokenOptionsQuestion, messyMultipleChoice, nonChoiceWithOptions);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 1. 审计阶段
                var audit = await healthService.AuditDataIntegrityAsync();
                Assert.True(audit.TotalDesyncedPublishStatusQuestions >= 2);
                Assert.True(audit.TotalMalformedChoiceQuestions >= 2);

                // 2. 自愈修复阶段
                int healedCount = await healthService.HealCorruptedQuestionsAsync();
                Assert.True(healedCount >= 5);

                // 3. 验证领域不变量
                var q1 = await context.Questions.FindAsync(rejectedQuestion.Id);
                var q2 = await context.Questions.FindAsync(pendingQuestion.Id);
                var q3 = await context.Questions.FindAsync(brokenOptionsQuestion.Id);
                var q4 = await context.Questions.FindAsync(messyMultipleChoice.Id);
                var q5 = await context.Questions.FindAsync(nonChoiceWithOptions.Id);

                Assert.NotNull(q1);
                Assert.False(q1.IsPublic); // 驳回试题必须私有

                Assert.NotNull(q2);
                Assert.False(q2.IsPublic); // 待审核试题必须私有

                Assert.NotNull(q3);
                var opts3 = System.Text.Json.JsonSerializer.Deserialize<List<string>>(q3.OptionsJson);
                Assert.NotNull(opts3);
                Assert.True(opts3.Count >= 4); // 破损选项补齐为标准4选项

                Assert.NotNull(q4);
                Assert.Equal("C,A,B", q4.CorrectAnswer); // 多选题答案从 JSON 数组规范化为逗号分隔格式

                Assert.NotNull(q5);
                Assert.Equal("[]", q5.OptionsJson); // 非选择题清空选项
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        #endregion

        #region 2. STEM 自然科学与微观核物理等价推理测试

        [Theory]
        [InlineData("1 Ci", "3.7e10 Bq")]
        [InlineData("1 Ci", "3.7*10^10 Bq")]
        [InlineData("1 mCi", "37 MBq")]
        [InlineData("1 mCi", "3.7e7 Bq")]
        [InlineData("1 uCi", "3.7e4 Bq")]
        [InlineData("1 uCi", "37 kBq")]
        [InlineData("1 GBq", "10^9 Bq")]
        [InlineData("1000 Bq", "1 kBq")]
        public void PracticeService_CheckFillInBlankMatch_RadioactivityEquivalences(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
            Assert.True(PracticeService.CheckFillInBlankMatch(user, expected));
        }

        [Theory]
        [InlineData("1 Gy", "100 rad")]
        [InlineData("1 Gy", "1000 mGy")]
        [InlineData("2 kGy", "2000 Gy")]
        [InlineData("0.01 Gy", "1 rad")]
        public void PracticeService_CheckFillInBlankMatch_AbsorbedDoseEquivalences(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
            Assert.True(PracticeService.CheckFillInBlankMatch(user, expected));
        }

        [Theory]
        [InlineData("1 Sv", "100 rem")]
        [InlineData("1 Sv", "1000 mSv")]
        [InlineData("1 Sv", "10^6 uSv")]
        [InlineData("0.01 Sv", "1 rem")]
        public void PracticeService_CheckFillInBlankMatch_DoseEquivalentEquivalences(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
            Assert.True(PracticeService.CheckFillInBlankMatch(user, expected));
        }

        [Theory]
        [InlineData("760 Torr", "1 atm")]
        [InlineData("1 Torr", "133.32 Pa")]
        [InlineData("14.7 psi", "101.3 kPa")]
        [InlineData("1 psi", "6895 Pa")]
        public void PracticeService_CheckFillInBlankMatch_VacuumAndFluidPressureEquivalences(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
            Assert.True(PracticeService.CheckFillInBlankMatch(user, expected));
        }

        [Theory]
        [InlineData("1 MB", "1024 KB")]
        [InlineData("1 GB", "1024 MB")]
        [InlineData("1 TB", "1024 GB")]
        [InlineData("1 Byte", "8 bit")]
        [InlineData("2 Byte", "16 bit")]
        [InlineData("1 KB", "1024 Byte")]
        public void PracticeService_CheckFillInBlankMatch_DataStorageCapacityEquivalences(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
            Assert.True(PracticeService.CheckFillInBlankMatch(user, expected));
        }

        #endregion

        #region 3. 数学排列组合多位参数解析与公式等价测试

        [Theory]
        [InlineData("\\binom{10}{3}", "120")]
        [InlineData("\\binom{12}{4}", "495")]
        [InlineData("C_{10}^{3}", "\\binom{10}{3}")]
        [InlineData("C(10, 3)", "120")]
        [InlineData("C_7^2", "C_7^5")]
        [InlineData("\\binom{5}{2}", "10")]
        public void PracticeService_CheckCombinatoricsEquivalence_MultiDigitBracesSupport(string u, string c)
        {
            Assert.True(PracticeService.CheckCombinatoricsEquivalence(u, c));
            Assert.True(PracticeService.CheckFillInBlankMatch(u, c));
        }

        [Theory]
        [InlineData("\\log_2 3", "\\frac{\\ln 3}{\\ln 2}")]
        [InlineData("\\log_a b", "\\frac{\\ln b}{\\ln a}")]
        [InlineData("\\log_a b", "\\frac{\\lg b}{\\lg a}")]
        [InlineData("\\log_{10} 5", "\\frac{\\ln 5}{\\ln 10}")]
        [InlineData("\\frac{\\ln 7}{\\ln 5}", "\\log_5 7")]
        public void PracticeService_CheckLogarithmBaseChangeEquivalence_Matches(string u, string c)
        {
            Assert.True(PracticeService.CheckLogarithmBaseChangeEquivalence(u, c));
            Assert.True(PracticeService.CheckFillInBlankMatch(u, c));
        }

        [Theory]
        [InlineData("[1, 2]", "x \\in [1, 2]")]
        [InlineData("(0, +inf)", "x in (0, +inf)")]
        [InlineData("[-1, 1]", "x属于[-1, 1]")]
        public void PracticeService_CheckFillInBlankMatch_IntervalMembershipStripping(string expected, string user)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(expected, user));
        }

        #endregion

        #region 4. UX 反馈与等价教学解析生成测试

        [Fact]
        public void PracticeService_GenerateEquivalentMatchReason_ProvidesPedagogicalExplanations()
        {
            var r1 = PracticeService.GenerateEquivalentMatchReason("1 Ci", "3.7e10 Bq");
            Assert.Contains("核物理与辐射剂量单位等价", r1);

            var r2 = PracticeService.GenerateEquivalentMatchReason("1 MB", "1024 KB");
            Assert.Contains("计算机数据容量与存储单位等价", r2);

            var r3 = PracticeService.GenerateEquivalentMatchReason("760 Torr", "1 atm");
            Assert.Contains("压强单位科学等价", r3);

            var r4 = PracticeService.GenerateEquivalentMatchReason("\\log_2 3", "\\frac{\\ln 3}{\\ln 2}");
            Assert.Contains("对数换底公式等价", r4);
        }

        [Fact]
        public async Task PracticeService_SubmitAnswer_PreservesUserAnswerInResult()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var teacherId = Guid.NewGuid();
                var studentId = Guid.NewGuid();
                context.Users.Add(new User { Id = teacherId, Username = "stem_teacher", Role = UserRole.Teacher });
                context.Users.Add(new User { Id = studentId, Username = "student_ux", Role = UserRole.Student });

                var q = new Question
                {
                    Id = Guid.NewGuid(),
                    CreatedByUserId = teacherId,
                    Stem = "测试：下列选项中哪个是质数？",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A. 4\", \"B. 6\", \"C. 7\", \"D. 8\"]",
                    CorrectAnswer = "C",
                    StandardAnalysis = "因为 7 只有 1 和 7 两个因数，所以是质数。",
                    Subject = "数学",
                    Category = "数论基础",
                    GradeTarget = "初一",
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved
                };
                context.Questions.Add(q);
                await context.SaveChangesAsync();

                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                // 提交正确作答并验证 UserAnswer 字段
                var result = await practiceService.SubmitAnswerAsync(q, "C", timeTakenSeconds: 5, currentCombo: 0, targetUserId: studentId);
                Assert.True(result.IsCorrect);
                Assert.Equal("C", result.UserAnswer);
                Assert.Equal("C", result.StandardAnswer);

                // 提交错误作答并验证 UserAnswer 字段
                var wrongResult = await practiceService.SubmitAnswerAsync(q, "A", timeTakenSeconds: 5, currentCombo: 0, targetUserId: studentId);
                Assert.False(wrongResult.IsCorrect);
                Assert.Equal("A", wrongResult.UserAnswer);
                Assert.Equal("C", wrongResult.StandardAnswer);
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
