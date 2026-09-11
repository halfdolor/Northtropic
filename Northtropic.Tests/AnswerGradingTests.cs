using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class AnswerGradingTests
    {
        [Theory]
        [InlineData("2/3", "2/3")]
        [InlineData("2 / 3", "2/3")]
        [InlineData("2/3", "2 / 3")]
        [InlineData("2/3", "2/3 或 0.67")]
        [InlineData("0.67", "2/3 或 0.67")]
        [InlineData("1/2", "0.5")]
        [InlineData("0.5", "1/2")]
        [InlineData("0.50", "0.5")]
        [InlineData("\\frac{2}{3}", "2/3")]
        [InlineData("$2/3$", "2/3")]
        public void CheckFillInBlank_FractionsAndEquivalence_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"User input '{user}' should match correct answer '{correct}'");
        }

        [Theory]
        [InlineData("男", "男/女")]
        [InlineData("女", "男/女")]
        [InlineData("变大", "变大 / 增大")]
        [InlineData("增大", "变大 / 增大")]
        [InlineData("6", "6; 8")]
        [InlineData("8", "6; 8")]
        public void CheckFillInBlank_MultipleAlternativeAnswers_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"User input '{user}' should match correct alternative in '{correct}'");
        }

        [Theory]
        [InlineData("6", "x=6")]
        [InlineData("x=6", "6")]
        [InlineData("x = 6", "x=6")]
        [InlineData("y=3", "y=3")]
        public void CheckFillInBlank_AlgebraicEquations_ShouldPass(string user, string correct)
        {
            bool isMatch = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(isMatch, $"User input '{user}' should match algebraic answer '{correct}'");
        }

        [Fact]
        public void SingleChoice_CaseAndPrefixTolerance_ShouldMatch()
        {
            var q = new Question
            {
                Type = QuestionType.SingleChoice,
                CorrectAnswer = "A"
            };

            Assert.True(PracticeService.CheckAnswerCorrectness(q, "A"));
            Assert.True(PracticeService.CheckAnswerCorrectness(q, "a"));
            Assert.True(PracticeService.CheckAnswerCorrectness(q, "A. 苹果"));
            Assert.False(PracticeService.CheckAnswerCorrectness(q, "B"));
        }

        [Fact]
        public void MultipleChoice_OrderAndDelimiterTolerance_ShouldMatch()
        {
            var q = new Question
            {
                Type = QuestionType.MultipleChoice,
                CorrectAnswer = "A, B, D"
            };

            // 任意排列与中英文标点容错
            Assert.True(PracticeService.CheckAnswerCorrectness(q, "A, B, D"));
            Assert.True(PracticeService.CheckAnswerCorrectness(q, "D, B, A"));
            Assert.True(PracticeService.CheckAnswerCorrectness(q, "A、B、D"));
            Assert.True(PracticeService.CheckAnswerCorrectness(q, "abd"));
            Assert.False(PracticeService.CheckAnswerCorrectness(q, "A, B"));
            Assert.False(PracticeService.CheckAnswerCorrectness(q, "A, B, C, D"));
        }
    }
}
