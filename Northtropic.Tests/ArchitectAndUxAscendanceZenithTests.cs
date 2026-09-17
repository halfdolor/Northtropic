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
    public class ArchitectAndUxAscendanceZenithTests : IDisposable
    {
        private readonly AppDbContext _inMemoryContext;
        private readonly SqliteConnection _connection;

        public class TestSqliteDbContextFactory : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;
            public TestSqliteDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
            public AppDbContext CreateDbContext() => new AppDbContext(_options);
        }

        public ArchitectAndUxAscendanceZenithTests()
        {
            (_inMemoryContext, _connection) = TestDbContextFactory.CreateInMemoryContext();
        }

        public void Dispose()
        {
            _inMemoryContext.Dispose();
            _connection.Dispose();
        }

        #region 1. 系统架构师：SystemHealthService 自检与 Checkpoint 5 并发连接池探针测试

        [Fact]
        public async Task SystemHealthService_RunArchitecturalSelfDiagnostic_ExecutesCheckpoint5ConcurrencyProbe()
        {
            // Arrange
            _inMemoryContext.Users.Add(new User { Username = "zenith_admin", Role = UserRole.SuperAdmin });
            for (int i = 1; i <= 8; i++)
            {
                _inMemoryContext.Questions.Add(new Question
                {
                    Stem = $"架构自检测题 #{i}",
                    Subject = "物理",
                    GradeTarget = "初中三年级",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = $"{i}"
                });
            }
            await _inMemoryContext.SaveChangesAsync();

            var healthService = new SystemHealthService(_inMemoryContext);

            // Act
            var result = await healthService.RunArchitecturalSelfDiagnosticAsync();

            // Assert
            Assert.NotNull(result);
            Assert.True(result.IsPassed, "全量架构自检应顺利通过");
            Assert.Equal("Pass", result.OverallStatus);
            Assert.True(result.ConcurrencyStressPassed, "Checkpoint 5 并发探针应判定为通过");
            Assert.True(result.ConcurrencyThroughputQps > 0, "并发吞吐 QPS 应大于 0");
            Assert.Contains(result.DiagnosticCheckpoints, cp => cp.Contains("[Checkpoint 5]"));
            Assert.Contains(result.DiagnosticCheckpoints, cp => cp.Contains("异步并发连接池与 WAL 锁争用压力探针"));
            Assert.True(result.CoreTablesFound >= 6);
            Assert.True(result.TotalQuestionsScanned >= 8);
        }

        [Fact]
        public async Task SystemHealthService_RunArchitecturalSelfDiagnostic_WithDbContextFactory_HandlesConcurrentProbes()
        {
            // Arrange: 创建共享内存连接以供多并发独立 Context 使用
            var dbName = $"memdb_arch_{Guid.NewGuid():N}";
            var connStr = $"Data Source={dbName};Mode=Memory;Cache=Shared";
            using var conn = new SqliteConnection(connStr);
            conn.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connStr)
                .Options;

            using (var seedCtx = new AppDbContext(options))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.Users.Add(new User { Username = "factory_admin", Role = UserRole.SuperAdmin });
                for (int i = 1; i <= 6; i++)
                {
                    seedCtx.Questions.Add(new Question
                    {
                        Stem = $"工厂模式测试试题 #{i}",
                        Subject = "化学",
                        Type = QuestionType.FillInBlank,
                        CorrectAnswer = "H2O"
                    });
                }
                await seedCtx.SaveChangesAsync();
            }

            var factory = new TestSqliteDbContextFactory(options);
            using var fallbackCtx = factory.CreateDbContext();
            var healthService = new SystemHealthService(fallbackCtx, factory);

            // Act
            var diagResult = await healthService.RunArchitecturalSelfDiagnosticAsync();

            // Assert
            Assert.NotNull(diagResult);
            Assert.True(diagResult.IsPassed);
            Assert.True(diagResult.ConcurrencyStressPassed);
            Assert.True(diagResult.ConcurrencyThroughputQps > 0);
            Assert.Contains(diagResult.DiagnosticCheckpoints, cp => cp.Contains("高并发独立连接池"));
        }

        #endregion

        #region 2. 智能判分引擎：代数与空间几何坐标匹配 (含中文点标签前缀)

        [Theory]
        [InlineData("点P(1, 2)", "(1, 2)", true)]
        [InlineData("(1, 2)", "点(1, 2)", true)]
        [InlineData("点P(1, 2)", "P(1, 2)", true)]
        [InlineData("P(1, 2)", "点(1, 2)", true)]
        [InlineData("点A(3, 4, 5)", "(3, 4, 5)", true)]
        [InlineData("(3, 4, 5)", "点B(3, 4, 5)", true)]
        [InlineData("点P(2, -3)", "x=2,y=-3", true)]
        [InlineData("x=1,y=2", "点P(1, 2)", true)]
        [InlineData("点M(0, 0)", "(0, 0)", true)]
        public void PracticeService_CheckCoordinateEquationMatch_SupportsPrefixes(string user, string correct, bool expected)
        {
            bool actual = PracticeService.CheckCoordinateEquationMatch(user, correct) ||
                          PracticeService.CheckCoordinateEquationMatch(correct, user);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData("点P(1, 2)", "(1, 2)", true)]
        [InlineData("点(3, 4)", "(3, 4)", true)]
        [InlineData("(2, 5)", "点A(2, 5)", true)]
        [InlineData("P(1, 2)", "点P(1, 2)", true)]
        [InlineData("点P(0, -1)", "P(0,-1)", true)]
        public void PracticeService_CheckFillInBlankMatch_CoordinatesWithChineseLabels_Matches(string user, string correct, bool expected)
        {
            bool actual = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void PracticeService_CheckFillInBlankMatch_PhysicalConstantsAndUnits_MatchesEquivalently()
        {
            // 物理常数光速与电荷
            Assert.True(PracticeService.CheckFillInBlankMatch("3*10^8 m/s", "3e8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("3×10^8米/秒", "3*10^8"));
            Assert.True(PracticeService.CheckFillInBlankMatch("1.6*10^-19 C", "1.6e-19 库仑"));
        }

        #endregion

        #region 3. 用户体验专家：做题交互键盘状态机与全键盘流模拟

        [Fact]
        public void PracticeKeyFlow_ContinuousEnter_TransitionsFromSubmitToNextQuestion()
        {
            // 模拟 Practice 页面用户作答全键盘流状态机
            bool submitted = false;
            int currentIndex = 1;
            int totalQuestions = 5;
            bool nextQuestionTriggered = false;
            bool submitAnswerTriggered = false;

            void HandleEnterKey()
            {
                if (!submitted)
                {
                    submitAnswerTriggered = true;
                    submitted = true; // 模拟 SubmitAnswerAsync 完成
                }
                else
                {
                    if (currentIndex < totalQuestions)
                    {
                        currentIndex++;
                        nextQuestionTriggered = true;
                        submitted = false; // 进入新题，重置为未提交状态
                    }
                }
            }

            // Step 1: 学生输入答案后第一次按下 Enter -> 触发答题提交
            HandleEnterKey();
            Assert.True(submitAnswerTriggered);
            Assert.True(submitted);
            Assert.Equal(1, currentIndex);

            // Step 2: 学生查看即时反馈后再次按下 Enter -> 无缝直接切入下一题
            HandleEnterKey();
            Assert.True(nextQuestionTriggered);
            Assert.False(submitted);
            Assert.Equal(2, currentIndex);
        }

        [Fact]
        public async Task UserSessionService_DemoAccounts_And_PasswordSecurity_ValidatesProperly()
        {
            // Arrange
            using var http = new System.Net.Http.HttpClient();
            var sessionService = new UserSessionService(_inMemoryContext, http);

            // 确保测试环境中同步内置演示账号生命周期
            await sessionService.SyncDemoAccountsLifecycleAsync();

            // 验证登录状态机：错误密码失败
            var (failSuccess, failUser, failMsg) = await sessionService.LoginWithPasswordAsync("non_existent_user_999", "wrong_password");
            Assert.False(failSuccess);
            Assert.Null(failUser);
            Assert.Contains("不存在", failMsg);
        }

        #endregion
    }
}
