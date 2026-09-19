using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxSupremeEvolutionTests
    {
        public class SqliteDbContextFactory : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;
            public SqliteDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
            public AppDbContext CreateDbContext() => new AppDbContext(_options);
        }

        private static (SqliteConnection Connection, DbContextOptions<AppDbContext> Options) CreateSharedInMemoryDb()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            using (var ctx = new AppDbContext(options))
            {
                ctx.Database.EnsureCreated();
            }

            return (connection, options);
        }

        #region 1. 系统架构师：化学热化学方程式与焓变 ΔH 智能等价评测

        [Theory]
        [InlineData("C(s) + O2(g) = CO2(g)  ΔH = -393.5 kJ/mol", "C(s) + O2(g) = CO2(g)  \\Delta H = -393.5 kJ·mol^-1", true)]
        [InlineData("O2(g) + C(s) = CO2(g) ; ΔH = -393.5kJ/mol", "C(s) + O2(g) = CO2(g)  \\Delta H = -393.5 kJ/mol", true)]
        [InlineData("C(s) + H2O(g) = CO(g) + H2(g)  ΔH = +131.5 kJ/mol", "C(s) + H2O(g) = CO(g) + H2(g)  \\Delta H = 131.5 kJ/mol", true)]
        [InlineData("2H2(g) + O2(g) = 2H2O(l)  ΔH = -571.6 kJ/mol", "2H2(g) + O2(g) = 2H2O(l)  ΔH = -571600 J/mol", true)]
        [InlineData("N2(g) + 3H2(g) = 2NH3(g)  ΔH = -92.4 kJ/mol", "3H2(g) + N2(g) = 2NH3(g)  \\Delta H = -92.4 kJ*mol^-1", true)]
        [InlineData("C(s) + O2(g) = CO2(g)  ΔH = -393.5 kJ/mol", "C(s) + O2(g) = CO2(g)  ΔH = -285.8 kJ/mol", false)] // 焓变数值不一致
        [InlineData("C(s) + O2(g) = CO2(g)  ΔH = -393.5 kJ/mol", "2C(s) + O2(g) = 2CO(g)  ΔH = -393.5 kJ/mol", false)] // 反应式不匹配
        public void CheckThermochemicalEquationMatch_EvaluatesCorrectly(string user, string correct, bool expected)
        {
            bool actual = PracticeService.CheckThermochemicalEquationMatch(user, correct);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void CheckAnswerCorrectness_ThermochemicalEquation_ReturnsPedagogicalFeedback()
        {
            var q = new Question
            {
                Id = Guid.NewGuid(),
                Subject = "化学",
                Type = QuestionType.FillInBlank,
                CorrectAnswer = "C(s) + O2(g) = CO2(g)  \\Delta H = -393.5 kJ·mol^-1",
                Stem = "写出碳完全燃烧的热化学方程式"
            };

            string userAnswer = "O2(g) + C(s) = CO2(g)  ΔH = -393.5 kJ/mol";
            bool isCorrect = PracticeService.CheckAnswerCorrectness(q, userAnswer);
            Assert.True(isCorrect);

            string feedback = PracticeService.GenerateEquivalentMatchReason(q, userAnswer);
            Assert.Contains("热化学方程式", feedback);
        }

        #endregion

        #region 2. 系统架构师：物理量纲与科学单位扩展换算测试

        [Theory]
        // 电流 (A, mA, μA, kA)
        [InlineData("0.5 A", "500 mA", true)]
        [InlineData("2 mA", "2000 μA", true)]
        [InlineData("1 kA", "1000 A", true)]
        [InlineData("0.003 安培", "3 毫安", true)]
        // 电能实用单位 (kWh, 度, J)
        [InlineData("1 度", "1 kWh", true)]
        [InlineData("2 度", "7.2*10^6 J", true)]
        [InlineData("1 kW·h", "3600000 J", true)]
        [InlineData("0.5 千瓦时", "0.5 度", true)]
        // 时间 (h, min, s, ms)
        [InlineData("1.5 h", "90 min", true)]
        [InlineData("2 min", "120 s", true)]
        [InlineData("0.5 s", "500 ms", true)]
        [InlineData("3 小时", "180 分钟", true)]
        // 电容 (F, μF, nF, pF)
        [InlineData("100 μF", "100000 nF", true)]
        [InlineData("1 nF", "1000 pF", true)]
        // 磁感应强度 (T, mT)
        [InlineData("0.5 特斯拉", "500 mT", true)]
        [InlineData("2 毫特", "0.002 特", true)]
        // 跨量纲防碰撞
        [InlineData("500 mA", "500 mV", false)]
        [InlineData("1 h", "1 kg", false)]
        public void CheckScientificUnitMultiplierEquivalence_NewUnits_EvaluatesCorrectly(string u, string c, bool expected)
        {
            bool actual = PracticeService.CheckScientificUnitMultiplierEquivalence(u, c);
            Assert.Equal(expected, actual);
        }

        #endregion

        #region 3. 用户体验专家：解析几何直线截距式方程等价测试

        [Theory]
        [InlineData("x/2 + y/3 = 1", "y/3 + x/2 = 1", true)]
        [InlineData("\\frac{x}{2} + \\frac{y}{3} = 1", "3x + 2y - 6 = 0", true)]
        [InlineData("x/4 + y/(-5) = 1", "-5x + 4y + 20 = 0", true)]
        [InlineData("x/2 + y/3 = 1", "2x + 3y = 1", false)] // 系数不同
        public void LinearEquation_InterceptForm_EvaluatesEquivalent(string user, string correct, bool expected)
        {
            bool actual = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, actual);
        }

        #endregion

        #region 4. 用户体验专家：错题本多维检索过滤测试

        [Fact]
        public void ErrorBook_MultiDimensionalSearch_FiltersExpectedItems()
        {
            var qMath = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "已知双曲线焦点在x轴上，求渐近线方程",
                Subject = "数学",
                Category = "解析几何",
                GradeTarget = "高二",
                Type = QuestionType.FillInBlank
            };

            var qPhysics = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "求通过定值电阻的感应电流大小",
                Subject = "物理",
                Category = "电磁感应",
                GradeTarget = "高三",
                Type = QuestionType.SingleChoice
            };

            var item1 = new ErrorItem
            {
                Id = Guid.NewGuid(),
                Question = qMath,
                QuestionId = qMath.Id,
                ErrorReasonCategory = "公式记错",
                AiCustomAdvice = "注意区分双曲线与椭圆的焦点坐标关系"
            };

            var item2 = new ErrorItem
            {
                Id = Guid.NewGuid(),
                Question = qPhysics,
                QuestionId = qPhysics.Id,
                ErrorReasonCategory = "粗心大意",
                AiCustomAdvice = "法拉第电磁感应定律在闭合回路中的电流推导"
            };

            var list = new List<ErrorItem> { item1, item2 };

            // 1. 按学科搜索
            string searchSubject = "物理";
            var resultSubject = list.Where(i => i.Question != null && (
                i.Question.Stem.Contains(searchSubject, StringComparison.OrdinalIgnoreCase) ||
                i.Question.Category.Contains(searchSubject, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(i.Question.Subject) && i.Question.Subject.Contains(searchSubject, StringComparison.OrdinalIgnoreCase))
            )).ToList();
            Assert.Single(resultSubject);
            Assert.Equal(item2.Id, resultSubject[0].Id);

            // 2. 按年级搜索
            string searchGrade = "高二";
            var resultGrade = list.Where(i => i.Question != null && (
                (!string.IsNullOrEmpty(i.Question.GradeTarget) && i.Question.GradeTarget.Contains(searchGrade, StringComparison.OrdinalIgnoreCase))
            )).ToList();
            Assert.Single(resultGrade);
            Assert.Equal(item1.Id, resultGrade[0].Id);

            // 3. 按题型搜索
            string searchType = "单选题";
            var resultType = list.Where(i => i.Question != null && (
                (!string.IsNullOrEmpty(i.Question.TypeDisplayName) && i.Question.TypeDisplayName.Contains(searchType, StringComparison.OrdinalIgnoreCase))
            )).ToList();
            Assert.Single(resultType);
            Assert.Equal(item2.Id, resultType[0].Id);

            // 4. 按错因归因搜索
            string searchReason = "公式记错";
            var resultReason = list.Where(i =>
                (!string.IsNullOrEmpty(i.ErrorReasonCategory) && i.ErrorReasonCategory.Contains(searchReason, StringComparison.OrdinalIgnoreCase))
            ).ToList();
            Assert.Single(resultReason);
            Assert.Equal(item1.Id, resultReason[0].Id);

            // 5. 按短 ID 搜索
            string shortId = item1.Id.ToString().Substring(0, 8);
            var resultId = list.Where(i =>
                i.Id.ToString().Contains(shortId, StringComparison.OrdinalIgnoreCase)
            ).ToList();
            Assert.Single(resultId);
            Assert.Equal(item1.Id, resultId[0].Id);
        }

        #endregion

        #region 5. 系统架构师：单次消费 Ticket 安全验证机制

        [Fact]
        public async Task UserSessionService_DownloadTicket_IsOneTimeUse()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                using var context = factory.CreateDbContext();
                var sessionService = new UserSessionService(context, new System.Net.Http.HttpClient());

                var testUserId = Guid.NewGuid();
                string purpose = "question_export";

                // 生成单次下载凭证 Ticket
                string ticket = await sessionService.GenerateDownloadTicketAsync(testUserId, purpose);
                Assert.False(string.IsNullOrWhiteSpace(ticket));

                // 首次消费验证应当成功
                var (valid1, uid1, purp1, _) = await sessionService.ValidateAndConsumeDownloadTicketAsync(ticket);
                Assert.True(valid1);
                Assert.Equal(testUserId, uid1);
                Assert.Equal(purpose, purp1);

                // 二次消费验证（防重放与爬虫）必须失败
                var (valid2, uid2, purp2, _) = await sessionService.ValidateAndConsumeDownloadTicketAsync(ticket);
                Assert.False(valid2);
            }
        }

        #endregion
    }
}
