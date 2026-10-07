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
    public class ImmersionAndAdaptiveDifficultyTests
    {
        [Fact]
        public void AppModeService_ToggleMode_SwitchesBetweenBeginnerAndProfessional()
        {
            var service = new AppModeService();
            Assert.False(service.IsBeginnerMode);

            service.ToggleMode();
            Assert.True(service.IsBeginnerMode);

            service.ToggleMode();
            Assert.False(service.IsBeginnerMode);
        }

        [Fact]
        public void AppModeService_T_ReturnsCorrespondingTextBasedOnMode()
        {
            var service = new AppModeService();
            // 默认专业模式
            Assert.Equal("动态梯度", service.T("动态梯度", "智能循序渐进"));

            // 切换为入门模式
            service.ToggleMode();
            Assert.Equal("智能循序渐进", service.T("动态梯度", "智能循序渐进"));
        }

        [Fact]
        public void AppModeService_Format_ReplacesTechnicalTermsInBeginnerMode()
        {
            var service = new AppModeService();
            string proText = "🎯 智能自适应推题 (动态梯度)，依据艾宾浩斯抗遗忘记忆曲线与苏格拉底式启发引导，突破最近发展区！";

            // 专业模式下保持原样
            Assert.Equal(proText, service.Format(proText));

            // 入门模式下自动通俗化
            service.ToggleMode();
            string beginnerFormatted = service.Format(proText);

            Assert.DoesNotContain("动态梯度", beginnerFormatted);
            Assert.DoesNotContain("艾宾浩斯", beginnerFormatted);
            Assert.DoesNotContain("苏格拉底", beginnerFormatted);
            Assert.DoesNotContain("最近发展区", beginnerFormatted);

            Assert.Contains("循序渐进", beginnerFormatted);
            Assert.Contains("科学记忆复习曲线", beginnerFormatted);
            Assert.Contains("循循善诱启发思考", beginnerFormatted);
            Assert.Contains("能力进阶区", beginnerFormatted);
        }

        [Fact]
        public async Task PracticeService_GetAdaptiveQuestionsAsync_NeverReturnsChineseQuestionsWhenChemistrySelected()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            {
                var user = new User { Id = Guid.NewGuid(), Username = "test_chem_user", Grade = "九年级" };
                context.Users.Add(user);

                // 添加 5 道语文试题
                for (int i = 1; i <= 5; i++)
                {
                    context.Questions.Add(new Question
                    {
                        Id = Guid.NewGuid(),
                        Subject = "语文",
                        Category = "文言文阅读",
                        GradeTarget = "九年级",
                        Difficulty = 1,
                        IsPublic = true,
                        Stem = $"语文试题题干 {i}",
                        Type = QuestionType.SingleChoice,
                        OptionsJson = "[\"A. 选项\", \"B. 选项\"]",
                        CorrectAnswer = "A",
                        StandardAnalysis = "语文解析"
                    });
                }

                // 添加仅 2 道化学试题
                for (int i = 1; i <= 2; i++)
                {
                    context.Questions.Add(new Question
                    {
                        Id = Guid.NewGuid(),
                        Subject = "化学",
                        Category = "溶液与溶解度",
                        GradeTarget = "九年级",
                        Difficulty = 2,
                        IsPublic = true,
                        Stem = $"化学试题题干 {i}",
                        Type = QuestionType.SingleChoice,
                        OptionsJson = "[\"A. 选项\", \"B. 选项\"]",
                        CorrectAnswer = "A",
                        StandardAnalysis = "化学解析"
                    });
                }

                await context.SaveChangesAsync();

                var practiceService = new PracticeService(
                    context,
                    new FakeGamificationService { CurrentUser = user },
                    new FakeUserSessionService { ActiveUser = user },
                    new FakeAiTutorService()
                );

                // 请求化学试题 5 题 (库中只有 2 题)
                var result = await practiceService.GetAdaptiveQuestionsAsync(user.Id, "九年级", "化学", "全部", 5);

                // 核心断言：绝对不能混入语文试题！所有的试题学科必须全部为“化学”
                Assert.NotEmpty(result);
                Assert.All(result, q => Assert.Equal("化学", q.Subject));
                Assert.DoesNotContain(result, q => q.Subject == "语文");
            }
        }

        [Fact]
        public async Task PracticeService_GetAdaptiveQuestionsAsync_FiltersByDifficultyAccurately()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            {
                var user = new User { Id = Guid.NewGuid(), Username = "diff_user", Grade = "初三" };
                context.Users.Add(user);

                // 添加不同难度的化学试题
                context.Questions.Add(new Question { Id = Guid.NewGuid(), Subject = "化学", Category = "酸碱盐", GradeTarget = "初三", Difficulty = 1, IsPublic = true, Stem = "题1", Type = QuestionType.SingleChoice, OptionsJson = "[\"A\", \"B\"]", CorrectAnswer = "A" });
                context.Questions.Add(new Question { Id = Guid.NewGuid(), Subject = "化学", Category = "酸碱盐", GradeTarget = "初三", Difficulty = 2, IsPublic = true, Stem = "题2", Type = QuestionType.SingleChoice, OptionsJson = "[\"A\", \"B\"]", CorrectAnswer = "A" });
                context.Questions.Add(new Question { Id = Guid.NewGuid(), Subject = "化学", Category = "酸碱盐", GradeTarget = "初三", Difficulty = 2, IsPublic = true, Stem = "题3", Type = QuestionType.SingleChoice, OptionsJson = "[\"A\", \"B\"]", CorrectAnswer = "A" });
                context.Questions.Add(new Question { Id = Guid.NewGuid(), Subject = "化学", Category = "酸碱盐", GradeTarget = "初三", Difficulty = 4, IsPublic = true, Stem = "题4", Type = QuestionType.SingleChoice, OptionsJson = "[\"A\", \"B\"]", CorrectAnswer = "A" });

                await context.SaveChangesAsync();

                var practiceService = new PracticeService(
                    context,
                    new FakeGamificationService { CurrentUser = user },
                    new FakeUserSessionService { ActiveUser = user },
                    new FakeAiTutorService()
                );

                // 指定难度 2
                var result = await practiceService.GetAdaptiveQuestionsAsync(user.Id, "初三", "化学", "全部", 5, difficulty: 2);

                Assert.Equal(2, result.Count);
                Assert.All(result, q => Assert.Equal(2, q.Difficulty));
            }
        }

        [Fact]
        public async Task GamificationService_LowDifficulty_DiminishesRewardAndZerosAfter10Minutes()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            {
                var user = new User { Id = Guid.NewGuid(), Username = "diminish_user", Exp = 0, Coins = 100, Level = 1 };
                context.Users.Add(user);
                await context.SaveChangesAsync();

                var userSession = new FakeUserSessionService { ActiveUser = user };
                var gamification = new GamificationService(context, userSession);

                // 重置低难度会话
                GamificationService.ResetLowDifficultySession(user.Id);

                // 第 1 次作答难度 1 的试题 (baseExp = 20)
                var reward1 = await gamification.ProcessAnswerRewardAsync(isCorrect: true, baseExp: 20, combo: 0, difficulty: 1, targetUserId: user.Id);
                Assert.False(reward1.IsLowDifficultyDiminished); // 第一次不递减 (100%)
                Assert.Equal(1.0, reward1.LowDifficultyMultiplier, precision: 2);
                Assert.Equal(20, reward1.EarnedExp);

                // 第 2 次作答难度 1 (multiplier 应为 0.8)
                var reward2 = await gamification.ProcessAnswerRewardAsync(isCorrect: true, baseExp: 20, combo: 0, difficulty: 1, targetUserId: user.Id);
                Assert.True(reward2.IsLowDifficultyDiminished);
                Assert.Equal(0.8, reward2.LowDifficultyMultiplier, precision: 2);
                Assert.Equal(16, reward2.EarnedExp);

                // 模拟作答连续低难度超过 10 分钟 (设为 11 分钟前开始)
                GamificationService.SetLowDifficultySessionForTesting(user.Id, DateTime.Now.AddMinutes(-11), 3);

                var rewardTimeout = await gamification.ProcessAnswerRewardAsync(isCorrect: true, baseExp: 20, combo: 0, difficulty: 1, targetUserId: user.Id);
                Assert.True(rewardTimeout.IsZeroRewardDueToLowDifficultyThreshold);
                Assert.Equal(0, rewardTimeout.EarnedExp);
                Assert.Equal(0, rewardTimeout.EarnedCoins);

                // 当用户作答难度 >= 3 的题目时，自动重置低难度惩罚，获得全额收益
                var rewardMedium = await gamification.ProcessAnswerRewardAsync(isCorrect: true, baseExp: 30, combo: 0, difficulty: 3, targetUserId: user.Id);
                Assert.False(rewardMedium.IsZeroRewardDueToLowDifficultyThreshold);
                Assert.False(rewardMedium.IsLowDifficultyDiminished);
                Assert.Equal(30, rewardMedium.EarnedExp);
                Assert.True(rewardMedium.EarnedCoins > 0);
            }
        }
    }
}
