using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Northtropic.Data;
using Northtropic.Models;
using Northtropic.Services;
using Xunit;

namespace Northtropic.Tests
{
    public class ArchitectAndUxHardeningTests
    {
        [Fact]
        public async Task AddQuestionAsync_Teacher_Success_ShouldSetMetadataAndPersist()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var teacher = new User { Id = Guid.NewGuid(), Username = "物理王老师", Role = UserRole.Teacher };
                context.Users.Add(teacher);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);
                var newQ = new Question
                {
                    Stem = "关于浮力的计算，以下说法正确的是？",
                    Subject = "初中物理",
                    Category = "浮力与阿基米德原理",
                    Type = QuestionType.SingleChoice,
                    Difficulty = 3,
                    GradeTarget = "八年级下册",
                    OptionsJson = "[\"A. 浸在液体中的物体不受浮力\",\"B. 浮力大小等于排开液体的重力\",\"C. 浮力随深度无限增大\",\"D. 只有悬浮物体受浮力\"]",
                    CorrectAnswer = "B",
                    StandardAnalysis = "阿基米德原理：浸入液体中的物体受到向上的浮力，浮力大小等于它排开液体所受的重力。"
                };

                var (success, msg, saved) = await service.AddQuestionAsync(newQ, teacher.Id);

                Assert.True(success, msg);
                Assert.NotNull(saved);
                Assert.Equal(teacher.Id, saved.CreatedByUserId);
                Assert.False(saved.IsPublic);
                Assert.Equal(PublishStatusEnum.Private, saved.PublishStatus);
                Assert.True(saved.CreatedAt > DateTime.MinValue);

