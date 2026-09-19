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
    public class ArchitectAndUxAscendantEvolutionTests
    {
        [Fact]
        public void DataIntegrityHealAllResultDto_AggregationAndDefaults_ShouldBeAccurate()
        {
            var result = new DataIntegrityHealAllResultDto
            {
                PurgedOrphansCount = 5,
                DeduplicatedQuestionsCount = 3,
                HealedCorruptedQuestionsCount = 2,
                HealedBalancesCount = 4,
                HealedStudyPlansCount = 1,
                HealedHomeworkCount = 6,
                HealedErrorBooksCount = 7,
                HealedBindingsCount = 8,
                HealedFavoritesCount = 9,
                ElapsedMilliseconds = 125.4
            };

            int expectedTotal = 5 + 3 + 2 + 4 + 1 + 6 + 7 + 8 + 9;
            Assert.Equal(expectedTotal, result.TotalHealedCount);
            Assert.True(result.Success);
            Assert.Equal(125.4, result.ElapsedMilliseconds);
        }

        [Fact]
        public async Task SystemHealthService_HealCorruptedQuestions_RepairsChoiceOptionsAndPurgesUnservableQuestions()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var userId = Guid.NewGuid();
                var user = new User
                {
                    Id = userId,
                    Username = "healer_student",
                    Password = "hash",
                    Role = UserRole.Student,
                    Grade = "高一"
                };
                context.Users.Add(user);

                // 1. 选项 JSON 损坏的单选题 (应自愈补全标准选项数组)
                var corruptedChoiceQ = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "下列属于国际单位制基本单位的是？",
                    CorrectAnswer = "A",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "invalid-corrupted-json-not-array",
                    Subject = "物理",
                    GradeTarget = "高一",
                    IsPublic = true
                };

                // 2. 题干为空的不可用题 (应清理并级联删除关联的错题、练习记录与收藏)
                var emptyStemQ = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "   ",
                    CorrectAnswer = "B",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A. 选项A\", \"B. 选项B\"]",
                    Subject = "物理",
                    GradeTarget = "高一",
                    IsPublic = true
                };

                // 3. 正常试题
                var normalQ = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "正常的选择题",
                    CorrectAnswer = "C",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A. 1\", \"B. 2\", \"C. 3\", \"D. 4\"]",
                    Subject = "化学",
                    GradeTarget = "高一",
                    IsPublic = true
                };

                context.Questions.AddRange(corruptedChoiceQ, emptyStemQ, normalQ);

                // 关联数据
                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    QuestionId = emptyStemQ.Id,
                    UserWrongAnswer = "A",
                    IsMastered = false
                };
                var record = new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    QuestionId = emptyStemQ.Id,
                    IsCorrect = false,
                    UserAnswer = "A"
                };
                var favorite = new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    QuestionId = emptyStemQ.Id
                };
                var llmLog = new LlmGenerationLog
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    QuestionId = emptyStemQ.Id,
                    ModelName = "gpt-4o-mini",
                    Subject = "物理",
                    Category = "基础",
                    PromptTokens = 10,
                    CompletionTokens = 20,
                    TotalTokens = 30
                };

                context.ErrorItems.Add(errorItem);
                context.PracticeRecords.Add(record);
                context.UserFavorites.Add(favorite);
                context.LlmGenerationLogs.Add(llmLog);
                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 验证审计能检测到破损
                var auditBefore = await healthService.AuditDataIntegrityAsync();
                Assert.True(auditBefore.TotalCorruptedQuestions >= 2, "审计应检出损坏的选项与空白题干");

                // 执行试题自愈
                int healedCount = await healthService.HealCorruptedQuestionsAsync();
                Assert.True(healedCount >= 2, $"应自愈并清理至少 2 道试题，实际: {healedCount}");

                // 检查 corruptedChoiceQ 选项已自愈
                var healedChoice = await context.Questions.FindAsync(corruptedChoiceQ.Id);
                Assert.NotNull(healedChoice);
                var parsedOptions = System.Text.Json.JsonSerializer.Deserialize<string[]>(healedChoice!.OptionsJson);
                Assert.NotNull(parsedOptions);
                Assert.Contains("A. 选项A", parsedOptions!);
                Assert.Contains("B. 选项B", parsedOptions!);

                // 检查 emptyStemQ 已被安全移除
                Assert.Null(await context.Questions.FindAsync(emptyStemQ.Id));

                // 检查级联孤儿已彻底清理，且 LLM 日志的外键已置空脱敏
                Assert.False(await context.ErrorItems.AnyAsync(e => e.QuestionId == emptyStemQ.Id));
                Assert.False(await context.PracticeRecords.AnyAsync(r => r.QuestionId == emptyStemQ.Id));
                Assert.False(await context.UserFavorites.AnyAsync(f => f.QuestionId == emptyStemQ.Id));
                var refreshedLog = await context.LlmGenerationLogs.FindAsync(llmLog.Id);
                Assert.NotNull(refreshedLog);
                Assert.Null(refreshedLog!.QuestionId);
            }
            finally
            {
                connection.Close();
            }
        }

        [Fact]
        public async Task SystemHealthService_HealAllInvariants_OrchestratesDomainConvergenceAccurately()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var userId = Guid.NewGuid();
                var user = new User
                {
                    Id = userId,
                    Username = "convergence_user",
                    Password = "hash",
                    Role = UserRole.Student,
                    Grade = "高一",
                    Level = 1,
                    Exp = 5000 // 与 Level 1 严重脱节
                };
                context.Users.Add(user);

                var q = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试试题",
                    CorrectAnswer = "A",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A. 1\", \"B. 2\"]",
                    Subject = "物理",
                    GradeTarget = "高一",
                    IsPublic = true
                };
                context.Questions.Add(q);

                // 构造孤儿练习记录 (引用不存在的 UserId)
                var orphanRecord = new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = Guid.NewGuid(), // 孤儿
                    QuestionId = q.Id,
                    IsCorrect = true,
                    UserAnswer = "A"
                };
                context.PracticeRecords.Add(orphanRecord);

                // 构造重复收藏
                var fav1 = new UserFavorite { Id = Guid.NewGuid(), UserId = userId, QuestionId = q.Id, CreatedAt = DateTime.Now.AddHours(-2) };
                var fav2 = new UserFavorite { Id = Guid.NewGuid(), UserId = userId, QuestionId = q.Id, CreatedAt = DateTime.Now };
                context.UserFavorites.AddRange(fav1, fav2);

                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);

                // 一键全量自愈编排
                var healAll = await healthService.HealAllInvariantsAsync();

                Assert.True(healAll.Success);
                Assert.True(healAll.TotalHealedCount >= 2, $"全库编排自愈应收敛至少2项异常，实际: {healAll.TotalHealedCount}");
                Assert.True(healAll.PurgedOrphansCount >= 1, "应清理孤儿记录");
                Assert.True(healAll.HealedFavoritesCount >= 1, "应自愈重复收藏");
                Assert.True(healAll.HealedBalancesCount >= 1, "应校准游戏化资产与等级");
                Assert.True(healAll.OperationsExecuted.Count >= 2, "应记录执行的自愈管道");
            }
            finally
            {
                connection.Close();
            }
        }

        [Theory]
        [InlineData("5 kg·m/s", "5 N·s")]
        [InlineData("5 kg*m/s", "5 N*s")]
        [InlineData("5 kg m/s", "5 N·s")]
        [InlineData("5 kg·m·s^-1", "5 N·s")]
        [InlineData("10 千克·米/秒", "10 牛·秒")]
        [InlineData("10 千克米每秒", "10 牛秒")]
        [InlineData("12 kg·m/s", "12 N*s")]
        public void PracticeService_CheckFillInBlankMatch_MomentumAndImpulse_ShouldPass(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct), $"'{user}' 应与动量/冲量答案 '{correct}' 等价");
        }

        [Theory]
        [InlineData("100 J/s", "100 W")]
        [InlineData("100 J/s", "100 瓦")]
        [InlineData("100 焦/秒", "100 瓦特")]
        [InlineData("50 焦耳每秒", "50 瓦")]
        [InlineData("60 J*s^-1", "60 W")]
        public void PracticeService_CheckFillInBlankMatch_PowerAndWorkRate_ShouldPass(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct), $"'{user}' 应与功率/做功速率答案 '{correct}' 等价");
        }

        [Theory]
        [InlineData("500 V/m", "500 N/C")]
        [InlineData("500 伏/米", "500 牛/库")]
        [InlineData("200 伏特每米", "200 牛顿每库仑")]
        [InlineData("100 V*m^-1", "100 N*c^-1")]
        [InlineData("5 C/s", "5 A")]
        [InlineData("5 库/秒", "5 安")]
        [InlineData("8 库仑每秒", "8 安培")]
        public void PracticeService_CheckFillInBlankMatch_ElectromagneticUnits_ShouldPass(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct), $"'{user}' 应与电磁学单位 '{correct}' 等价");
        }

        [Theory]
        [InlineData("10 rad/s", "10 rad*s^-1")]
        [InlineData("10 弧度/秒", "10 rad/s")]
        [InlineData("5 弧度每秒", "5 rad/s")]
        [InlineData("3.5 kW·h", "3.5 度电")]
        [InlineData("3.5 kw*h", "3.5 千瓦时")]
        [InlineData("10 kWh", "10 度电")]
        public void PracticeService_CheckFillInBlankMatch_AngularVelocityAndKwh_ShouldPass(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct), $"'{user}' 应与角速度/电能单位 '{correct}' 等价");
        }

        [Theory]
        [InlineData("\\angle A", "∠A")]
        [InlineData("角A", "∠A")]
        [InlineData("角1", "∠1")]
        [InlineData("\\parallel", "//")]
        [InlineData("平行", "//")]
        [InlineData("平行于", "//")]
        [InlineData("\\perp", "⊥")]
        [InlineData("垂直", "⊥")]
        [InlineData("垂直于", "⊥")]
        [InlineData("\\subseteq", "⊆")]
        [InlineData("包含于", "⊆")]
        [InlineData("\\subsetneqq", "⫋")]
        [InlineData("真包含于", "⫋")]
        [InlineData("\\iff", "充要条件")]
        [InlineData("当且仅当", "充要条件")]
        [InlineData("<=>", "充要条件")]
        public void PracticeService_CheckFillInBlankMatch_GeometryAndLogic_ShouldPass(string user, string correct)
        {
            Assert.True(PracticeService.CheckFillInBlankMatch(user, correct), $"'{user}' 应与几何/逻辑符号 '{correct}' 等价");
        }
    }
}
