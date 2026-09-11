using System.Text.Json;
using Northtropic.Helpers;
using Northtropic.Models;
using Xunit;

namespace Northtropic.Tests
{
    public class QuestionShuffleHelperTests
    {
        [Fact]
        public void NonChoiceQuestion_ShouldNotBeShuffled()
        {
            var q = new Question
            {
                Type = QuestionType.FillInBlank,
                Stem = "测试填空题",
                CorrectAnswer = "42",
                OptionsJson = "[]"
            };

            var result = QuestionShuffleHelper.ShuffleQuestionOptions(q);

            Assert.Equal(q.Stem, result.Stem);
            Assert.Equal("42", result.CorrectAnswer);
        }

        [Fact]
        public void SingleChoice_WhenAnswerContainsLetters_ShouldNotCauseMultipleCorrectAnswers()
        {
            // 验证核心 Bug：当选项纯文本或答案中包含 'B'（如 Blazor），旧代码的 ans.Contains("B") 会错误把选项 B 也标记为正确
            var original = new Question
            {
                Type = QuestionType.SingleChoice,
                Stem = "在 ASP.NET Core 8.0 中，哪个托管模型支持服务端预渲染且首屏轻量？",
                OptionsJson = JsonSerializer.Serialize(new[]
                {
                    "A. Blazor Server",
                    "B. Blazor WebAssembly",
                    "C. Classic MVC",
                    "D. WebForms"
                }),
                CorrectAnswer = "A"
            };

            // 执行多次洗牌验证随机置换下仍保持有且仅有 1 个正确答案
            for (int i = 0; i < 20; i++)
            {
                var shuffled = QuestionShuffleHelper.ShuffleQuestionOptions(original);

                // 拆分洗牌后的正确答案
                var correctTokens = shuffled.CorrectAnswer.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                Assert.Single(correctTokens);

                // 获取选中的选项文本，应该对应 "Blazor Server"
                var shuffledOptions = JsonSerializer.Deserialize<List<string>>(shuffled.OptionsJson);
                Assert.NotNull(shuffledOptions);
                Assert.Equal(4, shuffledOptions.Count);

                string selectedPrefix = correctTokens[0];
                var correctOption = shuffledOptions.First(opt => opt.StartsWith(selectedPrefix + "."));
                Assert.Contains("Blazor Server", correctOption);
                Assert.DoesNotContain("Blazor WebAssembly", correctOption);
            }
        }

        [Fact]
        public void MultipleChoice_ShouldCorrectlyRemapAllCorrectAnswers()
        {
            var original = new Question
            {
                Type = QuestionType.MultipleChoice,
                Stem = "以下属于初中物理电学国际单位的有？",
                OptionsJson = JsonSerializer.Serialize(new[]
                {
                    "A. 安培 (A)",
                    "B. 伏特 (V)",
                    "C. 欧姆 (Ω)",
                    "D. 千米每小时 (km/h)"
                }),
                CorrectAnswer = "A, B, C"
            };

            var shuffled = QuestionShuffleHelper.ShuffleQuestionOptions(original);

            var correctTokens = shuffled.CorrectAnswer.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, correctTokens.Length);

            var shuffledOptions = JsonSerializer.Deserialize<List<string>>(shuffled.OptionsJson);
            Assert.NotNull(shuffledOptions);

            // 验证重映射后的前缀对应的选项均不是 D (千米每小时)
            foreach (var token in correctTokens)
            {
                var opt = shuffledOptions.First(o => o.StartsWith(token + "."));
                Assert.DoesNotContain("千米每小时", opt);
            }
        }

        [Fact]
        public void OptionsWithChinesePrefix_ShouldBeCleanedAndReindexed()
        {
            var original = new Question
            {
                Type = QuestionType.SingleChoice,
                Stem = "中国首都是？",
                OptionsJson = JsonSerializer.Serialize(new[]
                {
                    "A、北京",
                    "B、上海",
                    "C、广州",
                    "D、深圳"
                }),
                CorrectAnswer = "A"
            };

            var shuffled = QuestionShuffleHelper.ShuffleQuestionOptions(original);
            var shuffledOptions = JsonSerializer.Deserialize<List<string>>(shuffled.OptionsJson);
            Assert.NotNull(shuffledOptions);

            string correctPrefix = shuffled.CorrectAnswer.Trim();
            var correctOpt = shuffledOptions.First(o => o.StartsWith(correctPrefix + "."));
            Assert.Contains("北京", correctOpt);
        }
    }
}