                var fromDb = await context.Questions.FindAsync(saved.Id);
                Assert.NotNull(fromDb);
                Assert.Equal("B", fromDb.CorrectAnswer);
            }
        }

        [Fact]
        public async Task AddQuestionAsync_Student_DeniedByRBAC()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User { Id = Guid.NewGuid(), Username = "张同学", Role = UserRole.Student };
                context.Users.Add(student);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);
                var newQ = new Question
                {
                    Stem = "测试题干",
                    Subject = "初中物理",
                    Category = "浮力",
                    Type = QuestionType.SingleChoice,
                    OptionsJson = "[\"A. 1\",\"B. 2\"]",
                    CorrectAnswer = "A"
                };

                var (success, msg, saved) = await service.AddQuestionAsync(newQ, student.Id);

                Assert.False(success);
                Assert.Contains("权限不足", msg);
                Assert.Null(saved);
                Assert.Empty(await context.Questions.ToListAsync());
            }
        }

        [Fact]
        public async Task AddQuestionAsync_Validation_EmptyStemOrAnswer_ShouldFail()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var teacher = new User { Id = Guid.NewGuid(), Username = "李老师", Role = UserRole.Teacher };
                context.Users.Add(teacher);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Case 1: Empty Stem
                var emptyStemQ = new Question
                {
                    Stem = "   ",
                    Subject = "数学",
                    Category = "代数",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "5"
                };
                var (s1, m1, _) = await service.AddQuestionAsync(emptyStemQ, teacher.Id);
                Assert.False(s1);
                Assert.Contains("试题题干不能为空", m1);

                // Case 2: Empty Answer
                var emptyAnswerQ = new Question
                {
                    Stem = "1 + 1 = ?",
                    Subject = "数学",
                    Category = "代数",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "  "
                };
                var (s2, m2, _) = await service.AddQuestionAsync(emptyAnswerQ, teacher.Id);
                Assert.False(s2);
                Assert.Contains("正确答案不能为空", m2);
            }
        }

        [Fact]
        public async Task UpdateQuestionAsync_TeacherCannotModifyOtherTeacherQuestion_IDOR_Protection()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var teacher1 = new User { Id = Guid.NewGuid(), Username = "教师A", Role = UserRole.Teacher };
                var teacher2 = new User { Id = Guid.NewGuid(), Username = "教师B", Role = UserRole.Teacher };
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "教师A的原题题干",
                    Subject = "高中数学",
                    Category = "导数",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "e^x",
                    CreatedByUserId = teacher1.Id,
                    IsPublic = false,
                    PublishStatus = PublishStatusEnum.Private
                };

                context.Users.AddRange(teacher1, teacher2);
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Act: Teacher 2 attempts to tamper with Teacher 1's question
                question.Stem = "恶意篡改题干";
                var updateResult = await service.UpdateQuestionAsync(question, teacher2.Id);

                // Assert: Must be rejected by IDOR guard
                Assert.False(updateResult);

                // Check DB content remains intact
                var dbQuestion = await context.Questions.AsNoTracking().FirstOrDefaultAsync(q => q.Id == question.Id);
                Assert.NotNull(dbQuestion);
                Assert.Equal("教师A的原题题干", dbQuestion.Stem);
            }
        }

        [Fact]
        public async Task UpdateQuestionAsync_AdminCanModifyAnyQuestion()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var teacher = new User { Id = Guid.NewGuid(), Username = "教师A", Role = UserRole.Teacher };
                var admin = new User { Id = Guid.NewGuid(), Username = "超级管理员", Role = UserRole.SuperAdmin };
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "初始题干",
                    Subject = "初中化学",
                    Category = "酸碱盐",
                    Type = QuestionType.FillInBlank,
                    CorrectAnswer = "NaCl",
                    CreatedByUserId = teacher.Id
                };

                context.Users.AddRange(teacher, admin);
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Act: Admin modifies
                question.Stem = "管理员校对修订后的题干";
                var updateResult = await service.UpdateQuestionAsync(question, admin.Id);

                // Assert: Succeeded
                Assert.True(updateResult);
                var dbQuestion = await context.Questions.AsNoTracking().FirstOrDefaultAsync(q => q.Id == question.Id);
                Assert.NotNull(dbQuestion);
                Assert.Equal("管理员校对修订后的题干", dbQuestion.Stem);
            }
        }

        [Fact]
        public async Task DeleteQuestionAsync_TeacherCannotDeleteOtherTeacherQuestion_IDOR_Protection()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var teacher1 = new User { Id = Guid.NewGuid(), Username = "教师A", Role = UserRole.Teacher };
                var teacher2 = new User { Id = Guid.NewGuid(), Username = "教师B", Role = UserRole.Teacher };
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "教师A的题",
                    Subject = "初中物理",
                    Category = "电学",
                    CorrectAnswer = "10Ω",
                    CreatedByUserId = teacher1.Id
                };

                context.Users.AddRange(teacher1, teacher2);
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Act: Teacher 2 attempts to delete Teacher 1's question
                var deleteResult = await service.DeleteQuestionAsync(question.Id, teacher2.Id);

                // Assert: Must be rejected
                Assert.False(deleteResult);
                var dbQuestion = await context.Questions.FindAsync(question.Id);
                Assert.NotNull(dbQuestion);
            }
        }

        [Fact]
        public async Task DeleteQuestionAsync_AuthorCanDelete_CascadesCleanup()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var teacher = new User { Id = Guid.NewGuid(), Username = "教师A", Role = UserRole.Teacher };
                var student = new User { Id = Guid.NewGuid(), Username = "学生", Role = UserRole.Student };
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "将要删除的题",
                    Subject = "通用知识",
                    Category = "科普",
                    CorrectAnswer = "Yes",
                    CreatedByUserId = teacher.Id
                };

                context.Users.AddRange(teacher, student);
                context.Questions.Add(question);

                // Add referencing foreign entities
                var favorite = new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = question.Id,
                    CreatedAt = DateTime.UtcNow
                };
                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = question.Id,
                    UserWrongAnswer = "No",
                    CreatedAt = DateTime.UtcNow
                };

                context.UserFavorites.Add(favorite);
                context.ErrorItems.Add(errorItem);
                await context.SaveChangesAsync();

                var service = new QuestionManagementService(context);

                // Act: Teacher deletes own question
                var deleteResult = await service.DeleteQuestionAsync(question.Id, teacher.Id);

                // Assert: Succeeded and cascaded
                Assert.True(deleteResult);
                Assert.Null(await context.Questions.FindAsync(question.Id));
                Assert.Null(await context.UserFavorites.FindAsync(favorite.Id));
                Assert.Null(await context.ErrorItems.FindAsync(errorItem.Id));
            }
        }

        [Fact]
        public void PaginationMath_SlicingLogic_Accurate()
        {
            // Simulate 45 items with page size 10
            var allItems = Enumerable.Range(1, 45).ToList();
            int pageSize = 10;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)allItems.Count / pageSize));

            Assert.Equal(5, totalPages);

            // Page 1
            var page1 = allItems.Skip(0).Take(pageSize).ToList();
            Assert.Equal(10, page1.Count);
            Assert.Equal(1, page1.First());
            Assert.Equal(10, page1.Last());

            // Page 5 (last page)
            var page5 = allItems.Skip(4 * pageSize).Take(pageSize).ToList();
            Assert.Equal(5, page5.Count);
            Assert.Equal(41, page5.First());
            Assert.Equal(45, page5.Last());
        }
    }
}
