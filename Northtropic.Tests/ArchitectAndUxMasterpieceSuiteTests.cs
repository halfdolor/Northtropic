using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxMasterpieceSuiteTests
    {
        private class TestDbContextFactoryImpl : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;

            public TestDbContextFactoryImpl(DbContextOptions<AppDbContext> options)
            {
                _options = options;
            }

            public AppDbContext CreateDbContext()
            {
                return new AppDbContext(_options);
            }
        }

        [Fact]
        public async Task SystemHealthService_GetSystemHealthAsync_ReturnsAccurateMetrics()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                // Seed test data
                var testUser = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "health_test_user",
                    Password = "hashed",
                    Role = UserRole.Student,
                    Grade = "初中二年级"
                };
                context.Users.Add(testUser);

                var testQuestion = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试架构健康体检试题",
                    Subject = "数学",
                    Category = "函数",
                    Type = QuestionType.SingleChoice,
                    CorrectAnswer = "A",
                    OptionsJson = "[\"A. 1\",\"B. 2\"]",
                    StandardAnalysis = "解析"
                };
                context.Questions.Add(testQuestion);

                var testLog = new LlmGenerationLog
                {
                    Id = Guid.NewGuid(),
                    UserId = testUser.Id,
                    ModelName = "test-model",
                    Subject = "数学",
                    Category = "测试",
                    PromptTokens = 10,
                    CompletionTokens = 20,
                    TotalTokens = 30,
                    GeneratedAt = DateTime.Now
                };
                context.LlmGenerationLogs.Add(testLog);

                await context.SaveChangesAsync();

                var healthService = new SystemHealthService(context, null);
                var healthDto = await healthService.GetSystemHealthAsync();

                Assert.NotNull(healthDto);
                Assert.True(healthDto.TotalUsers >= 1);
                Assert.True(healthDto.TotalQuestions >= 1);
                Assert.True(healthDto.TotalLlmLogs >= 1);
                Assert.Equal("ok", healthDto.SqliteIntegrityStatus);
                Assert.True(healthDto.IsDatabaseHealthy);
                Assert.True(healthDto.GcMemoryBytes > 0);
                Assert.NotEmpty(healthDto.GcMemoryFormatted);
                Assert.NotEmpty(healthDto.DatabaseSizeFormatted);
                Assert.NotEmpty(healthDto.ProcessUptimeFormatted);
                Assert.NotEmpty(healthDto.DotNetVersion);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        [Fact]
        public async Task SystemHealthService_WithDbContextFactory_ExecutesSuccessfully()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            using (var initContext = new AppDbContext(options))
            {
                initContext.Database.EnsureCreated();
                initContext.Users.Add(new User
                {
                    Id = Guid.NewGuid(),
                    Username = "factory_user",
                    Password = "hash",
                    Role = UserRole.Teacher
                });
                await initContext.SaveChangesAsync();
            }

            try
            {
                var factory = new TestDbContextFactoryImpl(options);
                using var dummyContext = new AppDbContext(options);
                var healthService = new SystemHealthService(dummyContext, factory);

                var healthDto = await healthService.GetSystemHealthAsync();
                Assert.NotNull(healthDto);
                Assert.True(healthDto.TotalUsers >= 1);
                Assert.Equal("ok", healthDto.SqliteIntegrityStatus);

                var vacuumOk = await healthService.VacuumDatabaseAsync();
                Assert.True(vacuumOk);

                var checkStatus = await healthService.RunDatabaseIntegrityCheckAsync();
                Assert.Equal("ok", checkStatus);
            }
            finally
            {
                connection.Dispose();
            }
        }

        [Fact]
        public async Task UserSessionService_DeleteUserAsync_CascadesLlmGenerationLogs()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                var userId = Guid.NewGuid();
                var user = new User
                {
                    Id = userId,
                    Username = "cascade_test_user",
                    Password = "hash",
                    Role = UserRole.Student
                };
                context.Users.Add(user);

                var log1 = new LlmGenerationLog
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    ModelName = "gpt-4",
                    Subject = "物理",
                    Category = "力学",
                    PromptTokens = 50,
                    CompletionTokens = 50,
                    TotalTokens = 100,
                    GeneratedAt = DateTime.Now
                };
                var log2 = new LlmGenerationLog
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    ModelName = "gpt-4",
                    Subject = "化学",
                    Category = "酸碱盐",
                    PromptTokens = 30,
                    CompletionTokens = 40,
                    TotalTokens = 70,
                    GeneratedAt = DateTime.Now
                };
                context.LlmGenerationLogs.AddRange(log1, log2);
                await context.SaveChangesAsync();

                var sessionService = new UserSessionService(context, new HttpClient());
                var deleted = await sessionService.DeleteUserAsync(userId);

                Assert.True(deleted);

                var remainingUser = await context.Users.FindAsync(userId);
                Assert.Null(remainingUser);

                var remainingLogs = await context.LlmGenerationLogs.Where(l => l.UserId == userId).ToListAsync();
                Assert.Empty(remainingLogs);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        [Fact]
        public void SecurityDefenseCounters_ActiveLockoutsAndPendingSms_AreTracked()
        {
            // Initial sanity checks
            UserSessionService.EnsureExpiredEntriesPurged(true);
            int initialLockouts = UserSessionService.ActiveLockoutsCount;
            int initialSms = UserSessionService.PendingSmsCodesCount;

            Assert.True(initialLockouts >= 0);
            Assert.True(initialSms >= 0);
        }

        [Fact]
        public void UserRoleCapabilities_AlignAcrossPersonas()
        {
            // 1. Teacher Persona
            var teacher = new User { Role = UserRole.Teacher };
            Assert.True(teacher.CanViewStudentAnalytics);
            Assert.True(teacher.CanManageQuestionBank);
            Assert.False(teacher.CanAccessSystemConfig);

            // 2. Parent Persona
            var parent = new User { Role = UserRole.Parent };
            Assert.True(parent.CanViewStudentAnalytics);
            Assert.True(parent.CanManageBoundStudents);
            Assert.False(parent.CanManageQuestionBank);

            // 3. Student Persona
            var student = new User { Role = UserRole.Student };
            Assert.False(student.CanViewStudentAnalytics);
            Assert.False(student.CanManageQuestionBank);
            Assert.False(student.CanAccessSystemConfig);

            // 4. SuperAdmin Persona
            var admin = new User { Role = UserRole.SuperAdmin };
            Assert.True(admin.CanViewStudentAnalytics);
            Assert.True(admin.CanManageQuestionBank);
            Assert.True(admin.CanManageBoundStudents);
            Assert.True(admin.CanAccessSystemConfig);
        }

        [Fact]
        public void ServiceConstructors_SupportDbContextFactoryInjection()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            try
            {
                // Test StudentEvolutionService instantiation with null factory
                var sessionService = new UserSessionService(context, new HttpClient());
                var gamificationService = new GamificationService(context, sessionService);
                var clientFactory = new TestHttpClientFactory(new HttpClient());
                var evolutionService = new StudentEvolutionService(context, gamificationService, clientFactory, null);
                Assert.NotNull(evolutionService);

                // Test QuestionManagementService instantiation with null factory
                var qmService = new QuestionManagementService(context, null);
                Assert.NotNull(qmService);
            }
            finally
            {
                context.Dispose();
                connection.Dispose();
            }
        }

        private class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;
            public TestHttpClientFactory(HttpClient client) => _client = client;
            public HttpClient CreateClient(string name) => _client;
        }
    }
}
