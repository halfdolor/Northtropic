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
    public class ArchitectAndUxMasterpieceTests
    {
        [Theory]
        [InlineData("CO2↑", "CO2")]
        [InlineData("CO_2\\uparrow", "CO2")]
        [InlineData("BaSO4↓", "BaSO4")]
        [InlineData("BaSO_4\\downarrow", "baso4")]
        [InlineData("CaCO3↓", "caco3")]
        [InlineData("Ca(OH)2", "ca(oh)_2")]
        [InlineData("Fe^{3+}", "Fe3+")]
        [InlineData("N2 + 3H2 \\rightleftharpoons 2NH3", "n2+3h2<=>2nh3")]
        [InlineData("2SO2 + O2 <==> 2SO3", "2so2+o2<=>2so3")]
        public void CheckFillInBlankMatch_ChemicalPrecipitateGasAndArrows(string user, string correct)
        {
            bool matched = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(matched, $"Failed chemical equivalence between '{user}' and '{correct}'");
        }

        [Theory]
        [InlineData("对", "正确", true)]
        [InlineData("√", "对", true)]
        [InlineData("true", "正确", true)]
        [InlineData("T", "对", true)]
        [InlineData("yes", "对", true)]
        [InlineData("错", "错误", true)]
        [InlineData("×", "错误", true)]
        [InlineData("false", "错", true)]
        [InlineData("F", "错误", true)]
        [InlineData("no", "错", true)]
        [InlineData("对", "错", false)]
        [InlineData("正确", "错误", false)]
        [InlineData("√", "×", false)]
        public void CheckFillInBlankMatch_JudgementEquivalence(string user, string correct, bool expected)
        {
            bool matched = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, matched);
        }

        [Theory]
        [InlineData("2\\sqrt{3}", "2*sqrt(3)")]
        [InlineData("2sqrt(3)", "2*sqrt(3)")]
        [InlineData("2\\pi", "2*pi")]
        [InlineData("2π", "2*pi")]
        [InlineData("3\\times 10^8", "3*10^8")]
        [InlineData("3.0*10^8", "3e8")]
        public void CheckFillInBlankMatch_ImplicitMultiplicationRadicalsAndPi(string user, string correct)
        {
            bool matched = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(matched, $"Failed math radical/pi equivalence between '{user}' and '{correct}'");
        }

        [Theory]
        [InlineData("1. 动能 2. 势能", "动能; 势能")]
        [InlineData("一、牛顿第一定律 二、惯性定律", "牛顿第一定律; 惯性定律")]
        [InlineData("(1) 熔化 (2) 凝固", "熔化; 凝固")]
        [InlineData("① 3 ② 5", "3; 5")]
        public void CheckFillInBlankMatch_OrderedNumberedBlanks(string user, string correct)
        {
            bool matched = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(matched, $"Failed numbered blanks matching between '{user}' and '{correct}'");
        }

        [Fact]
        public void ErrorBookService_GetRecommendedReviewInterval_NegativeAndZeroBoundary()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var fakeSession = new FakeUserSessionService();
                var fakeGamification = new GamificationService(context, fakeSession);
                var service = new ErrorBookService(context, fakeGamification, fakeSession);

                // 负数与 0 次复习必须严格返回初始阶段的 12 小时，防止回退缺陷
                Assert.Equal(TimeSpan.FromHours(12), service.GetRecommendedReviewInterval(-1));
                Assert.Equal(TimeSpan.FromHours(12), service.GetRecommendedReviewInterval(-5));
                Assert.Equal(TimeSpan.FromHours(12), service.GetRecommendedReviewInterval(0));

                // 正常梯次
                Assert.Equal(TimeSpan.FromHours(36), service.GetRecommendedReviewInterval(1));
                Assert.Equal(TimeSpan.FromHours(84), service.GetRecommendedReviewInterval(2));
                Assert.Equal(TimeSpan.FromHours(156), service.GetRecommendedReviewInterval(3));
                Assert.Equal(TimeSpan.FromHours(336), service.GetRecommendedReviewInterval(4));
            }
        }

        [Fact]
        public void ErrorBookService_RetentionHealthScore_ClampingAndClockSkewDefense()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var fakeSession = new FakeUserSessionService();
                var fakeGamification = new GamificationService(context, fakeSession);
                var service = new ErrorBookService(context, fakeGamification, fakeSession);

                var item = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = Guid.NewGuid(),
                    QuestionId = Guid.NewGuid(),
                    RevisionCount = 0,
                    CreatedAt = DateTime.Now,
                    LastRevisedAt = null,
                    IsMastered = false
                };

                // 1. 刚刚创建，健康度应接近 100%
                var scoreJustNow = service.CalculateRetentionHealthScore(item, asOf: DateTime.Now);
                Assert.InRange(scoreJustNow, 95.0, 100.0);

                // 2. 客户端或服务器时钟漂移（如 asOf 早于 CreatedAt）
                var scoreClockSkew = service.CalculateRetentionHealthScore(item, asOf: DateTime.Now.AddHours(-2));
                Assert.Equal(100.0, scoreClockSkew);

                // 3. 逾期很久（如 1000 小时后）
                var scoreOverdue = service.CalculateRetentionHealthScore(item, asOf: DateTime.Now.AddHours(1000));
                Assert.InRange(scoreOverdue, 0.0, 10.0);

                // 4. 已融会贯通 (IsMastered = true)
                item.IsMastered = true;
                var scoreMastered = service.CalculateRetentionHealthScore(item);
                Assert.Equal(100.0, scoreMastered);
            }
        }

        [Fact]
        public async Task GamificationService_UpdateStreak_SameDayActivityTimestampUpdated()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var initialStudyTime = DateTime.Today.AddSeconds(1); // 当天凌晨
                var student = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "全勤学霸",
                    Role = UserRole.Student,
                    CurrentStreak = 5,
                    LastStudyDate = initialStudyTime
                };
                context.Users.Add(student);
                await context.SaveChangesAsync();

                var sessionMock = new FakeUserSessionService { ActiveUser = student };
                var gamificationService = new GamificationService(context, sessionMock);

                // 当天下午再次完成一次练习打卡
                await gamificationService.UpdateStreakAsync();

                var userInDb = await context.Users.FindAsync(student.Id);
                Assert.NotNull(userInDb);
                Assert.Equal(5, userInDb.CurrentStreak); // 连胜天数保持 5 天（当天不重复加天数）
                Assert.True(userInDb.LastStudyDate > initialStudyTime, "同日二次学习时未刷新最后学习时间戳！");
            }
        }
    }
}
