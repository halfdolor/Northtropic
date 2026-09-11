using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxTranscendentSupremeTests
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

        #region 1. 系统架构师：IDbContextFactory 并发与多线程隔离测试

        [Fact]
        public async Task GamificationService_ConcurrentRewardProcessing_WithFactory_ShouldBeThreadSafe()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                var testUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "ThreadSafeStudent",
                    Role = UserRole.Student,
                    Exp = 100,
                    Coins = 50,
                    Level = 1
                };

                using (var setupCtx = factory.CreateDbContext())
                {
                    setupCtx.Users.Add(testUser);
                    await setupCtx.SaveChangesAsync();
                }

                // 启动 GamificationService，传入 IDbContextFactory
                using var dummyContext = factory.CreateDbContext();
                var fakeSession = new FakeUserSessionService { ActiveUser = testUser };
                var gamificationService = new GamificationService(dummyContext, fakeSession, factory);

                // 模拟并发调用 10 个线程同时提交答案奖励
                var tasks = Enumerable.Range(1, 10).Select(i => Task.Run(async () =>
                {
                    return await gamificationService.ProcessAnswerRewardAsync(
                        isCorrect: true,
                        baseExp: 10,
                        combo: i,
                        difficulty: 3,
                        timeTakenSeconds: 15,
                        isHistoryWrong: false,
                        targetUserId: testUser.Id
                    );
                })).ToArray();

                var results = await Task.WhenAll(tasks);

                Assert.All(results, r => Assert.True(r.EarnedExp > 0));

                // 验证数据库最终状态的一致性，无并发冲突或数据脏读
                using (var verifyCtx = factory.CreateDbContext())
                {
                    var updatedUser = await verifyCtx.Users.FindAsync(testUser.Id);
                    Assert.NotNull(updatedUser);
                    Assert.True(updatedUser.Level >= 2);
                    Assert.True(updatedUser.Coins > 50);
                    Assert.Equal(10, updatedUser.TotalAnswered);
                    Assert.Equal(10, updatedUser.TotalCorrect);
                }
            }
        }

        [Fact]
        public async Task CurriculumConfigService_ConcurrentAccess_WithFactory_ShouldSucceed()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                var admin = new User { Id = Guid.NewGuid(), Username = "SuperAdmin", Role = UserRole.SuperAdmin };

                using var setupCtx = factory.CreateDbContext();
                setupCtx.Users.Add(admin);
                await setupCtx.SaveChangesAsync();

                using var dummyContext = factory.CreateDbContext();
                var fakeSession = new FakeUserSessionService { ActiveUser = admin };
                var curriculumService = new CurriculumConfigService(dummyContext, fakeSession, factory);

                // 并发初始化与读取
                var tasks = Enumerable.Range(1, 8).Select(async _ =>
                {
                    await curriculumService.EnsureInitializedAsync();
                    return await curriculumService.GetAllConfigsAsync();
                }).ToArray();

                var results = await Task.WhenAll(tasks);

                Assert.All(results, list => Assert.NotEmpty(list));
            }
        }

        [Fact]
        public async Task QuestionImportService_TransactionalIntegrity_OnBatchImport_RollsBackOnError()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                var teacher = new User { Id = Guid.NewGuid(), Username = "Teacher", Role = UserRole.Teacher };

                using (var setupCtx = factory.CreateDbContext())
                {
                    setupCtx.Users.Add(teacher);
                    await setupCtx.SaveChangesAsync();
                }

                using var dummyContext = factory.CreateDbContext();
                var fakeSession = new FakeUserSessionService { ActiveUser = teacher };
                var importService = new QuestionImportService(dummyContext, fakeSession, null, factory);

                // 准备传输中断导致的残缺损坏 JSON 数据，测试防半导入原子性保障
                var corruptedJson = @"[
                    {
                        ""stem"": ""题目1：已知 x = 2，求 x^2"",
                        ""type"": ""单选题"",
                        ""options"": [""2"", ""4"", ""8"", ""16""],
                        ""answer"": ""B"",
                        ""grade"": ""初中一年级"",
                        ""subject"": ""数学""
                    },
                    { CORRUPTED_EOF
                ";

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(corruptedJson));
                var result = await importService.ParseAndImportQuestionsByFileFormatAsync(stream, "test_questions.json", teacher.Id);

                // 验证导入报告指出了错误行诊断信息
                Assert.False(result.IsSuccess);
                Assert.True(result.ErrorMessages.Count > 0);

                // 验证由于事务保障，未残留脏数据
                using (var verifyCtx = factory.CreateDbContext())
                {
                    var questionCount = await verifyCtx.Questions.CountAsync();
                    Assert.Equal(0, questionCount);
                }
            }
        }

        [Fact]
        public async Task QuestionManagementService_ExportCsv_SanitizesFormulaInjection()
        {
            var (connection, options) = CreateSharedInMemoryDb();
            using (connection)
            {
                var factory = new SqliteDbContextFactory(options);
                var teacher = new User { Id = Guid.NewGuid(), Username = "ExportTeacher", Role = UserRole.Teacher };

                var maliciousQuestion = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "=1+1",
                    CorrectAnswer = "@SUM(A1:A10)",
                    StandardAnalysis = "+cmd|' /C calc'!A0",
                    Subject = "数学",
                    GradeTarget = "初中一年级",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"-2+5\", \"选项B\"]",
                    CreatedByUserId = teacher.Id,
                    Difficulty = 3
                };

                using (var setupCtx = factory.CreateDbContext())
                {
                    setupCtx.Users.Add(teacher);
                    setupCtx.Questions.Add(maliciousQuestion);
                    await setupCtx.SaveChangesAsync();
                }

                using var dummyContext = factory.CreateDbContext();
                var qmService = new QuestionManagementService(dummyContext);

                var csvBytes = await qmService.ExportQuestionsCsvAsync(teacher.Id);
                var csvText = Encoding.UTF8.GetString(csvBytes);

                // 验证公式注入特征字符已全部被单引号转义，防止 Excel / WPS 执行任意命令或宏
                Assert.Contains("'=1+1", csvText);
                Assert.Contains("'@SUM(A1:A10)", csvText);
                Assert.Contains("'+cmd|' /C calc'!A0", csvText);
                Assert.Contains("'-2+5", csvText);
            }
        }

        #endregion

        #region 2. 用户体验专家：数学比例与比值智能等价测试

        [Theory]
        [InlineData("3:4", "3:4", true)]
        [InlineData("3:4", "3比4", true)]
        [InlineData("3比4", "3:4", true)]
        [InlineData("3:4", "3/4", true)]
        [InlineData("3/4", "3:4", true)]
        [InlineData("3:4", "0.75", true)]
        [InlineData("0.75", "3:4", true)]
        [InlineData("1:2:3", "1比2比3", true)]
        [InlineData("2:4:6", "1:2:3", true)]
        [InlineData("1比2比3", "2:4:6", true)]
        [InlineData("1:2", "0.5", true)]
        [InlineData("0.5", "1:2", true)]
        public void CheckFillInBlankMatch_RatiosAndFractions_ShouldMatch(string user, string correct, bool expected)
        {
            var match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, match);
        }

        [Fact]
        public void GenerateEquivalentMatchReason_RatioFeedback_ProvidesClearGuidance()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "3比4" };
            var reason = PracticeService.GenerateEquivalentMatchReason(q, "3:4");
            Assert.Contains("比值与比率智能等价", reason);
        }

        #endregion

        #region 3. 用户体验专家：有机化学结构简式与分子式智能等价测试

        [Theory]
        [InlineData("CH2=CH2", "C2H4", true)]
        [InlineData("C2H4", "CH2=CH2", true)]
        [InlineData("CH≡CH", "C2H2", true)]
        [InlineData("CH3CH2OH", "C2H5OH", true)]
        [InlineData("C2H5OH", "CH3CH2OH", true)]
        [InlineData("CH3COOH", "C2H4O2", true)]
        [InlineData("CH3CHO", "C2H4O", true)]
        [InlineData("HCHO", "CH2O", true)]
        public void CheckFillInBlankMatch_OrganicStructuralFormulas_ShouldMatch(string user, string correct, bool expected)
        {
            var match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, match);
        }

        [Fact]
        public void GenerateEquivalentMatchReason_OrganicFeedback_ProvidesClearGuidance()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "C2H4" };
            var reason = PracticeService.GenerateEquivalentMatchReason(q, "CH2=CH2");
            Assert.Contains("有机化学结构简式与分子式等价", reason);
        }

        [Theory]
        [InlineData("胆矾", "CuSO4*5H2O", true)]
        [InlineData("胆矾", "CuSO4·5H2O", true)]
        [InlineData("CuSO4·5H2O", "胆矾", true)]
        [InlineData("明矾", "KAl(SO4)2*12H2O", true)]
        [InlineData("绿矾", "FeSO4*7H2O", true)]
        [InlineData("石英", "SiO2", true)]
        [InlineData("大苏打", "Na2S2O3", true)]
        [InlineData("海波", "Na2S2O3", true)]
        public void CheckFillInBlankMatch_ChemicalMineralsAndHydrates_ShouldMatch(string user, string correct, bool expected)
        {
            var match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, match);
        }

        #endregion

        #region 4. 用户体验专家：电极半反应式电子转移移项等价测试

        [Theory]
        [InlineData("Zn - 2e- = Zn2+", "Zn = Zn2+ + 2e-", true)]
        [InlineData("Zn = Zn2+ + 2e-", "Zn - 2e- = Zn2+", true)]
        [InlineData("2Cl- - 2e- = Cl2", "2Cl- = Cl2 + 2e-", true)]
        [InlineData("2e- + Cu2+ = Cu", "Cu2+ + 2e- = Cu", true)]
        [InlineData("Cu2+ + 2e- = Cu", "2e- + Cu2+ = Cu", true)]
        public void CheckFillInBlankMatch_ElectrochemicalHalfReactions_ShouldMatch(string user, string correct, bool expected)
        {
            var match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, match);
        }

        [Fact]
        public void GenerateEquivalentMatchReason_ElectrochemicalFeedback_ProvidesClearGuidance()
        {
            var q = new Question { Type = QuestionType.FillInBlank, CorrectAnswer = "Zn = Zn2+ + 2e-" };
            var reason = PracticeService.GenerateEquivalentMatchReason(q, "Zn - 2e- = Zn2+");
            Assert.Contains("电极反应式与电子转移等价", reason);
        }

        #endregion

        #region 5. 用户体验专家：物理与科学复合单位智能识别测试

        [Theory]
        [InlineData("9.8 N/kg", "9.8", true)]
        [InlineData("9.8", "9.8 N/kg", true)]
        [InlineData("9.8 牛/千克", "9.8 N/kg", true)]
        [InlineData("4.2*10^3 J/(kg*℃)", "4200", true)]
        [InlineData("4200 焦/(千克·摄氏度)", "4200 J/(kg*℃)", true)]
        [InlineData("100 Ω", "100", true)]
        [InlineData("100 ohm", "100 Ω", true)]
        [InlineData("9.8 m/s²", "9.8 m/s^2", true)]
        [InlineData("9.8 m/s2", "9.8 m/s²", true)]
        public void CheckFillInBlankMatch_CompositeScientificUnits_ShouldMatch(string user, string correct, bool expected)
        {
            var match = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, match);
        }

        #endregion
    }
}
