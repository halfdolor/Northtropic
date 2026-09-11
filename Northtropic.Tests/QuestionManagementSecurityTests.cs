using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class QuestionManagementSecurityTests
    {
        [Fact]
        public async Task DirectPublishAsync_TeacherOrAdmin_ShouldSucceed()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var teacher = new User { Id = Guid.NewGuid(), Username = "张老师", Role = UserRole.Teacher };
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "力学测试题",
                    IsPublic = false,
                    PublishStatus = PublishStatusEnum.Private,
                    CreatedByUserId = teacher.Id
                };

                context.Users.Add(teacher);
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Act
                var result = await service.DirectPublishAsync(question.Id, teacher.Id);

                // Assert
                Assert.True(result);
                var updated = await context.Questions.FindAsync(question.Id);
                Assert.NotNull(updated);
                Assert.True(updated.IsPublic);
                Assert.Equal(PublishStatusEnum.Approved, updated.PublishStatus);
            }
        }

        [Fact]
        public async Task DirectPublishAsync_Student_ShouldBeRejectedByRBAC()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User { Id = Guid.NewGuid(), Username = "李同学", Role = UserRole.Student };
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "学生自拟题",
                    IsPublic = false,
                    PublishStatus = PublishStatusEnum.Private,
                    CreatedByUserId = student.Id
                };

                context.Users.Add(student);
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Act
                var result = await service.DirectPublishAsync(question.Id, student.Id);

                // Assert: Students cannot direct publish into public bank without review
                Assert.False(result);
                var updated = await context.Questions.FindAsync(question.Id);
                Assert.NotNull(updated);
                Assert.False(updated.IsPublic);
                Assert.Equal(PublishStatusEnum.Private, updated.PublishStatus);
            }
        }

        [Fact]
        public async Task RetractToPrivateAsync_AuthorOrAdmin_CanRetract_UnauthorizedCannot()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var author = new User { Id = Guid.NewGuid(), Username = "王老师", Role = UserRole.Teacher };
                var admin = new User { Id = Guid.NewGuid(), Username = "超级管理", Role = UserRole.SuperAdmin };
                var stranger = new User { Id = Guid.NewGuid(), Username = "陌路人", Role = UserRole.Student };

                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "公开题",
                    IsPublic = true,
                    PublishStatus = PublishStatusEnum.Approved,
                    CreatedByUserId = author.Id
                };

                context.Users.AddRange(author, admin, stranger);
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Act 1: Stranger attempts to retract
                var strangerResult = await service.RetractToPrivateAsync(question.Id, stranger.Id);
                Assert.False(strangerResult);

                // Act 2: Author retracts own question
                var authorResult = await service.RetractToPrivateAsync(question.Id, author.Id);
                Assert.True(authorResult);

                var updated = await context.Questions.FindAsync(question.Id);
                Assert.NotNull(updated);
                Assert.False(updated.IsPublic);
                Assert.Equal(PublishStatusEnum.Private, updated.PublishStatus);
            }
        }

        [Fact]
        public async Task GetQuestionStatisticsAsync_ShouldReturnCorrectAggregations()
        {
            // Arrange
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var teacher = Guid.NewGuid();
                context.Questions.AddRange(
                    new Question { Stem = "Q1", IsPublic = true, CreatedByUserId = teacher },
                    new Question { Stem = "Q2", IsPublic = true, CreatedByUserId = Guid.NewGuid() },
                    new Question { Stem = "Q3", IsPublic = false, CreatedByUserId = teacher }
                );
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Act
                var (total, pub, my) = await service.GetQuestionStatisticsAsync(teacher);

                // Assert
                Assert.Equal(3, total);
                Assert.Equal(2, pub);
                Assert.Equal(2, my);
            }
        }
    }
}
