using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Helpers;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class SharedMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly string _connectionString;

        public SharedMemoryDbContextFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public AppDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connectionString)
                .Options;
            return new AppDbContext(options);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateDbContext());
        }
    }

    public class ArchitectAndUxOmniApexEvolutionTests
    {
        [Fact]
        public async Task UserSessionService_WithDbContextFactory_SupportsConcurrentAsyncOperations()
        {
            string dbName = "omni_apex_" + Guid.NewGuid().ToString("N");
            string connStr = $"Data Source=file:{dbName}?mode=memory&cache=shared";

            using var masterConn = new SqliteConnection(connStr);
            masterConn.Open();

            var initOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(masterConn)
                .Options;

            using (var initContext = new AppDbContext(initOptions))
            {
                initContext.Database.EnsureCreated();

                // 预置 10 个测试用户
                for (int i = 1; i <= 10; i++)
                {
                    initContext.Users.Add(new User
                    {
                        Id = Guid.NewGuid(),
                        Username = $"Student{i}",
                        PhoneNumber = $"138000000{i:D2}",
                        Password = PasswordHasher.HashPassword("Password123!"),
                        Role = UserRole.Student,
                        Grade = "初中二年级",
                        AccountStatus = UserAccountStatus.Approved,
                        DailyTargetQuestions = 10,
                        RegisteredAt = DateTime.Now
                    });
                }
                await initContext.SaveChangesAsync();
            }

            var factory = new SharedMemoryDbContextFactory(connStr);
            using var defaultContext = factory.CreateDbContext();
            using var httpClient = new HttpClient();
            var sessionService = new UserSessionService(defaultContext, httpClient, factory);

            // 并发执行 20 个异步操作（同时读、写、登录、查询统计）
            var tasks = new List<Task>();
            for (int i = 1; i <= 10; i++)
            {
                int idx = i;
                tasks.Add(Task.Run(async () =>
                {
                    var loginRes = await sessionService.LoginWithPasswordAsync($"138000000{idx:D2}", "Password123!");
                    Assert.True(loginRes.Success);
                    Assert.NotNull(loginRes.User);

                    var users = await sessionService.GetAllUsersAsync();
                    Assert.True(users.Count >= 10);

                    var stats = await sessionService.GetUserStatisticsAsync();
                    Assert.True(stats.TotalUsers >= 10);

                    bool targetUpdated = await sessionService.UpdateDailyTargetAsync(loginRes.User.Id, 25 + idx);
                    Assert.True(targetUpdated);
                }));
            }

            // 所有并发任务应安全完成，绝不发生 EF Core 多线程操作异常
            await Task.WhenAll(tasks);
        }

        [Fact]
        public async Task UserSessionService_FallbackToScopedContext_WhenFactoryIsNull()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var testUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "FallbackUser",
                    PhoneNumber = "13911112222",
                    Password = PasswordHasher.HashPassword("TestPass123"),
                    Role = UserRole.Student,
                    AccountStatus = UserAccountStatus.Approved,
                    RegisteredAt = DateTime.Now
                };
                context.Users.Add(testUser);
                await context.SaveChangesAsync();

                using var httpClient = new HttpClient();
                // 传入 null 工厂测试降级分支
                var sessionService = new UserSessionService(context, httpClient, null);

                var loginRes = await sessionService.LoginWithPasswordAsync("13911112222", "TestPass123");
                Assert.True(loginRes.Success);
                Assert.Equal("FallbackUser", loginRes.User?.Username);

                var userList = await sessionService.GetAllUsersAsync();
                Assert.Contains(userList, u => u.Username == "FallbackUser");
            }
        }

        [Theory]
        [InlineData("100\\mu m", "100")]
        [InlineData("50 μm", "50")]
        [InlineData("2.5 um", "2.5")]
        [InlineData("10\\mu s", "10")]
        [InlineData("100 pF", "100")]
        [InlineData("50 mT", "50")]
        [InlineData("100 微米", "100")]
        [InlineData("20 毫安", "20")]
        [InlineData("5 微法", "5")]
        [InlineData("3.6\\times 10^{6} J", "3.6\\times 10^{6}")]
        public void PracticeService_StripCommonUnits_CorrectlyStripsMicroscaleAndChineseUnits(string input, string expected)
        {
            string stripped = PracticeService.StripCommonUnits(input);
            Assert.Equal(expected, stripped);
        }

        [Fact]
        public void PracticeService_CheckFillInBlankMatch_MatchesMicroscaleUnitsIntelligently()
        {
            // 考生输入有单位，标准答案无单位或不同单位表示
            bool match1 = PracticeService.CheckFillInBlankMatch("50\\mu m", "50");
            Assert.True(match1);

            bool match2 = PracticeService.CheckFillInBlankMatch("20毫安", "20mA");
            Assert.True(match2);

            bool match3 = PracticeService.CheckFillInBlankMatch("100pF", "100 皮法");
            Assert.True(match3);

            bool match4 = PracticeService.CheckFillInBlankMatch("1000 nm", "1000");
            Assert.True(match4);
        }

        [Fact]
        public async Task UserSessionService_RBAC_PreventsUnauthorizedPasswordReset()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var targetStudent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "VictimStudent",
                    PhoneNumber = "13800000001",
                    Password = PasswordHasher.HashPassword("OriginalPass123"),
                    Role = UserRole.Student,
                    AccountStatus = UserAccountStatus.Approved
                };

                var unrelatedParent = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "AttackerParent",
                    PhoneNumber = "13800000002",
                    Password = PasswordHasher.HashPassword("ParentPass123"),
                    Role = UserRole.Parent,
                    AccountStatus = UserAccountStatus.Approved
                };

                context.Users.AddRange(targetStudent, unrelatedParent);
                await context.SaveChangesAsync();

                using var httpClient = new HttpClient();
                var sessionService = new UserSessionService(context, httpClient);

                // 未绑定关系的家长尝试重置其他学生的密码应被 RBAC 拒绝
                bool resetResult = await sessionService.ResetUserPasswordAsync(targetStudent.Id, "HackedPass123", unrelatedParent.Id);
                Assert.False(resetResult);

                // 验证密码未被篡改
                var refreshedStudent = await context.Users.FindAsync(targetStudent.Id);
                Assert.True(PasswordHasher.VerifyPassword("OriginalPass123", refreshedStudent!.Password));
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("1234567")] // 少于8位
        [InlineData("12345678")] // 弱口令纯数字
        [InlineData("abcdefgh")] // 弱口令纯字母
        [InlineData("password123")] // 常见弱口令字典
        public async Task UserSessionService_RegisterWithPassword_RejectsInsecurePasswords(string weakPassword)
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                using var httpClient = new HttpClient();
                var sessionService = new UserSessionService(context, httpClient);

                var regResult = await sessionService.RegisterWithPasswordAsync(
                    "TestUser", UserRole.Student, "13812345678", "test@test.com", weakPassword, weakPassword);

                Assert.False(regResult.Success);
                Assert.Contains("密码", regResult.Message);
            }
        }
    }
}
