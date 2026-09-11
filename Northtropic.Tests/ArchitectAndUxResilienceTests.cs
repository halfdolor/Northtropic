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
    public class TestAppDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly SqliteConnection _connection;

        public TestAppDbContextFactory(SqliteConnection connection)
        {
            _connection = connection;
        }

        public AppDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new AppDbContext(options);
        }
    }

    public class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient();
        }
    }

    public class ArchitectAndUxResilienceTests
    {
        #region 1. DbContextFactory 架构解耦与异步并发安全测试

        [Fact]
        public async Task AiQuestionGenerator_WithDbContextFactory_SavesViaFactoryContext()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = "TestStudent",
                    Role = UserRole.Student,
                    Grade = "初中二年级"
                };
                context.Users.Add(user);
                await context.SaveChangesAsync();

                var gamificationService = new FakeGamificationService { CurrentUser = user };
                var factory = new TestAppDbContextFactory(connection);
                var generator = new AiQuestionGeneratorService(gamificationService, context, new FakeHttpClientFactory(), factory);

                // 生成启发式题目
                var questions = await generator.GenerateBatchQuestionsAsync("初中二年级", "初中物理", "浮力与压强", 3);

                Assert.Equal(3, questions.Count);

                // 使用新上下文查询，验证持久化成功且隔离
                using var verifyContext = factory.CreateDbContext();
                var savedCount = await verifyContext.Questions.CountAsync(q => q.Subject == "初中物理" && q.Category == "浮力与压强");
                Assert.True(savedCount >= 3);

                var logCount = await verifyContext.LlmGenerationLogs.CountAsync(l => l.UserId == user.Id);
                Assert.True(logCount >= 3);
            }
        }

        #endregion

        #region 2. 题目打乱器安全性保底测试 (QuestionShuffleHelper)

        [Fact]
        public void QuestionShuffleHelper_WhenOptionsFormatUnmatchable_SafelyFallsBackToOriginal()
        {
            // 构造无法匹配选项前缀或异常格式的题目
            var original = new Question
            {
                Id = Guid.NewGuid(),
                Stem = "异常格式题目",
                Type = QuestionType.SingleChoice,
                Subject = "初中物理",
                Category = "光学",
                OptionsJson = "[\"没有任何前缀的选项一\", \"没有任何前缀的选项二\"]",
                CorrectAnswer = "完全不存在的答案Z"
            };

            var shuffled = QuestionShuffleHelper.ShuffleQuestionOptions(original);

            // 架构保底机制应发挥作用：返回原始对象，绝不破坏 CorrectAnswer
            Assert.NotNull(shuffled);
            Assert.Equal("完全不存在的答案Z", shuffled.CorrectAnswer);
            Assert.Same(original, shuffled);
        }

        #endregion

        #region 3. 收藏夹与访客边界防御测试 (PracticeService)

        [Fact]
        public async Task PracticeService_ToggleFavorite_GuidEmpty_ReturnsFalseWithoutModifyingDb()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());
                var qId = Guid.NewGuid();

                // 访客空 GUID 收藏 -> 应该安全返回 false，不写入脏数据
                bool toggled = await practiceService.ToggleFavoriteAsync(Guid.Empty, qId);
                Assert.False(toggled);

                bool isFav = await practiceService.IsFavoriteAsync(Guid.Empty, qId);
                Assert.False(isFav);

                var favs = await practiceService.GetFavoriteQuestionsAsync(Guid.Empty);
                Assert.Empty(favs);

                Assert.Equal(0, await context.UserFavorites.CountAsync());
            }
        }

        #endregion

        #region 4. 作业布置防越权与实体有效性校验 (PracticeService)

        [Fact]
        public async Task PracticeService_CreateHomeworkAssignment_SecurityAndValidation()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var studentUser = new User { Id = Guid.NewGuid(), Username = "StudentA", Role = UserRole.Student };
                var parentUser = new User { Id = Guid.NewGuid(), Username = "ParentA", Role = UserRole.Parent };
                var otherParent = new User { Id = Guid.NewGuid(), Username = "ParentB", Role = UserRole.Parent };
                context.Users.AddRange(studentUser, parentUser, otherParent);

                // parentUser 绑定 studentUser
                context.StudentParentBindings.Add(new StudentParentBinding
                {
                    Id = Guid.NewGuid(),
                    ParentUserId = parentUser.Id,
                    StudentUserId = studentUser.Id
                });
                await context.SaveChangesAsync();

                var practiceService = new PracticeService(context, new FakeGamificationService(), new FakeUserSessionService(), new FakeAiTutorService());

                // 1. 空 GUID 校验
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    practiceService.CreateHomeworkAssignmentAsync(Guid.Empty, studentUser.Id, "测试作业", "物理", "力学", 5, 2, null, ""));

                // 2. 学生无权布置作业
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    practiceService.CreateHomeworkAssignmentAsync(studentUser.Id, studentUser.Id, "测试作业", "物理", "力学", 5, 2, null, ""));

                // 3. 目标学生不存在
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    practiceService.CreateHomeworkAssignmentAsync(parentUser.Id, Guid.NewGuid(), "测试作业", "物理", "力学", 5, 2, null, ""));

                // 4. 未绑定的家长越权布置
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    practiceService.CreateHomeworkAssignmentAsync(otherParent.Id, studentUser.Id, "越权作业", "物理", "力学", 5, 2, null, ""));

                // 5. 合法绑定的家长正常布置
                var assignment = await practiceService.CreateHomeworkAssignmentAsync(
                    parentUser.Id, studentUser.Id, "合法期末攻坚作业", "物理", "力学", 5, 3, DateTime.Now.AddDays(3), "认真复习！");

                Assert.NotNull(assignment);
                Assert.Equal("合法期末攻坚作业", assignment.Title);
                Assert.Equal(parentUser.Id, assignment.CreatorUserId);
                Assert.Equal(studentUser.Id, assignment.StudentUserId);
            }
        }

        #endregion

        #region 5. 级联清理 PracticeRecords 维护引用完整性 (QuestionManagementService)

        [Fact]
        public async Task QuestionManagementService_DeleteQuestion_CascadesPracticeRecords()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var admin = new User { Id = Guid.NewGuid(), Username = "SuperAdmin", Role = UserRole.SuperAdmin };
                var student = new User { Id = Guid.NewGuid(), Username = "Student", Role = UserRole.Student };
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    Stem = "测试被删除的题目",
                    Subject = "数学",
                    Category = "代数",
                    CreatedByUserId = admin.Id
                };
                context.Users.AddRange(admin, student);
                context.Questions.Add(question);
                await context.SaveChangesAsync();

                // 添加相关答题记录、错题本记录与收藏记录
                var practiceRecord = new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = question.Id,
                    UserAnswer = "A",
                    IsCorrect = true,
                    AnsweredAt = DateTime.Now
                };
                var errorItem = new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = question.Id,
                    ErrorReasonCategory = "概念模糊",
                    CreatedAt = DateTime.Now
                };
                var favorite = new UserFavorite
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = question.Id,
                    CreatedAt = DateTime.Now
                };
                context.PracticeRecords.Add(practiceRecord);
                context.ErrorItems.Add(errorItem);
                context.UserFavorites.Add(favorite);
                await context.SaveChangesAsync();

                var qms = new QuestionManagementService(context);

                // 删除题目
                bool deleted = await qms.DeleteQuestionAsync(question.Id, admin.Id);
                Assert.True(deleted);

                // 验证 Question 及所有关联记录被彻底级联清理
                Assert.Null(await context.Questions.FindAsync(question.Id));
                Assert.False(await context.PracticeRecords.AnyAsync(r => r.QuestionId == question.Id));
                Assert.False(await context.ErrorItems.AnyAsync(e => e.QuestionId == question.Id));
                Assert.False(await context.UserFavorites.AnyAsync(f => f.QuestionId == question.Id));
            }
        }

        [Fact]
        public async Task QuestionManagementService_BatchDeleteQuestions_CascadesPracticeRecords()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var admin = new User { Id = Guid.NewGuid(), Username = "SuperAdmin", Role = UserRole.SuperAdmin };
                var student = new User { Id = Guid.NewGuid(), Username = "Student", Role = UserRole.Student };
                var q1 = new Question { Id = Guid.NewGuid(), Stem = "批量题1", Subject = "数学", Category = "几何", CreatedByUserId = admin.Id };
                var q2 = new Question { Id = Guid.NewGuid(), Stem = "批量题2", Subject = "数学", Category = "几何", CreatedByUserId = admin.Id };
                context.Users.AddRange(admin, student);
                context.Questions.AddRange(q1, q2);

                context.PracticeRecords.Add(new PracticeRecord { Id = Guid.NewGuid(), UserId = student.Id, QuestionId = q1.Id, UserAnswer = "A", AnsweredAt = DateTime.Now });
                context.PracticeRecords.Add(new PracticeRecord { Id = Guid.NewGuid(), UserId = student.Id, QuestionId = q2.Id, UserAnswer = "B", AnsweredAt = DateTime.Now });
                await context.SaveChangesAsync();

                var qms = new QuestionManagementService(context);
                int count = await qms.BatchDeleteQuestionsAsync(new[] { q1.Id, q2.Id }, admin.Id);
                Assert.Equal(2, count);

                Assert.False(await context.PracticeRecords.AnyAsync(r => r.QuestionId == q1.Id || r.QuestionId == q2.Id));
            }
        }

        #endregion

        #region 6. 学情分析 AsNoTracking 与内存开销优化测试 (StudentEvolutionService)

        [Fact]
        public async Task StudentEvolutionService_GetMasteryOverview_AsNoTracking_DoesNotPolluteChangeTracker()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var student = new User { Id = Guid.NewGuid(), Username = "StudentEva", Role = UserRole.Student, Grade = "初中二年级" };
                var question = new Question { Id = Guid.NewGuid(), Stem = "力学试题", Subject = "初中物理", Category = "力学与牛顿第一定律" };
                context.Users.Add(student);
                context.Questions.Add(question);

                context.PracticeRecords.Add(new PracticeRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = question.Id,
                    UserAnswer = "A",
                    IsCorrect = true,
                    AnsweredAt = DateTime.Now
                });
                context.ErrorItems.Add(new ErrorItem
                {
                    Id = Guid.NewGuid(),
                    UserId = student.Id,
                    QuestionId = question.Id,
                    ErrorReasonCategory = "审题粗心",
                    CreatedAt = DateTime.Now
                });
                await context.SaveChangesAsync();

                // 清空当前 context 中已有的 tracking
                context.ChangeTracker.Clear();

                var service = new StudentEvolutionService(context, new FakeGamificationService(), new FakeHttpClientFactory());
                var report = await service.GetMasteryOverviewAsync(student.Id, "初中物理");

                Assert.NotEmpty(report);

                // 核心架构验证：只读查询使用 AsNoTracking，不会在 ChangeTracker 中驻留大量实体
                var trackedPracticeRecords = context.ChangeTracker.Entries<PracticeRecord>().ToList();
                var trackedErrorItems = context.ChangeTracker.Entries<ErrorItem>().ToList();

                Assert.Empty(trackedPracticeRecords);
                Assert.Empty(trackedErrorItems);
            }
        }

        #endregion

        #region 7. EF Core 数据库索引结构定义测试 (AppDbContext)

        [Fact]
        public void AppDbContext_HomeworkAssignment_Indexes_ConfiguredCorrectly()
        {
            var (context, connection) = TestDbContextFactory.CreateInMemoryContext();
            using (connection)
            using (context)
            {
                var entityType = context.Model.FindEntityType(typeof(HomeworkAssignment));
                Assert.NotNull(entityType);

                var indexes = entityType.GetIndexes().ToList();

                // 检验 (StudentUserId, IsCompleted) 复合索引
                var compositeIndex = indexes.FirstOrDefault(idx =>
                    idx.Properties.Count == 2 &&
                    idx.Properties.Any(p => p.Name == nameof(HomeworkAssignment.StudentUserId)) &&
                    idx.Properties.Any(p => p.Name == nameof(HomeworkAssignment.IsCompleted)));
                Assert.NotNull(compositeIndex);

                // 检验 CreatorUserId 索引
                var creatorIndex = indexes.FirstOrDefault(idx =>
                    idx.Properties.Count == 1 &&
                    idx.Properties[0].Name == nameof(HomeworkAssignment.CreatorUserId));
                Assert.NotNull(creatorIndex);
            }
        }

        #endregion
    }
}
