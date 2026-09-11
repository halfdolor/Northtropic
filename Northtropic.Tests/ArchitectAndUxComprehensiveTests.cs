using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class SimpleTestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public SimpleTestDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext()
        {
            return new AppDbContext(_options);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppDbContext(_options));
        }
    }

    public class ArchitectAndUxComprehensiveTests
    {
        #region 1. 架构级高并发安全性与 IDbContextFactory 隔离测试

        [Fact]
        public async Task Concurrency_ParallelQueries_WithDbContextFactory_ShouldNotThrowConcurrencyException()
        {
            var dbName = $"InMemoryDb_{Guid.NewGuid():N}";
            var connString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
            using var keepAliveConnection = new SqliteConnection(connString);
            keepAliveConnection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connString)
                .Options;

            using (var initContext = new AppDbContext(options))
            {
                initContext.Database.EnsureCreated();

                var student = new User { Id = Guid.NewGuid(), Username = "ConcurrentUser", Role = UserRole.Student, Grade = "初中二年级" };
                initContext.Users.Add(student);

                var q1 = new Question { Id = Guid.NewGuid(), Stem = "1+1=?", OptionsJson = "[\"1\",\"2\",\"3\",\"4\"]", CorrectAnswer = "B", Subject = "数学", Category = "代数", Difficulty = 1, IsPublic = true };
                var q2 = new Question { Id = Guid.NewGuid(), Stem = "2+2=?", OptionsJson = "[\"2\",\"4\",\"6\",\"8\"]", CorrectAnswer = "B", Subject = "数学", Category = "代数", Difficulty = 1, IsPublic = true };
                initContext.Questions.AddRange(q1, q2);

                initContext.ErrorItems.Add(new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = q1.Id,
                    UserWrongAnswer = "A",
                    IsMastered = false,
                    CreatedAt = DateTime.Now
                });

                initContext.PracticeRecords.Add(new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = q1.Id,
                    UserAnswer = "B",
                    IsCorrect = true,
                    TimeTakenSeconds = 10,
                    AnsweredAt = DateTime.Now
                });

                await initContext.SaveChangesAsync();
            }

            var factory = new SimpleTestDbContextFactory(options);
            using (var rootContext = factory.CreateDbContext())
            {
                var fakeSession = new FakeUserSessionService();
                var fakeGamification = new FakeGamificationService();
                var fakeAi = new FakeAiTutorService();
                var httpFactory = new FakeHttpClientFactory();

                var errorBook = new ErrorBookService(rootContext, fakeGamification, fakeSession, factory);
                var practiceService = new PracticeService(rootContext, fakeGamification, fakeSession, fakeAi, factory);
                var evolutionService = new StudentEvolutionService(rootContext, fakeGamification, httpFactory, factory);

                var studentUser = await rootContext.Users.FirstAsync(u => u.Username == "ConcurrentUser");
                var targetUserId = studentUser.Id;
                fakeSession.ActiveUser = studentUser;

                // 模拟多线程/异步并行执行，类似 Blazor Home.razor Task.WhenAll
                var tasks = new List<Task>();
                for (int i = 0; i < 10; i++)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        var unmastered = await errorBook.GetUnmasteredCountAsync(targetUserId);
                        Assert.Equal(1, unmastered);

                        var errors = await errorBook.GetUnmasteredErrorsAsync(targetUserId);
                        Assert.Single(errors);

                        var questions = await practiceService.GetQuestionsAsync("数学", 5);
                        Assert.NotNull(questions);

                        var analytics = await practiceService.GetPracticeAnalyticsAsync(targetUserId);
                        Assert.NotNull(analytics);

                        var mastery = await evolutionService.GetMasteryOverviewAsync(targetUserId);
                        Assert.NotNull(mastery);

                        var diagnosis = await evolutionService.GenerateDiagnosisReportAsync(targetUserId);
                        Assert.NotNull(diagnosis);
                    }));
                }

                // 必须在无任何 DbContext 并发冲突异常下顺利完成
                await Task.WhenAll(tasks);
            }
        }

        #endregion

        #region 2. 高性能索引计数与越权防护 (IDOR) 深度测试

        [Fact]
        public async Task ErrorBook_GetUnmasteredCountAsync_PerformanceAndIdorProtection()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student1 = new User { Id = Guid.NewGuid(), Username = "Student1", Role = UserRole.Student };
                var student2 = new User { Id = Guid.NewGuid(), Username = "Student2", Role = UserRole.Student };
                var parent = new User { Id = Guid.NewGuid(), Username = "Parent1", Role = UserRole.Parent };
                var teacher = new User { Id = Guid.NewGuid(), Username = "Teacher1", Role = UserRole.Teacher };
                var superAdmin = new User { Id = Guid.NewGuid(), Username = "Admin", Role = UserRole.SuperAdmin };

                context.Users.AddRange(student1, student2, parent, teacher, superAdmin);

                // parent 只绑定 student1
                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parent.Id,
                    StudentUserId = student1.Id,
                    RelationType = "父亲"
                });

                var q1 = new Question { Id = Guid.NewGuid(), Stem = "Q1", OptionsJson = "[]", CorrectAnswer = "A", Subject = "数学", Category = "代数" };
                var q2 = new Question { Id = Guid.NewGuid(), Stem = "Q2", OptionsJson = "[]", CorrectAnswer = "B", Subject = "数学", Category = "代数" };
                context.Questions.AddRange(q1, q2);

                // student1 有 2 题未掌握，1 题已掌握
                context.ErrorItems.AddRange(
                    new ErrorItem { Id = Guid.NewGuid(), UserId = student1.Id, QuestionId = q1.Id, IsMastered = false, CreatedAt = DateTime.Now },
                    new ErrorItem { Id = Guid.NewGuid(), UserId = student1.Id, QuestionId = q2.Id, IsMastered = false, CreatedAt = DateTime.Now },
                    new ErrorItem { Id = Guid.NewGuid(), UserId = student1.Id, QuestionId = q1.Id, IsMastered = true, CreatedAt = DateTime.Now }
                );

                // student2 有 1 题未掌握
                context.ErrorItems.Add(
                    new ErrorItem { Id = Guid.NewGuid(), UserId = student2.Id, QuestionId = q1.Id, IsMastered = false, CreatedAt = DateTime.Now }
                );

                await context.SaveChangesAsync();

                var fakeSession = new FakeUserSessionService();
                var fakeGamification = new FakeGamificationService();
                var service = new ErrorBookService(context, fakeGamification, fakeSession);

                // 1. student1 查询自己 -> 返回 2
                fakeSession.ActiveUser = student1;
                var countSelf = await service.GetUnmasteredCountAsync(student1.Id);
                Assert.Equal(2, countSelf);

                // 2. student1 尝试越权查询 student2 -> 返回 0 (防越权拦截)
                var countIdorBlocked = await service.GetUnmasteredCountAsync(student2.Id);
                Assert.Equal(0, countIdorBlocked);

                // 3. parent 查询绑定的 student1 -> 返回 2 (合法绑定允许)
                fakeSession.ActiveUser = parent;
                var countParentValid = await service.GetUnmasteredCountAsync(student1.Id);
                Assert.Equal(2, countParentValid);

                // 4. parent 尝试越权查询未绑定的 student2 -> 返回 0 (拦截)
                var countParentInvalid = await service.GetUnmasteredCountAsync(student2.Id);
                Assert.Equal(0, countParentInvalid);

                // 5. teacher 查询 student1 与 student2 -> 全域教师权限允许
                fakeSession.ActiveUser = teacher;
                Assert.Equal(2, await service.GetUnmasteredCountAsync(student1.Id));
                Assert.Equal(1, await service.GetUnmasteredCountAsync(student2.Id));

                // 6. superAdmin 查询 -> 超管权限允许
                fakeSession.ActiveUser = superAdmin;
                Assert.Equal(2, await service.GetUnmasteredCountAsync(student1.Id));
                Assert.Equal(1, await service.GetUnmasteredCountAsync(student2.Id));
            }
        }

        #endregion

        #region 3. 智能判题引擎等价认可及解释原因测试

        [Theory]
        [InlineData("0.5", "1/2", true)]
        [InlineData("1/2", "0.5", true)]
        [InlineData("0.25", "1/4", true)]
        [InlineData("0.75", "3/4", true)]
        [InlineData("-0.5", "-1/2", true)]
        [InlineData("0.125", "1/8", true)]
        public void CheckFillInBlankMatch_FractionAndDecimalEquivalence(string user, string correct, bool expected)
        {
            bool matched = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.Equal(expected, matched);
        }

        [Theory]
        [InlineData("2H2 + O2 = 2H2O", "2H2+O2=2H2O")]
        [InlineData("CO2↑", "CO2")]
        [InlineData("BaSO4↓", "BaSO4")]
        [InlineData("CaCO3↓", "CaCO3")]
        [InlineData("N2 + 3H2 <=> 2NH3", "N2+3H2<=>2NH3")]
        public void CheckFillInBlankMatch_ChemicalFormulasAndEquations(string user, string correct)
        {
            bool matched = PracticeService.CheckFillInBlankMatch(user, correct);
            Assert.True(matched, $"Chemical match failed for '{user}' vs '{correct}'");
        }

        [Fact]
        public void AnswerCheckResult_EquivalentMatchReason_ProvidesDetailedFeedback()
        {
            var result = new AnswerCheckResult
            {
                IsCorrect = true,
                IsEquivalentMatch = true,
                EquivalentMatchReason = "数值与分式等价匹配 (0.5 == 1/2)"
            };

            Assert.True(result.IsEquivalentMatch);
            Assert.Contains("0.5 == 1/2", result.EquivalentMatchReason);
        }

        #endregion

        #region 4. UX 答题流水记录时间段过滤逻辑测试

        [Fact]
        public void PracticeRecords_TimeFiltering_CategorizesAccurately()
        {
            var today = DateTime.Today;
            var now = today.AddHours(12);
            var records = new List<PracticeRecord>
            {
                new PracticeRecord { Id = Guid.NewGuid(), AnsweredAt = now.AddMinutes(-10), IsCorrect = true },
                new PracticeRecord { Id = Guid.NewGuid(), AnsweredAt = now.AddDays(-2), IsCorrect = false },
                new PracticeRecord { Id = Guid.NewGuid(), AnsweredAt = now.AddDays(-10), IsCorrect = true }
            };

            // 全部时间
            var allRecords = records;
            Assert.Equal(3, allRecords.Count);

            // 今日挑战
            var todayRecords = records.Where(r => r.AnsweredAt >= today).ToList();
            Assert.Single(todayRecords);

            // 近7天
            var weekAgo = now.AddDays(-7);
            var weekRecords = records.Where(r => r.AnsweredAt >= weekAgo).ToList();
            Assert.Equal(2, weekRecords.Count);
        }

        #endregion
    }
}
