using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class GamificationLogicTests
    {
        [Theory]
        [InlineData(0, 1.0)]
        [InlineData(1, 1.0)]
        [InlineData(2, 1.2)]
        [InlineData(3, 1.3)]
        [InlineData(4, 1.5)]
        [InlineData(5, 1.8)]
        [InlineData(6, 2.0)]
        [InlineData(7, 2.2)]
        [InlineData(8, 2.5)]
        [InlineData(9, 2.8)]
        [InlineData(10, 3.0)]
        [InlineData(50, 3.0)]
        public void CalculateComboMultiplier_ShouldMatchExpectedTiers(int combo, double expectedMultiplier)
        {
            double mult = GamificationService.CalculateComboMultiplier(combo);
            Assert.Equal(expectedMultiplier, mult, precision: 2);
        }

        [Theory]
        [InlineData(1, 100)]
        [InlineData(2, 200)]
        [InlineData(5, 500)]
        [InlineData(10, 1000)]
        public void CalculateExpNeeded_ShouldScaleLinearlyWithLevel(int level, int expectedExp)
        {
            int exp = GamificationService.CalculateExpNeeded(level);
            Assert.Equal(expectedExp, exp);
        }
    }
}
