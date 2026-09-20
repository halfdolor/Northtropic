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
    public class ArchitectAndUxSovereignApexZenithEvolutionTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly SqliteConnection _connection;
        private readonly SimpleTestDbContextFactory _factory;
        private readonly SystemHealthService _healthService;

        public ArchitectAndUxSovereignApexZenithEvolutionTests()
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

        [Fact]
        public async Task QuestionDomainInvariants_AuditAndHealing_RestoresPublishStatusAndNormalizesChoices()
        {
            // Arrange
            var q1 = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "题1：可见性为真但审核状态为私有",
                Subject = "数学",
                Category = "代数",
                GradeTarget = "高一",
                Difficulty = 2,
                BaseExpReward = 10,
                Type = QuestionType.SingleChoice,
                OptionsJson = "[\"A\", \"B\", \"C\", \"D\"]",
                CorrectAnswer = "A",
                IsPublic = true,
                PublishStatus = PublishStatusEnum.Private
            };

            var q2 = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "题2：审核已放行但可见性未置为公开",
                Subject = "物理",
                Category = "电磁学",
                GradeTarget = "高二",
                Difficulty = 3,
                BaseExpReward = 15,
                Type = QuestionType.SingleChoice,
                OptionsJson = "[\"A\", \"B\", \"C\", \"D\"]",
                CorrectAnswer = "B",
                IsPublic = false,
                PublishStatus = PublishStatusEnum.Approved
            };

            var q3 = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "题3：单选题正确答案被 JSON 括号包裹",
                Subject = "化学",
                Category = "化学反应",
                GradeTarget = "高一",
                Difficulty = 2,
                BaseExpReward = 10,
                Type = QuestionType.SingleChoice,
                OptionsJson = "[\"A\", \"B\", \"C\", \"D\"]",
                CorrectAnswer = "[\"C\"]",
                IsPublic = true,
                PublishStatus = PublishStatusEnum.Approved
            };

            var q4 = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "题4：多选题答案包含冗余句点与中文顿号",
                Subject = "通用知识",
                Category = "基础概念",
                GradeTarget = "通用",
                Difficulty = 3,
                BaseExpReward = 20,
                Type = QuestionType.MultipleChoice,
                OptionsJson = "[\"A\", \"B\", \"C\", \"D\"]",
                CorrectAnswer = "A、B、D.",
                IsPublic = true,
                PublishStatus = PublishStatusEnum.Approved
            };

            _context.Questions.AddRange(q1, q2, q3, q4);
            await _context.SaveChangesAsync();

            // Act 1: 审计排查
            var initialAudit = await _healthService.AuditDataIntegrityAsync();

            // Assert 1: 准确检测到领域不变量异常
            Assert.True(initialAudit.TotalDesyncedPublishStatusQuestions >= 2);
            Assert.True(initialAudit.TotalMalformedChoiceQuestions >= 2);

            // Act 2: 执行自愈修复
            int healedCount = await _healthService.HealCorruptedQuestionsAsync();
            Assert.True(healedCount >= 4);

            // Act 3: 重新审计
            var postAudit = await _healthService.AuditDataIntegrityAsync();

            // Assert 3: 不变量已被彻底自愈归零
            Assert.Equal(0, postAudit.TotalDesyncedPublishStatusQuestions);
            Assert.Equal(0, postAudit.TotalMalformedChoiceQuestions);

            // 校验具体实体字段修复结果（使用全新 DbContext 规避本地内存缓存）
            using var verifyDb = _factory.CreateDbContext();
            var savedQ1 = await verifyDb.Questions.FindAsync(q1.Id);
            var savedQ2 = await verifyDb.Questions.FindAsync(q2.Id);
            var savedQ3 = await verifyDb.Questions.FindAsync(q3.Id);
            var savedQ4 = await verifyDb.Questions.FindAsync(q4.Id);

            Assert.NotNull(savedQ1);
            Assert.Equal(PublishStatusEnum.Approved, savedQ1.PublishStatus);

            Assert.NotNull(savedQ2);
            Assert.True(savedQ2.IsPublic);

            Assert.NotNull(savedQ3);
            Assert.Equal("C", savedQ3.CorrectAnswer);

            Assert.NotNull(savedQ4);
            Assert.Equal("A、B、D", savedQ4.CorrectAnswer);
        }

        [Fact]
        public async Task HealAllInvariants_ExecutesCleanlyAndRestoresSystemHealth()
        {
            // Arrange: 注入脱节题目
            var qDesync = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "脱节测试题",
                Subject = "数学",
                Category = "几何",
                GradeTarget = "初三",
                Difficulty = 2,
                BaseExpReward = 10,
                Type = QuestionType.SingleChoice,
                OptionsJson = "[\"A\", \"B\"]",
                CorrectAnswer = "[\"A\"]",
                IsPublic = true,
                PublishStatus = PublishStatusEnum.Private
            };
            _context.Questions.Add(qDesync);
            await _context.SaveChangesAsync();

            // Act
            var result = await _healthService.HealAllInvariantsAsync();

            // Assert
            Assert.NotNull(result);
            Assert.True(result.ElapsedMilliseconds >= 0);

            var postAudit = await _healthService.AuditDataIntegrityAsync();
            Assert.Equal(0, postAudit.TotalDesyncedPublishStatusQuestions);
            Assert.Equal(0, postAudit.TotalMalformedChoiceQuestions);
        }

        [Theory]
        [InlineData("P \\implies Q", "P => Q")]
        [InlineData("P \\iff Q", "P <=> Q")]
        [InlineData("P \\land Q", "P ^ Q")]
        [InlineData("P \\lor Q", "P v Q")]
        [InlineData("\\neg P", "!P")]
        [InlineData("A \\Rightarrow B", "A => B")]
        [InlineData("A \\Leftrightarrow B", "A <=> B")]
        public void STEM_MathematicalLogic_EquivalenceEvaluations(string userAnswer, string correctAnswer)
        {
            var q = new Question
            {
                Id = Guid.NewGuid(),
                Type = QuestionType.FillInBlank,
                CorrectAnswer = correctAnswer,
                Stem = "逻辑命题等价证明"
            };

            bool isMatch = PracticeService.CheckAnswerCorrectness(q, userAnswer);
            Assert.True(isMatch, $"Expected user answer '{userAnswer}' to match standard answer '{correctAnswer}'");
        }

        [Theory]
        [InlineData("2 T", "2 Wb/m^2")]
        [InlineData("2 特斯拉", "2 Wb/m²")]
        [InlineData("50 Hz", "50 s^-1")]
        [InlineData("50 赫兹", "50 1/s")]
        [InlineData("1 kW·h", "3.6*10^6 J")]
        [InlineData("1 度", "3.6e6 焦耳")]
        [InlineData("1 eV", "1.602*10^-19 J")]
        [InlineData("1 atm", "101325 Pa")]
        public void STEM_PhysicsElectromagneticAndEnergy_UnitConversions(string userAnswer, string correctAnswer)
        {
            var q = new Question
            {
                Id = Guid.NewGuid(),
                Type = QuestionType.FillInBlank,
                CorrectAnswer = correctAnswer,
                Stem = "理科科学单位等价换算"
            };

            bool isMatch = PracticeService.CheckAnswerCorrectness(q, userAnswer);
            Assert.True(isMatch, $"Expected physical unit '{userAnswer}' to match standard '{correctAnswer}'");
        }

        [Fact]
        public void GenerateEquivalentMatchReason_PedagogicalFeedbackGeneration()
        {
            // 1. 数理逻辑命题等价
            var qLogic = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "P => Q", Stem = "命题逻辑题" };
            string logicReason = PracticeService.GenerateEquivalentMatchReason(qLogic, "P \\implies Q");
            Assert.Contains("数理逻辑命题等价", logicReason);

            // 2. 反三角函数等价
            var qTrig = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "\\sin^{-1}(x)", Stem = "三角函数题" };
            string trigReason = PracticeService.GenerateEquivalentMatchReason(qTrig, "\\arcsin(x)");
            Assert.Contains("反三角函数表达等价", trigReason);

            // 3. 物理电磁与能量国际单位
            var qMag = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "2 Wb/m^2", Stem = "磁学题" };
            string magReason = PracticeService.GenerateEquivalentMatchReason(qMag, "2 T");
            Assert.Contains("物理电磁/频率/能量单位智能对齐等价", magReason);

            var qEnergy = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "3.6e6 J", Stem = "电能功耗题" };
            string energyReason = PracticeService.GenerateEquivalentMatchReason(qEnergy, "1 kW·h");
            Assert.Contains("物理电磁/频率/能量单位智能对齐等价", energyReason);
        }
    }
}
