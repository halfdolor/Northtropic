using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ErrorBookServiceTests
    {
        [Fact]
        public async Task DeleteErrorItemAsync_WithAuthorizedUser_ShouldDeleteSuccessfully()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var userId = Guid.NewGuid();
                var question = new Question { Stem = "测试题1", OptionsJson = "[]", CorrectAnswer = "A" };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Question = question,
                    UserWrongAnswer = "B",
                    IsMastered = false,
                    ErrorReasonCategory = "概念模糊"
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = new User { Id = userId } };
                var fakeGamification = new FakeGamificationService();
                var service = new ErrorBookService(context, fakeGamification, fakeUserSession);

                // Act
                var result = await service.DeleteErrorItemAsync(errorItem.Id);

                // Assert
                Assert.True(result);
                var existsInDb = await context.ErrorItems.AnyAsync(e => e.Id == errorItem.Id);
                Assert.False(existsInDb);
            }
        }

        [Fact]
        public async Task DeleteErrorItemAsync_WithUnauthorizedUser_ShouldReturnFalseAndNotDelete()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var ownerUserId = Guid.NewGuid();
                var hackerUserId = Guid.NewGuid();
                var question = new Question { Stem = "测试题1", OptionsJson = "[]", CorrectAnswer = "A" };
                context.Questions.Add(question);

                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = ownerUserId,
                    Question = question,
                    UserWrongAnswer = "B",
                    IsMastered = false
                };
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                // Hacker tries to delete owner's error item
                var fakeUserSession = new FakeUserSessionService { ActiveUser = new User { Id = hackerUserId } };
                var fakeGamification = new FakeGamificationService();
                var service = new ErrorBookService(context, fakeGamification, fakeUserSession);

                // Act
                var result = await service.DeleteErrorItemAsync(errorItem.Id);

                // Assert
                Assert.False(result);
                var existsInDb = await context.ErrorItems.AnyAsync(e => e.Id == errorItem.Id);
                Assert.True(existsInDb, "Item must not be deleted by unauthorized user");
            }
        }

        [Fact]
        public async Task BatchDeleteErrorItemsAsync_ShouldOnlyDeleteTargetUsersItems()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user1 = Guid.NewGuid();
                var user2 = Guid.NewGuid();
                var question = new Question { Stem = "测试题1", OptionsJson = "[]", CorrectAnswer = "A" };
                context.Questions.Add(question);

                var item1 = new ErrorItem { Id = Guid.NewGuid(), UserId = user1, Question = question, UserWrongAnswer = "B" };
                var item2 = new ErrorItem { Id = Guid.NewGuid(), UserId = user1, Question = question, UserWrongAnswer = "C" };
                var item3 = new ErrorItem { Id = Guid.NewGuid(), UserId = user2, Question = question, UserWrongAnswer = "D" };

                context.ErrorItems.AddRange(item1, item2, item3);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = new User { Id = user1 } };
                var fakeGamification = new FakeGamificationService();
                var service = new ErrorBookService(context, fakeGamification, fakeUserSession);

                // Act: Request to delete item1, item2, and item3
                var deletedCount = await service.BatchDeleteErrorItemsAsync(new[] { item1.Id, item2.Id, item3.Id });

                // Assert: User1 can only delete item1 and item2 (deletedCount = 2)
                Assert.Equal(2, deletedCount);
                Assert.False(await context.ErrorItems.AnyAsync(e => e.Id == item1.Id));
                Assert.False(await context.ErrorItems.AnyAsync(e => e.Id == item2.Id));
                Assert.True(await context.ErrorItems.AnyAsync(e => e.Id == item3.Id), "User2's item must remain intact");
            }
        }

        [Fact]
        public async Task ClearMasteredErrorsAsync_ShouldOnlyDeleteMasteredItems()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = Guid.NewGuid();
                var question = new Question { Stem = "测试题1", OptionsJson = "[]", CorrectAnswer = "A" };
                context.Questions.Add(question);

                var masteredItem = new ErrorItem { Id = Guid.NewGuid(), UserId = user, Question = question, UserWrongAnswer = "B", IsMastered = true };
                var unmasteredItem = new ErrorItem { Id = Guid.NewGuid(), UserId = user, Question = question, UserWrongAnswer = "C", IsMastered = false };

                context.ErrorItems.AddRange(masteredItem, unmasteredItem);
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = new User { Id = user } };
                var fakeGamification = new FakeGamificationService();
                var service = new ErrorBookService(context, fakeGamification, fakeUserSession);

                // Act
                var clearedCount = await service.ClearMasteredErrorsAsync();

                // Assert
                Assert.Equal(1, clearedCount);
                Assert.False(await context.ErrorItems.AnyAsync(e => e.Id == masteredItem.Id));
                Assert.True(await context.ErrorItems.AnyAsync(e => e.Id == unmasteredItem.Id), "Unmastered item should not be cleared");
            }
        }

        [Fact]
        public async Task GetUnmasteredCountAsync_ShouldReturnAccurateCount()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = Guid.NewGuid();
                var question = new Question { Stem = "测试题1", OptionsJson = "[]", CorrectAnswer = "A" };
                context.Questions.Add(question);

                context.ErrorItems.AddRange(
                    new ErrorItem { Id = Guid.NewGuid(), UserId = user, Question = question, UserWrongAnswer = "B", IsMastered = false },
                    new ErrorItem { Id = Guid.NewGuid(), UserId = user, Question = question, UserWrongAnswer = "C", IsMastered = false },
                    new ErrorItem { Id = Guid.NewGuid(), UserId = user, Question = question, UserWrongAnswer = "D", IsMastered = true }
                );
                await context.SaveChangesAsync();

                var fakeUserSession = new FakeUserSessionService { ActiveUser = new User { Id = user } };
                var fakeGamification = new FakeGamificationService();
                var service = new ErrorBookService(context, fakeGamification, fakeUserSession);

                // Act
                var count = await service.GetUnmasteredCountAsync(user);

                // Assert
                Assert.Equal(2, count);
            }
        }
    }
}
