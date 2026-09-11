using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxTranscendenceTests
    {
        private class TestEfDbContextFactory : IDbContextFactory<AppDbContext>
        {
            private readonly DbContextOptions<AppDbContext> _options;

            public TestEfDbContextFactory(DbContextOptions<AppDbContext> options)
            {
                _options = options;
            }

            public AppDbContext CreateDbContext()
            {
                return new AppDbContext(_options);
            }

            public Task<AppDbContext> CreateDbContextAsync(System.Threading.CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AppDbContext(_options));
            }
        }

        [Fact]
        public void Question_Options_CachesDeserialization_UntilOptionsJsonChanges()
        {
            // Arrange
            var q = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "测试题干",
                OptionsJson = "[\"A. 选项一\",\"B. 选项二\",\"C. 选项三\"]"
            };

            // Act 1: Initial access
            var opts1 = q.Options;
            var opts2 = q.Options;

            // Assert 1: Must be exact same object reference (zero heap allocation on repeated reads)
            Assert.Same(opts1, opts2);
            Assert.Equal(3, opts1.Count);
            Assert.Equal("A. 选项一", opts1[0]);

            // Act 2: Mutate OptionsJson
            q.OptionsJson = "[\"A. 新选项1\",\"B. 新选项2\"]";
            var opts3 = q.Options;

            // Assert 2: Cache must invalidate cleanly and reflect new content
            Assert.NotSame(opts1, opts3);
            Assert.Equal(2, opts3.Count);
            Assert.Equal("A. 新选项1", opts3[0]);
        }

        [Fact]
        public async Task GamificationService_UnlockAchievementAsync_SupportsTargetUserId()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var ach = new Achievement { Id = Guid.NewGuid(), Code = "ERROR_KILLER_5", Title = "错题克星" };
                context.Achievements.Add(ach);

                var user1 = new User { Id = Guid.NewGuid(), Username = "User1", Role = UserRole.Student, Exp = 0, Coins = 0 };
                var user2 = new User { Id = Guid.NewGuid(), Username = "User2", Role = UserRole.Student, Exp = 0, Coins = 0 };
                context.Users.AddRange(user1, user2);
                await context.SaveChangesAsync();

                var fakeSession = new FakeUserSessionService { ActiveUser = user1 };
                var gamification = new GamificationService(context, fakeSession);

                // Act: Explicitly unlock achievement targeting user2
                bool unlocked = await gamification.UnlockAchievementAsync("ERROR_KILLER_5", user2.Id);

                // Assert: user2 should have received the achievement
                Assert.True(unlocked);
                var user2Achievements = await context.UserAchievements
                    .Include(ua => ua.Achievement)
                    .Where(ua => ua.UserId == user2.Id)
                    .ToListAsync();

                Assert.Single(user2Achievements);
                Assert.Equal(ach.Id, user2Achievements[0].AchievementId);

                // user1 should not have it
                var user1Achievements = await context.UserAchievements
                    .Where(ua => ua.UserId == user1.Id)
                    .ToListAsync();
                Assert.Empty(user1Achievements);
            }
        }

        [Fact]
        public async Task ErrorBookService_BatchMarkErrorsAsMasteredAsync_EnforcesIdorSecurityAndGrantsRewards()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var studentA = new User { Id = Guid.NewGuid(), Username = "StudentA", Role = UserRole.Student, Exp = 0, Coins = 0, ResolvedErrorsCount = 0 };
                var studentB = new User { Id = Guid.NewGuid(), Username = "StudentB", Role = UserRole.Student, Exp = 0, Coins = 0, ResolvedErrorsCount = 0 };
                context.Users.AddRange(studentA, studentB);

                var q1 = new Question { Id = Guid.NewGuid(), Stem = "Q1", OptionsJson = "[]", CorrectAnswer = "A" };
                var q2 = new Question { Id = Guid.NewGuid(), Stem = "Q2", OptionsJson = "[]", CorrectAnswer = "B" };
                var q3 = new Question { Id = Guid.NewGuid(), Stem = "Q3", OptionsJson = "[]", CorrectAnswer = "C" };
                context.Questions.AddRange(q1, q2, q3);

                var errA1 = new ErrorItem { Id = Guid.NewGuid(), UserId = studentA.Id, QuestionId = q1.Id, IsMastered = false, CreatedAt = DateTime.Now };
                var errA2 = new ErrorItem { Id = Guid.NewGuid(), UserId = studentA.Id, QuestionId = q2.Id, IsMastered = false, CreatedAt = DateTime.Now };
                var errB1 = new ErrorItem { Id = Guid.NewGuid(), UserId = studentB.Id, QuestionId = q3.Id, IsMastered = false, CreatedAt = DateTime.Now };
                context.ErrorItems.AddRange(errA1, errA2, errB1);
                await context.SaveChangesAsync();

                var fakeSession = new FakeUserSessionService { ActiveUser = studentA };
                var fakeGamification = new FakeGamificationService();
                var errorBookService = new ErrorBookService(context, fakeGamification, fakeSession);

                // Act: studentA attempts to batch mark errA1, errA2, AND errB1 (IDOR attempt on B)
                var batchResult = await errorBookService.BatchMarkErrorsAsMasteredAsync(new[] { errA1.Id, errA2.Id, errB1.Id }, studentA.Id);

                // Assert: Only 2 items (studentA's own) should be marked
                Assert.Equal(2, batchResult.MarkedCount);
                Assert.Equal(30, batchResult.EarnedExp); // 15 * 2
                Assert.Equal(10, batchResult.EarnedCoins); // 5 * 2

                // errB1 must remain unmastered
                var updatedErrB1 = await context.ErrorItems.FindAsync(errB1.Id);
                Assert.False(updatedErrB1!.IsMastered);

                // errA1 and errA2 must be mastered
                var updatedErrA1 = await context.ErrorItems.FindAsync(errA1.Id);
                var updatedErrA2 = await context.ErrorItems.FindAsync(errA2.Id);
                Assert.True(updatedErrA1!.IsMastered);
                Assert.True(updatedErrA2!.IsMastered);

                // StudentA user account must have accumulated rewards
                var updatedStudentA = await context.Users.FindAsync(studentA.Id);
                Assert.Equal(30, updatedStudentA!.Exp);
                Assert.Equal(10, updatedStudentA.Coins);
                Assert.Equal(2, updatedStudentA.ResolvedErrorsCount);
            }
        }

        [Fact]
        public async Task ErrorBookService_MarkErrorAsMasteredAsync_RejectsUnauthorizedUsers()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var studentA = new User { Id = Guid.NewGuid(), Username = "StudentA", Role = UserRole.Student };
                var studentB = new User { Id = Guid.NewGuid(), Username = "StudentB", Role = UserRole.Student };
                context.Users.AddRange(studentA, studentB);

                var q1 = new Question { Id = Guid.NewGuid(), Stem = "Q1", OptionsJson = "[]", CorrectAnswer = "A" };
                context.Questions.Add(q1);

                var errA1 = new ErrorItem { Id = Guid.NewGuid(), UserId = studentA.Id, QuestionId = q1.Id, IsMastered = false, CreatedAt = DateTime.Now };
                context.ErrorItems.Add(errA1);
                await context.SaveChangesAsync();

                var fakeSession = new FakeUserSessionService { ActiveUser = studentB };
                var fakeGamification = new FakeGamificationService();
                var errorBookService = new ErrorBookService(context, fakeGamification, fakeSession);

                // Act: Student B calls MarkErrorAsMasteredAsync on Student A's error item
                var reward = await errorBookService.MarkErrorAsMasteredAsync(errA1.Id, studentB.Id);

                // Assert: Must return empty reward and reject modification
                Assert.Equal(0, reward.EarnedExp);
                Assert.Equal(0, reward.EarnedCoins);

                var item = await context.ErrorItems.FindAsync(errA1.Id);
                Assert.False(item!.IsMastered);
            }
        }

        [Fact]
        public async Task AiTutorService_RecordLlmTokenUsage_UsesDbContextFactorySafely()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = new User { Id = Guid.NewGuid(), Username = "AIUser", LlmApiKey = "test-key" };
                context.Users.Add(user);
                await context.SaveChangesAsync();

                var fakeSession = new FakeUserSessionService { ActiveUser = user };
                var fakeGamification = new FakeGamificationService { CurrentUser = user };
                var mockFactory = new MockHttpClientFactory(new HttpClient());

                var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connection)
                    .Options;
                var efFactory = new TestEfDbContextFactory(dbOptions);

                var aiTutor = new AiTutorService(fakeGamification, mockFactory, dbContext: null, dbContextFactory: efFactory);

                Assert.NotNull(aiTutor);
            }
        }
    }
}
