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
    public class ArchitectAndUxZenithSovereignTranscendenceTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;
        private readonly SimpleTestDbContextFactory _factory;
        private readonly SystemHealthService _healthService;

        public ArchitectAndUxZenithSovereignTranscendenceTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var builder = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection);

            _context = new AppDbContext(builder.Options);
            _context.Database.EnsureCreated();

            _factory = new SimpleTestDbContextFactory(builder.Options);
            _healthService = new SystemHealthService(_context, _factory);
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Theory]
        [InlineData("U = Ed", "E = U/d")]
        [InlineData("E = \\frac{U}{d}", "d = \\frac{U}{E}")]
        [InlineData("u=de", "ed=u")]
        [InlineData("d = U / E", "U = Ed")]
        public void PhysicsFormulaEquivalence_ElectricFieldAndPotential_MatchesBidirectional(string studentAnswer, string standardAnswer)
        {
            // Act
            bool isEquiv = PracticeService.CheckPhysicsFormulaEquivalence(studentAnswer, standardAnswer);
            string reason = PracticeService.GenerateEquivalentMatchReason(studentAnswer, standardAnswer);

            // Assert
            Assert.True(isEquiv);
            Assert.Contains("电场电势差与场强关系等价", reason);
        }

        [Theory]
        [InlineData("F = qE", "E = F/q")]
        [InlineData("F = Eq", "q = F/E")]
        [InlineData("E = \\frac{F}{q}", "F = qE")]
        [InlineData("qe = f", "f = qe")]
        public void PhysicsFormulaEquivalence_ElectricForce_MatchesBidirectional(string studentAnswer, string standardAnswer)
        {
            // Act
            bool isEquiv = PracticeService.CheckPhysicsFormulaEquivalence(studentAnswer, standardAnswer);
            string reason = PracticeService.GenerateEquivalentMatchReason(studentAnswer, standardAnswer);

            // Assert
            Assert.True(isEquiv);
            Assert.Contains("电场力公式等价", reason);
        }

        [Theory]
        [InlineData("Q = mq", "q = Q/m")]
        [InlineData("q = \\frac{Q}{m}", "m = \\frac{Q}{q}")]
        [InlineData("Q = Vq", "q = Q/V")]
        [InlineData("Q = qV", "V = Q/q")]
        public void PhysicsFormulaEquivalence_CalorificHeatValue_MatchesBidirectional(string studentAnswer, string standardAnswer)
        {
            // Act
            bool isEquiv = PracticeService.CheckPhysicsFormulaEquivalence(studentAnswer, standardAnswer);
            string reason = PracticeService.GenerateEquivalentMatchReason(studentAnswer, standardAnswer);

            // Assert
            Assert.True(isEquiv);
            Assert.Contains("燃料热值公式等价", reason);
        }

        [Theory]
        [InlineData("G = mg", "m = G/g")]
        [InlineData("mg = G", "g = \\frac{G}{m}")]
        [InlineData("m = g/g", "g = mg")]
        public void PhysicsFormulaEquivalence_GravityAndMass_MatchesBidirectional(string studentAnswer, string standardAnswer)
        {
            // Act
            bool isEquiv = PracticeService.CheckPhysicsFormulaEquivalence(studentAnswer, standardAnswer);
            string reason = PracticeService.GenerateEquivalentMatchReason(studentAnswer, standardAnswer);

            // Assert
            Assert.True(isEquiv);
            Assert.Contains("重力公式等价", reason);
        }

        [Theory]
        [InlineData("p1/T1 = p2/T2", "p1T2 = p2T1")]
        [InlineData("p1V1 = p2V2", "p1/p2 = V2/V1")]
        [InlineData("V1/T1 = V2/T2", "V1T2 = V2T1")]
        [InlineData("p1V1/T1 = p2V2/T2", "(p1V1)/T1 = (p2V2)/T2")]
        public void PhysicsFormulaEquivalence_GasLawsAndCombinedState_MatchesBidirectional(string studentAnswer, string standardAnswer)
        {
            // Act
            bool isEquiv = PracticeService.CheckPhysicsFormulaEquivalence(studentAnswer, standardAnswer);
            string reason = PracticeService.GenerateEquivalentMatchReason(studentAnswer, standardAnswer);

            // Assert
            Assert.True(isEquiv);
            Assert.Contains("理想气体状态与实验定律等价", reason);
        }

        [Theory]
        [InlineData("y = a(x-h)^2 + k", "y = a(x-h)² + k")]
        [InlineData("x = -b/(2a)", "x = -b/2a")]
        [InlineData("x = -\\frac{b}{2a}", "x = -b/(2a)")]
        public void MathFormulaEquivalence_QuadraticVertexAndAxis_MatchesBidirectional(string studentAnswer, string standardAnswer)
        {
            // Act
            bool isEquiv = PracticeService.CheckPhysicsFormulaEquivalence(studentAnswer, standardAnswer);
            string reason = PracticeService.GenerateEquivalentMatchReason(studentAnswer, standardAnswer);

            // Assert
            Assert.True(isEquiv);
            Assert.Contains("二次函数顶点与对称轴等价", reason);
        }

        [Theory]
        [InlineData("(x-a)^2 + (y-b)^2 = r^2", "(x-a)² + (y-b)² = r²")]
        [InlineData("(x-x_0)^2 + (y-y_0)^2 = r^2", "(x-x0)² + (y-y0)² = r²")]
        public void MathFormulaEquivalence_CircleStandardEquation_MatchesBidirectional(string studentAnswer, string standardAnswer)
        {
            // Act
            bool isEquiv = PracticeService.CheckPhysicsFormulaEquivalence(studentAnswer, standardAnswer);
            string reason = PracticeService.GenerateEquivalentMatchReason(studentAnswer, standardAnswer);

            // Assert
            Assert.True(isEquiv);
            Assert.Contains("圆的标准方程等价", reason);
        }

        [Theory]
        [InlineData("Kw = [H+][OH-]", "Kw = c(H+)c(OH-)")]
        [InlineData("Kw = 10^-14", "Kw = 1.0*10^-14")]
        [InlineData("[H+][OH-] = 10^-14", "10^-14 = [H+][OH-]")]
        public void ChemistryFormulaEquivalence_WaterKwEquilibrium_MatchesBidirectional(string studentAnswer, string standardAnswer)
        {
            // Act
            bool isEquiv = PracticeService.CheckPhysicsFormulaEquivalence(studentAnswer, standardAnswer);
            string reason = PracticeService.GenerateEquivalentMatchReason(studentAnswer, standardAnswer);

            // Assert
            Assert.True(isEquiv);
            Assert.Contains("水的离子积常数等价", reason);
        }

        [Fact]
        public async Task SystemHealth_InvisibleCharsAndHtmlEntitiesHealing_CleansContaminatedQuestions()
        {
            // Arrange: 构造混有零宽字符（\u200B, \uFEFF）和未转义 HTML 实体（&nbsp;, &lt;, &gt;, &amp;）的试题
            var qContaminated = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "题干带有零宽字符\u200B与BOM\uFEFF以及&nbsp;空格和&lt;标记&gt;",
                Subject = "物理",
                Category = "电磁学",
                GradeTarget = "高二",
                Difficulty = 3,
                BaseExpReward = 15,
                Type = QuestionType.FillInBlank,
                OptionsJson = "[]",
                CorrectAnswer = "U=Ed\u200E&amp;qE\u200F",
                IsPublic = true,
                PublishStatus = PublishStatusEnum.Approved
            };
            _context.Questions.Add(qContaminated);
            await _context.SaveChangesAsync();

            // Act: 触发数据不变量自愈
            int healedCount = await _healthService.HealCorruptedQuestionsAsync();

            // Assert
            using var verifyDb = _factory.CreateDbContext();
            var healedQ = await verifyDb.Questions.FindAsync(qContaminated.Id);
            Assert.NotNull(healedQ);
            Assert.DoesNotContain('\u200B', healedQ.Stem);
            Assert.DoesNotContain('\uFEFF', healedQ.Stem);
            Assert.DoesNotContain("&nbsp;", healedQ.Stem);
            Assert.Contains("<标记>", healedQ.Stem);

            Assert.DoesNotContain('\u200E', healedQ.CorrectAnswer);
            Assert.DoesNotContain('\u200F', healedQ.CorrectAnswer);
            Assert.DoesNotContain("&amp;", healedQ.CorrectAnswer);
            Assert.Contains("&", healedQ.CorrectAnswer);

            // 验证试题领域完整性审计
            var audit = await _healthService.AuditDataIntegrityAsync();
            Assert.Equal(0, audit.TotalCorruptedQuestions);
            Assert.Equal(0, audit.TotalDesyncedPublishStatusQuestions);
            Assert.Equal(0, audit.TotalMalformedChoiceQuestions);
        }
    }
}
